using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧТО ОТВЕТИЛ ДВИЖОК: код и ТЕЛО КАК ЕСТЬ.
///
/// Именно так, а не «вид ответа и обрезанная подробность», как у пробы
/// (<see cref="EngineRpcAnswer"/>): телефону нужен ВЕСЬ ответ движка — в `session/list` лежит
/// список разговоров человека, а не признак «метод признан». Обрезка тут была бы потерей данных.
///
/// <see cref="Status"/> = <c>0</c> — ответа не было вовсе (движок молчит, сети нет). Это НЕ то же
/// самое, что отказ движка: телефону говорится разное, и различает их эта граница.
/// </summary>
public sealed record PhoneEngineAnswer(int Status, string Body)
{
    /// <summary>Ответа не дождались. <paramref name="detail"/> — вид беды, без значения и без пути.</summary>
    public static PhoneEngineAnswer Failed(string detail) => new(0, detail);

    /// <summary>Ответ движка принят.</summary>
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Движок не принял НАШ вход (cookie движка) — телефону это показывать нельзя.</summary>
    public bool RefusedOurLogin => Status is 401 or 403;
}

/// <summary>
/// ДВЕРИ КАЛИТКИ — всё, что она делает с миром, и всё это ДЕЛЕГАТАМИ.
///
/// **Зачем дверями.** Калитка выставляет наружу дверь к движку, и проверять её на живом движке
/// значило бы: (а) поднимать движок в каждой проверке, (б) не уметь проверить отказ движка
/// (`gateway/…`), (в) зависеть от чужой версии. С дверями проверка подставляет свой движок,
/// свой файл пропуска, свои часы и свою подписку на события — и перебирает ровно те случаи, ради
/// которых калитка написана. Тот же приём у копий, обновления и сервера.
///
/// <see cref="Address"/> — АДРЕС ПРИВЯЗКИ, а не «любой»: наружу калитка не выставляется
/// (<c>0.0.0.0</c> не берётся никогда), и адрес выбирает человек в разделе «Телефон» (F1.d).
/// Нет адреса — калитка говорит словами и не открывается; угадывать за человека панель
/// не имеет права (<c>docs\PHONE-F0.md</c> §7.3).
///
/// ⚠️ **Адрес — ДВЕРЬ (<see cref="Func{TResult}"/>), а не готовая строка** (уточнение владельца
/// 11.10.2026 после живого прогона: адрес до калитки НЕ ДОЕЗЖАЛ — калитка получала адрес,
/// прочитанный при сборке дверей, то есть значение на момент старта панели, а не тот, который
/// человек выбрал и видит в разделе). Спрашивается он в <see cref="PhoneGate.Start"/>, ровно
/// тогда, когда калитка встаёт.
///
/// <see cref="Bound"/> — ОБРАТНАЯ ДВЕРЬ: калитка СООБЩАЕТ адрес, на котором ВСТАЛА, и панель
/// записывает его следом (<c>PhoneLastAddress</c>). Без неё «на каком адресе стоит калитка»
/// знала бы только она сама, а раздел показывал бы догадку.
/// </summary>
public sealed record PhoneGateDoors(
    Func<string> Address,
    int Port,
    Func<string, string, PhoneEngineAnswer> Engine,
    PhonePassFiles Pass,
    PhoneEventDoors Events,
    Func<DateTimeOffset> Clock,
    Action<string> Log,
    Action<string> Bound,
    Func<IServerControl?>? Server = null);

/// <summary>
/// ЧЕМ КОНЧИЛСЯ ЗАПУСК КАЛИТКИ. <see cref="ReasonKey"/> — ИМЯ строки-причины (пусто, когда
/// открылась), и оно нужно не для красоты: по нему проверка называет, ЧЕМ именно отказано,
/// а не «не пустило». <see cref="Text"/> — уже собранные слова, с подставленным портом.
/// </summary>
public sealed record PhoneGateStart(bool Opened, string ReasonKey, string Text)
{
    public static PhoneGateStart Yes(string text) => new(true, string.Empty, text);

    public static PhoneGateStart No(string reasonKey, string text) => new(false, reasonKey, text);
}

/// <summary>
/// ЯЗЫК СТРАНИЦЫ ВХОДА. Страница отдаётся телефону, а телефон — не панель: у него свой язык,
/// и спросить его панель обязана у него самого (`Accept-Language`), а не показывать телефону
/// язык, выбранный на компьютере.
///
/// ⚠️ Языков ТРИ, и все три — те же, на которых говорит панель (<see cref="Loc.Known"/>):
/// отдельного «языка телефона» в панели не заводится, иначе он разошёлся бы с общим.
/// </summary>
public static class PhoneGateLanguages
{
    /// <summary>
    /// ЧТО ПОКАЗАТЬ. Берётся первый язык из <c>Accept-Language</c>, который панель знает;
    /// не подошёл ни один — язык панели (<paramref name="fallback"/>).
    ///
    /// Веса (<c>q=</c>) не разбираются, и это названо честно: у браузера порядок в заголовке
    /// и есть порядок предпочтения, а вес на практике лишь уточняет его. Разбор весов завёл бы
    /// здесь вторую реализацию разбора HTTP-заголовка — ровно то, чего в панели не держат.
    /// </summary>
    public static string For(string? acceptLanguage, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            foreach (var item in acceptLanguage!.Split(','))
            {
                var tag = item.Split(';')[0].Trim();
                if (tag.Length == 0) continue;

                // «ru-RU» и «zh-Hans-CN» — язык это первая часть до разделителя.
                var primary = tag.Split('-')[0];

                foreach (var known in Loc.Known)
                {
                    if (string.Equals(primary, known, StringComparison.OrdinalIgnoreCase)) return known;
                }
            }
        }

        var chosen = LanguageDecisions.Normalize(fallback);

        return chosen == LanguageDecisions.Auto ? Loc.Language : chosen;
    }
}

