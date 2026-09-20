using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshTray;

public sealed class PeakState
{
    public bool InPeak { get; set; }
    public string StateText { get; set; } = "";
    public string NextText { get; set; } = "";
    public string WindowText { get; set; } = "";
    public string ScheduleText { get; set; } = "";
    public string Checked { get; set; } = "";
}

/// <summary>
/// Тарифные окна DeepSeek: где сейчас пик (полная цена), а где вне пика (вдвое
/// дешевле), и когда следующее переключение. Перенос dsh-peak.ps1; окна лежат
/// в pricing.json, время в нём — UTC, на экран выводится по местному.
/// </summary>
public sealed class PeakService
{
    private readonly string _path;
    private Plan _cache;
    private string _cacheKey = "";

    public PeakService(string pricingPath)
    {
        _path = pricingPath;
    }

    private sealed class Plan
    {
        public string Checked = Loc.T("peak.checked.noDate");
        public List<Window> Windows = new();
    }

    private sealed class Window
    {
        public int[] Days = Array.Empty<int>();
        public int From;
        public int To;
    }

    private sealed class PricingFile
    {
        [JsonPropertyName("checked")] public string Checked { get; set; }

        // Прежние версии писали сюда московское смещение и брали его для показа.
        // Теперь смещение спрашивается у системы (LocalZone), а поле в файле —
        // только справка; поэтому оно здесь намеренно не читается.
        [JsonPropertyName("peakWindows")] public List<PeakWindow> PeakWindows { get; set; }
    }

    private sealed class PeakWindow
    {
        [JsonPropertyName("days")] public int[] Days { get; set; }
        [JsonPropertyName("from")] public string From { get; set; }
        [JsonPropertyName("to")] public string To { get; set; }
    }

    // Если файла нет или он испорчен — работаем на этих значениях (пн–пт 01:00–04:00 и 06:00–10:00 UTC).
    private static Plan Fallback() => new()
    {
        Checked = Loc.T("peak.checked.builtin"),
        Windows = new List<Window>
        {
            new() { Days = new[] { 1, 2, 3, 4, 5 }, From = 60, To = 240 },
            new() { Days = new[] { 1, 2, 3, 4, 5 }, From = 360, To = 600 },
        },
    };

    private Plan GetPlan()
    {
        if (!File.Exists(_path)) return Fallback();

        FileInfo info;
        try
        {
            info = new FileInfo(_path);
        }
        catch
        {
            return Fallback();
        }

        var key = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        if (_cache != null && _cacheKey == key) return _cache;

        Plan plan;
        try
        {
            var data = JsonSerializer.Deserialize<PricingFile>(File.ReadAllText(_path));
            if (data?.PeakWindows == null || data.PeakWindows.Count == 0)
            {
                throw new InvalidOperationException(Loc.T("peak.err.noWindow"));
            }

            var windows = new List<Window>();
            foreach (var window in data.PeakWindows)
            {
                var from = ToMinutes(window.From);
                var to = ToMinutes(window.To);
                if (from < 0 || to < 0)
                {
                    throw new InvalidOperationException(Loc.T("peak.err.badTime", window.From, window.To));
                }
                var days = window.Days ?? Array.Empty<int>();
                if (days.Length == 0) throw new InvalidOperationException(Loc.T("peak.err.noDays"));
                windows.Add(new Window { Days = days, From = from, To = to });
            }

            plan = new Plan
            {
                Checked = string.IsNullOrWhiteSpace(data.Checked) ? Loc.T("peak.checked.noDate") : data.Checked,
                Windows = windows,
            };
        }
        catch
        {
            // Битый файл не должен ронять панель.
            var fallback = Fallback();
            fallback.Checked = Loc.T("peak.checked.unreadable");
            plan = fallback;
        }

        _cache = plan;
        _cacheKey = key;
        return plan;
    }

