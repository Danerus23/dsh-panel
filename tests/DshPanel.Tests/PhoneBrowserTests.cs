using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЖИВОЙ БРАУЗЕРНЫЙ ПРОГОН СТРАНИЦЫ ТЕЛЕФОНА — проверка, которой в воротах не было.
///
/// **Зачем она.** Живой прогон владельца 12.10.2026 нашёл на странице три дефекта, и ни один
/// из них не видел HTTP-клиент на петле: он разметку НЕ РАЗБИРАЕТ. А сломался именно разбор —
/// два элемента с одним `id`, строка опроса вне формы ответа, скрипт, читающий не тот элемент,
/// который нажимает человек. Браузерный движок разбирает разметку так же, как телефон человека,
/// поэтому проверка идёт им: **Edge или Chrome в безголовом режиме**, через отладочный протокол
/// (CDP) — им же ставится cookie устройства, снимается DOM и считаются запросы опроса.
///
/// ⚠️ **Браузера нет — проверка ПАДАЕТ словами, а не пропускается.** Молчаливый пропуск сделал бы
/// ворота слепыми ровно к этому классу дефектов, а он уже стоил живого прогона.
///
/// ⚠️ **Калитка поднимается ТОЛЬКО на петле и на свободном порту** (3080/3081/43127 не занимаются
/// никогда) — правило, найденное живьём: прежний стенд слушал сетевой адрес машины, и Windows
/// завела от этого два широких правила брандмауэра.
/// </summary>
public class PhoneBrowserTests
{
    /// <summary>Адрес, на котором стоит калитка, — ПЕТЛЯ, и никакой другой.</summary>
    private const string Loop = "127.0.0.1";

    private const string EventId = "event-browser-1";

