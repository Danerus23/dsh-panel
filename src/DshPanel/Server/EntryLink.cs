using System.Text.RegularExpressions;

namespace DshPanel.Server;

/// <summary>
/// Ссылка входа в панель — то, что движок печатает при запуске своим выводом:
/// <c>dsh web: http://127.0.0.1:3080/?token=…</c>.
///
/// ⚠️ В ссылке лежит ТОКЕН, и это делает её секретом (красная линия 7): в журнал, в отчёт,
/// в подписи окон и в кадры <c>--shot</c> она не попадает. Наружу она выходит ровно одним
/// способом — в браузер человека по его щелчку.
/// </summary>
public readonly record struct EntryLink(string Url, int Port)
{
    /// <summary>Ссылки нет. Не «ссылка на порт 0», а именно отсутствие: так и говорят человеку.</summary>
    public static EntryLink None => new(string.Empty, 0);

    public bool IsEmpty => Url.Length == 0;
}

/// <summary>
/// Что ответил порт по ССЫЛКЕ. Отдельным значением, а не «да/нет» от измеряющего: правило
/// годности ссылки — ЧИСТОЕ решение, и оно обязано перебираться проверками, а не жить внутри
/// HTTP-запроса, которого в тестах нет.
/// </summary>
/// <param name="StatusCode">Код ответа; при <paramref name="Failed"/> не значит ничего.</param>
/// <param name="SetCookie">
/// Сервер выдал cookie входа. Это и есть признак «токен принят».
/// </param>
/// <param name="Failed">Запрос не прошёл вовсе: порт не ответил, имени не разобрать, таймаут.</param>
public readonly record struct LinkProbe(int StatusCode, bool SetCookie, bool Failed)
{
    /// <summary>Запрос не прошёл. Молчание — не подтверждение.</summary>
    public static LinkProbe Failed2 => new(0, false, true);

    public static LinkProbe Answer(int statusCode, bool setCookie) => new(statusCode, setCookie, false);
}

/// <summary>
/// Разбор ссылки входа — ЧИСТЫЕ функции. Здесь нет ни диска, ни сети, ни браузера: именно
/// поэтому «ссылка про этот ли порт» и «что уходит в журнал» перебираются проверками,
/// а не проверяются глазами на живом движке.
///
/// Разбор перенесён из v1 (<c>DshTray</c>): там ссылка доставалась из журнала той же
/// регуляркой, и форма строки движка с тех пор не менялась.
/// </summary>
public static class EntryLinkDecisions
{
    /// <summary>
    /// Строка движка со ссылкой. Регулярка взята у v1 ДОСЛОВНО: она уже пережила смену версий
    /// движка (в <c>0.1.7-rc.2</c> переделали CLI), и менять работающее здесь нечего.
    /// </summary>
    private static readonly Regex LinkPattern = new(
        @"dsh web:\s+(https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Порт, названный в ссылке ЯВНО: <c>схема://хост:порт</c> до разделителя пути, запроса
    /// или конца строки. Именно «явно» — потому что адрес без порта про наш сервер не говорит
    /// ничего, а <see cref="Uri"/> разницы между «:80» и «без порта» не сохраняет.
    /// </summary>
    private static readonly Regex PortInUrl = new(
        @"^[a-z][a-z0-9+.-]*://[^/?#]*?:(\d{1,5})(?=[/?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Ссылка из строки вывода движка или пусто, если её там нет. Возвращается СЫРАЯ ссылка —
    /// с токеном: маскирование делает тот, кто пишет в журнал, и это разные решения.
    /// </summary>
    public static string Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var match = LinkPattern.Match(line);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>
    /// Порт, НАЗВАННЫЙ в ссылке, или <c>0</c> — «порт не назван» (мусор, адрес без порта).
    ///
    /// Отдельным ответом, а не «80»: ссылка без порта не про НАШ сервер, и притвориться,
    /// что она про порт 80, значило бы отдать человеку чужой адрес.
    ///
    /// Порт читается из ТЕКСТА ссылки, а не у <see cref="Uri"/>: тот по правилам схемы стирает
    /// порт, равный умолчанию, и «http://127.0.0.1:80/?token=…» вернул бы «порт не назван».
    /// У нас же важно ровно обратное — назван ли порт ЯВНО.
    /// </summary>
    public static int PortOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;

        var text = url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out _)) return 0;

        var match = PortInUrl.Match(text);
        if (!match.Success) return 0;

