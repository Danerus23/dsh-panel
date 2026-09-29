using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DshPanel.Localization;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки языка интерфейса: механизм, словари и правила выбора.
///
/// Три вещи, ради которых эти проверки и написаны:
///
/// 1. **Ключ — это имя члена <c>PanelStrings</c>.** Отражение ловит обе беды сразу: член без
///    записи в словаре (человек увидит в окне имя ключа — ровно этот дефект был в v1
///    с <c>about.noLink</c>) и запись без члена (ключ остался от прежней строки).
/// 2. **Подстановки обязаны совпадать во всех языках.** <c>{0}</c> в русском и <c>{1}</c>
///    в переводе — это упавшая строка в окне у человека, а не косметика.
/// 3. **Язык проверок ЗАДАН ЯВНО** (<see cref="TestLocale"/>) и не зависит от языка машины.
///    Проверки других языков идут через <see cref="Loc.TIn"/> и глобального состояния не трогают:
///    xunit гоняет классы параллельно, и «проверил три языка» не должно означать «проверил
///    тот, который успел выставиться».
/// </summary>
public class LocalizationTests
{
    private static Dictionary<string, string> Ru => new(Loc.Dictionary("ru"), StringComparer.Ordinal);

    private static List<string> Members() =>
        typeof(PanelStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    // ---- правило выбора языка ------------------------------------------------------------

    [Theory]
    // Переменная окружения сильнее настройки — так снимаются кадры на трёх языках, не трогая профиль.
    [InlineData("ru", "zh", "ru", "zh")]
    // «auto» и пустое значение означают язык системы.
    [InlineData("auto", null, "ru", "ru")]
    [InlineData("auto", null, "zh", "zh")]
    [InlineData("", null, "en", "en")]
    [InlineData(null, null, "ru", "ru")]
    // Незнакомый язык системы — английский, а не пустое окно.
    [InlineData("auto", null, "de", "en")]
    [InlineData("auto", null, "", "en")]
    // Незнакомая ПЕРЕМЕННАЯ окружения — тоже английский: её ставит человек в своей оболочке,
    // и отказывать ему нечем. Это единственный вход, где незнакомое значение даёт язык,
    // а не отказ: ключ `--lang` отвергает RefuseSwitch, а настройку приводит к «auto» SettingsStore.
    [InlineData(null, "de", "ru", "en")]
    [InlineData("zh", "xx", "ru", "en")]
    // Незнакомое значение настройки — тоже английский (это защита механизма; в файле настроек
    // такое значение ещё раньше приводится к «auto», см. проверку ниже).
    [InlineData("de", null, "ru", "en")]
    [InlineData("RU", null, "ru", "ru")]
    [InlineData("  zh  ", null, "ru", "zh")]
    public void Выбор_языка_идёт_в_порядке_v1(string? preference, string? environment, string system, string expected)
    {
        Assert.Equal(expected, LanguageDecisions.Resolve(preference, environment, system));
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("EN", "en")]
    [InlineData(" zh ", "zh")]
    [InlineData("auto", "auto")]
    [InlineData("de", "auto")]
    [InlineData("русский", "auto")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData(null, "auto")]
    public void Мусор_в_файле_настроек_значит_как_в_системе(string? value, string expected)
    {
        Assert.Equal(expected, LanguageDecisions.Normalize(value));
    }

    [Fact]
    public void Значения_языка_и_настройка_согласованы()
    {
        // Список окна настроек и список, который понимает механизм, — один и тот же.
        Assert.Equal(LanguageDecisions.Auto, PanelSettings.NormalizeLanguage(PanelSettings.LanguageAt(0)));
        Assert.Equal(LanguageDecisions.Auto, PanelSettings.Default.Language);

        for (var index = 0; index < PanelSettings.LanguageValues.Length; index++)
        {
            var value = PanelSettings.LanguageAt(index);
            Assert.Equal(index, PanelSettings.LanguageIndex(value));
            Assert.Equal(value, LanguageDecisions.Normalize(value));
        }

        Assert.Equal(LanguageDecisions.Auto, PanelSettings.NormalizeLanguage("мусор"));
        Assert.Equal(LanguageDecisions.Auto, PanelSettings.LanguageAt(-1));
        Assert.Equal(LanguageDecisions.Auto, PanelSettings.LanguageAt(99));
    }

    [Fact]
    public void Ключ_языка_снимается_до_разбора_режимов()
    {
        Assert.Equal("zh", LanguageDecisions.SwitchValue(new[] { "--shot", "кадр.png", "--lang", "zh" }));
        Assert.Equal("ru", LanguageDecisions.SwitchValue(new[] { "--lang", "ru" }));

        // Ключ без языка: значение не подставляется из соседнего ключа, иначе «--lang --shot кадр»
        // съело бы съёмку и панель открыла бы окно владельцу.
        Assert.Null(LanguageDecisions.SwitchValue(new[] { "--lang", "--shot", "кадр.png" }));
        Assert.Null(LanguageDecisions.SwitchValue(new[] { "--shot", "кадр.png" }));
        Assert.True(LanguageDecisions.HasSwitch(new[] { "--lang" }));
        Assert.False(LanguageDecisions.HasSwitch(new[] { "--shot", "кадр.png" }));

        Assert.Equal(
            new[] { "--shot", "кадр.png" },
            LanguageDecisions.WithoutSwitch(new[] { "--shot", "кадр.png", "--lang", "zh" }));
        Assert.Equal(
            new[] { "--isolation-selftest" },
            LanguageDecisions.WithoutSwitch(new[] { "--lang", "--isolation-selftest" }));
        Assert.Equal(new string[0], LanguageDecisions.WithoutSwitch(new[] { "--lang", "zh" }));

        // Ключ проверки остаётся известным, иначе разбор назвал бы его опечаткой.
        Assert.Contains(LanguageDecisions.Switch, StartModes.Known);
        Assert.Contains("--lang-selftest", StartModes.Known);
    }

    // ---- отказ на незнакомый язык ключа --------------------------------------------------

    /// <summary>
    /// Принятые значения ключа <c>--lang</c>: пустое и «auto» — «как в системе», известные языки —
    /// тем более. Регистр и лишние пробелы значения не меняют: «--lang EN» и «--lang ru» — не опечатка.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("ru")]
    [InlineData("EN")]
    [InlineData(" zh ")]
    public void Отказ_ключа_молчит_на_принятых_значениях(string? value)
    {
        Assert.Null(LanguageDecisions.RefuseSwitch(value));
    }

    /// <summary>
    /// Незнакомое значение ключа — ОТКАЗ, и в нём названы все три языка: человеку видно, что писать
    /// вместо опечатки. Отказ, который просто «нельзя», заставляет искать список языков в документах.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("xx")]
    [InlineData("q")]
    public void Отказ_ключа_называет_все_языки(string value)
    {
        var refusal = LanguageDecisions.RefuseSwitch(value);

        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.Contains(LanguageDecisions.Switch, refusal!, StringComparison.Ordinal);
        Assert.Contains(value, refusal!, StringComparison.Ordinal);

        foreach (var language in LanguageDecisions.Known)
        {
            Assert.Contains(language, refusal!, StringComparison.Ordinal);
        }
    }

    // ---- подстановки ---------------------------------------------------------------------

    [Theory]
    [InlineData("Порт {0}, PID {1}", new[] { 0, 1 })]
    [InlineData("{0}", new[] { 0 })]
    [InlineData("нет подстановок", new int[0])]
    [InlineData("до {0:yyyy} после", new[] { 0 })]
    [InlineData("{{0}} — экранированная скобка", new int[0])]
    [InlineData("{{0}} и {1}", new[] { 1 })]
    [InlineData("", new int[0])]
    public void Номера_подстановок_находятся(string text, int[] expected)
    {
        Assert.Equal(expected, LanguageDecisions.Placeholders(text).ToArray());
    }

    [Fact]
    public void Наборы_подстановок_сравниваются()
    {
        Assert.True(LanguageDecisions.SamePlaceholders("{0} и {1}", "first {1}, second {0}"));
        Assert.True(LanguageDecisions.SamePlaceholders("нет", "none at all"));
        Assert.False(LanguageDecisions.SamePlaceholders("{0}", "{1}"));
        Assert.False(LanguageDecisions.SamePlaceholders("{0} {1}", "{0}"));
        Assert.False(LanguageDecisions.SamePlaceholders("текст", "{0}"));
    }

    // ---- поведение пропажи ---------------------------------------------------------------

    private static readonly Dictionary<string, string> RussianSample = new(StringComparer.Ordinal)
    {
        ["есть.русский"] = "по-русски",
        ["пустой"] = "",
    };

    private static readonly Dictionary<string, string> EnglishSample = new(StringComparer.Ordinal)
    {
        ["есть.русский"] = "in english",
        ["пустой"] = "in english",
        ["только.английский"] = "english only",
    };

    [Fact]
    public void Ключа_нет_в_языке_берётся_английский()
    {
        var text = LanguageDecisions.Pick("только.английский", RussianSample, EnglishSample, out var missing);

        Assert.Equal("english only", text);
        Assert.True(missing);
    }

    [Fact]
    public void Пустое_значение_это_пропуск_а_не_перевод()
    {
        var text = LanguageDecisions.Pick("пустой", RussianSample, EnglishSample, out var missing);

        Assert.Equal("in english", text);
        Assert.True(missing);

        // И без английского пустое значение тоже не «перевод»: вернётся сам ключ.
        var alone = LanguageDecisions.Pick("пустой", RussianSample, null, out var missingToo);
        Assert.Equal("пустой", alone);
        Assert.True(missingToo);
    }

    [Fact]
    public void Ключа_нет_нигде_возвращается_сам_ключ()
    {
        var text = LanguageDecisions.Pick("нет.нигде", RussianSample, EnglishSample, out var missing);

        Assert.Equal("нет.нигде", text);
        Assert.True(missing);

        // А найденный ключ пропажей не считается: иначе список пропавших был бы всегда полон.
        LanguageDecisions.Pick("есть.русский", RussianSample, EnglishSample, out var found);
        Assert.False(found);
    }

    [Fact]
    public void Пропавший_ключ_виден_в_окне_и_попадает_в_список()
    {
        const string key = "нет.такого.ключа.в.словаре";

        // Именно так это и выглядит у человека: в окне стоит имя ключа, а не пустое место.
        Assert.Equal(key, Loc.T(key));
        Assert.Contains(key, Loc.MissingKeys);
    }

    // ---- TIn не трогает состояние --------------------------------------------------------

    [Fact]
    public void Явный_язык_не_меняет_выбранный()
    {
        var before = Loc.Language;
        var preference = Loc.Preference;

        // Русский текст — из русского словаря, что бы ни было выбрано сейчас.
        Assert.Equal(Ru["ServerRunning"], Loc.TIn("ru", nameof(PanelStrings.ServerRunning)));

        Assert.Equal(before, Loc.Language);
        Assert.Equal(preference, Loc.Preference);
    }

    [Fact]
    public void Язык_проверок_задан_явно()
    {
        // Модульный инициализатор тестов выставил русский: на английской машине проверки
        // сравнивают те же строки, что и на русской.
        Loc.Init("ru");

        Assert.Equal("ru", Loc.Language);
        Assert.Equal("ru", Loc.Preference);
        Assert.True(Loc.KeyCount > 0);
        Assert.Equal("Свой сервер панели: работает", Loc.T(nameof(PanelStrings.ServerRunning)));
    }

    // ---- словари -------------------------------------------------------------------------

    [Fact]
    public void Русский_словарь_непуст_и_без_пустых_значений()
    {
        var russian = Ru;

        Assert.True(russian.Count > 200, $"ключей в русском словаре: {russian.Count}");
        Assert.All(russian, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"пустое значение: {pair.Key}"));
    }

