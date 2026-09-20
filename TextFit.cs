using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Подгонка подписи под ширину метки, когда внутри путь. В пути нет пробелов, поэтому
/// перенести его по словам нельзя: длинный путь обрезается на краю метки. Убираем звенья
/// из середины — начало и имя файла остаются видны, пропуск помечен многоточием.
///
/// Нужно потому, что пути у всех разные: у одного `C:\Users\Иван\.dsh`, у другого
/// `C:\Users\Иван\AppData\Local\Temp\dsh-acceptance-2026...\.dsh`, и вторая подпись
/// в прежнюю ширину уже не влезает.
/// </summary>
internal static class TextFit
{
    /// <summary>
    /// Укорачивает пути внутри подписи, пока она не влезет в метку. Проверяется самое
    /// длинное слово: у метки с переносом этого достаточно, чтобы текст не обрезался.
    /// </summary>
    public static string Fit(Label label, string text, params string[] paths)
    {
        if (label == null || string.IsNullOrEmpty(text)) return text;
        if (Fits(label, text)) return text;

        var known = paths?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        if (known.Length == 0) return text;

        // Каждый шаг убирает у всех путей по одному звену из середины.
        for (var drop = 1; drop <= 24; drop++)
        {
            var candidate = Compose(text, known, drop);
            if (Fits(label, candidate)) return candidate;
        }

        return Compose(text, known, int.MaxValue);
    }

    private static string Compose(string text, string[] paths, int drop)
    {
        var result = text;
        foreach (var path in paths)
        {
            var shortened = Shorten(path, drop);
            if (!string.Equals(shortened, path, StringComparison.Ordinal)) result = result.Replace(path, shortened);
        }

        return result;
    }

    /// <summary>Убирает из пути звенья посередине: `C:\Users\Иван\.dsh\skills\мой` → `C:\…\мой`.</summary>
    private static string Shorten(string path, int drop)
    {
        var parts = path.Split('\\');
        if (parts.Length <= 2) return path;
        if (drop >= parts.Length - 1) return "…\\" + parts[^1];

        return parts[0] + "\\…\\" + string.Join("\\", parts.Skip(drop + 1));
    }

    /// <summary>
    /// Влезает ли подпись: самое длинное слово не шире метки и на перенос хватает высоты.
    /// Второе важно для меток с несколькими строками: укорачивание пути уменьшает и их число.
    /// </summary>
    private static bool Fits(Label label, string text)
    {
        if (LayoutCheck.LongestWord(text, label.Font) > label.ClientSize.Width) return false;

        var measured = TextRenderer.MeasureText(text, label.Font,
            new Size(Math.Max(1, label.ClientSize.Width), int.MaxValue), TextFormatFlags.WordBreak);
        return measured.Height <= label.ClientSize.Height;
    }
}
