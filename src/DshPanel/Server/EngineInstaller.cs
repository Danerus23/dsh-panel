using System.Diagnostics;

namespace DshPanel.Server;

/// <summary>Чем кончилась установка движка.</summary>
public enum EngineInstallOutcome
{
    /// <summary>Поставлено и сверено с проверенным набором.</summary>
    Installed,

    /// <summary>Ставить нечего или нельзя — причина названа ключом.</summary>
    Refused,

    /// <summary>npm отказал (код возврата не 0).</summary>
    NpmFailed,

    /// <summary>Установка не уложилась в срок и снята вместе с потомками.</summary>
    TooLong,

    /// <summary>Поставленное дерево разошлось с проверенным набором — установка не принята.</summary>
    TreeWrong,

    /// <summary>Место установки не подготовилось (нет прав, занято, нет диска).</summary>
    NoPlace,
}

/// <summary>Итог установки: что решили, где, чем кончилось и что в дереве.</summary>
public sealed record EngineInstallResult(
    EngineInstallOutcome Outcome,
    string Version,
    string InstallDir,
    string RefusalKey,
    string Detail,
    EngineTreeReport? Tree,
    bool RolledBack = false,
    string Warning = "",
    IReadOnlyList<string>? Removed = null)
{
    /// <summary>Ставить можно и поставлено.</summary>
    public bool Ok => Outcome == EngineInstallOutcome.Installed;

    /// <summary>Отказ, а не поломка: панель не стала ставить и сказала почему.</summary>
    public bool Refusal => !Ok;

    /// <summary>Убранные версии: сколько их было (пусто — убирать было нечего).</summary>
    public int RemovedCount => Removed?.Count ?? 0;
}

/// <summary>Чем кончился запуск npm: код, вывод (уже обрезанный) и признак «снят по сроку».</summary>
public sealed record EngineNpmRun(int Code, string Output, bool TimedOut);

/// <summary>
/// ЗАПУСК npm — ОДНА ДВЕРЬ (этап E3.1).
///
/// **Почему не «npm, который в PATH».** Панель знает путь к Node (им поднимается движок), а npm
/// лежит **рядом** с ним: `<папка Node>\npm.cmd`. Спрашивать PATH значило бы поставить движок тем
/// npm, который человек, возможно, и не звал. Если рядом npm нет — ищем в PATH и называем это
/// словами; не нашли — отказ, а не «попробуем как-нибудь».
///
/// ⚠️ **Срок обязателен, и процесс снимается вместе с потомками.** Установка 588 пакетов идёт
/// 48–87 с замером, но на медленной сети — минуты; зависший npm оставил бы за собой живой узел,
/// которого никто не ждёт.
/// </summary>
public static class EngineNpm
{
    /// <summary>
    /// Доводы запуска. `ci` — установка СТРОГО по замку (npm проверяет суммы вложений и падает,
    /// если замок и `package.json` разошлись); `--no-audit` и `--no-fund` убирают лишние походы
    /// в сеть и лишние строки вывода.
    ///
    /// ⚠️ **`--ignore-scripts` — из ЖИВОГО ПРОГОНА В ВМ (10.10.2026), и это не мелочь.**
    /// У пяти пакетов замка есть установочные скрипты (`koffi`, `node-pty`, `protobufjs`,
    /// `@google/genai`, `@deepseek-ai/dsh-subprocess-local`). На машине владельца их **блокировал
    /// npm 12** (своя политика `allowScripts`), и проба движка была зелёной — 84/84 имён, 8/8 умений.
    /// В чистой ВМ те же скрипты **запустились**, и `koffi` упал: его скрипт зовёт `node`, а `node`
    /// не виден в PATH — панель поставила СВОЙ Node и зовёт его полным путём. Установка кончалась
    /// отказом npm, и это не имело отношения ни к замку, ни к суммам.
    /// Поэтому скрипты не запускаются вовсе — ровно то состояние, на котором дерево мерили.
    /// </summary>
    public const string Arguments = "ci --ignore-scripts --no-audit --no-fund";

    /// <summary>
    /// Сколько строк вывода npm оставляем в отчёте. Было 3 — и живого прогона в ВМ не хватило,
    /// чтобы понять причину: хвост заняли строки-извещения npm, а сама ошибка осталась выше.
    /// Шесть строк — по-прежнему «хвост», но причина в него попадает.
    /// </summary>
    public const int TailLines = 6;