/// <summary>
/// СТРАНИЦА ВХОДА ТЕЛЕФОНА — объяснение, поле для шести цифр и кнопка. Ни ссылок, ни ключей,
/// ни путей, ни картинок: страницу смотрит телефон, и всё, чего на ней нет, не может утечь.
///
/// ⚠️ **БЕЗ СКРИПТА ВООБЩЕ** (решение владельца 11.10.2026, шаг «вид телефона»): прежде шесть цифр
/// уходили в калитку скриптом (`fetch`), и без скрипта вход не работал — а вход обязан работать
/// всегда. Теперь это **обычная форма**: `POST /pair` с полем `code`, и **слова об ошибке приходят
/// СТРАНИЦЕЙ** (её собирает эта же дверь с <paramref name="reason"/>), а не строкой JSON.
///
/// Вид — ОДНОЙ таблицей на обе страницы (<see cref="PhonePageStyle"/>): страница открывается
/// с телефона, и «как будто на HTML» выглядеть она не должна (решение владельца 11.10.2026).
///
/// Язык приходит доводом и берётся у САМОГО телефона (<see cref="PhoneGateLanguages"/>).
/// Строки — из тех же трёх словарей, что и вся панель, поэтому «третьего языка» у страницы нет.
/// </summary>
public static class PhoneLoginPage
{
    /// <summary>
    /// Собрать страницу на названном языке. <paramref name="reason"/> — ПОЧЕМУ не пустило
    /// (пусто — страница показывает только форму): человек обязан прочитать причину словами,
    /// а не догадываться, почему «ничего не произошло».
    /// </summary>
    public static string Html(string language, string reason = "", string code = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        string T(string key) => Escape(Loc.TIn(language, key));

        var lines = new List<string>
        {
            "<!DOCTYPE html>",
            "<html lang=\"" + Escape(language) + "\">",
            "<head>",
            "<meta charset=\"utf-8\">",
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            "<title>" + T(nameof(PanelStrings.PhonePageTitle)) + "</title>",
            "<style>" + PhonePageStyle.Css + "</style>",
            "</head>",
            "<body>",
            "<main class=\"card\">",
            "<h1 class=\"screen-title\">" + T(nameof(PanelStrings.PhonePageTitle)) + "</h1>",
            "<p class=\"note\">" + T(nameof(PanelStrings.PhonePageIntro)) + "</p>",
            // СЛОВА-ОГРАНИЧИТЕЛЬ — на странице входа, то есть там, куда человек и попадает по QR
            // (решение владельца 17.10.2026). Та же строка, что в разделе «Телефон» настроек.
            "<p class=\"hint\">" + T(nameof(PanelStrings.PhoneLimits)) + "</p>",

            // ЗАМОК СТРАНИЦЫ ВХОДА НАЗВАН ЗДЕСЬ ЖЕ: ни одного внешнего ресурса и ни одного чужого
            // адреса; скрипта на этой странице НЕТ ВООБЩЕ (он есть только у страницы телефона —
            // там, где живёт живое обновление). Всё, что здесь есть, приходит одним ответом.
            "<form method=\"post\" action=\"/pair\">",
            "<label class=\"field-label\" for=\"code\">" + T(nameof(PanelStrings.PhonePageCodeLabel)) + "</label>",
        };

        // БЕДА — СЛОВАМИ И РЯДОМ С ПОЛЕМ: GOV.UK validation — ошибка показывается там, где человек
        // её исправляет, а не отдельным экраном (`30-mobile-web.md` §4). Слова берутся из словаря
        // (<paramref name="reason"/> — имя причины), поэтому «код не подошёл» — со строчной буквы.
        //
        // ⚠️ И ОШИБКА СВЯЗАНА С ПОЛЕМ ПРОГРАММНО, А НЕ ТОЛЬКО МЕСТОМ (правка 12.10.2026):
        // `id="code-error"` у самой ошибки и `aria-describedby="code-error"` у поля. Норма — WAI
        // «form fields can be associated with the corresponding error message using
        // aria-describedby» (https://www.w3.org/WAI/tutorials/forms/notifications/); без этого
        // читалка прочитает ошибку ДО поля, но не как «ошибка этого поля» (`80-accessibility.md`
        // Ш-28). Ссылку на несуществующий `id` не ставим никогда — читается как пустота.
        var codeError = !string.IsNullOrWhiteSpace(reason);

        if (codeError)
        {
            lines.Add(
                "<p class=\"error\" id=\"code-error\">" + T(nameof(PanelStrings.PhonePageError)) + " "
                + Escape(reason) + "</p>");
        }

        // ⚠️ ВВЕДЁННОЕ НЕ СТИРАЕТСЯ (норма GOV.UK: показать страницу «with the form fields as the
        // user filled them in»; `30-mobile-web.md` §4): код возвращается в поле, а рамка помечена
        // `code-bad`. Заставлять человека набирать шесть цифр заново — дефект, а не строгость.
        // ⚠️ `autocomplete="one-time-code"` — НЕ `off` (правка 12.10.2026, норма подтверждена
        // web.dev: «Use autocomplete="one-time-code" on the input field», одноразовые коды и SMS:
        // https://web.dev/articles/sms-otp-form). `off` здесь прямо МЕШАЛ: он отключал подстановку
        // кода из сообщения, а поле собирает именно одноразовый код сопряжения. Это не «данные о
        // человеке» (SC 1.3.5 к нам не применяется), поэтому токен выбирается по назначению поля.
        lines.Add(
            "<input class=\"code" + (code.Length > 0 ? " code-bad" : string.Empty)
            + "\" id=\"code\" name=\"code\" type=\"tel\" inputmode=\"numeric\""
            + " autocomplete=\"one-time-code\""
            + (codeError ? " aria-describedby=\"code-error\"" : string.Empty)
            + " maxlength=\"6\" value=\"" + Escape(code) + "\">");

        lines.Add("<button class=\"action wide\" type=\"submit\">" + T(nameof(PanelStrings.PhonePageSubmit)) + "</button>");
        lines.Add("</form>");
        lines.Add("</main>");
        lines.Add("</body>");
        lines.Add("</html>");

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Экранирование на всякий случай: строки приходят из СВОИХ словарей, но переводчик — человек,
    /// и однажды в переводе окажется `<`. Тогда страница сломается у него, а не у всех.
    /// </summary>
    private static string Escape(string text) => (text ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
/// КАЛИТКА ТЕЛЕФОНА — СЛУШАТЕЛЬ, КОТОРЫЙ ПУСКАЕТ ТОЛЬКО СВОЁ УСТРОЙСТВО И ТОЛЬКО НА ПЯТЬ ДЕЙСТВИЙ.
///
/// **Что здесь есть и чего здесь нет.** Есть: свой свободный порт на выбранном адресе СВОЕЙ сети,
/// вход по коду из шести цифр, свой пропуск в защищённой от скриптов cookie, список устройств,
/// ограничение попыток, белый список имён и подписка на поток событий движка (вопрос агента).
/// Нет: страницы телефона и красоты (F2), раздела «Телефон» в настройках (F1.d), ключа
/// <c>--phone-selftest</c> (F1.e) и туннеля наружу (отложен владельцем).
///
/// **Слушателя поднимает ТОЛЬКО право** (<see cref="RunRights.MayOpenPhoneGate"/>): обычный запуск
/// панели человеком, который сам включил телефон. Прогон проверки и изолированный прогон калитку
/// не открывают — и это не «мы постараемся», а отказ в первой же строке <see cref="Start"/>,
/// названный словами.
///
/// **Почему свой разбор HTTP, а не `HttpListener`.** `HttpListener` на не-loopback адресе требует
/// резервирования URL (`netsh http add urlacl`), то есть **прав администратора**, — а панель
/// ставится без них (<c>docs\PHONE-F0.md</c> §2). Тянуть ASP.NET/Kestrel в раздачу из одного exe
/// тоже незачем. Поэтому слушатель — <see cref="TcpListener"/> на выбранном адресе и порту плюс
/// минимальный HTTP/1.1: строка запроса, заголовки до пустой строки, `Content-Length` с пределом
/// тела, ответ с `Connection: close`. Ни `chunked`, ни `keep-alive` здесь нет и не будет: нашим
/// маршрутам они не нужны, а лишний разбор — лишняя дверь.
///
/// **Что калитка НЕ делает никогда** (и это проверяется): не занимает порты <c>3080</c> и
/// <c>3081</c>, не съезжает молча на другой порт, не проксирует движок целиком (ни статики,
/// ни <c>/plugins/*</c>), не отдаёт телефону cookie движка, его `clientId` и домашний каталог
/// машины, не протаскивает чужие заголовки.
/// </summary>
public sealed class PhoneGate : IDisposable
{
    /// <summary>Имя cookie телефона. Своё: cookie движка к телефону не попадает.</summary>
    public const string CookieName = "dsh-phone";

    /// <summary>
    /// ИМЯ УСТРОЙСТВА, ПОД КОТОРЫМ КАЛИТКА ЗАВОДИТ ЗАПИСЬ ПРИ СОПРЯЖЕНИИ.
    ///
    /// ⚠️ **Латиницей, хотя человеку имя показывается в списке подключённых.** Имя уезжает
    /// в cookie (<c>dsh-phone=&lt;имя&gt;.&lt;подпись&gt;</c>), а заголовки HTTP — ASCII:
    /// кириллица в имени либо отвергается браузером, либо приезжает обратно искажённой, и подпись
    /// перестаёт совпадать — то есть сопряжение ломается ЦЕЛИКОМ. Это не догадка: на кириллическом
    /// имени упало 16 проверок калитки разом.
    ///
    /// ⚠️ И это имя, а не «адрес телефона»: адрес в своей сети выдаётся по DHCP и меняется,
    /// <c>User-Agent</c> подделывается одной строкой, а имя файла пропуска — единственное, что
    /// телефону нечем подделать. Одно имя на устройство выбрано потому, что у движка телефоны
    /// НЕРАЗЛИЧИМЫ вовсе (замер <c>docs\PHONE-F0.md</c> §5.4): двух вкладок одного браузера панель
    /// и не должна различать, а «какой именно это телефон» она не знает и знать не может.
    /// Имена нумеруются по порядку — см. <see cref="DeviceIdFor"/>.
    /// </summary>
    public const string DeviceNamePrefix = "phone";

    /// <summary>
    /// ИМЯ УСТРОЙСТВА ПО ЧИСЛУ УЖЕ СОПРЯЖЁННЫХ: <c>phone-1</c>, <c>phone-2</c> и так далее.
    /// Номер — это число сопряжённых устройств плюс один, поэтому повторное сопряжение с того же
    /// телефона (второй браузер, сброшенные cookie) заведёт ОТДЕЛЬНУЮ запись, и отвязать её можно
    /// поимённо. Так и задумано: различить «тот же телефон» от «другого» панели нечем.
    /// </summary>
    public static string DeviceIdFor(int existing) =>
        DeviceNamePrefix + "-" + (existing + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// СКОЛЬКО ЖИВЁТ ПРОПУСК ТЕЛЕФОНА — **30 суток**, столько же, сколько чеканит движок своей
    /// cookie входа (замер <c>docs\PHONE-LIVE.md</c> §1: `Max-Age=2592000`). Мера не наша выдумка,
    /// а взята у того, кому телефон и так доверяет: телефон в своей сети не должен сопрягаться
    /// заново каждую неделю.
    /// </summary>
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// ПРЕДЕЛ ЗАПРОСА — **64 КиБ** (тело и заголовки вместе). Не «на всякий случай»: самый большой
    /// наш вызов — постановка задачи с текстом, и она в этот предел укладывается с запасом; всё,
    /// что больше, это уже не «телефон попросил», а «кто-то шлёт панели мусор». Предел объявлен
    /// константой и меняется в ОДНОМ месте.
    /// </summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Предел заголовков по длине. Отдельно от предела тела: длинный заголовок съел бы
    /// память до того, как станет ясна длина тела.</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>Сколько заголовков принимаем. Больше — отказ словами, а не «прочитаем сколько влезет».</summary>
    public const int MaxHeaders = 64;

    /// <summary>Сколько ждём запрос целиком. Телефон в своей сети отвечает мгновенно; медленное —
    /// это не он, и держать нитку незачем.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Как часто калитка проверяет, жива ли подписка на события движка.</summary>
    public static readonly TimeSpan SubscriptionCheck = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Один висящий вопрос агента: его `eventId`, вопросы — тем же JSON, что пришёл, — и `agentId`
    /// того, кто спрашивает.
    ///
    /// ⚠️ **`agentId` здесь не «на всякий случай», а ради ОТМЕНЫ** (этап F2): вопрос, который никто
    /// не взял, висит вечно и освобождается только отменой хода (<c>docs\PHONE-LIVE.md</c> §4).
    /// Отменяет калитка тем же вызовом, что и панель, — <c>session/cancel</c> с `sessionId`. Замер
    /// ЧТЕНИЕМ движка 11.10.2026 (`dsh-api-session-controller`): отмена ищет агента в реестре
    /// ПО `sessionId` (`agents.get(request.sessionId)`), а водопад отдаёт `agentId` — для обычной
    /// сессии это одно и то же значение. ⚠️ **Живьём это не проверено** (живой прогон с телефоном —
    /// F4 и слово владельца), поэтому отказ движка (`session/not-found`) говорится телефону СЛОВАМИ,
    /// а не выдаётся за сделанное.
    /// </summary>
    private sealed record PendingQuestion(string EventId, string Questions, string AgentId);

    /// <summary>
    /// ЧЕМ КОНЧИЛСЯ ПОСЛЕДНИЙ ВОПРОС — и это ОТДЕЛЬНЫЙ ответ, а не «вопроса нет».
    ///
    /// ⚠️ Разница нужна странице телефона (F2): «вопрос снят» и «вы ответили» — разные слова,
    /// и человек, увидевший вместо кнопок одно и то же «вопроса нет», не понял бы, отправился ли
    /// его ответ. Проигравший (вопрос взял кто-то другой или агент его снял) обязан прочитать это
    /// словами — решение владельца 11.10.2026.
    /// </summary>
    private enum QuestionOutcome
    {
        /// <summary>Ничего не кончалось: вопроса не было вовсе или он ещё висит.</summary>
        None,

        /// <summary>Ответ ушёл в движок (и движок его принял).</summary>
        Answered,

        /// <summary>Вопрос больше не ждёт: его взял другой или агент его снял.</summary>
        Taken,

        /// <summary>Ход агента отменён НАМИ — с этой страницы.</summary>
        Cancelled,
    }

    /// <summary>Выпущенный код сопряжения и срок его жизни.</summary>
    private sealed record Pairing(string Code, DateTimeOffset ExpiresAt);

    private readonly PhoneGateDoors _doors;
    private readonly Func<bool> _enabled;
    private readonly bool _humanLaunch;
    private readonly RunContext _context;

    /// <summary>
    /// ПРАВО, ОТДАННОЕ САМОТЕСТОМ (<c>--phone-selftest</c>), — и только им.
    ///
    /// ⚠️ **Зачем это здесь, если право спрашивается первой строкой.** Самотест калитки живёт
    /// ТОЛЬКО в изоляции (он пишет файл пропуска и поднимает настоящий движок), а изолированному
    /// прогону права открыть калитку не даёт никто и никогда — <see cref="RunRights.MayOpenPhoneGate"/>
    /// отвечает «нет» именно затем, чтобы прогон проверки не выставил порт в сеть владельца.
    /// Значит без этой двери самотест не смог бы проверить НИЧЕГО, кроме самого отказа, и «калитка
    /// открывается по праву» осталось бы непроверенным.
    ///
    /// **Чем это не дыра.** Право даёт ОДИН вызывающий — самотест, и он:
    ///   * работает под своим корнем (<c>--run-root</c>), а без корня отказывается сам;
    ///   * слушает ТОЛЬКО петлю (<c>127.0.0.1</c>): наружу такой порт не виден;
    ///   * берёт порт СВОБОДНЫМ у системы, а <c>3080</c>/<c>3081</c>/<c>43127</c> запрещены
    ///     решением (<see cref="PortForbidden"/>), и запрет спрашивается ДО этого права;
    ///   * гасит слушателя за собой (<see cref="Stop"/>), а прогон убирает свой корень.
    /// И, главное, **сам отказ по праву проверяется первым делом** — до того, как право будет
    /// выдано (<see cref="PhoneSelfTest"/>): «изолированному прогону калитка не открывается».
    ///
    /// Имя намеренно звучит как то, чем является: <c>internal</c>, один писатель, и рядом —
    /// причина. Право, розданное «поудобнее», здесь однажды открыло бы дверь наружу.
    ///
    /// ⚠️ **Чего это право НЕ отменяет — выключенного телефона.** «Человек запустил панель» и
    /// «человек разрешил телефон» — два РАЗНЫХ условия (<paramref name="enabled"/> во втором),
    /// и самотесту нужно проверить их по отдельности: без этого проверка «при выключенном телефоне
    /// калитка отказывает» зеленела бы на калитке с выданным правом, то есть не проверяла бы ничего.
    /// </summary>
    internal bool AllowForSelfTest { get; set; }

    /// <summary>
    /// ЧЕМ КАЛИТКА СПРАШИВАЕТ РУБИЛЬНИК — ДВЕРЬЮ, А НЕ ЗНАЧЕНИЕМ (уточнение владельца 11.10.2026).
    ///
    /// ⚠️ Прежде <c>enabled</c> приходил готовым булевым и читался ОДИН раз при сборке калитки.
    /// Из-за этого щелчок рубильника не мог поднять дверь у той же калитки: значение, прочитанное
    /// на старте панели, оставалось «выключено» навсегда и требовало перезапуска. Теперь значение
    /// спрашивается в <see cref="Start"/> — то есть в тот момент, когда человек щёлкнул.
    /// </summary>
    public PhoneGate(PhoneGateDoors doors, Func<bool> enabled, bool humanLaunch, RunContext context)
    {
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _humanLaunch = humanLaunch;
    }

    private readonly object _gate = new();

    /// <summary>
    /// ЗАМОК ПРОПУСКА — вокруг «прочитать, изменить, записать».
    ///
    /// ⚠️ **Не тот же, что <see cref="_gate"/>.** `_gate` охраняет слушателя и пару в памяти, и брать
    /// его на время файловых операций значило бы останавливать калитку на каждом сопряжении. Здесь
    /// нужен ровно один порядок: два изменения пропуска **одновременно прочитают одно состояние**
    /// и второе затрёт первое (устройство «отвязалось», а оно снова в файле; два сопряжения выбрали
    /// одно имя устройства). Писателей три — сопряжение, <see cref="ForgetDevice"/> и
    /// <see cref="ForgetAllDevices"/>, — и все три ходят через этот замок.
    /// </summary>
    private readonly object _pass = new();

    /// <summary>Неудачные попытки входа — в памяти и только на время работы калитки: код живёт
    /// минуты, и хранить попытки на диске незачем (<c>docs\PHONE-F0.md</c> §9).</summary>
    private readonly List<PhoneDecisions.Attempt> _attempts = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accept;
    private Timer? _watch;

    private Pairing? _pairing;
    private PendingQuestion? _question;

    /// <summary>Последний `eventId` — для журнала и для ответа «этот вопрос уже не ждёт».</summary>
    private string _lastEventId = string.Empty;

    /// <summary>Чем кончился последний вопрос (см. <see cref="QuestionOutcome"/>).</summary>
    private QuestionOutcome _outcome = QuestionOutcome.None;

    /// <summary>
    /// ОТВЕТ УЖЕ ОТПРАВЛЯЕТСЯ — и второй раз не отправляется.
    ///
    /// ⚠️ Флаг, а не «мы быстро ответим»: запросы обслуживаются КАЖДЫЙ В СВОЕЙ ЗАДАЧЕ
    /// (<c>Task.Run</c> в цикле приёма), поэтому два нажатия на странице телефона приходят двумя
    /// нитками. Без флага телефон отправил бы два ответа на один вопрос — а «ответ уходит один раз»
    /// это требование владельца, а не пожелание.
    /// </summary>
    private bool _answering;

    private string _clientId = string.Empty;
    private IPhoneEventLink? _link;

    /// <summary>
    /// НОМЕР ЭКРАНА — вариант A: на экране ОДИН вопрос, а вопросы идут по очереди (решение
    /// владельца 11.10.2026). Калитка держит его у себя (её никто об этом не спрашивает), и
    /// **сдвигается он ТОЛЬКО после удачной отправки** — после отказа человек остаётся на том же
    /// экране со своим выбором и своим текстом.
    /// </summary>
    private int _screen;

    /// <summary>
    /// ЧТО УЖЕ ОТВЕЧЕНО НА ЭКРАНАХ, по `id` вопроса. Здесь ровно то, что уйдёт движку одной
    /// строкой `selected`, и больше ничего: ни номеров вариантов, ни подписей по отдельности.
    ///
    /// ⚠️ **Копится НЕ СТРАНИЦЕЙ, А КАЛИТКОЙ.** Страница показывает один вопрос и не знает, что
    /// было на прошлых: будь накопление у неё, перезагрузка страницы (кнопка «Проверить», возврат
    /// из беды) стёрла бы уже отвеченное, и ответ ушёл бы неполным.
    /// </summary>
    private readonly Dictionary<string, string> _screenAnswers = new(StringComparer.Ordinal);

    /// <summary>
    /// К КАКОМУ ВОПРОСУ ОТНОСИТСЯ НАКОПЛЕННОЕ. Вопросы приходят СОБЫТИЯМИ, и один `eventId`
    /// сменяется другим: без этой сверки ответы прежнего хода достались бы новому.
    /// </summary>
    private string _screenEventId = string.Empty;

    /// <summary>Сколько раз ПОДРЯД подписка не открылась — по этому числу растёт отступ между
    /// попытками. Нужен не для красоты: движка может не быть часами, и «одна строка в журнал
    /// каждые пять секунд» — это не слова, а шум.</summary>
    private int _failures;

    /// <summary>Раньше этого времени новую попытку подписки не делаем.</summary>
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;

    /// <summary>Слушает ли калитка прямо сейчас. Не «право ли выдано», а «порт занят ею» —
    /// этим признаком F1.d покажет человеку состояние, и он же нужен проверкам.</summary>
    public bool Listening
    {
        get
        {
            lock (_gate) return _listener is not null;
        }
    }

    /// <summary>Сколько устройств сопряжено — по ФАЙЛУ пропуска. Само число не секрет: им панель
    /// отвечает на вопрос «подключён ли телефон» (F1.d).</summary>
    public int DeviceCount => PhonePass.Load(_doors.Pass).DeviceCount;

    /// <summary>
    /// ЕСТЬ ЛИ ЧЕМ ДОСТАТЬ ВОПРОС АГЕНТА — то есть есть ли вход в сервер DSH ПРЯМО СЕЙЧАС.
    ///
    /// **От какого случая (находка владельца 13.10.2026).** Раздел «Телефон» говорил «телефон
    /// подключён · адрес: …», когда сервер на порту поднят НЕ панелью: телефон сопряжён, дверь
    /// открыта, а ссылки входа у панели нет, и вопрос агента на телефон не придёт НИКОГДА.
    /// Строка обещала работающий телефон, а работала только дверь к телефону.
    ///
    /// ⚠️ Признак берётся у ДВЕРИ СОБЫТИЙ (<see cref="PhoneGateDoors.Events"/>,
    /// <see cref="PhoneEventDoors.Present"/>), а не выдумывается: дверь событий отвечает на вопрос
    /// «есть ли вход СЕЙЧАС» по ссылке входа.
    ///
    /// ⚠️ **И СПРАШИВАЕТСЯ ОН КАЖДЫЙ РАЗ, А НЕ ЗАПОМИНАЕТСЯ — это исправление живого дефекта
    /// 14.10.2026.** Прежде дверь событий считала признак один раз при сборке, и после перезапуска
    /// панели (когда свой сервер узнаётся по записи на секунду позже) он навсегда оставался
    /// «входа нет»: раздел говорил «связи с сервером DSH нет» про работающий СВОЙ сервер, а телефон
    /// не получал вопросов вовсе. Теперь признак живой, и вход, появившийся позже, доезжает до
    /// калитки и до раздела БЕЗ перезапуска панели.
    /// </summary>
    public bool EngineLinked => _doors.Events.Present;

    /// <summary>
    /// ПОДКЛЮЧЁН ЛИ ТЕЛЕФОН ПРЯМО СЕЙЧАС. Отвечает «да» ровно тогда, когда есть устройство,
    /// которое ОТМЕЧЕНО КАК ВИДЕННОЕ (<see cref="PhoneDeviceView.LastSeen"/>) — то есть телефон
    /// не просто сопряжён, а хотя бы раз выходил на связь.
    ///
    /// ⚠️ **Признак считается по СВОЕЙ записи, а не спрашивается у движка** — спросить негде:
    /// движок не знает, кто к нему подключён (замер <c>docs\PHONE-F0.md</c> §5.4), а список
    /// устройств живёт у калитки. Ведёт ли телефон себя тихо или им сейчас пользуются, по этой
    /// записи не видно, и строка меню об этом и не говорит: она называет состояние, которое
    /// панель действительно знает.
    /// </summary>
    public bool Connected =>
        PhonePass.Load(_doors.Pass).SafeDevices.Any(view => !string.IsNullOrWhiteSpace(view.LastSeen));

    /// <summary>
    /// СПИСОК СОПРЯЖЁННЫХ УСТРОЙСТВ — в том виде, в каком его видит человек: без адреса
    /// (<see cref="PhonePass.SafeDevices"/>), с «когда подключился» и «когда видели».
    ///
    /// Файл читается на каждый вызов, как и <see cref="DeviceCount"/>: раздел настроек показывает
    /// список в момент отрисовки, и держать его в памяти значит однажды показать устаревшее.
    /// Пропуска нет или он битый — пустой список, и раздел скажет об этом словами.
    /// </summary>
    public IReadOnlyList<PhoneDeviceView> Devices => PhonePass.Load(_doors.Pass).SafeDevices;

    /// <summary>
    /// ОТВЯЗАТЬ ОДНО УСТРОЙСТВО ПО ИМЕНИ. Пропуск при этом НЕ меняется: отвязка закрывает вход
    /// поимённо (<see cref="PhonePass.HasDevice"/>), а остальные устройства продолжают ходить.
    /// Отсюда и «список пуст, а пропуск есть» — состояние, в котором панель никого не пускает,
    /// и оно законно.
    ///
    /// Возвращает, записалось ли. Не записалось (прогон без права) — человеку говорится словами,
    /// а не делается вид, что устройство отвязано: оно ходило бы дальше со своей подписью.
    /// </summary>
    public bool ForgetDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        // ЧТЕНИЕ-ИЗМЕНЕНИЕ-ЗАПИСЬ ПОД ЗАМКОМ: иначе одновременная отвязка двух устройств
        // вернула бы одно из них обратно — прочитали одно состояние, записали по очереди.
        lock (_pass)
        {
            var pass = PhonePass.Load(_doors.Pass);

            // Пропуска нет — отвязывать нечего, и это НЕ ошибка: устройство и так не пустят.
            if (!pass.Any) return true;

            var next = pass.WithoutDevice(deviceId.Trim());

            return next.Save(_doors.Pass);
        }
    }

    /// <summary>
    /// ЗАБЫТЬ ВСЕ УСТРОЙСТВА — вместе с пропуском. Так исполняется рубильник «Разрешить телефон»
    /// в выключенную сторону: старые cookie обязаны перестать пускать, иначе выключение ничего бы
    /// не значило. Подписи при этом обесцениваются по устройству: они считаются от пропуска
    /// (<see cref="PhonePass.DeviceSignature"/>), а его больше нет.
    ///
    /// Возвращает, записалось ли: прогон без права не пишет ничего, и «отвязали всё» было бы
    /// неправдой.
    /// </summary>
    public bool ForgetAllDevices()
    {
        // Пустой пропуск сохраняется как пустой: файл остаётся на месте с пустым токеном,
        // и `PhonePass.Any` честно отвечает «пропуска нет».
        //
        // Под замком — по той же причине, что и отвязка одного: без него одновременное сопряжение
        // успело бы прочитать прежнее состояние и записать его ОБРАТНО уже после выключения —
        // то есть «телефон выключен», а устройство снова пускает.
        lock (_pass) return PhonePass.Empty.Save(_doors.Pass);
    }

    // ---- правила порта ----------------------------------------------------------------------

    /// <summary>
    /// ПОРТ, КОТОРЫЙ КАЛИТКА НЕ ЗАНИМАЕТ НИКОГДА: порт владельца (<c>3080</c>) и запасной порт
    /// сервера панели (<c>3081</c>). Первый — канал сессии агента, второй — то, куда панель
    /// съезжает сама в прогоне проверки. Оба заняты работой панели, и калитка не имеет права
    /// встать на них даже случайно: настройку можно испортить руками, а «слушает не тот порт»
    /// человек заметит не сразу.
    /// </summary>
    public static bool PortForbidden(int port) =>
        port == ServerDecisions.OwnerPort || port == ServerDecisions.RunFallbackPort;

    /// <summary>
    /// ГОДИТСЯ ЛИ ПОРТ. Два условия, и оба названы по отдельности: настоящий номер порта
    /// (<see cref="ServerDecisions.IsAllowedPort"/> — он же запрещает <c>3080</c>) и не запасной
    /// порт панели (<see cref="PortForbidden"/>). Право на порт владельца не спрашивается вовсе:
    /// у калитки его НЕТ ни у какого запуска.
    /// </summary>
    public static bool PortAllowed(int port) =>
        ServerDecisions.IsAllowedPort(port, mayOccupyOwnerPort: false) && !PortForbidden(port);

    // ---- запуск и остановка -----------------------------------------------------------------

    /// <summary>
    /// ОТКРЫТЬ КАЛИТКУ — И НИКОГДА не открывать её без права.
    ///
    /// Порядок проверок — от права к железу, и он же порядок слов: сначала «этому прогону нельзя»,
    /// потом «этот порт нельзя», потом «этого адреса нет», и только потом связывание. Обратный
    /// порядок означал бы, что прогон проверки успевает занять порт до того, как спросит право.
    /// </summary>
    public PhoneGateStart Start()
    {
        // (0) РУБИЛЬНИК. Спрашивается ЗДЕСЬ и каждый раз заново (дверь, а не значение): человек
        //     мог включить телефон только что, у уже собранной калитки. Значение, прочитанное
        //     однажды при сборке, требовало бы перезапуска панели — то есть рубильник не работал бы.
        var enabled = _enabled();

        // (1) ПРАВО. Первым делом и до всего остального: прогон проверки и изолированный прогон
        //     не открывают калитку НИКОГДА, а выключенный телефон — и у обычного запуска человеком.
        //     Право самотеста (AllowForSelfTest) спрашивается ЗДЕСЬ ЖЕ и не отменяет остальных
        //     условий: порт, адрес и запрет 3080/3081 проверяются ниже и без исключений.
        if ((!AllowForSelfTest || !enabled) && !RunRights.MayOpenPhoneGate(_context, _humanLaunch, enabled))
            return PhoneGateStart.No(PanelStrings.PhoneGateNoRightLog, Loc.T(PanelStrings.PhoneGateNoRightLog));

        // (2) ПОРТ. Занятый — отказ словами; молчаливого переезда нет ровно как у сервера панели.
        if (PortForbidden(_doors.Port))
        {
            return PhoneGateStart.No(
                PanelStrings.PhoneGatePortServerLogFormat,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGatePortServerLogFormat, _doors.Port));
        }

        if (!ServerDecisions.IsAllowedPort(_doors.Port, mayOccupyOwnerPort: false))
        {
            return PhoneGateStart.No(
                PanelStrings.PhoneGatePortWrongLogFormat,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGatePortWrongLogFormat, _doors.Port));
        }

        // (3) АДРЕС. Наружу калитка не выставляется: `0.0.0.0` здесь не берётся ни при каких
        //     обстоятельствах, а «адреса нет» — это отказ, а не выбор наугад.
        //     ⚠️ Адрес спрашивается у ДВЕРИ (см. `PhoneGateDoors.Address`), и это тот же адрес,
        //     который человек видит в разделе: прежняя редакция брала значение, прочитанное при
        //     сборке калитки, — и живой прогон показал, что до калитки адрес так и не доехал.
        var wanted = (_doors.Address() ?? string.Empty).Trim();

        if (!IPAddress.TryParse(wanted, out var address))
        {
            return PhoneGateStart.No(PanelStrings.PhoneGateNoAddressLog, Loc.T(PanelStrings.PhoneGateNoAddressLog));
        }

        // (4) СВЯЗЫВАНИЕ. Здесь и только здесь порт действительно занимается.
        var listener = new TcpListener(address, _doors.Port);

        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Stop();

            return PhoneGateStart.No(
                PanelStrings.PhoneGatePortBusyFormat,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGatePortBusyFormat, _doors.Port));
        }

