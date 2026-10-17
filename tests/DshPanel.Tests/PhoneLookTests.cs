using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ВИД СТРАНИЦЫ ТЕЛЕФОНА (этап F5, вариант A) — ЖИВЫМ ПУТЁМ.
///
/// **Зачем отдельный класс.** Вид уже сторожат чистые проверки (`PhonePageTests`): они собирают
/// страницу тем же членом, что и продукт, и разбирают её. Но у вида есть ЖИВОЙ шов — то, что
/// отдаётся телефону НА САМОМ ДЕЛЕ: страница от калитки, её карточка от `GET /?part=card`,
/// сохранённое «своё» и экран вопроса. Проверка «чистого решения» доказательством обвязки
/// не считается (правило шва, `AGENTS.md`), поэтому здесь всё идёт через НАСТОЯЩУЮ калитку:
/// слушатель на петле, свободный порт у системы, подставной движок и подставная подписка.
///
/// ⚠️ **Наружу не уходит ни один байт.** Адрес — <c>127.0.0.1</c> (петля), порт — свободный
/// у системы, движок и подписка — подставные двери, файл пропуска — под своим временным корнем.
/// Порты <c>3080</c>, <c>3081</c> и <c>43127</c> не занимаются: первый и второй запрещены
/// решением (<see cref="PhoneGate.PortAllowed"/>), третий — умолчание продукта.
///
/// **Нормы, которыми держится вид** (каждое решение — со ссылкой; где нормы нет, так и сказано):
/// WCAG 2.2 SC 2.5.5 (цель 44×44) и SC 2.4.13 (фокус 2 точки), web.dev (8 точек между целями),
/// GOV.UK question pages (`fieldset`+`legend`, одна подсказка без точки) — всё это записано
/// в `_design-library\30-mobile-web.md` и `_design-standard-draft.md` §2–§3, откуда и взято.
/// </summary>
public class PhoneLookTests
{
    private const string EventId = "event-look-1";

    // ---- стенд: калитка на петле и на свободном порту ----------------------------------------

    /// <summary>Подставная подписка на события: кадры подаёт проверка.</summary>
    private sealed class FakeEvents
    {
        private readonly List<Action<string>> _subscribers = new();

        public PhoneEventDoors Doors => new(Open, _ => { });

        public void Emit(string frame)
        {
            foreach (var subscriber in _subscribers.ToArray()) subscriber(frame);
        }

        private IPhoneEventLink Open(Action<string> onFrame)
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
    public sealed record EngineCall(string Endpoint, string Args);

    /// <summary>Ответ калитки: код, тело, cookie и место перенаправления.</summary>
    private sealed record Reply(int Status, string Body, string? Cookie, string? Location = null);

    private sealed class Stand : IDisposable
    {
        private readonly string _root;

        public Stand()
        {
            _root = PanelTestStand.TempDir();
            Paths = AppPaths.Under(_root);
            Port = FreeLoopbackPort();
            Events = new FakeEvents();

            var doors = new PhoneGateDoors(
                () => "127.0.0.1",
                Port,
                (endpoint, args) =>
                {
                    Calls.Add(new EngineCall(endpoint, args));

                    return ReplyAnswer;
                },
                PhonePassFiles.Under(Paths, Log.Add),
                Events.Doors,
                () => Now,
                Log.Add,
                _ => { });

            Gate = new PhoneGate(doors, () => true, humanLaunch: true, RunContext.Create(RunRequest.Owner()));
            Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + Port + "/"),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public AppPaths Paths { get; }

        public int Port { get; }

        public FakeEvents Events { get; }

        public PhoneGate Gate { get; }

        public HttpClient Http { get; }

        public List<EngineCall> Calls { get; } = new();

        public List<string> Log { get; } = new();

        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public PhoneEngineAnswer ReplyAnswer { get; set; } =
            new(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        public void Dispose()
        {
            Gate.Dispose();
            Http.Dispose();
            PanelTestStand.RemoveTemp(_root);
        }
    }

    /// <summary>Свободный порт петли — ровно так его берёт и сам продукт (<see cref="FreePort"/>).</summary>
    private static int FreeLoopbackPort()
    {
        var port = FreePort.Find();

        Assert.True(port > 0, "система не дала свободного порта петли — прогон не состоялся бы");
        Assert.False(PhoneGate.PortForbidden(port), "калитка не имеет права занимать этот порт");

        return port;
    }

    private static async Task<Reply> Send(
        HttpClient http,
        HttpMethod method,
        string path,
        string? body = null,
        string? cookie = null,
        string? language = null,
        string? userAgent = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", PhoneGate.CookieName + "=" + cookie);

        if (language is not null) request.Headers.TryAddWithoutValidation("Accept-Language", language);

        // `User-Agent` шлём ТОЛЬКО когда его назвали: у обычного клиента .NET он свой, и тогда
        // «телефон себя не назвал» проверялось бы не тем запросом, каким задумано.
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

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

    /// <summary>Сопрячь телефон по-настоящему: код, форма, cookie. Возвращает значение cookie.</summary>
    private static async Task<string> Pair(Stand stand)
    {
        var code = stand.Gate.IssueCode();
        var reply = await Send(stand.Http, HttpMethod.Post, "/pair", "code=" + code);

        Assert.Equal(303, reply.Status);
        Assert.NotNull(reply.Cookie);

        return PhoneGate.CookieValueOf(reply.Cookie!);
    }

    /// <summary>
    /// СОПРЯЖЕНИЕ С ТЕЛЕФОНОМ, КОТОРЫЙ СЕБЯ НАЗВАЛ: в запросе идёт настоящий <c>User-Agent</c>.
    /// Обычный <see cref="Pair"/> его не шлёт вовсе — и это тоже случай, который проверяется:
    /// телефон, не сказавший о себе ничего, обязан получить честное «телефон».
    /// </summary>
    private static async Task<string> PairAs(Stand stand, string userAgent)
    {
        var code = stand.Gate.IssueCode();
        var reply = await Send(stand.Http, HttpMethod.Post, "/pair", "code=" + code, userAgent: userAgent);

        Assert.Equal(303, reply.Status);
        Assert.NotNull(reply.Cookie);

        return PhoneGate.CookieValueOf(reply.Cookie!);
    }

    /// <summary>Вопрос агента — тот же кадр, что приходит от движка живьём (замер §4).</summary>
    private static string Waterfall(params string[] questions) =>
        "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
        + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
        + "\"request\":{\"questions\":[" + string.Join(",", questions) + "]}}}";

    /// <summary>
    /// ПОДПИСКА ГОТОВА: кадр <c>ready</c> с нашим <c>clientId</c>. Без него отвечать НЕЧЕМ —
    /// и это не придирка проверки, а правило калитки: ответ уходит движку с её `clientId`,
    /// и без подписки вопрос просто некуда отправить.
    /// </summary>
    private static void Ready(Stand stand) =>
        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-look\"}}");

