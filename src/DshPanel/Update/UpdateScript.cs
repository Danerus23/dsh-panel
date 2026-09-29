using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// СЦЕНАРИЙ ЗАМЕНЫ ФАЙЛОВ — <c>update.cmd</c>. Он только СОЧИНЯЕТСЯ здесь: ни один прогон
/// проверки его не запускает (это замена файлов у человека, а не проверка), и ни один прогон
/// не пишет в каталоги владельца.
///
/// **Четыре урока панели 1.x, и каждый стоил человеку сломанного обновления.**
///
/// 1. **Тело — только ASCII, данные приходят аргументами** (<c>%~1…%~7</c>). cmd.exe читает
///    <c>.cmd</c> в кодировке консоли (на русской Windows — cp866), и не-ASCII путь, вписанный
///    в тело, превращается в несуществующий: панель закрывалась, а обновление молча не применялось.
/// 2. **Хвостовой разделитель срезается** (<see cref="SafePath"/>). Панель у человека лежит
///    в <c>%LOCALAPPDATA%\Programs\DSH Panel</c>, и путь, ушедший в кавычках с хвостовым «\»,
///    съедает закрывающую кавычку: robocopy получает ключи как часть пути и отказывается копировать.
/// 3. **Пути не попадают ни в строки <c>echo</c>, ни в текст команды PowerShell.** «&amp;», «(», «)»
///    и «%» внутри <c>echo</c> рвут строку на несколько команд, а у <c>powershell -Command "…"</c>
///    значение дописывается к самой команде. Поэтому значения уходят в PowerShell через окружение
///    (<c>set "DSH_UPD_…"</c>), а шапку журнала пишет сама панель (<see cref="LogHeader"/>).
/// 4. **Панель поднимается только там, где файлы на месте** — после удачной замены или после
///    удачного отката. Если панель не вышла вовремя, мьютекс занят, его не удалось проверить,
///    откат не удался или страховочной копии нет — не запускается НИЧЕГО: полузаменённую папку
///    поднимать нельзя, а лишняя копия — это второй значок в трее.
///
/// В каждой ветке последней в журнал идёт строка итога (<see cref="UpdateOutcomes.ResultMarkerPrefix"/>),
/// и это ЕДИНСТВЕННЫЙ источник правды об итоге: код возврата сценария признаком успеха не является
/// (robocopy отвечает нулём и на «нечего копировать»). Список итогов живёт в
/// <see cref="UpdateOutcomes.Marker"/> — здесь он только подставляется, второй таблицы нет.
/// </summary>
public static class UpdateScript
{
    /// <summary>Имя сценария внутри каталога обновления.</summary>
    public const string FileName = "update.cmd";

    /// <summary>Имя журнала замены. Сценарий в него ДОПИСЫВАЕТ, шапку пишет панель.</summary>
    public const string LogFileName = "update.log";

    /// <summary>
    /// Путь без хвостового разделителя — тот вид, в каком путь подставляется в кавычках.
    ///
    /// «C:\…\DSH Panel\» ломает разбор: cmd считает «\"» экранированной кавычкой и передаёт
    /// robocopy остаток строки вместе с путём (ошибка 123, замена не состоялась, панель
    /// вернулась прежней версии). Корень диска («C:\») остаётся как есть — иначе от него
    /// не остаётся ничего.
    /// </summary>
    public static string SafePath(string? path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');

        return trimmed.EndsWith(':') ? trimmed + "\\" : trimmed;
    }

