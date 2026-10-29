using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace TableViewProbe;

/// <summary>Одна измеренная ячейка таблицы — то, что видно в кадре и то, что можно посчитать.</summary>
internal sealed record CellMeasure(
    string Kind,
    int Column,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    bool Right,
    bool Wrap,
    int Lines,
    double TextHeight);

/// <summary>
/// ЗАМЕР ВЁРСТКИ ТАБЛИЦЫ — ЧИСЛА, А НЕ «НА ГЛАЗ». Снимается с НАСТОЯЩЕГО дерева после раскладки
/// (<c>Show()</c> + проход диспетчера + тик отрисовки): координаты — в точках окна, 96 dpi,
/// и совпадают с пикселями кадра 1:1.
///
/// Считается по обоим видам ОДНОЙ И ТОЙ ЖЕ меркой:
/// * «сейчас» — рамки строк <c>TableGrid.AddLine</c> и их <c>TextBlock</c>;
/// * «прототип» — <c>TableViewRow</c>/<c>TableViewCell</c>/<c>TableViewColumnHeader</c>.
///
/// ⚠️ Число строк текста берётся у <c>TextBlock.TextLayout</c> отражением, а не «по высоте»:
/// высота зависит от кегля, а строки — это то, что человек читает. Там, где разметка текста
/// недоступна, честно остаётся 0, и это видно в JSON (а не подставлено).
/// </summary>
internal static class Measure
{
    private const string HintClass = "hint";

