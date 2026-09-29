using System.Globalization;
using DshPanel.Agents;
using DshPanel.Shell;

namespace DshPanel.Peak;

/// <summary>
/// Строка первой таблицы: день недели и ДВА его ответа по МЕСТНОМУ времени — окна дорогого
/// времени и окна дешёвого.
///
/// ⚠️ **Состав записи изменён 27.09.2026 по замечанию владельца** («табличка пиков собрана
/// неверно — сделать ДВЕ таблицы»). Прежде здесь было «день · окна · цена», и цена дня словами
/// («пик и вне пика») стояла рядом с расписанием — то есть таблица отвечала на два разных вопроса
/// сразу и не отвечала ни на один. Теперь расписание отвечает таблица «График пиков» (эта запись),
/// а цену показывает своя таблица «Стоимость» — из данных страницы цен.
/// </summary>
public readonly record struct PeakTableRow(string Day, string PeakWindows, string OffPeakWindows, bool HasPeak);

/// <summary>
/// ЧИТАЕМАЯ ФОРМА окон пика: таблица по дням недели, её заголовок и слова о том, что сейчас.
///
/// Зачем отдельно от <see cref="PeakDecisions"/>. Строка расписания («пн–пт 04:00–07:00,
/// 09:00–13:00 (UTC+03:00)») отвечает на вопрос «какие вообще есть окна», но не отвечает
/// на два других: «а в субботу что?» и «где дороже, а где дешевле?». Жалоба владельца
/// (просмотр живьём, 27.09.2026): *«не хватает формы запроса окон пиков и тарифов, там было
/// сделано удобно чтобы можно было прочитать»*. Поэтому те же самые окна раскладываются
/// по семи дням — по одному ответу на день.
///
/// **Ничего не выдумывается и в сеть не ходит.** Дни, часы и дни недели считает
/// <see cref="PeakDecisions"/> из профиля агента (<see cref="AgentProfile.PeakWindows"/>),
/// а слова берутся из словаря. Здесь — только раскладка по дням.
///
/// ⚠️ **Часы только местные.** В профиле окна лежат в UTC (так их публикует агент), и показать
/// их как есть значило бы соврать человеку про его собственные часы: у него «01:00 UTC» —
/// это 04:00 на стенке. Перевод делает <see cref="PeakDecisions.Local"/>, и он же двигает
/// дни недели, когда окно уезжает за полночь.
///
/// ⚠️ **Окна дешёвого времени — ВЫЧИСЛЕННАЯ обратная сторона пика**, а не второй источник: пик
/// занимает часть суток, всё остальное в этих сутках дешевле. Считать это второй таблицей
/// значило бы однажды показать «вне пика» там, где по расписанию пик.
/// </summary>
public static class PeakTable
{
    /// <summary>
    /// Дни недели в том порядке, в каком их читает человек: неделя начинается с понедельника.
    ///
    /// ⚠️ Порядок задан ЯВНО, а не взят у <see cref="DayOfWeek"/>: там неделя отсчитывается
    /// от воскресенья, и таблица начиналась бы с него.
    /// </summary>
    private static readonly int[] Week = { 1, 2, 3, 4, 5, 6, 0 };

    /// <summary>Сколько минут в сутках: граница, по которую считаются и пик, и его обратная сторона.</summary>
    private const int DayMinutes = 1440;

    /// <summary>
    /// Семь строк — по одной на день недели, от понедельника к воскресенью. В каждой строке:
    /// название дня, окна пика и окна дешёвого времени по местному времени.
    ///
    /// День без окон пика говорит это СЛОВАМИ в своей колонке («нет»), а обратная сторона такого
    /// дня — «вне пика весь день»: пустое место читалось бы как «не посчитали».
    /// </summary>
    public static IReadOnlyList<PeakTableRow> Rows(AgentProfile agent, int offsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var rows = new List<PeakTableRow>(Week.Length);

        foreach (var day in Week)
        {
            var peak = Spans(agent, offsetMinutes, day);
            var offPeak = OffPeakSpans(agent, offsetMinutes, day);

            rows.Add(new PeakTableRow(
                PeakDecisions.DayName(day),
                peak.Count > 0 ? Join(peak) : PanelStrings.PeakTableNoPeak,
                peak.Count == 0
                    ? PanelStrings.PeakTableOffPeakAllDay
                    : offPeak.Count > 0
                        ? Join(offPeak)
                        : PanelStrings.PeakTableNoPeak,
                peak.Count > 0));
        }

        return rows;
    }

    /// <summary>
    /// ОКНА ПИКА ЭТОГО ДНЯ по местному времени — единственный источник и для колонки «Пик»,
    /// и для обратной стороны. Сдвиг берётся по началу окна и переносит вместе с ним дни недели
    /// (см. <see cref="PeakDecisions.Local"/>).
    /// </summary>
    public static IReadOnlyList<(int From, int To)> Spans(AgentProfile agent, int offsetMinutes, int day)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var found = new List<(int From, int To)>();

