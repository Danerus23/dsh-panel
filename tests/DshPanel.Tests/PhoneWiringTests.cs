using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОБВЯЗКА ТЕЛЕФОНА ЦЕЛИКОМ — ЖИВЫМ ПУТЁМ, А НЕ ПО ЧАСТЯМ (уточнения владельца 11.10.2026
/// после живого прогона).
///
/// **Зачем отдельный класс.** У каждой из этих вещей уже есть своя проверка «по частям»:
/// `PhoneGateTests` — калитка, `PhoneSectionTests` — раздел, `FirewallTests` — правило. А живой
/// прогон показал три дефекта ровно НА СТЫКАХ, где части сходятся: калитку заводили только
/// при включённом телефоне (щелчок рубильника поднимать было нечего), адрес до калитки
/// не доезжал вовсе (она брала значение, прочитанное при сборке дверей), а правило брандмауэра
/// читалось как «чужое», потому что `netsh` звали без `verbose`. Поэтому здесь проверяется
/// СБОРКА: калитку собирает ТОТ ЖЕ член, что и в продукте (`App.CreatePhoneGate`), порт — тот же
/// свободный порт петли, файл пропуска — под своим временным корнем, а окно собирается как
/// в продукте (`PanelTestStand.SettingsStand`).
///
/// ⚠️ Наружу не уходит ни один байт: адрес — петля, движок подставной, подписка подставная.
/// Порт — свободный у системы; <c>3080</c>/<c>3081</c>/<c>43127</c> не занимаются.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PhoneWiringTests
{
    // ---- чем подставляются двери ------------------------------------------------------------

    /// <summary>Подставная подписка на события: кадры подаёт проверка.</summary>
    private sealed class FakeEvents
    {
        private readonly List<Action<string>> _subscribers = new();

        public PhoneEventDoors Doors => new(Open, _ => { });

        public void Emit(string frame)
        {
            foreach (var subscriber in _subscribers.ToArray()) subscriber(frame);
        }

        private IPhoneEventLink? Open(Action<string> onFrame)
        {
            _subscribers.Clear();
            _subscribers.Add(onFrame);

            return new Link();
        }

        private sealed class Link : IPhoneEventLink
        {
            public bool Alive { get; set; } = true;

            public void Dispose() => Alive = false;
        }
    }

    /// <summary>Один вызов движка: имя и доводы как есть.</summary>
    private sealed record EngineCall(string Endpoint, string Args);

    /// <summary>
    /// СТЕНД ОБВЯЗКИ: калитка, собранная ТЕМ ЖЕ членом, что в продукте, свой корень, свой порт
    /// петли и подставные двери движка и событий.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        private readonly string _root;

        public Stand(string root, bool phoneEnabled, bool humanLog = true, bool emptyAddress = false)
        {
            _root = root;
            Paths = AppPaths.Under(root);
            Store = new SettingsStore(Paths.SettingsFile);
            Port = FreeLoopbackPort();
            Events = new FakeEvents();

            // ⚠️ РАЗРЕШЕНИЕ ЗАПИСЫВАЕТСЯ ДО ЧТЕНИЯ: владелец настроек читает файл ОДИН раз при
            // сборке, и «включим телефон потом» до него не доедет — калитка получила бы «выключено»
            // и не встала бы (это и есть поведение продукта, а не удобство проверки).
            if (phoneEnabled) Seed(Store, settings => settings.PhoneEnabled = true);

            Control = new SettingsController(
                Store,
                Paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: Log.Add);

            // ⚠️⚠️ АДРЕС ПРИВЯЗКИ — ТОЛЬКО ПЕТЛЯ, И ЭТО ЖЕЛЕЗНОЕ ПРАВИЛО ПРОВЕРОК (найдено живым
            // прогоном 11.10.2026): стенд с адресом из `RealNetworkAddresses.Read()` поднял
            // НАСТОЯЩУЮ калитку на сетевом адресе машины владельца — Windows задала свой вопрос и
            // завела два широких разрешающих правила для `DshPanel.Tests.exe` (входящее, любой порт,
            // Private+Public). Проверка не имеет права открывать порт в чужую сеть.
            //
            // «Какой адрес выбрало решение» проверяется ЧИСТОЙ `GateAddress` (там же и отказ пустым
            // ответом), а НЕ связыванием сокета; отказ калитки на пустом адресе — отдельной проверкой
            // с пустой дверью (`emptyAddress`). Настоящий адрес машины проверкам не нужен вовсе.
            Func<string> address = emptyAddress ? EmptyAddress : LoopbackAddress;

            var doors = new PhoneGateDoors(
                () => { AddressAsked++; return address(); },
                Port,
                (endpoint, args) =>
                {
                    Calls.Add(new EngineCall(endpoint, args));

                    return Reply;
                },
                PhonePassFiles.Under(Paths, Log.Add),
                Events.Doors,
                () => DateTimeOffset.Now,
                Log.Add,
                Bound: value => App.RememberPhoneAddress(Control, value));

            Gate = App.CreatePhoneGate(
                RunContext.Create(RunRequest.Owner()),
                humanLaunch: humanLog,
                doors,
                () => Control.Settings.PhoneEnabled,
                Log.Add);

            Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + Port + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        /// <summary>Сколько раз калитка СПРОСИЛА адрес: по этому числу видно, что она спрашивает
        /// его в момент запуска, а не берёт значение, прочитанное при сборке дверей.</summary>
        public int AddressAsked { get; private set; }

        private static string LoopbackAddress() => "127.0.0.1";

        /// <summary>Пустой адрес — то, чем решение отвечает, когда годного адреса нет.</summary>
        private static string EmptyAddress() => string.Empty;

        public AppPaths Paths { get; }

        public SettingsStore Store { get; }

        public SettingsController Control { get; }

        public int Port { get; }

        public FakeEvents Events { get; }

        public PhoneGate? Gate { get; }

        public HttpClient Http { get; }

        public List<EngineCall> Calls { get; } = new();

        public List<string> Log { get; } = new();

        public PhoneEngineAnswer Reply { get; set; } =
            new(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}");

        public void Dispose()
        {
            Gate?.Dispose();
            Http.Dispose();
            RealNetworkAddresses.Test = null;
            PanelTestStand.RemoveTemp(_root);
        }
    }

    // ---- мелкие двери проверки --------------------------------------------------------------

    private static int FreeLoopbackPort()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);

            try
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                if (!PhoneGate.PortForbidden(port) && port != PhonePass.DefaultPort) return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("не нашлось свободного порта петли, который калитка вправе занять");
    }

    /// <summary>АДРЕС СВОЕЙ СЕТИ — подставной: настоящая сеть этой машины проверке не нужна.</summary>
    private static RawAddress Wired(string ip) =>
        new("Ethernet", "Realtek PCIe GbE Family Controller", true, ip, "255.255.255.0", 25, OnLink: true);

    private static Address One(string ip) => new("Ethernet", ip, AddressKind.Physical, OnLink: true);

    private static void Seed(SettingsStore store, Action<PanelSettings> change)
    {
        var next = SettingsStore.Clean(new PanelSettings());
        change(next);

        Assert.True(store.Save(next), "подготовка проверки не записалась");
    }

    /// <summary>Послать калитке запрос. <paramref name="cookie"/> — значение НАШЕЙ cookie.</summary>
    private static async Task<(int Status, string Body, string? Location, string? Cookie)> Send(
        HttpClient http,
        HttpMethod method,
        string path,
        string? body = null,
        string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", PhoneGate.CookieName + "=" + cookie);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        var location = response.Headers.TryGetValues("Location", out var places) ? string.Join("; ", places) : null;
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join("; ", values) : null;

        return ((int)response.StatusCode, text, location, setCookie);
    }

    // ---- 1. калитка: владение, рубильник, право ---------------------------------------------

    /// <summary>
    /// КАЛИТКА ЗАВОДИТСЯ ПО ПРАВУ ВЛАДЕНИЯ И ПОДНИМАЕТСЯ ТЕМ ЖЕ ОБЪЕКТОМ ПО РУБИЛЬНИКУ.
    ///
    /// ⚠️ **Это и есть дефект живого прогона.** Прежде право было одно, и при выключенном телефоне
    /// калитки не существовало ВОВСЕ: щелчок рубильника поднимать было нечего, а код сопряжения
    /// выпустить нечем. Теперь калитка есть всегда, когда прогон ею владеет, и поднимает её
    /// отдельное действие — ТУ ЖЕ САМУЮ.
    ///
    /// Мутации, которые обязаны упасть здесь: «`CreatePhoneGate` возвращает null при выключенном
    /// телефоне» · «`SetPhoneAllowed(true)` не зовёт `StartPhoneGate`» · «поднимается другая
    /// калитка, а не та, которой владеет прогон».
    /// </summary>
    [Fact]
    public void Калитка_владеется_по_праву_и_поднимается_рубильником()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

            // Телефон ВЫКЛЮЧЕН в файле — и калитка всё равно есть: ею владеет запуск человеком.
            using var stand = new Stand(dir, phoneEnabled: false);

            // (1) ВЛАДЕНИЕ: калитка ЕСТЬ и при выключенном телефоне — иначе рубильнику нечего
            //     поднимать. И сама она не слушает.
            Assert.NotNull(stand.Gate);
            Assert.False(stand.Gate!.Listening);

            var refused = stand.Gate.Start();

            Assert.False(refused.Opened);
            Assert.Equal(PanelStrings.PhoneGateNoRightLog, refused.ReasonKey);

            // (2) РУБИЛЬНИК: согласие записывается, и ТА ЖЕ калитка поднимается отдельным действием.
            var section = new PhoneSection(
                stand.Control, () => stand.Gate, stand.Log.Add, () => DateTimeOffset.Now, _ => Array.Empty<byte>());

            Assert.True(section.SetPhoneAllowed(true));
            Assert.True(stand.Control.Settings.PhoneEnabled);

            // ⚠️ Сам рубильник калитку НЕ поднимает: сперва узкое правило брандмауэра.
            Assert.False(stand.Gate.Listening, "рубильник поднял калитку до правила брандмауэра");

            Assert.True(section.StartPhoneGate());
            Assert.True(stand.Gate.Listening, "калитка не встала по отдельному действию");

            // (3) И выключение гасит её (слушателя не держим выключенным телефоном).
            Assert.True(section.SetPhoneAllowed(false));
            Assert.False(stand.Gate.Listening);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРОГОНУ ПРОВЕРКИ КАЛИТКА НЕ ДОСТАЁТСЯ ВОВСЕ — ни владения, ни слушания. Здесь проверяется
    /// именно право: тем же членом, что и продукт, но с «не человеком» на входе.
    /// </summary>
    [Fact]
    public void Прогону_проверки_калитка_не_достаётся()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            using var stand = new Stand(dir, phoneEnabled: false, humanLog: false);

            Assert.Null(stand.Gate);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---- 2. адрес: доезжает и записывается --------------------------------------------------

    /// <summary>
    /// АДРЕС СПРАШИВАЕТСЯ В МОМЕНТ ЗАПУСКА И ЗАПИСЫВАЕТСЯ СЛЕДОМ.
    ///
    /// ⚠️ Второй дефект живого прогона: калитка получала адрес, прочитанный ПРИ СБОРКЕ дверей, —
    /// то есть значение на момент старта панели. Теперь адрес — ДВЕРЬ, и спрашивается она
    /// в <c>Start()</c>; на каком адресе встали, калитка сообщает обратно, и след кладёт в настройки
    /// ТОТ ЖЕ член, что и продукт (<see cref="App.RememberPhoneAddress"/>).
    ///
    /// ⚠️ Привязка — петля (настоящий адрес своей сети проверка занимать не вправе); выбор адреса
    /// проверяется отдельно — чистой `GateAddress` и живой сборкой дверей ниже.
    ///
    /// Мутации, которые обязаны упасть здесь: «адрес не доезжает (готовая строка вместо двери)» ·
    /// «след адреса не записывается» · «в след уехал другой адрес».
    /// </summary>
    [Fact]
    public void Адрес_доезжает_до_калитки_и_пишется_следом()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            using var stand = new Stand(dir, phoneEnabled: true);

            // (1) ДО ЗАПУСКА адрес НЕ спрашивали: значение, прочитанное при сборке дверей, — ровно
            //     тот дефект, ради которого дверь и заведена.
            Assert.Equal(0, stand.AddressAsked);

            Assert.True(stand.Gate!.Start().Opened);

            Assert.Equal(1, stand.AddressAsked);
            Assert.True(stand.Gate.Listening);

            // (2) СЛЕД — В ФАЙЛЕ НАСТРОЕК, под своим временным корнем: тот же адрес, на котором встали.
            var saved = new SettingsStore(stand.Paths.SettingsFile).Load();

            Assert.True(saved.Ok, $"настройки не прочитались: {saved.Problem}");
            Assert.Equal("127.0.0.1", saved.Settings.PhoneLastAddress);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// АДРЕС КАЛИТКИ БЕРЁТСЯ РЕШЕНИЕМ РАЗДЕЛА, А НЕ СЛЕДОМ ИЗ НАСТРОЕК.
    ///
    /// ⚠️ Это проверка СБОРКИ ДВЕРЕЙ у продукта (`App`): прежде туда уходил `PhoneLastAddress` —
    /// то есть «по какому адресу телефон приходил в прошлый раз», а не «какой адрес годен сейчас».
    /// Разошлись они ровно тогда, когда адрес сменился, а панель об этом ещё не знала.
    ///
    /// Проверка читает ИСХОДНИКИ: поведением этого не доказать — важно, ЧТО именно положено
    /// в дверь адреса, а не как она отвечает.
    /// </summary>
    [Fact]
    public void Адрес_калитки_берётся_решением_раздела()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var app = UiThreadTests.WithoutComments(
            File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "App.axaml.cs")));

        var panel = WindowlessTests.BodyAt(app, "private void StartPanel(");

        Assert.False(string.IsNullOrWhiteSpace(panel), "тело старта панели не найдено — проверять нечего");

        // (1) Адрес — ТЕМ ЖЕ решением, что показывает раздел.
        Assert.Contains("PhoneDecisions.GateAddress", panel, StringComparison.Ordinal);
        Assert.Contains("NetworkAddresses.Collect", panel, StringComparison.Ordinal);

        // (2) И НЕ следом из настроек: «последний виденный» адрес — не решение о том, где встать.
        Assert.DoesNotContain("PhoneLastAddress", panel, StringComparison.Ordinal);

        // (3) А след, который калитка сообщает обратно, пишется тем же членом, что проверен живьём.
        Assert.Contains("RememberPhoneAddress", panel, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПУСТОЙ АДРЕС — ОТКАЗ СЛОВАМИ, А НА ПЕТЛЮ КАЛИТКА НЕ «ПЕРЕЕЗЖАЕТ» САМА.
    ///
    /// ⚠️ Дверь адреса здесь ПУСТАЯ (`emptyAddress`) — ровно то, чем решение отвечает, когда
    /// годного адреса нет. Прежде эта проверка шла «живой сборкой дверей продукта»: дверь звала
    /// <c>PhoneDecisions.GateAddress</c> поверх `RealNetworkAddresses.Read()`. Это оказалось
    /// ОПАСНО и снято 11.10.2026: на машине с настоящей сетью такой стенд поднимает калитку на
    /// сетевом адресе владельца (Windows завела два широких правила для `DshPanel.Tests.exe`).
    /// Выбор адреса проверяется ЧИСТОЙ `GateAddress` (ниже), а здесь остаётся то, что можно
    /// проверить только живьём: пустой адрес не превращается ни в петлю, ни в «первый попавшийся».
    /// </summary>
    [Fact]
    public void Пустое_решение_об_адресе_это_отказ()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            using var stand = new Stand(dir, phoneEnabled: true, emptyAddress: true);

            var start = stand.Gate!.Start();

            Assert.False(start.Opened);
            Assert.Equal(PanelStrings.PhoneGateNoAddressLog, start.ReasonKey);
            Assert.Equal(1, stand.AddressAsked);
            Assert.False(stand.Gate.Listening);

            // И ПОРТ ПЕТЛИ СВОБОДЕН: «не встала» — это не занятый порт.
            Assert.True(FreeLoopbackPort() > 0);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НИ ОДИН СТЕНД ПРОВЕРОК НЕ СЛУШАЕТ НИЧЕГО, КРОМЕ ПЕТЛИ, — ВОРОТА ПО ИСХОДНИКАМ.
    ///
    /// **От какого случая (живой прогон 11.10.2026).** Стенд этой самой проверки брал адрес из
    /// <c>RealNetworkAddresses.Read()</c> и поднимал НАСТОЯЩУЮ калитку на сетевом адресе машины
    /// владельца: Windows задала свой вопрос и завела **два широких разрешающих правила для
    /// `DshPanel.Tests.exe`** (входящее, любой порт, Private+Public). Правила — на машине владельца,
    /// и снимать их он решает сам; дело проверок — не открывать порт в чужую сеть НИКОГДА.
    ///
    /// Проверка читает ИСХОДНИКИ проверок и требует трёх вещей:
    /// <list type="number">
    /// <item>настоящие адреса машины не читаются вовсе — <c>RealNetworkAddresses.Read()</c> в пробах
    /// не встречается (адрес, который нужен как ЗНАЧЕНИЕ, подставляется через
    /// <c>RealNetworkAddresses.Test</c> — так делают проверки раздела «Телефон»);</item>
    /// <item><c>NetworkAddresses.Collect</c> — тот же запрет и по той же причине: это второй способ
    /// собрать список настоящих адресов;</item>
    /// <item>в СПИСКЕ ДОВОДОВ каждой сборки дверей калитки (<c>new PhoneGateDoors(…)</c>) нет ни
    /// одного адреса, кроме петли: <c>127.0.0.1</c>.</item>
    /// </list>
    ///
    /// Мутации, которые обязаны упасть здесь: «стенд берёт адрес из настоящей сети» (это ровно тот
    /// дефект, ради которого ворота заведены) · «в двери калитки подставлен сетевой адрес литералом».
    /// </summary>
    [Fact]
    public void Ни_один_стенд_проверок_не_слушает_ничего_кроме_петли()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var folder = Path.Combine(root!, "tests", "DshPanel.Tests");
        var sources = Directory
            .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Code: UiThreadTests.WithoutComments(File.ReadAllText(path))))
            .ToArray();

        Assert.True(sources.Length > 20, $"исходников проверок нашлось {sources.Length} — проверять нечего");

        // ⚠️ НАЗВАНИЯ-ИГОЛКИ СОБИРАЮТСЯ ИЗ ЧАСТЕЙ, и это не украшение: собранная целиком иголка
        // лежала бы в ЭТОМ ЖЕ файле литералом, и проверка находила бы саму себя — падала бы всегда.
        var readCall = "RealNetworkAddresses." + "Read()";
        var collectCall = "NetworkAddresses." + "Collect(";

        foreach (var (path, code) in sources)
        {
            var name = Path.GetFileName(path);

            // (1) НАСТОЯЩИЕ АДРЕСА МАШИНЫ НЕ ЧИТАЮТСЯ — только подставляются.
            //
            // ⚠️ Ищем ВЫЗОВ (со скобками), а не имя: имя встречается в проверке `App` и в этой
            // самой — СЛОВАМИ, внутри строк. Строка о продукте — не вызов.
            Assert.False(
                code.Contains(readCall, StringComparison.Ordinal),
                $"{name}: проверка читает настоящие адреса машины — адрес как значение "
                + "подставляется через RealNetworkAddresses.Test");

            // (2) И СПИСОК НАСТОЯЩИХ АДРЕСОВ НЕ СОБИРАЕТСЯ: это второй способ сделать то же.
            Assert.False(
                code.Contains(collectCall, StringComparison.Ordinal),
                $"{name}: проверка собирает настоящие адреса машины");

            // (3) В ДВЕРЯХ КАЛИТКИ — ТОЛЬКО ПЕТЛЯ.
            foreach (var doors in DoorsArguments(code))
            {
                foreach (var address in Addresses(doors))
                {
                    Assert.Equal("127.0.0.1", address);
                }
            }
        }
    }

    /// <summary>Адреса, похожие на IPv4, в названном куске исходника — как есть, текстом.</summary>
    private static IEnumerable<string> Addresses(string code) =>
        System.Text.RegularExpressions.Regex
            .Matches(code, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")
            .Select(match => match.Value);

    /// <summary>
    /// КУСКИ ИСХОДНИКА СО СПИСКОМ ДОВОДОВ КАЖДОЙ СБОРКИ ДВЕРЕЙ: от <c>new PhoneGateDoors(</c>
    /// до закрывающей его скобки. Скобки считаются, а не ищутся «до первой»: доводы внутри —
    /// обычные лямбды со своими скобками.
    /// </summary>
    private static IEnumerable<string> DoorsArguments(string code)
    {
        const string head = "new PhoneGateDoors(";

        for (var at = code.IndexOf(head, StringComparison.Ordinal);
             at >= 0;
             at = code.IndexOf(head, at + head.Length, StringComparison.Ordinal))
        {
            var depth = 0;

            for (var index = code.IndexOf('(', at); index < code.Length; index++)
            {
                if (code[index] == '(') depth++;
                else if (code[index] == ')' && --depth == 0)
                {
                    yield return code.Substring(at, index - at + 1);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// АДРЕС ВЫБИРАЕТСЯ РЕШЕНИЕМ, А НЕ УГАДЫВАНИЕМ: выбор человека, если он годен; иначе —
    /// единственный годный; иначе — пусто. Отдельной проверкой, потому что это ЧИСТОЕ решение
    /// и оно же решает, встанет ли калитка.
    /// </summary>
    [Fact]
    public void Адрес_выбирается_решением()
    {
        var one = new[] { One("192.168.0.7") };
        var two = new[] { One("192.168.0.7"), One("192.168.0.9") };

        // (1) Один годный — он и берётся, выбор человека не нужен.
        Assert.Equal("192.168.0.7", PhoneDecisions.GateAddress(one, string.Empty));

        // (2) Несколько — без выбора человека адреса НЕТ: гадать нельзя (решение владельца).
        Assert.Equal(string.Empty, PhoneDecisions.GateAddress(two, string.Empty));

        // (3) Выбор человека решает, когда он годен.
        Assert.Equal("192.168.0.9", PhoneDecisions.GateAddress(two, "192.168.0.9"));

        // (4) Выбор ПЕРЕСТАЛ быть годным (адрес сменился, адаптер выключили): он не держится —
        //     берётся единственный оставшийся, а среди нескольких адреса нет вовсе (гадать нельзя).
        Assert.Equal("192.168.0.7", PhoneDecisions.GateAddress(one, "10.0.0.5"));
        Assert.Equal(string.Empty, PhoneDecisions.GateAddress(two, "10.0.0.5"));

        // (5) Ни одного годного (или список пуст) — пусто, и калитка скажет словами.
        Assert.Equal(string.Empty, PhoneDecisions.GateAddress(Array.Empty<Address>(), "192.168.0.7"));
        Assert.Equal(string.Empty, PhoneDecisions.GateAddress(null, "192.168.0.7"));

        // (6) Петля и APIPA телефону не годятся — правило отбора то же, что у раздела.
        Assert.Equal(
            string.Empty,
            PhoneDecisions.GateAddress(
                new[]
                {
                    new Address("Loopback", "127.0.0.1", AddressKind.Physical, OnLink: true),
                    new Address("APIPA", "169.254.10.1", AddressKind.Physical, OnLink: true),
                },
                string.Empty));
    }

    // ---- 5. следы в журнале -----------------------------------------------------------------

    /// <summary>
    /// ЖУРНАЛ ПОМНИТ СОПРЯЖЕНИЕ, ВОПРОС И ОТВЕТ — И НЕ ПОМНИТ СЕКРЕТОВ.
    ///
    /// Уточнение владельца 11.10.2026: «следы без секретов». Проверка идёт ЖИВЫМ путём калитки
    /// (настоящий слушатель, настоящее сопряжение, настоящий ответ формой) и требует ДВУХ вещей:
    /// строки есть — и в них нет ни пропуска из файла, ни значения cookie, ни подписи.
    /// </summary>
    [Fact]
    public async Task Журнал_помнит_сопряжение_вопрос_и_ответ_без_секретов()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            using var stand = new Stand(dir, phoneEnabled: true);

            Assert.True(stand.Gate!.Start().Opened);

            // (1) СОПРЯЖЕНИЕ — настоящим кодом и настоящей страницей.
            var code = stand.Gate.IssueCode();
            var paired = await Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}");

            Assert.Equal(200, paired.Status);
            Assert.NotNull(paired.Cookie);

            Assert.Contains(
                stand.Log,
                line => line.Contains("сопряж", StringComparison.Ordinal));

            var cookie = PhoneGate.CookieValueOf(paired.Cookie!);

            // (2) ВОПРОС ПОКАЗАН ТЕЛЕФОНУ.
            stand.Events.Emit(
                "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
            stand.Events.Emit(
                "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
                + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
                + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
                + "\"options\":[{\"label\":\"Да\"}]}]}}}");

            Assert.Contains(
                stand.Log,
                line => line.Contains("вопрос агента показан", StringComparison.Ordinal)
                    && line.Contains("event-7", StringComparison.Ordinal)
                    && line.Contains("1", StringComparison.Ordinal));

            // (3) ОТВЕТ УШЁЛ В ДВИЖОК.
            var answered = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=0", cookie);

            Assert.Equal(303, answered.Status);
            Assert.Contains(
                stand.Log,
                line => line.Contains("ушёл в Сервер DSH", StringComparison.Ordinal)
                    && line.Contains("event-7", StringComparison.Ordinal)
                    && line.Contains("phone-1", StringComparison.Ordinal));

            // (4) И НИ ОДНОГО СЕКРЕТА: ни значения cookie, ни пропуска из файла, ни подписи.
            var pass = File.Exists(stand.Paths.PhonePassFile)
                ? File.ReadAllText(stand.Paths.PhonePassFile)
                : string.Empty;

            foreach (var line in stand.Log)
            {
                Assert.DoesNotContain(cookie, line, StringComparison.Ordinal);
                Assert.DoesNotContain("dsh-phone=", line, StringComparison.Ordinal);

                if (pass.Length > 0) Assert.DoesNotContain(pass, line, StringComparison.Ordinal);
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---- 4. порядок: правило ДО калитки ------------------------------------------------------

    /// <summary>
    /// РУБИЛЬНИК НЕ ПОДНИМАЕТ КАЛИТКУ, ПОКА НЕТ УЗКОГО ПРАВИЛА, — и говорит об этом словами;
    /// после удачного добавления правила калитка встаёт САМА.
    ///
    /// ⚠️ Это третий дефект живого прогона: калитка поднималась первой, Windows задавала свой
    /// вопрос и открывала ШИРОКИЕ правила. Порядок теперь обратный, и проверяется он на ЖИВОЙ
    /// обвязке: окно, настоящий раздел, НАСТОЯЩАЯ калитка на свободном порту петли и подставной
    /// брандмауэр (настоящий трогать на машине нельзя — решение владельца).
    ///
    /// Мутации, которые обязаны упасть здесь: «калитка поднимается до правила» · «человеку
    /// не сказано, чего не хватает» · «после удачного добавления калитка не поднимается» ·
    /// «правило открывает калитку без согласия на телефон».
    /// </summary>
    [AvaloniaFact]
    public void Правило_брандмауэра_идёт_до_калитки()
    {
        var dir = PanelTestStand.TempDir();
        var log = new List<string>();
        PhoneGate? gate = null;
        SettingsWindow? window = null;

        try
        {
            RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

            var paths = AppPaths.Under(dir);
            var control = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: log.Add);

            var firewall = new CountingFirewall();

            gate = App.CreatePhoneGate(
                RunContext.Create(RunRequest.Owner()),
                humanLaunch: true,
                new PhoneGateDoors(
                    // ⚠️ Привязка — ПЕТЛЯ: настоящий адрес своей сети проверка занимать не вправе.
                    // Выбор адреса проверяется отдельно и чисто (`GateAddress`) плюс живой сборкой
                    // дверей (`Пустое_решение_об_адресе_это_отказ`).
                    () => "127.0.0.1",
                    FreeLoopbackPort(),
                    (_, _) => new PhoneEngineAnswer(200, "{}"),
                    PhonePassFiles.Under(paths, log.Add),
                    PhoneEventDoors.None,
                    () => DateTimeOffset.Now,
                    log.Add,
                    Bound: address => App.RememberPhoneAddress(control, address)),
                () => control.Settings.PhoneEnabled,
                log.Add)!;

            window = new SettingsWindow();
            window.Attach(
                control,
                autostart: null,
                phone: new PhoneSection(control, () => gate!, log.Add),
                firewall: firewall);

            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            PanelTestStand.Settle();

            var check = window.FindControl<CheckBox>("PhoneAllowedCheck")!;
            var result = window.FindControl<TextBlock>("FirewallResultText")!;

            // (1) ПРАВИЛА НЕТ — калитка НЕ встаёт, и человеку сказано, чего не хватает.
            check.IsChecked = true;
            PanelTestStand.Settle();

            Assert.True(control.Settings.PhoneEnabled, "рубильник не записал согласие");
            Assert.False(gate.Listening, "калитка встала ДО правила брандмауэра");
            Assert.True(result.IsVisible, "о нехватке правила молчат");
            Assert.Equal(PanelStrings.PhoneGateBlockedByFirewall, result.Text);

            // (2) ЧЕЛОВЕК СТАВИТ ПРАВИЛО — и калитка встаёт САМА, без второго щелчка рубильника.
            window.PressFirewall();

            WaitFor(() => gate.Listening, "калитку после добавления правила");

            Assert.True(firewall.Rule);

            // (3) И ОБРАТНО: при готовом правиле калитка встаёт по щелчку рубильника — тем же
            //     объектом, который до этого молчал.
            check.IsChecked = false;
            PanelTestStand.Settle();

            Assert.False(gate.Listening, "выключение не погасило калитку");

            check.IsChecked = true;
            PanelTestStand.Settle();

            Assert.True(gate.Listening, "калитка не встала при готовом правиле");
        }
        finally
        {
            window?.CloseQuietly();
            gate?.Dispose();
            RealNetworkAddresses.Test = null;
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «ПРОЧИТАТЬ НЕ УДАЛОСЬ» — ЭТО НЕ «ПРАВИЛА НЕТ»: калитка НЕ поднимается и об этом сказано
    /// словами. Открыть дверь в надежде значило бы получить тот самый вопрос Windows и широкие
    /// правила — то, ради чего порядок и вводился.
    /// </summary>
    [AvaloniaFact]
    public void Непрочитанное_правило_калитку_не_поднимает()
    {
        var dir = PanelTestStand.TempDir();
        var log = new List<string>();
        PhoneGate? gate = null;
        SettingsWindow? window = null;

        try
        {
            RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

            var paths = AppPaths.Under(dir);
            var control = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: log.Add);

            gate = App.CreatePhoneGate(
                RunContext.Create(RunRequest.Owner()),
                humanLaunch: true,
                new PhoneGateDoors(
                    // ⚠️ Привязка — ПЕТЛЯ: настоящий адрес своей сети проверка занимать не вправе.
                    // Выбор адреса проверяется отдельно и чисто (`GateAddress`) плюс живой сборкой
                    // дверей (`Пустое_решение_об_адресе_это_отказ`).
                    () => "127.0.0.1",
                    FreeLoopbackPort(),
                    (_, _) => new PhoneEngineAnswer(200, "{}"),
                    PhonePassFiles.Under(paths, log.Add),
                    PhoneEventDoors.None,
                    () => DateTimeOffset.Now,
                    log.Add,
                    Bound: address => App.RememberPhoneAddress(control, address)),
                () => control.Settings.PhoneEnabled,
                log.Add)!;

            window = new SettingsWindow();
            window.Attach(
                control,
                autostart: null,
                phone: new PhoneSection(control, () => gate!, log.Add),
                firewall: new CountingFirewall { Unreadable = true });

            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            PanelTestStand.Settle();

            window.FindControl<CheckBox>("PhoneAllowedCheck")!.IsChecked = true;
            PanelTestStand.Settle();

            Assert.False(gate.Listening, "калитка встала на непрочитанном правиле");
            Assert.Equal(
                PanelStrings.PhoneGateBlockedByFirewall,
                window.FindControl<TextBlock>("FirewallResultText")!.Text);
        }
        finally
        {
            window?.CloseQuietly();
            gate?.Dispose();
            RealNetworkAddresses.Test = null;
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ДОЖДАТЬСЯ ФОНА, А НЕ СПАТЬ НАУГАД: изменение правила идёт в фоновой нитке (иначе окно
    /// замёрзло бы на запросе Windows), значит проверка обязана ЖДАТЬ, прокручивая очередь.
    /// </summary>
    private static void WaitFor(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < until)
        {
            PanelTestStand.Settle();

            if (done()) return;

            System.Threading.Thread.Sleep(20);
        }

        Assert.Fail($"не дождались: {what}");
    }

    /// <summary>Брандмауэр, который «стоит» или «не читается» — и считает добавления правила.</summary>
    private sealed class CountingFirewall : IFirewallDoor
    {
        public bool Rule { get; set; }

        public bool Unreadable { get; set; }

        public FirewallState Read(int port)
        {
            if (Unreadable) return FirewallDecisions.Unverified("netsh: no output");

            return Rule ? FirewallDecisions.Ready(port) : FirewallDecisions.Absent();
        }

        public FirewallChange Add(int port)
        {
            Rule = true;

            return FirewallChange.Yes(string.Empty);
        }

        public FirewallChange Remove()
        {
            Rule = false;

            return FirewallChange.Yes(string.Empty);
        }
    }
}