    [Fact]
    public async Task Браузер_видит_страницу_без_галочки_и_опрос_идёт()
    {
        var browser = Browser();

        Assert.False(
            browser is null,
            "браузера нет — проверить нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", BrowserPaths()) + ")");

        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-browser\"}}");
        stand.Events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        // (1) РАЗБОР РАЗМЕТКИ БРАУЗЕРОМ, А НЕ СТРОКОЙ: страница отдаётся калиткой как есть.
        var page = await stand.Http.GetStringAsync("/?x=1", TestContext.Current.CancellationToken);

        Assert.Contains("class=\"card\"", page, StringComparison.Ordinal);

        var profile = Path.Combine(Path.GetTempPath(), "dsh-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new BrowserRun(browser!, profile);

        try
        {
            await live.WaitForDevTools();

            // COOKIE УСТРОЙСТВА — через тот же протокол: страница без него отдаёт вход, и мерить
            // на ней разметку вопроса было бы нечем.
            await live.Cookie(PhoneGate.CookieName, cookie);

            await live.Enable();

            await live.Navigate(Url(stand.Port));

            // (2) DOM ЖИВОЙ СТРАНИЦЫ: ГАЛОЧКИ НЕТ ВООБЩЕ (её убрал владелец 12.10.2026 — подмена
            //     карточки уносила отметку), `id` уникальны, поле своего ответа — ВНУТРИ формы ответа.
            Assert.Equal("0", await live.Evaluate("document.querySelectorAll('.live').length"));
            Assert.Equal("0", await live.Evaluate("document.querySelectorAll('input[type=checkbox]').length"));

            var ids = await live.Evaluate(
                "JSON.stringify([...document.querySelectorAll('[id]')].map(function(e){return e.id;}))");

            var list = JsonSerializer.Deserialize<List<string>>(AsJson(ids))!;

            Assert.NotEmpty(list);
            Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());

            Assert.Equal(
                "1",
                await live.Evaluate("document.querySelectorAll('form[action=\"/answer\"] input[name$=\"-own\"]').length"));

            // И СТРАНИЦА ГОВОРИТ, КАКИМ УСТРОЙСТВОМ ВОШЛИ — прямо в DOM, а не в исходнике.
            // ⚠️ СПРАШИВАЕМ ЕГО ПО ИМЕНИ (`[data-device]`), И СПРАШИВАЕМ СТРОГО: прежняя редакция
            // звала `querySelector('.device')` и на пропавшем узле получала `''` — то есть
            // «крючок переименовали, а прогон не заметил» проходило зелёным (найдено разбором
            // `80-accessibility.md` §12). Теперь у пропажи узла нет тихого ответа.
            Assert.Equal(
                "1",
                await live.Evaluate("document.querySelectorAll('[data-device]').length"));

            var device = await live.Evaluate(
                "var e=document.querySelector('[data-device]');e===null?'НЕТ УЗЛА':e.textContent");

            Assert.False(
                device == "НЕТ УЗЛА",
                "страница не назвала устройство: узла с доводом `data-device` в DOM нет");

            // ⚠️ И НАЗЫВАЕТ ОНА ЧЕЛОВЕКА ЧЕЛОВЕЧЕСКИ (решение владельца 12.10.2026): короткое имя
            // телефона из `User-Agent`, а когда его нет — честное «телефон». Внутреннего `phone-N`
            // на странице нет ВООБЩЕ, и это проверяется здесь же: номер человек не выбирал.
            Assert.Contains(
                DshPanel.Localization.Loc.TIn("ru", "PhonePageDeviceFallback"),
                device,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("phone-", device, StringComparison.OrdinalIgnoreCase);

            // ⚠️ (2.3) РЯД СО ССЫЛКОЙ «ПРОВЕРИТЬ» СКРИПТ УБРАЛ, КОГДА ОПРОС ПОШЁЛ
            // (решение владельца 12.10.2026). Здесь это видно ФАКТОМ DOM, а не словом в исходнике:
            // страница вопроса, скрипт живой — ряда нет. Нет скрипта — ряд на месте (это стерегут
            // чистые проверки: `data-auto` есть в разметке каждого живого состояния).
            Assert.Equal("0", await live.Evaluate("document.querySelectorAll('[data-auto]').length"));
            Assert.Equal("0", await live.Evaluate("document.querySelectorAll('.auto').length"));

            // (3) ОПРОС ИДЁТ: карточка спрашивается своим же адресом.
            var beforeTick = await live.Polls(TimeSpan.FromSeconds(7));

            Assert.True(beforeTick > 0, $"опрос не пошёл вовсе: запросов «?part=card» за 7 с — {beforeTick}");
        }
        finally
        {
            live.Stop();
            TryRemove(profile);
        }
    }

    /// <summary>
    /// СКРЫТАЯ СТРАНИЦА НЕ СПРАШИВАЕТ ВООБЩЕ, А ВЕРНУВШИСЬ — СПРАШИВАЕТ СРАЗУ.
    ///
    /// **Зачем отдельной проверкой и в браузере.** «Опрос замирает, когда страница ушла с экрана» —
    /// утверждение о ПОВЕДЕНИИ браузера (`document.visibilityState`), и строкой в исходнике его
    /// не доказать: HTTP-клиент на петле разметку не разбирает, а разбор — именно то, на чём мы
    /// уже спотыкались (живой прогон 12.10.2026). Видимость подменяется ДО загрузки страницы
    /// (`Page.addScriptToEvaluateOnNewDocument`), и мерится ФАКТ запросов, а не «скрипт так написан».
    ///
    /// Мутации, которые обязаны упасть здесь: **«опрос идёт при скрытой странице»** (снята проверка
    /// видимости) · «после возврата на страницу опрос не возобновляется».
    /// </summary>
    [Fact]
    public async Task Скрытая_страница_не_спрашивает_а_вернувшись_спрашивает()
    {
        var browser = Browser();

        Assert.False(
            browser is null,
            "браузера нет — проверить нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", BrowserPaths()) + ")");

        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-browser\"}}");
        stand.Events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        var profile = Path.Combine(Path.GetTempPath(), "dsh-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new BrowserRun(browser!, profile);

        try
        {
            await live.WaitForDevTools();
            await live.Cookie(PhoneGate.CookieName, cookie);
            await live.Enable();

            // ВИДИМОСТЬ ПОДМЕНЯЕТСЯ ДО ЗАГРУЗКИ: страница рождается «скрытой», и это не «мы потом
            // отвернулись», а именно то, что видит браузер, ушедший в фон.
            await live.BeforeLoad(
                "window.__vis='hidden';"
                + "Object.defineProperty(document,'visibilityState',{get:function(){return window.__vis;}});");

            await live.Navigate(Url(stand.Port));

            Assert.Equal("hidden", await live.Evaluate("document.visibilityState"));

            var hidden = await live.Polls(TimeSpan.FromSeconds(7));

            Assert.True(hidden == 0, $"страница скрыта, а опрос идёт: запросов «?part=card» за 7 с — {hidden}");

            // ВЕРНУЛИСЬ НА СТРАНИЦУ — ОПРОС ВОЗОБНОВЛЯЕТСЯ САМ (событие `visibilitychange`).
            await live.Evaluate("window.__vis='visible';document.dispatchEvent(new Event('visibilitychange'));'ok'");

            Assert.Equal("visible", await live.Evaluate("document.visibilityState"));

            var back = await live.Polls(TimeSpan.FromSeconds(7));

            Assert.True(back > 0, $"страница снова видима, а опрос не возобновился: запросов {back}");
        }
        finally
        {
            live.Stop();
            TryRemove(profile);
        }
    }

    // ---- 5. клавиатура и масштаб (§3 и §4 банка: Ш-17, Ш-22) ---------------------------------

    /// <summary>
    /// ОБХОД TAB'ОМ С ВИДИМЫМ ФОКУСОМ — ЖИВЫМ БРАУЗЕРОМ, НА КАЖДОМ ОРГАНЕ.
    ///
    /// **От какого случая.** В банке про это сказано прямо: «ЖИВЬЁМ (обойти Tab'ом и снять фокус
    /// на каждом органе в браузере) — **у нас этого прогона нет**» (`80-accessibility.md` §3, Ш-17).
    /// Разбор CSS видит правило `:focus-visible`, но не видит, что браузер **применил** его к этому
    /// органу: тень у строки варианта, `outline:none` в чужом правиле или `:focus` вместо
    /// `:focus-visible` — всё это проходит разбор.
    ///
    /// ⚠️ Мерится ИЗМЕНЕНИЕ ОТ СОСТОЯНИЯ: сколько органов получают фокус — столько и обязаны
    /// показать его. Считаем по числу РАЗНЫХ органов с непустой обводкой ИЛИ тенью, а не «есть
    /// правило в таблице».
    ///
    /// Мутации, которые обязаны упасть здесь: «убрать `:focus-visible`» · «`outline:none`» ·
    /// «убрать `:focus-within` у строки варианта» · «снять орган с обхода (сделать `disabled`)».
    /// </summary>
    [Fact]
    public async Task Обход_tab_ом_даёт_видимый_фокус_на_каждом_органе()
    {
        var browser = Browser();

        Assert.False(
            browser is null,
            "браузера нет — проверить нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", BrowserPaths()) + ")");

        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-browser\"}}");
        stand.Events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        var profile = Path.Combine(Path.GetTempPath(), "dsh-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new BrowserRun(browser!, profile);

        try
        {
            await live.WaitForDevTools();
            await live.Cookie(PhoneGate.CookieName, cookie);
            await live.Enable();
            await live.Screen(390, 844, 1);

            await live.Navigate(Url(stand.Port));

            // ТАБ ЖМЁТСЯ НАСТОЯЩИМ НАЖАТИЕМ, А НЕ `focus()` ИЗ СКРИПТА — и это не придирка:
            // `:focus-visible` браузер отмечает по СПОСОБУ получения фокуса, и программный
            // `focus()` его не зажигает. Первая редакция этой проверки звала `e.focus()` и получила
            // «обводка 3px none» на всех органах — то есть мерила не то, что видит человек.
            const string Look =
                "(function(){"
                + "var e=document.activeElement;"
                + "if(!e||e===document.body)return JSON.stringify({id:'нет фокуса'});"
                + "var id=e.tagName+'.'+(e.className||'')+'#'+(e.id||'');"
                + "if(e.tagName==='INPUT'&&e.type==='radio'){"
                + "  var row=e.closest('.opt-body');"
                + "  if(!row)return JSON.stringify({id:id,tag:'radio без строки'});"
                + "  var s=getComputedStyle(row);"
                + "  return JSON.stringify({id:id,ok:s.boxShadow&&s.boxShadow!=='none',"
                + "    seen:s.boxShadow,visible:e.matches(':focus-visible')});"
                + "}"
                + "var s=getComputedStyle(e);"
                + "return JSON.stringify({id:id,ok:(parseFloat(s.outlineWidth)||0)>=2&&s.outlineStyle==='solid',"
                + "  seen:s.outlineWidth+' '+s.outlineStyle,visible:e.matches(':focus-visible')});"
                + "})()";

            var seen = new List<string>();
            var bad = new List<string>();

            for (var press = 0; press < 24; press++)
            {
                await live.Tab();

                var look = JsonSerializer.Deserialize<FocusLook>(AsJson(await live.Evaluate(Look)))!;

                if (look.id == "нет фокуса") break;

                if (seen.Contains(look.id)) break;

                seen.Add(look.id);

                if (!look.ok)
                {
                    bad.Add(look.id + ": фокуса не видно (" + look.seen + ", :focus-visible = "
                        + look.visible.ToString().ToLowerInvariant() + ")");
                }
            }

            Assert.True(seen.Count >= 4, $"обход Tab'ом нашёл всего {seen.Count} органов: {string.Join(", ", seen)}");
            Assert.True(bad.Count == 0, "органы без видимого фокуса: " + string.Join(" | ", bad));
        }
        finally
        {
            live.Stop();
            TryRemove(profile);
        }
    }

    /// <summary>
    /// ТЁМНАЯ ТЕМА ТЕЛЕФОНА ПРИМЕНЯЕТСЯ — и это ВИДНО браузеру, а не описано в таблице стилей.
    ///
    /// **От какого случая (брифинг фазы 3, «кадр тёмной темы не применился»; и фаза 2, п. 2.5:
    /// кадры страницы — «две темы»).** Тёмная тема у телефона живёт блоком
    /// `@media (prefers-color-scheme: dark)`; написанный блок ещё не значит применённый, а
    /// посмотреть на телефон владельца в тёмном режиме проверка не может.
    ///
    /// ⚠️ **Тёмная тема ставится ПОДМЕНОЙ браузера** (`Emulation.setAutoDarkModeOverride`),
    /// а не настройкой Windows: менять оформление машины ради проверки нельзя.
    ///
    /// ⚠️ Мерится ФАКТ из живой страницы: вычисленный фон `body` обязан быть равен значению
    /// `--page` СВОЕЙ темы (светлое значение берётся ИЗ ТАБЛИЦЫ СТИЛЕЙ, чтобы проверка не завела
    /// второй список цветов), а цвета двух тем обязаны РАЗЛИЧАТЬСЯ.
    ///
    /// Мутации, которые обязаны упасть здесь: «блок тёмной темы вырезан» · «тёмная тема
    /// переопределяет не те переменные» · «страница берёт фон не из `--page`».
    /// </summary>
    [Fact]
    public async Task Тёмная_тема_телефона_применяется_в_браузере()
    {
        var browser = Browser();

        Assert.False(
            browser is null,
            "браузера нет — проверить нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", BrowserPaths()) + ")");

        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        var profile = Path.Combine(Path.GetTempPath(), "dsh-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new BrowserRun(browser!, profile);

        try
        {
            await live.WaitForDevTools();
            await live.Cookie(PhoneGate.CookieName, cookie);
            await live.Enable();

            // СВЕТЛАЯ — явно выключенной подменой, а не «как получилось»: машина сборки может
            // стоять в тёмном оформлении, и тогда «светлый» кадр проверки был бы тёмным.
            await live.Light();
            await live.Navigate(Url(stand.Port));

            var light = await live.Evaluate(
                "getComputedStyle(document.body).backgroundColor+'|'+getComputedStyle(document.documentElement).getPropertyValue('--page').trim()");

            await live.Dark();
            await live.Reload();

            var dark = await live.Evaluate(
                "getComputedStyle(document.body).backgroundColor+'|'+getComputedStyle(document.documentElement).getPropertyValue('--page').trim()");

            // ФОН СТРАНИЦЫ — ЭТО `--page` СВОЕЙ ТЕМЫ (иначе краска живёт в разметке, а таблица
            // стилей ни при чём).
            Assert.Equal($"{Rgb("#F0F0F0")}|#F0F0F0", light);
            Assert.Equal($"{Rgb("#1F1F1F")}|#1F1F1F", dark);

            // И ТЕМЫ РАЗЛИЧАЮТСЯ: «тёмная тема не применилась» — ровно это равенство.
            Assert.NotEqual(light, dark);
        }
        finally
        {
            live.Stop();
            TryRemove(profile);
        }
    }

    /// <summary>Цвет <c>#RRGGBB</c> в том виде, в каком его отдаёт браузер: <c>rgb(r, g, b)</c>.</summary>
    private static string Rgb(string hex) =>
        "rgb(" + Convert.ToInt32(hex.Substring(1, 2), 16).ToString(CultureInfo.InvariantCulture)
        + ", " + Convert.ToInt32(hex.Substring(3, 2), 16).ToString(CultureInfo.InvariantCulture)
        + ", " + Convert.ToInt32(hex.Substring(5, 2), 16).ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// ЗУМ 200 % И УЗКОЕ ОКНО (320 точек): ТЕКСТ НЕ ОБРЕЗАН И НЕ УЕХАЛ ВБОК.
    ///
    /// **От какого случая.** В банке: «Это рассуждение, а не замер: живьём 200 % мы не выставляли»
    /// (`80-accessibility.md` §4, Ш-22). Норма — SC 1.4.4: текст масштабируется до **200 %**
    /// «without loss of content or functionality»; провалы — F69 (обрезало) и F80 (поле не выросло).
    /// Плюс SC 1.4.10 (перестройка при 320 точках, без прокрутки в двух направлениях).
    ///
    /// ⚠️ Мерится ФАКТ, а не «правило написано»: ширина содержимого против ширины окна и высота
    /// содержимого против высоты **прокручиваемой** области. Если текст обрезан `overflow:hidden`
    /// или заперт `height`, второе число окажется МЕНЬШЕ первого.
    ///
    /// Мутации, которые обязаны упасть здесь: «`max-height` на карточке» · «`overflow:hidden`» ·
    /// «фиксированная `height` у карточки» · «`width:640px` вместо `60ch`» (появится прокрутка вбок).
    /// </summary>
    [Fact]
    public async Task Зум_200_процентов_не_режет_и_не_уводит_текст_вбок()
    {
        var browser = Browser();

        Assert.False(
            browser is null,
            "браузера нет — проверить нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", BrowserPaths()) + ")");

        using var stand = new Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await Pair(stand);

        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-browser\"}}");
        stand.Events.Emit(
            "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
            + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
            + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Делать?\",\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}");

        var profile = Path.Combine(Path.GetTempPath(), "dsh-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new BrowserRun(browser!, profile);

        try
        {
            await live.WaitForDevTools();
            await live.Cookie(PhoneGate.CookieName, cookie);
            await live.Enable();

            // УЗКОЕ ОКНО (320 точек) И ЗУМ 200 % — одновременно: это самый тесный случай нормы.
            await live.Screen(320, 640, 2);

            await live.Navigate(Url(stand.Port));

            const string Fit =
                "(function(){"
                + "var d=document.documentElement;"
                + "var o=document.querySelector('main.card')||document.body;"
                + "var texts=['.screen-title','.note','.legend','.hint','.action'];"
                + "var hidden=[];"
                + "texts.forEach(function(sel){"
                + "  document.querySelectorAll(sel).forEach(function(e){"
                + "    var r=e.getBoundingClientRect();"
                + "    if(r.width<1||r.height<1)hidden.push(sel+' ('+e.textContent.substring(0,20)+')');"
                + "  });"
                + "});"
                + "return JSON.stringify({"
                + "zoom:window.devicePixelRatio,"
                + "viewport:d.clientWidth,"
                + "over:Math.max(0,o.scrollWidth-d.clientWidth),"
                + "need:o.scrollHeight,"
                + "have:o.clientHeight,"
                + "text:(document.body.innerText||'').length,"
                + "hidden:hidden});"
                + "})()";

            var fit = JsonSerializer.Deserialize<FitResult>(AsJson(await live.Evaluate(Fit)))!;

            // ЧТО МЕРИЛОСЬ — и это часть доказательства: страница правда в узком окне, при зуме.
            Assert.True(fit.over == 0, $"страница уехала вбок на {fit.over} точек: прокрутка в двух направлениях (SC 1.4.10)");
            Assert.True(fit.text > 40, $"на странице почти нет текста ({fit.text} знаков) — мерить нечего");

            // ЧТО БЫЛО ВИДНО — остаётся видимым: у каждого текстового места есть размер на экране.
            // Обрезка по высоте, `display:none` от чужого правила и «текст уехал за край» ловятся
            // здесь; ⚠️ чего проверка НЕ видит — читаемость (её смотрит человек).
            Assert.True(
                fit.hidden.Count == 0,
                "при зуме 200 % пропало с экрана: " + string.Join(" | ", fit.hidden));

            // ТЕКСТ НЕ ОБРЕЗАН: содержимое карточки обязано уместиться в своё поле, а не «исчезнуть».
            // Высота СОДЕРЖИМОГО МЕНЬШЕ высоты поля — это честный признак обрезки по высоте.
            Assert.True(
                fit.have == 0 || fit.need <= fit.have + 40,
                $"содержимое карточки ({fit.need}) выше её поля ({fit.have}) — текст обрезан (F69)");
        }
        finally
        {
            live.Stop();
            TryRemove(profile);
        }
    }

    private sealed record FocusLook(string id, bool ok, string seen, bool visible);

    private sealed record FitResult(
        double zoom, int viewport, double over, int need, int have, int text, List<string> hidden);

    // ---- браузер ---------------------------------------------------------------------------

    /// <summary>Куда смотрим: Edge, потом Chrome. Пути — те, что ставит Windows.</summary>
    internal static IEnumerable<string> BrowserPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe");
    }

    internal static string? Browser() => BrowserPaths().FirstOrDefault(File.Exists);

    internal static string Url(int port) => "http://" + Loop + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";

    private static string AsJson(string value)
    {
        // `Runtime.evaluate` с `returnByValue` отдаёт строку JSON-ом: разворачиваем один раз.
        var text = value.Trim();

        return text.Length > 1 && text[0] == '"' ? JsonSerializer.Deserialize<string>(text)! : text;
    }

    internal static void TryRemove(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Профиль браузера мог остаться занятым — это не беда проверки.
        }
    }

    /// <summary>
    /// БРАУЗЕР С ОТЛАДОЧНЫМ ПРОТОКОЛОМ: запуск, ожидание порта, cookie, переход и два вопроса —
    /// «что в DOM» и «сколько раз спросили карточку». Всё общение — своим сокетом (CDP по WebSocket),
    /// сторонних библиотек не тянем.
    /// </summary>
    internal sealed class BrowserRun : IDisposable
    {
        private readonly Process _process;
        private readonly string _profile;
        private readonly Cdp _cdp;

        public BrowserRun(string exe, string profile)
        {
            _profile = profile;

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--headless=new --disable-gpu --no-first-run --no-default-browser-check"
                    + " --disable-extensions --remote-debugging-port=0 --user-data-dir=\"" + profile + "\""
                    + " about:blank",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("браузер не запустился");

            _cdp = new Cdp(Port(profile));
        }

        /// <summary>Порт отладки браузер пишет САМ в файл профиля — угадывать его нечем.</summary>
        private static int Port(string profile)
        {
            var file = Path.Combine(profile, "DevToolsActivePort");

            var until = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < until)
            {
                if (File.Exists(file))
                {
                    var first = File.ReadAllLines(file).FirstOrDefault();

                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                        return port;
                }

                Thread.Sleep(200);
            }

            throw new InvalidOperationException("браузер не открыл отладочный порт за 30 с");
        }

        public Task WaitForDevTools() => _cdp.Connect();

        public Task Enable() => _cdp.Send("Page.enable").ContinueWith(_ => _cdp.Send("Network.enable")).Unwrap();

        /// <summary>
        /// СКАЗАТЬ БРАУЗЕРУ, ЧТО ИСПОЛНИТЬ ДО ЗАГРУЗКИ СТРАНИЦЫ (<c>Page.addScriptToEvaluateOnNewDocument</c>).
        ///
        /// ⚠️ Затем и нужно: подменить видимость ПОСЛЕ загрузки значило бы «страница уже успела
        /// спросить карточку» — а нам надо, чтобы она родилась скрытой, как у человека, ушедшего
        /// в другое приложение.
        /// </summary>
        public Task BeforeLoad(string source) =>
            _cdp.Send(
                "Page.addScriptToEvaluateOnNewDocument",
                "{\"source\":" + JsonSerializer.Serialize(source) + "}");

        public Task Cookie(string name, string value) =>
            _cdp.Send(
                "Network.setCookie",
                "{\"name\":\"" + name + "\",\"value\":\"" + value + "\",\"domain\":\"" + Loop + "\",\"path\":\"/\"}");

        /// <summary>Одна клавиша в страницу — НАСТОЯЩИМ нажатием отладочного протокола.</summary>
        public Task Key(string key, string code, int virtualKey) =>
            _cdp.Send(
                "Input.dispatchKeyEvent",
                "{\"type\":\"rawKeyDown\",\"key\":\"" + key + "\",\"code\":\"" + code
                + "\",\"windowsVirtualKeyCode\":" + virtualKey.ToString(CultureInfo.InvariantCulture) + "}");

        /// <summary>Клавиша Tab — так же, как её жмёт человек.</summary>
        public Task Tab() => Key("Tab", "Tab", 9);

        /// <summary>
        /// ТЁМНАЯ ТЕМА БРАУЗЕРА — ПОДМЕНОЙ, А НЕ НАСТРОЙКОЙ МАШИНЫ: `prefers-color-scheme` иначе
        /// не переключить, а менять оформление Windows ради кадра нельзя.
        ///
        /// ⚠️ **Через `Emulation.setEmulatedMedia`, а не `setAutoDarkModeOverride`.** Второй способ
        /// стоит здесь с прежней редакции и **не существует в отладочном протоколе**: вызов уходил
        /// без ошибки и ничего не менял — то есть «тёмная тема браузера» была обещанием, а страница
        /// оставалась светлой. Нашлось это проверкой `Тёмная_тема_телефона_применяется_в_браузере`
        /// (она сравнила фон живой страницы с `--page` своей темы).
        /// </summary>
        public Task Dark() => Media("dark");

        public Task Light() => Media("light");

        /// <summary>Подмена `prefers-color-scheme` — единственный способ переключить тему страницы.</summary>
        private Task Media(string scheme) =>
            _cdp.Send(
                "Emulation.setEmulatedMedia",
                "{\"features\":[{\"name\":\"prefers-color-scheme\",\"value\":\"" + scheme + "\"}]}");

        /// <summary>КАДР СТРАНИЦЫ — PNG целиком, как его отдаёт отладочный протокол.</summary>
        public async Task<byte[]> Shot()
        {
            var reply = await _cdp.Send("Page.captureScreenshot", "{\"format\":\"png\"}");

            using var document = JsonDocument.Parse(reply);

            if (!document.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("data", out var data))
            {
                throw new InvalidOperationException("браузер не отдал кадр страницы");
            }

            return Convert.FromBase64String(data.GetString()!);
        }

        /// <summary>
        /// ПЕРЕЗАГРУЗИТЬ СТРАНИЦУ И ДОЖДАТЬСЯ ЕЁ — затем, чтобы смена темы применилась к уже
        /// загруженному документу, а не только к следующему.
        /// </summary>
        public async Task Reload()
        {
            await _cdp.Send("Page.reload", "{\"ignoreCache\":true}");

            await Settled();
        }

        /// <summary>
        /// ДОЖДАТЬСЯ, ЧТО СТРАНИЦА «УСТОЯЛАСЬ»: загрузилась и скрипт успел убрать ряд «Проверить»
        /// (если он вообще есть у этого состояния). Ждём ПРИЗНАК, а не «сколько-то секунд»:
        /// иначе кадр снимался бы то до, то после работы скрипта.
        /// </summary>
        public async Task Settled()
        {
            var until = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < until)
            {
                var state = await Evaluate("document.readyState");

                if (!state.Contains("complete", StringComparison.Ordinal))
                {
                    await Task.Delay(150);
                    continue;
                }

                // У живого состояния ряд обязан уйти; у остальных его и не было.
                var pending = await Evaluate("document.querySelectorAll('[data-auto]').length");

                if (pending == "0") return;

                await Task.Delay(150);
            }

            throw new InvalidOperationException("страница не устоялась за 20 с");
        }

        /// <summary>
        /// ЗУМ И РАЗМЕР ОКНА — через тот же отладочный протокол. Величины подменяются в единицах
        /// CSS: зум 200 % — это `2`, а не «на глаз»; узкое окно — 320 точек (норма перестройки
        /// содержимого, SC 1.4.10).
        /// </summary>
        public Task Screen(int width, int height, double zoom) =>
            _cdp.Send(
                "Emulation.setDeviceMetricsOverride",
                "{\"width\":" + width.ToString(CultureInfo.InvariantCulture)
                + ",\"height\":" + height.ToString(CultureInfo.InvariantCulture)
                + ",\"deviceScaleFactor\":1,\"mobile\":true,\"scale\":"
                + zoom.ToString("0.####", CultureInfo.InvariantCulture) + "}");

        public async Task Navigate(string url)
        {
            await _cdp.Send("Page.navigate", "{\"url\":\"" + url + "\"}");

            // Ждём загрузку: признак — готовность документа, а не «сколько-то секунд».
            var until = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < until)
            {
                var state = await Evaluate("document.readyState");

                if (state.Contains("complete", StringComparison.Ordinal)) return;

                await Task.Delay(200);
            }

            throw new InvalidOperationException("страница не загрузилась за 20 с");
        }

        /// <summary>Значение выражения в странице — строкой (значения у нас строки и числа).</summary>
        public async Task<string> Evaluate(string expression)
        {
            var reply = await _cdp.Send(
                "Runtime.evaluate",
                "{\"expression\":" + JsonSerializer.Serialize(expression) + ",\"returnByValue\":true}");

            using var document = JsonDocument.Parse(reply);

            var result = document.RootElement.GetProperty("result").GetProperty("result");

            return result.TryGetProperty("value", out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
                : string.Empty;
        }

        /// <summary>
        /// СКОЛЬКО РАЗ СПРОСИЛИ КАРТОЧКУ ЗА ОКНО. Считаем по событиям сети — то есть по ФАКТУ
        /// запроса, а не по «скрипт так написан».
        /// </summary>
        public async Task<int> Polls(TimeSpan window)
        {
            var was = _cdp.Polls;

            await Task.Delay(window);

            return _cdp.Polls - was;
        }

        public void Stop()
        {
            _cdp.Dispose();

            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
            catch
            {
                // Браузер мог закрыться сам.
            }

            _process.Dispose();
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// МАЛЕНЬКИЙ КЛИЕНТ ОТЛАДОЧНОГО ПРОТОКОЛА: команда — по номеру, события — в счётчик.
    /// Нужен ровно один счётчик — запросы карточки (`?part=card`).
    /// </summary>
    private sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Dictionary<int, TaskCompletionSource<string>> _waiting = new();
        private readonly object _gate = new();
        private int _next;
        private int _polls;
        private Task? _reader;

        public Cdp(int port)
        {
            Port = port;
        }

        public int Port { get; }

        public int Polls
        {
            get
            {
                lock (_gate) return _polls;
            }
        }

        public async Task Connect()
        {
            var until = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < until)
            {
                var url = await Target();

                if (url.Length > 0)
                {
                    await _socket.ConnectAsync(new Uri(url), CancellationToken.None);

                    // ЧИТАЕМ СОКЕТ ПОСТОЯННО, А НЕ «КОГДА ОТПРАВИЛИ КОМАНДУ»: запросы карточки
                    // приходят между командами, и события, не прочитанные вовремя, теряются —
                    // на этом проверка и провалилась в первом прогоне («опрос не пошёл вовсе»).
                    _reader = Task.Run(ReadLoop);

                    return;
                }

                await Task.Delay(300);
            }

            throw new InvalidOperationException("отладочный протокол браузера не ответил за 30 с");
        }

        /// <summary>Адрес страницы для протокола: берём первую вкладку.</summary>
        private async Task<string> Target()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            try
            {
                var list = await client.GetStringAsync(
                    "http://" + Loop + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/json/list");

                using var document = JsonDocument.Parse(list);

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type)
                        && type.GetString() == "page"
                        && item.TryGetProperty("webSocketDebuggerUrl", out var socket))
                    {
                        return socket.GetString() ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Порт ещё не поднялся — повторим.
            }

            return string.Empty;
        }

        public async Task<string> Send(string method, string parameters = "{}")
        {
            var id = Interlocked.Increment(ref _next);
            var promise = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate) _waiting[id] = promise;

            var text = "{\"id\":" + id.ToString(CultureInfo.InvariantCulture)
                + ",\"method\":\"" + method + "\",\"params\":" + parameters + "}";

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

            return await promise.Task;
        }

        private async Task ReadLoop()
        {
            while (_socket.State == WebSocketState.Open)
            {
                string frame;

                try
                {
                    frame = await Read();
                }
                catch
                {
                    break;
                }

                using var document = JsonDocument.Parse(frame);

                if (document.RootElement.TryGetProperty("method", out var name)
                    && name.GetString() == "Network.requestWillBeSent"
                    && frame.Contains("part=card", StringComparison.Ordinal))
                {
                    lock (_gate) _polls++;
                }

                if (document.RootElement.TryGetProperty("id", out var answer) && answer.ValueKind == JsonValueKind.Number)
                {
                    var id = answer.GetInt32();

                    TaskCompletionSource<string>? promise;

                    lock (_gate)
                    {
                        if (!_waiting.TryGetValue(id, out promise)) promise = null;
                        else _waiting.Remove(id);
                    }

                    promise?.TrySetResult(frame);
                }
            }

            // Сокет закрылся — все, кто ждал ответа, обязаны узнать об этом, а не ждать вечно.
            lock (_gate)
            {
                foreach (var promise in _waiting.Values) promise.TrySetException(
                    new InvalidOperationException("браузер закрыл отладочный канал"));

                _waiting.Clear();
            }
        }

        private async Task<string> Read()
        {
            var buffer = new byte[64 * 1024];
            using var text = new MemoryStream();

            while (true)
            {
                var part = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                if (part.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("канал закрыт");

                text.Write(buffer, 0, part.Count);

                if (part.EndOfMessage) break;
            }

            return Encoding.UTF8.GetString(text.ToArray());
        }

        public void Dispose()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "готово", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Закрытие — не то, о чём стоит сообщать.
            }

            _socket.Dispose();
        }
    }

    // ---- стенд калитки (петля, свободный порт, свой временный корень) -----------------------

    internal sealed class FakeEvents
    {
        private readonly List<Action<string>> _subscribers = new();

        public PhoneEventDoors Doors => new(
            onFrame =>
            {
                _subscribers.Clear();
                _subscribers.Add(onFrame);

                return new Link();
            },
            _ => { });

        public void Emit(string frame)
        {
            foreach (var subscriber in _subscribers.ToList()) subscriber(frame);
        }

        private sealed class Link : IPhoneEventLink
        {
            public bool Alive { get; set; } = true;

            public void Dispose() => Alive = false;
        }
    }

    internal sealed class Stand : IDisposable
    {
        private readonly string _root;

        public Stand()
        {
            _root = Path.Combine(Path.GetTempPath(), "dsh-browser-gate-" + Guid.NewGuid().ToString("N"));
            var paths = AppPaths.Under(_root);

            Port = FreeLoopbackPort();
            Events = new FakeEvents();

            var doors = new PhoneGateDoors(
                () => Loop,
                Port,
                (_, _) => new PhoneEngineAnswer(200, "{\"type\":\"server-response\",\"result\":{\"ok\":true}}"),
                PhonePassFiles.Under(paths, _ => { }),
                Events.Doors,
                () => new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero),
                _ => { },
                _ => { });

            Gate = new PhoneGate(doors, () => true, humanLaunch: true, RunContext.Create(RunRequest.Owner()));

            Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(Url(Port)),
                Timeout = TimeSpan.FromSeconds(20),
            };
        }

        public int Port { get; }

        public FakeEvents Events { get; }

        public PhoneGate Gate { get; }

        public HttpClient Http { get; }

        public void Dispose()
        {
            Gate.Dispose();
            Http.Dispose();
            TryRemove(_root);
        }
    }

    internal static async Task<string> Pair(Stand stand)
    {
        var code = stand.Gate.IssueCode();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code });

        using var reply = await stand.Http.PostAsync("/pair", content);

        var setCookie = reply.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values)
            : null;

        Assert.NotNull(setCookie);

        return PhoneGate.CookieValueOf(setCookie!);
    }

    /// <summary>Свободный порт ПЕТЛИ: связываемся с нулём и отдаём номер системе.</summary>
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
