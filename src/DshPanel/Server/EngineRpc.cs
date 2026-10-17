using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧТО ОТВЕТИЛ ДВИЖОК НА ВЫЗОВ RPC. Вид ответа — не украшение: на нём стоит вся проба
/// поверхности, и ответы, которые нельзя путать, названы по отдельности.
/// </summary>
public enum EngineRpcAnswerKind
{
    /// <summary>Ответ разобран, ошибки нет: метод есть и вызов принят.</summary>
    Ok,

    /// <summary>Метод ЕСТЬ, но доводы не те: движок отказал проверкой доводов. Значит имя признано.</summary>
    Refused,

    /// <summary>Метода НЕТ: движок ответил `404 not found` — до разбора тела.</summary>
    NoMethod,

    /// <summary>Вход не состоялся (`401`/`403`): cookie не принята или её нет.</summary>
    Unauthorized,

    /// <summary>Метод есть, но отказал по другой причине (`gateway/context-*`, `internal`, …).</summary>
    Other,

    /// <summary>Ответ не разобран: это не наш конверт (или наш конверт не разобран движком).</summary>
    Malformed,

    /// <summary>Ответа не дождались или сеть отказала.</summary>
    Transport,
}

/// <summary>
/// Один ответ движка: вид, код HTTP, код ошибки движка (если он назвал) и подробность.
/// <see cref="Detail"/> — техническая строка для отчёта пробы; значения доводов, результатов
/// и секретов сюда не попадают.
/// </summary>
public sealed record EngineRpcAnswer(EngineRpcAnswerKind Kind, int StatusCode, string Code, string Detail)
{
    /// <summary>
    /// Признаёт ли движок ИМЯ метода. Ответ «доводы не те» — признаёт: проверка доводов идёт
    /// ПОСЛЕ поиска метода, и это подтверждено замером. Ответа не дождались — тоже «признаёт»:
    /// незнакомый метод движок отсекает мгновенным `404`, а не молчанием; что движок жив,
    /// отдельно доказывает настоящий вызов пробы.
    /// </summary>
    public bool MethodExists => Kind is EngineRpcAnswerKind.Ok
        or EngineRpcAnswerKind.Refused
        or EngineRpcAnswerKind.Other
        or EngineRpcAnswerKind.Transport;
}

/// <summary>
/// ВХОД В ДВИЖОК: обмен ссылки входа на cookie. Отдельно от вызовов, потому что это РАЗНАЯ
/// работа: вход отвечает `303` и заголовком, вызовы — конвертом.
///
/// **Правило движка (замер `docs\ENGINE.md` §6 и замер 10.10.2026):** `GET &lt;ссылка с токеном&gt;`
/// → `303 SeeOther` плюс `set-cookie: dsh-auth-&lt;base64url(sha256(authority))&gt;`, где
/// `authority` — заголовок `Host` как `host[:port]` (обмен занял 0,094 с). Дальше принимается
/// **только cookie**: ни `Authorization`, ни токен в строке запроса не годятся. Редирект
/// НЕ разворачиваем: за `Location` может стоять что угодно.
///
/// ⚠️ Полученная cookie — секрет сессии: наружу из этого класса выходит только «получена/нет»,
/// и значение не попадает ни в отчёт, ни в журнал.
/// </summary>
public static class EngineRpcLogin
{
    public sealed record Session(bool Ok, int StatusCode, string CookieHeader, string Detail)
    {
        public static Session Failed(string detail) => new(false, 0, string.Empty, detail);
    }

