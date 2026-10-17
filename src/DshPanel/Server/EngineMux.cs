using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧТО ПРИШЛО КАДРОМ ИЗ ПОТОКА <c>$events</c>. Вид назван по смыслу, а не по имени кадра: калитке
/// важно не «какой это JSON», а «это наш ключ», «это вопрос человеку», «вопрос сняли» и «канал
/// сломался».
/// </summary>
public enum PhoneEventKind
{
    /// <summary>Кадр не про нас (например `emit` чужого события) — пропускается молча, но виден в журнале.</summary>
    Other,

    /// <summary>Первый кадр потока: у нас есть СВОЙ `clientId`. Наружу (телефону) он не выходит никогда.</summary>
    Ready,

    /// <summary>Водопад `user-questions/request`: агент спрашивает человека, и это надо показать телефону.</summary>
    Question,

    /// <summary>Кто-то ответил раньше нас: вопрос снят. Молчать об этом нельзя — иначе вопрос «висит».</summary>
    Cancelled,

    /// <summary>Ошибка кадра или обрыв потока: канал чинить, и об этом говорят словами.</summary>
    Failure,
}

/// <summary>
/// РАЗОБРАННЫЙ КАДР. <see cref="ClientId"/> заполняется ТОЛЬКО у <see cref="PhoneEventKind.Ready"/>,
/// <see cref="EventId"/> и <see cref="QuestionsJson"/> — у вопроса и у снятия.
///
/// ⚠️ **Здесь НЕТ поля под `host`** из кадра `ready`, и это не упущение, а конструкция: замер
/// <c>docs\PHONE-LIVE.md</c> §3 показал, что `ready` отдаёт ДОМАШНИЙ КАТАЛОГ машины. Поля,
/// в которое его можно положить, у этого типа нет вовсе, — значит «утекло по дороге» невозможно
/// не по внимательности, а по устройству.
///
/// <see cref="Detail"/> — короткая строка ДЛЯ ЖУРНАЛА. В неё идёт только то, что панель написала
/// сама либо код ошибки движка: текст ошибки движка в журнал не переносится, потому что в нём
/// бывают пути (правило красной линии 7 — пути проходят через `DisplayMask`, а проще не носить).
/// </summary>
public sealed record PhoneEventFrame(
    PhoneEventKind Kind,
    string ClientId,
    string EventId,
    string QuestionsJson,
    string Detail,
    string AgentId = "")
{
    /// <summary>Кадр, о котором калитке сказать нечего.</summary>
    public static PhoneEventFrame Other(string detail) =>
        new(PhoneEventKind.Other, string.Empty, string.Empty, "[]", detail);
}

/// <summary>
/// РАЗБОР КАДРА ПОТОКА <c>$events</c> — ЧИСТАЯ функция от строки. Ни сети, ни сокета, ни состояния
/// здесь нет, и это не стилистика: разбор проверяется подставными кадрами (в том числе теми, что
/// сняты живьём в <c>docs\PHONE-LIVE.md</c> §4), а не живым движком.
///
/// Форма кадра — замер, а не догадка: от движка приходит <c>{type:'item',streamId,value:{…}}</c>,
/// и полезное лежит в <c>value</c>. Поток событий приложения шлёт внутри <c>value</c> кадры
/// `ready` · `emit` · `waterfall` · `cancel`.
/// </summary>
public static class PhoneEventFrames
{
    /// <summary>Имя водопада «агент спрашивает человека». Одно место на панель.</summary>
    public const string QuestionEvent = "user-questions/request";

