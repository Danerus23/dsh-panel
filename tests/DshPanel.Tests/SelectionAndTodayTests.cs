using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Agents;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПАЧКА u5 (2.4.0): выбранная строка списка видна (DEF-047), сегодняшний день таблицы пиков
/// назван словом (DEF-049), нижний ряд главного окна влезает в 640×640 с полем (DEF-050).
///
/// ⚠️ **Почему отдельным классом.** Все три проверки ВИДОВЫЕ: они показывают окно, читают
/// нарисованные пиксели и меряют геометрию. Указатель и тема общие на процесс, поэтому класс
/// идёт в коллекции <see cref="PanelLookCollection"/> — как остальные проверки вида
/// (`tests\AGENTS.md`).
///
/// **Что здесь доказано ЧИСЛОМ, а не «на глаз»:**
/// * контраст подложки выбранной строки к фону списка — по КАДРУ (DEF-047). Замер до правки:
///   1,00:1 (`p8-frames.md` D-1), пол — 3:1 (WCAG 1.4.11, `50-lists-navigation.md` §2);
/// * контраст ЧЕРНИЛ выбранной строки к её подложке — пол 4,5:1 (WCAG 1.4.3): подложка, прошедшая
///   3:1 к фону, темна, и прежние чернила на ней не читались бы;
/// * у сегодняшней строки таблицы пиков ДВА признака: слово «сегодня» из словаря и подложка;
/// * на 640×640 нижний ряд карточки баланса виден целиком, поле до края окна ≥ 8, содержимое
///   не обрезано (нет прокрутки на дефолтном размере).
/// </summary>
[Collection(PanelLookCollection.Name)]
public class SelectionAndTodayTests
{
    private const double SelectionContrastFloor = 3.0;
    private const double InkContrastFloor = 4.5;
    private const double BottomFieldFloor = 8.0;

    /// <summary>Цвет в вид <c>0xRRGGBB</c> — тот, который понимает <see cref="TrayPalette.Contrast"/>.</summary>
    private static uint Rgb(Color colour) => ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;

    /// <summary>Контраст двух цветов по WCAG — тем же счётом, что у палитры значка (одна формула на панель).</summary>
    private static double Contrast(Color first, Color second) => TrayPalette.Contrast(Rgb(first), Rgb(second));

    private static Rect Frame(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(point);

        return new Rect(point!.Value, control.Bounds.Size);
    }

