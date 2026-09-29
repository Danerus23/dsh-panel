using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТАБЛИЦА «СТОИМОСТЬ» — цена за единицу, как её печатает СТРАНИЦА ЦЕН.
///
/// Замечание владельца 27.09.2026, глядя на панель живьём: *«табличка пиков собрана неверно —
/// сделать ДВЕ таблицы»*. Эта — вторая: расписание живёт в «Графике пиков»
/// (<c>PeakTableViewTests</c>), а здесь только цена, и вопрос «сколько стоит» получает свой ответ.
///
/// Проверяется НАСТОЯЩИЙ построитель (<see cref="PriceTableView"/>): колонки, шапка, строки
/// и разделители читаются у собранной таблицы. Данные берутся готовыми (<see cref="PriceRow"/>) —
/// второй расчёт цены завёл бы вторую правду о тарифе.
/// </summary>
public class PriceTableViewTests
{
    /// <summary>Строки такой же формы, как у страницы цен: единица и два тарифа.</summary>
    private static readonly PriceRow[] Sample =
    {
        new("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
        new("1M INPUT TOKENS (CACHE MISS)", "$0.15", "$0.3"),
        new("1M OUTPUT TOKENS", "$0.6", "$1.2"),
    };

    private static Grid Table(IReadOnlyList<PriceRow> rows)
    {
        var grid = new Grid();
        PriceTableView.Fill(grid, rows);

        return grid;
    }

    /// <summary>
    /// ТРИ КОЛОНКИ С ТЕКСТОМ И ДВА ПРОМЕЖУТКА; шапка — строки словаря, а не литералы: правило
    /// проекта «ни одного текста в разметке» действует и здесь, иначе на английском кадре подписи
    /// остались бы русскими.
    ///
    /// ⚠️ Колонки цен названы ТЕМИ ЖЕ СЛОВАМИ, что колонки графика («Пик» и «Вне пика»): одно
    /// понятие — одно имя. Второй набор слов для того же самого разошёлся бы с первым.
    /// </summary>
    [AvaloniaFact]
    public void Таблица_это_три_колонки_с_подписями_из_словаря()
    {
        var table = Table(Sample);

        Assert.Equal(5, table.ColumnDefinitions.Count);

        Assert.True(table.ColumnDefinitions[0].Width.IsStar, "колонка единицы не растягивается");
        Assert.Equal(12, table.ColumnDefinitions[1].Width.Value);
        Assert.True(table.ColumnDefinitions[2].Width.IsAuto, "колонка цены вне пика не Auto");
        Assert.Equal(12, table.ColumnDefinitions[3].Width.Value);
        Assert.True(table.ColumnDefinitions[4].Width.IsAuto, "колонка цены пика не Auto");

        var header = (Grid)PeakTableProbe.Lines(table)[0].Child!;

        var texts = header.Children.OfType<TextBlock>().OrderBy(cell => Grid.GetColumn(cell))
            .Select(cell => cell.Text)
            .ToArray();

        Assert.Equal(
            new[]
            {
                PanelStrings.PriceTableColumnItem,
                PanelStrings.PeakTableColumnOffPeak,
                PanelStrings.PeakTableColumnPeak,
            },
            texts);

        Assert.Equal(3, texts.Distinct().Count());

        // Обе цены справа, единица слева — как и её значения.
        Assert.Equal(
            TextAlignment.Left,
            header.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == 0).TextAlignment);

        foreach (var column in new[] { 2, 4 })
        {
            Assert.Equal(
                TextAlignment.Right,
                header.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == column).TextAlignment);
        }

        foreach (var cell in header.Children.OfType<TextBlock>())
        {
            Assert.Contains("hint", cell.Classes);
            Assert.Equal(FontWeight.SemiBold, cell.FontWeight);
        }

        // ⚠️ НАИМЕНЬШАЯ ШИРИНА КОЛОНКИ ЦЕНЫ — ЛИТЕРАЛ, а не сравнение константы с самой собой:
        // обнули её — и колонки цен в разных строках разойдутся (шапка шире числа), а проверка,
        // написанная через ту же константу, промолчала бы. Число названо прямо, и рядом —
        // что именно оно держит.
        Assert.Equal(72, PriceTableView.PriceColumnWidth);

        foreach (var column in new[] { 2, 4 })
        {
            var cell = header.Children.OfType<TextBlock>().Single(candidate => Grid.GetColumn(candidate) == column);

            Assert.Equal(PriceTableView.PriceColumnWidth, cell.MinWidth);
        }

