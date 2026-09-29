using System.Diagnostics;
using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// Чем панель гасит найденный процесс.
///
/// Отдельный тип — не украшение: именно на этом месте проверка подставляет подставного убийцу
/// и убеждается, что БЕЗ подтверждения человека панель не гасит встроенный сервер ВООБЩЕ,
/// а не «гасит, но пишет извинение в журнал». Такую проверку с настоящим убийцей написать
/// нельзя: она убила бы процесс, в котором сама идёт.
/// </summary>
public interface IProcessKiller
{
    /// <summary>
    /// Погасить процесс с потомками.
    /// <paramref name="startedAtTicks"/> — время создания, которое видела панель в момент
    /// признания сервера: не совпало — не гасим, номер процесса успел достаться другому.
    /// </summary>
    bool Kill(int pid, long startedAtTicks, out string error);
}

/// <summary>
/// Настоящее гашение процесса с потомками. Приёмы перенесены из v1
/// (<c>ServerController.KillTree</c>): сначала .NET-убийство с проверкой времени создания,
/// затем <c>taskkill /T /F</c> — но тоже лишь убедившись, что номер всё ещё принадлежит
/// ТОМУ ЖЕ процессу.
/// </summary>
public sealed class TreeProcessKiller : IProcessKiller
{
    public bool Kill(int pid, long startedAtTicks, out string error)
    {
        error = string.Empty;

        // Не гасим себя и не гасим «неизвестно кого»: без времени создания сверять нечего.
        if (pid <= 0 || pid == Environment.ProcessId)
        {
            error = PanelStrings.KillNoPid;
            return false;
        }

        if (startedAtTicks == 0)
        {
            error = PanelStrings.KillNoStartTime;
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.Ticks != startedAtTicks)
            {
                error = string.Format(CultureInfo.CurrentCulture, PanelStrings.KillAnotherProcessFormat, pid);
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            // Процесс уже мёртв или не дал себя убить — ниже пробуем taskkill, но лишь
            // убедившись, что номер всё ещё принадлежит тому же процессу.
            error = $"{ex.GetType().Name} — {ex.Message}";
        }

        if (!IsSameProcess(pid, startedAtTicks))
        {
            error = string.Format(CultureInfo.CurrentCulture, PanelStrings.KillAnotherProcessFormat, pid);
            return false;
        }

        try
        {
            var start = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var killer = Process.Start(start);
            killer?.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            // Вызывающий сам проверит, освободился ли порт, и скажет об этом словами.
            error = $"{ex.GetType().Name} — {ex.Message}";
            return false;
        }
    }

    /// <summary>Тот же ли это процесс: номер совпадает и время создания то же.</summary>
    private static bool IsSameProcess(int pid, long startedAtTicks) =>
        startedAtTicks != 0 && ProcessFacts.StartTicks(pid) == startedAtTicks;
}
