using System.Diagnostics;

namespace DshPanel.Platform;

/// <summary>
/// СПРОСИТЬ У ПРОГРАММЫ ЕЁ ВЕРСИЮ — одна дверь на всю панель.
///
/// **Зачем отдельная дверь.** Версии нужны отчёту окружения («какая сейчас DSH, какая Node» —
/// просьба владельца 28.09.2026), а спрашивать их можно двумя способами: запуском программы
/// (<c>node --version</c>) и чтением <c>package.json</c> из каталога пакета. Второй способ дешевле
/// и не запускает чужой процесс, поэтому он и предпочтителен там, где каталог известен; первый
/// нужен для Node, у которого каталога с <c>package.json</c> под рукой нет.
///
/// ⚠️ **Наружу не летит ни одно исключение и не уходит ни один зависший процесс.** Программа может
/// отсутствовать, отказать, зависнуть или напечатать мусор — во всех случаях ответ один: пустая
/// строка, то есть «спросить не удалось». Выдумать версию нельзя: в отчёте она выглядела бы
/// проверенным фактом, а человек по ней решал бы, совместима ли панель с его окружением.
///
/// ⚠️ **Процессу даётся срок** (<see cref="TimeoutMs"/>), и по его истечении он снимается вместе
/// с потомками: иначе справка о версии оставила бы за собой живой узел, которого никто не ждёт.
/// </summary>
public static class VersionProbe
{
    /// <summary>Сколько ждём ответа на вопрос о версии. Версия — не та вещь, ради которой стоит ждать.</summary>
    public const int TimeoutMs = 5000;

    /// <summary>
    /// Версия словами. Пусто — спросить не удалось (нет программы, отказ, срок, пустой вывод).
    /// Берётся ПЕРВАЯ непустая строка вывода: программы печатают версию первой, а дальше идёт
    /// что угодно, и тащить это в отчёт нельзя.
    /// </summary>
    public static string Run(string? executable, string arguments)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return string.Empty;

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                return string.Empty;
            }

            foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
            {
                var text = line.Trim();
                if (text.Length > 0) return text;
            }

            return string.Empty;
        }
        catch
        {
            // Программы нет, запуск запрещён, вывод не прочитался — ответ один: «не удалось».
            return string.Empty;
        }
    }

    /// <summary>
    /// Версия из <c>package.json</c> пакета по его каталогу. Читается без запуска процессов
    /// (у глобальной установки npm рядом с движком лежит готовый номер) — поэтому это первый
    /// способ, а запуск программы — второй.
    /// </summary>
    public static string FromPackage(string? packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory)) return string.Empty;

        try
        {
            var path = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(path)) return string.Empty;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("version", out var version)
                ? (version.GetString() ?? string.Empty).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