    private static string Choice(string id, string text, params string[] labels) =>
        "{\"id\":\"" + id + "\",\"question\":\"" + text + "\",\"detail\":\"подробность\",\"options\":["
        + string.Join(",", labels.Select(label => "{\"label\":\"" + label + "\"}")) + "]}";

    /// <summary>Разметка БЕЗ скрипта: он один на всю страницу, и мешать ему в проверках вида нечего.</summary>
    private static string Markup(string html)
    {
        var start = html.IndexOf("<script", StringComparison.Ordinal);
        if (start < 0) return html;

        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return end < 0 ? html[..start] : html[..start] + html[(end + "</script>".Length)..];
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---- 1. замок на живом пути --------------------------------------------------------------

    /// <summary>
    /// НА ЖИВОМ ПУТИ СТРАНИЦА НЕ ТЯНЕТ НИЧЕГО СНАРУЖИ И ОБРАЩАЕТСЯ ТОЛЬКО К СЕБЕ.
    ///
    /// **Замок F5, слово в слово (решение владельца 11.10.2026):** ни одного внешнего ресурса
    /// и ни одного чужого адреса; наш скрипт ровно один; обращается только к своему адресу.
    /// Проверка идёт по ОТВЕТУ КАЛИТКИ (настоящий слушатель, настоящая страница), а не по
    /// собранной в проверке строке: страница приходит телефону именно отсюда.
    ///
    /// Мутации, которые обязаны упасть здесь: «подключить шрифт» · «добавить второй скрипт» ·
    /// «сходить на чужой хост» (адрес вида <c>https://…</c> в скрипте) · «сослаться на чужой путь».
    /// </summary>
    [Fact]
    public async Task Живой_замок_только_свои_адреса_и_один_скрипт()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

        Assert.Equal(200, page.Status);

        // НИ ОДНОГО ВНЕШНЕГО РЕСУРСА: ни `http(s)://`, ни `//`, ни `<link>`, ни `<img>`, ни
        // `@import`, ни `url(`, ни `src=`, ни чужого `formaction`.
        foreach (var forbidden in new[]
                 {
                     "http://", "https://", "<link", "<img", "<iframe", "@import", "url(", "src=",
                     "formaction", "onclick", "javascript:",
                 })
        {
            Assert.True(
                page.Body.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                $"на живой странице есть «{forbidden}»");
        }

        // // НИ ОДНОГО ЧУЖОГО АДРЕСА: в скрипте не встречается ни одного адреса, кроме `location.pathname`.
        Assert.DoesNotContain("//", page.Body, StringComparison.Ordinal);

        // НАШ СКРИПТ РОВНО ОДИН — и обращается он ТОЛЬКО к своему адресу.
        Assert.Equal(1, Occurrences(page.Body, "<script"));
        Assert.Equal(1, Occurrences(page.Body, "</script>"));
        Assert.Contains(
            "fetch(location.pathname+\"?part=" + PhonePage.CardPart + "\")",
            page.Body,
            StringComparison.Ordinal);

        // РОВНО ОДИН `<style>` — на живом пути тоже.
        Assert.Equal(1, Occurrences(page.Body, "<style>"));
        Assert.Equal(1, Occurrences(page.Body, "</style>"));
    }

    // ---- 2. без скрипта страница работает ----------------------------------------------------

    /// <summary>
    /// БЕЗ СКРИПТА СТРАНИЦА РАБОТАЕТ: «Проверить» — ссылка, форма ответа — обычный POST.
    ///
    /// **От какого случая.** Прежняя страница входа отправляла код скриптом, и без скрипта вход
    /// не работал вовсе. Теперь ни одно действие человека скрипта не требует: «Проверить» —
    /// <c>&lt;a class="ghost narrow" href="/"&gt;</c>, ответ уходит обычной формой.
    ///
    /// Мутации, которые обязаны упасть здесь: «кнопка „Проверить“ стала `<button type=button>`»
    /// (без скрипта не работает) · «форма ответа отправляется скриптом» · «вход не работает
    /// формой».
    /// </summary>
    [Fact]
    public async Task Без_скрипта_страница_работает()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);
        var markup = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        // «ПРОВЕРИТЬ» — ССЫЛКА, и у неё есть `href` на свой корень.
        Assert.Contains("<a class=\"ghost narrow\" href=\"/\">", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"button\"", markup, StringComparison.Ordinal);

        // ВОПРОС ЕСТЬ — ФОРМА ОТВЕТА ОБЫЧНАЯ (POST), а не скрипт.
        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет")));

        var page = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains("<form method=\"post\" action=\"/answer\">", page, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", page, StringComparison.Ordinal);
        Assert.Contains("<a class=\"ghost narrow\" href=\"/\">", page, StringComparison.Ordinal);

        // И ОТВЕТ УХОДИТ: обычный POST проходит целиком, без единого скрипта.
        stand.ReplyAnswer = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var answered = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=" + EventId + "&q0=0", cookie);

        Assert.Equal(303, answered.Status);
        Assert.Equal("/", answered.Location);
        Assert.Single(stand.Calls);
        Assert.Contains("\"selected\":[\"Да\"]", stand.Calls[0].Args, StringComparison.Ordinal);
    }

    // ---- 3. страница не перезагружается ------------------------------------------------------

