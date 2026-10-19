using DshPanel.Backup;
using DshPanel.Files;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПРОБА ОБНОВЛЕНИЯ ДВИЖКА — НАСТОЯЩИМИ ДВЕРЯМИ, В ИЗОЛЯЦИИ (приёмка E3.2).
///
/// **Зачем отдельная проба, если есть проверки.** Проверки идут подставными дверями: они
/// доказывают порядок и слова, но не то, что настоящий 7-Zip снимет настоящую копию профиля,
/// настоящий npm поставит дерево, а настоящий движок поднимется и пройдёт пробу 84/84. Ровно
/// на таком разрыве панель уже попадалась (E3.1: `npm ci` ставил 1 пакет вместо 588 — подставные
/// двери этого не видели).
///
/// **Что делает проба, по шагам (и каждый шаг — то же, что делает человек кнопкой):**
///
/// 1. **Node:** системный проверяется **по данным замка** (в ВМ подставлен ровно `22.11.0` —
///    выше нижней границы 20 и ниже порога замка `22.19.0`), и панель обязана взять **свой**,
///    скачав его с официального источника и сверив сумму;
/// 2. **движок «до обновления»** ставится по встроенному замку в свою папку — это состояние,
///    с которого начинается обновление;
/// 3. **профиль с работой** заводится в доме движка (сессии, настройки, вложение) — чтобы
///    предохранительная копия была настоящей, с весом и числом файлов;
/// 4. **свой сервер поднимается** (на свободном порту, в доме прогона): иначе шаг «погасить свой
///    движок» был бы пустым — гасить было бы нечего;
/// 5. **путь обновления идёт через НАСТОЯЩИЕ двери** (<see cref="EngineUpdateRealDoors"/>):
///    гашение своего движка → копия профиля дверью 7-Zip → установка по замку → **живая проба**
///    на поставленном движке (свой временный дом, свободный порт) → запись → подъём сервера заново;
/// 6. **проверяется, что осталось на диске:** копия из семейства `dsh2-before-engine-` (ротация её
///    не трогает), профиль цел, запись называет версию, сервер снова работает.
///
/// **Подложенная беда** (<c>beda</c>): место копий занято ФАЙЛОМ — снять копию нельзя. Панель
/// обязана **отказать и не начинать установку** («полуархива не бывает») и **поднять прежний
/// движок заново**, а не оставить человека без сервера.
///
/// ⚠️ **Чего проба НЕ проверяет, и это названо прямо:** смену НОМЕРА версии. В замке одна версия —
/// та, что мы проверили пробой; поставить другую по этому замку нельзя, а непроверенную панель
/// ставить не имеет права. Проверяется вся ЦЕПОЧКА подмены, а не разница номеров.
///
/// ⚠️ **Без изоляции отказывается сама** (код 2): проба ставит движок (сотни мегабайт), поднимает
/// сервер и снимает копию профиля — без изоляции всё это легло бы рядом с данными человека.
/// </summary>
public static class EngineUpdateSelfTest
{
    /// <summary>Срок на всю пробу: две установки по замку (48–87 с каждая замером), проба живьём и копия.</summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromMinutes(30);

    public static int Run(TimeSpan timeout) => Run(timeout, planted: false);

