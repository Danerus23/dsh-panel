using System.Text.RegularExpressions;

namespace DshPanel.Shell;

/// <summary>
/// Путь в том виде, в каком его можно показать в окне, снять в кадр и записать в журнал.
///
/// Зачем: и кадр `--shot`, и журнал панели — файлы, которые уезжают наружу (снимок в README,
/// журнал в резервную копию и в чужой отчёт). Имя пользователя в пути — личные данные,
/// и правило проекта простое: не выносить. Поэтому каталог пользователя заменяется на <c>~</c>:
/// путь остаётся понятным, а имени в нём нет.
///
/// Это маскировка ДЛЯ ПОКАЗА, а не подмена: работать панель продолжает с настоящим путём.
/// Живёт в <c>Shell</c> — рядом со строками и журналом, потому что нужно и настройкам,
/// и серверу, и обоим окнам.
/// </summary>
public static class DisplayMask
{
    private static readonly Regex ForeignProfile = new(
        @"^([A-Za-z]:\\Users\\)([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Path(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(profile);
            if (string.Equals(text, trimmed, StringComparison.OrdinalIgnoreCase)) return "~";

            if (text.StartsWith(trimmed + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "~" + text[trimmed.Length..];
        }

        // Профиль другого человека на этой машине — тоже личные данные.
        var match = ForeignProfile.Match(text);
        return match.Success ? "~" + text[(match.Groups[1].Length + match.Groups[2].Length)..] : text;
    }
}
