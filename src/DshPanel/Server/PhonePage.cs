using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧТО СТРАНИЦА ПОКАЗЫВАЕТ ПРЯМО СЕЙЧАС.
///
/// Состояний ровно столько, сколько у телефона бывает ПОЛОЖЕНИЙ, а не столько, сколько бывает
/// ответов движка: телефону нечего делать с кодом 403 и с текстом ошибки движка — ему нужны СЛОВА
/// и одно из двух: кнопки (вопрос ждёт ответа) или их отсутствие (ждать нечего).
///
/// <see cref="Problem"/> — «беда словами»: ответ не отправился, отменить не удалось, выбран не
/// каждый вопрос. У неё нет ни вопроса, ни кнопок, только <see cref="PhonePageView.Note"/>.
/// </summary>
public enum PhonePageKind
{
    /// <summary>Вопросов нет — ждём. Единственное состояние, в котором телефон ждёт молча.</summary>
    Waiting,

    /// <summary>
    /// СВЯЗИ С СЕРВЕРОМ DSH НЕТ: вопрос агента телефону показать нечем (решение владельца
    /// 13.10.2026, вариант «а»).
    ///
    /// ⚠️ **Отдельно от <see cref="Waiting"/>, и это про правду, а не про красоту.** Ожидание
    /// обещает: «как только вопрос появится, он покажется здесь сам». При отсутствии связи вопрос
    /// не появится НИКОГДА, и прежняя страница обещала человеку то, чего не будет, — он ждал бы
    /// у телефона, пока на компьютере не поднят сервер.
    /// </summary>
    NoEngine,

    /// <summary>Вопрос агента ждёт ответа: показывается ОДИН вопрос и его варианты.</summary>
    Question,

    /// <summary>Ответ уже отправлен — второй раз не отправляем.</summary>
    Answered,

    /// <summary>Вопрос уже не ждёт: его взял кто-то другой или агент его снял.</summary>
    Taken,

    /// <summary>Ход агента отменён — ответ больше не понадобится.</summary>
    Cancelled,

    /// <summary>Вопрос пришёл, но разобрать его не вышло: отвечать нечем.</summary>
    Unreadable,

    /// <summary>Беда, сказанная словами (<see cref="PhonePageView.Note"/>): слова и путь «Повторить».</summary>
    Problem,
}

/// <summary>
/// РАЗОБРАННЫЙ ВОПРОС АГЕНТА — то, что нужно показать и чем ответить.
///
/// <see cref="Options"/> — ПОДПИСИ вариантов В ТОМ ЖЕ ПОРЯДКЕ, в каком они пришли. Порядок здесь
/// не украшение, а часть ответа: телефон присылает обратно НОМЕР варианта, а подпись берётся
/// отсюда (<see cref="PhoneAnswer.Build"/>) — переставь порядок, и человек ответит не то.
/// </summary>
public sealed record PhoneQuestion(string Id, string Header, string Text, string Detail, IReadOnlyList<string> Options);

/// <summary>
/// ВСЁ, ЧТО НУЖНО СТРАНИЦЕ: состояние, ОДИН показанный вопрос, номер экрана из общего числа,
/// накопленные ответы, «живое» ли это состояние и — для <see cref="PhonePageKind.Problem"/> —
/// слова беды.
///
/// ⚠️ **ОДИН ВОПРОС НА ЭКРАН — вариант A, выбранный владельцем 11.10.2026.** <see cref="Questions"/>
/// несёт РОВНО ОДИН вопрос, и это не упрощение страницы, а её вид: строка «Вопрос N из M»
/// (<see cref="Screen"/>, <see cref="Total"/>) говорит, сколько их всего. Накапливает ответы
/// КАЛИТКА (`PhoneGate`): она держит и номер экрана, и уже отвеченное.
///
/// ⚠️ <see cref="Live"/> выбирает ВЫЗЫВАЮЩИЙ: у вопроса, ожидания и исходов живое обновление ЕСТЬ
/// (опрос раз в <see cref="PhonePage.PollMs"/> миллисекунд своим же адресом — <c>GET /?part=card</c>),
/// у беды его НЕТ вовсе. У беды оно не «выключено галочкой», а не бывает: беда приходит в ответ
/// на ОТПРАВКУ формы, и опрос заставил бы человека увидеть не тот вопрос, на который он отвечал.
///
/// <see cref="Answers"/> — УЖЕ ОТВЕЧЕННОЕ НА ЭТОМ ЭКРАНЕ, по `id` вопроса: из него страница
/// дорисовывает выбранное и возвращает свой текст, когда человек вернулся после отказа (решение
/// владельца 11.10.2026: введённое и выбранное не пропадает без действия человека). Пусто —
/// ничего не выбрано.
///
/// <see cref="Note"/> — слова беды для <see cref="PhonePageKind.Problem"/>: их собирает калитка
/// (она одна знает, что именно не вышло), а страница только показывает их человеку.
/// </summary>
public sealed record PhonePageView(
    PhonePageKind Kind,
    string EventId,
    IReadOnlyList<PhoneQuestion> Questions,
    bool Live,
    string Note = "",
    int Screen = 0,
    int Total = 1,
    IReadOnlyDictionary<string, string>? Answers = null,
    string Device = "")
{
    /// <summary>Вопросов нет — ждём. Живое обновление есть: новый вопрос должен показаться сам.</summary>
    public static PhonePageView Waiting() =>
        new(PhonePageKind.Waiting, string.Empty, Array.Empty<PhoneQuestion>(), true);

    /// <summary>
    /// СВЯЗИ С СЕРВЕРОМ DSH НЕТ — показать правду вместо обещания (решение владельца 13.10.2026).
    ///
    /// ⚠️ Живое обновление ЗДЕСЬ ЕСТЬ (`Live = true`), и это не противоречие: связь может
    /// появиться в любую минуту — человек поднимет сервер на компьютере, — и страница обязана
    /// показать вопрос сама, без перезагрузки телефоном. Отличается это состояние от ожидания
    /// не опросом, а СЛОВАМИ: «вопрос придёт сам» против «показывать нечем, посмотрите на компьютер».
    /// </summary>
    public static PhonePageView NoEngine() =>
        new(PhonePageKind.NoEngine, string.Empty, Array.Empty<PhoneQuestion>(), true);

    /// <summary>ЧЕМ КОНЧИЛОСЬ: ответ отправлен, вопрос снят, ход отменён. Обновление тоже есть —
    /// затем, чтобы телефон не застыл на исходе, если агент спросит снова.</summary>
    public static PhonePageView Outcome(PhonePageKind kind) =>
        new(kind, string.Empty, Array.Empty<PhoneQuestion>(), true);

    /// <summary>
    /// БЕДА СЛОВАМИ: <paramref name="note"/> — то, что человеку и скажут. Кнопок у неё нет, но путь
    /// есть: «Повторить» — обычная ссылка на корень (`&lt;a class="ghost" href="/"&gt;`), она
    /// работает и без скрипта.
    ///
    /// ⚠️ **Живого обновления у беды НЕТ** (<c>Live = false</c>) — и это не мелочь: страница беды
    /// отвечает на ОТПРАВКУ формы, и опрос подменил бы человеку карточку ровно тогда, когда он
    /// читает, что именно не вышло.
    /// </summary>
    public static PhonePageView Problem(string note) =>
        new(PhonePageKind.Problem, string.Empty, Array.Empty<PhoneQuestion>(), false, note);
}

