using System.Diagnostics;
using System.Text;

namespace DshTray;

/// <summary>
/// Запуск внешней программы с ожиданием результата.
///
/// Зачем отдельный класс: раньше в нескольких местах вывод читался так —
/// `StandardOutput.ReadToEnd()` и только потом `StandardError.ReadToEnd()`. Если дочерний
/// процесс успевал заполнить буфер stderr (4 КБ) и ждал, пока его прочтут, он не завершался,
/// stdout не закрывался, и ожидание не наступало никогда: установка движка в мастере висела
/// навсегда. Здесь оба потока читаются одновременно, поэтому взаимоблокировки нет.
/// </summary>
internal static class ProcessRunner
{
    public sealed class Outcome
    {
        public bool Started { get; set; }
        public int ExitCode { get; set; } = -1;
        public string Output { get; set; } = "";
        public bool TimedOut { get; set; }
        public string Error { get; set; } = "";

        public bool Ok => Started && !TimedOut && ExitCode == 0;
    }

    public static Outcome Run(string executable, string arguments, int timeoutMs = 60000, string workingDirectory = null)
    {
        var outcome = new Outcome();

        try
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workingDirectory ?? "",
                // node и npm печатают в UTF-8, а .NET по умолчанию читает вывод в кодировке
                // консоли (на русской Windows — 866): вывод npm превращался в крякозябры,
                // и по нему нельзя было понять причину сбоя.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Папка своего Node — в PATH дочернего процесса: npm-скрипты пакетов вызывают
            // «node …» по имени, а переносимый Node в PATH не прописан (см. NodeLocator.AddToPath).
            NodeLocator.AddToPath(info);

            using var process = Process.Start(info);
            if (process == null)
            {
                outcome.Error = "процесс не запустился";
                return outcome;
            }

            outcome.Started = true;

            var output = new StringBuilder();
            var error = new StringBuilder();
            using var outputDone = new ManualResetEventSlim(false);
            using var errorDone = new ManualResetEventSlim(false);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) outputDone.Set();
                else lock (output) output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) errorDone.Set();
                else lock (error) error.AppendLine(e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Ввод закрываем: программа, которая чего-то ждёт со входа, иначе не завершится.
            try { process.StandardInput.Close(); } catch { }

            if (!process.WaitForExit(timeoutMs))
            {
                outcome.TimedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(5000); } catch { }
            }

            outputDone.Wait(2000);
            errorDone.Wait(2000);

            outcome.ExitCode = process.HasExited ? process.ExitCode : -1;
            outcome.Output = (output.ToString() + error.ToString()).Trim();
            return outcome;
        }
        catch (Exception exception)
        {
            outcome.Error = exception.Message;
            return outcome;
        }
    }

    /// <summary>Первая непустая строка вывода: так узнаём версии node, npm и подобное.</summary>
    public static string FirstLine(string executable, string arguments, int timeoutMs = 10000)
    {
        var outcome = Run(executable, arguments, timeoutMs);
        if (!outcome.Started || outcome.Output.Length == 0) return "";

        return outcome.Output
            .Replace("\r", "")
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "";
    }
}
