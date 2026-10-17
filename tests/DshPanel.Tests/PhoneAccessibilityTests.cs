using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДОСТУПНОСТЬ СТРАНИЦЫ ТЕЛЕФОНА — ТО, ЧТО ПРОВЕРЯЕТСЯ РАЗБОРОМ (план `80-accessibility.md` §12).
///
/// **Зачем этот класс.** В банке стоял прямой список: пять проверок, которых у нас НЕТ, и каждая
/// — с нормой и с тем, какая мутация обязана её уронить. Класс закрывает четыре из пяти разбором
/// (пятая — обход Tab'ом и зум 200 % — живёт в браузерном прогоне, `PhoneBrowserTests`), плюс
/// главный долг §1.1: **все пары «фон/текст» из `<style>`**, а не две из четырнадцати.
///
/// **Чего здесь НЕТ.** Смысла перевода, живой читалки, зума человеком и «красоты» — это `глазом`
/// и человеком (`80-accessibility.md` §9(в), §13). Разбор видит числа и разметку, и только их.
/// </summary>
public class PhoneAccessibilityTests
{
    // ---- 1. ВСЕ ПАРЫ «ФОН/ТЕКСТ» ИЗ <style> (§1.1, Ш-1, Ш-3, Ш-6, Ш-7) -----------------------

    /// <summary>
    /// ВСЕ ПАРЫ ЦВЕТОВ ИЗ ТАБЛИЦЫ СТИЛЕЙ ПРОТИВ ПОЛОВ 4,5 И 3,0 — В ОБЕИХ ТЕМАХ.
    ///
    /// **От какого случая.** Сторожились ДВЕ пары из четырнадцати: акцент и «чернила на карточке».
    /// `--ink-2`, `--line`, `--bad`, `--warn`, цвет фокуса и подложка выбранного считались только
    /// в банке — то есть подмена любого из них проходила ворота молча (`80-accessibility.md` §1.1:
    /// «правка на будущее: расширить цикл до **всех** пар»).
    ///
    /// ⚠️ **Цвета берутся ИЗ `<style>`, а не из литералов проверки** — иначе проверка смотрела бы
    /// на свой список, а не на то, что видит телефон (ровно этот дефект уже был: AF14 из журнала).
    /// Пол: **4,5** для текста (у нас нет ни одного «крупного» текста — §1.2) и **3,0** для органов
    /// и их состояний: рамки, цвет фокуса, акцент.
    ///
    /// ⚠️ Подложка выбранного (`--accent-wash`) — это <c>#RRGGBBAA</c>, и её настоящий цвет зависит
    /// от того, на что она положена. Здесь она кладётся на **карточку** (единственное место, где
    /// она бывает) и считается обычной наложенной прозрачностью.
    ///
    /// Мутации, которые обязаны упасть здесь: «акцент стал светлее фона» · «тихий текст слился
    /// с карточкой» · «рамка стала еле видной» · «цвет фокуса подменили на серый» · «`--bad`
    /// потерял контраст» · «тёмная тема не переопределила переменную».
    /// </summary>
    [Fact]
    public void Все_пары_цветов_проходят_пол_в_обеих_темах()
    {
        foreach (var theme in Themes())
        {
            var pairs = new (string What, string Ink, string Back, double Floor)[]
            {
                // ТЕКСТ — пол 4,5 (§1.2: крупного текста у нас нет вовсе).
                ("чернила на карточке (.note, .opt-text, body)", theme["--ink"], theme["--card"], 4.5),
                ("чернила на странице (.note за карточкой)", theme["--ink"], theme["--page"], 4.5),
                ("тихий текст на карточке (.hint, .count)", theme["--ink-2"], theme["--card"], 4.5),
                ("тихий текст на странице", theme["--ink-2"], theme["--page"], 4.5),
                ("текст кнопки на акценте (.action)", theme["--accent-ink"], theme["--accent"], 4.5),
                ("беда на карточке (.problem, .error)", theme["--bad"], theme["--card"], 4.5),
                ("предупреждение на карточке (.warn)", theme["--warn"], theme["--card"], 4.5),
                ("выбранная строка: текст на подложке", theme["--ink"], Wash(theme), 4.5),

                // ОРГАНЫ И ИХ СОСТОЯНИЯ — пол 3,0 (SC 1.4.11).
                ("рамка строки и полей (--line на карточке)", theme["--line"], theme["--card"], 3.0),
                ("акцент на карточке (кружок выбора)", theme["--accent"], theme["--card"], 3.0),
                ("рамка выбранного на подложке", theme["--accent"], Wash(theme), 3.0),
                ("цвет фокуса на карточке", theme["--focus"], theme["--card"], 3.0),
                ("цвет фокуса на странице", theme["--focus"], theme["--page"], 3.0),
            };

            foreach (var (what, ink, back, floor) in pairs)
            {
                var ratio = Contrast(ink, back);

                Assert.True(
                    ratio >= floor,
                    $"{theme["--theme"]}: пара «{what}» ниже пола: {ink} на {back} = "
                    + ratio.ToString("F2", CultureInfo.InvariantCulture) + ", пол "
                    + floor.ToString("F1", CultureInfo.InvariantCulture));
            }
        }

        // И САМ НАБОР ПОЛОН: четырнадцать пар из §1.1 — это ДВЕ ТЕМЫ по семь текстовых
        // и по шесть «органных»; считаем не «сколько вышло», а что темы ДВЕ и что набор не пуст.
        Assert.Equal(2, Themes().Count);
    }

