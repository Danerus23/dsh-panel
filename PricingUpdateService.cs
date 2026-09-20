using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshTray;

public sealed class PeakWindow
{
    public int[] Days { get; set; } = Array.Empty<int>();
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class PriceLine
{
    public string Item { get; set; } = "";
    public string Tier { get; set; } = "";
    public List<string> Prices { get; set; } = new();
}

public sealed class PricingCheckResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>Подробности для журнала: сколько знаков разбирали, что нашлось в каждой версии.</summary>
    public string Diag { get; set; } = "";

    public List<PeakWindow> Windows { get; set; } = new();
    public List<PriceLine> Prices { get; set; } = new();
    public List<string> Models { get; set; } = new();
    public bool PricesParsed { get; set; }

    /// <summary>Смещение местного времени в минутах — для показа окон на экране.</summary>
    public int OffsetMinutes { get; set; }
    public string SourceUrl { get; set; } = "";

    /// <summary>Окна с официальной страницы отличаются от тех, по которым работаем.</summary>
    public bool Differs { get; set; }
    public string CurrentText { get; set; } = "";
    public string FoundText { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Окна пика и цены с официальной страницы DeepSeek. Читаются обе языковые
/// версии страницы: английская фраза «Peak hours are … UTC, Monday through
/// Friday» и китайская «北京时间周一至周五 … 为高峰时段» (пекинское время
/// переводится в UTC). Если формулировку перепишут в одной версии, сработает
/// вторая; если не сойдётся ни одна — окна остаются прежними, а причина
/// попадает в журнал. Окна применяются только по подтверждению, цены — показ.
///
/// Формулировки сайта меняются (в сентябре 2026 обе версии переписали), поэтому
/// разбор нарочно не завязан на конец фразы: в английской дни берутся до точки,
/// в китайской — окно вокруг «北京时间». Проверять после каждой правки следует
/// обе версии сразу: `--pricing-check --from <сохранённая страница>`.
/// </summary>
public sealed class PricingUpdateService
{
    public const string EnglishUrl = "https://api-docs.deepseek.com/quick_start/pricing";
    public const string ChineseUrl = "https://api-docs.deepseek.com/zh-cn/quick_start/pricing";

    private static readonly string[] DayNames =
    {
        "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
    };

    private static readonly Dictionary<char, int> ChineseDays = new()
    {
        ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['日'] = 0, ['天'] = 0,
    };

    private readonly AppPaths _paths;

    public PricingUpdateService(AppPaths paths)
    {
        _paths = paths;
    }

    // --- получение страницы ------------------------------------------------

    public static string Fetch(string url)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/1.0");

        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Loc.T("err.http", (int)response.StatusCode));
        }

