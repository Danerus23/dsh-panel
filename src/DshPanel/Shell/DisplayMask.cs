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

    /// <summary>
    /// ПУТЬ ПРОФИЛЯ ГДЕ УГОДНО ВНУТРИ СТРОКИ — для сообщений среды, а не для показываемого пути.
    /// Шаблон тот же, что у <see cref="ForeignProfile"/>, но без привязки к началу.
    /// </summary>
    private static readonly Regex AnyProfile = new(
        @"([A-Za-z]:\\Users\\)([^\\\s""']+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    /// <summary>
    /// ЗАМАСКИРОВАТЬ ПУТИ ПРОФИЛЯ ВНУТРИ СТРОКИ, А НЕ СТРОКУ ЦЕЛИКОМ.
    ///
    /// **Зачем отдельная дверь.** <see cref="Path"/> маскирует строку, которая САМА является
    /// путём: «<c>C:\Users\имя\…</c>» → «<c>~…</c>». А сообщение исключения приходит как
    /// «<c>FileNotFoundException: Could not find file 'C:\Users\имя\…\DshPanel-2.4.1.zip.part-0'</c>»
    /// — путь стоит в середине, начинается строка с типа, и <see cref="Path"/> его не видит вовсе.
    ///
    /// **От какого случая (DEF-059, 2.4.2).** Именно это сообщение человек прочитал в окне
    /// 24.10.2026 (тип исключения и путь владельца — красная линия 7). Теперь такой текст уходит
    /// только в журнал, и уходит замаскированным: личных данных в нём нет, а причина для разбора
    /// осталась.
    /// </summary>
    public static string PathsIn(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0) return string.Empty;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(profile);
            value = value.Replace(trimmed, "~", StringComparison.OrdinalIgnoreCase);
        }

        return AnyProfile.Replace(value, "~");
    }
}