    /// <summary>
    /// В ПАЛИТРЕ НЕТ НИ ОДНОГО ЦВЕТА, КОТОРЫЙ НЕ УЧАСТВУЕТ В ПАРАХ, — «новый цвет не пройдёт молча».
    ///
    /// Это вторая половина долга §1.1: недостаточно перечислить сегодняшние пары, надо, чтобы
    /// **завтрашний** цвет тоже попал под проверку. Поэтому сверяется не список пар, а список
    /// переменных: появилась новая `--что-нибудь` в `:root` — она обязана быть названа в разборе
    /// либо быть заведомо служебной (`--theme` — имя темы, а не цвет).
    ///
    /// Мутации, которые обязаны упасть здесь: «добавить в палитру четвёртый цвет и забыть про него».
    /// </summary>
    [Fact]
    public void В_палитре_нет_цвета_без_своей_пары()
    {
        var named = new HashSet<string>(StringComparer.Ordinal)
        {
            "--page", "--card", "--ink", "--ink-2", "--line", "--accent", "--accent-ink",
            "--accent-wash", "--focus", "--warn", "--bad", "--theme",
        };

        foreach (var theme in Themes())
        {
            foreach (var name in theme.Keys)
            {
                Assert.True(
                    named.Contains(name),
                    $"{theme["--theme"]}: в палитре появилась переменная {name}, которой нет ни в одной "
                    + "паре разбора — новый цвет обязан попасть под проверку контраста");
            }
        }

        Assert.Equal(12, named.Count);
    }

    // ---- 2. ШАГ МЕЖДУ ЦЕЛЯМИ (§2, Ш-11) ------------------------------------------------------