    /// <summary>
    /// PATH ДЛЯ npm: ПЕРВЫМ ИДЁТ ПАПКА NODE, которым панель ставит движок. Иначе `node` для
    /// дочерних процессов npm — это «не является внутренней или внешней командой»: у человека
    /// без системного Node панель ставит свой и зовёт его полным путём, а PATH о нём не знает.
    /// Замер: именно на этом упал `koffi` в чистой ВМ.
    /// </summary>
    public static string PathWith(string? nodePath, string? existing)
    {
        var current = existing ?? string.Empty;
        var directory = string.IsNullOrWhiteSpace(nodePath) ? string.Empty : Path.GetDirectoryName(nodePath.Trim()) ?? string.Empty;

        if (directory.Length == 0) return current;
        if (current.Length == 0) return directory;

        return directory + Path.PathSeparator + current;
    }

    /// <summary>Путь к npm: сначала рядом с Node, потом в PATH. Пусто — не нашли.</summary>
    public static string PathNear(string? nodePath)
    {
        if (!string.IsNullOrWhiteSpace(nodePath))
        {
            try
            {
                var directory = Path.GetDirectoryName(nodePath.Trim());
                if (!string.IsNullOrEmpty(directory))
                {
                    foreach (var name in new[] { "npm.cmd", "npm.exe", "npm" })
                    {
                        var candidate = Path.Combine(directory, name);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch
            {
                // Путь к Node из настроек может быть мусором — тогда ищем в PATH.
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var name in new[] { "npm.cmd", "npm.exe" })
                {
                    var candidate = Path.Combine(part.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // Кривой кусок PATH — не повод не искать дальше.
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// ЗАПУСТИТЬ npm В КАТАЛОГЕ УСТАНОВКИ. `.cmd` запускается через `cmd.exe` (Windows не умеет
    /// запускать командные файлы напрямую без оболочки). Вывод читается целиком, но в отчёт уходит
    /// только хвост: человеку нужна последняя сказанная строка, а не весь лог npm.
    ///
    /// <paramref name="nodePath"/> — Node, которым панель ставит движок: его папка идёт ПЕРВОЙ
    /// в PATH дочернего процесса (<see cref="PathWith"/>). Без этого дочерние процессы npm
    /// не находят `node` — и именно так в чистой ВМ упал `koffi`.
    ///
    /// Полный вывод npm кладётся рядом (`npm-output.txt` в каталоге установки): хвост отвечает
    /// «чем кончилось», а причину иногда видно только выше по логу — и она должна быть человеку
    /// доступна, а не потеряна.
    /// </summary>
    public static EngineNpmRun Run(string npmPath, string workDir, int timeoutMs, string? nodePath = null)
    {
        try
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };

            start.Environment["PATH"] = PathWith(nodePath, start.Environment.TryGetValue("PATH", out var existing)
                ? existing
                : Environment.GetEnvironmentVariable("PATH"));

            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(npmPath);
            foreach (var argument in Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null) return new EngineNpmRun(-1, string.Empty, false);

            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            var whole = output + "\n" + errors;

            Save(workDir, whole);

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                return new EngineNpmRun(-1, EngineLog.Redact(Tail(whole)), true);
            }

            return new EngineNpmRun(process.ExitCode, EngineLog.Redact(Tail(whole)), false);
        }
        catch (Exception error)
        {
            return new EngineNpmRun(-1, EngineLog.Redact(error.Message), false);
        }
    }

    /// <summary>Полный вывод npm — рядом с установкой, чтобы причину можно было прочитать целиком.</summary>
    private static void Save(string workDir, string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(workDir, "npm-output.txt"), text, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Не сохранилось — это не повод считать установку неудавшейся: хвост всё равно уйдёт.
        }
    }

    /// <summary>Хвост вывода: последние непустые строки, склеенные через `; `.</summary>
    public static string Tail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;

        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        return string.Join("; ", lines.TakeLast(TailLines));
    }
}

/// <summary>
/// ДВЕРИ УСТАНОВКИ — то, что установщик делает с МИРОМ (спрашивает версию Node, запускает npm,
/// читает дерево). Вынесены доводами ради одной причины: без них установку нельзя проверить
/// подставными ответами, а «проверим живьём» на машине человека — не проверка.
/// </summary>
public sealed record EngineInstallDoors(
    Func<string, string> NodeVersion,
    Func<string, string, int, string?, EngineNpmRun> RunNpm,
    Func<string, EngineTreeDisk.Read> ReadTree)
{
    /// <summary>Настоящие двери: запускают настоящий Node и настоящий npm, читают настоящий диск.</summary>
    public static EngineInstallDoors Real { get; } = new(
        nodePath => Platform.VersionProbe.Run(nodePath, "--version"),
        (npmPath, workDir, timeoutMs, nodePath) => EngineNpm.Run(npmPath, workDir, timeoutMs, nodePath),
        installDir => EngineTreeDisk.From(installDir));
}

/// <summary>
/// УСТАНОВКА СВОЕГО ДВИЖКА ПО ЗАМКУ (этап E3.1, шаг 2 владельца).
///
/// **Порядок — не украшение, а сам смысл.** Сначала решается, ЧТО ставить (<see cref="EngineInstallDecisions"/>:
/// версия из подсказок, сверенная с замком), потом проверяется Node (нужен 20 или новее), потом
/// в папку версии кладутся `package-lock.json` (раздел `lock` замка КАК ЕСТЬ) и `package.json`
/// (из его корневой записи), и только потом зовётся `npm ci`: суммы вложений проверяет он.
/// Последним шагом панель сверяет **состав и версии** поставленного дерева с замком — и если
/// разошлось, установка **не принята**, а расхождение названо поимённо.
///
/// **Чем это кончается у человека.** Ставить по замку без Node и без npm нечем: оба случая —
/// отказ словами, а не молчаливая попытка. В сеть идёт только npm, и только за вложениями,
/// перечисленными в замке: адреса сверены с официальным реестром заранее.
/// </summary>
public static class EngineInstaller
{
    /// <summary>
    /// Срок установки. Замер: 48–87 с на нашем канале (25 536 файлов, 213,6 МБ), у `0.2.0-rc.2` —
    /// 56–87 с; срок взят с большим запасом на медную сеть, но конечный.
    /// </summary>
    public const int TimeoutMs = 20 * 60 * 1000;

    /// <summary>Сколько расхождений называем словами — остальные уходят числами.</summary>
    public const int NamedProblems = 3;

    /// <summary>Установить движок настоящими дверями.</summary>
    public static EngineInstallResult Install(
        EngineTreeLock? engineLock,
        EngineHints? hints,
        string? engineDir,
        string? nodePath,
        string? npmPath = null) =>
        Install(engineLock, hints, engineDir, nodePath, npmPath, EngineInstallDoors.Real, null);

    /// <summary>
    /// Установить движок с названными дверями и рассказом о ходе (`progress` — строка в журнал
    /// на каждом шаге: человек должен видеть, на чём остановилось, а не «поставляю…» вечно).
    ///
    /// <param name="ownNode"/> зовётся ТОЛЬКО когда системный Node не годится (нет или старше
    /// порога): он добывает свой — скачанный с официального источника и сверенный. `null` значит
    /// «своего Node нет» — тогда панель отказывает словами, как раньше.</param>
    /// <param name="recordPath">
    /// Файл записи «что стоит сейчас» (<see cref="Isolation.AppPaths.EngineStateFile"/>). Пусто —
    /// установка без записи (так её зовут проверки подставными дверями): тогда и уборка прежней
    /// версии не делается — она опирается на запись, а не на догадку.
    /// </param>
    public static EngineInstallResult Install(
        EngineTreeLock? engineLock,
        EngineHints? hints,
        string? engineDir,
        string? nodePath,
        string? npmPath,
        EngineInstallDoors doors,
        Action<string>? progress,
        Func<EngineNodeResult>? ownNode = null,
        string? recordPath = null)
    {
        ArgumentNullException.ThrowIfNull(doors);

        if (engineLock is null) return Refuse(EngineInstallOutcome.Refused, EngineInstallRefusals.NoLock, string.Empty);

        var plan = EngineInstallDecisions.Decide(engineLock, hints, engineDir);

        if (!plan.Any) return Refuse(EngineInstallOutcome.Refused, plan.RefusalKey, plan.Version);

        if (string.IsNullOrWhiteSpace(engineDir))
            return Refuse(EngineInstallOutcome.Refused, EngineInstallRefusals.NoPlace, plan.Version);

        // ⚠️ Node проверяется ДО всего остального: без него npm не запустится, и «ставить» значило бы
        // завести папку и признаться в беде после. Проверяется ВЕРСИЯ, а не наличие (решение
        // владельца): системный годится, если он 20 или новее; иначе панель берёт СВОЙ Node —
        // скачанный с официального источника и сверенный (`ownNode`), — и только если и его нет,
        // отказывает словами.
        var node = (nodePath ?? string.Empty).Trim();
        var nodeVersion = (doors.NodeVersion(node) ?? string.Empty).Trim();

        // Чем поставили — запоминается для записи «что стоит сейчас»: системный Node в описи
        // не появляется НИКОГДА (он не наш), а свой — появляется вместе с версией и путём.
        var usedNode = new EngineNodeResult(
            EngineNodeOutcome.SystemSuits, node, nodeVersion, string.Empty, string.Empty, Ours: false);

        if (!EngineNode.Fits(nodeVersion, engineLock))
        {
            var own = ownNode?.Invoke();

            if (own is not null && own.Ok)
            {
                node = own.NodePath;
                nodeVersion = own.Version;
                usedNode = own;

                if (own.Ours) progress?.Invoke(EngineInstallPhrases.NodeReady(own.Version, own.NodePath));
            }
            else if (own is not null && !own.Ok && own.Detail.Length > 0)
            {
                // Свой Node не добылся — и это НЕ «нет Node»: причина у своего, и она называется.
                return Refuse(EngineInstallOutcome.Refused, own.RefusalKey, plan.Version, own.Detail);
            }
            else if (nodeVersion.Length == 0)
            {
                return Refuse(EngineInstallOutcome.Refused, EngineInstallRefusals.NodeMissing, plan.Version,
                    EngineInstallPhrases.NodeMissing());
            }
            else
            {
                return Refuse(EngineInstallOutcome.Refused, EngineInstallRefusals.NodeOld, plan.Version,
                    EngineInstallPhrases.NodeOld(nodeVersion, EngineNode.RequiredFrom(engineLock)));
            }
        }

        // Пустой путь к npm — это «не нашли» (так проверяется отказ), а `null` — «поищи сам».
        var tool = npmPath is null ? EngineNpm.PathNear(node) : npmPath.Trim();

        if (tool.Length == 0)
            return Refuse(EngineInstallOutcome.Refused, EngineInstallRefusals.NpmMissing, plan.Version,
                EngineInstallPhrases.NpmMissing());

        // Папка ВЕРСИИ — одна дверь (`OwnEngine.VersionDir`): две версии не смешиваются,
        // и путь установки совпадает с тем, откуда панель потом поднимает сервер.
        var installDir = OwnEngine.VersionDir(engineDir, plan.Version);

        // ⚠️ ПРЕЖНЯЯ ВЕРСИЯ И СПИСОК ПОСТАВЛЕННОГО ЧИТАЮТСЯ ДО УСТАНОВКИ: после неё запись
        // «что стоит сейчас» сменится, и откату было бы некуда возвращаться, а уборке — нечего
        // сверять (она трогает ТОЛЬКО то, что ставила панель).
        var before = EngineInstallRecord.Read(recordPath);
        var previous = before.Previous;

        // ⚠️ БЫЛА ЛИ ПАПКА ДО НАС — это решает, что делает откат. Если папка уже стояла (человек
        // ставит ту же версию повторно, или панель переставляет её), неудача НЕ даёт права её
        // сносить: там лежит рабочая установка, и «откат» стёр бы именно её. Убирается только то,
        // что создано ЭТОЙ попыткой.
        var existedBefore = Directory.Exists(installDir);

        try
        {
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(installDir, "package-lock.json"), engineLock.NpmLockJson, Utf8);
            File.WriteAllText(Path.Combine(installDir, "package.json"), engineLock.PackageJson, Utf8);
        }
        catch (Exception error)
        {
            return Refuse(EngineInstallOutcome.NoPlace, EngineInstallRefusals.NoPlace, plan.Version,
                EngineInstallPhrases.NoPlace(installDir, error.Message));
        }

        progress?.Invoke(EngineInstallPhrases.Started(plan.Version, installDir));

        var run = doors.RunNpm(tool, installDir, TimeoutMs, node);

        if (run.TimedOut)
            return Rolled(EngineInstallOutcome.TooLong, EngineInstallRefusals.TooLong, plan.Version, installDir, existedBefore,
                EngineInstallPhrases.TooLong(TimeoutMs / 60000));

        if (run.Code != 0)
            return Rolled(EngineInstallOutcome.NpmFailed, EngineInstallRefusals.NpmFailed, plan.Version, installDir, existedBefore,
                EngineInstallPhrases.NpmFailed(run.Code, run.Output));

        var read = doors.ReadTree(installDir);
        var tree = EngineTreeCheck.Against(engineLock, read.Paths, read.VersionOf);

        if (!tree.Ok)
        {
            var rolled = Rolled(EngineInstallOutcome.TreeWrong, EngineInstallRefusals.TreeWrong, plan.Version,
                installDir, existedBefore, EngineInstallPhrases.TreeProblems(tree));

            return rolled with { Tree = tree };
        }

        // --- УДАЧА: запись «что стоит сейчас», затем уборка версий, которые больше не нужны ----
        var kept = new List<string>();

        if (recordPath is not null && recordPath.Length > 0)
        {
            var record = EngineInstallRecord.After(plan.Version, installDir, usedNode, before);

            if (!record.Write(recordPath, out var why))
            {
                // Установка состоялась, а запись — нет: говорим словами, но не притворяемся,
                // что всё хорошо. Откат в таком случае просто некуда делать, и это названо.
                progress?.Invoke(EngineInstallPhrases.RecordFailed(why));
            }
        }

        // Держим ТЕКУЩУЮ и ПРЕЖНЮЮ (решение владельца): убираем остальное — и только СВОИ версии,
        // те, что панель ставила сама (чужую папку она не трогает: про неё не знает).
        var extra = EngineRetention.ToRemove(plan.Version, previous, before.Known);
        kept.AddRange(EngineRetention.Remove(engineDir, extra));

        if (kept.Count > 0) progress?.Invoke(EngineInstallPhrases.Removed(kept.Count));

        return new EngineInstallResult(
            EngineInstallOutcome.Installed,
            plan.Version,
            installDir,
            string.Empty,
            EngineInstallPhrases.Numbers(tree),
            tree,
            Removed: kept);
    }

    /// <summary>
    /// УБРАТЬ НЕУДАВШУЮСЯ ПОПЫТКУ И СКАЗАТЬ ОБ ЭТОМ СЛОВАМИ. Папка версии удаляется помощником
    /// (<see cref="EngineRetention"/> → <see cref="Files.TreeDeletion"/>): половина дерева на диске
    /// хуже, чем её отсутствие, — иначе следующая попытка ставила бы «поверх» и считала бы обломки
    /// частью установки. Запись «что стоит сейчас» при этом НЕ трогается: панель остаётся на прежней
    /// версии, и предупреждение об этом — в <see cref="EngineInstallResult.Warning"/>.
    ///
    /// ⚠️ <paramref name="existedBefore"/> — предохранитель, а не подробность: если папка версии
    /// **уже стояла** до этой попытки, убирать её нельзя. Там рабочая установка, и «откат» стёр бы
    /// именно её — то есть сделал бы ровно то, от чего откат и заведён.
    /// </summary>
    private static EngineInstallResult Rolled(
        EngineInstallOutcome outcome,
        string refusalKey,
        string version,
        string installDir,
        bool existedBefore,
        string detail)
    {
        if (!existedBefore)
            EngineRetention.Remove(Path.GetDirectoryName(installDir), new[] { Path.GetFileName(installDir) });

        var rolled = !existedBefore && !Directory.Exists(installDir);

        return new EngineInstallResult(
            outcome,
            version,
            installDir,
            refusalKey,
            detail,
            null,
            RolledBack: rolled,
            Warning: EngineInstallPhrases.RolledBack(version));
    }

    private static EngineInstallResult Refuse(EngineInstallOutcome outcome, string refusalKey, string version, string detail = "") =>
        new(outcome, version, string.Empty, refusalKey, detail, null);

    private static readonly System.Text.UTF8Encoding Utf8 = new(false);
}