    public static string Capture(Window window, string mode, string theme, int windowWidth, int windowHeight)
    {
        var panel = window.FindControl<Grid>("PriceTablePanel")
                    ?? throw new InvalidOperationException("В окне нет PriceTablePanel");

        var table = panel.Children.OfType<TableView>().FirstOrDefault();

        var cells = new List<CellMeasure>();
        var rows = new List<double>();
        var headerHeight = 0.0;

        if (table is null)
        {
            WalkNow(panel, window, cells, rows, ref headerHeight);
        }
        else
        {
            WalkPrototype(table, window, cells, rows, ref headerHeight);
        }

        var origin = table is null
            ? ToWindow(panel, window)
            : ToWindow(table, window);

        var width = table?.Bounds.Width ?? panel.Bounds.Width;
        var height = table?.Bounds.Height ?? panel.Bounds.Height;

        // ПРАВЫЙ КРАЙ ЧИСЕЛ: у правой колонки все значения и её шапка обязаны кончаться одной
        // вертикалью. Разброс больше половины точки — это уже «столбик рассыпался».
        var numericColumns = cells.Where(cell => cell.Right).GroupBy(cell => cell.Column).OrderBy(g => g.Key)
            .Select(group => new
            {
                column = group.Key,
                rightEdges = group.Select(cell => Math.Round(cell.X + cell.Width, 1)).Distinct().OrderBy(v => v).ToList(),
                spread = Math.Round(group.Max(cell => cell.X + cell.Width) - group.Min(cell => cell.X + cell.Width), 1),
            })
            .ToList();

        var wrapped = cells.Where(cell => cell.Lines > 1).ToList();
        var clipped = cells.Where(cell => cell.TextHeight > cell.Height + 0.5 || cell.Lines == 0 && cell.Wrap)
            .Select(cell => cell.Text).ToList();

        var result = new
        {
            mode,
            theme,
            window = new { width = windowWidth, height = windowHeight },
            table = new
            {
                x = Math.Round(origin.X, 1),
                y = Math.Round(origin.Y, 1),
                width = Math.Round(width, 1),
                height = Math.Round(height, 1),
            },
            headerHeight = Math.Round(headerHeight, 1),
            rowHeights = rows.Select(value => Math.Round(value, 1)).ToList(),
            numericColumns,
            wrapped = wrapped.Select(cell => new { cell.Column, cell.Text, cell.Lines }).ToList(),
            clipped,
            counts = new
            {
                cells = cells.Count,
                headers = cells.Count(cell => cell.Kind == HeaderKind),
                values = cells.Count(cell => cell.Kind == ValueKind),
                changes = cells.Count(cell => cell.Kind == ChangeKind),
            },
            cells = cells.Select(cell => new
            {
                kind = cell.Kind,
                column = cell.Column,
                text = cell.Text,
                x = Math.Round(cell.X, 1),
                y = Math.Round(cell.Y, 1),
                width = Math.Round(cell.Width, 1),
                height = Math.Round(cell.Height, 1),
                right = cell.Right,
                wrap = cell.Wrap,
                lines = cell.Lines,
                textHeight = Math.Round(cell.TextHeight, 1),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

        var right = numericColumns.Count == 0
            ? "нет"
            : string.Join(", ", numericColumns.Select(c => $"кол.{c.column} разброс={c.spread}"));

        Console.WriteLine(
            $"ИЗМЕР mode={mode} тема={theme} окно={windowWidth}x{windowHeight} "
            + $"таблица={Math.Round(width, 1)}x{Math.Round(height, 1)} шапка={Math.Round(headerHeight, 1)} "
            + $"строки=[{string.Join("; ", rows.Select(v => Math.Round(v, 1)))}] "
            + $"ячеек={cells.Count} переносов={wrapped.Count} обрезано={clipped.Count} правыйКрай: {right}");

        return json;
    }

    private const string HeaderKind = "header";

    private const string ValueKind = "value";

    private const string ChangeKind = "change";

    /// <summary>«Сейчас»: каждая строка — Border вокруг Grid из TextBlock (Views\TableGrid).</summary>
    private static void WalkNow(
        Grid panel,
        Window window,
        List<CellMeasure> cells,
        List<double> rows,
        ref double headerHeight)
    {
        var rowIndex = 0;

        foreach (var border in panel.Children.OfType<Border>())
        {
            if (border.Child is not Grid grid) continue;

            var origin = ToWindow(border, window);
            var isHeader = border.Classes.Contains(HintClass)
                           || grid.Children.OfType<TextBlock>().All(block => block.Classes.Contains(HintClass));

            if (isHeader)
            {
                headerHeight = Math.Max(headerHeight, border.Bounds.Height);
            }
            else
            {
                rows.Add(border.Bounds.Height);
            }

            foreach (var block in grid.Children.OfType<TextBlock>())
            {
                var column = Grid.GetColumn(block);
                var point = ToWindow(block, window);

                cells.Add(new CellMeasure(
                    isHeader ? HeaderKind : KindOf(column, block),
                    column,
                    block.Text ?? string.Empty,
                    point.X,
                    point.Y,
                    block.Bounds.Width,
                    block.Bounds.Height,
                    block.TextAlignment == TextAlignment.Right,
                    block.TextWrapping == TextWrapping.Wrap,
                    LineCount(block),
                    LayoutHeight(block)));
            }

            rowIndex++;
        }

        _ = rowIndex;
    }

    /// <summary>«Прототип»: строки TableViewRow, ячейки TableViewCell, шапка TableViewColumnHeader.</summary>
    private static void WalkPrototype(
        TableView table,
        Window window,
        List<CellMeasure> cells,
        List<double> rows,
        ref double headerHeight)
    {
        var visual = table.GetVisualDescendants().ToList();

        // ГДЕ СТОЯТ КОНТЕЙНЕРЫ — вторая половина ответа на «почему шапка сдвинута».
        foreach (var container in visual.Where(control =>
                     control.GetType().Name is "TableViewColumnHeadersPresenter" or "TableViewCellsPresenter"))
        {
            var point = ToWindow(container, window);
            Console.WriteLine(
                $"КОНТЕЙНЕР {container.GetType().Name} x={point.X:0.#} y={point.Y:0.#} "
                + $"w={container.Bounds.Width:0.#} h={container.Bounds.Height:0.#}");
        }

        foreach (var header in visual.OfType<TableViewColumnHeader>().Take(1))
        {
            var point = ToWindow(header, window);
            Console.WriteLine(
                $"КОНТЕЙНЕР первый-заголовок x={point.X:0.#} w={header.Bounds.Width:0.#} "
                + $"отступ={header.Padding}");
        }

        foreach (var row in visual.OfType<TableViewRow>().Take(1))
        {
            var cell = row.GetVisualDescendants().OfType<TableViewCell>().FirstOrDefault();

            if (cell is null) continue;

            var rowPoint = ToWindow(row, window);
            var cellPoint = ToWindow(cell, window);

            Console.WriteLine(
                $"КОНТЕЙНЕР первая-строка x={rowPoint.X:0.#} w={row.Bounds.Width:0.#}; "
                + $"первая-ячейка x={cellPoint.X:0.#} w={cell.Bounds.Width:0.#} отступ={cell.Padding}");
        }

        foreach (var header in visual.OfType<TableViewColumnHeader>())
        {
            headerHeight = Math.Max(headerHeight, header.Bounds.Height);

            if (header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is not { } block) continue;

            var column = header.Column is null ? -1 : table.Columns.IndexOf(header.Column);
            var point = ToWindow(block, window);

            cells.Add(new CellMeasure(
                HeaderKind,
                column,
                block.Text ?? string.Empty,
                point.X,
                point.Y,
                block.Bounds.Width,
                block.Bounds.Height,
                block.TextAlignment == TextAlignment.Right,
                block.TextWrapping == TextWrapping.Wrap,
                LineCount(block),
                LayoutHeight(block)));
        }

        foreach (var row in visual.OfType<TableViewRow>())
        {
            rows.Add(row.Bounds.Height);

            foreach (var cell in row.GetVisualDescendants().OfType<TableViewCell>())
            {
                if (cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is not { } block) continue;

                var column = cell.Column is null ? -1 : table.Columns.IndexOf(cell.Column);
                var point = ToWindow(block, window);

                cells.Add(new CellMeasure(
                    KindOf(column, block),
                    column,
                    block.Text ?? string.Empty,
                    point.X,
                    point.Y,
                    block.Bounds.Width,
                    block.Bounds.Height,
                    block.TextAlignment == TextAlignment.Right,
                    block.TextWrapping == TextWrapping.Wrap,
                    LineCount(block),
                    LayoutHeight(block)));
            }
        }
    }

    private static string KindOf(int column, TextBlock block) =>
        column is 3 or 6 || block.Classes.Contains("tone-bad")
            || block.Classes.Contains("tone-good") || block.Classes.Contains("tone-neutral")
            ? ChangeKind
            : ValueKind;

    private static Point ToWindow(Visual control, Window window) =>
        control.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);

    /// <summary>Число строк разметки текста — отражением: свойство есть, но имя не хардкодим.</summary>
    private static int LineCount(TextBlock block)
    {
        var layout = block.GetType()
            .GetProperty("TextLayout", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);

        if (layout?.GetType().GetProperty("TextLines")?.GetValue(layout) is ICollection lines) return lines.Count;

        return 0;
    }

    /// <summary>Высота разметки: больше высоты ячейки — текст обрезан, а не перенесён.</summary>
    private static double LayoutHeight(TextBlock block)
    {
        var layout = block.GetType()
            .GetProperty("TextLayout", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);

        return layout?.GetType().GetProperty("Height")?.GetValue(layout) is double height ? height : 0;
    }
}