    /// <summary>Разобрать кадр. Что угодно непонятное — <see cref="PhoneEventFrame.Other"/>: поток
    /// чужой работы не имеет права ронять калитку исключением.</summary>
    public static PhoneEventFrame Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PhoneEventFrame.Other("пустой кадр");

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(text!);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return PhoneEventFrame.Other("не объект");
            root = document.RootElement.Clone();
        }
        catch
        {
            return PhoneEventFrame.Other("не разобран");
        }

        var type = Text(root, "type");

        // ОШИБКА ПОТОКА — это Failure, а не «мимо»: молчание о ней выглядело бы живым каналом,
        // и телефон ждал бы вопроса, которого уже никто не принесёт.
        if (string.Equals(type, "error", StringComparison.Ordinal))
        {
            var code = string.Empty;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                code = Text(error, "code");

            return new PhoneEventFrame(PhoneEventKind.Failure, string.Empty, string.Empty, "[]",
                code.Length > 0 ? code : "ошибка потока");
        }

        if (string.Equals(type, "end", StringComparison.Ordinal))
            return new PhoneEventFrame(PhoneEventKind.Failure, string.Empty, string.Empty, "[]", "поток закрыт движком");

        if (!string.Equals(type, "item", StringComparison.Ordinal))
            return PhoneEventFrame.Other(type.Length > 0 ? type : "без вида");

        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return PhoneEventFrame.Other("кадр без value");

        var valueType = Text(value, "type");

        switch (valueType)
        {
            case "ready":
                // ⚠️ `host` из этого кадра НЕ ЧИТАЕТСЯ ВООБЩЕ — ни в поле, ни в журнал.
                return new PhoneEventFrame(PhoneEventKind.Ready, Text(value, "clientId"), string.Empty, "[]", "готово");

            case "cancel":
                return new PhoneEventFrame(PhoneEventKind.Cancelled, string.Empty, Text(value, "eventId"), "[]", "снят");

            case "waterfall":
                var name = Text(value, "event");
                if (!string.Equals(name, QuestionEvent, StringComparison.Ordinal))
                    return PhoneEventFrame.Other("водопад " + name);

                return new PhoneEventFrame(
                    PhoneEventKind.Question,
                    string.Empty,
                    Text(value, "eventId"),
                    Questions(value),
                    "вопрос",

                    // ⚠️ `agentId` читается ТОЛЬКО затем, чтобы страница телефона могла ОТМЕНИТЬ ход
                    // (F2): вопрос без ответившего висит вечно, и снимает его `session/cancel`
                    // с `sessionId` (движок ищет агента в реестре по нему — замер чтением §4 отчёта F2).
                    // Других полей кадра здесь не читается ни одного: `request` уже отфильтрован
                    // (`Questions`), а служебные поля движка телефону не нужны.
                    Text(value, "agentId"));

            default:
                // `emit` и всё прочее — не наша работа: калитка их не показывает и о них не молчит
                // в журнале (иначе журнал панели стал бы потоком чужой жизни).
                return PhoneEventFrame.Other(valueType.Length > 0 ? valueType : "без вида кадра");
        }
    }

    /// <summary>
    /// ВОПРОСЫ ТЕЛЕФОНУ — из кадра берётся РОВНО <c>request.questions</c> и заново пишется в JSON.
    /// Не «вырезаем кусок строки»: вырезанный кусок однажды унёс бы вместе с вопросами соседнее
    /// поле кадра (а в соседях у `waterfall` есть и `agentId`, и служебные поля движка).
    /// Вопросов нет — пустой массив: телефон обязан увидеть «вопрос без вопросов», а не пустоту.
    /// </summary>
    private static string Questions(JsonElement value)
    {
        if (!value.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
            return "[]";

        if (!request.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return "[]";

        return questions.GetRawText();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// ГОТОВЫЕ КАДРЫ, которые калитка ШЛЁТ движку. Вынесены отдельно и собираются текстом ровно
/// затем, чтобы форма открытия потока была проверена без сокета: это тот же довод, по которому
/// отдельно живёт конверт вызова (<see cref="EngineRpcClient.Envelope"/>).
/// </summary>
public static class EngineMuxFrames
{
    /// <summary>
    /// КАДР ОТКРЫТИЯ ПОТОКА: <c>{type:'open',streamId,endpoint,payload:{args:{…}}}</c> — замер
    /// <c>docs\PHONE-LIVE.md</c> §3. Доводы `$events` пустые: кадр `ready` с `clientId` приходит
    /// сам, и просить больше нечего.
    /// </summary>
    public static string Open(string streamId, string endpoint, string argsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var args = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;

        return "{\"type\":\"open\",\"streamId\":" + JsonSerializer.Serialize(streamId)
            + ",\"endpoint\":" + JsonSerializer.Serialize(endpoint)
            + ",\"payload\":{\"args\":" + args + "}}";
    }

    /// <summary>Кадр закрытия потока: <c>{type:'cancel',streamId}</c>.</summary>
    public static string Cancel(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        return "{\"type\":\"cancel\",\"streamId\":" + JsonSerializer.Serialize(streamId) + "}";
    }
}

/// <summary>
/// СКЛЕЙКА ОБРЫВКОВ. Кадр движка приходит сообщением WebSocket, а сообщение бывает разрезано на
/// несколько частей (у `ClientWebSocket` это `EndOfMessage = false`). Собирать их обязан кто-то один:
/// разберись калитка сама — и половина кадра однажды была бы разобрана как целый, то есть «мимо».
///
/// Класс отдельный и без сокета, поэтому проверяется прямой подачей двух кусков.
/// </summary>
public sealed class MuxTextBuffer
{
    private readonly MemoryStream _carried = new();

    /// <summary>Добавить кусок. Отвечает готовым кадром — или <c>null</c>, если кадр ещё не целый.</summary>
    public string? Add(byte[] chunk, int count, bool endOfMessage)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (count > 0) _carried.Write(chunk, 0, count);
        if (!endOfMessage) return null;

        var text = Encoding.UTF8.GetString(_carried.ToArray());
        _carried.SetLength(0);

        return text;
    }
}

/// <summary>
/// ССЫЛКА НА ОТКРЫТУЮ ПОДПИСКУ. Калитке от неё нужно ровно одно: «жива ли» — чтобы переоткрыть,
/// когда оборвалась (замер §4: события приложения НЕ переигрываются, и без подписки вопрос агента
/// висит вечно).
/// </summary>
public interface IPhoneEventLink : IDisposable
{
    /// <summary>Жива ли подписка ПРЯМО СЕЙЧАС. <c>false</c> — калитка обязана переоткрыть.</summary>
    bool Alive { get; }
}

/// <summary>
/// ДВЕРЬ К ПОТОКУ СОБЫТИЙ ДВИЖКА — делегатами, а не классом сокета. По той же причине, по которой
/// дверями сделаны пропуск и движок: калитка обязана проверяться БЕЗ движка и без сети —
/// подставная дверь отдаёт кадры `ready`, `waterfall` и `cancel` по заказу проверки.
///
/// ⚠️ **<see cref="Present"/> — ВОПРОС, А НЕ СНИМОК, и это стоило живого дефекта (14.10.2026).**
/// Прежде признак был полем записи: его считали один раз — когда двери СОБИРАЛИСЬ, то есть на
/// старте панели. А ссылка входа в этот миг ещё не известна: контроллер отдаёт её только тогда,
/// когда свой сервер уже узнан — после перезапуска панели это происходит секундой позже, по записи
/// `own-server.txt`. Итог: признак навсегда оставался `false`, калитка не открывала подписку вовсе,
/// телефон молчал даже сопряжённым, а раздел «Телефон» говорил «связи с сервером DSH нет» про
/// работающий СВОЙ сервер. Поэтому теперь он **спрашивается в тот миг, когда о нём спрашивают** —
/// как адрес и cookie движка в <see cref="Doors"/>.
///
/// <see cref="Present"/> = <c>false</c> — входа нет ПРЯМО СЕЙЧАС. Тогда калитка работает, но
/// говорит словами, что вопрос агента показать нечем; появится вход — подписка откроется сама,
/// без перезапуска панели.
/// </summary>
public sealed record PhoneEventDoors(
    Func<Action<string>, IPhoneEventLink?> Open,
    Action<string> Log,
    Func<bool>? CanOpen = null)
{
    /// <summary>
    /// Вход есть ПРЯМО СЕЙЧАС. Спрашивается у двери всякий раз заново: ссылка входа появляется
    /// и исчезает вместе с сервером, а двери собираются один раз — на старте панели.
    ///
    /// ⚠️ Не задано — считается, что вход есть: так живут подставные двери проверок, которым
    /// нечего спрашивать (и это безопасная сторона ровно как у прочих дверей модуля: подставная
    /// дверь без признака ведёт себя как настоящая рабочая, а не как «входа нет»).
    /// </summary>
    public bool Present => CanOpen?.Invoke() ?? true;

    /// <summary>Двери нет: открывать нечего — и не появится.</summary>
    public static PhoneEventDoors None { get; } = new(_ => null, _ => { }, () => false);

    /// <summary>
    /// ДВЕРЬ, КОТОРАЯ ОТКРЫВАЕТСЯ, КОГДА ПОЯВИТСЯ ВХОД. <paramref name="canOpen"/> — вопрос
    /// «есть ли вход сейчас»; сам вход (адрес и cookie) спрашивается в момент открытия
    /// (<see cref="EngineMux.Doors"/>).
    /// </summary>
    public static PhoneEventDoors When(Func<bool> canOpen, Func<Action<string>, IPhoneEventLink?> open, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);

        return new PhoneEventDoors(open, log, canOpen);
    }
}

/// <summary>
/// НАСТОЯЩИЙ КЛИЕНТ МУЛЬТИПЛЕКСОРА ДВИЖКА: <c>WS /api/remote.mux</c>, кадр `open` на поток
/// <c>$events</c>, чтение кадров до обрыва.
///
/// **Зачем отдельным файлом, а не внутри калитки.** Калитка — про наружную дверь: кто пущен, что
/// ему можно, что он видит. Здесь — про движок: рукопожатие, кадры, обрыв. Смешай их — и «калитка
/// пускает кого попало» проверялось бы только живым движком, а «поток разбирается верно» — только
/// через слушатель. Разведены затем, чтобы каждое проверялось там, где оно живёт: калитка —
/// подставной дверью, разбор кадров — подставными кадрами (<see cref="PhoneEventFrames"/>).
///
/// **Почему в потоке ВСЕГДА что-то сломается и это нормально.** События приложения не
/// переигрываются (замер §4): после сна телефона, после перезапуска движка подписку надо
/// поднимать заново, и вопрос, заданный в промежуток, калитка НЕ УВИДИТ. Это названо честно —
/// и в коде, и в отчёте, — а не спрятано за «переподключимся и всё догоним».
/// </summary>
public sealed class EngineMux : IPhoneEventLink
{
    /// <summary>Путь канала управления. Тот же, каким проверяет умение «ответ на вопрос» проба.</summary>
    public const string MuxPath = "/api/remote.mux";

    /// <summary>Имя нашего потока в мультиплексоре. Своё, чтобы не путать с чужими подписками.</summary>
    public const string StreamId = "phone-events";

    /// <summary>Имя потока событий приложения (замер §3).</summary>
    public const string EventsEndpoint = PhoneDecisions.EventsEndpoint;

    private readonly ClientWebSocket _socket;
    private readonly Action<string> _onFrame;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly MuxTextBuffer _buffer = new();

    private volatile bool _alive;

    private EngineMux(ClientWebSocket socket, Action<string> onFrame, Action<string> log)
    {
        _socket = socket;
        _onFrame = onFrame;
        _log = log;
    }

    /// <summary>Жива ли подписка: и сокет открыт, и чтение не закончилось.</summary>
    public bool Alive => _alive && _socket.State == WebSocketState.Open;

    /// <summary>
    /// ДВЕРЬ ДЛЯ КАЛИТКИ: адрес и cookie движка спрашиваются В МОМЕНТ открытия, а не при сборке
    /// двери. Это не мелочь: cookie движка живёт в замыкании рядом с вызовом
    /// (<see cref="PhoneGateRealDoors"/>) и обновляется после отказа входа — прочитай её один раз
    /// при сборке, и после перезапуска движка подписка открывалась бы со старым входом всегда.
    /// </summary>
    public static PhoneEventDoors Doors(
        Func<string> baseUrl,
        Func<string> cookie,
        TimeSpan timeout,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(log);

        return new PhoneEventDoors(
            onFrame => Open(baseUrl(), cookie(), timeout, onFrame, log),
            log);
    }

    /// <summary>
    /// ТА ЖЕ ДВЕРЬ, НО С ВОПРОСОМ «ЕСТЬ ЛИ ВХОД СЕЙЧАС» (<paramref name="canOpen"/>).
    ///
    /// **От какого случая (живой дефект 14.10.2026).** Ссылка входа появляется у панели НЕ при
    /// сборке дверей: после перезапуска панели свой сервер узнаётся по записи `own-server.txt`
    /// секундой позже, и только тогда контроллер отдаёт ссылку. Собери <see cref="Present"/> один
    /// раз при сборке — и он навсегда скажет «входа нет»: калитка не откроет подписку вовсе,
    /// телефон промолчит даже сопряжённым, а человек прочитает «связи с сервером DSH нет» про
    /// работающий СВОЙ сервер. Поэтому признак спрашивается в тот миг, когда о нём спрашивают.
    ///
    /// ⚠️ Вход (адрес и cookie) по-прежнему берётся В МОМЕНТ ОТКРЫТИЯ — иначе после перезапуска
    /// движка подписка открывалась бы со старым входом всегда (правило записано над
    /// <see cref="Doors"/> и здесь не отменяется).
    /// </summary>
    public static PhoneEventDoors DoorsWhen(
        Func<bool> canOpen,
        Func<string> baseUrl,
        Func<string> cookie,
        TimeSpan timeout,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(log);

        return PhoneEventDoors.When(
            canOpen,
            onFrame => Open(baseUrl(), cookie(), timeout, onFrame, log),
            log);
    }

    /// <summary>
    /// ОТКРЫТЬ ПОДПИСКУ. <c>null</c> — не открылась, и об этом сказано в журнал словами: молчаливое
    /// «нет подписки» выглядело бы живой калиткой, у которой просто нет вопросов.
    /// </summary>
    public static IPhoneEventLink? Open(
        string? baseUrl,
        string? cookieHeader,
        TimeSpan timeout,
        Action<string> onFrame,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            log(PanelStrings.PhoneEventNoEngine);
            return null;
        }

        ClientWebSocket? socket = null;

        try
        {
            socket = new ClientWebSocket();

            // Своего прокси у петли быть не должно: системный прокси увёл бы подписку на чужой адрес.
            socket.Options.Proxy = null;
            socket.Options.SetRequestHeader("User-Agent", ProductLinks.UserAgent);
            ApplyCookie(socket.Options, baseUrl!, cookieHeader, log);

            using (var stop = new CancellationTokenSource(timeout))
                socket.ConnectAsync(Endpoint(baseUrl!), stop.Token).GetAwaiter().GetResult();

            var link = new EngineMux(socket, onFrame, log);

            link.Send(EngineMuxFrames.Open(StreamId, EventsEndpoint));
            link.Pump();

            return link;
        }
        catch (Exception error)
        {
            socket?.Dispose();

            log(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.PhoneEventOpenFailedFormat,
                error.GetType().Name));

            return null;
        }
    }

    /// <summary>
    /// КУДА СОКЕТОМ. Отдельной функцией и по той же причине, что у пробы: строка собирается ОДИН
    /// раз, и «http → ws, https → wss» не разъезжается между рукопожатием и подпиской.
    /// </summary>
    public static Uri Endpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var text = baseUrl.TrimEnd('/');
        var scheme = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";

        return new Uri(scheme + text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..] + MuxPath);
    }

    /// <summary>
    /// ПОЛОЖИТЬ COOKIE ДВИЖКА. Отвечает, приложилась ли она, — и это не украшение: без cookie
    /// рукопожатие получает `401` (замер §1), то есть подписка не откроется ВООБЩЕ, и «вопросов
    /// нет» было бы неправдой.
    ///
    /// Два способа, и первый — документированный для `ClientWebSocket` (<c>Options.Cookies</c>).
    /// Заголовок напрямую оставлен запасным: `Cookie` — ограниченный заголовок, и `SetRequestHeader`
    /// на нём в одних версиях .NET бросает, в других молчит. Молчащую потерю cookie ловит
    /// возвращаемое значение, а не надежда.
    /// </summary>
    public static bool ApplyCookie(ClientWebSocketOptions options, string baseUrl, string? cookieHeader, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(cookieHeader)) return false;

        try
        {
            var container = new CookieContainer();
            container.SetCookies(new Uri(baseUrl), cookieHeader!);
            options.Cookies = container;

            return true;
        }
        catch
        {
            // Разобрать контейнером не вышло — пробуем заголовком.
        }

        try
        {
            options.SetRequestHeader("Cookie", cookieHeader!);
            return true;
        }
        catch
        {
            log(PanelStrings.PhoneEventCookieFailed);
            return false;
        }
    }

    private void Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _stop.Token)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// ЧТЕНИЕ КАДРОВ — фоном, до обрыва. Обрыв НЕ прячется: он гасит признак «жива» (калитка
    /// переоткроет подписку) и говорит о себе строкой в журнал.
    /// </summary>
    private void Pump()
    {
        _alive = true;

        _ = Task.Run(async () =>
        {
            var chunk = new byte[8192];

            try
            {
                while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var received = await _socket.ReceiveAsync(chunk, _stop.Token);

                    if (received.MessageType == WebSocketMessageType.Close) break;

                    var frame = _buffer.Add(chunk, received.Count, received.EndOfMessage);
                    if (frame is not null) _onFrame(frame);
                }
            }
            catch (Exception)
            {
                // Обрыв, отмена или закрытие слушателя — во всех случаях подписки больше нет.
            }
            finally
            {
                // «Оборвалась» говорится ТОЛЬКО про настоящий обрыв. Своё закрытие (гашение калитки)
                // в журнал не идёт: иначе каждая остановка панели писала бы беду, которой нет.
                var wasAlive = _alive;
                _alive = false;

                if (wasAlive && !_stop.IsCancellationRequested) _log(PanelStrings.PhoneEventLostLog);
            }
        });
    }

    public void Dispose()
    {
        _alive = false;

        try
        {
            _stop.Cancel();
        }
        catch
        {
            // Отмена уже отменённого — не беда.
        }

        try
        {
            _socket.Dispose();
        }
        catch
        {
            // Сокет уже закрыт — этого и добивались.
        }

        _stop.Dispose();
    }
}