        var stop = new CancellationTokenSource();

        lock (_gate)
        {
            _listener = listener;
            _stop = stop;
        }

        _accept = Task.Run(() => AcceptLoopAsync(listener, stop.Token));

        // ПОДПИСКА НА СОБЫТИЯ — УСЛОВИЕ РАБОТЫ, А НЕ УКРАШЕНИЕ: без подписчика вопрос агента
        // висит вечно (опыт В замера `docs\PHONE-LIVE.md` §4). Поэтому она открывается сразу
        // и переоткрывается, если оборвалась.
        EnsureSubscription();

        _watch = new Timer(_ => Tick(), null, SubscriptionCheck, SubscriptionCheck);

        // ПОДПИСКА СПРАШИВАЕТСЯ ЗАНОВО, КОГДА О СЕРВЕРЕ СТАНЕТ ИЗВЕСТНО (задание владельца
        // 14.10.2026, §1): при запуске калитки состояние сервера ещё не прочитано, и входа нет.
        // Снятие перед подпиской — идиома панели: повторный запуск не должен копить обработчики.
        WatchServer(watch: false);
        WatchServer(watch: true);

        // АДРЕС, НА КОТОРОМ ВСТАЛИ, — обратно в панель: раздел показывает след «по какому адресу
        // телефон найдёт панель», и это НЕ догадка, а слово самой калитки. Зовётся ПОСЛЕ
        // связывания: сообщить адрес, на котором не встали, значило бы соврать.
        _doors.Bound(wanted);

