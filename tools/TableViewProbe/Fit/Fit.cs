using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshPanel;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Views;

namespace TableViewProbe.Fit;

/// <summary>
/// ПРОБА «ПИКИ» И «ПЛАН НАКАТА» — второй шаг этапа 2.4.3.
///
/// Две мини-таблицы по образцу наших (матрица окон — как «График пиков» и окна истории цен;
/// план наката — как «Что вернём», с галочкой согласия В ЯЧЕЙКЕ) собираются НАСТОЯЩИМ
/// <see cref="TableView"/> и меряются теми же числами, что «История цен» в T1: высоты, переносы,
/// выравнивание шапки над значениями, правый край чисел. Сверх T1 — вид (какими свойствами темы
/// он набран), сколько тем придётся переопределить и что делают мышь и клавиатура с органом
/// внутри ячейки.
///
/// Ключи: <c>--fit &lt;каталог&gt; [--case matrix|plan] [--theme light|dark] [--name файл]
/// [--measure файл] [--width N]</c>. Незнакомый ключ — отказ кодом 2.
/// </summary>
internal static class Fit
{
    public static int Run(string[] args)
    {
        var dir = string.Empty;
        var fitCase = "matrix";
        var theme = "light";
        var name = string.Empty;
        var measure = string.Empty;
        var width = 0;
        var first = 1;

        // Каталог стоит сразу за ключом режима: `--fit <каталог>` — как `--frames` у T1.
        if (args.Length < 2) Refuse("--fit без каталога");

        dir = args[1];
        first = 2;

        for (var i = first; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fit":
                    dir = Take(args, ref i, "--fit");
                    break;
                case "--case":
                    fitCase = Take(args, ref i, "--case");
                    if (fitCase is not ("matrix" or "plan")) Refuse($"незнакомый вид «{fitCase}»: годятся matrix|plan");
                    break;
                case "--theme":
                    theme = Take(args, ref i, "--theme");
                    if (theme is not ("light" or "dark")) Refuse($"незнакомая тема «{theme}»: годятся light|dark");
                    break;
                case "--name":
                    name = Take(args, ref i, "--name");
                    break;
                case "--measure":
                    measure = Take(args, ref i, "--measure");
                    break;
                case "--width":
                    width = int.Parse(Take(args, ref i, "--width"), CultureInfo.InvariantCulture);
                    break;
                default:
                    Refuse($"незнакомый ключ «{args[i]}»");
                    break;
            }
        }

        if (dir.Length == 0) Refuse("--fit без каталога");
        if (width <= 0) width = fitCase == "matrix" ? 626 : 900;

        Directory.CreateDirectory(dir);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        if (Loc.KeyCount == 0) Loc.Init("ru");

        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        var table = fitCase == "matrix" ? FitTables.Matrix(Matrix()) : FitTables.Plan(Plan());
        var panel = new Grid { Name = "FitPanel" };
        panel.Children.Add(table);

        var window = new Window
        {
            Title = "ПРОБА fit " + fitCase,
            Width = width,
            SizeToContent = SizeToContent.Height,
            Content = new Border { Padding = new Thickness(16), Child = panel },
        };

        window.Show();
        Pump();

        // ШИРИНА — РУКАМИ, как в T1: у TableView «звезда» не тянется, иначе звездные колонки
        // померились бы по содержимому (урок кадра 02).
        Console.WriteLine($"ШИРИНА панель={panel.Bounds.Width:0.#} таблица={table.Bounds.Width:0.#}");
        if (panel.Bounds.Width > 0) table.Width = panel.Bounds.Width;
        Pump();

        var size = new PixelSize((int)Math.Ceiling(window.ClientSize.Width), (int)Math.Ceiling(window.ClientSize.Height));

        var json = FitMeasure.Capture(window, fitCase, table, size.Width, size.Height);

        if (measure.Length > 0)
        {
            File.WriteAllText(measure, json);
            Console.WriteLine("ИЗМЕР файл=" + measure);
        }

