using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DshPanel.Agents;
using DshPanel.Shell;

namespace DshPanel.Pricing;

/// <summary>
/// РАЗБОР СТРАНИЦЫ ЦЕН — ЧИСТАЯ функция от текста страницы: ни сети, ни файлов, ни настроек.
///
/// Зачем отдельно от клиента. Живой запрос в проверках делать нельзя (сеть от имени владельца —
/// это его право, а не наше), а разбор обязан быть проверен ПО НАСТОЯЩЕЙ странице. Поэтому
/// разбор отдан функции, которой на вход дают ЗАПИСАННУЮ страницу: проверки гоняют её на копии,
/// снятой панелью 1.x, а клиент лишь приносит тот же текст из сети.
///
/// **Знание перенесено из панели 1.x** (<c>dsh-tray\PricingUpdateService.cs</c>), и это не
/// копирование кода, а перенос опыта: там разбор дважды ломался на переписанной странице
/// («в сентябре 2026 обе версии переписали»), и оба раза причина была одна — разбор ждал
/// определённого порядка слов. Здесь разбор ищет ФОРМУ (число с денежным знаком, подпись
/// единицы, пометку тарифа), а не фразу целиком:
///
/// * цены берутся из той же таблицы, где названы модели, — по денежному знаку в ячейке;
/// * окна пика читаются из фразы про пик и из фразы с «北京时间» (пекинское время = UTC+8);
/// * заголовки единиц («1M INPUT TOKENS (CACHE HIT)») остаются СЛОВАМИ СТРАНИЦЫ: переводить
///   их значило бы выдумывать названия, которых на странице нет.
///
/// ⚠️ **Чего разбор не делает:** не пересчитывает валюту (курса панель не знает), не достраивает
/// пропущенное и не падает на незнакомой странице — он честно отвечает «не разобралось»,
/// а причину показывает человеку словами.
/// </summary>
public static class PricingPage
{
    /// <summary>Денежный знак впереди или единица «元» позади — по ним ячейка и признаётся ценой.</summary>
    private static readonly Regex PriceSign = new(@"[$¥]", RegexOptions.CultureInvariant);