    /// <summary>
    /// КРУГ 24 НЕ ЗАДЕВАЕТ СОСЕДА — ШАГ МЕЖДУ ЦЕЛЯМИ СЧИТАЕТСЯ ПО ТАБЛИЦЕ, А НЕ НА ГЛАЗ.
    ///
    /// **От какого случая.** Стояло только `gap:8px` между вариантами, и в банке прямо сказано:
    /// «поменять `gap:8px` на `gap:0` — **ничего** не упадёт; проверки шага нет» (`Ш-11`).
    ///
    /// Норма — исключение Spacing у SC 2.5.8: «if a **24 CSS pixel diameter circle** is centered
    /// on the bounding box of each, the circles do **not intersect** another target». При
    /// `gap:0` круги всё равно не пересекутся, ПОКА строки ≥ 24 — и это здесь и проверяется:
    /// шаг (высота строки + отбивка) обязан быть **≥ 24**.
    ///
    /// Мутации, которые обязаны упасть здесь: «`gap:8px` → `gap:0` при `min-height` варианта 20» ·
    /// «у `.action` сняли `min-height`» · «между кнопками убрали отбивку и высоту».
    /// </summary>
    [Fact]
    public void Круг_24_не_задевает_соседа()
    {
        var css = PhonePageStyle.Css;

        // ВАРИАНТЫ: строка минус круг, дальше отбивка.
        var optionStep = MinHeight(css, ".opt-body{") + Gap(css, ".opts{");

        Assert.True(
            optionStep >= 24,
            $"шаг между вариантами {optionStep} меньше круга 24 — круги пересекутся (SC 2.5.8, Spacing)");

        // КНОПКИ И ПОЛЯ, стоящие друг под другом: каждая сама по себе не ниже круга, а между
        // строками ответа и «Ответить» стоит отбивка широкого ряда.
        foreach (var selector in new[] { ".action{", ".ghost{", ".own-input{", ".code{" })
        {
            Assert.True(
                MinHeight(css, selector) >= 24,
                $"орган {selector} ниже круга 24 — его цель не покрывает норму шага");
        }

        // ⚠️ И ГЛАВНОЕ, ЧТО ЗДЕСЬ ЛОВИТСЯ: «44 высоты уже решает всё» — неправда, решает ШАГ.
        // Между «Ответить» (`.action.wide`) и строкой опроса отбивка есть; если её снять,
        // шаг останется = высоте кнопки, и это тоже ≥ 24 — поэтому проверка требует ещё и
        // положительной отбивки, а не «повезло с высотой».
        Assert.Contains("margin-top:8px", Rule(css, ".wide{"), StringComparison.Ordinal);
        Assert.Contains("margin:0 0 8px", Rule(css, ".count{"), StringComparison.Ordinal);
    }

    // ---- 3. СВЯЗЬ ОШИБКИ С ПОЛЕМ (§5, Ш-28) --------------------------------------------------

