using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Pricing;

/// <summary>
/// КУДА ПОШЛА ЦЕНА: подорожала, подешевела или сравнивать не с чем.
///
/// Отдельным перечислением, а не цветом и не стрелкой: решение «это рост или падение» принимается
/// ЗДЕСЬ и проверяется перебором, а цвет (красный/зелёный/серый) и знак (↑/↓/—) — его перевод
/// в точки на экране (<see cref="PricingChangeDecisions.Tone"/>, <see cref="PricingHistoryText.Arrow"/>).
/// </summary>
public enum PricingChangeDirection
{
    /// <summary>Сравнивать не с чем: цены на одной из сторон нет вовсе, либо числа равны.</summary>
    None,

    /// <summary>Подорожало — для человека плохая новость, и цвет у неё красный.</summary>
    Up,

    /// <summary>Подешевело — зелёный.</summary>
    Down,
}

/// <summary>
/// СРАВНЕНИЕ ДВУХ СНИМКОВ — ЧИСТАЯ функция: ни ввода-вывода, ни окон, ни готового текста.
///
/// **Сравниваются ЧИСЛА, а не текст.** «$0.6» и «$0.60» — одна цена, и записи об изменении
/// из-за неё быть не должно: страница вправе печатать то же число другой записью. Разбор числа
/// берётся у разбора страницы (<see cref="PricingPage.PriceValue"/>), а не пишется здесь заново:
/// второй разбор цен однажды разошёлся бы с первым.
///
/// **Пустой результат — это результат.** «Проверили, ничего не изменилось» возвращает <c>null</c>,
/// и вызывающий по нему видит: ни записи, ни шарика.
/// </summary>
public static class PricingChangeDecisions
{
    /// <summary>
    /// Что изменилось между двумя снимками. <c>null</c> — сравнивать не с чем (первого снимка нет)
    /// либо изменений нет вовсе: и то и другое означает «новой записи не будет».
    /// </summary>
    public static PricingChange? Between(PricingSnapshot? previous, PricingSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Первого снимка нет — сравнить не с чем. Это и есть молчаливая базовая точка: она
        // записывается, но записи об изменении и шарика не даёт.
        if (previous is null) return null;

        var prices = PriceRows(previous.Rows, current.Rows);
        var windows = !PricingDecisions.SameWindows(previous.Windows, current.Windows);

        if (prices.Count == 0 && !windows) return null;

        return new PricingChange(
            current.At,
            current.SourceUrl,
            current.Language,
            current.Model,
            prices,
            previous.Windows,
            current.Windows);
    }

    /// <summary>
    /// СТРОКИ, КОТОРЫЕ ИЗМЕНИЛИСЬ. Порядок — как на новой странице, а пропавшие строки идут
    /// следом: так запись читается в том же порядке, в каком человек видит таблицу.
    ///
    /// Имя строки — СЛОВО СТРАНИЦЫ («1M OUTPUT TOKENS»), и оно не переводится: переводить
    /// заголовки страницы панель не вправе (<see cref="PricingPage"/>).
    /// </summary>
    public static IReadOnlyList<PriceChangeRow> PriceRows(
        IReadOnlyList<PriceRow> before,
        IReadOnlyList<PriceRow> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<PriceChangeRow>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in after)
        {
            known.Add(row.Item);

            var was = Find(before, row.Item) ?? new PriceRow(row.Item, string.Empty, string.Empty);

            if (SamePrice(was.OffPeak, row.OffPeak) && SamePrice(was.Peak, row.Peak)) continue;

            changes.Add(new PriceChangeRow(row.Item, was.OffPeak, row.OffPeak, was.Peak, row.Peak));
        }

        foreach (var row in before)
        {
            // Строка уже разобрана выше — второй раз её в записи быть не должно.
            if (!known.Add(row.Item)) continue;

            changes.Add(new PriceChangeRow(row.Item, row.OffPeak, string.Empty, row.Peak, string.Empty));
        }