    /// <summary>Пометка дешёвого тарифа на обоих языках страницы.</summary>
    private static readonly Regex OffPeakTier = new(@"(off-peak|空闲)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Пометка дорогого тарифа. ⚠️ Проверяется ВТОРОЙ: «OFF-PEAK» содержит «PEAK».</summary>
    private static readonly Regex PeakTier = new(@"(peak|高峰)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Подпись строки-единицы: «1M INPUT TOKENS …», «百万tokens输入…». Её и печатает страница.</summary>
    private static readonly Regex TokensLabel = new("tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Пара часов «01:00 - 04:00» и родственные тире страницы.</summary>
    private static readonly Regex ClockSpan = new(
        @"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})", RegexOptions.CultureInvariant);

    private static readonly string[] DayNames =
    {
        "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
    };

    private static readonly Dictionary<char, int> ChineseDays = new()
    {
        ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['日'] = 0, ['天'] = 0,
    };

    /// <summary>
    /// Разобрать страницу цен. Язык нужен ровно для одного — какой знак валюты поставить перед
    /// числом; само число берётся СО СТРАНИЦЫ и не пересчитывается.
    /// </summary>
    public static PricingResult Parse(string html, string language, string url, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return PricingResult.Failed(PanelStrings.PricePageEmpty, url, language, now);
        }

        var table = Regex.Match(html, @"<table.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!table.Success)
        {
            return PricingResult.Failed(PanelStrings.PriceTableMissing, url, language, now);
        }

        var rows = Rows(table.Value);
        var prices = PriceRows(rows, language);

        if (prices.Count == 0)
        {
            return PricingResult.Failed(PanelStrings.PriceTableMissing, url, language, now);
        }

        return new PricingResult(
            Ok: true,
            Error: string.Empty,
            SourceUrl: url,
            Language: language,
            Model: FirstModel(rows),
            Rows: prices,
            Windows: Windows(html),
            CheckedAt: now);
    }

    /// <summary>
    /// Название модели, для которой напечатаны цены. Страница объявляет НЕСКОЛЬКО моделей, и панель
    /// не вправе выбирать между ними молча: цены показываются по ПЕРВОЙ колонке (она же первая
    /// в шапке «MODEL»), а её имя стоит рядом с ценами словами.
    /// </summary>
    public static string FirstModel(IReadOnlyList<List<string>> rows)
    {
        foreach (var row in rows)
        {
            if (!row.Any(cell => cell.Equals("MODEL", StringComparison.OrdinalIgnoreCase) || cell.Contains("模型")))
            {
                continue;
            }

            foreach (var cell in row.Skip(1))
            {
                var cleaned = Clean(cell);
                if (cleaned.Length == 0) continue;
                if (cleaned.StartsWith("BASE URL", StringComparison.OrdinalIgnoreCase)) continue;

                return cleaned;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Строки цен: единица страницы и её цена вне пика и в пик. Порядок — как на странице.
    ///
    /// Единица «держится» до следующей: страница печатает её один раз на два ряда тарифа
    /// (в разметке это <c>rowspan</c>, а в разобранном виде — пустое место в первой ячейке).
    /// Берётся ПЕРВАЯ цена строки — то есть цена первой модели, той самой, что названа в
    /// <see cref="FirstModel"/>.
    /// </summary>
    public static List<PriceRow> PriceRows(IReadOnlyList<List<string>> rows, string language)
    {
        var order = new List<string>();
        var offPeak = new Dictionary<string, string>(StringComparer.Ordinal);
        var peak = new Dictionary<string, string>(StringComparer.Ordinal);

        var item = string.Empty;

        foreach (var row in rows)
        {
            var value = row.Select(PriceValue).FirstOrDefault(candidate => candidate is not null);
            if (value is null) continue;

            var label = row.FirstOrDefault(cell => TokensLabel.IsMatch(cell));
            if (!string.IsNullOrWhiteSpace(label)) item = Clean(label);
            if (item.Length == 0) continue;

            if (!offPeak.ContainsKey(item) && !peak.ContainsKey(item)) order.Add(item);

            var text = PricingDecisions.Currency(language) + value;
            var tiers = string.Join(" ", row);

            if (OffPeakTier.IsMatch(tiers)) offPeak[item] = text;
            else if (PeakTier.IsMatch(tiers)) peak[item] = text;
        }

        return order
            .Select(name => new PriceRow(
                name,
                offPeak.TryGetValue(name, out var cheap) ? cheap : PanelStrings.PriceValueMissing,
                peak.TryGetValue(name, out var dear) ? dear : PanelStrings.PriceValueMissing))
            .ToList();
    }

    /// <summary>
    /// Число из ячейки цены — БЕЗ знака валюты: знак панель ставит сама, по языку
    /// (<see cref="PricingDecisions.Currency"/>), потому что страница нужного языка уже дала
    /// число в своей валюте. Ячейка без денежного признака («1M», «2500», «384K») ценой не является
    /// — иначе в цены попало бы всё подряд.
    /// </summary>
    public static string? PriceValue(string cell)
    {
        var text = (cell ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        var marked = PriceSign.IsMatch(text) || text.Contains('元');
        if (!marked) return null;

        var digits = new string(text.Where(character => char.IsDigit(character) || character is '.' or ',').ToArray());

        return digits.Length == 0 ? null : digits;
    }

    /// <summary>
    /// ОКНА ПИКА СО СТРАНИЦЫ. Читаются обе языковые версии: английская фраза про пик называет
    /// время в UTC, китайская — в пекинском (UTC+8), и разница снимается сдвигом.
    ///
    /// Пусто означает «на этой странице фразы про пик нет» — и это честный ответ: страница цен
    /// вправе переписать формулировку, а панель не вправе достроить окна за неё.
    /// </summary>
    public static IReadOnlyList<PeakWindow> Windows(string html)
    {
        var text = StripTags(html);

        var english = Regex.Match(
            text,
            @"Peak hours[^.]{0,60}?(?<times>\d{1,2}:\d{2}\s*[-–—]\s*\d{1,2}:\d{2}[^.,;]*?)\s+UTC,\s*(?<days>[^.;()]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (english.Success)
        {
            return Build(english.Groups["times"].Value, english.Groups["days"].Value, shiftHours: 0);
        }

        var at = text.IndexOf("北京时间", StringComparison.Ordinal);
        if (at < 0) return Array.Empty<PeakWindow>();

        var window = text.Substring(at, Math.Min(200, text.Length - at));
        var times = ClockSpan.Match(window);
        if (!times.Success) return Array.Empty<PeakWindow>();

        // Пекинское время — UTC+8: в UTC вычитаем восемь часов. Дни стоят ПЕРЕД часами, поэтому
        // в сборку они уходят из начала отрывка, а часы — с места совпадения.
        return Build(window[times.Index..], window[..times.Index], shiftHours: -8);
    }

    /// <summary>Строки таблицы и ячейки в них — без тегов и лишних пробелов.</summary>
    private static List<List<string>> Rows(string table)
    {
        var rows = new List<List<string>>();

        foreach (Match row in Regex.Matches(table, @"<tr.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = new List<string>();

            foreach (Match cell in Regex.Matches(row.Value, @"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var value = WebUtility.HtmlDecode(Regex.Replace(cell.Groups[1].Value, "<[^>]+>", " "));
                cells.Add(Regex.Replace(value, @"\s+", " ").Trim());
            }

            if (cells.Count > 0) rows.Add(cells);
        }

        return rows;
    }

    /// <summary>Убирает сноску-номер вроде «deepseek-flash (1)» — как это делала панель 1.x.</summary>
    private static string Clean(string text) =>
        Regex.Replace(text ?? string.Empty, @"\s*\(\d+\)\s*$", string.Empty).Trim();

    /// <summary>
    /// Собрать окна: <paramref name="times"/> — часы, <paramref name="days"/> — подпись дней,
    /// <paramref name="shiftHours"/> — на сколько часов сдвинуть, чтобы получить UTC.
    /// </summary>
    private static List<PeakWindow> Build(string times, string days, int shiftHours)
    {
        var dayList = Days(days);
        var result = new List<PeakWindow>();

        if (dayList.Length == 0) return result;

        foreach (Match span in ClockSpan.Matches(times))
        {
            var from = (int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture) * 60)
                     + int.Parse(span.Groups[2].Value, CultureInfo.InvariantCulture);
            var to = (int.Parse(span.Groups[3].Value, CultureInfo.InvariantCulture) * 60)
                   + int.Parse(span.Groups[4].Value, CultureInfo.InvariantCulture);

            var shiftedFrom = from + (shiftHours * 60);
            var shiftedTo = to + (shiftHours * 60);

            // Сдвиг может увести окно на соседние сутки — тогда вместе с ним двигаются и дни
            // недели; иначе «пн 01:00 UTC» показывалось бы понедельником у человека, у которого
            // это уже воскресенье.
            var dayShift = (int)Math.Floor(shiftedFrom / 1440.0);

            result.Add(new PeakWindow(Shift(dayList, dayShift), Wrap(shiftedFrom), Wrap(shiftedTo)));
        }

        return result;
    }

    private static int Wrap(int minutes) => ((minutes % 1440) + 1440) % 1440;

    private static int[] Shift(int[] days, int shift)
    {
        if (shift == 0) return days;

        return days.Select(day => ((day + shift) % 7 + 7) % 7).Distinct().OrderBy(day => day).ToArray();
    }

    /// <summary>
    /// Дни недели из подписи: «Monday through Friday», «weekdays», «周末», «周一至周五» — те же
    /// случаи, что разбирала панель 1.x, включая перечисление и диапазон.
    /// </summary>
    private static int[] Days(string text)
    {
        var lower = (text ?? string.Empty).ToLowerInvariant();

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

    private static string StripTags(string html)
    {
        var withoutScripts = Regex.Replace(
            html, @"<(script|style).*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutScripts, "<[^>]+>", " ");

        return WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " "));
    }
}
