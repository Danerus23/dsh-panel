using DshPanel.Agents;
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Shell;

namespace DshPanel.Pricing;

/// <summary>
/// ТЕКСТ ИСТОРИИ ЦЕН — собранный ПРИ ПОКАЗЕ и на языке панели.
///
/// **Почему здесь, а не в файле истории.** Записи об изменении лежат СТРУКТУРОЙ (числа, имена
/// строк страницы, окна) — иначе смена языка оставила бы всю прежнюю историю на прежнем языке
/// (требование владельца 28.09.2026). Слова собираются в тот момент, когда человек смотрит окно.
///
/// **Почему здесь, а не в окне.** Теми же словами говорит ШАРИК (он называет, что именно
/// изменилось), и второй сборки той же фразы в панели быть не должно: разойдясь, окно и шарик
/// говорили бы об одном событии по-разному.
///
/// ⚠️ Необязательный <c>language</c> — для проверок: с ним видно, что ОДНА И ТА ЖЕ запись
/// на другом языке читается другими словами. В панели он не передаётся никогда — язык берётся
/// у <see cref="Loc"/> на момент показа.
/// </summary>
public static class PricingHistoryText
{
    /// <summary>Заголовок шарика. Короткий: подробности — в его тексте.</summary>
    public static string NoticeTitle(string? language = null) =>
        T(language, nameof(PanelStrings.NotifyPricingChangedTitle));