        return new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
    }

    /// <summary>Адреса источников: свой (из переменной окружения) или оба языковых.</summary>
    public static IEnumerable<string> SourceUrls()
    {
        var custom = Environment.GetEnvironmentVariable("DSH_TRAY_PRICING_SOURCE");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            yield return custom;
            yield break;
        }

        yield return EnglishUrl;
        yield return ChineseUrl;
    }

    public PricingCheckResult Check()
    {
        var errors = new List<string>();

        foreach (var url in SourceUrls())
        {
            try
            {
                var result = Parse(Fetch(url), _paths);
                result.SourceUrl = url;
                if (result.Ok) return result;
                errors.Add($"{url}: {result.Error}");
            }
            catch (Exception error)
            {
                errors.Add($"{url}: {error.Message}");
            }
        }

        return new PricingCheckResult { Ok = false, Error = string.Join("; ", errors) };
    }

    // --- разбор страницы ---------------------------------------------------

    public static PricingCheckResult Parse(string html, AppPaths paths)
    {
        var result = new PricingCheckResult();
        if (string.IsNullOrWhiteSpace(html))
        {
            result.Error = Loc.T("err.pageEmpty");
            return result;
        }

        result.OffsetMinutes = LocalZone.OffsetMinutes(DateTime.UtcNow);
        var text = StripTags(html);

        var english = ParseEnglish(text);
        var chinese = english == null || english.Count == 0 ? ParseChinese(text) : null;
        var windows = english != null && english.Count > 0 ? english : chinese;

        if (windows == null || windows.Count == 0)
        {
            // Человеку — короткая причина, подробности (сколько знаков, что нашлось в каждой
            // языковой версии) уходят в журнал: в окне они только пугали длиной.
            result.Error = Loc.T("err.noPeakPhrase");
            result.Diag = Loc.T("err.noPeakPhraseDiag", text.Length, Describe(english), Describe(chinese));
            return result;
        }

        result.Ok = true;
        result.Windows = windows;
        result.Prices = ParsePrices(html, out var models, out var pricesOk);
        result.Models = models;
        result.PricesParsed = pricesOk;

        var current = ReadCurrentWindows(paths);
        result.CurrentText = Format(current, result.OffsetMinutes);
        result.FoundText = Format(windows, result.OffsetMinutes);
        result.Differs = !SameWindows(current, windows);
        return result;
    }

    private static string Describe(List<PeakWindow> windows)
    {
        if (windows == null) return Loc.T("err.phraseNone");
        return windows.Count > 0 ? Loc.T("err.phraseParsed", windows.Count) : Loc.T("err.phrasePartial");
    }

    /// <summary>
    /// «Peak hours are 01:00 - 04:00 and 06:00 - 10:00 UTC, Monday through Friday, excluding
    /// Chinese public holidays. All other hours are off-peak, including weekends…».
    /// Раньше фраза заканчивалась на «(all other hours are off-peak…», и разбор ждал скобку
    /// сразу после дней; теперь после дней идёт «, excluding …». Поэтому дни берём до точки,
    /// а не до скобки.
    /// </summary>
    private static List<PeakWindow> ParseEnglish(string text)
    {
        var match = Regex.Match(
            text,
            @"Peak hours[^.]{0,60}?(?<times>\d{1,2}:\d{2}\s*[-–—]\s*\d{1,2}:\d{2}[^.,;]*?)\s+UTC,\s*(?<days>[^.;()]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;
        return BuildWindows(match.Groups["times"].Value, match.Groups["days"].Value, shiftHours: 0);
    }

    /// <summary>
    /// Китайская страница пишет про пекинское время: «北京时间周一至周五（不含中国法定节假日）
    /// 9:00 - 12:00、14:00 - 18:00 为高峰时段». Раньше фраза начиналась с «高峰时段为», и разбор
    /// ждал именно такого порядка; теперь время и дни стоят до слов «为高峰时段», а между днями
    /// вставлена оговорка в скобках. Ищем «北京时间» и разбираем окно вокруг него по частям:
    /// так разбор переживёт и следующую перестановку слов.
    /// </summary>
    private static List<PeakWindow> ParseChinese(string text)
    {
        var at = text.IndexOf("北京时间", StringComparison.Ordinal);
        if (at < 0) return null;

        var window = text.Substring(at, Math.Min(200, text.Length - at));
        var times = Regex.Match(window, @"\d{1,2}:\d{2}\s*[-–—]\s*\d{1,2}:\d{2}[^为]*");
        if (!times.Success) return null;

        // Пекинское время — UTC+8, поэтому в UTC вычитаем восемь часов.
        var days = window[..times.Index];
        return BuildWindows(times.Value, days, shiftHours: -8);
    }

    private static List<PeakWindow> BuildWindows(string times, string days, int shiftHours)
    {
        var dayList = ParseDays(days);
        var result = new List<PeakWindow>();
        if (dayList.Length == 0) return result;

        foreach (Match span in Regex.Matches(times, @"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})"))
        {
            var from = new TimeOnly(
                int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(span.Groups[2].Value, CultureInfo.InvariantCulture));
            var to = new TimeOnly(
                int.Parse(span.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(span.Groups[4].Value, CultureInfo.InvariantCulture));

            result.Add(new PeakWindow
            {
                Days = dayList,
                From = from.AddHours(shiftHours).ToString("HH:mm", CultureInfo.InvariantCulture),
                To = to.AddHours(shiftHours).ToString("HH:mm", CultureInfo.InvariantCulture),
            });
        }

        return result;
    }

    private static int[] ParseDays(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains('周') || lower.Contains('每') || lower.Contains('末'))
        {
            if (lower.Contains("周末")) return new[] { 0, 6 };
            if (lower.Contains("工作日")) return new[] { 1, 2, 3, 4, 5 };
            if (lower.Contains("每天") || lower.Contains("每日")) return new[] { 0, 1, 2, 3, 4, 5, 6 };

            var chinese = new List<int>();
            foreach (Match match in Regex.Matches(lower, @"周[一二三四五六日天]"))
            {
                if (ChineseDays.TryGetValue(match.Value[1], out var day) && !chinese.Contains(day)) chinese.Add(day);
            }

            if (chinese.Count == 2 && Regex.IsMatch(lower, @"[至到\-–—~]"))
            {
                var range = Expand(chinese[0], chinese[1]);
                if (range.Count > 0) return range.ToArray();
            }

            if (chinese.Count > 0) return chinese.Distinct().OrderBy(value => value).ToArray();
        }

        if (lower.Contains("weekday")) return new[] { 1, 2, 3, 4, 5 };
        if (lower.Contains("weekend")) return new[] { 0, 6 };
        if (lower.Contains("every day") || lower.Contains("all days") || lower.Contains("daily") || lower.Contains("each day"))
        {
            return new[] { 0, 1, 2, 3, 4, 5, 6 };
        }

        var found = new List<int>();
        foreach (Match match in Regex.Matches(lower, @"mon|tue|wed|thu|fri|sat|sun"))
        {
            for (var day = 0; day < DayNames.Length; day++)
            {
                if (!DayNames[day].StartsWith(match.Value, StringComparison.Ordinal)) continue;
                if (!found.Contains(day)) found.Add(day);
                break;
            }
        }

        if (found.Count == 0) return Array.Empty<int>();

        // «Monday through Friday» — диапазон, а не два дня подряд.
        if (found.Count == 2 && Regex.IsMatch(lower, @"(through|thru|to|-|–|—)"))
        {
            var range = Expand(found[0], found[1]);
            if (range.Count > 0) return range.ToArray();
        }

        return found.Distinct().OrderBy(value => value).ToArray();
    }

    private static List<int> Expand(int from, int to)
    {
        var result = new List<int>();
        var day = from;
        for (var step = 0; step < 7; step++)
        {
            result.Add(day);
            if (day == to) break;
            day = (day + 1) % 7;
        }

        return result.Count > 0 && result.Contains(to)
            ? result.Distinct().OrderBy(value => value).ToList()
            : new List<int>();
    }

    private static List<PriceLine> ParsePrices(string html, out List<string> models, out bool ok)
    {
        models = new List<string>();
        ok = false;
        var lines = new List<PriceLine>();

        var tableMatch = Regex.Match(html, @"<table.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!tableMatch.Success) return lines;

        var rows = new List<List<string>>();
        foreach (Match row in Regex.Matches(tableMatch.Value, @"<tr.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = new List<string>();
            foreach (Match cell in Regex.Matches(row.Value, @"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var value = WebUtility.HtmlDecode(Regex.Replace(cell.Groups[1].Value, "<[^>]+>", " "));
                value = Regex.Replace(value, @"\s+", " ").Trim();
                cells.Add(value);
            }

            if (cells.Count > 0) rows.Add(cells);
        }

        if (rows.Count == 0) return lines;

        // Заголовок: «MODEL | deepseek-flash | deepseek-v4-pro» (или «模型 | …»).
        foreach (var row in rows)
        {
            if (!row.Any(cell => cell.Equals("MODEL", StringComparison.OrdinalIgnoreCase) || cell.Contains("模型"))) continue;
            foreach (var cell in row.Skip(1))
            {
                if (cell.Length > 0 && !cell.StartsWith("BASE URL", StringComparison.OrdinalIgnoreCase)) models.Add(Clean(cell));
            }
            break;
        }

        string item = "";
        foreach (var row in rows)
        {
            var prices = row.Where(IsPrice).ToList();
            if (prices.Count == 0) continue;

            var tier = row.FirstOrDefault(cell =>
                cell.Equals("OFF-PEAK", StringComparison.OrdinalIgnoreCase) ||
                cell.Equals("PEAK", StringComparison.OrdinalIgnoreCase) ||
                cell.Contains("空闲") || cell.Contains("高峰")) ?? "";

            // Название строки — там, где сказано «1M … TOKENS» (или «百万tokens»);
            // «PRICING»/«价格» и сноски вроде «(3)» подписью строки не считаем.
            var label = row.FirstOrDefault(cell => cell.Contains("TOKENS", StringComparison.OrdinalIgnoreCase) || cell.Contains("tokens"))
                        ?? row.FirstOrDefault(cell =>
                            !IsPrice(cell) &&
                            !cell.Equals("OFF-PEAK", StringComparison.OrdinalIgnoreCase) &&
                            !cell.Equals("PEAK", StringComparison.OrdinalIgnoreCase) &&
                            !cell.Contains("空闲") && !cell.Contains("高峰") &&
                            !cell.StartsWith("PRICING", StringComparison.OrdinalIgnoreCase) &&
                            !cell.StartsWith("价格", StringComparison.Ordinal) &&
                            cell.Length > 0);

            if (!string.IsNullOrEmpty(label)) item = Clean(label);
            if (string.IsNullOrEmpty(item)) continue;

            lines.Add(new PriceLine { Item = item, Tier = tier, Prices = prices });
        }

        ok = lines.Count > 0;
        return lines;
    }

    private static bool IsPrice(string cell)
    {
        if (cell.StartsWith("$", StringComparison.Ordinal)) return true;
        return Regex.IsMatch(cell, @"^\d+(?:\.\d+)?\s*(?:元|¥)$");
    }

    /// <summary>Убирает сноску-номер вроде «deepseek-flash (1)».</summary>
    private static string Clean(string text)
    {
        return Regex.Replace(text, @"\s*\(\d+\)\s*$", "").Trim();
    }

    // --- сравнение и запись ------------------------------------------------

    private static string Format(List<PeakWindow> windows, int offsetMinutes)
    {
        if (windows.Count == 0) return "—";

        var order = new List<string>();
        var parts = new Dictionary<string, List<string>>();
        foreach (var window in windows)
        {
            var from = ToMinutes(window.From);
            var to = ToMinutes(window.To);
            var days = window.Days ?? Array.Empty<int>();

            string span;
            if (from < 0 || to < 0 || days.Length == 0)
            {
                // Время в окне не разобралось — показываем как есть, без перевода.
                span = window.From + "–" + window.To;
            }
            else
            {
                // Перевод в местное время может сдвинуть окно на соседние сутки:
                // тогда вместе со временем сдвигаются и дни недели.
                var shifted = from + offsetMinutes;
                days = LocalZone.ShiftDays(days, LocalZone.DayShift(shifted));
                span = LocalZone.Clock(shifted) + "–" + LocalZone.Clock(to + offsetMinutes);
            }

            var label = FormatDays(days);
            if (!parts.TryGetValue(label, out var list))
            {
                list = new List<string>();
                parts[label] = list;
                order.Add(label);
            }

            list.Add(span);
        }

        return string.Join("; ", order.Select(label => $"{label} {string.Join(", ", parts[label])}"))
               + $" ({LocalZone.Label(offsetMinutes)})";
    }

    private static int ToMinutes(string time)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return -1;
        }

        return parsed.Hour * 60 + parsed.Minute;
    }

    private static string FormatDays(int[] days)
    {
        var sorted = days.Distinct().OrderBy(day => day).ToArray();
        if (sorted.Length == 0) return "—";
        if (sorted.Length == 7) return Loc.T("peak.days.daily");
        if (sorted.Length == 5 && sorted[0] == 1 && sorted[4] == 5) return Loc.T("peak.days.workweek");
        if (sorted.Length == 2 && sorted[0] == 0 && sorted[1] == 6) return Loc.T("peak.days.weekend");
        return string.Join(", ", sorted.Select(day => Loc.T("peak.day." + day)));
    }

    private static bool SameWindows(List<PeakWindow> left, List<PeakWindow> right)
    {
        if (left.Count != right.Count) return false;

        string Key(PeakWindow window) =>
            string.Join(",", window.Days.OrderBy(day => day)) + "|" + Normalize(window.From) + "|" + Normalize(window.To);

        var a = left.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToList();
        var b = right.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToList();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static string Normalize(string time)
    {
        return TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("HH:mm", CultureInfo.InvariantCulture)
            : time;
    }

    private static List<PeakWindow> ReadCurrentWindows(AppPaths paths)
    {
        var result = new List<PeakWindow>();
        try
        {
            if (!File.Exists(paths.PricingPath)) return result;

            using var document = JsonDocument.Parse(File.ReadAllText(paths.PricingPath));
            if (!document.RootElement.TryGetProperty("peakWindows", out var windows)) return result;

            foreach (var window in windows.EnumerateArray())
            {
                var days = window.TryGetProperty("days", out var daysElement)
                    ? daysElement.EnumerateArray().Select(day => day.GetInt32()).ToArray()
                    : Array.Empty<int>();

                result.Add(new PeakWindow
                {
                    Days = days,
                    From = window.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "",
                    To = window.TryGetProperty("to", out var to) ? to.GetString() ?? "" : "",
                });
            }
        }
        catch
        {
            // Битый файл — считаем, что окон нет.
        }

        return result;
    }

    /// <summary>Записывает новые окна в pricing.json, сохранив прежний файл рядом.</summary>
    public string Apply(PricingCheckResult result)
    {
        if (result == null || !result.Ok || result.Windows.Count == 0)
        {
            return Loc.T("err.nothingToApply");
        }

        try
        {
            var offsetMinutes = LocalZone.OffsetMinutes(DateTime.UtcNow);
            var payload = new Dictionary<string, object>
            {
                ["checked"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["source"] = string.IsNullOrWhiteSpace(result.SourceUrl) ? EnglishUrl : result.SourceUrl,
                ["note"] = "Время в окнах — UTC, как на официальной странице цен; на экран выводится по местному "
                           + "времени машины. Дни: 0=вс, 1=пн, 2=вт, 3=ср, 4=чт, 5=пт, 6=сб. Пик — полная цена, "
                           + "всё остальное время вдвое дешевле. localOffsetMinutes — смещение этой машины "
                           + "в минутах на момент записи, только справка: показ считается от системы. "
                           + "Обновлено приложением DSH Panel.",
                ["localOffsetMinutes"] = offsetMinutes,
                ["peakWindows"] = result.Windows.Select(window => new Dictionary<string, object>
                {
                    ["days"] = window.Days,
                    ["from"] = window.From,
                    ["to"] = window.To,
                }).ToList(),
            };

            if (File.Exists(_paths.PricingPath))
            {
                File.Copy(_paths.PricingPath, _paths.PricingPath + ".bak", overwrite: true);
            }

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            File.WriteAllText(_paths.PricingPath, json + Environment.NewLine, new UTF8Encoding(false));
            return $"окна пика обновлены: {result.FoundText}";
        }
        catch (Exception error)
        {
            return "не удалось записать pricing.json: " + error.Message;
        }
    }

    private static string StripTags(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style).*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " "));
    }
}