    /// <summary>
    /// КОРЕНЬ ВЫЗОВОВ ПО ССЫЛКЕ ВХОДА — схема, узел и порт, без токена и без пути.
    ///
    /// Отдельной дверью, а не «соберём строку на месте»: ссылка входа — СЕКРЕТ (в ней токен),
    /// и всякий, кто собирает из неё адрес сам, однажды забудет отрезать строку запроса и утащит
    /// токен туда, где ему быть не должно (в заголовок `Referer` чужого запроса, в журнал, в отчёт).
    /// Здесь токен отрезается ОДНИМ местом. Пусто — ссылка не разобралась, и звать некуда.
    /// </summary>
    public static string BaseUrlOf(string? entryUrl)
    {
        if (string.IsNullOrWhiteSpace(entryUrl)) return string.Empty;

        if (!Uri.TryCreate(entryUrl.Trim(), UriKind.Absolute, out var uri)) return string.Empty;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return string.Empty;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static Session Exchange(string? entryUrl, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(entryUrl)) return Session.Failed("ссылки входа нет");

        if (!EntryLinkDecisions.IsLoopbackHost(entryUrl))
        {
            // Токен входа уходит только на свою машину — правило панели (красная линия 7).
            return Session.Failed("ссылка ведёт не на свою машину");
        }

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var http = new HttpClient(handler) { Timeout = timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, entryUrl.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);

            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);

            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values.Select(v => v.Split(';')[0].Trim()))
                : string.Empty;

            var ok = (int)response.StatusCode is >= 200 and < 400 && cookies.Length > 0;

            return new Session(
                ok,
                (int)response.StatusCode,
                ok ? cookies : string.Empty,
                ok ? "cookie получена" : "cookie движок не выдал");
        }
        catch (Exception error)
        {
            return Session.Failed($"{error.GetType().Name}: {error.Message}");
        }
    }
}

/// <summary>
/// ВЫЗОВ ПОВЕРХНОСТИ RPC — одна дверь в движок по HTTP, и живёт она здесь одна.
///
/// **Форма вызова — ЗАМЕР, а не догадка** (снят 10.10.2026 живой пробой на нашем движке
/// `@deepseek-ai/dsh` `0.1.5-rc.2`; первая редакция этого класса звала иначе и получала `404`
/// на КАЖДЫЙ метод — ошибка названа здесь, чтобы её не повторили):
///
/// * **метод стоит В ПУТИ**: `POST /api/&lt;namespace&gt;/&lt;method&gt;`. Запрос на сам `/api`
///   (метод в теле) движок отсекает: `404 not found`. Точечные имена (`session.list`) тоже `404`;
/// * тело — конверт: <c>{"type":"client-request","rpcId":"…","method":"&lt;ns/method&gt;","payload":{"args":{…}}}</c>.
///   ⚠️ Доводы лежат ВНУТРИ <c>payload.args</c>: конверт без `args` движок разбирает как
///   `gateway/bad-request` («invalid client-request message») — и это НЕ признак признания метода;
/// * **неизвестный метод — `404` с телом `not found` (text/plain)**, и отдаётся он ДО разбора тела.
///   Поэтому «не знаю метода» и «не разобрал конверт» не смешиваются, и на этом стоит проба;
/// * **существующий метод отвечает `200 application/json`** — даже когда внутри отказ:
///   `{"type":"server-response","rpcId":"…","result":{"ok":false,"error":{"code":"gateway/…"}}}`;
/// * битый JSON → `400 body is not JSON`; чужой или пустой `content-type` → `415 content type must
///   be application/json`. Оба ответа — «наша форма вызова сломалась», и проба считает их отказом;
/// * потоковые методы (`session/control`, `session/follow`, `workspace/follow`,
///   `workspaceFiles/changes`) одиночным вызовом отвечают `gateway/signature-invalid` («stream
///   Remote methods must be opened through the stream carrier») — имя признано, содержимое нет.
///
/// Смысл пробы: **«доводы не те» доказывает, что имя есть**, а «метода нет» — что его больше нет.
/// </summary>
public sealed class EngineRpcClient : IDisposable
{
    /// <summary>Коды движка, означающие «имя есть, а вызов не принят».</summary>
    private static readonly string[] RefusedCodes =
    {
        "gateway/arguments-invalid",     // замер: args не совпали с дескриптором
        "gateway/input-invalid",         // замер: довод не прошёл проверку вида
        "gateway/bad-request",           // замер: конверт не разобран
        "gateway/signature-invalid",     // замер: потоковый метод зовут одиночным вызовом
        "gateway/binding-invalid",
        "gateway/context-not-found",
        "gateway/context-unavailable",
    };