        foreach (var window in agent.PeakWindows)
        {
            var shiftedFrom = window.FromMinutes + offsetMinutes;
            var span = window.ToMinutes - window.FromMinutes;
            if (span <= 0) span += DayMinutes;

            var dayShift = (int)Math.Floor(shiftedFrom / (double)DayMinutes);
            var localDay = ((day - dayShift) % 7 + 7) % 7;

            if (!window.Days.Contains(localDay)) continue;

            var start = Wrap(shiftedFrom);
            var end = start + span;

            // Окно, перешедшее за полночь, — ДВА отрезка внутри суток, и оба обязаны быть в ответе:
            // иначе вторая половина окна попала бы в «дешёвое время» и панель показала бы неправду.
            if (end <= DayMinutes) found.Add((start, end));
            else
            {
                found.Add((start, DayMinutes));
                found.Add((0, end - DayMinutes));
            }
        }

        return found.OrderBy(span => span.From).ToList();
    }

    /// <summary>
    /// ОКНА ДЕШЁВОГО ВРЕМЕНИ ЭТОГО ДНЯ: сутки минус окна пика. Считается ОТ <see cref="Spans"/>,
    /// а не по своему правилу: второго расчёта расписания в панели быть не должно.
    ///
    /// Пустой ответ означает «дешевле в этих сутках не бывает» — день целиком занят пиком.
    /// </summary>
    public static IReadOnlyList<(int From, int To)> OffPeakSpans(AgentProfile agent, int offsetMinutes, int day)
    {
        var busy = Spans(agent, offsetMinutes, day).OrderBy(span => span.From).ToList();
        var merged = new List<(int From, int To)>();

        foreach (var span in busy)
        {
            if (merged.Count > 0 && span.From <= merged[^1].To)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, span.To));
                continue;
            }

            merged.Add(span);
        }

        var free = new List<(int From, int To)>();
        var cursor = 0;

        foreach (var span in merged)
        {
            if (span.From > cursor) free.Add((cursor, span.From));
            cursor = Math.Max(cursor, span.To);
        }

        if (cursor < DayMinutes) free.Add((cursor, DayMinutes));

        return free;
    }

    /// <summary>Заголовок таблицы с названием часового пояса: без него «04:00» читалось бы как «на твоих часах».</summary>
    public static string Heading(int offsetMinutes) => string.Format(
        CultureInfo.CurrentCulture,
        PanelStrings.PeakTableHeadingFormat,
        PeakDecisions.ZoneLabel(offsetMinutes));

    /// <summary>
    /// Где дороже, а где дешевле — ОДНОЙ строкой из готовых слов панели: «Пик: полная цена ·
    /// Вне пика: вдвое дешевле». Своей формулировки у этих двух ответов быть не должно: те же
    /// слова стоят в окне панели и в подсказке значка, и второй их набор однажды разошёлся бы.
    /// </summary>
    public static string Legend() => PanelStrings.PeakInPeak + " · " + PanelStrings.PeakOffPeak;

    /// <summary>Что сейчас: полная цена или вдвое дешевле. Те же слова, что в окне панели и в значке.</summary>
    public static string NowText(PeakState state) =>
        state.InPeak ? PanelStrings.PeakInPeak : PanelStrings.PeakOffPeak;

    /// <summary>Когда переключение — словами и по местному времени (пусто, если впереди его нет).</summary>
    public static string NextText(PeakState state) => PeakDecisions.NextSwitchText(state);

    /// <summary>
    /// КОРОТКАЯ СТРОКА ПОД ТАБЛИЦЕЙ ГРАФИКА: «сейчас: вне пика, до 04:00» — где человек находится
    /// прямо сейчас и до каких пор. Просил её владелец (замечание 27.09.2026) — чтобы «человек
    /// сразу видел, где он сейчас», не сверяя семь строк таблицы с часами на стенке.
    ///
    /// Переключения впереди нет — строка остаётся без времени: выдумать час было бы хуже, чем
    /// сказать «сейчас» без «до».
    /// </summary>
    public static string NowLine(PeakState state)
    {
        var what = state.InPeak ? PanelStrings.PeakShortInPeak : PanelStrings.PeakShortOffPeak;
        var next = state.NextSwitchUtc;

        if (next is null)
        {
            return string.Format(CultureInfo.CurrentCulture, PanelStrings.PeakNowLineNoTimeFormat, what);
        }

        var at = PeakDecisions.LocalClock(next.Value, PeakDecisions.OffsetMinutesAt(next.Value));

        return string.Format(CultureInfo.CurrentCulture, PanelStrings.PeakNowLineFormat, what, at);
    }

    /// <summary>
    /// НОМЕР СТРОКИ СЕГОДНЯШНЕГО ДНЯ в таблице: неделя начинается с понедельника, поэтому
    /// воскресенье (0 у <see cref="DayOfWeek"/>) стоит последним.
    ///
    /// Считается ОТ МОМЕНТА, а не «по системным часам внутри построителя»: у дня и у часового
    /// пояса обязан быть один источник «сейчас», иначе на границе суток подсветка уехала бы
    /// на соседний день.
    /// </summary>
    public static int TodayIndex(DateTimeOffset now) => (((int)now.DayOfWeek) + 6) % 7;

    /// <summary>Часы и минуты местного времени для границы окна; конец суток — «24:00», а не «00:00».</summary>
    private static string Clock(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    private static string Join(IReadOnlyList<(int From, int To)> spans) =>
        string.Join(", ", spans.Select(span => Clock(span.From) + "–" + Clock(span.To)));

    private static int Wrap(int minutes) => ((minutes % DayMinutes) + DayMinutes) % DayMinutes;
}
