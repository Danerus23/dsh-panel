using System.Globalization;
using DshPanel.Agents;
using DshPanel.Shell;

namespace DshPanel.Peak;

/// <summary>Что за переключение ждёт впереди: начало пика (дороже) или его конец (дешевле).</summary>
public enum PeakMoment
{
    /// <summary>Начнётся пик — цена вырастет.</summary>
    PeakStart,

    /// <summary>Пик кончится — станет вдвое дешевле.</summary>
    PeakEnd,
}

/// <summary>
/// Состояние тарифа на один момент времени. Записи, а не класса с полями: состояние сравнивается
/// в тестах и печатается в отчёт целиком.
/// </summary>
public readonly record struct PeakState(
    bool InPeak,
    DateTime? NextSwitchUtc,
    PeakMoment NextKind,
    int MinutesUntilNext,
    string ScheduleText,
    string Checked)
{
    public bool HasNext => NextSwitchUtc is not null;
}

/// <summary>
/// Тарифное окно агента, приведённое к МЕСТНОМУ времени: дни недели, на которые оно попадает
/// после сдвига, и границы часами. Одна форма на все читаемые виды расписания — и на строку,
/// и на таблицу по дням: два пересчёта одних и тех же окон однажды разошлись бы.
/// </summary>
public readonly record struct LocalPeakWindow(IReadOnlyList<int> Days, string From, string To);

/// <summary>
/// Окна пика — ЧИСТЫЕ функции от профиля агента и момента времени.
///
/// Перенесено из v1 (<c>PeakService.cs</c>) как РЕШЕНИЯ, а не как код: там же остались тексты
/// на трёх языках и кеш файла, здесь — только «в пике ли мы» и «когда следующее переключение».
///
/// Что важно и почему:
///
/// * **окна хранятся в UTC** — так их публикует агент, и так их нельзя перепутать при переводе
///   часов: смещение спрашивается у системы на КАЖДЫЙ момент (<see cref="TimeZoneInfo"/>),
///   а не берётся один раз;
/// * **перевод в местное время может сдвинуть окно на соседние сутки** — тогда вместе со временем
///   сдвигаются и дни недели, иначе «пн 01:00 UTC» показывалось бы как «пн 04:00» у человека,
///   у которого это уже вторник;
/// * **следующее переключение ищется не только вперёд по сегодняшнему дню**, а по неделе вперёд:
///   в пятницу вечером следующее начало пика — в понедельник, и панель обязана это знать,
///   иначе предупреждение «скоро пик» не сработает никогда;
/// * **китайские праздники не учитываются** — см. предупреждение в <see cref="AgentCatalog.DeepSeek"/>.
/// </summary>
public static class PeakDecisions
{
    /// <summary>Насколько вперёд ищем переключения: неделя с запасом в сутки.</summary>
    private const int SearchDaysAhead = 8;

    public static PeakState State(AgentProfile agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var utc = now.UtcDateTime;
        var offset = OffsetMinutes(now);

        var intervals = Intervals(agent, utc);
        var inPeak = intervals.Any(i => utc >= i.Start && utc < i.End);

        DateTime? next = null;
        foreach (var interval in intervals)
        {
            foreach (var boundary in new[] { interval.Start, interval.End })
            {
                if (boundary <= utc) continue;
                if (next is null || boundary < next) next = boundary;
            }
        }

        var kind = PeakMoment.PeakStart;
        var minutes = 0;

        if (next is not null)
        {
            minutes = (int)Math.Round((next.Value - utc).TotalMinutes);

            // Вид переключения определяется тем, что происходит ПОСЛЕ него: если следующий
            // интервал начинается в этот момент — будет пик, если кончается — станет дешевле.
            kind = intervals.Any(i => i.Start == next.Value) ? PeakMoment.PeakStart : PeakMoment.PeakEnd;
        }

        return new PeakState(
            InPeak: inPeak,
            NextSwitchUtc: next,
            NextKind: kind,
            MinutesUntilNext: minutes,
            ScheduleText: ScheduleText(agent, offset),
            Checked: agent.PricingChecked);
    }

