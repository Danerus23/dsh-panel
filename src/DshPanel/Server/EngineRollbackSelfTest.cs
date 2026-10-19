using System.Globalization;
using DshPanel.Files;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПРОБА ОТКАТА ПО КНОПКЕ — НАСТОЯЩИМИ ДВЕРЯМИ, В ИЗОЛЯЦИИ (приёмка E3.3).
///
/// **Что здесь проверяется живьём, а не подставными дверями:** панель держит **прежнюю** версию
/// на диске, переводит запись «стоит сейчас» на «новую», поднимает на ней сервер — и **возвращается**
/// настоящим путём: проба прежней версии на свободном порту в своём временном доме → гашение своего
/// сервера → перезапись записи → подъём сервера заново **уже на прежней версии**.
///
/// **Три случая, и каждый — своё обещание:**
///
/// 1. **Возвращаться некуда** (на диске одна версия): отказ словами, и **ни одна дверь не спрошена**;
/// 2. **Откат состоялся**: запись называет прежнюю версию, сервер поднят заново, **обе папки целы**
///    (держим текущую и прежнюю);
/// 3. **Подложенная беда** (<c>beda</c>): у прежней версии сломан движок — проба обязана **отказать
///    ДО всякой подмены**, запись остаётся на «новой» версии, а сервер продолжает работать.
///
/// ⚠️ **Чего проба НЕ проверяет, и это сказано прямо:** смену НОМЕРА версии. В замке одна версия
/// (та, что прошла пробу), поэтому «новая» версия здесь — **копия дерева под другим именем**,
/// сделанная самой пробой вне панели. Проверяется **ЦЕПОЧКА отката**, а не разница номеров:
/// настоящая вторая версия появится у человека, когда мы проверим и заведём её замок.
///
/// ⚠️ **Без изоляции отказывается сама** (код 2): проба ставит движок (сотни мегабайт), копирует
/// дерево и поднимает сервер — без изоляции всё это легло бы рядом с данными человека.
/// </summary>
public static class EngineRollbackSelfTest
{
    /// <summary>Срок: установка по замку (48–87 с замером), копия дерева, две живые пробы и запуски сервера.</summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromMinutes(30);

    public static int Run(TimeSpan timeout) => Run(timeout, planted: false);