        if (fitCase == "plan")
        {
            table.SelectedIndex = 0;
            Pump();
            FitMeasure.Parts(window, table, Product());
            FitMeasure.Input(window, table);
            Pump();
            Appearance(window, table, size);

            // ВИДИМОСТЬ ФОКУСА — ЧИСЛОМ: два кадра одного окна и разница пикселей. Строка и галочка
            // получают фокус по Tab (NavigationMethod.Tab), как у человека, а не вызовом Focus().
            FocusDiff(window, table, size);
        }
        else
        {
            FitMeasure.Parts(window, table, Product());
            Appearance(window, table, size);
        }

        FitMeasure.Resources();

        var result = name.Length == 0 ? 0 : Shot(window, Path.Combine(dir, name), size);

        return result;
    }

    /// <summary>Строки матрицы: три дня с разным переносом и пара «Было/Стало» из истории цен.</summary>
    private static IReadOnlyList<MatrixRow> Matrix() => new[]
    {
        new MatrixRow("пн", "08:00–22:00", "22:00–08:00"),
        new MatrixRow("вт", "08:00–22:00, 03:00–05:00", "23:00–07:00"),
        new MatrixRow("ср", "08:00–22:00", "23:00–07:00, 12:00–14:00, 03:00–05:00"),
        new MatrixRow(PanelStrings.PricingHistoryColumnBefore, "08:00–22:00\n22:00–08:00", "круглые сутки"),
        new MatrixRow(PanelStrings.PricingHistoryColumnAfter, "09:00–21:00", "21:00–09:00\n12:00–14:00"),
    };

    /// <summary>Строки плана: три группы и три галочки согласия — те же, что стоят под таблицей сегодня.</summary>
    private static IReadOnlyList<PlanRow> Plan() => new[]
    {
        new PlanRow(PanelStrings.RestoreEngineCheck, "~\\AppData\\Roaming\\npm", "1 208", PanelStrings.RestoreTableLands, true),
        new PlanRow(PanelStrings.RestorePanelCheck, "~\\AppData\\Local\\DshPanel2", "46", PanelStrings.RestoreTableLands, true),
        new PlanRow(PanelStrings.RestoreKeysCheck, "~\\AppData\\Roaming\\DshPanel2\\keys", "12", "нужно согласие", false),
    };

    /// <summary>
    /// НАСТОЯЩАЯ ПРОДУКТОВАЯ ТАБЛИЦА — ДЛЯ СРАВНЕНИЯ ВИДА. Собирается тем же построителем, что
    /// в окнах панели, и в дерево не ставится: у неё меряется только то, чем набран её вид.
    /// </summary>
    private static Grid Product()
    {
        var grid = new Grid();
        PriceTableView.Fill(grid, Array.Empty<DshPanel.Pricing.PriceRow>());

        return grid;
    }

    /// <summary>
    /// ВИД — ПИКСЕЛЯМИ, А НЕ СЛОВАМИ: тон подложки выбранной строки, полоса шапки и ВЕРТИКАЛЬНЫЕ
    /// линии между колонками (в наших таблицах их нет вовсе — строки разделены горизонтально).
    /// </summary>
    private static void Appearance(Window window, TableView table, PixelSize size)
    {
        var pixels = Pixels(window, size);
        var rows = table.GetVisualDescendants().OfType<TableViewRow>().ToList();
        var header = table.GetVisualDescendants().OfType<TableViewColumnHeader>().FirstOrDefault();
        var origin = table.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);

        if (rows.Count >= 2 && header is not null)
        {
            var second = RowPoint(rows[1], window);
            var selected = rows.FirstOrDefault(row => row.IsSelected);

            Console.WriteLine(
                $"ВИД шапка={Hex(pixels, size, (int)origin.X + 3, (int)origin.Y + 3)} "
                + $"выбранная={(selected is null ? "нет-выбора" : Hex(pixels, size, (int)RowPoint(selected, window).X + 3, (int)(RowPoint(selected, window).Y + selected.Bounds.Height / 2)))} "
                + $"невыбранная={Hex(pixels, size, (int)second.X + 3, (int)(second.Y + rows[1].Bounds.Height / 2))}");
        }

        // СТЫКИ КОЛОНОК: считаем, сколько точек в теле таблицы отличаются от подложки на 4 точки левее.
        var top0 = origin.Y + (header?.Bounds.Height ?? 0) + 2;
        var bottom = origin.Y + table.Bounds.Height - 2;
        var boundary = origin.X;

        for (var index = 0; index < table.Columns.Count - 1; index++)
        {
            boundary += table.Columns[index].ActualWidth;
            var x = (int)boundary;
            var y0 = (int)top0;
            var y1 = (int)bottom;
            var line = 0;

            if (x - 4 >= 0 && y1 > y0)
            {
                var reference = Hex(pixels, size, x - 4, y0 + 2);

                for (var y = y0; y < y1; y++)
                {
                    if (Hex(pixels, size, x, y) != reference) line++;
                }
            }

            Console.WriteLine($"ВИД стык-колонок#{index} x={x} линия={line} из {Math.Max(0, y1 - y0)}");
        }
    }

    private static Point RowPoint(Control control, Window window) =>
        control.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);

    private static string Hex(byte[] pixels, PixelSize size, int x, int y)
    {
        if (x < 0 || y < 0 || x >= size.Width || y >= size.Height) return "вне-кадра";

        var i = (y * size.Width + x) * 4;

        return "#" + pixels[i + 2].ToString("X2") + pixels[i + 1].ToString("X2") + pixels[i].ToString("X2");
    }

    private static void FocusDiff(Window window, TableView table, PixelSize size)
    {
        var row = table.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault();
        var box = table.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();

        if (row is not null)
        {
            Compare(window, size, "строка", () => row.Focus(NavigationMethod.Tab));
        }

        if (box is not null)
        {
            Compare(window, size, "галка", () => box.Focus(NavigationMethod.Tab));
        }
    }

    /// <summary>Сколько пикселей меняет фокус: ноль — это «фокуса не видно», и так и говорится.</summary>
    private static void Compare(Window window, PixelSize size, string role, Action focus)
    {
        window.FocusManager?.Focus(null, NavigationMethod.Unspecified);
        Pump();

        var before = Pixels(window, size);

        focus();
        Pump();

        var after = Pixels(window, size);
        var changed = 0;

        for (var i = 0; i + 3 < before.Length; i += 4)
        {
            if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2]) changed++;
        }

        Console.WriteLine($"ФОКУС {role}: изменилось пикселей={changed} из {size.Width * size.Height}");
    }

    private static byte[] Pixels(Window window, PixelSize size)
    {
        var (_, pixels) = Render(window, size);

        return pixels;
    }

    private static (RenderTargetBitmap Bitmap, byte[] Pixels) Render(Window window, PixelSize size)
    {
        var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return (bitmap, buffer);
    }

    /// <summary>Кадр — как в <c>Program.Shot</c>: «не пустой» проверяется числом, а не глазами.</summary>
    private static int Shot(Window window, string path, PixelSize size)
    {
        var (bitmap, pixels) = Render(window, size);
        bitmap.Save(path, new PngBitmapEncoderOptions());

        var colors = new HashSet<int>();
        long opaque = 0;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0) opaque++;
            colors.Add((pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2]);
        }

        Console.WriteLine($"КАДР {size.Width}x{size.Height} непрозрачных={opaque} цветов={colors.Count} файл={path}");

        return opaque > 0 && colors.Count > 1 ? 0 : 1;
    }

    private static void Pump()
    {
        Dispatcher.FromThread(System.Threading.Thread.CurrentThread)?.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.FromThread(System.Threading.Thread.CurrentThread)?.RunJobs();
    }

    private static string Take(string[] args, ref int i, string key)
    {
        if (i + 1 >= args.Length) Refuse($"{key} без значения");

        return args[++i];
    }

    private static void Refuse(string reason)
    {
        Console.WriteLine("ПРОБА ОТКАЗ: " + reason);
        Environment.Exit(2);
    }
}