/// <summary>
/// ПОЛЯ ФОРМЫ ОТВЕТА ОДНОГО ЭКРАНА: <c>eventId</c>, <c>q&lt;экран&gt;</c> — НОМЕР варианта строкой
/// (<c>""</c> = не выбрано), <c>q&lt;экран&gt;-own</c> — СВОЙ ОТВЕТ человека словами.
///
/// ⚠️ **Выбор — СТРОКОЙ, а не числом:** «не выбрано» и «выбрано нулевое» — разные вещи, и число,
/// у которого нет значения «пусто», различать их не дало бы. Границы индекса проверяет калитка
/// (<see cref="PhoneGate"/>).
///
/// ⚠️ <see cref="Screen"/> — НОМЕР ЭКРАНА, а не «номер вопроса из кадра»: при варианте A экран
/// показывает РОВНО ОДИН вопрос, и номер экрана и есть его номер. Имя поля собирается из него
/// (<c>q0</c>, <c>q1</c>), потому что так его называет разметка (<see cref="PhonePage"/>).
/// </summary>
public sealed record PhoneAnswerFields(string EventId, int Screen, string Choice, string Own)
{
    /// <summary>СВОЙ ТЕКСТ, а не выбор: непустое — значит человек писал в поле.</summary>
    public bool HasOwn => Own.Length > 0;
}

/// <summary>
/// СТРАНИЦА ТЕЛЕФОНА — ОДИН HTML БЕЗ ЕДИНОГО ВНЕШНЕГО РЕСУРСА И С ОДНИМ СВОИМ СКРИПТОМ.
///
/// **Замок, и он назван словами** (решение владельца 11.10.2026): **ни одного внешнего ресурса
/// и ни одного чужого адреса; наш скрипт ровно один; обращается только к своему адресу.**
///
/// **Почему так строго.** Страница отдаётся телефону в ДОМАШНЕЙ сети, где наружу может не быть
/// ничего вовсе: любая картинка, шрифт или скрипт «снаружи» — это либо вечная загрузка, либо
/// молчаливая утечка того, что телефон смотрит панель (адрес панели виден тому, кто отдал файл).
/// Поэтому здесь нет ни <c>http://</c>, ни <c>//</c>, ни <c>&lt;link&gt;</c>, ни <c>&lt;img&gt;</c>,
/// ни <c>@import</c>, ни <c>url(</c> — и есть РОВНО ОДИН <c>&lt;script&gt;</c>, свой.
///
/// **Что этот скрипт делает и чего не делает.** Раз в <see cref="PollMs"/> миллисекунд он
/// спрашивает СВОЮ ЖЕ страницу (<c>GET /?part=card</c>) и подменяет ТОЛЬКО карточку; ни одного
/// чужого хоста в нём нет вовсе. Опрос идёт, **пока страница видима**, и приостанавливается, пока
/// человек выбрал вариант или пишет свой ответ (<see cref="Poll"/>); галочки «не обновлять само»
/// на странице НЕТ — её убрал владелец 12.10.2026, потому что подмена карточки уносила отметку.
///
/// ⚠️ **`meta refresh` ОТМЕНЁН и не вернётся.** WCAG 2.2 SC 2.2.2 требует механизма остановки
/// для любого самообновления, а meta refresh его не даёт; вдобавок он стирал уже отмеченное
/// (замер владельца 11.10.2026). Для автообновления F41/SC 3.2.5 тоже называют его непригодным
/// (`_design-standard-draft.md` §2.8).
///
/// ⚠️ **БЕЗ СКРИПТА СТРАНИЦА РАБОТАЕТ ЦЕЛИКОМ.** Кнопка «Проверить» — ССЫЛКА
/// (<c>&lt;a class="ghost narrow" href="/"&gt;</c>), а не <c>&lt;button&gt;</c>: без скрипта она
/// просто перезапрашивает ту же страницу, а внутри формы ответа она не заводит ВЛОЖЕННУЮ форму
/// (вложенная форма — невалидный HTML, и браузер разбирает её как захочет). Форма ответа
/// отправляется обычным POST.
///
/// **Чего на странице нет и не будет.** Ни путей машины, ни имени машины, ни её адреса, ни ссылки
/// входа (<c>token=</c>): показать здесь нечего, кроме слов из словаря панели и текста вопроса,
/// пришедшего от агента. Всё, что приходит от агента (заголовок, текст, подробность, подписи
/// вариантов), ЭКРАНИРУЕТСЯ — вопрос со знаком <c>&lt;</c> обязан остаться словами, а не сломать
/// страницу и не стать разметкой.
///
/// **Язык приходит доводом** и берётся у САМОГО телефона (<see cref="PhoneGateLanguages"/>),
/// а слова — из тех же трёх словарей, что и вся панель. Свой ответ человека уходит движку ТОЙ ЖЕ
/// строкой, что и выбранный вариант (<c>selected</c>), поэтому «свой» и «выбранный» различимы
/// только словами — и это правило названо в отчёте.
/// </summary>
public static class PhonePage
{
    /// <summary>
    /// ЧЕРЕЗ СКОЛЬКО СТРАНИЦА СПРАШИВАЕТ СЕБЯ САМА, в миллисекундах: 2,5 с. Число одно на всю
    /// панель и стоит РЯДОМ со словами, которые о нём говорят (<see cref="PollSeconds"/>):
    /// разойтись они не имеют права — страница обещала бы одно, а делала другое.
    ///
    /// Норма — «раз в 2–3 с» (решение владельца 11.10.2026); чаще незачем: телефон в своей сети,
    /// а человек читает вопрос. Своего числа у нормы нет, и это названо честно.
    /// </summary>
    public const int PollMs = 2500;

    /// <summary>Число секунд для слов о живом обновлении — ИНВАРИАНТНОЙ культурой: телефону
    /// китайский или русский разделитель дробной части не должен менять смысл обещания. Пишется
    /// РОВНО так, как попадёт в слова (запятой), потому что слова собирает <c>string.Format</c>
    /// инвариантной культурой, а «2.5» он показал бы как «2,5» — то есть подстановка и константа
    /// разошлись бы. Что это то же число, что <see cref="PollMs"/>, стережёт проверка.</summary>
    public const string PollSeconds = "2,5";

    /// <summary>Имя части страницы, которой живёт опрос: <c>GET /?part=card</c>.</summary>
    public const string CardPart = "card";

    /// <summary>Имя параметра запроса, которым просят ТОЛЬКО карточку.</summary>
    public const string PartParameter = "part";

    /// <summary>Имя единственного скрипта на странице — по нему его и стерегут проверки.</summary>
    private const string Script = "<script>";