    /// <summary>
    /// Шапка журнала: версия, папка панели, папка сборки и режим запуска. Пишет её САМА панель,
    /// а не сценарий, и по той же причине, что и всё остальное: путь с «&amp;», «(», «)» или «%»
    /// внутри строки <c>echo</c> разбирается cmd как несколько команд, и в журнал попадала бы
    /// обрезанная строка с ошибкой вместо пути. Здесь путь ложится в файл как есть.
    /// </summary>
    public static string LogHeader(string target, string staged, string version, string startArgument, DateTimeOffset now)
    {
        var header = new System.Text.StringBuilder();

        header.AppendLine("update started " + now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        header.AppendLine("version: " + version);
        header.AppendLine("target: " + target);
        header.AppendLine("staged: " + staged);
        header.AppendLine("start: " + (startArgument.Length > 0 ? startArgument : "plain"));

        return header.ToString();
    }

    /// <summary>
    /// Тело сценария одной строкой (переводы строк — CRLF: так его читает cmd). Отдельной чистой
    /// функцией, а не «написать и посмотреть»: текст сценария — единственное, что здесь можно
    /// проверить прогоном, и проверки читают ИМЕННО его.
    /// </summary>
    /// <param name="version">
    /// Версия, которую обещал выпуск (числовая часть тега). Одна на весь сценарий: та же строка
    /// уходит и в шапку журнала (<see cref="LogHeader"/>), и в проверку скопированной панели.
    /// Пустая (или без ключа) значит «сверять не с чем» — тогда проверка копии пропускается,
    /// а не сравнивает файл с пустой строкой и не валит исправное обновление.
    /// </param>
    public static string Text(string version = "") => string.Join("\r\n", Lines(version)) + "\r\n";

    /// <summary>Строки сценария — по одной, чтобы проверкам было что читать построчно.</summary>
    public static IReadOnlyList<string> Lines(string version = "")
    {
        var exe = "%TARGET%\\" + UpdateStaging.PanelExeName;
        var process = UpdateStaging.PanelProcessName;
        var wanted = version.Trim();

        var lines = new List<string>
        {
            "@echo off",
            "rem DSH Panel 2.0 self-update. ASCII only: cmd reads this file in the OEM code page.",
            "rem Args: %~1 pid, %~2 target, %~3 staged, %~4 backup, %~5 log, %~6 mutex, %~7 start.",
            "rem The last two have defaults; the five paths must come from the printed command.",
            "setlocal EnableExtensions",
            "set \"PID=%~1\"",
            "set \"TARGET=%~2\"",
            "set \"STAGED=%~3\"",
            "set \"BACKUP=%~4\"",
            "set \"LOG=%~5\"",
            "set \"MUTEX=%~6\"",
            "set \"START=%~7\"",
            "if \"%MUTEX%\"==\"\" set \"MUTEX=" + InstanceSignal.DefaultMutexName + "\"",

            "rem Without the paths there is nothing to do: say so instead of copying into nowhere.",
            "if \"%TARGET%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex start & exit /b 1)",
            "if \"%STAGED%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex start & exit /b 1)",
            "if \"%LOG%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex start & exit /b 1)",

            "rem The header (version, target, staged, start) was written into the log by the panel:",
            "rem a path with &, ( ) or % inside a batch echo line breaks the line. Here we append.",
            "rem The promised version is the ONE thing the panel passes in twice: it is already in the",
            "rem header, and the copy is checked against it further down. Empty means the panel had no",
            "rem version to promise - the check is skipped rather than comparing against nothing.",
            "set \"VERSION=" + wanted + "\"",
            "",

            "rem Wait for the panel to exit: about two minutes at most. Nothing is copied while it is",
            "rem alive: a half-replaced folder cannot be started. The pause is a real one second",
            "rem (ping -n 2): ping -n 1 on loopback returns at once, and the wait would be seconds.",
            "for /L %%i in (1,1,120) do (",
            "  tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul || goto exited",
            "  ping -n 2 -w 1000 127.0.0.1 >nul",
            ")",
            Echo("warning: the panel with pid %PID% did not exit in time"),
            Echo("nothing was copied: replacing files of a running panel breaks it"),
            Result(UpdateResultKind.NotAppliedPanelRunning),
            "exit /b 1",
            "",

            ":exited",
            "",
            "rem The single-instance mutex must be free before the new copy starts: a copy started too",
            "rem early sees the mutex taken and exits at once, leaving the person without a panel.",
            "rem The check itself may fail (no PowerShell, broken name) - that is not \"busy\", and the",
            "rem log must say what really happened. Exit codes: 0 free, 1 busy, 2 could not check.",
            "rem The wait is about a minute: 60 checks with a real one second pause between them.",
            "rem The name goes through the environment: inside the -Command text an apostrophe or an",
            "rem ampersand would break the command, and PowerShell would report nonsense about it.",
            "set \"DSH_UPD_MUTEX=%MUTEX%\"",
            "for /L %%i in (1,1,60) do (",
            "  powershell -NoProfile -ExecutionPolicy Bypass -Command \"$n = $env:DSH_UPD_MUTEX; $m = $null; try { $m = [System.Threading.Mutex]::OpenExisting($n); try { $got = $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $got = $true }; if ($got) { exit 0 } else { exit 1 } } catch [System.Threading.WaitHandleCannotBeOpenedException] { exit 0 } catch { [Console]::Error.WriteLine('mutex check failed: ' + $_.Exception.Message); exit 2 } finally { if ($m -ne $null) { $m.Dispose() } }\" 2>> \"%LOG%\"",
            "  if errorlevel 2 goto mutex-unknown",
            "  if not errorlevel 1 goto free",
            "  ping -n 2 -w 1000 127.0.0.1 >nul",
            ")",
            Echo("warning: the single-instance mutex was still held after about a minute"),
            Echo("nothing was copied: another panel instance is running"),
            Result(UpdateResultKind.NotAppliedMutex),
            "exit /b 1",
            "",

            ":mutex-unknown",
            "rem The check did not work at all: do not copy (a live panel may be holding the mutex),",
            "rem but do not blame \"another instance\" either - the reason is written above.",
            Echo("warning: the single-instance mutex could not be checked: see the line above"),
            Echo("nothing was copied: replacing files of a possibly running panel breaks it"),
            Result(UpdateResultKind.MutexCheckFailed),
            "exit /b 1",
            "",

            ":free",
            "robocopy \"%STAGED%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "set \"RC=%ERRORLEVEL%\"",
            Echo("robocopy exit code: %RC%"),
            "if %RC% GEQ 8 goto rollback",
            Echo("files replaced"),
            "",
            "rem THE COPIED FILES MUST BE A WORKING PANEL OF THE PROMISED VERSION.",
            "rem robocopy's success code only says the bytes were copied - it says nothing about what",
            "rem they were. Found by the laboratory run 28.09.2026: a staged build that was not a panel",
            "rem at all was copied over the working folder, the script declared success and the panel",
            "rem never came up - the person was left without a panel. Nothing verified the copy before.",
            "rem The version is read from the copied exe AND compared with the one promised by the",
            "rem release: a different file that happens to carry a version is caught too. No version",
            "rem was promised - there is nothing to compare, and the copy goes on unchecked.",
            "if \"%VERSION%\"==\"\" goto start",
            "set \"DSH_UPD_PROC=%TARGET%\\" + UpdateStaging.PanelExeName + "\"",
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"$p = $env:DSH_UPD_PROC; $want = '%VERSION%'; if (-not (Test-Path -LiteralPath $p)) { exit 1 }; $got = ''; try { $got = (Get-Item -LiteralPath $p).VersionInfo.ProductVersion } catch { exit 1 }; if ([string]::IsNullOrWhiteSpace($got)) { exit 1 }; $numeric = ($got -split '\\+')[0].TrimStart('v','V'); if ($numeric -ine $want) { exit 1 }; exit 0\" 2>> \"%LOG%\"",
            "if errorlevel 1 goto copied-not-a-panel",
            "goto start",
            "",
            ":copied-not-a-panel",
            "rem The copy is not a panel of the promised version: put the previous one back instead of",
            "rem starting something that will not come up. The person keeps a working panel.",
            Echo("the copied files are NOT a panel of the promised version: nothing was started"),
            "goto rollback",
            "",

            ":rollback",
            "rem A rollback needs the whole previous version: a folder without the panel exe is not a",
            "rem safety copy, and restoring from it would mix versions in the panel folder.",
            "if not exist \"%BACKUP%\\" + UpdateStaging.PanelExeName + "\" goto no-backup",
            Echo("update failed, restoring the previous version"),
            "robocopy \"%BACKUP%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "if errorlevel 8 goto restore-failed",
            Result(UpdateResultKind.RolledBack),
            "goto failed",
            "",

            ":restore-failed",
            "rem The copy is there, but putting it back did not work: start nothing (the panel folder",
            "rem may be half replaced) and keep both the copy and the staged build for manual repair.",
            Echo("the rollback did not work: the previous version was NOT restored"),
            Echo("nothing was started: the panel folder may be half replaced"),
            Echo("the safety copy and the staged build were left in place"),
            Result(UpdateResultKind.FailedRestore),
            "exit /b 1",
            "",

            ":no-backup",
            "rem Nothing to roll back to: say so instead of pretending the rollback worked, and start",
            "rem nothing. The staged build stays where it is for manual repair.",
            Echo("no usable safety copy of the previous version: nothing to roll back to"),
            Echo("nothing was started: the panel folder may be half replaced"),
            Echo("the staged build was left in place"),
            Result(UpdateResultKind.FailedNoBackup),
            "exit /b 1",
            "",

            ":failed",
            "rem Only reached after a rollback that worked: the previous version is in place again, so",
            "rem the panel may be started.",
            Echo("the update was not applied, the previous version was restored"),
            "if not exist \"" + exe + "\" goto failed-no-exe",
            StartLine(exe),
            "exit /b 1",
            "",

            ":failed-no-exe",
            Echo("warning: there is no " + UpdateStaging.PanelExeName + " in the target folder, nothing was started"),
            "exit /b 1",
            "",

            ":start",
            "rem Files are replaced: start the panel the same way it was running before, so a person",
            "rem who lived in the tray does not get a full-screen window on top of their work.",
            StartLine(exe),
            "",

            "rem Make sure the panel that came up is the one from the target folder. The path goes",
            "rem through the environment, and PowerShell compares the process path itself: &, ( ) and",
            "rem % in it are harmless here, unlike in find/findstr, which split such a path apart.",
            "set \"DSH_UPD_PROC=" + exe + "\"",
            "powershell -NoProfile -Command \"$want = $env:DSH_UPD_PROC; for ($i = 0; $i -lt 20; $i++) { foreach ($p in @(Get-Process " + process + " -ErrorAction SilentlyContinue)) { $path = ''; try { $path = [string]$p.Path } catch { $path = '' }; if ($path -ieq $want) { exit 0 } }; Start-Sleep -Seconds 1 }; exit 1\" 2>> \"%LOG%\"",
            "if not errorlevel 1 set \"SEEN=1\"",
            "if defined SEEN (",
            "  " + Result(UpdateResultKind.Applied),
            "  " + Echo("the panel was restarted from the target folder"),
            ") else (",
            "  " + Echo("warning: files were replaced, but the panel from the target folder did not come up"),
            "  " + Result(UpdateResultKind.AppliedNoPanel),
            ")",
            "del \"%~f0\"",
        };

        return lines;
    }

    /// <summary>
    /// Строка запуска панели — одна на сценарий, и это не мелочь: она стоит в ДВУХ ветках
    /// (после удачной замены и после удачного отката), и разошедшиеся копии запускали бы панель
    /// по-разному. Режим запуска — ОДИН ключ из кода самой панели (пусто — обычный запуск окном):
    /// ничего, что набрал человек, на эту строку не попадает.
    /// </summary>
    private static string StartLine(string exe) =>
        "if \"%START%\"==\"\" (start \"\" \"" + exe + "\") else (start \"\" \"" + exe + "\" %START%)";

    /// <summary>Строка журнала, которую пишет сценарий: значение подставляется данными панели.</summary>
    private static string Echo(string text) => "echo " + text + " >> \"%LOG%\"";

    /// <summary>
    /// Строка итога. Текст итога берётся у <see cref="UpdateOutcomes.Marker"/>: список итогов
    /// ОДИН на панель, и сценарий, знающий свои итоги отдельно, разошёлся бы с читателем журнала.
    /// </summary>
    private static string Result(UpdateResultKind kind) =>
        Echo(UpdateOutcomes.ResultMarkerPrefix + UpdateOutcomes.Marker(kind));
}