    public static int Run(TimeSpan timeout, bool planted)
    {
        var context = RunContext.Current;

        if (!context.IsIsolated)
        {
            Console.WriteLine(
                "ДВИЖОК ОТКАТ ПРОВАЛ: проба ставит настоящий движок, копирует дерево и поднимает сервер. " +
                "Без изоляции это легло бы рядом с данными человека. Запустите с «--run-root <свой путь>».");
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

        var lockFile = EngineTreeLock.Load();

        Check("встроенный замок читается", lockFile.Any);
        Check("замок цел", lockFile.Complete);
        Check("у каждого пакета замка есть сумма", lockFile.FullySummed);
        Check("все адреса замка — с официального реестра", lockFile.AllFromRegistry);

        if (!lockFile.Any)
        {
            Print(report);
            Console.WriteLine("ДВИЖОК ОТКАТ ПРОВАЛ");
            return 1;
        }

        // NODE — по данным замка (в ВМ подставлен ровно 22.11.0: выше 20 и ниже порога замка).
        var systemNode = DshEngine.FindNode();
        var systemVersion = DshPanel.Platform.VersionProbe.Run(systemNode, "--version");

        report.Add($"системный Node: {(string.IsNullOrWhiteSpace(systemNode) ? "не найден" : systemNode)} ({systemVersion})");
        report.Add($"порог из замка: {EngineNode.RequiredFrom(lockFile)}");

        var node = EngineNodeInstall.Ensure(
            paths.NodeDir, systemNode, systemVersion, lockFile,
            EngineNodeDoors.Real(new Update.HttpUpdateDownload()), report.Add);

        Check("Node добыт", node.Ok);

        if (!node.Ok)
        {
            report.Add($"свой Node не добылся: {node.Detail}");
            Print(report);
            Console.WriteLine("ДВИЖОК ОТКАТ ПРОВАЛ");
            return 1;
        }

        report.Add(node.Ours ? $"свой Node: {node.NodePath} ({node.Version})" : $"системный Node: {node.NodePath} ({node.Version})");

        var fetch = new HttpEngineHintsClient(ProductLinks.EngineHintsUrl).Read();
        var hints = fetch.Ok ? EngineHints.Parse(fetch.Json) : EngineHints.Empty;

        report.Add(fetch.Ok ? $"подсказки прочитаны: записей {hints.Versions.Count}" : $"подсказки НЕ прочитаны: {fetch.Detail}");

        // --- (1) ПРЕЖНЯЯ ВЕРСИЯ: то, к чему возвращаемся ------------------------------------------
        var previous = EngineInstaller.Install(
            lockFile, hints, paths.EngineDir, systemNode, null, EngineInstallDoors.Real,
            report.Add, () => node, paths.EngineStateFile);

        Check("прежняя версия поставлена по замку", previous.Ok);

        if (!previous.Ok)
        {
            report.Add($"установка не прошла: {previous.Outcome}; {previous.Detail}");
            Print(report);
            Console.WriteLine("ДВИЖОК ОТКАТ ПРОВАЛ");
            return 1;
        }

        var older = previous.Version;
        var olderBin = OwnEngine.BinPath(paths.EngineDir, older);

        Check("bin.js прежней версии на месте", File.Exists(olderBin));

        var log = new List<string>();

        // СВОЙ СЕРВЕР: движок ищется ТЕМ ЖЕ путём, что у панели (`OwnEngine.Locate` — сначала своя
        // поставленная версия из записи). Поэтому после отката он поднимется с ПРЕЖНЕЙ версии,
        // и это проверяется не обещанием, а тем, что панель сама так решает.
        var controller = new ServerController(
            paths.DshHome,
            () => Directory.CreateDirectory(paths.DataDir).FullName,
            log.Add,
            mayOccupyOwnerPort: false,
            locateEngine: () => OwnEngine.Locate(paths),
            requestedPort: () => FreePort.Find(),
            allowParallelStart: true,
            files: ServerStateFiles.Under(paths, log.Add));

        var doors = EngineUpdateRealDoors.Rollback(paths, () => controller, report.Add);

        // --- (2) ВОЗВРАЩАТЬСЯ НЕКУДА: на диске одна версия ---------------------------------------
        var alone = Service(paths, () => 0, doors, report.Add);
        var aloneAnswer = alone.Rollback();

        report.Add($"отказ «некуда»: «{aloneAnswer}»");

        Check("возвращаться некуда — раздел это видит", !alone.RollbackAvailable);
        Check("отказ сказан словами «возвращаться некуда»",
            aloneAnswer == DshPanel.Localization.Loc.T(EngineRollbackDecisions.Words.NoPrevious));

        // --- (3) «НОВАЯ» ВЕРСИЯ — КОПИЯ ДЕРЕВА (настоящей второй версии в замке нет) ---------------
        var newer = Bump(older);
        var newerDir = OwnEngine.VersionDir(paths.EngineDir, newer);

        report.Add($"«новая» версия {newer} — копия дерева {older} (настоящей второй версии в замке нет: проверяется ЦЕПОЧКА отката)");

        var copied = CopyTree(OwnEngine.VersionDir(paths.EngineDir, older), newerDir);
        Check("«новая» версия разложена", copied);

        var before = EngineInstallRecord.Read(paths.EngineStateFile);
        var moved = EngineInstallRecord.After(newer, newerDir, node, before);
        var written = moved.Write(paths.EngineStateFile, out var why);

        Check("запись переведена на «новую» версию", written);

        if (why.Length > 0) report.Add($"запись: {why}");

        // --- (4) СЕРВЕР ПОДНЯТ НА «НОВОЙ» ВЕРСИИ -------------------------------------------------
        var started = controller.Start(timeout);

        report.Add($"свой сервер: работает = {started.IsRunning}, порт {started.Port}");
        Check("сервер поднят на «новой» версии", started.IsRunning && controller.Owner == ServerOwner.Panel);

        if (!started.IsRunning)
        {
            foreach (var line in controller.EngineTail()) report.Add("движок| " + line);
            controller.Dispose();
            Print(report);
            Console.WriteLine("ДВИЖОК ОТКАТ ПРОВАЛ");
            return 1;
        }

        // --- (5) БЕДА: прежний движок сломан ------------------------------------------------------
        if (planted)
        {
            File.Delete(olderBin);
            report.Add($"подложенная беда: у прежней версии убран {olderBin}");
        }

        var service = Service(paths, () => started.Port, doors, report.Add);

        Check("есть куда возвращаться", service.RollbackAvailable);
        Check("цель отката — прежняя версия", service.RollbackTarget == older);

        report.Add($"вопрос отката: «{service.RollbackQuestion.Text}»");

        Check("вопрос отката говорит про сессии и обе версии",
            service.RollbackQuestion.Text.Contains("сессии", StringComparison.OrdinalIgnoreCase)
            && service.RollbackQuestion.Text.Contains(newer, StringComparison.Ordinal)
            && service.RollbackQuestion.Text.Contains(older, StringComparison.Ordinal));

        var answer = service.Rollback();
        var after = EngineInstallRecord.Read(paths.EngineStateFile);

        report.Add($"итог отката: «{answer}»");

        if (planted)
        {
            Check("проба прежней версии отказала — подмены НЕ было", after.EngineVersion == newer);
            Check("сервер НЕ гасился (ничего не меняли)",
                controller.Refresh().IsRunning && controller.Owner == ServerOwner.Panel);
            Check("прежняя папка так и осталась сломанной", !File.Exists(olderBin));
            Check("слова — «остаёмся на версии»", answer.Contains(newer, StringComparison.Ordinal));
        }
        else
        {
            Check("запись называет прежнюю версию", after.EngineVersion == older);
            Check("прежняя папка цела", File.Exists(olderBin));
            Check("«новая» папка осталась (держим текущую и прежнюю)", Directory.Exists(newerDir));
            Check("панель поднимает теперь ИМЕННО прежнюю версию",
                string.Equals(OwnEngine.BinPath(paths.EngineDir, after.EngineVersion), olderBin, StringComparison.OrdinalIgnoreCase));
            Check("сервер поднят заново", controller.Refresh().IsRunning && controller.Owner == ServerOwner.Panel);
            Check("одна строка после дела говорит про сессии новой версии",
                answer.Contains(older, StringComparison.Ordinal) && answer.Contains("сессии", StringComparison.OrdinalIgnoreCase));
        }

        // ВРЕМЕННЫЙ ДОМ ПРОБЫ НЕ ОСТАЛСЯ: обе пробы (прежней версии) убирают за собой.
        Check("временный дом пробы убран", !Directory.Exists(Path.Combine(paths.StateDir, "engine-probe-" + older)));

        // ⚠️ ПОГАСИТЬ СВОЙ СЕРВЕР — ПРОГОН ЗА СОБОЙ УБИРАЕТ. Найдено замером 18.10.2026 (задача `t4`,
        // блок B): `Dispose()` у контроллера гасит только объект процесса, а НЕ движок, поэтому после
        // этого самотеста в `%TEMP%` оставался живой `node` (дважды: PID 24016 и PID 32612). Прогон
        // проверки обязан гасить то, что поднял, — те же правила, что у соседних самотестов.
        var stopped = controller.Stop(confirmed: true);
        Check("свой сервер погашен — прогон за собой убрал", !stopped.IsRunning);

        controller.Dispose();

        Print(report);

        Console.WriteLine(failed ? "ДВИЖОК ОТКАТ ПРОВАЛ" : "ДВИЖОК ОТКАТ УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ ДВИЖКА» СО СВОИМИ ДВЕРЯМИ — тем же путём, каким его собирает `App`:
    /// откат идёт **через сервис**, а не мимо него (иначе проверялось бы не то, что видит человек).
    /// </summary>
    private static EngineUpdateService Service(
        AppPaths paths,
        Func<int> port,
        EngineRollbackDoors doors,
        Action<string> log) =>
        new(
            new EngineUpdateSectionDoors(
                ReadHints: () => EngineHints.Empty,
                ReadFresh: () => string.Empty,
                OwnPort: port,
                Run: EngineUpdateDoors.None)
            {
                ReadRecord = () => EngineInstallRecord.Read(paths.EngineStateFile),
                ReadFolders = () => EngineRetention.Installed(paths.EngineDir),
                RollbackDoors = doors,
            },
            log);

    /// <summary>«Новая» версия для пробы: старшая часть та же, номер выпуска на единицу больше.</summary>
    private static string Bump(string version)
    {
        var parts = version.Split('-', 2);
        var numbers = parts[0].Split('.');

        if (numbers.Length < 3 || !int.TryParse(numbers[2], out var patch)) return version + "-next";

        numbers[2] = (patch + 1).ToString(CultureInfo.InvariantCulture);

        return string.Join('.', numbers) + (parts.Length > 1 ? "-" + parts[1] : string.Empty);
    }

    /// <summary>
    /// РАЗЛОЖИТЬ КОПИЮ ДЕРЕВА: своя рекурсия, потому что уборка проекта (`TreeDeletion`) — про
    /// удаление, а копирования в проекте нет вовсе. Ссылки копируются как ссылки (в сборке движка
    /// их нет, но если появятся — копия не должна разворачивать чужое дерево внутрь себя).
    /// </summary>
    private static bool CopyTree(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(to);

            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

            foreach (var folder in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(folder);

                // ССЫЛКА КОПИРУЕТСЯ КАК ССЫЛКА: разворачивать чужое дерево внутрь копии нельзя
                // (в сборке движка ссылок нет, но копия не имеет права тянуть за собой чужое).
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.CreateSymbolicLink(
                        Path.Combine(to, name),
                        new DirectoryInfo(folder).LinkTarget ?? folder);
                    continue;
                }

                if (!CopyTree(folder, Path.Combine(to, name))) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Print(IReadOnlyList<string> report)
    {
        foreach (var line in report) Console.WriteLine("ДВИЖОК| " + line);
    }
}