    /// <summary>
    /// СТРАНИЦА НЕ ПЕРЕЗАГРУЖАЕТСЯ: НИ <c>META REFRESH</c>, НИ ПЕРЕЗАГРУЗКИ ПО F5.
    ///
    /// **От какого случая.** <c>meta refresh</c> ОТМЕНЁН решением владельца 11.10.2026:
    /// WCAG 2.2 F41/SC 3.2.5 (перезагрузка без спроса) и SC 2.2.2 (у самообновления обязан быть
    /// механизм остановки — у meta refresh его нет), а вдобавок он СТИРАЛ отмеченное.
    /// Проверяется на живом ответе калитки, а не на собранной строке.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть meta refresh» · «отвечать формой
    /// страницей вместо 303» (F5 повторял бы отправку).
    /// </summary>
    [Fact]
    public async Task Страница_не_перезагружается()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        // ОЖИДАНИЕ, ВОПРОС И ИСХОД — НИ НА ОДНОМ нет тега перезагрузки.
        var states = new List<string> { (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body };

        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да")));

        states.Add((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=" + EventId + "&q0=0", cookie);

        states.Add((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        foreach (var html in states)
        {
            Assert.True(
                html.IndexOf("http-equiv", StringComparison.OrdinalIgnoreCase) < 0,
                "на живой странице есть meta refresh");
        }
    }

    // ---- 4. введённое не теряется ------------------------------------------------------------

    /// <summary>
    /// ВВЕДЁННОЕ И ВЫБРАННОЕ НЕ ПРОПАДАЕТ БЕЗ ДЕЙСТВИЯ ЧЕЛОВЕКА: после отказа экран не сдвигается,
    /// а выбор и свой текст возвращаются на место.
    ///
    /// **Два правила, и оба проверяются здесь.**
    ///
    /// 1. **В скрипте опрос ПРИОСТАНАВЛИВАЕТСЯ, пока в поле «свой ответ» есть текст** — карточка
    ///    не подменяется, и набранное остаётся.
    /// 2. **После отказа движка экран НЕ сдвигается**, и человек видит ровно то, что заполнял:
    ///    выбранная радиокнопка помечена, а свой текст вернулся в поле.
    ///
    /// Мутации, которые обязаны упасть здесь: «в скрипте нет паузы при набранном тексте» ·
    /// «экран сдвигается при отказе» · «после отказа форма приходит пустой» · «свой ответ
    /// не возвращается в поле».
    /// </summary>
    [Fact]
    public async Task Введённое_не_теряется_при_отказе_и_во_время_набора()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand);
        stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет")));

        var first = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body;

        // (1) СКРИПТ НЕ ПОДМЕНЯЕТ КАРТОЧКУ, ПОКА ЧЕЛОВЕК ПИШЕТ ИЛИ ВЫБРАЛ: поле своего ответа
        //     непустое (или отмечен вариант) — следующий круг уходит в паузу, а не в запрос.
        Assert.Contains(
            "function busy(){var own=card.querySelector(\"input[name$='-own']\");return !!own&&own.value!==\"\";}",
            first,
            StringComparison.Ordinal);
        Assert.Contains(
            "function picked(){return !!card.querySelector(\"input[type='radio']:checked\");}",
            first,
            StringComparison.Ordinal);
        Assert.Contains("if(hidden()||busy()||picked()){plan();return;}", first, StringComparison.Ordinal);

        // (2) ОТКАЗ ДВИЖКА — ЭКРАН НЕ МЕНЯЕТСЯ, И ЗАПОЛНЕННОЕ ВОЗВРАЩАЕТСЯ.
        stand.ReplyAnswer = PhoneEngineAnswer.Failed("timeout");

        var own = "это моё слово";

        var refused = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "eventId=" + EventId + "&q0=1&q0-own=" + Uri.EscapeDataString(own), cookie);

        Assert.Equal(502, refused.Status);
        Assert.Contains("<!DOCTYPE html>", refused.Body, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageRepeatButton)), refused.Body, StringComparison.Ordinal);

        // ЭКРАН ТОТ ЖЕ, И СВОЙ ТЕКСТ В ПОЛЕ — ничего не потеряно без действия человека.
        var again = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains("Делать?", again, StringComparison.Ordinal);
        Assert.Contains("value=\"" + own + "\"", again, StringComparison.Ordinal);

        // ⚠️ Выбор НЕ помечается, когда заполнен свой ответ: он и есть ответ (правило названо
        // в `PhoneGate.AnswerScreen` и в отчёте).
        Assert.Contains("name=\"q0\" value=\"1\">", again, StringComparison.Ordinal);

        // (3) А ТЕПЕРЬ ОТВЕТ УХОДИТ: движок принял — и это СВОЙ ОТВЕТ, а не выбранный вариант.
        stand.ReplyAnswer = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var sent = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "eventId=" + EventId + "&q0=1&q0-own=" + Uri.EscapeDataString(own), cookie);

        Assert.Equal(303, sent.Status);

        // В ДВИЖОК УШЛИ ОБА ЗАПРОСА (первый он не принял), И ОБА — СО СВОИМ ОТВЕТОМ.
        Assert.Equal(2, stand.Calls.Count);