        foreach (var line in PeakTableProbe.Blocks(table).Skip(1))
        {
            foreach (var column in new[] { 2, 4 })
            {
                var cell = line.Single(candidate => Grid.GetColumn(candidate) == column);

                Assert.Equal(PriceTableView.PriceColumnWidth, cell.MinWidth);
            }
        }
    }

    /// <summary>
    /// СТРОКА НА КАЖДУЮ ЕДИНИЦУ СТРАНИЦЫ, и все три значения — ровно те, что пришли готовыми.
    /// Пустого места в таблице быть не должно: строка без цены читалась бы как «панель забыла».
    /// </summary>
    [AvaloniaFact]
    public void Строка_на_каждую_единицу_и_значения_приходят_готовыми()
    {
        var table = Table(Sample);
        var cells = PeakTableProbe.Cells(table);

        // Четыре линии: шапка и три единицы.
        Assert.Equal(4, cells.Count);

        for (var index = 0; index < Sample.Length; index++)
        {
            Assert.Equal(
                new[] { Sample[index].Item, Sample[index].OffPeak, Sample[index].Peak },
                cells[index + 1]);
        }

        Assert.All(cells.Skip(1), line => Assert.All(line, value => Assert.False(string.IsNullOrWhiteSpace(value))));
    }

    /// <summary>
    /// БЕЗ ЦЕН ТАБЛИЦА ОСТАЁТСЯ ОДНОЙ ШАПКОЙ — и это честно: почему цен нет, говорит строка под
    /// таблицей словами, а подставленные нули читались бы как настоящая цена.
    /// </summary>
    [AvaloniaFact]
    public void Без_цен_остаётся_одна_шапка()
    {
        var table = Table(Array.Empty<PriceRow>());

        Assert.Single(PeakTableProbe.Lines(table));
        Assert.Equal(3, PeakTableProbe.Cells(table)[0].Length);
    }

    /// <summary>Разделитель — тот же, что у таблицы графика: тонкая линия снизу у каждой линии.</summary>
    [AvaloniaFact]
    public void Разделитель_тот_же_что_у_таблицы_графика()
    {
        foreach (var line in PeakTableProbe.Lines(Table(Sample)))
        {
            Assert.Contains(PeakTableView.SeparatorClass, line.Classes);
            Assert.Equal(1, line.BorderThickness.Bottom);
            Assert.Equal(4, line.Padding.Bottom);
        }
    }

    /// <summary>
    /// ПОВТОРНОЕ НАПОЛНЕНИЕ ЗАМЕНЯЕТ ТАБЛИЦУ: её пересобирают при каждом разборе страницы,
    /// и накопление строк показало бы цены двух чтений сразу.
    /// </summary>
    [AvaloniaFact]
    public void Повторное_наполнение_заменяет_таблицу()
    {
        var table = Table(Sample);

        PriceTableView.Fill(table, new[] { new PriceRow("ONLY", "$1", "$2") });

        var cells = PeakTableProbe.Cells(table);

        Assert.Equal(2, cells.Count);
        Assert.Equal(new[] { "ONLY", "$1", "$2" }, cells[1]);
        Assert.Equal(5, table.ColumnDefinitions.Count);
    }

    /// <summary>
    /// ЦЕНЫ И ИХ ПОДПИСИ СТОЯТ НА ОДНОЙ ВЕРТИКАЛИ. Свойство <c>TextAlignment</c> можно выставить
    /// и не увидеть ничего, а человеку нужно ровно это: числа столбиком, а не вразнобой.
    /// Меряется НАСТОЯЩАЯ вёрстка показанного окна.
    /// </summary>
    [AvaloniaFact]
    public void Цены_и_их_подписи_стоят_на_одной_вертикали()
    {
        var window = new PeakWindow();
        window.Attach(new PeakWindowTests.StubBalance(
            DshPanel.Agents.AgentCatalog.DeepSeek,
            DshPanel.Peak.PeakDecisions.State(DshPanel.Agents.AgentCatalog.DeepSeek, DateTimeOffset.Now)),
            new StubPricing(Sample));

        window.Show();
        PanelTestStand.Settle();

        var table = window.FindControl<Grid>("PriceTablePanel")!;
        var lines = PeakTableProbe.Lines(table);

        double Right(TextBlock cell)
        {
            var point = cell.TranslatePoint(new Point(0, 0), window);

            Assert.NotNull(point);

            return point!.Value.X + cell.Bounds.Width;
        }

        TextBlock Price(Grid line, int column) =>
            line.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == column);

        for (var column = 2; column <= 4; column += 2)
        {
            var reference = Right(Price((Grid)lines[0].Child!, column));

            Assert.True(reference > 0, $"подпись колонки {column} не померилась — вёрстка не отработала");

            for (var index = 1; index < lines.Count; index++)
            {
                var cell = Price((Grid)lines[index].Child!, column);

                Assert.True(cell.Bounds.Width > 0, $"ячейка колонки {column} в строке {index} не померилась");

                Assert.True(
                    Math.Abs(Right(cell) - reference) < 1,
                    $"цена строки {index} кончается на {Right(cell):0.#}, а подпись колонки — на {reference:0.#}");
            }
        }

        window.Close();
    }

    /// <summary>
    /// ТАБЛИЦА ЦЕН — ОДНА НА ПАНЕЛЬ и живёт в окне «Пики и тарифы»: у неё один построитель
    /// (<see cref="PriceTableView.Fill"/>), а из окна настроек её убрали (решение владельца
    /// 28.09.2026: для пиков и цен есть своё окно, вызываемое из главной панели).
    ///
    /// ⚠️ Прежняя редакция сравнивала таблицу настроек с таблицей окна — то есть сторожила дубль.
    /// Теперь проверка сторожит ОБРАТНОЕ: дубля в настройках нет, а таблица в окне полная,
    /// с шапкой и подписью «откуда взято». Вернётся дубль — упадёт здесь.
    /// </summary>
    [AvaloniaFact]
    public void Таблица_цен_есть_только_в_окне_и_она_полная()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var pricing = new StubPricing(Sample);

            var settings = PanelTestStand.SettingsStand(dir, pricing: pricing);
            settings.Show();
            PanelTestStand.Settle();
            settings.ShowSection(SettingsWindow.BalanceSection);
            PanelTestStand.Settle();

            var peak = new PeakWindow();
            peak.Attach(
                new PeakWindowTests.StubBalance(
                    DshPanel.Agents.AgentCatalog.DeepSeek,
                    DshPanel.Peak.PeakDecisions.State(DshPanel.Agents.AgentCatalog.DeepSeek, DateTimeOffset.Now)),
                pricing);
            peak.Show();
            PanelTestStand.Settle();

            // В настройках таблицы цен нет ни в каком виде.
            Assert.Null(settings.FindControl<Grid>("PriceTablePanel"));
            Assert.Null(settings.FindControl<TextBlock>("PriceTitleText"));
            Assert.Null(settings.FindControl<TextBlock>("PriceSourceText"));

            var inPeak = PeakTableProbe.Cells(peak.FindControl<Grid>("PriceTablePanel")!);

            Assert.Equal(Sample.Length + 1, inPeak.Count);

            // И объяснение «откуда взято» стоит в окне: пустая таблица без слов читалась бы
            // как «панель сломалась».
            Assert.NotEmpty(peak.FindControl<TextBlock>("PriceSourceText")!.Text ?? string.Empty);
            Assert.NotEmpty(peak.FindControl<TextBlock>("PriceTitleText")!.Text ?? string.Empty);

            peak.Close();
            settings.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// КНОПКА «ОБНОВИТЬ ИНФОРМАЦИЮ» ЗОВЁТ РАЗБОР, и её подсказка называет СОСТОЯНИЕ: у доступной —
    /// что она сделает, у недоступной — почему нельзя. Кнопка без подсказки и кнопка, молча
    /// ничего не делающая, читаются одинаково — «панель не поняла».
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_обновления_зовёт_разбор_и_объясняет_себя()
    {
        var window = new PeakWindow();
        var pricing = new StubPricing(Sample);

        window.Attach(
            new PeakWindowTests.StubBalance(
                DshPanel.Agents.AgentCatalog.DeepSeek,
                DshPanel.Peak.PeakDecisions.State(DshPanel.Agents.AgentCatalog.DeepSeek, DateTimeOffset.Now)),
            pricing);

        window.Show();
        PanelTestStand.Settle();

        var button = window.FindControl<Button>("RefreshPriceButton")!;

        Assert.Equal(PanelStrings.PeakRefreshButton, button.Content?.ToString());
        Assert.True(button.IsEnabled);
        Assert.Equal(PanelStrings.TipPeakRefreshButton, ToolTip.GetTip(button) as string);

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        PanelTestStand.Settle();

        Assert.Equal(1, pricing.Refreshes);

        window.Close();
    }

    /// <summary>
    /// В ПРОГОНЕ ПРОВЕРКИ КНОПКА НЕДОСТУПНА И НАЗЫВАЕТ ПРИЧИНУ: сети у панели здесь нет вовсе,
    /// и «нажми — и ничего не произойдёт» было бы худшим из ответов.
    ///
    /// ⚠️ **Щелчок здесь НЕ поднимается, и это не упущение.** У недоступного органа каркас
    /// не доставляет нажатие вовсе, и поднять событие в обход — значит проверить то, чего в жизни
    /// не бывает. Поэтому проверяются ДВА свойства, которые человек и видит: кнопка серая
    /// и подсказка у неё — причина отказа. Что щелчок ДОСТУПНОЙ кнопки зовёт разбор, сторожит
    /// соседняя проверка.
    /// </summary>
    [AvaloniaFact]
    public void В_прогоне_проверки_кнопка_недоступна_и_называет_причину()
    {
        var window = new PeakWindow();
        var pricing = new StubPricing(Sample, allowed: false);

        window.Attach(
            new PeakWindowTests.StubBalance(
                DshPanel.Agents.AgentCatalog.DeepSeek,
                DshPanel.Peak.PeakDecisions.State(DshPanel.Agents.AgentCatalog.DeepSeek, DateTimeOffset.Now)),
            pricing);

        window.Show();
        PanelTestStand.Settle();

        var button = window.FindControl<Button>("RefreshPriceButton")!;

        Assert.False(button.IsEnabled, "в прогоне проверки кнопка разбора доступна — а сети здесь нет");
        Assert.Equal(PanelStrings.TipPeakRefreshLocked, ToolTip.GetTip(button) as string);

        // Подсказка отказа — НЕ та же, что у рабочей кнопки: иначе человек не понял бы, чего ждёт
        // панель, и жал бы серую кнопку.
        Assert.NotEqual(PanelStrings.TipPeakRefreshButton, ToolTip.GetTip(button) as string);

        Assert.Equal(0, pricing.Refreshes);

        window.Close();
    }

    /// <summary>
    /// ОТВЕТ ПО ЦЕНАМ НЕ СТИРАЕТ НЕСОХРАНЁННУЮ ПРАВКУ. Это не мелочь и не «на всякий случай»:
    /// окно настроек однажды уже спотыкалось на этом — перерисовка всего окна перечитывает поля
    /// из настроек и уносит с собой то, что человек только что набрал. Поэтому подписка на
    /// контроллер цен перерисовывает ТОЛЬКО таблицы, и проверка сторожит именно это.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_по_ценам_не_стирает_несохранённую_правку()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var pricing = new StubPricing(Sample);
            var window = PanelTestStand.SettingsStand(dir, pricing: pricing);

            window.Show();
            PanelTestStand.Settle();

            var box = window.FindControl<TextBox>("WorkDirBox")!;
            var typed = "C:\\Temp\\правка-владельца";

            box.Text = typed;
            PanelTestStand.Settle();

            Assert.True(window.HasUnsavedChanges(), "правка не видна окну сразу после ввода");

            window.ShowSection(SettingsWindow.BalanceSection);
            PanelTestStand.Settle();

            pricing.Signal();
            PanelTestStand.Settle();

            Assert.Equal(typed, box.Text);
            Assert.True(window.HasUnsavedChanges(), "ответ по ценам стёр несохранённую правку");
            Assert.False(window.Saved, "показ цен записал настройки — этого делать нельзя");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Подставной контроллер цен: ответ задан проверкой, сети нет вовсе.
    ///
    /// ⚠️ Он здесь, а не в общей оснастке: оснастку правят другие рабочие, а этой проверке нужен
    /// свой ответ на «какие цены». Живой запрос в проверках запрещён — это сеть от имени владельца.
    /// </summary>
    internal sealed class StubPricing : IPricingControl
    {
        public StubPricing(IReadOnlyList<PriceRow> rows, bool allowed = true)
        {
            Result = new PricingResult(true, string.Empty, "https://example.invalid/pricing", "ru", "demo", rows,
                Array.Empty<DshPanel.Agents.PeakWindow>(), DateTimeOffset.Now);
            Allowed = allowed;
        }

        public event Action? Changed;

        public PricingResult Result { get; }

        public bool Busy => false;

        public bool Allowed { get; }

        public int Refreshes { get; private set; }

        public string PriceHeadingText => PanelStrings.PriceTableTitle;

        public string PriceSourceText => Result.SourceUrl;

        /// <summary>
        /// Истории цен у этой подстановки нет: она про таблицу «Стоимость», а история живёт
        /// своим окном и своими проверками (<c>PricingHistoryTests</c>). Пустая история честнее
        /// выдуманной: окно истории по ней скажет «изменений не было».
        /// </summary>
        public PricingHistory History => PricingHistory.Empty;

        public void Refresh() => Refreshes++;

        /// <summary>Сказать окну, что ответ изменился, — так проверяется подписка окна на контроллер.</summary>
        public void Signal() => Changed?.Invoke();
    }
}
