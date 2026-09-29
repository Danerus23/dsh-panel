using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DshPanel.Localization;

/// <summary>
/// Строки интерфейса. Словари лежат ВНУТРИ сборки (<c>Localization\ru.json</c>, <c>en.json</c>,
/// <c>zh.json</c>), поэтому панель остаётся одним exe без папки с переводами.
///
/// Язык берётся из настройки (<c>settings.json</c>, поле <c>language</c>): «auto» — по языку
/// системы, иначе ru / en / zh. Переменная <see cref="LanguageDecisions.Variable"/> или ключ
/// <c>--lang</c> перебивают настройку — так снимаются кадры окон на трёх языках, не трогая профиль.
///
/// Ключ — ИМЯ ЧЛЕНА <c>Shell\PanelStrings</c>, и другого способа назвать строку в панели нет:
/// список ключей берётся у самого типа отражением, поэтому «ключ, которого нет в PanelStrings»
/// и «строка, для которой забыли ключ» видны обе (это и проверяет <c>--lang-selftest</c>).
///
/// Ключа нет в выбранном языке — берём английский; нет и там — возвращаем сам ключ, чтобы пропажа
/// была видна прямо в окне, и запоминаем её: список отдаёт <see cref="MissingKeys"/>.
/// </summary>
public static class Loc
{
    /// <summary>Языки, на которых панель говорит. Тот же список, что у чистых решений.</summary>
    public static readonly string[] Known = LanguageDecisions.Known;