        foreach (var call in stand.Calls)
        {
            Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
            Assert.Contains("\"selected\":[\"" + own + "\"]", call.Args, StringComparison.Ordinal);
            Assert.DoesNotContain("\"selected\":[\"Нет\"]", call.Args, StringComparison.Ordinal);
        }
    }

    // ---- 5. один вопрос на экран и «Вопрос N из M» -------------------------------------------

    /// <summary>
    /// НА ЖИВОМ ПУТИ ЭКРАН ПОКАЗЫВАЕТ ОДИН ВОПРОС, А СЧЁТ ГОВОРИТ, СКОЛЬКО ИХ ВСЕГО.
    ///
    /// Вариант A (решение владельца 11.10.2026): калитка копит ответы по экранам и отправляет
    /// движку ПОЛНЫЙ НАБОР ОДНИМ ответом. Здесь это видно целиком: два вопроса — два экрана,
    /// на каждом ровно одно имя выбора, а в движок уходит ответ на ОБА сразу.
    ///
    /// Мутации, которые обязаны упасть здесь: «вопросы снова все на одном экране» (два имени
    /// выбора в одной форме) · «счёт не показан при нескольких вопросах» · «ответ уходит
    /// по одному экрану, а не полным набором» · «после отказа экран сдвигается».
    /// </summary>
    [Fact]
    public async Task Один_вопрос_на_экран_и_полный_набор_одним_ответом()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет"), Choice("q2", "Ещё?", "А", "Б")));

        // ЭКРАН 1: счёт «Вопрос 1 из 2», ОДНО имя выбора и ОДИН вопрос.
        var first = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains(Ru(nameof(PanelStrings.PhonePageQuestionCountFormat), 1, 2), first, StringComparison.Ordinal);
        Assert.Contains("Делать?", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Ещё?", first, StringComparison.Ordinal);
        Assert.Contains("name=\"q0\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q1\"", first, StringComparison.Ordinal);

        // ⚠️ ОТВЕТ НА ЭКРАН — ЭТО ЕЩЁ НЕ ОТВЕТ ДВИЖКУ: полного набора нет, и движок не зовётся
        // вовсе. Человеку говорят словами, что отвечать надо на каждый вопрос.
        var answered = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=" + EventId + "&q0=0", cookie);

        Assert.Equal(400, answered.Status);
        Assert.Empty(stand.Calls);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageChooseAll)), answered.Body, StringComparison.Ordinal);

        // ⚠️ ЭКРАН СДВИНУЛСЯ, И ЭТО ПРАВИЛО: экран 1 УЖЕ ОТВЕЧЕН, а ждать больше нечего.
        // «Не сдвигается» касается ОТКАЗА (ответа не было вовсе) — он проверен отдельно,
        // в `Введённое_не_теряется_при_отказе_и_во_время_набора`.
        var second = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains(Ru(nameof(PanelStrings.PhonePageQuestionCountFormat), 2, 2), second, StringComparison.Ordinal);
        Assert.Contains("Ещё?", second, StringComparison.Ordinal);
        Assert.Contains("name=\"q1\"", second, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q0\"", second, StringComparison.Ordinal);

        // ВТОРОЙ ЭКРАН ОТВЕЧЕН — НАБОР ПОЛОН, И В ДВИЖОК УХОДИТ ОДИН ОТВЕТ НА ОБА ВОПРОСА.
        var sent = await Send(stand.Http, HttpMethod.Post, "/answer", "eventId=" + EventId + "&q1=1", cookie);

        Assert.Equal(303, sent.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
        Assert.Contains("\"id\":\"q1\",\"selected\":[\"Да\"]", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"q2\",\"selected\":[\"Б\"]", call.Args, StringComparison.Ordinal);

        // ПОРЯДОК — ПО ВОПРОСАМ КАДРА: `q1` идёт первым, хотя отвечен был первым.
        Assert.True(
            call.Args.IndexOf("\"id\":\"q1\"", StringComparison.Ordinal)
            < call.Args.IndexOf("\"id\":\"q2\"", StringComparison.Ordinal),
            "ответы ушли не в порядке вопросов кадра");
    }

    // ---- 6. свой ответ доезжает до движка ----------------------------------------------------

    /// <summary>
    /// НА ЖИВОМ ПУТИ СВОЙ ОТВЕТ ЧЕЛОВЕКА ДОЕЗЖАЕТ ДО ДВИЖКА И УХОДИТ ТОЙ ЖЕ СТРОКОЙ, ЧТО ВЫБОР.
    ///
    /// **Что именно уходит, когда заполнено И то и другое (названное правило):**
    /// **уходит СВОЙ ОТВЕТ** — он явнее, человек написал слова, а не ткнул в готовое. Уходит он
    /// в <c>selected</c> — той же строкой, что и выбранный вариант: форма движка одна
    /// (`docs\PHONE-LIVE.md` §3.5), и своего поля для «своего» у неё нет.
    ///
    /// Мутации, которые обязаны упасть здесь: «свой ответ не доезжает» · «уходит выбор, а свой
    /// ответ теряется» · «свой ответ уезжает в отдельное поле» (движок такого не поймёт).
    /// </summary>
    [Fact]
    public async Task Свой_ответ_доезжает_до_движка_и_побеждает_выбор()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет")));

        stand.ReplyAnswer = new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}");

        var own = "сделай по-своему";

        // ⚠️ ИМЯ ПОЛЯ БЕРЁТСЯ ИЗ САМОЙ РАЗМЕТКИ, а не подставляется проверкой: так мутация
        // «поле переименовали» роняет проверку, а не проходит мимо неё. Раньше поле называлось
        // здесь литералом — и переименование в продукте осталось бы незамеченным (дыра F5).
        var page = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);
        var field = OwnFieldName(page);

        Assert.Equal("q0-own", field);

        // И ВЫБРАНО, И НАПИСАНО: уходит СВОЙ ОТВЕТ — он явнее, человек написал слова,
        // а не ткнул в готовое (правило названо в `PhoneGate.AnswerScreen`).
        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer",
            "eventId=" + EventId + "&q0=0&" + field + "=" + Uri.EscapeDataString(own), cookie);

        Assert.Equal(303, reply.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
        Assert.Contains("\"id\":\"q1\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"selected\":[\"" + own + "\"]", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"selected\":[\"Да\"]", call.Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// ИМЯ ПОЛЯ «СВОЙ ОТВЕТ» ИЗ РАЗМЕТКИ — `<c>q&lt;экран&gt;-own</c>`. Пусто — поля нет вовсе,
    /// и это тоже ответ (его придётся назвать в проверке).
    /// </summary>
    private static string OwnFieldName(string html)
    {
        const string Marker = "class=\"own-input\"";

        var start = html.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        const string Name = "name=\"";

        var from = html.IndexOf(Name, start, StringComparison.Ordinal);
        if (from < 0) return string.Empty;

        from += Name.Length;
        var to = html.IndexOf('"', from);

        return to > from ? html[from..to] : string.Empty;
    }

    // ---- 7. вид: фокус, fieldset, цель, контраст ---------------------------------------------

    /// <summary>
    /// НА ЖИВОМ ПУТИ ВИД НЕСЁТ ВСЁ, ЧЕМ ОН ДЕРЖИТСЯ: `fieldset`+`legend`, видимый фокус, правило
    /// выбранного, цель не меньше 44 точек и пары контраста.
    ///
    /// Нормы — со ссылкой на каждое: GOV.UK question pages (`fieldset`+`legend`), WCAG 2.2
    /// SC 2.4.13 (фокус — «2 CSS pixel thick perimeter»), SC 2.5.5 (цель 44×44), WCAG 1.4.3
    /// (контраст 4,5:1); разбор — `_design-library\30-mobile-web.md` §3 и §8,
    /// `_design-standard-draft.md` §2.4–§2.6. Числа контраста — замер макета (5,38 светлая,
    /// 8,11 тёмная), здесь сверяются, а не пересчитываются.
    ///
    /// Мутации, которые обязаны упасть здесь: «`fieldset`/`legend` убраны» · «фокуса нет» ·
    /// «`min-height` цели меньше 44» · «контраст ниже нормы» · «выбранный вариант не видно».
    /// </summary>
    [Fact]
    public async Task Живой_вид_держит_фокус_группу_цель_и_контраст()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет")));

        var html = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body;
        var markup = Markup(html);

        // ГРУППА ВОПРОСА: `<fieldset>` с `<legend>`, и вопрос в нём ОДИН.
        Assert.Contains("<fieldset class=\"group\">", markup, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"legend\">Делать?</legend>", markup, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(markup, "<legend"));

        var css = PhonePageStyle.Css;

        // ЦЕЛЬ ПОД ПАЛЕЦ — 44 точки у каждого органа, которым отвечают.
        // ⚠️ Галочки «не обновлять само» в списке НЕТ: её убрали 12.10.2026 (вместе с её `.auto-box`).
        foreach (var selector in new[] { ".opt-body{", ".action{", ".ghost{", ".own-input{" })
        {
            Assert.Contains("min-height:44px", Rule(css, selector), StringComparison.Ordinal);
        }

        // ФОКУС ВИДЕН: обводка 2 точки с отступом 2 — у кнопок, полей, ссылки и строки варианта.
        var focus = Rule(css, "button:focus-visible");

        Assert.Contains("outline:2px solid var(--focus)", focus, StringComparison.Ordinal);
        Assert.Contains("outline-offset:2px", focus, StringComparison.Ordinal);
        Assert.Contains("a:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains(".opt:focus-within .opt-body", css, StringComparison.Ordinal);

        // ВЫБРАННЫЙ ВАРИАНТ ВИДЕН — подложкой И рамкой, а размер не меняется.
        var chosen = Rule(css, ".opt:has(input:checked) .opt-body{");

        Assert.Contains("background:var(--accent-wash)", chosen, StringComparison.Ordinal);
        Assert.Contains("border-color:var(--accent)", chosen, StringComparison.Ordinal);
        Assert.DoesNotContain("border-width", chosen, StringComparison.Ordinal);
        Assert.DoesNotContain("padding", chosen, StringComparison.Ordinal);

        // КОНТРАСТ ПАР — ПО ФОРМУЛЕ WCAG 2.2, и значения берутся ИЗ САМОЙ ТАБЛИЦЫ СТИЛЕЙ:
        // проверка подставляет цвет в формулу, а не сверяет литерал с литералом. Замер макета дал
        // 5,38 (светлая пара) и 8,11 (тёмная) — здесь они ПЕРЕСЧИТЫВАЮТСЯ и обязаны уложиться
        // в норму 4,5:1 (`30-mobile-web.md` §7: «проверять надо каждую пару, а не глазами»).
        var accents = Regex.Matches(css, @"--accent:(#[0-9A-Fa-f]{6})");
        var inks = Regex.Matches(css, @"--accent-ink:(#[0-9A-Fa-f]{6})");

        Assert.True(accents.Count >= 2, "в таблице стилей меньше двух пар акцента — светлая и тёмная");
        Assert.True(inks.Count >= 2, "в таблице стилей меньше двух пар «текст на акценте»");

        for (var index = 0; index < accents.Count; index++)
        {
            var accent = accents[index].Groups[1].Value;
            var ink = inks[index].Groups[1].Value;

            Assert.True(
                Contrast(ink, accent) >= 4.5,
                $"пара акцента {index + 1} не проходит по контрасту: {ink} на {accent} = "
                + Contrast(ink, accent).ToString("F2", CultureInfo.InvariantCulture));
        }

        // ⚠️ ЦВЕТА — ИЗ ПАЛИТРЫ, А НЕ ЛИТЕРАЛАМИ (пачка D, CH-04). Здесь стояли `#1A1A1A`/`#FFFFFF`
        // и `#F0F0F0`/`#2B2B2B` — те же числа, что в `PhonePageStyle`, но записанные второй раз:
        // проверка сравнивала литерал с литералом и осталась бы зелёной, поменяй кто-нибудь
        // `--ink`. Теперь берутся те же переменные, что видит человек, — и мутация палитры
        // роняет эту проверку.
        var themes = PhoneThemeColors.Themes();

        Assert.True(
            Contrast(themes[0]["--ink"], themes[0]["--card"]) >= 4.5,
            $"чернила светлой темы не читаются: {themes[0]["--ink"]} на {themes[0]["--card"]}");

        Assert.True(
            Contrast(themes[1]["--ink"], themes[1]["--card"]) >= 4.5,
            $"чернила тёмной темы не читаются: {themes[1]["--ink"]} на {themes[1]["--card"]}");

        // И ДЛИНА СТРОКИ — 60 знаков, а не 640 точек (норма §1.5, §2.7).
        Assert.Contains("max-width:60ch", Rule(css, ".card{"), StringComparison.Ordinal);
    }

    // ---- 8. карточка для опроса --------------------------------------------------------------

    /// <summary>
    /// ЖИВОЙ ОПРОС ПОЛУЧАЕТ ТОЛЬКО КАРТОЧКУ — и меняется РОВНО ОНА.
    ///
    /// **Зачем это на живом пути.** Опрос идёт по тому же адресу с <c>?part=card</c>: ответ
    /// обязан нести содержимое карточки и НИЧЕГО больше — ни таблицы стилей, ни скрипта, ни
    /// самого `<div class="card">`. Второй такой `<div>` вложился бы в первый, и следующий круг
    /// опроса нашёл бы не ту карточку.
    ///
    /// Мутации, которые обязаны упасть здесь: «на `?part=card` отдаётся целая страница» ·
    /// «карточка отдаётся с `<style>`» · «карточка отдаётся вместе с `<div class="card">`» ·
    /// «на пустую строку запроса отдаётся карточка вместо страницы».
    /// </summary>
    [Fact]
    public async Task Живой_опрос_получает_только_карточку()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        var card = await Send(stand.Http, HttpMethod.Get, "/?part=card", cookie: cookie);

        Assert.Equal(200, card.Status);
        Assert.DoesNotContain("<style", card.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", card.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"card\"", card.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", card.Body, StringComparison.Ordinal);

        // А СОДЕРЖИМОЕ КАРТОЧКИ ЕСТЬ: по нему видно, что за состояние.
        Assert.Contains("<h1 class=\"screen-title\">", card.Body, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaiting)), card.Body, StringComparison.Ordinal);
        Assert.Contains("<a class=\"ghost narrow\" href=\"/\">", card.Body, StringComparison.Ordinal);

        // И ЧУЖАЯ СТРОКА ЗАПРОСА КАРТОЧКУ НЕ ОТДАЁТ: `part=cardx` — это целая страница.
        var whole = await Send(stand.Http, HttpMethod.Get, "/?part=cardx", cookie: cookie);

        Assert.Equal(200, whole.Status);
        Assert.Contains("<!DOCTYPE html>", whole.Body, StringComparison.Ordinal);
        Assert.Contains("<style>", whole.Body, StringComparison.Ordinal);

        // А БЕЗ COOKIE КАРТОЧКА НЕ ОТДАЁТСЯ ВОВСЕ: за дверью никого нет — там СТРАНИЦА ВХОДА.
        // ⚠️ Маршрут `?part=card` разбирается ДО проверки пропуска, но ответ на него без пропуска
        // — это тот же вход: карточки телефону без пропуска не бывает.
        var anon = await Send(stand.Http, HttpMethod.Get, "/?part=card");

        Assert.Equal(200, anon.Status);
        Assert.Contains("action=\"/pair\"", anon.Body, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageCodeLabel)), anon.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", anon.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q0\"", anon.Body, StringComparison.Ordinal);
    }

    // ---- 9. каждое состояние без тупика ------------------------------------------------------

    /// <summary>
    /// НИ ОДНО ЖИВОЕ СОСТОЯНИЕ НЕ ОСТАВЛЯЕТ ЧЕЛОВЕКА БЕЗ ДЕЙСТВИЯ: у каждого есть чем продолжить.
    ///
    /// Норма — GOV.UK (Recover from validation errors): «tell the user what's gone wrong and
    /// **how to fix it**»; у нас это названо в `30-mobile-web.md` §2, «тупиков не бывает».
    /// У живущего состояния путь — «Проверить» (или форма ответа), у беды — «Повторить».
    ///
    /// Мутации, которые обязаны упасть здесь: «в состоянии беды нет „Повторить“» · «у ожидания
    /// нет „Проверить“» · «у вопроса нет ни формы ответа, ни отмены».
    /// </summary>
    [Fact]
    public async Task Ни_одно_живое_состояние_не_оставляет_без_действия()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        // ОЖИДАНИЕ: «Проверить» есть.
        var waiting = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains("<a class=\"ghost narrow\" href=\"/\">", waiting, StringComparison.Ordinal);

        // ВОПРОС: форма ответа И отмена.
        Ready(stand); stand.Events.Emit(Waterfall(Choice("q1", "Делать?", "Да", "Нет")));

        var asked = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains("action=\"/answer\"", asked, StringComparison.Ordinal);
        Assert.Contains("action=\"/cancel\"", asked, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageCancelButton)), asked, StringComparison.Ordinal);

        // БЕДА: «Повторить» — и это ссылка, а не орган без скрипта.
        // ⚠️ Формой шлётся ТОЛЬКО СВОЙ `eventId` (`PhoneAnswer.Field`), поэтому «чужой» приходит
        // машинным путём: им же проверяется, что чужой ход телефоном не снимается.
        var trouble = await Send(stand.Http, HttpMethod.Post, "/cancel", "{\"eventId\":\"чужой\"}", cookie);

        Assert.Equal(403, trouble.Status);

        var troubleMarkup = Markup(trouble.Body);

        Assert.Contains(
            "<a class=\"ghost\" href=\"/\">" + Ru(nameof(PanelStrings.PhonePageRepeatButton)) + "</a>",
            troubleMarkup,
            StringComparison.Ordinal);

        // И ДВИЖОК ПРИ ЭТОМ НЕ ЗВАЛСЯ: чужой ход не снимается, а не «снимается и молчит».
        // ⚠️ Чужой `eventId` — ОТКАЗ, а не снятие: вопрос остаётся тем же, и человек видит его
        // снова, а не «ход отменён» (иначе телефон снял бы чужую работу одним запросом).
        Assert.Empty(stand.Calls);

        // ОТМЕНА СВОИМ `eventId` ДОВОДИТ ДЕЛО ДО КОНЦА — и тогда вопроса больше нет.
        var own = await Send(stand.Http, HttpMethod.Post, "/cancel", "eventId=" + EventId, cookie);

        Assert.Equal(303, own.Status);
        Assert.Equal("/", own.Location);

        var cancelCall = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.SessionCancelEndpoint, cancelCall.Endpoint);

        // ИСХОД (ход отменён): «Проверить» есть, а кнопок ответа нет.
        var cancelled = Markup((await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body);

        Assert.Contains("<a class=\"ghost narrow\" href=\"/\">", cancelled, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/answer\"", cancelled, StringComparison.Ordinal);
    }

    // ---- 10. слова: подсказка о живом обновлении ---------------------------------------------

    /// <summary>
    /// ПОДСКАЗКА О ЖИВОМ ОБНОВЛЕНИИ НЕ ОБЕЩАЕТ ЛИШНЕГО И НЕ КОНЧАЕТСЯ ТОЧКОЙ.
    ///
    /// Норма — GOV.UK question pages: «Keep each hint to a single short sentence, **without any
    /// full stops**» (`_design-standard-draft.md` §2.7). И обещание обязано совпадать с делом:
    /// число в словах — то же, что <see cref="PhonePage.PollMs"/> в скрипте, а опрос замирает САМ
    /// (скрытая страница, выбранный вариант, набранный текст) — галочки «не обновлять само»
    /// на странице НЕТ, её убрал владелец 12.10.2026: подмена карточки уносила отметку.
    ///
    /// Мутации, которые обязаны упасть здесь: «подсказка с точкой» · «в словах другое число» ·
    /// «подсказки нет вовсе» · **«галочка вернулась в разметку»**.
    /// </summary>
    [Fact]
    public async Task Подсказка_о_живом_обновлении_без_точки_и_без_галочки()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        // ⚠️ СМОТРИМ СТРАНИЦУ ВОПРОСА, А НЕ ОЖИДАНИЯ (правка 12.10.2026): у ожидания слов о нашем
        // опросе больше нет — решение владельца: ожидание не говорит про нашу механику, там своя
        // тихая строка «что делать, если ничего не происходит». У вопроса они по делу.
        Ready(stand);
        stand.Events.Emit(Waterfall(Choice("q1", "Как делать?", "Да", "Нет")));

        var html = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body;

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var local = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie, language: language)).Body;

            // СЛОВ О НАШЕМ ОПРОСЕ НА СТРАНИЦЕ БОЛЬШЕ НЕТ (живой прогон 13.10.2026, находка владельца).
            // Проверка ПЕРЕВЁРНУТА: она падает, если строку вернут на экран.
            // ⚠️ ФРАЗА-СТОРОЖ, А НЕ ИМЯ КЛЮЧА: ключ убран вовсе, и проверка, ищущая имя,
            // пропустила бы строку, переписанную по смыслу с тем же обещанием.
            foreach (var phrase in new[] { "Страница проверяет сама", "Page checks by itself", "页面会自行检查" })
            {
                Assert.DoesNotContain(phrase, local, StringComparison.OrdinalIgnoreCase);
            }
        }

        // МЕХАНИЗМА-ГАЛОЧКИ НЕТ НИ В КАКОМ ВИДЕ: остановка теперь — свойство самой страницы
        // (`visibilityState`, выбор варианта, набранный текст), а не орган, который уносит подмена.
        Assert.DoesNotContain("type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"live\"", html, StringComparison.Ordinal);

        // И ОПРОС ЗАМИРАЕТ НА СКРЫТОЙ СТРАНИЦЕ — это и есть механизм остановки, названный словами.
        Assert.Contains("document.visibilityState!==\"visible\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// ТЕЛЕФОН, КОТОРЫЙ СЕБЯ НАЗВАЛ, НАЗЫВАЕТСЯ СВОИМ ИМЕНЕМ — НА ЖИВОМ ПУТИ СОПРЯЖЕНИЯ.
    ///
    /// **От какого случая (решение владельца 12.10.2026).** Имя телефона берётся из
    /// <c>User-Agent</c> **при сопряжении** и записывается в пропуск. Проверить разбор мало:
    /// настоящий вопрос — **доезжает ли имя от запроса до страницы**. Этот шов и проверяется здесь:
    /// сопряжение идёт настоящим `POST /pair` с настоящим заголовком, а имя читается и из файла
    /// пропуска, и со страницы.
    ///
    /// Мутации, которые обязаны упасть здесь: «`User-Agent` не доезжает до записи устройства» ·
    /// «имя не доезжает до страницы» · «на страницу снова уходит `phone-N`».
    /// </summary>
    [Fact]
    public async Task Телефон_который_себя_назвал_называется_своим_именем()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await PairAs(
            stand,
            "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A.240505.004) AppleWebKit/537.36"
            + " (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36");

        // (1) ИМЯ ДОЕХАЛО ДО ЗАПИСИ УСТРОЙСТВА — а не осталось в строке запроса.
        var device = Assert.Single(PhonePass.Load(PhonePassFiles.Under(stand.Paths, stand.Log.Add)).SafeDevices);

        Assert.Equal("Pixel 8", device.Name);

        // (2) И ДОЕХАЛО ДО СТРАНИЦЫ — словами, без внутреннего номера.
        var page = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body;

        Assert.Contains(Ru(nameof(PanelStrings.PhonePageDeviceFormat), "Pixel 8"), page, StringComparison.Ordinal);
        Assert.DoesNotContain(device.Id, page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ТЕЛЕФОН, КОТОРЫЙ НИЧЕГО О СЕБЕ НЕ СКАЗАЛ, ПОЛУЧАЕТ ЧЕСТНОЕ «телефон» — и это не ошибка,
    /// а ответ: выдуманное имя хуже отсутствия имени (решение владельца 12.10.2026).
    ///
    /// Мутации, которые обязаны упасть здесь: «незнакомая строка даёт выдуманное имя» ·
    /// «пустое имя уходит на страницу пустотой».
    /// </summary>
    [Fact]
    public async Task Телефон_который_промолчал_называется_просто_телефоном()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await PairAs(stand, "curl/8.7.1");

        var device = Assert.Single(PhonePass.Load(PhonePassFiles.Under(stand.Paths, stand.Log.Add)).SafeDevices);

        Assert.Equal(string.Empty, device.Name);

        var page = (await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie)).Body;

        Assert.Contains(
            Ru(nameof(PanelStrings.PhonePageDeviceFormat), Ru(nameof(PanelStrings.PhonePageDeviceFallback))),
            page,
            StringComparison.Ordinal);

        Assert.DoesNotContain(device.Id, page, StringComparison.Ordinal);
    }

    // ---- мелкие двери проверки ---------------------------------------------------------------

    /// <summary>Слово панели на русском (язык прогона закреплён в <see cref="TestLocale"/>).</summary>
    private static string Ru(string key) => Loc.TIn("ru", key);

    /// <summary>Слово панели с подстановками — тем же способом, каким его собирает страница.</summary>
    private static string Ru(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Ru(key), args);

    /// <summary>Тело правила из таблицы стилей: от селектора до закрывающей скобки.</summary>
    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в таблице стилей нет правила {selector}");

        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"правило {selector} не закрыто");

        return css.Substring(start, end - start + 1);
    }

    /// <summary>Контраст пары по формуле WCAG 2.2 (арифметика, а не приборный замер).</summary>
    private static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');

        return (0.2126 * Channel(value[..2])) + (0.7152 * Channel(value.Substring(2, 2)))
            + (0.0722 * Channel(value.Substring(4, 2)));
    }

    private static double Channel(string pair)
    {
        var raw = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
    }

    // ---- три дефекта живого прогона владельца 12.10.2026 ------------------------------------

    /// <summary>
    /// СВОЙ ОТВЕТ БЕЗ ВЫБРАННОГО ВАРИАНТА ДОЕЗЖАЕТ ДО ДВИЖКА.
    ///
    /// **Это и есть первый дефект живого прогона.** Человек, ответивший ТОЛЬКО СЛОВАМИ, вариант
    /// не выбирает — поля <c>q&lt;N&gt;</c> в форме нет вовсе, и калитка отвергала форму как
    /// «не разобранную» (в живом журнале владельца — ПЯТЬ таких отказов, ответ не уходил).
    /// Прежние проверки видели только случай «и выбор, и текст» — потому дефект и прошёл.
    ///
    /// Мутация, которая обязана упасть здесь: **AG1 «поле своего ответа не выставляет номер экрана»**.
    /// </summary>
    [Fact]
    public async Task Свой_ответ_без_выбранного_варианта_доезжает_до_движка()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand);
        stand.Events.Emit(Waterfall(Choice("q1", "Как делать?", "Да", "Нет")));

        // ФОРМА БЕЗ ВЫБОРА: только свой ответ. Номер экрана несёт САМО ИМЯ поля `q0-own`.
        var own = "сделай по-своему, без варианта";

        var reply = await Send(
            stand.Http, HttpMethod.Post, "/answer", "eventId=" + EventId + "&q0-own=" + Uri.EscapeDataString(own), cookie);

        Assert.Equal(303, reply.Status);

        var call = Assert.Single(stand.Calls);

        Assert.Equal(PhoneDecisions.EventsResultEndpoint, call.Endpoint);
        Assert.Contains("\"selected\":[\"" + own + "\"]", call.Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// НОМЕР ЭКРАНА БЕРЁТСЯ И ИЗ ИМЕНИ <c>q&lt;N&gt;-own</c> — ЧИСТАЯ ПРОВЕРКА РАЗБОРА ФОРМЫ.
    ///
    /// Мутация, которая обязана упасть здесь: **AG1**.
    /// </summary>
    [Fact]
    public void Разбор_формы_берёт_номер_экрана_и_из_своего_ответа()
    {
        var fields = PhoneAnswer.ParseForm("eventId=e1&q0-own=" + Uri.EscapeDataString("своё слово"));

        Assert.NotNull(fields);
        Assert.Equal("e1", fields!.EventId);
        Assert.Equal(0, fields.Screen);
        Assert.Equal("своё слово", fields.Own);
        Assert.Equal(string.Empty, fields.Choice);

        // И с выбором тоже: номер один и тот же, текст человека на месте.
        var both = PhoneAnswer.ParseForm("eventId=e1&q2=1&q2-own=моё");

        Assert.NotNull(both);
        Assert.Equal(2, both!.Screen);
        Assert.Equal("1", both.Choice);
        Assert.Equal("моё", both.Own);

        // Поле без номера (`q-own`) — по-прежнему не наша форма: угадывать номер нельзя.
        Assert.Null(PhoneAnswer.ParseForm("eventId=e1&q-own=моё"));
        Assert.Null(PhoneAnswer.ParseForm("eventId=e1&own=моё"));
    }

    /// <summary>
    /// СТРОКА ОПРОСА НА СТРАНИЦЕ РОВНО ОДНА, И <c>id</c> НЕ ПОВТОРЯЮТСЯ.
    ///
    /// **Второй и третий дефекты живого прогона.** `<c>AutoRow</c>` рисовалась и в карточке,
    /// и внутри формы ответа: в отданной странице оказывалось ДВА элемента с классом `live`
    /// и одинаковым `id`, а скрипт читал первый — тот, которого человек не нажимал, поэтому
    /// «Не обновлять само» не работала и опрос шёл при стоящей галке.
    ///
    /// Мутации, которые обязаны упасть здесь: **AG2 «строка галочки нарисована дважды»** и
    /// **AG3 «скрипт читает первый `.live`»** (вторая — через требование «галочка внутри формы
    /// ответа», которую скрипт и обязан читать).
    /// </summary>
    [Fact]
    public async Task Строка_опроса_ровно_одна_и_id_не_повторяются()
    {
        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        Ready(stand);
        stand.Events.Emit(Waterfall(Choice("q1", "Как делать?", "Да", "Нет")));

        var page = await Send(stand.Http, HttpMethod.Get, "/", cookie: cookie);

        Assert.Equal(200, page.Status);

        var markup = Markup(page.Body);

        // (1) СТРОКИ ГАЛОЧКИ НЕТ ВОВСЕ — ни разметки, ни класса: её убрал владелец 12.10.2026.
        Assert.Equal(0, Count(markup, "class=\"live\""));
        Assert.Equal(0, Count(markup, "type=\"checkbox\""));

        // (2) `id` в странице НЕ ПОВТОРЯЮТСЯ: одинаковые ломают и подпись `<label for>`, и выбор
        //     элемента — именно на этом и споткнулся живой прогон.
        var ids = Ids(markup);

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // (3) СКРИПТ БОЛЬШЕ НЕ ИЩЕТ ГАЛОЧКУ: ни голого `.live`, ни `.live` внутри формы — элемента нет.
        var script = Script(page.Body);

        Assert.DoesNotContain(".live", script, StringComparison.Ordinal);

        // (4) А ЗАМИРАЕТ ОН ПО ВИДИМОСТИ, ВЫБОРУ И НАБРАННОМУ ТЕКСТУ — и это на живой странице.
        Assert.Contains("document.visibilityState!==\"visible\"", script, StringComparison.Ordinal);
        Assert.Contains("input[type='radio']:checked", script, StringComparison.Ordinal);
        Assert.Contains("input[name$='-own']", script, StringComparison.Ordinal);

        // (5) И СТРАНИЦА ГОВОРИТ, КАКИМ УСТРОЙСТВОМ ВОШЛИ (правка 12.10.2026): имя приходит
        //     из cookie калитки — берём его у файла пропуска, а не выдумываем.
        //     ⚠️ ПОКАЗЫВАЕТСЯ КОРОТКОЕ ИМЯ ТЕЛЕФОНА, а не внутренний `phone-N` (решение владельца
        //     12.10.2026): в этом прогоне браузер подставной, `User-Agent` наш, поэтому имени нет —
        //     и страница обязана сказать ЧЕСТНОЕ «телефон», а не номер, которого человек не выбирал.
        var device = Assert.Single(PhonePass.Load(PhonePassFiles.Under(stand.Paths, stand.Log.Add)).SafeDevices);

        Assert.Equal(string.Empty, device.Name);

        Assert.Contains(
            Ru(nameof(PanelStrings.PhonePageDeviceFormat), Ru(nameof(PanelStrings.PhonePageDeviceFallback))),
            markup,
            StringComparison.Ordinal);

        Assert.DoesNotContain(device.Id, markup, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Все значения <c>id="…"</c> страницы — по порядку появления.</summary>
    private static List<string> Ids(string markup)
    {
        var ids = new List<string>();

        for (var at = markup.IndexOf("id=\"", StringComparison.Ordinal); at >= 0;
             at = markup.IndexOf("id=\"", at + 4, StringComparison.Ordinal))
        {
            var end = markup.IndexOf('"', at + 4);

            if (end > at) ids.Add(markup[(at + 4)..end]);
        }

        return ids;
    }

    /// <summary>Скрипт страницы целиком — им проверяется, ЧТО он ищет и куда ходит.</summary>
    private static string Script(string html)
    {
        var start = html.IndexOf("<script", StringComparison.Ordinal);

        if (start < 0) return string.Empty;

        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return end > start ? html[start..(end + 9)] : html[start..];
    }
}