        var text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.PhoneGateOpenedLogFormat, wanted, _doors.Port);

        _doors.Log(text);

        return PhoneGateStart.Yes(text);
    }

    /// <summary>
    /// ЗАКРЫТЬ КАЛИТКУ И ОСВОБОДИТЬ ПОРТ. После остановки порт снова связывается — это проверяется
    /// отдельно, потому что «остановилась, а порт держит» выглядит как «калитка не остановилась».
    /// </summary>
    public void Stop()
    {
        Timer? watch;
        CancellationTokenSource? stop;
        TcpListener? listener;

        lock (_gate)
        {
            watch = _watch;
            stop = _stop;
            listener = _listener;

            _watch = null;
            _stop = null;
            _listener = null;
        }

        if (watch is null && stop is null && listener is null) return;

        watch?.Dispose();

        // И ОТ СООБЩЕНИЯ О СЕРВЕРЕ ОТПИСЫВАЕМСЯ: у остановленной калитки подписка на чужое
        // событие — утечка (калитка живёт дольше одной остановки).
        WatchServer(watch: false);

        try
        {
            stop?.Cancel();
        }
        catch
        {
            // Отмена уже отменённого — не беда.
        }

        listener?.Stop();

        lock (_gate)
        {
            // ПОДПИСКА ГАСНЕТ ВМЕСТЕ С КАЛИТКОЙ: канал к движку не должен переживать дверь,
            // ради которой он открыт.
            _link?.Dispose();
            _link = null;
            _clientId = string.Empty;
            _question = null;
            _outcome = QuestionOutcome.None;
            _answering = false;
        }

        _doors.Log(PanelStrings.PhoneGateStoppedLog);
    }

    public void Dispose() => Stop();

    // ---- код сопряжения ---------------------------------------------------------------------

    /// <summary>
    /// ВЫПУСТИТЬ НОВЫЙ КОД СОПРЯЖЕНИЯ и отдать его тому, кто покажет его человеку.
    ///
    /// Код НЕ хранится на диске и живёт <see cref="PhoneDecisions.CodeLifetime"/> (5 минут):
    /// диктуют его с экрана на телефон, и после этого он не нужен. Зовёт этот метод раздел
    /// «Телефон» (F1.d) — сейчас его зовут проверки, и это честно названо в отчёте.
    /// </summary>
    public string IssueCode()
    {
        var code = PhoneDecisions.Code();

        lock (_gate) _pairing = new Pairing(code, _doors.Clock() + PhoneDecisions.CodeLifetime);

        return code;
    }

    /// <summary>Есть ли СЕЙЧАС живой код. «Показать код или предложить взять новый» решает тот,
    /// у кого есть экран (F1.d); здесь только правда о состоянии.</summary>
    public bool HasLiveCode()
    {
        lock (_gate) return _pairing is not null && PhoneDecisions.CodeAlive(_doors.Clock(), _pairing.ExpiresAt);
    }

    /// <summary>
    /// ЖИВОЙ КОД КАК ЕСТЬ — для того, кто показывает его человеку (F1.d). Пусто — живого кода нет,
    /// и выдумывать его нельзя: цифры, по которым не пустят, хуже их отсутствия.
    ///
    /// ⚠️ **Истёкший код не отдаётся, но и не забывается:** он просто перестаёт быть живым
    /// (<see cref="HasLiveCode"/>), и следующая просьба покажет новый. Отдельной «чистки» здесь
    /// нет намеренно — сравнивать время в одном месте (<see cref="PhoneDecisions.CodeAlive"/>)
    /// надёжнее, чем в двух.
    /// </summary>
    public string CurrentCode()
    {
        lock (_gate)
        {
            if (_pairing is null) return string.Empty;
            if (!PhoneDecisions.CodeAlive(_doors.Clock(), _pairing.ExpiresAt)) return string.Empty;

            return _pairing.Code;
        }
    }

    // ---- подписка на события движка ---------------------------------------------------------

    /// <summary>
    /// ПРО СЕРВЕР УЖЕ СПРОШЕНО — ответ «входа нет» можно давать.
    ///
    /// Двери сервера у калитки может не быть вовсе (подстановки проверок, прогон без сервера) —
    /// тогда считается, что прочитано: подстановка отвечает готовым состоянием, и молчаливо
    /// «неизвестное» сделало бы её слепой.
    /// </summary>
    private bool ServerKnown => _doors.Server?.Invoke()?.Known ?? true;

    /// <summary>
    /// СОСТОЯНИЕ СЕРВЕРА СТАЛО ИЗВЕСТНО (или сменилось) — СПРОСИТЬ ПОДПИСКУ ЗАНОВО.
    ///
    /// **Зачем.** Подписка нужна КАК МОЖНО РАНЬШЕ: движок события НЕ переигрывает, и вопрос
    /// агента, заданный в промежуток без подписки, до телефона не дойдёт ВООБЩЕ. Прежде первая
    /// попытка шла при запуске калитки (когда входа ещё нет) и следующая — на часовом такте,
    /// то есть через пять секунд. Теперь состояние сервера прочитано — и подписка спрашивается
    /// в тот же миг, без ожидания такта (задание владельца 14.10.2026, §1).
    /// </summary>
    private void OnServerStateChanged() => EnsureSubscription();

    /// <summary>
    /// ПОДПИСАТЬСЯ (или отписаться) НА СООБЩЕНИЕ О СОСТОЯНИИ СЕРВЕРА. Зовётся при запуске
    /// и остановке калитки: у остановленной калитки подписка на чужое сообщение — утечка.
    /// </summary>
    private void WatchServer(bool watch)
    {
        var server = _doors.Server?.Invoke();

        if (server is null) return;

        if (watch) server.Changed += OnServerStateChanged;
        else server.Changed -= OnServerStateChanged;
    }

    /// <summary>
    /// ДЕРЖАТЬ ПОДПИСКУ ЖИВОЙ. Зовётся при открытии калитки и потом по часам.
    ///
    /// ⚠️ **Честно о промежутке.** События приложения движок НЕ переигрывает (замер §4): пока
    /// подписки не было, вопрос агента, заданный в этот промежуток, калитка НЕ УВИДИТ. Это
    /// ограничение движка, а не наша небрежность, и оно названо и здесь, и в отчёте.
    /// </summary>
    public void EnsureSubscription()
    {
        if (!_doors.Events.Present)
        {
            // «СВЯЗИ НЕТ» ГОВОРИТСЯ ТОЛЬКО КОГДА ЭТО РЕШЕНИЕ, А НЕ «ЕЩЁ НЕ СМОТРЕЛИ»
            // (задание владельца 14.10.2026, §1).
            //
            // **От какого случая.** Калитка спрашивает подписку при запуске — то есть РАНЬШЕ, чем
            // панель прочитает состояние сервера. Ссылки входа в этот миг ещё нет, и в журнал
            // уходило «связи с сервером DSH нет — вопрос агенту телефону показать нечем» про
            // работающий СВОЙ сервер: живой случай владельца, 14.10.2026 23:53:43, первая же
            // секунда запуска. Молчание здесь — не «всё хорошо», а «ещё не знаем»: подписку
            // спросим заново, когда о сервере станет известно (`OnServerStateChanged`).
            if (ServerKnown) _doors.Log(PanelStrings.PhoneEventDoorsMissing);

            return;
        }

        lock (_gate)
        {
            // КАЛИТКА НЕ СЛУШАЕТ — ПОДПИСКУ НЕ ОТКРЫВАЕМ. Иначе закрытая дверь оставила бы
            // открытым канал к движку: часы, опоздавшие на мгновение, завели бы подписку уже
            // после остановки, и гасить её было бы некому.
            if (_listener is null) return;

            if (_link is { Alive: true }) return;

            // Прежняя ссылка (если была) гасится: оборванный сокет держать незачем.
            _link?.Dispose();
            _link = null;

            // ⚠️ `clientId` ОБНУЛЯЕТСЯ вместе со ссылкой: он принадлежит ИМЕННО ЭТОЙ подписке,
            // и ответ, отправленный со старым `clientId` после переоткрытия, был бы ответом
            // «от несуществующего клиента» — движок отбросил бы его молча.
            _clientId = string.Empty;

            _link = _doors.Events.Open(OnFrame);
        }
    }

    /// <summary>
    /// ЧАСЫ ПОДПИСКИ — с ОТСТУПОМ после неудачи. Первая попытка идёт через
    /// <see cref="SubscriptionCheck"/>, каждая следующая неудачная — вдвое реже, до минуты.
    ///
    /// Зачем отступ. Движка может не быть часами (человек не запускал его), и попытка «каждые
    /// пять секунд» писала бы в журнал строку за строкой — это не «сказать словами», а шум,
    /// в котором настоящая беда не видна. Удачная подписка отступ сбрасывает: обрыв лечится
    /// быстро, а «движка нет» — редко и одной строкой.
    /// </summary>
    private void Tick()
    {
        lock (_gate)
        {
            if (_link is { Alive: true })
            {
                _failures = 0;
                _nextTry = DateTimeOffset.MinValue;
                return;
            }

            if (_nextTry > _doors.Clock()) return;
        }

        EnsureSubscription();

        lock (_gate)
        {
            if (_link is { Alive: true })
            {
                _failures = 0;
                _nextTry = DateTimeOffset.MinValue;
                return;
            }

            _failures = Math.Min(_failures + 1, 5);

            var seconds = Math.Min(
                SubscriptionCheck.TotalSeconds * Math.Pow(2, _failures - 1), 60);

            _nextTry = _doors.Clock() + TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>
    /// КАДР ИЗ ПОТОКА. Разбор — чистой функцией (<see cref="PhoneEventFrames"/>), здесь только
    /// состояние: наш `clientId`, текущий вопрос и признак «вопрос снят».
    ///
    /// ⚠️ `host` из кадра `ready` сюда не доходит вовсе (поля под него нет в разобранном кадре) —
    /// значит домашний каталог машины не может уехать телефону ни через `GET /question`,
    /// ни через журнал.
    /// </summary>
    private void OnFrame(string? text)
    {
        var frame = PhoneEventFrames.Parse(text);

        lock (_gate)
        {
            switch (frame.Kind)
            {
                case PhoneEventKind.Ready:
                    _clientId = frame.ClientId;
                    return;

                case PhoneEventKind.Question:
                    _question = new PendingQuestion(frame.EventId, frame.QuestionsJson, frame.AgentId);
                    _lastEventId = frame.EventId;
                    _outcome = QuestionOutcome.None;
                    _answering = false;

                    // НОВЫЙ ВОПРОС — НАЧИНАЕМ С ПЕРВОГО ЭКРАНА И С ЧИСТОГО ЛИСТА: ответы прежнего
                    // хода к этому вопросу не относятся, и унести их движку значило бы ответить
                    // за человека на то, чего он не видел.
                    _screen = 0;
                    _screenEventId = frame.EventId;
                    _screenAnswers.Clear();

                    // СЛЕД В ЖУРНАЛЕ: вопрос показан телефону. Номер события и ЧИСЛО вопросов —
                    // и ни одного слова из самого вопроса: в нём бывает чужой текст, а журнал
                    // читает владелец (уточнение владельца 11.10.2026: «следы без секретов»).
                    _doors.Log(string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.PhoneGateQuestionLogFormat,
                        frame.EventId,
                        PhonePage.Parse(frame.QuestionsJson).Count));
                    return;

                case PhoneEventKind.Cancelled:
                    // СНЯТИЕ — по НАШЕМУ вопросу и ни по какому другому: чужой `cancel` не имеет
                    // права стереть вопрос, который телефону ещё не показали.
                    if (frame.EventId.Length > 0 && frame.EventId == _lastEventId)
                    {
                        _question = null;
                        _outcome = QuestionOutcome.Taken;
                    }

                    return;

                case PhoneEventKind.Failure:
                    // Канал сломался — считаем подписку мёртвой, чтобы часы её переоткрыли.
                    _clientId = string.Empty;
                    _doors.Log(string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.PhoneEventBrokenLogFormat, frame.Detail));
                    return;

                default:
                    return;
            }
        }
    }

    // ---- сеть ------------------------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Слушатель закрыт (остановка калитки) или сокет отказал — цикл кончается.
                // Слов здесь не порождается: об остановке калитка уже сказала.
                return;
            }

            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                using var timed = CancellationTokenSource.CreateLinkedTokenSource(token);
                timed.CancelAfter(RequestTimeout);

                var stream = client.GetStream();
                var request = await ReadAsync(stream, client, timed.Token).ConfigureAwait(false);

                var response = request is null
                    ? PhoneHttpResponse.Words(400, false, Loc.T(PanelStrings.PhoneGateBadRequest))
                    : Route(request);

                await WriteAsync(stream, response, timed.Token).ConfigureAwait(false);

                // ВЕЖЛИВОЕ ЗАКРЫТИЕ — и это не украшение. Если закрыть сокет, не дочитав то, что
                // клиент ещё шлёт, Windows отвечает ему `RST`, и ответ до него НЕ ДОХОДИТ: отказ
                // «слишком большое тело» превратился бы в «соединение сброшено», то есть в молчание.
                // Поэтому сначала «я больше не пишу», потом дочитывание остатка с пределом.
                await LingerAsync(stream, timed.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Клиент ушёл посреди запроса — это его дело, а не беда калитки. Молчать здесь
                // можно: ответа всё равно некому отдать.
            }
        }
    }

    /// <summary>
    /// ОДИН РАЗОБРАННЫЙ ЗАПРОС. Из заголовков сохраняются ТРИ и только они: `Cookie` (кто пришёл),
    /// `Content-Length` (сколько читать) и `Accept-Language` (на каком языке говорить).
    /// Чужие заголовки не сохраняются вовсе — значит их нельзя случайно передать дальше,
    /// и «никаких чужих заголовков» держится устройством, а не внимательностью.
    /// </summary>
    private sealed record PhoneHttpRequest(
        string Method,
        string Path,
        string Query,
        string Cookie,
        string Language,
        string Address,
        string Body,
        bool TooBig,
        string UserAgent);

    private static async Task<PhoneHttpRequest?> ReadAsync(NetworkStream stream, TcpClient client, CancellationToken token)
    {
        var head = new MemoryStream();
        var chunk = new byte[4096];
        var end = -1;

        while (end < 0)
        {
            if (head.Length > MaxHeaderBytes) return TooBig(string.Empty);

            var read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0) return null;

            head.Write(chunk, 0, read);
            end = HeaderEnd(head.GetBuffer(), (int)head.Length);
        }

        var text = Encoding.UTF8.GetString(head.GetBuffer(), 0, end + 4);
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');

        if (first.Length < 2) return null;

        var method = first[0].Trim().ToUpperInvariant();

        // СТРОКА ЗАПРОСА БЕРЁТСЯ ЦЕЛИКОМ, а маршрут — без `?`: живому обновлению нужен
        // `?part=card`, остальным маршрутам — только путь. Разбор строки запроса в
        // <see cref="PhonePage.CardWanted"/> и больше нигде.
        var target = first[1];
        var mark = target.IndexOf('?');
        var path = mark < 0 ? target : target[..mark];
        var query = mark < 0 ? string.Empty : target[(mark + 1)..];

        var cookie = string.Empty;
        var language = string.Empty;
        var userAgent = string.Empty;
        var headers = 0;
        var contentLength = 0L;

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0) continue;

            headers++;

            if (headers > MaxHeaders) return TooBig(AddressOf(client));

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)) cookie = value;

            if (string.Equals(name, "Accept-Language", StringComparison.OrdinalIgnoreCase)) language = value;

            // ⚠️ `User-Agent` читается ТОЛЬКО ЗАТЕМ, чтобы назвать телефон человеку
            // (`PhoneDecisions.DeviceName`), и ничего не решает: доступ решает файл пропуска.
            // Подделать строку ничего не даёт — она не примета устройства.
            if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)) userAgent = value;

            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength < 0 || contentLength > MaxBodyBytes) return TooBig(AddressOf(client));

        var body = new byte[contentLength];
        var carried = (int)Math.Min(head.Length - (end + 4), contentLength);

        if (carried > 0) Array.Copy(head.GetBuffer(), end + 4, body, 0, carried);

        var filled = carried;

        while (filled < contentLength)
        {
            var read = await stream
                .ReadAsync(body.AsMemory(filled, (int)contentLength - filled), token)
                .ConfigureAwait(false);

            if (read <= 0) break;

            filled += read;
        }

        return new PhoneHttpRequest(
            method, path, query, cookie, language, AddressOf(client),
            Encoding.UTF8.GetString(body, 0, filled), false, userAgent);
    }

    private static PhoneHttpRequest TooBig(string address) =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, address, string.Empty, true,
            string.Empty);

    /// <summary>Адрес просящего — для ограничения попыток. Не разобрался — «адрес неизвестен»,
    /// и это отдельный ключ лимита, а не «пропустить мимо счёта».</summary>
    private static string AddressOf(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint is IPEndPoint end
                ? PhoneDecisions.AddressOf(end.Address.ToString())
                : PhoneDecisions.UnknownAddress;
        }
        catch
        {
            return PhoneDecisions.UnknownAddress;
        }
    }

    /// <summary>Где кончаются заголовки: индекс начала <c>CRLFCRLF</c>, иначе <c>-1</c>.</summary>
    private static int HeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (buffer[index] == 13 && buffer[index + 1] == 10 && buffer[index + 2] == 13 && buffer[index + 3] == 10)
                return index;
        }

        return -1;
    }

    // ---- маршруты ---------------------------------------------------------------------------

    private PhoneHttpResponse Route(PhoneHttpRequest request)
    {
        if (request.TooBig)
        {
            return PhoneHttpResponse.Words(
                413, false,
                string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.PhoneGateTooBigFormat, MaxBodyBytes / 1024));
        }

        if (request.Path == "/")
        {
            if (request.Method != "GET") return NotAllowed(request);

            // ЖИВОЕ ОБНОВЛЕНИЕ ПРОСИТ ТОЛЬКО КАРТОЧКУ: `GET /?part=card`. Это ответ на запрос
            // САМОЙ ЖЕ страницы (скрипт), и в нём нет ни `<style>`, ни `<script>` — только то,
            // чем подменяется карточка.
            //
            // ⚠️ **ЗА ДВЕРЬЮ — ТО ЖЕ ПРАВИЛО, ЧТО И НА СТРАНИЦЕ:** кто ещё не сопряжён, получает
            // ВХОД, а не карточку. Иначе «покажи карточку» стало бы вторым адресом работы —
            // без пропуска, без cookie и без единого слова о том, что ты не свой.
            if (!PhonePage.CardWanted(request.Query)) return Page(request);

            return Authorized(request, out var deviceId)
                ? PhoneHttpResponse.Html(PhonePage.Html(Language(request), View(deviceId), cardOnly: true))
                : PhoneHttpResponse.Html(PhoneLoginPage.Html(Language(request)));
        }

        if (request.Path == "/pair")
            return request.Method == "POST" ? Pair(request) : NotAllowed(request);

        if (request.Path == "/question")
            return request.Method == "GET" ? Question(request) : NotAllowed(request);

        if (request.Path == "/answer")
            return request.Method == "POST" ? Answer(request) : NotAllowed(request);

        if (request.Path == "/cancel")
            return request.Method == "POST" ? Cancel(request) : NotAllowed(request);

        if (request.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return request.Method == "POST"
                ? Api(request, request.Path[5..])
                : NotAllowed(request);
        }

        return PhoneHttpResponse.Words(
            404, false,
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateNotFoundFormat, request.Path));
    }

    private PhoneHttpResponse NotAllowed(PhoneHttpRequest request) =>
        PhoneHttpResponse.Words(
            405, false,
            string.Format(
                CultureInfo.CurrentCulture, PanelStrings.PhoneGateBadMethodFormat, request.Method, request.Path));

    /// <summary>
    /// КОРЕНЬ КАЛИТКИ — И ЭТО СТРАНИЦА (этап F2).
    ///
    /// ⚠️ **Без cookie — прежняя страница ВХОДА, с cookie — СТРАНИЦА ТЕЛЕФОНА.** Это не две двери,
    /// а одна: кто ещё не сопряжён, получает вход; кто свой — работу. Второго адреса телефону
    /// не заводится: он должен знать ОДИН адрес, и он у него уже есть (QR и слова раздела).
    ///
    /// Язык берётся у САМОГО телефона (<see cref="PhoneGateLanguages"/>), а не у панели: у телефона
    /// свой язык, и он говорит его заголовком <c>Accept-Language</c>.
    /// </summary>
    private PhoneHttpResponse Page(PhoneHttpRequest request)
    {
        var language = Language(request);

        return Authorized(request, out var deviceId)
            ? PhoneHttpResponse.Html(PhonePage.Html(language, View(deviceId)))
            : PhoneHttpResponse.Html(PhoneLoginPage.Html(language));
    }

    /// <summary>
    /// ТА ЖЕ ЗАПИСЬ, НО С ИМЕНЕМ УСТРОЙСТВА (решение владельца 12.10.2026): страница говорит
    /// человеку, каким устройством он вошёл, — с двух телефонов иначе не отличить, чей это экран
    /// и чей ответ уйдёт движку. Имя берётся ИЗ COOKIE (`Authorized`), а не из доводов запроса:
    /// подставить чужое телефон не может.
    ///
    /// ⚠️ **Показывается КОРОТКОЕ ИМЯ ТЕЛЕФОНА, а не внутренний `phone-N`** (решение владельца
    /// 12.10.2026): имя разобрано из <c>User-Agent</c> при сопряжении и лежит в записи устройства.
    /// Телефон себя не назвал — страница говорит честное «телефон» (`PhonePageDeviceFallback`),
    /// а не номер, которого человек не выбирал.
    /// </summary>
    private PhonePageView View(string deviceId)
    {
        var name = PhonePass.Load(_doors.Pass).SafeDevices
            .FirstOrDefault(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? string.Empty;

        return View() with
        {
            Device = PhoneDecisions.DeviceDisplay(name, Loc.T(PanelStrings.PhonePageDeviceFallback)),
        };
    }

    /// <summary>Язык телефона — по <c>Accept-Language</c>, а не по языку панели (см. <see cref="Page"/>).</summary>
    private static string Language(PhoneHttpRequest request) =>
        PhoneGateLanguages.For(request.Language, Loc.Language);

    /// <summary>
    /// ЧТО СТРАНИЦА ПОКАЗЫВАЕТ ПРЯМО СЕЙЧАС — одной записью, по состоянию калитки.
    ///
    /// ⚠️ Порядок решений назван: ВИСЯЩИЙ ВОПРОС главнее «чем кончился прошлый», потому что
    /// за время, пока человек читал ответ, мог прийти новый вопрос — и показать ему «вы ответили»
    /// вместо нового вопроса значило бы потерять работу агента.
    ///
    /// ⚠️ **ОДИН ВОПРОС НА ЭКРАН — вариант A** (решение владельца 11.10.2026): странице отдаётся
    /// РОВНО ОДИН вопрос — тот, на который человек отвечает сейчас, — а строку «Вопрос N из M»
    /// она собирает из <see cref="PhonePageView.Screen"/> и <see cref="PhonePageView.Total"/>.
    /// Полный набор накапливает калитка (<see cref="_screen"/>), а не страница.
    ///
    /// ⚠️ Вопрос, который не разобрался (`PhonePage.Parse` вернул пусто), показывается как
    /// <see cref="PhonePageKind.Unreadable"/>: отвечать без вопроса нечем, а «вопросов нет» было бы
    /// неправдой — телефон ждал бы, глядя на висящий вопрос.
    /// </summary>
    private PhonePageView View()
    {
        lock (_gate)
        {
            if (_question is not null)
            {
                var questions = PhonePage.Parse(_question.Questions);

                if (questions.Count == 0)
                    return new PhonePageView(PhonePageKind.Unreadable, _question.EventId, questions, true);

                // ЭКРАН — ПЕРВЫЙ НЕОТВЕЧЕННЫЙ, и он же запоминается как текущий: на него смотрит
                // сверка формы в `AnswerScreen`. Человек мог вернуться на прежний экран, но
                // показано ему то, что он ещё не отвечал.
                var screen = NextScreen(questions);

                _screen = screen;

                // ЖИВОЕ ОБНОВЛЕНИЕ У ВОПРОСА ЕСТЬ, и это не возврат прежнего дефекта: подмена
                // карточки ломала выбор только у meta refresh (перезагрузка стирала форму).
                // Здесь карточка подменяется ЦЕЛИКОМ заново, ответы с прошлых экранов лежат в
                // калитке (`_screenAnswers`), а пока человек пишет свой ответ — опрос приостановлен.
                return new PhonePageView(
                    PhonePageKind.Question,
                    _question.EventId,
                    new[] { questions[screen] },
                    true,
                    string.Empty,
                    screen,
                    questions.Count,
                    Answered(questions[screen].Id));
            }

            return _outcome switch
            {
                QuestionOutcome.Answered => PhonePageView.Outcome(PhonePageKind.Answered),
                QuestionOutcome.Taken => PhonePageView.Outcome(PhonePageKind.Taken),
                QuestionOutcome.Cancelled => PhonePageView.Outcome(PhonePageKind.Cancelled),

                // СВЯЗИ С СЕРВЕРОМ DSH НЕТ — И ЭТО НАДО СКАЗАТЬ, А НЕ ЖДАТЬ МОЛЧА (решение
                // владельца 13.10.2026, вариант «а»). Прежняя страница в этом положении говорила
                // «панель на связи и ждёт вопроса от агента» — обещала вопрос, которому неоткуда
                // взяться: двери к событиям движка нет вовсе (пустая ссылка входа — сервер поднят
                // не панелью). Своё состояние вместо обещания, и оно же проверяемо.
                _ when !_doors.Events.Present => PhonePageView.NoEngine(),

                _ => PhonePageView.Waiting(),
            };
        }
    }

    /// <summary>
    /// ЧТО НА ЭТОМ ЭКРАНЕ УЖЕ ОТВЕЧЕНО — по `id` вопроса. Один ответ и не больше: экран
    /// показывает ровно один вопрос. Пусто — человек ещё ничего не выбрал и не написал.
    /// </summary>
    private IReadOnlyDictionary<string, string> Answered(string questionId)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_screenAnswers.TryGetValue(questionId, out var answer)) answers[questionId] = answer;

        return answers;
    }

    /// <summary>
    /// КАКОЙ ЭКРАН ПОКАЗЫВАТЬ: ПЕРВЫЙ, НА КОТОРЫЙ ЧЕЛОВЕК ЕЩЁ НЕ ОТВЕЧАЛ.
    ///
    /// **Почему так, а не «сдвиг после удачи».** Удача у вопроса ОДНА: ответ уходит движку одним
    /// вызовом (`$events/result`), когда отвечены ВСЕ экраны. Между экранами никакой удачи нет,
    /// и сдвигать счётчик было бы нечем — а сдвиг при отказе движка потерялся бы, и человек
    /// застрял бы на отвеченном экране. Спросить «чего ещё не хватает» надёжнее: состояние одно
    /// (накопленные ответы), и оно же — правда о том, что показывать.
    ///
    /// Все отвечены — показывается ПЕРВЫЙ: это состояние «вот-вот уйдёт целиком» и одновременно
    /// «человек может поправить первый ответ», а не пустой экран.
    /// </summary>
    private int NextScreen(IReadOnlyList<PhoneQuestion> questions)
    {
        for (var index = 0; index < questions.Count; index++)
        {
            if (!_screenAnswers.ContainsKey(questions[index].Id)) return index;
        }

        return 0;
    }

    /// <summary>
    /// БЕДА СЛОВАМИ — СТРАНИЦЕЙ, а не текстом: телефон показывает ответ целиком, и «Ответ не
    /// отправлен: …» обязан быть страницей, а не голой строкой. Путь из беды у страницы есть —
    /// «Повторить» ссылкой на корень.
    /// </summary>
    private static PhoneHttpResponse Problem(string language, int status, string words) =>
        new(status, "text/html; charset=utf-8", PhonePage.Html(language, PhonePageView.Problem(words)), null);

    private PhoneHttpResponse Pair(PhoneHttpRequest request)
    {
        var language = PhoneGateLanguages.For(request.Language, Loc.Language);

        // ⚠️ ФОРМА ИЛИ JSON — как и у ответа: страница входа шлёт ОБЫЧНУЮ форму (скрипта на ней
        // больше нет), а машинный путь остался для проверок и скриптов. Форме отвечают СТРАНИЦЕЙ
        // (успех — перенаправлением на корень, беда — страницей входа со словами), JSON — словами.
        var form = !LooksJson(request.Body);

        // ЧТО ЧЕЛОВЕК НАБРАЛ — читается ДО всяких решений: и «не та форма кода», и «пауза после
        // перебора» возвращают его на страницу с тем же набранным (норма GOV.UK: форма приходит
        // «as the user filled them in»; `30-mobile-web.md` §4).
        var typed = form ? PhoneAnswer.Field(request.Body, "code") : null;
        var typedCode = (typed ?? string.Empty).Trim();

        // ⚠️ СТРАНИЦА БЕДЫ СТАВИТСЯ С ТЕМ ЖЕ КОДОМ, ЧТО И СЛОВА: отказ обязан быть отказом и по
        // коду ответа тоже, а не «200 со страницей, на которой написано, что не пустили». Тем же
        // правилом живёт страница беды у формы ответа (`Problem`).
        PhoneHttpResponse Say(int status, bool ok, string words) =>
            form
                ? PhoneHttpResponse.Html(PhoneLoginPage.Html(language, ok ? string.Empty : words, typedCode))
                    with { Status = status }
                : PhoneHttpResponse.Words(status, ok, words);

        var now = _doors.Clock();
        var address = PhoneDecisions.AddressOf(request.Address);

        // (1) ЛИМИТЫ. Спрашиваются ДО разбора кода: перебор обязан упираться в паузу, а не
        //     получать «ещё одну попытку угадать».
        var verdict = PhoneDecisions.DecideAttempt(_attempts, address, now);

        if (!verdict.Allowed) return Say(429, false, verdict.Text());

        // Поле формы `code` — тем же разбором, что и поля ответа: значение раскодируется, а «не та
        // форма» (не шесть цифр) остаётся ОТДЕЛЬНЫМ отказом от «не тот код».
        var code = form
            ? PhoneDecisions.CodeValid(typedCode) ? typedCode : null
            : CodeOf(request.Body);

        if (code is null)
        {
            Remember(address, now);
            return Say(403, false, Loc.T(PanelStrings.PhoneGateCodeWrongShape));
        }

        Pairing? pairing;

        lock (_gate) pairing = _pairing;

        if (pairing is null)
        {
            Remember(address, now);
            return Say(403, false, Loc.T(PanelStrings.PhoneGateNoCode));
        }

        if (!PhoneDecisions.CodeAlive(now, pairing.ExpiresAt))
        {
            Remember(address, now);
            return Say(403, false, Loc.T(PanelStrings.PhoneGateCodeExpired));
        }

        if (!PhoneDecisions.CodeMatches(code, pairing.Code))
        {
            Remember(address, now);
            return Say(403, false, Loc.T(PanelStrings.PhoneGateBadCode));
        }

        // (2) УСПЕХ. КОД УМИРАЕТ ПЕРВЫМ ДЕЛОМ — до записи пропуска: не запишись файл, человек
        //     получит отказ и возьмёт новый код, а не сможет войти тем же (подсмотренным) кодом.
        lock (_gate) _pairing = null;

        // ⚠️ ЧТЕНИЕ-ИЗМЕНЕНИЕ-ЗАПИСЬ ПОД ЗАМКОМ ПРОПУСКА (дефект CH-01): имя устройства считается
        // ПО ЧИСЛУ записанных, поэтому два одновременных сопряжения без замка выбрали бы одно имя
        // и затёрли друг друга. Дверь при этом уже не мешает: файл читается с разрешением на
        // запись (`PhonePassFiles.OpenRead`), а пишется во временный файл и переименовывается.
        string device;
        PhonePass pass;

        lock (_pass)
        {
            device = DeviceIdFor(PhonePass.Load(_doors.Pass).DeviceCount);

            // ⚠️ ЗАВОДИТСЯ НОВОЕ УСТРОЙСТВО, А НЕ НОВЫЙ ПРОПУСК: `WithNewDevice`, а не `Create`.
            // Иначе каждое сопряжение стирало бы прежние устройства, и «список подключённых» всегда
            // состоял бы из одного — человек увидел бы, что второй телефон отвязал первый.
            pass = PhonePass.Load(_doors.Pass).WithNewDevice(
                device, address, PricingDecisions.Stamp(now), PhoneDecisions.DeviceName(request.UserAgent));

            if (!pass.Save(_doors.Pass))
            {
                // Пропуск в памяти никому не нужен: он не переживёт перезапуск панели, и телефон
                // остался бы с cookie, которой на следующем запуске не соответствует ничего.
                // ПРИЧИНА отказа уже в журнале — её пишет `PhonePass.Save` (`PhonePassSaveFailedLogFormat`).
                return Say(500, false, Loc.T(PanelStrings.PhoneGatePassNotSaved));
            }
        }

        var signature = pass.DeviceSignature(device);

        if (signature.Length == 0) return Say(500, false, Loc.T(PanelStrings.PhoneGatePassNotSaved));

        // СЛЕД В ЖУРНАЛЕ: телефон сопряжён. Имя устройства (`phone-1`) и адрес, с которого он
        // пришёл, — НЕ пропуск и НЕ подпись: пропуск машины не покидает, а подпись не показывается
        // никому и никогда (уточнение владельца 11.10.2026: «следы без секретов»).
        _doors.Log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.PhoneGatePairedLogFormat, device, address));

        var cookie = SetCookie(CookieValue(device, signature));

        // ФОРМЕ — ПЕРЕНАПРАВЛЕНИЕ НА КОРЕНЬ: cookie уже стоит, и человек попадает на страницу
        // телефона (F5 не повторит отправку кода). Машинному пути — слова и cookie, как было.
        return form
            ? PhoneHttpResponse.Redirect(PhoneGateRoot) with { Cookie = cookie }
            : PhoneHttpResponse.Words(200, true, Loc.T(PanelStrings.PhoneGatePaired)) with { Cookie = cookie };
    }

    /// <summary>Код из тела <c>{"code":"123456"}</c>. <c>null</c> — тела нет, оно не JSON или поле
    /// не строка; «не та форма» и «не тот код» — РАЗНЫЕ отказы, и человеку они говорят разное.</summary>
    private static string? CodeOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("code", out var code)) return null;
            if (code.ValueKind != JsonValueKind.String) return null;

            var text = code.GetString() ?? string.Empty;

            return PhoneDecisions.CodeValid(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ЧТО ВИСИТ ПРЯМО СЕЙЧАС. Отдаются ТОЛЬКО `eventId` и `questions` — и ничего из кадра `ready`:
    /// в нём лежит домашний каталог машины, и телефону его показывать нечего (замер §3).
    /// </summary>
    private PhoneHttpResponse Question(PhoneHttpRequest request)
    {
        if (!Authorized(request, out _))
            return PhoneHttpResponse.Words(401, false, Loc.T(PanelStrings.PhoneGateUnauthorized));

        lock (_gate)
        {
            if (_question is not null)
            {
                return PhoneHttpResponse.Json(
                    200,
                    "{\"state\":\"question\",\"eventId\":" + Quote(_question.EventId)
                        + ",\"questions\":" + _question.Questions + "}");
            }

            // ЧЕМ КОНЧИЛСЯ ПРЕДЫДУЩИЙ — говорится и здесь, машинной дверью (`GET /question`
            // остаётся ею для проверок и для тех, кто читает JSON). ⚠️ Слова одни и те же для
            // всех исходов НАМЕРЕННО: менять их значило бы менять поведение двери, которую уже
            // стерегут проверки самотеста. Разные слова человеку говорит СТРАНИЦА (F2) — по
            // <see cref="_outcome"/>.
            if (_outcome != QuestionOutcome.None)
            {
                return PhoneHttpResponse.Json(
                    200,
                    "{\"state\":\"cancelled\",\"message\":"
                        + Quote(Loc.T(PanelStrings.PhoneGateQuestionCancelled)) + "}");
            }

            return PhoneHttpResponse.Json(
                200,
                "{\"state\":\"none\",\"message\":" + Quote(Loc.T(PanelStrings.PhoneGateNoQuestionNow)) + "}");
        }
    }

    /// <summary>
    /// ОТВЕТ НА ВОПРОС АГЕНТА — ДВУМЯ ПУТЯМИ, И ОБА ИДУТ ЧЕРЕЗ НАС (этап F2).
    ///
    /// * **СТРАНИЦА ТЕЛЕФОНА** шлёт ФОРМУ (`application/x-www-form-urlencoded`: `eventId` и выборы
    ///   `q0`, `q1`, …). ⚠️ Подписи вариантов калитка берёт ИЗ СВОЕГО вопроса, а не из формы:
    ///   телефон присылает НОМЕР варианта, поэтому подделать ответ он не может. Ответ формы —
    ///   СТРАНИЦА, а не строка: телефон показывает её целиком;
    /// * **МАШИННЫЙ путь** шлёт JSON `{eventId, answers}` — он остаётся для проверок и для тех,
    ///   кто читает JSON, и отвечает он словами, как отвечал.
    ///
    /// `clientId` и cookie движка подставляет калитка: телефону вход в движок не показывается
    /// и знать его не нужно.
    ///
    /// ⚠️ **ОТВЕТ УХОДИТ ОДИН РАЗ.** Два нажатия на странице приходят ДВУМЯ запросами в разных
    /// задачах (<c>Task.Run</c> в цикле приёма), поэтому между «взял вопрос» и «ушёл в движок»
    /// стоит <see cref="_answering"/>: второй запрос получает СЛОВА, а не второй ответ движку.
    /// </summary>
    private PhoneHttpResponse Answer(PhoneHttpRequest request)
    {
        var language = PhoneGateLanguages.For(request.Language, Loc.Language);

        // ФОРМА ИЛИ JSON — решает ФОРМА ТЕЛА, а не заголовок: заголовков калитка хранит три
        // и только три (`Cookie`, `Accept-Language`, адрес), а `Content-Type` в них нет.
        // JSON начинается с `{` — значит это машинный путь; всё прочее пробуем разобрать формой.
        //
        // ⚠️ **Разбор — это ещё не «наша форма».** Тело `eventId=…` БЕЗ имени выбора (`q0`) тоже
        // разбирается, но формой ОТВЕТА не является: им отвечает `POST /cancel`. Поэтому
        // `PhoneAnswer.ParseForm` отказывает, когда экран не назван, а «форма ли это» решается
        // ЗДЕСЬ — тем, разобралось ли тело целиком (<see cref="LooksForm"/>).
        var fields = LooksJson(request.Body) ? null : PhoneAnswer.ParseForm(request.Body);
        var form = PhoneAnswer.LooksForm(request.Body);

        // ⚠️ ОТКАЗ ФОРМЕ — ТОЖЕ СТРАНИЦА (решение владельца 11.10.2026): сырой JSON человек
        // на телефоне видеть не должен НИКОГДА, а форма шлётся именно человеком.
        if (!Authorized(request, out var deviceId))
        {
            return form
                ? Problem(language, 401, Loc.T(PanelStrings.PhoneGateUnauthorized))
                : PhoneHttpResponse.Words(401, false, Loc.T(PanelStrings.PhoneGateUnauthorized));
        }

        // «СЛОВАМИ ИЛИ СТРАНИЦЕЙ» — одним решением: у формы и у машинного пути слова одни и те же,
        // а вот ответ разный, и разойтись они не должны. ⚠️ Здесь же остаётся СЛЕД В ЖУРНАЛЕ, когда
        // ответ НЕ ушёл: причина называется коротко, и в ней нет ни пропуска, ни подписи, ни cookie
        // (уточнение владельца 11.10.2026: «следы без секретов»).
        PhoneHttpResponse Say(int status, bool ok, string words, string reason)
        {
            if (!ok)
            {
                _doors.Log(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.PhoneGateAnswerRefusedLogFormat, reason));
            }

            return form
                ? Problem(language, status, words)
                : PhoneHttpResponse.Words(status, ok, words);
        }

        string eventId;
        string answers;

        if (form)
        {
            // ФОРМА ЕСТЬ, А РАЗОБРАТЬ ЕЁ НЕ ВЫШЛО — это «не наша форма»: имени выбора в теле нет,
            // и отвечать не на что. Словами, а не пятисотой: человек мог прислать что угодно.
            if (fields is null)
                return Say(400, false, Loc.T(PanelStrings.PhoneGateBadRequest), "форма ответа не разобрана");

            // ⚠️ ЭКРАН ПРИНИМАЕТСЯ ЗДЕСЬ, ПОД ЗАМКОМ, И ВМЕСТЕ С НАКОПЛЕННЫМ: ответ уходит движку
            // ПОЛНЫМ набором, а экран двигается ТОЛЬКО после удачи (решение владельца 11.10.2026).
            var collected = AnswerScreen(fields, out var refusal, out eventId);

            if (collected is null) return Say(400, false, refusal, "ответ не собран");

            answers = collected;
        }
        else
        {
            if (!TryAnswer(request.Body, out eventId, out answers))
                return PhoneHttpResponse.Words(400, false, Loc.T(PanelStrings.PhoneGateBadRequest));
        }

        string clientId;

        lock (_gate)
        {
            if (_question is null)
            {
                _answering = false;

                return Say(403, false, !form
                    ? Loc.T(PanelStrings.PhoneGateNoQuestion)
                    : NoQuestionWords(), "нет вопроса");
            }

            // ЧУЖОЙ И УСТАРЕВШИЙ ВОПРОС — отказ СЛОВАМИ, а не молчание: телефон обязан понять,
            // что отвечает не на тот вопрос, который видит человек.
            if (!string.Equals(_question.EventId, eventId, StringComparison.Ordinal))
            {
                _answering = false;

                return Say(403, false, !form
                    ? Loc.T(PanelStrings.PhoneGateStaleQuestion)
                    : Loc.T(PanelStrings.PhonePageTaken), "устаревший вопрос");
            }

            if (_clientId.Length == 0)
            {
                _answering = false;

                return Say(503, false, !form
                    ? Loc.T(PanelStrings.PhoneGateNoChannel)
                    : string.Format(CultureInfo.CurrentCulture, PanelStrings.PhonePageAnswerFailedFormat, Loc.T(PanelStrings.PhoneGateNoChannel)),
                    "нет канала к движку");
            }

            clientId = _clientId;
        }

        // Конверт ответа — ЗАМЕР (замер §4): `clientId`, `eventId`, `outcome` с ответами.
        var args = "{\"clientId\":" + Quote(clientId)
            + ",\"eventId\":" + Quote(eventId)
            + ",\"outcome\":{\"kind\":\"result\",\"value\":{\"answers\":" + answers + "}}}";

        var answer = _doors.Engine(PhoneDecisions.EventsResultEndpoint, args);

        if (answer.Status == 0)
        {
            _answering = false;

            return Say(
                502, false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateEngineSilentFormat, answer.Body),
                "движок молчит");
        }

        // ⚠️ Отказ НАШЕГО входа в движок телефону показывать нельзя: он прочитал бы это как
        // «моя cookie плоха» и пошёл бы сопрягаться заново. Это беда панели, и она говорит о ней
        // сама, а телефону достаётся честное «движок сейчас не отвечает».
        if (answer.RefusedOurLogin || !answer.Ok)
        {
            _answering = false;

            return Say(
                502, false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateEngineRefusedFormat, answer.Status),
                "движок отказал");
        }

        // ДВИЖОК ОТВЕЧАЕТ `200` И НА ОТКАЗ: чужой или уже снятый `eventId` приходит как
        // `gateway/internal` «identifies no active event stream» (замер §4). Поэтому «принято»
        // решает не код HTTP, а отсутствие ошибки в конверте; иначе телефону сказали бы
        // «ответ отправлен» там, где ответ никуда не пошёл.
        if (EngineRpcClient.ErrorCode(answer.Body).Length > 0)
        {
            lock (_gate)
            {
                _answering = false;

                if (_question is not null && _question.EventId == eventId)
                {
                    _question = null;
                    _outcome = QuestionOutcome.Taken;
                }
            }

            return Say(403, false, !form
                ? Loc.T(PanelStrings.PhoneGateStaleQuestion)
                : Loc.T(PanelStrings.PhonePageTaken), "движок не знает такого события");
        }

        lock (_gate)
        {
            _answering = false;

            // Вопрос снимается, только если он ТОТ ЖЕ: за время ответа мог прийти новый.
            if (_question is not null && _question.EventId == eventId)
            {
                _question = null;
                _outcome = QuestionOutcome.Answered;
            }

            _screen = 0;
            _screenAnswers.Clear();
        }

        // СЛЕД В ЖУРНАЛЕ: ответ телефона УШЁЛ в движок. Номер события и имя устройства —
        // ни пропуска, ни подписи, ни cookie.
        _doors.Log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.PhoneGateAnswerLogFormat, eventId, deviceId));

        // ОТВЕТИВШЕМУ — НА СТРАНИЦУ ЗАНОВО (303 + `Location: /`), а не страница с обновлением:
        // после ответа человек должен увидеть «ответ отправлен», а не повторно отправлять ту же
        // форму по F5 (уточнение владельца 11.10.2026).
        return !form
            ? PhoneHttpResponse.Words(200, true, Loc.T(PanelStrings.PhoneGateAnswerSent))
            : PhoneHttpResponse.Redirect(PhoneGateRoot);
    }

    /// <summary>
    /// ОТМЕНИТЬ ХОД АГЕНТА, КОТОРЫЙ СПРАШИВАЕТ (этап F2).
    ///
    /// **Зачем это на странице.** Замер <c>docs\PHONE-LIVE.md</c> §4: **вопрос без ответившего
    /// висит вечно** и освобождается ТОЛЬКО отменой. Если телефон увидел вопрос, на который
    /// ответить не хочет (или не может), у него должен быть выход — иначе ход агента останется
    /// стоять, а человек будет думать, что панель сломалась.
    ///
    /// * отменяет калитка **той же дверью, что и панель**: <c>session/cancel</c> с `sessionId`
    ///   (движок ищет агента в реестре по `sessionId` — замер чтением §4 отчёта F2);
    /// * `eventId` формы сверяется с висящим вопросом: отменить ЧУЖОЙ ход телефоном нельзя;
    /// * ⚠️ **панель сама не отвечает никогда** (решение владельца 11.10.2026) — и здесь тоже:
    ///   отмена не подставляет ответ, а снимает ожидание.
    ///
    /// Отказ движка говорится СЛОВАМИ (`PhonePageCancelFailedFormat`), а не выдаётся за сделанное.
    /// </summary>
    private PhoneHttpResponse Cancel(PhoneHttpRequest request)
    {
        var language = PhoneGateLanguages.For(request.Language, Loc.Language);

        // ⚠️ ОТМЕНА — НЕ ОТВЕТ: у неё ОДНО поле (`eventId`), и разбирать её формой ответа нельзя
        // (та требует имени выбора `q0`, которого у отмены нет и быть не должно). «Форма ли это»
        // решается тем же признаком, что и у ответа (<see cref="PhoneAnswer.LooksForm"/>).
        var form = PhoneAnswer.LooksForm(request.Body);

        if (!Authorized(request, out var deviceId))
        {
            return form
                ? Problem(language, 401, Loc.T(PanelStrings.PhoneGateUnauthorized))
                : PhoneHttpResponse.Words(401, false, Loc.T(PanelStrings.PhoneGateUnauthorized));
        }

        var eventId = form ? PhoneAnswer.Field(request.Body, "eventId") ?? string.Empty : string.Empty;
        string agentId;

        lock (_gate)
        {
            if (_question is null)
                return Problem(language, 403, Loc.T(PanelStrings.PhonePageNoQuestion));

            if (eventId.Length == 0 && !TryCancel(request.Body, out eventId))
                return Problem(language, 400, Loc.T(PanelStrings.PhonePageNoQuestion));

            if (!string.Equals(_question.EventId, eventId, StringComparison.Ordinal))
                return Problem(language, 403, Loc.T(PanelStrings.PhonePageTaken));

            if (_question.AgentId.Length == 0)
            {
                return Problem(
                    language, 503,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.PhonePageCancelFailedFormat,
                        Loc.T(PanelStrings.PhoneGateNoChannel)));
            }

            agentId = _question.AgentId;
        }

        var args = "{\"request\":{\"sessionId\":" + Quote(agentId) + "}}";
        var answer = _doors.Engine(PhoneDecisions.SessionCancelEndpoint, args);

        if (answer.Status == 0 || answer.RefusedOurLogin || !answer.Ok || EngineRpcClient.ErrorCode(answer.Body).Length > 0)
        {
            var reason = answer.Status == 0
                ? answer.Body
                : EngineRpcClient.ErrorCode(answer.Body) is { Length: > 0 } code
                    ? code
                    : "HTTP " + answer.Status;

            return Problem(
                language,
                502,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhonePageCancelFailedFormat, reason));
        }

        lock (_gate)
        {
            // СНИМАЕТСЯ ТОЛЬКО ТОТ ЖЕ ВОПРОС: за время отмены мог прийти новый, и снять его
            // «за компанию» значило бы отменить чужой ход.
            if (_question is not null && _question.EventId == eventId)
            {
                _question = null;
                _outcome = QuestionOutcome.Cancelled;
            }

            _answering = false;
        }

        // СЛЕД В ЖУРНАЛЕ: ход агента отменён С ТЕЛЕФОНА. Номер события и имя устройства.
        _doors.Log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.PhoneGateCancelledLogFormat, eventId, deviceId));

        // ОТМЕНИВШЕМУ — НА СТРАНИЦУ ЗАНОВО (303 + `Location: /`): сказанное «ход отменён» человек
        // читает уже на странице, а не в теле ответа на POST (F5 не повторяет отмену).
        return PhoneHttpResponse.Redirect(PhoneGateRoot);
    }

    /// <summary>Корень калитки — одна дверь и для входа, и для страницы телефона.</summary>
    private const string PhoneGateRoot = "/";

    /// <summary>
    /// СЛОВА «ОТВЕЧАТЬ НЕ НА ЧТО» — ПО ИСХОДУ, а не одни и те же: «вы уже ответили», «вопрос снят»
    /// и «вопроса не было» — три РАЗНЫХ ответа человеку, и один на всех заставил бы его гадать,
    /// отправился ли его ответ. Читается под тем же замком, что и состояние.
    /// </summary>
    private string NoQuestionWords() => _outcome switch
    {
        QuestionOutcome.Answered => Loc.T(PanelStrings.PhonePageAnsweredAgain),
        QuestionOutcome.Taken => Loc.T(PanelStrings.PhonePageTaken),
        QuestionOutcome.Cancelled => Loc.T(PanelStrings.PhonePageCancelled),
        _ => Loc.T(PanelStrings.PhonePageNoQuestion),
    };

    /// <summary>
    /// ПРИНЯТЬ ОТВЕТ ОДНОГО ЭКРАНА И СОБРАТЬ ПОЛНЫЙ НАБОР. <c>null</c> — ответ не принят,
    /// и <paramref name="words"/> говорит человеку, почему.
    ///
    /// **Что здесь происходит, по порядку.**
    ///
    /// 1. **Вопроса нет** — отвечать не на что (слова по исходу, <see cref="NoQuestionWords"/>).
    /// 2. **Ответ уже отправляется** (<see cref="_answering"/>) — второй раз не отправляем.
    /// 3. **Вопрос не разобрался** — отвечать нечем.
    /// 4. **Экран показывается не тот**, на который отвечают: браузер мог отправить форму
    ///    устаревшей карточки. Экран НЕ сдвигается, ответ не уходит, и ответ НЕ ЗАПОМИНАЕТСЯ.
    /// 5. **ЧТО ЧЕЛОВЕК ОТВЕТИЛ.** Правило названо ОДНО и действует всегда: **заполнен свой ответ —
    ///    уходит СВОЙ ОТВЕТ, даже когда выбран вариант.** Он явнее: человек написал слова, а не
    ///    ткнул в готовое. Уходит он ТОЙ ЖЕ строкой, что и выбор (<c>selected</c>) — форма движка
    ///    одна (`docs\PHONE-LIVE.md` §3.5), и своего поля для «своего» у неё нет.
    /// 6. **ПОЛНЫЙ НАБОР — ОДНИМ ОТПРАВЛЕНИЕМ.** Не отвечен хоть один вопрос — отказ целиком:
    ///    неполный ответ движок примет за ответ, и агент пойдёт дальше по решению, которого
    ///    человек не принимал. Не отвечен МОГУТ быть только следующие экраны: до них человек
    ///    ещё не дошёл, и экран НЕ СДВИГАЕТСЯ — он ждёт ответа на этот.
    ///
    /// ⚠️ **Экран сдвигается только после того, как ответ УШЁЛ движку** (успех — сдвиг, отказ —
    /// тот же экран). Поэтому «отвечено» и «отправлено» здесь идут разными шагами, и промежуточный
    /// отказ ничего не теряет: текущий экран уже отвечен, а следующие ждут своей очереди.
    /// </summary>
    private string? AnswerScreen(PhoneAnswerFields form, out string words, out string eventId)
    {
        words = string.Empty;
        eventId = string.Empty;

        lock (_gate)
        {
            if (_question is null)
            {
                words = NoQuestionWords();
                return null;
            }

            if (_answering)
            {
                words = Loc.T(PanelStrings.PhonePageAnsweredAgain);
                return null;
            }

            var questions = PhonePage.Parse(_question.Questions);

            if (questions.Count == 0)
            {
                words = Loc.T(PanelStrings.PhonePageUnreadable);
                return null;
            }

            // ЭКРАН НАЗВАН ФОРМОЙ, А НЕ УГАДАН. Ноль здесь — «первый экран», и это не «пусто».
            // Ответ на ДРУГОЙ экран не запоминается: это ответ не на то, что человек видит.
            //
            // ⚠️ Экран сверяется с ТЕМ, ЧТО ПОКАЗАНО, и показывается ВСЕГДА первый неотвеченный
            // (<see cref="NextScreen"/>). Поэтому «ответил на первый» само делает второй текущим —
            // отдельного сдвига после удачи не нужно, и он не потерялся бы при отказе движка.
            if (form.Screen != _screen)
            {
                words = Loc.T(PanelStrings.PhonePageTaken);
                return null;
            }
            var question = questions[_screen];
            var own = form.Own.Trim();
            string answer;

            if (own.Length > 0)
            {
                answer = own;
            }
            else
            {
                if (!int.TryParse(form.Choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosen))
                {
                    words = Loc.T(PanelStrings.PhonePageChooseAll);
                    return null;
                }

                var options = question.Options ?? Array.Empty<string>();

                if (chosen < 0 || chosen >= options.Count)
                {
                    words = Loc.T(PanelStrings.PhonePageChooseAll);
                    return null;
                }

                answer = options[chosen];
            }

            // ОТВЕТ ЭТОГО ЭКРАНА ЗАПОМИНАЕТСЯ ДО ПОПЫТКИ СБОРКИ — и это не мелочь: полного набора
            // ещё нет (следующие экраны впереди), но ЭТОТ экран уже отвечен. Запомни его после
            // проверки полноты — и экран застрял бы навсегда: `Build` вернул бы `null`, ответ
            // не записался бы, и следующий показ снова показал бы тот же экран.
            _screenAnswers[question.Id] = answer;

            eventId = _question.EventId;

            // ⚠️ ПОЛНЫЙ НАБОР: ответы идут В ПОРЯДКЕ ВОПРОСОВ КАДРА, а не в порядке экранов —
            // движок ищет вопрос по `id`, но порядок здесь часть формы ответа, и он же виден
            // в теле вызова при разборе живого прогона.
            var built = PhoneAnswer.Build(questions, _screenAnswers);

            // НЕПОЛНЫЙ НАБОР — ОТКАЗ, НО ЭКРАН УЖЕ ОТВЕЧЕН: следующий покажет `View()`, а не это
            // место. Ничего не «съезжает»: `PhoneAnswer.Build` порядок не меняет.
            if (built is null)
            {
                words = Loc.T(PanelStrings.PhonePageChooseAll);
                return null;
            }

            _answering = true;

            return built;
        }
    }

    /// <summary>ТЕЛО — ЭТО JSON? Смотрим на первый непробельный знак: `{` бывает только у JSON,
    /// а форма начинается с имени поля.</summary>
    private static bool LooksJson(string body)
    {
        var text = (body ?? string.Empty).TrimStart();

        return text.StartsWith('{');
    }

    /// <summary>Тело отмены: <c>{"eventId":"…"}</c>. Машинный путь рядом с формой — по той же причине,
    /// что у ответа: проверкам и скриптам нужен JSON, а странице — форма.</summary>
    private static bool TryCancel(string body, out string eventId)
    {
        eventId = string.Empty;

        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("eventId", out var id)) return false;
            if (id.ValueKind != JsonValueKind.String) return false;

            eventId = id.GetString() ?? string.Empty;

            return eventId.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ответ телефона: <c>{"eventId":"…","answers":[…]}</c>. Ответы передаются движку
    /// КАК ЕСТЬ — панель их не толкует: она их не понимает и понимать не должна.</summary>
    private static bool TryAnswer(string body, out string eventId, out string answers)
    {
        eventId = string.Empty;
        answers = "[]";

        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("eventId", out var id)) return false;
            if (id.ValueKind != JsonValueKind.String) return false;

            eventId = id.GetString() ?? string.Empty;
            if (eventId.Length == 0) return false;

            if (!document.RootElement.TryGetProperty("answers", out var list)) return false;
            if (list.ValueKind != JsonValueKind.Array) return false;

            answers = list.GetRawText();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ВЫЗОВ ДВИЖКА — БЕЛЫМ СПИСКОМ, А НЕ МОСТОМ.
    ///
    /// Порядок здесь — тоже правило: сначала «пустили ли тебя», потом «можно ли это имя», потом
    /// «не поток ли это», потом «есть ли доводы», и только потом движок. Обратный порядок означал бы,
    /// что чужое имя уходит в движок «на проверку», то есть что белого списка нет вовсе.
    /// </summary>
    private PhoneHttpResponse Api(PhoneHttpRequest request, string endpoint)
    {
        if (!Authorized(request, out _))
            return PhoneHttpResponse.Words(401, false, Loc.T(PanelStrings.PhoneGateUnauthorized));

        var verdict = PhoneDecisions.DecideCall(endpoint);

        if (!verdict.Allowed) return PhoneHttpResponse.Words(403, false, verdict.Text());

        var call = PhoneDecisions.Allowed.First(item =>
            string.Equals(item.Endpoint, verdict.Endpoint, StringComparison.Ordinal));

        // ПОТОК ОДИНОЧНЫМ POST НЕ ЗОВЁТСЯ — и это сказано СЛОВАМИ, а не молчанием: молчание
        // выглядело бы «телефон ничего не получил», а на деле он зовёт то, чего так не зовут
        // (`docs\PHONE-LIVE.md` §2: движок отвечает `gateway/signature-invalid`). Поток телефону
        // открывает страница телефона (F2) — своей дверью, а не сырым POST.
        if (call.Mode == EngineRpcMode.Stream)
        {
            return PhoneHttpResponse.Words(
                403, false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateStreamRefusedFormat, endpoint));
        }

        // `$events/result` — СЛУЖЕБНАЯ ДВЕРЬ ДВИЖКА. Телефон отвечает на вопрос своей дверью
        // (`POST /answer`): там калитка подставляет НАШ `clientId`, и телефон его не видит.
        if (string.Equals(endpoint, PhoneDecisions.EventsResultEndpoint, StringComparison.Ordinal))
            return PhoneHttpResponse.Words(403, false, Loc.T(PanelStrings.PhoneGateInternalRefused));

        var args = ArgsOf(request.Body);

        if (args is null)
        {
            return PhoneHttpResponse.Words(
                400, false,
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.PhoneGateNoArgumentsFormat,
                    PhoneDecisions.ArgumentKeyFor(endpoint)));
        }

        var engine = _doors.Engine(endpoint, args);

        if (engine.Status == 0)
        {
            return PhoneHttpResponse.Words(
                502, false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateEngineSilentFormat, engine.Body));
        }

        if (engine.RefusedOurLogin)
        {
            return PhoneHttpResponse.Words(
                502, false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneGateEngineRefusedFormat, engine.Status));
        }

        // ОТВЕТ ДВИЖКА УХОДИТ ТЕЛЕФОНУ КАК ЕСТЬ: ни разбора, ни обрезки, ни добавления своего.
        return new PhoneHttpResponse(engine.Status, "application/json; charset=utf-8", engine.Body, null);
    }

    /// <summary>Доводы из тела <c>{"args":{…}}</c> — СЫРЫМ текстом объекта и без толкования:
    /// панель не знает, что в них, и знать не должна. <c>null</c> — доводов нет вовсе.</summary>
    private static string? ArgsOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("args", out var args)) return null;
            if (args.ValueKind != JsonValueKind.Object) return null;

            return args.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    // ---- пропуск устройства -----------------------------------------------------------------

    /// <summary>
    /// ПУСКАТЬ ЛИ ЭТУ ПРОСЬБУ. Условие одно и оно строгое: подпись устройства совпала
    /// (<see cref="PhonePass.VerifyDevice"/>), а тот же метод проверяет и «устройство не отвязано» —
    /// иначе отвязанный телефон продолжил бы ходить со своей прежней подписью.
    /// </summary>
    private bool Authorized(PhoneHttpRequest request, out string deviceId)
    {
        deviceId = string.Empty;

        var cookie = CookieValueOf(request.Cookie);

        if (cookie.Length == 0) return false;

        var dot = cookie.IndexOf('.');
        if (dot <= 0 || dot == cookie.Length - 1) return false;

        var device = cookie[..dot];
        var signature = cookie[(dot + 1)..];

        if (!PhonePass.Load(_doors.Pass).VerifyDevice(device, signature)) return false;

        deviceId = device;

        return true;
    }

    /// <summary>Значение НАШЕЙ cookie из заголовка `Cookie`. Чужие cookie не трогаются вовсе.</summary>
    public static string CookieValueOf(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return string.Empty;

        foreach (var item in cookieHeader!.Split(';'))
        {
            var piece = item.Trim();
            var equals = piece.IndexOf('=');

            if (equals <= 0) continue;

            if (string.Equals(piece[..equals].Trim(), CookieName, StringComparison.Ordinal))
                return piece[(equals + 1)..].Trim();
        }

        return string.Empty;
    }

    /// <summary>Значение cookie телефона: <c>deviceId.подпись</c>. САМ ПРОПУСК сюда не попадает
    /// никогда — за этим стоит <see cref="PhonePass.DeviceSignature"/> и его довод.</summary>
    public static string CookieValue(string deviceId, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);

        return deviceId + "." + signature;
    }

    /// <summary>
    /// ЗАГОЛОВОК НАШЕЙ COOKIE.
    ///
    /// ⚠️ **`Secure` НЕ СТАВИТСЯ, И ЭТО НАЗВАНО СЛОВАМИ.** Панель работает в своей сети по HTTP
    /// без шифрования: с `Secure` браузер телефона не отдал бы cookie ни разу, и сопряжение
    /// «проходило» бы без входа. Ограничение записано в `docs\ROADMAP-BORROWED.md`, F3 (шифрование),
    /// и пока оно не снято, вход защищают код сопряжения и пропуск, а не канал.
    /// `HttpOnly` — от чтения скриптами, `SameSite=Lax` — от чужой страницы, `Path=/` — на всю калитку.
    ///
    /// ⚠️ **`Lax`, А НЕ `Strict`, И ЭТО ИЗМЕРЕНО ЖИВЫМ ПРОГОНОМ 12.10.2026.** С `Strict` cookie
    /// НЕ прикладывается к переходу, начатому ДРУГИМ приложением, — а вход по QR именно такой:
    /// камеру открывает другое приложение. Владелец видел следствие: адрес, набранный руками,
    /// пускал сразу, а **вход по QR каждый раз спрашивал код** и заводил новое устройство (отсюда
    /// четыре устройства вместо одного).
    ///
    /// **Чем `Lax` не хуже здесь, словами.** Всё, что МЕНЯЕТ состояние, — это <c>POST</c>
    /// (`/pair`, `/answer`, `/cancel`), а к чужим POST `Lax` cookie не прикладывает вовсе: значит
    /// чужой странице по-прежнему нечем ответить за человека и нечем сопрячься. А обычный
    /// top-level переход GET'ом из другого приложения cookie получает — ровно случай QR.
    /// `HttpOnly` и срок жизни сохранены без изменений.
    /// </summary>
    public static string SetCookie(string value) =>
        CookieName + "=" + value
        + "; Path=/; HttpOnly; SameSite=Lax; Max-Age="
        + (int)CookieLifetime.TotalSeconds;

    // ---- попытки ----------------------------------------------------------------------------

    private void Remember(string address, DateTimeOffset now)
    {
        var from = now - PhoneDecisions.AttemptWindow;

        lock (_gate)
        {
            // Старое выбрасывается при каждой записи: список попыток не имеет права расти вечно —
            // калитка живёт месяцами.
            _attempts.RemoveAll(attempt => attempt.At < from);
            _attempts.Add(new PhoneDecisions.Attempt(address, now));
        }
    }

    // ---- ответ ------------------------------------------------------------------------------

    /// <summary>
    /// ДОЧИТАТЬ ОСТАТОК ЗАПРОСА И ЗАКРЫТЬ СОКЕТ ВЕЖЛИВО.
    ///
    /// Windows отвечает `RST` всякому, кто закрывает сокет с непрочитанными данными, — и тогда
    /// уже отправленный ответ до клиента не доходит. Проверка «слишком большое тело» на этом
    /// и попала бы: отказ был бы, а слов человек не увидел.
    ///
    /// Предел дочитывания назван: клиент, который шлёт бесконечно, держит соединение не дольше
    /// общего срока запроса (<see cref="RequestTimeout"/>) и не больше полумегабайта.
    /// </summary>
    private static async Task LingerAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            stream.Socket.Shutdown(SocketShutdown.Send);

            var scrap = new byte[4096];
            var left = 512 * 1024;

            while (left > 0)
            {
                var read = await stream.ReadAsync(scrap.AsMemory(), token).ConfigureAwait(false);
                if (read <= 0) return;

                left -= read;
            }
        }
        catch
        {
            // Клиент уже ушёл — ровно этого и добивались.
        }
    }

    private static async Task WriteAsync(NetworkStream stream, PhoneHttpResponse response, CancellationToken token)    {
        var body = Encoding.UTF8.GetBytes(response.Body);

        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n");

        if (response.Cookie is not null) head.Append("Set-Cookie: ").Append(response.Cookie).Append("\r\n");

        // ПЕРЕНАПРАВЛЕНИЕ — ЗАГОЛОВКОМ: браузер уходит на `Location` сам, поэтому F5 повторяет
        // уже GET, а не отправку формы.
        if (response.Location is not null) head.Append("Location: ").Append(response.Location).Append("\r\n");

        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        303 => "See Other",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "OK",
    };

    /// <summary>Ответ калитки телефону. <see cref="Cookie"/> — только у удачного сопряжения,
    /// <see cref="Location"/> — только у перенаправления (303) после формы.</summary>
    private sealed record PhoneHttpResponse(
        int Status,
        string ContentType,
        string Body,
        string? Cookie,
        string? Location = null)
    {
        public static PhoneHttpResponse Json(int status, string body) =>
            new(status, "application/json; charset=utf-8", body, null);

        public static PhoneHttpResponse Html(string body) =>
            new(200, "text/html; charset=utf-8", body, null);

        /// <summary>
        /// ОТПРАВИТЬ БРАУЗЕР НА ДРУГУЮ СТРАНИЦУ — 303 и <c>Location</c>, тело пустое.
        ///
        /// ⚠️ Именно 303, а не 302: <c>See Other</c> обязывает браузер сделать следующий запрос
        /// методом <c>GET</c>. С 302 браузер повторил бы POST на <c>/</c> по F5 — то есть отправлял
        /// бы ответ на вопрос повторно, чего решением владельца быть не должно.
        /// </summary>
        public static PhoneHttpResponse Redirect(string location) =>
            new(303, "text/html; charset=utf-8", string.Empty, null, location);

        /// <summary>Слова человеку в одном виде: <c>{"ok":…,"message":"…"}</c>.</summary>
        public static PhoneHttpResponse Words(int status, bool ok, string words) =>
            Json(status, "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":" + Quote(words) + "}");
    }

    /// <summary>Как писать строки в JSON: кириллица и китайский — СЛОВАМИ, а не <c>\uXXXX</c>.</summary>
    private static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Доводы в JSON одной строкой — тем же кодировщиком, что и слова человеку.</summary>
    private static string Quote(string text) => JsonSerializer.Serialize(text ?? string.Empty, Readable);
}