    /// <summary>
    /// ПРОСЯТ ТОЛЬКО КАРТОЧКУ? <c>part=card</c> — и ничего кроме: строка запроса приходит сюда
    /// ЦЕЛИКОМ, а разбор её живёт ОДНИМ местом. Значение сверяется строго: `part=cardx` — это
    /// не наша просьба, и отдавать на неё карточку вместо страницы значило бы гадать.
    /// </summary>
    public static bool CardWanted(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        foreach (var pair in query!.Split('&'))
        {
            var cut = pair.IndexOf('=');
            if (cut <= 0) continue;

            if (!string.Equals(pair[..cut], PartParameter, StringComparison.Ordinal)) continue;

            return string.Equals(pair[(cut + 1)..], CardPart, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// СОБРАТЬ СТРАНИЦУ. Единственный вход; ничего не читает и никуда не ходит.
    ///
    /// <paramref name="cardOnly"/> — ОТДАТЬ ТОЛЬКО СОДЕРЖИМОЕ КАРТОЧКИ: ровно это просит свой же
    /// скрипт (<c>?part=card</c>), и ровно это он подменяет. Ни таблицы стилей, ни скрипта, ни
    /// самого <c>&lt;div class="card"&gt;</c> в таком ответе нет — они уже стоят в странице,
    /// которая его позвала.
    ///
    /// ⚠️ **Слово берётся ПО ИМЕНИ КЛЮЧА** (<c>nameof(PanelStrings.X)</c>), а не готовой строкой
    /// <c>PanelStrings.X</c>, — и это не придирка. Готовая строка уже переведена на язык ПАНЕЛИ,
    /// и <see cref="Loc.TIn"/> вернул бы её как есть (ключа с таким текстом в словаре нет) —
    /// то есть довод <paramref name="language"/> оказался бы мёртвым, а телефон получал бы язык,
    /// выбранный на компьютере.
    /// </summary>
    public static string Html(string language, PhonePageView view, bool cardOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(view);

        // ОПРОС ПРОСИТ ТОЛЬКО СОДЕРЖИМОЕ КАРТОЧКИ: сам `<div class="card">` уже стоит на странице,
        // которая его позвала, — второй такой же внутри первого был бы карточкой в карточке.
        if (cardOnly) return CardInner(language, view) + "\n";

        // ⚠️ `<title>` НАЗЫВАЕТ СОСТОЯНИЕ, А НЕ ОДНО И ТО ЖЕ НА ВСЕХ СТРАНИЦАХ (решение владельца
        // 12.10.2026): читалка читает заголовок ДО разметки, и «Агент ждёт ответа» на экране
        // ожидания было бы ложью вдвойне — и глазами, и на слух.
        var title = Word(language, Heading(view));

        var lines = new List<string>
        {
            "<!DOCTYPE html>",
            "<html lang=\"" + Escape(language) + "\">",
            "<head>",
            "<meta charset=\"utf-8\">",
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            "<title>" + title + "</title>",

            // ВИД — ВНУТРИ страницы и ОДНОЙ таблицей на обе страницы панели: страница обязана
            // работать офлайн, а телефону — выглядеть как панель, а не как голый текст
            // (решение владельца 11.10.2026: «выглядит ужасно, без разметки»).
            "<style>" + PhonePageStyle.Css + "</style>",
            "</head>",
            "<body>",
            Card(language, view),

            // ЕДИНСТВЕННЫЙ НАШ СКРИПТ — и он один на всю панель. `meta refresh` на странице нет
            // вовсе (WCAG 2.2 F41/SC 3.2.5). ⚠️ Механизма остановки у автообновления здесь НЕТ,
            // и это НАЗВАННОЕ отступление с основанием (владелец 12.10.2026): обновление — сама
            // задача этой страницы, а опрос прекращается сам, как только страница уходит с экрана
            // (`document.visibilityState`), и не идёт, пока человек выбрал вариант или пишет текст.
            Script + Poll() + "</script>",
            "</body>",
            "</html>",
            string.Empty,
        };

        return string.Join("\n", lines);
    }

    /// <summary>
    /// КАРТОЧКА ЦЕЛИКОМ — тот самый кусок, которым живёт опрос. Собирается ОДНИМ местом и для
    /// целой страницы, и для <c>?part=card</c>: разойдись они, и скрипт подменял бы карточку
    /// карточкой другого вида.
    /// </summary>
    private static string Card(string language, PhonePageView view)
    {
        var lines = new List<string>
        {
            // `data-live` — признак «эту карточку спрашивают сама страница»: по нему скрипт находит
            // карточку и замолкает, если её нет (вход, беда).
            "<div class=\"card\"" + (view.Live ? " data-live" : string.Empty) + ">",
        };

        lines.Add(CardInner(language, view));
        lines.Add("</div>");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// ЧТО ЗАГОЛОВОК У ЭТОГО СОСТОЯНИЯ — ОДНО МЕСТО НА ВСЕ СОСТОЯНИЯ.
    ///
    /// **От какого случая (решение владельца 12.10.2026).** Заголовок был один на всё: «Агент ждёт
    /// ответа». У ожидания он **врал**: агент ничего не спрашивает, а человек читал «ждите» и ждал
    /// ответа на вопрос, которого нет. Теперь заголовок называет состояние:
    /// у вопроса — «агент ждёт ответа», у ожидания — «ответа пока не нужно», у исходов — их слова.
    ///
    /// ⚠️ Заголовок исходов — **ОТДЕЛЬНАЯ строка от тела**: тело говорит «что дальше» (одна фраза),
    /// а заголовок называется коротко. Так читалка получает состояние **до** разметки, а не после
    /// (`60-states.md` §8, GOV.UK: `<title>` и `h1` называют состояние).
    /// </summary>
    private static string Heading(PhonePageView view) => view.Kind switch
    {
        PhonePageKind.Question => nameof(PanelStrings.PhonePageQuestionTitle),
        PhonePageKind.Waiting => nameof(PanelStrings.PhonePageWaitingTitle),
        PhonePageKind.NoEngine => nameof(PanelStrings.PhonePageNoEngineTitle),
        PhonePageKind.Answered => nameof(PanelStrings.PhonePageAnswered),
        PhonePageKind.Taken => nameof(PanelStrings.PhonePageTaken),
        PhonePageKind.Cancelled => nameof(PanelStrings.PhonePageCancelled),
        PhonePageKind.Problem => nameof(PanelStrings.PhonePageProblemTitle),
        _ => nameof(PanelStrings.PhonePageUnreadable),
    };

    /// <summary>
    /// ЧТО ЛЕЖИТ ВНУТРИ КАРТОЧКИ — заголовок состояния и всё остальное. Собирается ОДНИМ местом
    /// и для целой страницы, и для опроса: разойдись они, и скрипт подменял бы карточку карточкой
    /// другого вида.
    /// </summary>
    private static string CardInner(string language, PhonePageView view)
    {
        var lines = new List<string>
        {
            // ЗАГОЛОВОК МЕНЯЕТСЯ ПО СОСТОЯНИЮ, И ЭТО ПРО ПРАВДУ, А НЕ ПРО КРАСОТУ (решение владельца
            // 12.10.2026): «Агент ждёт ответа» стоит РОВНО там, где вопрос есть. У ожидания он свой:
            // сказать «агент ждёт ответа», когда он ничего не спрашивает, — соврать человеку,
            // и он будет ждать ответа на вопрос, которого нет.
            "<h1 class=\"screen-title\">" + Word(language, Heading(view)) + "</h1>",
        };

        // КАКОЕ УСТРОЙСТВО СМОТРИТ СТРАНИЦУ (решение владельца 12.10.2026). Строка тихая и стоит
        // ОДИН раз на страницу: у человека с двумя телефонами иначе не отличить, чей это экран и
        // чей ответ ушёл. Идентификатор (`phone-N`) знает калитка из cookie — страница его только
        // показывает и ничего по нему не решает.
        //
        // ⚠️ `data-device` — КРЮЧОК, А НЕ СТИЛЬ (`data-live` у карточки назван так же). Прежняя
        // редакция носила класс `device` без правила в таблице стилей — а у нас действует
        // «класс без правила — дефект», и ворота этого не видели, потому что ни одна страница
        // прогона не несла непустого `Device` (найдено разбором `80-accessibility.md` §12).
        // Вид строке даёт `.hint`; имя для браузерного прогона — этим доводом.
        if (view.Device.Length > 0)
        {
            lines.Add(
                "<p class=\"hint\" data-device>"
                + Words(language, nameof(PanelStrings.PhonePageDeviceFormat), view.Device)
                + "</p>");
        }

        // СЛОВА-ОГРАНИЧИТЕЛЬ — на каждой странице телефона (решение владельца 17.10.2026): та же
        // строка, что в разделе «Телефон» настроек и на странице входа. Стоит ПОСЛЕ карточки
        // состояния, чтобы не оттеснять ответ на вопрос агента.
        lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhoneLimits)) + "</p>");

        CardBody(lines, language, view);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// ЧТО ИМЕННО В КАРТОЧКЕ — по состоянию. Слова, а не молчание: пустая страница читается
    /// человеком как «всё в порядке».
    /// </summary>
    private static void CardBody(List<string> lines, string language, PhonePageView view)
    {
        var questions = view.Questions ?? Array.Empty<PhoneQuestion>();

        switch (view.Kind)
        {
            case PhonePageKind.Question when questions.Count > 0:
                AnswerForm(lines, language, view, questions[0]);
                break;

            // ВОПРОС ЕСТЬ, А ЧИТАТЬ НЕЧЕГО (разобрать не вышло) — то же, что неразобранный вопрос:
            // кнопок без вопроса не бывает, а «вопросов нет» было бы неправдой — телефон ждал бы,
            // глядя на висящий вопрос.
            case PhonePageKind.Question:
            case PhonePageKind.Unreadable:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageUnreadable)) + "</p>");
                break;

            case PhonePageKind.Waiting:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageWaiting)) + "</p>");
                lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhonePageWaitingNext)) + "</p>");
                WaitingRow(lines, language);
                break;

            // СВЯЗИ С СЕРВЕРОМ DSH НЕТ — СВОИМИ СЛОВАМИ, а не ожиданием: вопроса ждать неоткуда.
            // Опрос при этом остаётся живым (см. PhonePageView.NoEngine): сервер могут поднять
            // в любую минуту, и тогда вопрос обязан появиться сам.
            case PhonePageKind.NoEngine:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageNoEngine)) + "</p>");
                lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhonePageNoEngineNext)) + "</p>");

                // ТУТ НИЧЕГО НЕ ПРОИСХОДИТ ПО ОПРЕДЕЛЕНИЮ — значит путь «Проверить» нужен ровно
                // так же, как у ожидания: сервер могут поднять на компьютере в любую минуту.
                WaitingRow(lines, language);
                break;

            case PhonePageKind.Answered:
                // ⚠️ ЗАГОЛОВОК УЖЕ ГОВОРИТ «ОТВЕТ ОТПРАВЛЕН» (`Heading`), поэтому здесь его нет:
                // живой прогон на телефоне 13.10.2026 показал одно и то же дважды — заголовком
                // и первой строкой. Осталось «что дальше» — оно и отвечает на вопрос человека.
                lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhonePageAnsweredNext)) + "</p>");
                break;

            case PhonePageKind.Taken:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageTaken)) + "</p>");
                lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhonePageTakenNext)) + "</p>");
                break;

            case PhonePageKind.Cancelled:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageCancelled)) + "</p>");
                lines.Add("<p class=\"hint\">" + Word(language, nameof(PanelStrings.PhonePageCancelledNext)) + "</p>");
                break;

            case PhonePageKind.Problem:
                lines.Add("<p class=\"problem\">" + Trouble(language, view.Note) + "</p>");

                // ПУТЬ ИЗ БЕДЫ. Ссылкой, а не кнопкой: она работает БЕЗ СКРИПТА и не тянет ничего
                // снаружи (это наш же адрес). Норма — GOV.UK «tell the user what's gone wrong and
                // how to fix it» (`30-mobile-web.md` §2, «тупиков не бывает»).
                lines.Add(
                    "<a class=\"ghost\" href=\"/\">" + Word(language, nameof(PanelStrings.PhonePageRepeatButton)) + "</a>");
                break;

            // Состояние, которого страница не знает (запись собрана кем-то другим): показывается
            // то же, что у неразобранного вопроса, — слова и ничего больше. Молчания не бывает.
            default:
                lines.Add("<p class=\"note\">" + Word(language, nameof(PanelStrings.PhonePageUnreadable)) + "</p>");
                break;
        }

        // СТРОКА ОПРОСА — ВНИЗУ, ОДНОЙ ТИХОЙ СТРОКОЙ (решение владельца 11.10.2026): главное
        // действие на экране одно, и «Проверить» рядом с ним читалось бы как второе.
        //
        // ⚠️ **У ВОПРОСА ЭТУ СТРОКУ РИСУЕТ ФОРМА ОТВЕТА, И ТОЛЬКО ОНА** (живой прогон владельца
        // 12.10.2026): прежде строка рисовалась ЗДЕСЬ и ещё раз внутри формы — в отданной странице
        // оказывалось ДВА элемента с классом `live` и одинаковым `id`, а скрипт читал ПЕРВЫЙ,
        // тот, которого человек не нажимал: галочка «не обновлять само» не работала.
        // Одна строка на состояние — и та, которую человек видит.
        //
        // ⚠️ **И У ОЖИДАНИЯ ОНА СВОЯ** (решение владельца 12.10.2026): без слов о нашем опросе
        // (они про наше устройство, а не про дело человека) — её рисует ветка состояния,
        // и второй раз здесь она не появляется. Это уже вторая редакция одной и той же ошибки
        // «строка нарисована дважды», поэтому и названа здесь прямо.
        //
        // ⚠️ **И СЛОВ О НАШЕМ ОПРОСЕ НЕТ НИГДЕ, КРОМЕ ОЖИДАНИЯ** (живой прогон на телефоне
        // 13.10.2026, находка владельца): «Страница проверяет сама каждые {0} с» стояла и на экране
        // вопроса, и на экране исхода. Это **наша механика, а не забота человека**: ему довольно
        // того, что страница обновляется сама, а путь без скрипта остаётся кнопкой «Проверить».
        // Поэтому у живого состояния здесь рисуется ТОЛЬКО ряд «Проверить».
        // ⚠️ **СВЯЗИ НЕТ — ТОЖЕ ЖИВОЕ СОСТОЯНИЕ, И РЯД «ПРОВЕРИТЬ» ЕМУ НУЖЕН** (решение владельца
        // 13.10.2026): сервер могут поднять на компьютере в любую минуту, и «Проверить» — тот самый
        // путь для человека, у которого ничего не происходит. Ряд здесь БЕЗ слов о нашем опросе.
        if (view.Live && view.Kind != PhonePageKind.Question && view.Kind != PhonePageKind.Waiting
            && view.Kind != PhonePageKind.NoEngine)
        {
            CheckRow(lines, language);
        }
    }


    /// <summary>
    /// ТИХИЙ РЯД С «ПРОВЕРИТЬ» — ССЫЛКОЙ, без слов о нашем опросе. Крючок `data-auto` здесь НЕ
    /// ставится: его снимает скрипт, и это правильно **у вопроса и у исходов** (совет «нажмите,
    /// если ничего не происходит» человеку, у которого всё происходит, — шум), а **у ожидания
    /// путь обязан остаться**: там ничего не происходит по определению, и «Проверить» — то самое
    /// «что делать, если ничего не происходит».
    /// </summary>
    private static void WaitingRow(List<string> lines, string language)
    {
        lines.Add("<div class=\"auto\">");
        lines.Add(CheckLink(language));
        lines.Add("</div>");
    }

    private static string CheckLink(string language) =>
        "<a class=\"ghost narrow\" href=\"/\">"
        + Word(language, nameof(PanelStrings.PhonePageCheckButton)) + "</a>";

    /// <summary>
    /// РЯД «ПРОВЕРИТЬ» У ЖИВОГО СОСТОЯНИЯ — с крючком `data-auto`.
    ///
    /// ⚠️ **Скрипт убирает его, как только опрос пошёл** (решение владельца 12.10.2026). Так путь
    /// остаётся и при выключенном, и при сломанном скрипте: нет скрипта — ряд не тронут, и ссылка
    /// работает обычным переходом.
    /// </summary>
    private static void CheckRow(List<string> lines, string language)
    {
        lines.Add("<div class=\"auto\" data-auto>");
        lines.Add(CheckLink(language));
        lines.Add("</div>");
    }

    /// <summary>
    /// ВОПРОС, ЕГО ВАРИАНТЫ И ПОЛЕ «СВОЙ ОТВЕТ» — ОДНОЙ ФОРМОЙ.
    ///
    /// ⚠️ **ОДИН ВОПРОС НА ЭКРАН** (вариант A, решение владельца 11.10.2026): форма несёт ровно
    /// один <c>q&lt;экран&gt;</c>. Полный набор ответов собирает КАЛИТКА из накопленного
    /// (<see cref="PhoneGate"/>) — ответ уходит движку ОДНИМ вызовом, а не по экрану.
    /// </summary>
    private static void AnswerForm(
        List<string> lines, string language, PhonePageView view, PhoneQuestion question)
    {
        var eventId = Escape(view.EventId);

        // ИМЯ ВЫБОРА — НОМЕР ЭКРАНА: у ЭКРАНА 1 это `q0`. Номер экрана (число программы, с нуля)
        // и номер в словах («Вопрос 1 из 2», с единицы) — РАЗНЫЕ числа, и здесь берётся первый:
        // так его называет форма, а перевод «на человеческий» делает счётная строка ниже.
        var name = "q" + view.Screen.ToString(CultureInfo.InvariantCulture);

        lines.Add("<form method=\"post\" action=\"/answer\">");

        // `eventId` скрытым полем: телефон отвечает на ТОТ вопрос, который ему показали, а не на
        // тот, который в это время висит. Калитка сверит его со своим (чужой ход так не снять).
        lines.Add("<input type=\"hidden\" name=\"eventId\" value=\"" + eventId + "\">");

        // СЧЁТ — ТОЛЬКО КОГДА ВОПРОСОВ НЕСКОЛЬКО: «Вопрос 1 из 1» ничего не сообщает, а место
        // занимает (норма GOV.UK: указатель продвижения — можно, а не обязательно). Считается
        // ПО-ЧЕЛОВЕЧЕСКИ: с единицы, а не с нуля — <see cref="PhonePageView.Screen"/> хранит
        // номер экрана числом программы.
        if (view.Total > 1)
        {
            lines.Add(
                "<p class=\"count\">"
                + Words(
                    language,
                    nameof(PanelStrings.PhonePageQuestionCountFormat),
                    view.Screen + 1,
                    view.Total)
                + "</p>");
        }

        var legend = Escape(question.Text);

        // ВОПРОС — В `<legend>` ВНУТРИ `<fieldset>`: так читалка связывает вопрос с его вариантами,
        // а не читает их двумя несвязанными кусками (GOV.UK question pages; `30-mobile-web.md` §8).
        // Заголовок состояния при этом остаётся единственным `h1` страницы.
        lines.Add("<fieldset class=\"group\">");
        lines.Add("<legend class=\"legend\">" + legend + "</legend>");

        // ПОДРОБНОСТЬ — ТИШЕ и БЕЗ ТОЧКИ В КОНЦЕ (норма GOV.UK: подсказка — одна короткая фраза
        // без точек; `_design-standard-draft.md` §2.7). Пусто — подсказки нет вовсе.
        if (!string.IsNullOrEmpty(question.Detail))
            lines.Add("<p class=\"hint\">" + Escape(question.Detail) + "</p>");

        var options = question.Options ?? Array.Empty<string>();

        if (options.Count > 0)
        {
            lines.Add("<div class=\"opts\">");

            for (var option = 0; option < options.Count; option++)
            {
                // ЧТО ЧЕЛОВЕК УЖЕ ВЫБРАЛ — дорисовывается `checked`: после отказа он обязан увидеть
                // свой прежний выбор, а не пустую форму (решение владельца 11.10.2026). Свой ответ
                // (словами) выбором не считается — он живёт в поле ниже.
                var picked = Own(view, question.Id).Length == 0
                    && Chosen(view, question.Id) == Number(option);

                var chosen = picked ? " checked" : string.Empty;

                // ВАРИАНТ — НАЖИМАЕМАЯ СТРОКА ЦЕЛИКОМ: `<label>` обнимает и кружок, и подпись,
                // поэтому попасть пальцем можно в любое место строки, а не в кружок 24 точки.
                lines.Add(
                    "<label class=\"opt\"><span class=\"opt-body\"><input class=\"opt-input\" type=\"radio\""
                    + " name=\"" + name + "\" value=\"" + Number(option) + "\"" + chosen
                    + "><span class=\"opt-text\">" + Escape(options[option]) + "</span></span></label>");
            }

            lines.Add("</div>");
        }

        lines.Add("</fieldset>");

        // СВОЙ ОТВЕТ — НЕОБЯЗАТЕЛЬНОЕ ПОЛЕ: подпись над полем, объяснение — ПОДСКАЗКОЙ НИЖЕ,
        // а `placeholder` короткий (норма GOV.UK: подпись не прячется в placeholder —
        // `30-mobile-web.md` §4). Пока выбрано (то есть когда идёт ввод), поле уходит целиком.
        lines.Add("<div class=\"own\">");
        lines.Add(
            "<label class=\"field-label\" for=\"" + name + "-own\">"
            + Word(language, nameof(PanelStrings.PhonePageOwnLabel)) + "</label>");
        lines.Add(
            "<input class=\"own-input\" id=\"" + name + "-own\" name=\"" + name + "-own\" type=\"text\""
            + " autocomplete=\"off\" maxlength=\"200\" placeholder=\""
            + Word(language, nameof(PanelStrings.PhonePageOwnPlaceholder)) + "\" value=\""
            + Escape(Own(view, question.Id)) + "\">");
        lines.Add("</div>");

        lines.Add("<button class=\"action wide\" type=\"submit\">"
            + Word(language, nameof(PanelStrings.PhonePageAnswerButton)) + "</button>");

        // ⚠️ ЗДЕСЬ ОСТАЁТСЯ ТОЛЬКО РЯД «ПРОВЕРИТЬ» (живой прогон 13.10.2026, находка владельца):
        // слов «страница проверяет сама каждые N с» на экране вопроса быть не должно — это наша
        // механика. Ряд убирает скрипт, когда опрос пошёл; без скрипта он остаётся обычной ссылкой.
        CheckRow(lines, language);
        lines.Add("</form>");

        // ОТМЕНА — ОТДЕЛЬНОЙ формой и с предупреждением: вопрос без ответившего висит вечно
        // (замер `docs\PHONE-LIVE.md` §4), поэтому выход нужен — но отмена останавливает работу
        // агента, и человек обязан прочитать об этом ДО нажатия, а не догадаться после.
        lines.Add("<hr>");
        lines.Add("<form method=\"post\" action=\"/cancel\">");
        lines.Add("<input type=\"hidden\" name=\"eventId\" value=\"" + eventId + "\">");
        lines.Add("<p class=\"warn\">" + Word(language, nameof(PanelStrings.PhonePageCancelWarning)) + "</p>");
        lines.Add("<button class=\"ghost\" type=\"submit\">"
            + Word(language, nameof(PanelStrings.PhonePageCancelButton)) + "</button>");
        lines.Add("</form>");
    }

    /// <summary>Что человек выбрал на этом вопросе раньше (пусто — ничего).</summary>
    private static string Chosen(PhonePageView view, string questionId) =>
        view.Answers is not null && view.Answers.TryGetValue(questionId, out var answer) ? answer : string.Empty;

    /// <summary>
    /// СВОЙ ОТВЕТ ЧЕЛОВЕКА, ЕСЛИ ОН ЕГО ПИСАЛ. В <see cref="PhonePageView.Answers"/> лежит ровно
    /// то, что уйдёт движку, и отличить «выбран вариант» от «написан свой» можно по одному признаку:
    /// **свой ответ не является номером варианта.** Так поле восстанавливается после отказа, и
    /// человек не набирает заново (решение владельца 11.10.2026).
    ///
    /// ⚠️ Выбор и свой ответ неразличимы в ответе движку НАМЕРЕННО: они уходят одной строкой
    /// (<c>selected</c>), и это форма движка, а не наша. Различимы они в СЛОВАХ — и это в отчёте.
    /// </summary>
    private static string Own(PhonePageView view, string questionId)
    {
        var answer = Chosen(view, questionId);

        // Номер варианта (в том числе «0») — это выбор, а не свой текст.
        return int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? string.Empty
            : answer;
    }

    /// <summary>
    /// ЕДИНСТВЕННЫЙ НАШ СКРИПТ — ЖИВОЕ ОБНОВЛЕНИЕ БЕЗ ПЕРЕЗАГРУЗКИ.
    ///
    /// Что он делает, по порядку:
    ///
    /// * находит карточку по `data-live`; её нет (вход, беда) — не делает НИЧЕГО и никуда не ходит;
    /// * раз в <see cref="PollMs"/> просит <c>location.pathname + "?part=card"</c> — СВОЙ ЖЕ адрес,
    ///   ни одного чужого хоста, ни одной сторонней библиотеки;
    /// * подменяет ТОЛЬКО карточку;
    /// * **страница не видима — опрос СТОИТ ВСЕЦЕЛО** (не «не подменяем», а «не спрашиваем»):
    ///   запросов нет вовсе, и человек, ушедший с экрана, их не оставит; вернулся на страницу —
    ///   опрос возобновляется сам (`visibilitychange`, и первый же круг спрашивает снова);
    /// * **выбран вариант ответа (`:checked`) — опрос приостановлен**: подмена карточки съела бы
    ///   выбор человека, а «введённое не пропадает без действия человека» — уже принятое правило;
    /// * **в поле «свой ответ» есть текст — опрос приостановлен** по той же причине;
    /// * `meta refresh` не участвует вовсе;
    /// * **ряд со ссылкой «Проверить» УБИРАЕТСЯ, как только опрос пошёл** (решение владельца
    ///   12.10.2026): путь остаётся и при выключенном, и при сломанном скрипте — нет скрипта,
    ///   ряд не тронут и ссылка работает. Убирается он ОДИН раз и не возвращается: совет
    ///   «нажмите, если ничего не происходит» человеку, у которого всё происходит, — шум.
    ///
    /// ⚠️ **ГАЛОЧКИ «НЕ ОБНОВЛЯТЬ САМО» ЗДЕСЬ БОЛЬШЕ НЕТ** (убрана владельцем 12.10.2026): карточку
    /// подменяют ЦЕЛИКОМ, и вместе с ней приходила НОВАЯ, снятая галочка — отметка человека
    /// терялась. Остановка теперь не «орган», который можно потерять, а свойство самой страницы:
    /// нет видимой страницы — нет опроса.
    ///
    /// ⚠️ **Доводов у него нет ни одного** — и это правило, а не мелочь: в скрипт не подставляется
    /// ни язык, ни адрес, ни время. Один и тот же скрипт на всех страницах и всех языках означает,
    /// что подменить в нём нечего.
    /// </summary>
    private static string Poll() => string.Join(
        "\n",
        "(function(){",
        "\"use strict\";",
        "var card=document.querySelector(\".card[data-live]\");",
        "if(!card)return;",
        "if(location.protocol.indexOf(\"http\")!==0)return;",
        // ОПРОС ПОШЁЛ — РЯД «ПРОВЕРИТЬ» УХОДИТ. Снятие стоит ЗДЕСЬ, а не внутри круга: путь
        // «нажать, если ничего не происходит» нужен ровно до того, как стало происходить.
        // Нет скрипта — ряда не касается никто, и ссылка работает обычным переходом.
        "var row=document.querySelector(\"[data-auto]\");",
        "if(row&&row.parentNode)row.parentNode.removeChild(row);",
        "var PAUSE=" + PollMs.ToString(CultureInfo.InvariantCulture) + ";",
        "var timer=null;",
        // СТРАНИЦА НЕ ВИДИМА — НЕ СПРАШИВАЕМ ВООБЩЕ. Не «не подменяем»: запросов нет вовсе.
        "function hidden(){return document.visibilityState!==\"visible\";}",
        // НАБРАННОЕ НЕ ПРОПАДАЕТ БЕЗ ДЕЙСТВИЯ ЧЕЛОВЕКА: свой текст — пауза.
        "function busy(){var own=card.querySelector(\"input[name$='-own']\");return !!own&&own.value!==\"\";}",
        // ВЫБРАННЫЙ ВАРИАНТ — ТОЖЕ ПАУЗА: подмена карточки съела бы выбор человека.
        "function picked(){return !!card.querySelector(\"input[type='radio']:checked\");}",
        "function plan(){timer=setTimeout(tick,PAUSE);}",
        "function tick(){",
        "if(hidden()||busy()||picked()){plan();return;}",
        "fetch(location.pathname+\"?part=" + CardPart + "\")",
        ".then(function(a){return a.ok?a.text():\"\";})",
        ".then(function(t){if(t.indexOf(\"<\")===0&&!hidden()&&!busy()&&!picked())card.innerHTML=t;plan();})",
        ".catch(function(){plan();});",
        "}",
        // ВЕРНУЛСЯ НА СТРАНИЦУ — СПРАШИВАЕМ СРАЗУ, не дожидаясь своей очереди.
        "document.addEventListener(\"visibilitychange\",function(){",
        "if(!hidden()){clearTimeout(timer);tick();}",
        "});",
        "plan();",
        "})();");

    /// <summary>
    /// РАЗОБРАТЬ <c>questions</c> ИЗ КАДРА ДВИЖКА: массив объектов
    /// <c>[{"id":"…","header":"…","question":"…","detail":"…","options":[{"label":"…","description":"…"}]}]</c>.
    ///
    /// ⚠️ **Разбор ТЕРПИМЫЙ, и это не мягкость, а условие работы.** Форму кадра движок не
    /// обещал: на кривом вопросе страница обязана сказать «разобрать не смогла»
    /// (<see cref="PhonePageKind.Unreadable"/>), а не уронить калитку исключением. Поэтому битый
    /// JSON, не массив, запись без <c>id</c> и запись без вариантов ПРОПУСКАЮТСЯ молча — и только
    /// они: всё, что разобралось, обязано дойти до телефона.
    ///
    /// ⚠️ **Порядок вопросов сохраняется как пришёл**, и это часть ответа: вариант A показывает
    /// вопросы ПО ОДНОМУ в этом порядке, а ответы уходят движку в нём же.
    /// </summary>
    public static IReadOnlyList<PhoneQuestion> Parse(string? questionsJson)
    {
        var questions = new List<PhoneQuestion>();

        if (string.IsNullOrWhiteSpace(questionsJson)) return questions;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(questionsJson);
        }
        catch (JsonException)
        {
            return questions;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return questions;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var id = Text(element, "id");

                // БЕЗ ИМЕНИ ВОПРОСА ОТВЕТ УЙДЁТ В НИКУДА: движок ищет вопрос по `id`, и вариант
                // «подставить что-нибудь» означал бы ответ не на тот вопрос.
                if (string.IsNullOrWhiteSpace(id)) continue;

                var options = OptionLabels(element);

                // БЕЗ ВАРИАНТОВ ОТВЕЧАТЬ НЕЧЕМ: пустая форма хуже её отсутствия — человек жал бы
                // кнопку и не понимал, почему ничего не происходит.
                if (options.Count == 0) continue;

                questions.Add(new PhoneQuestion(
                    id,
                    Text(element, "header"),
                    Text(element, "question"),
                    Text(element, "detail"),
                    options));
            }
        }

        return questions;
    }

    /// <summary>
    /// СЛОВА БЕДЫ. Пустое место вместо слов было бы молчанием, а молчание телефон читает как
    /// «всё в порядке»: поэтому пустая беда говорится словами «разобрать не смогла».
    /// </summary>
    private static string Trouble(string language, string? note) =>
        string.IsNullOrWhiteSpace(note)
            ? Word(language, nameof(PanelStrings.PhonePageUnreadable))
            : Escape(note);

    /// <summary>Слово панели на языке телефона — ПО ИМЕНИ ключа (почему так — см. <see cref="Html"/>).</summary>
    private static string Word(string language, string key) => Escape(Loc.TIn(language, key));

    /// <summary>
    /// СЛОВО С ПОДСТАНОВКАМИ. Культура — <see cref="CultureInfo.InvariantCulture"/>: страница
    /// отдаётся телефону, и цифра в ней обязана быть одна и та же при любой локали машины.
    /// </summary>
    private static string Words(string language, string key, params object[] args) =>
        Escape(string.Format(CultureInfo.InvariantCulture, Loc.TIn(language, key), args));

    /// <summary>Число в разметке — всегда инвариантной культурой (см. <see cref="Words"/>).</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ЭКРАНИРОВАНИЕ. Строки словаря пишет человек-переводчик, а текст вопроса — агент: и тот
    /// и другой однажды вставят знак <c>&lt;</c>. Тогда страница сломается у человека, а не у всех.
    /// </summary>
    private static string Escape(string? text) => (text ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Строковое поле записи: не строка и не запись — пусто, а не исключение.</summary>
    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// ПОДПИСИ ВАРИАНТОВ В ПОРЯДКЕ ПРИХОДА. Подпись — <c>label</c>; пусто — годится
    /// <c>description</c> (телефону нужно знать, за что он голосует, и вариант без слов ему
    /// бесполезен); строка вместо записи — тоже подпись. Совсем без слов вариант пропускается.
    /// </summary>
    private static List<string> OptionLabels(JsonElement question)
    {
        var labels = new List<string>();

        if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return labels;

        foreach (var option in options.EnumerateArray())
        {
            var label = Text(option, "label");

            if (label.Length == 0) label = Text(option, "description");

            if (label.Length == 0 && option.ValueKind == JsonValueKind.String)
                label = option.GetString() ?? string.Empty;

            if (label.Length == 0) continue;

            labels.Add(label);
        }

        return labels;
    }
}

