using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СТРАНИЦА ТЕЛЕФОНА (этапы F2 и F5) — ЧИСТОЕ: HTML, разбор вопроса и сборка ответа.
///
/// **Что здесь и чего здесь НЕТ.** Здесь проверяется ТО, ЧТО ОТДАЁТСЯ ТЕЛЕФОНУ, и то, что
/// из его слов получается. Ни окна, ни сети, ни Avalonia, ни движка здесь не нужно: страница —
/// чистая функция (<see cref="PhonePage.Html"/>), разбор кадра — тоже (<see cref="PhonePage.Parse"/>),
/// и ответ собирается из СВОИХ вопросов (<see cref="PhoneAnswer.Build"/>). Поэтому обычный
/// <c>[Fact]</c>, без <c>[AvaloniaFact]</c>. **Живой путь** (калитка на петле и свободном порту)
/// стерегут <see cref="PhoneGateTests"/> и <see cref="PhoneLookTests"/>.
///
/// **Что стерегут эти проверки.** Первое — ЗАМОК, и он после F5 звучит так: **ни одного внешнего
/// ресурса и ни одного чужого адреса; наш скрипт ровно один; обращается только к своему адресу**
/// (решение владельца 11.10.2026). Прежняя редакция запрещала на странице ЛЮБОЙ скрипт и держала
/// автообновление на <c>meta refresh</c> — это противоречие снято: <c>meta refresh</c> отменён
/// (WCAG 2.2 F41/SC 3.2.5; SC 2.2.2 требует механизма остановки), а один СВОЙ скрипт разрешён.
/// Второе — «человеку всегда говорится словами»: и вопрос, и ожидание, и беда. Третье — «телефон
/// не может подделать ответ»: форма присылает НОМЕР варианта, а подпись берётся из своего вопроса.
///
/// ⚠️ **Слово берётся ПО ИМЕНИ КЛЮЧА** (<c>nameof(PanelStrings.X)</c>), а не готовой строкой
/// <c>PanelStrings.X</c> — ровно так же, как это делает сама страница. Готовая строка уже
/// переведена на язык панели, и подстановка её в <see cref="Loc.TIn"/> вернула бы её как есть:
/// проверка, написанная на готовых строках, «сходилась» бы на любой локали и не заметила бы,
/// что довод «язык телефона» стал мёртвым.
/// </summary>
public class PhonePageTests
{
    /// <summary>`eventId` вопроса — как его присылает движок (вид значения здесь не важен,
    /// важно, что он один и тот же и что он доезжает до формы).</summary>
    private const string EventId = "8b4b3ed5-0000-4000-8000-000000000001";

    // ---- подставные вопросы -----------------------------------------------------------------

    /// <summary>Вопрос агента: по умолчанию один, с двумя вариантами.</summary>
    private static PhoneQuestion Question(
        string id = "q1",
        string header = "Вопрос",
        string text = "Ставить задачу?",
        string detail = "Ход ждёт ответа",
        IReadOnlyList<string>? options = null) =>
        new(id, header, text, detail, options ?? new[] { "Да", "Нет" });

    /// <summary>
    /// СТРАНИЦА ВОПРОСА — ТЕМ ВИДОМ, КАКИМ ЕЁ СОБИРАЕТ КАЛИТКА (вариант A): ОДИН вопрос на экране,
    /// номер экрана и общее число, живое обновление. Второй вопрос передаётся только затем, чтобы
    /// счёт «Вопрос 1 из 2» было чем проверить: странице он не отдаётся (её дело — один экран).
    /// </summary>
    private static PhonePageView Asked(params PhoneQuestion[] questions)
    {
        var all = questions.Length == 0 ? new[] { Question() } : questions;

        return new PhonePageView(
            PhonePageKind.Question,
            EventId,
            new[] { all[0] },
            Live: true,
            Screen: 0,
            Total: all.Length);
    }

    /// <summary>Слово панели на названном языке — тем же способом, каким его берёт страница.</summary>
    private static string Word(string language, string key) => Loc.TIn(language, key);

    /// <summary>Слово на русском (язык прогона проверок закреплён в <see cref="TestLocale"/>).</summary>
    private static string Ru(string key) => Word("ru", key);

    /// <summary>Слово словаря с подставленными значениями — как его соберёт страница.</summary>
    private static string Ru(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Ru(key), args);

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