    /// <summary>
    /// Подложка выбранной строки списка разделов настроек — НЕ фон списка: контраст по кадру
    /// не ниже 3:1, и это число одинаково в ОБЕИХ темах.
    ///
    /// Замер ДО правки (кадр p8, 22.10.2026): подложка `#F2F2F2` = фон списка `#F2F2F2` →
    /// **1,00:1**; выбор держался одной полужирной подписью (`DEF-047`). Проверка идёт по
    /// НАРИСОВАННЫМ пикселям, а не по объявленному свойству: свойство можно выставить и не увидеть.
    ///
    /// Точки замера: (1) внутри выбранной строки у её верхнего левого угла — там подложка,
    /// а не текст (внутренние отступы строки 12,8); (2) в списке ПОД последней строкой — там
    /// фон списка. Тот же приём, каким мерили до правки.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Подложка_выбранной_строки_настроек_проходит_три_к_одному(bool dark)
    {
        var dir = PanelTestStand.TempDir();
        var saved = Application.Current!.RequestedThemeVariant;

        try
        {
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            PanelTestStand.Settle();

            var list = window.FindControl<ListBox>("SectionsList");
            Assert.NotNull(list);

            var items = list!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.True(items.Count >= 4, $"в списке разделов {items.Count} строк — мерить нечего");

            var selected = items.Single(item => item.IsSelected);
            var where = $"тема {(dark ? "тёмная" : "светлая")}";

            // (1) Подложка: угол строки, выше подписи и правее скругления.
            var row = Frame(window, selected);
            var fill = PanelTestStand.ColorAt(window, new Point(row.X + 8, row.Y + 4));

            // (2) Фон списка: под последней строкой, внутри самого списка.
            var last = items.OrderBy(item => Frame(window, item).Y).Last();
            var listFrame = Frame(window, list);
            var background = PanelTestStand.ColorAt(
                window, new Point(listFrame.X + listFrame.Width / 2, Math.Min(Frame(window, last).Bottom + 8, listFrame.Bottom - 4)));

            var contrast = Contrast(fill, background);

            Assert.True(
                contrast >= SelectionContrastFloor,
                $"{where}: подложка выбранной строки {fill} к фону списка {background} = " +
                $"{contrast:0.00}:1 — ниже пола {SelectionContrastFloor:0.0}:1 (DEF-047)");

            // И подложка — ИМЕННО та, что названа в палитре: подставленный «похожий оттенок»
            // разошёлся бы с замером в комментарии и с числами контраста.
            var expected = PanelLook.SelectionFill(dark);
            Assert.True(
                Math.Abs(fill.R - expected.R) <= 2 &&
                Math.Abs(fill.G - expected.G) <= 2 &&
                Math.Abs(fill.B - expected.B) <= 2,
                $"{where}: подложка на кадре {fill}, а палитра называет {expected} — вид и замер разошлись");

            // ЧЕРНИЛА: второй признак выбора обязан читаться на подложке (пол 4,5:1).
            var label = selected.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.NotNull(label);

            var inkBrush = label!.Foreground as ISolidColorBrush;
            Assert.NotNull(inkBrush);

            var inkContrast = Contrast(inkBrush!.Color, fill);
            Assert.True(
                inkContrast >= InkContrastFloor,
                $"{where}: подпись {inkBrush.Color} на подложке {fill} = {inkContrast:0.00}:1 — ниже пола {InkContrastFloor:0.0}:1");

            // НЕ ТОЛЬКО ЦВЕТ: у выбранной строки полужирная подпись, у соседней — нет.
            Assert.Equal(FontWeight.SemiBold, selected.FontWeight);
            Assert.NotEqual(FontWeight.SemiBold, items.First(item => !item.IsSelected).FontWeight);

            window.CloseQuietly();
        }
        finally
        {
            Application.Current.RequestedThemeVariant = saved;
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ТО ЖЕ В ИСТОРИИ ЦЕН: список записей выбирается так же, и «выбранная запись» обязана
    /// отличаться от фона списка не меньше чем на 3:1 (DEF-047 говорит про оба списка:
    /// «то же в истории цен»).
    ///
    /// ⚠️ Это не повтор проверки настроек «на всякий случай»: список истории — другой `ListBox`
    /// с ДРУГИМ шаблоном строки (`DataTemplate` с `TextBlock`), и сток `App.axaml` обязан
    /// покрывать оба. Проверка идёт по кадру показанного окна, как и первая.
    /// </summary>
    [AvaloniaFact]
    public void Подложка_выбранной_записи_истории_цен_проходит_три_к_одному()
    {
        var window = new PricingHistoryWindow();
        window.Attach(new HistoryStub(HistoryWithOneChange()));

        try
        {
            window.Show();
            PanelTestStand.Settle();

            var list = window.FindControl<ListBox>("EntriesList");
            Assert.NotNull(list);

            var items = list!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.True(items.Count >= 2, $"в истории цен {items.Count} записей — сравнить выбор не с чем");

            var selected = items.Single(item => item.IsSelected);
            var row = Frame(window, selected);
            var fill = PanelTestStand.ColorAt(window, new Point(row.X + 6, row.Y + 4));

            var last = items.OrderBy(item => Frame(window, item).Y).Last();
            var listFrame = Frame(window, list);
            var background = PanelTestStand.ColorAt(
                window,
                new Point(listFrame.X + listFrame.Width / 2, Math.Min(Frame(window, last).Bottom + 8, listFrame.Bottom - 4)));

            var contrast = Contrast(fill, background);

            Assert.True(
                contrast >= SelectionContrastFloor,
                $"история цен: подложка выбранной записи {fill} к фону списка {background} = " +
                $"{contrast:0.00}:1 — ниже пола {SelectionContrastFloor:0.0}:1 (DEF-047)");

            var label = selected.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.NotNull(label);

            var ink = (label!.Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
            Assert.True(
                Contrast(ink, fill) >= InkContrastFloor,
                $"история цен: подпись {ink} на подложке {fill} = {Contrast(ink, fill):0.00}:1 — ниже пола {InkContrastFloor:0.0}:1");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// СЕГОДНЯШНИЙ ДЕНЬ ТАБЛИЦЫ ПИКОВ НАЗВАН СЛОВОМ: в ячейке дня стоит «чт · сегодня»
    /// (из словаря, три языка), а не одно «чт» под зелёной заливкой.
    ///
    /// Замер ДО правки (кадр p8, D-6): зелёная заливка строки «чт» ничем, кроме цвета,
    /// не объяснена — подсвеченное «чт» читается как «четверг» и только; провал WCAG 1.4.1.
    /// День недели НЕ заменён: «чт» остаётся первым (день — содержание таблицы).
    ///
    /// Проверка идёт через НАСТОЯЩИЙ построитель (<see cref="PeakTableView"/>) и сравнивает текст
    /// с ТЕМ ЖЕ форматом словаря: подпись, написанная в коде литералом, не совпала бы с ним
    /// на английском и китайском кадре.
    /// </summary>
    [AvaloniaFact]
    public void Сегодняшний_день_таблицы_пиков_назван_словом_а_день_остался()
    {
        var rows = PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 0);
        const int today = 3;

        var table = new Grid();
        PeakTableView.Fill(table, rows, todayIndex: today);

        var cells = PeakTableProbe.Cells(table);
        Assert.Equal(rows.Count + 1, cells.Count);

        var expected = string.Format(
            System.Globalization.CultureInfo.CurrentCulture, PanelStrings.PeakDayTodayFormat, rows[today].Day);

        // Слово «сегодня» — добавка, а не замена: день недели остался первым в строке.
        Assert.StartsWith(rows[today].Day, expected, StringComparison.Ordinal);
        Assert.NotEqual(rows[today].Day, expected);

        // Ячейка дня сегодняшней строки — ровно этот текст.
        Assert.Equal(expected, cells[today + 1][0]);

        // У остальных шести дней слова «сегодня» нет: иначе признак «сегодня» перестал бы
        // что-либо значить.
        for (var index = 0; index < rows.Count; index++)
        {
            if (index == today) continue;

            Assert.Equal(rows[index].Day, cells[index + 1][0]);
        }

        // И ВТОРОЙ признак остался: у сегодняшней строки — своя подложка (класс), у остальных нет.
        // Значит смысл несут ДВА канала: слово и цвет.
        var lines = PeakTableProbe.Lines(table);
        Assert.Contains(PanelLook.PeakTodayRowClass, lines[today + 1].Classes);
        Assert.All(
            lines.Where((line, index) => index != today + 1),
            line => Assert.DoesNotContain(PanelLook.PeakTodayRowClass, line.Classes));
    }

    /// <summary>
    /// НИЖНИЙ РЯД ГЛАВНОГО ОКНА НА 640×640: кнопки видны целиком, поле до края окна не меньше 8,
    /// и содержимое НЕ обрезано — на дефолтном размере прокручивать нечего.
    ///
    /// Замер ДО правки (кадр p8 `main.png`, D-16): содержимое 695 точек при окне 640 — нижний ряд
    /// карточки баланса («Обновить баланс», «Пики и тарифы…») стоял на самой границе и был обрезан
    /// снизу. Окно снято ТОЙ ЖЕ образцовой дверью, что кадр `--shot`
    /// (<see cref="ShotDrawers"/>): пустое окно влезло бы и до правки, и проверка прошла бы
    /// через состояние, в котором дефект невозможен.
    ///
    /// Поле меряется по КАДРУ-геометрии: `TranslatePoint` даёт точки окна, а не «сколько кто
    /// объявил в разметке» — объявленный отступ мог бы и не дойти до нижнего ряда.
    /// </summary>
    [AvaloniaFact]
    public void Нижний_ряд_главного_окна_на_640x640_виден_с_полем_не_меньше_восьми()
    {
        var drawers = ShotDrawers.All();

        var window = new MainWindow { Width = 640, Height = 640 };
        window.Attach(drawers.Server);
        window.AttachBalance(drawers.Balance);
        window.AttachAgentChoice(drawers.Settings);

        try
        {
            window.Show();
            PanelTestStand.Settle();

            var scroll = window.FindControl<ScrollViewer>("MainContentScroll");
            Assert.NotNull(scroll);

            var bottom = window.ClientSize.Height;

            foreach (var name in new[] { "RefreshBalanceButton", "PeakButton" })
            {
                var button = window.FindControl<Button>(name);
                Assert.NotNull(button);
                Assert.True(button!.IsVisible, $"«{name}» не показана");

                var frame = Frame(window, button);
                var where = $"окно 640x640, «{name}»: {frame}";

                Assert.True(frame.Width > 0 && frame.Height > 0, $"«{name}» без размера — {where}");
                Assert.True(frame.Y >= 0, $"«{name}» выше окна — {where}");
                Assert.True(
                    frame.Bottom <= bottom - BottomFieldFloor + 0.5,
                    $"«{name}» ближе {BottomFieldFloor:0} точек к нижнему краю окна — {where}");
                Assert.True(frame.Right <= window.ClientSize.Width + 0.5, $"«{name}» шире окна — {where}");
            }

            // СОДЕРЖИМОЕ НЕ ОБРЕЗАНО: на дефолтном размере дефект выглядел именно как «низ
            // карточки уехал за край». Поэтому проверяется не только ряд, но и весь кадр:
            // протяжённость содержимого не длиннее видимой области.
            Assert.True(
                scroll!.Extent.Height <= scroll.Viewport.Height + 0.5,
                $"содержимое главного окна длиннее видимой области: " +
                $"Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#} — нижний ряд обрезан");
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- подстановки

    /// <summary>История с одной записью изменения: две строки в списке (первая цена и изменение).</summary>
    private static PricingHistory HistoryWithOneChange()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(3));
        var windows = AgentCatalog.DeepSeek.PeakWindows;

        var anchor = new PricingSnapshot(
            at,
            PricingDecisions.Url(DshPanel.Localization.LanguageDecisions.Auto),
            DshPanel.Localization.LanguageDecisions.Auto,
            "deepseek-chat",
            new[] { new PriceRow("chat · input", "$0.25", "$0.50") },
            windows);

        var change = new PricingChange(
            at.AddHours(1),
            PricingDecisions.Url(DshPanel.Localization.LanguageDecisions.Auto),
            DshPanel.Localization.LanguageDecisions.Auto,
            "deepseek-chat",
            new[] { new PriceChangeRow("chat · input", "$0.27", "$0.25", "$0.55", "$0.50") },
            windows,
            windows);

        return new PricingHistory(anchor, new[] { change }, 0);
    }

    /// <summary>Подстановка контроллера цен: окно истории показывает ровно отданную историю.</summary>
    private sealed class HistoryStub : IPricingControl
    {
        public HistoryStub(PricingHistory history) => History = history;

        public PricingHistory History { get; }

        public PricingResult Result => PricingResult.NotRequested("ru");

        public bool Busy => false;

        public bool Allowed => false;

        public string PriceHeadingText => PanelStrings.PriceTableTitle;

        public string PriceSourceText => string.Empty;

#pragma warning disable CS0067 // событие нужно интерфейсу; подстановка его не поднимает
        public event Action? Changed;
#pragma warning restore CS0067

        public void Refresh()
        {
        }
    }
}
