using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DshPanel.Server;

/// <summary>
/// Что панель может узнать о ЧУЖОМ процессе: командная строка, имя образа, время создания.
///
/// Здесь только ввод-вывод, и отдельным файлом он лежит затем, чтобы решения о владении
/// (<see cref="ServerDecisions"/>) оставались чистыми и перебирались тестами. Перенесено из v1
/// без изменения приёмов: на них там стояло признание владения, и подменять проверенное
/// на красивое здесь нечего.
///
/// Правило поведения одно на весь файл: **не удалось узнать — значит нет**. Пустая командная
/// строка и нулевое время создания не подтверждают владение, а отказывают в нём (предупредить
/// безопаснее, чем принять чужое за своё), и никогда не приводят к исключению наверх.
/// </summary>
public static class ProcessFacts
{
    private const int ProcessCommandLineInformation = 60;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(IntPtr process, int informationClass,
        IntPtr information, int informationLength, out int returnLength);

    /// <summary>
    /// Командная строка процесса: NtQueryInformationProcess с классом ProcessCommandLineInformation
    /// (то же, что показывает диспетчер задач). Пустая строка — сведений нет: нет прав на чужой
    /// процесс, процесс уже завершился или запрос не поддержан.
    /// </summary>
    public static string CommandLine(int pid)
    {
        // Раскладка UNICODE_STRING зависит от разрядности процесса: указатель на строку лежит на
        // смещении 8 только в 64-битном. Собираем под win-x64, но если панель когда-нибудь соберут
        // под x86, чтение по этому смещению дало бы мусорный адрес, а PtrToStringUni по нему —
        // необрабатываемое падение (AccessViolation в .NET не ловится). Пустая строка означает
        // «владение не подтверждено», то есть тот же безопасный дефолт.
        if (IntPtr.Size != 8) return string.Empty;

        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return string.Empty;

            var size = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation,
                        buffer, size, out var needed);

                    if (status == 0)
                    {
                        // UNICODE_STRING: Length (2 байта), MaximumLength (2 байта), указатель на
                        // строку (8 — смещение x64, см. проверку разрядности выше). Строка
                        // скопирована в наш же буфер, поэтому указатель годен сразу.
                        var length = Marshal.ReadInt16(buffer);
                        var pointer = Marshal.ReadIntPtr(buffer, 8);
                        if (length <= 0 || pointer == IntPtr.Zero) return string.Empty;
                        return Marshal.PtrToStringUni(pointer, length / 2)?.Trim() ?? string.Empty;
                    }

                    if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall) return string.Empty;
                    size = Math.Max(needed, size * 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>Имя процесса, как его называет система. Пусто — сведений нет.</summary>
    public static string Name(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Время создания процесса в тиках; 0 — процесса нет, он завершился или сведения о нём
    /// недоступны. По этому значению панель убеждается, что гасит ТОТ ЖЕ процесс, которого
    /// проверяла: номера процессов переиспользуются.
    /// </summary>
    public static long StartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return 0;
            return process.StartTime.Ticks;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Это образ node.exe. Спрашиваем и систему (имя процесса), и командную строку: если
    /// система назвала процесс иначе, командной строке не верим — она могла быть подделана.
    /// </summary>
    public static bool IsNodeImage(string processName, string commandLine)
    {
        var knownByName = processName.Length > 0;
        var nodeByName = string.Equals(processName, "node", StringComparison.OrdinalIgnoreCase);
        if (knownByName && !nodeByName) return false;

        var image = Path.GetFileName(ServerDecisions.FirstToken(commandLine));
        var nodeByLine = string.Equals(image, "node.exe", StringComparison.OrdinalIgnoreCase);

        return nodeByName || nodeByLine;
    }

    /// <summary>Процесс с таким номером существует и ещё не завершился.</summary>
    public static bool IsAlive(int pid) => StartTicks(pid) != 0;
}