/// <summary>
/// ОТВЕТ ТЕЛЕФОНА: ФОРМА В СЛОВАХ — И СЛОВА В JSON.
///
/// **Зачем это отдельно от страницы.** Здесь живёт единственное место, где выбор ЧЕЛОВЕКА
/// превращается в то, что уходит движку. Проверить это на живой калитке значило бы поднимать
/// движок и ходить по сети; здесь же всё чистое — значит и проверяется перебором.
///
/// ⚠️ **ПОДПИСЬ БЕРЁТСЯ ИЗ СВОЕГО ВОПРОСА, А НЕ ИЗ ФОРМЫ.** Форма присылает только НОМЕР варианта
/// (<c>q0=1</c>), и подставить чужой текст телефон не может: чужую подпись взять неоткуда.
/// Именно поэтому <see cref="Build"/> не доверяет присланным строкам ни в чём, кроме номера.
///
/// ⚠️ **А СВОЙ ОТВЕТ ЧЕЛОВЕКА УХОДИТ КАК ЕСТЬ** — той же строкой <c>selected</c>, что и выбранный
/// вариант (`docs\PHONE-LIVE.md` §3.5: форма движка одна). Своего поля для него у движка нет,
/// и выдумывать его панель не вправе.
/// </summary>
public static class PhoneAnswer
{
    /// <summary>
    /// ОТВЕТ УХОДИТ ДВИЖКУ КАК ЕСТЬ, и экранирование здесь — про КАВЫЧКИ, ОБРАТНУЮ ЧЕРТУ И ЗНАКИ
    /// УПРАВЛЕНИЯ, а не про буквы. Ослабленное экранирование выбрано намеренно: строка уходит
    /// движку ТЕЛОМ ЗАПРОСА и в разметку не попадает никогда, а кириллическая подпись, записанная
    /// как <c>\u0414\u0430</c>, читается человеком в журнале и в проверке как мусор.
    /// </summary>
    private static readonly JsonSerializerOptions AnswerJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// РАЗОБРАТЬ ТЕЛО <c>application/x-www-form-urlencoded</c> ОДНОГО ЭКРАНА:
    /// <c>eventId=…&amp;q0=1&amp;q0-own=своё</c>. <c>null</c> — тела нет, оно не форма,
    /// <c>eventId</c> пуст или не назван номер экрана.
    ///
    /// ⚠️ <c>eventId</c> обязателен: без него ответ ушёл бы «вообще», и калитка не смогла бы
    /// сверить, на ТОТ ли вопрос отвечает телефон. Пустое значение — тоже отказ: пустая строка
    /// выглядит как поле, а не как ответ.
    ///
    /// ⚠️ Имя поля — <c>q&lt;экран&gt;</c>, и экран берётся ИЗ НЕГО: у варианта A экран показывает
    /// ровно один вопрос, поэтому «номер вопроса» и «номер экрана» — одно и то же число.
    /// </summary>
    public static PhoneAnswerFields? ParseForm(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        string? eventId = null;
        string? choice = null;
        var own = string.Empty;
        var screen = -1;

        foreach (var pair in body.Split('&'))
        {
            if (pair.Length == 0) continue;

            var cut = pair.IndexOf('=');

            // Пара без знака «=» — не поле формы: пропускается, а не читается как «ключ без
            // значения». Иначе JSON машинного пути (`{"eventId":"…"}`) притворился бы формой.
            if (cut < 0) continue;

            var name = Decode(pair[..cut]);
            var value = Decode(pair[(cut + 1)..]);

            if (string.Equals(name, "eventId", StringComparison.Ordinal))
            {
                eventId = value;
                continue;
            }

            // `q0-own` — СВОЙ ОТВЕТ. Проверяется РАНЬШЕ выбора: имя выбора — префикс этого имени,
            // и разобранное вторым оно съело бы свой ответ.
            //
            // ⚠️ **НОМЕР ЭКРАНА БЕРЁТСЯ И ОТСЮДА** (живой прогон владельца 12.10.2026): человек,
            // ответивший ТОЛЬКО СЛОВАМИ, вариант не выбирает — поля `q<N>` в форме НЕТ вовсе,
            // и без этой строки форма отвергалась как «не разобрана» (в живом журнале — пять таких
            // отказов, а ответ человека не уходил). Цифры стоят между `q` и `-own` и означают ровно
            // тот же номер экрана, что и у выбора.
            if (name.StartsWith('q') && name.EndsWith("-own", StringComparison.Ordinal))
            {
                if (int.TryParse(
                        name[1..^"-own".Length],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var ownScreen)
                    && screen < 0)
                {
                    screen = ownScreen;
                }

                own = value;
                continue;
            }

            // `q0`, `q1`, … — номер ЭКРАНА. Всё прочее (в том числе чужие поля) — не наше дело.
            if (name.Length < 2 || name[0] != 'q') continue;
            if (!int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;

            screen = index;
            choice = value;
        }

        if (string.IsNullOrWhiteSpace(eventId)) return null;

        // ЭКРАН НЕ НАЗВАН — ЭТО НЕ НАША ФОРМА: без номера калитка не знает, какому вопросу
        // принадлежит выбор, и «подставить нулевой» значило бы ответить не на тот вопрос.
        if (screen < 0) return null;

        return new PhoneAnswerFields(eventId, screen, choice ?? string.Empty, own);
    }

    /// <summary>
    /// ТЕЛО — ФОРМА? Форма — это пары `имя=значение`, разделённые `&amp;`, и хотя бы одна из них
    /// обязана нести знак «=» ДО первого `&amp;` (то есть быть парой, а не «текстом со знаком
    /// равенства где-то дальше»).
    ///
    /// ⚠️ **JSON ФОРМОЙ НЕ СЧИТАЕТСЯ**, хотя знак «=» в нём бывает (`{"a":"b=c"}`). Машинный путь
    /// (`{eventId, answers}`) обязан остаться машинным: ответь ему страницей — и скрипт получил бы
    /// HTML вместо слов. Проверка эта — ОДНА на обе двери (`/answer` и `/cancel`) и на вход
    /// (код формы), поэтому расхождений между ними быть не может.
    ///
    /// ⚠️ **Это НЕ «наша форма ответа».** Тело `eventId=…` формой ЯВЛЯЕТСЯ, и им отвечает
    /// `POST /cancel`; формой ОТВЕТА оно становится только с именем выбора (<c>q0</c>).
    /// Разница нужна затем, чтобы человеку отвечали СТРАНИЦЕЙ там, где отвечает человек,
    /// и словами там, где отвечает машина (`PhoneGate.Answer`).
    /// </summary>
    public static bool LooksForm(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var text = body!.TrimStart();

        // Машинный путь начинается с `{` — это JSON, и формой он не становится никогда.
        if (text.StartsWith('{')) return false;

        var first = text.Split('&')[0];

        return first.Contains('=', StringComparison.Ordinal);
    }

    /// <summary>
    /// ОДНО ПОЛЕ ФОРМЫ ПО ИМЕНИ — для дверей, у которых поле одно (вход: `code`).
    ///
    /// `null` — тела нет, оно не форма или такого поля нет. Значение раскодируется ТЕМ ЖЕ способом,
    /// что и у ответа (`+` — пробел, `%D0%B0` — «а»): второй разбор однажды разошёлся бы с первым,
    /// и код с кириллицей в одном месте читался бы, а в другом нет.
    /// </summary>
    public static string? Field(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(name)) return null;

        foreach (var pair in body!.Split('&'))
        {
            var cut = pair.IndexOf('=');

            if (cut < 0) continue;
            if (!string.Equals(Decode(pair[..cut]), name, StringComparison.Ordinal)) continue;

            return Decode(pair[(cut + 1)..]);
        }

        return null;
    }

