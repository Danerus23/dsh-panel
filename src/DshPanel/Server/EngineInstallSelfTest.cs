using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПРОБА УСТАНОВКИ ДВИЖКА ПО ЗАМКУ — на собранной панели, в изоляции (этап E3.1, шаг 2 владельца).
///
/// **Зачем отдельная проба.** Установка — единственное место в панели, которое **ходит в сеть
/// и запускает чужую программу** (npm). Проверить это подставными дверями можно и нужно
/// (`EngineInstallerTests`), но подставные двери не доказывают, что НАСТОЯЩИЙ npm по НАШЕМУ замку
/// соберёт НАСТОЯЩЕЕ дерево: ровно на этом панель и попалась — замер 10.10.2026 показал, что по
/// дайджесту первой редакции `npm ci` ставит 1 пакет вместо 588.
///
/// **Что делает проба.** Ставит движок по встроенному замку в СВОЮ папку под корнем прогона
/// (`&lt;корень&gt;\engine\&lt;версия&gt;`), читает подсказки тем же клиентом, что панель, —
/// и затем **прогоняет на поставленном движке полную пробу сервера** (`--server-selftest`:
/// 84 имени поверхности вызовов и 8 умений панели). Это и есть требование владельца
/// «после установки — проба умений».
///
/// ⚠️ **Без изоляции отказывается сама** (код 2): установка пишет сотни мегабайт и заводит дерево,
/// а прогон без изоляции завёл бы его рядом с данными человека. Порт владельца при этом не
/// занимается: проба сервера берёт свободный.
/// </summary>
public static class EngineInstallSelfTest
{
    /// <summary>Срок на установку и пробу. Установка замером — 48–87 с, проба сервера — до 240 с.</summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromMinutes(20);

    public static int Run(TimeSpan timeout) => Run(timeout, planted: false);