        return changes;
    }

    /// <summary>
    /// ОДНА ЛИ ЭТО ЦЕНА. Текст сравнивается как есть, а если оба значения — числа, то ЧИСЛА:
    /// «$0.6» и «$0.60» — одна цена, и записи об изменении из-за неё не будет.
    ///
    /// Пустое значение и прочерк числами не являются: «цены не было» и «цена ноль» — разные вещи,
    /// и смешать их значило бы объявить изменением появление строки, которой раньше не было...
    /// то есть ровно то, чем оно и является.
    /// </summary>
    public static bool SamePrice(string? left, string? right)
    {
        var first = (left ?? string.Empty).Trim();
        var second = (right ?? string.Empty).Trim();

        if (string.Equals(first, second, StringComparison.Ordinal)) return true;

        return TryNumber(first, out var a) && TryNumber(second, out var b) && a == b;
    }

    /// <summary>
    /// Куда пошла цена. Сравнивать не с чем — «None»: цены на одной из сторон нет вовсе
    /// (строка появилась или пропала), и стрелку рисовать не по чему.
    /// </summary>
    public static PricingChangeDirection Direction(string? before, string? after)
    {
        if (!TryNumber(before, out var was) || !TryNumber(after, out var now)) return PricingChangeDirection.None;
        if (now == was) return PricingChangeDirection.None;

        return now > was ? PricingChangeDirection.Up : PricingChangeDirection.Down;
    }

    /// <summary>
    /// ТОН ИЗМЕНЕНИЯ — теми же ролями, что у меню значка: подорожание красное («плохо»),
    /// удешевление зелёное («хорошо»), «сравнивать не с чем» — серое («панель не знает»).
    ///
    /// Решение владельца 28.09.2026: рост цены для человека плохая новость. Числа цвета берутся
    /// у палитры значка (<see cref="Tray.TrayPalette"/>) и проверены по контрасту — «на глаз»
    /// здесь не подбирается ничего.
    /// </summary>
    public static TrayTone Tone(string? before, string? after) => Direction(before, after) switch
    {
        PricingChangeDirection.Up => TrayTone.Bad,
        PricingChangeDirection.Down => TrayTone.Good,
        _ => TrayTone.Neutral,
    };

    /// <summary>
    /// РАЗНИЦА ЦЕН СЛОВАМИ ДЛЯ ЧЕЛОВЕКА: «+$0.1», «-¥0.02». Пусто — когда числа нет с одной
    /// из сторон либо цена не изменилась: тогда окно показывает прочерк
    /// (<see cref="PricingHistoryText.Arrow"/>), а не выдуманный ноль.
    /// </summary>
    public static string Difference(string? before, string? after)
    {
        if (!TryNumber(before, out var was) || !TryNumber(after, out var now)) return string.Empty;
        if (now == was) return string.Empty;

        var mark = CurrencyMark(after);
        var value = Math.Abs(now - was).ToString("0.####", CultureInfo.InvariantCulture);

        return (now > was ? "+" : "-") + mark + value;
    }

    /// <summary>
    /// Число из записанной цены. Разбор берётся у страницы цен: её разбор уже знает, что числом
    /// считается только ячейка с денежным признаком.
    /// </summary>
    public static bool TryNumber(string? price, out decimal number)
    {
        number = 0;

        var digits = PricingPage.PriceValue(price ?? string.Empty);
        if (digits is null) return false;

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static PriceRow? Find(IReadOnlyList<PriceRow> rows, string item)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Item, item, StringComparison.Ordinal)) return row;
        }

        return null;
    }

    /// <summary>Знак валюты перед числом («$», «¥»). Его и печатает страница нужного языка.</summary>
    private static string CurrencyMark(string? price)
    {
        var text = (price ?? string.Empty).Trim();

        return text.Length > 0 && !char.IsDigit(text[0]) ? text[..1] : string.Empty;
    }
}