    [Fact]
    public void Каждый_член_PanelStrings_имеет_запись_а_лишних_записей_нет()
    {
        var members = Members();
        var russian = Ru;

        Assert.True(members.Count > 200, $"членов PanelStrings: {members.Count}");

        var withoutEntry = members.Where(name => !russian.ContainsKey(name)).ToList();
        var withoutMember = russian.Keys.Where(key => !members.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();

        Assert.Empty(withoutEntry);
        Assert.Empty(withoutMember);
    }

    [Fact]
    public void Строка_из_словаря_доходит_до_окна()
    {
        // Связка «ключ → словарь → Loc.T»: если её порвать, проверка выше осталась бы зелёной,
        // а окно показало бы имена ключей.
        foreach (var name in new[]
                 {
                     nameof(PanelStrings.ServerRunning),
                     nameof(PanelStrings.StartButton),
                     nameof(PanelStrings.SettingsTitle),
                     nameof(PanelStrings.BackupWindowTitle),
                     nameof(PanelStrings.TrayToolTip),
                 })
        {
            Assert.Equal(Ru[name], Loc.T(name));
        }
    }

    [Fact]
    public void Сверка_языков_видит_пропуски_и_лишние_ключи()
    {
        var gaps = Loc.CompareLanguages(out var total);

        Assert.Equal(Loc.Known.Length, gaps.Count);
        Assert.True(total >= Ru.Count);

        // Ни один ключ русского словаря не может быть назван пропуском: русский — источник.
        Assert.DoesNotContain(gaps["ru"], key => Ru.ContainsKey(key));

        var union = Loc.Known
            .SelectMany(language => Loc.Dictionary(language).Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(union.Length, total);

        // Пропуск в языке — ровно те ключи объединения, которых в нём нет или которые пусты.
        foreach (var language in Loc.Known)
        {
            var dictionary = Loc.Dictionary(language);

            var expected = union
                .Where(key => !dictionary.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                .ToArray();

            Assert.Equal(expected, gaps[language].OrderBy(key => key, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void Чужой_словарь_не_противоречит_русскому()
    {
        var russian = Ru;

        foreach (var language in Loc.Known)
        {
            if (language == "ru") continue;

            var other = Loc.Dictionary(language);

            // Словаря ещё нет вовсе — проверять нечего. Что он пуст, видно в `--lang-selftest`
            // отдельной строкой «строк 0»: молчания о пропаже здесь нет.
            if (other.Count == 0) continue;

            foreach (var pair in other)
            {
                // Ключ, которого нет в русском, — не перевод, а опечатка или строка, оставшаяся
                // от прежней версии: он молчит и мешает считать опись полной.
                Assert.True(russian.ContainsKey(pair.Key), $"{language}: лишний ключ {pair.Key}");

                Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"{language}: пустое значение {pair.Key}");

                // Разошедшиеся подстановки — это упавшая строка в окне у человека, а не косметика.
                Assert.True(
                    LanguageDecisions.SamePlaceholders(russian[pair.Key], pair.Value),
                    $"{language}: разошлись подстановки у {pair.Key}: «{russian[pair.Key]}» ≠ «{pair.Value}»");
            }
        }
    }

    /// <summary>
    /// Полнота словаря: каждый ключ русского словаря обязан быть и в двух других.
    ///
    /// ⚠️ Неполный словарь — это НЕ «сошлось», и проверка на нём ПАДАЕТ. Раньше она звала
    /// <c>Assert.Skip</c>, и это было слепое пятно: удали кто-нибудь перевод завтра — проверка
    /// снова «пропустилась» бы, а набор остался бы зелёным. Проверка, которая не может упасть,
    /// ничего не стоит.
    ///
    /// Пока английский и китайский словари ПИСАЛИСЬ (их писал отдельный рабочий), проверка была
    /// КРАСНОЙ — так и задумано: «позеленить» её нельзя ничем, кроме самих переводов.
    /// На 26.09.2026 три словаря сошлись ключ в ключ (по 476 ключей), и проверка зелёная — но
    /// зелёная она ровно потому, что расхождения НЕТ, а не потому, что её не спросили.
    /// Что именно недостаёт, видно в сообщении: число и первые имена; полная сверка — в
    /// <c>--lang-selftest</c>, он называет каждый недостающий ключ и отдаёт код 1.
    /// </summary>
    [Fact]
    public void Три_словаря_сходятся_ключ_в_ключ()
    {
        var russian = Ru;
        var behind = new List<string>();

        foreach (var language in Loc.Known)
        {
            if (language == "ru") continue;

            var other = Loc.Dictionary(language);
            var missing = russian.Keys
                .Where(key => !other.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length > 0) behind.Add($"{language}: нет {missing.Length} ({string.Join(", ", missing.Take(8))})");
        }

        if (behind.Count > 0)
        {
            Assert.Fail("словари разошлись ключ в ключ — неполный словарь это не «сошлось»: " + string.Join("; ", behind));
        }

        foreach (var language in Loc.Known)
        {
            if (language == "ru") continue;

            Assert.Equal(
                russian.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                Loc.Dictionary(language).Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void Заголовки_окон_берутся_из_строк()
    {
        // Кадры окон уезжают в README на трёх языках: заголовок, оставшийся в разметке
        // литералом, на английском кадре остался бы русским.
        Assert.Equal(Ru[nameof(PanelStrings.AppName)], PanelStrings.AppName);
        Assert.Equal(Ru[nameof(PanelStrings.SettingsWindowTitle)], PanelStrings.SettingsWindowTitle);
        Assert.Equal(Ru[nameof(PanelStrings.ConfirmStopTitle)], PanelStrings.ConfirmStopTitle);
        Assert.Equal(Ru[nameof(PanelStrings.BackupWindowTitle)], PanelStrings.BackupWindowTitle);
    }

    [Fact]
    public void Названия_языков_одинаковы_во_всех_словарях()
    {
        // «English» и «中文» пишутся на своём языке всегда: человек, у которого интерфейс
        // на незнакомом языке, обязан узнать свой язык в списке. Проверка сторожит
        // и механизм, и переводчиков: эти два ключа переводить нельзя.
        foreach (var name in new[] { "SettingsLanguageEnglish", "SettingsLanguageChinese", "SettingsLanguageRussian" })
        {
            var russian = Loc.TIn("ru", name);
            Assert.False(string.IsNullOrWhiteSpace(russian));

            foreach (var language in Loc.Known)
            {
                var text = Loc.TIn(language, name);
                if (text == name) continue;     // языка ещё нет — пропуск, а не расхождение
                Assert.Equal(russian, text);
            }
        }
    }
}

/// <summary>
/// Коллекция проверок, которые МЕНЯЮТ глобальный язык процесса (<see cref="Loc.Init"/>) или
/// переменную окружения языка.
///
/// Почему отдельная и почему в ней ЗАПРЕЩЁН параллельный прогон: язык — состояние на весь процесс,
/// и проверка, выставившая «en», сломала бы соседний класс, который в это же время читает строки
/// и ждёт русского. <c>DisableParallelization</c> значит «этот класс идёт один, а не рядом
/// с другими»: иначе покрытие композиции покупалось бы ценой чужих случайных падений — то есть
/// проверка, найденная один раз, ломала бы доверие к набору каждый второй прогон.
/// </summary>
[CollectionDefinition(LanguageStateCollection.Name, DisableParallelization = true)]
public sealed class LanguageStateCollection
{
    public const string Name = "language-state";
}

/// <summary>
/// Композиция «значение → <see cref="Loc.Init"/> → выбранный язык» — то, чего чистая функция
/// <see cref="LanguageDecisions.Resolve"/> не покрывает.
///
/// Ровно здесь жил дефект, найденный холодным проверяющим 26.09.2026: <c>Init</c> звал
/// <c>Normalize</c> ДО <c>Resolve</c>, поэтому незнакомое значение превращалось в «auto» раньше,
/// чем его видело решение, и обещание документации расходилось с делом. Чистая функция была
/// проверена, а её единственный настоящий вызов — нет.
///
/// За собой каждая проверка прибирает: и переменную окружения, и язык возвращает к «ru» —
/// тому, что выставлен для всего прогона (<see cref="TestLocale"/>).
/// </summary>
[Collection(LanguageStateCollection.Name)]
public class LanguageStateTests
{
    /// <summary>Прибрать за собой: и переменную, и язык — в то состояние, что было до проверки.</summary>
    private static void Restore(string? variable)
    {
        Environment.SetEnvironmentVariable(LanguageDecisions.Variable, variable);
        Loc.Init("ru");
    }

    /// <summary>
    /// Незнакомое значение, дошедшее до <see cref="Loc.Init"/> СЫРЫМ, — английский, а не язык
    /// системы. Здесь и ловится возврат «Normalize до Resolve»: тогда «de» стало бы «auto»,
    /// и панель молча заговорила бы на языке машины вместо обещанного английского.
    /// </summary>
    [Fact]
    public void Незнакомое_значение_доходит_до_решения_сырым()
    {
        var variable = Environment.GetEnvironmentVariable(LanguageDecisions.Variable);

        try
        {
            Environment.SetEnvironmentVariable(LanguageDecisions.Variable, null);

            Loc.Init("de");

            // «ru» здесь стоять не может: это язык ЭТОЙ машины, и именно он получился бы,
            // если бы «de» подменили на «auto» до вызова решения.
            Assert.Equal(LanguageDecisions.English, Loc.Language);
            Assert.Equal(LanguageDecisions.Auto, Loc.Preference);

            // Известное значение проходит как есть — и в язык, и в показ.
            Loc.Init("zh");
            Assert.Equal("zh", Loc.Language);
            Assert.Equal("zh", Loc.Preference);
        }
        finally
        {
            Restore(variable);
        }
    }

    /// <summary>
    /// Переменная окружения сильнее настройки, а незнакомая переменная — английский: это тот самый
    /// вход, где незнакомое значение даёт язык, а не отказ.
    /// </summary>
    [Fact]
    public void Незнакомая_переменная_окружения_даёт_английский()
    {
        var variable = Environment.GetEnvironmentVariable(LanguageDecisions.Variable);

        try
        {
            Environment.SetEnvironmentVariable(LanguageDecisions.Variable, "de");
            Loc.Init(null);

            Assert.Equal(LanguageDecisions.English, Loc.Language);
            Assert.Equal(LanguageDecisions.Auto, Loc.Preference);

            // Переменная сильнее настройки — тот же порядок, что у чистой функции.
            Environment.SetEnvironmentVariable(LanguageDecisions.Variable, "zh");
            Loc.Init("ru");

            Assert.Equal("zh", Loc.Language);
            Assert.Equal("ru", Loc.Preference);
        }
        finally
        {
            Restore(variable);
        }
    }

    /// <summary>
    /// Значение ключа <c>--lang</c> разбирается ДО <see cref="Loc.Init"/>: незнакомый язык —
    /// громкий отказ, а не «взяли другой язык». Проверка сторожит порядок двух шагов: убери отказ —
    /// и «--lang de» молча даст кадр не на том языке, а заметить это в отчёте нечем.
    /// </summary>
    [Fact]
    public void Незнакомый_язык_ключа_отвергается_до_выбора()
    {
        var variable = Environment.GetEnvironmentVariable(LanguageDecisions.Variable);
        var raw = LanguageDecisions.SwitchValue(new[] { "--shot", "кадр.png", "--lang", "de" });

        Assert.Equal("de", raw);

        try
        {
            Environment.SetEnvironmentVariable(LanguageDecisions.Variable, null);

            var refusal = LanguageDecisions.RefuseSwitch(raw);

            Assert.False(string.IsNullOrWhiteSpace(refusal));
            foreach (var language in LanguageDecisions.Known)
            {
                Assert.Contains(language, refusal!, StringComparison.Ordinal);
            }

            // А если бы отказа не было, Init дал бы английский (не язык системы): «de» — не «auto».
            Loc.Init(raw);
            Assert.Equal(LanguageDecisions.English, Loc.Language);
        }
        finally
        {
            Restore(variable);
        }
    }
}
