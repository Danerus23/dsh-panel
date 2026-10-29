using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TableViewProbe.Fit;

/// <summary>Одна измеренная ячейка мини-таблицы: где стоит, сколько места заняла, сколько строк текста.</summary>
internal sealed record FitCell(
    string Kind,
    int Column,
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    int Lines,
    double TextHeight,
    bool Right,
    bool Wrap);

/// <summary>
/// ЗАМЕР ДВУХ МИНИ-ТАБЛИЦ — ТЕМИ ЖЕ ЧИСЛАМИ, ЧТО В T1 МЕРИЛИ «ИСТОРИЮ ЦЕН»: высота шапки, высоты
/// строк, переносы, обрезка, правый край чисел, выравнивание шапки над своими значениями.
///
/// Сверх вёрстки здесь три вещи, которых у T1 не было и которые решают судьбу «Пиков» и «Плана»:
/// 1. <see cref="Parts"/> — ЧЕМ набрана высота и вид: какие свойства темы дают шапке полосу,
///    строке 40 точек, ячейке вертикальные линии; и чем залита выбранная строка;
/// 2. <see cref="Resources"/> — сколько тем и кистей темы Fluent придётся переопределить,
///    чтобы прототип стал НАШИМ видом (по именам, а не «на глаз»);
/// 3. <see cref="Input"/> — конфликт «выбор строки ↔ орган в ячейке»: щелчок по галочке,
///    попадание фокуса с клавиатуры, Space и стрелки.
/// </summary>
internal static class FitMeasure
{
    public static string Capture(Window window, string fitCase, TableView table, int windowWidth, int windowHeight)
    {
        var cells = new List<FitCell>();
        var rows = new List<double>();
        var headerHeight = 0.0;

        foreach (var header in table.GetVisualDescendants().OfType<TableViewColumnHeader>())
        {
            headerHeight = Math.Max(headerHeight, header.Bounds.Height);

            if (header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is not { } block) continue;

            cells.Add(Cell("header", ColumnIndex(table, header.Column), block, window));
        }

        foreach (var row in table.GetVisualDescendants().OfType<TableViewRow>())
        {
            rows.Add(row.Bounds.Height);

            foreach (var cell in row.GetVisualDescendants().OfType<TableViewCell>())
            {
                var column = ColumnIndex(table, cell.Column);

                if (cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is { } block)
                {
                    cells.Add(Cell("cell", column, block, window));
                }

                if (cell.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault() is { } box)
                {
                    var point = ToWindow(box, window);

                    cells.Add(new FitCell(
                        "box", column, box.IsChecked == true ? "галка" : "пусто",
                        point.X, point.Y, box.Bounds.Width, box.Bounds.Height, 0, 0, false, false));
                }
            }
        }

        // ВЫРАВНИВАНИЕ: шапка колонки обязана стоять над своими значениями (разность x в точках).
        var columns = new List<object>();

        foreach (var group in cells.Where(cell => cell.Column >= 0).GroupBy(cell => cell.Column).OrderBy(g => g.Key))
        {
            var header = group.FirstOrDefault(cell => cell.Kind == "header");
            var value = group.FirstOrDefault(cell => cell.Kind == "cell" || cell.Kind == "box");
            var right = group.Where(cell => cell.Right).ToList();

            columns.Add(new
            {
                column = group.Key,
                headerX = Round(header?.X),
                valueX = Round(value?.X),
                headerWidth = Round(header?.Width),
                valueWidth = Round(value?.Width),
                deltaX = header is null || value is null ? 0 : Round(value.X - header.X),
                spread = right.Count == 0
                    ? 0
                    : Round(right.Max(cell => cell.X + cell.Width) - right.Min(cell => cell.X + cell.Width)),
                lines = group.Max(cell => cell.Lines),
                wrap = group.Any(cell => cell.Wrap),
            });
        }

        var wrapped = cells.Where(cell => cell.Lines > 1).ToList();
        var clipped = cells.Where(cell => cell.Lines > 0 && cell.TextHeight > cell.Height + 0.5).ToList();

        var result = new
        {
            fitCase,
            window = new { width = windowWidth, height = windowHeight },
            table = new
            {
                x = Round(ToWindow(table, window).X),
                y = Round(ToWindow(table, window).Y),
                width = Round(table.Bounds.Width),
                height = Round(table.Bounds.Height),
                columns = table.Columns.Count,
                columnWidths = table.Columns.Select(column => Round(column.ActualWidth)).ToList(),
            },
            headerHeight = Round(headerHeight),
            rowHeights = rows.Select(Round).ToList(),
            columns,
            wrapped = wrapped.Select(cell => new { kind = cell.Kind, cell.Column, cell.Lines, cell.Text }).ToList(),
            clipped = clipped.Select(cell => new { kind = cell.Kind, cell.Column, cell.Text }).ToList(),
            counts = new
            {
                cells = cells.Count(cell => cell.Kind == "cell"),
                headers = cells.Count(cell => cell.Kind == "header"),
                boxes = cells.Count(cell => cell.Kind == "box"),
            },
            cells = cells.Select(cell => new
            {
                kind = cell.Kind,
                cell.Column,
                text = cell.Text,
                x = Round(cell.X),
                y = Round(cell.Y),
                w = Round(cell.Width),
                h = Round(cell.Height),
                lines = cell.Lines,
                textHeight = Round(cell.TextHeight),
                right = cell.Right,
                wrap = cell.Wrap,
            }).ToList(),
        };

        Console.WriteLine(
            $"ИЗМЕР fit={fitCase} окно={windowWidth}x{windowHeight} "
            + $"таблица={Round(table.Bounds.Width)}x{Round(table.Bounds.Height)} шапка={Round(headerHeight)} "
            + $"строки=[{string.Join("; ", rows.Select(Round))}] ячеек={result.counts.cells} "
            + $"переносов={wrapped.Count} обрезано={clipped.Count}");

        foreach (var column in columns.Select(item => JsonSerializer.Serialize(item)))
        {
            Console.WriteLine("КОЛОНКА " + column);
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// ЧЕМ НАБРАН ВИД. Печатает действующие значения (не «что в теме написано», а что применилось
    /// к настоящему органу в дереве): отступы, подложки, рамки, скругления, шрифт и высоты.
    /// Строка «СЕЙЧАС У НАС» — то, чем тот же вид набран в продуктовой таблице.
    /// </summary>
    public static int Parts(Window window, TableView table, Grid product)
    {
        var boxes = 0;

        Part("шапка", table.GetVisualDescendants().OfType<TableViewColumnHeader>().FirstOrDefault());
        Part("шапка-текст", table.GetVisualDescendants().OfType<TableViewColumnHeader>()
            .SelectMany(header => header.GetVisualDescendants().OfType<TextBlock>()).FirstOrDefault());
        Part("ячейка", table.GetVisualDescendants().OfType<TableViewCell>().FirstOrDefault());
        Part("ячейка-текст", table.GetVisualDescendants().OfType<TableViewCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBlock>()).FirstOrDefault());
        Part("строка", table.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault());

        if (table.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault() is { } box)
        {
            boxes++;
            Part("галка", box);
        }

        // ПОДЛОЖКА ВЫБРАННОЙ СТРОКИ: TableViewRow : ListBoxItem, и подложку рисует его шаблон —
        // смотрим И саму строку, и первый ContentPresenter её шаблона (не ячейки: у ячейки такой же
        // по имени presenter, и он мельче — так и была поймана ошибка первого замера).
        var row = table.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault();

        if (row is not null && row.IsSelected)
        {
            Part("выбранная-строка", row);
            Part("выбранная-строка-шаблон", row.GetVisualChildren().OfType<ContentPresenter>().FirstOrDefault());
        }

        // ЧТО ПРИДЁТСЯ ПЕРЕОПРЕДЕЛИТЬ: свойства вида у TableView НЕ настраиваются вовсе.
        var tableProps = typeof(TableView).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        var columnProps = typeof(TableViewColumn).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();

        Console.WriteLine(
            "СВОЙСТВА TableView: высота-строки=" + Has(tableProps, "RowHeight")
            + " высота-шапки=" + Has(tableProps, "HeaderHeight")
            + " ширина-колонки=" + Has(columnProps, "Width")
            + " объединение-ячеек=" + Has(columnProps, "ColumnSpan") + "/" + Has(columnProps, "RowSpan")
            + " границы-ячеек=" + Has(columnProps, "BorderThickness"));

        // СЕЙЧАС У НАС — та же мерка по продуктовой сетке: строка Border, шапка и ячейка.
        var lines = product.Children.OfType<Border>().ToList();

        if (lines.Count > 0)
        {
            Part("наша-шапка", lines[0]);
            Part("наша-шапка-текст", lines[0].Child is Grid top ? top.Children.OfType<TextBlock>().FirstOrDefault() : null);
            Part("наша-строка", lines[^1]);
            Part("наша-ячейка-текст", lines[^1].Child is Grid last ? last.Children.OfType<TextBlock>().FirstOrDefault() : null);
        }

        return boxes;
    }

    /// <summary>
    /// СКОЛЬКО ТЕМ/КИСТЕЙ ТЕМЫ FLUENT ПРИДЁТСЯ ПЕРЕОПРЕДЕЛИТЬ. Имена — из самих словарей темы,
    /// а не из документации: ищутся ключи про таблицу, галочку и строку списка.
    /// </summary>
    public static int Resources()
    {
        var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (Application.Current is not null)
        {
            foreach (var style in Application.Current.Styles)
            {
                if (style is Styles styles) Walk(styles.Resources, keys);
            }
        }

        var topics = new[] { "TableView", "CheckBox", "ListBoxItem" };
        var found = keys.Where(pair => topics.Any(topic => pair.Key.Contains(topic, StringComparison.Ordinal))).ToList();

        Console.WriteLine(
            $"РЕСУРСЫ тема={Application.Current?.RequestedThemeVariant} всего-ключей={keys.Count} "
            + $"про-таблицу/галочку/строку={found.Count}");

        foreach (var pair in found) Console.WriteLine($"РЕСУРС {pair.Key} = {pair.Value}");

        return found.Count;
    }

    /// <summary>
    /// КОНФЛИКТ «ВЫБОР СТРОКИ ↔ ОРГАН В ЯЧЕЙКЕ». Четыре опыта, каждый — то, что человек сделает
    /// мышью или клавиатурой: щелчок по галочке, Tab, Space, стрелка вниз.
    /// </summary>
    public static void Input(Window window, TableView table)
    {
        var box = table.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
        var row = table.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault();

        window.Activate();

        // ВЫБОР СНЯТ ДО ЩЕЛЧКА: иначе «выбор не изменился» означало бы «он уже стоял», а не «щелчок
        // по органу строку не выбирает». Первый замер так и соврал — 0 до и 0 после.
        table.SelectedIndex = -1;
        Pump();

        var selectedBefore = table.SelectedIndex;
        var checkedBefore = box?.IsChecked;

        if (box is not null)
        {
            var point = ToWindow(box, window) + new Point(box.Bounds.Width / 2, box.Bounds.Height / 2);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        Console.WriteLine(
            $"ОПЫТ щелчок-по-галке: выбор={Selected(table)} (было {selectedBefore}) "
            + $"галка={box?.IsChecked} (было {checkedBefore}) фокус={Focused(window)}");

        // ЩЕЛЧОК ПО ОБЫЧНОЙ ЯЧЕЙКЕ — сторож того, что выбор строки вообще работает в мини-таблице.
        table.SelectedIndex = -1;
        Pump();

        if (table.GetVisualDescendants().OfType<TableViewCell>().Skip(1).FirstOrDefault() is { } cell)
        {
            var point = ToWindow(cell, window) + new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        Console.WriteLine($"ОПЫТ щелчок-по-ячейке: выбор={Selected(table)}");

        // ПРОВЕРКА САМОЙ ПРОВЕРКИ: та же клавиша по такой же галочке ВНЕ таблицы. Не переключилась —
        // значит Headless не доставляет клавиши вовсе, и «Space не работает в ячейке» ничего не значит.
        var control = new CheckBox { IsChecked = false };
        var probe = new Window { Content = control, Width = 120, Height = 60, SizeToContent = SizeToContent.Height };

        probe.Show();
        Pump();

        // ОКНО ПРОВЕРКИ ДОЛЖНО БЫТЬ ДЕЙСТВУЮЩИМ: Headless доставляет клавишу тому окну, которое
        // активно. Первый прогон этого не делал — галка осталась неотмеченной, и «Space в ячейке
        // не работает» было бы выводом из сломанной проверки, а не из TableView.
        probe.Activate();
        Pump();
        control.Focus(NavigationMethod.Tab);
        Pump();

        // SPACE СРАБАТЫВАЕТ НА ОТПУСКАНИИ КЛАВИШИ, а не на нажатии: KeyPress без KeyRelease не
        // переключает галку — проверено этим же контролем в первом прогоне.
        probe.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        probe.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();

        Console.WriteLine($"ОПЫТ контроль-вне-таблицы: галка={control.IsChecked} фокус={Focused(probe)}");
        probe.Close();
        Pump();
        window.Activate();
        Pump();

        // ФОКУС С КЛАВИАТУРЫ: Tab от строки — доходит ли до ячейки/галочки и в каком порядке.
        table.SelectedIndex = 0;
        row?.Focus(NavigationMethod.Directional);
        Pump();

        var chain = new List<string>();

        for (var step = 0; step < 6; step++)
        {
            window.FocusManager?.TryMoveFocus(NavigationDirection.Next);
            Pump();
            chain.Add(Identify(table, window));
        }

        Console.WriteLine("ОПЫТ Tab: " + string.Join(" → ", chain));

        // SPACE НА ГАЛОЧКЕ И НА СТРОКЕ: переключает ли галочку и меняет ли выбор строки.
        if (box is not null)
        {
            table.SelectedIndex = 0;
            box.IsChecked = true;
            box.Focus(NavigationMethod.Tab);
            Pump();

            var before = (box.IsChecked, table.SelectedIndex);

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Pump();

            Console.WriteLine(
                $"ОПЫТ Space-на-галке: галка={box.IsChecked} (было {before.IsChecked}) "
                + $"выбор={Selected(table)} (было {before.SelectedIndex})");

            // СТРЕЛКА ВНИЗ ПРИ ФОКУСЕ НА ГАЛОЧКЕ: ходит ли выбор строк, пока фокус в органе.
            var rowBefore = table.SelectedIndex;

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Pump();

            Console.WriteLine(
                $"ОПЫТ стрелка-вниз-на-галке: выбор={Selected(table)} (было {rowBefore}) фокус={Focused(window)}");
        }

        // SPACE НА СТРОКЕ: ListBox по Space выделяет строку — и это второй смысл той же клавиши.
        table.SelectedIndex = -1;
        row?.Focus(NavigationMethod.Directional);
        Pump();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();

        Console.WriteLine($"ОПЫТ Space-на-строке: выбор={Selected(table)}");
    }

    /// <summary>Кто получил фокус — по имени, а не «какой-то CheckBox»: иначе шесть шагов Tab неотличимы.</summary>
    private static string Identify(TableView table, Window window)
    {
        if (window.FocusManager?.GetFocusedElement() is not Control control) return "нет";

        if (control is CheckBox && window.Name != "probe")
        {
            var boxes = table.GetVisualDescendants().OfType<CheckBox>().ToList();
            var index = boxes.IndexOf((CheckBox)control);

            if (index >= 0) return "Галка#" + index;
        }

        if (control is TableViewRow row)
        {
            var rows = table.GetVisualDescendants().OfType<TableViewRow>().ToList();
            var index = rows.IndexOf(row);

            if (index >= 0) return "Строка#" + index;
        }

        return control.GetType().Name;
    }

    /// <summary>Названное значение свойства у настоящего органа: чем набран вид.</summary>
    private static void Part(string role, object? control)
    {
        if (control is not Control visual)
        {
            Console.WriteLine($"ТЕМА {role} = НЕТ");
            return;
        }

        var parts = new List<string>();

        foreach (var name in new[] { "Padding", "Margin", "Background", "BorderThickness", "BorderBrush", "CornerRadius", "FontSize", "FontWeight", "MinHeight", "Height" })
        {
            var property = visual.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.GetValue(visual) is not { } value) continue;

            parts.Add(name + "=" + Format(value));
        }

        Console.WriteLine(
            $"ТЕМА {role} {visual.GetType().Name} h={Round(visual.Bounds.Height)} w={Round(visual.Bounds.Width)} "
            + string.Join(" ", parts));
    }

    private static string Format(object value) => value switch
    {
        SolidColorBrush brush => "#" + brush.Color.ToString()[3..] + (brush.Opacity < 1 ? $"@{brush.Opacity:0.##}" : string.Empty),
        IBrush brush => brush.GetType().Name,
        Thickness thickness => $"{thickness.Left:0.#},{thickness.Top:0.#},{thickness.Right:0.#},{thickness.Bottom:0.#}",
        CornerRadius radius => $"{radius.TopLeft:0.#},{radius.TopRight:0.#},{radius.BottomRight:0.#},{radius.BottomLeft:0.#}",
        double number => double.IsNaN(number) ? "auto" : number.ToString("0.#", CultureInfo.InvariantCulture),
        GridLength length => length.IsAbsolute ? length.Value.ToString("0.#", CultureInfo.InvariantCulture) : length.ToString(),
        _ => value.ToString() ?? "?",
    };

    private static void Walk(IResourceDictionary? dictionary, SortedDictionary<string, string> keys)
    {
        if (dictionary is null) return;

        if (dictionary is ResourceDictionary concrete)
        {
            foreach (var key in concrete.Keys)
            {
                if (key is string name) keys[name] = concrete[key]?.GetType().Name ?? "null";
            }
        }

        foreach (var merged in dictionary.MergedDictionaries) Walk(merged as IResourceDictionary, keys);

        foreach (var pair in dictionary.ThemeDictionaries) Walk(pair.Value as IResourceDictionary, keys);
    }

    private static string Has(IReadOnlyCollection<string> names, string name) => names.Contains(name) ? "есть" : "нет";

    private static string Selected(TableView table) => table.SelectedIndex.ToString(CultureInfo.InvariantCulture);

    private static string Focused(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Control)?.GetType().Name ?? "нет";

    private static FitCell Cell(string kind, int column, TextBlock block, Window window)
    {
        var point = ToWindow(block, window);

        return new FitCell(
            kind, column, block.Text ?? string.Empty, point.X, point.Y, block.Bounds.Width, block.Bounds.Height,
            Lines(block), LayoutHeight(block),
            block.TextAlignment == TextAlignment.Right, block.TextWrapping == TextWrapping.Wrap);
    }

    private static int ColumnIndex(TableView table, TableViewColumn? column) =>
        column is null ? -1 : table.Columns.IndexOf(column);

    private static Point ToWindow(Visual control, Window window) =>
        control.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);

    private static double Round(double value) => Math.Round(value, 1);

    private static double Round(double? value) => value is null ? 0 : Math.Round(value.Value, 1);

    /// <summary>Число строк разметки — отражением, как в T1: имя свойства не хардкодим.</summary>
    private static int Lines(TextBlock block)
    {
        var layout = block.GetType().GetProperty("TextLayout", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);

        return layout?.GetType().GetProperty("TextLines")?.GetValue(layout) is ICollection lines ? lines.Count : 0;
    }

    private static double LayoutHeight(TextBlock block)
    {
        var layout = block.GetType().GetProperty("TextLayout", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);

        return layout?.GetType().GetProperty("Height")?.GetValue(layout) is double height ? height : 0;
    }

    /// <summary>Проход очереди и отрисовки — как в <see cref="Probe.Pump"/> для кадра без экрана.</summary>
    private static void Pump()
    {
        Dispatcher.FromThread(System.Threading.Thread.CurrentThread)?.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.FromThread(System.Threading.Thread.CurrentThread)?.RunJobs();
    }
}