    /// <summary>
    /// ОШИБКА СВЯЗАНА С ПОЛЕМ ПРОГРАММНО: `id` у ошибки И `aria-describedby` у поля.
    ///
    /// **От какого случая.** Слова были, рамка `code-bad` была, а программной связи не было:
    /// у `<p class="error">` не было `id`, у `<input>` — `aria-describedby`. Читалка прочитает
    /// ошибку ДО поля, но не как «ошибка этого поля» (`Ш-28`).
    ///
    /// ⚠️ И ОБРАТНАЯ СТОРОНА ПРОВЕРЯЕТСЯ ЗДЕСЬ ЖЕ: **на чистой странице связи НЕТ и быть не должно** —
    /// ссылка на несуществующий `id` читается как пустота (`40-forms.md` §3).
    ///
    /// Мутации, которые обязаны упасть здесь: «у ошибки убрали `id`» · «у поля убрали
    /// `aria-describedby`» · «`aria-describedby` повесили на чистую страницу» ·
    /// «`aria-describedby` ссылается на чужой `id`».
    /// </summary>
    [Fact]
    public void Ошибка_связана_с_полем_программно()
    {
        var broken = PhoneLoginPage.Html("ru", "код не подошёл", "482913");

        Assert.Contains("<p class=\"error\" id=\"code-error\">", broken, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"code-error\"", broken, StringComparison.Ordinal);

        // ССЫЛКА ВЕДЁТ НА СУЩЕСТВУЮЩИЙ `id` — а не «в никуда».
        Assert.Contains("id=\"code-error\"", broken, StringComparison.Ordinal);

        // И ЧИСТАЯ СТРАНИЦА О СВЯЗИ МОЛЧИТ: там нет ни ошибки, ни описания.
        var clean = PhoneLoginPage.Html("ru");

        Assert.DoesNotContain("aria-describedby", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"code-error\"", clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОЛЕ КОДА — ОДНОРАЗОВЫЙ КОД, А НЕ «НЕ ЗАПОЛНЯТЬ»: `autocomplete="one-time-code"`.
    ///
    /// **От какого случая.** Стояло `autocomplete="off"`, и это прямо мешало: браузер не подставлял
    /// код из сообщения. Норма подтверждена web.dev: «Use <c>autocomplete="one-time-code"</c> on the
    /// input field» (https://web.dev/articles/sms-otp-form); SC 1.3.5 к нам не применяется — код
    /// сопряжения не «данные о человеке», поэтому токен выбирается **по назначению поля**
    /// (`Ш-30`, `40-forms.md` §3).
    ///
    /// А у поля своего ответа `off` ОСТАЁТСЯ: это свободный текст, а не одноразовый код.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернули `autocomplete="off"` полю кода» ·
    /// «поставили `one-time-code` полю своего ответа».
    /// </summary>
    [Fact]
    public void Поле_кода_объявлено_одноразовым_кодом()
    {
        var login = PhoneLoginPage.Html("ru");

        Assert.Contains("autocomplete=\"one-time-code\"", login, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"6\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("autocomplete=\"off\"", login, StringComparison.Ordinal);

        var asked = PhonePage.Html("ru", Asked());

        Assert.Contains("autocomplete=\"off\"", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("autocomplete=\"one-time-code\"", asked, StringComparison.Ordinal);
    }

    // ---- 4. ЗАПРЕТЫ РАЗМЕТКИ И ТАБЛИЦЫ (§3, §4, §7; Ш-19, Ш-23, Ш-40, Ш-45) ------------------

    /// <summary>
    /// В РАЗМЕТКЕ НЕТ НИ `tabindex`, НИ ПОСТОРОННИХ `aria-*`/`role` — ПОРЯДОК ОБХОДА ЭТО РАЗМЕТКА.
    ///
    /// Норма: SC 2.4.3 — «focusable components receive focus in an order that preserves meaning»
    /// (https://www.w3.org/TR/WCAG22/); положительный `tabindex` вырывает орган из порядка, и
    /// человек теряет место. Роли и `aria`-состояния нам не нужны: разметка — обычные `form`,
    /// `label`, `fieldset`, `legend`, `button`, и читалка берёт их оттуда.
    ///
    /// ⚠️ **Единственное разрешённое `aria`-имя — `aria-describedby`**, и только потому, что оно
    /// связывает ошибку с полем (§5, Ш-28). Всё остальное (`aria-live`, `role="alert"`, `aria-hidden`)
    /// появится только вместе с нормой, которая этого потребует, — тогда правило меняется **здесь**.
    ///
    /// Мутации, которые обязаны упасть здесь: «добавить `tabindex="1"` кнопке» · «повесить
    /// `role="alert"`» · «спрятать что-то `aria-hidden`».
    /// </summary>
    [Fact]
    public void Разметка_без_tabindex_и_посторонних_aria()
    {
        foreach (var (name, html) in EveryPage())
        {
            Assert.DoesNotContain("tabindex", html, StringComparison.OrdinalIgnoreCase);

            foreach (Match match in Regex.Matches(html, @"\saria-([a-z-]+)="))
            {
                Assert.True(
                    match.Groups[1].Value == "describedby",
                    $"{name}: в разметке появилось aria-{match.Groups[1].Value} — разрешено только "
                    + "aria-describedby (связь ошибки с полем)");
            }

            Assert.DoesNotContain("role=", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// В ТАБЛИЦЕ СТИЛЕЙ НЕТ АНИМАЦИИ, НЕ РЕЖЕТСЯ ТЕКСТ И НЕ ЗАПИРАЮТСЯ ОТБИВКИ.
    ///
    /// Три нормы, у каждой своё основание:
    /// * **анимации нет вовсе** — SC 2.3.1 и SC 2.3.3: мигание и движение отдельные критерии,
    ///   а «плавная подмена карточки» на телефоне это ещё и батарея (`Ш-40`);
    /// * **`max-height` и `overflow:hidden` не встречаются** — SC 1.4.4 (зум 200 %) и SC 1.4.12
    ///   (чужие настройки отбивок): F69/F80/F104 — «обрезало» и «поле не выросло» (`Ш-22`, `Ш-23`);
    /// * **`!important` нет** — им запирают отбивки, и тогда **чужая** настройка ломает страницу.
    ///
    /// ⚠️ И живое `height` у текста: фиксированная высота — тот же F69 при масштабе. Здесь
    /// запрещены `height` и `max-height` у правил; `min-height` (наша цель 44) — не запрещено.
    ///
    /// Мутации, которые обязаны упасть здесь: «добавить `transition`» · «`@keyframes`» ·
    /// «`max-height:100px`» · «`overflow:hidden`» · «`!important` на `margin`» ·
    /// «`height:40px` у подсказки».
    /// </summary>
    [Fact]
    public void Таблица_стилей_без_анимации_и_замков_на_текст()
    {
        var css = PhonePageStyle.Css;

        foreach (var forbidden in new[] { "animation", "transition", "@keyframes", "!important", "overflow:hidden" })
        {
            Assert.DoesNotContain(forbidden, css, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("max-height", css, StringComparison.Ordinal);

        // `height` без «min-» и без «max-» запрещена ВСЮДУ, КРОМЕ системного кружка выбора:
        // его 24×24 — это и есть форма органа (SC 1.4.11), текста в нём нет, и обрезать в нём
        // нечего. У любого другого правила фиксированная высота — это F69 при масштабе.
        // ⚠️ Сплошная проверка нашла ровно это место (`.opt-input{width:24px;height:24px;…}`)
        // и заодно доказала, что она видит таблицу, а не пустоту.
        const string Height = "height:";

        Assert.Contains(Height, css, StringComparison.Ordinal);

        foreach (Match match in Regex.Matches(css, @"(?<![a-z-])height:"))
        {
            // Селектор правила — текст от предыдущей `}` до `{` перед совпадением.
            var open = css.LastIndexOf('{', match.Index);
            var previous = css.LastIndexOf('}', match.Index);
            var selector = css[(previous + 1)..open].Trim();

            Assert.True(
                selector == ".opt-input",
                $"в таблице стилей фиксированная высота у «{selector}» — при масштабе текст "
                + "обрежется (F69); вне системного кружка выбора `height` запрещена");
        }
    }

    // ---- 5. ПОДПИСИ И ПОДСКАЗКИ: ТОН И ЧИСЛО ФРАЗ (40-forms §1, §3, §11(б)) -------------------

    /// <summary>
    /// ПОДПИСИ КОНЧАЮТСЯ БЕЗ ДВОЕТОЧИЯ — все пятнадцать, в трёх языках.
    ///
    /// Норма прямая: GOV.UK «They should be short, direct and written in sentence case. **Do not use
    /// colons at the end of labels.**» (https://design-system.service.gov.uk/components/text-input/).
    /// До 12.10.2026 двоеточие стояло у пятнадцати подписей в каждом языке (`40-forms.md` §11(б) п. 2),
    /// в китайском — полноширинное `：`, поэтому поиск обычного `:` там давал ноль.
    ///
    /// ⚠️ **Список подписей назван ЗДЕСЬ**, а не выведен «всё, что кончается двоеточием»: двоеточие
    /// законно у строки **журнала** (`SettingsLoadLog` = «настройки:») и у **заголовка с числом**
    /// перед перечнем. Это не подписи поля, и запрет GOV.UK на них не распространяется.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть двоеточие одной подписи» (в любом языке) ·
    /// «дописать полноширинное `：` в китайскую подпись».
    /// </summary>
    [Fact]
    public void Подписи_без_двоеточия_в_конце()
    {
        string[] labels =
        {
            nameof(PanelStrings.BackupFolderLabel),
            nameof(PanelStrings.BackupKeysLabel),
            nameof(PanelStrings.BackupListCountFormat),
            nameof(PanelStrings.BackupScopeExtraLabel),
            nameof(PanelStrings.BackupScopeLabel),
            nameof(PanelStrings.SettingsActiveAgentLabel),
            nameof(PanelStrings.SettingsBackupEveryLabel),
            nameof(PanelStrings.SettingsBackupKeepLabel),
            nameof(PanelStrings.SettingsBalancePeriodLabel),
            nameof(PanelStrings.SettingsBalanceThresholdLabel),
            nameof(PanelStrings.SettingsKeysDirsLabel),
            nameof(PanelStrings.SettingsLanguageLabel),
            nameof(PanelStrings.SettingsPeakNotifyLabel),
            nameof(PanelStrings.SettingsPortLabel),
            nameof(PanelStrings.SettingsThemeLabel),
            nameof(PanelStrings.SettingsWorkDirLabel),
        };

        Assert.Equal(16, labels.Length);

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in labels)
            {
                var text = Loc.TIn(language, key);

                Assert.False(
                    text.EndsWith(':') || text.EndsWith('：'),
                    $"{language}/{key}: подпись кончается двоеточием — «{text}» (GOV.UK: «Do not use colons at the end of labels»)");
            }
        }
    }

    /// <summary>
    /// ПОДСКАЗКА ПОД ПОЛЕМ — ОДНА КОРОТКАЯ ФРАЗА БЕЗ ТОЧКИ, А ДЛИННОЕ ЖИВЁТ НАД ПОЛЕМ.
    ///
    /// Норма GOV.UK: «Keep hint text to a single short sentence, **without any full stops**»
    /// (https://design-system.service.gov.uk/components/text-input/); `40-forms.md` §3 требует ровно
    /// этого, приписав: «всё длинное выносится **над** полем обычным текстом».
    ///
    /// **От какого случая.** `SettingsPortHint` был ТРЕМЯ предложениями с тремя точками, и читалка
    /// читала их целиком при каждом входе в поле. Сегодня у порта есть короткая подсказка под полем
    /// и отдельное пояснение над ним — и обе сверяются здесь.
    ///
    /// ⚠️ «Короткая» — это число, а не чувство: **120 знаков** потолок (одна строка подсказки
    /// у GOV.UK — до двух строк по 60 знаков, `_design-standard-draft.md` §1.5). Длинное
    /// пояснение над полем потолка не имеет, но точку иметь право имеет: это не подсказка.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть три предложения в подсказку» ·
    /// «дописать точку в конце короткой фразы» · «выбросить пояснение над полем».
    /// </summary>
    [Fact]
    public void Подсказка_под_полем_коротка_и_без_точки()
    {
        Assert.True(
            Loc.TIn("ru", nameof(PanelStrings.SettingsPortHint)).Length <= 120,
            "подсказка под полем порта длиннее одной короткой фразы");

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in new[]
                     {
                         nameof(PanelStrings.SettingsPortHint),
                         nameof(PanelStrings.SettingsLanguageAppliesNextStart),
                     })
            {
                var text = Loc.TIn(language, key);

                Assert.False(
                    text.EndsWith('.') || text.EndsWith('。') || text.EndsWith('，'),
                    $"{language}/{key}: подсказка кончается точкой или запятой — «{text}»");

                Assert.True(
                    text.Length <= 160,
                    $"{language}/{key}: подсказка в {text.Length} знаков — это абзац, а не одна фраза");
            }

            // И ДЛИННОЕ ПОЯСНЕНИЕ ПОРТА — ЕСТЬ: без него числа и обещания пропали бы совсем.
            Assert.Contains("3080", Loc.TIn(language, nameof(PanelStrings.SettingsPortExplain)), StringComparison.Ordinal);

            // ...И ПОЯСНЕНИЕ ЯЗЫКА — ТОЖЕ: «применится после перезапуска» сказано словами,
            // а не оставлено человеку догадываться. ⚠️ Мерим НЕ знаками: 32 знака русского текста
            // и 24 знака китайского — это одно и то же по смыслу, а по длине разное (в китайском
            // один знак несёт слово). Поэтому требуется СЛОВО ПРО ПЕРЕЗАПУСК, а не длина строки.
            var explain = Loc.TIn(language, nameof(PanelStrings.SettingsLanguageExplain));

            Assert.False(string.IsNullOrWhiteSpace(explain), $"{language}: пояснение к языку пустое");
            Assert.True(
                explain.Contains("перезапуск", StringComparison.OrdinalIgnoreCase)
                || explain.Contains("restart", StringComparison.OrdinalIgnoreCase)
                || explain.Contains('重', StringComparison.Ordinal),
                $"{language}: в пояснении к языку не сказано, что выбранное применится после перезапуска: «{explain}»");

        };
    }
    // ---- подстановки --------------------------------------------------------------------------

    private static PhoneQuestion Question() =>
        new("q1", "Вопрос", "Ставить задачу?", "Ход ждёт ответа", new[] { "Да", "Нет" });

    private static PhonePageView Asked() =>
        new(PhonePageKind.Question, "event-acc-1", new[] { Question() }, Live: true, Screen: 0, Total: 1);

    /// <summary>
    /// Страницы прогона — те же, что у разбора классов (`PhonePageTests.EveryPage`).
    ///
    /// ⚠️ **СТРАНИЦА С ИМЕНЕМ УСТРОЙСТВА ЗДЕСЬ ОБЯЗАНА БЫТЬ** (пачка долгов 14.10.2026, §4): прежде
    /// в этом наборе не было НИ ОДНОЙ страницы с непустым `Device`, а строка имени устройства
    /// появляется только у сопряжённого телефона — значит разборы этого класса до неё не доходили
    /// вовсе, и упасть на её разметке не могли в принципе (слепое место; банк
    /// `80-accessibility.md` §13). Набор — КОПИЯ набора `PhonePageTests`, и копия отстала: там
    /// страница есть с 12.10.2026, здесь её не было.
    /// </summary>
    private static IEnumerable<(string Name, string Html)> EveryPage()
    {
        yield return ("вход", PhoneLoginPage.Html("ru"));
        yield return ("вход с бедой", PhoneLoginPage.Html("ru", "код не подошёл"));
        yield return ("вход с бедой и введённым кодом", PhoneLoginPage.Html("ru", "код не подошёл", "482913"));
        yield return ("ожидание", PhonePage.Html("ru", PhonePageView.Waiting()));

        // ⚠️ СТРАНИЦА С НЕПУСТЫМ `Device` — та самая, что бывает только у сопряжённого телефона.
        yield return ("ожидание с именем устройства", PhonePage.Html(
            "ru", PhonePageView.Waiting() with { Device = "Pixel 8" }));

        yield return ("вопрос", PhonePage.Html("ru", Asked()));
        yield return ("ответ отправлен", PhonePage.Html("ru", PhonePageView.Outcome(PhonePageKind.Answered)));
        yield return ("беда", PhonePage.Html("ru", PhonePageView.Problem("ответ не отправился")));
        yield return ("карточка для опроса", PhonePage.Html("ru", PhonePageView.Waiting(), cardOnly: true));
    }

    /// <summary>
    /// У КАЖДОГО КЛАССА РАЗМЕТКИ ЕСТЬ ПРАВИЛО — И У КЛАССА СТРОКИ ИМЕНИ УСТРОЙСТВА ТОЖЕ.
    ///
    /// **От какого случая (пачка долгов 14.10.2026, §4).** В этом классе набор страниц был КОПИЕЙ
    /// набора `PhonePageTests` и отстал от него: страницы с непустым `Device` в копии не было,
    /// и разбор классов до строки имени устройства не доходил. Норма — `_design-standard-draft.md`
    /// §2.3: «класс без правила — дефект».
    ///
    /// ⚠️ **Класс строки устройства берётся ИЗ РАЗМЕТКИ, а не написан здесь числом:** сегодня это
    /// общий `hint` (крючок устройства — довод `data-device`, а не класс: правка 12.10.2026), и
    /// написанный здесь литерал `hint` проверял бы мою память, а не страницу. Со сменой разметки
    /// проверка поедет за ней.
    ///
    /// Мутации, которые обязаны упасть здесь: **«убрать правило класса строки устройства»** ·
    /// «убрать страницу с именем устройства из набора» · «переименовать класс в разметке
    /// и забыть правило».
    /// </summary>
    [Fact]
    public void У_класса_страницы_с_именем_устройства_есть_правило()
    {
        var css = PhonePageStyle.Css;
        var pages = EveryPage().ToList();

        // (1) СТРАНИЦА С НЕПУСТЫМ `Device` — В НАБОРЕ: без неё разбор классов до неё не доходит.
        var device = Assert.Single(pages, pair => pair.Html.Contains("data-device", StringComparison.Ordinal));

        Assert.Equal("ожидание с именем устройства", device.Name);

        // (2) КЛАСС ЭТОЙ СТРОКИ — ИЗ ЕЁ ЖЕ РАЗМЕТКИ, и правило у него обязано быть.
        var line = Regex.Match(device.Html, "<p class=\"([^\"]+)\" data-device>");

        Assert.True(line.Success, "строка имени устройства потеряла крючок data-device");

        foreach (var spot in Classes(line.Groups[1].Value))
        {
            Assert.True(
                css.Contains("." + spot, StringComparison.Ordinal),
                $"у класса «{spot}» строки имени устройства нет правила в таблице стилей");
        }

        // (3) И ВЕСЬ НАБОР ЦЕЛИКОМ — по каждому классу каждой страницы, а не только по этой.
        foreach (var (name, html) in pages)
        {
            foreach (Match group in Regex.Matches(html, "class=\"([^\"]+)\""))
            {
                foreach (var spot in Classes(group.Groups[1].Value))
                {
                    Assert.True(
                        css.Contains("." + spot, StringComparison.Ordinal),
                        $"{name}: у класса «{spot}» нет правила в таблице стилей");
                }
            }
        }
    }

    /// <summary>Имена классов из значения атрибута: `class="a b"` → `a`, `b`.</summary>
    private static IEnumerable<string> Classes(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// ЦВЕТА ОБЕИХ ТЕМ — из общего разбора таблицы стилей (<see cref="PhoneThemeColors"/>).
    ///
    /// ⚠️ **Разбор переехал в <see cref="PhoneThemeColors"/> (пачка D, CH-04):** контраст обычного
    /// текста считался в двух других проверках на ЛИТЕРАЛАХ, а не на палитре. Чтобы палитра была
    /// одна на весь набор, разбор живёт одним местом; здесь остался только вызов — и он же
    /// доказывает, что разбор по-прежнему даёт две темы с полным набором имён.
    /// </summary>
    private static List<Dictionary<string, string>> Themes() => PhoneThemeColors.Themes().ToList();

    /// <summary>
    /// ПОДЛОЖКА ВЫБРАННОГО НА КАРТОЧКЕ: <c>--accent-wash</c> — это <c>#RRGGBBAA</c>, и её
    /// настоящий цвет зависит от того, на что она положена (единственное место — карточка).
    /// </summary>
    private static string Wash(Dictionary<string, string> theme)
    {
        var wash = theme["--accent-wash"];
        var card = theme["--card"];

        Assert.True(wash.Length == 9, $"подложка {wash} — не #RRGGBBAA: наложить её нечем");

        var alpha = int.Parse(wash.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        var mixed = "#";

        for (var channel = 0; channel < 3; channel++)
        {
            var top = int.Parse(wash.Substring(1 + (channel * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var bottom = int.Parse(card.Substring(1 + (channel * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            var value = (int)Math.Round((top * alpha) + (bottom * (1 - alpha)), MidpointRounding.AwayFromZero);

            mixed += value.ToString("X2", CultureInfo.InvariantCulture);
        }

        return mixed;
    }

    /// <summary>`min-height` правила в точках — «цель под палец» и шаг считаются из него.</summary>
    private static int MinHeight(string css, string selector)
    {
        var value = Regex.Match(Rule(css, selector), @"min-height:(\d+)px");

        Assert.True(value.Success, $"у правила {selector} нет min-height — цель нечем измерить");

        return int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Отбивка `gap` правила в точках.</summary>
    private static int Gap(string css, string selector)
    {
        var value = Regex.Match(Rule(css, selector), @"gap:(\d+)px");

        Assert.True(value.Success, $"у правила {selector} нет gap — шаг между целями нечем измерить");

        return int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture);
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

    /// <summary>Контраст пары по формуле WCAG 2.2 — арифметика, а не приборный замер.</summary>
    private static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// Относительная яркость канала. ⚠️ Порог **0,04045** — редакция WCAG с 2021 года; в старых
    /// проверках проекта стоит 0,03928, и это названо в банке (`80-accessibility.md` §1.1).
    /// </summary>
    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');

        return (0.2126 * Channel(value[..2])) + (0.7152 * Channel(value.Substring(2, 2)))
            + (0.0722 * Channel(value.Substring(4, 2)));
    }

    private static double Channel(string pair)
    {
        var raw = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        return raw <= 0.04045 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
    }
}
