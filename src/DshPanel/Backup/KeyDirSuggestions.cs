using DshPanel.Settings;
using DshPanel.Shell;
using System.Globalization;

namespace DshPanel.Backup;

/// <summary>Каталог ключей, который панель нашла у человека и предлагает кнопкой.</summary>
public sealed record KeyDirSuggestion(string Path, string Source)
{
    /// <summary>Строка для кнопки: откуда панель знает этот каталог и (через маску) что это за путь.</summary>
    public string Describe() => $"{Source}: {DisplayMask.Path(Path)}";
}

/// <summary>
/// КАТАЛОГИ КЛЮЧЕЙ, КОТОРЫЕ ПАНЕЛЬ ЗНАЕТ. Нужны ровно для одного: показать человеку найденное
/// и предложить кнопкой.
///
/// Почему предлагать, а не подставлять: это ЕГО каталоги, и решать про них должен он — правило
/// то же, что у рабочей папки от панели v1 (решение владельца 24.09.2026): «найденное у человека
/// предлагать, не подставляя молча».
///
/// Список маленький и НАЗВАННЫЙ, а не «поиск ключей по диску»: искать приватные ключи по всей
/// машине — это и медленно, и неприлично. Панель знает те места, которые называет документация
/// рабочей области: каталог ключей подписи приложения Android — в v1 он ездил в копию группой
/// <c>keys/android</c>. Каталог SSH здесь не нужен: он и так входит в состав копии по умолчанию.
/// </summary>
public static class KeyDirSuggestions
{
    /// <summary>Переменная, которой каталог ключей Android можно назвать явно: имя — не догадка.</summary>
    public const string AndroidKeysVariable = "DSH_PANEL_ANDROID_KEYS";

    /// <summary>Имя каталога ключей Android, которое панель знает.</summary>
    public const string AndroidKeysFolder = "AndroidKeys";

    /// <summary>
    /// Что показать человеку: найденное на диске и при этом ещё не взятое в настройки.
    /// Существование проверяется снаружи (<paramref name="exists"/>), поэтому правило
    /// перебирается проверкой и не трогает диск само.
    /// </summary>
    public static IReadOnlyList<KeyDirSuggestion> Find(
        Func<string, bool> exists, IEnumerable<string>? already)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var taken = new HashSet<string>(
            PanelSettings.NormalizeKeyDirs(already).Select(Full),
            StringComparer.OrdinalIgnoreCase);

        var found = new List<KeyDirSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, source) in Candidates())
        {
            var full = Full(path);
            if (full.Length == 0) continue;
            if (!seen.Add(full)) continue;
            if (taken.Contains(full)) continue;
            if (!exists(full)) continue;

            found.Add(new KeyDirSuggestion(full, source));
        }

        return found;
    }

    /// <summary>Кандидаты: только те, про которые панель знает, и в предсказуемом порядке.</summary>
    private static IEnumerable<(string Path, string Source)> Candidates()
    {
        var named = Environment.GetEnvironmentVariable(AndroidKeysVariable);
        if (!string.IsNullOrWhiteSpace(named))
            yield return (named, string.Format(CultureInfo.CurrentCulture, PanelStrings.KeyDirAndroidByVariableFormat, AndroidKeysVariable));

        yield return (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AndroidKeysFolder),
            PanelStrings.KeyDirAndroidNearProfile);

        yield return (Path.Combine(@"C:\", AndroidKeysFolder), PanelStrings.KeyDirAndroid);
    }

    /// <summary>Полный путь без хвостового разделителя; не разобрался — пусто (кандидат пропускаем).</summary>
    private static string Full(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)));
        }
        catch
        {
            return string.Empty;
        }
    }
}
