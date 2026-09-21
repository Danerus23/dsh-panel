using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DshTray;

public sealed class ServerStatus
{
    public int Port { get; set; }
    public string Url { get; set; } = "";
    public bool Running { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public bool PortBusyByOther { get; set; }

    /// <summary>Почему панель отказалась останавливать сервер (пусто — отказа не было).</summary>
    public string Refusal { get; set; } = "";

    public string StateText
    {
        get
        {
            if (Running) return Loc.T("state.running");
            if (PortBusyByOther) return Loc.T("state.portBusy");
            return Loc.T("state.stopped");
        }
    }
}

/// <summary>
/// Управление локальным сервером DeepSeek Harness: состояние, запуск, остановка,
/// перезапуск, ссылка для входа. Логика повторяет dsh-web-control.ps1, но сервер
/// поднимается напрямую через node — без процесса PowerShell.
/// </summary>
public sealed class ServerController
{
    private static readonly Regex UrlRegex = new(@"dsh web:\s+(https?://\S+)", RegexOptions.Compiled);
    private static readonly Regex NetstatRegex = new(@"^\s*TCP\s+\S+:(\d+)\s+\S+\s+(\S+)\s+(\d+)\s*$", RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly object _logGate = new();

    private Process _server;
    private StreamWriter _logWriter;
    private bool _urlFound;

    public ServerController(AppPaths paths, int port)
    {
        _paths = paths;
        Port = port;
    }

    public int Port { get; }

    /// <summary>
    /// Каталог, из которого запускается сервер (то, что DSH считает рабочим каталогом).
    /// Пусто или несуществующий путь — папка панели, как было у прежних версий. Мастер
    /// настройки спрашивает рабочую папку при первом запуске, меняется в «Настройках».
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    public string Url => $"http://127.0.0.1:{Port}";

    // --- состояние ---------------------------------------------------------

    public int GetPortOwner()
    {
        int pid;
        try
        {
            pid = TcpTable.GetListenerPid(Port);
        }
        catch
        {
            pid = -1;
        }

        if (pid > 0) return pid;
        if (pid == 0) return 0;
        return NetstatOwner();
    }

    private int NetstatOwner()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return 0;
            string line;
            var fallback = 0;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                var match = NetstatRegex.Match(line);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[1].Value, out var port) || port != Port) continue;
                var state = match.Groups[2].Value;
                var owner = int.TryParse(match.Groups[3].Value, out var parsed) ? parsed : 0;
                if (state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) return owner;
                if (fallback == 0 && owner > 0) fallback = owner;
            }
            process.WaitForExit(5000);
            return fallback;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Состояние сервера. <paramref name="cleanStalePid"/> выключает уборку устаревшей записи
    /// в server.pid — ею пользуется Stop: если остановить сервер не удастся, файлы состояния
    /// должны остаться на месте (в web-url.txt ссылка для входа с токеном, восстановить нечем).
    /// </summary>
    public ServerStatus GetStatus(bool cleanStalePid = true)
    {
        var status = new ServerStatus { Port = Port, Url = Url };
        var owner = GetPortOwner();
        if (owner <= 0)
        {
            // Порта никто не слушает: сервер остановлен или ещё поднимается. Заодно убираем
            // запись в server.pid, если процесс из неё уже мёртв (файл переживает перезагрузку).
            if (cleanStalePid) ForgetStalePid(0, false);
            return status;
        }

        status.Pid = owner;
        status.ProcessName = ProcessName(owner);

        // «Наш сервер» — это тот, чьё владение подтверждается сведениями о самом процессе:
        // записанный номер процесса плюс живой node с нашим портом в командной строке (см. IsOurs).
        // Файл ссылки для входа владением не считается: он лишь источник адреса с токеном, и раньше
        // одного упоминания порта в нём хватало, чтобы панель назвала своим — и остановила —
        // чужой процесс, случайно занявший наш порт.
        if (IsOurs(owner, Port, _paths)) status.Running = true;
        else status.PortBusyByOther = true;

        // Устаревшую запись в server.pid убираем здесь же: иначе на следующем определении
        // состояния она снова выдавала бы себя за доказательство владения.
        if (cleanStalePid) ForgetStalePid(owner, status.Running);

        return status;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Кто слушает порт: номер процесса, его имя и признак «это наш сервер». Нашим считаем
    /// только процесс, чьё владение подтверждается сведениями о нём самом (см. IsOurs):
    /// ссылка для входа на этот порт владением не считается. Без этой проверки мастер и
    /// настройки пугали бы человека янтарным «порт занят», когда порт держит его же
    /// запущенный сервер DSH, — но зато панель не называла бы своим чужой процесс.
    /// </summary>
    public static (int Pid, string Name, bool Ours) Listener(int port, AppPaths paths)
    {
        var pid = 0;
        try
        {
            pid = TcpTable.GetListenerPid(port);
        }
        catch
        {
            pid = 0;
        }

        if (pid <= 0) return (0, "", false);

        var name = ProcessName(pid);
        if (!string.Equals(name, "node", StringComparison.OrdinalIgnoreCase)) return (pid, name, false);

        return (pid, name, IsOurs(pid, port, paths));
    }

    // --- подтверждение владения --------------------------------------------
    //
    // Владение подтверждается сведениями о самом процессе, а не файлами состояния.
    // Проверка стоит на пути и определения состояния, и остановки сервера: процесс, не
    // прошедший её, не убивают никогда — иначе панель останавливала бы чужую программу,
    // занявшую её порт (на живой машине так чуть не погиб чужой node, поднятый прежней
    // копией панели: server.pid хранил мёртвый номер, а ссылка для входа упоминала порт).

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

    // Последний вердикт о владении: причина отказа и то, к кому она относилась. Нужен, чтобы
    // не писать одну и ту же строку на каждом тике таймера (1,5 с) — иначе журнал превратился
    // бы в поток «не подтверждён» и вытеснил всё остальное. -1 — вердикта ещё не было.
    private static int _verdictPid = -1;
    private static int _verdictPort = -1;
    private static string _verdictReason = "";

    /// <summary>
    /// Наш ли это сервер. Сервер наш, только если выполнено всё сразу:
    /// (а) в server.pid записан именно этот номер процесса;
    /// (б) процесс жив;
    /// (в) это node (образ node.exe);
    /// (г) в его командной строке есть наш порт;
    /// (д) это не наш собственный процесс.
    ///
    /// Путь к bin.js движка намеренно НЕ проверяется. Он законно меняется — свой переносимый
    /// Node, накат копии, переустановка движка, — а ложное «чужой» дороже: панель теряет
    /// способность остановить СВОЙ сервер и показывает вечное «порт занят другим процессом».
    /// Номер процесса из server.pid пишем только мы, а от переиспользования номера защищает
    /// время создания процесса: убийство (KillTree) сверяет его с тем, что видела эта проверка.
    ///
    /// Файл ссылки для входа (web-url.txt) владением не считается вообще: в нём адрес с
    /// токеном, и раньше одного упоминания порта в нём хватало, чтобы назвать своим — и
    /// остановить — чужой процесс. Номер процесса из server.pid сам по себе тоже не доказательство:
    /// файл переживает перезагрузку, а номера переиспользуются.
    ///
    /// Сведения о процессе получить не удалось (нет прав, чужая учётная запись, защищённый
    /// процесс) — считаем сервер чужим: предупредить безопаснее, чем остановить чужое.
    /// Причина отказа попадает в журнал приложения — один раз на смену вердикта.
    /// </summary>
    private static bool IsOurs(int pid, int port, AppPaths paths)
    {
        return IsOurs(pid, port, paths, out _);
    }

    /// <summary>
    /// То же, но наружу отдаётся время создания процесса — его Stop передаёт в KillTree, чтобы
    /// убить именно тот процесс, который проверяла эта проверка (номер процесса переиспользуется).
    /// </summary>
    private static bool IsOurs(int pid, int port, AppPaths paths, out long startedAt)
    {
        startedAt = 0;

        if (pid <= 0) return false;

        // (д) Свой собственный процесс сервером быть не может: раньше «Остановить» могла
        // прицелиться в саму панель, если её номер оказался в server.pid.
        if (pid == Environment.ProcessId)
        {
            LogVerdict(paths, pid, port, $"владение портом {port} (PID {pid}) не подтверждено: "
                                         + "это сама панель, считаю владельца чужим");
            return false;
        }

        // (а) Записанный номер обязан совпасть: без него владение подтвердить нечем.
        if (ReadRecordedPid(paths) != pid)
        {
            LogVerdict(paths, pid, port, $"владение портом {port} (PID {pid}) не подтверждено: "
                                         + "в server.pid записан другой номер, считаю владельца чужим");
            return false;
        }

        // (б) Процесс жив; заодно берём время его создания — по нему потом сверяется убийство.
        startedAt = ProcessStartTicks(pid);
        if (startedAt == 0)
        {
            LogVerdict(paths, pid, port, $"владение портом {port} (PID {pid}) не подтверждено: "
                                         + "процесс недоступен, считаю владельца чужим");
            return false;
        }

        // (в, г) Сведения о процессе: имя образа и командная строка.
        var commandLine = ProcessCommandLine(pid);
        if (commandLine.Length == 0)
        {
            startedAt = 0;
            LogVerdict(paths, pid, port, $"владелец порта {port} (PID {pid}) не подтверждён: сведения "
                                         + "о процессе недоступны, считаю его чужим");
            return false;
        }

        if (!IsNodeImage(pid, commandLine))
        {
            startedAt = 0;
            LogVerdict(paths, pid, port, $"владелец порта {port} (PID {pid}) не подтверждён: "
                                         + "это не node.exe, считаю его чужим");
            return false;
        }

        if (!CommandLineHasPort(commandLine, port))
        {
            startedAt = 0;
            LogVerdict(paths, pid, port, $"владелец порта {port} (PID {pid}) не подтверждён: "
                                         + "в командной строке нет нашего порта, считаю его чужим");
            return false;
        }

        // Владение подтверждено. В журнал это тоже попадает — но только как смена вердикта.
        LogVerdict(paths, pid, port, "");
        return true;
    }

    /// <summary>
    /// Пишет вердикт о владении в журнал, только когда он изменился. IsOurs зовут на каждом тике
    /// таймера трея (1,5 с), и без этой проверки при постоянно занятом порте журнал заполнялся бы
    /// одинаковыми строками (десятки тысяч в сутки) и ротировался бы за часы, вытесняя полезное.
    /// Пустая причина означает «владение подтверждено».
    /// </summary>
    private static void LogVerdict(AppPaths paths, int pid, int port, string reason)
    {
        if (_verdictPid == pid && _verdictPort == port
            && string.Equals(_verdictReason, reason, StringComparison.Ordinal)) return;

        _verdictPid = pid;
        _verdictPort = port;
        _verdictReason = reason;

        AppLog.Write(paths, reason.Length > 0
            ? reason
            : $"владение портом {port} подтверждено: PID {pid} — наш сервер");
    }

    /// <summary>Номер процесса из server.pid; 0 — файла нет, он нечитаем или в нём не число.</summary>
    private static int ReadRecordedPid(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.PidPath)) return 0;
            return int.TryParse(File.ReadAllText(paths.PidPath).Trim(), out var recorded) && recorded > 0
                ? recorded
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Убирает устаревшую запись в server.pid. Файл переживает перезагрузку, а номера процессов
    /// переиспользуются, поэтому запись — подсказка, а не доказательство.
    ///
    /// Правило: удаляем, если записанный процесс мёртв ЛИБО порт держит другой процесс. Оставляем,
    /// когда владение подтверждено, и когда порт пока свободен, а записанный процесс жив: сервер
    /// как раз поднимается, и запись только что сделала сама панель (Start пишет файл до того,
    /// как движок привяжет порт).
    ///
    /// Прежняя формулировка была инвертирована — она удаляла запись ровно тогда, когда порт держал
    /// именно записанный процесс, и оставляла её, когда порт занят чужим. Не «чинить» обратно.
    ///
    /// web-url.txt не трогаем никогда: в нём ссылка для входа с токеном.
    /// </summary>
    private void ForgetStalePid(int owner, bool ours)
    {
        try
        {
            if (ours) return;
            if (!File.Exists(_paths.PidPath)) return;

            var text = File.ReadAllText(_paths.PidPath).Trim();
            if (!int.TryParse(text, out var recorded) || recorded <= 0)
            {
                TryDelete(_paths.PidPath);
                AppLog.Write(_paths, "server.pid устарел (в файле не номер процесса) — убран");
                return;
            }

            // Мёртвая запись или порт занят кем-то другим — доказательством такая запись быть
            // не может. А вот «порт держит именно записанный процесс, но он не наш» оставляем:
            // своё решение принимает IsOurs, и переписывать чужой номер в файле нечего.
            if (IsAlive(recorded) && (owner <= 0 || owner == recorded)) return;

            TryDelete(_paths.PidPath);
            AppLog.Write(_paths, $"server.pid устарел (записан {recorded}, слушает {owner}) — убран");
        }
        catch
        {
            // Файл занят или нечитаем: проверка владения и без него честная, поэтому молчим.
        }
    }

    /// <summary>
    /// Время создания процесса в тиках; 0 — процесса нет, он завершился или сведения о нём
    /// недоступны (нет прав). По этому значению Stop убеждается, что убивает тот же процесс,
    /// который проверяла IsOurs: номера процессов переиспользуются.
    /// </summary>
    private static long ProcessStartTicks(int pid)
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

    /// <summary>Процесс с таким номером существует и ещё не завершился.</summary>
    private static bool IsAlive(int pid)
    {
        return ProcessStartTicks(pid) != 0;
    }

    /// <summary>
    /// Это образ node.exe. Спрашиваем и систему (имя процесса), и командную строку: если
    /// система назвала процесс иначе, командной строке не верим — она могла быть подделана.
    /// </summary>
    private static bool IsNodeImage(int pid, string commandLine)
    {
        var byName = ProcessName(pid);
        var knownByName = byName.Length > 0;
        var nodeByName = string.Equals(byName, "node", StringComparison.OrdinalIgnoreCase);
        if (knownByName && !nodeByName) return false;

        var image = Path.GetFileName(FirstToken(commandLine));
        var nodeByLine = string.Equals(image, "node.exe", StringComparison.OrdinalIgnoreCase);

        return nodeByName || nodeByLine;
    }

    /// <summary>Первый элемент командной строки — путь к самому исполняемому файлу.</summary>
    private static string FirstToken(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Порт в командной строке именно как аргумент запуска сервера: «--port 3080», «--port=3080»,
    /// «--port:3080» (движок и прежние сборки могли звать по-разному, и свой сервер из-за формы
    /// записи не должен становиться чужим). Хвост (?!\d) не даёт порту 3080 совпасть с 30800.
    /// </summary>
    private static bool CommandLineHasPort(string commandLine, int port)
    {
        return Regex.IsMatch(commandLine, @"--port[=:\s]+""?" + port + @"(?!\d)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Командная строка процесса: NtQueryInformationProcess с классом ProcessCommandLineInformation
    /// (то же, что показывает диспетчер задач). Пустая строка — сведений нет: нет прав на
    /// чужой процесс, процесс уже завершился или запрос не поддержан. Тогда владение не
    /// подтверждается, и панель считает владельца порта чужим.
    /// </summary>
    private static string ProcessCommandLine(int pid)
    {
        // Раскладка UNICODE_STRING зависит от разрядности процесса: указатель на строку лежит на
        // смещении 8 только в 64-битном. Собираем под win-x64, но если панель когда-нибудь соберут
        // под x86, чтение по этому смещению дало бы мусорный адрес, а PtrToStringUni по нему —
        // необрабатываемое падение (AccessViolation в .NET не ловится). Пустая строка означает
        // «владение не подтверждено», то есть тот же безопасный дефолт.
        if (IntPtr.Size != 8) return "";

        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return "";

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
                        if (length <= 0 || pointer == IntPtr.Zero) return "";
                        return Marshal.PtrToStringUni(pointer, length / 2)?.Trim() ?? "";
                    }

                    if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall) return "";
                    size = Math.Max(needed, size * 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>
    /// Все ссылки для входа, которые видит приложение: сначала свои, потом из
    /// папки прежнего лаунчера. Ссылка из архива может быть уже нерабочей,
    /// поэтому годность каждой проверяется отдельно.
    /// </summary>
    public List<string> CandidateUrls()
    {
        var result = new List<string>();
        foreach (var path in _paths.UrlCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var url = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (url.Length == 0) continue;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Regex.IsMatch(url, $":{Port}(?:[/?#]|$)")) continue;
                if (!result.Contains(url)) result.Add(url);
            }
            catch
            {
                // Нечитаемый файл — пробуем следующий.
            }
        }

        return result;
    }

    /// <summary>Ссылка для входа, напечатанная сервером ('' — неизвестна).</summary>
    public string GetAuthenticatedUrl(bool skipRunningCheck = false)
    {
        if (!skipRunningCheck && !GetStatus().Running) return "";
        var candidates = CandidateUrls();
        return candidates.Count > 0 ? candidates[0] : "";
    }

    /// <summary>Что ответил сервер на последнюю проверку готовности (для диагностики).</summary>
    public string LastProbeResult { get; private set; } = "проверок не было";

    /// <summary>Один запрос к странице: отвечает ли она и с каким кодом.</summary>
    public bool ProbeUrl(string url, out string status, int timeoutMs = 3000)
    {
        status = "";
        if (string.IsNullOrWhiteSpace(url)) { status = "пустая ссылка"; return false; }

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();

            status = $"HTTP {(int)response.StatusCode}";
            return (int)response.StatusCode < 400;
        }
        catch (Exception error)
        {
            status = "ошибка: " + error.Message;
            return false;
        }
    }

    /// <summary>
    /// Ждёт, пока какая-нибудь из известных ссылок начнёт отвечать, и возвращает
    /// именно её: устаревшая ссылка из архива прежнего лаунчера не должна
    /// подменять рабочую (иначе браузер открывает 401 и ждёт впустую).
    /// </summary>
    public string WaitForWorkingUrl(int timeoutSec, Action tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);

        while (DateTime.UtcNow < deadline)
        {
            foreach (var candidate in CandidateUrls())
            {
                if (ProbeUrl(candidate, out var status, 2000))
                {
                    LastProbeResult = status;
                    return candidate;
                }

                LastProbeResult = status;
            }

            Thread.Sleep(300);
            tick?.Invoke();
        }

        return "";
    }

    /// <summary>Ждёт готовности конкретной ссылки (диагностика).</summary>
    public bool WaitUntilServing(string url, int timeoutSec = 30, Action tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (ProbeUrl(url, out var status, 4000))
            {
                LastProbeResult = status;
                return true;
            }

            LastProbeResult = status;
            Thread.Sleep(300);
            tick?.Invoke();
        }

        return false;
    }

    /// <summary>Открывает браузер, дождавшись, пока страница начнёт отвечать.</summary>
    public bool OpenWebWhenReady(Action tick = null, int timeoutSec = 30)
    {
        var started = Environment.TickCount64;
        var url = WaitForWorkingUrl(timeoutSec, tick);

        if (url.Length == 0)
        {
            AppLog.Write(_paths, $"рабочая ссылка не появилась за {timeoutSec} с ({LastProbeResult})");
            return OpenWeb(quiet: true);
        }

        AppLog.Write(_paths, $"страница готова через {Environment.TickCount64 - started} мс ({LastProbeResult}), открываю браузер");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }

    public bool OpenWeb(bool quiet = false)
    {
        var url = GetAuthenticatedUrl();
        if (url.Length == 0)
        {
            if (quiet) return false;
            throw new InvalidOperationException(Loc.T("error.urlUnknown", Url));
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }

    public void OpenLog()
    {
        if (File.Exists(_paths.LogPath)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_paths.LogPath}\""));
        else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.LogDir}\""));
    }

    public void OpenDshFolder()
    {
        var home = AppPaths.DshHome;
        Directory.CreateDirectory(home);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{home}\""));
    }

    // --- запуск и остановка ------------------------------------------------

    public ServerStatus Start(bool openBrowser, Action tick = null, int timeoutSec = 90, int attempt = 1)
    {
        var status = GetStatus();
        if (status.Running)
        {
            if (openBrowser) OpenWebWhenReady(tick);
            return status;
        }
        if (status.PortBusyByOther)
        {
            throw new InvalidOperationException(
                Loc.T("error.portBusy", Port, status.ProcessName, status.Pid));
        }

        Directory.CreateDirectory(_paths.LogDir);
        if (File.Exists(_paths.LogPath) && new FileInfo(_paths.LogPath).Length > 5 * 1024 * 1024)
        {
            TryDelete(_paths.LogPath);
        }
        TryDelete(_paths.OwnUrlPath);

        var node = NodeLocator.ResolveNode();
        var bin = NodeLocator.ResolveDshBin();

        // Порт проверяем на возможность занять его по-настоящему: «кто-то слушает» — не то же
        // самое. Порт бывает зарезервирован системой (Hyper-V, WSL, обновления) или удержан
        // службой, которая не слушает; тогда движок не привяжется, а человек видел бы просто
        // «сервер не поднялся» без причины.
        if (!CanBind(Port))
        {
            throw new InvalidOperationException(Loc.T("error.portUnavailable", Port));
        }

        // Общий node_modules движка — до запуска: без него плагины не находятся и сервер не встаёт.
        EnsureEngineFallback(bin, shouldLog: false);

        var psi = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ResolveWorkingDirectory(),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(bin);
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--no-open");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(Port.ToString(CultureInfo.InvariantCulture));

        // PATH для движка собираем заново — из реестра, а не из окружения процесса.
        //
        // Почему: панель может быть запущена установщиком ДО того, как появился Node, и тогда её
        // окружение навсегда остаётся без него. Движок внутри такого процесса не находил node и npm
        // (его собственные шаги установки/подготовки профиля шли не тем инструментом), из-за чего
        // первый запуск после установки падал, а после перезапуска панели всё работало — ровно это
        // и случилось на приёмке. Реестр даёт актуальный PATH и пользователя, и системы.
        var npmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var nodeDir = Path.GetDirectoryName(node) ?? "";
        psi.Environment["Path"] = $"{nodeDir};{npmDir};{FreshPath()}";

        EnsureLogWriter();
        _urlFound = false;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        if (!process.Start())
        {
            CloseLog();
            throw new InvalidOperationException(Loc.T("error.serverStartFailed"));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _server = process;

        WriteLog($"запуск: dsh web --no-open --port {Port} (node PID {process.Id}), "
                 + $"рабочий каталог: {psi.WorkingDirectory}");
        try
        {
            File.WriteAllText(_paths.PidPath, process.Id.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
        }
        catch
        {
            // pid-файл — подсказка для других инструментов, без него тоже работаем.
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            tick?.Invoke();
            var now = GetStatus();
            if (now.Running)
            {
                if (openBrowser) OpenWebWhenReady(tick);
                return now;
            }
            if (process.HasExited) break;
        }

        // Первая попытка сразу после установки движка часто срывается: движок в этот момент
        // достраивает свой профиль и общие ссылки, и дерево плагинов не собирается. На приёмке
        // запуск удавался только после перезапуска панели — то есть дело было именно в неготовом
        // движке, а не в панели. Поэтому даём ему ещё две попытки, каждый раз обновляя ссылки.
        if (attempt < 3)
        {
            WriteLog($"попытка {attempt} не удалась, обновляю ссылки движка и повторяю");
            try { EnsureEngineFallback(NodeLocator.ResolveDshBin(), shouldLog: true); } catch { }
            Thread.Sleep(5000);
            return Start(openBrowser, tick, timeoutSec, attempt + 1);
        }

        // Показываем первую содержательную ошибку движка, а не хвост многострочной трассировки:
        // по строке «Cannot find package …» причина ясна сразу, по хвосту — нет.
        // Сначала проверяем, не занял ли порт кто-то другой: тогда причина в этом, а не в движке.
        var (portOwner, portName, portOurs) = Listener(Port, _paths);
        var hint = portOwner != 0 && !portOurs
            ? Loc.T("error.serverPortBusy", Port, portName)
            : FirstEngineError();

        // Иначе — первая содержательная ошибка движка: по строке «Cannot find package …»
        // причина ясна сразу, по хвосту многострочной трассировки — нет.
        if (hint.Length == 0) hint = TailLog(10);
        throw new InvalidOperationException(Loc.T("error.serverNotUp", Port, _paths.LogPath, hint));
    }

    /// <summary>
    /// Рабочий каталог сервера: выбранный пользователем, если он существует, иначе
    /// папка панели. Несуществующий путь не должен мешать серверу подняться — о нём
    /// пишем в журнал, а не молчим.
    /// </summary>
    private string ResolveWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory)) return _paths.BaseDir;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(WorkingDirectory.Trim());
            if (Directory.Exists(expanded)) return expanded;
            WriteLog($"рабочая папка не найдена, запускаю из папки панели: {expanded}");
        }
        catch
        {
            // Кривой путь — работаем из папки панели.
        }

        return _paths.BaseDir;
    }

    public ServerStatus Restart(bool openBrowser, Action tick = null)
    {
        try
        {
            Stop(tick);
        }
        catch
        {
            // Остановка не должна отменять перезапуск.
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (GetPortOwner() == 0) break;
            Thread.Sleep(300);
            tick?.Invoke();
        }

        return Start(openBrowser, tick);
    }

    public ServerStatus Stop(Action tick = null, int timeoutSec = 25)
    {
        // Определяем состояние без чистки server.pid: если остановить не удастся, файлы
        // состояния обязаны остаться на месте (см. в конце метода).
        var status = GetStatus(cleanStalePid: false);

        // Порт держит чужая программа. Панель не имеет права её останавливать: это может быть
        // чужой сервер с несохранёнными данными. Говорим об этом и выходим.
        if (!status.Running && status.PortBusyByOther)
        {
            status.Refusal = Loc.T("error.portBusy", status.Port, status.ProcessName, status.Pid);
            AppLog.Write(_paths, "остановка отменена: " + status.Refusal);
            return status;
        }

        // Убиваем только то, что доказанно наше.
        var process = _server;
        if (process != null)
        {
            // Свой сервер держим живым дескриптором: пока процесс не завершился, его номер не
            // может достаться чужому — поэтому убиваем по этому же дескриптору, а не по номеру.
            KillTree(process);
        }
        else if (status.Running && status.Pid > 0 && status.Pid != Environment.ProcessId
                 && IsOurs(status.Pid, Port, _paths, out var startedAt))
        {
            // Владение перепроверяем прямо перед убийством: между определением состояния и
            // этим моментом номер процесса мог достаться чужому, а чужое панель не убивает.
            // KillTree сверяет ещё и время создания — то самое, что видела эта проверка.
            KillTree(status.Pid, startedAt);
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            tick?.Invoke();
            if (GetPortOwner() == 0) break;
        }

        // Порт всё ещё занят — добиваем владельца, но только если он прошёл проверку владения
        // (номер из server.pid, живой node, наш порт в командной строке) и это по-прежнему тот
        // самый процесс: время создания сверяет KillTree. Чужой процесс не убиваем никогда.
        var owner = GetPortOwner();
        if (owner > 0 && owner != Environment.ProcessId
            && IsOurs(owner, Port, _paths, out var ownerStartedAt))
        {
            KillTree(owner, ownerStartedAt);
            Thread.Sleep(500);
            tick?.Invoke();
        }

        _server = null;
        CloseLog();

        // Файлы состояния убираем только если порт в итоге свободен. Если остановить не удалось,
        // server.pid и web-url.txt остаются: в ссылке токен входа, и восстановить его нечем —
        // стереть её на неудавшейся остановке значило бы потерять вход в панель навсегда.
        if (GetPortOwner() == 0)
        {
            TryDelete(_paths.PidPath);
            TryDelete(_paths.OwnUrlPath);
        }
        else
        {
            AppLog.Write(_paths, "порт остался занят — server.pid и web-url.txt оставлены "
                                 + "(в web-url.txt ссылка для входа с токеном)");
        }

        return GetStatus(cleanStalePid: false);
    }

    // --- служебное ---------------------------------------------------------

    private void EnsureLogWriter()
    {
        lock (_logGate)
        {
            if (_logWriter != null) return;
            try
            {
                _logWriter = new StreamWriter(_paths.LogPath, append: true, new UTF8Encoding(true)) { AutoFlush = true };
            }
            catch
            {
                _logWriter = null;
            }
        }
    }

    private void CloseLog()
    {
        lock (_logGate)
        {
            try
            {
                _logWriter?.Dispose();
            }
            catch
            {
                // Ничего: журнал уже мог быть закрыт.
            }
            _logWriter = null;
        }
    }

    /// <summary>
    /// Актуальный PATH: пользовательский и системный, из реестра.
    ///
    /// Переменная окружения процесса для этого не годится: панель, запущенная до установки Node
    /// (например, установщиком или автозапуском при входе), держит PATH без него, и все дочерние
    /// процессы — включая движок — получают устаревшее окружение.
    ///
    /// Путь к системному PATH в реестре был записан без разделителей
    /// («SYSTEMCurrentControlSet…»), ключ молча не открывался, и системный PATH в окружение
    /// сервера не попадал. Теперь неудача открытия ключа ещё и попадает в журнал: раньше она
    /// была неотличима от «ключ пуст».
    /// </summary>
    private string FreshPath()
    {
        var parts = new List<string>();

        void AddFrom(RegistryKey root, string subKey, string title)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key?.GetValue("Path") is string value && value.Length > 0)
                {
                    parts.Add(Environment.ExpandEnvironmentVariables(value));
                    return;
                }

                AppLog.Write(_paths, $"PATH: ключ реестра не прочитан или пуст — {title} ({subKey})");
            }
            catch (Exception error)
            {
                AppLog.Write(_paths, $"PATH: ключ реестра не прочитан — {title} ({subKey}): {error.Message}");
            }
        }

        AddFrom(Registry.CurrentUser, "Environment", "PATH пользователя");
        AddFrom(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            "системный PATH");
        parts.Add(Environment.GetEnvironmentVariable("Path") ?? "");

        return string.Join(";", parts.Where(part => part.Length > 0));
    }

    /// <summary>
    /// Можно ли действительно занять порт. Проверяем привязкой на loopback с разрешённым
    /// повторным использованием адреса (как у node): иначе порт в состоянии TIME_WAIT давал бы
    /// ложный отказ.
    /// </summary>
    public static bool CanBind(int port)
    {
        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = false };
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Общий <c>node_modules</c> движка в домашней папке: <c>%USERPROFILE%\.dsh\profiles\node_modules</c>.
    ///
    /// Зачем: движок подставляет туда по ссылке (junction) каждый пакет своей установки, и по
    /// этим ссылкам плагины — <c>@deepseek-ai/dsh-llm</c>, <c>dsh-web-app</c> и ещё сотни —
    /// находятся из каталога профиля обычным обходом родительских папок. Без этой папки сервер
    /// падает на старте: «plugin tree failed to load: Cannot find package
    /// '&#64;deepseek-ai/dsh-llm'» — это и случилось на приёмке в чистой Windows, где движок
    /// не успел создать ссылки сам.
    ///
    /// Устройство папки важно: движок, найдя в ней запись, которая не ссылка, падает с ошибкой
    /// «exists and is not a symlink». Поэтому кладём именно по ссылке на каждый пакет (junction;
    /// прав администратора, в отличие от симлинка, не требует), а не одну ссылку на весь каталог.
    /// Существующие записи не трогаем.
    /// </summary>
    private void EnsureEngineFallback(string binPath, bool shouldLog)
    {
        try
        {
            // <установка>\@deepseek-ai\dsh\lib\bin.js → движок в <установка>\@deepseek-ai\dsh
            var engineDir = Path.GetDirectoryName(Path.GetDirectoryName(binPath) ?? "");
            if (string.IsNullOrEmpty(engineDir) || !Directory.Exists(engineDir)) return;

            var engineModules = Path.Combine(engineDir, "node_modules");
            if (!Directory.Exists(engineModules)) return;

            var fallback = Path.Combine(_paths.ProfilesDir, "node_modules");
            Directory.CreateDirectory(fallback);

            // Список: сам пакет dsh и каждый пакет его установки (включая области @scope).
            var links = new List<(string Link, string Target)>
            {
                (Path.Combine(fallback, "dsh"), engineDir),
            };

            foreach (var entry in Directory.GetFileSystemEntries(engineModules))
            {
                var name = Path.GetFileName(entry);
                if (!Directory.Exists(entry)) continue;

                if (name.StartsWith('@'))
                {
                    // Область: ссылки кладём на каждый пакет внутри неё — так же, как движок.
                    Directory.CreateDirectory(Path.Combine(fallback, name));
                    foreach (var inner in Directory.GetFileSystemEntries(entry))
                    {
                        if (Directory.Exists(inner))
                        {
                            links.Add((Path.Combine(fallback, name, Path.GetFileName(inner)), inner));
                        }
                    }
                }
                else
                {
                    links.Add((Path.Combine(fallback, name), entry));
                }
            }

            var missing = links.Where(pair => !Directory.Exists(pair.Link) && !File.Exists(pair.Link)).ToList();
            if (missing.Count == 0) return;

            // Пачкой, одним файлом: сотни вызовов mklink по одному занимали бы секунды.
            var script = Path.Combine(Path.GetTempPath(), "dsh-panel-links-" + Guid.NewGuid().ToString("N") + ".cmd");
            var lines = new List<string> { "@echo off" };
            lines.AddRange(missing.Select(pair =>
                "mklink /J \"" + pair.Link + "\" \"" + pair.Target + "\" >nul"));
            File.WriteAllLines(script, lines, Encoding.ASCII);

            try
            {
                ProcessRunner.Run("cmd.exe", "/c \"" + script + "\"", 60000);
            }
            finally
            {
                try { File.Delete(script); } catch { }
            }

            var created = missing.Count(pair => Directory.Exists(pair.Link));
            AppLog.Write(_paths, "общий node_modules движка подготовлен: " + created + " из "
                                 + missing.Count + " ссылок в " + fallback);
        }
        catch (Exception error)
        {
            AppLog.Write(_paths, "не удалось подготовить общий node_modules движка: " + error.Message);
        }
    }

    /// <summary>
    /// Первая содержательная строка ошибки из журнала сервера: её показываем человеку вместо
    /// «смотрите журнал». Ошибки движка идут многострочными блоками, и по одной строке
    /// («Cannot find package …») причина уже понятна.
    /// </summary>
    private string FirstEngineError()
    {
        try
        {
            if (!File.Exists(_paths.LogPath)) return "";

            var tail = File.ReadLines(_paths.LogPath).TakeLast(400).ToList();

            // Журнал не стирается между запусками, поэтому ошибку ищем только после последней
            // отметки «запуск:» — иначе панель выдавала старую ошибку за свежую.
            var start = tail.FindLastIndex(line => line.Contains("запуск:", StringComparison.Ordinal));
            var lines = start >= 0 ? tail.Skip(start).ToList() : tail;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var index = trimmed.IndexOf("Error:", StringComparison.Ordinal);
                if (index >= 0)
                {
                    var text = trimmed[(index + "Error:".Length)..].Trim();
                    if (text.Length > 0) return text.Length > 180 ? text[..180] : text;
                }
            }

            var last = lines.LastOrDefault(line => line.Trim().Length > 0) ?? "";
            return last.Length > 180 ? last[..180] : last.Trim();
        }
        catch
        {
            return "";
        }
    }

    private void WriteLog(string text)
    {
        lock (_logGate)
        {
            if (_logWriter == null) return;
            try
            {
                _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
            }
            catch
            {
                // Проблемы с журналом не должны ломать управление сервером.
            }
        }
    }

    private void HandleLine(string line)
    {
        if (line == null) return;
        WriteLog(line);
        if (_urlFound) return;

        var match = UrlRegex.Match(line);
        if (!match.Success) return;

        _urlFound = true;
        try
        {
            File.WriteAllText(_paths.OwnUrlPath, match.Groups[1].Value, new UTF8Encoding(false));
            WriteLog($"ссылка для входа сохранена в {_paths.OwnUrlPath}");
        }
        catch
        {
            // Не смогли сохранить ссылку — «Открыть в браузере» сообщит об этом.
        }
    }

    private string TailLog(int lines)
    {
        try
        {
            if (!File.Exists(_paths.LogPath)) return "";
            var tail = File.ReadLines(_paths.LogPath).TakeLast(lines);
            return Environment.NewLine + string.Join(Environment.NewLine, tail);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Останавливает процесс и его потомков — но только если это по-прежнему ТОТ САМЫЙ процесс,
    /// которого проверяла IsOurs. Номер процесса переиспользуется, поэтому одного номера мало:
    /// между проверкой владения и убийством процесс мог завершиться и его номер достаться
    /// чужому. Время создания процесса (startedAt) — то, что видела проверка; не совпало —
    /// не убиваем.
    /// </summary>
    private static bool KillTree(int pid, long startedAt)
    {
        if (pid <= 0 || pid == Environment.ProcessId || startedAt == 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.Ticks != startedAt) return false;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch
        {
            // Процесс уже мёртв или не даёт себя убить — ниже пробуем taskkill, но лишь
            // убедившись, что номер всё ещё принадлежит тому же процессу.
        }

        if (!IsSameProcess(pid, startedAt)) return false;

        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var killer = Process.Start(psi);
            killer?.WaitForExit(5000);
            return true;
        }
        catch
        {
            // Ничего: вызывающий код проверит, освободился ли порт.
            return false;
        }
    }

    /// <summary>
    /// Останавливает процесс, который панель сама запустила и держит живым дескриптором.
    /// Здесь номер процесса переиспользован быть не может, пока дескриптор жив, поэтому
    /// убиваем по нему же, а не по номеру, взятому из файла или из таблицы портов.
    /// </summary>
    private static bool KillTree(Process process)
    {
        if (process == null) return false;

        try
        {
            if (process.HasExited) return false;
            if (process.Id == Environment.ProcessId) return false;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch
        {
            // Не дал себя убить: дальше порт проверит Stop и добьёт владельца по проверке владения.
            return false;
        }
    }

    /// <summary>Тот же ли это процесс: номер совпадает и время создания то же.</summary>
    private static bool IsSameProcess(int pid, long startedAt)
    {
        return startedAt != 0 && ProcessStartTicks(pid) == startedAt;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Файл занят — не критично.
        }
    }
}