    /// <summary>
    /// Расписание словами, по местному времени: «пн–пт 04:00–07:00, 09:00–13:00 (UTC+03:00)».
    /// Окна с одинаковыми днями собираются в одну строку — так их и читает человек.
    /// </summary>
    public static string ScheduleText(AgentProfile agent, int offsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return WindowsText(agent.PeakWindows, offsetMinutes);
    }

    /// <summary>
    /// ТЕ ЖЕ окна, но взятые не из профиля агента, а откуда угодно: так их показывает история цен
    /// (запись об изменении несёт окна со страницы). Второй сборки той же строки быть не должно —
    /// иначе «было 01:00–04:00, стало …» в истории читалось бы не так, как расписание в окне пиков.
    /// </summary>
    public static string WindowsText(IReadOnlyList<PeakWindow> windows, int offsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var labels = new List<string>();
        var parts = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var window in windows)
        {
            var local = Local(window, offsetMinutes);
            var label = DayLabel(local.Days);

            if (!parts.TryGetValue(label, out var list))
            {
                list = new List<string>();
                parts[label] = list;
                labels.Add(label);
            }

            list.Add($"{local.From}–{local.To}");
        }

        var rendered = labels.Select(label => $"{label} {string.Join(", ", parts[label])}");
        return $"{string.Join("; ", rendered)} ({ZoneLabel(offsetMinutes)})";
    }

    /// <summary>
    /// Откуда взяты окна: дата проверки, источник и само расписание одной строкой. Живёт здесь,
    /// а не в контроллере: этим текстом говорят ДВА места — окно панели и значок, — и второе
    /// составление той же строки однажды разошлось бы с первым (сегодня им пользуются ещё
    /// и настройки, и окно «Пики и тарифы»: строка «откуда взяты окна» в таблице пиков).
    ///
    /// ⚠️ Дата идёт через <see cref="CheckedText"/>, а не сырым полем профиля: в профиле она
    /// лежит по-машинному (<c>2026-09-24</c>), и та же дата на одном экране читалась бы как
    /// «проверено 2026-09-24», а на другом — «проверено 24.09.2026». Одна вещь — одно написание,
    /// тем более что владелец назвал именно «24.09.2026».
    /// </summary>
    public static string SourceText(AgentProfile agent, int offsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.PeakScheduleSourceFormat,
            CheckedText(agent),
            agent.PricingSource,
            ScheduleText(agent, offsetMinutes));
    }

    /// <summary>
    /// КОГДА таблица цен проверена — в том виде, в каком это читает человек: «24.09.2026».
    ///
    /// Зачем отдельно от <see cref="SourceText"/>. Владелец 27.09.2026 сказал про главное окно:
    /// *«Там где написано „окна пика“, проверено и дата с временем нужно… остальная информация
    /// не нужна в принципе»*. То есть в главном окне остаётся ОДНА дата, а источник, расписание
    /// и дни недели живут в своём окне — там <see cref="SourceText"/> и работает.
    ///
    /// ⚠️ В профиле агента лежит только ДАТА (<c>PricingChecked</c>, сегодня ISO <c>2026-09-24</c>);
    /// ВРЕМЕНИ проверки в нём НЕТ. Пример владельца («24.09.2026, 19:40») время содержит, но панель
    /// его не знает, а выдумывать нельзя: показана будет дата, и это названо прямо.
    ///
    /// Разобрать дату не удалось (профиль правят руками) — возвращаем строку КАК ЕСТЬ: пустая
    /// строка, мусор и всё прочее остаются собой. Ничего не выдумываем и не падаем.
    /// </summary>
    public static string CheckedText(AgentProfile agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return DateTime.TryParse(
            agent.PricingChecked,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)
            : agent.PricingChecked;
    }

    /// <summary>
    /// Когда следующее переключение тарифа — по местному времени: «Пик начнётся через 2 ч 15 мин
    /// (13:00)». Пусто, когда переключения впереди нет.
    ///
    /// Момент переводится в местное время со смещением НА САМ МОМЕНТ (см. <see cref="OffsetMinutesAt"/>):
    /// через границу перевода часов это другое число, а показать надо тот час, который будет на часах.
    /// </summary>
    public static string NextSwitchText(PeakState state)
    {
        // Момент — в локальную переменную: у свойства-записи компилятор не может доказать,
        // что между проверкой и чтением значение не изменилось (CS8629 иначе).
        var next = state.NextSwitchUtc;
        if (next is null) return string.Empty;

        var what = state.NextKind == PeakMoment.PeakStart
            ? PanelStrings.PeakNextStartFormat
            : PanelStrings.PeakNextEndFormat;

        var at = LocalClock(next.Value, OffsetMinutesAt(next.Value));

        return string.Format(
            CultureInfo.CurrentCulture, what, FormatSpan(state.MinutesUntilNext), at);
    }

    /// <summary>
    /// Одно окно, приведённое к местному времени: дни недели после сдвига и границы часами.
    /// Сдвиг берётся по НАЧАЛУ окна и переносит вместе с ним дни недели — иначе «пн 01:00 UTC»
    /// показывалось бы понедельником у человека, у которого это уже воскресенье.
    /// </summary>
    public static LocalPeakWindow Local(PeakWindow window, int offsetMinutes)
    {
        var from = window.FromMinutes + offsetMinutes;

        return new LocalPeakWindow(
            ShiftDays(window.Days, DayShift(from)),
            Clock(from),
            Clock(window.ToMinutes + offsetMinutes));
    }

    /// <summary>
    /// «2 ч 15 мин» — длительность для предупреждения и для строки «когда дешевле».
    ///
    /// Строки берутся ОБЩИЕ (<see cref="PanelStrings.SpanHoursFormat"/> и соседние): длительность
    /// словами — одна сущность на всю панель, и своих слов у пиков быть не должно. Пики просто
    /// первый её потребитель; второй — часы автокопии (<c>Backup\BackupSchedule</c>).
    /// </summary>
    public static string FormatSpan(int minutes)
    {
        if (minutes < 1) return PanelStrings.SpanLessMinute;

        var hours = minutes / 60;
        var rest = minutes % 60;

        if (hours == 0) return string.Format(CultureInfo.CurrentCulture, PanelStrings.SpanMinutesFormat, rest);
        if (rest == 0) return string.Format(CultureInfo.CurrentCulture, PanelStrings.SpanHoursFormat, hours);

        return string.Format(CultureInfo.CurrentCulture, PanelStrings.SpanHoursMinutesFormat, hours, rest);
    }

    /// <summary>
    /// Смещение местного времени на конкретный момент (передаётся местное время с его смещением).
    /// Так смещение и берётся у системы: записанного часового пояса в панели нет нигде —
    /// ни в настройках, ни в профиле агента. В v1 часовой пояс лежал ещё и в `pricing.json`
    /// («московское смещение»), и его приходилось игнорировать.
    /// </summary>
    public static int OffsetMinutes(DateTimeOffset at) => (int)at.Offset.TotalMinutes;

    /// <summary>
    /// Смещение местного времени на конкретный момент UTC — у системы спрашивается ЗАНОВО.
    ///
    /// Зачем отдельно от <see cref="OffsetMinutes"/>: у зон с переводом часов смещение по разные
    /// стороны границы разное, а момент следующего переключения пика может лежать уже за границей.
    /// Взять «текущее» смещение и применить его к моменту через две недели — значит показать час,
    /// которого в расписании нет. В v1 это правило записано прямо: «Смещение считаем на сам момент
    /// переключения: через границу перевода часов оно может отличаться от текущего».
    /// </summary>
    public static int OffsetMinutesAt(DateTime utc) =>
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).TotalMinutes;

    /// <summary>Название местного часового пояса — для отчёта проверки: видно, что он определён сам.</summary>
    public static string LocalZoneName() => TimeZoneInfo.Local.DisplayName;

    /// <summary>Часы и минуты по местному времени для подписи момента переключения.</summary>
    public static string LocalClock(DateTime utc, int offsetMinutes) => Clock(MinutesOfDay(utc) + offsetMinutes);

    // --- внутреннее ---------------------------------------------------------

    private readonly record struct Interval(DateTime Start, DateTime End);

    private static List<Interval> Intervals(AgentProfile agent, DateTime utc)
    {
        var intervals = new List<Interval>();
        var baseDay = utc.Date;

        // От вчера до недели вперёд: окно, начавшееся вчера, может ещё идти, а следующее
        // переключение в пятницу вечером — только в понедельник.
        for (var shift = -1; shift <= SearchDaysAhead; shift++)
        {
            var dayStart = baseDay.AddDays(shift);
            var weekday = (int)dayStart.DayOfWeek;

            foreach (var window in agent.PeakWindows)
            {
                if (!window.Days.Contains(weekday)) continue;

                var span = window.ToMinutes - window.FromMinutes;
                if (span <= 0) span += 1440;

                var start = dayStart.AddMinutes(window.FromMinutes);
                intervals.Add(new Interval(start, start.AddMinutes(span)));
            }
        }

        return intervals;
    }

    private static int MinutesOfDay(DateTime utc) => utc.Hour * 60 + utc.Minute;

    private static string Clock(int minutes)
    {
        var normalized = ((minutes % 1440) + 1440) % 1440;
        return $"{normalized / 60:D2}:{normalized % 60:D2}";
    }

    private static int DayShift(int minutesAfterShift) => (int)Math.Floor(minutesAfterShift / 1440.0);

    /// <summary>Дни недели после сдвига окна на сутки (или назад).</summary>
    private static int[] ShiftDays(IReadOnlyList<int> days, int shift)
    {
        if (shift == 0) return days.ToArray();

        return days.Select(day => ((day + shift) % 7 + 7) % 7).Distinct().OrderBy(day => day).ToArray();
    }

    /// <summary>«пн–пт», «выходные», «каждый день» или перечисление — как в v1.</summary>
    private static string DayLabel(IReadOnlyList<int> days)
    {
        var sorted = days.Distinct().OrderBy(day => day).ToArray();

        if (sorted.Length == 7) return PanelStrings.PeakDaysDaily;
        if (sorted.Length == 5 && sorted[0] == 1 && sorted[4] == 5) return PanelStrings.PeakDaysWorkWeek;
        if (sorted.Length == 2 && sorted[0] == 0 && sorted[1] == 6) return PanelStrings.PeakDaysWeekend;

        return string.Join(", ", sorted.Select(DayName));
    }

    /// <summary>
    /// Название дня недели. Открыто наружу, потому что им говорит не только строка расписания:
    /// таблица окон по дням называет те же семь дней, и второго набора имён быть не должно.
    /// </summary>
    public static string DayName(int day) => day switch
    {
        0 => PanelStrings.PeakDaySunday,
        1 => PanelStrings.PeakDayMonday,
        2 => PanelStrings.PeakDayTuesday,
        3 => PanelStrings.PeakDayWednesday,
        4 => PanelStrings.PeakDayThursday,
        5 => PanelStrings.PeakDayFriday,
        _ => PanelStrings.PeakDaySaturday,
    };

    /// <summary>Название местного часового пояса в виде «UTC+03:00» — по нему читают расписание.</summary>
    public static string ZoneLabel(int offsetMinutes)
    {
        var sign = offsetMinutes < 0 ? "-" : "+";
        var absolute = Math.Abs(offsetMinutes);
        return $"UTC{sign}{absolute / 60:D2}:{absolute % 60:D2}";
    }
}