    /// <summary>
    /// СОБРАТЬ ОТВЕТ ДВИЖКУ из вопросов и УЖЕ СОБРАННЫХ ответов (по `id` вопроса):
    /// <c>[{"id":"&lt;id&gt;","selected":["&lt;строка&gt;"]}]</c>. <c>null</c> — если хоть на один
    /// вопрос ответа нет.
    ///
    /// ⚠️ **«Не каждый отвечен» — ОТКАЗ ЦЕЛИКОМ, а не «сколько поняли»:** неполный ответ движок
    /// примет за ответ, и агент пойдёт дальше по решению, которого человек не принимал. Поэтому
    /// порядок ответов — ПОРЯДОК ВОПРОСОВ, а не порядок экранов: движок ищет вопрос по `id`.
    ///
    /// JSON собирается ВРУЧНУЮ, а строки отдаются <see cref="JsonSerializer"/> (свои кавычки
    /// однажды пропустили бы кавычку внутри подписи — и ответ стал бы другим ответом).
    /// </summary>
    public static string? Build(IReadOnlyList<PhoneQuestion> questions, IReadOnlyDictionary<string, string> answers)
    {
        if (questions is null || answers is null) return null;
        if (questions.Count == 0) return null;

        var built = new List<string>(questions.Count);

        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];

            if (!answers.TryGetValue(question.Id, out var answer) || answer.Length == 0) return null;

            built.Add(
                "{\"id\":" + JsonSerializer.Serialize(question.Id, AnswerJson)
                + ",\"selected\":[" + JsonSerializer.Serialize(answer, AnswerJson) + "]}");
        }

        return "[" + string.Join(",", built) + "]";
    }

    /// <summary>
    /// ЗНАКИ ФОРМЫ В СЛОВА. Кодировка — UTF-8: страница объявляет <c>charset=utf-8</c>, и телефон
    /// шлёт свои знаки именно в ней. Кривая последовательность остаётся как пришла: разбор формы
    /// не имеет права уронить калитку из-за одного испорченного знака в теле запроса.
    /// </summary>
    private static string Decode(string value) => value.Length == 0 ? string.Empty : WebUtility.UrlDecode(value);
}
