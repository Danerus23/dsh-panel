using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СЛУШАТЕЛЬ КАЛИТКИ ТЕЛЕФОНА (этап F1, кусок 2) — наружная дверь панели.
///
/// **Чем это проверяется.** Двери у калитки — делегаты, поэтому здесь НЕТ ни движка, ни сети
/// наружу, ни данных владельца: подставной движок записывает, ЧЕМ его позвали, подставная
/// подписка отдаёт кадры по заказу проверки, файл пропуска лежит под своим временным корнем
/// (<see cref="PanelTestStand.TempDir"/>). Слушатель при этом НАСТОЯЩИЙ: он поднимается на петле
/// и на СВОБОДНОМ порту, который система выдала связыванием на <c>0</c>.
///
/// ⚠️ **Порт <c>43127</c> здесь НЕ занимается никогда.** Он — умолчание продукта
/// (<see cref="PhonePass.DefaultPort"/>), и проверка этого не только не трогает, но и стережёт:
/// тестовый порт выбирается свободным, а умолчание сверяется числом. Порт владельца (<c>3080</c>)
/// и запасной порт панели (<c>3081</c>) не занимаются тем более — их запрет проверяется
/// РЕШЕНИЕМ (<see cref="PhoneGate.PortAllowed"/>), а не попыткой связывания.
/// </summary>
public sealed class PhoneGateTests
{
    // ---- чем подставляются двери ------------------------------------------------------------

    /// <summary>Один вызов, которым проверка позвала движок: имя и ДОВОДЫ КАК ЕСТЬ.</summary>
    public sealed record EngineCall(string Endpoint, string Args);

    /// <summary>Подставная подписка на поток событий: кадры подаёт проверка.</summary>
    private sealed class FakeEvents
    {
        private readonly List<Action<string>> _subscribers = new();

        public int Opens { get; private set; }

        public Link? Current { get; private set; }

        public PhoneEventDoors Doors => new(Open, _ => { });

        public void Emit(string frame)
        {
            foreach (var subscriber in _subscribers.ToArray()) subscriber(frame);
        }

        private IPhoneEventLink? Open(Action<string> onFrame)
        {
            Opens++;
            _subscribers.Clear();
            _subscribers.Add(onFrame);

            Current = new Link();

            return Current;
        }

        public sealed class Link : IPhoneEventLink
        {
            public bool Alive { get; set; } = true;

            public void Dispose() => Alive = false;
        }
    }

    /// <summary>
    /// ПОДСТАВНАЯ ПОДПИСКА С НЕСКОЛЬКИМИ ПОТОКАМИ (G1). У калитки УЖЕ есть своя подписка
    /// (вопросы), а живая подсказка списка держит СВОЮ — и дверь обязана отдавать кадры обеим.
    /// `FakeEvents` помнит только последнюю ссылку (так проверяются переоткрытия), а здесь нужны
    /// ВСЕ сразу и видно, какая закрылась: по этому числу проверяется «телефон ушёл — поток закрыт».
    /// </summary>
    private sealed class ManyEvents
    {
        private readonly object _gate = new();
        private readonly List<Link> _links = new();

        public PhoneEventDoors Doors => new(Open, _ => { });

        /// <summary>Сколько потоков открыто ВСЕГО (калитка открывает свой первым, на `Start`).</summary>
        public int Count
        {
            get { lock (_gate) return _links.Count; }
        }

        public bool Alive(int index)
        {
            lock (_gate) return index >= 0 && index < _links.Count && _links[index].Alive;
        }

        public void Emit(string frame)
        {
            Action<string>[] subscribers;

            lock (_gate)
                subscribers = _links.Where(link => link.Alive).Select(link => link.OnFrame).ToArray();

            foreach (var subscriber in subscribers) subscriber(frame);
        }

        private IPhoneEventLink? Open(Action<string> onFrame)
        {
            lock (_gate)
            {
                var link = new Link(onFrame);
                _links.Add(link);

                return link;
            }
        }

        public sealed class Link : IPhoneEventLink
        {
            public Link(Action<string> onFrame) => OnFrame = onFrame;

            public Action<string> OnFrame { get; }

            public bool Alive { get; private set; } = true;

            public void Dispose() => Alive = false;
        }
    }

    /// <summary>
    /// ПОДСТАВНАЯ ДВЕРЬ ЖИВОГО ПОТОКА (S1): проверка подаёт кадры сама, как движок, и видит,
    /// какая сессия открыта и закрылся ли поток, когда телефон ушёл. Настоящего движка и сокета
    /// здесь нет — как и у подставной подписки на события.
    /// </summary>
    private sealed class FakeFollow
    {
        private Action<string>? _onFrame;

        public int Opens { get; private set; }

        public string Session { get; private set; } = string.Empty;

        public bool Closed { get; private set; }

        public PhoneFollowDoors Doors => new(Open, _ => { }, () => true);

        public void Emit(string frame) => _onFrame?.Invoke(frame);

        private IPhoneFollowLink? Open(string sessionId, Action<string> onFrame)
        {
            Opens++;
            Session = sessionId;
            _onFrame = onFrame;
            Closed = false;

            return new Link(this);
        }

        private sealed class Link : IPhoneFollowLink
        {
            private readonly FakeFollow _owner;

            public Link(FakeFollow owner) => _owner = owner;

            public bool Alive => !_owner.Closed;

            public void Dispose() => _owner.Closed = true;
        }
    }

    /// <summary>Ответ калитки: код, тело, заголовок Set-Cookie и место перенаправления (если было).</summary>
    private sealed record Reply(int Status, string Body, string? Cookie, string? Location = null);

    /// <summary>
    /// СТЕНД: калитка на свободном порту петли со всеми подставными дверями. Всё, что она говорит
    /// и делает, остаётся здесь — наружу не уходит ни один байт.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        private readonly string _root;

        public Stand(
            string root,
            bool enabled,
            bool human,
            bool isolated,
            string? address,
            int? port,
            PhoneEventDoors events,
            PhonePassFiles? passFiles = null,
            Func<IServerControl?>? server = null,
            PhoneFollowDoors? follow = null,
            PhoneControlDoors? control = null,
            PhoneWorkspaceDoors? workspace = null)
        {
            _root = root;
            AddressValue = address ?? string.Empty;
            Paths = AppPaths.Under(root);
            Port = port ?? FreeLoopbackPort();
            EventDoors = events;

            Doors = new PhoneGateDoors(
                () => AddressValue,
                Port,
                Engine,
                passFiles ?? PhonePassFiles.Under(Paths, Log.Add),
                EventDoors,
                () => Now,
                Log.Add,
                Bound: value => BoundTo = value,
                Server: server,
                Follow: follow,
                Control: control,
                Workspace: workspace);

            Gate = new PhoneGate(Doors, () => enabled, human, isolated ? Isolated() : Owner());
            Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + Port + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public AppPaths Paths { get; }

        public int Port { get; }

        /// <summary>
        /// АДРЕС, КОТОРЫЙ ПРОЧИТАЕТ ДВЕРЬ при следующем запуске. Не «настройка стенда», а та же
        /// дверь, что у раздела «Телефон»: человек меняет адрес и сохраняет — и калитка читает его
        /// заново. Изменяемое поле нужно проверке, которая изображает этот самый шаг.
        /// </summary>
        public string AddressValue { get; set; }

        public PhoneGateDoors Doors { get; }

        public PhoneEventDoors EventDoors { get; }

        public PhoneGate Gate { get; }

        public HttpClient Http { get; }

        public List<EngineCall> Calls { get; } = new();

        /// <summary>Ответы ПО ИМЕНИ вызова: у дверей S3 за один запрос бывает два вызова (§3.2).</summary>
        public Dictionary<string, PhoneEngineAnswer> Replies { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// ЗАДЕРЖКА ДВИЖКА. Нажата (Reset) — вызов висит, пока проверка не отпустит: только так
        /// можно поймать ГОНКУ двух запросов, не полагаясь на удачу (DEF-072).
        /// </summary>
        public ManualResetEventSlim Hold { get; } = new(true);

        /// <summary>Снимок вызовов: двери приходят из разных ниток, список надо читать под замком.</summary>
        public EngineCall[] CallsNow()
        {
            lock (_callsGate) return Calls.ToArray();
        }

        private readonly object _callsGate = new();

        public List<string> Log { get; } = new();

        /// <summary>АДРЕС, НА КОТОРОМ ВСТАЛА КАЛИТКА: его сообщает она сама обратной дверью.</summary>
        public string? BoundTo { get; private set; }

        /// <summary>Что отвечает подставной движок. Проверка меняет это перед вызовом.</summary>
        public PhoneEngineAnswer Reply { get; set; } =
            new(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}");

        /// <summary>Часы стенда: ими управляет проверка, а не календарь.</summary>
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public string LastLog => Log.Count == 0 ? string.Empty : Log[^1];

        private PhoneEngineAnswer Engine(string endpoint, string args)
        {
            lock (_callsGate) Calls.Add(new EngineCall(endpoint, args));

            Hold.Wait(TimeSpan.FromSeconds(10));

            return Replies.TryGetValue(endpoint, out var answer) ? answer : Reply;
        }

        public void Dispose()
        {
            Gate.Dispose();
            Http.Dispose();
            PanelTestStand.RemoveTemp(_root);
        }
    }

    private static Stand NewStand(
        bool enabled = true,
        bool human = true,
        bool isolated = false,
        string? address = "127.0.0.1",
        int? port = null,
        PhoneEventDoors? events = null,
        PhonePassFiles? passFiles = null,
        Func<IServerControl?>? server = null,
        PhoneFollowDoors? follow = null,
        PhoneControlDoors? control = null,
        PhoneWorkspaceDoors? workspace = null) =>
        new(
            PanelTestStand.TempDir(), enabled, human, isolated, address, port,
            events ?? new FakeEvents().Doors, passFiles, server, follow, control, workspace);

    private static RunContext Owner() => RunContext.Create(RunRequest.Owner());

    private static RunContext Isolated() =>
        RunContext.Create(RunRequest.Parse(
            new[] { RunRequest.RootSwitchArgument, PanelTestStand.TempDir() }, _ => null));

    // ---- мелкие двери проверки --------------------------------------------------------------

    /// <summary>
    /// СВОБОДНЫЙ ПОРТ ПЕТЛИ: система выдаёт его связыванием на <c>0</c> — ровно так же, как это
    /// делает сам продукт (<c>FreePort</c>). Ни <c>43127</c>, ни <c>3080</c>, ни <c>3081</c>
    /// в проверках не занимаются, и это проверяется отдельной проверкой ниже.
    /// </summary>
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

    /// <summary>Связывается ли порт прямо сейчас. Гонка с освобождением порта гасится повтором:
    /// после закрытия слушателя системе нужно мгновение (замерено поведением Windows).</summary>
    private static bool CanBind(int port)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);

