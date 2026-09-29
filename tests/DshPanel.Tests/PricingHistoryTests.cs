using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

// ⚠️ У имени `PeakWindow` в панели два смысла — запись РАСПИСАНИЯ (`DshPanel.Agents.PeakWindow`:
// дни недели и границы окна) и само окно (`DshPanel.Views.PeakWindow`). Этому файлу нужны оба,
// поэтому имена разведены псевдонимами: без них имя неоднозначно.
using PeakWindow = DshPanel.Agents.PeakWindow;
using PeakView = DshPanel.Views.PeakWindow;

namespace DshPanel.Tests;

/// <summary>
/// ИСТОРИЯ ЦЕН: точки изменения, шарик и своё окно (требование владельца 28.09.2026,
/// п. 26 `docs\DESIGN.md`).
///
/// Три обещания, и у каждого своя беда:
///
/// 1. **в истории лежат ТОЧКИ ИЗМЕНЕНИЯ, а не каждая проверка.** Проверили — не изменилось:
///    новой записи нет, прежняя остаётся текущей. Иначе файл рос бы от каждого запуска панели,
///    а человек видел бы «изменения» там, где ничего не менялось;
/// 2. **сравнение идёт с ПРЕДЫДУЩЕЙ ЗАПИСЬЮ**, а не с прошлой проверкой и не с базовой точкой:
///    у второй записи базой стоит первая — ровно как сказал владелец;
/// 3. **шарик и запись — ОДНО событие**, и шарик называет ЧТО именно изменилось: «тариф
///    изменился» человеку бесполезно. Через общую дверь изоляции — своего пути к его рабочему
///    столу у истории нет.
///
/// ⚠️ Сети в этих проверках нет вовсе: страница цен приходит подстановкой
/// (<see cref="IPricingClient"/>), а сеть от имени владельца — его право, а не наше.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PricingHistoryTests
{
    /// <summary>
    /// Отчёт прогона: им проверка НАЗЫВАЕТ путь к сохранённому кадру окна. Кадры пишутся в %TEMP%
    /// (см. <see cref="PanelTestStand.SaveFrame"/>), и без строки в отчёте человек их не найдёт.
    /// </summary>
    private readonly ITestOutputHelper _output;

    public PricingHistoryTests(ITestOutputHelper output) => _output = output;

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<PriceRow> Cheap = new[]
    {
        new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
        new PriceRow("1M OUTPUT TOKENS", "$0.6", "$1.2"),
    };

    private static readonly IReadOnlyList<PriceRow> Dear = new[]
    {
        new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
        new PriceRow("1M OUTPUT TOKENS", "$0.7", "$1.2"),
    };

    // ---------------------------------------------------------------- оснастка

    /// <summary>Ответ «страница разобралась» с заданными ценами и окнами.</summary>
    private static PricingResult Answer(
        IReadOnlyList<PriceRow>? rows = null,
        IReadOnlyList<PeakWindow>? windows = null,
        DateTimeOffset? at = null,
        string language = "ru") => new(
        Ok: true,
        Error: string.Empty,
        SourceUrl: PricingDecisions.Url(language),
        Language: language,
        Model: "deepseek-flash",
        Rows: rows ?? Cheap,
        Windows: windows ?? AgentCatalog.DeepSeek.PeakWindows,
        CheckedAt: at ?? Now);

    private static PricingSnapshot Snapshot(
        IReadOnlyList<PriceRow>? rows = null,
        DateTimeOffset? at = null,
        IReadOnlyList<PeakWindow>? windows = null) =>
        new(at ?? Now, PricingDecisions.EnglishUrl, "ru", "deepseek-flash", rows ?? Cheap,
            windows ?? AgentCatalog.DeepSeek.PeakWindows);

    /// <summary>Подстановка вместо сети: отдаёт заранее записанные ответы по порядку и считает вызовы.</summary>
    private sealed class Sequence : IPricingClient
    {
        private readonly Queue<PricingResult> _answers;

        public Sequence(params PricingResult[] answers) => _answers = new Queue<PricingResult>(answers);

        public int Calls { get; private set; }

        public PricingResult Query(string language, DateTimeOffset now)
        {
            Calls++;
            return _answers.Count > 0 ? _answers.Dequeue() : Answer();
        }
    }

    /// <summary>Файл истории в памяти: проверке нужен не диск, а «что панель записала».</summary>
    private sealed class HistoryFile
    {
        public string Text { get; set; } = string.Empty;

        public int Writes { get; private set; }

        public PricingHistoryFiles Files => new(
            Read: () => Text,
            Write: text => { Writes++; Text = text; });
    }

    private sealed record Stand(
        PricingController Control,
        List<(NoticeKind Kind, string Title, string Text)> Notices,
        HistoryFile Store,
        List<string> Log);

    private static Stand Build(IPricingClient client, string stored = "")
    {
        var file = new HistoryFile { Text = stored };
        var notices = new List<(NoticeKind, string, string)>();
        var log = new List<string>();

        var controller = new PricingController(
            client,
            () => new PanelSettings(),
            () => "ru",
            allowed: true,
            clock: () => Now,
            dispatch: action => action(),
            log: log.Add,
            rememberCheckedAt: _ => { },
            rememberWindows: _ => { },
            rememberLast: _ => { },
            historyFiles: file.Files,
            notify: (kind, title, text) => notices.Add((kind, title, text)));

        return new Stand(controller, notices, file, log);
    }

    /// <summary>Щелчок «Обновить информацию» и ожидание фонового разбора.</summary>
    private static void Read(PricingController controller)
    {
        controller.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "разбор не завершился");
    }

    /// <summary>
    /// ЖИВОЙ КОНТРОЛЛЕР ЦЕН под своим временным корнем: страница приходит подстановкой вместо сети,
    /// а история пишется НАСТОЯЩИМ файлом под корнем прогона (<c>state\pricing-history.json</c>).
    /// Одна оснастка на все проверки окна: разошедшись, они мерили бы разные истории.
    /// </summary>
    private static PricingController Live(string dir, params PricingResult[] answers)
    {
        var paths = AppPaths.Under(dir);

        var control = new PricingController(
            new Sequence(answers.Length == 0 ? new[] { Answer() } : answers),
            () => new PanelSettings(),
            () => "ru",
            allowed: true,
            clock: () => Now,
            dispatch: action => action(),
            log: _ => { },
            rememberCheckedAt: _ => { },
            rememberWindows: _ => { },
            rememberLast: _ => { },
            historyFiles: PricingHistoryFiles.Under(paths),
            notify: (_, _, _) => { });

        Read(control);

        return control;
    }

    /// <summary>
    /// Подстановка контроллера цен для проверок окна: у истории один владелец, и окно обязано
    /// показывать именно его ответ.
    /// </summary>
    private sealed class StubPricing : IPricingControl
    {
        public PricingHistory History { get; set; } = PricingHistory.Empty;

        public PricingResult Result => PricingResult.NotRequested("ru");

        public bool Busy => false;

        public bool Allowed => false;

        public string PriceHeadingText => PanelStrings.PriceTableTitle;

        public string PriceSourceText => string.Empty;

        public event Action? Changed;

        public void Refresh()
        {
        }

        public void Raise() => Changed?.Invoke();
    }

    // ------------------------------------------------ 1. нет изменения — нет записи

    /// <summary>
    /// ПРОВЕРИЛИ — НЕ ИЗМЕНИЛОСЬ: НОВОЙ ЗАПИСИ НЕТ, ШАРИКА ТОЖЕ. Это первое, что сказал владелец:
    /// хранить снимок каждой проверки не нужно. И второе: файл при этом НЕ ПЕРЕПИСЫВАЕТСЯ —
    /// иначе панель трогала бы диск на каждой суточной проверке без единого изменения.
    /// </summary>
    [Fact]
    public void Проверка_без_изменений_не_заводит_ни_записи_ни_шарика()
    {
        var stand = Build(new Sequence(Answer(), Answer()));

        // Первое чтение: истории нет вовсе — пишется только МОЛЧАЛИВАЯ базовая точка.
        Read(stand.Control);

        Assert.NotNull(stand.Control.History.Anchor);
        Assert.Empty(stand.Control.History.Changes);
        Assert.Empty(stand.Notices);
        Assert.Contains(stand.Log, line => line.Contains("базовая точка", StringComparison.Ordinal));

        var afterAnchor = stand.Store.Text;
        var writes = stand.Store.Writes;

        // Второе чтение: та же страница — та же таблица. Записи быть не должно.
        Read(stand.Control);

        Assert.Empty(stand.Control.History.Changes);
        Assert.Empty(stand.Notices);
        Assert.Equal(afterAnchor, stand.Store.Text);
        Assert.Equal(writes, stand.Store.Writes);
    }

    // ------------------------------------------------ 2. изменение цен

    /// <summary>
    /// ИЗМЕНЕНИЕ ЦЕНЫ ЗАПИСАНО СТРУКТУРОЙ: модель, имя строки страницы, было/стало по вне-пика
    /// и по пику. И — ни одного готового слова: в файле нет текста, который пришлось бы переводить
    /// при смене языка (текст собирает панель при показе, см. проверку про язык ниже).
    /// </summary>
    [Fact]
    public void Изменение_цен_записано_структурой_а_не_готовым_текстом()
    {
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear)));

        Read(stand.Control);
        Read(stand.Control);

        var change = Assert.Single(stand.Control.History.Changes);

        Assert.Equal("deepseek-flash", change.Model);
        Assert.Equal(PricingDecisions.EnglishUrl, change.SourceUrl);
        Assert.Equal("ru", change.Language);
        Assert.True(change.WindowsChanged == false, "окна не менялись, а запись говорит обратное");

        var row = Assert.Single(change.Prices);

        Assert.Equal("1M OUTPUT TOKENS", row.Item);
        Assert.Equal("$0.6", row.OffPeakBefore);
        Assert.Equal("$0.7", row.OffPeakAfter);

        // Пик не менялся — и в записи он остаётся прежним с обеих сторон: изменившееся называется
        // изменением, а неизменившееся выдуманным.
        Assert.Equal("$1.2", row.PeakBefore);
        Assert.Equal("$1.2", row.PeakAfter);

        // Строка «попадание в кэш» не менялась вовсе — её в записи быть не должно.
        Assert.DoesNotContain(change.Prices, candidate => candidate.Item.Contains("CACHE HIT", StringComparison.Ordinal));

        // И в файле — числа и поля структуры, а не фраза на языке панели.
        var decoded = PricingHistoryStore.Decode(stand.Store.Text);
        Assert.Single(decoded.Changes);
        Assert.DoesNotContain("цены изменились", stand.Store.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.NotifyPricingChangedTitle, stand.Store.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------ 3. изменение только окон

    /// <summary>
    /// ИЗМЕНИЛИСЬ ТОЛЬКО ОКНА ПИКА — запись есть, цен в ней нет, а окна названы и «было», и «стало».
    /// Шарик при этом говорит, что окна ТАКИЕ НА СТРАНИЦЕ: расписание панели он не меняет
    /// (его меняет подтверждение человека), и слова это подтверждают.
    /// </summary>
    [Fact]
    public void Изменение_только_окон_пика_называет_окна()
    {
        var before = new[] { new PeakWindow(new[] { 1, 2, 3, 4, 5 }, 60, 240) };
        var after = new[] { new PeakWindow(new[] { 1, 2, 3, 4, 5 }, 120, 300) };

        var stand = Build(new Sequence(Answer(windows: before), Answer(windows: after)));

        Read(stand.Control);
        Read(stand.Control);

        var change = Assert.Single(stand.Control.History.Changes);

        Assert.Empty(change.Prices);
        Assert.False(change.PricesChanged);
        Assert.True(change.WindowsChanged);
        Assert.Equal(60, Assert.Single(change.WindowsBefore).FromMinutes);
        Assert.Equal(120, Assert.Single(change.WindowsAfter).FromMinutes);

        var notice = Assert.Single(stand.Notices);
        var offset = PeakDecisions.OffsetMinutes(DateTimeOffset.Now);

        // Окна в шарике названы словами расписания — теми же, что в окне пиков, и с «было/стало».
        // Цены не менялись, поэтому строка в шарике ровно одна.
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.NotifyPricingWindowsFormat,
                PeakDecisions.WindowsText(before, offset),
                PeakDecisions.WindowsText(after, offset)),
            notice.Text);
    }

    // ------------------------------------------------ 4. сравнение с предыдущей записью

    /// <summary>
    /// СРАВНЕНИЕ ИДЁТ С ПРЕДЫДУЩЕЙ ЗАПИСЬЮ, А НЕ С БАЗОВОЙ ТОЧКОЙ: у третьей записи «было» —
    /// это значение ВТОРОЙ записи. Сравнение с якорем показало бы «$0.6 → $0.8», то есть соврало бы
    /// про то, что человек видел вчера.
    /// </summary>
    [Fact]
    public void Сравнение_идёт_с_предыдущей_записью_а_не_с_базовой_точкой()
    {
        var middle = new[]
        {
            new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
            new PriceRow("1M OUTPUT TOKENS", "$0.7", "$1.2"),
        };

        var highest = new[]
        {
            new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
            new PriceRow("1M OUTPUT TOKENS", "$0.8", "$1.2"),
        };

        var stand = Build(new Sequence(Answer(), Answer(rows: middle), Answer(rows: highest)));

        Read(stand.Control);
        Read(stand.Control);
        Read(stand.Control);

        Assert.Equal(2, stand.Control.History.Changes.Count);

        var second = stand.Control.History.Changes[1];

        Assert.Equal("$0.7", Assert.Single(second.Prices).OffPeakBefore);
        Assert.Equal("$0.8", Assert.Single(second.Prices).OffPeakAfter);

        // И то же самое видно в файле: состояние предыдущей записи — свёртка якоря и изменений.
        var latest = PricingHistoryStore.Latest(PricingHistoryStore.Decode(stand.Store.Text));

        Assert.NotNull(latest);
        Assert.Equal("$0.8", latest!.Rows.Single(row => row.Item == "1M OUTPUT TOKENS").OffPeak);
    }

    /// <summary>
    /// ЦЕНА, ВЕРНУВШАЯСЯ К ПРЕЖНЕМУ ЗНАЧЕНИЮ, — ТОЖЕ ИЗМЕНЕНИЕ. Это вторая половина того же
    /// правила: если бы панель хранила только «что изменилось» и сравнивала с якорем, возврат
    /// цены прошёл бы незамеченным.
    /// </summary>
    [Fact]
    public void Возврат_цены_к_прежнему_значению_тоже_изменение()
    {
        var dear = new[]
        {
            new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
            new PriceRow("1M OUTPUT TOKENS", "$0.7", "$1.2"),
        };

        var stand = Build(new Sequence(Answer(), Answer(rows: dear), Answer()));

        Read(stand.Control);
        Read(stand.Control);
        Read(stand.Control);

        Assert.Equal(2, stand.Control.History.Changes.Count);

        var back = Assert.Single(stand.Control.History.Changes[1].Prices);

        Assert.Equal("$0.7", back.OffPeakBefore);
        Assert.Equal("$0.6", back.OffPeakAfter);
    }

    // ------------------------------------------------ 5. «текущие»

    /// <summary>
    /// «ТЕКУЩИЕ» — У САМОЙ СВЕЖЕЙ ЗАПИСИ, и после нового изменения подпись уезжает к новой.
    /// Подпись считается от ПОРЯДКА, а не хранится полем: хранимая, она пережила бы новую запись
    /// и оставила бы две «текущие» сразу.
    /// </summary>
    [Fact]
    public void Подпись_текущие_уезжает_к_самой_свежей_записи()
    {
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear), Answer(rows: Cheap)));

        Read(stand.Control);
        Read(stand.Control);

        var first = stand.Control.History.Changes[0];

        Assert.True(stand.Control.History.IsCurrent(0));

        var current = PricingHistoryText.EntryTitle(first, current: true);

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PricingHistoryEntryCurrentFormat, PricingHistoryText.Stamp(first.At)),
            current);

        Read(stand.Control);

        Assert.False(stand.Control.History.IsCurrent(0));
        Assert.True(stand.Control.History.IsCurrent(1));

        // Прежняя запись потеряла подпись — и это видно по её же подписи.
        Assert.Equal(
            PricingHistoryText.Stamp(first.At),
            PricingHistoryText.EntryTitle(stand.Control.History.Changes[0], current: false));
        Assert.NotEqual(current, PricingHistoryText.EntryTitle(stand.Control.History.Changes[0], current: false));
    }

    // ------------------------------------------------ 6. числа, а не текст

    /// <summary>
    /// «$0.6» И «$0.60» — ОДНА ЦЕНА. Сравнивать текст значило бы объявить изменением то,
    /// чего человек не увидит: страница вправе печатать то же число другой записью.
    /// А «проверено 27.09» изменением не является вовсе — это не цена.
    /// </summary>
    [Fact]
    public void Шесть_десятых_и_шестьдесят_сотых_это_одна_цена()
    {
        var padded = new[]
        {
            new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.0030", "$0.0060"),
            new PriceRow("1M OUTPUT TOKENS", "$0.60", "$1.20"),
        };

        var stand = Build(new Sequence(Answer(), Answer(rows: padded)));

        Read(stand.Control);
        Read(stand.Control);

        Assert.Empty(stand.Control.History.Changes);
        Assert.Empty(stand.Notices);

        Assert.True(PricingChangeDecisions.SamePrice("$0.6", "$0.60"));
        Assert.True(PricingChangeDecisions.SamePrice("$1.20", "$1.2"));
        Assert.False(PricingChangeDecisions.SamePrice("$0.6", "$0.7"));

        // «—» — это «цены нет», и она равна самой себе, а не числу.
        Assert.True(PricingChangeDecisions.SamePrice("—", "—"));
        Assert.False(PricingChangeDecisions.SamePrice("—", "$0.5"));

        // До- и после- значения одного порядка читаются как числа, а не как строки.
        Assert.True(PricingChangeDecisions.TryNumber("$0.60", out var number));
        Assert.Equal(0.60m, number);
    }

    // ------------------------------------------------ 7. предел 30

    /// <summary>
    /// ПРЕДЕЛ: 30 изменений. Самая старая запись уходит, и об этом СКАЗАНО СЛОВАМИ — молчаливая
    /// пропажа записи выглядела бы порчей файла.
    /// </summary>
    [Fact]
    public void Тридцать_изменений_предел_старая_запись_уходит_и_это_сказано()
    {
        // ⚠️ Число названо ПРЯМО, а не взято у константы: проверка, считающая предел от самого
        // предела, зеленела бы при любом его значении — то есть не стерегла бы ничего.
        Assert.Equal(30, PricingHistory.MaxChanges);

        var history = PricingHistoryStore.Check(PricingHistory.Empty, Snapshot());

        // 31 изменение: каждая проверка даёт цену на 0,01 выше прежней.
        for (var step = 1; step <= 31; step++)
        {
            var price = "$0." + (60 + step).ToString(CultureInfo.InvariantCulture);

            var rows = new[]
            {
                new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
                new PriceRow("1M OUTPUT TOKENS", price, "$1.2"),
            };

            history = PricingHistoryStore.Check(history, Snapshot(rows, Now.AddDays(step)));
        }

        Assert.Equal(30, history.Changes.Count);
        Assert.Equal(1, history.Dropped);

        // Ушла именно САМАЯ СТАРАЯ: первая оставшаяся запись — про «$0.61», а не про «$0.60».
        Assert.Equal("$0.61", Assert.Single(history.Changes[0].Prices).OffPeakBefore);

        var words = PricingHistoryText.DroppedText(history);

        Assert.False(string.IsNullOrWhiteSpace(words), "о пропаже записи человеку не сказано");
        Assert.Contains("30", words, StringComparison.Ordinal);
    }

    // ------------------------------------------------ 8. битый файл

    /// <summary>
    /// БИТЫЙ И ЧУЖОЙ ФАЙЛ ИСТОРИИ НЕ РОНЯЕТ ПАНЕЛЬ: он считается «истории нет», и следующая
    /// проверка пишет новую базовую точку. Тот же приём, что у памяти цен и у расписания окон:
    /// файлы состояния правят руками.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не json вовсе")]
    [InlineData("{}")]
    [InlineData("{\"Changes\":[]}")]
    [InlineData("{\"Anchor\":{\"At\":\"вчера\",\"Rows\":[{\"Item\":\"1M\",\"OffPeak\":\"$1\",\"Peak\":\"$2\"}]}}")]
    [InlineData("{\"Anchor\":null,\"Changes\":[null],\"Dropped\":0}")]
    [InlineData("{\"Anchor\":{\"At\":\"2026-09-28T12:00:00.0000000+00:00\",\"Rows\":[]},\"Changes\":[]}")]
    public void Битый_файл_истории_не_роняет_панель(string json)
    {
        Assert.Equal(PricingHistory.Empty, PricingHistoryStore.Decode(json));
        Assert.Equal(PricingHistory.Empty, PricingHistoryStore.Decode(PricingHistoryStore.Encode(PricingHistory.Empty)));
    }

    /// <summary>
    /// И КОНТРОЛЛЕР С БИТЫМ ФАЙЛОМ РАБОТАЕТ: проверка проходит целиком, а на диске оказывается
    /// годная история с новой базовой точкой.
    /// </summary>
    [Fact]
    public void Контроллер_переживает_битый_файл_истории()
    {
        var stand = Build(new Sequence(Answer()), stored: "{это не json}");

        Read(stand.Control);

        Assert.NotNull(stand.Control.History.Anchor);
        Assert.Empty(stand.Control.History.Changes);

        var decoded = PricingHistoryStore.Decode(stand.Store.Text);

        Assert.NotNull(decoded.Anchor);
        Assert.Empty(decoded.Changes);
    }

    // ------------------------------------------------ 9. шарик называет что именно

    /// <summary>
    /// ШАРИК НАЗЫВАЕТ ЧТО ИМЕННО ИЗМЕНИЛОСЬ, а не «тариф изменился»: модель, имя строки страницы,
    /// тариф (вне пика или пик) и оба числа. Общая фраза бесполезна — человеку нужно понять,
    /// что подорожало.
    /// </summary>
    [Fact]
    public void Шарик_называет_что_именно_изменилось()
    {
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear)));

        Read(stand.Control);
        Read(stand.Control);

        var notice = Assert.Single(stand.Notices);

        Assert.Equal(PanelStrings.NotifyPricingChangedTitle, notice.Title);
        Assert.Contains("deepseek-flash", notice.Text, StringComparison.Ordinal);
        Assert.Contains("1M OUTPUT TOKENS", notice.Text, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.PeakShortOffPeak, notice.Text, StringComparison.Ordinal);
        Assert.Contains("$0.6", notice.Text, StringComparison.Ordinal);
        Assert.Contains("$0.7", notice.Text, StringComparison.Ordinal);

        // Неизменившееся в шарике не называется: «попадание в кэш» не менялось.
        Assert.DoesNotContain("CACHE HIT", notice.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------ 10. общая дверь изоляции

    /// <summary>
    /// ШАРИК ИДЁТ ОБЩЕЙ ДВЕРЬЮ: в изолированном прогоне молчит КАЖДЫЙ вид сообщений, и история
    /// цен — не исключение. Своего пути к рабочему столу владельца у неё нет: контроллер отдаёт
    /// решение в дверь, а показывает или гасит его она.
    /// </summary>
    [Fact]
    public void Шарик_подавляется_общей_дверью_в_изолированном_прогоне()
    {
        Assert.False(IsolationRules.ShouldNotify(NoticeKind.PricingChanged, isolatedRun: true));
        Assert.False(IsolationRules.DoorDecision(NoticeKind.PricingChanged, isolated: true));
        Assert.True(IsolationRules.ShouldNotify(NoticeKind.PricingChanged, isolatedRun: false));

        // И контроллер отдаёт шарик ИМЕННО этим видом: дверь гасит по виду, и «свой» вид
        // в обход общего предохранителя был бы дырой.
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear)));

        Read(stand.Control);
        Read(stand.Control);

        Assert.Equal(NoticeKind.PricingChanged, Assert.Single(stand.Notices).Kind);
    }

    // ------------------------------------------------ 11. язык при показе

    /// <summary>
    /// СТРОКИ ЗАПИСИ СОБИРАЮТСЯ НА ЯЗЫКЕ ПАНЕЛИ ПРИ ПОКАЗЕ. Поэтому смена языка переводит
    /// и СТАРЫЕ записи: в файле лежат числа и имена строк страницы, а не фраза на том языке,
    /// на котором панель говорила в тот день.
    /// </summary>
    [Fact]
    public void Строки_записи_собираются_на_языке_панели_при_показе()
    {
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear)));

        Read(stand.Control);
        Read(stand.Control);

        var stored = PricingHistoryStore.Decode(stand.Store.Text);
        var change = Assert.Single(stored.Changes);

        var russian = PricingHistoryText.NoticeText(change, "ru");
        var english = PricingHistoryText.NoticeText(change, "en");
        var chinese = PricingHistoryText.NoticeText(change, "zh");

        Assert.NotEqual(russian, english);
        Assert.NotEqual(russian, chinese);

        // Назван именно язык словаря, а не подстрочник: тарифные слова берутся из en.json и zh.json.
        Assert.Contains(Loc.TIn("en", nameof(PanelStrings.PeakShortOffPeak)), english, StringComparison.Ordinal);
        Assert.Contains(Loc.TIn("zh", nameof(PanelStrings.PeakShortOffPeak)), chinese, StringComparison.Ordinal);

        // И то же с подписью записи: старая запись читается на языке сегодняшней панели.
        Assert.NotEqual(
            PricingHistoryText.EntryTitle(change, current: true, "ru"),
            PricingHistoryText.EntryTitle(change, current: true, "en"));

        // А «с чем сравнивается» называет дату одинаково: дата — не слово.
        Assert.Contains(
            PricingHistoryText.Stamp(stored.Anchor!.At),
            PricingHistoryText.ComparedText(stored, 0, "en"),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------ 12. первая цена в списке

    /// <summary>
    /// ПЕРВАЯ СТРОКА СПИСКА — ПЕРВАЯ ЦЕНА, И ОНА НЕ ПУСТАЯ. Решение владельца 29.09.2026 (п. 39):
    /// список не пуст, как только панель хоть раз прочитала цены, — иначе человек, у которого тариф
    /// не менялся, видел пустое окно и решал, что панель цены потеряла (его слова: *«Я думал она
    /// накапливать и хранит данные где-то отдельно и данные она не теряет»*).
    ///
    /// Здесь же названо то, ради чего окно переделывалось: справа у первой цены СВОИ строки цен
    /// и окон, а слова «изменений панель ещё не видела» ушли — они и сбивали с толку.
    /// </summary>
    [AvaloniaFact]
    public void Окно_без_изменений_показывает_первую_цену_с_датой_и_своими_строками()
    {
        var pricing = new StubPricing
        {
            History = new PricingHistory(Snapshot(at: Now), Array.Empty<PricingChange>(), 0),
        };

        var window = new PricingHistoryWindow();

        window.Attach(pricing);
        window.Show();
        PanelTestStand.Settle();

        // ⚠️ Список НЕ пуст: в нём ровно одна строка — первая цена, с датой и временем.
        var title = Assert.Single(window.EntryTitles);

        Assert.Contains(PricingHistoryText.Stamp(Now), title, StringComparison.Ordinal);
        Assert.Contains(PricingHistoryText.FirstPriceTitle(pricing.History.Anchor!), title, StringComparison.Ordinal);

        // ⚠️ И В СПИСКЕ ОНА ЧИТАЕТСЯ ЦЕЛИКОМ: подпись ПЕРЕНОСИТСЯ, а не обрезается многоточием.
        // Проверка родилась из кадра 29.09.2026: в списке стояло «28.09.2026, 15:00 — пер…» —
        // то есть слова «первая цена», ради которых окно и переделывалось, до человека не доходили
        // (в правой части они были, в списке — нет). Меряем НАРИСОВАННОЕ: обрезка оставила бы одну
        // строку высотой в один шрифт, а перенос даёт две, поэтому по высоте и видно, что вышло.
        var list = window.FindControl<ListBox>("EntriesList")!;
        PanelTestStand.Settle();

        var row = Avalonia.VisualTree.VisualExtensions
            .GetVisualDescendants(list)
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == title);

        Assert.NotNull(row);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, row!.TextWrapping);
        Assert.True(
            row.Bounds.Height > row.FontSize * 1.6,
            $"подпись первой цены не перенеслась: высота {row.Bounds.Height} при шрифте {row.FontSize}");

        // И она же выбрана: справа видно её собственную таблицу, а не пустое место.
        Assert.Equal(title, window.FindControl<TextBlock>("EntryTitleText")!.Text);

        // Сравнивать первую цену не с чем — и это честно: сравнивать не с чем.
        Assert.Equal(string.Empty, window.Compared);

        // Дата и время — в пояснении, и словами про то, что цены с тех пор не менялись.
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.PricingHistoryEmptyFormat,
                PricingHistoryText.Stamp(Now)),
            window.Empty);

        var prices = PeakTableProbe.Cells(window.FindControl<Grid>("PriceTablePanel")!);

        // Шапка плюс по строке на каждую цену снимка — «свои строки», а не пустота.
        Assert.Equal(Snapshot(at: Now).Rows.Count + 1, prices.Count);
        Assert.Contains(prices, row => row[0] == "1M OUTPUT TOKENS");

        // Столбцы изменения пусты: у первой цены сравнивать не с чем, и там честный прочерк.
        var dash = PricingHistoryText.Arrow(PricingChangeDirection.None);

        Assert.All(prices.Skip(1), row =>
        {
            Assert.Equal(dash, row[2]);
            Assert.Equal(dash, row[4]);
        });

        // Окна пика показаны как есть — БЕЗ «Было» и «Стало»: сравнивать не с чем.
        var windows = PeakTableProbe.Cells(window.FindControl<Grid>("WindowsTablePanel")!);

        Assert.Single(windows);
        Assert.DoesNotContain(
            windows.SelectMany(row => row),
            cell => cell == PanelStrings.PricingHistoryColumnBefore || cell == PanelStrings.PricingHistoryColumnAfter);

        // ⚠️ И СТАРЫХ СЛОВ ЗДЕСЬ НЕТ: «изменений панель ещё не видела» — это про ДРУГОЕ состояние.
        Assert.DoesNotContain(PanelStrings.PricingHistoryNeverChecked, window.Empty, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// ЖИВЫМ ПУТЁМ: НАСТОЯЩИЙ контроллер цен под своим временным корнем прочитал страницу —
    /// и окно показывает первую цену с датой этого чтения. Это и есть случай владельца: панель
    /// цены получила, а тариф с тех пор не менялся.
    /// </summary>
    [AvaloniaFact]
    public void Прочитанная_страница_даёт_в_окне_первую_цену_а_не_пустой_список()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var paths = AppPaths.Under(dir);

            var control = Live(dir, Answer());

            Assert.NotNull(control.History.Anchor);

            // И записано это НА ДИСК под своим корнем — окно читает то же, что записала панель.
            var stored = PricingHistoryStore.Decode(File.ReadAllText(paths.PricingHistoryFile));
            Assert.NotNull(stored.Anchor);
            Assert.Empty(stored.Changes);

            var window = new PricingHistoryWindow();

            window.Attach(control);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                var title = Assert.Single(window.EntryTitles);

                Assert.Contains(PricingHistoryText.Stamp(Now), title, StringComparison.Ordinal);
                Assert.Contains(PricingHistoryText.Stamp(Now), window.Empty, StringComparison.Ordinal);

                // И это НЕ состояние «ещё не читали»: слова у них разные, и человек их не спутает.
                Assert.DoesNotContain(PanelStrings.PricingHistoryNeverChecked, window.Empty, StringComparison.Ordinal);

                Assert.True(
                    PeakTableProbe.Lines(window.FindControl<Grid>("PriceTablePanel")!).Count > 1,
                    "у первой цены не показано ни одной строки цен");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СТРАНИЦУ ЕЩЁ НЕ ЧИТАЛИ — окно говорит об этом СЛОВАМИ, а не показывает пустую таблицу молча.
    /// Это ДРУГОЕ состояние, чем «первая цена есть, а изменений не было»: там человек видит дату
    /// и цены, а здесь показывать нечего.
    /// </summary>
    [AvaloniaFact]
    public void Пока_страницу_не_читали_окно_говорит_это_словами()
    {
        var pricing = new StubPricing { History = PricingHistory.Empty };
        var window = new PricingHistoryWindow();

        window.Attach(pricing);
        window.Show();
        PanelTestStand.Settle();

        Assert.Empty(window.EntryTitles);
        Assert.Equal(PanelStrings.PricingHistoryNeverChecked, window.Empty);
        Assert.Equal(string.Empty, window.Compared);

        // И таблицы пусты: показывать нечего, и выдумывать записи панель не вправе.
        Assert.Single(PeakTableProbe.Cells(window.FindControl<Grid>("PriceTablePanel")!));
        Assert.Empty(PeakTableProbe.Cells(window.FindControl<Grid>("WindowsTablePanel")!));

        window.Close();
    }

    /// <summary>
    /// ПЕРВАЯ ЦЕНА + ТОЧКИ ИЗМЕНЕНИЯ: строк ровно три, первая — первая цена, последняя «текущие»,
    /// и у первой цены сравнения нет вовсе (а у первой ЗАПИСИ оно есть — с первой ценой).
    /// </summary>
    [AvaloniaFact]
    public void Окно_говорит_к_какой_записи_идёт_сравнение()
    {
        var first = new PricingChange(
            Now, PricingDecisions.EnglishUrl, "ru", "deepseek-flash",
            new[] { new PriceChangeRow("1M OUTPUT TOKENS", "$0.6", "$0.7", "$1.2", "$1.2") },
            AgentCatalog.DeepSeek.PeakWindows,
            AgentCatalog.DeepSeek.PeakWindows);

        var second = new PricingChange(
            Now.AddDays(2), PricingDecisions.EnglishUrl, "ru", "deepseek-flash",
            new[] { new PriceChangeRow("1M OUTPUT TOKENS", "$0.7", "$0.8", "$1.2", "$1.2") },
            AgentCatalog.DeepSeek.PeakWindows,
            AgentCatalog.DeepSeek.PeakWindows);

        var history = new PricingHistory(
            Snapshot(at: Now.AddDays(-1)),
            new[] { first, second },
            1);

        var pricing = new StubPricing { History = history };
        var window = new PricingHistoryWindow();

        window.Attach(pricing);
        window.Show();
        PanelTestStand.Settle();

        // О пропаже самой старой записи сказано СЛОВАМИ и НА ЭКРАНЕ, а не только в файле:
        // молчаливая пропажа записи выглядела бы порчей истории.
        var dropped = window.FindControl<TextBlock>("DroppedText")!;

        Assert.False(string.IsNullOrWhiteSpace(dropped.Text), "о переполнении истории человеку не сказано");
        Assert.Contains("30", dropped.Text!, StringComparison.Ordinal);

        // ТРИ строки: первая цена, две точки изменения. Последняя — «текущие».
        Assert.Equal(3, window.EntryTitles.Count);
        Assert.Contains(PricingHistoryText.Stamp(history.Anchor!.At), window.EntryTitles[0], StringComparison.Ordinal);
        Assert.Contains(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PricingHistoryEntryCurrentFormat, PricingHistoryText.Stamp(second.At)),
            window.EntryTitles[2]);
        Assert.DoesNotContain(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PricingHistoryEntryCurrentFormat, PricingHistoryText.Stamp(first.At)),
            window.EntryTitles[1]);

        // По умолчанию выбрана САМАЯ СВЕЖАЯ запись, и сравнивается она с предыдущей.
        Assert.Contains(
            PricingHistoryText.Stamp(first.At),
            window.Compared,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            PricingHistoryText.Stamp(history.Anchor.At),
            window.Compared,
            StringComparison.Ordinal);

        var list = window.FindControl<ListBox>("EntriesList")!;

        // Первая ЗАПИСЬ сравнивается с ПЕРВОЙ ЦЕНОЙ — и это названо её словами, не «базовой точкой».
        list.SelectedIndex = 1;
        PanelTestStand.Settle();

        Assert.Contains(
            PricingHistoryText.Stamp(history.Anchor.At),
            window.Compared,
            StringComparison.Ordinal);

        Assert.Contains(
            PanelStrings.PricingHistoryAgainstAnchorFormat.Split('(')[0].Trim(),
            window.Compared,
            StringComparison.Ordinal);

        // А у самой первой цены сравнения нет: сравнивать не с чем, и об этом сказано словами.
        list.SelectedIndex = 0;
        PanelTestStand.Settle();

        Assert.Equal(string.Empty, window.Compared);
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.PricingHistoryFirstPriceNoteFormat,
                PricingHistoryText.Stamp(history.Anchor.At)),
            window.Empty);

        window.Close();
    }

    // ------------------------------------------------ 13. цвета изменений

    /// <summary>
    /// ПОДОРОЖАНИЕ КРАСНОЕ, УДЕШЕВЛЕНИЕ ЗЕЛЁНОЕ, «СРАВНИВАТЬ НЕ С ЧЕМ» — СЕРОЕ.
    /// Проверяются РОЛИ и их значения у палитры значка, а не «на глаз»: цвет берётся из одного
    /// места на всю панель, и там он измерен по контрасту.
    /// </summary>
    [Fact]
    public void Подорожание_красное_удешевление_зелёное_а_сравнивать_не_с_чем_серое()
    {
        Assert.Equal(TrayTone.Bad, PricingChangeDecisions.Tone("$0.6", "$0.7"));
        Assert.Equal(TrayTone.Good, PricingChangeDecisions.Tone("$0.7", "$0.6"));
        Assert.Equal(TrayTone.Neutral, PricingChangeDecisions.Tone("—", "$0.5"));
        Assert.Equal(TrayTone.Neutral, PricingChangeDecisions.Tone("$0.5", "—"));
        Assert.Equal(TrayTone.Neutral, PricingChangeDecisions.Tone("whatever", "whatever"));

        // И сами цвета — те же роли, что у меню значка.
        Assert.Equal(PanelLook.BadClass, PanelLook.ToneClass(TrayTone.Bad));
        Assert.Equal(PanelLook.GoodClass, PanelLook.ToneClass(TrayTone.Good));
        Assert.Equal(PanelLook.NeutralClass, PanelLook.ToneClass(TrayTone.Neutral));

        // Красное красное, зелёное зелёное, серое серое — по каналам, а не «похоже».
        var red = TrayPalette.Text(TrayTone.Bad, TrayPalette.MenuLight);
        var green = TrayPalette.Text(TrayTone.Good, TrayPalette.MenuLight);
        var grey = TrayPalette.Text(TrayTone.Neutral, TrayPalette.MenuLight);

        Assert.True(((red >> 16) & 0xFF) > ((red >> 8) & 0xFF), $"подорожание не красное: {red:X6}");
        Assert.True(((green >> 8) & 0xFF) > ((green >> 16) & 0xFF), $"удешевление не зелёное: {green:X6}");
        Assert.Equal((grey >> 16) & 0xFF, (grey >> 8) & 0xFF);
        Assert.Equal((grey >> 8) & 0xFF, grey & 0xFF);
    }

    /// <summary>
    /// И ТЕ ЖЕ ЦВЕТА — НА ЖИВЫХ ЯЧЕЙКАХ ОКНА: стрелка вверх покрашена красным, вниз — зелёным,
    /// прочерк «сравнивать не с чем» — серым. Свойство можно выставить и не увидеть ничего;
    /// здесь спрашивается у самих ячеек таблицы.
    /// </summary>
    [AvaloniaFact]
    public void Ячейки_изменений_покрашены_ролями_палитры()
    {
        var change = new PricingChange(
            Now, PricingDecisions.EnglishUrl, "ru", "deepseek-flash",
            new[]
            {
                new PriceChangeRow("ПОДОРОЖАЛО", "$0.6", "$0.7", "$1.2", "$1.2"),
                new PriceChangeRow("ПОДЕШЕВЕЛО", "$0.7", "$0.6", "$1.2", "$1.2"),
            },
            AgentCatalog.DeepSeek.PeakWindows,
            AgentCatalog.DeepSeek.PeakWindows);

        var pricing = new StubPricing
        {
            History = new PricingHistory(Snapshot(), new[] { change }, 0),
        };

        var window = new PricingHistoryWindow();

        window.Attach(pricing);
        window.Show();
        PanelTestStand.Settle();

        var cells = PeakTableProbe.Blocks(window.FindControl<Grid>("PriceTablePanel")!)
            .SelectMany(line => line)
            .ToList();

        var up = cells.Where(cell => (cell.Text ?? string.Empty).StartsWith(PricingHistoryText.Arrow(PricingChangeDirection.Up), StringComparison.Ordinal)).ToList();
        var down = cells.Where(cell => (cell.Text ?? string.Empty).StartsWith(PricingHistoryText.Arrow(PricingChangeDirection.Down), StringComparison.Ordinal)).ToList();
        var none = cells.Where(cell => (cell.Text ?? string.Empty) == PricingHistoryText.Arrow(PricingChangeDirection.None)).ToList();

        Assert.NotEmpty(up);
        Assert.NotEmpty(down);

        // У подорожавшей строки пик не менялся — значит есть и «сравнивать не с чем», и это
        // ровно та серость, которую обещает проверка выше.
        Assert.NotEmpty(none);

        Assert.All(up, cell => Assert.Contains(PanelLook.ToneClass(TrayTone.Bad), cell.Classes));
        Assert.All(down, cell => Assert.Contains(PanelLook.ToneClass(TrayTone.Good), cell.Classes));
        Assert.All(none, cell => Assert.Contains(PanelLook.ToneClass(TrayTone.Neutral), cell.Classes));

        // Кнопка «Закрыть» подписана теми же словами, что у остальных окон панели.
        var close = window.FindControl<Button>("CloseButton")!;
        Assert.Equal(PanelStrings.AboutCloseButton, close.Content?.ToString());

        window.Close();
    }

    // ------------------------------------------------ 14. дверь окна

    /// <summary>
    /// ДВЕРЬ ОКНА ИСТОРИИ — ТА ЖЕ, ЧТО У ОСТАЛЬНЫХ ПЯТИ: одно окно на панель, повторная просьба
    /// выводит на передний план уже открытое, закрытое строится заново, а без построителя дверь
    /// говорит об отказе словами. Проверка идёт через НАСТОЯЩУЮ связку и настоящий слот
    /// (<see cref="SingleWindowSlot"/>) — своего способа показа у нового окна нет.
    /// </summary>
    [AvaloniaFact]
    public void Дверь_истории_работает_как_двери_остальных_окон()
    {
        var built = new List<Window>();
        var log = new List<string>();
        var tray = new TrayIconHost("проверка");
        var panel = new PanelWindow(new MainWindow(), isolated: false, log.Add);

        var shell = new PanelShell(
            tray,
            panel,
            new ClassicDesktopStyleApplicationLifetime(),
            log.Add,
            isolated: false,
            mayOpenBrowser: false,
            entryLink: () => string.Empty,
            trayStatus: () => new TrayStatusLines(
                new TrayStatusLine("сервер", TrayTone.Neutral),
                new TrayStatusLine("агент", TrayTone.Neutral),
                new TrayStatusLine("тариф", TrayTone.Neutral),
                new TrayStatusLine("обновление", TrayTone.Neutral)),
            notify: (_, _, _) => { },
            createAbout: () => new AboutWindow(),
                createIssue: () => new IssueWindow(),
            checkUpdate: () => { },
            createPricingHistory: () => { var window = new PricingHistoryWindow(); built.Add(window); return window; });

        using (shell)
        {
            Assert.False(shell.PricingHistoryOpen, "окно ещё не открывали, а слот считает его открытым");

            shell.OpenPricingHistory();

            Assert.True(shell.PricingHistoryOpen, "окно открыто, а слот его не видит");
            Assert.Single(built);
            Assert.Contains(PanelStrings.PricingHistoryOpenedLog, log);

            shell.OpenPricingHistory();

            Assert.Single(built);
            Assert.Contains(PanelStrings.PricingHistoryRaisedLog, log);

            built[0].Close();

            Assert.False(shell.PricingHistoryOpen, "окно закрыто, а слот всё ещё считает его открытым");

            shell.OpenPricingHistory();

            Assert.Equal(2, built.Count);
        }

        // И у панели без построителя дверь называет отказ, а не молчит.
        var without = new List<string>();
        var bare = new PanelShell(
            new TrayIconHost("проверка"),
            new PanelWindow(new MainWindow(), isolated: false, without.Add),
            new ClassicDesktopStyleApplicationLifetime(),
            without.Add,
            isolated: false,
            mayOpenBrowser: false,
            entryLink: () => string.Empty,
            trayStatus: () => new TrayStatusLines(
                new TrayStatusLine("сервер", TrayTone.Neutral),
                new TrayStatusLine("агент", TrayTone.Neutral),
                new TrayStatusLine("тариф", TrayTone.Neutral),
                new TrayStatusLine("обновление", TrayTone.Neutral)),
            notify: (_, _, _) => { },
            createAbout: () => new AboutWindow(),
                createIssue: () => new IssueWindow(),
            checkUpdate: () => { });

        using (bare)
        {
            bare.OpenPricingHistory();

            Assert.False(bare.PricingHistoryOpen);
            Assert.Contains(PanelStrings.PanelLogPricingHistoryUnavailable, without);
            Assert.DoesNotContain(PanelStrings.PricingHistoryOpenedLog, without);
        }
    }

    /// <summary>
    /// КНОПКА «ИСТОРИЯ» СТОИТ В ОКНЕ «ПИКИ И ТАРИФЫ» РЯДОМ С «ОБНОВИТЬ ИНФОРМАЦИЮ» и поднимает
    /// просьбу ровно один раз за щелчок. Проверяется на настоящем окне: подпись, подсказка
    /// и щелчок — не «кнопка объявлена».
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_истории_в_окне_пиков_подписана_и_поднимает_просьбу()
    {
        var window = new PeakView();

        window.Show();
        PanelTestStand.Settle();

        var history = window.FindControl<Button>("HistoryButton")!;
        var refresh = window.FindControl<Button>("RefreshPriceButton")!;

        Assert.Equal(PanelStrings.PricingHistoryButton, history.Content?.ToString());
        Assert.Equal(PanelStrings.TipPricingHistoryButton, ToolTip.GetTip(history));

        // Рядом: обе кнопки — соседи в одной строке шапки, а не в разных углах окна.
        Assert.Same(refresh.Parent, history.Parent);

        var asked = 0;

        window.HistoryRequested += () => asked++;

        history.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PanelTestStand.Settle();

        Assert.Equal(1, asked);

        window.Close();
    }

    // ------------------------------------------------ базовая точка и файл

    /// <summary>
    /// БАЗОВАЯ ТОЧКА ХРАНИТСЯ МОЛЧА: без неё первое изменение не с чем сравнить, но сама она
    /// ни записи, ни шарика не даёт. И именно с неё сравнивается ПЕРВАЯ запись.
    /// </summary>
    [Fact]
    public void Базовая_точка_хранится_молча_и_становится_базой_первой_записи()
    {
        var stand = Build(new Sequence(Answer(), Answer(rows: Dear)));

        Read(stand.Control);

        Assert.NotNull(stand.Control.History.Anchor);
        Assert.Empty(stand.Control.History.Changes);
        Assert.Empty(stand.Notices);

        Read(stand.Control);

        var change = Assert.Single(stand.Control.History.Changes);

        Assert.Equal("$0.6", Assert.Single(change.Prices).OffPeakBefore);

        // Первая запись сравнивается с МОЛЧАЛИВОЙ базовой точкой — и говорит об этом якорем.
        var stored = PricingHistoryStore.Decode(stand.Store.Text);

        Assert.Contains(
            PricingHistoryText.Stamp(stored.Anchor!.At),
            PricingHistoryText.ComparedText(stored, 0),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ФАЙЛ ИСТОРИИ ЛЕЖИТ ПОД КОРНЕМ ПАНЕЛИ (одна дверь путей — <see cref="AppPaths"/>) и переживает
    /// запись с чтением. Изолированный прогон получает свой файл под своим корнем, а не файл
    /// владельца: это и есть обещание изоляции.
    /// </summary>
    [Fact]
    public void История_лежит_под_корнем_панели_и_переживает_запись_и_чтение()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-panel-history", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Under(root);

        Assert.Equal(
            Path.Combine(root, "state", "pricing-history.json"),
            paths.PricingHistoryFile);

        Assert.Contains(paths.PricingHistoryFile, paths.AllPaths);

        try
        {
            var history = PricingHistoryStore.Check(PricingHistory.Empty, Snapshot());
            history = PricingHistoryStore.Check(history, Snapshot(rows: Dear, at: Now.AddDays(1)));

            var files = PricingHistoryFiles.Under(paths);
            files.Write(PricingHistoryStore.Encode(history));

            Assert.True(File.Exists(paths.PricingHistoryFile));

            var back = PricingHistoryStore.Decode(files.Read());

            Assert.Equal(history.Changes.Count, back.Changes.Count);
            Assert.Equal(history.Dropped, back.Dropped);
            Assert.Equal(
                Assert.Single(history.Changes).Prices,
                Assert.Single(back.Changes).Prices);
            Assert.Equal(history.Anchor!.At, back.Anchor!.At);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // Прогон без права пишет в пустоту и говорит об этом — «сохранил» без файла было бы ложью.
        Assert.False(PricingHistoryFiles.None.Persists);
    }

    // ------------------------------------------------ 15. кадр для глаз

    /// <summary>
    /// ВИД ОКНА «ИСТОРИЯ ЦЕН» МОЖНО ПОСМОТРЕТЬ ГЛАЗАМИ: проверка сохраняет кадр окна в PNG и
    /// называет путь в отчёте. У этого окна живого просмотра не было вовсе (`AGENTS.md`: «кадра
    /// окна и живого просмотра НЕТ»), а переделка п. 39 — ровно про то, что человек ЧИТАЕТ.
    ///
    /// Кадр снимается в состоянии владельца: панель прочитала цены, тариф с тех пор не менялся —
    /// то есть ровно тот случай, где прежнее окно было ПУСТЫМ и выглядело как потеря цен.
    ///
    /// ⚠️ Кадр уходит в <c>%TEMP%</c> и НИКОГДА в репозиторий, <c>dist</c> или <c>upd-build</c>:
    /// в нём цены и время, полученные этим прогоном.
    ///
    /// ⚠️ И кадр `--shot pricing-history` этим НЕ подменяется: съёмка идёт прогоном БЕЗ права
    /// читать файл истории и честно показывает состояние «ещё не читали». Подставленная история
    /// в кадре README была бы ложью.
    /// </summary>
    [AvaloniaFact]
    public void Вид_истории_с_первой_ценой_сохраняется_кадром()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var control = Live(dir, Answer());
            var window = new PricingHistoryWindow();

            window.Attach(control);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                // В кадре — именно первая цена: одна строка списка и её собственные таблицы.
                Assert.Single(window.EntryTitles);

                var path = PanelTestStand.SaveFrame(window, "history-" + Guid.NewGuid().ToString("N")[..8]);

                _output.WriteLine("кадр окна «История цен» с первой ценой: " + path);

                Assert.True(File.Exists(path), "кадр не сохранён: " + path);
                Assert.True(new FileInfo(path).Length > 0, "кадр пуст: " + path);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