        return int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and < 65536 ? port : 0;
    }

    /// <summary>
    /// Ссылка про ЭТОТ порт. Сверка обязательна: вывод движка мог остаться от прежнего запуска
    /// (или прийти от чужого процесса), и открыть человеку вчерашний адрес — хуже, чем не открыть.
    /// </summary>
    public static bool MatchesPort(string? url, int port) =>
        port is > 0 and < 65536 && PortOf(url) == port;

    /// <summary>
    /// Ссылка в том виде, в каком её можно записать в журнал или показать в отчёте: токен вырезан.
    /// Маскирование берётся у <see cref="EngineLog"/> — второго правила о токене в панели нет.
    /// </summary>
    public static string Redacted(string? url) => EngineLog.Redact(url);

    /// <summary>
    /// Годится ли ЧУЖАЯ ссылка (из файла панели 1.x, <c>web-url.txt</c>) для НАШЕГО сервера.
    ///
    /// Три условия, и все обязательны:
    ///
    /// * **порт в ссылке — тот самый**, на котором стоит сервер. Чужой файл мог остаться
    ///   от прежнего запуска, и открыть человеку вчерашний адрес хуже, чем не открыть;
    /// * **хост — своя машина** (<see cref="IsLoopbackHost"/>). Файл чужой: в нём может стоять
    ///   ЛЮБОЙ адрес, а в ссылке — токен входа. Открыть её значит отдать токен тому хосту,
    ///   который назван в файле (красная линия 7), поэтому годной признаётся только ссылка
    ///   на loopback;
    /// * **порт принимает эту ссылку** (<paramref name="probe"/>, измеряет вызывающий: чистые
    ///   решения сети не знают) — см. <see cref="Accepts"/>.
    ///
    /// Решение отделено от измерения намеренно: так оно перебирается проверками, а не «мы
    /// посмотрели на живой панели 1.x и поверили».
    ///
    /// ⚠️ Проверка хоста идёт ДО сети: вызывающий обязан спросить <see cref="IsLoopbackHost"/>
    /// и <see cref="MatchesPort"/> прежде, чем делать запрос, — иначе токен уже ушёл бы на чужой
    /// хост, и «не годна» было бы сказано после утечки.
    /// </summary>
    public static bool UsableFallback(string? url, int port, LinkProbe probe) =>
        MatchesPort(url, port) && IsLoopbackHost(url) && Accepts(probe);

    /// <summary>
    /// Ссылка ведёт на СВОЮ машину — единственное, куда панель имеет право отправить токен входа.
    ///
    /// Разрешены ровно три имени: <c>127.0.0.1</c>, <c>localhost</c> и <c>::1</c> (регистр не
    /// важен). Список короткий намеренно: это имена самой машины, и других «своих» имён у панели
    /// нет. Имя вида <c>192.168.1.5</c> или <c>example.com</c> — уже чужой адрес, даже если порт
    /// на нём отвечает: файл панели 1.x чужой, и подставить в него можно что угодно.
    ///
    /// Хост берётся у <see cref="Uri"/> (<c>Host</c> без порта, в нижнем регистре), а скобки
    /// IPv6 снимаются: <c>[::1]</c> и <c>::1</c> — одна и та же машина, и разбирать их по-разному
    /// значило бы однажды не признать свою.
    ///
    /// ⚠️ Схема проверяется здесь же: ссылка входа всегда <c>http</c> или <c>https</c>, а эту
    /// функцию спрашивают ПЕРЕД тем, как отдать адрес оболочке. Пускать дальше <c>ftp:</c> или
    /// что-нибудь ещё «потому что хост свой» — значит оставить открытой дверь, которую закрывают
    /// в другом месте, и однажды закрыть её забудут.
    /// </summary>
    public static bool IsLoopbackHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        if (uri.Scheme is not ("http" or "https")) return false;

        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        return host is "127.0.0.1" or "localhost" or "::1";
    }

    /// <summary>
    /// Принимает ли сервер эту ссылку. Правило выведено ЗАМЕРОМ на живом сервере владельца
    /// 27.09.2026, а не из общих соображений, и это важно: наивное «ответ 200 или 302» сделало бы
    /// кнопку «Открыть панель» мёртвой ровно там, где она нужнее всего.
    ///
    /// Что измерено на работающем DSH (порт 3080):
    ///
    /// * <c>GET &lt;ссылка с токеном&gt;</c> → **303 SeeOther** и <c>Set-Cookie</c> в ответе:
    ///   токен принят, сервер выдал cookie входа;
    /// * <c>GET &lt;тот же адрес БЕЗ токена&gt;</c> → **401** с отпечатком
    ///   «dsh web authentication required».
    ///
    /// Отсюда правило: годна ссылка, на которую порт ответил **успешно или перенаправлением
    /// (2xx/3xx)** И **выдал cookie**. Одного кода мало: 200 без cookie означает, что отвечает
    /// не вход в панель, а что-то другое на том же порту. 401 и 403 — «токена нет или он чужой»,
    /// 404 — «адреса нет»; ни то, ни другое, ни третье открывать человеку нечем.
    ///
    /// ⚠️ Редиректы при измерении НЕ разворачиваются: иначе проверка уехала бы по адресу из
    /// заголовка <c>Location</c>, и «годность» зависела бы от чужого ответа.
    /// </summary>
    public static bool Accepts(LinkProbe probe) =>
        !probe.Failed
        && probe.SetCookie
        && probe.StatusCode is >= 200 and < 400;

    /// <summary>
    /// Строка файла ссылки: она же и есть ссылка, и ничего больше. Разбор терпим к переводам
    /// строк и пробелам — файл могут открыть и сохранить руками.
    /// </summary>
    public static EntryLink ReadFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EntryLink.None;

        var url = text.Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new EntryLink(url, PortOf(url))
            : EntryLink.None;
    }
}
