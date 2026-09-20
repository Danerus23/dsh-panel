using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DshTray;

/// <summary>
/// Строки интерфейса. Словари лежат внутри сборки (lang\ru.json, lang\en.json, lang\zh.json),
/// поэтому панель остаётся одним .exe без папок с переводами.
///
/// Язык берётся из настройки (settings.json, поле language): «auto» — по языку системы,
/// иначе ru / en / zh. Переменная DSH_PANEL_LANG или ключ --lang перебивают настройку —
/// так снимаются снимки окон на трёх языках, не трогая профиль.
///
/// Ключа нет в выбранном языке — берём английский; нет и там — возвращаем сам ключ, чтобы
/// пропажа была видна прямо в окне, и запоминаем её: список отдаёт --lang-check.
/// </summary>
internal static class Loc
{
    private static readonly string[] Known = { "ru", "en", "zh" };
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);

    /// <summary>Язык, на котором говорит интерфейс: «ru», «en» или «zh».</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Ключи, которых не нашлось ни в языке, ни в английском.</summary>
    public static IReadOnlyCollection<string> MissingKeys => Missing;

    /// <summary>Сколько строк в словаре выбранного языка.</summary>
    public static int KeyCount => _current.Count;

    /// <summary>
    /// Полная сверка трёх словарей, которые лежат внутри сборки: сколько всего ключей и что
    /// пропущено или оставлено пустым в каждом языке. `--lang-check` показывает это человеку,
    /// а tools\check-lang.mjs сторожит те же файлы в репозитории до сборки.
    /// </summary>
    public static Dictionary<string, List<string>> CompareLanguages(out int total)
    {
        var dictionaries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Known) dictionaries[language] = LoadDictionary(language);

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

    public static void Init(string preference = null)
    {
        Missing.Clear();
        _english = LoadDictionary("en");
        Language = Resolve(preference);
        _current = Language == "en" ? _english : LoadDictionary(Language);
    }

    /// <summary>Что показывать, если система не русская, не английская и не китайская: английский.</summary>
    private static string Resolve(string preference)
    {
        var forced = Environment.GetEnvironmentVariable("DSH_PANEL_LANG");
        if (string.IsNullOrWhiteSpace(forced)) forced = preference;

        var wanted = (forced ?? "auto").Trim().ToLowerInvariant();
        if (wanted.Length == 0 || wanted == "auto")
        {
            wanted = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        }

        return Array.IndexOf(Known, wanted) >= 0 ? wanted : "en";
    }

    /// <summary>Строка по ключу. Подстановки — как у string.Format: Loc.T("key", value).</summary>
    public static string T(string key, params object[] args)
    {
        var text = Pick(key);
        if (args == null || args.Length == 0) return text;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // В переводе потеряли или лишний {0} — показываем строку как есть.
            return text;
        }
    }

    private static string Pick(string key)
    {
        // Ключ с пустым значением — это тоже пропуск: раньше он молча подменялся английским,
        // и в --lang-check пропажа не была видна.
        if (_current.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) return value;
        if (_current.ContainsKey(key)) Missing.Add(key);

        if (_english.TryGetValue(key, out var english) && !string.IsNullOrEmpty(english))
        {
            if (!_current.ContainsKey(key)) Missing.Add(key);
            return english;
        }

        Missing.Add(key);
        return key;
    }

    /// <summary>
    /// Шрифт интерфейса. Для китайского берём шрифт с иероглифами: Segoe UI их не содержит,
    /// и в окне были бы квадратики.
    /// </summary>
    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        var family = Language == "zh" ? "Microsoft YaHei UI" : "Segoe UI";
        try
        {
            return new Font(family, size, style);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, style);
        }
    }

    private static Dictionary<string, string> LoadDictionary(string language)
    {
        var name = "DshTray.lang." + language + ".json";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return new Dictionary<string, string>(StringComparer.Ordinal);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            return parsed == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