    public static int Run(TimeSpan timeout, bool planted)
    {
        var context = RunContext.Current;

        if (!context.IsIsolated)
        {
            Console.WriteLine(
                "ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ: проба ставит настоящий движок, поднимает сервер и снимает " +
                "копию профиля — без изоляции это легло бы рядом с данными человека. " +
                "Запустите с «--run-root <свой путь>».");
            return 2;
        }

        var paths = context.Paths;
        var report = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        // ЗАМОК — ПЕРВЫМ: из него берётся порог версии Node (решение владельца `7265d9d`),
        // и без него решение о системном Node шло бы по одной нижней границе 20.
        var lockFile = EngineTreeLock.Load();

        Check("встроенный замок читается", lockFile.Any);
        Check("замок цел (записей столько, сколько объявлено)", lockFile.Complete);
        Check("у каждого пакета замка есть сумма", lockFile.FullySummed);
        Check("все адреса замка — с официального реестра", lockFile.AllFromRegistry);

        if (!lockFile.Any)
        {
            Print(report);
            Console.WriteLine("ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ");
            return 1;
        }

        // --- (1) NODE: системный решается ПО ЗАМКУ, свой добывается, если системный не годится ----
        var systemNode = DshEngine.FindNode();
        var systemVersion = DshPanel.Platform.VersionProbe.Run(systemNode, "--version");

        report.Add($"системный Node: {(string.IsNullOrWhiteSpace(systemNode) ? "не найден" : systemNode)} ({systemVersion})");
        report.Add($"порог из замка: {EngineNode.RequiredFrom(lockFile)}; нижняя граница без замка: {EngineNode.MinimumMajor}");

        var node = EngineNodeInstall.Ensure(
            paths.NodeDir,
            systemNode,
            systemVersion,
            lockFile,
            EngineNodeDoors.Real(new Update.HttpUpdateDownload()),
            report.Add);

        Check("Node добыт (системный годится по замку или скачан свой)", node.Ok);

        if (!node.Ok)
        {
            report.Add($"свой Node не добылся: {node.Detail}");
            Print(report);
            Console.WriteLine("ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ");
            return 1;
        }

        report.Add(node.Ours
            ? $"свой Node: {node.NodePath} (версия {node.Version})"
            : $"системный Node: {node.NodePath} (версия {node.Version})");

        if (node.Ours && !string.IsNullOrWhiteSpace(systemNode))
        {
            // ЭТО И ЕСТЬ ПРОВЕРКА СОСТОЯНИЯ ИЗ ВМ: системный Node есть, но замку не годится —
            // значит панель обязана была взять свой, а не «попробовать системный».
            Check("системный Node был, но замку не годился — панель взяла свой",
                !EngineNode.Fits(systemVersion, lockFile));
        }

        // ПОДСКАЗКИ — тем же клиентом, что у панели: версия для установки берётся из них.
        var fetch = new HttpEngineHintsClient(ProductLinks.EngineHintsUrl).Read();
        var hints = fetch.Ok ? EngineHints.Parse(fetch.Json) : EngineHints.Empty;

        report.Add(fetch.Ok
            ? $"подсказки прочитаны: записей {hints.Versions.Count}"
            : $"подсказки НЕ прочитаны: {fetch.Detail}");

        // --- (2) ДВИЖОК «ДО ОБНОВЛЕНИЯ»: по замку, в свою папку --------------------------------
        var before = EngineInstaller.Install(
            lockFile, hints, paths.EngineDir, systemNode, null, EngineInstallDoors.Real,
            report.Add, () => node, paths.EngineStateFile);

        Check("движок до обновления поставлен по замку", before.Ok);

        if (!before.Ok)
        {
            report.Add($"установка до обновления не прошла: {before.Outcome}; {before.Detail}");
            Print(report);
            Console.WriteLine("ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ");
            return 1;
        }

        var version = before.Version;
        var bin = OwnEngine.BinPath(paths.EngineDir, version);

        Check("bin.js поставленного движка на месте", File.Exists(bin));

        // --- (3) ПРОФИЛЬ С РАБОТОЙ -------------------------------------------------------------
        var profile = PlantProfile(paths.DshHome);

        report.Add($"профиль до обновления: {profile.Files} файлов, {profile.Bytes} байт");

        // --- (4) СВОЙ СЕРВЕР ПОДНЯТ: иначе «погасить свой движок» — пустой шаг ------------------
        var log = new List<string>();

        var controller = new ServerController(
            paths.DshHome,
            () => Directory.CreateDirectory(paths.DataDir).FullName,
            log.Add,
            mayOccupyOwnerPort: false,
            locateEngine: () => new DshEngine(node.NodePath, bin),
            requestedPort: () => FreePort.Find(),
            allowParallelStart: true,
            files: ServerStateFiles.Under(paths, log.Add));

        var started = controller.Start(timeout);

        report.Add($"свой сервер: работает = {started.IsRunning}, порт {started.Port}, движок {OwnEngine.VersionOf(bin)}");
        Check("свой сервер поднят на поставленном движке", started.IsRunning && controller.Owner == ServerOwner.Panel);

        if (!started.IsRunning)
        {
            foreach (var line in controller.EngineTail()) report.Add("движок| " + line);
            controller.Dispose();
            Print(report);
            Console.WriteLine("ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ");
            return 1;
        }

        // --- (5) ПОДЛОЖЕННАЯ БЕДА: место копий занято ФАЙЛОМ -----------------------------------
        if (planted)
        {
            try
            {
                File.WriteAllText(paths.BackupsDir, "занято файлом — папки для копии нет");
                report.Add($"подложенная беда: по пути копий лежит ФАЙЛ ({paths.BackupsDir})");
            }
            catch (Exception error)
            {
                report.Add($"подложить беду не удалось: {error.GetType().Name}");
            }
        }

        // --- (6) ПУТЬ ОБНОВЛЕНИЯ — НАСТОЯЩИМИ ДВЕРЯМИ -------------------------------------------
        //
        // ХОД ПУТИ идёт в СВОЙ список: по нему проверяется, что установки НЕ было (при беде)
        // и что слова сказаны, — и он же попадает в отчёт.
        var runLog = new List<string>();

        void Say(string line)
        {
            runLog.Add(line);
            report.Add(line);
        }

        var doors = EngineUpdateRealDoors.Update(paths, () => controller, () => hints, Say);
        var plan = EngineUpdateFlow.Plan(hints, freshVersion: string.Empty, freshWanted: false, ownPort: started.Port);
        var result = EngineUpdateRun.Run(plan, doors, Say);

        report.Add($"итог пути обновления: {result.Outcome}; версия «{result.Version}»; слова: {result.Detail}");

        // --- (7) ЧТО ОСТАЛОСЬ НА ДИСКЕ ---------------------------------------------------------
        var copies = EngineProfileCopyIn(paths.BackupsDir);
        var after = EngineInstallRecord.Read(paths.EngineStateFile);

        if (planted)
        {
            Check("копия не снялась — обновление НЕ начиналось", result.Outcome == EngineUpdateOutcome.CopyFailed);
            Check("установки не было вовсе (шага установки нет в журнале)",
                !runLog.Any(line => line.Contains(
                    EngineInstallPhrases.UpdateStep(EngineUpdateFlow.Words.InstallByLock, version),
                    StringComparison.Ordinal)));
            Check("прежняя версия движка на месте", File.Exists(bin) && after.EngineVersion == version);
            Check("слова — «полуархива не бывает»",
                result.Detail == EngineUpdateFlow.Words.CopyNotWhole
                && runLog.Any(line => line.Contains(
                    EngineInstallPhrases.UpdateStep(EngineUpdateFlow.Words.CopyNotWhole),
                    StringComparison.Ordinal)));
            Check("прежний движок поднят заново — человека без сервера не оставили",
                controller.Refresh().IsRunning && controller.Owner == ServerOwner.Panel);
            Check("профиль не тронут", ProfileIntact(paths.DshHome, profile));
        }
        else
        {
            Check("путь обновления прошёл целиком", result.Outcome == EngineUpdateOutcome.Updated);
            Check("предохранительная копия снята и она ОДНА", copies.Count == 1);

            if (copies.Count == 1)
            {
                Check("копия из предохранительного семейства (ротация её не трогает)",
                    BackupNaming.Kind(Path.GetFileName(copies[0])) == BackupFileKind.Safety);
                Check("копия ЦЕЛАЯ (в ней есть записи)", ZipWriter.CountFileEntries(copies[0]) > 0);
                report.Add($"копия: {Path.GetFileName(copies[0])}, записей {ZipWriter.CountFileEntries(copies[0])}, {new FileInfo(copies[0]).Length} байт");
            }

            Check("профиль цел после обновления", ProfileIntact(paths.DshHome, profile));
            Check("запись называет поставленную версию", after.EngineVersion == version);
            Check("движок поставлен и лежит в своей папке", File.Exists(bin));
            Check("живая проба прошла словами", runLog.Any(line => line.Contains(EngineInstallPhrases.UpdateProbed(), StringComparison.Ordinal)));
            Check("сервер поднят заново на новой версии", controller.Refresh().IsRunning && controller.Owner == ServerOwner.Panel);
            Check("панель считает поднятый движок своим", OwnEngine.IsOwnBinPath(bin, paths.EngineDir));
        }

        // ⚠️ ПОГАСИТЬ СВОЙ СЕРВЕР — ПРОГОН ЗА СОБОЙ УБИРАЕТ. Найдено замером 18.10.2026 (задача `t4`,
        // блок B): `Dispose()` у контроллера гасит только объект процесса, а НЕ движок, поэтому после
        // самотеста в `%TEMP%` оставался живой `node` (замер: PID 29096). То же было у самотеста отката.
        var stopped = controller.Stop(confirmed: true);
        Check("свой сервер погашен — прогон за собой убрал", !stopped.IsRunning);

        controller.Dispose();

        // СВОЙ ВРЕМЕННЫЙ ДОМ ПРОБЫ не должен остаться: проба убирает за собой (проверяется по факту).
        var probeHome = Path.Combine(paths.StateDir, "engine-probe-" + version);
        Check("временный дом живой пробы убран", !Directory.Exists(probeHome));

        Print(report);

        Console.WriteLine(failed ? "ДВИЖОК ОБНОВЛЕНИЕ ПРОВАЛ" : "ДВИЖОК ОБНОВЛЕНИЕ УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>Сколько файлов и байт в подложенной работе — снимается ДО обновления и сверяется после.</summary>
    private sealed record Planted(int Files, long Bytes, string Stamp)
    {
        /// <summary>Имена подложенных файлов: движок дописывает свои, и они в сверку не входят.</summary>
        public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// ПРОФИЛЬ С РАБОТОЙ: вложения, заметки и обычные файлы.
    ///
    /// ⚠️ **РАСКЛАДКА СЕССИЙ НЕ ПОДДЕЛЫВАЕТСЯ, И ЭТО ЗАМЕР (11.10.2026).** Первая редакция заводила
    /// «настоящую» раскладку (`sessions\prob-2026-10-10\session.v3.jsonl`) — и движок на ней
    /// **отказывался подниматься**: *«session artifact … uses the unsupported flat-file layout»*,
    /// а на правильной раскладке с подделанным содержимым он молча висел без ссылки входа
    /// (проверено тремя раскладками: две с сессией — движок не отвечает, одна без сессии — отвечает).
    /// Настоящая сессия требует хода модели и ключа, которых в прогоне нет, поэтому подделывать её
    /// нельзя: прогон мерил бы не цепочку обновления, а сломанную машину.
    ///
    /// Для копии это не подделка: 7-Zip снимает **настоящие** файлы настоящего веса, а «профиль цел»
    /// проверяется суммой содержимого всех файлов.
    /// </summary>
    private static Planted PlantProfile(string home)
    {
        var storages = Path.Combine(home, "storages", "prob-2026-10-10");
        var notes = Path.Combine(home, "prob-notes");

        Directory.CreateDirectory(storages);
        Directory.CreateDirectory(notes);

        File.WriteAllText(Path.Combine(home, "prob-settings.json"), "{\"проба\":true}");
        File.WriteAllText(Path.Combine(notes, "readme.txt"), "заметка прогона");
        File.WriteAllText(Path.Combine(storages, "notes.txt"), new string('с', 4096));

        var attachment = Path.Combine(storages, "attachment.bin");
        var bytes = new byte[4 * 1024 * 1024];

        // СОДЕРЖИМОЕ НЕСЖИМАЕМОЕ: на повторяющемся узоре 7-Zip ужимал 4 МБ в 55 КБ, и «вес копии»
        // переставал что-либо значить. Случайные байты дают честный вес — как у настоящих вложений.
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        File.WriteAllBytes(attachment, bytes);

        return Snapshot(home);
    }

    /// <summary>
    /// СНИМОК ПРОФИЛЯ — по ПОДЛОЖЕННЫМ файлам, и это не поблажка себе.
    ///
    /// **Движок пишет в свой дом сам** (`.anonymous-user-id`, `settings.json`, `profiles\node_modules`
    /// при первом старте) — «дерево не изменилось ни на файл» было бы неправдой, которую прогон
    /// не имеет права утверждать. Проверяется то, что обещано: **работа, ради которой снимали копию,
    /// цела** — каждый подложенный файл на месте, того же размера и того же содержимого.
    /// </summary>
    private static Planted Snapshot(string home)
    {
        var files = Directory.GetFiles(home, "*", SearchOption.AllDirectories)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = files.Sum(file => new FileInfo(file).Length);

        return new Planted(files.Length, total, Sum(files))
        {
            Names = files.Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToArray(),
        };
    }

    /// <summary>Работа цела: каждый файл снимка на месте и содержимое совпало (сверка по хешам).</summary>
    private static bool ProfileIntact(string home, Planted planted)
    {
        if (!Directory.Exists(home)) return false;

        var files = Directory.GetFiles(home, "*", SearchOption.AllDirectories)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Файлов МОЖЕТ стать больше (движок дописывает свои) — но ни один из снятых пропасть не смеет.
        if (files.Length < planted.Files) return false;

        var mine = files.Where(file => planted.Names.Contains(
            Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)).ToArray();

        return mine.Length >= planted.Files && Sum(mine) == planted.Stamp;
    }

    /// <summary>
    /// Сумма содержимого набора файлов: SHA-256 по именам и хешам в порядке сортировки.
    /// ⚠️ Считается БЕЗ путей: движок может переложить файл, и это не «потеря работы» — важно,
    /// что содержимое на месте и оно то же.
    /// </summary>
    private static string Sum(IEnumerable<string> files)
    {
        using var hash = System.Security.Cryptography.SHA256.Create();
        var text = new System.Text.StringBuilder();

        foreach (var file in files.OrderBy(name => Path.GetFileName(name), StringComparer.OrdinalIgnoreCase))
            text.Append(Path.GetFileName(file)).Append('=')
                .Append(Convert.ToHexString(hash.ComputeHash(File.ReadAllBytes(file)))).Append(';');

        return Convert.ToHexString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Предохранительные копии профиля в папке копий. Папки нет — пусто, а не исключение.</summary>
    private static IReadOnlyList<string> EngineProfileCopyIn(string backupsDir)
    {
        try
        {
            return Directory.Exists(backupsDir)
                ? Directory.GetFiles(backupsDir, EngineProfileCopy.Prefix + "*.zip")
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void Print(IReadOnlyList<string> report)
    {
        foreach (var line in report) Console.WriteLine("ДВИЖОК| " + line);
    }
}
