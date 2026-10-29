using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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
            Func<IServerControl?>? server = null)
        {
            _root = root;
            Paths = AppPaths.Under(root);
            Port = port ?? FreeLoopbackPort();
            EventDoors = events;

            Doors = new PhoneGateDoors(
                () => address ?? string.Empty,
                Port,
                Engine,
                passFiles ?? PhonePassFiles.Under(Paths, Log.Add),
                EventDoors,
                () => Now,
                Log.Add,
                Bound: value => BoundTo = value,
                Server: server);

            Gate = new PhoneGate(Doors, () => enabled, human, isolated ? Isolated() : Owner());
            Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + Port + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public AppPaths Paths { get; }

        public int Port { get; }

        public PhoneGateDoors Doors { get; }

        public PhoneEventDoors EventDoors { get; }

        public PhoneGate Gate { get; }

        public HttpClient Http { get; }

        public List<EngineCall> Calls { get; } = new();

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
            Calls.Add(new EngineCall(endpoint, args));

            return Reply;
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
        Func<IServerControl?>? server = null) =>
        new(
            PanelTestStand.TempDir(), enabled, human, isolated, address, port,
            events ?? new FakeEvents().Doors, passFiles, server);

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

        await client.ConnectAsync(IPAddress.Loopback, stand.Port);

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

        await client.ConnectAsync(IPAddress.Loopback, stand.Port);

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

        // А остальные вызовы по-прежнему уходят телефону БЕЗ разбора и обрезки: то же тело байт в байт.
        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"ok\":true,\"value\":{\"items\":[{\"sessionId\":\"s-9\"}]}}}");

        var other = await Send(
            stand.Http, HttpMethod.Post, "/api/session/page", "{\"args\":{\"request\":{}}}", cookie);

        Assert.Equal(stand.Reply.Body, other.Body);
    }

    /// <summary>
    /// ФОРМА ОТВЕТА НЕ ТА, КАКУЮ МЫ МЕРИЛИ, — ОТВЕТ УХОДИТ КАК ЕСТЬ, и причина названа в журнале.
    ///
    /// Это и есть граница фильтра: отфильтровать наугад значило бы однажды выбросить из списка
    /// живые разговоры. Поэтому «не смогли» здесь честнее, чем «попробовали».
    /// </summary>
    [Fact]
    public async Task Незнакомая_форма_списка_уходит_как_есть()
    {
        using var stand = NewStand();

        Assert.True(Started(stand));

        var cookie = await Pair(stand);

        stand.Reply = new PhoneEngineAnswer(200, "{\"result\":{\"ok\":true,\"sessions\":[{\"id\":1}]}}");

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie);

        Assert.Equal(200, reply.Status);
        Assert.Equal(stand.Reply.Body, reply.Body);
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

        var reply = await Send(stand.Http, HttpMethod.Get, "/");
        Assert.Equal(200, reply.Status);

        stand.Gate.Stop();

        Assert.False(stand.Gate.Listening);
        Assert.True(CanBind(stand.Port), $"порт {stand.Port} не освободился после остановки калитки");
        Assert.Contains(stand.Log, line => line.Contains("закрыто", StringComparison.OrdinalIgnoreCase));
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

        var reply = await Send(stand.Http, HttpMethod.Get, "/");

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
            var reply = await Send(stand.Http, HttpMethod.Get, "/", language: language);
            var intro = Loc.TIn(language, nameof(PanelStrings.PhonePageIntro));

            Assert.Equal(200, reply.Status);
            Assert.Contains(intro, reply.Body, StringComparison.Ordinal);
            seen.Add(reply.Body);
        }

        Assert.Equal(3, seen.Distinct(StringComparer.Ordinal).Count());

        // Язык панели (ru в прогоне проверок) — когда телефон ничего не просил.
        var plain = await Send(stand.Http, HttpMethod.Get, "/");
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
        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: value);

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
        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: value);

        Assert.Equal(200, page.Status);

        Assert.Contains(Loc.T(PanelStrings.PhonePageNoEngine), page.Body, StringComparison.Ordinal);
        Assert.Contains(Loc.T(PanelStrings.PhonePageNoEngineNext), page.Body, StringComparison.Ordinal);

        // И НИ ОДНОГО СЛОВА ОЖИДАНИЯ: оно здесь — обещание, которого не будет.
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageWaiting), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(PanelStrings.PhonePageWaitingNext), page.Body, StringComparison.Ordinal);

        // КАРТОЧКА ОПРОСА — ТО ЖЕ САМОЕ: связь появится, и карточка сменится сама.
        var card = await Send(stand.Http, HttpMethod.Get, "/?part=card", cookie: value);

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

        // Ответ движка отдан КАК ЕСТЬ, без обрезки и без добавления своего.
        Assert.Equal(stand.Reply.Body, reply.Body);
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

        var reply = await Send(stand.Http, HttpMethod.Get, "/");

        Assert.Equal(200, reply.Status);
        Assert.Contains("id=\"code\"", reply.Body, StringComparison.Ordinal);

        // 3 908 знаков — страница входа по-русски на ревизии этой правки; столько же она отдавала
        // ДО неё. Число — не «красивое», а СНЯТОЕ: правка чтения обязана ответ не менять.
        Assert.Equal(3908, reply.Body.Length);
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
            ("страница входа", RawRequest("GET", "/", host), 200),
            ("страница телефона", RawRequest("GET", "/", host, cookie), 200),
            ("перенаправление после входа", RawRequest("POST", "/pair", host, body: "code=" + code), 303),
            ("отказ: вопроса нет",
                RawRequest("POST", "/answer", host, cookie, "{\"eventId\":\"event-7\",\"answers\":[]}"), 403),
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
        var page = await Send(stand.Http, HttpMethod.Get, "/", host: foreign);

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
        var page = await Send(stand.Http, HttpMethod.Get, "/", host: ownHost);

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
        var page = await Send(stand.Http, HttpMethod.Get, "/");
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
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"event-7\",\"answers\":[]}", cookie);

        Assert.Equal(403, nothing.Status);
        Assert.False(string.IsNullOrWhiteSpace(nothing.Body));

        events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-42\"}}");
        events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"event-7\",\"request\":{\"questions\":[]}}}");

        // А теперь вопрос есть, и отвечать ЕСТЬ ЧЕМ (наш `clientId` пришёл) — но отвечают
        // НЕ на него. Отказ обязан быть именно про чужой вопрос, а не «нечем отправить».
        var stale = await Send(
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"чужой\",\"answers\":[]}", cookie);

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
            "{\"eventId\":\"event-1\",\"answers\":[]}", cookie);

        Assert.Equal(403, old.Status);

        // В ДВИЖОК НЕ УШЛО НИЧЕГО, КРОМЕ ОТМЕНЫ ПРЕЖНЕГО ХОДА.
        Assert.DoesNotContain(stand.Calls, call => call.Endpoint == PhoneDecisions.EventsResultEndpoint);

        // А НА НОВЫЙ — уходит, и именно на него.
        var fresh = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "{\"eventId\":\"event-2\",\"answers\":[]}", cookie);

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

        var anon = await Send(stand.Http, HttpMethod.Get, "/");

        Assert.Equal(200, anon.Status);
        Assert.Contains(Loc.T(PanelStrings.PhonePageCodeLabel), anon.Body, StringComparison.Ordinal);

        var cookie = await Pair(stand);
        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

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

        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

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

        var after = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

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
        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

        Assert.Contains(Loc.T(PanelStrings.PhonePageAnswered), page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", page.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРОИГРАВШИЙ ЧИТАЕТ СЛОВАМИ, А НЕ СМОТРИТ НА МЁРТВЫЕ КНОПКИ: вопрос взял кто-то другой
    /// (или агент его снял) — страница обязана сказать это, а кнопок не показывать.
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

        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

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
        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

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
            stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"event-7\",\"answers\":[]}", cookie);

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

        var start = WindowlessTests.BodyAt(gate, "public PhoneGateStart Start(");

        Assert.False(string.IsNullOrWhiteSpace(start), "тело запуска калитки не найдено — проверять нечего");
        Assert.Contains("RunRights.MayOpenPhoneGate", start, StringComparison.Ordinal);
        Assert.Contains("PortForbidden", start, StringComparison.Ordinal);

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
