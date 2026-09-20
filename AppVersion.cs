using System.Diagnostics;

namespace DshTray;

/// <summary>
/// Версия панели и история изменений. Номер версии задаётся в DshTray.csproj (SemVer),
/// а точное время сборки — в штампе build.txt: номер про смысл, штамп про время.
/// CHANGELOG.md лежит рядом с панелью и едет вместе с ней в установщик и в архив.
/// </summary>
internal static class AppVersion
{
    private const string FileName = "CHANGELOG.md";

    /// <summary>Короткий номер вида «1.1.0» — без суффикса сборки.</summary>
    public static string Short
    {
        get
        {
            var raw = Application.ProductVersion ?? "";
            var plus = raw.IndexOf('+');
            if (plus > 0) raw = raw.Substring(0, plus);
            return string.IsNullOrWhiteSpace(raw) ? "0.0.0" : raw.Trim();
        }
    }

    /// <summary>«DSH Panel 1.4.0» — для заголовков, трея и подсказок.</summary>
    public static string Title => Loc.T("app.title") + " " + Short;

    public static string PathIn(string baseDir) => Path.Combine(baseDir, FileName);

    public static bool Exists(string baseDir)
    {
        try { return File.Exists(PathIn(baseDir)); }
        catch { return false; }
    }

    /// <summary>Открывает историю версий блокнотом: он есть на любой Windows.</summary>
    public static void OpenChangelog(string baseDir)
    {
        var path = PathIn(baseDir);
        if (!File.Exists(path))
        {
            MessageBox.Show(Loc.T("err.historyMissing", path),
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(Loc.T("err.historyOpen", error.Message),
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
