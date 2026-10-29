using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DshPanel.Shell;
using DshPanel.Views;

namespace TableViewProbe.Fit;

/// <summary>
/// Строка МАТРИЦЫ ОКОН: подпись (день недели или «Было»/«Стало» из истории цен) и два окна —
/// пик и вне пика. Текст окна может быть многострочным: перенос и разная высота строки — ровно то,
/// что проверяется.
/// </summary>
internal sealed record MatrixRow(string Label, string Peak, string OffPeak)
{
    /// <summary>Что услышит человек, встав на строку: без этого в UIA уходит дамп записи (урок T1).</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Строка ПЛАНА НАКАТА: группа, куда ляжет, сколько файлов, что с ней — и СОГЛАСИЕ ГАЛОЧКОЙ
/// В ЯЧЕЙКЕ. Сегодня эти три галочки стоят ПОД таблицей отдельными строками
/// (<c>BackupWindow.axaml</c>: EngineCheck/PanelCheck/KeysConsentCheck); здесь спрашивается,
/// ложатся ли они в строку.
/// </summary>
internal sealed record PlanRow(string Group, string Target, string Files, string State, bool Consented)
{
    public override string ToString() => Group;
}

/// <summary>
/// ДВЕ МИНИ-ТАБЛИЦЫ НА <see cref="TableView"/> ПО ОБРАЗЦУ НАШИХ.
///
/// Столбцы, ширины и переносы взяты у наших построителей, а не придуманы: матрица — как
/// «График пиков» (<see cref="PeakTableView"/>: день 104, две звезды с переносом) и как «окна
/// пика» истории цен (подпись + перенос), план наката — как <see cref="RestoreTableView"/>
/// (группа 132, звёзды 1.6/1.4, файлов 88 справа) с ДОБАВЛЕННОЙ колонкой галочки.
///
/// ⚠️ Две вещи, добытые в T1 и повторённые здесь без проверки заново: ячейка и колонка обязаны
/// быть <c>Stretch</c> (иначе TextBlock меряется по тексту и <c>TextAlignment=Right</c> ничего
/// не значит), а ширина таблицы задаётся вызывающим — «звезда» у TableView не тянется.
/// </summary>
internal static class FitTables
{
    /// <summary>Матрица окон: тот же вид, что у «Графика пиков» (104 | 12 | звезда | 12 | звезда).</summary>
    public static TableView Matrix(IReadOnlyList<MatrixRow> rows)
    {
        var table = Base();

        table.Columns.Add(Column<MatrixRow>(
            PanelStrings.PeakTableColumnDay,
            new GridLength(PeakTableView.DayColumnWidth),
            right: false,
            wrap: false,
            row => row.Label));

        table.Columns.Add(Column<MatrixRow>(
            PanelStrings.PeakTableColumnPeak,
            new GridLength(1, GridUnitType.Star),
            right: false,
            wrap: true,
            row => row.Peak));

        table.Columns.Add(Column<MatrixRow>(
            PanelStrings.PeakTableColumnOffPeak,
            new GridLength(1, GridUnitType.Star),
            right: false,
            wrap: true,
            row => row.OffPeak));

        table.ItemsSource = rows;
        return table;
    }

    /// <summary>
    /// План наката: четыре колонки <see cref="RestoreTableView"/> и ПЯТАЯ — галочка согласия
    /// В ЯЧЕЙКЕ. Ширины те же, что у наших: группа 132, звёзды 1.6/1.4, файлов 88 справа.
    /// </summary>
    public static TableView Plan(IReadOnlyList<PlanRow> rows)
    {
        var table = Base();

        table.Columns.Add(Column<PlanRow>(
            PanelStrings.RestoreTableGroup,
            new GridLength(132),
            right: false,
            wrap: true,
            row => row.Group));

        table.Columns.Add(Column<PlanRow>(
            PanelStrings.RestoreTableTarget,
            new GridLength(1.6, GridUnitType.Star),
            right: false,
            wrap: true,
            row => row.Target));

        table.Columns.Add(Column<PlanRow>(
            PanelStrings.RestoreTableFiles,
            new GridLength(88),
            right: true,
            wrap: false,
            row => row.Files));

        table.Columns.Add(Column<PlanRow>(
            PanelStrings.RestoreTableState,
            new GridLength(1.4, GridUnitType.Star),
            right: false,
            wrap: true,
            row => row.State));

        // ПЯТАЯ КОЛОНКА — ГАЛОЧКА. Подпись колонки — слово самой панели («вернётся»): второго
        // слова про то же состояние не выдумываем.
        table.Columns.Add(Column<PlanRow>(
            PanelStrings.RestoreTableLands,
            GridLength.Auto,
            right: false,
            wrap: false,
            row => string.Empty,
            cell: row => Box(row)));

        table.ItemsSource = rows;
        return table;
    }

    /// <summary>Галочка в ячейке — тот же <see cref="CheckBox"/>, что стоит под таблицей сегодня.</summary>
    private static Control Box(PlanRow row) => new CheckBox
    {
        IsChecked = row.Consented,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Колонка: подпись полужирным hint и ячейка, растянутая на всю колонку (урок T1).</summary>
    private static TableViewColumn Column<T>(
        string header,
        GridLength width,
        bool right,
        bool wrap,
        Func<T, string> pick,
        Func<T, Control>? cell = null)
    {
        var title = new TextBlock
        {
            Text = header,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        };

        title.Classes.Add("hint");

        return new TableViewColumn
        {
            Header = title,
            Width = width,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CellTemplate = new FuncDataTemplate<T>((row, _) =>
                row is null ? new TextBlock() : cell?.Invoke(row) ?? Text(pick(row), right, wrap)),
        };
    }

    /// <summary>Ячейка: тот же TextBlock, что у <see cref="TableGrid.Cell"/>, плюс натяжка на колонку.</summary>
    private static Control Text(string text, bool right, bool wrap) => new TextBlock
    {
        Text = text,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TableView Base() => new()
    {
        CanUserResizeColumns = false,
        SelectionMode = SelectionMode.Single,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}