    /// <summary>
    /// То же, но с ПОДЛОЖЕННОЙ БЕДОЙ (<paramref name="planted"/>): установка зовётся дверью,
    /// которая заведомо отказывает (npm «не сошлась сумма»), и проверяется ОТКАТ — папка версии
    /// убрана, запись «что стоит сейчас» не тронута, панель осталась на прежней версии и сказала
    /// предупреждение про сессии словами.
    ///
    /// ⚠️ Беда подкладывается **в отдельную папку** (`engine-beda`), а не в рабочую: иначе неудача
    /// стёрла бы только что поставленный движок, и проба доказывала бы не откат, а свою же ошибку.
    /// </summary>
    public static int Run(TimeSpan timeout, bool planted)
    {
        var context = RunContext.Current;

        if (!context.IsIsolated)
        {
            Console.WriteLine(
                "ДВИЖОК ПРОВАЛ: проба ставит настоящий движок (сотни мегабайт) и поднимает его. " +
                "Без изоляции дерево завелось бы рядом с данными человека. " +
                "Запустите с «--run-root <свой путь>».");
            return 2;
        }

        var report = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        // ЗАМОК ЧИТАЕТСЯ ПЕРВЫМ: из него берётся ПОРОГ версии Node (решение владельца `7265d9d`),
        // и решать «годится ли Node» без замка нельзя — тогда осталась бы только нижняя граница 20.
        var lockFile = EngineTreeLock.Load();

        // NODE: на чистой машине его может не быть вовсе, а может стоять старый — и тогда панель
        // добывает СВОЙ (скачанный с официального источника и сверенный). Это и есть первый шаг
        // цепочки, которую проверяет прогон в ВМ, поэтому проба идёт ТЕМ ЖЕ путём, что панель.
        var systemNode = DshEngine.FindNode();
        var systemVersion = Platform.VersionProbe.Run(systemNode, "--version");

        report.Add($"системный Node: {(string.IsNullOrWhiteSpace(systemNode) ? "не найден" : systemNode + " (" + systemVersion + ")")}");

        var node = EngineNodeInstall.Ensure(
            context.Paths.NodeDir,
            systemNode,
            systemVersion,
            lockFile,
            EngineNodeDoors.Real(new Update.HttpUpdateDownload()),
            report.Add);

        Check("Node добыт (системный годится или скачан свой)", node.Ok);

        if (!node.Ok)
        {
            report.Add($"свой Node не добылся: {node.Detail}");
            Print(report);
            Console.WriteLine("ДВИЖОК ПРОВАЛ");
            return 1;
        }

        report.Add(node.Ours
            ? $"свой Node: {node.NodePath} (версия {node.Version})"
            : $"системный Node: {node.NodePath} (версия {node.Version})");

        var engineDir = Path.Combine(context.Paths.Root, "engine");
        report.Add($"папка движка прогона: {engineDir}");

        // Подсказки читаются ТЕМ ЖЕ клиентом, что у панели: версия для установки берётся из них,
        // и подменить её в пробе значило бы проверять не тот путь, которым идёт человек.
        var fetch = new HttpEngineHintsClient(ProductLinks.EngineHintsUrl).Read();
        var hints = fetch.Ok ? EngineHints.Parse(fetch.Json) : EngineHints.Empty;

        report.Add(fetch.Ok
            ? $"подсказки прочитаны: записей {hints.Versions.Count}"
            : $"подсказки НЕ прочитаны: {fetch.Detail}");

        Check("встроенный замок читается", lockFile.Any);
        Check("замок цел (записей столько, сколько объявлено)", lockFile.Complete);
        Check("у каждого пакета замка есть сумма", lockFile.FullySummed);
        Check("все адреса замка — с официального реестра", lockFile.AllFromRegistry);

        if (!lockFile.Any)
        {
            Print(report);
            Console.WriteLine("ДВИЖОК ПРОВАЛ");
            return 1;
        }

        var steps = new List<string>();
        var startedAt = System.Diagnostics.Stopwatch.StartNew();

        // ПОДЛОЖЕННАЯ БЕДА идёт В СВОЮ папку: иначе неудача стёрла бы только что поставленный
        // движок, и проба доказывала бы собственную ошибку, а не откат.
        if (planted)
        {
            var bedaDir = Path.Combine(context.Paths.Root, "engine-beda");
            report.Add($"подложенная беда: папка {bedaDir}, npm заведомо отказывает");

            var beda = EngineInstaller.Install(
                lockFile, hints, bedaDir, systemNode, null,
                // ⚠️ Подставная дверь отвечает про КОНКРЕТНЫЙ путь: пустой путь — «версии не знаю»,
                // и установщик тогда идёт за своим Node. Первая редакция пробы отвечала «годится»
                // на любой путь — и беда подкладывалась не туда: установка отказывала раньше npm
                // («нет npm»), а откат не проверялся вовсе.
                new EngineInstallDoors(
                    path => string.Equals(path, node.NodePath, StringComparison.OrdinalIgnoreCase) ? node.Version : string.Empty,
                    (_, _, _, _) => new EngineNpmRun(1, "подложенная беда: отказ npm", false),
                    installDir => EngineTreeDisk.From(installDir)),
                steps.Add, () => node, context.Paths.EngineStateFile);

            foreach (var step in steps) report.Add("ход| " + step);
            steps.Clear();

            report.Add($"итог подложенной беды: {beda.Outcome}; отказ {beda.RefusalKey}; деталь: {beda.Detail}");
            report.Add($"откат: {beda.RolledBack}; слова: {beda.Warning}");

            Check("подложенная беда не прошла за удачу", !beda.Ok);
            Check("папка неудавшейся версии убрана (откат)", beda.RolledBack);
            Check("папки подложенной беды больше нет", !Directory.Exists(OwnEngine.VersionDir(bedaDir, beda.Version)));
            Check("предупреждение про сессии сказано словами", beda.Warning.Contains("журнал новее", StringComparison.Ordinal));

            // И главное: рабочая установка и запись о ней ЦЕЛЫ.
            var afterBeda = EngineInstallRecord.Read(context.Paths.EngineStateFile);

            Check("запись «что стоит сейчас» не тронута откатом", afterBeda.EngineVersion == EngineRpcSurface.MeasuredOnVersion);
            Check("прежняя (рабочая) папка движка цела", Directory.Exists(OwnEngine.VersionDir(engineDir, EngineRpcSurface.MeasuredOnVersion)));

            // Живую пробу сервера на подложенной беде не гоняем: движка там уже нет.
            Print(report);

            if (failed)
            {
                Console.WriteLine("ДВИЖОК ПРОВАЛ");
                return 1;
            }

            Console.WriteLine("ДВИЖОК УСПЕХ");
            return 0;
        }

        // ⚠️ СИСТЕМНЫЙ путь идёт доводом, а `ownNode` — тем же путём, что у панели: если системный
        // Node не годится, установщик зовёт его сам и берёт уже добытый свой. Так проба проверяет
        // РЕШЕНИЕ установщика, а не обходит его.
        var result = EngineInstaller.Install(
            lockFile, hints, engineDir, systemNode, null, EngineInstallDoors.Real, steps.Add, () => node,
            context.Paths.EngineStateFile);

        startedAt.Stop();

        foreach (var step in steps) report.Add("ход| " + step);

        report.Add($"итог установки: {result.Outcome}; время {startedAt.Elapsed.TotalSeconds:F1} с");
        if (result.Detail.Length > 0) report.Add($"слова установки: {result.Detail}");

        Check("движок поставлен по замку", result.Ok);

        var bin = OwnEngine.BinPath(engineDir, result.Version);

        Check("bin.js поставленного движка на месте", File.Exists(bin));
        Check("панель считает этот путь своим", OwnEngine.IsOwnBinPath(bin, engineDir));
        Check("версия пакета совпала с плановой", OwnEngine.VersionOf(bin) == result.Version);

        // --- проба умений ПОСЛЕ установки: полная проба сервера на поставленном движке ---------
        if (result.Ok)
        {
            report.Add("проба на поставленном движке — той же проверкой, что «--server-selftest»");

            // ⚠️ СТАВИМ ОБА УКАЗАТЕЛЯ: на чистой машине системного Node нет вовсе, и проба
            // сервера искала бы движок по общим местам — а он лежит в НАШЕЙ папке под корнем
            // прогона. Без `DSH_PANEL_NODE` она честно скажет «запускать нечего» (так и вышло
            // в первом живом прогоне 10.10.2026).
            var previousNode = Environment.GetEnvironmentVariable(DshEngine.NodeOverrideVariable);
            var previous = Environment.GetEnvironmentVariable(DshEngine.EngineOverrideVariable);
            Environment.SetEnvironmentVariable(DshEngine.NodeOverrideVariable, node.NodePath);
            Environment.SetEnvironmentVariable(DshEngine.EngineOverrideVariable, bin);

            int probe;

            try
            {
                probe = ServerSelfTest.Run(TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 5, 1800)));
            }
            finally
            {
                Environment.SetEnvironmentVariable(DshEngine.EngineOverrideVariable, previous);
                Environment.SetEnvironmentVariable(DshEngine.NodeOverrideVariable, previousNode);
            }

            Check("проба на поставленном движке прошла (нужные имена 77, 8 умений)", probe == 0);
        }

        Print(report);

        if (failed)
        {
            Console.WriteLine("ДВИЖОК ПРОВАЛ");
            return 1;
        }

        Console.WriteLine("ДВИЖОК УСПЕХ");
        return 0;
    }

    private static void Print(IReadOnlyList<string> report)
    {
        foreach (var line in report) Console.WriteLine("ДВИЖОК| " + line);
    }
}
