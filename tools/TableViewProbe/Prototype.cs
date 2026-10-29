using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace TableViewProbe;

/// <summary>Одна ячейка прототипа: текст и то, что о нём надо знать виду.</summary>
internal sealed record ProtoCell(string Text, bool Right, bool Wrap, string? ToneClass);

/// <summary>Строка прототипа: ровно те же пять столбцов, что у нынешней таблицы истории.</summary>
internal sealed record ProtoRow(
    ProtoCell Item,
    ProtoCell OffPeak,
    ProtoCell OffPeakChange,
    ProtoCell Peak,
    ProtoCell PeakChange)
{
    /// <summary>
    /// ЧТО ПРОЧТЁТ ДИКТОР ПРО СТРОКУ. Без этого UIA отдавал имя записи как есть —
    /// «ProtoRow { Item = ProtoCell { Text = 1M INPUT …» (замер: ровно так и было).
    /// Своё имя строки — то, что человек услышит, встав на неё.
    /// </summary>
    public override string ToString() => Item.Text;
}

/// <summary>
/// ПРОТОТИП ТАБЛИЦЫ ЦЕН НА <see cref="TableView"/> — ТОЛЬКО ЯДРО 12.1.3, НИ ОДНОГО ПАКЕТА.
///
/// Вид повторяет нынешнюю таблицу «Истории цен» (Views\TableGrid + PriceTableView + правый блок
/// окна): пять столбцов — единица, цена вне пика, её изменение, цена пика, её изменение; числа
/// прижаты вправо, шапка выровнена ТАК ЖЕ, как её числа, длинная единица переносится, тон
/// изменения — тот же класс панели (<c>tone-bad</c>/<c>tone-good</c>/<c>tone-neutral</c>), что
/// ставит <c>PanelLook.Tone</c>.
///
/// ⚠️ ДВЕ ВЕЩИ, КОТОРЫЕ ПРИШЛОСЬ СДЕЛАТЬ РУКАМИ (обе проверены кадром 02 и записаны в отчёт):
/// 1. ячейка обязана быть <c>HorizontalAlignment=Stretch</c> — иначе TextBlock меряется по тексту
///    и <c>TextAlignment=Right</c> ничего не значит (числа вставали по левому краю);
/// 2. сама таблица не растягивается на ширину панели (562 вместо 594) — ширину задаёт вызывающий
///    (<see cref="Probe"/>), а <c>HorizontalAlignment</c> у неё <c>Left</c>: со <c>Stretch</c>
///    таблица с явной шириной центрируется в меньшем слоте и уезжает влево.
/// </summary>
internal static class Prototype
{
    /// <summary>Те же числа, что у нынешней таблицы: ширина цены 72, изменения 96 (PriceTableView/окно).</summary>
    public const double PriceWidth = 72;

    public const double ChangeWidth = 96;

    public static TableView Build(IReadOnlyList<ProtoRow> rows, string[] headers)
    {
        var table = new TableView
        {
            CanUserResizeColumns = false,
            SelectionMode = SelectionMode.Single,
            ItemsSource = rows,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        table.Columns.Add(Column(headers[0], new GridLength(1, GridUnitType.Star), right: false, row => row.Item));
        table.Columns.Add(Column(headers[1], new GridLength(PriceWidth), right: true, row => row.OffPeak));
        table.Columns.Add(Column(headers[2], new GridLength(ChangeWidth), right: true, row => row.OffPeakChange));
        table.Columns.Add(Column(headers[3], new GridLength(PriceWidth), right: true, row => row.Peak));
        table.Columns.Add(Column(headers[4], new GridLength(ChangeWidth), right: true, row => row.PeakChange));

        return table;
    }

    private static TableViewColumn Column(
        string header,
        GridLength width,
        bool right,
        Func<ProtoRow, ProtoCell> pick)
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

            // ⚠️ Заполнение ячейки задаёт КОЛОНКА, а не TextBlock: без этого ContentPresenter
            // меряется по тексту, и ни выравнивание шапки, ни правое выравнивание чисел не работают
            // (проверено: «$0.004» шириной 41 вместо 60, шапка «Вне пика» левее своего числа).
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CellTemplate = new FuncDataTemplate<ProtoRow>((row, _) => Cell(row is null ? null : pick(row))),
        };
    }

    /// <summary>Ячейка прототипа: тот же TextBlock, что делает <c>TableGrid.Cell</c>, плюс класс тона.</summary>
    private static Control Cell(ProtoCell? cell)
    {
        if (cell is null) return new TextBlock();

        var block = new TextBlock
        {
            Text = cell.Text,
            TextAlignment = cell.Right ? TextAlignment.Right : TextAlignment.Left,
            TextWrapping = cell.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,

            // ⚠️ Без Stretch TextBlock меряется по ТЕКСТУ, и TextAlignment=Right внутри него
            // ничего не значит: число встаёт по левому краю ячейки (проверено кадром 02: «$0.004»
            // шириной 41 вместо 72).
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (cell.ToneClass is { } tone) block.Classes.Add(tone);

        return block;
    }
}
