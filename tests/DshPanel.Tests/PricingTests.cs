using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЦЕНЫ СО СТРАНИЦЫ ЦЕН: разбор, решения и расписание. Замечание владельца 27.09.2026, глядя
/// на панель живьём: *«табличка пиков собрана неверно — сделать ДВЕ таблицы»*.
///
/// Три вещи, и у каждой своя беда:
///
/// 1. **разбор идёт по ЗАПИСАННОЙ странице** (<see cref="PricingFixture"/>) — не по выдуманному
///    тексту. Разбор — чистая функция именно ради этого: живой запрос в проверках запрещён,
///    это сеть от имени владельца;
/// 2. **валюта — по языку панели, и берётся со СТРАНИЦЫ ТОГО ЖЕ ЯЗЫКА.** Курса панель не знает,
///    и пересчитывать доллары в юани она не вправе: у английской и китайской страницы РАЗНЫЕ
///    числа, а не один и тот же текст с разным знаком;
/// 3. **сеть ходит только тот, кому можно.** Прогон проверки не ходит вовсе и говорит об этом
///    словами; автоматический разбор идёт при запуске панели, но НЕ чаще раза в сутки (решение
///    владельца), а окна пика со страницы применяет только щелчок человека — это и есть то
///    подтверждение, которого требует правило панели 1.x.
/// </summary>
public class PricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static string EnglishPage => PricingFixture.EnglishPhrase + " " + PricingFixture.EnglishTable;

    /// <summary>
    /// Подстановка вместо сети: считает вызовы и отдаёт ЗАРАНЕЕ ЗАПИСАННЫЙ ответ.
    /// Живого запроса в проверках не бывает вовсе — и это не «стараемся», а устройство:
    /// контроллер получает уговор <see cref="IPricingClient"/> обязательным параметром.
    /// </summary>
    private sealed class Client : IPricingClient
    {
        private readonly Func<string, PricingResult> _answer;

        public Client(Func<string, PricingResult> answer) => _answer = answer;

        public int Calls { get; private set; }

        public List<string> Languages { get; } = new();

        public PricingResult Query(string language, DateTimeOffset now)
        {
            Calls++;
            Languages.Add(language);

            return _answer(language);
        }
    }

    /// <summary>Ответ «страница разобралась»: цены и окна заданы проверкой.</summary>
    private static PricingResult Good(string language) => new(
        true, string.Empty, PricingDecisions.Url(language), language, "deepseek-flash",
        new[]
        {
            new PriceRow("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
            new PriceRow("1M OUTPUT TOKENS", "$0.6", "$1.2"),
        },
        AgentCatalog.DeepSeek.PeakWindows,
        Now);

    private static PricingController Controller(
        IPricingClient client,
        PanelSettings settings,
        bool allowed = true,
        bool isolated = false,
        Action<string>? checkedAt = null,
        Action<string>? windows = null,
        Action<string>? log = null,
        string language = "ru",
        Action<string>? last = null,
        PricingHistoryFiles? history = null,
        Action<NoticeKind, string, string>? notify = null) =>
        new(
            client,
            () => settings,
            () => language,
            allowed,
            () => Now,
            action => action(),
            log ?? (_ => { }),
            checkedAt ?? (_ => { }),
            windows ?? (_ => { }),
            last ?? (_ => { }),

            // Истории цен этим проверкам не нужно — они про разбор страницы, права и расписание.
            // Файла нет вовсе (`None`), и это честнее подставного: истории в прогоне и не должно быть.
            history ?? PricingHistoryFiles.None,
            notify ?? ((_, _, _) => { }),
            isolated);

    // ------------------------------------------------------------------ разбор страницы

    /// <summary>
    /// РАЗБОР АНГЛИЙСКОЙ СТРАНИЦЫ: единицы — СЛОВАМИ СТРАНИЦЫ, цены — её числа со знаком её
    /// валюты, модель названа. Ни одного выдуманного названия: перевести заголовки единиц значило бы
    /// напечатать то, чего на странице нет.
    /// </summary>
    [Fact]
    public void Английская_страница_разбирается_в_три_строки_цены()
    {
        var result = PricingPage.Parse(EnglishPage, "ru", PricingDecisions.EnglishUrl, Now);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("deepseek-flash", result.Model);

        Assert.Equal(
            new[]
            {
                "1M INPUT TOKENS (CACHE HIT)",
                "1M INPUT TOKENS (CACHE MISS)",
                "1M OUTPUT TOKENS",
            },
            result.Rows.Select(row => row.Item));

        Assert.Equal(new[] { "$0.003", "$0.15", "$0.6" }, result.Rows.Select(row => row.OffPeak));
        Assert.Equal(new[] { "$0.006", "$0.3", "$1.2" }, result.Rows.Select(row => row.Peak));
    }

    /// <summary>
    /// КИТАЙСКАЯ СТРАНИЦА — ОТДЕЛЬНЫЙ ДОКУМЕНТ С ДРУГИМИ ЧИСЛАМИ, и знак валюты у неё свой.
    /// Это и есть довод «не пересчитывать по курсу»: 0,02 юаня за попадание в кэш — не «0,003
    /// доллара, переведённые по курсу», а то, что напечатано на странице нужного языка.
    /// </summary>
    [Fact]
    public void Китайская_страница_даёт_юани_а_не_пересчёт()
    {
        var result = PricingPage.Parse(PricingFixture.ChineseTable, "zh", PricingDecisions.ChineseUrl, Now);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Rows.Count);

        Assert.Equal(new[] { "¥0.02", "¥1", "¥4" }, result.Rows.Select(row => row.OffPeak));
        Assert.Equal(new[] { "¥0.04", "¥2", "¥8" }, result.Rows.Select(row => row.Peak));

        // Знак — по языку, и он же стоит в решении: второй такой выбор разошёлся бы с первым.
        Assert.Equal("¥", PricingDecisions.Currency("zh"));
        Assert.Equal("$", PricingDecisions.Currency("ru"));
        Assert.Equal("$", PricingDecisions.Currency("en"));

        // И адрес — ТОЙ ЖЕ языковой версии: китайская страница читается китайским языком.
        Assert.Equal(PricingDecisions.ChineseUrl, PricingDecisions.Url("zh"));
        Assert.Equal(PricingDecisions.EnglishUrl, PricingDecisions.Url("ru"));
        Assert.Equal(PricingDecisions.EnglishUrl, PricingDecisions.Url("en"));
    }

    /// <summary>
    /// АДРЕС АНГЛИЙСКОЙ СТРАНИЦЫ — ОДИН на панель: он же назван источником в профиле агента.
    /// Второй такой адрес разошёлся бы с первым, и строка «откуда взяты окна» показывала бы
    /// не ту страницу, с которой панель читает цены.
    /// </summary>
    [Fact]
    public void Адрес_английской_страницы_совпадает_с_источником_профиля()
    {
        Assert.Equal(PricingDecisions.EnglishUrl, AgentCatalog.DeepSeek.PricingSource);
    }

    /// <summary>
    /// ОКНА ПИКА СО СТРАНИЦЫ: английская фраза называет время в UTC, и разбор даёт ровно те окна,
    /// по которым панель работает.
    /// </summary>
    [Fact]
    public void Английская_фраза_даёт_окна_профиля()
    {
        var windows = PricingPage.Windows(EnglishPage);

        Assert.True(PricingDecisions.SameWindows(AgentCatalog.DeepSeek.PeakWindows, windows));

        Assert.Equal(2, windows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, windows[0].Days);
        Assert.Equal(60, windows[0].FromMinutes);
        Assert.Equal(240, windows[0].ToMinutes);
        Assert.Equal(360, windows[1].FromMinutes);
        Assert.Equal(600, windows[1].ToMinutes);
    }

    /// <summary>
    /// КИТАЙСКАЯ ФРАЗА НАЗЫВАЕТ ПЕКИНСКОЕ ВРЕМЯ, и разбор обязан снять восемь часов: без этого
    /// «09:00» встало бы в расписание как 09:00 UTC, то есть человек увидел бы окно, которого нет.
    /// </summary>
    [Fact]
    public void Китайская_фраза_переводится_из_пекинского_времени()
    {
        var windows = PricingPage.Windows(PricingFixture.ChinesePhrase);

        Assert.True(PricingDecisions.SameWindows(AgentCatalog.DeepSeek.PeakWindows, windows));
        Assert.Equal(60, windows[0].FromMinutes);
    }

    /// <summary>
    /// ЗАПИСЬ КИТАЙСКОЙ СТРАНИЦЫ — БЕЗ ФРАЗЫ ПРО ЧАСЫ, и это честный ответ разбора: цены есть,
    /// окон нет. Панель не вправе достроить расписание за страницу и НЕ применяет пустой набор
    /// (см. <see cref="PricingWindows.Normalize"/>): расписание остаётся прежним.
    /// </summary>
    [Fact]
    public void Без_фразы_про_часы_окон_нет_а_цены_есть()
    {
        var result = PricingPage.Parse(PricingFixture.ChineseTable, "zh", PricingDecisions.ChineseUrl, Now);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.HasRows);
        Assert.Empty(result.Windows);

        Assert.Equal(string.Empty, PricingWindows.Normalize(PricingWindows.Encode(result.Windows, Now)));
    }

    /// <summary>
    /// ЧУЖОЙ ТЕКСТ — ЧЕСТНЫЙ ОТКАЗ, А НЕ ПУСТАЯ ТАБЛИЦА МОЛЧА: страницу переписали, и человек
    /// обязан узнать об этом словами, а не догадываться по пустому месту.
    /// </summary>
    [Fact]
    public void Страница_без_таблицы_и_пустая_страница_дают_названный_отказ()
    {
        Assert.Equal(
            PanelStrings.PricePageEmpty,
            PricingPage.Parse(string.Empty, "ru", PricingDecisions.EnglishUrl, Now).Error);

        var noTable = PricingPage.Parse("<html><body>цены переехали</body></html>", "ru", PricingDecisions.EnglishUrl, Now);

        Assert.False(noTable.Ok);
        Assert.Equal(PanelStrings.PriceTableMissing, noTable.Error);
        Assert.Empty(noTable.Rows);
    }

    /// <summary>
    /// ЦЕНОЙ СЧИТАЕТСЯ ТОЛЬКО ЯЧЕЙКА С ДЕНЕЖНЫМ ПРИЗНАКОМ. Без этого в цены попало бы всё подряд —
    /// «1M», «2500» и «384K» с той же страницы.
    /// </summary>
    [Theory]
    [InlineData("$0.003", "0.003")]
    [InlineData("$1.2", "1.2")]
    [InlineData("¥0.02", "0.02")]
    [InlineData("0.02元", "0.02")]
    [InlineData("1元", "1")]
    [InlineData("2500", null)]
    [InlineData("1M", null)]
    [InlineData("384K", null)]
    [InlineData("", null)]
    public void Ценой_считается_только_число_с_денежным_признаком(string cell, string? expected)
    {
        Assert.Equal(expected, PricingPage.PriceValue(cell));
    }

    // ------------------------------------------------------------------ суточное правило

    /// <summary>
    /// РАЗ В СУТКИ, И НЕ ЧАЩЕ: отметки нет — «пора» (панель, поставленная впервые, читает цены
    /// при первом же запуске), час назад и 23 часа назад — «рано», РОВНО сутки и больше — «пора».
    ///
    /// ⚠️ Граница названа прямо: «раз в сутки» — это «прошло не меньше суток», поэтому ровно
    /// сутки уже пора. Сдвинь границу на «больше» — и панель, открытая человеком раз в день
    /// в одно и то же время, не читала бы страницу НИКОГДА.
    ///
    /// <c>-1</c> означает «отметки нет вовсе»: параметр числовой, потому что набор случаев
    /// в <c>InlineData</c> обязан быть одного типа.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    public void Разбор_идёт_не_чаще_раза_в_сутки(int hoursAgo, bool expected)
    {
        var stamp = hoursAgo < 0 ? null : PricingDecisions.Stamp(Now.AddHours(-hoursAgo));

        Assert.Equal(expected, PricingDecisions.Due(Now, stamp, isolatedRun: false));
    }

    /// <summary>
    /// МУСОР В ОТМЕТКЕ ЗНАЧИТ «НИКОГДА НЕ ЧИТАЛИ», А НЕ «ЧИТАЛИ ТОЛЬКО ЧТО»: файл настроек правят
    /// руками, и неразобранная отметка обязана приводить к разбору, а не закрывать его навсегда.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("мусор")]
    [InlineData("0")]
    [InlineData("24")]
    public void Мусор_в_отметке_значит_никогда(string? stamp)
    {
        Assert.True(PricingDecisions.Due(Now, stamp, isolatedRun: false));
        Assert.Equal(string.Empty, PricingDecisions.CheckedText(stamp));
    }

    /// <summary>
    /// ИЗОЛИРОВАННЫЙ ПРОГОН НЕ РАЗБИРАЕТ СТРАНИЦУ ПО РАСПИСАНИЮ НИКОГДА — даже когда «пора»:
    /// это работа панели с сетью, и в прогоне проверки её нет вовсе (общий предикат изоляции).
    /// </summary>
    [Fact]
    public void Изолированный_прогон_по_расписанию_не_читает_страницу()
    {
        Assert.False(PricingDecisions.Due(Now, null, isolatedRun: true));
        Assert.True(PricingDecisions.Due(Now, null, isolatedRun: false));
    }

    /// <summary>
    /// ЧАСЫ ЗОВУТ РАЗБОР ТОЛЬКО КОГДА ПОРА: отметка «час назад» оставляет подстановку нетронутой,
    /// а «сутки назад» — читает страницу ровно один раз. Иначе панель стучалась бы в сеть каждый
    /// такт, и «не чаще раза в сутки» осталось бы словами.
    /// </summary>
    [Fact]
    public void Часы_читают_страницу_только_когда_пора()
    {
        var recent = new PanelSettings { PricingCheckedAt = PricingDecisions.Stamp(Now.AddHours(-1)) };
        var client = new Client(Good);
        var controller = Controller(client, recent);

        controller.Tick(Now);

        Assert.Equal(0, client.Calls);

        var stale = new PanelSettings { PricingCheckedAt = PricingDecisions.Stamp(Now.AddHours(-25)) };
        var staleClient = new Client(Good);
        var staleController = Controller(staleClient, stale);

        staleController.Tick(Now);

        Assert.True(SpinWait.SpinUntil(() => staleClient.Calls == 1, 5000), "страница не прочитана");

        // Второй такт в ту же минуту — и снова ничего: отметка уже новая.
        staleController.Tick(Now);

        Assert.Equal(1, staleClient.Calls);
    }

    /// <summary>
    /// ЦЕНЫ ПРОШЛОГО РАЗБОРА ПОКАЗЫВАЮТСЯ СРАЗУ ПРИ ЗАПУСКЕ — и это НЕ улучшение, а исправление
    /// дефекта, названного владельцем 28.09.2026: *«сами тарифы я заметил что не сохраняются.
    /// Я обновлял инфу вчера, но сегодня снова пусто, только пики сохранены»*.
    ///
    /// Причина была такая: разбор закрыт суточным гейтом (разобрали сегодня — второй раз сегодня
    /// не пойдём), а самих цен в настройках не лежало нигде — только отметка времени. Значит после
    /// перезапуска панели гейт закрыт (разбора не будет), память процесса пуста (показывать нечего),
    /// и таблица «Стоимость» стояла пустой до нажатия «Обновить информацию». Окна пика при этом
    /// оставались на месте — они лежали в настройках отдельной записью.
    ///
    /// ⚠️ Проверка НАЗЫВАЕТ клиента и требует, чтобы он не был спрошен: «показали вчерашнее»
    /// и «сходили в сеть при запуске» — разные вещи, и вторая нарушила бы суточное правило.
    /// </summary>
    [Fact]
    public void Цены_прошлого_разбора_видны_сразу_без_нового_похода_в_сеть()
    {
        var stored = PricingMemory.Encode(Good("ru"));

        Assert.NotEmpty(stored);

        var settings = new PanelSettings
        {
            PricingCheckedAt = PricingDecisions.Stamp(Now),
            PricingLast = stored,
        };

        var client = new Client(Good);
        var controller = Controller(client, settings);

        // Спрашивать не пришлось: таблица уже есть, и это таблица ПРОШЛОГО разбора.
        Assert.Equal(0, client.Calls);

        Assert.True(controller.Result.Ok);
        Assert.Equal(2, controller.Result.Rows.Count);
        Assert.Equal("deepseek-flash", controller.Result.Model);
        Assert.Equal(Now, controller.Result.CheckedAt);

        // И подпись «откуда взято» называет источник и время, а не молчит.
        Assert.Contains("deepseek", controller.PriceSourceText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА НЕ ВИДИТ ПАМЯТИ ВООБЩЕ: подставить ему вчерашнюю таблицу значило бы
    /// показать в прогоне проверки то, чего этот прогон не читал и читать не должен (красная
    /// линия 4: прогон проверки видит умолчания и не читает данные владельца).
    /// </summary>
    [Fact]
    public void Прогон_без_права_не_читает_память_цен()
    {
        var settings = new PanelSettings { PricingLast = PricingMemory.Encode(Good("ru")) };

        var controller = Controller(new Client(Good), settings, allowed: false);

        Assert.False(controller.Result.Ok);
        Assert.Empty(controller.Result.Rows);
    }

    /// <summary>
    /// УСПЕШНЫЙ РАЗБОР ЗАПОМИНАЕТСЯ ЦЕЛИКОМ, а причина отказа — НЕТ. Вчерашний отказ, показанный
    /// сегодня, выдавался бы за свежий: сеть могла починиться пять минут назад. Проверка называет
    /// оба случая, иначе «пишем всегда» прошло бы незамеченным.
    /// </summary>
    [Fact]
    public void Успешный_разбор_запоминается_а_отказ_нет()
    {
        var remembered = new List<string>();

        var controller = Controller(new Client(Good), new PanelSettings(), last: remembered.Add);

        controller.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "разбор не завершился");

        Assert.True(controller.Result.Ok, $"ответ не Ok: {controller.Result.Error}");

        // Успешный разбор запомнен ОДИН раз, и запомнено ИМЕННО то, что видит человек.
        var single = Assert.Single(remembered);
        var back = PricingMemory.Decode(single);

        Assert.NotNull(back);
        Assert.Equal(Good("ru").Rows, back!.Rows);
        Assert.Equal(Good("ru").Model, back.Model);
        Assert.Equal(Now, back.CheckedAt);

        // А ОТКАЗ НЕ ЗАПОМИНАЕТСЯ ВОВСЕ — ни причиной, ни пустой строкой: причина отказа,
        // показанная завтра, выдавалась бы за свежую (сеть могла починиться пять минут назад).
        // Проверка требует РОВНО НОЛЬ записей: «записали пустое» тоже было бы записью.
        var failed = new List<string>();
        var broken = Controller(
            new Client(_ => PricingResult.Failed("нет сети", PricingDecisions.Url("ru"), "ru", Now)),
            new PanelSettings(),
            last: failed.Add);

        broken.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !broken.Busy, 5000), "разбор не завершился");
        Assert.False(broken.Result.Ok);
        Assert.Empty(failed);
    }

    /// <summary>
    /// МУСОР В ПАМЯТИ — «НЕ ЧИТАЛИ», А НЕ ПАДЕНИЕ: файл настроек правят руками, и панель обязана
    /// это пережить. Тот же ответ у пустой записи и у записи без отметки времени: таблица без даты
    /// читалась бы как «прочитано неизвестно когда», а это хуже честного «ещё не читали».
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("не json вовсе")]
    [InlineData("{}")]
    [InlineData("{\"Rows\":[]}")]
    [InlineData("{\"Checked\":\"вчера\",\"Rows\":[{\"Item\":\"1M\",\"OffPeak\":\"$1\",\"Peak\":\"$2\"}]}")]
    public void Мусор_в_памяти_цен_значит_не_читали(string json)
    {
        Assert.Null(PricingMemory.Decode(json));
        Assert.Equal(string.Empty, PricingMemory.Normalize(json));
    }

    /// <summary>
    /// ПАМЯТЬ ЦЕН ПЕРЕЖИВАЕТ СОХРАНЕНИЕ ИЗ ОКНА НАСТРОЕК — по той же причине, что согласие
    /// на сервер и окна пика: окно пишет настройки ЦЕЛИКОМ, и поле, которого оно не знает,
    /// обнулялось бы каждым нажатием «Сохранить». Тогда прочитанные цены пропадали бы ровно
    /// у того, кто зашёл поменять что-то в настройках.
    /// </summary>
    [Fact]
    public void Память_цен_переживает_сохранение_настроек()
    {
        var json = PricingMemory.Encode(Good("ru"));

        var cleaned = SettingsStore.Clean(new PanelSettings { PricingLast = json });

        Assert.Equal(json, cleaned.PricingLast);

        // А мусор в файле приводится к «не читали» — дверью, а не как получится.
        Assert.Equal(string.Empty, SettingsStore.Clean(new PanelSettings { PricingLast = "мусор" }).PricingLast);
    }

    /// <summary>
    /// АВТОМАТИЧЕСКИЙ РАЗБОР ОКОН НЕ ПРИМЕНЯЕТ: расписание меняет человек, а ночной разбор только
    /// показывает цены (правило панели 1.x: «окна применяются только по подтверждению, цены — показ»).
    /// </summary>
    [Fact]
    public void Автоматический_разбор_не_меняет_расписание()
    {
        var settings = new PanelSettings();
        var applied = new List<string>();
        var checkedAt = new List<string>();

        var controller = Controller(
            new Client(Good),
            settings,
            checkedAt: checkedAt.Add,
            windows: applied.Add);

        controller.Tick(Now);

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "разбор не завершился");
        Assert.True(controller.Result.Ok);
        Assert.Empty(applied);
        Assert.Single(checkedAt);
    }

    /// <summary>
    /// ЩЕЛЧОК ЧЕЛОВЕКА ПРИМЕНЯЕТ И ОКНА: кнопка «Обновить информацию» и есть подтверждение,
    /// которого требует правило. Заодно отмечается время разбора — им закрыто суточное правило.
    /// </summary>
    [Fact]
    public void Щелчок_человека_применяет_окна_со_страницы()
    {
        var settings = new PanelSettings();
        var applied = new List<string>();
        var checkedAt = new List<string>();

        var controller = Controller(
            new Client(Good),
            settings,
            checkedAt: checkedAt.Add,
            windows: applied.Add);

        controller.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "разбор не завершился");

        var single = Assert.Single(applied);
        var stored = PricingWindows.Decode(single);

        Assert.True(PricingDecisions.SameWindows(AgentCatalog.DeepSeek.PeakWindows, stored.Windows));
        Assert.Single(checkedAt);

        // И записанное применяется к профилю — той же дверью, что у обоих окон панели.
        var agent = PricingWindows.Apply(
            AgentCatalog.DeepSeek,
            new PanelSettings { PricingPeakWindows = single, PricingCheckedAt = checkedAt[0] });

        Assert.True(PricingDecisions.SameWindows(AgentCatalog.DeepSeek.PeakWindows, agent.PeakWindows));

        // Свой набор окон — ДРУГОЙ день, и он обязан дойти до профиля целиком.
        var other = PricingWindows.Encode(
            new[] { new PeakWindow(new[] { 6, 0 }, 0, 120) }, Now);

        var changed = PricingWindows.Apply(AgentCatalog.DeepSeek, new PanelSettings { PricingPeakWindows = other });

        Assert.Single(changed.PeakWindows);
        Assert.Equal(new[] { 0, 6 }, changed.PeakWindows[0].Days);
        Assert.Equal(120, changed.PeakWindows[0].ToMinutes);
        Assert.NotEqual(AgentCatalog.DeepSeek.PricingChecked, changed.PricingChecked);

        // А мусор и пустое значение означают «как в профиле», а не «расписание пропало».
        Assert.Same(AgentCatalog.DeepSeek, PricingWindows.Apply(AgentCatalog.DeepSeek, new PanelSettings()));
        Assert.Same(
            AgentCatalog.DeepSeek,
            PricingWindows.Apply(
                AgentCatalog.DeepSeek,
                new PanelSettings { PricingPeakWindows = "{это не json}", PricingCheckedAt = "мусор" }));
    }

    // ------------------------------------------------------------------ права и слова

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА НЕ ХОДИТ В СЕТЬ ВООБЩЕ: подстановка не трогается ни по расписанию,
    /// ни по щелчку, а в журнал уходит строка о том, что разбор не делается. Молчания здесь быть
    /// не может: «панель ничего не прочитала» обязано быть видно словами.
    ///
    /// ⚠️ **Проверка ЖДЁТ, а не считает сразу.** Запрос уходит в фон (`Task.Run`), и «сейчас ноль
    /// вызовов» ничего не доказывало бы: вызов мог случиться через миллисекунду. Поэтому после
    /// каждого повода даётся секунда на то, чтобы вызов ПОЯВИЛСЯ, — и проверка падает, если он
    /// появился. Мутация «право не спрашивается» ловится именно этим ожиданием.
    /// </summary>
    [Fact]
    public void Без_права_страница_не_читается_и_это_сказано()
    {
        var log = new List<string>();
        var client = new Client(Good);
        var controller = Controller(client, new PanelSettings(), allowed: false, log: log.Add);

        controller.Start();
        controller.Tick(Now);
        controller.Refresh();

        Assert.Contains(PanelStrings.PanelLogPricingLocked, log);
        Assert.Equal(PanelStrings.PriceLocked, controller.PriceSourceText);
        Assert.False(controller.Busy);

        // Секунда на то, чтобы запрос ПОЯВИЛСЯ: не появился — значит панель в сеть не пошла.
        Assert.False(
            SpinWait.SpinUntil(() => client.Calls > 0, 1000),
            "прогон проверки сходил в сеть за страницей цен");

        Assert.Equal(0, client.Calls);
    }

    /// <summary>
    /// С ЧЕЛОВЕЧЕСКИМ ПРАВОМ ЧАСЫ ЗАПУСКАЮТСЯ и сразу делают первый такт: решение владельца —
    /// разбор ПРИ ЗАПУСКЕ панели. Строка об этом уходит в журнал.
    /// </summary>
    [Fact]
    public void С_правом_часы_запускаются_и_первый_разбор_идёт_сразу()
    {
        var log = new List<string>();
        var client = new Client(Good);
        var controller = Controller(client, new PanelSettings(), log: log.Add);

        controller.Start();

        Assert.Contains(PanelStrings.PanelLogPricingClock, log);
        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "первый разбор не завершился");
        Assert.Equal(1, client.Calls);

        controller.Dispose();
    }

    /// <summary>
    /// ОТКУДА ВЗЯТЫ ЦЕНЫ — СЛОВАМИ, и это требование владельца: *«если страница нужного языка
    /// не разобралась — покажи то, что есть, и скажи словами, откуда взято; не молчи»*.
    /// Три разных ответа, и каждый называет свою причину.
    /// </summary>
    [Fact]
    public void Строка_о_ценах_называет_источник_а_не_молчит()
    {
        // Ничего не читали — сказано, что не читали.
        var idle = Controller(new Client(Good), new PanelSettings());

        Assert.Equal(PanelStrings.PriceNeverRead, idle.PriceSourceText);

        // Прочитали — названы адрес страницы и время проверки.
        var client = new Client(Good);
        var read = Controller(client, new PanelSettings());

        read.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !read.Busy, 5000), "разбор не завершился");

        Assert.Contains(PricingDecisions.EnglishUrl, read.PriceSourceText, StringComparison.Ordinal);
        Assert.Contains(PricingDecisions.CheckedText(PricingDecisions.Stamp(Now)), read.PriceSourceText, StringComparison.Ordinal);

        // Обе подстановки на месте в словаре: строка без них не назвала бы ни адреса, ни времени.
        Assert.Contains("{0}", PanelStrings.PriceSourceFormat, StringComparison.Ordinal);
        Assert.Contains("{1}", PanelStrings.PriceSourceFormat, StringComparison.Ordinal);

        // Не вышло — названа причина, а не пустое место.
        var failed = Controller(
            new Client(_ => PricingResult.Failed("HTTP 503 Service Unavailable", PricingDecisions.EnglishUrl, "ru", Now)),
            new PanelSettings());

        failed.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !failed.Busy, 5000), "разбор не завершился");

        Assert.Contains("HTTP 503", failed.PriceSourceText, StringComparison.Ordinal);
        Assert.Contains("{0}", PanelStrings.PriceFailedFormat, StringComparison.Ordinal);
    }

    /// <summary>
    /// МОДЕЛЬ НАЗВАНА В ЗАГОЛОВКЕ ТАБЛИЦЫ. Страница печатает цены для НЕСКОЛЬКИХ моделей, и
    /// подставить первую молча значило бы показать человеку цену не того, чем он пользуется.
    /// </summary>
    [Fact]
    public void Заголовок_таблицы_называет_модель_со_страницы()
    {
        var idle = Controller(new Client(Good), new PanelSettings());

        Assert.Equal(PanelStrings.PriceTableTitle, idle.PriceHeadingText);

        var read = Controller(new Client(Good), new PanelSettings());

        read.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !read.Busy, 5000), "разбор не завершился");

        Assert.Contains("deepseek-flash", read.PriceHeadingText, StringComparison.Ordinal);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.PriceTableTitleFormat, "deepseek-flash"),
            read.PriceHeadingText);
    }

    /// <summary>
    /// ЯЗЫК ПАНЕЛИ ДОХОДИТ ДО ЗАПРОСА: контроллер спрашивает страницу НА ТОМ ЯЗЫКЕ, на котором
    /// говорит панель, — иначе знак валюты и числа разошлись бы со страницей.
    /// </summary>
    [Fact]
    public void Язык_панели_доходит_до_страницы()
    {
        var client = new Client(Good);
        var controller = Controller(client, new PanelSettings(), language: "zh");

        controller.Refresh();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "разбор не завершился");
        Assert.Equal(new[] { "zh" }, client.Languages);
    }
}