            try
            {
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                System.Threading.Thread.Sleep(100);
            }
            finally
            {
                listener.Stop();
            }
        }

        return false;
    }

    /// <summary>
    /// Послать калитке запрос. <paramref name="cookie"/> — ЗНАЧЕНИЕ нашей cookie (то, что лежит
    /// в <c>Set-Cookie</c> после `dsh-phone=`): заголовок собирается здесь, чтобы проверки не
    /// повторяли его форму по-своему.
    ///
    /// <paramref name="host"/> и <paramref name="origin"/> — СОВРАТЬ ПРО ДВЕРЬ (B-03): по умолчанию
    /// `HttpClient` ставит `Host` из адреса подключения и `Origin` не шлёт вовсе — ровно так ходит
    /// настоящий телефон. Доводы нужны проверкам, которые изображают подмену имени и чужую страницу.
    /// </summary>
    private static async Task<Reply> Send(
        HttpClient http,
        HttpMethod method,
        string path,
        string? json = null,
        string? cookie = null,
        string? language = null,
        int? bodyBytes = null,
        string? host = null,
        string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (json is not null || bodyBytes is not null)
        {
            var body = bodyBytes is null ? json! : new string('a', bodyBytes.Value);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", PhoneGate.CookieName + "=" + cookie);

        if (language is not null) request.Headers.TryAddWithoutValidation("Accept-Language", language);

        if (host is not null) request.Headers.Host = host;

        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values)
            : null;

        var location = response.Headers.TryGetValues("Location", out var places)
            ? string.Join("; ", places)
            : null;

        return new Reply((int)response.StatusCode, text, setCookie, location);
    }

    /// <summary>
    /// СЫРОЙ ЗАПРОС — там, где `HttpClient` не даёт соврать: `Host` из адреса он ставит всегда,
    /// а второй такой заголовок не пошлёт. Возвращает код из первой строки ответа
    /// (<c>0</c> — ответа не было вовсе).
    /// </summary>
    private static async Task<int> Raw(Stand stand, string head) => StatusOf(await RawResponse(stand, head));

    /// <summary>
    /// СЫРОЙ ОТВЕТ ЦЕЛИКОМ — голова как она пришла по проводу, до пустой строки.
    ///
    /// ⚠️ **Именно по проводу, а не через `HttpClient`:** тот РАЗБИРАЕТ `Cache-Control` в своё
    /// свойство и УБИРАЕТ строку из набора заголовков — на нём «заголовок есть» не докажешь
    /// (проверено: `TryGetValues("Cache-Control")` на разобранном ответе пуст).
    /// </summary>
    private static async Task<string> RawResponse(Stand stand, string head)
    {
        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, stand.Port, TestContext.Current.CancellationToken);
        using var stream = client.GetStream();
        stream.ReadTimeout = 20000;
        stream.WriteTimeout = 20000;

        var bytes = Encoding.UTF8.GetBytes(head);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        return await ReadHead(stream);
    }

    /// <summary>Голова ответа целиком: читаем до пустой строки (тело для головы не нужно).</summary>
    private static async Task<string> ReadHead(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();

        while (text.Length < 262144)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) break;

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));

            if (text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        return text.ToString();
    }

    /// <summary>
    /// ПОСЛАТЬ БАЙТЫ, КОТОРЫЕ HTTP-ЗАПРОСОМ НЕ ЯВЛЯЮТСЯ, и дождаться, чем кончится соединение:
    /// сколько байт калитка прислала в ответ и через сколько закрыла. Обрыв (`RST`) — тоже
    /// закрытие: байт в ответ всё равно ноль, и это тот же счёт.
    /// </summary>
    private static async Task<(int Bytes, TimeSpan Elapsed)> NonHttpAnswer(Stand stand, byte[] bytes)
    {
        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, stand.Port, TestContext.Current.CancellationToken);
        using var stream = client.GetStream();
        stream.ReadTimeout = 20000;
        stream.WriteTimeout = 20000;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var buffer = new byte[4096];
        var received = 0;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;

                received += read;
            }
        }
        catch (IOException)
        {
            // RST — закрытие, а не ответ: байт в ответ всё равно ноль.
        }

        clock.Stop();

        return (received, clock.Elapsed);
    }

    /// <summary>Код ответа из первой строки головы (<c>0</c> — ответа не было вовсе).</summary>
    private static int StatusOf(string head)
    {
        var first = head.Split("\r\n")[0].Split(' ');

        return first.Length >= 2 && int.TryParse(first[1], out var status) ? status : 0;
    }

    /// <summary>Запрос, собранный руками: метод, путь, своя дверь, cookie и тело по надобности.</summary>
    private static string RawRequest(
        string method, string path, string host, string cookie = "", string body = "", long? contentLength = null)
    {
        var request = method + " " + path + " HTTP/1.1\r\nHost: " + host + "\r\n";

        if (cookie.Length > 0) request += "Cookie: " + PhoneGate.CookieName + "=" + cookie + "\r\n";

        var length = contentLength ?? (body.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(body));

        if (length > 0)
        {
            request += "Content-Type: "
                + (body.StartsWith('{') ? "application/json" : "application/x-www-form-urlencoded") + "\r\n";
            request += "Content-Length: " + length + "\r\n";
        }

        return request + "\r\n" + body;
    }

    /// <summary>Сопрячь телефон по-настоящему: код, страница, cookie. Возвращает значение cookie.</summary>
    private static async Task<string> Pair(Stand stand)
    {
        var code = stand.Gate.IssueCode();
        var reply = await Send(
            stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}");

        Assert.Equal(200, reply.Status);
        Assert.NotNull(reply.Cookie);

        return PhoneGate.CookieValueOf(reply.Cookie!);
    }

    private static bool Started(Stand stand) => stand.Gate.Start().Opened;

    // ---- право: калитка открывается ТОЛЬКО им ------------------------------------------------

    /// <summary>
    /// БЕЗ ПРАВА КАЛИТКА НЕ ОТКРЫВАЕТСЯ — и слушателя не поднимает вовсе. Прогон проверки
    /// (<c>humanLaunch: false</c>) — это ровно тот случай, ради которого право и заведено:
    /// прогон живёт на машине владельца, и открытый им порт торчал бы в его сеть.
    /// </summary>
    [Fact]
    public void Без_права_калитка_не_открывается_и_порт_не_занимается()
    {
        using var stand = NewStand(human: false);

        var start = stand.Gate.Start();

        Assert.False(start.Opened);
        Assert.Equal(PanelStrings.PhoneGateNoRightLog, start.ReasonKey);
        Assert.False(string.IsNullOrWhiteSpace(start.Text), "отказ обязан быть словами, а не молчанием");
        Assert.False(stand.Gate.Listening);

        // Порт СВОБОДЕН: калитка его не заняла. Не «состояние говорит, что не занят», а связывание.
        Assert.True(CanBind(stand.Port), $"порт {stand.Port} занят, хотя права не было");
    }

    /// <summary>ВЫКЛЮЧЕННЫЙ ТЕЛЕФОН — отказ даже у обычного запуска человеком: «человек запустил
    /// панель» и «человек разрешил телефон» — разные вещи.</summary>
    [Fact]
    public void Выключенный_телефон_не_открывает_калитку()
    {
        using var stand = NewStand(enabled: false);

        var start = stand.Gate.Start();

        Assert.False(start.Opened);
        Assert.Equal(PanelStrings.PhoneGateNoRightLog, start.ReasonKey);
        Assert.False(stand.Gate.Listening);
    }

    /// <summary>ИЗОЛИРОВАННЫЙ ПРОГОН — тоже отказ: там свой корень, и наружу он не выставляет
    /// ничего ни при каких настройках.</summary>
    [Fact]
    public void Изолированный_прогон_не_открывает_калитку()
    {
        using var stand = NewStand(isolated: true, enabled: true, human: true);

        var start = stand.Gate.Start();

        Assert.False(start.Opened);
        Assert.Equal(PanelStrings.PhoneGateNoRightLog, start.ReasonKey);
    }

    // ---- порт ------------------------------------------------------------------------------

    /// <summary>
    /// ПОРТ ВЛАДЕЛЬЦА И ЗАПАСНОЙ ПОРТ ПАНЕЛИ КАЛИТКА НЕ ЗАНИМАЕТ НИКОГДА — и проверяется это
    /// РЕШЕНИЕМ, а не попыткой связывания: связать <c>3080</c> в проверке значило бы оборвать
    /// канал этой самой сессии, то есть чинить беду её же причиной.
    /// </summary>
    [Fact]
    public void Порт_сервера_панели_калитка_не_занимает()
    {
        Assert.False(PhoneGate.PortAllowed(ServerDecisions.OwnerPort));
        Assert.False(PhoneGate.PortAllowed(ServerDecisions.RunFallbackPort));

        Assert.True(PhoneGate.PortForbidden(ServerDecisions.OwnerPort));
        Assert.True(PhoneGate.PortForbidden(ServerDecisions.RunFallbackPort));
        Assert.False(PhoneGate.PortForbidden(PhonePass.DefaultPort));

        // Отказ по такому порту — СЛОВАМИ и до всякого связывания.
        using var stand = NewStand(port: ServerDecisions.RunFallbackPort);

        var start = stand.Gate.Start();

        Assert.False(start.Opened);
        Assert.Equal(PanelStrings.PhoneGatePortServerLogFormat, start.ReasonKey);
        Assert.False(stand.Gate.Listening);
    }

    /// <summary>ПОРТ <c>43127</c> В ТЕСТАХ НЕ ЗАНИМАЕТСЯ: он умолчание ПРОДУКТА, и проверка не
    /// только его не трогает, но и убеждается, что выбранный ею порт — другой.</summary>
    [Fact]
    public void Умолчание_43127_и_тестовый_порт_другой()
    {
        Assert.Equal(43127, PhonePass.DefaultPort);

        // Умолчание продукта калитке ГОДИТСЯ (это её порт), а тестовый порт — не оно.
        Assert.True(PhoneGate.PortAllowed(PhonePass.DefaultPort));

        var ports = Enumerable.Range(0, 5).Select(_ => FreeLoopbackPort()).ToList();

        Assert.DoesNotContain(PhonePass.DefaultPort, ports);
        Assert.DoesNotContain(ServerDecisions.OwnerPort, ports);
        Assert.DoesNotContain(ServerDecisions.RunFallbackPort, ports);
    }

    /// <summary>
    /// ЗАНЯТЫЙ ПОРТ — ОТКАЗ СЛОВАМИ, И МОЛЧАЛИВОГО ПЕРЕЕЗДА НЕТ. Проверка занимает порт САМА
    /// (своим слушателем петли) и требует, чтобы калитка не «взяла другой», а сказала причину.
    /// </summary>
    [Fact]
    public void Занятый_порт_отказ_словами_без_переезда()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();

        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;

            using var stand = NewStand(port: port);

            var start = stand.Gate.Start();

            Assert.False(start.Opened);
            Assert.Equal(PanelStrings.PhoneGatePortBusyFormat, start.ReasonKey);
            Assert.Contains(port.ToString(), start.Text, StringComparison.Ordinal);
            Assert.False(stand.Gate.Listening);
        }
        finally
        {
            busy.Stop();
        }
    }

    /// <summary>АДРЕСА НЕТ — ОТКАЗ СЛОВАМИ: наружу калитка не выставляется, и «взять хоть что-нибудь»
    /// здесь не бывает.</summary>
    [Fact]
    public void Без_адреса_калитка_не_открывается()
    {
        using var stand = NewStand(address: string.Empty);

        var start = stand.Gate.Start();

        Assert.False(start.Opened);
        Assert.Equal(PanelStrings.PhoneGateNoAddressLog, start.ReasonKey);
    }

    /// <summary>
    /// ВСЕСЕТЕВОЙ АДРЕС НЕ БЕРЁТСЯ НИКОГДА (B-04 плана 2.2.0).
    ///
    /// **От какого случая.** Калитка обещала это КОММЕНТАРИЕМ («0.0.0.0 здесь не берётся ни при
    /// каких обстоятельствах»), а код проверял только «разбирается ли строка в адрес» — то есть
    /// обещание держалось на слове. `0.0.0.0` и `::` значат «все адреса сразу»: с ними калитка
    /// открылась бы не человеку в его сети, а любому, кто до машины дотянется.
    ///
    /// Мутации, которые обязаны упасть здесь: **«убрать проверку»** (калитка встанет на `0.0.0.0`)
    /// и **«проверять только `0.0.0.0`»** (встанет на `::`).
    /// </summary>
    [Fact]
    public void Всесетевой_адрес_не_берётся()
    {
        foreach (var address in new[] { "0.0.0.0", "::", "0:0:0:0:0:0:0:0" })
        {
            using var stand = NewStand(address: address);

            var start = stand.Gate.Start();

            Assert.False(start.Opened, $"калитка встала на всесетевой адрес {address}");
            Assert.Equal(PanelStrings.PhoneGateAnyAddressLogFormat, start.ReasonKey);
            Assert.Contains(address, start.Text, StringComparison.Ordinal);
            Assert.False(stand.Gate.Listening);

            // И ПОРТ СВОБОДЕН: отказ — не «попробовала и не вышло», а «не пробовала вовсе».
            Assert.True(CanBind(stand.Port), $"порт {stand.Port} занят, хотя адрес всесетевой");
        }

        // Само решение тоже проверяется отдельно: оно о безопасности, а не о связывании.
        Assert.True(PhoneGate.WildcardAddress(System.Net.IPAddress.Parse("0.0.0.0")));
        Assert.True(PhoneGate.WildcardAddress(System.Net.IPAddress.Parse("::")));
        Assert.False(PhoneGate.WildcardAddress(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.False(PhoneGate.WildcardAddress(System.Net.IPAddress.Parse("192.168.1.10")));
        Assert.False(PhoneGate.WildcardAddress(null));
    }

    /// <summary>
    /// СПИСОК РАЗГОВОРОВ ЧИСТИТСЯ ОТ ПОДЧИНЁННЫХ СЕССИЙ, и об этом сказано ЧИСЛОМ в журнале
    /// (блок D плана 2.2.0, решение капитана; замер `l2`: 179 из 197 подчинённых, `session/page`
    /// по ним отвечает «subagent Sessions require their durable parent address»).
    ///
    /// ⚠️ Проверка идёт ЖИВЫМ ПУТЁМ калитки: тот же белый список, тот же пропуск устройства, тот же
    /// ответ — меняется только тело, которое отдаёт подставной движок.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть ответ движка как есть»** и
    /// **«фильтровать молча»** (без строки в журнале).
    /// </summary>
    [Fact]
    public async Task Подчинённые_сессии_не_показываются_телефону()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(
            200,
            "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":{\"items\":["
            + "{\"sessionId\":\"s-1\",\"blank\":false},"
            + "{\"sessionId\":\"s-2\",\"parentSessionId\":\"s-1\",\"blank\":false},"
            + "{\"sessionId\":\"s-3\",\"origin\":\"subagent\",\"blank\":false}]}}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"s-1\"", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"s-2\"", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"s-3\"", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("parentSessionId", reply.Body, StringComparison.Ordinal);

        // НЕ ПРЯЧЕМ МОЛЧА: число убрано и сказано.
        Assert.Contains(
            stand.Log,
            line => line.Contains("2", StringComparison.Ordinal) && line.Contains("3", StringComparison.Ordinal)
                && line.Contains("подчинённые сессии", StringComparison.OrdinalIgnoreCase));

        // И ДОГОН БОЛЬШЕ НЕ ОТДАЁТ ТЕЛО ДВИЖКА: записи пересобираются по белому списку,
        // а `system/message` (системный запрос с путями) в ответ не попадает вовсе (§0.5).
        stand.Reply = new PhoneEngineAnswer(
            200,
            "{\"result\":{\"ok\":true,\"value\":{\"records\":["
            + "{\"type\":\"event\",\"event\":{\"type\":\"system/message\",\"seq\":1,\"time\":1,"
            + "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"системный запрос\"}]}}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"user/message\",\"seq\":2,\"time\":2,"
            + "\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"привет\"}],\"source\":{\"kind\":\"user\"}}}}"
            + "]}}}");

        var other = await Send(
            stand.Http, HttpMethod.Post, "/api/session/page",
            "{\"args\":{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":\"s-1\"},\"throughSeq\":2}}}",
            cookie);

        Assert.Equal(200, other.Status);
        Assert.Contains("\"type\":\"user\"", other.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("system/message", other.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("системный запрос", other.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// КОРЕНЬ «РАБОТАЕТ», ПОКА РАБОТАЕТ ЕГО ПОДЧИНЁННЫЙ (слово владельца 11.11.2026: «Мне нравится
    /// работает пока работают подчиненные»). Прямой вызов subagent держит ход — поэтому раньше у
    /// корня горел спиннер; после перехода на команду корневая строка сама `running=false`, работа
    /// идёт в подчинённых, и телефон обязан видеть «Работает», а не зелёную галочку «Закончила».
    ///
    /// ⚠️ Подчинённая строка стоит в ответе ПЕРВОЙ: движок сортирует по `updatedAt`, и корень может
    /// прийти после неё. Поэтому проверка сторожит и порядок: агрегация — ДО сборки строк.
    ///
    /// Мутация, обязанная упасть здесь: **снять агрегацию** (running корня = только свой флаг) —
    /// красная именно `Корень_работает_пока_работает_подчинённая`.
    /// </summary>
    [Fact]
    public async Task Корень_работает_пока_работает_подчинённая()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        static bool Running(Reply reply, string id)
        {
            using var document = JsonDocument.Parse(reply.Body);

            foreach (var row in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (string.Equals(row.GetProperty("sessionId").GetString(), id, StringComparison.Ordinal))
                    return row.GetProperty("running").GetBoolean();
            }

            return false;
        }

        stand.Reply = new PhoneEngineAnswer(
            200,
            "{\"result\":{\"ok\":true,\"value\":{\"items\":["
            + "{\"sessionId\":\"s-sub\",\"parentSessionId\":\"s-root\",\"running\":true,\"blank\":false},"
            + "{\"sessionId\":\"s-root\",\"running\":false,\"blank\":false}]}}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"sessionId\":\"s-root\"", reply.Body, StringComparison.Ordinal);
        Assert.True(Running(reply, "s-root"), "корень работает, пока работает подчинённая");
        Assert.DoesNotContain("s-sub", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("parentSessionId", reply.Body, StringComparison.Ordinal);
        Assert.Contains(stand.Log, line => line.Contains("подчинённые сессии", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ОБРАТНЫЕ СЛУЧАИ АГРЕГАЦИИ: работающая подчинённая делает корень работающим ТОЛЬКО своего
    /// родителя и только когда она правда работает; отброшенные строки (без `parentSessionId`)
    /// ни на кого не влияют, а свой `running=true` остаётся работой (ИЛИ, а не замена).
    ///
    /// Проверка сторожит и «не утекло»: подчинённых строк в ответе нет, `total` считает только
    /// корни (счёт скрытых — как раньше).
    /// </summary>
    [Fact]
    public async Task Подчинённые_не_делают_корень_работающим_без_работы()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        static bool Running(Reply reply, string id)
        {
            using var document = JsonDocument.Parse(reply.Body);

            foreach (var row in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (string.Equals(row.GetProperty("sessionId").GetString(), id, StringComparison.Ordinal))
                    return row.GetProperty("running").GetBoolean();
            }

            return false;
        }

        static int Total(Reply reply)
        {
            using var document = JsonDocument.Parse(reply.Body);
            return document.RootElement.GetProperty("total").GetInt32();
        }
        // (а) подчинённая ЕСТЬ, но не работает — корень «Закончила».
        stand.Reply = new PhoneEngineAnswer(200, Items(
            "{\"sessionId\":\"s-sub\",\"parentSessionId\":\"s-root\",\"running\":false,\"blank\":false}",
            "{\"sessionId\":\"s-root\",\"running\":false,\"blank\":false}"));
        var sleeping = await Send(stand.Http, HttpMethod.Post, "/api/session/list", Args, cookie);
        Assert.False(Running(sleeping, "s-root"), "отдыхающая подчинённая не делает корень работающим");
        Assert.DoesNotContain("s-sub", sleeping.Body, StringComparison.Ordinal);
        Assert.Equal(1, Total(sleeping));

        // (б) работает подчинённая ЧУЖОГО корня — s-root не работает.
        stand.Reply = new PhoneEngineAnswer(200, Items(
            "{\"sessionId\":\"s-other-sub\",\"parentSessionId\":\"s-other\",\"running\":true,\"blank\":false}",
            "{\"sessionId\":\"s-root\",\"running\":false,\"blank\":false}"));
        var foreign = await Send(stand.Http, HttpMethod.Post, "/api/session/list", Args, cookie);
        Assert.False(Running(foreign, "s-root"), "чужая работающая подчинённая на корень не влияет");
        Assert.DoesNotContain("s-other-sub", foreign.Body, StringComparison.Ordinal);
        Assert.Equal(1, Total(foreign));

        // (в) подчинённая без parentSessionId (только origin) — отброшена, как раньше; корень не тронут.
        stand.Reply = new PhoneEngineAnswer(200, Items(
            "{\"sessionId\":\"s-orphan\",\"origin\":\"subagent\",\"running\":true,\"blank\":false}",
            "{\"sessionId\":\"s-root\",\"running\":false,\"blank\":false}"));
        var orphan = await Send(stand.Http, HttpMethod.Post, "/api/session/list", Args, cookie);
        Assert.False(Running(orphan, "s-root"), "подчинённая без родителя ни на кого не влияет");
        Assert.DoesNotContain("s-orphan", orphan.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("parentSessionId", orphan.Body, StringComparison.Ordinal);
        Assert.Equal(1, Total(orphan));

        // (г) свой running=true остаётся работой: агрегация — ИЛИ, а не замена.
        stand.Reply = new PhoneEngineAnswer(200, Items(
            "{\"sessionId\":\"s-root\",\"running\":true,\"blank\":false}"));
        var own = await Send(stand.Http, HttpMethod.Post, "/api/session/list", Args, cookie);
        Assert.True(Running(own, "s-root"), "свой ход — по-прежнему работа");
        Assert.Equal(1, Total(own));
    }

    /// <summary>Ответ движка со списком строк — чтобы кейсы агрегации читались одним глазом.</summary>
    private static string Items(params string[] rows) =>
        "{\"result\":{\"ok\":true,\"value\":{\"items\":[" + string.Join(",", rows) + "]}}}";

    /// <summary>Тело запроса двери списка — одно и то же во всех кейсах агрегации.</summary>
    private const string Args = "{\"args\":{\"_request\":{}}}";

    /// <summary>
    /// ФОРМА ОТВЕТА НЕ ТА, КАКУЮ МЫ МЕРИЛИ, — СПИСКА НЕТ, И ЭТО СКАЗАНО СЛОВАМИ И В ЖУРНАЛЕ.
    ///
    /// **Почему больше не «уходит как есть» (правка t4 по слову капитана).** Прежнее правило
    /// («не переписывать незнакомую форму») отдавало телефону ответ движка БАЙТ В БАЙТ — то есть
    /// ровно тот путь, которым уезжали `cwd` и `workingDirectory` (§0.4 контракта). Утечка путей
    /// важнее прежнего закрепления: списка нет — говорим «не прочитался», а причина идёт в журнал.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть ответ движка как есть»** (тело с `sessions`
    /// окажется у телефона) и **«молчать о форме»** (без строки в журнале).
    /// </summary>
    [Fact]
    public async Task Незнакомая_форма_списка_не_отдаётся_телефону()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"ok\":true,\"sessions\":[{\"id\":1}]}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(502, reply.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebListUnreadable), reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sessions\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains(stand.Log, line => line.Contains("не той формы", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ОТВЕТ, КОТОРЫЙ ДВИЖОК НЕ ПРИНЯЛ, НЕ ВЫДАЁТСЯ ЗА ОТПРАВЛЕННЫЙ (блок D плана 2.2.0).
    ///
    /// **От какого замера (`l1`).** Старая дверь (`$events/result`) принимает ответ на обеих версиях
    /// (`200 {ok:true}`), а `userQuestions/*` отвечают `200 {ok:true, value:false}` — «не принял».
    /// Риск назван в задаче: если апстрим переключит профиль на `mode: timed`, старая дверь начнёт
    /// отвечать молча, и без этой проверки человек прочитал бы «ответ отправлен» там, где вопрос висит.
    ///
    /// Мутации, которые обязаны упасть здесь: **«считать принятым всё, где нет кода ошибки»** и
    /// **«снимать вопрос, даже когда ответ не принят»**.
    /// </summary>
    [Fact]
    public async Task Непринятый_ответ_не_выдаётся_за_отправленный()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        stand.Reply = new PhoneEngineAnswer(
            200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":false}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        // Это НЕ «ответ отправлен»: код отказа и слова, а не 200.
        Assert.NotEqual(200, reply.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateAnswerNotTaken), reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhoneGateAnswerSent), reply.Body, StringComparison.Ordinal);

        // ВОПРОС ОСТАЛСЯ: телефон, открыв страницу заново, обязан увидеть тот же вопрос, а не пустоту.
        var page = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Equal(200, page.Status);
        Assert.Contains("\"state\":\"question\"", page.Body, StringComparison.Ordinal);
    }

    /// <summary>ПОСЛЕ ОСТАНОВКИ ПОРТ СВОБОДЕН: «остановилась, а порт держит» выглядело бы как
    /// «калитка не остановилась», и человек не смог бы её поднять заново.</summary>
    [Fact]
    public async Task После_остановки_порт_освобождается()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var reply = await Send(stand.Http, HttpMethod.Get, "/ask");
        Assert.Equal(200, reply.Status);

        stand.Gate.Stop();

        Assert.False(stand.Gate.Listening);
        Assert.True(CanBind(stand.Port), $"порт {stand.Port} не освободился после остановки калитки");
        Assert.Contains(stand.Log, line => line.Contains("закрыто", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ПОВТОРНЫЙ START НЕ ЛОМАЕТ ОТКРЫТУЮ ДВЕРЬ (DEF-069).
    ///
    /// **От какого случая (живой 2.4.4).** В журнале: `23:54:20 … открыто на …:43127`, а через
    /// пять секунд `порт 43127 занят` — и после этого ЛЮБОЙ `Host` получал 403 (`.71`, `localhost`,
    /// `127.0.0.1`, соседние адреса — все). `Start` во второй раз связывал тот же порт, ловил
    /// `SocketException` и обнулял `_own`, хотя прежний слушатель был ЖИВ. Страница телефона не
    /// открывалась; лечил только перезапуск панели.
    ///
    /// **Что требуется.** Второй `Start` при живой двери: `Opened = true`, ровно ОДНА подписка
    /// (второго `accept`-цикла быть не может — порт один), имя двери цело — и это доказывается
    /// ЖИВЫМ запросом `GET /` → 200, а не 403.
    ///
    /// Мутации, которые обязаны упасть здесь: **«обнулить `_own` на занятом порту»** (старое тело
    /// `catch` — тогда второй `Start` говорит «занято», а `GET /` отвечает 403) и **«завести на
    /// повтор вторую подписку»** (число открытий подписки станет 2).
    /// </summary>
    [Fact]
    public async Task Повторный_Start_идемпотентен_и_дверь_не_теряется()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);

        Assert.True(Started(stand));
        Assert.Equal(1, events.Opens);

        // ПЕРВЫЙ ОТВЕТ — чтобы второй сравнивался не с пустотой, а с работающей дверью.
        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Get, "/ask")).Status);

        // ВТОРОЙ Start — ровно то, что делает раздел «Телефон» при сохранении настроек.
        var again = stand.Gate.Start();

        Assert.True(again.Opened, "повторный Start при живой двери сказал «не открыто»: " + again.Text);
        Assert.Contains(stand.Port.ToString(), again.Text, StringComparison.Ordinal);

        // ДВЕРЬ ЦЕЛА — и это проверяется ЖИВЫМ запросом, а не полем: сверка двери берёт `_own`,
        // и обнулённое имя двери отвечает 403 на верном адресе (ровно дефект 2.4.4).
        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Get, "/ask")).Status);
        Assert.True(stand.Gate.Listening);

        // НИ ВТОРОЙ ПОДПИСКИ, НИ ВТОРОГО СЛУШАТЕЛЯ: повтор ничего не заводит.
        Assert.Equal(1, events.Opens);

        stand.Gate.Stop();

        Assert.False(stand.Gate.Listening);
        Assert.True(CanBind(stand.Port), $"порт {stand.Port} не освободился: повтор завёл вторую дверь");
    }

    /// <summary>
    /// СМЕНА АДРЕСА ПРИ ЖИВОЙ ДВЕРИ — ПЕРЕЕЗД, А НЕ ВТОРАЯ ДВЕРЬ (DEF-069).
    ///
    /// Раздел «Телефон» даёт сменить адрес и сразу зовёт `Start`. Связывать новый адрес ПОВЕРХ
    /// прежнего слушателя нельзя: прежний остался бы открытым — с чужим для него именем двери
    /// (403 своему же телефону), — а `Stop` погасил бы только новый. Поэтому дверь на другом
    /// адресе сначала закрывается, и открыта ровно одна — на том адресе, который человек видит.
    ///
    /// Мутации, которые обязаны упасть здесь: **«не закрывать прежнюю дверь при переезде»** (тогда
    /// `127.0.0.1` продолжает принимать — проверка последнего соединения краснеет), и **«считать
    /// переездом любой повтор»** — тогда повтор на том же адресе заведёт вторую подписку и уронит
    /// `Повторный_Start_идемпотентен_и_дверь_не_теряется`.
    /// </summary>
    [Fact]
    public async Task Смена_адреса_при_живой_двери_переоткрывает_её()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);

        Assert.True(Started(stand));
        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Get, "/ask")).Status);

        // ЧЕЛОВЕК СМЕНИЛ АДРЕС В РАЗДЕЛЕ И НАЖАЛ «СОХРАНИТЬ»: дверь читает адрес заново.
        stand.AddressValue = "127.0.0.2";

        var moved = stand.Gate.Start();

        Assert.True(moved.Opened, "дверь на новом адресе не открылась: " + moved.Text);
        Assert.Equal("127.0.0.2", stand.BoundTo);

        // НОВАЯ ДВЕРЬ ОТВЕЧАЕТ: своё имя двери — новый адрес.
        // `UseProxy = false` — чтобы проверка не зависела от прокси машины: 127.0.0.2 — петля,
        // но настроенный системный прокси мог бы увести запрос мимо калитки.
        using var movedDoor = new HttpClient(
            new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri("http://127.0.0.2:" + stand.Port + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };

        Assert.Equal(200, (await Send(movedDoor, HttpMethod.Get, "/ask")).Status);

        // ПРЕЖНЯЯ ДВЕРЬ ЗАКРЫТА, а не оставлена вторым слушателем: соединение не устанавливается.
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();

            await client.ConnectAsync(IPAddress.Loopback, stand.Port, TestContext.Current.CancellationToken);
        });
    }

    // ---- страница входа --------------------------------------------------------------------

    /// <summary>
    /// СТРАНИЦА ВХОДА ОТДАЁТСЯ И НЕ НЕСЁТ ЛИШНЕГО: объяснение и поле для шести цифр. Ни ссылок,
    /// ни ключей, ни путей — всё, чего на странице нет, не может утечь телефону.
    /// </summary>
    [Fact]
    public async Task Страница_входа_отдаётся_и_лишнего_не_несёт()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var reply = await Send(stand.Http, HttpMethod.Get, "/ask");

        Assert.Equal(200, reply.Status);
        Assert.Contains("id=\"code\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"6\"", reply.Body, StringComparison.Ordinal);

        // Ни одной ссылки наружу и ни одного пути: страница — дверь, а не карта машины.
        Assert.DoesNotContain("http://", reply.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", reply.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", reply.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// СТРАНИЦА ГОВОРИТ НА ТРЁХ ЯЗЫКАХ, и язык берётся У ТЕЛЕФОНА (заголовок `Accept-Language`),
    /// а не у панели: телефону показывать язык, выбранный на компьютере, — значит не показать
    /// ничего. Все три текста обязаны отличаться: «переведено» проверяется сравнением.
    /// </summary>
    [Fact]
    public async Task Страница_входа_говорит_на_трёх_языках()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var seen = new List<string>();

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var reply = await Send(stand.Http, HttpMethod.Get, "/ask", language: language);
            var intro = Loc.TIn(language, nameof(PanelStrings.PhonePageIntro));

            Assert.Equal(200, reply.Status);
            Assert.Contains(intro, reply.Body, StringComparison.Ordinal);
            seen.Add(reply.Body);
        }

        Assert.Equal(3, seen.Distinct(StringComparer.Ordinal).Count());

        // Язык панели (ru в прогоне проверок) — когда телефон ничего не просил.
        var plain = await Send(stand.Http, HttpMethod.Get, "/ask");
        Assert.Contains(Loc.TIn("ru", nameof(PanelStrings.PhonePageIntro)), plain.Body, StringComparison.Ordinal);
    }

    // ---- сопряжение ------------------------------------------------------------------------

    /// <summary>
    /// ВЕРНЫЙ КОД ПУСКАЕТ И СТАВИТ COOKIE. Cookie — НАША, а не движка, и в ней НЕ пропуск,
    /// а пара «устройство.подпись»: мастер-пропуск не покидает машину.
    ///
    /// ⚠️ `Secure` в cookie НЕ ставится — и это проверяется здесь же: панель работает в своей сети
    /// по HTTP без шифрования, и с `Secure` браузер телефона не отдал бы cookie ни разу.
    /// Ограничение названо в `docs\ROADMAP-BORROWED.md` (F3).
    /// </summary>
    [Fact]
    public async Task Верный_код_пускает_и_ставит_cookie()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var code = stand.Gate.IssueCode();

        Assert.True(stand.Gate.HasLiveCode());
        Assert.Equal(6, code.Length);
        Assert.All(code, symbol => Assert.True(char.IsDigit(symbol)));

        var reply = await Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}");

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"ok\":true", reply.Body, StringComparison.Ordinal);

        var cookie = reply.Cookie;
        Assert.NotNull(cookie);

        Assert.Contains(PhoneGate.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", cookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", cookie, StringComparison.Ordinal);

        // ⚠️ ИМЕННО `Lax`, А НЕ `Strict` — и это проверяется отдельно (правка 12.10.2026):
        // со `Strict` cookie не прикладывается к переходу из ДРУГОГО приложения, и вход по QR
        // каждый раз спрашивал код, плодя устройства. Вернётся `Strict` — проверка упадёт.
        Assert.DoesNotContain("SameSite=Strict", cookie, StringComparison.Ordinal);
        Assert.Contains("Path=/", cookie, StringComparison.Ordinal);
        Assert.Contains("Max-Age=" + (int)PhoneGate.CookieLifetime.TotalSeconds, cookie, StringComparison.Ordinal);

        // ⚠️ `Secure` НЕ ставится, и это названо словами в самом коде: HTTP в своей сети.
        Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase);

        // Cookie СОПРЯЖЁННОГО устройства проходит, и файл пропуска лежит под нашим корнем.
        var value = PhoneGate.CookieValueOf(cookie!);

        Assert.Contains(".", value, StringComparison.Ordinal);
        Assert.True(File.Exists(stand.Paths.PhonePassFile), "пропуск обязан лечь в свой файл");
        Assert.Equal(1, stand.Gate.DeviceCount);

        var question = await Send(stand.Http, HttpMethod.Get, "/question", cookie: value);
        Assert.Equal(200, question.Status);
    }

    /// <summary>
    /// ФОРМА ВХОДА ПУСКАЕТ БЕЗ СКРИПТА — ПЕРЕНАПРАВЛЕНИЕМ НА КОРЕНЬ, А БЕДА ПРИХОДИТ СТРАНИЦЕЙ.
    ///
    /// Живой путь F5 (решение владельца 11.10.2026): на странице входа БОЛЬШЕ НЕТ СКРИПТА — страница
    /// обязана работать в глухой домашней сети и на телефоне без JS, — поэтому сопряжение идёт
    /// ОБЫЧНОЙ формой <c>POST /pair</c> с полем <c>code</c>. И отвечать форме словами JSON нельзя:
    /// браузер показал бы человеку JSON вместо страницы.
    ///
    /// Мутации, которые обязаны упасть здесь: «отвечать форме JSON» (человек увидел бы
    /// <c>{"ok":false…}</c>) · «после верного кода не ставить cookie» (телефон остался бы на входе) ·
    /// «не перевести на корень» (F5 повторил бы отправку кода, а он одноразовый).
    /// </summary>
    [Fact]
    public async Task Вход_формой_пускает_перенаправлением_а_беда_приходит_страницей()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        // (1) ВЕРНЫЙ КОД — ОБЫЧНОЙ ФОРМОЙ, КАК ЕГО ШЛЁТ БРАУЗЕР.
        var code = stand.Gate.IssueCode();
        var paired = await Send(stand.Http, HttpMethod.Post, "/pair", "code=" + code);

        Assert.Equal(303, paired.Status);
        Assert.Equal("/", paired.Location);
        Assert.Empty(paired.Body);
        Assert.NotNull(paired.Cookie);
        Assert.Contains(PhoneGate.CookieName + "=", paired.Cookie, StringComparison.Ordinal);

        // И ЧЕЛОВЕК ПОПАДАЕТ НА СТРАНИЦУ ТЕЛЕФОНА, А НЕ СНОВА НА ВХОД.
        var value = PhoneGate.CookieValueOf(paired.Cookie!);
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: value);

        Assert.Equal(200, page.Status);
        Assert.DoesNotContain("id=\"code\"", page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageWaiting), page.Body, StringComparison.Ordinal);

        // (2) НЕВЕРНЫЙ КОД — СТРАНИЦЕЙ СО СЛОВАМИ, А НЕ JSON.
        var fresh = stand.Gate.IssueCode();
        var wrong = fresh == "000000" ? "111111" : "000000";
        var refused = await Send(stand.Http, HttpMethod.Post, "/pair", "code=" + wrong);

        Assert.Equal(403, refused.Status);
        Assert.Null(refused.Cookie);
        Assert.Contains("<!DOCTYPE html>", refused.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageError), refused.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateBadCode), refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ok\":", refused.Body, StringComparison.Ordinal);

        // ФОРМА НА МЕСТЕ: человек не остаётся на странице без поля, куда вводить код.
        Assert.Contains("action=\"/pair\"", refused.Body, StringComparison.Ordinal);
        Assert.Contains("id=\"code\"", refused.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВЕРИ К СОБЫТИЯМ НЕТ — НА ТЕЛЕФОНЕ ПРАВДА, А НЕ ОБЕЩАНИЕ (решение владельца 13.10.2026).
    ///
    /// **От какого случая.** Сервер, поднятый НЕ панелью, не оставляет ссылки входа: войти в него
    /// панель не может, и вопрос агента на телефон не придёт НИКОГДА. Прежде калитка в этом
    /// положении отдавала ожидание («панель на связи и ждёт вопроса от агента») — то есть обещала
    /// вопрос, которому неоткуда взяться. Теперь состояние своё, и в нём сказано, что смотреть
    /// надо на компьютер.
    ///
    /// ⚠️ Проверяется ЖИВЫМ ПУТЁМ КАЛИТКИ, а не одной записью состояния: решает здесь калитка
    /// (`View()`), и «признак есть, а страница всё равно врёт» — ровно та беда, которую так
    /// не поймать.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть ожидание, когда двери событий нет» ·
    /// «считать двери событий существующими всегда» (тогда калитка снова открывала бы подписку
    /// в никуда) · «убрать слова о компьютере со страницы».
    /// </summary>
    [Fact]
    public async Task Без_двери_событий_телефон_видит_правду_а_не_ожидание()
    {
        using var stand = NewStand(events: PhoneEventDoors.None);
        Assert.True(Started(stand));

        var code = stand.Gate.IssueCode();
        var paired = await Send(stand.Http, HttpMethod.Post, "/pair", "code=" + code);

        Assert.Equal(303, paired.Status);

        var value = PhoneGate.CookieValueOf(paired.Cookie!);
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: value);

        Assert.Equal(200, page.Status);

        Assert.Contains(Loc.T(PanelStrings.PhonePageNoEngine), page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageNoEngineNext), page.Body, StringComparison.Ordinal);

        // И НИ ОДНОГО СЛОВА ОЖИДАНИЯ: оно здесь — обещание, которого не будет.
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageWaiting), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageWaitingNext), page.Body, StringComparison.Ordinal);

        // КАРТОЧКА ОПРОСА — ТО ЖЕ САМОЕ: связь появится, и карточка сменится сама.
        var card = await Send(stand.Http, HttpMethod.Get, "/ask?part=card", cookie: value);

        Assert.Equal(200, card.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageNoEngine), card.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// КОД УМИРАЕТ ПОСЛЕ ПЕРВОГО УСПЕХА: повтор того же кода не пускает. Это не придирка —
    /// код диктуют вслух с экрана, и подсмотренный код не должен годиться второй раз.
    /// </summary>
    [Fact]
    public async Task Повтор_того_же_кода_не_пускает()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var code = stand.Gate.IssueCode();
        var body = "{\"code\":\"" + code + "\"}";

        var first = await Send(stand.Http, HttpMethod.Post, "/pair", body);
        Assert.Equal(200, first.Status);

        var second = await Send(stand.Http, HttpMethod.Post, "/pair", body);

        Assert.Equal(403, second.Status);
        Assert.False(string.IsNullOrWhiteSpace(second.Body), "отказ обязан быть словами");
        Assert.Null(second.Cookie);
        Assert.False(stand.Gate.HasLiveCode());
    }

    /// <summary>
    /// НЕВЕРНЫЙ КОД СЧИТАЕТ ПОПЫТКИ, А ПЕРЕБОР УПИРАЕТСЯ В ПАУЗУ: три неудачи подряд включают
    /// паузу (<see cref="PhoneDecisions.BlockAfter"/>), и дальше ответ — <c>429</c> со сроком
    /// ожидания, а не «ещё одна попытка угадать».
    /// </summary>
    [Fact]
    public async Task Неверный_код_считает_попытки_и_упирается_в_паузу()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        stand.Gate.IssueCode();

        // Чужой код выбирается ТАК, чтобы он не совпал с выпущенным: проверка не имеет права
        // «сойтись» на совпадении, а совпадение здесь было бы случайностью, а не ответом.
        var issued = stand.Gate.IssueCode();
        var wrongCode = issued == "000000" ? "111111" : "000000";
        var wrong = "{\"code\":\"" + wrongCode + "\"}";

        for (var attempt = 1; attempt <= PhoneDecisions.BlockAfter; attempt++)
        {
            var refused = await Send(stand.Http, HttpMethod.Post, "/pair", wrong);

            Assert.Equal(403, refused.Status);
        }

        var paused = await Send(stand.Http, HttpMethod.Post, "/pair", wrong);

        Assert.Equal(429, paused.Status);
        Assert.Contains("мин", paused.Body, StringComparison.Ordinal);
    }

    /// <summary>КОД НЕ ТОЙ ФОРМЫ — свой отказ: «12» и «12a45б» не должны доходить до сравнения,
    /// иначе отказ был бы неотличим от «не тот код».</summary>
    [Fact]
    public async Task Код_не_той_формы_отвергается_словами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        stand.Gate.IssueCode();

        foreach (var body in new[] { "{\"code\":\"12\"}", "{\"code\":\"12a45б\"}", "{}" })
        {
            var reply = await Send(stand.Http, HttpMethod.Post, "/pair", body);

            Assert.Equal(403, reply.Status);
            Assert.False(string.IsNullOrWhiteSpace(reply.Body));
        }
    }

    // ---- пропуск устройства ----------------------------------------------------------------

    /// <summary>БЕЗ COOKIE — <c>401</c> НА ВСЁ, ЧТО ЗА ДВЕРЬЮ. Пустой ответ и «молчание» запрещены:
    /// у отказа обязаны быть слова.</summary>
    [Fact]
    public async Task Без_cookie_всё_за_дверью_отвечает_401()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/question"),
                     (HttpMethod.Post, "/answer"),
                     (HttpMethod.Post, "/api/session/list"),
                 })
        {
            var reply = await Send(stand.Http, method, path, json: "{\"args\":{\"_request\":{}}}");

            Assert.Equal(401, reply.Status);
            Assert.False(string.IsNullOrWhiteSpace(reply.Body), $"отказ на {path} обязан быть словами");
        }

        // И движок при этом НЕ звался вовсе: за дверь не пустили — значит и ходить некуда.
        Assert.Empty(stand.Calls);
    }

    /// <summary>ЧУЖАЯ ПОДПИСЬ — <c>401</c>. Подпись привязана к устройству и считается от пропуска,
    /// поэтому «тот же пропуск, но другое устройство» входа не даёт.</summary>
    [Fact]
    public async Task Чужая_подпись_не_пускает()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var device = cookie[..cookie.IndexOf('.')];

        // Тот же пропуск, чужое устройство, «правдоподобная» подпись.
        var foreign = PhoneGate.CookieValue(device, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.Equal(401, (await Send(stand.Http, HttpMethod.Get, "/question", cookie: foreign)).Status);

        // Другое устройство с подписью, посчитанной от ТОГО ЖЕ пропуска, — тоже 401: устройства нет в списке.
        // ⚠️ Имя устройства здесь ЛАТИНИЦЕЙ: значение cookie уезжает заголовком, а заголовок HTTP
        // обязан быть ASCII. Настоящие имена устройств у панели — шестнадцатеричные, и это не случайность.
        var other = PhoneGate.CookieValue(
            "foreign-device-1",
            PhonePass.Load(PhonePassFiles.Under(stand.Paths, _ => { })).DeviceSignature("foreign-device-1"));

        Assert.Equal(401, (await Send(stand.Http, HttpMethod.Get, "/question", cookie: other)).Status);

        // А своя — пускает: иначе проверка была бы слепой.
        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)).Status);
    }

    /// <summary>
    /// ОТВЯЗКА УСТРОЙСТВА ПРЕКРАЩАЕТ ДОСТУП. Без этого «отвязать телефон» не значило бы ничего:
    /// подпись отвязанного устройства осталась бы верной (она считается от того же пропуска).
    /// </summary>
    [Fact]
    public async Task Отвязка_устройства_прекращает_доступ()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)).Status);

        var files = PhonePassFiles.Under(stand.Paths, _ => { });
        var pass = PhonePass.Load(files);
        var device = Assert.Single(pass.SafeDevices).Id;

        Assert.True(pass.WithoutDevice(device).Save(files));

        Assert.Equal(401, (await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)).Status);
        Assert.Equal(
            401,
            (await Send(stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie)).Status);
    }

    /// <summary>
    /// НЕЧЕМ ЗАПИСАТЬ ПРОПУСК — СОПРЯЖЕНИЕ НЕ ПРОХОДИТ: телефон не получает cookie, которой
    /// на следующем запуске панели не соответствует ничего. Прогон без права на свои данные
    /// получает <see cref="PhonePassFiles.None"/>, и калитка обязана отказать СЛОВАМИ, а не
    /// «пустить до перезапуска».
    /// </summary>
    [Fact]
    public async Task Без_права_писать_пропуск_сопряжение_не_проходит()
    {
        using var stand = NewStand(passFiles: PhonePassFiles.None);
        Assert.True(Started(stand));

        var code = stand.Gate.IssueCode();
        var reply = await Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}");

        Assert.Equal(500, reply.Status);
        Assert.Null(reply.Cookie);
        Assert.False(string.IsNullOrWhiteSpace(reply.Body));
    }

    // ---- белый список ----------------------------------------------------------------------

    /// <summary>
    /// РАЗРЕШЁННОЕ ИМЯ УХОДИТ В ДВИЖОК С НАШИМ КОНВЕРТОМ И КЛЮЧОМ ДОВОДОВ. Калитка не толкует
    /// доводы и не подставляет их — она передаёт ровно то, что прислал телефон, а конверт движка
    /// собирает ОДНА дверь (<c>EngineRpcClient.CallRaw</c>).
    /// </summary>
    [Fact]
    public async Task Разрешённое_имя_уходит_в_движок_с_доводами_как_есть()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list",
            "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(200, reply.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal("session/list", call.Endpoint);

        // ⚠️ ИМЕННО `_request`: замер (`docs\PHONE-LIVE.md` §2) — без него движок отвечает
        // `gateway/arguments-invalid` «missing "_request"».
        Assert.Equal("{\"_request\":{}}", call.Args);

        // А ВОТ ОТВЕТ — СВОЙ: строка списка собирается по allowlist (§0.4 контракта), и даже
        // пустой ответ движка телефону едет СВОИМ ответом (пустой список), а не его конвертом:
        // иначе через эту дверь уезжали бы `cwd` и `projections.workingDirectory`.
        Assert.Contains("\"total\":0", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"shown\":0", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("server-response", reply.Body, StringComparison.Ordinal);
    }

    /// <summary>ДОВОДЫ НЕ ПРИСЛАНЫ — отказ словами, и слова НАЗЫВАЮТ ключ, которого ждёт движок.</summary>
    [Fact]
    public async Task Без_доводов_отказ_называет_ключ()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var reply = await Send(stand.Http, HttpMethod.Post, "/api/session/list", "{}", cookie);

        Assert.Equal(400, reply.Status);
        Assert.Contains("_request", reply.Body, StringComparison.Ordinal);
        Assert.Empty(stand.Calls);
    }

    /// <summary>ЧУЖОЕ ИМЯ — <c>403</c> СЛОВАМИ, и в движок оно не уходит: белый список — это
    /// список, а не «спросим у движка, пусть скажет».</summary>
    [Fact]
    public async Task Чужое_имя_получает_отказ_словами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        foreach (var endpoint in new[] { "settings/mutate", "credentials/list", "session/listAll" })
        {
            var reply = await Send(
                stand.Http, HttpMethod.Post, "/api/" + endpoint, "{\"args\":{\"request\":{}}}", cookie);

            Assert.Equal(403, reply.Status);
            Assert.Contains(endpoint, reply.Body, StringComparison.Ordinal);
        }

        Assert.Empty(stand.Calls);
    }

    /// <summary>
    /// СТАТИКА И <c>/plugins/*</c> НЕ ПРОКСИРУЮТСЯ. У движка они открыты БЕЗ входа (замер
    /// <c>docs\PHONE-LIVE.md</c> §1), и «прозрачный мост» отдал бы телефону готовый отпечаток
    /// машины. Калитка пускает ИМЕНА, и ничего, кроме имён.
    /// </summary>
    [Fact]
    public async Task Статика_и_плагины_наружу_не_отдаются()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        // (1) GET по этим путям калитка не принимает вовсе.
        foreach (var path in new[] { "/api/assets/app.js", "/plugins/events", "/favicon.svg", "/manifest.webmanifest" })
        {
            var reply = await Send(stand.Http, HttpMethod.Get, path);

            Assert.NotEqual(200, reply.Status);
        }

        // (2) POST по «похожим на вызов» именам — отказ белого списка, а не проксирование.
        foreach (var endpoint in new[] { "plugins/events", "assets/app.js", "session/uploadFileBinary" })
        {
            var reply = await Send(
                stand.Http, HttpMethod.Post, "/api/" + endpoint, "{\"args\":{}}", cookie);

            Assert.Equal(403, reply.Status);
        }

        // (3) Незнакомый путь — 404 словами, а не «переправим в движок как есть».
        var nowhere = await Send(stand.Http, HttpMethod.Get, "/plugins");

        Assert.Equal(404, nowhere.Status);
        Assert.Empty(stand.Calls);
    }

    /// <summary>СЛИШКОМ БОЛЬШОЕ ТЕЛО — <c>413</c> СЛОВАМИ. Предел назван константой, и отказ идёт
    /// ДО чтения тела: иначе «предел» был бы пределом уже прочитанного.</summary>
    [Fact]
    public async Task Слишком_большое_тело_отвергается()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list",
            cookie: cookie, bodyBytes: PhoneGate.MaxBodyBytes + 4096);

        Assert.Equal(413, reply.Status);
        Assert.Contains((PhoneGate.MaxBodyBytes / 1024).ToString(), reply.Body, StringComparison.Ordinal);
        Assert.Empty(stand.Calls);
    }

    /// <summary>
    /// ПОТОКОВОЕ ИМЯ ПО ОДИНОЧНОМУ POST НЕ ЗОВЁТСЯ — и это сказано СЛОВАМИ («это поток»),
    /// а не молчанием: молчание выглядело бы «телефон ничего не получил», а на деле он зовёт то,
    /// чего так не зовут (`gateway/signature-invalid`, замер §2). Поток телефону открывает
    /// страница телефона (F2).
    /// </summary>
    [Fact]
    public async Task Потоковое_имя_по_POST_отвергается_словами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        foreach (var endpoint in new[] { "session/follow", "session/control", "$events" })
        {
            var reply = await Send(
                stand.Http, HttpMethod.Post, "/api/" + endpoint, "{\"args\":{\"request\":{}}}", cookie);

            Assert.Equal(403, reply.Status);
            Assert.Contains(endpoint, reply.Body, StringComparison.Ordinal);
        }

        Assert.Empty(stand.Calls);
    }

    /// <summary>СЛУЖЕБНАЯ ДВЕРЬ ОТВЕТА СЫРЫМ НЕ ЗОВЁТСЯ: телефон отвечает своей дверью
    /// (<c>POST /answer</c>), где калитка подставляет НАШ <c>clientId</c>.</summary>
    [Fact]
    public async Task Служебная_дверь_ответа_сырым_запросом_не_зовётся()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/$events/result", "{\"args\":{}}", cookie);

        Assert.Equal(403, reply.Status);
        Assert.False(string.IsNullOrWhiteSpace(reply.Body));
        Assert.Empty(stand.Calls);
    }

    /// <summary>НЕ ТОТ МЕТОД — <c>405</c> СЛОВАМИ: у каждого пути свой метод, и «молча ничего
    /// не сделать» здесь запрещено так же, как везде.</summary>
    [Fact]
    public async Task Не_тот_метод_отвечает_405()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var get = await Send(stand.Http, HttpMethod.Get, "/pair");
        Assert.Equal(405, get.Status);

        var post = await Send(stand.Http, HttpMethod.Post, "/", "{}");
        Assert.Equal(405, post.Status);

        Assert.False(string.IsNullOrWhiteSpace(get.Body));
        Assert.False(string.IsNullOrWhiteSpace(post.Body));
    }

    // ---- чтение запроса: что считается запросом (DEF-062) ------------------------------------

    /// <summary>
    /// TLS CLIENTHELLO — НЕ ЗАПРОС: калитка закрывает соединение СРАЗУ и НИЧЕГО не пишет.
    ///
    /// ⚠️ **Числа этого дефекта.** Первый вход на страницу телефона занимал **15 068 мс**: браузер
    /// пробует HTTPS и первым шлёт TLS ClientHello (`0x16 0x03…`), а калитка держала его до
    /// <see cref="PhoneGate.RequestTimeout"/> (15 с). Здесь проверяются ОБА числа сразу: **0 байт**
    /// ответа и закрытие **≤100 мс** — предел выбран так, чтобы отличать «сразу» от «по сроку»,
    /// а не мерить скорость машины.
    /// </summary>
    [Fact]
    public async Task TLS_соединение_закрывается_сразу_и_молча()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        // Настоящая голова TLS ClientHello: 0x16 — handshake, 0x03 0x01 — запись TLS 1.0,
        // дальше длина и тело рукопожатия. Содержимое не важно: важно, что это НЕ HTTP-метод.
        var hello = new byte[]
        {
            0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0xFC, 0x03, 0x03, 0x9A, 0x1B,
        };

        // Прогрев: первое соединение после старта платит за JIT — мерить надо дорогу, а не её
        // первый шаг. Мусор, который HTTP-методом не является ни одним байтом.
        var warm = await NonHttpAnswer(stand, new byte[] { 0x01, 0x02, 0x03 });
        Assert.Equal(0, warm.Bytes);

        var best = TimeSpan.MaxValue;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var answer = await NonHttpAnswer(stand, hello);

            // Байты проверяются на КАЖДОЙ попытке: «0 байт» — не среднее, а условие каждого входа.
            Assert.Equal(0, answer.Bytes);

            if (answer.Elapsed < best) best = answer.Elapsed;
        }

        Assert.True(
            best < TimeSpan.FromMilliseconds(100),
            $"TLS-соединение закрывалось за {best.TotalMilliseconds:F1} мс (предел 100 мс): калитка снова ждёт RequestTimeout");
    }

    /// <summary>
    /// GET / ЧЕРЕЗ КАЛИТКУ — ПРЕЖНИЙ ОТВЕТ: <c>200</c> и страница входа ТОЙ ЖЕ ДЛИНЫ. Число
    /// закреплено намеренно: правка «закрывать любое соединение после первого чтения» (M-D2)
    /// оставила бы TLS-проверку зелёной, а телефон — без страницы; длина ловит это раньше глаз.
    /// </summary>
    [Fact]
    public async Task GET_через_калитку_отдаёт_страницу_входа_прежней_длины()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var reply = await Send(stand.Http, HttpMethod.Get, "/ask");

        Assert.Equal(200, reply.Status);
        Assert.Contains("id=\"code\"", reply.Body, StringComparison.Ordinal);

        // 3 913 знаков — страница входа по-русски с формулировкой 2.5.0 (t22/t23): строка
        // ограничений стала на пять байт длиннее прежней (было 3 908). Число — не «красивое»,
        // а СНЯТОЕ: правка текста словаря не должна незаметно менять длину страницу.
        Assert.Equal(3913, reply.Body.Length);
    }

    /// <summary>
    /// РАЗРЕЗАННЫЙ ПО TCP ЗАПРОС ПРИНИМАЕТСЯ: «GE», пауза, «T / HTTP/1.1…». Решать по первому
    /// куску нельзя — иначе телефон, чей запрос лёг в два сегмента, получал бы закрытие вместо
    /// страницы. Пауза здесь обязательна: она гарантирует, что калитка получила «GE» ОТДЕЛЬНЫМ
    /// чтением, а не сросшийся запрос.
    /// </summary>
    [Fact]
    public async Task Разрезанный_по_TCP_запрос_принимается()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var token = TestContext.Current.CancellationToken;

        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, stand.Port, token);

        using var stream = client.GetStream();
        stream.ReadTimeout = 20000;
        stream.WriteTimeout = 20000;

        await stream.WriteAsync(Encoding.ASCII.GetBytes("GE"), token);
        await stream.FlushAsync(token);

        await Task.Delay(300, token);

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "T / HTTP/1.1\r\nHost: 127.0.0.1:" + stand.Port + "\r\n\r\n"), token);
        await stream.FlushAsync(token);

        var head = await ReadHead(stream);

        Assert.Equal(200, StatusOf(head));
    }

    // ---- заголовки безопасности ответов (B-10) ----------------------------------------------

    /// <summary>Значение заголовка из СЫРОЙ головы ответа. Пусто — заголовка нет.
    /// ⚠️ Разбор свой, а не `HttpClient`: тот разбирает `Cache-Control` в типизированное свойство
    /// и убирает строку из набора — «заголовок есть» на нём не докажешь (замерено).</summary>
    private static string HeaderValue(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            if (line.Length == 0) break;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// ВСЕ ВИДЫ ОТВЕТОВ КАЛИТКИ — ПО ОДНОМУ РАЗУ, СЫРЫМ ПУТЁМ. Возвращает «чем это было», код
    /// и голову ответа как она пришла по проводу: на ней и проверяются заголовки безопасности.
    ///
    /// Семь исходов, и каждый пишется СВОЕЙ дверью ответа: страница входа (200), страница телефона
    /// с cookie (200 — в ней личное), перенаправление после входа (303), отказ «вопроса нет» (403),
    /// «здесь ничего нет» (404), «не тот метод» (405), «слишком большое тело» (413). Если заголовки
    /// ставить не в одном месте, а «там, где не забыли», эти семь разойдутся — и проверка увидит.
    /// </summary>
    private static async Task<IReadOnlyList<(string Where, int Status, string Head)>> AllGateReplies(Stand stand)
    {
        var host = "127.0.0.1:" + stand.Port;

        // Телефон сопрягается ПО-НАСТОЯЩЕМУ: страница телефона и отказ по ответу — только с cookie.
        var cookie = await Pair(stand);
        var code = stand.Gate.IssueCode();

        var requests = new (string Where, string Request, int Status)[]
        {
            ("страница входа", RawRequest("GET", "/ask", host), 200),
            ("страница телефона", RawRequest("GET", "/ask", host, cookie), 200),
            ("перенаправление после входа", RawRequest("POST", "/pair", host, body: "code=" + code), 303),
            ("отказ: вопроса нет",
                RawRequest("POST", "/answer", host, cookie, "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}"), 403),
            ("незнакомый путь", RawRequest("GET", "/plugins/events", host), 404),
            ("не тот метод", RawRequest("GET", "/pair", host), 405),
            ("слишком большое тело",
                RawRequest("POST", "/api/session/list", host, cookie, "{}", PhoneGate.MaxBodyBytes + 4096), 413),
        };

        var replies = new List<(string, int, string)>();

        foreach (var item in requests)
        {
            var head = await RawResponse(stand, item.Request);
            var status = StatusOf(head);

            // КОД СВЕРЯЕТСЯ ПЕРВЫМ: если запрос не дал ожидаемого исхода, проверка заголовков
            // проверяла бы не тот ответ — и падать обязана здесь, а не молча на пустом заголовке.
            Assert.Equal(item.Status, status);

            replies.Add((item.Where, status, head));
        }

        return replies;
    }

    /// <summary>
    /// ОТВЕТЫ КАЛИТКИ НЕ КЭШИРУЮТСЯ (B-10): `Cache-Control: no-store` на КАЖДОМ ответе, включая
    /// ошибки. Не «не кэшируй надолго», а «не сохраняй вовсе»: в ответах личное — вопрос агента
    /// (в нём бывает текст чужой переписки), список сессий, имя устройства, — а телефон может
    /// перейти к другому человеку; и после «Отключить телефон» страница не должна остаться в кэше.
    ///
    /// Мутация, которая обязана упасть: **«убрать no-store»**.
    /// </summary>
    [Fact]
    public async Task Каждый_ответ_калитки_запрещает_кэш()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        foreach (var reply in await AllGateReplies(stand))
            Assert.Equal("no-store", HeaderValue(reply.Head, "Cache-Control"));
    }

    /// <summary>
    /// ОТВЕТЫ КАЛИТКИ НЕ ДАЮТ УГАДЫВАТЬ ТИП (B-10): `X-Content-Type-Options: nosniff` на каждом
    /// ответе. Без него браузер может отрисовать как HTML ответ, который мы объявили
    /// `application/json` (ASVS 5.0 3.4.4, L2).
    ///
    /// Мутация, которая обязана упасть: **«убрать nosniff»**.
    /// </summary>
    [Fact]
    public async Task Каждый_ответ_калитки_запрещает_подмену_типа()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        foreach (var reply in await AllGateReplies(stand))
            Assert.Equal("nosniff", HeaderValue(reply.Head, "X-Content-Type-Options"));
    }

    /// <summary>
    /// ОТВЕТЫ КАЛИТКИ НЕЛЬЗЯ ВСТАВИТЬ В РАМКУ (B-10): `Content-Security-Policy: frame-ancestors
    /// 'none'` на каждом ответе. Выбран ИМЕННО CSP, а не `X-Frame-Options`: ASVS 5.0 3.4.6 требует
    /// `frame-ancestors` для каждого ответа и называет `X-Frame-Options` устаревшим.
    ///
    /// ⚠️ Отсутствие `X-Frame-Options` — тоже проверка: это ЗАПИСАННОЕ решение, а не забывчивость.
    /// Появится второй заголовок — проверка покраснеет и заставит назвать причину.
    ///
    /// Мутация, которая обязана упасть: **«убрать frame-ancestors»**.
    /// </summary>
    [Fact]
    public async Task Каждый_ответ_калитки_запрещает_фреймы()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        foreach (var reply in await AllGateReplies(stand))
        {
            Assert.Equal("frame-ancestors 'none'", HeaderValue(reply.Head, "Content-Security-Policy"));
            Assert.Equal(string.Empty, HeaderValue(reply.Head, "X-Frame-Options"));
        }
    }

    // ---- своя дверь: `Host` и `Origin` (B-03) ------------------------------------------------

    /// <summary>
    /// СВЕРКА ДВЕРИ — ЧИСТОЕ РЕШЕНИЕ, ПОЭТОМУ ПРОВЕРЯЕТСЯ БЕЗ СЕТИ: все формы имени, которые
    /// бывают у настоящего телефона, и все, которые приходят от чужого.
    ///
    /// Своё имя — ровно то, что показывает раздел «Телефон»: адрес и порт калитки. Плюс `localhost`
    /// на петле (второе имя той же двери; так ходят прогоны проверки) и IPv6 в скобках.
    /// </summary>
    [Fact]
    public void Своё_имя_двери_пускается()
    {
        var lan = IPAddress.Parse("192.168.1.5");
        var loop = IPAddress.Loopback;
        var v6 = IPAddress.Parse("fe80::12");

        // Обычный адрес калитки в своей сети и его порт — так приходит настоящий телефон.
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("192.168.1.5:43127", string.Empty, lan, 43127));

        // Регистр в имени — не смысл: имена без регистра.
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("LOCALHOST:8080", string.Empty, loop, 8080));

        // Петля: `localhost` — второе имя ТОЙ ЖЕ двери.
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("localhost:8080", string.Empty, loop, 8080));
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("127.0.0.1:8080", string.Empty, loop, 8080));

        // IPv6: имя двери — в скобках, как требует HTTP.
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("[fe80::12]:43127", string.Empty, v6, 43127));

        // Своя страница: ровно `http://` + своё имя.
        Assert.Equal(
            PhoneGate.DoorVerdict.Own,
            PhoneGate.JudgeDoor("192.168.1.5:43127", "http://192.168.1.5:43127", lan, 43127));

        // Заголовка `Origin` нет вовсе — это обычный переход GET, и это не чужая страница.
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("192.168.1.5:43127", null, lan, 43127));
    }

    /// <summary>
    /// ЧУЖОЕ ИМЯ ДВЕРИ — ОТКАЗ. Это подмена имени (DNS rebinding): подключение идёт на наш адрес,
    /// а зовут дверь чужим именем. Каждая строка — отдельный способ соврать.
    /// </summary>
    [Fact]
    public void Чужое_имя_двери_отвергается()
    {
        var lan = IPAddress.Parse("192.168.1.5");
        var loop = IPAddress.Loopback;

        // Чужой адрес и чужой порт.
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("evil.example:43127", string.Empty, lan, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.6:43127", string.Empty, lan, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5:43128", string.Empty, lan, 43127));

        // Порт не назван, а дверь не на порту по умолчанию: браузер так не пишет, но пусть не проходит.
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5", string.Empty, lan, 43127));

        // Машинное имя вместо адреса — это НОВОЕ имя двери, а поверхность калитки не растёт.
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("my-pc:43127", string.Empty, lan, 43127));

        // `localhost` — только для двери на петле: на адресе своей сети это чужое имя.
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("localhost:43127", string.Empty, lan, 43127));

        // Пустое имя и мусор: «дверь не назвали» — не «своя».
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor(string.Empty, string.Empty, lan, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5:43127, evil.example:43127", string.Empty, lan, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5:43127:9", string.Empty, lan, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5:", string.Empty, lan, 43127));

        // IPv6 без скобок — не имя двери, а мусор: `fe80::12` не отличить от имени с портом.
        Assert.Equal(
            PhoneGate.DoorVerdict.ForeignHost,
            PhoneGate.JudgeDoor("fe80::12", string.Empty, IPAddress.Parse("fe80::12"), 43127));
    }

    /// <summary>
    /// ДВЕРЬ НА ПОРТУ 80: браузер ВЫБРАСЫВАЕТ `:80` из адреса, поэтому имя без порта обязано
    /// признаваться — иначе калитка, поставленная на 80, закрылась бы для своего же телефона.
    /// На всяком другом порту имя без порта — чужое.
    /// </summary>
    [Fact]
    public void Дверь_на_порту_80_принимает_имя_без_порта()
    {
        var lan = IPAddress.Parse("192.168.1.5");

        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("192.168.1.5", string.Empty, lan, 80));
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("192.168.1.5:80", string.Empty, lan, 80));
        Assert.Equal(PhoneGate.DoorVerdict.Own, PhoneGate.JudgeDoor("192.168.1.5", "http://192.168.1.5", lan, 80));

        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("192.168.1.5", string.Empty, lan, 8080));
    }

    /// <summary>
    /// ДВЕРИ НЕТ ВОВСЕ (калитка не встала): своё имя не пускает НИКОГО. Это правило fail-closed:
    /// «своего имени нет» не значит «своё», иначе незакрытая калитка пускала бы по любому адресу.
    /// </summary>
    [Fact]
    public void Без_своего_имени_двери_отказ()
    {
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("127.0.0.1:43127", string.Empty, null, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignHost, PhoneGate.JudgeDoor("127.0.0.1:43127", "http://127.0.0.1:43127", null, 43127));
    }

    /// <summary>
    /// ЧУЖАЯ СТРАНИЦА В `Origin` — ОТКАЗ, и `null` тоже. `null` присылает песочница, файл и
    /// `data:` — ни один из них не наша страница. Отдельно проверено, что `https://` чужой:
    /// калитка отдаёт страницу только по HTTP (шифрования у неё нет — F3).
    /// </summary>
    [Fact]
    public void Чужая_страница_в_Origin_отвергается()
    {
        var loop = IPAddress.Loopback;
        const string host = "127.0.0.1:43127";

        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "null", loop, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "http://192.168.1.5:43127", loop, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "https://127.0.0.1:43127", loop, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "http://127.0.0.1:43128", loop, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "http://127.0.0.1:43127/", loop, 43127));
        Assert.Equal(PhoneGate.DoorVerdict.ForeignOrigin, PhoneGate.JudgeDoor(host, "file://127.0.0.1:43127", loop, 43127));
    }

    /// <summary>
    /// ПОДМЕНЁННОЕ ИМЯ ДВЕРИ НЕ ДЕЛАЕТ НИЧЕГО — и это проверено на САМОМ ДЕЙСТВИИ: сопряжение
    /// кода не требует, поэтому чужое имя двери с ВЕРНЫМ кодом обязано кончиться отказом
    /// **до** записи устройства. Заодно видно, что чужое имя не получает и страницу входа:
    /// «отказ до действия» — это не только порядок строк в коде.
    /// </summary>
    [Fact]
    public async Task Чужой_Host_отвергается_до_действия()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var code = stand.Gate.IssueCode();
        var foreign = "evil.example:" + stand.Port;

        var pair = await Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}", host: foreign);

        Assert.Equal(403, pair.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateForeignDoor), pair.Body, StringComparison.Ordinal);

        // ДЕЙСТВИЯ НЕ БЫЛО: устройства не завелось, код остался жив, cookie не выдали.
        Assert.Equal(0, stand.Gate.DeviceCount);
        Assert.True(stand.Gate.HasLiveCode());
        Assert.Null(pair.Cookie);

        // И страницы не было: чужое имя двери не видит даже вход.
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", host: foreign);

        Assert.Equal(403, page.Status);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageCodeLabel), page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// СВОИ `Host` И `Origin` ПРОХОДЯТ — иначе отказ выше доказывал бы только то, что калитка
    /// закрыта для всех. Это НАСТОЯЩАЯ форма запроса телефона: форма входа уходит POST'ом со своей
    /// же страницы, и браузер прикладывает к ней свой `Origin`.
    /// </summary>
    [Fact]
    public async Task Свой_Host_и_свой_Origin_проходят()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var ownHost = "127.0.0.1:" + stand.Port;
        var ownOrigin = "http://" + ownHost;

        var code = stand.Gate.IssueCode();

        var pair = await Send(
            stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}",
            host: ownHost, origin: ownOrigin);

        Assert.Equal(200, pair.Status);
        Assert.NotNull(pair.Cookie);

        // Обычный переход GET — без `Origin` вовсе: браузер его к переходу не прикладывает.
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", host: ownHost);

        Assert.Equal(200, page.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCodeLabel), page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// `ORIGIN: NULL` — ОТКАЗ, И ОТВЕТ НЕ УХОДИТ: `null` присылает песочница (чужой iframe), файл
    /// и `data:` — ни один из них не наша страница, а ответ на вопрос агента — действие.
    /// Проверено по последствию: в движок не ушло ничего, вопрос остался ждать.
    /// </summary>
    [Fact]
    public async Task Origin_null_отвергается_и_ответ_не_уходит()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}",
            cookie, origin: "null");

        Assert.Equal(403, reply.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateForeignDoor), reply.Body, StringComparison.Ordinal);

        // ДЕЙСТВИЯ НЕ БЫЛО: ответа в движке нет, вопрос на месте.
        Assert.Empty(stand.Calls);

        var question = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"state\":\"question\"", question.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЧУЖАЯ СТРАНИЦА ОТВЕТИТЬ НЕ МОЖЕТ — и это не про cookie: `/pair` кода не требует ни от кого,
    /// и форма с чужого сайта завела бы устройство. Проверены оба вида чужой страницы — чужой
    /// адрес и наша же схема с чужим портом.
    /// </summary>
    [Fact]
    public async Task Чужой_Origin_отвергается_до_действия()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        foreach (var origin in new[] { "https://evil.example", "http://127.0.0.1:" + (stand.Port + 1) })
        {
            var reply = await Send(
                stand.Http, HttpMethod.Post, "/answer",
                "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}",
                cookie, origin: origin);

            Assert.Equal(403, reply.Status);
            Assert.Contains(Loc.T(PanelStrings.PhoneGateForeignDoor), reply.Body, StringComparison.Ordinal);
        }

        Assert.Empty(stand.Calls);

        var question = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"state\":\"question\"", question.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАПРОС БЕЗ ИМЕНИ ДВЕРИ И ЗАПРОС С ДВУМЯ ИМЕНАМИ — ОТКАЗ. `HttpClient` так не умеет (он
    /// всегда ставит `Host` из адреса и второй не пошлёт), поэтому запросы сырые. Заодно видно,
    /// что сырой запрос со СВОИМ именем проходит: иначе проверка доказывала бы только то, что
    /// калитка молчит для всех.
    /// </summary>
    [Fact]
    public async Task Запрос_без_имени_двери_и_с_двумя_именами_отвергается()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var own = "127.0.0.1:" + stand.Port;

        Assert.Equal(403, await Raw(stand, "GET / HTTP/1.1\r\n\r\n"));
        Assert.Equal(
            403,
            await Raw(stand, "GET / HTTP/1.1\r\nHost: " + own + "\r\nHost: evil.example:9\r\n\r\n"));
        Assert.Equal(200, await Raw(stand, "GET / HTTP/1.1\r\nHost: " + own + "\r\n\r\n"));
    }

    /// <summary>
    /// СЛИШКОМ БОЛЬШОЙ ЗАПРОС ОТВЕЧАЕТ <c>413</c> РАНЬШЕ СВЕРКИ ДВЕРИ, и это НАМЕРЕННО: у такого
    /// запроса заголовки не разобраны вовсе, и «чужая дверь» вместо «слишком много» соврала бы
    /// своему же телефону (предел 64 КиБ — настоящий, телефон в него упирается текстом задачи).
    /// Проверка держит порядок: перенос сверки выше уронит её.
    /// </summary>
    [Fact]
    public async Task Слишком_большой_запрос_отвечает_413_раньше_сверки_двери()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list",
            cookie: cookie,
            bodyBytes: PhoneGate.MaxBodyBytes + 4096,
            host: "evil.example:" + stand.Port);

        Assert.Equal(413, reply.Status);
        Assert.Empty(stand.Calls);
    }

    // ---- подписка на события и вопрос агента ------------------------------------------------

    /// <summary>ПОДПИСКА ОТКРЫВАЕТСЯ ПРИ ЗАПУСКЕ И ПЕРЕОТКРЫВАЕТСЯ ПОСЛЕ ОБРЫВА. Без подписчика
    /// вопрос агента висит вечно (опыт В замера §4), поэтому подписка — условие работы, а не
    /// улучшение.</summary>
    [Fact]
    public void Подписка_открывается_и_переоткрывается()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);

        Assert.True(Started(stand));
        Assert.Equal(1, events.Opens);
        Assert.NotNull(events.Current);

        // ОБРЫВ: подписка перестала быть живой — калитка обязана открыть её заново.
        events.Current!.Alive = false;

        stand.Gate.EnsureSubscription();

        Assert.Equal(2, events.Opens);
        Assert.True(stand.Gate.Listening);
    }

    /// <summary>
    /// ВОПРОС АГЕНТА ДОХОДИТ ДО ТЕЛЕФОНА, А ДОМАШНИЙ КАТАЛОГ МАШИНЫ — НЕТ.
    ///
    /// Кадр `ready` отдаёт `host.home` (замер §3) — это путь в доме человека, и телефону его
    /// показывать нельзя ни при каких условиях. Проверка подаёт кадр с ним и требует, чтобы он
    /// не встретился НИ В ОДНОМ ответе калитки.
    /// </summary>
    [Fact]
    public async Task Вопрос_виден_телефону_а_дом_машины_нет()
    {
        const string home = "C:\\Users\\кто-то-домашний";

        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\",\"host\":{\"home\":\"" + home.Replace("\\", "\\\\") + "\"}}}");

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"}]}]}}}");

        var reply = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"state\":\"question\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("event-7", reply.Body, StringComparison.Ordinal);
        Assert.Contains("Делать?", reply.Body, StringComparison.Ordinal);

        // ⚠️ НИ clientId движка, НИ его дом наружу не выходят.
        Assert.DoesNotContain("client-42", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(home, reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("кто-то-домашний", reply.Body, StringComparison.Ordinal);

        // И на странице входа их тоже нет — она вообще не знает про поток.
        var page = await Send(stand.Http, HttpMethod.Get, "/ask");
        Assert.DoesNotContain(home, page.Body, StringComparison.Ordinal);
    }

    /// <summary>ОТВЕТ УХОДИТ С НАШИМ <c>clientId</c> И ОТВЕТОМ ТЕЛЕФОНА. Телефон не знает ни
    /// <c>clientId</c>, ни cookie движка — их подставляет калитка.</summary>
    [Fact]
    public async Task Ответ_уходит_с_нашим_clientId()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(200, reply.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);

        // Ответ ушёл РОВНО с нашим clientId, с тем же eventId и с ответом телефона как есть.
        Assert.Contains("\"clientId\":\"client-42\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"eventId\":\"event-7\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"result\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("{\"id\":\"q1\",\"selected\":[\"Да\"]}", call.Args, StringComparison.Ordinal);
    }

    /// <summary>ЧУЖОЙ ИЛИ УСТАРЕВШИЙ <c>eventId</c> — ОТКАЗ СЛОВАМИ, а не молчание: человек на
    /// телефоне обязан понять, что отвечает не на тот вопрос, который видит.</summary>
    [Fact]
    public async Task Чужой_eventId_отвергается_словами()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        // Вопроса нет вовсе — отвечать не на что.
        var nothing = await Send(
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(403, nothing.Status);
        Assert.False(string.IsNullOrWhiteSpace(nothing.Body));

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        // А теперь вопрос есть, и отвечать ЕСТЬ ЧЕМ (наш `clientId` пришёл) — но отвечают
        // НЕ на него. Отказ обязан быть именно про чужой вопрос, а не «нечем отправить».
        var stale = await Send(
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"чужой\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(403, stale.Status);
        Assert.Empty(stand.Calls);
    }

    /// <summary>
    /// КАДР <c>cancel</c> СНИМАЕТ ВОПРОС: кто-то (компьютер или второе устройство) ответил раньше.
    /// Молчать об этом нельзя — иначе на телефоне остался бы вопрос, на который уже ответили.
    /// </summary>
    [Fact]
    public async Task Отмена_снимает_вопрос_и_телефону_говорят_словами()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        Assert.Contains(
            "\"state\":\"question\"",
            (await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)).Body,
            StringComparison.Ordinal);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"cancel\",\"eventId\":\"event-7\"}}");

        var after = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"state\":\"cancelled\"", after.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(after.Body));
    }

    // ---- один слот вопроса: второй не затирает первый молча, и ожидание имеет предел (B-05) ----

    /// <summary>
    /// ВТОРОЙ ВОПРОС ОТМЕНЯЕТ ПЕРВЫЙ — а не выбрасывает его молча (B-05). Слот один, и прежний ход
    /// агента ОСВОБОЖДАЕТСЯ отменой: `session/cancel` с `sessionId` ПРЕЖНЕГО вопроса. После этого
    /// телефон видит НОВЫЙ вопрос, а не пустоту.
    ///
    /// Мутации, которые обязаны упасть: **M1 «второй затирает первый без отмены»** · «отменить не
    /// прежнего, а нового».
    /// </summary>
    [Fact]
    public async Task Второй_вопрос_отменяет_первый_а_не_затирает()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-1\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"header\":\"Первый\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        var first = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"eventId\":\"event-1\"", first.Body, StringComparison.Ordinal);

        // ВТОРОЙ ВОПРОС — ДРУГОГО АГЕНТА.
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-2\",\"agentId\":\"session-2\","
            + "\"request\":{\"questions\":[{\"id\":\"q2\",\"header\":\"Второй\",\"question\":\"Тоже делать?\","
            + "\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        // ХОД ПЕРВОГО АГЕНТА ОТМЕНЁН — и именно ПЕРВОГО: чужой `sessionId` в отмене означал бы
        // «снят вопрос, а ход стоит», то есть ровно тот дефект, ради которого правка.
        var cancel = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.SessionCancelEndpoint, cancel.Endpoint);
        Assert.Contains("\"sessionId\":\"session-1\"", cancel.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("session-2", cancel.Args, StringComparison.Ordinal);

        // И телефону показан ВТОРОЙ вопрос.
        var second = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"eventId\":\"event-2\"", second.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТВЕТ НА ПРЕЖНИЙ ВОПРОС ПОСЛЕ ЗАМЕНЫ В ЧУЖОЙ ХОД НЕ УХОДИТ (B-05). Это не про подпись
    /// и не про cookie: `eventId` сверяется с ТЕКУЩИМ вопросом, и ответ на снятый вопрос получает
    /// отказ словами, а ответ на новый — уходит.
    ///
    /// Мутация, которая обязана упасть: «проверять `eventId` только у формы» (машинный путь
    /// отправил бы ответ старого вопроса с `clientId` нового хода).
    /// </summary>
    [Fact]
    public async Task Ответ_на_первый_вопрос_после_замены_в_чужой_ход_не_уходит()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-1\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[]}}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-2\",\"agentId\":\"session-2\","
            + "\"request\":{\"questions\":[]}}}");

        var old = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-1\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(403, old.Status);

        // В ДВИЖОК НЕ УШЛО НИЧЕГО, КРОМЕ ОТМЕНЫ ПРЕЖНЕГО ХОДА.
        Assert.DoesNotContain(stand.Calls, call => call.Endpoint == PhoneDecisions.EventsResultEndpoint);

        // А НА НОВЫЙ — уходит, и именно на него.
        var fresh = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-2\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(200, fresh.Status);

        var result = Assert.Single(
            stand.Calls, call => call.Endpoint == PhoneDecisions.EventsResultEndpoint);

        Assert.Contains("\"eventId\":\"event-2\"", result.Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВОПРОС БЕЗ ОТВЕТЧИКА СНИМАЕТСЯ ПО ПРЕДЕЛУ — ход агента не ждёт вечно (B-05). Часы стенда
    /// двигает проверка, а зовёт она ТЕ ЖЕ часы, что и калитка (`Tick`), поэтому проверяется
    /// настоящий путь, а не копия решения.
    ///
    /// Требуется три вещи и все три проверяются: ход ОТМЕНЁН (`session/cancel` с `sessionId`
    /// вопроса) · телефону сказано, ЧТО случилось (слова о пределе, а не «на него ответили») ·
    /// в журнале предел назван ЧИСЛОМ.
    ///
    /// Мутация, которая обязана упасть: **M2 «ожидание без предела»** (предел убран).
    /// </summary>
    [Fact]
    public async Task Вопрос_без_ответчика_снимается_по_пределу()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[]}}}");

        Assert.Empty(stand.Calls);

        // ПРЕДЕЛ ИСТЁК: часы стенда уходят за него, и калитка делает то же, что делает её таймер.
        stand.Now += PhoneGate.QuestionWaitLimit + TimeSpan.FromSeconds(1);

        stand.Gate.Tick();

        var cancel = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.SessionCancelEndpoint, cancel.Endpoint);
        Assert.Contains("\"sessionId\":\"session-1\"", cancel.Args, StringComparison.Ordinal);

        // ТЕЛЕФОНУ — СЛОВА О ПРЕДЕЛЕ: «ответа не было 10 мин., ход отменён», а не «на него уже ответили».
        var minutes = (int)PhoneGate.QuestionWaitLimit.TotalMinutes;
        var expected = string.Format(Loc.T(PanelStrings.PhoneGateQuestionNoAnswerFormat), minutes);
        var after = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"state\":\"cancelled\"", after.Body, StringComparison.Ordinal);
        Assert.Contains(expected, after.Body, StringComparison.Ordinal);

        // И В ЖУРНАЛЕ ПРЕДЕЛ НАЗВАН ЧИСЛОМ, а не «давно».
        var logged = string.Format(
            Loc.T(PanelStrings.PhoneGateQuestionNoAnswerLogFormat), minutes, "event-7");

        Assert.Contains(logged, stand.Log);
    }

    /// <summary>
    /// СВЕЖИЙ ВОПРОС ПРЕДЕЛОМ НЕ СНИМАЕТСЯ — иначе «предел» был бы «снимать всегда», то есть
    /// человек никогда не успевал бы ответить. Мутация, которая обязана упасть: «сравнение предела
    /// перевёрнуто» (снимается сразу).
    /// </summary>
    [Fact]
    public async Task Свежий_вопрос_пределом_не_снимается()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[]}}}");

        // Прошла ПОЛОВИНА предела — вопрос ещё ждёт человека.
        stand.Now += TimeSpan.FromTicks(PhoneGate.QuestionWaitLimit.Ticks / 2);

        stand.Gate.Tick();

        Assert.Empty(stand.Calls);

        var page = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"state\":\"question\"", page.Body, StringComparison.Ordinal);
        Assert.Contains("\"eventId\":\"event-7\"", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОВТОР ТОГО ЖЕ ВОПРОСА ХОД НЕ ОТМЕНЯЕТ: тот же `eventId` — это ТОТ ЖЕ вопрос (так бывает
    /// после переоткрытия подписки), и «замена» здесь означала бы отмену живого хода.
    /// </summary>
    [Fact]
    public async Task Повтор_того_же_вопроса_ход_не_отменяет()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        for (var time = 0; time < 2; time++)
        {
            events.Emit(
                "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
                + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
                + "\"request\":{\"questions\":[]}}}");
        }

        Assert.Empty(stand.Calls);

        var page = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"eventId\":\"event-7\"", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВА ВОПРОСА ОТ ОДНОЙ СЕССИИ — БЕЗ ОТМЕНЫ: `session/cancel` отменяет ТЕКУЩИЙ ход сессии, и при
    /// совпавшем `agentId` отмена погасила бы НОВЫЙ ход, то есть ровно то, что надо сохранить.
    /// Такого в движке быть не может (ждущий ход второго вопроса не задаёт), но код обязан быть
    /// безопасным и здесь: вопрос уступает место, в журнале — строка о замене, отмены нет.
    /// </summary>
    [Fact]
    public async Task Два_вопроса_одной_сессии_отмены_не_шлют()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[]}}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-8\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[]}}}");

        Assert.Empty(stand.Calls);

        var page = await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie);

        Assert.Contains("\"eventId\":\"event-8\"", page.Body, StringComparison.Ordinal);
    }

    // ---- страница телефона (этап F2) --------------------------------------------------------

    /// <summary>
    /// КОРЕНЬ ОТДАЁТ ВХОД БЕЗ COOKIE И СТРАНИЦУ ТЕЛЕФОНА С COOKIE.
    ///
    /// ⚠️ Это не «две двери», а одна: кто ещё не сопряжён — получает вход, кто свой — работу.
    /// Второго адреса телефону не заводится: он должен знать ОДИН адрес, и он у него уже есть.
    ///
    /// Мутации, которые обязаны упасть здесь: «страница отдаётся без cookie» · «после сопряжения
    /// телефон снова видит вход» · «на странице телефона нет слов ожидания».
    /// </summary>
    [Fact]
    public async Task Корень_отдаёт_вход_без_cookie_и_страницу_телефона_с_cookie()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var anon = await Send(stand.Http, HttpMethod.Get, "/ask");

        Assert.Equal(200, anon.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCodeLabel), anon.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Equal(200, page.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageWaiting), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageCodeLabel), page.Body, StringComparison.Ordinal);

        // Вопросов нет — и ни одной формы: пустых кнопок «на всякий случай» быть не должно.
        Assert.DoesNotContain("<form", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВОПРОС НА СТРАНИЦЕ — СЛОВАМИ И КНОПКАМИ, и обе двери на месте: ответ и отмена.
    ///
    /// Мутации, которые обязаны упасть здесь: «вопрос показан не словами» · «нет вариантов ответа» ·
    /// «форма ответа шлёт мимо калитки» · «нет возможности отменить вопрос».
    /// </summary>
    [Fact]
    public async Task Вопрос_на_странице_показан_словами_и_кнопками()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"header\":\"Заголовок\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Equal(200, page.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageQuestionTitle), page.Body, StringComparison.Ordinal);
        Assert.Contains("Делать?", page.Body, StringComparison.Ordinal);

        // ⚠️ А `header` КАДРА НА СТРАНИЦЕ НЕ ПОКАЗЫВАЕТСЯ — и это названное решение, а не забывчивость:
        // выбранный владельцем макет варианта A (`_phone-mock\A-light.html`) его не показывает,
        // а макет — единственное, что владелец уже одобрил глазом (F5, решение 11.10.2026).
        // Человеку видны текст вопроса, подробность и варианты; `header` остаётся разобранным
        // (`PhonePage.Parse`), но не рисуется. Это в отчёте: «красоту судит глаз владельца».
        Assert.DoesNotContain("Заголовок", page.Body, StringComparison.Ordinal);

        // ВАРИАНТЫ — КНОПКАМИ ВЫБОРА, и оба на месте.
        Assert.Contains("Да", page.Body, StringComparison.Ordinal);
        Assert.Contains("Нет", page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageAnswerButton), page.Body, StringComparison.Ordinal);

        // ОБЕ ДВЕРИ: ответ уходит в калитку (не в движок сырым POST), отмена — своей дверью.
        Assert.Contains("action=\"/answer\"", page.Body, StringComparison.Ordinal);
        Assert.Contains("action=\"/cancel\"", page.Body, StringComparison.Ordinal);
        Assert.Contains("event-7", page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCancelWarning), page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCancelButton), page.Body, StringComparison.Ordinal);

        // ⚠️ И СТРАНИЦА ВОПРОСА САМА НЕ ОБНОВЛЯЕТСЯ (решение владельца 11.10.2026 по живому
        // прогону: обновление СТИРАЛО уже выбранный ответ). Нет тега — нет и сброса выбора.
        Assert.DoesNotContain("http-equiv=\"refresh\"", page.Body, StringComparison.Ordinal);

        // ⚠️ В движок со страницы не уходит НИЧЕГО: страница только показывает.
        Assert.Empty(stand.Calls);
    }

    /// <summary>
    /// ФОРМА ОТВЕЧАЕТ ТЕМ ЖЕ ПУТЁМ, ЧТО И ГОТОВАЯ ДВЕРЬ: наш `clientId`, наш `eventId`, а ПОДПИСИ
    /// ВАРИАНТОВ калитка берёт ИЗ СВОЕГО вопроса — форма присылает только НОМЕР варианта, поэтому
    /// подделать ответ телефон не может.
    ///
    /// Мутации, которые обязаны упасть здесь: «ответ уходит в движок мимо двери» · «в ответ уехала
    /// подпись из формы, а не из вопроса» · «в ответе нет нашего clientId».
    /// </summary>
    [Fact]
    public async Task Форма_отвечает_через_калитку_и_подписи_берутся_из_вопроса()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        // ФОРМА, А НЕ JSON: телефон шлёт номер варианта (1 — «Нет»), а не подпись.
        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=1", cookie);

        // ⚠️ ОТВЕТ ФОРМЫ — ПЕРЕНАПРАВЛЕНИЕ НА КОРЕНЬ, а не страница с обновлением (решение
        // владельца 11.10.2026): F5 после ответа повторяет GET, а не отправку, и человек попадает
        // на страницу, где сказано «ответ отправлен».
        Assert.Equal(303, reply.Status);
        Assert.Equal("/", reply.Location);
        Assert.Empty(reply.Body);

        var after = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Contains(Loc.T(PanelStrings.PhonePageAnswered), after.Body, StringComparison.Ordinal);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
        Assert.Contains("\"clientId\":\"client-42\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"eventId\":\"event-7\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("{\"id\":\"q1\",\"selected\":[\"Нет\"]}", call.Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОВТОР НАЖАТИЯ НЕ ШЛЁТ ВТОРОЙ ОТВЕТ. Требование владельца дословно: «ответ уходит один раз».
    ///
    /// ⚠️ Два нажатия приходят ДВУМЯ запросами в разных задачах — поэтому проверка и зовёт дверь
    /// дважды подряд: второй раз движок не должен быть позван вовсе.
    ///
    /// Мутации, которые обязаны упасть здесь: «повтор нажатия отправляет второй ответ» ·
    /// «после ответа страница молчит о том, что ответ уже ушёл».
    /// </summary>
    [Fact]
    public async Task Повтор_нажатия_не_шлёт_второй_ответ()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"}]}]}}}");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var first = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=0", cookie);

        Assert.Equal(303, first.Status);
        Assert.Equal("/", first.Location);
        Assert.Single(stand.Calls);

        var second = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=0", cookie);

        Assert.Contains(Loc.T(PanelStrings.PhonePageAnsweredAgain), second.Body, StringComparison.Ordinal);
        Assert.Single(stand.Calls);

        // И СТРАНИЦА больше не показывает кнопок: вопрос кончился, и это сказано словами.
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Contains(Loc.T(PanelStrings.PhonePageAnswered), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРОИГРАВШИЙ ЧИТАЕТ СЛОВАМИ, А НЕ СМОТРИТ НА МЁРТВЫЕ КНОПКИ: вопрос уже отвечен
    /// не с телефона — страница обязана сказать это, а кнопок не показывать.
    ///
    /// Мутации, которые обязаны упасть здесь: «проигравший молчит» · «снятый вопрос показан
    /// с живыми кнопками».
    /// </summary>
    [Fact]
    public async Task Снятый_вопрос_страница_говорит_словами()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"}]}]}}}");

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"cancel\",\"eventId\":\"event-7\"}}");

        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Equal(200, page.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageTaken), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Делать?", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТМЕНА ИДЁТ ТОЙ ЖЕ ДВЕРЬЮ, ЧТО У ПАНЕЛИ: <c>session/cancel</c> с `sessionId` ТОГО, КТО
    /// СПРАШИВАЕТ.
    ///
    /// Замер `docs\PHONE-LIVE.md` §4: вопрос без ответившего висит вечно и освобождается только
    /// отменой. Отмена обязана уйти ИМЕННО в движок и именно тем вызовом — иначе агент так и будет
    /// ждать, а телефону скажут, что всё хорошо.
    ///
    /// Мутации, которые обязаны упасть здесь: «отмена не освобождает агента (движок не позван)» ·
    /// «отмена ушла другим вызовом» · «отменяется не тот, кто спрашивает» · «отменить можно чужой
    /// вопрос».
    /// </summary>
    [Fact]
    public async Task Отмена_зовёт_движок_той_же_дверью()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"}]}]}}}");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"accepted\":true}}");

        var reply = await Send(stand.Http, HttpMethod.Post, "/cancel", "eventId=event-7", cookie);

        // ОТМЕНА — ТОЖЕ ПЕРЕНАПРАВЛЕНИЕ НА КОРЕНЬ: сказанное «ход отменён» человек читает
        // на странице, а не в теле ответа на POST (F5 не повторяет отмену).
        Assert.Equal(303, reply.Status);
        Assert.Equal("/", reply.Location);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.SessionCancelEndpoint, call.Endpoint);
        Assert.Contains("\"sessionId\":\"session-1\"", call.Args, StringComparison.Ordinal);

        // И ВОПРОС СНЯТ: страница больше не показывает ни вопроса, ни кнопок.
        var page = await Send(stand.Http, HttpMethod.Get, "/ask", cookie: cookie);

        Assert.Contains(Loc.T(PanelStrings.PhonePageCancelled), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", page.Body, StringComparison.Ordinal);

        // ЧУЖОЙ ВОПРОС ОТМЕНИТЬ НЕЛЬЗЯ: вопроса больше нет — и движок не зовётся.
        stand.Calls.Clear();

        var again = await Send(stand.Http, HttpMethod.Post, "/cancel", "eventId=чужой", cookie);

        Assert.Empty(stand.Calls);
        Assert.False(string.IsNullOrWhiteSpace(again.Body));
    }

    /// <summary>
    /// ВЫБРАН НЕ КАЖДЫЙ ВОПРОС — ОТВЕТ НЕ УХОДИТ, и это сказано словами. Молчаливая отправка
    /// пустого ответа была бы ответом агента «ни на что».
    ///
    /// Мутации, которые обязаны упасть здесь: «неполный выбор отправлен движку» ·
    /// «номер варианта вне границ принят».
    /// </summary>
    [Fact]
    public async Task Неполный_выбор_не_отправляет_ответ()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\","
            + "\"options\":[{\"label\":\"Да\"}]}]}}}");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        // НИЧЕГО НЕ ВЫБРАНО — ответа нет.
        var empty = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=", cookie);

        Assert.Equal(400, empty.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageChooseAll), empty.Body, StringComparison.Ordinal);
        Assert.Empty(stand.Calls);

        // НОМЕР ВНЕ ГРАНИЦ — тоже отказ, а не «первый вариант».
        var outside = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=event-7&q0=7", cookie);

        Assert.Equal(400, outside.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageChooseAll), outside.Body, StringComparison.Ordinal);
        Assert.Empty(stand.Calls);
    }

    /// <summary>БЕЗ <c>clientId</c> ОТВЕЧАТЬ НЕЧЕМ: подписка не открыта или `ready` ещё не пришёл.
    /// Это отказ СЛОВАМИ, а не «отправим пустое и промолчим».</summary>
    [Fact]
    public async Task Без_подписки_ответ_отвергается_словами()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        // Вопрос есть, а `ready` не приходил — значит отвечать нечем.
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(503, reply.Status);
        Assert.False(string.IsNullOrWhiteSpace(reply.Body));
        Assert.Empty(stand.Calls);
    }

    /// <summary>ДВИЖОК НЕ ОТВЕТИЛ — <c>502</c> СЛОВАМИ, а не пустота и не «ответ отправлен».</summary>
    [Fact]
    public async Task Молчание_движка_отвечает_словами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = PhoneEngineAnswer.Failed("timeout");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(502, reply.Status);
        Assert.False(string.IsNullOrWhiteSpace(reply.Body));
    }

    /// <summary>ОТКАЗ ВХОДА В ДВИЖОК НЕ ПОКАЗЫВАЕТСЯ ТЕЛЕФОНУ КАК «ТЫ НЕ СОПРЯЖЁН»: телефон принял бы
    /// это за свою беду и пошёл бы сопрягаться заново. Это беда панели, и код у неё свой.</summary>
    [Fact]
    public async Task Отказ_входа_в_движок_не_выдаётся_за_чужую_cookie()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(401, "unauthorized");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(502, reply.Status);
    }

    // ---- настоящие двери: конверт, cookie и токен ------------------------------------------

    /// <summary>Один запрос, каким его увидел ПОДСТАВНОЙ движок: строка запроса, cookie и тело.</summary>
    public sealed record SeenCall(string Line, string Cookie, string Body);

    /// <summary>
    /// ПОДСТАВНОЙ ДВИЖОК — маленький HTTP-сервер на ПЕТЛЕ: отдаёт cookie входа и записывает,
    /// ЧЕМ его позвали. Нужен ровно затем, чтобы «настоящие двери» калитки
    /// (<see cref="PhoneGateRealDoors"/>) были проверены ЦЕЛИКОМ — обмен ссылки входа, конверт
    /// вызова, cookie, — но без движка и без единого байта наружу.
    /// </summary>
    private sealed class FakeEngine : IDisposable
    {
        private const string Probe =
            "{\"type\":\"server-response\",\"rpcId\":\"probe\",\"result\":{\"ok\":true,\"value\":{\"items\":[]}}}";

        private readonly TcpListener _listener;

        public FakeEngine()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(Loop);
        }

        public int Port { get; }

        /// <summary>Ссылка входа — с ТОКЕНОМ, как у настоящего движка. Он не должен уехать никуда,
        /// кроме самого обмена.</summary>
        public string EntryLink => "http://127.0.0.1:" + Port + "/?token=" + Token;

        public string Token => "sekretnyj-token-vhoda";

        public List<SeenCall> Seen { get; } = new();

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // Уже закрыт — этого и добивались.
            }
        }

        private async Task Loop()
        {
            while (true)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (method, path, cookie, body) = Read(stream);

                    lock (Seen) Seen.Add(new SeenCall(method + " " + path, cookie, body));

                    var isCall = path.StartsWith("/api/", StringComparison.Ordinal);

                    var head = isCall
                        ? "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                            + Probe.Length + "\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 303 See Other\r\nLocation: /\r\nSet-Cookie: dsh-auth-probe=v1.payload.signature\r\n"
                            + "Content-Length: 0\r\nConnection: close\r\n\r\n";

                    var bytes = Encoding.ASCII.GetBytes(head);

                    stream.Write(bytes, 0, bytes.Length);

                    if (isCall)
                    {
                        var payload = Encoding.UTF8.GetBytes(Probe);
                        stream.Write(payload, 0, payload.Length);
                    }

                    stream.Flush();
                }
                catch
                {
                    // Клиент ушёл — для подставного движка это не беда.
                }
            }
        }

        private static (string Method, string Path, string Cookie, string Body) Read(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return (string.Empty, string.Empty, string.Empty, string.Empty);

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            var whole = text.ToString();
            var lines = whole[..headerEnd].Split("\r\n");
            var first = lines[0].Split(' ');

            var length = 0;
            var cookie = string.Empty;

            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), out length);

                if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    cookie = line["Cookie:".Length..].Trim();
            }

            var body = whole[(headerEnd + 4)..];

            while (body.Length < length)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                body += Encoding.UTF8.GetString(buffer, 0, read);
            }

            return (
                first.Length > 0 ? first[0] : string.Empty,
                first.Length > 1 ? first[1] : string.Empty,
                cookie,
                body);
        }
    }

    /// <summary>
    /// НАСТОЯЩИЕ ДВЕРИ КАЛИТКИ ШЛЮТ НАШ КОНВЕРТ, КЛАДУТ COOKIE ДВИЖКА И НЕ РАЗНОСЯТ ТОКЕН ВХОДА.
    ///
    /// ⚠️ Токен входа — секрет (в нём ключ ко всему движку), и он уходит РОВНО в одном месте:
    /// в обмен ссылки на cookie. Ни в вызове, ни в журнале, ни в адресе вызова его быть не должно —
    /// проверка это и стережёт, а не «мы аккуратно».
    /// </summary>
    [Fact]
    public void Настоящие_двери_шлют_наш_конверт_и_не_разносят_токен()
    {
        using var engine = new FakeEngine();
        using var stand = NewStand();

        var log = new List<string>();

        var doors = PhoneGateRealDoors.Build(
            AppPaths.Under(PanelTestStand.TempDir()),
            () => engine.EntryLink,
            () => "127.0.0.1",
            stand.Port,
            bound: _ => { },
            log.Add);

        var answer = doors.Engine("session/list", "{\"_request\":{}}");

        Assert.Equal(200, answer.Status);
        Assert.Contains("\"value\":{\"items\":[]}", answer.Body, StringComparison.Ordinal);

        // ЧТЕНИЕ ОЧЕРЕДИ S3 ТОЖЕ СОБРАНО — иначе D4 отвечала бы «не прочиталась» всегда.
        Assert.NotNull(doors.Control);
        Assert.True(doors.Control!.Present, "вход есть, а дверь очереди говорит «нет входа»");

        // Адрес вызова собран из ссылки входа БЕЗ токена и без пути.
        Assert.Equal("http://127.0.0.1:" + engine.Port, EngineRpcLogin.BaseUrlOf(engine.EntryLink));

        List<SeenCall> seen;

        lock (engine.Seen) seen = engine.Seen.ToList();

        var call = Assert.Single(seen, item => item.Line.StartsWith("POST ", StringComparison.Ordinal));

        Assert.Equal("POST /api/session/list", call.Line);

        // ⚠️ КОНВЕРТ — НАШ, ровно тот, что снят замером: вид, rpcId, метод в теле и доводы
        // внутри `payload.args` (`docs\PHONE-LIVE.md` §2).
        Assert.Contains("\"type\":\"client-request\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"rpcId\":\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"session/list\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"args\":{\"_request\":{}}}", call.Body, StringComparison.Ordinal);

        // Cookie движка приложена — и это cookie движка, а не токен входа.
        Assert.Contains("dsh-auth-probe=", call.Cookie, StringComparison.Ordinal);

        // ⚠️ ТОКЕН ВХОДА — только в обмене ссылки: ни в вызове, ни в адресе, ни в журнале.
        Assert.DoesNotContain(engine.Token, call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(engine.Token, call.Cookie, StringComparison.Ordinal);
        Assert.DoesNotContain(engine.Token, EngineRpcLogin.BaseUrlOf(engine.EntryLink), StringComparison.Ordinal);
        Assert.DoesNotContain(log, line => line.Contains(engine.Token, StringComparison.Ordinal));

        // ...и ровно ОДИН запрос его вообще нёс — тот самый обмен ссылки на cookie.
        var carried = seen
            .Where(item => item.Line.Contains(engine.Token, StringComparison.Ordinal)
                        || item.Body.Contains(engine.Token, StringComparison.Ordinal))
            .ToList();

        var exchange = Assert.Single(carried);

        Assert.StartsWith("GET ", exchange.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВЕРЬ СОБЫТИЙ САМА ВХОДИТ В ДВИЖОК — и без этого подписка не открылась бы НИКОГДА.
    ///
    /// Подписка открывается при запуске калитки, то есть ДО первого вызова телефона, когда своей
    /// cookie у панели ещё нет. С пустой cookie движок отвечает `401` (замер §1), рукопожатие
    /// не состоится — а вызова, который добыл бы вход, может не быть вовсе: человек открыл панель
    /// и ждёт вопроса агента на телефоне. Проверка подаёт подставной движок и требует, чтобы
    /// дверь событий САМА сходила за входом.
    /// </summary>
    [Fact]
    public void Дверь_событий_сама_входит_в_движок()
    {
        using var engine = new FakeEngine();

        var log = new List<string>();

        var doors = PhoneGateRealDoors.Build(
            AppPaths.Under(PanelTestStand.TempDir()),
            () => engine.EntryLink,
            () => "127.0.0.1",
            FreeLoopbackPort(),
            bound: _ => { },
            log.Add);

        // Вызовов движку ещё не было — cookie пуста.
        var link = doors.Events.Open(_ => { });

        // Подставной движок рукопожатия не умеет — подписка не открывается, и это честный ответ...
        Assert.Null(link);

        // ...но ВХОД СОСТОЯЛСЯ: дверь событий сходила за cookie САМА, а не ждала первого вызова.
        //
        // ⚠️ Здесь важна ТОЧНОСТЬ признака, и она далась мутацией (N19 «дверь событий не входит
        // в движок» — первая редакция этой проверки её ПРОПУСТИЛА). Рукопожатие WebSocket — тоже
        // `GET`, поэтому «был GET» ничего не доказывало. Доказывают два признака: обмен ссылки
        // входа (в пути — токен) и cookie движка В ЗАГОЛОВКЕ рукопожатия.
        List<SeenCall> seen;

        lock (engine.Seen) seen = engine.Seen.ToList();

        Assert.Contains(seen, item => item.Line.Contains("token=", StringComparison.Ordinal));

        var handshake = Assert.Single(
            seen, item => item.Line.Contains(EngineMux.MuxPath, StringComparison.Ordinal));

        Assert.Contains("dsh-auth-probe=", handshake.Cookie, StringComparison.Ordinal);
    }

    // ---- ворота по исходникам ---------------------------------------------------------------

    /// <summary>
    /// КАЛИТКА НЕ ПОДНИМАЕТСЯ МИМО ПРАВА — и это проверяется ПО ИСХОДНИКАМ, а не по слову.
    ///
    /// Четыре требования, и каждое ловит свою правку:
    ///
    /// 1. **внутри `PhoneGate.Start` право ОТКРЫТИЯ спрашивается** (<c>RunRights.MayOpenPhoneGate</c>)
    ///    — снятая проверка права не останется незамеченной даже там, где `App` её уже спросил;
    /// 2. **в `App` калитка создаётся под правом ВЛАДЕНИЯ** (<c>RunRights.MayOwnPhoneGate</c>,
    ///    уточнение владельца 11.10.2026): владение — «этот прогон вообще имеет дело с калиткой»,
    ///    и оно не зависит от рубильника, иначе выключенный телефон означал бы «калитки нет вовсе»
    ///    и щелчок рубильника не мог бы её поднять;
    /// 3. **создание живёт ОДНИМ членом** (`App.CreatePhoneGate`), и `StartPanel` зовёт именно его:
    ///    разложи сборку по месту — и проверка «заводится по праву» смотрела бы на что попало;
    /// 4. **`new PhoneGate(` не встречается НИГДЕ, кроме `App` и самого САМОТЕСТА** — иначе вторую
    ///    калитку завели бы мимо права, и слушатель встал бы на машине владельца из чужого места.
    ///
    ///    ⚠️ **Почему самотест назван здесь поимённо, а не «просто добавлен в список».** Право
    ///    открытия изолированному прогону не даёт НИКТО (в этом его смысл), а самотест калитки живёт
    ///    только в изоляции — значит без своего права он не смог бы проверить ничего, кроме отказа.
    ///    Право это ему выдаёт ОДНА строка (`AllowForSelfTest`), и она проверяется отдельно:
    ///    `PhoneSelfTestTests` требует, чтобы калитка БЕЗ этого права отказывала, а `PhoneGate.Start`
    ///    спрашивает право первой строкой. То есть список ниже — не ослабление правила, а перечень
    ///    ДВУХ мест, где калитка заводится: обычный запуск человеком и самотест в изоляции.
    ///    Третьего места быть не должно, и оно падает здесь.
    /// </summary>
    [Fact]
    public void Калитка_заводится_по_праву_и_только_в_App()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var source = Path.Combine(root, "src", "DshPanel");

        // (1) право спрашивается ВНУТРИ запуска калитки.
        var gate = UiThreadTests.WithoutComments(
            File.ReadAllText(Path.Combine(source, "Server", "PhoneGate.cs")));

        // С t2 (DEF-069) запуск разделён: `Start` держит замок, проверки и связывание — в `Open`.
        // Право по-прежнему спрашивается ВНУТРИ запуска — проверяется по телу `Open`, а `Start`
        // обязан делегировать именно туда (иначе «Start обходит право» вернулся бы, не тронув проверку).
        var start = WindowlessTests.BodyAt(gate, "public PhoneGateStart Start(");

        Assert.False(string.IsNullOrWhiteSpace(start), "тело запуска калитки не найдено — проверять нечего");
        Assert.Contains("Open()", start, StringComparison.Ordinal);

        var open = WindowlessTests.BodyAt(gate, "private PhoneGateStart Open(");

        Assert.False(string.IsNullOrWhiteSpace(open), "тело самого запуска не найдено — проверять нечего");
        Assert.Contains("RunRights.MayOpenPhoneGate", open, StringComparison.Ordinal);
        Assert.Contains("PortForbidden", open, StringComparison.Ordinal);

        // (2) и (3) — по исходникам панели.
        var files = Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Select(path => new SourceStringsTests.SourceFile(path, File.ReadAllText(path)))
            .ToList();

        var creations = new List<string>();

        foreach (var file in files)
        {
            var code = UiThreadTests.WithoutComments(file.Text);

            if (code.Contains("new PhoneGate(", StringComparison.Ordinal))
                creations.Add(Path.GetFileName(file.Path));
        }

        Assert.Equal(
            new[] { "App.axaml.cs", "PhoneSelfTest.cs" },
            creations.OrderBy(name => name, StringComparer.Ordinal));

        var app = UiThreadTests.WithoutComments(
            File.ReadAllText(Path.Combine(source, "App.axaml.cs")));

        // (2) ВЛАДЕНИЕ спрашивается там, где калитка создаётся, и НЕ зависит от рубильника.
        var factory = WindowlessTests.BodyAt(app, "internal static PhoneGate? CreatePhoneGate(");

        Assert.False(string.IsNullOrWhiteSpace(factory), "тело сборки калитки не найдено — проверять нечего");
        Assert.Contains("RunRights.MayOwnPhoneGate", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MayOpenPhoneGate", factory, StringComparison.Ordinal);

        // (3) И `StartPanel` зовёт ИМЕННО этот член, а не собирает калитку у себя.
        var panel = WindowlessTests.BodyAt(app, "private void StartPanel(");

        Assert.Contains("RunRights.MayOwnPhoneGate", panel, StringComparison.Ordinal);
        Assert.Contains("CreatePhoneGate(", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("new PhoneGate(", panel, StringComparison.Ordinal);
        Assert.Contains("PhoneGateRefusedLogFormat", panel, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРАВО САМОТЕСТА ВЫДАЁТСЯ РОВНО В ОДНОМ МЕСТЕ.
    ///
    /// `PhoneGate.AllowForSelfTest` — дверь для самотеста: настоящего права в изоляции нет ни у кого
    /// (в этом его смысл), а самотест без двери проверил бы только отказ. Значит дверь обязана быть
    /// ОДНА и названа поимённо: одно присваивание в `PhoneSelfTest.cs` и ни одного в продуктовом коде.
    /// Иначе «право самотеста» стало бы настройкой, которую однажды выставили бы при обычном запуске —
    /// и калитка встала бы на машине владельца мимо права.
    ///
    /// Проверка читает ИСХОДНИКИ, а не поведение: поведением здесь доказать нечего — важна не реакция
    /// на флаг, а то, что выставить его некому, кроме самотеста.
    /// </summary>
    [Fact]
    public void Право_самотеста_выдаётся_ровно_в_одном_месте()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var source = Path.Combine(root, "src", "DshPanel");
        var holders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var code = UiThreadTests.WithoutComments(File.ReadAllText(path));

            if (code.Contains("AllowForSelfTest = true", StringComparison.Ordinal))
                holders.Add(Path.GetFileName(path));
        }

        Assert.Equal(new[] { "PhoneSelfTest.cs" }, holders.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// ДВЕРЬ СОБЫТИЙ — ДВЕРЬ, А НЕ СНИМОК: вход, появившийся ПОЗЖЕ, доезжает до калитки
    /// БЕЗ перезапуска панели (живой дефект 14.10.2026, ночь).
    ///
    /// **Что произошло у владельца.** Он нажал «Поднять свой сервер заново» — и это сработало:
    /// свой сервер поднят, ссылка входа получена и сохранена, после перезапуска панели сервер узнан
    /// по записи (`state\own-server.txt`), `state\entry-link.txt` на месте. А панель говорила
    /// обратное: раздел «Телефон» — «связи с сервером DSH нет: вопросы агента на телефон не придут»,
    /// «Обновление» — «он поднят не панелью», а калитка каждые ~65 с повторяла
    /// <c>PhoneEventDoorsMissing</c>. Настоящий телефон владельца сопрягся — и не получил ничего.
    ///
    /// **Почему.** Признак «вход есть» считался ОДИН РАЗ, когда двери собирались, то есть на старте
    /// панели. В этот миг ссылки ещё нет: контроллер отдаёт её только при `Owner == Panel` и
    /// `_port > 0`, а свой сервер узнаётся по записи секундой позже. Значит признак навсегда
    /// оставался `false` — и калитка не открывала подписку ВООБЩЕ.
    ///
    /// ⚠️ Проверка водит ЖИВОЙ контроллер: запись о своём сервере и ссылка входа лежат в его
    /// файлах (подставных, как в `OwnServerTests`), двери собирает НАСТОЯЩИЙ
    /// <see cref="PhoneGateRealDoors.Build"/>, и вопрос «есть ли вход» задаётся двери ДО и ПОСЛЕ
    /// того, как контроллер узнал свой сервер.
    ///
    /// Мутация, которая обязана упасть здесь: **вернуть снимок `entryLink()` при сборке дверей**
    /// (`EngineMux.DoorsWhen` → `EngineMux.Doors` с проверкой на месте) → <c>EngineLinked</c>
    /// остаётся `false` навсегда, и проверка падает на первом же утверждении после узнавания.
    /// </summary>
    [Fact]
    public void Вход_появившийся_после_старта_доезжает_до_калитки_без_перезапуска()
    {
        // --- состояние панели: свой сервер ПОДНЯТ (запись о нём есть), ссылка входа СОХРАНЕНА ---
        const int port = 3097;
        const int pid = 4242;
        const long ticks = 638_000_000_000_000_000;
        const string link = "http://127.0.0.1:3097/?token=0123456789abcdef";

        // ⚠️ ССЫЛКА ВХОДА ЛЕЖИТ В ФАЙЛЕ С САМОГО НАЧАЛА — как у владельца (файл 72 б, записан
        // в 00:09:49, панель перезапущена в 00:12:11). Неизвестна она панели ПОТОМУ, что свой
        // сервер ещё не узнан: контроллер отдаёт ссылку только при `Owner == Panel` и `_port > 0`.
        var entry = link;
        var own = new OwnServerRecord(port, pid, ticks).Format();

        var state = new ServerStateFiles(
            ReadEntryLink: () => entry,
            WriteEntryLink: url => entry = url,
            ClearEntryLink: () => entry = string.Empty,
            ReadOwnServer: () => own,
            WriteOwnServer: text => own = text,
            ClearOwnServer: () => own = string.Empty);

        // ⚠️ СЛУШАТЕЛЯ НЕТ ДО ПОРЫ: `ListenerPid = 0` — это ровно то положение, в котором панель
        // стоит в миг сборки дверей (состояние сервера ещё не прочитано). Появится он ПОСЛЕ того,
        // как панель перечитает состояние, — и именно тогда ссылка входа становится известной.
        var listener = 0;

        var probe = new ServerProbe(
            ListenerPid: _ => listener,
            Listeners: () => listener == 0 ? null : new[] { new PortTable.Listener(port, listener) },
            CommandLine: _ => string.Empty,
            ProcessName: _ => "node",
            StartTicks: _ => ticks,
            AnswersFingerprint: _ => true);

        var log = new List<string>();

        using var controller = new ServerController(
            @"C:\Temp\dsh-lazy-home",
            () => @"C:\Temp",
            log.Add,
            mayOccupyOwnerPort: false,
            locateEngine: () => null,
            killer: new SilentKiller(),
            probe: probe,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            files: state,
            ownEngineDir: () => @"C:\Temp\dsh-lazy-home\engine");

        // --- двери калитки: НАСТОЯЩИЕ, собранные ровно так, как их собирает панель ---
        var gatePort = FreeLoopbackPort();

        var doors = PhoneGateRealDoors.Build(
            AppPaths.Under(PanelTestStand.TempDir()),
            () => controller.EntryLink,
            () => "127.0.0.1",
            gatePort,
            _ => { },
            log.Add,

            // ДВЕРЬ СЕРВЕРА — ТА ЖЕ, ЧТО У ПАНЕЛИ: по ней калитка знает, прочитано ли состояние
            // (задание владельца 14.10.2026, §1).
            server: () => controller);

        using var gate = new PhoneGate(
            doors, () => true, humanLaunch: true, RunContext.Create(RunRequest.Owner()));

        // (1) ДО УЗНАВАНИЯ: входа нет — и про связь НЕ СКАЗАНО НИЧЕГО. «Связи нет» — это ответ,
        // а состояние сервера ещё не прочитано: молчание значит «ещё не знаем» (правка 14.10.2026,
        // §1: прежде здесь уходила в журнал ложная строка про работающий СВОЙ сервер).
        Assert.Equal(string.Empty, controller.EntryLink);
        Assert.False(gate.EngineLinked, "вход ещё не появился, а калитка считает, что он есть");

        try
        {
            Assert.True(gate.Start().Opened, "калитка не встала на петле — мерить нечего");

            gate.EnsureSubscription();

            Assert.DoesNotContain(
                PanelStrings.PhoneEventDoorsMissing,
                string.Join("\n", log),
                StringComparison.Ordinal);

            // (2) ПАНЕЛЬ ПЕРЕЧИТАЛА СОСТОЯНИЕ: свой сервер узнан по записи — и вход сразу известен.
            listener = pid;

            var restored = controller.Refresh();

            Assert.Equal(ServerOwner.Panel, controller.Owner);
            Assert.Equal(port, restored.Port);
            Assert.Equal(link, controller.EntryLink);

            // (3) ГЛАВНОЕ: признак живой. Двери СОБРАНЫ РАНЬШЕ (на старте панели) и не пересобраны —
            // но вход, появившийся позже, доехал до калитки САМ.
            Assert.True(
                gate.EngineLinked,
                "вход появился, а калитка по-прежнему считает, что связи с сервером DSH нет: " +
                "признак снят один раз при сборке дверей, а не спрашивается сейчас");

            var before = log.Count;
            gate.EnsureSubscription();

            // Подписка ЧИТАЕТСЯ: калитка больше не говорит «вопрос показать нечем», а идёт
            // открывать поток (адрес у нас — петля без движка, поэтому дальше будет отказ СВЯЗИ,
            // и это правильный ответ: дверь есть, движка нет).
            Assert.DoesNotContain(
                PanelStrings.PhoneEventDoorsMissing,
                string.Join("\n", log.Skip(before)),
                StringComparison.Ordinal);
        }
        finally
        {
            gate.Stop();
        }
    }

    /// <summary>
    /// СОСТОЯНИЕ СЕРВЕРА СТАЛО ИЗВЕСТНО — ПОДПИСКА ОТКРЫВАЕТСЯ СРАЗУ, И БЕЗ ЛОЖНОЙ СТРОКИ О СВЯЗИ.
    ///
    /// **От какого случая (пачка долгов 14.10.2026, §1).** В журнале владельца, 14.10.2026 23:53:43,
    /// в первую же секунду запуска: «связи с сервером DSH нет — вопрос агенту телефону показать
    /// нечем». Это была **ложь**: калитка спрашивает подписку ПРИ ЗАПУСКЕ, то есть раньше, чем
    /// панель прочитает состояние сервера, — ссылки входа в этот миг ещё нет. Опаснее строки то,
    /// что движок события **не переигрывает**, а следующая попытка шла на часовом такте (5 с):
    /// вопрос, заданный в эти секунды, до телефона не доезжал вовсе.
    ///
    /// ⚠️ Две половины одной беды, и обе меряются в одном прогоне:
    /// (1) пока состояние **не прочитано** — про связь не говорится НИЧЕГО (молчание значит
    /// «ещё не знаем», а не «всё хорошо»);
    /// (2) как только оно стало известно — подписка открывается **тут же, по сообщению**.
    /// В проверке нет ни одного ожидания и ни одного такта часов: если бы подписка ждала такта,
    /// `Open` не позвался бы до конца прогона, и второе утверждение упало бы.
    ///
    /// Мутации, которые обязаны упасть здесь: **«писать строку о связи, не глядя на прочитанность»**
    /// (`EnsureSubscription` без проверки `ServerKnown`) · **«не подписываться на сообщение
    /// о состоянии сервера»** (снять `WatchServer(watch: true)`).
    /// </summary>
    [Fact]
    public void Состояние_стало_известно_подписка_открывается_сразу_без_ложной_строки()
    {
        // ПОДСТАНОВКА СЕРВЕРА: состояние ещё НЕ прочитано (`Known = false`), входа нет.
        var server = new PanelTestStand.StubServer(ServerState.Stopped(0)) { Known = false };

        var link = false;
        var opened = 0;

        var events = PhoneEventDoors.When(
            canOpen: () => link,
            open: _ =>
            {
                opened++;
                return null;
            },
            log: _ => { });

        using var stand = NewStand(events: events, server: () => server);

        Assert.True(stand.Gate.Start().Opened, "калитка не встала — мерить нечего");

        // (1) СОСТОЯНИЕ ЕЩЁ НЕ ПРОЧИТАНО: про связь не сказано НИЧЕГО.
        Assert.Equal(0, opened);
        Assert.DoesNotContain(
            PanelStrings.PhoneEventDoorsMissing,
            string.Join("\n", stand.Log),
            StringComparison.Ordinal);

        // (2) СОСТОЯНИЕ СТАЛО ИЗВЕСТНО — тем же сообщением, каким о смене говорит контроллер
        // сервера: свой сервер узнан, ссылка входа есть. Подписка обязана открыться СРАЗУ.
        link = true;
        server.Known = true;
        server.Signal();

        Assert.Equal(1, opened);
    }

    /// <summary>
    /// ДВА ИЗМЕНЕНИЯ ПРОПУСКА НЕ ЗАТИРАЮТ ДРУГ ДРУГА — «прочитать, изменить, записать» идёт
    /// ПОД ЗАМКОМ (дефект `CH-01` цикла C1, пачка D).
    ///
    /// **Что было.** Писателей пропуска трое — сопряжение, «отвязать это устройство» и «отвязать
    /// все», — и **все три** ходили мимо замка: прочитать, изменить в памяти, записать. Два
    /// одновременных изменения читают ОДНО состояние, и чья запись легла последней, тот и прав.
    /// Человек отвязал два устройства — вернулось одно; выключил телефон, а старое устройство
    /// снова пускает. Это не теория: отвязка из окна и сопряжение с телефона идут одновременно
    /// ровно тогда, когда человек этим и занят.
    ///
    /// **Как проверяется.** Дверь пропуска подменена: чтение сообщает, что вошло, и **стоит, пока
    /// его не отпустят**. Пока первое изменение сидит в чтении, запускается второе — и обязан
    /// пройти ровно один вход: второй ждёт замка, а не читает то же самое состояние.
    /// После отпускания оба устройства обязаны исчезнуть: ни одно изменение не потерялось.
    ///
    /// **Мутация, которая обязана упасть здесь:** снять замок `_pass` с отвязки — `CF11`.
    /// </summary>
    [Fact]
    public async Task Два_изменения_пропуска_не_затирают_друг_друга()
    {
        var root = PanelTestStand.TempDir();
        var inside = 0;
        var released = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);

        try
        {
            var paths = AppPaths.Under(root);
            var real = PhonePassFiles.Under(paths, _ => { });
            var stamp = Pricing.PricingDecisions.Stamp(DateTimeOffset.Now);

            Assert.True(
                PhonePass.Create()
                    .WithDevice("phone-1", "192.168.0.10", stamp)
                    .WithDevice("phone-2", "192.168.0.11", stamp)
                    .Save(real),
                "исходный пропуск не записался — мерить нечего");

            var gated = real with
            {
                Read = () =>
                {
                    Interlocked.Increment(ref inside);
                    entered.Set();
                    released.Wait(TimeSpan.FromSeconds(10));

                    return real.Read();
                },
            };

            using var stand = new Stand(
                root, enabled: true, human: true, isolated: false, address: "127.0.0.1", port: null,
                events: new FakeEvents().Doors, passFiles: gated);

            var first = Task.Run(() => stand.Gate.ForgetDevice("phone-1"), TestContext.Current.CancellationToken);

            Assert.True(
                entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "первое изменение не дошло до чтения");

            var second = Task.Run(() => stand.Gate.ForgetDevice("phone-2"), TestContext.Current.CancellationToken);

            // ВТОРОЙ ЖДЁТ ЗАМКА: за время, за которое он заведомо успел бы войти, вход всё ещё один.
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref inside));

            released.Set();

            Assert.True(await first, "первая отвязка не записалась");
            Assert.True(await second, "вторая отвязка не записалась");

            // НИ ОДНО ИЗМЕНЕНИЕ НЕ ПОТЕРЯЛОСЬ: устройств не осталось.
            Assert.Equal(0, PhonePass.Load(real).DeviceCount);
        }
        finally
        {
            released.Set();
            PanelTestStand.RemoveTemp(root);
        }
    }

    // ---- S1: список сессий и живой поток -------------------------------------------------------

    /// <summary>ЭКРАНИРОВАТЬ СТРОКУ ДЛЯ ПОДСТАВНОГО ОТВЕТА: путь в JSON идёт с удвоенной косой.</summary>
    private static string J(string text) => (text ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// ЧТЕНИЕ ЖИВОГО ПОТОКА В ПРОВЕРКЕ: свой TCP-клиент — `HttpClient` незавершённый ответ
    /// не отдаёт. Помнит ВСЁ, что пришло, и умеет дождаться метки: порядок событий проверяется
    /// по нему, а не по нашим надеждам.
    /// </summary>
    private sealed class SseClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StringBuilder _seen = new();

        public SseClient(Stand stand, string path, string? cookie)
        {
            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, stand.Port);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 250;

            var head = "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + stand.Port + "\r\n";

            if (cookie is not null) head += "Cookie: " + PhoneGate.CookieName + "=" + cookie + "\r\n";

            head += "\r\n";

            var bytes = Encoding.UTF8.GetBytes(head);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        public string Seen => _seen.ToString();

        /// <summary>Ждать, пока в потоке появится метка; вернуть ВСЁ, что успело прийти.</summary>
        public async Task<string> WaitFor(string marker, int timeoutMs = 6000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                Pump();

                if (_seen.ToString().Contains(marker, StringComparison.Ordinal)) return _seen.ToString();

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            return _seen.ToString();
        }

        public string Pump()
        {
            var buffer = new byte[8192];

            try
            {
                var read = _stream.Read(buffer, 0, buffer.Length);

                if (read > 0) _seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (IOException)
            {
                // Срок чтения: новых байт пока нет — это не ошибка проверки.
            }

            return _seen.ToString();
        }

        /// <summary>Сколько раз метка встретилась в том, что уже пришло (счётчик кадров, G1).</summary>
        public int Count(string marker) =>
            Seen.Split(marker, StringSplitOptions.None).Length - 1;

        /// <summary>Ждать, пока меток станет НЕ МЕНЬШЕ <paramref name="count"/>; вернуть, сколько их.</summary>
        public async Task<int> WaitForCount(string marker, int count, int timeoutMs = 6000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                Pump();

                var seen = Count(marker);
                if (seen >= count) return seen;

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            return Count(marker);
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// СПИСОК СЕССИЙ — СВОИ ОБЪЕКТЫ ПО БЕЛОМУ СПИСКУ (S1, контракт §2.1).
    ///
    /// **От какого случая.** Прежняя дверь отдавала строки движка КАК ЕСТЬ, и телефону уезжали
    /// `cwd` и `projections.workingDirectory` (поправка §0.4). Здесь проверяется противное:
    /// в ответе есть РОВНО разрешённые поля, подчинённые убраны, мусорная строка отброшена,
    /// очерк — три последние записи, статистика — два числа, название — не длиннее 200.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть строку движка как есть»** (в теле появится
    /// `cwd`/`workingDirectory`) и **«не фильтровать подчинённые»** (в теле появится `session-sub`).
    /// </summary>
    [Fact]
    public async Task Список_сессий_собирается_по_белому_списку()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var machine = Environment.MachineName;
        var longTitle = new string('т', 260);

        stand.Reply = new PhoneEngineAnswer(
            200,
            "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":{\"items\":["
            + "{\"sessionId\":\"session-1\",\"updatedAt\":1761770000000,\"running\":true,\"blank\":false,"
            + "\"cwd\":\"" + J(home) + "\\\\work\",\"projections\":{\"values\":{\"title\":\"" + longTitle + "\","
            + "\"sessionStats\":{\"turns\":3,\"steps\":9,\"llmMs\":5},"
            + "\"turnOutline\":[{\"turn\":1,\"seq\":1,\"prompt\":\"п1\",\"response\":\"о1\"},"
            + "{\"turn\":2,\"seq\":2,\"prompt\":\"п2\",\"response\":\"о2\"},"
            + "{\"turn\":3,\"seq\":3,\"prompt\":\"п3\",\"response\":\"о3\"},"
            + "{\"turn\":4,\"seq\":4,\"prompt\":\"п4\",\"response\":\"о4\"}],"
            + "\"inbox\":{\"next-turn\":[{\"id\":\"m1\"}],\"next-step\":[]},"
            + "\"workingDirectory\":\"" + J(home) + "\"}}},"
            + "{\"sessionId\":\"session-2\",\"updatedAt\":1761700000000,\"running\":false,\"blank\":true,"
            + "\"projections\":{\"values\":{\"title\":\"Коротко\"}}},"
            + "{\"sessionId\":\"session-sub\",\"parentSessionId\":\"session-1\",\"origin\":\"subagent\","
            + "\"updatedAt\":1761770000000,\"running\":true,\"blank\":false},"
            + "\"мусор\""
            + "]}}}");

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"ok\":true", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"total\":2", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"shown\":2", reply.Body, StringComparison.Ordinal);
        Assert.Contains("session-1", reply.Body, StringComparison.Ordinal);
        Assert.Contains("session-2", reply.Body, StringComparison.Ordinal);

        // ЗАПРЕЩЁННОЕ — ИМЕНАМИ, А НЕ «ПОХОЖЕ НЕТ»: каждое из этих полей есть в ответе движка.
        foreach (var banned in new[] { "session-sub", "parentSessionId", "origin", "cwd", "workingDirectory", "llmMs", home, machine })
            Assert.DoesNotContain(banned, reply.Body, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(reply.Body);
        var items = json.RootElement.GetProperty("items");

        Assert.Equal(2, items.GetArrayLength());

        var first = items[0];
        var names = first.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(9, names.Count);

        foreach (var wanted in new[] { "sessionId", "title", "updatedAt", "running", "blank", "question", "stats", "inbox", "outline" })
            Assert.Contains(wanted, names);

        Assert.Equal(200, first.GetProperty("title").GetString()!.Length);
        Assert.True(first.GetProperty("running").GetBoolean());
        Assert.False(first.GetProperty("question").GetBoolean());
        Assert.Equal(
            new[] { "turns", "steps" },
            first.GetProperty("stats").EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, first.GetProperty("outline").GetArrayLength());
        Assert.Equal("п4", first.GetProperty("outline")[2].GetProperty("prompt").GetString());
        Assert.Equal(1, first.GetProperty("inbox").GetProperty("nextTurn").GetInt32());
        Assert.Equal(0, first.GetProperty("inbox").GetProperty("nextStep").GetInt32());
    }

    /// <summary>
    /// ЛИМИТ 50: `total` — сколько корневых ДО лимита, `shown` — сколько отдано. Постраничности
    /// в S1 нет: телефон говорит «Показаны последние 50 из N» (контракт §2.1).
    /// </summary>
    [Fact]
    public async Task Список_сессий_отдаёт_не_больше_пятидесяти_и_называет_всего()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var rows = string.Join(",", Enumerable.Range(1, 60).Select(index =>
            "{\"sessionId\":\"session-" + index + "\",\"updatedAt\":" + (1761770000000 - index)
            + ",\"running\":false,\"blank\":false}"));

        stand.Reply = new PhoneEngineAnswer(
            200, "{\"result\":{\"ok\":true,\"value\":{\"items\":[" + rows + "]}}}");

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, reply.Status);

        using var json = JsonDocument.Parse(reply.Body);

        Assert.Equal(60, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(50, json.RootElement.GetProperty("shown").GetInt32());
        Assert.Equal(50, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Contains("session-50", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("session-51", reply.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// «ЖДЁТ ОТВЕТА» В СПИСКЕ — ИЗ ВИСЯЩЕГО ВОПРОСА КАЛИТКИ, а не из догадки (контракт §2.1, п. 5):
    /// строка той сессии, чей агент спросил, получает `question: true`; соседняя — `false`.
    /// </summary>
    [Fact]
    public async Task Список_помечает_сессию_с_висящим_вопросом()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\"}]}}}");

        stand.Reply = new PhoneEngineAnswer(
            200, "{\"result\":{\"ok\":true,\"value\":{\"items\":["
            + "{\"sessionId\":\"session-1\",\"updatedAt\":1,\"running\":true,\"blank\":false},"
            + "{\"sessionId\":\"session-2\",\"updatedAt\":2,\"running\":false,\"blank\":false}"
            + "]}}}");

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, reply.Status);

        using var json = JsonDocument.Parse(reply.Body);
        var items = json.RootElement.GetProperty("items");

        Assert.True(items[0].GetProperty("question").GetBoolean(), "сессия с висящим вопросом не помечена");
        Assert.False(items[1].GetProperty("question").GetBoolean(), "чужая сессия помечена как ждущая");
    }

    /// <summary>
    /// ОТКАЗЫ СПИСКА — СЛОВАМИ И КОДОМ (контракт §2.11): нет входа — 401; чужая дверь — 403;
    /// движок молчит/ответил не той формой/прислал ошибку — 502; пустая установка — 200 и пусто.
    /// </summary>
    [Fact]
    public async Task Список_сессий_отказывает_словами_а_не_пустотой()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var noCookie = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions");

        Assert.Equal(401, noCookie.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateUnauthorized), noCookie.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);

        var foreign = await Send(
            stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie, host: "evil.example:80");

        Assert.Equal(403, foreign.Status);
        Assert.DoesNotContain("evil.example", foreign.Body, StringComparison.Ordinal);

        // ДВИЖОК МОЛЧИТ.
        stand.Reply = PhoneEngineAnswer.Failed("молчит");

        var silent = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(502, silent.Status);
        Assert.Contains(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateEngineSilentFormat, "молчит"),
            silent.Body, StringComparison.Ordinal);

        // ОТВЕТ НЕ ТОЙ ФОРМЫ — «не прочитался», а НЕ «пусто».
        stand.Reply = new PhoneEngineAnswer(200, "это не json");

        var broken = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(502, broken.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebListUnreadable), broken.Body, StringComparison.Ordinal);

        // ДВИЖОК ПРИСЛАЛ ОТКАЗ — код в журнал, фраза телефону.
        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"error\":{\"code\":\"gateway/bad\"}}}");

        var refused = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(502, refused.Status);
        Assert.DoesNotContain("gateway", refused.Body, StringComparison.Ordinal);

        // ПУСТАЯ УСТАНОВКА — ЗАКОННЫЙ ПУСТОЙ СПИСОК.
        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"ok\":true}}");

        var empty = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, empty.Status);

        using var json = JsonDocument.Parse(empty.Body);

        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("shown").GetInt32());
    }

    /// <summary>
    /// ЖИВОЙ ПОТОК: `snapshot`, записи, недописанный ответ — И НИ ОДНОГО ЗАПРЕЩЁННОГО ПОЛЯ.
    ///
    /// **От какого случая.** Движок кладёт в заголовок `cwd`/`parentSession`, в проекции —
    /// `workingDirectory`, в журнал — `system/message` и аргументы инструментов. Всё это —
    /// пути и служебный текст, которым нечего делать на телефоне (контракт §1.6, §2.2).
    ///
    /// Мутации, которые обязаны упасть здесь: **«отдать кадр движка как есть»** (появится `cwd`),
    /// **«отправить thinking инструментов»** (появится `arguments`/вывод) и **«не разворачивать
    /// черновик»** (текста черновика не будет).
    /// </summary>
    [Fact]
    public async Task Живой_поток_отдаёт_свои_кадры_и_не_отдаёт_путей()
    {
        var follow = new FakeFollow();

        using var stand = NewStand(follow: follow.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var machine = Environment.MachineName;

        using var sse = new SseClient(stand, "/api/phone/live?session=session-1", cookie);

        Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

        for (var attempt = 0; attempt < 100 && follow.Opens == 0; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, follow.Opens);
        Assert.Equal("session-1", follow.Session);

        var snapshot = "{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{"
            + "\"type\":\"snapshot\","
            + "\"header\":{\"version\":4,\"id\":\"session-1\",\"createdAt\":1761770000000,"
            + "\"cwd\":\"" + J(home) + "\\\\work\",\"parentSession\":\"session-0\"},"
            + "\"cursor\":35,\"hasMore\":true,"
            + "\"records\":["
            + "{\"type\":\"event\",\"event\":{\"type\":\"user/message\",\"seq\":36,\"time\":1761770001000,"
            + "\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"привет из " + J(home) + "\\\\work\"}],\"source\":{\"kind\":\"user\"}}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"user/message\",\"seq\":37,\"time\":1761770002000,"
            + "\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"служебный текст\"}],\"source\":{\"kind\":\"system-prompt\"}}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"system/message\",\"seq\":38,\"time\":1761770003000,"
            + "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"системный запрос\"}]}}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"turn/start\",\"seq\":39,\"time\":1761770004000,\"data\":{\"turn\":2}}}"
            + "],"
            + "\"projections\":{\"asOfSeq\":35,\"values\":{\"title\":\"Разговор\","
            + "\"workingDirectory\":\"" + J(home) + "\"}},"
            + "\"assistantStream\":{\"revision\":1,\"activeAttempt\":{\"attemptId\":\"a1\",\"startedAfterSeq\":30,"
            + "\"turn\":2,\"step\":1,\"nextIndex\":2,\"stream\":["
            + "{\"type\":\"text-chunks\",\"texts\":[\"черно\",\"вик\"]},"
            + "{\"type\":\"chunk\",\"chunk\":{\"type\":\"reasoning\",\"text\":\"рассуждение не слать\"}}]}}}}";

        follow.Emit(snapshot);

        var text = await sse.WaitFor("event: snapshot");

        Assert.Contains("event: snapshot", text, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Разговор\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"user\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"turn\"", text, StringComparison.Ordinal);
        Assert.Contains("привет из ~", text, StringComparison.Ordinal);
        Assert.Contains("черновик", text, StringComparison.Ordinal);

        foreach (var banned in new[]
                 {
                     "cwd", "parentSession", "workingDirectory", "system/message", "системный запрос",
                     "служебный текст", "рассуждение не слать", "reasoning", J(home), machine, "session-0",
                 })
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);

        // ЖУРНАЛЬНЫЕ ЗАПИСИ: слово агента, инструмент без доводов и исхода, конец хода.
        follow.Emit("{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{\"type\":\"event\","
            + "\"event\":{\"type\":\"assistant/message\",\"seq\":40,\"time\":1761770005000,"
            + "\"data\":{\"turn\":2,\"step\":1,\"message\":{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"ответ агента\"},{\"type\":\"reasoning\",\"text\":\"не слать\"}]},"
            + "\"stream\":[{\"type\":\"text-chunks\",\"texts\":[\"не слать\"]}]}}}}");

        follow.Emit("{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{\"type\":\"event\","
            + "\"event\":{\"type\":\"tool/call\",\"seq\":41,\"time\":1761770006000,"
            + "\"data\":{\"turn\":2,\"step\":1,\"callId\":\"c1\",\"name\":\"bash\","
            + "\"arguments\":\"rm -rf " + J(home) + "\\\\секрет\"}}}}");

        follow.Emit("{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{\"type\":\"event\","
            + "\"event\":{\"type\":\"tool/result\",\"seq\":42,\"time\":1761770007000,"
            + "\"data\":{\"turn\":2,\"step\":1,\"message\":{\"role\":\"tool\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"вывод инструмента\"}],"
            + "\"toolCallId\":\"c1\",\"isError\":true},\"meta\":{\"diff\":\"не слать\"}}}}}");

        follow.Emit("{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{\"type\":\"event\","
            + "\"event\":{\"type\":\"turn/end\",\"seq\":43,\"time\":1761770008000,"
            + "\"data\":{\"turn\":2,\"reason\":{\"kind\":\"completed\"}}}}}");

        text = await sse.WaitFor("\"type\":\"turn-end\"");

        Assert.Contains("\"type\":\"assistant\"", text, StringComparison.Ordinal);
        Assert.Contains("ответ агента", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"tool-call\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"bash\"", text, StringComparison.Ordinal);
        Assert.Contains("\"callId\":\"c1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"failed\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"completed\"", text, StringComparison.Ordinal);

        foreach (var banned in new[] { "arguments", "rm -rf", "вывод инструмента", "meta", "diff", "не слать" })
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);

        // НЕДОПИСАННЫЙ ОТВЕТ: только текст, без `reasoning` и служебных кадров.
        follow.Emit("{\"type\":\"item\",\"streamId\":\"phone-live-1\",\"value\":{\"type\":\"assistant-stream\","
            + "\"frame\":{\"type\":\"chunk\",\"attemptId\":\"a1\",\"revision\":1,\"index\":2,\"time\":1761770009000,"
            + "\"chunk\":{\"type\":\"text-delta\",\"text\":\"кусочек ответа\"}}}}");

        text = await sse.WaitFor("кусочек ответа");

        Assert.Contains("event: assistant", text, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"text\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОБРЫВ ТЕЛЕФОНА ЗАКРЫВАЕТ ПОТОК ДВИЖКА: одно SSE — один `WS`, и он не должен переживать
    /// экран (контракт §2.2). Об уходе клиента узнаётся ЗАПИСЬЮ: телефон закрыл сокет, калитка
    /// шлёт ему кадр, запись не удалась — ссылка закрыта.
    ///
    /// Мутация, которая обязана упасть здесь: **«не закрывать поток движка при обрыве»** — тогда
    /// `Closed` останется `false`.
    /// </summary>
    [Fact]
    public async Task Обрыв_телефона_закрывает_поток_движка()
    {
        var follow = new FakeFollow();

        using var stand = NewStand(follow: follow.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var sse = new SseClient(stand, "/api/phone/live?session=session-1", cookie);

        try
        {
            Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

            for (var attempt = 0; attempt < 100 && follow.Opens == 0; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(1, follow.Opens);

            follow.Emit("{\"type\":\"item\",\"value\":{\"type\":\"snapshot\",\"header\":{\"id\":\"session-1\"},"
                + "\"cursor\":1,\"hasMore\":false,\"records\":[]}}");

            Assert.Contains("event: snapshot", await sse.WaitFor("event: snapshot"));

            sse.Dispose();

            var record = "{\"type\":\"item\",\"value\":{\"type\":\"event\",\"event\":{\"type\":\"turn/start\","
                + "\"seq\":2,\"time\":1761770000000,\"data\":{\"turn\":1}}}}";

            var deadline = DateTime.UtcNow.AddSeconds(8);

            while (!follow.Closed && DateTime.UtcNow < deadline)
            {
                follow.Emit(record);
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(follow.Closed, "поток движка пережил уход телефона");
        }
        finally
        {
            sse.Dispose();
        }
    }

    /// <summary>
    /// ПРЕДЕЛ ЖИЗНИ ЖИВОГО ПОТОКА — 30 МИНУТ (контракт §2.2): телефон переподключается и берёт
    /// свежий `snapshot`. Проверка ДВИГАЕТ ЧАСЫ калитки (`stand.Now`), а не спит полчаса — иначе
    /// такая проверка не стоила бы ничего.
    /// </summary>
    [Fact]
    public async Task Живой_поток_кончается_по_пределу_жизни()
    {
        var follow = new FakeFollow();

        using var stand = NewStand(follow: follow.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        using var sse = new SseClient(stand, "/api/phone/live?session=session-1", cookie);

        Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

        for (var attempt = 0; attempt < 100 && follow.Opens == 0; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, follow.Opens);

        // ЧАСЫ ВПЕРЁД — за предел жизни; любой кадр будит поток, и он прощается с причиной.
        //
        // ⚠️ ЭТО НЕ ГОНКА, и держится она порядком В КАЛИТКЕ: `RunLiveAsync` назначает `end` ДО
        // `follow.Open` (флейк F1 ревью t9 был ровно в обратном порядке — тест мог опередить
        // назначение срока). Значит `Opens == 1` и есть точка «срок уже назначен», а сон или
        // повторные попытки здесь не нужны: двигать часы раньше нельзя, позже — можно всегда.
        stand.Now += PhoneGate.LiveLifetime + TimeSpan.FromSeconds(1);

        follow.Emit("{\"type\":\"item\",\"value\":{\"type\":\"event\",\"event\":{\"type\":\"system/message\","
            + "\"seq\":1,\"time\":1,\"data\":{}}}}");

        var text = await sse.WaitFor("event: end");

        Assert.Contains("event: end", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"lifetime\"", text, StringComparison.Ordinal);
        Assert.True(await ClosedEnough(follow), "поток движка не закрылся после предела жизни");
    }

    /// <summary>Дождаться закрытия подставного потока: закрытие идёт в `finally`, уже после события.</summary>
    private static async Task<bool> ClosedEnough(FakeFollow follow)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (follow.Closed) return true;

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return follow.Closed;
    }

    /// <summary>
    /// ОТКАЗЫ ЖИВОГО ПОТОКА ДО ЗАГОЛОВКОВ: 401 без входа, 400 плохой адрес, 429 больше четырёх
    /// экранов (контракт §2.2). После заголовков сказать это уже нельзя — `EventSource` тела
    /// ошибки не читает, поэтому всё, что можно, спрашивается ДО потока.
    /// </summary>
    [Fact]
    public async Task Живой_поток_отказывает_до_заголовков()
    {
        using var stand = NewStand(follow: new FakeFollow().Doors);
        Assert.True(Started(stand));

        var noCookie = await Send(stand.Http, HttpMethod.Get, "/api/phone/live?session=session-1");

        Assert.Equal(401, noCookie.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateUnauthorized), noCookie.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);

        var badId = await Send(stand.Http, HttpMethod.Get, "/api/phone/live?session=%20%D0%B0", cookie: cookie);

        Assert.Equal(400, badId.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebSessionMissing), badId.Body, StringComparison.Ordinal);

        var screens = new List<SseClient>();

        try
        {
            for (var index = 0; index < PhoneGate.MaxLiveScreens; index++)
            {
                var client = new SseClient(stand, "/api/phone/live?session=session-1", cookie);
                screens.Add(client);

                Assert.Contains("200", await client.WaitFor("HTTP/1.1 200"));
            }

            var fifth = await Send(stand.Http, HttpMethod.Get, "/api/phone/live?session=session-1", cookie: cookie);

            Assert.Equal(429, fifth.Status);
            Assert.Contains(Loc.T(PanelStrings.PhoneWebTooManyScreens), fifth.Body, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in screens) client.Dispose();
        }
    }

    /// <summary>
    /// ДОГОН (`/api/session/page`) НЕ ОТДАЁТ ДОМАШНИЙ КАТАЛОГ И ИМЯ МАШИНЫ. Полная сборка записей
    /// по белому списку — следующий шаг контракта; здесь закрыто то, что иначе утекло бы сейчас:
    /// `system/message` и аргументы инструментов несут пути.
    ///
    /// Мутация, которая обязана упасть здесь: **«не маскировать тело догона»** — путь владельца
    /// появится в ответе целиком.
    /// </summary>
    [Fact]
    public async Task Догон_маскирует_путь_и_имя_машины()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var machine = Environment.MachineName;

        stand.Reply = new PhoneEngineAnswer(
            200,
            "{\"result\":{\"ok\":true,\"value\":{\"records\":["
            + "{\"type\":\"event\",\"event\":{\"type\":\"system/message\",\"seq\":1,\"time\":1,"
            + "\"data\":{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + J(home) + "\\\\work\"}]}}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"user/message\",\"seq\":2,\"time\":2,"
            + "\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"путь " + J(home) + "\\\\work на " + machine + "\"}],"
            + "\"source\":{\"kind\":\"user\"}}}}],\"hasMore\":false}}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/page",
            "{\"args\":{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":\"session-1\"},\"throughSeq\":10}}}",
            cookie);

        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain(J(home), reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(home, reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(machine, reply.Body, StringComparison.Ordinal);
        Assert.Contains("~", reply.Body, StringComparison.Ordinal);
        Assert.Contains(PhoneMasks.Machine, reply.Body, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(reply.Body);

        // ОТВЕТ — СВОЙ (`ok`/`records`/`hasMore`), а не тело движка: системное сообщение
        // (и его путь) в ответ не попало вовсе, а путь в словаре человека замаскирован.
        Assert.True(json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean());
        Assert.True(json.RootElement.TryGetProperty("records", out _));
        Assert.DoesNotContain("system/message", reply.Body, StringComparison.Ordinal);
    }

    // ---- пульт S1+S2: оболочка на `/`, ассеты из exe ---------------------------------------

    /// <summary>ГДЕ ЛЕЖИТ АССЕТ В ОБОЛОЧКЕ: путь из `href`/`src` — тот же, что отдаёт калитка.</summary>
    private static string AssetIn(string html, string attribute)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            attribute + "=\"(/assets/[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.True(match.Success, $"в оболочке нет {attribute}=\"/assets/…\"");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// КОРЕНЬ ОТДАЁТ ОБОЛОЧКУ ПУЛЬТА, А НЕ СТАРУЮ СТРАНИЦУ (S1+S2).
    ///
    /// **От какого случая.** До t7 на `/` жила серверная страница входа/вопроса. Теперь там React-пульт,
    /// а старая страница — запасной адрес `/ask`; проверки старой страницы переехали туда.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть старую страницу на `/`»** (в теле появится
    /// `id="code"`) и **«отдать оболочку без слов»** (в `data-boot` не будет нужного слова).
    /// </summary>
    [Fact]
    public async Task Корень_отдаёт_оболочку_пульта_и_не_отдаёт_старую_страницу()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var anon = await Send(stand.Http, HttpMethod.Get, "/");

        Assert.Equal(200, anon.Status);
        Assert.Contains("<div id=\"root\"", anon.Body, StringComparison.Ordinal);
        Assert.Contains("&quot;authed&quot;:false", anon.Body, StringComparison.Ordinal);
        Assert.Contains("&quot;lang&quot;:&quot;ru&quot;", anon.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebListTitle), anon.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebLoading), anon.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebNoScriptTitle), anon.Body, StringComparison.Ordinal);

        // СТАРАЯ СТРАНИЦА — НЕ ЗДЕСЬ, и внешних адресов у оболочки нет вовсе.
        Assert.DoesNotContain("id=\"code\"", anon.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", anon.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", anon.Body, StringComparison.OrdinalIgnoreCase);

        // ВОШЁЛ — ОБОЛОЧКА ЗНАЕТ ЭТО, и имя устройства берётся из пропуска.
        var cookie = await Pair(stand);

        var authed = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

        Assert.Equal(200, authed.Status);
        Assert.Contains("&quot;authed&quot;:true", authed.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageDeviceFallback), authed.Body, StringComparison.Ordinal);

        // И МЕТОД ТОТ ЖЕ: POST на корень — 405 словами.
        Assert.Equal(405, (await Send(stand.Http, HttpMethod.Post, "/", "{}", cookie)).Status);
    }

    /// <summary>
    /// ЯЗЫК ОБОЛОЧКИ — ЯЗЫК ТЕЛЕФОНА (`Accept-Language`): слова в `data-boot` и `lang` меняются вместе.
    /// </summary>
    [Fact]
    public async Task Оболочка_говорит_на_языке_телефона()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var reply = await Send(stand.Http, HttpMethod.Get, "/", language: language);

            Assert.Equal(200, reply.Status);
            Assert.Contains($"&quot;lang&quot;:&quot;{language}&quot;", reply.Body, StringComparison.Ordinal);
            Assert.Contains(
                Loc.TIn(language, nameof(PanelStrings.PhoneWebListTitle)), reply.Body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// АССЕТЫ ПУЛЬТА ОТДАЮТСЯ ИЗ EXE: тот же путь, что в оболочке; тип по расширению; кэш `immutable`;
    /// CSP и `nosniff` — на новой поверхности тоже. Незнакомое имя — 404, «похожего» не ищем.
    ///
    /// Мутации, которые обязаны упасть здесь: **«отдавать ассеты с `no-store`»** (ломает `immutable`)
    /// и **«искать ассет по подстроке»** (`/assets/shell.html` оказался бы в ответе).
    /// </summary>
    [Fact]
    public async Task Ассеты_пульта_отдаются_из_exe_с_immutable()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var shell = await Send(stand.Http, HttpMethod.Get, "/");
        var host = "127.0.0.1:" + stand.Port;

        foreach (var (attribute, type) in new[] { ("src", "text/javascript"), ("href", "text/css") })
        {
            var path = AssetIn(shell.Body, attribute);
            var head = await RawResponse(stand, RawRequest("GET", path, host));

            Assert.Contains("200 OK", head, StringComparison.Ordinal);
            Assert.Contains("Content-Type: " + type, head, StringComparison.Ordinal);
            Assert.Contains("Cache-Control: " + PhoneWeb.AssetCache, head, StringComparison.Ordinal);
            Assert.Contains("Content-Security-Policy: " + PhoneWeb.Csp, head, StringComparison.Ordinal);
            Assert.Contains("X-Content-Type-Options: nosniff", head, StringComparison.Ordinal);
        }

        // ИМЯ — ОДИН В ОДИН: ни оболочки, ни пути «выше каталога» через ассеты не выдают.
        Assert.Equal(404, (await Send(stand.Http, HttpMethod.Get, "/assets/shell.html", cookie: null)).Status);
        Assert.Equal(404, (await Send(stand.Http, HttpMethod.Get, "/assets/%2e%2e%2fshell.html")).Status);
        Assert.Equal(404, (await Send(stand.Http, HttpMethod.Get, "/assets/")).Status);
    }

    /// <summary>ЗАПАСНОЙ АДРЕС `/ask` — ПРЕЖНЯЯ СТРАНИЦА ЦЕЛИКОМ, со своим опросом карточки.</summary>
    [Fact]
    public async Task Запасной_адрес_отдаёт_прежнюю_страницу()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var page = await Send(stand.Http, HttpMethod.Get, "/ask");

        Assert.Equal(200, page.Status);
        Assert.Contains("id=\"code\"", page.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);
        var card = await Send(stand.Http, HttpMethod.Get, "/ask?part=card", cookie: cookie);

        Assert.Equal(200, card.Status);
        Assert.DoesNotContain("<script", card.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>КАТОК ВОПРОСА В ПРОВЕРКЕ: «готово» с `clientId`, затем водопад вопроса.</summary>
    private static void Ask(FakeEvents events, string eventId, string agentId, string questionsJson) =>
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"" + eventId + "\",\"agentId\":\"" + agentId + "\""
            + ",\"request\":{\"questions\":" + questionsJson + "}}}");

    /// <summary>СПИСОК СЕССИЙ ДЛЯ СОПОСТАВЛЕНИЯ: корневая `s-1` и подчинённая `sub-1`.</summary>
    private static PhoneEngineAnswer RootIndex() => new(
        200, "{\"result\":{\"ok\":true,\"value\":{\"items\":["
        + "{\"sessionId\":\"s-1\",\"updatedAt\":2,\"running\":true,\"blank\":false},"
        + "{\"sessionId\":\"sub-1\",\"parentSessionId\":\"s-1\",\"origin\":\"subagent\",\"updatedAt\":1,\"running\":true,\"blank\":false}"
        + "]}}}");

    /// <summary>
    /// ВОПРОС ДЛЯ ПУЛЬТА: КАРТОЧКА, КОРНЕВАЯ СЕССИЯ И НИ ОДНОГО СЕКРЕТА (S2, контракт §2.3).
    ///
    /// **От какого случая.** Вопрос подчинённого приходит с `agentId` подчинённой сессии — карточка
    /// должна встать в КОРНЕВУЮ (её человек и открыл), а сам `agentId` и `clientId` не уезжают.
    /// `intent` не толкуется и не передаётся; `options` — с подписями и описаниями.
    ///
    /// Мутации, которые обязаны упасть здесь: **«отдать кадр вопроса как есть»** (появится `agentId`/
    /// `intent`) и **«не искать корень»** (в `sessionId` окажется `sub-1`).
    /// </summary>
    [Fact]
    public async Task Вопрос_отдаётся_карточкой_и_привязан_к_корневой_сессии()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");

        Ask(
            events, "event-7", "sub-1",
            "[{\"id\":\"q1\",\"question\":\"Делать?\",\"header\":\"План\",\"detail\":\"Подробнее\","
            + "\"options\":[{\"label\":\"Да\",\"description\":\"согласен\"},{\"label\":\"Нет\"}],"
            + "\"multiSelect\":true,\"intent\":{\"kind\":\"plan-review\"}}]");

        stand.Reply = RootIndex();

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"state\":\"question\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("event-7", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"s-1\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"fromSubagent\":true", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"header\":\"План\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"detail\":\"Подробнее\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"Да\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"description\":\"согласен\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"multiSelect\":true", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"expiresAt\":", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"remainingSeconds\":600", reply.Body, StringComparison.Ordinal);

        foreach (var banned in new[] { "client-42", "sub-1", "intent", "plan-review", "agentId" })
            Assert.DoesNotContain(banned, reply.Body, StringComparison.Ordinal);

        Assert.Equal(401, (await Send(stand.Http, HttpMethod.Get, "/api/phone/question")).Status);
    }

    /// <summary>
    /// СОСТОЯНИЯ ВОПРОСА — ТЕМИ ЖЕ СЛОВАМИ, ЧТО ВИДИТ ЧЕЛОВЕК: нет — `none`, ответили — `answered`.
    /// Отдельно от вопроса-карточки, потому что «вопроса нет» — тоже ОТВЕТ, а не ошибка (контракт §2.3).
    /// </summary>
    [Fact]
    public async Task Вопрос_исходы_названы_состоянием_а_не_ошибкой()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var none = await Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie);

        Assert.Equal(200, none.Status);
        Assert.Contains("\"state\":\"none\"", none.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateNoQuestionNow), none.Body, StringComparison.Ordinal);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");

        Ask(events, "event-7", "s-1", "[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"}]}]");

        stand.Reply = RootIndex();

        var asked = await Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie);

        Assert.Contains("\"state\":\"question\"", asked.Body, StringComparison.Ordinal);

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}");

        var answered = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie);

        Assert.Equal(200, answered.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateAnswerSent), answered.Body, StringComparison.Ordinal);

        var after = await Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie);

        Assert.Equal(200, after.Status);
        Assert.Contains("\"state\":\"answered\"", after.Body, StringComparison.Ordinal);
        Assert.Contains("event-7", after.Body, StringComparison.Ordinal);

        // НЕ УЛОЖИЛОСЬ В ПРЕДЕЛЫ (§2.3) — `unreadable`, а не обрезанная карточка: ответить
        // на обрезанный вопрос значило бы ответить не на то, что спросили.
        var many = "[" + string.Join(
            ",",
            Enumerable.Range(1, PhoneQuestionCards.MaxQuestions + 1)
                .Select(index => "{\"id\":\"q" + index + "\",\"question\":\"вопрос\"}")) + "]";

        Ask(events, "event-9", "s-1", many);

        var unreadable = await Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie);

        Assert.Equal(200, unreadable.Status);
        Assert.Contains("\"state\":\"unreadable\"", unreadable.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageUnreadable), unreadable.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТВЕТ ПО JSON СОБИРАЕТСЯ ЗАНОВО: чужие поля не уезжают в движок, а `clientId` подставляет
    /// калитка (контракт §2.5). Предел «своих слов» проверяется, а не обрезается.
    /// </summary>
    [Fact]
    public async Task Ответ_по_JSON_собирается_по_белому_списку()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");

        Ask(events, "event-7", "s-1", "[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"}]}]");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-7\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"],"
            + "\"custom\":\"свои слова\",\"extra\":\"не слать\"}]}", cookie);

        Assert.Equal(200, reply.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
        Assert.Contains("\"custom\":\"свои слова\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("client-42", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("extra", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("не слать", call.Args, StringComparison.Ordinal);

        // СЛИШКОМ ДЛИННЫЕ СВОИ СЛОВА — ОТКАЗ СЛОВАМИ, а не тихое обрезание.
        Ask(events, "event-8", "s-1", "[{\"id\":\"q1\",\"question\":\"Ещё?\"}]");

        var longCustom = new string('я', PhoneAnswerCards.MaxCustom + 1);

        var tooLong = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-8\",\"answers\":[{\"id\":\"q1\",\"custom\":\"" + longCustom + "\"}]}", cookie);

        Assert.Equal(400, tooLong.Status);
    }

    /// <summary>
    /// ОТМЕНА ПО JSON ОТВЕЧАЕТ JSON (поправка §0.6), а ФОРМА — ПО-ПРЕЖНЕМУ 303 НА СТРАНИЦУ.
    /// «Первый ответивший» и «устаревший вопрос» говорятся СЛОВАМИ, без кодов движка.
    ///
    /// Мутации, которые обязаны упасть здесь: **«отвечать формой на JSON»** (тело станет HTML)
    /// и **«разрешить отмену по чужому eventId»** (чужой ход был бы снят).
    /// </summary>
    [Fact]
    public async Task Отмена_по_JSON_отвечает_словами_а_форма_страницей()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");

        Ask(events, "event-7", "s-1", "[{\"id\":\"q1\",\"question\":\"Делать?\"}]");

        stand.Reply = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}");

        var json = await Send(stand.Http, HttpMethod.Post, "/cancel", "{\"eventId\":\"event-7\"}", cookie);

        Assert.Equal(200, json.Status);
        Assert.Contains("\"ok\":true", json.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCancelled), json.Body, StringComparison.Ordinal);
        Assert.Null(json.Location);
        Assert.Contains(stand.Calls, call => call.Endpoint == PhoneDecisions.SessionCancelEndpoint);

        // ВОПРОС ПРИШЁЛ ЗАНОВО — И ЧУЖОЙ eventId ЕГО НЕ СНИМАЕТ.
        Ask(events, "event-8", "s-1", "[{\"id\":\"q1\",\"question\":\"Ещё?\"}]");

        var stale = await Send(stand.Http, HttpMethod.Post, "/cancel", "{\"eventId\":\"event-7\"}", cookie);

        Assert.Equal(403, stale.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageTaken), stale.Body, StringComparison.Ordinal);

        // ФОРМА — ПО-ПРЕЖНЕМУ НА СТРАНИЦУ (старая страница телефона не ломается).
        var form = await Send(stand.Http, HttpMethod.Post, "/cancel", "eventId=event-8", cookie);

        Assert.Equal(303, form.Status);
        Assert.Equal("/", form.Location);
    }

    /// <summary>
    /// ДОГОН ПРОВЕРЯЕТ ДОВОДЫ: подчинённый адрес — 403 СЛОВАМИ, кривые числа — 400, а
    /// `maxMessages` больше 50 — УРЕЗАЕТСЯ (контракт §2.4). Движок при отказе доводов не зовётся.
    /// </summary>
    [Fact]
    public async Task Догон_проверяет_доводы_и_урезает_размер()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        const string subagent =
            "{\"args\":{\"request\":{\"address\":{\"kind\":\"subagent\",\"parentSessionId\":\"s-1\"},\"throughSeq\":10}}}";

        var foreign = await Send(stand.Http, HttpMethod.Post, "/api/session/page", subagent, cookie);

        Assert.Equal(403, foreign.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebSubagentRefused), foreign.Body, StringComparison.Ordinal);
        Assert.Empty(stand.Calls);

        const string badSeq =
            "{\"args\":{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":\"s-1\"},\"throughSeq\":-5}}}";

        Assert.Equal(400, (await Send(stand.Http, HttpMethod.Post, "/api/session/page", badSeq, cookie)).Status);
        Assert.Empty(stand.Calls);

        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"ok\":true,\"value\":{\"records\":[],\"hasMore\":false}}}");

        const string many =
            "{\"args\":{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":\"s-1\"},\"throughSeq\":10,\"maxMessages\":999}}}";

        Assert.Equal(200, (await Send(stand.Http, HttpMethod.Post, "/api/session/page", many, cookie)).Status);

        var call = Assert.Single(stand.Calls);

        Assert.Contains("\"maxMessages\":50", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("999", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("turnWindow", call.Args, StringComparison.Ordinal);

        // ОТКАЗ ДВИЖКА — СЛОВАМИ, А НЕ ТЕЛОМ.
        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"error\":{\"code\":\"gateway/bad-request\"}}}");

        var refused = await Send(stand.Http, HttpMethod.Post, "/api/session/page", many, cookie);

        Assert.Equal(502, refused.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebOlderFailed), refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway", refused.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОДЧИНЁННАЯ СЕССИЯ В ЖИВОМ ПОТОКЕ — ОТКАЗ СЛОВАМИ (контракт §2.2, §2.10), а не `lost`:
    /// переписку подчинённого телефон не показывает, и поток движка для неё даже не открывается.
    /// </summary>
    [Fact]
    public async Task Живой_поток_подчинённой_сессии_отказывает_словами()
    {
        var follow = new FakeFollow();

        using var stand = NewStand(follow: follow.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = RootIndex();

        using var sse = new SseClient(stand, "/api/phone/live?session=sub-1", cookie);

        var text = await sse.WaitFor("event: error");

        Assert.Contains("event: error", text, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"subagent\"", text, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebSubagentRefused), text, StringComparison.Ordinal);
        Assert.Equal(0, follow.Opens);
    }

    // ---- двери S3: задача, новая задача, стоп, очередь, «убрать» ---------------------------------

    /// <summary>
    /// ПОДСТАВНАЯ ДВЕРЬ ЧТЕНИЯ ОЧЕРЕДИ (<c>session/control</c>). Проверка подаёт кадры сама и видит,
    /// ОТКРЫЛА ЛИ калитка поток и ЗАКРЫЛА ЛИ его: «живых потоков после ответа — 0» — обязательство
    /// калитки (контракт §5.4), и оно проверяется здешним <see cref="Closed"/>.
    /// </summary>
    private sealed class FakeControl
    {
        private readonly object _gate = new();
        private readonly List<Action<string>> _subscribers = new();
        private readonly List<Link> _links = new();

        /// <summary>Сколько раз калитка открыла поток — под замком: чтения идут из разных ниток.</summary>
        public int Opens
        {
            get
            {
                lock (_gate) return _links.Count;
            }
        }

        /// <summary>Сколько потоков ЖИВО СЕЙЧАС: после ответа обязано быть 0 (контракт §5.4).</summary>
        public int Live
        {
            get
            {
                lock (_gate) return _links.Count(link => link.Active);
            }
        }

        public PhoneControlDoors Doors => new(Open, _ => { }, () => true);

        /// <summary>Подать кадр ВСЕМ открытым потокам: при нескольких чтениях адресат — каждый из них.</summary>
        public void Emit(string frame)
        {
            Action<string>[] subscribers;

            lock (_gate) subscribers = _subscribers.ToArray();

            foreach (var subscriber in subscribers) subscriber(frame);
        }

        private IPhoneQueueLink? Open(Action<string> onFrame)
        {
            lock (_gate)
            {
                var link = new Link();

                _links.Add(link);
                _subscribers.Add(onFrame);

                return link;
            }
        }

        private sealed class Link : IPhoneQueueLink
        {
            public bool Active { get; private set; } = true;

            public bool Alive => Active;

            public void Dispose() => Active = false;
        }
    }

    /// <summary>Строка JSON в читаемом виде: кириллица остаётся словами, а не <c>\uXXXX</c>.</summary>
    private static string Js(string? text) =>
        "\"" + (text ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
        + "\"";

    /// <summary>Один пункт очереди в форме проекции <c>inbox</c> (контракт §5.2).</summary>
    private static string Item(string id, string text, string kind = "user") =>
        "{\"id\":" + Js(id) + ",\"role\":\"user\",\"source\":{\"kind\":" + Js(kind) + "}, \"content\":[{\"type\":\"text\",\"text\":" + Js(text) + "}]}";

    /// <summary>
    /// КАДР BASELINE (<c>session/control</c>) — ровно та форма, какую отдаёт 0.2.1: очередь лежит
    /// в <c>value.projections[&lt;sessionId&gt;].values.inbox</c> (поправка §0.1 контракта S3).
    /// </summary>
    private static string Baseline(
        string sessionId, string nextTurn = "[]", string nextStep = "[]", string queues = "", string extraSessions = "")
    {
        var box = "{\"asOfSeq\":7,\"values\":{\"inbox\":{\"next-turn\":" + nextTurn
            + ",\"next-step\":" + nextStep + "}" + (queues.Length > 0 ? ",\"queues\":" + queues : string.Empty) + "}}";

        return "{\"type\":\"item\",\"streamId\":\"phone-queue-1\",\"value\":{\"type\":\"baseline\",\"value\":{\"projections\":{"
            + Js(sessionId) + ":" + box + extraSessions + "}}}}";
    }

    private static PhoneEngineAnswer Accepted() =>
        new(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":{\"accepted\":true}}}");

    private static PhoneEngineAnswer Created(string sessionId) =>
        new(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":{\"sessionId\":"
            + Js(sessionId) + "}}}");

    private static PhoneEngineAnswer Refused(string code, string reason = "") =>
        new(200, "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":false,\"error\":{\"code\":"
            + Js(code) + (reason.Length > 0 ? ",\"details\":{\"reason\":" + Js(reason) + "}" : string.Empty) + "}}}");

    /// <summary>
    /// ПРОЧИТАТЬ ОЧЕРЕДЬ, ПОДАВ КАДР: запрос идёт своей ниткой (дверь ждёт поток), кадр уходит
    /// тогда, когда калитка его уже открыла, — иначе проверка гадала бы по времени.
    /// </summary>
    private static async Task<Reply> QueueRead(
        Stand stand, FakeControl control, string? frame, string cookie, string session = "session-1", string? language = null)
    {
        var before = control.Opens;
        var task = Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=" + session, cookie: cookie, language: language);

        for (var attempt = 0; attempt < 600 && control.Opens == before; attempt++) await Task.Delay(5, TestContext.Current.CancellationToken);

        if (frame is not null) control.Emit(frame);

        return await task;
    }

    /// <summary>Один JSON-объект доводов движка — по имени вызова, как его записала проверка.</summary>
    private static JsonElement Arguments(Stand stand, string endpoint, int index = 0)
    {
        var calls = stand.CallsNow().Where(call => string.Equals(call.Endpoint, endpoint, StringComparison.Ordinal)).ToArray();

        Assert.True(calls.Length > index, $"вызова «{endpoint}» №{index + 1} не было: {calls.Length}");

        using var document = JsonDocument.Parse(calls[index].Args);

        return document.RootElement.Clone();
    }

    /// <summary>
    /// ДВЕРЬ ЗАДАЧИ ОТКАЗЫВАЕТ ДО ДЕЙСТВИЯ И СЛОВАМИ (контракт S3 §2.4, F-1).
    ///
    /// Порядок стережётся не текстом, а двумя вещами: у отказов свои ПОЛНЫЕ фразы (а не обрывки
    /// страницы входа), и ни один из них не зовёт движок — <c>Calls</c> остаётся пустым.
    /// Мутации, которые обязаны упасть здесь: вернуть обрывок `PhoneGateUnauthorized` · вернуть
    /// обрывок `PhoneGateTooBigFormat` («запрос… (64 КиБ)») · снять предел тела.
    /// </summary>
    [Fact]
    public async Task Дверь_задачи_отказывает_до_действия_и_словами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var noCookie = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-1\",\"requestId\":\"r-1\",\"text\":\"привет\"}");

        Assert.Equal(401, noCookie.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskUnauthorized)), noCookie.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhoneGateUnauthorized), noCookie.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);

        var foreign = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-1\",\"requestId\":\"r-1\",\"text\":\"привет\"}",
            cookie: cookie, host: "evil.example:80");

        Assert.Equal(403, foreign.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskForeignDoor)), foreign.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", foreign.Body, StringComparison.Ordinal);

        var get = await Send(stand.Http, HttpMethod.Get, "/api/phone/prompt", cookie: cookie);

        Assert.Equal(405, get.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskNotAccepted)), get.Body, StringComparison.Ordinal);

        // ТЕЛО БОЛЬШЕ 64 КиБ — свой текст S3, без «КиБ» и без слова «запрос» (F-1).
        var big = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", cookie: cookie, bodyBytes: 65 * 1024);

        Assert.Equal(413, big.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskTooBig)), big.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("КиБ", big.Body, StringComparison.Ordinal);

        // ПУСТОЙ, ДЛИННЫЙ, НЕРАЗОБРАННЫЙ И ПЛОХОЙ АДРЕС — каждый своим словом.
        var empty = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-1\",\"requestId\":\"r-1\",\"text\":\"   \"}", cookie: cookie);

        Assert.Equal(400, empty.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskEmpty)), empty.Body, StringComparison.Ordinal);

        var longText = new string('я', 4001);

        var tooLong = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-1\",\"requestId\":\"r-1\",\"text\":" + Js(longText) + "}", cookie: cookie);

        Assert.Equal(400, tooLong.Status);
        Assert.Contains(
            string.Format(CultureInfo.CurrentCulture, Loc.T(nameof(PanelStrings.PhoneTaskTooLongFormat)), 4000),
            tooLong.Body, StringComparison.Ordinal);

        var broken = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", "это не json", cookie: cookie);

        Assert.Equal(400, broken.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBadRequest)), broken.Body, StringComparison.Ordinal);

        var badSession = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"плохая сессия\",\"requestId\":\"r-1\",\"text\":\"привет\"}", cookie: cookie);

        Assert.Equal(400, badSession.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBadSession)), badSession.Body, StringComparison.Ordinal);

        Assert.Empty(stand.CallsNow());
    }

    /// <summary>
    /// ЗАДАЧА УХОДИТ В ДВИЖОК РОВНО СВОИМИ ДОВОДАМИ (контракт S3 §2.2, §9.2).
    ///
    /// Мутации, которые обязаны упасть здесь: передать тело телефона в движок как есть (M5) ·
    /// перечеканить `requestId` (M11) · отправить `mode: steer`.
    /// </summary>
    [Fact]
    public async Task Задача_уходит_в_движок_ровно_своими_доводами()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = Accepted();

        var reply = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-7\",\"requestId\":\"req-7\",\"text\":\"  построй отчёт  \","
            + "\"cwd\":\"C:\\\\секрет\",\"mode\":\"steer\"}", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("\"ok\":true", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"accepted\":true", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-7\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"req-7\"", reply.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskAccepted)), reply.Body, StringComparison.Ordinal);

        var calls = stand.CallsNow();

        Assert.Single(calls);
        Assert.Equal("session/prompt", calls[0].Endpoint);

        var request = Arguments(stand, "session/prompt").GetProperty("request");

        Assert.Equal("req-7", request.GetProperty("requestId").GetString());
        Assert.Equal("session-7", request.GetProperty("sessionId").GetString());
        Assert.Equal("queue", request.GetProperty("mode").GetString());
        Assert.Equal("построй отчёт", request.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("text", request.GetProperty("content")[0].GetProperty("type").GetString());

        // ЛИШНЕЕ ИЗ ТЕЛА ТЕЛЕФОНА НЕ УХОДИТ ВООБЩЕ: ни ключа, ни значения.
        Assert.DoesNotContain("cwd", calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("секрет", calls[0].Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТКАЗ ДВИЖКА — <c>502</c> СЛОВАМИ, БЕЗ КОДА (контракт S3 §2.4): код уходит в журнал и только
    /// туда, а причина печатается фразой без числа (F-2).
    /// Мутации: положить код движка в `message` (M4) · печатать число вместо причины.
    /// </summary>
    [Fact]
    public async Task Отказ_движка_это_502_словами_без_кода()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var body = "{\"sessionId\":\"session-1\",\"requestId\":\"r-1\",\"text\":\"задача\"}";

        stand.Reply = Refused("gateway/internal", "inner detail");

        var refused = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        Assert.Equal(502, refused.Status);
        Assert.Contains(
            string.Format(
                CultureInfo.CurrentCulture,
                Loc.T(nameof(PanelStrings.PhoneTaskNotTakenFormat)),
                Loc.T(nameof(PanelStrings.PhoneTaskReasonRefused))),
            refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway/", refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("502", refused.Body, StringComparison.Ordinal);

        // СЕССИИ БОЛЬШЕ НЕТ — 403, а не 502 и не 400.
        stand.Reply = Refused("session/not-found");

        var gone = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        Assert.Equal(403, gone.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskSessionGone)), gone.Body, StringComparison.Ordinal);

        // ПОДЧИНЁННАЯ СЕССИЯ — 403 своими словами.
        stand.Reply = Refused("session/agent-busy", "use subagent delivery for this child session");

        var subagent = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        Assert.Equal(403, subagent.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebSubagentRefused), subagent.Body, StringComparison.Ordinal);

        // ДВИЖОК МОЛЧИТ — 502 с причиной «не ответил вовремя».
        stand.Reply = PhoneEngineAnswer.Failed("молчит");

        var silent = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        Assert.Equal(502, silent.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskReasonTimeout)), silent.Body, StringComparison.Ordinal);

        // ЖУРНАЛ: код движка есть В ЖУРНАЛЕ (а не в ответе).
        Assert.Contains(stand.Log, line => line.Contains("gateway/internal", StringComparison.Ordinal));
    }

    /// <summary>
    /// DEF-072: ДВЕ ОДНОВРЕМЕННЫЕ ОТПРАВКИ — ОДИН ВЫЗОВ ДВИЖКА, ВТОРАЯ ОТКАЗ СЛОВАМИ.
    ///
    /// Задержка движка <see cref="Stand.Hold"/> делает гонку НАСТОЯЩЕЙ, а не случайной: первая
    /// дверь уже в движке, вторая стучится в тот же миг. Мутация «пропустить проверку защёлки» —
    /// красная (два вызова и два `200`).
    /// </summary>
    [Fact]
    public async Task Две_отправки_в_полёте_дают_один_вызов_движка()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = Accepted();
        stand.Hold.Reset();

        var body = "{\"sessionId\":\"session-5\",\"requestId\":\"r-1\",\"text\":\"первая\"}";
        var first = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        for (var attempt = 0; attempt < 400 && stand.CallsNow().Length == 0; attempt++) await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Single(stand.CallsNow());

        var second = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", body, cookie: cookie);

        Assert.Equal(403, second.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBusy)), second.Body, StringComparison.Ordinal);
        Assert.Single(stand.CallsNow());

        stand.Hold.Set();

        var done = await first;

        Assert.Equal(200, done.Status);

        // ЗАЩЁЛКА СНЯТА: следующая отправка проходит.
        var next = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-5\",\"requestId\":\"r-2\",\"text\":\"вторая\"}", cookie: cookie);

        Assert.Equal(200, next.Status);
        Assert.Equal(2, stand.CallsNow().Length);
    }

    /// <summary>
    /// НОВАЯ ЗАДАЧА: ДВА ВЫЗОВА, ПУСТЫЕ ДОВОДЫ СОЗДАНИЯ, ПОВТОР — В ТОТ ЖЕ РАЗГОВОР (контракт §3).
    ///
    /// Мутации: D2 без карты `requestId` (M10 — повтор создал бы второй разговор) · положить в
    /// создание `cwd`/`agentPreset` · позвать создание на повторе.
    /// </summary>
    [Fact]
    public async Task Новая_задача_создаёт_разговор_и_повтор_идёт_в_него()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Replies["session/create"] = Created("session-new-1");
        stand.Replies["session/prompt"] = Accepted();

        var body = "{\"requestId\":\"new-1\",\"text\":\"сделай сайт\",\"sessionId\":\"игнор\"}";

        var created = await Send(stand.Http, HttpMethod.Post, "/api/phone/new", body, cookie: cookie);

        Assert.Equal(200, created.Status);
        Assert.Contains("\"sessionId\":\"session-new-1\"", created.Body, StringComparison.Ordinal);
        Assert.Contains("\"repeated\":false", created.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskNewAccepted)), created.Body, StringComparison.Ordinal);

        // СОЗДАНИЕ — ПУСТЫМИ ДОВОДАМИ: ни папки, ни пресета, ни рабочей области.
        var create = Arguments(stand, "session/create");

        Assert.Equal("request", create.EnumerateObject().Single().Name);
        Assert.Empty(create.GetProperty("request").EnumerateObject());

        // ЗАДАЧА УШЛА В СОЗДАННЫЙ РАЗГОВОР С ТЕМ ЖЕ `requestId`.
        var prompt = Arguments(stand, "session/prompt").GetProperty("request");

        Assert.Equal("session-new-1", prompt.GetProperty("sessionId").GetString());
        Assert.Equal("new-1", prompt.GetProperty("requestId").GetString());

        var repeated = await Send(stand.Http, HttpMethod.Post, "/api/phone/new", body, cookie: cookie);

        Assert.Equal(200, repeated.Status);
        Assert.Contains("\"repeated\":true", repeated.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-new-1\"", repeated.Body, StringComparison.Ordinal);
        Assert.Equal(
            1, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/create", StringComparison.Ordinal)));
        Assert.Equal(
            2, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/prompt", StringComparison.Ordinal)));
    }

    /// <summary>
    /// D2 ДЕРЖИТ ЗАЩЁЛКУ ПО УСТРОЙСТВУ, А НЕ ПО `requestId` (§3.4): два разных `requestId` от
    /// двойного нажатия не имеют права создать два разговора.
    /// </summary>
    [Fact]
    public async Task Новая_задача_держит_защёлку_по_устройству()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Replies["session/create"] = Created("session-new-2");
        stand.Replies["session/prompt"] = Accepted();
        stand.Hold.Reset();

        var first = Send(stand.Http, HttpMethod.Post, "/api/phone/new",
            "{\"requestId\":\"a-1\",\"text\":\"раз\"}", cookie: cookie);

        for (var attempt = 0; attempt < 400 && stand.CallsNow().Length == 0; attempt++) await Task.Delay(5, TestContext.Current.CancellationToken);

        var second = await Send(stand.Http, HttpMethod.Post, "/api/phone/new",
            "{\"requestId\":\"b-2\",\"text\":\"два\"}", cookie: cookie);

        Assert.Equal(403, second.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBusy)), second.Body, StringComparison.Ordinal);

        stand.Hold.Set();

        Assert.Equal(200, (await first).Status);
        Assert.Equal(
            1, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/create", StringComparison.Ordinal)));
    }

    /// <summary>
    /// N-1 (находка t7): ЗАЩЁЛКА D2 ЖИВЁТ ДОЛЬШЕ СРОКА СВОЕЙ ДВЕРИ.
    ///
    /// **Что было.** Защёлка истекала через 20 с, а дверь D2 вправе работать 22 с. В окне 20…22 с
    /// первая отправка ЕЩЁ ШЛА, а защёлка уже была снята сроком — и вторая отправка с ДРУГИМ
    /// `requestId` занимала освободившееся место и звала `session/create`, то есть рождала ВТОРОЙ
    /// разговор ровно там, ради чего защёлка и заведена.
    ///
    /// **Чем доказано.** Подставные часы двигаются на 21 с — больше прежних 20 с и меньше срока
    /// двери 22 с; первая отправка в это время держится в движке (`Stand.Hold`). Вторая — `403`,
    /// создание одно. Мутация «вернуть 20 с» — красная именно здесь.
    /// </summary>
    [Fact]
    public async Task Защёлка_новой_задачи_переживает_двадцать_секунд()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Replies["session/create"] = Created("session-new-4");
        stand.Replies["session/prompt"] = Accepted();
        stand.Hold.Reset();

        var first = Send(stand.Http, HttpMethod.Post, "/api/phone/new",
            "{\"requestId\":\"a-1\",\"text\":\"раз\"}", cookie: cookie);

        for (var attempt = 0; attempt < 400 && stand.CallsNow().Length == 0; attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        // ПОДСТАВНЫЕ ЧАСЫ: 21 с — уже больше прежних 20 с, но ещё меньше срока двери 22 с.
        stand.Now = stand.Now.AddSeconds(21);

        var second = await Send(stand.Http, HttpMethod.Post, "/api/phone/new",
            "{\"requestId\":\"b-2\",\"text\":\"два\"}", cookie: cookie);

        Assert.Equal(403, second.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBusy)), second.Body, StringComparison.Ordinal);
        Assert.Equal(
            1, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/create", StringComparison.Ordinal)));

        stand.Hold.Set();

        Assert.Equal(200, (await first).Status);

        // И ВТОРОГО РАЗГОВОРА НЕТ: создание так и осталось одно.
        Assert.Equal(
            1, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/create", StringComparison.Ordinal)));
    }

    /// <summary>
    /// РАЗГОВОР СОЗДАН, А ЗАДАЧА НЕ ПРИНЯТА: ПОВТОР ИДЁТ В ТОТ ЖЕ РАЗГОВОР (§3.4).
    /// Второго разговора не появляется, даже когда первая попытка кончилась отказом.
    /// </summary>
    [Fact]
    public async Task Разговор_создался_а_задача_не_принялась_повтор_идёт_в_тот_же()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Replies["session/create"] = Created("session-new-3");
        stand.Replies["session/prompt"] = Refused("session/agent-busy", "busy");

        var body = "{\"requestId\":\"c-1\",\"text\":\"работа\"}";

        var refused = await Send(stand.Http, HttpMethod.Post, "/api/phone/new", body, cookie: cookie);

        Assert.Equal(502, refused.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskReasonRefused)), refused.Body, StringComparison.Ordinal);

        stand.Replies["session/prompt"] = Accepted();

        var again = await Send(stand.Http, HttpMethod.Post, "/api/phone/new", body, cookie: cookie);

        Assert.Equal(200, again.Status);
        Assert.Contains("\"repeated\":true", again.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\":\"session-new-3\"", again.Body, StringComparison.Ordinal);
        Assert.Equal(
            1, stand.CallsNow().Count(call => string.Equals(call.Endpoint, "session/create", StringComparison.Ordinal)));
    }

    /// <summary>
    /// СТОП: ДВА ЧЕСТНЫХ ИСХОДА И НЕСЛОМАННЫЙ ПОВТОР (контракт §4.3). «Нет живого агента» —
    /// <c>200 idle</c>, а не <c>502</c>: останавливать нечего, это не беда.
    /// Мутация M7: перевести `session/not-found` в `502` — красная.
    /// </summary>
    [Fact]
    public async Task Стоп_отвечает_двумя_честными_исходами()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var body = "{\"sessionId\":\"session-3\"}";

        var two = "[" + Item("m-1", "раз") + "," + Item("m-2", "два") + "]";

        var before = await QueueRead(stand, control, Baseline("session-3", two), cookie, session: "session-3");

        Assert.Equal(2, JsonDocument.Parse(before.Body).RootElement.GetProperty("total").GetInt32());

        stand.Reply = Accepted();

        var stopped = await Send(stand.Http, HttpMethod.Post, "/api/phone/stop", body, cookie: cookie);

        Assert.Equal(200, stopped.Status);
        Assert.Contains("\"stopped\":true", stopped.Body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"stopped\"", stopped.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneStopDone)), stopped.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("session-3", stopped.Body, StringComparison.Ordinal);

        var cancel = Arguments(stand, "session/cancel").GetProperty("request");

        Assert.Equal("session-3", cancel.GetProperty("sessionId").GetString());
        Assert.Single(cancel.EnumerateObject());

        // ОЧЕРЕДЬ ПОСЛЕ ОТМЕНЫ — ТА ЖЕ: дверь стопа её НЕ ТРОГАЕТ (движок отменяет с keepInbox).
        Assert.DoesNotContain(
            stand.CallsNow(), call => string.Equals(call.Endpoint, "session/updateQueue", StringComparison.Ordinal));

        var after = await QueueRead(stand, control, Baseline("session-3", two), cookie, session: "session-3");

        Assert.Equal(2, JsonDocument.Parse(after.Body).RootElement.GetProperty("total").GetInt32());

        // ВТОРОЕ НАЖАТИЕ: движок отвечает `session/not-found` — «уже не работает», и это 200.
        stand.Reply = Refused("session/not-found");

        var idle = await Send(stand.Http, HttpMethod.Post, "/api/phone/stop", body, cookie: cookie);

        Assert.Equal(200, idle.Status);
        Assert.Contains("\"stopped\":false", idle.Body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"idle\"", idle.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneStopIdle)), idle.Body, StringComparison.Ordinal);

        var idleAgain = await Send(stand.Http, HttpMethod.Post, "/api/phone/stop", body, cookie: cookie);

        Assert.Equal(200, idleAgain.Status);
    }

    /// <summary>
    /// ОЧЕРЕДЬ ЧИТАЕТСЯ ИЗ ПРОЕКЦИИ `inbox` И ТОЛЬКО ОТТУДА (контракт §5.1–§5.2).
    ///
    /// Три вещи, каждая своей мутацией: **M1** — читать `baseline.value.queues` вместо проекции
    /// (тогда очередь пуста ВСЕГДА); **M12** — не фильтровать `source.kind` (служебный пункт
    /// уехал бы телефону вместе с `id`); **M9** — не закрыть поток (живой `WS` после ответа).
    /// </summary>
    [Fact]
    public async Task Очередь_читается_из_проекции_inbox()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var longText = new string('т', 400);
        var nextTurn = "[" + Item("m-1", "первое") + "," + Item("m-2", longText) + "]";
        var nextStep = "[" + Item("m-3", "в текущий ход") + "," + Item("srv-1", "служебное", "user-approval") + "]";

        var reply = await QueueRead(stand, control, Baseline("session-1", nextTurn, nextStep), cookie);

        Assert.Equal(200, reply.Status);

        using var json = JsonDocument.Parse(reply.Body);

        var root = json.RootElement;

        Assert.Equal("ok", root.GetProperty("state").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("shown").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());

        var items = root.GetProperty("items");

        // STEERING ПЕРВОЙ, внутри групп — порядок движка (§5.2).
        Assert.Equal(new[] { "m-3", "m-1", "m-2" }, items.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(
            new[] { "steering", "queued", "queued" },
            items.EnumerateArray().Select(item => item.GetProperty("placement").GetString()));

        // СЛУЖЕБНЫЙ ПУНКТ НЕ УЕХАЛ ВООБЩЕ — НИ ЕГО ID, НИ ЕГО ТЕКСТ.
        Assert.DoesNotContain("srv-1", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("служебное", reply.Body, StringComparison.Ordinal);

        // ПРЕВЬЮ ≤ 300 + «…», А `chars` — ПОЛНАЯ ДЛИНА.
        var second = items[2];

        Assert.Equal(400, second.GetProperty("chars").GetInt32());
        Assert.Equal(300, second.GetProperty("text").GetString()!.Length);
        Assert.EndsWith("…", second.GetProperty("text").GetString()!, StringComparison.Ordinal);

        // ЗАПРЕЩЁННЫХ ПОЛЕЙ В ОТВЕТЕ НЕТ (контракт §9.1).
        foreach (var banned in new[] { "next-turn", "next-step", "asOfSeq", "projections", "source", "role", "content" })
            Assert.DoesNotContain(banned, reply.Body, StringComparison.Ordinal);

        // ПОТОК ЗАКРЫТ ДО ОТВЕТА: одно открытие и ни одного живого.
        Assert.Equal(1, control.Opens);
        Assert.Equal(0, control.Live);

        // M1: ОЧЕРЕДЬ НЕ ЧИТАЕТСЯ ИЗ `queues`. Если бы читалась — ниже было бы 2, а не 0.
        var queues = "{\"next-turn\":[" + Item("q-1", "из queues") + "," + Item("q-2", "тоже") + "],\"next-step\":[]}";

        var fromQueues = await QueueRead(stand, control, Baseline("session-1", "[]", "[]", queues), cookie);

        Assert.Equal(200, fromQueues.Status);

        using var queueJson = JsonDocument.Parse(fromQueues.Body);

        Assert.Equal(0, queueJson.RootElement.GetProperty("total").GetInt32());
        Assert.DoesNotContain("из queues", fromQueues.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОЧЕРЕДЬ ОТДАЁТ НЕ БОЛЬШЕ ДВАДЦАТИ И НАЗЫВАЕТ ВСЕГО (контракт §5.3): 45 пунктов —
    /// «показаны первые 20 из 45», и телефон это скажет словами t2/t3.
    /// </summary>
    [Fact]
    public async Task Очередь_отдаёт_двадцать_пунктов_и_называет_всего()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var rows = string.Join(",", Enumerable.Range(1, 45).Select(index => Item("m-" + index, "пункт " + index)));

        var reply = await QueueRead(stand, control, Baseline("session-1", "[" + rows + "]"), cookie);

        Assert.Equal(200, reply.Status);

        using var json = JsonDocument.Parse(reply.Body);

        Assert.Equal(45, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(20, json.RootElement.GetProperty("shown").GetInt32());
        Assert.True(json.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(20, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("пункт 21", reply.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОЧЕРЕДЬ ОТКАЗЫВАЕТ ЧЕСТНО: нет входа — 401; плохой адрес — 400; чужая дверь — 403;
    /// подчинённая — 403; поток не открылся / baseline не пришёл — 502 СЛОВАМИ, а не пустым
    /// списком; сессия не открыта — <c>200 no-live</c> (а не «пусто»).
    /// </summary>
    [Fact]
    public async Task Очередь_отказывает_словами_а_не_пустотой()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var noCookie = await Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-1");

        Assert.Equal(401, noCookie.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskUnauthorized)), noCookie.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);

        // ИНДЕКС «КТО ЧЕЙ РОДИТЕЛЬ» — СРАЗУ: калитка помнит его 30 с, и поздняя подмена не прочлась бы.
        stand.Replies["session/list"] = new PhoneEngineAnswer(
            200, "{\"result\":{\"ok\":true,\"value\":{\"items\":["
                 + "{\"sessionId\":\"session-sub\",\"parentSessionId\":\"session-1\",\"origin\":\"subagent\"}]}}}");

        var badSession = await Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=%D0%B0", cookie: cookie);

        Assert.Equal(400, badSession.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBadSession)), badSession.Body, StringComparison.Ordinal);

        var foreign = await Send(
            stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-1", cookie: cookie, origin: "http://evil.example");

        Assert.Equal(403, foreign.Status);

        var post = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue?session=session-1", cookie: cookie);

        Assert.Equal(405, post.Status);

        // BASELINE НЕ ПРИШЁЛ — 502 в свой срок, а не молчание.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var silent = await QueueRead(stand, control, null, cookie);

        clock.Stop();

        Assert.Equal(502, silent.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneQueueUnreadable)), silent.Body, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"дверь держала {clock.Elapsed.TotalSeconds:0.0} с");

        // СЕССИЯ НЕ ОТКРЫТА — state no-live, а не «очередь пуста».
        var other = await QueueRead(stand, control, Baseline("session-2"), cookie);

        Assert.Equal(200, other.Status);
        Assert.Contains("\"state\":\"no-live\"", other.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneQueueNoLive)), other.Body, StringComparison.Ordinal);

        // ПОДЧИНЁННАЯ СЕССИЯ — 403 (её переписку показывают только на компьютере).
        var subagent = await Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-sub", cookie: cookie);

        Assert.Equal(403, subagent.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneWebSubagentRefused), subagent.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// СЛИШКОМ МНОГО ЧТЕНИЙ — <c>429</c> И БЕЗ ОТКРЫТИЯ ПОТОКА (контракт §5.4): четыре чтения
    /// в полёте, пятое получает слова, а не ещё один <c>WS</c>.
    /// </summary>
    [Fact]
    public async Task Очередь_не_открывает_пятый_поток()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var tasks = new List<Task<Reply>>();

        for (var index = 0; index < PhoneGate.MaxQueueReads; index++)
            tasks.Add(Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-1", cookie: cookie));

        for (var attempt = 0; attempt < 600 && control.Opens < PhoneGate.MaxQueueReads; attempt++) await Task.Delay(5, TestContext.Current.CancellationToken);

        var fifth = await Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-1", cookie: cookie);

        Assert.Equal(429, fifth.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneQueueBusy)), fifth.Body, StringComparison.Ordinal);
        Assert.Equal(PhoneGate.MaxQueueReads, control.Opens);

        control.Emit(Baseline("session-1"));

        foreach (var task in tasks) Assert.Equal(200, (await task).Status);
    }

    /// <summary>
    /// ПУНКТ УБИРАЕТСЯ, А ПОВТОР — НЕ ОШИБКА (контракт §6.3): <c>removed</c>, потом <c>gone</c>.
    /// Доводы — <c>action:{kind:remove}</c> и переложенный <c>id → itemId</c>.
    /// Мутации: перевести `queue-item-not-found` в 502 (M3) · положить код движка в слова (M4).
    /// </summary>
    [Fact]
    public async Task Пункт_убирается_и_повтор_не_ошибка()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = Accepted();

        var removed = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\",\"itemId\":\"m-1\"}", cookie: cookie);

        Assert.Equal(200, removed.Status);
        Assert.Contains("\"state\":\"removed\"", removed.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneQueueRemoved)), removed.Body, StringComparison.Ordinal);

        var request = Arguments(stand, "session/updateQueue").GetProperty("request");

        Assert.Equal("session-1", request.GetProperty("sessionId").GetString());
        Assert.Equal("m-1", request.GetProperty("itemId").GetString());
        Assert.Equal("remove", request.GetProperty("action").GetProperty("kind").GetString());

        // ПОВТОР: пункта уже нет — это успех («уже нет»), а не ошибка.
        stand.Reply = Refused("session/queue-item-not-found");

        var gone = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\",\"itemId\":\"m-1\"}", cookie: cookie);

        Assert.Equal(200, gone.Status);
        Assert.Contains("\"state\":\"gone\"", gone.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneQueueAlreadyGone)), gone.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-item-not-found", gone.Body, StringComparison.Ordinal);

        // ЧУЖОЙ/ПОДДЕЛЬНЫЙ ПУНКТ — тот же `gone`: ничего не удаляется и ничего не рассказывается.
        var forged = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\",\"itemId\":\"forged-9\"}", cookie: cookie);

        Assert.Equal(200, forged.Status);
        Assert.Contains("\"state\":\"gone\"", forged.Body, StringComparison.Ordinal);

        // ПЛОХОЙ ПУНКТ И ПЛОХОЙ АДРЕС — 400 СВОИМИ СЛОВАМИ, БЕЗ ВЫЗОВА ДВИЖКА.
        var calls = stand.CallsNow().Length;

        var badItem = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\",\"itemId\":\"плохой id\"}", cookie: cookie);

        Assert.Equal(400, badItem.Status);
        Assert.Contains(Loc.T(nameof(PanelStrings.PhoneTaskBadRequest)), badItem.Body, StringComparison.Ordinal);

        var noItem = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\"}", cookie: cookie);

        Assert.Equal(400, noItem.Status);
        Assert.Equal(calls, stand.CallsNow().Length);
    }

    /// <summary>
    /// ПОСЛЕ УБРАННОГО ПУНКТА ОЧЕРЕДЬ КОРОЧЕ (контракт §6.3): движок отдаёт проекцию без пункта —
    /// и телефон видит на один меньше. Это сквозная проверка «убрать → перечитать».
    /// </summary>
    [Fact]
    public async Task После_удаления_очередь_короче()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var two = "[" + Item("m-1", "раз") + "," + Item("m-2", "два") + "]";
        var one = "[" + Item("m-2", "два") + "]";

        var before = await QueueRead(stand, control, Baseline("session-1", two), cookie);

        Assert.Equal(2, JsonDocument.Parse(before.Body).RootElement.GetProperty("total").GetInt32());

        stand.Reply = Accepted();

        var removed = await Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
            "{\"sessionId\":\"session-1\",\"itemId\":\"m-1\"}", cookie: cookie);

        Assert.Equal(200, removed.Status);

        var after = await QueueRead(stand, control, Baseline("session-1", one), cookie);

        Assert.Equal(1, JsonDocument.Parse(after.Body).RootElement.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// ЯЗЫК СЛОВ — ЯЗЫК ТЕЛЕФОНА (контракт §0.10): `Accept-Language: zh` даёт китайские слова
    /// и на отказе, и на успехе, и на очереди. Мутация M13: `Loc.T` вместо `Loc.TIn`.
    /// </summary>
    [Fact]
    public async Task Двери_говорят_на_языке_телефона()
    {
        var control = new FakeControl();

        using var stand = NewStand(control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var unauthorized = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", "{}", language: "zh");

        Assert.Equal(401, unauthorized.Status);
        Assert.Contains(Loc.TIn("zh", nameof(PanelStrings.PhoneTaskUnauthorized)), unauthorized.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.TIn("ru", PanelStrings.PhoneTaskUnauthorized), unauthorized.Body, StringComparison.Ordinal);

        var badSession = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"а\",\"requestId\":\"r\",\"text\":\"т\"}", cookie: cookie, language: "zh");

        Assert.Equal(400, badSession.Status);
        Assert.Contains(Loc.TIn("zh", nameof(PanelStrings.PhoneTaskBadSession)), badSession.Body, StringComparison.Ordinal);

        stand.Reply = Accepted();

        var accepted = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-1\",\"requestId\":\"r\",\"text\":\"т\"}", cookie: cookie, language: "zh");

        Assert.Equal(200, accepted.Status);
        Assert.Contains(Loc.TIn("zh", nameof(PanelStrings.PhoneTaskAccepted)), accepted.Body, StringComparison.Ordinal);

        var queue = await QueueRead(stand, control, Baseline("session-2"), cookie, language: "zh");

        Assert.Contains(Loc.TIn("zh", nameof(PanelStrings.PhoneQueueNoLive)), queue.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЖУРНАЛ — ЧИСЛА И КОДЫ, БЕЗ ЗНАЧЕНИЙ (контракт §9.3): ни текста задачи, ни адреса сессии,
    /// ни cookie, ни `requestId`. Мутация «печатать текст в журнал» — красная.
    /// </summary>
    [Fact]
    public async Task Журнал_без_текста_задачи_и_адресов()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = Accepted();

        var secret = "секретный текст задачи";

        var reply = await Send(stand.Http, HttpMethod.Post, "/api/phone/prompt",
            "{\"sessionId\":\"session-42\",\"requestId\":\"req-42\",\"text\":" + Js(secret) + "}", cookie: cookie);

        Assert.Equal(200, reply.Status);

        var log = string.Join("\n", stand.Log);

        Assert.Contains("prompt", log, StringComparison.Ordinal);
        Assert.Contains("accepted", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("session-42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("req-42", log, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, log, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEF-071: ПОТОЛОК 64 КиБ — НА КАЖДОЕ СОБЫТИЕ ЛЕНТЫ, ВКЛЮЧАЯ `event` И `assistant`.
    ///
    /// **Почему 20 000 управляющих знаков.** В JSON `\u0001` занимает шесть байт: 20 000 знаков —
    /// это около 120 КБ, то есть кадр вдвое больше потолка. Проверка «по знакам» этого бы не
    /// поймала. Мутация «снять потолок» — красная (байт станет больше 65 536).
    /// </summary>
    [Fact]
    public void Потолок_события_ленты_64_килобайта()
    {
        var control = JsonSerializer.Serialize(new string('\u0001', 20000));

        var user = "{\"type\":\"item\",\"value\":{\"type\":\"event\",\"event\":{\"type\":\"user/message\","
            + "\"seq\":1,\"time\":1,\"data\":{\"source\":{\"kind\":\"user\"},\"content\":[{\"type\":\"text\",\"text\":"
            + control + "}]}}}}";

        var frame = PhoneLiveFrames.Translate(user);

        Assert.Equal(PhoneLiveKind.Record, frame.Kind);
        Assert.True(Encoding.UTF8.GetByteCount(frame.Json) <= PhoneLiveFrames.MaxEventBytes,
            $"событие {Encoding.UTF8.GetByteCount(frame.Json)} байт — больше потолка 64 КиБ");
        Assert.Equal(1, frame.Trimmed);

        // ТЕКСТ УРЕЗАН, А КАДР ОСТАЛСЯ РАЗБИРАЕМЫМ.
        using (var json = JsonDocument.Parse(frame.Json))
            Assert.EndsWith("…", json.RootElement.GetProperty("record").GetProperty("text").GetString()!, StringComparison.Ordinal);

        // ТО ЖЕ ДЛЯ ПРОЦЕССНОГО КАДРА ОТВЕТА (`assistant`).
        var assistant = "{\"type\":\"item\",\"value\":{\"type\":\"assistant-stream\",\"frame\":{\"type\":\"chunk\","
            + "\"attemptId\":\"a-1\",\"index\":0,\"chunk\":{\"type\":\"text-delta\",\"text\":" + control + "}}}}";

        var stream = PhoneLiveFrames.Translate(assistant);

        Assert.Equal(PhoneLiveKind.Assistant, stream.Kind);
        Assert.True(Encoding.UTF8.GetByteCount(stream.Json) <= PhoneLiveFrames.MaxEventBytes);
        Assert.Equal(1, stream.Trimmed);

        // КОРОТКИЙ ТЕКСТ НЕ ТРОГАЕТСЯ ВООБЩЕ — иначе «потолок» резал бы обычную ленту.
        var shortFrame = PhoneLiveFrames.Translate(user.Replace(control, Js("коротко"), StringComparison.Ordinal));

        Assert.Equal(0, shortFrame.Trimmed);
        Assert.Contains("коротко", shortFrame.Json, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEF-093: ПУСТОЙ ОТВЕТ АГЕНТА — НЕ ЗАПИСЬ.
    ///
    /// **Что было.** Шаг без текста (размышление, «Think») приходит без текстовых блоков, и
    /// калитка отправляла запись с `text:""`. Телефон рисовал ПУСТОЙ пузырь: строка без слов.
    /// Пустая строка — это не слово агента, а отсутствие слова; её место — в счёте отброшенного
    /// (журнал), а не в ленте человека.
    ///
    /// Проверка идёт ТЕМ ЖЕ входом, что и живой поток: одиночный кадр `event` и снимок `snapshot`.
    /// Мутация «снова отправлять пустой» — красная.
    /// </summary>
    [Fact]
    public void Пустой_ответ_агента_не_становится_записью()
    {
        static string Assistant(string text) =>
            "{\"type\":\"item\",\"value\":{\"type\":\"event\",\"event\":{\"type\":\"assistant/message\","
            + "\"seq\":40,\"time\":1,\"data\":{\"turn\":1,\"step\":1,\"message\":{\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"text\",\"text\":" + Js(text) + "}]}}}}}";

        // 1. ТЕКСТА НЕТ ВОВСЕ — записи нет, кадр посчитан отброшенным.
        var empty = PhoneLiveFrames.Translate(Assistant(string.Empty));

        Assert.Equal(PhoneLiveKind.Ignored, empty.Kind);
        Assert.Equal(string.Empty, empty.Json);
        Assert.Equal(1, empty.Dropped);

        // 2. ТОЛЬКО ПРОБЕЛЫ — то же самое: пустая строка не бывает словом.
        var blank = PhoneLiveFrames.Translate(Assistant("   \n\t  "));

        Assert.Equal(PhoneLiveKind.Ignored, blank.Kind);
        Assert.Equal(string.Empty, blank.Json);

        // 3. НЕПУСТОЙ — как раньше: запись с текстом.
        var word = PhoneLiveFrames.Translate(Assistant("ответ агента"));

        Assert.Equal(PhoneLiveKind.Record, word.Kind);
        Assert.Contains("ответ агента", word.Json, StringComparison.Ordinal);
        Assert.Contains("ответ агента", word.Json, StringComparison.Ordinal);
        Assert.Equal(0, word.Trimmed);

        // 4. СНИМОК: пустая запись выброшена, соседняя на месте, счёт отброшенного — 1.
        var snapshot = PhoneLiveFrames.Translate(
            "{\"type\":\"item\",\"value\":{\"type\":\"snapshot\",\"header\":{\"id\":\"session-1\"},"
            + "\"cursor\":1,\"hasMore\":false,\"records\":["
            + "{\"type\":\"event\",\"event\":{\"type\":\"user/message\",\"seq\":1,\"time\":1,"
            + "\"data\":{\"source\":{\"kind\":\"user\"},\"content\":[{\"type\":\"text\",\"text\":\"привет\"}]}}},"
            + "{\"type\":\"event\",\"event\":{\"type\":\"assistant/message\",\"seq\":2,\"time\":2,"
            + "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"role\":\"assistant\",\"content\":[]}}}}]}}");

        Assert.Equal(PhoneLiveKind.Snapshot, snapshot.Kind);
        Assert.Equal(1, snapshot.Dropped);
        Assert.Contains("привет", snapshot.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"assistant\"", snapshot.Json, StringComparison.Ordinal);
    }

    // ---- S4a: 14-е имя, архив, живая очередь и признак «список мог измениться» -------------------

    /// <summary>
    /// ПОДСТАВНАЯ ДВЕРЬ АРХИВА (<c>workspace/follow</c>): кадр подаёт проверка, поток считает
    /// открытия и закрытия. «Не прочиталось» — открыть и промолчать (<see cref="Silent"/>).
    /// </summary>
    private sealed class FakeWorkspace
    {
        private readonly object _gate = new();
        private readonly List<Link> _links = new();

        public string? Reply { get; set; }

        public bool Silent { get; set; }

        public int Opens
        {
            get
            {
                lock (_gate) return _links.Count;
            }
        }

        public int Live
        {
            get
            {
                lock (_gate) return _links.Count(link => link.Active);
            }
        }

        public PhoneWorkspaceDoors Doors => new(Open, _ => { }, () => true);

        private IPhoneWorkspaceLink? Open(Action<string> onFrame)
        {
            lock (_gate)
            {
                var link = new Link();

                _links.Add(link);

                if (!Silent && Reply is { } frame) onFrame(frame);

                return link;
            }
        }

        private sealed class Link : IPhoneWorkspaceLink
        {
            public bool Active { get; private set; } = true;

            public bool Alive => Active;

            public void Dispose() => Active = false;
        }
    }

    /// <summary>
    /// BASELINE <c>workspace/follow</c> — С МАРКЕРАМИ в тех полях, которые наружу уехать не должны
    /// (<c>items[].path/title</c>, <c>pinnedSessionIds</c>): утечка ловится строкой, а не надеждой.
    /// </summary>
    private static string Archive(params string[] archived) =>
        "{\"type\":\"item\",\"streamId\":\"phone-workspace-1\",\"value\":{\"type\":\"baseline\",\"value\":{"
        + "\"items\":[{\"workspaceId\":\"w-1\",\"path\":\"секрет-путь\",\"title\":\"секрет-название\","
        + "\"sessionIds\":[\"session-1\"],\"createdAt\":1,\"updatedAt\":2}],"
        + "\"archivedSessionIds\":[" + string.Join(",", archived.Select(Js)) + "],"
        + "\"pinnedSessionIds\":[\"pinned-1\"]}}}";

    /// <summary>ОТВЕТ <c>session/list</c> ровно той формы, какую отдаёт движок: result → value → items.</summary>
    private static string SessionList(params string[] sessionIds) =>
        "{\"result\":{\"ok\":true,\"value\":{\"items\":["
        + string.Join(
            ",",
            sessionIds.Select(id =>
                "{\"sessionId\":" + Js(id) + ",\"updatedAt\":1761770000000,\"running\":false,\"blank\":false}"))
        + "]}}}";

    /// <summary>Кадр проекции <c>session/control</c> — ровно та форма, какую отдаёт 0.2.1.</summary>
    private static string Projection(string sessionId, string key, string value) =>
        "{\"type\":\"item\",\"streamId\":\"phone-queue-1\",\"value\":{\"type\":\"projection\","
        + "\"sessionId\":" + Js(sessionId) + ",\"key\":" + Js(key)
        + ",\"value\":" + value + ",\"seq\":9}}";

    /// <summary>
    /// РАЗМЕРЫ СОБЫТИЙ <c>queue</c> В БАЙТАХ — по строке <c>data:</c> каждого такого события.
    /// Так проверяется потолок 64 КиБ (DEF-071) на БОЛЬШОМ inbox, а не на маленьком примере.
    /// </summary>
    private static List<int> QueueEventBytes(string sse)
    {
        var sizes = new List<int>();
        var at = 0;

        while (at < sse.Length)
        {
            var marker = sse.IndexOf("event: queue", at, StringComparison.Ordinal);
            if (marker < 0) break;

            var data = sse.IndexOf("data: ", marker, StringComparison.Ordinal);
            if (data < 0) break;

            var end = sse.IndexOf((char)10, data);
            if (end < 0) end = sse.Length;

            sizes.Add(Encoding.UTF8.GetByteCount(sse[data..end].TrimEnd((char)13)));
            at = end;
        }

        return sizes;
    }

    /// <summary>
    /// 14-Е ИМЯ — ТОЛЬКО ЧТЕНИЕ: в списке оно есть как поток, а сырым POST телефону отказывают
    /// словами, и до движка имя не доходит.
    /// </summary>
    [Fact]
    public async Task Четырнадцатое_имя_открыто_только_на_чтение()
    {
        using var stand = NewStand();
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var call = PhoneDecisions.Allowed.Single(item => item.Endpoint == PhoneDecisions.WorkspaceFollowEndpoint);

        Assert.Equal(EngineRpcMode.Stream, call.Mode);

        var posted = await Send(stand.Http, HttpMethod.Post, "/api/workspace/follow", "{\"args\":{}}", cookie);

        Assert.Equal(403, posted.Status);
        Assert.Contains(PhoneDecisions.WorkspaceFollowEndpoint, posted.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(stand.CallsNow(), call => call.Endpoint == PhoneDecisions.WorkspaceFollowEndpoint);
    }

    /// <summary>
    /// СПИСОК НЕ ПОКАЗЫВАЕТ АРХИВНЫХ — и при повторе тоже; в журнале только СЧЁТ, без имён и путей.
    /// Мутации, которые обязаны упасть: «не фильтровать архив» (в теле появится session-arch),
    /// «отдать данные рабочего пространства» (появится секрет-путь/секрет-название/pinned-1).
    /// </summary>
    [Fact]
    public async Task Список_скрывает_архивные_сессии_и_считает_их_без_имён()
    {
        var workspace = new FakeWorkspace { Reply = Archive("session-arch") };

        using var stand = NewStand(workspace: workspace.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, SessionList("session-1", "session-arch", "session-2"));

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("session-1", reply.Body, StringComparison.Ordinal);
        Assert.Contains("session-2", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("session-arch", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("секрет", reply.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned-1", reply.Body, StringComparison.Ordinal);

        var log = string.Join("\n", stand.Log);

        Assert.Contains("архивные сессии", log, StringComparison.Ordinal);
        Assert.DoesNotContain("session-arch", log, StringComparison.Ordinal);
        Assert.DoesNotContain("секрет", log, StringComparison.Ordinal);

        var again = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.DoesNotContain("session-arch", again.Body, StringComparison.Ordinal);
        Assert.Equal(1, workspace.Opens);
        Assert.Equal(0, workspace.Live);
    }

    /// <summary>
    /// АРХИВ НЕ ПРОЧИТАЛСЯ — СПИСОК ОТДАН КАК ЕСТЬ, ПРИЧИНА НАЗВАНА В ЖУРНАЛЕ.
    /// Отфильтровать наугад хуже, чем не отфильтровать: человек остался бы без списка.
    /// </summary>
    [Fact]
    public async Task Архив_не_прочитался_список_отдаётся_как_есть()
    {
        var workspace = new FakeWorkspace { Silent = true };

        using var stand = NewStand(workspace: workspace.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, SessionList("session-1", "session-arch"));

        var reply = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, reply.Status);
        Assert.Contains("session-arch", reply.Body, StringComparison.Ordinal);
        Assert.Contains(
            stand.Log, line => line.Contains("архив сессий не прочитан", StringComparison.Ordinal));
    }

    /// <summary>
    /// ЖИВАЯ ОЧЕРЕДЬ — В ТОМ ЖЕ SSE: своё уезжает, чужое (другая сессия, другой ключ) — нет,
    /// а поток control закрывается вместе с лентой.
    /// </summary>
    [Fact]
    public async Task Живой_поток_отдаёт_очередь_и_не_отдаёт_чужое()
    {
        var follow = new FakeFollow();
        var control = new FakeControl();

        using var stand = NewStand(follow: follow.Doors, control: control.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        var sse = new SseClient(stand, "/api/phone/live?session=session-1", cookie);

        try
        {
            Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

            for (var attempt = 0; attempt < 200 && control.Opens == 0; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.Equal(1, control.Opens);

            control.Emit(Baseline("session-1", "[" + Item("m-1", "первое") + "]", "[]"));
            control.Emit(Projection(
                "session-1", "inbox",
                "{\"next-turn\":[" + Item("m-2", "второе") + "],\"next-step\":[]}"));
            control.Emit(Projection("session-1", "workingDirectory", "\"C:\\\\секрет\""));
            control.Emit(Projection(
                "session-1", "turnOutline",
                "[{\"turn\":1,\"prompt\":\"маркер-текст\",\"response\":\"маркер-ответ\"}]"));
            control.Emit(Projection(
                "session-other", "inbox",
                "{\"next-turn\":[" + Item("x-1", "чужое") + "],\"next-step\":[]}"));

            var text = await sse.WaitFor("m-2");

            Assert.Contains("event: queue", text, StringComparison.Ordinal);
            Assert.Contains("первое", text, StringComparison.Ordinal);
            Assert.Contains("второе", text, StringComparison.Ordinal);

            foreach (var banned in new[]
                     {
                         "workingDirectory", "секрет", "маркер-текст", "маркер-ответ", "чужое",
                         "session-other", "x-1", "next-turn", "asOfSeq",
                     })
                Assert.DoesNotContain(banned, text, StringComparison.Ordinal);

            // ЖУРНАЛ: СЫРЫЕ КАДРЫ УПРАВЛЕНИЯ В НЕГО НЕ ПОПАДАЮТ (F-01). Строка до фильтра уносила
            // workingDirectory (путь владельца), turnOutline (тексты ходов) и чужие сессии — проверяем
            // это маркерами, а не «похоже на кадр».
            var log = string.Join("\n", stand.Log);

            Assert.DoesNotContain("секрет", log, StringComparison.Ordinal);
            Assert.DoesNotContain("маркер-текст", log, StringComparison.Ordinal);
            Assert.DoesNotContain("session-other", log, StringComparison.Ordinal);
            Assert.DoesNotContain("phone-queue-1", log, StringComparison.Ordinal);

            // БОЛЬШОЙ INBOX: СОБЫТИЕ queue ОБЯЗАНО УЛОЖИТЬСЯ В 64 КиБ (DEF-071) — режется с хвоста.
            var huge = string.Join(
                ",", Enumerable.Range(1, 20).Select(index => Item("big-" + index, new string('я', 20000))));

            control.Emit(Baseline("session-1", "[" + huge + "]"));

            text = await sse.WaitFor("\"hasMore\":true");

            var sizes = QueueEventBytes(text);

            Assert.NotEmpty(sizes);
            Assert.All(
                sizes,
                size => Assert.True(
                    size <= PhoneLiveFrames.MaxEventBytes,
                    $"событие queue {size} байт больше потолка {PhoneLiveFrames.MaxEventBytes}"));

            sse.Dispose();
        }
        finally
        {
            sse.Dispose();
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);

        while (control.Live > 0 && DateTime.UtcNow < deadline)
        {
            // КАДР, КОТОРЫЙ ТОЧНО ПИШЕТСЯ В СОКЕТ: `turn/start` разбор считает «мимо» и записи
            // не делает, а нам нужна именно неудачная запись — по ней калитка узнаёт уход клиента.
            follow.Emit(
                "{\"type\":\"item\",\"value\":{\"type\":\"assistant-stream\",\"frame\":{\"type\":\"chunk\","
                + "\"attemptId\":\"a1\",\"revision\":1,\"index\":1,\"time\":1,\"chunk\":{\"type\":\"text-delta\","
                + "\"text\":\"кусочек\"}}}}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, control.Live);
    }

    /// <summary>
    /// ПРИЗНАК «СПИСОК МОГ ИЗМЕНИТЬСЯ» — ИЗ УЖЕ ОТКРЫТОГО <c>$events</c>: кадр
    /// <c>api-session/status</c> ставит один бит, выдача списка его снимает, а адрес сессии
    /// из кадра в ответ не уезжает.
    /// </summary>
    [Fact]
    public async Task Признак_список_мог_измениться_ставится_кадром_статуса()
    {
        var events = new FakeEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, SessionList("session-1"));

        var first = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.Equal(200, first.Status);
        Assert.DoesNotContain("\"stale\"", first.Body, StringComparison.Ordinal);

        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"emit\","
            + "\"event\":\"api-session/status\",\"sessionId\":\"session-hidden-9\",\"running\":false}}");

        var body = string.Empty;

        for (var attempt = 0; attempt < 100 && !body.Contains("\"stale\":true", StringComparison.Ordinal); attempt++)
        {
            body = (await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)).Body;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Contains("\"stale\":true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("session-hidden-9", body, StringComparison.Ordinal);

        var cleared = await Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie);

        Assert.DoesNotContain("\"stale\"", cleared.Body, StringComparison.Ordinal);
    }

    // ---- G1: живая подсказка списка --------------------------------------------------------

    /// <summary>
    /// КАДР ДВИЖКА «ХОД СМЕНИЛ СОСТОЯНИЕ» — с ЛИШНИМИ полями, которых телефону видеть нельзя:
    /// так проверка доказывает не только «кадр пришёл», но и «из него ничего не уехало».
    /// </summary>
    private static string StatusFrame(string session, string home) =>
        "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"emit\","
        + "\"event\":\"api-session/status\",\"sessionId\":\"" + session + "\",\"running\":true,"
        + "\"cwd\":\"" + J(home) + "\\\\work\"}}";

    /// <summary>
    /// ЖИВАЯ ПОДСКАЗКА СПИСКА (G1, слово владельца 11.11.2026): на КАЖДЫЙ кадр
    /// <c>api-session/status</c> в СВОЁМ <c>$events</c> — РОВНО один кадр <c>stale</c>, и в нём
    /// НЕТ НИЧЕГО, кроме имени события: ни id сессии, ни «идёт ли ход», ни пути.
    ///
    /// **От какого дефекта.** `$events` был открыт только у живой ленты — на экране списка его
    /// не было вовсе, и статус «работает/закончила» не менялся без перезагрузки.
    ///
    /// Мутация, обязанная упасть здесь: **«не открывать `$events` в подсказке»** — кадр не придёт,
    /// и проверка упадёт на `event: stale` (имя падения — эта проверка).
    /// </summary>
    [Fact]
    public async Task Живая_подсказка_списка_отдаёт_один_бит_без_данных()
    {
        var events = new ManyEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var hidden = "session-hidden-g1";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using var sse = new SseClient(stand, "/api/phone/list/live", cookie);

        Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

        // СВОЯ ПОДПИСКА: калитка держит одну для вопросов, подсказка — вторую (одно SSE — один `WS`).
        for (var attempt = 0; attempt < 100 && events.Count < 2; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count);

        events.Emit(StatusFrame(hidden, home));

        var text = await sse.WaitFor("event: stale");

        Assert.Contains("event: stale", text, StringComparison.Ordinal);
        Assert.Contains("data: {}", text, StringComparison.Ordinal);

        foreach (var banned in new[] { hidden, "sessionId", "running", "cwd", home })
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);

        // СПИСОК КАЛИТКА НЕ ЧИТАЕТ: на событие — НИ ОДНОГО вызова session/list.
        Assert.DoesNotContain(stand.CallsNow(), call => call.Endpoint == "session/list");

        // ВЕЧНОГО «stale» НЕТ: без новых кадров второго не будет.
        await Task.Delay(400, TestContext.Current.CancellationToken);
        sse.Pump();

        Assert.Equal(1, sse.Count("event: stale"));
    }

    /// <summary>
    /// ОТКАЗЫ ПОДСКАЗКИ — ДО ЗАГОЛОВКОВ: без входа 401, не тем методом 405, без входа в движок 502,
    /// третья подсказка — 429 (предел назван числом: <see cref="PhoneGate.MaxListLive"/>).
    ///
    /// Мутации, обязанные упасть здесь: **«снять счётчик мест»** (третья получит 200) и
    /// **«не спрашивать вход в движок»** (без `$events` будет 200 вместо 502).
    /// </summary>
    [Fact]
    public async Task Подсказка_списка_отказывает_до_заголовков()
    {
        var events = new ManyEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var noCookie = await Send(stand.Http, HttpMethod.Get, "/api/phone/list/live");

        Assert.Equal(401, noCookie.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneGateUnauthorized), noCookie.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);

        var wrongMethod = await Send(stand.Http, HttpMethod.Post, "/api/phone/list/live", cookie: cookie);

        Assert.Equal(405, wrongMethod.Status);

        // ОТКАЗЫ НЕ ОТКРЫЛИ НИ ОДНОЙ ПОДПИСКИ: в списке только своя подписка калитки.
        Assert.Equal(1, events.Count);

        var opened = new List<SseClient>();

        try
        {
            for (var index = 0; index < PhoneGate.MaxListLive; index++)
            {
                var client = new SseClient(stand, "/api/phone/list/live", cookie);
                opened.Add(client);

                Assert.Contains("200", await client.WaitFor("HTTP/1.1 200"));
            }

            var third = await Send(stand.Http, HttpMethod.Get, "/api/phone/list/live", cookie: cookie);

            Assert.Equal(429, third.Status);
            Assert.Contains(Loc.T(PanelStrings.PhoneWebTooManyScreens), third.Body, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in opened) client.Dispose();
        }

        // ВХОДА В ДВИЖОК НЕТ — 502 ТЕМИ ЖЕ СЛОВАМИ, ЧТО У ЖИВОЙ ЛЕНТЫ.
        using var closed = NewStand(events: PhoneEventDoors.None);
        Assert.True(Started(closed));

        var closedCookie = await Pair(closed);

        var refused = await Send(closed.Http, HttpMethod.Get, "/api/phone/list/live", cookie: closedCookie);

        Assert.Equal(502, refused.Status);
        Assert.Contains(Loc.T(PanelStrings.PhoneEventNoEngine), refused.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// УХОД ТЕЛЕФОНА ЗАКРЫВАЕТ `$events` ПОДСКАЗКИ (G1): одно SSE — один `WS`, он не переживает
    /// экран, а счётчик открытых подсказок возвращается к НУЛЮ. Уход узнаётся ЗАПИСЬЮ — кадром
    /// (тот же способ, что у живой ленты: отдельного чтения сокета нет).
    ///
    /// Мутация, обязанная упасть: **«не закрывать `$events` в `finally`»** — ссылка останется живой,
    /// а `ListLiveOpen` не вернётся к нулю.
    /// </summary>
    [Fact]
    public async Task Уход_телефона_закрывает_подписку_подсказки()
    {
        var events = new ManyEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var sse = new SseClient(stand, "/api/phone/list/live", cookie);

        try
        {
            Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

            for (var attempt = 0; attempt < 100 && events.Count < 2; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.Equal(2, events.Count);
            Assert.Equal(1, stand.Gate.ListLiveOpen);

            sse.Dispose();

            var deadline = DateTime.UtcNow.AddSeconds(8);

            while (events.Alive(1) && DateTime.UtcNow < deadline)
            {
                // ПИСЬМО В МЁРТВЫЙ СОКЕТ — по нему калитка и узнаёт уход (как у живой ленты).
                // Часы двигаются, иначе окно склейки на замороженных часах никогда не кончится.
                stand.Now += PhoneGate.ListLiveCoalesce;
                events.Emit(StatusFrame("session-gone", home));
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(events.Alive(1));

            for (var attempt = 0; attempt < 200 && stand.Gate.ListLiveOpen > 0; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.Equal(0, stand.Gate.ListLiveOpen);
        }
        finally
        {
            sse.Dispose();
        }
    }

    /// <summary>
    /// ЧАСТЫЕ КАДРЫ СКЛЕИВАЮТСЯ: НЕ ЧАЩЕ ОДНОГО `stale` В <see cref="PhoneGate.ListLiveCoalesce"/>
    /// (250 мс). Первый кадр уходит сразу, а пачка в окне — ОДНИМ кадром в конце окна: бит один.
    /// Часы двигает проверка (`stand.Now`), а не сон на 250 мс.
    ///
    /// Мутации, обязанные упасть: **«снять склейку»** (на пачку придёт три кадра) и
    /// **«держать stale вечно»** (без новых событий придёт второй).
    /// </summary>
    [Fact]
    public async Task Частые_кадры_статуса_склеиваются_в_один()
    {
        var events = new ManyEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using var sse = new SseClient(stand, "/api/phone/list/live", cookie);

        Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

        for (var attempt = 0; attempt < 100 && events.Count < 2; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        events.Emit(StatusFrame("session-1", home));

        Assert.Equal(1, await sse.WaitForCount("event: stale", 1));

        // ПАЧКА В ОКНЕ: три кадра подряд — кадр подсказки остаётся ОДИН.
        events.Emit(StatusFrame("session-2", home));
        events.Emit(StatusFrame("session-3", home));
        events.Emit(StatusFrame("session-4", home));

        await Task.Delay(400, TestContext.Current.CancellationToken);
        sse.Pump();

        Assert.Equal(1, sse.Count("event: stale"));

        // ОКНО КОНЧИЛОСЬ — РОВНО ОДИН кадр на всю пачку.
        stand.Now += PhoneGate.ListLiveCoalesce;

        Assert.Equal(2, await sse.WaitForCount("event: stale", 2));

        await Task.Delay(400, TestContext.Current.CancellationToken);
        sse.Pump();

        Assert.Equal(2, sse.Count("event: stale"));
    }

    /// <summary>
    /// СРОК ЖИЗНИ ПОДСКАЗКИ — 30 МИНУТ, как у живой ленты (<see cref="PhoneGate.LiveLifetime"/>):
    /// дольше телефон держать поток не должен, он переподключится сам. Часы двигает проверка.
    /// </summary>
    [Fact]
    public async Task Подсказка_списка_кончается_по_пределу_жизни()
    {
        var events = new ManyEvents();

        using var stand = NewStand(events: events.Doors);
        Assert.True(Started(stand));

        var cookie = await Pair(stand);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using var sse = new SseClient(stand, "/api/phone/list/live", cookie);

        Assert.Contains("200", await sse.WaitFor("HTTP/1.1 200"));

        for (var attempt = 0; attempt < 100 && events.Count < 2; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        stand.Now += PhoneGate.LiveLifetime + TimeSpan.FromSeconds(1);

        // ЛЮБОЙ БИТ БУДИТ ПОТОК — и он прощается с причиной (как у живой ленты).
        events.Emit(StatusFrame("session-1", home));

        var text = await sse.WaitFor("event: end");

        Assert.Contains("event: end", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"lifetime\"", text, StringComparison.Ordinal);

        for (var attempt = 0; attempt < 200 && stand.Gate.ListLiveOpen > 0; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.False(events.Alive(1));
        Assert.Equal(0, stand.Gate.ListLiveOpen);
    }

    [Fact]
    public void Кадр_очереди_читается_из_проекции_inbox()
    {
        var baseline = "{\"type\":\"item\",\"streamId\":\"phone-queue-1\",\"value\":{\"type\":\"baseline\",\"value\":{\"projections\":{\"session-live\":{\"asOfSeq\":3,\"values\":{\"inbox\":{\"next-turn\":[{\"id\":\"m-1\",\"source\":{\"kind\":\"user\"},\"content\":[{\"type\":\"text\",\"text\":\"первое\"}]}],\"next-step\":[]}}}}}}}";
        var ok = true;
        try { using var doc = System.Text.Json.JsonDocument.Parse(baseline); } catch { ok = false; }
        var live = PhoneQueue.Live(baseline, "session-live", "нет", PhoneLiveFrames.MaxEventBytes - 1024);
        Assert.True(ok, "baseline не разбирается: " + baseline);
        Assert.NotNull(live.View);
        Assert.Contains("первое", live.View!.Json, StringComparison.Ordinal);
    }
    /// <summary>Убийца, который ничего не убивает: в этой проверке гасить нечего.</summary>
    private sealed class SilentKiller : IProcessKiller
    {
        public bool Kill(int pid, long startedAtTicks, out string error)
        {
            error = string.Empty;
            return true;
        }
    }
}