    private static readonly object Gate = new();
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.Ordinal);

    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);

    /// <summary>Выбранный язык: «ru», «en» или «zh». До <see cref="Init"/> — английский.</summary>
    public static string Language { get; private set; } = LanguageDecisions.English;

    /// <summary>
    /// Что просили: «auto», «ru», «en» или «zh». Отличается от <see cref="Language"/> ровно
    /// тем, что при «auto» язык выбран системой, — это и показывает окно настроек.
    /// </summary>
    public static string Preference { get; private set; } = LanguageDecisions.Auto;

    /// <summary>
    /// Ключи, которых не нашлось ни в выбранном языке, ни в английском. Снимок на момент чтения:
    /// список читают и проверка, и человек, а меняется он из другой нити.
    /// </summary>
    public static IReadOnlyCollection<string> MissingKeys
    {
        get
        {
            lock (Gate) return Missing.ToArray();
        }
    }

    /// <summary>Сколько строк в словаре выбранного языка.</summary>
    public static int KeyCount => _current.Count;

    /// <summary>
    /// Выбрать язык. Зовётся ОДИН раз за прогон и обязательно ДО построения интерфейса:
    /// окно, собранное раньше, осталось бы на прежнем языке.
    ///
    /// <paramref name="preference"/> — значение настройки или ключа <c>--lang</c>.
    ///
    /// ⚠️ В <see cref="LanguageDecisions.Resolve"/> значение уходит КАК ПРИШЛО, а не приведённое
    /// к известному виду. Иначе ветка «незнакомая переменная окружения → английский» была бы
    /// недостижима, а обещание документации <c>Resolve</c> расходилось бы с делом: мусор
    /// превращался бы в <c>auto</c> раньше, чем его видел <c>Resolve</c> (дефект, найденный
    /// холодным проверяющим 26.09.2026). Мусор из файла настроек сюда не доходит — его приводит
    /// к <c>auto</c> <c>SettingsStore.Clean</c>, а незнакомый язык ключа <c>--lang</c> отвергает
    /// <see cref="LanguageDecisions.RefuseSwitch"/> ещё до вызова <see cref="Init"/>.
    ///
    /// <see cref="Preference"/> при этом ПРИВОДИТСЯ к известному виду: его показывает окно настроек,
    /// и там «de» из ниоткуда выглядело бы как выбор человека.
    /// </summary>
    public static void Init(string? preference = null)
    {
        Preference = LanguageDecisions.Normalize(preference);
        Language = LanguageDecisions.Resolve(
            preference,
            Environment.GetEnvironmentVariable(LanguageDecisions.Variable),
            LanguageDecisions.SystemLanguage());

        lock (Gate)
        {
            Missing.Clear();
            _english = Load(LanguageDecisions.English);
            _current = Language == LanguageDecisions.English ? _english : Load(Language);
        }
    }

    /// <summary>Строка по ключу. Подстановки — как у <c>string.Format</c>: <c>Loc.T("key", value)</c>.</summary>
    public static string T(string key, params object[] args)
    {
        var text = LanguageDecisions.Pick(key, _current, _english, out var missing);

        // Пропажа запоминается здесь и только здесь: список отдаёт проверке «чего нет»,
        // и он же объясняет человеку, почему в окне стоит имя ключа вместо объяснения.
        if (missing)
        {
            lock (Gate) Missing.Add(key);
        }

        return Apply(text, args);
    }

    /// <summary>
    /// Строка ЯВНОГО языка. Состояние не трогает: ни выбранный язык, ни список пропавших ключей.
    ///
    /// Нужна там, где языков несколько сразу: проверка трёх словарей и сверка подстановок.
    /// Без неё тесты зависели бы от порядка запуска — xunit гоняет классы параллельно, и один
    /// из них однажды выставил бы язык другому.
    /// </summary>
    public static string TIn(string language, string key, params object[] args)
    {
        var wanted = LanguageDecisions.Normalize(language);
        if (wanted == LanguageDecisions.Auto) wanted = Language;

        var text = LanguageDecisions.Pick(
            key, Load(wanted), Load(LanguageDecisions.English), out _);

        return Apply(text, args);
    }

    /// <summary>
    /// Полная сверка трёх словарей, которые лежат внутри сборки: сколько всего ключей и что
    /// пропущено или оставлено пустым в каждом языке. <c>--lang-selftest</c> показывает это
    /// человеку и отдаёт расхождения кодом возврата 1.
    ///
    /// Объединение ключей берётся по всем трём словарям, а не по русскому: так видно и лишний
    /// ключ, которого нет в языке-источнике.
    /// </summary>
    public static Dictionary<string, List<string>> CompareLanguages(out int total)
    {
        var dictionaries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Known) dictionaries[language] = Load(language);

        var union = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dictionary in dictionaries.Values)
        {
            foreach (var key in dictionary.Keys) union.Add(key);
        }

        total = union.Count;

        var gaps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var language in Known)
        {
            var missing = new List<string>();
            foreach (var key in union)
            {
                // Пустое значение считается пропуском — ровно как в Pick.
                if (!dictionaries[language].TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
                {
                    missing.Add(key);
                }
            }

            gaps[language] = missing;
        }

        return gaps;
    }

    /// <summary>
    /// Словарь языка как он есть — для сверки подстановок и для проверок без экрана
    /// (<c>--lang-selftest</c>, <c>tests\DshPanel.Tests\LocalizationTests.cs</c>).
    ///
    /// Внутренний намеренно: снаружи у словаря два читателя — сверка и отчёт, — и открывать
    /// его всему приложению незачем. Строку берут через <see cref="T"/>, а не из словаря.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Dictionary(string language)
    {
        var wanted = LanguageDecisions.Normalize(language);
        return Load(wanted == LanguageDecisions.Auto ? Language : wanted);
    }

    private static string Apply(string text, object[]? args)
    {
        if (args is null || args.Length == 0) return text;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // В переводе потеряли или лишний {0} — показываем строку как есть, а не падаем
            // в окне у человека.
            return text;
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(language, out var cached)) return cached;

            var loaded = Read(language);
            Cache[language] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// Чтение словаря из сборки. Файла может НЕ БЫТЬ — и это нормальный ответ, а не ошибка:
    /// английский и китайский словари пишутся отдельно от механизма, и в промежутке между
    /// работами панель обязана запускаться. Тогда словарь пуст, а строки возвращаются ключами.
    /// </summary>
    private static Dictionary<string, string> Read(string language)
    {
        try
        {
            var name = ResourceName(language);
            if (name is null) return new Dictionary<string, string>(StringComparer.Ordinal);

            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(name);
            if (stream is null) return new Dictionary<string, string>(StringComparer.Ordinal);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());

            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // Разбор словаря не имеет права уронить панель: кривой перевод — это плохой перевод,
            // а не повод не запуститься.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Имя ресурса ищется по СУФФИКСУ (<c>.Localization.ru.json</c>), а не собирается как
    /// «DshPanel.Localization.ru.json»: имя сборки в него не хардкодится, и переименование
    /// проекта не превратит все строки в ключи.
    /// </summary>
    private static string? ResourceName(string language)
    {
        var suffix = ".Localization." + language + ".json";

        foreach (var name in typeof(Loc).Assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return null;
    }
}
