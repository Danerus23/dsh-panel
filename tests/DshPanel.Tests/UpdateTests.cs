using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОБНОВЛЕНИЕ ПАНЕЛИ — ПЕРВЫЙ СРЕЗ: проверка выпусков и извещение.
///
/// Четыре вещи, и у каждой своя беда:
///
/// 1. **версии сравниваются ЧИСЛАМИ, а не строками** — «2.10.0» новее «2.9.9», иначе человек
///    не получил бы новый выпуск никогда, а панель молилась бы на строковый порядок;
/// 2. **суточный гейт нельзя обойти переводом часов** — отметка в будущем означает «рано»,
///    а не «прошло больше суток»;
/// 3. **шарик один раз на версию и никогда для пропущенной** — проверка идёт при каждом запуске
///    панели, и без этого человек получал бы одно и то же сообщение каждый день;
/// 4. **сети в проверках нет вовсе** — уговор <see cref="IUpdateClient"/> подставляется,
///    а разбор ответа GitHub — чистая функция. Живой запрос это сеть от имени владельца,
///    то есть его право, а не наше.
///
/// ⚠️ Ни скачивания, ни SHA-256, ни замены файлов здесь нет и быть не должно: это второй срез.
/// Проверки покрывают ровно то, что есть.
/// </summary>
public class UpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Notes =
        "## RU\n\nРусские заметки\n\n## EN\n\nEnglish notes\n\n## ZH\n\n中文说明";

    // ------------------------------------------------------------------ версия

    /// <summary>
    /// ЧИСЛОВАЯ ЧАСТЬ ВЕРСИИ: хеш сборки и буква тега отбрасываются. Хеш дописывает сам .NET,
    /// буква «v» — обычай тегов GitHub, и ни то, ни другое версией не является.
    /// </summary>
    [Theory]
    [InlineData("2.0.0+481fb6b", "2.0.0")]
    [InlineData("v1.21.0", "1.21.0")]
    [InlineData(" V2.10.0+HASH ", "2.10.0")]
    [InlineData("2.0.0", "2.0.0")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Числовая_часть_версии_отбрасывает_хеш_и_букву_тега(string? version, string expected)
    {
        Assert.Equal(expected, UpdateDecisions.Numeric(version));
    }

    /// <summary>
    /// СРАВНЕНИЕ ИДЁТ ПО ЧИСЛАМ, А НЕ ПО СТРОКАМ. Случай «2.10.0 против 2.9.9» — тот самый,
    /// ради которого правило и заведено: по строкам «2.9.9» больше, и панель объявила бы
    /// последней версией старый выпуск.
    /// </summary>
    [Theory]
    [InlineData("2.10.0", "2.9.9", true)]
    [InlineData("2.9.9", "2.10.0", false)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.0.1", "2.0.0", true)]
    [InlineData("3.0.0", "2.99.99", true)]
    [InlineData("2.0.0+abc", "2.0.0+def", false)]
    [InlineData("v2.1.0", "2.0.0", true)]
    [InlineData("2.0", "2.0.0", false)]
    [InlineData("2.0.0.1", "2.0.0", true)]
    [InlineData("", "2.0.0", false)]
    [InlineData("2.0.0", "", true)]
    [InlineData(null, null, false)]
    public void Версии_сравниваются_по_числам(string? candidate, string? current, bool expected)
    {
        Assert.Equal(expected, UpdateDecisions.IsNewer(candidate, current));
    }

    /// <summary>
    /// ПРИ РАВНЫХ ЧИСЛАХ РЕШАЕТ ПРЕДРЕЛИЗНЫЙ ХВОСТ: финальный выпуск новее предварительного,
    /// а хвосты сравниваются по SemVer («rc.10» новее «rc.2»). Без этого правила человек,
    /// поставивший предрелиз 2.0, не получил бы финальный выпуск 2.0 никогда.
    /// </summary>
    [Theory]
    [InlineData("2.0.0", "2.0.0-rc.1", true)]
    [InlineData("2.0.0-rc.1", "2.0.0", false)]
    [InlineData("2.0.0-rc.10", "2.0.0-rc.2", true)]
    [InlineData("2.0.0-rc.2", "2.0.0-rc.10", false)]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1", false)]
    [InlineData("2.0.1-rc.1", "2.0.0", true)]
    public void Предрелизный_хвост_решает_при_равных_числах(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateDecisions.IsNewer(candidate, current));
    }

    // ------------------------------------------------------------------ суточный гейт

    /// <summary>
    /// РАЗ В СУТКИ, И НЕ ЧАЩЕ. Отметки нет — «пора» (панель, поставленная впервые, узнаёт
    /// о выпуске при первом же запуске), 23 часа — «рано», 25 часов — «пора».
    ///
    /// <c>-1</c> означает «отметки нет вовсе»: параметр числовой, потому что набор случаев
    /// в <c>InlineData</c> обязан быть одного типа.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(25, true)]
    public void Проверка_идёт_не_чаще_раза_в_сутки(int hoursAgo, bool expected)
    {
        var stamp = hoursAgo < 0 ? null : Pricing.PricingDecisions.Stamp(Now.AddHours(-hoursAgo));

        Assert.Equal(expected, UpdateDecisions.ShouldCheck(Now, stamp, isolatedRun: false));
    }

    /// <summary>
    /// ЧАСЫ ПЕРЕВЕДЕНЫ НАЗАД — ПРОВЕРЯТЬ НЕЛЬЗЯ, и это НЕ ошибка. Отметка в будущем иначе
    /// обходила бы суточный гейт: человек отматывает время вперёд, панель считает «прошло больше
    /// суток» и стучится в GitHub сколько угодно раз. Молчание здесь — правильный ответ,
    /// и человеку о нём говорить нечем: он не сделал ничего плохого.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(240)]
    public void Отметка_в_будущем_значит_рано(int hoursAhead)
    {
        var stamp = Pricing.PricingDecisions.Stamp(Now.AddHours(hoursAhead));

        Assert.False(UpdateDecisions.ShouldCheck(Now, stamp, isolatedRun: false));
    }

    /// <summary>
    /// МУСОР В ОТМЕТКЕ ЗНАЧИТ «НИКОГДА НЕ ПРОВЕРЯЛИ»: файл настроек правят руками,
    /// и неразобранная отметка обязана приводить к проверке, а не закрывать её навсегда.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("мусор")]
    [InlineData("0")]
    public void Мусор_в_отметке_значит_пора(string? stamp)
    {
        Assert.True(UpdateDecisions.ShouldCheck(Now, stamp, isolatedRun: false));
    }

    /// <summary>
    /// ИЗОЛИРОВАННЫЙ ПРОГОН НЕ ПРОВЕРЯЕТ ВЫПУСКИ ПО РАСПИСАНИЮ НИКОГДА — даже когда «пора»:
    /// это работа панели с сетью, и в прогоне проверки её нет вовсе.
    /// </summary>
    [Fact]
    public void Изолированный_прогон_по_расписанию_не_проверяет()
    {
        Assert.False(UpdateDecisions.ShouldCheck(Now, null, isolatedRun: true));
        Assert.True(UpdateDecisions.ShouldCheck(Now, null, isolatedRun: false));
    }

    /// <summary>
    /// ЗАПИСЬ ОТМЕТКИ И ЕЁ РАЗБОР — ОДНА ДВЕРЬ НА ПАНЕЛЬ, и это дверь ЦЕН: формат круговой
    /// даты-времени живёт в <c>PricingDecisions.Stamp</c>. Вторая реализация формата однажды
    /// разошлась бы с первой, и отметки перестали бы читаться.
    /// </summary>
    [Fact]
    public void Отметка_проверки_читается_тем_же_форматом_что_цены()
    {
        var stamp = Pricing.PricingDecisions.Stamp(Now);

        Assert.True(Pricing.PricingDecisions.TryParse(stamp, out var back));
        Assert.Equal(Now, back);
        Assert.Equal(stamp, Pricing.PricingDecisions.Stamp(back));
    }

    // ------------------------------------------------------------------ шарик

    /// <summary>
    /// ШАРИК ОДИН РАЗ НА ВЕРСИЮ и никогда для пропущенной. Проверка выпусков идёт при каждом
    /// запуске панели: без правила «один раз» человек получал бы одно и то же сообщение каждый
    /// день, пока не обновится, а «пропустить версию» перестало бы что-либо значить.
    /// </summary>
    [Fact]
    public void Шарик_говорит_один_раз_на_версию()
    {
        Assert.True(UpdateDecisions.ShouldAnnounce("2.1.0", string.Empty, "2.0.0", string.Empty));
        Assert.False(UpdateDecisions.ShouldAnnounce("2.1.0", string.Empty, "2.0.0", "2.1.0"));

        // Версии с хешем и буквой тега — та же версия: второй раз о ней говорить нечего.
        Assert.False(UpdateDecisions.ShouldAnnounce("v2.1.0", string.Empty, "2.0.0", "2.1.0+abc"));

        // Выпуск на GitHub мог и откатиться назад: «1.21.1» после рассказа про «1.22.0» только путает.
        Assert.False(UpdateDecisions.ShouldAnnounce("2.1.0", string.Empty, "2.0.0", "2.2.0"));
        Assert.True(UpdateDecisions.ShouldAnnounce("2.3.0", string.Empty, "2.0.0", "2.2.0"));
    }

    /// <summary>
    /// О ВЕРСИИ, КОТОРАЯ НЕ НОВЕЕ ПРОПУЩЕННОЙ, ПАНЕЛЬ ТОЖЕ МОЛЧИТ: выпуск на GitHub мог
    /// откатиться назад, и шарик про «2.1.0» после ответа «пропустить 2.2.0» только путает.
    /// А следующий выпуск НОВЕЕ пропущенного — уже новость: иначе один пропуск выключил бы
    /// извещения навсегда.
    /// </summary>
    [Fact]
    public void Версия_не_новее_пропущенной_не_объявляется()
    {
        Assert.False(UpdateDecisions.ShouldAnnounce("2.1.0", "2.2.0", "2.0.0", string.Empty));
        Assert.False(UpdateDecisions.ShouldAnnounce("2.2.0", "2.2.0", "2.0.0", string.Empty));
        Assert.True(UpdateDecisions.ShouldAnnounce("2.3.0", "2.2.0", "2.0.0", string.Empty));
    }

    /// <summary>
    /// ПРОПУЩЕННАЯ ВЕРСИЯ — РЕШЕНИЕ ЧЕЛОВЕКА, И ОНО СИЛЬНЕЕ НОВИЗНЫ: о версии, которую он
    /// пропустил, панель молчит даже тогда, когда она всё ещё новее установленной. А следующий
    /// выпуск новее пропущенного — уже новость, и о нём говорят.
    /// </summary>
    [Fact]
    public void Пропущенная_версия_не_объявляется()
    {
        Assert.False(UpdateDecisions.ShouldAnnounce("2.1.0", "2.1.0", "2.0.0", string.Empty));
        Assert.False(UpdateDecisions.ShouldAnnounce("v2.1.0", "2.1.0+abc", "2.0.0", string.Empty));
        Assert.True(UpdateDecisions.ShouldAnnounce("2.3.0", "2.1.0", "2.0.0", string.Empty));
    }

    /// <summary>
    /// НЕЧЕГО ОБЪЯВЛЯТЬ, КОГДА ВЫПУСКА НЕТ ИЛИ ОН НЕ НОВЕЕ: пустой ответ GitHub и выпуск,
    /// равный установленной панели, — не новость.
    /// </summary>
    [Theory]
    [InlineData(null, "2.0.0", false)]
    [InlineData("", "2.0.0", false)]
    [InlineData("   ", "2.0.0", false)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("1.9.0", "2.0.0", false)]
    [InlineData("2.0.1", "2.0.0", true)]
    public void Нечего_объявлять_когда_выпуска_нет_или_он_не_новее(string? latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateDecisions.ShouldAnnounce(latest, string.Empty, current, string.Empty));
    }

    // ------------------------------------------------------------------ заметки

    /// <summary>
    /// ЗАМЕТКИ — БЛОК НУЖНОГО ЯЗЫКА. Тело выпуска разбито заголовками <c>## RU</c>, <c>## EN</c>,
    /// <c>## ZH</c>, и панель показывает ТОЛЬКО свой блок: иначе человек читал бы заметки
    /// на трёх языках сразу.
    /// </summary>
    [Theory]
    [InlineData("ru", "Русские заметки")]
    [InlineData("en", "English notes")]
    [InlineData("zh", "中文说明")]
    [InlineData("RU", "Русские заметки")]
    [InlineData(" ru ", "Русские заметки")]
    public void Заметки_берутся_на_языке_панели(string language, string expected)
    {
        Assert.Equal(expected, UpdateDecisions.PickNotes(Notes, language));
    }

    /// <summary>
    /// ЧУЖОЙ ЯЗЫК ПОЛУЧАЕТ АНГЛИЙСКИЙ БЛОК, а не пустое место: пустое окно человеку хуже,
    /// чем заметки не на его языке (правило панели 1.x).
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("")]
    [InlineData(null)]
    public void Чужой_язык_получает_английский_блок(string? language)
    {
        Assert.Equal("English notes", UpdateDecisions.PickNotes(Notes, language));
    }

    /// <summary>
    /// ПУСТОЙ БЛОК НУЖНОГО ЯЗЫКА — ЭТО «БЛОКА НЕТ»: панель берёт английский, а не показывает
    /// пустоту. Так же читает тело и панель 1.x.
    /// </summary>
    [Fact]
    public void Пустой_блок_своего_языка_заменяется_английским()
    {
        const string body = "## RU\n\n## EN\n\nEnglish notes";

        Assert.Equal("English notes", UpdateDecisions.PickNotes(body, "ru"));
    }

    /// <summary>
    /// НЕТ НИ ОДНОГО ПОЗНАВАЕМОГО БЛОКА — ВЕСЬ ТЕКСТ КАК ЕСТЬ: так выглядят выпуски до появления
    /// языковых блоков, и показать вместо них пустоту значило бы соврать, что заметок нет.
    /// </summary>
    [Fact]
    public void Тело_без_блоков_показывается_целиком()
    {
        const string body = "Обычные заметки к выпуску\n\n- пункт\n- другой";

        Assert.Equal(body, UpdateDecisions.PickNotes(body, "ru"));
        Assert.Equal(body, UpdateDecisions.PickNotes(body, "zh"));
    }

    /// <summary>
    /// ЗАГОЛОВОК ВНУТРИ ОГРАЖДЁННОГО БЛОКА — НЕ ЗАГОЛОВОК. Тело выпуска приходит с GitHub и его
    /// правят руками: история, которая рассказывает про формат заметок и показывает пример
    /// в ограждении, иначе объявила бы пример настоящим блоком — и настоящий перевод был бы отброшен.
    ///
    /// Проверяется это ДВУМЯ языками сразу: разница между «пример не стал блоком» и «пример стал
    /// блоком» видна только на запросе того языка, который в примере назван.
    /// </summary>
    [Fact]
    public void Заголовок_в_ограждении_не_считается_блоком()
    {
        const string body =
            "## RU\n\nНастоящие заметки\n\n```\n## EN\n\nэто пример, а не блок\n```\n";

        var russian = UpdateDecisions.PickNotes(body, "ru");
        var english = UpdateDecisions.PickNotes(body, "en");

        // Блока `en` в теле нет, и пример в ограждении его не создал: английский НЕ получил
        // «это пример, а не блок» как свои заметки, а взял единственный настоящий блок.
        // Проверяется это началом строки, а не поиском подстроки: пример лежит ВНУТРИ русского
        // блока как его содержимое — это ограждение, а не блок, и убирать его из заметок нельзя.
        Assert.StartsWith("Настоящие заметки", english, StringComparison.Ordinal);
        Assert.Equal(russian, english);

        // Служебный заголовок СВОЕГО языка в заметки не попадает: его видит только разбор.
        Assert.DoesNotContain("## RU", russian, StringComparison.Ordinal);
        Assert.StartsWith("Настоящие заметки", russian, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРИМЕР В ОГРАЖДЕНИИ НЕ ОТБИРАЕТ БЛОК У НАСТОЯЩЕГО ЯЗЫКА. Тело рассказывает про формат
    /// заметок и показывает «## EN» в ограждении — настоящий английский блок при этом обязан
    /// остаться собой, иначе человек прочитал бы пример вместо перевода.
    /// </summary>
    [Fact]
    public void Пример_в_ограждении_не_подменяет_настоящий_блок()
    {
        const string body =
            "## RU\n\nРусские заметки\n\n## EN\n\nНастоящие английские\n\n```\n## EN\n\nпример\n```\n";

        Assert.Equal("Русские заметки", UpdateDecisions.PickNotes(body, "ru"));
        Assert.StartsWith("Настоящие английские", UpdateDecisions.PickNotes(body, "en"), StringComparison.Ordinal);
    }

    /// <summary>
    /// СВОЕГО БЛОКА НЕТ, А ЧУЖИЕ ЕСТЬ — БЕРЁТСЯ ПЕРВЫЙ НЕПУСТОЙ, А НЕ ТЕЛО С ЗАГОЛОВКАМИ.
    /// Английский идёт первым (правило панели 1.x), за ним русский — источник истины проекта.
    /// Отдать вместо этого тело как есть значило бы показать человеку «## RU» и служебные
    /// строки разметки как текст заметок.
    /// </summary>
    [Fact]
    public void Без_английского_блока_берётся_первый_непустой()
    {
        const string onlyRussian = "## RU\n\nТолько по-русски";
        const string onlyChinese = "## ZH\n\n只有中文";

        Assert.Equal("Только по-русски", UpdateDecisions.PickNotes(onlyRussian, "en"));
        Assert.Equal("Только по-русски", UpdateDecisions.PickNotes(onlyRussian, "zh"));
        Assert.Equal("只有中文", UpdateDecisions.PickNotes(onlyChinese, "en"));
        Assert.Equal("只有中文", UpdateDecisions.PickNotes(onlyChinese, "ru"));

        // Свой блок есть — он и берётся, а не первый по списку.
        Assert.Equal("Только по-русски", UpdateDecisions.PickNotes(onlyRussian, "ru"));
    }

    /// <summary>
    /// ПЕРВЫЙ ЗАГОЛОВОК МОЖЕТ БЫТЬ ПО-РУССКИ, А ЗАПРОШЕН АНГЛИЙСКИЙ: блок выбирается по языку,
    /// а не по порядку в теле. Иначе перевод, поставленный первым, решал бы за человека.
    /// </summary>
    [Fact]
    public void Блок_выбирается_по_языку_а_не_по_порядку()
    {
        const string body = "## RU\n\nПервым русский\n\n## EN\n\nEnglish second";

        Assert.Equal("English second", UpdateDecisions.PickNotes(body, "en"));
        Assert.Equal("Первым русский", UpdateDecisions.PickNotes(body, "ru"));
    }

    /// <summary>
    /// ПУСТОЙ БЛОК НЕ СЧИТАЕТСЯ БЛОКОМ и при выборе запасного: пустое место человеку хуже,
    /// чем заметки не на его языке.
    /// </summary>
    [Fact]
    public void Пустой_блок_пропускается_при_выборе_запасного()
    {
        const string body = "## EN\n\n## RU\n\nРусские заметки";

        Assert.Equal("Русские заметки", UpdateDecisions.PickNotes(body, "en"));
        Assert.Equal("Русские заметки", UpdateDecisions.PickNotes(body, "zh"));
    }

    /// <summary>
    /// ПУСТОЕ ТЕЛО — ПУСТЫЕ ЗАМЕТКИ, а не исключение: выпуск без заметок бывает, и панель
    /// обязана это пережить.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Пустое_тело_даёт_пустые_заметки(string? body)
    {
        Assert.Equal(string.Empty, UpdateDecisions.PickNotes(body, "ru"));
    }

    /// <summary>
    /// ЯЗЫК ПАНЕЛИ ПЕРЕВОДИТСЯ В ИМЯ БЛОКА, а незнакомый не выдумывается: «de» — это «своего блока
    /// нет», и панель честно берёт английский, а не ищет блок «de».
    /// </summary>
    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("EN", "en")]
    [InlineData(" zh ", "zh")]
    [InlineData("de", "")]
    [InlineData(null, "")]
    public void Язык_панели_переводится_в_имя_блока(string? language, string expected)
    {
        Assert.Equal(expected, UpdateDecisions.LanguageKey(language));
    }

    // ------------------------------------------------- заметки с МАШИННЫМИ МЕТКАМИ (выпуск 1.21.0)

    /// <summary>
    /// Тело выпуска с машинными метками — в той же раскладке, в какой его собирает выпускающий
    /// скрипт и в какой пришёл настоящий выпуск `v1.21.0`: английский блок первым, человеческие
    /// заголовки рядом с метками, русский и китайский ниже.
    /// </summary>
    private const string Marked =
        "# DSH Panel 1.21.0\n" +
        "\n" +
        "## English\n" +
        "\n" +
        "<!-- dsh-notes:en -->\n" +
        "### Added\n" +
        "\n" +
        "- Release notes in the language of the interface.\n" +
        "<!-- /dsh-notes:en -->\n" +
        "\n" +
        "## Русский\n" +
        "\n" +
        "<!-- dsh-notes:ru -->\n" +
        "### Добавлено\n" +
        "\n" +
        "- **Заметки к выпуску на языке интерфейса.** Панель выбирает блок по языку панели.\n" +
        "<!-- /dsh-notes:ru -->\n" +
        "\n" +
        "## 中文\n" +
        "\n" +
        "<!-- dsh-notes:zh -->\n" +
        "### 新增\n" +
        "\n" +
        "- 更新说明使用界面语言显示。\n" +
        "<!-- /dsh-notes:zh -->";

    /// <summary>
    /// ПАНЕЛЬ ПОНИМАЕТ МЕТКИ <c>&lt;!-- dsh-notes:xx --&gt;</c> — и это не запас на будущее,
    /// а найденный дефект (замечание владельца 28.09.2026).
    ///
    /// Тело выпуска 1.21.0 несёт русский текст, но размечено МЕТКАМИ, а не заголовками `## RU`.
    /// Панель 2.0 знала только заголовки, блоков не находила и отдавала тело ЦЕЛИКОМ — а первым
    /// в теле идёт английский. То есть на русской панели человек читал английские заметки.
    /// Отсюда правило: **блоков не нашлось — тело целиком** остаётся верным только для выпусков
    /// БЕЗ ЯЗЫКОВЫХ БЛОКОВ, а выпуск с метками блоками обладает.
    /// </summary>
    [Theory]
    [InlineData("ru", "### Добавлено")]
    [InlineData("en", "### Added")]
    [InlineData("zh", "### 新增")]
    [InlineData("de", "### Added")]
    public void Заметки_с_машинными_метками_берутся_на_языке_панели(string language, string first)
    {
        var notes = UpdateDecisions.PickNotes(Marked, language);

        Assert.StartsWith(first, notes, StringComparison.Ordinal);

        // Человеческий заголовок стоит РЯДОМ С меткой и внутрь блока не попадает: он граница
        // страницы выпуска, а не часть перевода.
        Assert.DoesNotContain("## Русский", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("## English", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("dsh-notes", notes, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАГОЛОВОК ВНУТРИ БЛОКА — СОДЕРЖИМОЕ, А НЕ ГРАНИЦА: тело с метками несёт рядом
    /// человеческие заголовки, и признать их началом нового блока значило бы разорвать перевод.
    /// Проверка называет язык, чей заголовок стоит ВНУТРИ: «## Русский» не должен превратить
    /// английский блок в русский и наоборот.
    /// </summary>
    [Fact]
    public void Заголовок_внутри_блока_с_метками_не_режет_блок()
    {
        var russian = UpdateDecisions.PickNotes(Marked, "ru");
        var english = UpdateDecisions.PickNotes(Marked, "en");

        Assert.Contains("Заметки к выпуску на языке интерфейса", russian, StringComparison.Ordinal);
        Assert.DoesNotContain("Release notes", russian, StringComparison.Ordinal);

        Assert.Contains("Release notes", english, StringComparison.Ordinal);
        Assert.DoesNotContain("Заметки к выпуску", english, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАКРЫВАЮЩАЯ МЕТКА БЕЗ ОТКРЫВАЮЩЕЙ — обычный текст: комментарий, оставшийся в заметках,
    /// не должен обрывать настоящий перевод.
    /// </summary>
    [Fact]
    public void Закрывающая_метка_без_открывающей_границей_не_считается()
    {
        const string body =
            "<!-- /dsh-notes:ru -->\nтекст до\n<!-- dsh-notes:ru -->\nНАСТОЯЩИЙ РУССКИЙ\n<!-- /dsh-notes:ru -->";

        var notes = UpdateDecisions.PickNotes(body, "ru");

        Assert.Contains("НАСТОЯЩИЙ РУССКИЙ", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("текст до", notes, StringComparison.Ordinal);
    }

    /// <summary>
    /// НЕЗАКРЫТАЯ МЕТКА — ЭТО НЕ БЛОК: оборванный перевод не показывается, а уже закрытый
    /// остаётся. Иначе незакрытая метка отдала бы человеку половину чужого языка.
    /// </summary>
    [Fact]
    public void Незакрытая_метка_в_блок_не_попадает()
    {
        const string body =
            "<!-- dsh-notes:en -->\nНАЧАЛО английского\n<!-- /dsh-notes:en -->\n\n" +
            "<!-- dsh-notes:ru -->\nОБОРВАНО без закрытия";

        Assert.Equal("НАЧАЛО английского", UpdateDecisions.PickNotes(body, "en"));

        // Русского блока в теле нет: незакрытая метка его не создала.
        Assert.Equal("НАЧАЛО английского", UpdateDecisions.PickNotes(body, "ru"));
    }

    /// <summary>
    /// МЕТКА ВНУТРИ ОГРАЖДЁННОГО БЛОКА — ПРИМЕР, А НЕ ГРАНИЦА. Тело приходит с GitHub и его правят
    /// руками: история, которая рассказывает про формат заметок и показывает метку в ограждении,
    /// иначе объявила бы пример настоящим блоком. Проверяется ДВУМЯ языками сразу — разница видна
    /// только на том языке, который назван в примере.
    /// </summary>
    [Fact]
    public void Метка_в_ограждении_не_считается_блоком()
    {
        const string body =
            "<!-- dsh-notes:en -->\nНАСТОЯЩИЙ АНГЛИЙСКИЙ\n<!-- /dsh-notes:en -->\n\n" +
            "```\n<!-- dsh-notes:ru -->\nПРИМЕР из блока кода\n<!-- /dsh-notes:ru -->\n```";

        Assert.Equal("НАСТОЯЩИЙ АНГЛИЙСКИЙ", UpdateDecisions.PickNotes(body, "en"));

        // Примера как блока нет — берётся единственный настоящий блок, а не текст примера.
        Assert.Equal("НАСТОЯЩИЙ АНГЛИЙСКИЙ", UpdateDecisions.PickNotes(body, "ru"));
    }

    /// <summary>
    /// ОБА ВИДА ГРАНИЦ РАБОТАЮТ РЯДОМ. Выпуск, собранный скриптом (метки), и выпуск, размеченный
    /// руками (заголовки), — ОДИН И ТОТ ЖЕ разбор: иначе панель пришлось бы учить дважды.
    /// </summary>
    [Fact]
    public void Заголовки_и_метки_разбираются_одним_правилом()
    {
        Assert.Equal("Русские заметки", UpdateDecisions.PickNotes(Notes, "ru"));
        Assert.StartsWith("### Добавлено", UpdateDecisions.PickNotes(Marked, "ru"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ЧЕЛОВЕЧЕСКИЙ ЗАГОЛОВОК НАД МЕТКОЙ ТОГО ЖЕ ЯЗЫКА — САМЫЙ КОВАРНЫЙ СЛУЧАЙ, и он был
    /// настоящим дефектом (найден этой работой 28.09.2026).
    ///
    /// Заголовок «## English» открывает блок с ключом «en», а следом метка того же языка второй
    /// блок с тем же ключом уже не заводит (правило «первый блок остаётся») — и текст метки уезжал
    /// в пустоту: словарь выходил <c>[en] = пусто, [ru] = текст</c>, панель на английском отдавала
    /// РУССКИЙ блок (пустой английский для неё неотличим от отсутствующего).
    ///
    /// Поэтому метки сильнее заголовков: есть в теле хоть одна метка — границами считаются только
    /// они. Проверка обязана называть ОБА языка: на одном «en» она зеленела бы и на сломанном коде,
    /// если английский блок случайно оказывался первым непустым.
    /// </summary>
    [Fact]
    public void Заголовок_над_меткой_того_же_языка_не_съедает_блок()
    {
        const string body =
            "## English\n" +
            "\n" +
            "<!-- dsh-notes:en -->\n" +
            "НАСТОЯЩИЙ АНГЛИЙСКИЙ\n" +
            "<!-- /dsh-notes:en -->\n" +
            "\n" +
            "## Русский\n" +
            "\n" +
            "<!-- dsh-notes:ru -->\n" +
            "НАСТОЯЩИЙ РУССКИЙ\n" +
            "<!-- /dsh-notes:ru -->";

        Assert.Equal("НАСТОЯЩИЙ АНГЛИЙСКИЙ", UpdateDecisions.PickNotes(body, "en"));
        Assert.Equal("НАСТОЯЩИЙ РУССКИЙ", UpdateDecisions.PickNotes(body, "ru"));

        // Человеческие заголовки в заметки не попадают: они для страницы выпуска.
        Assert.DoesNotContain("## English", UpdateDecisions.PickNotes(body, "en"), StringComparison.Ordinal);
        Assert.DoesNotContain("## Русский", UpdateDecisions.PickNotes(body, "ru"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАГОЛОВОК ПОД МЕТКОЙ — ЧУЖОГО ЯЗЫКА ОН ТОЖЕ НЕ ОТКРЫВАЕТ. Тело с метками несёт
    /// «## Русский» между блоками: прими его разбор за границу — и русский блок получил бы
    /// английское содержимое.
    /// </summary>
    [Fact]
    public void Заголовок_между_блоками_с_метками_ничего_не_открывает()
    {
        const string body =
            "<!-- dsh-notes:en -->\nENGLISH TEXT\n<!-- /dsh-notes:en -->\n" +
            "\n" +
            "## Русский\n" +
            "\n" +
            "<!-- dsh-notes:ru -->\nРУССКИЙ ТЕКСТ\n<!-- /dsh-notes:ru -->";

        Assert.Equal("ENGLISH TEXT", UpdateDecisions.PickNotes(body, "en"));
        Assert.Equal("РУССКИЙ ТЕКСТ", UpdateDecisions.PickNotes(body, "ru"));
    }

    // ------------------------------------------------------------------ ответ GitHub

    /// <summary>Ответ GitHub в том виде, в каком его отдаёт <c>releases/latest</c>.</summary>
    private static JsonElement Response(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// НОРМАЛЬНЫЙ ОТВЕТ РАЗБИРАЕТСЯ ЦЕЛИКОМ: тег, страница, дата, заметки на языке панели
    /// и сырое тело рядом. Сырое тело нужно не «на всякий случай»: язык панели меняется,
    /// и заметки обязаны пересобраться на новом языке, не дожидаясь следующей проверки.
    /// </summary>
    [Fact]
    public void Нормальный_ответ_разбирается()
    {
        const string json =
            "{\"tag_name\":\"v2.1.0\",\"html_url\":\"https://github.com/example/releases/tag/v2.1.0\"," +
            "\"published_at\":\"2026-09-27T10:00:00Z\",\"body\":\"## RU\\n\\nНовое\\n\\n## EN\\n\\nNew\"}";

        var result = HttpUpdateClient.Parse(Response(json), "ru", Now);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("v2.1.0", result.Latest);
        Assert.Equal("https://github.com/example/releases/tag/v2.1.0", result.PageUrl);
        Assert.Equal("2026-09-27T10:00:00Z", result.Published);
        Assert.Equal("Новое", result.Notes);
        Assert.Contains("## EN", result.NotesRaw, StringComparison.Ordinal);
        Assert.Equal(Now, result.CheckedAt);
    }

    /// <summary>
    /// ОТВЕТ БЕЗ ТЕГА — НАЗВАННЫЙ ОТКАЗ, а не «выпуск версии ""»: без тега сравнивать нечего,
    /// и это честная причина, а не пустое место.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"\"}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"body\":\"заметки без версии\"}")]
    public void Ответ_без_тега_даёт_названный_отказ(string json)
    {
        var result = HttpUpdateClient.Parse(Response(json), "ru", Now);

        Assert.False(result.Ok);
        Assert.Equal(PanelStrings.UpdateNoTag, result.Error);
    }

    /// <summary>
    /// ПРОЧИЕ ПОЛЯ НЕОБЯЗАТЕЛЬНЫ: выпуск без тела и без страницы — это выпуск с пустыми заметками,
    /// а не поломка.
    /// </summary>
    [Fact]
    public void Ответ_без_заметок_и_страницы_не_ломает_разбор()
    {
        var result = HttpUpdateClient.Parse(Response("{\"tag_name\":\"v2.1.0\"}"), "ru", Now);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("v2.1.0", result.Latest);
        Assert.Equal(string.Empty, result.Notes);
        Assert.Equal(string.Empty, result.PageUrl);
        Assert.Equal(string.Empty, result.Published);
    }

    // ------------------------------------------------------------------ клиент и сеть

    /// <summary>Подстановка вместо сети: считает вызовы и отдаёт заранее записанный ответ.</summary>
    private sealed class FakeClient : IUpdateClient
    {
        private readonly Func<UpdateRelease> _answer;

        public FakeClient(Func<UpdateRelease> answer) => _answer = answer;

        public int Calls { get; private set; }

        public List<string> Languages { get; } = new();

        public UpdateRelease Latest(string language, DateTimeOffset now)
        {
            Calls++;
            Languages.Add(language);

            return _answer();
        }
    }

    /// <summary>Подставной обработчик HTTP: отдаёт заданный код и тело, считая запросы.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;

        public FakeHttp(HttpStatusCode code, string body)
        {
            _code = code;
            _body = body;
        }

        public int Calls { get; private set; }

        public List<string> UserAgents { get; } = new();

        public List<string> Accepts { get; } = new();

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (request.Headers.TryGetValues("User-Agent", out var agents)) UserAgents.AddRange(agents);
            if (request.Headers.TryGetValues("Accept", out var accepts)) Accepts.AddRange(accepts);

            return new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>Подставной обработчик, который обрывает связь: так выглядит ошибка сети.</summary>
    private sealed class BrokenHttp : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("сеть недоступна");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("сеть недоступна");
    }

    private static HttpUpdateClient Client(FakeHttp http) =>
        new(new HttpClient(http) { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>
    /// БИТЫЙ JSON — ЭТО ОТКАЗ, А НЕ ИСКЛЮЧЕНИЕ НАРУЖУ. Ответ приходит и от посредника в сети,
    /// и от самого GitHub; панель, у которой проверка обновлений валит приложение, — это панель,
    /// которую человек больше не запустит.
    /// </summary>
    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("<html>выпуск переехал</html>")]
    public void Битый_json_даёт_отказ_без_исключения(string body)
    {
        var http = new FakeHttp(HttpStatusCode.OK, body);

        var result = Client(http).Latest("ru", Now);

        Assert.False(result.Ok);
        Assert.Equal(PanelStrings.UpdateBadJson, result.Error);
        Assert.Equal(1, http.Calls);
    }

    /// <summary>
    /// ОТСУТСТВИЕ ВЫПУСКА (404) — «выпусков пока нет», а не «HTTP 404»: репозиторий молод,
    /// и первый выпуск появится позже. Человеку нужна причина, а не код.
    /// </summary>
    [Fact]
    public void Отсутствие_выпуска_названо_словами()
    {
        var result = Client(new FakeHttp(HttpStatusCode.NotFound, "{}")).Latest("ru", Now);

        Assert.False(result.Ok);
        Assert.Equal(PanelStrings.UpdateNoRelease, result.Error);
    }

    /// <summary>
    /// ЛИМИТ GITHUB (403 и 429) — ЭТО НЕ ПОЛОМКА: без токена GitHub даёт 60 запросов в час
    /// с одного адреса, и человеку нужно сказать «повторите позже», а не «HTTP 403».
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public void Лимит_GitHub_назван_словами(HttpStatusCode code)
    {
        var result = Client(new FakeHttp(code, "{}")).Latest("ru", Now);

        Assert.False(result.Ok);
        Assert.Equal(PanelStrings.UpdateRateLimited, result.Error);
    }

    /// <summary>Прочий отказ назван кодом ответа — другого объяснения у панели для него нет.</summary>
    [Fact]
    public void Прочий_отказ_назван_кодом()
    {
        var result = Client(new FakeHttp(HttpStatusCode.InternalServerError, "{}")).Latest("ru", Now);

        Assert.False(result.Ok);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАПРОС УХОДИТ С ОБЯЗАТЕЛЬНЫМИ ЗАГОЛОВКАМИ И НА НАЗВАННЫЙ АДРЕС. Без <c>User-Agent</c>
    /// GitHub отвечает 403 на любой запрос, а без <c>Accept: application/vnd.github+json</c>
    /// ответ приходит в разметке — и разбор JSON падает. Адрес берётся у продукта,
    /// а не собирается в клиенте: второй такой адрес однажды разошёлся бы с первым.
    /// </summary>
    [Fact]
    public void Запрос_уходит_с_обязательными_заголовками()
    {
        var http = new FakeHttp(HttpStatusCode.OK, "{\"tag_name\":\"v2.1.0\"}");

        var result = Client(http).Latest("ru", Now);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, http.Calls);
        Assert.Contains(ProductLinks.UserAgent, http.UserAgents[0], StringComparison.Ordinal);
        Assert.Contains("application/vnd.github+json", http.Accepts[0], StringComparison.Ordinal);
        Assert.Contains(ProductLinks.ReleasesRepository, ProductLinks.ReleasesApiUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОШИБКА СЕТИ НЕ ЛЕТИТ НАРУЖУ: обрыв связи, отказ DNS, недоступный адрес — всё это названный
    /// отказ с причиной. Проверяется подстановкой, которая обрывает связь ЛОКАЛЬНО, — живой запрос
    /// в проверках был бы сетью от имени владельца, то есть его правом, а не нашим.
    /// </summary>
    [Fact]
    public void Ошибка_сети_не_летит_наружу()
    {
        using var http = new HttpClient(new BrokenHttp()) { Timeout = TimeSpan.FromSeconds(1) };

        var result = new HttpUpdateClient(http).Latest("ru", Now);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(string.Empty, result.Latest);
    }

    // ------------------------------------------------------------------ контроллер

    private static UpdateRelease Release(
        string latest = "v2.1.0", string language = "ru", DateTimeOffset? at = null) =>
        new(
            true,
            string.Empty,
            latest,
            ProductLinks.ReleasesPageUrl,
            "2026-09-27T10:00:00Z",
            UpdateDecisions.PickNotes(Notes, language),
            Notes,
            at ?? Now);

    private static UpdateController Controller(
        IUpdateClient client,
        PanelSettings settings,
        Action<UpdateTraces>? remember = null,
        Action<string>? checkedAt = null,
        Action<string>? log = null,
        bool allowed = true,
        bool isolated = false,
        string current = "2.0.0",
        string language = "ru") =>
        new(
            client,
            () => settings,
            () => language,
            () => current,
            allowed,
            () => Now,
            action => action(),
            log ?? (_ => { }),
            remember ?? (_ => { }),
            checkedAt ?? (_ => { }),
            isolated);

    /// <summary>
    /// АВТОМАТИЧЕСКАЯ ПРОВЕРКА ИДЁТ ПО РАСПИСАНИЮ И ОТМЕЧАЕТСЯ: отметки нет — проверка есть,
    /// час назад — проверки нет. Второй такт в ту же минуту в сеть не идёт.
    ///
    /// ⚠️ Отметку времени пишет И АВТОМАТИЧЕСКАЯ проверка, а не только щелчок человека: именно
    /// ею закрыто правило «не чаще раза в сутки». Мутация «отметку пишет только человек»
    /// сначала не уронила НИЧЕГО — этот случай и есть та дыра, которую она нашла.
    /// </summary>
    [Fact]
    public void Часы_проверяют_только_когда_пора()
    {
        var fresh = new PanelSettings { UpdateCheckedAt = Pricing.PricingDecisions.Stamp(Now.AddHours(-1)) };
        var first = new FakeClient(() => Release());
        var idle = Controller(first, fresh);

        idle.Tick(Now);

        Assert.Equal(0, first.Calls);

        var stale = new PanelSettings { UpdateCheckedAt = Pricing.PricingDecisions.Stamp(Now.AddHours(-25)) };
        var second = new FakeClient(() => Release());
        var stamps = new List<string>();
        var busy = Controller(second, stale, checkedAt: stamps.Add);

        busy.Tick(Now);

        Assert.True(SpinWait.SpinUntil(() => second.Calls == 1, 5000), "выпуски не запрошены");
        Assert.True(SpinWait.SpinUntil(() => stamps.Count == 1, 5000), "автоматическая проверка не отметилась");

        busy.Tick(Now);

        Assert.Equal(1, second.Calls);
        Assert.Single(stamps);
    }

    /// <summary>
    /// ПРОВЕРКА ЗАПИСЫВАЕТ СЛЕДЫ ЧЕРЕЗ ВЛАДЕЛЬЦА НАСТРОЕК, а не сама: отметка времени — всегда,
    /// а версия, дата, адрес, заметки и сырое тело — вместе, одной записью.
    /// </summary>
    [Fact]
    public void Проверка_пишет_следы_одной_записью()
    {
        var settings = new PanelSettings();
        var stamps = new List<string>();
        var traces = new List<UpdateTraces>();

        var controller = Controller(
            new FakeClient(() => Release()),
            settings,
            remember: traces.Add,
            checkedAt: stamps.Add);

        controller.Check();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "проверка не завершилась");
        Assert.True(controller.Result.Ok, controller.Result.Error);
        Assert.True(controller.Newer);

        var single = Assert.Single(traces);
        Assert.Equal("v2.1.0", single.Latest);
        Assert.Equal("Русские заметки", single.Notes);
        Assert.Contains("## ZH", single.NotesRaw, StringComparison.Ordinal);
        Assert.Equal(ProductLinks.ReleasesPageUrl, single.PageUrl);
        Assert.NotEmpty(single.Published);

        Assert.Single(stamps);
        Assert.True(Pricing.PricingDecisions.TryParse(stamps[0], out var at));
        Assert.Equal(Now, at);
    }

    /// <summary>
    /// ОТКАЗ СЕТИ НЕ ПИШЕТ НИ СЛЕДОВ, НИ ОТМЕТКИ: записывать «проверено» про неудавшуюся проверку
    /// значило бы закрыть суточный гейт на сутки вперёд — то есть соврать. Зато причина называется.
    /// </summary>
    [Fact]
    public void Отказ_сети_не_пишет_следов()
    {
        var settings = new PanelSettings();
        var stamps = new List<string>();
        var traces = new List<UpdateTraces>();

        var controller = Controller(
            new FakeClient(() => UpdateRelease.Failed(PanelStrings.UpdateRateLimited, Now)),
            settings,
            remember: traces.Add,
            checkedAt: stamps.Add);

        controller.Check();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "проверка не завершилась");
        Assert.False(controller.Result.Ok);
        Assert.Empty(traces);
        Assert.Empty(stamps);
        Assert.Contains(PanelStrings.UpdateRateLimited, controller.SourceText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ШАРИК О НОВОЙ ВЕРСИИ — СОБЫТИЕ КОНТРОЛЛЕРА, и он молчит о версии, о которой уже говорили,
    /// и о пропущенной. Щелчок человека («Проверить сейчас») шарика не даёт: это его собственный
    /// запрос, и рассказывать ему о том, что он только что увидел в окне, незачем.
    /// </summary>
    [Fact]
    public void Шарик_даётся_один_раз_и_не_даётся_для_пропущенной()
    {
        var settings = new PanelSettings();
        var told = new List<string>();

        var controller = Controller(new FakeClient(() => Release()), settings);
        controller.Announce += told.Add;

        // Щелчок человека: он сам попросил — шарика нет.
        controller.Check();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "проверка не завершилась");
        Assert.Empty(told);

        // Автоматическая проверка: версия новая (в настройках её ещё нет) — говорим.
        settings.UpdateLatest = "v2.0.5";
        var auto = Controller(new FakeClient(() => Release()), settings);
        auto.Announce += told.Add;
        auto.Check();

        Assert.True(SpinWait.SpinUntil(() => !auto.Busy, 5000), "проверка не завершилась");

        // О версии уже говорили (она записана) — второй раз молчим.
        settings.UpdateLatest = "v2.1.0";
        var quiet = Controller(new FakeClient(() => Release()), settings);
        quiet.Announce += told.Add;
        quiet.Check();

        Assert.True(SpinWait.SpinUntil(() => !quiet.Busy, 5000), "проверка не завершилась");

        // Пропущенная версия — тоже молчание, и это ответ человека, а не забывчивость панели.
        settings.UpdateLatest = string.Empty;
        settings.UpdateSkippedVersion = "2.1.0";
        var skipped = Controller(new FakeClient(() => Release()), settings);
        skipped.Announce += told.Add;
        skipped.Check();

        Assert.True(SpinWait.SpinUntil(() => !skipped.Busy, 5000), "проверка не завершилась");
        Assert.True(skipped.Skipped);
        Assert.Empty(told);
        Assert.Contains("2.1.0", skipped.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ ГЛАВНОЕ УТВЕРЖДЕНИЕ О ПРОГОНЕ ПРОВЕРКИ: **без права он в сеть не ходит вовсе**
    /// и ничего не записывает. Проверка ЖДЁТ, а не считает сразу: запрос уходит в фон, и «сейчас
    /// ноль вызовов» ничего не доказывало бы — вызов мог случиться через миллисекунду.
    /// </summary>
    [Fact]
    public void Без_права_сети_нет_и_ничего_не_пишется()
    {
        var settings = new PanelSettings();
        var stamps = new List<string>();
        var traces = new List<UpdateTraces>();
        var log = new List<string>();
        var client = new FakeClient(() => Release());

        var controller = Controller(
            client,
            settings,
            remember: traces.Add,
            checkedAt: stamps.Add,
            log: log.Add,
            allowed: false);

        controller.Start();
        controller.Tick(Now);
        controller.Check();

        Assert.Contains(PanelStrings.PanelLogUpdateLocked, log);
        Assert.Equal(PanelStrings.UpdateLocked, controller.SourceText);
        Assert.False(controller.Busy);
        Assert.Empty(traces);
        Assert.Empty(stamps);

        // Секунда на то, чтобы запрос ПОЯВИЛСЯ: не появился — значит панель в сеть не пошла.
        Assert.False(
            SpinWait.SpinUntil(() => client.Calls > 0, 1000),
            "прогон проверки сходил в сеть за выпусками");

        Assert.Equal(0, client.Calls);
    }

    /// <summary>
    /// С ЧЕЛОВЕЧЕСКИМ ПРАВОМ ЧАСЫ ЗАПУСКАЮТСЯ и сразу делают первый такт: решение владельца —
    /// проверка ПРИ ЗАПУСКЕ панели. Второй раз за сутки она не сработает: такт спросит отметку.
    /// </summary>
    [Fact]
    public void С_правом_часы_запускаются_и_первая_проверка_идёт_сразу()
    {
        var log = new List<string>();
        var client = new FakeClient(() => Release());
        var controller = Controller(client, new PanelSettings(), log: log.Add);

        controller.Start();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "первая проверка не завершилась");
        Assert.Equal(1, client.Calls);

        // Строка журнала проверяется ПОСЛЕ ожидания: запись идёт из фоновой задачи, и чтение
        // списка параллельно с записью — это гонка в самой проверке, а не дефект панели.
        Assert.Contains(PanelStrings.PanelLogUpdateClock, log);

        controller.Dispose();
    }

    /// <summary>
    /// ЯЗЫК ПАНЕЛИ ДОХОДИТ ДО ЗАПРОСА — иначе заметки выбирались бы не на том языке.
    /// </summary>
    [Fact]
    public void Язык_панели_доходит_до_запроса()
    {
        var client = new FakeClient(() => Release(language: "zh"));
        var controller = Controller(client, new PanelSettings(), language: "zh");

        controller.Check();

        Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "проверка не завершилась");
        Assert.Equal(new[] { "zh" }, client.Languages);
        Assert.Equal("中文说明", controller.Notes);
    }

    /// <summary>
    /// СТРОКА СОСТОЯНИЯ НАЗЫВАЕТ ПРИЧИНУ, А НЕ МОЛЧИТ: три разных ответа — нет права,
    /// ещё не проверяли, не вышло.
    /// </summary>
    [Fact]
    public void Строка_состояния_называет_причину()
    {
        Assert.Equal(
            PanelStrings.UpdateLocked,
            Controller(new FakeClient(() => Release()), new PanelSettings(), allowed: false).SourceText);

        Assert.Equal(
            PanelStrings.UpdateNeverChecked,
            Controller(new FakeClient(() => Release()), new PanelSettings()).SourceText);

        var failed = Controller(
            new FakeClient(() => UpdateRelease.Failed("HTTP 503", Now)),
            new PanelSettings());

        failed.Check();

        Assert.True(SpinWait.SpinUntil(() => !failed.Busy, 5000), "проверка не завершилась");
        Assert.Contains("HTTP 503", failed.SourceText, StringComparison.Ordinal);
        Assert.Contains("{0}", PanelStrings.UpdateFailedFormat, StringComparison.Ordinal);
    }

    /// <summary>
    /// «ПОСЛЕДНЯЯ ВЕРСИЯ» — ЭТО ТОЖЕ СЛОВАМИ: когда выпуск не новее, панель не молчит пустотой,
    /// а говорит, что обновляться некуда.
    /// </summary>
    [Fact]
    public void Совпадающая_версия_называет_себя_последней()
    {
        var same = Controller(new FakeClient(() => Release("v2.0.0")), new PanelSettings());

        same.Check();

        Assert.True(SpinWait.SpinUntil(() => !same.Busy, 5000), "проверка не завершилась");
        Assert.False(same.Newer);
        Assert.Equal(PanelStrings.UpdateLatest, same.StatusText);
    }
}