    public PeakState GetState(DateTime? at = null)
    {
        var plan = GetPlan();
        var utc = at?.ToUniversalTime() ?? DateTime.UtcNow;

        // Окна в файле — в UTC. На экран выводим по местному времени машины:
        // смещение спрашиваем у системы, а не берём из файла.
        var offsetMinutes = LocalZone.OffsetMinutes(utc);

        var intervals = new List<(DateTime Start, DateTime End)>();
        var baseDay = utc.Date;
        for (var shift = -1; shift <= 7; shift++)
        {
            var dayStart = baseDay.AddDays(shift);
            var weekday = (int)dayStart.DayOfWeek;
            foreach (var window in plan.Windows)
            {
                if (Array.IndexOf(window.Days, weekday) < 0) continue;
                var span = window.To - window.From;
                if (span <= 0) span += 1440;
                var start = dayStart.AddMinutes(window.From);
                intervals.Add((start, start.AddMinutes(span)));
            }
        }

        var inPeak = intervals.Any(interval => utc >= interval.Start && utc < interval.End);

        DateTime? next = null;
        foreach (var interval in intervals)
        {
            foreach (var boundary in new[] { interval.Start, interval.End })
            {
                if (boundary <= utc) continue;
                if (next == null || boundary < next) next = boundary;
            }
        }

        var stateText = Loc.T(inPeak ? "peak.state.peak" : "peak.state.offPeak");

        var nextText = "";
        if (next != null)
        {
            var minutes = (int)Math.Round((next.Value - utc).TotalMinutes);
            // Смещение считаем на сам момент переключения: через границу перевода
            // часов оно может отличаться от текущего.
            var localNext = LocalZone.Clock(next.Value.AddMinutes(LocalZone.OffsetMinutes(next.Value)));
            var what = Loc.T(inPeak ? "peak.next.offPeak" : "peak.next.peak");
            nextText = Loc.T("peak.next.line", what, localNext, FormatSpan(minutes));
        }

        return new PeakState
        {
            InPeak = inPeak,
            StateText = stateText,
            NextText = nextText,
            WindowText = BuildWindowText(plan, offsetMinutes),
            ScheduleText = BuildScheduleText(plan, offsetMinutes),
            Checked = plan.Checked,
        };
    }

    /// <summary>Окна без префикса: «пн–пт 04:00–07:00, 09:00–13:00 (UTC+03:00)».</summary>
    private static string BuildScheduleText(Plan plan, int offsetMinutes)
    {
        var labels = new List<string>();
        var parts = new Dictionary<string, List<string>>();
        foreach (var window in plan.Windows)
        {
            // Перевод в местное время может сдвинуть окно на соседние сутки —
            // тогда вместе со временем сдвигаются и дни недели.
            var from = window.From + offsetMinutes;
            var label = FormatDays(LocalZone.ShiftDays(window.Days, LocalZone.DayShift(from)));
            if (!parts.TryGetValue(label, out var list))
            {
                list = new List<string>();
                parts[label] = list;
                labels.Add(label);
            }
            list.Add($"{LocalZone.Clock(from)}–{LocalZone.Clock(window.To + offsetMinutes)}");
        }

        var rendered = labels.Select(label => $"{label} {string.Join(", ", parts[label])}");
        return $"{string.Join("; ", rendered)} ({LocalZone.Label(offsetMinutes)})";
    }

    private static string BuildWindowText(Plan plan, int offsetMinutes)
    {
        return Loc.T("peak.windowTitle", BuildScheduleText(plan, offsetMinutes));
    }

    private static int ToMinutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!match.Success) return -1;
        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hours < 24 && minutes < 60) return hours * 60 + minutes;
        return -1;
    }

    private static string FormatDays(int[] days)
    {
        var sorted = days.Distinct().OrderBy(day => day).ToArray();
        if (sorted.Length == 7) return Loc.T("peak.days.daily");
        if (sorted.Length == 5 && sorted[0] == 1 && sorted[4] == 5) return Loc.T("peak.days.workweek");
        if (sorted.Length == 2 && sorted[0] == 0 && sorted[1] == 6) return Loc.T("peak.days.weekend");
        return string.Join(", ", sorted.Select(day => Loc.T("peak.day." + day)));
    }

    private static string FormatSpan(int minutes)
    {
        if (minutes < 1) return Loc.T("peak.span.lessMinute");
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours == 0) return Loc.T("peak.span.minutes", rest);
        if (rest == 0) return Loc.T("peak.span.hours", hours);
        return Loc.T("peak.span.hoursMinutes", hours, rest);
    }
}
