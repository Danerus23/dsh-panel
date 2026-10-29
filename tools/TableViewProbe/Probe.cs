using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshPanel;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Views;

namespace TableViewProbe;

/// <summary>Ключи пробы. Незнакомый ключ — отказ кодом 2, а не «снимем как получилось».</summary>
internal sealed record Options(
    string Mode,
    string Theme,
    int Entry,
    int Select,
    int Width,
    int Seconds,
    bool Offscreen,
    string Dir,
    string Name,
    string MeasureFile)
{
    public static Options Parse(string[] args)
    {
        var mode = "now";
        var theme = "light";
        var entry = 0;
        var select = -1;
        var width = 0;
        var seconds = 3;
        var offscreen = false;
        var dir = string.Empty;
        var name = string.Empty;
        var measure = string.Empty;

        // Каталог кадров стоит сразу за ключом режима: `--frames <каталог>`, как в задании.
        var first = 1;

        if (args[0] == "--frames")
        {
            if (args.Length < 2) Refuse("--frames без каталога");

            dir = args[1];
            first = 2;
        }

        for (var i = first; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode":
                    mode = Take(args, ref i, "--mode");
                    if (mode is not ("now" or "proto")) Refuse($"незнакомый вид «{mode}»: годятся now|proto");
                    break;
                case "--theme":
                    theme = Take(args, ref i, "--theme");
                    if (theme is not ("light" or "dark")) Refuse($"незнакомая тема «{theme}»: годятся light|dark");
                    break;
                case "--entry":
                    entry = Number(Take(args, ref i, "--entry"), "--entry");
                    break;
                case "--select":
                    select = Number(Take(args, ref i, "--select"), "--select");
                    break;
                case "--width":
                    width = Number(Take(args, ref i, "--width"), "--width");
                    break;
                case "--seconds":
                    seconds = Number(Take(args, ref i, "--seconds"), "--seconds");
                    break;
                case "--offscreen":
                    offscreen = true;
                    break;
                case "--name":
                    name = Take(args, ref i, "--name");
                    break;
                case "--measure":
                    measure = Take(args, ref i, "--measure");
                    break;
                default:
                    Refuse($"незнакомый ключ «{args[i]}»");
                    break;
            }
        }

        return new Options(mode, theme, entry, select, width, seconds, offscreen, dir, name, measure);
    }

    private static string Take(string[] args, ref int i, string key)
    {
        if (i + 1 >= args.Length) Refuse($"{key} без значения");

        return args[++i];
    }

    private static int Number(string text, string key) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{key}: не число «{text}»");

    public static void Refuse(string reason)
    {
        Console.WriteLine("ПРОБА ОТКАЗ: " + reason);
        Environment.Exit(2);
    }
}