    /// <summary>
    /// ТЕКСТ ШАРИКА — «что именно изменилось», а не «тариф изменился».
    ///
    /// Две строки, и у каждой свой повод: цены и окна пика меняются независимо, и свести их
    /// в одну фразу значило бы назвать окна ценами. Окна названы так, как они напечатаны
    /// на странице: ШАРИК РАСПИСАНИЕ НЕ МЕНЯЕТ — оно меняется по подтверждению человека.
    /// </summary>
    public static string NoticeText(PricingChange change, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(change);

        var lines = new List<string>();

        if (change.PricesChanged)
        {
            var rows = change.Prices.Select(row => PriceRowText(row, language));
            var model = change.Model.Length > 0 ? change.Model + ", " : string.Empty;

            lines.Add(T(language, nameof(PanelStrings.NotifyPricingPricesFormat), model + string.Join("; ", rows)));
        }

        if (change.WindowsChanged)
        {
            lines.Add(T(
                language,
                nameof(PanelStrings.NotifyPricingWindowsFormat),
                WindowsText(change.WindowsBefore),
                WindowsText(change.WindowsAfter)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Подпись записи в списке слева: «28.09.2026, 14:32 — текущие».</summary>
    public static string EntryTitle(PricingChange change, bool current, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(change);

        var stamp = Stamp(change.At);

        return current
            ? T(language, nameof(PanelStrings.PricingHistoryEntryCurrentFormat), stamp)
            : stamp;
    }

    /// <summary>
    /// Подпись ПЕРВОЙ ЦЕНЫ в списке: «28.09.2026, 14:32 — первая цена». Слова владельца (п. 39),
    /// а не внутренние «якорь» и «базовая точка».
    /// </summary>
    public static string FirstPriceTitle(PricingSnapshot snapshot, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return T(language, nameof(PanelStrings.PricingHistoryFirstPriceFormat), Stamp(snapshot.At));
    }

    /// <summary>
    /// ПОДПИСЬ СТРОКИ СПИСКА ПО ЕЁ НОМЕРУ — ОДНО место на весь список.
    ///
    /// Строка 0 (когда панель уже получала цены) — ПЕРВАЯ ЦЕНА, дальше точки изменения, последняя
    /// «текущие». Именно поэтому подписи собираются по номеру, а не разбираются обратно из готового
    /// текста: разбор подписи сломался бы от первой же правки слов (п. 39 <c>docs\DESIGN.md</c>).
    /// </summary>
    public static string EntryTitle(PricingHistory history, int entry, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.IsFirstPrice(entry) && history.Anchor is { } first)
        {
            return FirstPriceTitle(first, language);
        }

        var change = history.ChangeAt(entry);

        return change is null ? string.Empty : EntryTitle(change, history.IsCurrentEntry(entry), language);
    }

    /// <summary>
    /// ЧТО СКАЗАНО У ПЕРВОЙ ЦЕНЫ: когда панель её получила и менялось ли что-нибудь с тех пор.
    ///
    /// Два разных ответа про одну и ту же первую цену, и оба — правда: у неё не с чем сравнивать
    /// (это первая), а когда изменений нет, она же и есть текущее состояние. Второй подписи
    /// в строке списка для этого не заводится — одна мысль, одна строка.
    /// </summary>
    public static string FirstPriceNote(PricingHistory history, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Anchor is not { } first) return string.Empty;

        var stamp = Stamp(first.At);

        return history.HasChanges
            ? T(language, nameof(PanelStrings.PricingHistoryFirstPriceNoteFormat), stamp)
            : T(language, nameof(PanelStrings.PricingHistoryEmptyFormat), stamp);
    }

    /// <summary>
    /// ПАНЕЛЬ ЕЩЁ НИ РАЗУ НЕ ЧИТАЛА ЦЕНЫ — сказать это словами. Это ДРУГОЕ состояние, чем «первая
    /// цена есть, а изменений не было»: там человек видит дату и цены, а здесь показывать нечего,
    /// и молчащее окно читалось бы как поломка.
    /// </summary>
    public static string NeverCheckedText(PricingHistory history, string? language = null) =>
        history.Entries == 0 ? T(language, nameof(PanelStrings.PricingHistoryNeverChecked)) : string.Empty;

    /// <summary>
    /// ЧТО СРАВНИВАЕТСЯ — иначе «+$0.1» читается как рост к вчерашнему дню, которым он не является.
    /// У ПЕРВОЙ точки изменения предыдущее — это ПЕРВАЯ ЦЕНА, и она названа человеку своими словами
    /// (не «якорь» и не «базовая точка»): с 29.09.2026 она ещё и стоит первой строкой списка, и он
    /// её видит.
    /// </summary>
    public static string ComparedText(PricingHistory history, int index, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (index < 0 || index >= history.Changes.Count) return string.Empty;

        var at = index > 0 ? history.Changes[index - 1].At : history.Anchor?.At;
        if (at is null) return string.Empty;

        var stamp = Stamp(at.Value);

        return index > 0
            ? T(language, nameof(PanelStrings.PricingHistoryComparedFormat), stamp)
            : T(language, nameof(PanelStrings.PricingHistoryAgainstAnchorFormat), stamp);
    }

    /// <summary>
    /// «Самая старая запись ушла» — словами, а не молча: предел в 30 изменений человек обязан
    /// видеть, иначе пропажа записи выглядела бы порчей файла.
    ///
    /// Назван ПРЕДЕЛ, а не сегодняшняя длина списка: строка говорит о правиле хранения
    /// («хранятся последние 30»), и при правленом руками файле с меньшим числом записей она
    /// остаётся правдой о правиле, а не выдумкой о содержимом.
    /// </summary>
    public static string DroppedText(PricingHistory history, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        return history.WasTrimmed
            ? T(language, nameof(PanelStrings.PricingHistoryDroppedFormat), PricingHistory.MaxChanges)
            : string.Empty;
    }

    /// <summary>Знак направления: стрелка вверх, стрелка вниз или прочерк «сравнивать не с чем».</summary>
    public static string Arrow(PricingChangeDirection direction, string? language = null) => direction switch
    {
        PricingChangeDirection.Up => T(language, nameof(PanelStrings.PricingHistoryArrowUp)),
        PricingChangeDirection.Down => T(language, nameof(PanelStrings.PricingHistoryArrowDown)),
        _ => T(language, nameof(PanelStrings.PricingHistoryArrowNone)),
    };

    /// <summary>
    /// ЯЧЕЙКА «ИЗМЕНЕНИЕ»: стрелка и разница. Прочерк без числа — когда сравнивать не с чем:
    /// выдуманный ноль читался бы как «цена не изменилась», а она на этой стороне просто неизвестна.
    /// </summary>
    public static string Delta(string? before, string? after, string? language = null)
    {
        var direction = PricingChangeDecisions.Direction(before, after);
        var arrow = Arrow(direction, language);
        var difference = PricingChangeDecisions.Difference(before, after);

        return direction == PricingChangeDirection.None || difference.Length == 0
            ? arrow
            : arrow + " " + difference;
    }

    /// <summary>Цена в ячейке истории. Пусто — прочерк: «цены не было» словами, а не пустым местом.</summary>
    public static string Price(string? value, string? language = null) =>
        string.IsNullOrEmpty(value) ? T(language, nameof(PanelStrings.PriceValueMissing)) : value!;

    /// <summary>Окна пика в том же виде, в каком их показывает расписание: по местному времени с поясом.</summary>
    public static string WindowsText(IReadOnlyList<PeakWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return windows.Count == 0
            ? PanelStrings.PeakTableNoPeak
            : PeakDecisions.WindowsText(windows, PeakDecisions.OffsetMinutes(DateTimeOffset.Now));
    }

    /// <summary>Отметка времени в том виде, в каком её читает человек: «28.09.2026, 14:32».</summary>
    public static string Stamp(DateTimeOffset at) =>
        PricingDecisions.CheckedText(PricingDecisions.Stamp(at));

    /// <summary>
    /// Строка словаря на языке панели (или на названном языке — для проверок). Строку берём
    /// у <see cref="Loc"/>, а не из литерала: литерал остался бы русским на английском кадре.
    /// </summary>
    private static string T(string? language, string key, params object[] args) =>
        string.IsNullOrWhiteSpace(language) ? Loc.T(key, args) : Loc.TIn(language!, key, args);

    /// <summary>
    /// ОДНА СТРОКА ЦЕНЫ В ШАРИКЕ: «1M OUTPUT TOKENS вне пика: $0.6 → $0.7». Две цены одной строки
    /// (вне пика и в пик) меняются порознь, поэтому каждая названа своим куском.
    /// </summary>
    private static string PriceRowText(PriceChangeRow row, string? language)
    {
        ArgumentNullException.ThrowIfNull(row);

        var parts = new List<string>();

        if (!PricingChangeDecisions.SamePrice(row.OffPeakBefore, row.OffPeakAfter))
        {
            parts.Add(T(
                language,
                nameof(PanelStrings.NotifyPricingPriceRowFormat),
                row.Item,
                T(language, nameof(PanelStrings.PeakShortOffPeak)),
                Price(row.OffPeakBefore, language),
                Price(row.OffPeakAfter, language)));
        }

        if (!PricingChangeDecisions.SamePrice(row.PeakBefore, row.PeakAfter))
        {
            parts.Add(T(
                language,
                nameof(PanelStrings.NotifyPricingPriceRowFormat),
                row.Item,
                T(language, nameof(PanelStrings.PeakShortInPeak)),
                Price(row.PeakBefore, language),
                Price(row.PeakAfter, language)));
        }

        return string.Join("; ", parts);
    }
}