    /// <summary>Коды движка, означающие «имени нет».</summary>
    private static readonly string[] NoMethodCodes =
    {
        "gateway/method-unavailable",
        "gateway/lookup-not-found",
        "gateway/definition-unavailable",
        "gateway/invocation-unavailable",
        "gateway/lookup-failed",
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _cookie;

    public EngineRpcClient(string baseUrl, string cookieHeader, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUrl = baseUrl.TrimEnd('/');
        _cookie = cookieHeader ?? string.Empty;
        _http = new HttpClient { Timeout = timeout };
    }

    /// <summary>Адрес вызова одного метода: метод — сегмент пути.</summary>
    public string UrlOf(string endpoint) => _baseUrl + "/api/" + endpoint.Trim().Trim('/');

    /// <summary>
    /// КОНВЕРТ ОДИНОЧНОГО ВЫЗОВА — ОДНО место, где он собирается. Вторая сборка конверта
    /// в панели означала бы, что однажды поправят одну из двух, и половина вызовов начнёт
    /// получать `gateway/bad-request` («invalid client-request message»).
    /// </summary>
    public static string Envelope(string endpoint, string? argsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var args = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;

        return "{\"type\":\"client-request\",\"rpcId\":\"" + Guid.NewGuid().ToString("N")
            + "\",\"method\":" + JsonSerializer.Serialize(endpoint)
            + ",\"payload\":{\"args\":" + args + "}}";
    }

    /// <summary>
    /// Позвать метод. <paramref name="argsJson"/> — объект доводов (по умолчанию пустой):
    /// проба называет доводы открытым текстом, чтобы в отчёте было видно, ЧЕМ именно позвали.
    /// </summary>
    public EngineRpcAnswer Call(string endpoint, string argsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var body = Envelope(endpoint, argsJson);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, UrlOf(endpoint))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);
            if (_cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", _cookie);

            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
            var status = (int)response.StatusCode;
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (status is 401 or 403)
                return new EngineRpcAnswer(EngineRpcAnswerKind.Unauthorized, status, string.Empty, "вход не принят");

            // 404 приходит ДО разбора тела и означает ровно одно: такого метода у движка нет.
            if (status == 404)
                return new EngineRpcAnswer(EngineRpcAnswerKind.NoMethod, status, "http/404", "движок не знает такого метода");

            // 400/415 — это про НАШ конверт, а не про метод: значит форма вызова разошлась
            // с движком. Проба обязана на этом падать, поэтому вид назван «не разобран».
            if (status is 400 or 415)
                return new EngineRpcAnswer(EngineRpcAnswerKind.Malformed, status, $"http/{status}", Short(text));

            var code = ErrorCode(text);
            var envelope = LooksLikeOurEnvelope(text);

            if (!envelope)
                return new EngineRpcAnswer(EngineRpcAnswerKind.Malformed, status, code, Short(text));

            if (code.Length == 0)
                return new EngineRpcAnswer(EngineRpcAnswerKind.Ok, status, string.Empty, Short(text));

            var kind = RefusedCodes.Contains(code) ? EngineRpcAnswerKind.Refused
                : NoMethodCodes.Contains(code) ? EngineRpcAnswerKind.NoMethod
                : EngineRpcAnswerKind.Other;

            return new EngineRpcAnswer(kind, status, code, Short(text));
        }
        catch (TaskCanceledException)
        {
            // Ответа не дождались. Это НЕ «метода нет»: неизвестный метод отсекается мгновенным
            // 404. Что движок жив, доказывает настоящий вызов пробы.
            return new EngineRpcAnswer(EngineRpcAnswerKind.Transport, 0, "timeout", "ответа не дождались");
        }
        catch (Exception error)
        {
            return new EngineRpcAnswer(EngineRpcAnswerKind.Transport, 0, error.GetType().Name, "сеть отказала");
        }
    }

    /// <summary>
    /// ПОЗВАТЬ МЕТОД И ОТДАТЬ ОТВЕТ **КАК ЕСТЬ** — код и тело, без разбора и без обрезки.
    ///
    /// Нужен ровно одному читателю — калитке телефона (этап F). Её правило звучит так: **ответ
    /// движка уходит телефону тем же, чем пришёл**. `Call` для этого не годится: он нарочно
    /// обрезает тело до 160 знаков (`Short`) и оставляет только вид и код ошибки — в отчёт пробе
    /// этого хватает, а телефону в `session/list` нужен весь список.
    ///
    /// ⚠️ Конверт здесь ТОТ ЖЕ (<see cref="Envelope"/>): вторая сборка конверта разошлась бы
    /// с первой, и телефон получал бы `gateway/bad-request` там, где проба отвечает `200`.
    /// Cookie кладётся так же, как у `Call`, — значит «ходит в движок как петля» остаётся
    /// свойством ОДНОГО класса, а не обещанием калитки.
    /// </summary>
    public PhoneEngineAnswer CallRaw(string endpoint, string argsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, UrlOf(endpoint))
            {
                Content = new StringContent(Envelope(endpoint, argsJson), Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);
            if (_cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", _cookie);

            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            return new PhoneEngineAnswer((int)response.StatusCode, text);
        }
        catch (Exception error)
        {
            // Ответа не дождались — это НЕ «движок отказал»: калитка обязана сказать телефону
            // словами, что движка не слышно, а не выдать пустоту за ответ.
            return PhoneEngineAnswer.Failed(error.GetType().Name);
        }
    }

    /// <summary>
    /// РУКОПОЖАТИЕ КАНАЛА УПРАВЛЕНИЯ (`/api/remote.mux`). Отдельная проба, потому что потоковый
    /// вызов одиночным запросом не отвечает вовсе: `session/control` на `POST` даёт
    /// `gateway/signature-invalid` («stream Remote methods must be opened through the stream
    /// carrier»). Значит «имя признано» тут ничего не доказывает — доказывает только открытый
    /// канал, и открываем мы его тем же cookie, что и вызовы.
    ///
    /// Ответ на вопрос агента без этого канала невозможен, и умение обязано быть проверено
    /// по-настоящему: иначе панель пообещает то, чего не умеет.
    /// </summary>
    public bool MuxHandshake(TimeSpan timeout)
    {
        try
        {
            var http = _baseUrl + "/api/remote.mux";
            var scheme = http.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
            var endpoint = new Uri(scheme + http[(http.IndexOf("://", StringComparison.Ordinal) + 3)..]);

            using var socket = new System.Net.WebSockets.ClientWebSocket();

            // Своего прокси у петли быть не должно: системный прокси увёл бы пробу на чужой адрес.
            socket.Options.Proxy = null;
            socket.Options.SetRequestHeader("User-Agent", ProductLinks.UserAgent);
            if (_cookie.Length > 0) socket.Options.SetRequestHeader("Cookie", _cookie);

            using var stop = new CancellationTokenSource(timeout);
            socket.ConnectAsync(endpoint, stop.Token).GetAwaiter().GetResult();

            return socket.State == System.Net.WebSockets.WebSocketState.Open;
        }
        catch
        {
            // Канал не открылся — это ответ «не умею ответить на вопрос», а не падение пробы.
            return false;
        }
    }

    /// <summary>Наш ли это конверт: у ответа движка есть <c>rpcId</c> и <c>result</c>.</summary>
    private static bool LooksLikeOurEnvelope(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("result", out _)
                && root.TryGetProperty("rpcId", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Код ошибки движка (`gateway/…`), если он отказал с разобранным конвертом.</summary>
    public static string ErrorCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!document.RootElement.TryGetProperty("result", out var result)) return string.Empty;
            if (!result.TryGetProperty("error", out var error)) return string.Empty;
            if (!error.TryGetProperty("code", out var code)) return string.Empty;

            return code.GetString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Ответ в отчёт — обрезанный и без значения результата: в значении бывают и пути, и имена
    /// сессий человека. Пробе хватает вида и кода ошибки.
    /// </summary>
    private static string Short(string? text)
    {
        var flat = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    public void Dispose() => _http.Dispose();
}
