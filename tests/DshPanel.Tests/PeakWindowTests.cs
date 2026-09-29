using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using DshPanel.Agents;
using DshPanel.Balance;
using DshPanel.Peak;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

// ⚠️ У имени `PeakWindow` в панели два смысла: запись РАСПИСАНИЯ (DshPanel.Agents.PeakWindow —
// дни недели и границы окна, лежит в профиле агента) и само окно (DshPanel.Views.PeakWindow).
// Этому файлу нужны оба пространства имён, поэтому окно зовётся псевдонимом: без него имя
// неоднозначно, а переименовывать ни то, ни другое нельзя (запись — домен, окно — требование).
using PeakView = DshPanel.Views.PeakWindow;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО «ПИКИ И ТАРИФЫ» — отдельное окно с состоянием, переключением, таблицей и источником.
///
/// Решение дирижёра, названное владельцу: расписание окон, цена и состояние не помещаются
/// в карточку главного окна, не превратив её в простыню. Здесь проверяется то, что окно обещает:
///
/// 1. **четыре разные вещи отдельными строками** — состояние, переключение, пояснение цен
///    и откуда окна; ни одна не пуста и они не совпадают текстом;
/// 2. **та же таблица, что в разделе настроек** — один построитель (<see cref="PeakTableView"/>)
///    наполняет оба окна, и структура таблиц совпадает строка в строку;
/// 3. **окно живёт без связки** — его строят проверки, и построение без <c>Attach</c> не падает:
///    строки про живое состояние пусты, а не выдуманы;
/// 4. **связка действительно работает** — состояние берётся у контроллера, и окно
///    перерисовывается по его сигналу, а не по своим часам.
///
/// ⚠️ Подставной контроллер баланса живёт в этом файле намеренно: общая оснастка
/// (<c>PanelTestStand</c>) чужая, её правят другие рабочие, а проверке нужен свой ответ
/// на «какой сейчас тариф».
///
/// ⚠️ Коллекция вида взята та же, что у остальных проверок с кадром (<c>PanelLookCollection</c>):
/// у неё запрещён параллельный прогон, потому что указатель и тема — состояние ОДНО на процесс,
/// и соседняя проверка сдвинула бы кадр этой (на этом панель уже спотыкалась).
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PeakWindowTests
{
    private static void Settle() => PanelTestStand.Settle();

    /// <summary>Момент внутри окна пика: 02:00 UTC понедельника (окно 01:00–04:00).</summary>
    private static readonly DateTimeOffset InPeakMoment = new(2026, 9, 21, 2, 0, 0, TimeSpan.Zero);

    /// <summary>Момент между окнами: 12:00 UTC понедельника — цена вне пика.</summary>
    private static readonly DateTimeOffset OffPeakMoment = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Контроллер баланса с заданным состоянием: настоящее окно не нужно — нужен ответ.
    ///
    /// Открыт внутрь сборки проверок: им пользуется и <c>PriceTableViewTests</c> (окну «Пики
    /// и тарифы» нужен баланс, чтобы вообще показать таблицы). Второй такой же подстановки
    /// в проверках быть не должно — разойдясь, они мерили бы разные окна.
    /// </summary>
    internal sealed class StubBalance : IBalanceControl
    {
        public StubBalance(AgentProfile agent, PeakState peak)
        {
            Agent = agent;
            Peak = peak;
        }

        public event Action? Changed;

        public AgentProfile Agent { get; }

        public BalanceResult Result => BalanceResult.NotRequested(Agent.Id);

        public PeakState Peak { get; private set; }

        public bool Busy => false;

        public bool Allowed => false;

        public string StatusText => string.Empty;

        public string DetailText => string.Empty;

        public string PeakText => PeakTable.NowText(Peak);

        public string NextText => PeakTable.NextText(Peak);

        public string ScheduleText =>
            PeakDecisions.SourceText(Agent, PeakDecisions.OffsetMinutes(DateTimeOffset.Now));

        /// <summary>Короткая строка главного окна — здесь не показывается, но её требует интерфейс.</summary>
        public string PeakCheckedText => PeakDecisions.CheckedText(Agent);

        public TrayTone Tone => TrayTone.Neutral;

        public void Refresh()
        {
        }

        public void AgentChanged()
        {
        }

        /// <summary>Один такт часов контроллера: пересчитать пик и сказать об этом подписчику.</summary>
        public void Tick(DateTimeOffset now)
        {
            Peak = PeakDecisions.State(Agent, now);
            Changed?.Invoke();
        }
    }

    private static StubBalance Stub(DateTimeOffset now) =>
        new(AgentCatalog.DeepSeek, PeakDecisions.State(AgentCatalog.DeepSeek, now));

    /// <summary>Таблица, как её читает человек: одна строка — один день.</summary>
    private static IReadOnlyList<string> Flat(Grid table) =>
        PeakTableProbe.Cells(table).Select(line => string.Join(" | ", line)).ToList();

    // ------------------------------------------------------------------ окно без связки

    /// <summary>
    /// ОКНО, ПОСТРОЕННОЕ БЕЗ СВЯЗКИ, НЕ ПАДАЕТ и честно говорит, что живого состояния нет:
    /// строки про «сейчас», переключение и источник ПУСТЫ, а не заполнены чужим агентом.
    /// Именно так окно строят проверки, и упасть на этом оно не имеет права.
    /// </summary>
    [AvaloniaFact]
    public void Окно_без_связки_строится_и_не_выдумывает_состояние()
    {
        var window = new PeakView();

        // До Show: построение уже обязано пройти целиком (подписи ставятся в конструкторе).
        Assert.Equal(PanelStrings.PeakWindowTitle, window.Title);
        Assert.Equal(string.Empty, window.StateText);
        Assert.Equal(string.Empty, window.SourceText);
        Assert.Empty(window.Rows);

        window.Show();
        Settle();

        var heading = window.FindControl<TextBlock>("HeadingText")!;
        var subtitle = window.FindControl<TextBlock>("SubtitleText")!;
        var tableTitle = window.FindControl<TextBlock>("TableTitleText")!;
        var tableHeading = window.FindControl<TextBlock>("TableHeadingText")!;
        var legend = window.FindControl<TextBlock>("LegendText")!;

        Assert.Equal(PanelStrings.PeakWindowHeading, heading.Text);
        Assert.Equal(PanelStrings.PeakWindowSubtitle, subtitle.Text);
        Assert.Contains("hint", subtitle.Classes);

        // Название таблицы графика и заголовок с часовым поясом есть ВСЕГДА: они не зависят
        // ни от агента, ни от связки — это свойства самих часов.
        Assert.Equal(PanelStrings.PeakTableTitle, tableTitle.Text);
        Assert.Contains(PeakDecisions.ZoneLabel(PeakDecisions.OffsetMinutes(DateTimeOffset.Now)), tableHeading.Text!);
        Assert.Equal(PeakTable.Legend(), legend.Text);
        Assert.Contains(PanelStrings.PeakInPeak, legend.Text!, StringComparison.Ordinal);

        // Таблица графика — только шапка: строк с днями нет, потому что нет и агента.
        var table = window.FindControl<Grid>("PeakTablePanel")!;
        Assert.Single(PeakTableProbe.Lines(table));

        Assert.Equal(
            new[]
            {
                PanelStrings.PeakTableColumnDay,
                PanelStrings.PeakTableColumnPeak,
                PanelStrings.PeakTableColumnOffPeak,
            },
            PeakTableProbe.Cells(table)[0]);

        // И ВТОРАЯ таблица тоже на месте — одной шапкой: цен панель ещё не читала, а выдумывать
        // их она не вправе.
        var priceTable = window.FindControl<Grid>("PriceTablePanel")!;
        Assert.Single(PeakTableProbe.Lines(priceTable));
        Assert.Equal(PanelStrings.PriceTableTitle, window.FindControl<TextBlock>("PriceTitleText")!.Text);

        // Связки с контроллером цен нет — строка-объяснение молчит, а не советует кнопку,
        // которой в этом окне может и не быть.
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("PriceSourceText")!.Text);
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("NowLineText")!.Text);

        // Прокрутка — как в настройках: полоса видна всегда, горизонтальная запрещена.
        var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;

        Assert.Equal(ScrollBarVisibility.Visible, scroll.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);

        window.Close();
    }

    /// <summary>Подпись кнопки закрытия — ТА ЖЕ, что у окна «О программе»: второго набора слов нет.</summary>
    [AvaloniaFact]
    public void Кнопка_закрытия_названа_теми_же_словами_что_в_о_программе()
    {
        var about = new AboutWindow();
        var window = new PeakView();

        window.Show();
        Settle();

        var close = window.FindControl<Button>("CloseButton")!;

        Assert.Equal(PanelStrings.AboutCloseButton, close.Content?.ToString());
        Assert.Equal(about.CloseLine, close.Content?.ToString());
        Assert.Equal(PanelStrings.TipCloseAboutButton, ToolTip.GetTip(close));

        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();

        Assert.False(window.IsVisible, "кнопка закрытия не закрыла окно");
    }

    // ------------------------------------------------------------------ четыре ответа

    /// <summary>
    /// ОКНО ПОКАЗЫВАЕТ ЧЕТЫРЕ РАЗНЫЕ ВЕЩИ ЧЕТЫРЬМЯ СТРОКАМИ: состояние (<c>NowText</c>),
    /// переключение (<c>NextText</c>), пояснение цен (<c>LegendText</c>) и откуда окна
    /// (<c>PeakSourceText</c>). Ни одна не пуста, и они не подменяют друг друга.
    /// </summary>
    [AvaloniaFact]
    public void Окно_называет_состояние_переключение_цену_и_источник()
    {
        var agent = AgentCatalog.DeepSeek;
        var now = DateTimeOffset.Now;
        var state = PeakDecisions.State(agent, now);

        var window = new PeakView();
        window.Attach(new StubBalance(agent, state));
        window.Show();
        Settle();

        var nowText = window.FindControl<TextBlock>("NowText")!;
        var nextText = window.FindControl<TextBlock>("NextText")!;
        var legendText = window.FindControl<TextBlock>("LegendText")!;
        var sourceText = window.FindControl<TextBlock>("PeakSourceText")!;

        var lines = new[] { nowText.Text, nextText.Text, legendText.Text, sourceText.Text };

        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line), "одна из четырёх строк пуста"));
        Assert.Equal(4, lines.Distinct().Count());

        // Каждая строка — свой орган, а не одна склейка из четырёх ответов.
        Assert.Equal(4, new object[] { nowText, nextText, legendText, sourceText }.Distinct().Count());

        Assert.Equal(PeakTable.NowText(state), nowText.Text);
        Assert.Equal(PeakTable.NextText(state), nextText.Text);
        Assert.Equal(PeakTable.Legend(), legendText.Text);

        var offset = PeakDecisions.OffsetMinutes(now);
        Assert.Equal(PeakDecisions.SourceText(agent, offset), sourceText.Text);

        // То же видно и снаружи окна — тем, кто проверяет окно без глаз.
        Assert.Equal(nowText.Text, window.StateText);
        Assert.Equal(sourceText.Text, window.SourceText);

        // Состояние покрашено тем же тоном, что строка тарифа в меню значка.
        Assert.Contains(PanelLook.ToneClass(TrayStatus.PeakTone(state.InPeak)), nowText.Classes);

        window.Close();
    }

    // ------------------------------------------------------------------ одна таблица на панель

    /// <summary>
    /// ТАБЛИЦА ГРАФИКА — ОДНА НА ПАНЕЛЬ, и живёт она в окне «Пики и тарифы»: у неё один построитель
    /// (<see cref="PeakTableView.Fill"/>), поэтому разойтись с чем-либо она не может по устройству.
    ///
    /// ⚠️ Проверка переписана 28.09.2026 вместе с решением владельца: таблицы пиков и цен из окна
    /// настроек УБРАНЫ (для них есть своё окно). Прежняя редакция мерила «окно против раздела
    /// настроек» — то есть сторожила ровно тот дубль, которого больше быть не должно. Теперь она
    /// сторожит ОТСУТСТВИЕ дубля в настройках и полноту таблицы в окне: вернётся дубль — упадёт здесь.
    /// </summary>
    [AvaloniaFact]
    public void Таблица_графика_есть_только_в_окне_и_она_полная()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var agent = AgentCatalog.DeepSeek;

            var settings = PanelTestStand.SettingsStand(dir);
            settings.Show();
            Settle();

            settings.ShowSection(SettingsWindow.BalanceSection);
            Settle();

            var peak = new PeakView();
            peak.Attach(Stub(DateTimeOffset.Now));
            peak.Show();
            Settle();

            // Ни построителя расписания, ни построителя цен в настройках не осталось.
            Assert.Null(settings.FindControl<Grid>("PeakTablePanel"));
            Assert.Null(settings.FindControl<Grid>("PriceTablePanel"));

            var inPeak = peak.FindControl<Grid>("PeakTablePanel")!;

            // Таблица в окне полная: строка заголовков и семь дней.
            Assert.Equal(8, PeakTableProbe.Lines(inPeak).Count);

            // И она — про того же агента: семь строк и обе цены на месте.
            Assert.Equal(7, peak.Rows.Count);
            Assert.Equal(PeakTable.Rows(agent, PeakDecisions.OffsetMinutes(DateTimeOffset.Now)), peak.Rows);

            peak.Close();
            settings.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ выравнивание

    /// <summary>
    /// КОЛОНКИ ВЫРОВНЕНЫ ПО ВСЕМ ВОСЬМИ СТРОКАМ: у дня, у окон и у цены каждый столбец стоит
    /// на своей вертикали — и в шапке, и в строках.
    ///
    /// Проверка родилась из дефекта этой же работы: у каждой строки своя сетка (строка — это рамка
    /// с разделителем снизу), и колонки <c>Auto</c> в разных сетках совпадают только тогда, когда
    /// совпадает их содержимое. А содержимое разное — в шапке «День», в строках «пн», — поэтому
    /// шапка «Окна пика» уезжала вправо от самих окон на ширину слова «День». Заметить это
    /// на одной строке нельзя: сравнивать надо шапку со ВСЕМИ строками.
    /// </summary>
    [AvaloniaFact]
    public void Колонки_таблицы_выровнены_по_всем_строкам()
    {
        var window = new PeakView();
        window.Attach(Stub(DateTimeOffset.Now));
        window.Show();
        Settle();

        var lines = PeakTableProbe.Lines(window.FindControl<Grid>("PeakTablePanel")!);
        var header = (Grid)lines[0].Child!;

        // Левая граница ячейки в координатах окна: мерится НАСТОЯЩАЯ вёрстка, а не свойство.
        double Left(Grid line, int column)
        {
            var cell = line.Children.OfType<TextBlock>().Single(candidate => Grid.GetColumn(candidate) == column);
            var point = cell.TranslatePoint(new Point(0, 0), window);

            Assert.NotNull(point);

            return point!.Value.X;
        }

        Assert.True(Left(header, 2) > Left(header, 0), "колонка окон начинается не после дня");

        var day = Left(header, 0);
        var windows = Left(header, 2);

        for (var index = 1; index < lines.Count; index++)
        {
            var line = (Grid)lines[index].Child!;

            Assert.True(
                Math.Abs(Left(line, 0) - day) < 1,
                $"день строки {index} начинается на {Left(line, 0):0.#}, а шапка — на {day:0.#}");

            Assert.True(
                Math.Abs(Left(line, 2) - windows) < 1,
                $"окна строки {index} начинаются на {Left(line, 2):0.#}, а шапка колонки — на {windows:0.#}");
        }

        window.Close();
    }

    /// <summary>
    /// КОЛОНКА ДЕШЁВОГО ВРЕМЕНИ СТОИТ НА СВОЕЙ ВЕРТИКАЛИ ПО ВСЕМ ВОСЬМИ СТРОКАМ — так же, как
    /// день и пик. Прежде эта проверка мерила ПРАВЫЙ край колонки цены: цены из этой таблицы уехали
    /// в свою («Стоимость»), и здесь остались две колонки окон, обе слева. Мерится НАСТОЯЩАЯ
    /// вёрстка: свойство <c>TextAlignment</c> можно выставить и не увидеть ничего.
    /// </summary>
    [AvaloniaFact]
    public void Колонка_дешёвого_времени_стоит_на_своей_вертикали()
    {
        var window = new PeakView();
        window.Attach(Stub(DateTimeOffset.Now));
        window.Show();
        Settle();

        var lines = PeakTableProbe.Lines(window.FindControl<Grid>("PeakTablePanel")!);

        // Левая граница ячейки в координатах окна: мерится НАСТОЯЩАЯ вёрстка, а не свойство.
        double Left(Grid line, int column)
        {
            var cell = line.Children.OfType<TextBlock>().Single(candidate => Grid.GetColumn(candidate) == column);
            var point = cell.TranslatePoint(new Point(0, 0), window);

            Assert.NotNull(point);

            return point!.Value.X;
        }

        var header = (Grid)lines[0].Child!;
        var offPeak = Left(header, 4);

        Assert.True(offPeak > Left(header, 2), "колонка дешёвого времени начинается не после колонки пика");

        for (var index = 1; index < lines.Count; index++)
        {
            var line = (Grid)lines[index].Child!;

            Assert.True(
                Math.Abs(Left(line, 4) - offPeak) < 1,
                $"дешёвое время строки {index} начинается на {Left(line, 4):0.#}, а шапка колонки — на {offPeak:0.#}");
        }

        window.Close();
    }

    // ------------------------------------------------------------------ связка работает

    /// <summary>
    /// ОКНО ДЕЙСТВИТЕЛЬНО РИСУЕТ ТО, О ЧЁМ ГОВОРИТ: кадр пустого окна не равен кадру со связкой,
    /// а кадр в пик не равен кадру вне пика.
    ///
    /// Это проверка ЖИВОГО ПУТИ (правило шва): свойства можно выставить и не увидеть ничего —
    /// так в панели уже случалось. Здесь мерятся НАРИСОВАННЫЕ точки настоящего окна, и пустой
    /// (или не меняющийся) кадр обязан провалить проверку.
    /// </summary>
    [AvaloniaFact]
    public void Кадр_окна_меняется_вместе_с_состоянием()
    {
        var stub = Stub(DateTimeOffset.Now);

        var window = new PeakView();
        window.Show();
        Settle();

        var empty = PanelTestStand.FrameHash(window);

        window.Attach(stub);
        Settle();

        var filled = PanelTestStand.FrameHash(window);

        Assert.NotEqual(empty, filled);

        stub.Tick(InPeakMoment);
        Settle();

        var inPeak = PanelTestStand.FrameHash(window);

        stub.Tick(OffPeakMoment);
        Settle();

        var offPeak = PanelTestStand.FrameHash(window);

        Assert.NotEqual(inPeak, offPeak);

        window.Close();
    }

    /// <summary>
    /// СОСТОЯНИЕ БЕРЁТСЯ У КОНТРОЛЛЕРА, И ОКНО СЛЕДИТ ЗА НИМ: два разных такта его часов дают
    /// два разных ответа на экране. Без подписки на <c>Changed</c> окно показало бы состояние,
    /// застывшее на моменте построения, — то есть неправду про текущий тариф.
    /// </summary>
    [AvaloniaFact]
    public void Окно_следит_за_состоянием_контроллера_и_перерисовывается()
    {
        var stub = Stub(DateTimeOffset.Now);

        var window = new PeakView();
        window.Attach(stub);
        window.Show();
        Settle();

        var nowText = window.FindControl<TextBlock>("NowText")!;

        // Пик: 02:00 UTC понедельника — внутри окна 01:00–04:00.
        stub.Tick(InPeakMoment);
        Settle();

        Assert.Equal(PanelStrings.PeakInPeak, window.StateText);
        Assert.Contains(PanelLook.ToneClass(TrayStatus.PeakTone(inPeak: true)), nowText.Classes);

        // Вне пика: 12:00 UTC того же дня — между окнами.
        stub.Tick(OffPeakMoment);
        Settle();

        Assert.Equal(PanelStrings.PeakOffPeak, window.StateText);
        Assert.Contains(PanelLook.ToneClass(TrayStatus.PeakTone(inPeak: false)), nowText.Classes);

        // Переключение тоже пересчитано — иначе окно показывало бы «когда» от прежнего такта.
        var nextText = window.FindControl<TextBlock>("NextText")!;
        Assert.Equal(PeakTable.NextText(stub.Peak), nextText.Text);
        Assert.False(string.IsNullOrWhiteSpace(nextText.Text));

        window.Close();
    }
}
