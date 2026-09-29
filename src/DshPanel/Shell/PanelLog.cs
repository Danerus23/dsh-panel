using DshPanel.Isolation;

namespace DshPanel.Shell;

/// <summary>
/// Журнал панели: строка со временем в файл под корнем прогона.
///
/// Зачем отдельно, а не «просто Console». Панель — приложение без консоли, и её вывод
/// не видит никто. А урок v1 прямой: **подавленное решение обязано быть видно в журнале**,
/// иначе «панель молчала» ничем не объяснить — ни человеку, ни проверке.
///
/// Путь берётся из <see cref="RunContext"/>, поэтому в изолированном прогоне журнал ложится
/// под его корень и чужих файлов не касается.
/// </summary>
public static class PanelLog
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            var path = RunContext.Current.Paths.LogFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            lock (Gate)
            {
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Журнал не имеет права уронить панель: он последняя линия, а не первая.
            // Молча — намеренно: сообщать о неудаче журнала некуда.
        }
    }
}
