using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DshPanel.Views;

/// <summary>
/// ГЕОМЕТРИЯ ТАБЛИЦ ПАНЕЛИ — ОДНО место на обе таблицы окон пика: «График пиков» и «Стоимость».
///
/// Зачем отдельно. У таблиц РАЗНЫЕ колонки (у графика — день, пик и вне пика; у стоимости —
/// единица и две цены), но ОДНА строка: рамка с тонким разделителем снизу, ячейки в общей сетке
/// и заголовок, выровненный так же, как его значения. Напиши это дважды — и разделитель однажды
/// окажется толще в одной таблице, чем в другой, а заметить это на одном окне нельзя.
///
/// Здесь только раскладка. Данные приходят ГОТОВЫМИ (<c>Peak\PeakTable</c> и <c>Pricing</c>):
/// ни одна из таблиц ничего не считает, и второй правды о тарифе не заводится.
/// </summary>
internal static class TableGrid
{
    /// <summary>Промежуток между колонками — из шкалы панели (4/8/12/16/24).</summary>
    public const double Gutter = 12;

    /// <summary>
    /// Начать таблицу заново: своя сетка строк, колонки по числу объявленных.
    ///
    /// Сетка ОЧИЩАЕТСЯ целиком: её пересобирают при смене агента и при каждом разборе страницы,
    /// а накопление строк дало бы таблицу из двух агентов сразу.
    /// </summary>
    public static void Prepare(Grid target, IReadOnlyList<GridLength> columns)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);

        target.Children.Clear();
        target.ColumnDefinitions.Clear();
        target.RowDefinitions.Clear();

        foreach (var width in columns) target.ColumnDefinitions.Add(new ColumnDefinition(width));
    }

    /// <summary>
    /// Одна строка таблицы: ячейки в общей сетке, а вокруг них рамка с ТОНКИМ разделителем снизу
    /// и отступом, чтобы линия не прилипала к тексту.
    ///
    /// <paramref name="rowClass"/> — необязательный класс строки: по нему <c>App.axaml</c> ставит
    /// подложку сегодняшнего дня (тема, а не подобранный в окне оттенок) либо подсветку строки
    /// под курсором у таблицы, по которой выбирают (таблица копий).
    ///
    /// Возвращается САМА рамка строки: её берёт тот, кому строка нужна как орган, а не как текст
    /// (таблица копий вешает на неё курсор-руку и щелчок). Таблицы пиков и цен возвращённое
    /// значение не смотрят — им строка и нужна только содержимым.
    /// </summary>
    public static Border AddLine(
        Grid target,
        int line,
        int columnSpan,
        IReadOnlyList<(TextBlock Cell, int Column)> cells,
        string? rowClass = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(cells);

        target.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var row = new Grid();
        foreach (var definition in target.ColumnDefinitions) row.ColumnDefinitions.Add(new ColumnDefinition(definition.Width));

        foreach (var (cell, column) in cells)
        {
            Grid.SetColumn(cell, column);
            row.Children.Add(cell);
        }

        var border = new Border
        {
            Classes = { PeakTableView.SeparatorClass },
            BorderThickness = new Thickness(0, 0, 0, PeakTableView.SeparatorThickness),
            Padding = new Thickness(0, 0, 0, PeakTableView.RowBottomPadding),
            Child = row,
        };

        if (rowClass is not null) border.Classes.Add(rowClass);

        Grid.SetRow(border, line);
        Grid.SetColumnSpan(border, columnSpan);

        target.Children.Add(border);

        return border;
    }

    /// <summary>
    /// Подпись колонки: мелкая серая (класс <c>hint</c>), но полужирная — она и отделяет шапку
    /// от строк. Выравнивание подписи — ТО ЖЕ, ЧТО У ЕЁ ЗНАЧЕНИЙ: колонка, у которой шапка стоит
    /// слева, а числа справа, читается криво.
    /// </summary>
    public static TextBlock Header(string text, bool right = false, double minWidth = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = minWidth,
        };

        block.Classes.Add("hint");
        block.FontWeight = FontWeight.SemiBold;

        return block;
    }

    /// <summary>
    /// Ячейка таблицы: <paramref name="right"/> — прижата к правому краю колонки (цены),
    /// <paramref name="wrap"/> — переносится по строкам (окна пика длиннее узкой колонки).
    /// </summary>
    public static TextBlock Cell(string text, bool wrap = false, bool right = false, double minWidth = 0) => new()
    {
        Text = text,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = minWidth,
    };
}