    /// <summary>Ответ по `id` вопроса — то, чем калитка кормит сборку ответа.</summary>
    private static Dictionary<string, string> Said(params (string Id, string Answer)[] answers)
    {
        var said = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, answer) in answers) said[id] = answer;

        return said;
    }

    // ---- 1. замок: ничего снаружи, один свой скрипт, только свой адрес -----------------------

    /// <summary>
    /// СТРАНИЦА НЕ ТЯНЕТ НИЧЕГО СНАРУЖИ — НИ НА ОДНОМ СОСТОЯНИИ.
    ///
    /// ⚠️ **Замок после F5 (решение владельца 11.10.2026):** ни одного внешнего ресурса и ни одного
    /// чужого адреса; наш скрипт ровно один; обращается только к своему адресу. Поэтому
    /// `<c>&lt;script</c>` из запретов УБРАН (он теперь есть и он свой), а `<c>http://</c>`,
    /// `<c>https://</c>`, `<c>&lt;link</c>`, `<c>&lt;img</c>`, `<c>@import</c>`, `<c>url(</c>`,
    /// `<c>src=</c>` — остались: ими и тянется чужое.
    ///
    /// Мутации, которые обязаны упасть здесь: «подключить шрифт» (<c>&lt;link&gt;</c>),
    /// «показать картинку-логотип» (<c>&lt;img&gt;</c>), «подтянуть оформление» (<c>@import</c>
    /// или <c>url(</c>), «отправить форму на чужой адрес» (<c>formaction</c>), «дотянуться
    /// до чужого хоста скриптом».
    ///
    /// ⚠️ **Почему на странице вопроса проверка НЕ может быть просто «нет <c>https://</c>».**
    /// Текст вопроса пишет агент, и он вправе упомянуть ссылку — телефону её и показывают СЛОВАМИ.
    /// Утечкой была бы не ссылка в тексте, а ЗАГРУЗКА по ней: поэтому на этой странице
    /// запрещаются средства загрузки (теги, атрибуты, <c>@import</c>), а сама ссылка обязана
    /// остаться текстом.
    /// </summary>
    [Fact]
    public void Страница_не_тянет_ничего_снаружи()
    {
        var waiting = PhonePage.Html("ru", PhonePageView.Waiting());

        // На странице ожидания нет ни одной ссылки и ни одного средства загрузки — целиком.
        foreach (var forbidden in new[]
                 {
                     "http://", "https://", "<link", "<img", "<iframe", "@import",
                     "url(", "src=", "formaction",
                 })
        {
            Assert.DoesNotContain(forbidden, waiting, StringComparison.OrdinalIgnoreCase);
        }

        // `<a href="/">` — СВОЙ адрес, и он обязан быть: без него кнопка «Проверить» не работает
        // без скрипта. Чужим адресом он не становится, и это проверяется отдельно ниже.
        Assert.Contains("href=\"/\"", waiting, StringComparison.Ordinal);

        // Страница ВОПРОСА — с текстом, в котором есть и ссылка, и знаки разметки.
        var asked = PhonePage.Html("ru", Asked(Question(
            text: "Смотри https://example.invalid/ и <b>тег</b>",
            detail: "a > b & c")));

        foreach (var forbidden in new[]
                 {
                     "<link", "<img", "<iframe", "@import", "url(", "src=", "formaction",
                 })
        {
            Assert.DoesNotContain(forbidden, asked, StringComparison.OrdinalIgnoreCase);
        }

        // Ссылка из вопроса осталась СЛОВАМИ: она видна человеку, но загружать по ней нечего.
        Assert.Contains("https://example.invalid/", asked, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;тег&lt;/b&gt;", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// НАШ СКРИПТ РОВНО ОДИН, И ОБРАЩАЕТСЯ ОН ТОЛЬКО К СВОЕМУ АДРЕСУ.
    ///
    /// **От какого случая.** Автообновление без перезагрузки: страница сама просит у себя карточку
    /// (<c>GET /?part=card</c>) и подменяет только её. Это единственное место, где страница вообще
    /// куда-то ходит, поэтому оно и стережётся так строго: ровно один тег, ни одной сторонней
    /// библиотеки, ни одного чужого хоста.
    ///
    /// Мутации, которые обязаны упасть здесь: «добавить второй скрипт» · «подключить чужой
    /// скрипт» (<c>src=</c>) · «сходить на чужой хост» (<c>https://…</c> в <c>fetch</c>) ·
    /// «сходить на чужой путь» (адрес не <c>location.pathname</c>).
    /// </summary>
    [Fact]
    public void Наш_скрипт_ровно_один_и_только_свой_адрес()
    {
        foreach (var (name, html) in EveryPage())
        {
            var scripts = Occurrences(html, "<script");

            Assert.True(scripts <= 1, $"{name}: скриптов на странице больше одного ({scripts})");
        }

        var page = PhonePage.Html("ru", PhonePageView.Waiting());

        Assert.Equal(1, Occurrences(page, "<script"));
        Assert.Equal(1, Occurrences(page, "</script>"));

        // ЕДИНСТВЕННЫЙ ЗАПРОС — НА СВОЙ ЖЕ АДРЕС, и это видно в самом скрипте.
        Assert.Contains("fetch(location.pathname+\"?part=card\")", page, StringComparison.Ordinal);

        // НИ ОДНОГО ЧУЖОГО ХОСТА: `http://` и `https://` на странице нет вовсе (в том числе
        // в скрипте), и ни одного `src=`.
        Assert.DoesNotContain("http://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// `META REFRESH` НА СТРАНИЦЕ НЕТ ВОВСЕ — НИ НА ОДНОМ СОСТОЯНИИ.
    ///
    /// **От какого случая.** Прежде автообновление держалось на <c>&lt;meta http-equiv="refresh"&gt;</c>,
    /// и это противоречило сразу двум вещам: WCAG 2.2 F41/SC 3.2.5 (перезагрузка без спроса —
    /// изменение контекста) и SC 2.2.2 (у любого самообновления обязан быть механизм остановки,
    /// а у meta refresh его нет). Вдобавок перезагрузка СТИРАЛА уже отмеченное (замер владельца
    /// 11.10.2026). Решение владельца: `meta refresh` ОТМЕНЁН.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть meta refresh» · «вернуть его только
    /// у ожидания».
    /// </summary>
    [Fact]
    public void Мета_обновления_на_странице_нет_вовсе()
    {
        foreach (var (name, html) in EveryPage())
        {
            Assert.True(
                html.IndexOf("http-equiv", StringComparison.OrdinalIgnoreCase) < 0,
                $"{name}: на странице есть meta refresh");
        }
    }

    // ---- 2. вопрос показан словами, ответ — выбором -----------------------------------------

    /// <summary>
    /// ВОПРОС ПОКАЗАН СЛОВАМИ, А ВАРИАНТЫ — КНОПКАМИ ВЫБОРА. Проверяется всё, чем отвечает
    /// телефон: куда шлёт форма, что несёт вместе с ответом и чем становится выбор.
    ///
    /// Мутации, которые обязаны упасть здесь: «убрать скрытый <c>eventId</c>» (ответ ушёл бы
    /// неизвестно на что), «сделать варианты текстом вместо радиокнопок» (ответить нечем),
    /// «потерять подпись варианта», «отправить форму не на <c>/answer</c>».
    /// </summary>
    [Fact]
    public void Вопрос_показан_словами_и_варианты_кнопками_выбора()
    {
        var html = PhonePage.Html("ru", Asked());

        Assert.Contains("Ставить задачу?", html, StringComparison.Ordinal);
        Assert.Contains("Ход ждёт ответа", html, StringComparison.Ordinal);

        Assert.Contains("<form method=\"post\" action=\"/answer\">", html, StringComparison.Ordinal);
        Assert.Contains("name=\"eventId\" value=\"" + EventId + "\"", html, StringComparison.Ordinal);

        Assert.Contains("type=\"radio\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q0\" value=\"0\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q0\" value=\"1\"", html, StringComparison.Ordinal);

        // ПОДПИСЬ ОБНЯТА СТРОКОЙ ЦЕЛИКОМ: попадать пальцем можно в любую точку строки,
        // а не в кружок 24 точки.
        Assert.Contains("class=\"opt\"><span class=\"opt-body\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"opt-text\">Да</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"opt-text\">Нет</span>", html, StringComparison.Ordinal);

        // Кнопка ответа — словами словаря.
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageAnswerButton)), html, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВОПРОС — В `<legend>` ВНУТРИ `<fieldset>`: читалка связывает вопрос с его вариантами.
    ///
    /// Норма — GOV.UK question pages (разметка `fieldset` + `legend` с вопросом); у нас это
    /// названо в `_design-library\30-mobile-web.md` §8. Мутации, которые обязаны упасть здесь:
    /// «убрать fieldset» · «убрать legend» · «оставить legend пустым» (читалка скажет «группа»
    /// без имени).
    /// </summary>
    [Fact]
    public void Вопрос_живёт_в_fieldset_с_legend()
    {
        var html = PhonePage.Html("ru", Asked(Question(text: "Делать?")));

        Assert.Contains("<fieldset class=\"group\">", html, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"legend\">Делать?</legend>", html, StringComparison.Ordinal);
        Assert.Contains("</fieldset>", html, StringComparison.Ordinal);

        // И ВОПРОС ТАМ ВСЕГО ОДИН — вариант A: два `legend` на странице значили бы, что экран
        // показывает больше одного вопроса.
        Assert.Equal(1, Occurrences(html, "<legend"));
    }

    /// <summary>
    /// ОДИН ВОПРОС НА ЭКРАН, А СЧЁТ СТРОКИ — ТОЛЬКО КОГДА ВОПРОСОВ НЕСКОЛЬКО.
    ///
    /// Вариант A (решение владельца 11.10.2026): кнопка отвечает ровно на показанный вопрос,
    /// а строка «Вопрос N из M» говорит, сколько их всего. Мутации, которые обязаны упасть здесь:
    /// «показать все вопросы на одном экране» (два `q`-имени в одной форме) · «показывать
    /// <c>Вопрос 1 из 1</c>» (счёт, который ничего не сообщает) · «нумеровать с нуля в словах».
    /// </summary>
    [Fact]
    public void Один_вопрос_на_экран_и_счёт_только_когда_их_несколько()
    {
        // ЭКРАН ЕДИНСТВЕННОГО ВОПРОСА: ни счётной строки, ни чужого имени выбора.
        var one = PhonePage.Html("ru", Asked(Question(id: "q1")));

        Assert.DoesNotContain(
            Ru(nameof(PanelStrings.PhonePageQuestionCountFormat), 1, 1), one, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q1\"", one, StringComparison.Ordinal);
        Assert.Contains("name=\"q0\" value=\"0\"", one, StringComparison.Ordinal);

        // ВТОРОЙ ЭКРАН ИЗ ДВУХ: счёт есть, имя выбора — своё (`q1`), а первого экрана здесь нет.
        var second = PhonePage.Html(
            "ru",
            new PhonePageView(
                PhonePageKind.Question,
                EventId,
                new[] { Question(id: "q2", text: "Ещё?", options: new[] { "А", "Б" }) },
                Live: true,
                Screen: 1,
                Total: 2));

        Assert.Contains(
            Ru(nameof(PanelStrings.PhonePageQuestionCountFormat), 2, 2),
            Markup(second),
            StringComparison.Ordinal);
        Assert.Contains("name=\"q1\" value=\"0\"", second, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"q0\"", second, StringComparison.Ordinal);
        Assert.Contains("<span class=\"opt-text\">А</span>", second, StringComparison.Ordinal);
    }

    // ---- 3. живое обновление и путь без скрипта ---------------------------------------------

    /// <summary>Разметка БЕЗ скрипта: он один на всю страницу, и мешать ему в проверках вида
    /// (карточка, галочка, слова) нечего.</summary>
    private static string Markup(string html)
    {
        var start = html.IndexOf("<script", StringComparison.Ordinal);
        if (start < 0) return html;

        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return end < 0 ? html[..start] : html[..start] + html[(end + "</script>".Length)..];
    }

    /// <summary>
    /// У КАЖДОГО ЖИВОГО СОСТОЯНИЯ ЕСТЬ КНОПКА-ССЫЛКА «ПРОВЕРИТЬ» — И НИ ОДНОЙ ГАЛОЧКИ.
    ///
    /// ⚠️ **ГАЛОЧКИ «НЕ ОБНОВЛЯТЬ САМО» НА СТРАНИЦЕ НЕТ ВОВСЕ** (убрана владельцем 12.10.2026):
    /// карточку опрос подменяет ЦЕЛИКОМ, и вместе с ней приходила новая, СНЯТАЯ галочка — отметка
    /// человека терялась (владелец проверил живьём: поставил галочку, ничего не нажимал, вопрос
    /// появился сам). Поэтому проверка требует НИ РАЗМЕТКИ, НИ СЛОВ галочки, а остановку опроса
    /// стережёт <see cref="Опрос_замирает_на_скрытой_странице_и_пока_человек_выбирает_или_пишет"/>.
    ///
    /// **Кнопка — ССЫЛКА, а не `<button>`** (решение владельца 11.10.2026): без скрипта она
    /// работает обычным переходом на ту же страницу, а внутри формы ответа не заводит ВЛОЖЕННУЮ
    /// форму (вложенная форма — невалидный HTML, и браузер разбирает её как захочет).
    ///
    /// Мутации, которые обязаны упасть здесь: «кнопка „Проверить“ стала <c>type=button</c> или
    /// `<button>`» (без скрипта не работает) · **«галочка вернулась в разметку»** · «вернулись её
    /// слова» · «ссылки „Проверить“ нет» · «слов о живом обновлении нет».
    /// </summary>
    [Fact]
    public void Живые_состояния_дают_ссылку_проверить_и_не_дают_галочки()
    {
        foreach (var (name, html) in EveryPage())
        {
            // САМА РАЗМЕТКА — без скрипта: в нём те же слова встречаются как селекторы, и путать
            // разметку со скриптом значило бы проверять не то.
            var markup = Markup(html);

            // ГАЛОЧКИ НЕТ НИГДЕ И НИ В КАКОМ ВИДЕ — ни у живого состояния, ни у беды.
            Assert.DoesNotContain("type=\"checkbox\"", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"live\"", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("live-", markup, StringComparison.Ordinal);

            if (!markup.Contains("data-live", StringComparison.Ordinal)) continue;

            // ССЫЛКА, А НЕ КНОПКА: `<a class="ghost narrow" href="/">`.
            Assert.True(
                markup.Contains("<a class=\"ghost narrow\" href=\"/\">", StringComparison.Ordinal),
                $"{name}: нет ссылки «Проверить»");
            Assert.True(
                markup.Contains(">" + Ru(nameof(PanelStrings.PhonePageCheckButton)) + "</a>"),
                $"{name}: ссылка «Проверить» названа не словами словаря");

            // ⚠️ И У РЯДА ЕСТЬ КРЮЧОК `data-auto` (решение владельца 12.10.2026): по нему скрипт
            // убирает ряд со ссылкой, когда опрос пошёл. Без крючка убрать его нечем, и «Проверить»
            // осталась бы на экране навсегда — а именно это и просили убрать. У ОЖИДАНИЯ крючка
            // НЕТ намеренно: там ничего не происходит по определению, и путь обязан остаться
            // (см. отдельную проверку ниже).
            if (name != "ожидание")
            {
                Assert.True(
                    markup.Contains("<div class=\"auto\" data-auto>", StringComparison.Ordinal),
                    $"{name}: у ряда со ссылкой «Проверить» нет крючка `data-auto`");
            }

            // СЛОВ О ЖИВОМ ОБНОВЛЕНИИ НЕТ НИ У ОДНОГО СОСТОЯНИЯ (живой прогон 13.10.2026, находка
            // владельца: это наша механика, а не забота человека). Проверка падает, если строку вернут.
            Assert.DoesNotContain("Страница проверяет сама", markup, StringComparison.OrdinalIgnoreCase);

            // А `<button ... type="button">` НА ЖИВОМ СОСТОЯНИИ НЕТ ВОВСЕ: «Проверить» — ссылка.
            Assert.DoesNotContain("type=\"button\"", markup, StringComparison.Ordinal);
        }

        // И СЛОВ ГАЛОЧКИ НЕТ НИ В ОДНОМ СЛОВАРЕ: ключ убран, и `Loc` вернул бы его ИМЯ —
        // значит «галочка исчезла» проверено и словами, а не только разметкой.
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            Assert.Equal("PhonePageLiveOff", Loc.TIn(language, "PhonePageLiveOff"));
            Assert.Equal("PhonePageLiveHintFormat", Loc.TIn(language, "PhonePageLiveHintFormat"));
        }
    }

    /// <summary>
    /// У БЕДЫ ЖИВОГО ОБНОВЛЕНИЯ НЕТ, А ПУТЬ ИЗ НЕЁ ЕСТЬ: «Повторить» ссылкой.
    ///
    /// **От какого случая.** Норма GOV.UK (Recover from validation errors): «tell the user what's
    /// gone wrong and **how to fix it**»; у нас это названо в `30-mobile-web.md` §2, «тупиков
    /// не бывает». И у беды НЕ бывает опроса: она приходит в ответ на отправку формы, и подмена
    /// карточки заставила бы человека смотреть не на то, на что он отвечал.
    ///
    /// Мутации, которые обязаны упасть здесь: «в состоянии беды нет „Повторить“» · «беда
    /// обновляется сама» · «путь из беды сделан кнопкой без <c>href</c>» (без скрипта не работает).
    /// </summary>
    [Fact]
    public void Беда_даёт_путь_повторить_и_не_обновляется_сама()
    {
        var words = Ru(nameof(PanelStrings.PhonePageAnswerFailedFormat), "связи нет");
        var html = PhonePage.Html("ru", PhonePageView.Problem(words));

        Assert.Contains(words, html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"problem\">", html, StringComparison.Ordinal);

        // ПУТЬ ЕСТЬ — и он ссылкой на свой корень.
        Assert.Contains(
            "<a class=\"ghost\" href=\"/\">" + Ru(nameof(PanelStrings.PhonePageRepeatButton)) + "</a>",
            html,
            StringComparison.Ordinal);

        // А ОПРОСА У НЕЁ НЕТ ВОВСЕ: ни признака живого состояния, ни галочки. Смотрим РАЗМЕТКУ:
        // в самом скрипте эти слова стоят селекторами, и путать их с разметкой нельзя.
        var markup = Markup(html);

        Assert.DoesNotContain("data-live", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"live\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <summary>
    /// СЛОВ О НАШЕМ ОПРОСЕ НА СТРАНИЦЕ НЕТ — НИ НА ОДНОМ СОСТОЯНИИ.
    ///
    /// **От какого случая (живой прогон на телефоне 13.10.2026, находка владельца).** Прежде
    /// на экране вопроса и на экранах исходов стояла строка «Страница проверяет сама каждые 2,5 с» —
    /// **наша механика, а не забота человека**. Решение владельца: этого человеку знать не нужно.
    /// Ключ `PhonePageLiveWhileOpenFormat` убран вовсе, а проверка стережёт САМУ ФРАЗУ: вернут
    /// слова в разметку — она упадёт.
    ///
    /// ⚠️ **Почему фраза, а не имя ключа.** Проверка, ищущая имя ключа, ловила бы только возврат
    /// ТОГО ЖЕ ключа; фраза ловит и переписанную по смыслу строку с тем же обещанием.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть строку о живом обновлении на экран вопроса» ·
    /// «вернуть её на экран ответа» · «вернуть её у ожидания».
    /// </summary>
    [Fact]
    public void Слов_о_нашем_опросе_на_странице_нет()
    {
        // ФРАЗА-СТОРОЖ: то, что человек читал до 13.10.2026, — на трёх языках, как оно и звучало.
        var guard = new[] { "Страница проверяет сама", "Page checks by itself", "页面会自行检查" };

        foreach (var (name, html) in EveryPage())
        {
            var markup = Markup(html);

            foreach (var phrase in guard)
            {
                Assert.DoesNotContain(phrase, markup, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// РЯД «ПРОВЕРИТЬ» СНЯТЬ ЕСТЬ ЧЕМ: у него крючок `data-auto`, и скрипт его ищет.
    ///
    /// **От какого случая.** Ряд убирает САМ СКРИПТ, когда опрос пошёл (решение владельца
    /// 12.10.2026) — так путь остаётся и при выключенном, и при сломанном скрипте. Но «скрипт
    /// убирает» держалось на слове: не было проверки, что крючок в разметке и что его имя
    /// в скрипте совпадают. Переименуй крючок — ряд остался бы на экране навсегда, и ни один
    /// прогон не упал бы.
    ///
    /// ⚠️ **Сверяются ОБЕ стороны:** крючок есть в разметке живого состояния и его же ищет скрипт.
    ///
    /// Мутации, которые обязаны упасть здесь: «снять `data-auto` с разметки» · «переименовать
    /// крючок только в разметке» · «убрать селектор `[data-auto]` из скрипта».
    /// </summary>
    [Fact]
    public void Ряд_проверить_снимается_скриптом_по_своему_крючку()
    {
        var asked = PhonePage.Html("ru", Asked());
        var markup = Markup(asked);

        // (1) У РЯДА ЖИВОГО СОСТОЯНИЯ — СВОЙ КРЮЧОК.
        Assert.Contains("<div class=\"auto\" data-auto>", markup, StringComparison.Ordinal);

        // (2) И ЕГО ЖЕ ИЩЕТ СКРИПТ: сверяется имя, а не «скрипт что-то делает».
        Assert.Contains("document.querySelector(\"[data-auto]\")", asked, StringComparison.Ordinal);

        // (3) У ОЖИДАНИЯ КРЮЧКА НЕТ НАМЕРЕННО: там ничего не происходит по определению,
        //     и путь обязан остаться (то же решение владельца).
        var waiting = Markup(PhonePage.Html("ru", PhonePageView.Waiting()));

        Assert.Contains("<div class=\"auto\">", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("data-auto", waiting, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОПРОС ЗАМИРАЕТ НА СКРЫТОЙ СТРАНИЦЕ, ПОКА ВЫБРАН ВАРИАНТ И ПОКА ЧЕЛОВЕК ПИШЕТ.
    ///
    /// ⚠️ **Галочки «не обновлять само» больше нет** (убрана владельцем 12.10.2026: подмена карточки
    /// уносила отметку). Механизм остановки заменён тем, что теряться не может:
    ///
    /// * **страница не видима — запросов НЕТ ВООБЩЕ** (`document.visibilityState`), и вернувшись
    ///   на страницу человек получает свежее сразу (`visibilitychange`);
    /// * **выбран вариант (`:checked`) — пауза**: подмена карточки съела бы выбор, а «введённое
    ///   не пропадает без действия человека» — принятое правило;
    /// * **набран свой ответ — пауза** по той же причине.
    ///
    /// Мутации, которые обязаны упасть здесь: **«опрос идёт при скрытой странице»** (снята проверка
    /// видимости) · **«выбор варианта теряется при опросе»** (снята пауза по `:checked`) ·
    /// «в скрипте нет паузы при набранном тексте» · «запрос стоит до проверок».
    /// </summary>
    [Fact]
    public void Опрос_замирает_на_скрытой_странице_и_пока_человек_выбирает_или_пишет()
    {
        var page = PhonePage.Html("ru", Asked());

        // СКРЫТАЯ СТРАНИЦА — НЕ СПРАШИВАЕМ ВООБЩЕ.
        Assert.Contains(
            "function hidden(){return document.visibilityState!==\"visible\";}",
            page,
            StringComparison.Ordinal);

        // ВЫБРАННЫЙ ВАРИАНТ И НАБРАННЫЙ ТЕКСТ — ТОЖЕ ПАУЗА.
        Assert.Contains(
            "function busy(){var own=card.querySelector(\"input[name$='-own']\");return !!own&&own.value!==\"\";}",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "function picked(){return !!card.querySelector(\"input[type='radio']:checked\");}",
            page,
            StringComparison.Ordinal);

        // ВСЕ ТРИ ПРОВЕРКИ СТОЯТ ПЕРЕД ЗАПРОСОМ: пауза — это выход из круга, а не «не подменяем».
        var guard = page.IndexOf("if(hidden()||busy()||picked()){plan();return;}", StringComparison.Ordinal);
        var fetch = page.IndexOf("fetch(location.pathname", StringComparison.Ordinal);

        Assert.True(guard > 0, "в скрипте нет проверок видимости, выбора и набранного текста");
        Assert.True(fetch > guard, "проверки стоят ПОСЛЕ запроса — остановка была бы обманом");

        // И ПОДМЕНА — ТОЖЕ ПОД ТОЙ ЖЕ ЗАЩИТОЙ: между запросом и подменой человек мог выбрать вариант.
        Assert.Contains(
            "if(t.indexOf(\"<\")===0&&!hidden()&&!busy()&&!picked())card.innerHTML=t;",
            page,
            StringComparison.Ordinal);

        // ВЕРНУЛСЯ НА СТРАНИЦУ — СПРАШИВАЕМ СРАЗУ.
        Assert.Contains("document.addEventListener(\"visibilitychange\"", page, StringComparison.Ordinal);

        // И ВООБЩЕ: у скрипта нет ни одного чужого адреса — он ходит только к себе.
        Assert.DoesNotContain("XMLHttpRequest", page, StringComparison.Ordinal);
        Assert.DoesNotContain("document.write", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// СТРАНИЦА ГОВОРИТ, КАКИМ УСТРОЙСТВОМ ВОШЁЛ ЧЕЛОВЕК (решение владельца 12.10.2026).
    ///
    /// ⚠️ С двух телефонов иначе не отличить, чей это экран и чей ответ уйдёт движку. Имя приходит
    /// ОТ КАЛИТКИ (из cookie) и только показывается: страница по нему ничего не решает.
    ///
    /// Мутации, которые обязаны упасть здесь: «страница не говорит, какое устройство текущее» ·
    /// «имя устройства не экранируется».
    /// </summary>
    [Fact]
    public void Страница_называет_текущее_устройство()
    {
        var asked = new PhonePageView(
            PhonePageKind.Question,
            "event-1",
            new[] { new PhoneQuestion("q1", string.Empty, "Делать?", string.Empty, new[] { "Да" }) },
            true,
            Screen: 0,
            Total: 1,
            Device: "phone-2");

        var html = PhonePage.Html("ru", asked);

        Assert.Contains(Ru(nameof(PanelStrings.PhonePageDeviceFormat), "phone-2"), html, StringComparison.Ordinal);

        // ⚠️ КРЮЧОК — ИМЯ, А НЕ ВИД: строка помечена доводом `data-device` (как карточка —
        // `data-live`), потому что класс `device` правила не имел и был ДЕФЕКТОМ вида, а не
        // крючком. За этим следит и браузерный прогон (`PhoneBrowserTests`), и разбор классов.
        Assert.Contains("data-device", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"hint device\"", html, StringComparison.Ordinal);

        // И ЭКРАНИРУЕТСЯ: имя приходит от калитки, но подстановка — это подстановка.
        var evil = PhonePage.Html("ru", asked with { Device = "<b>чужой</b>" });

        Assert.DoesNotContain("<b>чужой</b>", evil, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;", evil, StringComparison.Ordinal);

        // А БЕЗ ИМЕНИ СТРОКИ НЕТ ВОВСЕ — вход и неавторизованное состояние её не показывают.
        Assert.DoesNotContain(
            Ru(nameof(PanelStrings.PhonePageDeviceFormat)).Replace("{0}", string.Empty).Trim(),
            PhonePage.Html("ru", PhonePageView.Waiting()),
            StringComparison.Ordinal);
    }

    // ---- 4. после ответа и снятия кнопок ответа нет ------------------------------------------

    /// <summary>
    /// ПОСЛЕ ОТВЕТА, СНЯТИЯ И ОТМЕНЫ — СЛОВА, И НИ ОДНОЙ ФОРМЫ ОТВЕТА.
    ///
    /// ⚠️ Это не украшение: кнопка на снятом вопросе отправила бы ответ в никуда, а человек
    /// решил бы, что ответил. Мутации, которые обязаны упасть здесь: «показать форму на любом
    /// состоянии», «оставить кнопку ответа всегда», «перепутать исходы местами» (слова — разные,
    /// и проверка называет каждое).
    /// </summary>
    [Fact]
    public void После_ответа_снятия_и_отмены_кнопок_ответа_нет()
    {
        var cases = new (PhonePageKind Kind, string Key)[]
        {
            (PhonePageKind.Answered, nameof(PanelStrings.PhonePageAnswered)),
            (PhonePageKind.Taken, nameof(PanelStrings.PhonePageTaken)),
            (PhonePageKind.Cancelled, nameof(PanelStrings.PhonePageCancelled)),
        };

        foreach (var (kind, key) in cases)
        {
            var html = PhonePage.Html("ru", PhonePageView.Outcome(kind));

            Assert.Contains(Ru(key), html, StringComparison.Ordinal);

            Assert.DoesNotContain("action=\"/answer\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/cancel\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("type=\"radio\"", html, StringComparison.Ordinal);
        }
    }

    // ---- 5. неразобранный вопрос ------------------------------------------------------------

    /// <summary>
    /// НЕРАЗОБРАННЫЙ ВОПРОС ГОВОРИТ СЛОВАМИ И НЕ ДАЁТ НИ ВЫБОРА, НИ ОТМЕНЫ.
    ///
    /// Два пути к одному и тому же: состояние <see cref="PhonePageKind.Unreadable"/> и состояние
    /// «вопрос есть, а разобранных вопросов нет». Оба обязаны дать слова — и ни одной формы ответа:
    /// пустая форма выглядела бы как работающая.
    ///
    /// Мутации, которые обязаны упасть здесь: «показать пустую форму», «промолчать» (пустая
    /// страница читается как «всё в порядке»), «сказать „вопросов нет“» (неправда: вопрос висит).
    /// </summary>
    [Fact]
    public void Неразобранный_вопрос_говорит_словами_и_не_даёт_кнопок()
    {
        var byKind = PhonePage.Html("ru", PhonePageView.Outcome(PhonePageKind.Unreadable));
        var byEmpty = PhonePage.Html(
            "ru",
            new PhonePageView(PhonePageKind.Question, EventId, Array.Empty<PhoneQuestion>(), Live: true));

        foreach (var html in new[] { byKind, byEmpty })
        {
            Assert.Contains(Ru(nameof(PanelStrings.PhonePageUnreadable)), html, StringComparison.Ordinal);

            Assert.DoesNotContain("action=\"/answer\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/cancel\"", html, StringComparison.Ordinal);
        }
    }

    // ---- 6. экранирование -------------------------------------------------------------------

    /// <summary>
    /// ТЕКСТ ВОПРОСА ЭКРАНИРУЕТСЯ И РАЗМЕТКОЙ НЕ СТАНОВИТСЯ.
    ///
    /// Вопрос пишет агент, а подписи вариантов — движок: и тот и другой однажды вставят <c>&lt;</c>
    /// или кавычку. Мутации, которые обязаны упасть здесь: «отдать текст как есть» (страница
    /// сломалась бы, а вопрос стал бы разметкой), «экранировать только <c>&lt;</c>» (кавычка
    /// закрыла бы атрибут <c>value</c> и позволила бы дописать в разметку своё).
    /// </summary>
    [Fact]
    public void Текст_вопроса_экранируется_и_не_становится_разметкой()
    {
        var html = PhonePage.Html("ru", Asked(Question(
            header: "Вопрос <script>",
            text: "<b>жирный</b> & \"кавычки\"",
            detail: "a < b",
            options: new[] { "<i>вариант</i>", "Нет" })));

        Assert.Contains("&lt;b&gt;жирный&lt;/b&gt; &amp; &quot;кавычки&quot;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;вариант&lt;/i&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &lt; b", html, StringComparison.Ordinal);

        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"кавычки\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// СВОЙ ОТВЕТ ЧЕЛОВЕКА ТОЖЕ ЭКРАНИРУЕТСЯ, А НЕ ВОЗВРАЩАЕТСЯ РАЗМЕТКОЙ.
    ///
    /// Он приходит ОТ ЧЕЛОВЕКА и ложится в атрибут <c>value</c>: кавычка в нём закрыла бы атрибут,
    /// а знак <c>&lt;</c> — открыл бы тег. Мутации, которые обязаны упасть здесь: «вернуть свой
    /// ответ как есть» · «экранировать только знак <c>&lt;</c>».
    /// </summary>
    [Fact]
    public void Свой_ответ_человека_экранируется_в_поле()
    {
        var html = PhonePage.Html(
            "ru",
            new PhonePageView(
                PhonePageKind.Question,
                EventId,
                new[] { Question(id: "q1", options: new[] { "Да", "Нет" }) },
                Live: true,
                Answers: Said(("q1", "\" на <b>своё</b> & прочее"))));

        Assert.Contains(
            "value=\"&quot; на &lt;b&gt;своё&lt;/b&gt; &amp; прочее\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>своё</b>", html, StringComparison.Ordinal);
    }

    // ---- 7. разбор вопросов терпим -----------------------------------------------------------

    /// <summary>
    /// РАЗБОР ВОПРОСОВ ТЕРПИМ: КРИВОЙ КАДР — ЭТО «НЕТ ВОПРОСОВ», А НЕ ПАДЕНИЕ КАЛИТКИ.
    ///
    /// Формы кадра движок не обещал, а вопрос приходит из живого потока: исключение здесь стоило
    /// бы телефону всей страницы. Разбирается ровно то, что разобралось.
    ///
    /// Мутации, которые обязаны упасть здесь: «бросить исключение на битый JSON», «принять запись
    /// без <c>id</c>» (ответ ушёл бы в никуда), «принять вопрос без вариантов» (отвечать нечем),
    /// «потерять порядок подписей» (телефон ответил бы не тем вариантом, который видел).
    /// </summary>
    [Fact]
    public void Разбор_вопросов_терпим_к_кривому_входу()
    {
        var normal = PhonePage.Parse(
            "[{\"id\":\"q1\",\"header\":\"Вопрос\",\"question\":\"Ставить задачу?\",\"detail\":\"подробность\","
            + "\"options\":[{\"label\":\"Да\",\"description\":\"сразу\"},{\"label\":\"Нет\"}]},"
            + "{\"id\":\"q2\",\"question\":\"Ещё?\",\"options\":[{\"label\":\"А\"},{\"label\":\"Б\"}]}]");

        Assert.Equal(2, normal.Count);
        Assert.Equal("q1", normal[0].Id);
        Assert.Equal("Вопрос", normal[0].Header);
        Assert.Equal("Ставить задачу?", normal[0].Text);
        Assert.Equal("подробность", normal[0].Detail);
        Assert.Equal(new[] { "Да", "Нет" }, normal[0].Options);
        Assert.Equal(new[] { "А", "Б" }, normal[1].Options);

        // ЗАПИСЬ БЕЗ ВАРИАНТОВ И ЗАПИСЬ БЕЗ ИМЕНИ ПРОПУСКАЮТСЯ — по отдельности, а не «весь кадр».
        Assert.Empty(PhonePage.Parse("[{\"id\":\"q1\",\"question\":\"без вариантов\",\"options\":[]}]"));
        Assert.Empty(PhonePage.Parse("[{\"id\":\"q1\",\"options\":[{\"description\":\"\"}]}]"));
        Assert.Empty(PhonePage.Parse("[{\"question\":\"без id\",\"options\":[{\"label\":\"Да\"}]}]"));

        // ГОДНАЯ ЗАПИСЬ РЯДОМ С КРИВОЙ ДОХОДИТ: терпимость — не «всё или ничего».
        Assert.Single(PhonePage.Parse(
            "[{\"id\":\"\",\"options\":[{\"label\":\"Да\"}]},{\"id\":\"q2\",\"options\":[{\"label\":\"А\"}]}]"));

        // Битый JSON, не массив, пустое и «ничего» — пустой список БЕЗ ИСКЛЮЧЕНИЯ.
        Assert.Empty(PhonePage.Parse("{это не json"));
        Assert.Empty(PhonePage.Parse("\"просто строка\""));
        Assert.Empty(PhonePage.Parse("{\"questions\":[]}"));
        Assert.Empty(PhonePage.Parse("[]"));
        Assert.Empty(PhonePage.Parse(null));
        Assert.Empty(PhonePage.Parse("   "));

        // Строка вместо записи — тоже подпись: движок вправе прислать и так.
        Assert.Equal(new[] { "Да", "Нет" }, PhonePage.Parse("[{\"id\":\"q1\",\"options\":[\"Да\",\"Нет\"]}]")[0].Options);
    }

    // ---- 8. разбор формы --------------------------------------------------------------------

    /// <summary>
    /// ФОРМА РАЗБИРАЕТСЯ ПО СВОИМ ПОЛЯМ: <c>eventId</c>, номер экрана из имени <c>q&lt;экран&gt;</c>,
    /// выбор и свой ответ.
    ///
    /// ⚠️ **Свой ответ читается РАНЬШЕ выбора**, и это не порядок ради порядка: имя выбора
    /// (<c>q0</c>) — префикс имени своего ответа (<c>q0-own</c>), и разобранный вторым выбор съел бы
    /// свой ответ. Мутации, которые обязаны упасть здесь: «не отличить <c>q0-own</c> от <c>q0</c>»
    /// (свой ответ уехал бы как номер варианта) · «взять только <c>q0</c>» · «счесть форму без
    /// номера экрана своей».
    /// </summary>
    [Fact]
    public void Разбор_формы_берёт_событие_экран_выбор_и_свой_ответ()
    {
        var form = PhoneAnswer.ParseForm("eventId=" + EventId + "&q0=1&q0-own=");

        Assert.NotNull(form);
        Assert.Equal(EventId, form!.EventId);
        Assert.Equal(0, form.Screen);
        Assert.Equal("1", form.Choice);
        Assert.Equal(string.Empty, form.Own);
        Assert.False(form.HasOwn);

        // ВТОРОЙ ЭКРАН — СВОЁ ИМЯ, и свой ответ доезжает отдельно от выбора.
        var second = PhoneAnswer.ParseForm("eventId=" + EventId + "&q1=0&q1-own=%D1%81%D0%B2%D0%BE%D1%91");

        Assert.NotNull(second);
        Assert.Equal(1, second!.Screen);
        Assert.Equal("0", second.Choice);
        Assert.Equal("своё", second.Own);
        Assert.True(second.HasOwn);

        // ДРУГИЕ ПОЛЯ ФОРМЫ НЕ НАШЕ ДЕЛО: `eventId`, экран, выбор и свой ответ — и всё.
        var extra = PhoneAnswer.ParseForm("eventId=a&q0=1&чужое=2");

        Assert.NotNull(extra);
        Assert.Equal("1", extra!.Choice);
    }

    /// <summary>
    /// ЗНАЧЕНИЯ ФОРМЫ РАСКОДИРУЮТСЯ: <c>+</c> — пробел, <c>%D0%B0</c> — «а».
    ///
    /// Мутации, которые обязаны упасть здесь: «не раскодировать вовсе» (кириллица в подписи
    /// доехала бы знаками процентов), «раскодировать только значение, но не <c>eventId</c>»,
    /// «уронить разбор на кривой последовательности» (телефон получил бы пятисотую вместо отказа
    /// словами).
    /// </summary>
    [Fact]
    public void Разбор_формы_раскодирует_значения()
    {
        var form = PhoneAnswer.ParseForm("eventId=%D0%B0%D0%B1+%2D&q0=%D0%94%D0%B0");

        Assert.NotNull(form);
        Assert.Equal("аб -", form!.EventId);
        Assert.Equal("Да", form.Choice);

        // КРИВАЯ ПОСЛЕДОВАТЕЛЬНОСТЬ НЕ РОНЯЕТ РАЗБОР: знак остаётся как пришёл.
        Assert.Equal("a%zz", PhoneAnswer.ParseForm("eventId=a%zz&q0=1")!.EventId);
    }

    /// <summary>
    /// ТЕЛО БЕЗ <c>eventId</c> ИЛИ БЕЗ НОМЕРА ЭКРАНА — НЕ НАША ФОРМА. Ответ на неизвестно какой
    /// вопрос хуже отказа: калитка не смогла бы сверить, на ТОТ ли вопрос отвечает телефон.
    ///
    /// Мутации, которые обязаны упасть здесь: «принять форму без <c>eventId</c>» (ответ ушёл бы
    /// на висящий вопрос, которого человек не видел), «подставить нулевой экран» (ответ ушёл бы
    /// не тому вопросу) · «принять JSON машинного пути за форму» · «счесть пустое <c>eventId</c>
    /// значением».
    /// </summary>
    [Fact]
    public void Разбор_формы_отказывает_без_события_и_без_экрана()
    {
        Assert.Null(PhoneAnswer.ParseForm(null));
        Assert.Null(PhoneAnswer.ParseForm(string.Empty));
        Assert.Null(PhoneAnswer.ParseForm("   "));
        Assert.Null(PhoneAnswer.ParseForm("q0=1"));
        Assert.Null(PhoneAnswer.ParseForm("eventId="));
        Assert.Null(PhoneAnswer.ParseForm("eventId=%20"));

        // ЭКРАН НЕ НАЗВАН — форма не наша: гадать номер нельзя.
        Assert.Null(PhoneAnswer.ParseForm("eventId=a"));

        // ⚠️ А `q0-own` ЭКРАН НАЗЫВАЕТ (правка 12.10.2026: человек, ответивший только словами,
        // варианта не выбирает — поля `q0` в форме нет, и без этого правила ответ не уходил).
        // Номер здесь обязан быть: `q-own` и `own` — по-прежнему не наша форма.
        Assert.Null(PhoneAnswer.ParseForm("eventId=a&q-own=своё"));
        Assert.Null(PhoneAnswer.ParseForm("eventId=a&own=своё"));

        // Машинный путь шлёт JSON (`{"eventId":"…"}`) — это НЕ форма, и путать их нельзя.
        Assert.Null(PhoneAnswer.ParseForm("{\"eventId\":\"x\"}"));
    }

    // ---- 9. сборка ответа -------------------------------------------------------------------

    /// <summary>
    /// ПОДПИСЬ БЕРЁТСЯ ИЗ СВОЕГО ВОПРОСА, А НЕ ИЗ ФОРМЫ — и это главное обещание ответа.
    ///
    /// Калитка превращает НОМЕР варианта в подпись САМА и только из своего вопроса, поэтому
    /// подставить чужой текст телефону нечем. Мутации, которые обязаны упасть здесь: «взять
    /// подпись из формы» (чужой текст уехал бы движку от имени человека), «сдвинуть индекс на
    /// единицу», «отдать подпись пустой».
    /// </summary>
    [Fact]
    public void Сборка_ответа_собирает_накопленное_по_порядку_вопросов()
    {
        var questions = new[]
        {
            Question(id: "q1", options: new[] { "Да", "Нет" }),
            Question(id: "q2", options: new[] { "А", "Б" }),
        };

        // ПОЛНЫЙ НАБОР ИДЁТ ОДНИМ ОТВЕТОМ, В ПОРЯДКЕ ВОПРОСОВ КАДРА.
        Assert.Equal(
            "[{\"id\":\"q1\",\"selected\":[\"Да\"]},{\"id\":\"q2\",\"selected\":[\"Б\"]}]",
            PhoneAnswer.Build(questions, Said(("q1", "Да"), ("q2", "Б"))));

        // СВОЙ ОТВЕТ ЧЕЛОВЕКА УХОДИТ ТОЙ ЖЕ СТРОКОЙ `selected`, что и выбранный вариант.
        Assert.Equal(
            "[{\"id\":\"q1\",\"selected\":[\"своё слово\"]},{\"id\":\"q2\",\"selected\":[\"А\"]}]",
            PhoneAnswer.Build(questions, Said(("q1", "своё слово"), ("q2", "А"))));

        // НЕПОЛНЫЙ НАБОР — ОТКАЗ ЦЕЛИКОМ: движок принял бы его за ответ.
        Assert.Null(PhoneAnswer.Build(questions, Said(("q1", "Да"))));
        Assert.Null(PhoneAnswer.Build(questions, Said(("q2", "Б"))));
        Assert.Null(PhoneAnswer.Build(questions, Said(("q1", "Да"), ("q2", ""))));
        Assert.Null(PhoneAnswer.Build(questions, Said()));
        Assert.Null(PhoneAnswer.Build(questions, new Dictionary<string, string>()));
        Assert.Null(PhoneAnswer.Build(Array.Empty<PhoneQuestion>(), Said()));
        Assert.Null(PhoneAnswer.Build(null!, Said()));
        Assert.Null(PhoneAnswer.Build(questions, null!));

        // ПОРЯДОК — ПО ВОПРОСАМ, А НЕ ПО ТОМУ, КАК ОТВЕТЫ ЛЕЖАЛИ В СЛОВАРЕ.
        Assert.Equal(
            "[{\"id\":\"q1\",\"selected\":[\"Нет\"]},{\"id\":\"q2\",\"selected\":[\"А\"]}]",
            PhoneAnswer.Build(questions, Said(("q2", "А"), ("q1", "Нет"))));

        // А КАВЫЧКА В ПОДПИСИ НЕ ЛОМАЕТ JSON: строка отдаётся сериализатору, а не склейке.
        var sharp = new[] { Question(id: "q\"1", options: new[] { "Да\"или нет" }) };

        Assert.Equal(
            "[{\"id\":\"q\\\"1\",\"selected\":[\"Да\\\"или нет\"]}]",
            PhoneAnswer.Build(sharp, Said(("q\"1", "Да\"или нет"))));
    }

    // ---- 10. отмена хода --------------------------------------------------------------------

    /// <summary>
    /// НА СТРАНИЦЕ ВОПРОСА ЕСТЬ И ОТМЕНА — СВОЕЙ ФОРМОЙ, СО СЛОВАМИ ПРЕДУПРЕЖДЕНИЯ И СО СВОИМ
    /// <c>eventId</c>.
    ///
    /// **Зачем.** Вопрос без ответившего висит вечно и освобождается только отменой (замер
    /// <c>docs\PHONE-LIVE.md</c> §4): без выхода телефон оказался бы заперт на вопросе, на который
    /// ответить не хочет. Но отмена останавливает работу агента — об этом говорится ДО нажатия.
    ///
    /// Мутации, которые обязаны упасть здесь: «отменять без предупреждения», «отправить отмену
    /// формой ответа» (отменился бы не тот ход), «убрать <c>eventId</c> у отмены» (можно было бы
    /// снять чужой вопрос).
    /// </summary>
    [Fact]
    public void Страница_вопроса_даёт_и_отмену_хода()
    {
        var html = PhonePage.Html("ru", Asked());

        Assert.Contains("<form method=\"post\" action=\"/cancel\">", html, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageCancelWarning)), html, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageCancelButton)), html, StringComparison.Ordinal);

        // ДВЕ формы и ДВА своих `eventId`: и у ответа, и у отмены — тот самый вопрос.
        Assert.Equal(2, Occurrences(html, "name=\"eventId\" value=\"" + EventId + "\""));
        Assert.Equal(2, Occurrences(html, "method=\"post\""));

        // А НА ОСТАЛЬНЫХ СОСТОЯНИЯХ ОТМЕНЫ НЕТ: отменять нечего.
        Assert.DoesNotContain(
            "action=\"/cancel\"", PhonePage.Html("ru", PhonePageView.Waiting()), StringComparison.Ordinal);
    }

    // ---- 11. заголовок, кодировка, язык -----------------------------------------------------

    /// <summary>
    /// ЗАГОЛОВОК, КОДИРОВКА И МАСШТАБ НА МЕСТЕ.
    ///
    /// Мутации, которые обязаны упасть здесь: «забыть <c>charset</c>» (кириллица превратилась бы
    /// в кракозябры), «забыть масштаб» (на телефоне страница стала бы шириной в палец),
    /// «назвать заголовок иначе, чем <c>h1</c>» (две разные надписи об одном и том же).
    /// </summary>
    [Fact]
    public void Заголовок_страницы_и_кодировка_на_месте()
    {
        var html = PhonePage.Html("ru", PhonePageView.Waiting());

        // ⚠️ У ОЖИДАНИЯ СВОЙ ЗАГОЛОВОК (решение владельца 12.10.2026): «Агент ждёт ответа» стоит
        // ровно там, где вопрос есть. Здесь проверяется ОЖИДАНИЕ — и оно обязано НЕ звать к ответу.
        var title = Ru(nameof(PanelStrings.PhonePageWaitingTitle));

        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains(
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            html,
            StringComparison.Ordinal);

        Assert.Contains("<title>" + title + "</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"screen-title\">" + title + "</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"ru\">", html, StringComparison.Ordinal);

        // А У ВОПРОСА — СВОЙ, И ОН ДРУГОЙ: два состояния не смеют говорить одно и то же.
        var asked = PhonePage.Html("ru", Asked());

        Assert.Contains(
            "<h1 class=\"screen-title\">" + Ru(nameof(PanelStrings.PhonePageQuestionTitle)) + "</h1>",
            asked,
            StringComparison.Ordinal);

        Assert.DoesNotContain(title, asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// СТРАНИЦА ГОВОРИТ НА ЯЗЫКЕ ТЕЛЕФОНА, А НЕ НА ЯЗЫКЕ ПАНЕЛИ.
    ///
    /// Язык приходит доводом от <see cref="PhoneGateLanguages"/> — он спросил его у самого
    /// телефона. Мутации, которые обязаны упасть здесь: «взять готовую строку <c>PanelStrings.X</c>
    /// вместо имени ключа» (готовая строка уже переведена на язык панели и вернулась бы как есть —
    /// телефон получил бы чужой язык), «всегда отдавать русский», «забыть <c>lang</c>».
    /// </summary>
    [Fact]
    public void Страница_говорит_на_языке_телефона()
    {
        var russian = PhonePage.Html("ru", PhonePageView.Waiting());
        var english = PhonePage.Html("en", PhonePageView.Waiting());
        var chinese = PhonePage.Html("zh", PhonePageView.Waiting());

        Assert.Contains(Word("ru", nameof(PanelStrings.PhonePageWaiting)), russian, StringComparison.Ordinal);
        Assert.Contains(Word("en", nameof(PanelStrings.PhonePageWaiting)), english, StringComparison.Ordinal);
        Assert.Contains(Word("zh", nameof(PanelStrings.PhonePageWaiting)), chinese, StringComparison.Ordinal);

        // Три языка — три РАЗНЫХ слова: «переведено» проверяется сравнением, а не наличием.
        Assert.NotEqual(russian, english);
        Assert.NotEqual(english, chinese);
        Assert.NotEqual(russian, chinese);

        Assert.Contains("<html lang=\"en\">", english, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"zh\">", chinese, StringComparison.Ordinal);

        // НОВЫЕ СЛОВА F5 ПЕРЕВЕДЕНЫ НА ВСЕ ТРИ — и это проверяется по каждому ключу отдельно:
        // ключ, оставшийся только русским, показал бы человеку имя ключа.
        foreach (var key in new[]
                 {
                     nameof(PanelStrings.PhonePageCheckButton),
                     nameof(PanelStrings.PhonePageDeviceFormat),
                     nameof(PanelStrings.PhonePageRepeatButton),
                     nameof(PanelStrings.PhonePageOwnLabel),
                     nameof(PanelStrings.PhonePageOwnPlaceholder),
                 })
        {
            foreach (var language in new[] { "ru", "en", "zh" })
            {
                var text = Word(language, key);

                Assert.False(text.Length == 0 || text == key, $"{language}: слова {key} нет в словаре");
            }

            Assert.NotEqual(Word("ru", key), Word("en", key));
            Assert.NotEqual(Word("en", key), Word("zh", key));
        }

        // А ЧИСЛО В СЛОВАХ ЖИВОГО ОБНОВЛЕНИЯ — ОДНО НА ВСЕ ЯЗЫКИ (подстановка одинаковой формы).
        // ⚠️ СЛОВ О НАШЕМ ОПРОСЕ НЕТ НА СТРАНИЦЕ НИ У ОДНОГО СОСТОЯНИЯ (находка владельца
        // 13.10.2026). Проверка перевёрнута: она падает, если строку вернут на экран.
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            Assert.DoesNotContain("Страница проверяет сама", PhonePage.Html(language, "ru" == language ? Asked() : Asked()), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- 12. вид: одна таблица стилей, кнопки под палец, класс без правила — дефект ----------

    /// <summary>
    /// ВСЕ СТРАНИЦЫ, КОТОРЫЕ ПАНЕЛЬ ОТДАЁТ ТЕЛЕФОНУ: два входа и положения работы.
    ///
    /// ⚠️ Вид проверяется НА КАЖДОМ из них, а не на одном: состояния собираются РАЗНЫМИ ветками
    /// <see cref="PhonePage.Html"/>, и таблица стилей, потерянная в одной ветке, осталась бы
    /// незамеченной проверкой, смотрящей только на ожидание. Вход — вторая страница панели,
    /// и таблица у неё обязана быть ТА ЖЕ САМАЯ (один продукт, один адрес).
    /// </summary>
    /// <summary>
    /// ОГРАНИЧЕНИЯ ТЕЛЕФОНА СКАЗАНЫ НА САМОЙ СТРАНИЦЕ — и на входе (куда попадает QR), и на
    /// странице вопроса, на всех трёх языках (решение владельца 17.10.2026: «работает в своей сети:
    /// вход по QR и ответ на вопрос агента; постановка задачи и управление агентами — в разработке;
    /// наружу не выходит»).
    ///
    /// **Почему проверка, а не слово.** Строка живёт в словарях, и её отсутствие на странице не
    /// заметить ничем: страница отрисуется, остальные проверки пройдут. Здесь слова берутся
    /// ИЗ СЛОВАРЯ (<see cref="Loc.TIn"/>) и ищутся в готовой разметке — то есть проверяется то,
    /// что человек увидит.
    ///
    /// ⚠️ **Четыре ограничения стерегутся маркерами** — по одному на каждое и на каждый язык:
    /// (а) своя сеть, (б) вход по QR и ответ на вопрос агента, (в) постановка задачи и управление
    /// агентами — в разработке, (г) наружу не выходит. Маркер ловит ПОТЕРЮ ограничения при
    /// переводе; смысл перевода читает человек (названо в отчёте работы).
    ///
    /// Мутация, которая обязана упасть здесь: убрать строку со страницы входа или со страницы
    /// вопроса (в `PhoneGate.cs` / `PhonePage.cs`) — проверка назовёт язык и страницу.
    /// </summary>
    [Fact]
    public void Ограничения_телефона_сказаны_на_странице_и_на_всех_трёх_языках()
    {
        var markers = new Dictionary<string, string[]>
        {
            ["ru"] = new[] { "своей сети", "QR", "в разработке", "наружу" },
            ["en"] = new[] { "own network", "QR", "in development", "external connections" },
            ["zh"] = new[] { "自己的网络", "二维码", "仍在开发中", "向外" },
        };

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var words = Loc.TIn(language, nameof(PanelStrings.PhoneLimits));

            // Ключ есть в словаре: `Loc` на пропавшем ключе вернул бы ЕГО ИМЯ.
            Assert.NotEqual(nameof(PanelStrings.PhoneLimits), words);
            Assert.False(string.IsNullOrWhiteSpace(words));

            foreach (var marker in markers[language])
            {
                Assert.Contains(marker, words, StringComparison.Ordinal);
            }

            // Те же слова — на странице ВХОДА (её открывает QR) и на странице ВОПРОСА.
            Assert.Contains(words, PhoneLoginPage.Html(language), StringComparison.Ordinal);
            Assert.Contains(words, PhonePage.Html(language, PhonePageView.Waiting()), StringComparison.Ordinal);
            Assert.Contains(words, PhonePage.Html(language, Asked()), StringComparison.Ordinal);
        }
    }
    /// <summary>
    /// СТРАНИЦА ВХОДА ГОВОРИТ НА ЯЗЫКЕ ТЕЛЕФОНА, А НЕ НА ЯЗЫКЕ ПАНЕЛИ. Найдено 17.10.2026
    /// проверкой слов-ограничителя: `PhoneLoginPage.Html` передавал строки ГОТОВЫМ ЗНАЧЕНИЕМ
    /// (`T(PanelStrings.X)` — уже переведённая строка панели), а `T` ждёт ИМЯ ключа
    /// (`Loc.TIn(language, key)`); на «чужом» языке ключ не находился, и `Loc` возвращал сам ключ —
    /// то есть русский текст. Телефон с английским языком видел русскую страницу входа.
    ///
    /// Мутация: вернуть в `PhoneGate.cs` вызовы `T(PanelStrings.X)` без `nameof` — проверка назовёт
    /// язык, на котором страница не совпала со словарём.
    /// </summary>
    [Fact]
    public void Страница_входа_говорит_на_языке_телефона()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var html = PhoneLoginPage.Html(language);

            Assert.Contains("lang=\"" + language + "\"", html, StringComparison.Ordinal);

            foreach (var key in new[] { nameof(PanelStrings.PhonePageTitle), nameof(PanelStrings.PhonePageIntro), nameof(PanelStrings.PhonePageCodeLabel), nameof(PanelStrings.PhonePageSubmit) })
            {
                var words = Loc.TIn(language, key);

                Assert.NotEqual(key, words);
                Assert.Contains(words, html, StringComparison.Ordinal);
            }

            // И НИ ОДНОГО ЧУЖОГО ЯЗЫКА: русская подпись не должна стоять на английской странице.
            if (language != "ru")
            {
                Assert.DoesNotContain(Loc.TIn("ru", nameof(PanelStrings.PhonePageIntro)), html, StringComparison.Ordinal);
            }
        }
    }
    private static IEnumerable<(string Name, string Html)> EveryPage()
    {
        yield return ("вход", PhoneLoginPage.Html("ru"));
        yield return ("вход с бедой", PhoneLoginPage.Html("ru", "код не подошёл"));
        yield return ("вход с бедой и введённым кодом", PhoneLoginPage.Html("ru", "код не подошёл", "482913"));
        yield return ("ожидание", PhonePage.Html("ru", PhonePageView.Waiting()));
        yield return ("вопрос", PhonePage.Html("ru", Asked()));
        yield return ("вопрос со своим ответом", PhonePage.Html(
            "ru",
            new PhonePageView(
                PhonePageKind.Question, EventId, new[] { Question() }, Live: true, Answers: Said(("q1", "своё")))));
        yield return ("ответ отправлен", PhonePage.Html("ru", PhonePageView.Outcome(PhonePageKind.Answered)));
        yield return ("вопрос снят", PhonePage.Html("ru", PhonePageView.Outcome(PhonePageKind.Taken)));
        yield return ("ход отменён", PhonePage.Html("ru", PhonePageView.Outcome(PhonePageKind.Cancelled)));
        yield return ("неразобранный вопрос", PhonePage.Html(
            "ru", new PhonePageView(PhonePageKind.Unreadable, EventId, Array.Empty<PhoneQuestion>(), Live: true)));
        yield return ("беда", PhonePage.Html("ru", PhonePageView.Problem("ответ не отправился")));

        // ⚠️ СТРАНИЦА С ИМЕНЕМ УСТРОЙСТВА — ЗДЕСЬ И ТОЛЬКО ЗАТЕМ, ЧТОБЫ ЭТА СТРОКА БЫЛА ВИДНА ВОРОТАМ.
        // Она появляется лишь у сопряжённого телефона (`PhoneGate`: `View(deviceId)`), и прежняя
        // редакция этого набора знала её только через одну отдельную проверку — то есть разбор
        // «у каждого класса разметки есть правило» до неё НЕ ДОХОДИЛ вовсе, и класс `device`
        // без правила прожил незамеченным (найдено разбором `80-accessibility.md` §12).
        yield return ("вопрос с именем устройства", PhonePage.Html(
            "ru",
            new PhonePageView(
                PhonePageKind.Question,
                EventId,
                new[] { Question() },
                Live: true,
                Screen: 0,
                Total: 1,
                Device: "phone-1")));

        yield return ("карточка для опроса", PhonePage.Html("ru", PhonePageView.Waiting(), cardOnly: true));
    }

    /// <summary>
    /// КРЮЧОК УСТРОЙСТВА — ОДИН ДОВОД В ОДНОМ МЕСТЕ, И ЗА НЕГО ДЕРЖИТСЯ ПРОГОН.
    ///
    /// **От какого случая.** Класс `device` был заведён в разметке и не имел правила в таблице
    /// стилей, а разбор «у каждого класса есть правило» до него НЕ ДОХОДИЛ: страницы с непустым
    /// `Device` в <see cref="EveryPage"/> не было, и ворота **не могли упасть в принципе**
    /// (найдено разбором `80-accessibility.md` §12). Сегодня крючок назван доводом, страница в
    /// наборе есть, а ЭТА проверка смотрит на исходник и стережёт три вещи, каждая из которых
    /// тихо вернула бы слепое место:
    /// (1) довод написан ровно в одном месте и написан как ДОВОД, а не как класс;
    /// (2) браузерный прогон спрашивает ИМЕННО его — а не «что-нибудь, что найдётся»;
    /// (3) страница с непустым `Device` действительно есть в наборе страниц прогона.
    ///
    /// Мутации, которые обязаны упасть здесь: «довод снова стал классом `device`» ·
    /// «страница с именем устройства убрана из <see cref="EveryPage"/>)» ·
    /// «браузерный прогон снова спрашивает класс» · «строку устройства печатают дважды».
    /// </summary>
    [Fact]
    public void Крючок_устройства_один_и_его_спрашивает_браузерный_прогон()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "DshPanel", "Server", "PhonePage.cs"));

        Assert.Equal(1, Occurrences(page, "\" data-device>\""));
        Assert.DoesNotContain("hint device", page, StringComparison.Ordinal);

        var browser = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tests", "DshPanel.Tests", "PhoneBrowserTests.cs"));

        Assert.Contains("[data-device]", browser, StringComparison.Ordinal);

        // И СТРАНИЦА С ИМЕНЕМ УСТРОЙСТВА — В НАБОРЕ: без неё разбор классов до неё не доходит.
        var asked = new PhonePageView(
            PhonePageKind.Question,
            EventId,
            new[] { Question() },
            Live: true,
            Screen: 0,
            Total: 1,
            Device: "phone-1");

        var withDevice = PhonePage.Html("ru", asked);

        Assert.Contains("data-device", withDevice, StringComparison.Ordinal);
        Assert.Contains(
            Ru(nameof(PanelStrings.PhonePageDeviceFormat), "phone-1"),
            withDevice,
            StringComparison.Ordinal);

        Assert.Contains(
            EveryPage(),
            pair => pair.Name == "вопрос с именем устройства"
                && pair.Html.Contains("data-device", StringComparison.Ordinal));
    }

    /// <summary>
    /// КОРЕНЬ РЕПОЗИТОРИЯ — ВВЕРХ ОТ КАТАЛОГА СБОРКИ ПО ФАЙЛУ <c>DshPanel.slnx</c>: так его ищут
    /// и прочие ворота по исходникам (`SourceStringsTests`, `FirewallTests`, `UiThreadTests`).
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DshPanel.slnx"))) return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет DshPanel.slnx");

        return string.Empty;
    }

    /// <summary>Тело правила из таблицы стилей: от селектора до закрывающей скобки.</summary>
    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в таблице стилей нет правила {selector}");

        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"правило {selector} не закрыто");

        return css.Substring(start, end - start + 1);
    }

    /// <summary>
    /// ВИД ЖИВЁТ ОДНОЙ ТАБЛИЦЕЙ СТИЛЕЙ И ВНУТРИ САМОЙ СТРАНИЦЫ — НА ОБЕИХ СТРАНИЦАХ.
    ///
    /// **От какого случая.** Владелец открыл страницу с телефона и сказал: *«выглядит ужасно, без
    /// разметки, как будто на HTML»* — имена классов в разметке стояли, а правил к ним не было
    /// ни одного. Отсюда две вещи, и обе проверяются здесь: правила ЕСТЬ, и живут они ОДНОЙ
    /// таблицей ВНУТРИ страницы (второй таблицы в панели быть не должно, а внешняя сломала бы
    /// работу в сети без интернета).
    ///
    /// ⚠️ **РОВНО ОДИН `<style>` — на каждой ЦЕЛОЙ странице** (норма `_design-standard-draft.md`
    /// §2.2). Ответ на `?part=card` таблицы не несёт ВООБЩЕ: он подменяет карточку уже открытой
    /// страницы, и второй таблицы в ней не появляется.
    ///
    /// Мутации, которые обязаны упасть здесь: «правил вида нет» (таблица вырезана) · «две таблицы
    /// стилей» (вторая вставка — страница уже не одна) · «таблица не на той странице» (вид есть
    /// у работы, но не у входа) · «вид уехал во внешний файл» (<c>&lt;link&gt;</c>).
    /// </summary>
    [Fact]
    public void Вид_живёт_одной_таблицей_стилей_внутри_страницы()
    {
        foreach (var (name, html) in EveryPage())
        {
            if (name == "карточка для опроса")
            {
                Assert.Equal(0, Occurrences(html, "<style>"));
                continue;
            }

            Assert.True(Occurrences(html, "<style>") == 1, $"{name}: таблица стилей не одна");
            Assert.True(Occurrences(html, "</style>") == 1, $"{name}: закрытий таблицы стилей не одно");

            // ТАБЛИЦА — ИМЕННО ЭТА: своя, одна на обе страницы, а не «какая-нибудь».
            Assert.True(
                Occurrences(html, PhonePageStyle.Css) == 1,
                $"{name}: таблица стилей не та, что у всей панели");
        }

        // ОБЕ СТРАНИЦЫ — ОДИН ПРОДУКТ: таблица у входа и у работы одна и та же строка.
        var login = PhoneLoginPage.Html("ru");
        var work = PhonePage.Html("ru", PhonePageView.Waiting());

        Assert.Contains("<style>" + PhonePageStyle.Css + "</style>", login, StringComparison.Ordinal);
        Assert.Contains("<style>" + PhonePageStyle.Css + "</style>", work, StringComparison.Ordinal);
    }

    /// <summary>
    /// КАЖДОЕ ИМЯ КЛАССА ИЗ РАЗМЕТКИ ИМЕЕТ ПРАВИЛО В ТАБЛИЦЕ — «класс без правила» это дефект
    /// (норма `_design-standard-draft.md` §2.3: именно так и выглядела страница «как будто на HTML»).
    ///
    /// Мутации, которые обязаны упасть здесь: «переименовать класс в разметке и забыть правило» ·
    /// «вырезать правило из таблицы» · «оставить класс только в комментарии».
    /// </summary>
    [Fact]
    public void У_каждого_класса_разметки_есть_правило()
    {
        var css = PhonePageStyle.Css;

        foreach (var (name, html) in EveryPage())
        {
            foreach (var spot in ClassNames(html))
            {
                Assert.True(
                    css.Contains("." + spot, StringComparison.Ordinal),
                    $"{name}: у класса «{spot}» нет правила в таблице стилей");
            }
        }
    }

    /// <summary>Имена классов из атрибутов <c>class="…"</c> разметки — по одному разу каждое.</summary>
    private static IEnumerable<string> ClassNames(string html)
    {
        const string Marker = "class=\"";

        var found = new HashSet<string>(StringComparer.Ordinal);

        for (var start = html.IndexOf(Marker, StringComparison.Ordinal);
             start >= 0;
             start = html.IndexOf(Marker, start + Marker.Length, StringComparison.Ordinal))
        {
            start += Marker.Length;

            var end = html.IndexOf('"', start);
            if (end < 0) break;

            foreach (var name in html[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (found.Add(name)) yield return name;
            }
        }
    }

    /// <summary>
    /// ЦЕЛЬ ПОД ПАЛЕЦ, ВИДИМЫЙ ФОКУС, ВЫБРАННОЕ ВИДНО И РАЗМЕР НЕ МЕНЯЕТСЯ.
    ///
    /// Нормы: WCAG 2.2 SC 2.5.5 (AAA) — «at least 44 by 44 CSS pixels»; SC 2.4.13 — «2 CSS pixel
    /// thick perimeter» и изменение контраста «at least 3:1»; web.dev — «at least 8 pixels of
    /// spacing»; `_design-standard-draft.md` §1.11 — состояние не смеет менять размер.
    ///
    /// Мутации, которые обязаны упасть здесь: «кнопки без 44 точек» · «вариант снова только
    /// кружок» (строка перестала быть нажимаемой) · «фокуса нет вовсе» · «выбранный вариант
    /// не видно» · «выбранный меняет толщину рамки» (соседние строки съезжают) · «8 точек между
    /// вариантами вернули к шести» · «строка длиннее 60 знаков».
    /// </summary>
    [Fact]
    public void Цель_под_палец_фокус_виден_и_размер_не_меняется()
    {
        var css = PhonePageStyle.Css;

        // ЦЕЛЬ — НЕ МЕНЬШЕ 44 ТОЧЕК: кнопка, строка варианта, поле и галочка со своей подписью.
        Assert.Contains("min-height:44px", Rule(css, ".opt-body{"), StringComparison.Ordinal);
        Assert.Contains("min-height:44px", Rule(css, ".action{"), StringComparison.Ordinal);
        Assert.Contains("min-height:44px", Rule(css, ".ghost{"), StringComparison.Ordinal);
        Assert.Contains("min-height:44px", Rule(css, ".code{"), StringComparison.Ordinal);
        Assert.Contains("min-height:44px", Rule(css, ".own-input{"), StringComparison.Ordinal);

        // КНОПКА — ВО ВСЮ ШИРИНУ: её жмут, а не прицеливаются. Узкая — только «Проверить».
        Assert.Contains("width:100%", Rule(css, ".action{"), StringComparison.Ordinal);
        Assert.Contains("width:auto", Rule(css, ".narrow{"), StringComparison.Ordinal);

        // ВАРИАНТ — СТРОКА ЦЕЛИКОМ: кружок и подпись в одной нажимаемой строке.
        Assert.Contains("display:flex", Rule(css, ".opt-body{"), StringComparison.Ordinal);

        // МЕЖДУ ВАРИАНТАМИ — 8 ТОЧЕК (норма web.dev: «at least 8 pixels of spacing»).
        Assert.Contains("gap:8px", Rule(css, ".opts{"), StringComparison.Ordinal);

        // ФОКУС ВИДЕН: `:focus-visible` с обводкой 2 точки и отступом 2 — и у кнопок, и у полей,
        // и у ссылки, и у строки варианта (`:focus-within`).
        var focus = Rule(css, "button:focus-visible");

        Assert.Contains("outline:2px solid var(--focus)", focus, StringComparison.Ordinal);
        Assert.Contains("outline-offset:2px", focus, StringComparison.Ordinal);
        Assert.Contains("a:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("input:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("label:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains(".opt:focus-within .opt-body", css, StringComparison.Ordinal);

        // ВЫБРАННЫЙ ВАРИАНТ ВИДЕН — И РАЗМЕР НЕ МЕНЯЕТСЯ: подложка И рамка, толщина ТА ЖЕ (2 точки),
        // а `padding`/`border-width` при `:checked` не трогаются вовсе.
        var checkedRule = Rule(css, ".opt:has(input:checked) .opt-body{");

        Assert.Contains("border-color:var(--accent)", checkedRule, StringComparison.Ordinal);
        Assert.Contains("background:var(--accent-wash)", checkedRule, StringComparison.Ordinal);
        Assert.DoesNotContain("border-width", checkedRule, StringComparison.Ordinal);
        Assert.DoesNotContain("padding", checkedRule, StringComparison.Ordinal);

        // У САМОЙ СТРОКИ ВАРИАНТА РАМКА ТОЙ ЖЕ ТОЛЩИНЫ: 2 точки.
        Assert.Contains("border:2px solid var(--line)", Rule(css, ".opt-body{"), StringComparison.Ordinal);

        // ДЛИНА СТРОКИ — 60 ЗНАКОВ, А НЕ 640 ТОЧЕК (норма §1.5 и §2.7: 50–60 знаков).
        Assert.Contains("max-width:60ch", Rule(css, ".card{"), StringComparison.Ordinal);

        // ОБЕ ТЕМЫ — по системной настройке телефона.
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);

        // ПОДСКАЗКИ — БЕЗ ТОЧКИ В КОНЦЕ (норма GOV.UK: «without any full stops»; §2.7).

    }

    /// <summary>
    /// ЦВЕТА ПАР МЕРЯЮТСЯ, А НЕ ВЫБИРАЮТСЯ НА ГЛАЗ: акцент и текст на нём — в ОБЕИХ темах.
    ///
    /// ⚠️ Числа взяты из макета (`_phone-mock\A-light.html`): **5,38 — светлая пара, 8,11 —
    /// тёмная**; здесь они не пересчитываются заново, а СВЕРЯЮТСЯ со значениями в таблице стилей.
    /// Норма — WCAG 1.4.3: 4,5:1 для обычного текста (`30-mobile-web.md` §7: «проверять надо
    /// каждую пару, а не глазами»).
    ///
    /// Мутации, которые обязаны упасть здесь: «акцент стал светлее фона» · «текст кнопки слился
    /// с подложкой» · «тёмная тема потеряла свой акцент».
    /// </summary>
    [Fact]
    public void Акцент_в_обеих_темах_проходит_по_контрасту()
    {
        var css = PhonePageStyle.Css;

        // СВЕТЛАЯ ТЕМА: белый текст на #0F6CBD — 5,38 (замер макета).
        Assert.Contains("--accent:#0F6CBD", css, StringComparison.Ordinal);
        Assert.Contains("--accent-ink:#FFFFFF", css, StringComparison.Ordinal);

        // ТЁМНАЯ ТЕМА: #10222E на #4CC2FF — 8,11 (замер макета).
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.Contains("--accent:#4CC2FF", css, StringComparison.Ordinal);
        Assert.Contains("--accent-ink:#10222E", css, StringComparison.Ordinal);

        // И ОБА ЧИСЛА СЧИТАЮТСЯ ЗДЕСЬ ЖЕ — чтобы пара не «поехала» незаметно для глаза.
        Assert.True(Contrast("#FFFFFF", "#0F6CBD") >= 5.3, "светлая пара акцента ниже замера макета");
        Assert.True(Contrast("#10222E", "#4CC2FF") >= 8.0, "тёмная пара акцента ниже замера макета");

        // ОБЫЧНЫЙ ТЕКСТ НА КАРТОЧКЕ — ТОЖЕ не ниже 4,5. ⚠️ Цвета — ИЗ ПАЛИТРЫ, а не литералами
        // (пачка D, CH-04): вторая копия чисел `#1A1A1A`/`#FFFFFF` и `#F0F0F0`/`#2B2B2B` делала
        // проверку слепой к правке палитры — она сравнивала литерал с литералом.
        var themes = PhoneThemeColors.Themes();

        Assert.True(
            Contrast(themes[0]["--ink"], themes[0]["--card"]) >= 4.5,
            $"чернила светлой темы не читаются на карточке: {themes[0]["--ink"]} на {themes[0]["--card"]}");

        Assert.True(
            Contrast(themes[1]["--ink"], themes[1]["--card"]) >= 4.5,
            $"чернила тёмной темы не читаются на карточке: {themes[1]["--ink"]} на {themes[1]["--card"]}");
    }

    /// <summary>
    /// КОНТРАСТ ПАРЫ ПО ФОРМУЛЕ WCAG 2.2 — арифметика, а не приборный замер (и это названо честно:
    /// `30-mobile-web.md` §11 помечает свой пересчёт так же).
    /// </summary>
    private static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        var light = Math.Max(a, b);
        var dark = Math.Min(a, b);

        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');

        var r = Channel(value[..2]);
        var g = Channel(value.Substring(2, 2));
        var b = Channel(value.Substring(4, 2));

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Channel(string pair)
    {
        var raw = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// ВХОД РАБОТАЕТ ОБЫЧНОЙ ФОРМОЙ, А БЕДА ПРИХОДИТ СЛОВАМИ И НЕ СТИРАЕТ ВВЕДЁННОЕ.
    ///
    /// **От какого случая.** Прежняя страница входа отправляла код скриптом (<c>fetch</c>), и без
    /// скрипта вход не работал вовсе — а вход обязан работать всегда. Теперь это обычная форма,
    /// а отказ приходит СТРАНИЦЕЙ.
    ///
    /// ⚠️ И ГЛАВНОЕ ПРО ВВЕДЁННОЕ (норма GOV.UK validation: «show them the page again, with the
    /// form fields as the user filled them in»): код возвращается в поле, а рамка помечена
    /// `code-bad`. Мутации, которые обязаны упасть здесь: «вернуть скрипт на страницу входа» ·
    /// «отвечать беду JSON-ом» · «промолчать о причине отказа» · «стереть введённый код» ·
    /// «не пометить поле как ошибочное».
    /// </summary>
    [Fact]
    public void Вход_работает_обычной_формой_и_не_стирает_введённое()
    {
        var clean = PhoneLoginPage.Html("ru");
        var broken = PhoneLoginPage.Html("ru", "код не подошёл", "482913");

        // ФОРМА — ОБЫЧНАЯ: её отправляет браузер, а не скрипт. На входе скрипта нет вовсе.
        Assert.Contains("<form method=\"post\" action=\"/pair\">", clean, StringComparison.Ordinal);
        Assert.Contains("name=\"code\"", clean, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);

        // БЕДА — НА ВИДУ И СЛОВАМИ, РЯДОМ С ПОЛЕМ, а не экраном в стороне. ⚠️ И С `id`: он нужен
        // затем, чтобы `aria-describedby` поля вёл на живую ошибку, а не в пустоту (§5, Ш-28).
        Assert.Contains("<p class=\"error\" id=\"code-error\">", broken, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageError)), broken, StringComparison.Ordinal);
        Assert.Contains("код не подошёл", broken, StringComparison.Ordinal);

        // А ЧИСТАЯ СТРАНИЦА О БЕДЕ МОЛЧИТ: «ошибка» там, где всё в порядке, — это ложь.
        Assert.DoesNotContain(Ru(nameof(PanelStrings.PhonePageError)), clean, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"error\"", clean, StringComparison.Ordinal);

        // ВВЕДЁННОЕ НА МЕСТЕ, И РАМКА ПОМЕЧЕНА: `class="code code-bad"` + `value="482913"`.
        Assert.Contains("class=\"code code-bad\"", broken, StringComparison.Ordinal);
        Assert.Contains("value=\"482913\"", broken, StringComparison.Ordinal);

        // А НА ЧИСТОЙ СТРАНИЦЕ ПОЛЕ ПУСТОЕ И БЕЗ ПОМЕТКИ.
        Assert.Contains("class=\"code\"", clean, StringComparison.Ordinal);
        Assert.Contains("value=\"\"", clean, StringComparison.Ordinal);

        // ПРИЧИНА ЭКРАНИРУЕТСЯ: она приходит из слов панели, но переводчик — человек.
        var withMarkup = PhoneLoginPage.Html("ru", "a <b> & c");

        Assert.Contains("a &lt;b&gt; &amp; c", withMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", withMarkup, StringComparison.Ordinal);

        // И ВВЕДЁННЫЙ КОД — ТОЖЕ: кавычка в нём не закрывает атрибут.
        var sharp = PhoneLoginPage.Html("ru", "код не подошёл", "\" onfocus=\"x");

        Assert.Contains("value=\"&quot; onfocus=&quot;x\"", sharp, StringComparison.Ordinal);
        Assert.DoesNotContain("onfocus=\"x\"", sharp, StringComparison.Ordinal);
    }

    /// <summary>
    /// КАРТОЧКА ДЛЯ ОПРОСА — ТОЛЬКО СОДЕРЖИМОЕ: ни таблицы стилей, ни скрипта, ни самой карточки.
    ///
    /// **Зачем это отдельно.** Опрос подменяет `innerHTML` карточки, а карточка уже стоит на
    /// странице: приди в ответе второй `<div class="card">`, он вложился бы в первый, и скрипт
    /// нашёл бы не ту карточку на следующем круге.
    ///
    /// Мутации, которые обязаны упасть здесь: «отдать в ответе целую страницу» (опрос подменил бы
    /// карточку страницей) · «отдать карточку с `<style>`» (вторая таблица стилей на странице) ·
    /// «отдать карточку без заголовка» · «отдать чужую часть вместо карточки».
    /// </summary>
    [Fact]
    public void Карточка_для_опроса_содержит_только_содержимое()
    {
        var card = PhonePage.Html("ru", Asked(), cardOnly: true);

        Assert.Contains("<h1 class=\"screen-title\">", card, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\" action=\"/answer\">", card, StringComparison.Ordinal);
        Assert.Contains("Делать?", PhonePage.Html("ru", Asked(Question(text: "Делать?")), cardOnly: true), StringComparison.Ordinal);

        Assert.DoesNotContain("<style", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"card\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", card, StringComparison.Ordinal);
        Assert.DoesNotContain("</body>", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРОСЬБА О КАРТОЧКЕ РАЗБИРАЕТСЯ СТРОГО: <c>part=card</c> — и ничего кроме.
    ///
    /// Мутации, которые обязаны упасть здесь: «отдавать карточку на любую строку запроса» ·
    /// «принимать <c>cardx</c>» · «падать на пустой строке».
    /// </summary>
    [Fact]
    public void Просьбу_о_карточке_разбираем_строго()
    {
        Assert.True(PhonePage.CardWanted("part=card"));
        Assert.True(PhonePage.CardWanted("a=1&part=card"));
        Assert.True(PhonePage.CardWanted("part=card&b=2"));

        Assert.False(PhonePage.CardWanted(null));
        Assert.False(PhonePage.CardWanted(string.Empty));
        Assert.False(PhonePage.CardWanted("part=cardx"));
        Assert.False(PhonePage.CardWanted("part="));
        Assert.False(PhonePage.CardWanted("part"));
        Assert.False(PhonePage.CardWanted("other=card"));
    }

    /// <summary>
    /// НОЛЬ ВОПРОСОВ — НЕ ПАДЕНИЕ И НЕ ПУСТАЯ СТРАНИЦА, а сказанное словами состояние.
    ///
    /// Мутации, которые обязаны упасть здесь: «уронить сборку на пустом списке» · «отдать
    /// страницу без слов» (молчание читается как «всё в порядке»).
    /// </summary>
    [Fact]
    public void Без_вопросов_страница_говорит_словами_а_не_молчит()
    {
        var waiting = PhonePage.Html("ru", PhonePageView.Waiting());

        Assert.Contains("</html>", waiting, StringComparison.Ordinal);
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaiting)), waiting, StringComparison.Ordinal);

        // И ЭТО ЖЕ НА КАРТОЧКЕ: опрос получает те же слова, а не пустоту.
        var card = PhonePage.Html("ru", PhonePageView.Waiting(), cardOnly: true);

        Assert.True(card.Trim().Length > 0, "карточка ожидания пуста — опрос подменил бы её пустотой");
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaiting)), card, StringComparison.Ordinal);
    }

    // ---- 12. экран ожидания: не врать и отвечать на четыре вопроса (решение владельца 12.10.2026) --

    /// <summary>
    /// ЭКРАН ОЖИДАНИЯ НЕ ВРАТ: «Агент ждёт ответа» стоит только там, где вопрос ЕСТЬ, а «Ответ
    /// отправлен» — только там, где ответ был.
    ///
    /// **От какого случая.** Заголовок был один на все состояния («Агент ждёт ответа»), и на экране
    /// ожидания он **врал**: агент ничего не спрашивает, а человек читал «ждите» и ждал ответа
    /// на вопрос, которого нет.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть общий заголовок на все состояния» ·
    /// «показать „Агент ждёт ответа“ у ожидания» · «показать „Ответ отправлен“ у ожидания» ·
    /// «сделать заголовок ожидания таким же, как у вопроса».
    /// </summary>
    [Fact]
    public void Экран_ожидания_не_врёт_про_вопрос_и_про_отправленный_ответ()
    {
        var waiting = PhonePage.Html("ru", PhonePageView.Waiting());

        Assert.Contains(
            "<h1 class=\"screen-title\">" + Ru(nameof(PanelStrings.PhonePageWaitingTitle)) + "</h1>",
            waiting,
            StringComparison.Ordinal);

        Assert.DoesNotContain(Ru(nameof(PanelStrings.PhonePageQuestionTitle)), waiting, StringComparison.Ordinal);
        Assert.DoesNotContain(Ru(nameof(PanelStrings.PhonePageAnswered)), waiting, StringComparison.Ordinal);

        // И ЭТО ПРОВЕРЯЕТСЯ НА ТРЁХ ЯЗЫКАХ: заголовки состояний обязаны быть РАЗНЫМИ словами,
        // а не «одинаковыми по смыслу и совпавшими по строке».
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var asked = Word(language, nameof(PanelStrings.PhonePageQuestionTitle));
            var waitingTitle = Word(language, nameof(PanelStrings.PhonePageWaitingTitle));

            Assert.NotEqual(asked, waitingTitle);

            Assert.Contains(
                "<h1 class=\"screen-title\">" + waitingTitle + "</h1>",
                PhonePage.Html(language, PhonePageView.Waiting()),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ЭКРАН ОЖИДАНИЯ ОТВЕЧАЕТ НА ЧЕТЫРЕ ВОПРОСА, НО НЕ ГОВОРИТ ПРО НАШУ МЕХАНИКУ ОПРОСА.
    ///
    /// Четыре вопроса задания владельца: **связь есть? · ждут ли от меня? · что было последним? ·
    /// что делать, если ничего не происходит?** Здесь проверяются три, которые страница знает сама;
    /// «что было последним» живёт отдельным состоянием исхода (у него свои слова и свой путь).
    ///
    /// ⚠️ **Слов про опрос на ожидании НЕТ** (решение владельца 12.10.2026): «страница проверяет
    /// сама каждые 2,5 с» — это про наше устройство, а не про дело человека; у вопроса и у исходов
    /// те же слова остаются, там они по делу.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть слова о числе секунд на ожидание» ·
    /// «убрать строку „что делать, если ничего не происходит“» · «убрать путь „Проверить“».
    /// </summary>
    [Fact]
    public void Экран_ожидания_отвечает_на_четыре_вопроса_и_молчит_о_механике()
    {
        // ⚠️ СМОТРИМ РАЗМЕТКУ, А НЕ СТРАНИЦУ СО СКРИПТОМ: сам скрипт ищет ряд по крючку
        // `[data-auto]`, и по целой странице «есть крючок» доказывало бы работу скрипта,
        // а не разметку. `Markup` вырезает `<script>` — это и есть то, что видит человек.
        var waiting = Markup(PhonePage.Html("ru", PhonePageView.Waiting()));

        // (1) СВЯЗЬ ЕСТЬ — сказано словами, а не оставлено человеку догадываться по пустому месту.
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaiting)), waiting, StringComparison.Ordinal);

        // (2) ЖДУТ ЛИ ОТ МЕНЯ — сказано прямо: ответа пока не нужно.
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaitingTitle)), waiting, StringComparison.Ordinal);
        Assert.DoesNotContain(Ru(nameof(PanelStrings.PhonePageQuestionTitle)), waiting, StringComparison.Ordinal);

        // (4) ЧТО ДЕЛАТЬ, ЕСЛИ НИЧЕГО НЕ ПРОИСХОДИТ — тихой строкой рядом с путём.
        Assert.Contains(Ru(nameof(PanelStrings.PhonePageWaitingNext)), waiting, StringComparison.Ordinal);

        // И ПУТЬ ЕСТЬ — И ОН ОСТАЁТСЯ: «Проверить» ссылкой, работает без скрипта, и крючка
        // `data-auto` у него НЕТ — стало быть, скрипт его не уберёт. Это и есть ответ на четвёртый
        // вопрос («что делать, если ничего не происходит») у экрана, где ничего не происходит.
        Assert.Contains(
            "<a class=\"ghost narrow\" href=\"/\">" + Ru(nameof(PanelStrings.PhonePageCheckButton)) + "</a>",
            waiting,
            StringComparison.Ordinal);

        Assert.Contains("<div class=\"auto\">", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("data-auto", waiting, StringComparison.Ordinal);

        // ⚠️ А ПРО НАШУ МЕХАНИКУ — НИ СЛОВА (фраза-сторож: ключа больше нет вовсе).
        Assert.DoesNotContain("Страница проверяет сама", waiting, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// У КАЖДОГО ИСХОДА ЕСТЬ ОТВЕТ «ЧТО ДАЛЬШЕ», А ПУТЬ ИЗ БЕДЫ НЕ ТРОНУТ.
    ///
    /// **От какого случая (`60-states.md` §4).** «Ответ отправлен.» без «а мне что делать» — это
    /// тупик, только вежливый. А у **беды** пути не меняем: там опроса нет вовсе, и «Повторить» —
    /// единственный выход (решение владельца 12.10.2026: ссылку «Повторить» на экране беды
    /// **не трогать**).
    ///
    /// Мутации, которые обязаны упасть здесь: «убрать строку „что дальше“ у исхода» ·
    /// «заменить „Повторить“ на беде ссылкой „Проверить“» · «поставить беде крючок `data-auto`».
    /// </summary>
    [Fact]
    public void У_каждого_исхода_есть_что_дальше_а_у_беды_путь_не_тронут()
    {
        foreach (var (kind, key) in new[]
                 {
                     (PhonePageKind.Answered, nameof(PanelStrings.PhonePageAnsweredNext)),
                     (PhonePageKind.Taken, nameof(PanelStrings.PhonePageTakenNext)),
                     (PhonePageKind.Cancelled, nameof(PanelStrings.PhonePageCancelledNext)),
                 })
        {
            var html = PhonePage.Html("ru", PhonePageView.Outcome(kind));

            Assert.Contains(Ru(key), html, StringComparison.Ordinal);
        }

        // БЕДА: путь — «Повторить», и НИКАКОГО ряда «Проверить» рядом с ним. ⚠️ Смотрим РАЗМЕТКУ:
        // в скрипте крючок `[data-auto]` упомянут, и по целой странице проверка ловила бы скрипт.
        var problem = Markup(PhonePage.Html("ru", PhonePageView.Problem("ответ не отправился")));

        Assert.Contains(
            "<a class=\"ghost\" href=\"/\">" + Ru(nameof(PanelStrings.PhonePageRepeatButton)) + "</a>",
            problem,
            StringComparison.Ordinal);

        Assert.DoesNotContain("data-auto", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// СВЯЗИ С СЕРВЕРОМ DSH НЕТ — СТРАНИЦА ГОВОРИТ ЭТО, А НЕ ОБЕЩАЕТ ВОПРОС (решение владельца
    /// 13.10.2026, вариант «а»).
    ///
    /// **От какого случая.** Прежняя страница в этом положении показывала ожидание: «Панель на связи
    /// и ждёт вопроса от агента. Как только вопрос появится, он покажется здесь сам». Это обещание,
    /// которого не будет: сервер поднят НЕ панелью, ссылки входа у неё нет, и вопрос не придёт
    /// никогда — человек ждал бы у телефона, пока на компьютере ничего не меняется.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть ожидание на состояние без связи» ·
    /// «убрать строку „посмотрите на компьютер“» · «убрать путь „Проверить“» · «поставить крючок
    /// `data-auto`» (тогда скрипт снял бы путь у состояния, где ничего не происходит).
    /// </summary>
    [Fact]
    public void Без_связи_с_сервером_страница_говорит_правду_а_не_обещает_вопрос()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var markup = Markup(PhonePage.Html(language, PhonePageView.NoEngine()));

            // (1) ЗАГОЛОВОК НАЗЫВАЕТ ПОЛОЖЕНИЕ, И ОН НЕ ТАКОЙ, КАК У ОЖИДАНИЯ.
            Assert.Contains(
                "<h1 class=\"screen-title\">" + Word(language, nameof(PanelStrings.PhonePageNoEngineTitle)) + "</h1>",
                markup,
                StringComparison.Ordinal);

            Assert.NotEqual(
                Word(language, nameof(PanelStrings.PhonePageNoEngineTitle)),
                Word(language, nameof(PanelStrings.PhonePageWaitingTitle)));

            // (2) САМА ПРАВДА: связи нет, вопрос показать нечем.
            Assert.Contains(Word(language, nameof(PanelStrings.PhonePageNoEngine)), markup, StringComparison.Ordinal);

            // (3) ЧТО ДЕЛАТЬ: посмотреть на компьютер.
            Assert.Contains(Word(language, nameof(PanelStrings.PhonePageNoEngineNext)), markup, StringComparison.Ordinal);

            // (4) И НИ ОДНОГО СЛОВА ОБ ОЖИДАНИИ: оно в этом положении — обещание, которого не будет.
            Assert.DoesNotContain(Word(language, nameof(PanelStrings.PhonePageWaiting)), markup, StringComparison.Ordinal);
            Assert.DoesNotContain(Word(language, nameof(PanelStrings.PhonePageWaitingTitle)), markup, StringComparison.Ordinal);
            Assert.DoesNotContain(Word(language, nameof(PanelStrings.PhonePageWaitingNext)), markup, StringComparison.Ordinal);

            // (5) ПУТЬ ОСТАЁТСЯ И КРЮЧКА У НЕГО НЕТ: сервер могут поднять в любую минуту, и
            // «Проверить» — тот же путь для человека, у которого ничего не происходит.
            Assert.Contains(
                "<a class=\"ghost narrow\" href=\"/\">" + Word(language, nameof(PanelStrings.PhonePageCheckButton)) + "</a>",
                markup,
                StringComparison.Ordinal);

            Assert.DoesNotContain("data-auto", markup, StringComparison.Ordinal);
        }

        // И ЭТО ЖЕ НА КАРТОЧКЕ, которой живёт опрос: связь появится — карточка сменится сама.
        var card = PhonePage.Html("ru", PhonePageView.NoEngine(), cardOnly: true);

        Assert.Contains(Ru(nameof(PanelStrings.PhonePageNoEngine)), card, StringComparison.Ordinal);
    }
}