/// <summary>
/// ДВА ВИДА ОДНОЙ ТАБЛИЦЫ В НАСТОЯЩЕМ ОКНЕ «ИСТОРИЯ ЦЕН».
///
/// Окно — продуктовое (<see cref="PricingHistoryWindow"/>), данные — образцовые
/// (<see cref="ShotDrawers.Pricing"/>: история из <c>ShotSamples.PricingHistoryText</c>).
/// Вид выбирается ключом <c>--mode</c>:
/// * <c>now</c> — как есть: <c>PriceTableView.Fill</c> + <c>TableGrid</c>;
/// * <c>proto</c> — содержимое панели <c>PriceTablePanel</c> заменяется на <c>TableView</c>
///   (<see cref="Prototype"/>), данные берутся ИЗ УЖЕ ПОСТРОЕННОЙ таблицы «сейчас» — те же слова,
///   те же тона, те же выравнивания. Продукт при этом не меняется: подмена живёт в этом проекте.
/// </summary>
internal static class Probe
{
    public static int Frames(Options options)
    {
        if (options.Dir.Length == 0) Options.Refuse("--frames без каталога");

        Directory.CreateDirectory(options.Dir);

        _headless = true;
        Shown(options, headless: true);

        var window = Build(options);

        window.Show();
        Pump();

        Fit(window);
        Pump();

        Select(window, options.Select);
        Pump();

        var width = (int)Math.Ceiling(window.ClientSize.Width);
        var height = (int)Math.Ceiling(window.ClientSize.Height);

        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"ПРОБА ПРОВАЛ: пустой размер окна {width}x{height}");
            return 2;
        }

        var result = 0;

        if (options.Name.Length > 0)
        {
            var path = Path.Combine(options.Dir, options.Name);
            result = Save(window, path, width, height);
        }

        var json = Measure.Capture(window, options.Mode, options.Theme, width, height);

        if (options.MeasureFile.Length > 0)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(options.MeasureFile));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(options.MeasureFile, json);
            Console.WriteLine("ИЗМЕР файл=" + options.MeasureFile);
        }

        return result;
    }

    public static int Live(Options options)
    {
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .SetupWithoutStarting();

        Theme(options.Theme);

        var window = Build(options);

        if (options.Offscreen)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-3000, 80);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Show();

        Console.WriteLine(
            $"LIVE окно pid={Environment.ProcessId} вид={options.Mode} тема={options.Theme} "
            + $"внеЭкрана={options.Offscreen} секунд={options.Seconds}");
        Console.Out.Flush();

        using var stop = new CancellationTokenSource();

        // РАСКЛАДКА И ВЫБОР — ВНУТРИ ЦИКЛА СООБЩЕНИЙ: у Win32 раскладку двигает он, и до цикла
        // окно ещё не измерено. Две отложенные ступени: сначала ширина/выбор, потом «готово»
        // (к этому моменту кадр уже отрисован и UIA есть что читать).
        var layout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        layout.Tick += (_, _) =>
        {
            layout.Stop();

            Fit(window);

            // ⚠️ В живом прогоне ширину задаём ДО первого кадра: у TableView «звезда» не тянется,
            // и без этого таблица уже нарисовалась бы на 32 точки уже панели.
            Select(window, options.Select);
        };

        var ready = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        ready.Tick += (_, _) =>
        {
            ready.Stop();

            Console.WriteLine(
                $"LIVE готово title={window.Title} размер={window.ClientSize.Width}x{window.ClientSize.Height}");
            Console.Out.Flush();
        };

        var quit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1 + options.Seconds) };
        quit.Tick += (_, _) =>
        {
            quit.Stop();
            Console.WriteLine("LIVE конец");
            Console.Out.Flush();
            stop.Cancel();
        };

        layout.Start();
        ready.Start();
        quit.Start();

        Dispatcher.UIThread.MainLoop(stop.Token);

        window.Close();
        return 0;
    }

    /// <summary>Подъём платформы и темы: у кадра без экрана — голова, у живого прогона — Win32.</summary>
    private static void Shown(Options options, bool headless)
    {
        if (!headless) return;

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Theme(options.Theme);
    }

    private static void Theme(string theme)
    {
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant =
            theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>
    /// СЛОВА — КАК У ЧЕЛОВЕКА: без <see cref="Loc.Init"/> панель отдаёт ключи вместо фраз
    /// (проверено первым прогоном: в таблице стояло «PriceTableColumnItem»). Язык кадра назван
    /// явно — «ru», чтобы кадр не зависел от языка машины.
    /// </summary>
    private static void Words()
    {
        if (Loc.KeyCount == 0) Loc.Init("ru");
    }

    /// <summary>
    /// ОКНО И ЕГО ТАБЛИЦА. Одно окно на оба вида: подменяется ТОЛЬКО панель таблицы цен,
    /// найденная по имени (<c>PriceTablePanel</c>), — шапка окна, список записей и таблица окон
    /// остаются продуктовыми. Своего окна проба не заводит: иначе мерился бы не наш экран.
    /// </summary>
    private static PricingHistoryWindow Build(Options options)
    {
        Words();

        var window = new PricingHistoryWindow();

        if (options.Width > 0) window.Width = options.Width;

        var pricing = ShotDrawers.Pricing();
        window.Attach(pricing);

        var list = window.FindControl<ListBox>("EntriesList");

        if (list is not null && pricing.History.Entries > 0)
        {
            list.SelectedIndex = Math.Clamp(options.Entry, 0, pricing.History.Entries - 1);
        }

        var panel = window.FindControl<Grid>("PriceTablePanel")
                    ?? throw new InvalidOperationException("В окне истории нет панели PriceTablePanel");

        if (options.Mode == "proto")
        {
            var rows = ReadRows(panel);

            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "Таблица «сейчас» пуста — прототипу нечего показать: сначала должен отработать продукт");
            }

            var headers = new[]
            {
                PanelStrings.PriceTableColumnItem,
                PanelStrings.PeakTableColumnOffPeak,
                PanelStrings.PricingHistoryColumnChange,
                PanelStrings.PeakTableColumnPeak,
                PanelStrings.PricingHistoryColumnChange,
            };

            panel.Children.Clear();
            panel.Children.Add(Prototype.Build(rows, headers));
        }

        return window;
    }

    /// <summary>
    /// СТРОКИ — ИЗ НАСТОЯЩЕЙ ТАБЛИЦЫ, А НЕ ИЗ ВТОРОГО РАСЧЁТА. Так прототип и «сейчас» показывают
    /// одни и те же слова по построению: второй расчёт тех же строк однажды разошёлся бы с первым
    /// (и это был бы не замер таблицы, а замер двух разборов).
    /// </summary>
    private static IReadOnlyList<ProtoRow> ReadRows(Grid panel)
    {
        var rows = new List<ProtoRow>();

        foreach (var border in panel.Children.OfType<Border>())
        {
            if (border.Child is not Grid grid) continue;

            var map = grid.Children.OfType<TextBlock>()
                .GroupBy(Grid.GetColumn)
                .ToDictionary(group => group.Key, group => group.First());

            if (map.Count > 0 && map.Values.All(block => block.Classes.Contains("hint"))) continue;

            rows.Add(new ProtoRow(
                Cell(map, 0, wrap: true),
                Cell(map, 2, right: true),
                Cell(map, 3, right: true),
                Cell(map, 5, right: true),
                Cell(map, 6, right: true)));
        }

        return rows;
    }

    private static ProtoCell Cell(IReadOnlyDictionary<int, TextBlock> map, int column, bool right = false, bool wrap = false)
    {
        if (!map.TryGetValue(column, out var block))
        {
            return new ProtoCell(string.Empty, right, wrap, null);
        }

        var tone = block.Classes.FirstOrDefault(name => name.StartsWith("tone-", StringComparison.Ordinal));

        return new ProtoCell(block.Text ?? string.Empty, block.TextAlignment == TextAlignment.Right, block.TextWrapping == TextWrapping.Wrap, tone);
    }

    /// <summary>
    /// ШИРИНА ТАБЛИЦЫ — РУКАМИ, И ЭТО НАЗВАННАЯ ЦЕНА ПРОТОТИПА.
    ///
    /// Замер кадра 02: <c>TableView</c> с колонкой <c>Star</c> не растянулся на ширину панели —
    /// 562 точки вместо 594, звёздная колонка мерилась по содержимому, и правый край таблицы
    /// не совпал с блоком «Изменения окон пика» ниже. Задание ширины САМОЙ таблице лечит край,
    /// но сдвигает её содержимое на 4 точки влево (шапка «Единица» обрезалась в «диница») —
    /// поэтому растягивается КОЛОНКА, а размер таблицы получается сам.
    ///
    /// ⚠️ В переезде такую связку пришлось бы писать на каждый экран: «звезда» у TableView
    /// не тянется сама.
    /// </summary>
    private static void Fit(Window window)
    {
        var panel = window.FindControl<Grid>("PriceTablePanel");
        var table = panel?.Children.OfType<TableView>().FirstOrDefault();

        if (panel is null || table is null || table.Columns.Count == 0 || panel.Bounds.Width <= 0) return;

        var missing = panel.Bounds.Width - table.Bounds.Width;

        Console.WriteLine(
            $"ШИРИНА панель={panel.Bounds.Width:0.#} таблица={table.Bounds.Width:0.#} "
            + $"желает={table.DesiredSize.Width:0.#} Width={table.Width:0.#} "
            + $"выравнивание={table.HorizontalAlignment} звезда={StarWidth(table):0.#} недостаёт={missing:0.#}");

        table.Width = panel.Bounds.Width;

        Pump();
    }

    private static double StarWidth(TableView table) =>
        table.Columns.Count == 0 ? 0 : table.Columns[0].ActualWidth;

    /// <summary>Выбранная строка прототипа и фокус на ней — то состояние, чего у «сейчас» нет.</summary>
    private static void Select(Window window, int index)
    {
        if (index < 0) return;

        var table = window.FindControl<Grid>("PriceTablePanel")?.Children.OfType<TableView>().FirstOrDefault();
        if (table is null) return;

        window.Activate();

        table.SelectedIndex = index;
        table.Focus();

        var row = table.GetVisualDescendants().OfType<TableViewRow>().ElementAtOrDefault(index);
        row?.Focus();

        // ФОКУС СТАВИТСЯ ПСЕВДОКЛАССОМ — как в продуктовой съёмке (Program.ShotState):
        // у безголового кадра нет ни мыши, ни клавиатуры, и одного Focus() теме мало.
        if (row is not null)
        {
            var rowClasses = (Avalonia.Controls.IPseudoClasses)row.Classes;
            rowClasses.Set(":focus", true);
            rowClasses.Set(":focus-visible", true);
        }

        ((Avalonia.Controls.IPseudoClasses)table.Classes).Set(":focus", true);

        Console.WriteLine(
            $"ВЫБОР строка={table.SelectedIndex} фокус-таблицы={table.IsFocused} фокус-строки={row?.IsFocused}");
    }

    private static bool _headless;

    /// <summary>
    /// ПРОХОД ОЧЕРЕДИ И ОТРИСОВКИ. У кадра без экрана раскладку двигает тик головой платформы;
    /// у живого Win32-окна этого делать нельзя (Compositor не поднят той платформой) — там
    /// раскладку двигает цикл сообщений, и хватает очереди.
    /// </summary>
    private static void Pump()
    {
        Dispatcher.FromThread(Thread.CurrentThread)?.RunJobs();
        if (_headless) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.FromThread(Thread.CurrentThread)?.RunJobs();
    }

    /// <summary>Кадр — как в <c>Program.Shot</c>: RenderTargetBitmap, и «не пустой» проверяется числами.</summary>
    private static int Save(Window window, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);

        var stride = width * 4;
        var buffer = new byte[stride * height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        bitmap.Save(path, new PngBitmapEncoderOptions());

        long opaque = 0;
        var colors = new HashSet<int>();

        for (var i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (buffer[i + 3] != 0) opaque++;
            colors.Add((buffer[i] << 16) | (buffer[i + 1] << 8) | buffer[i + 2]);
        }

        Console.WriteLine($"КАДР {width}x{height} непрозрачных={opaque} цветов={colors.Count} файл={path}");

        return opaque > 0 && colors.Count > 1 ? 0 : 1;
    }
}
