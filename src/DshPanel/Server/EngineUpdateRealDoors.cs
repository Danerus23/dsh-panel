using System.Globalization;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// НАСТОЯЩИЕ ДВЕРИ ОБНОВЛЕНИЯ ДВИЖКА (пункты 4–7 плана E3.2): погасить СВОЙ сервер, снять
/// предохранительную копию профиля, поставить версию по замку, проверить её живьём.
///
/// **Почему одним местом.** Дверей ровно четыре, и каждая — единственный способ сделать это
/// действие в панели: гашение идёт через контроллер (он же говорит предупреждение), копия — дверью
/// 7-Zip (`ZipWriter`, как у копий панели), установка — `EngineInstaller` по встроенному замку,
/// проба — `EngineLiveProbe` на **своём временном доме** (ни профиля владельца, ни его порта).
///
/// **Три границы, и все три — красные линии проекта:**
///
/// 1. **Чужой движок не гасится НИКОГДА.** Если сервер не наш (`Adopted` — чужой, взятый под
///    управление), дверь отвечает «не смогла»: путь обновления на этом останавливается словами
///    «сначала остановите его», а не подменяет версию под чужой работой.
/// 2. **Порт владельца не занимается пробой.** Проба поднимает движок на **свободном** порту
///    и в **своём временном доме**; порт 3080/3081 проба не трогает вовсе.
/// 3. **Временный дом убирается за собой** помощником `TreeDeletion` (одна дверь удаления дерева
///    в проекте) — после пробы на диске не остаётся брошенного второго профиля.
///
/// ⚠️ Двери создаются **один раз** на раздел настроек: установщик помнит свой Node, а проба —
/// ничего (каждый раз новый временный дом). Никакого состояния между запусками обновления здесь нет.
/// </summary>
public static class EngineUpdateRealDoors
{
    /// <summary>
    /// СОБРАТЬ ЧЕТЫРЕ НАСТОЯЩИЕ ДВЕРИ. <paramref name="hints"/> — подсказки (из них установщик берёт
    /// версию: ставить он умеет только проверенную), <paramref name="server"/> — контроллер панели
    /// (может быть <c>null</c>: без окна и сервера панель всё равно умеет обновлять движок).
    /// </summary>
    public static EngineUpdateDoors Update(
        AppPaths paths,
        Func<ServerController?> server,
        Func<EngineHints> hints,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(log);

        // ДОВОДЫ ЗАМКА читаются ОДИН раз на раздел: замок — встроенный ресурс сборки, он не меняется
        // на ходу, а решать «годится ли Node» без него нельзя (порог версии берётся из него).
        var locks = EngineTreeLocks.Load();
        var lockFile = locks.Primary;

        // СВОЙ NODE ДОБЫВАЕТСЯ ЛЕНИВО и помнится: он нужен и установке, и пробе, а скачивание
        // с официального источника — не то, что стоит делать дважды.
        EngineNodeResult? node = null;

        EngineNodeResult Node()
        {
            if (node is not null) return node;

            var systemNode = DshEngine.FindNode();
            var systemVersion = DshPanel.Platform.VersionProbe.Run(systemNode, "--version");

            log(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.EngineUpdateNodeCheckLogFormat,
                string.IsNullOrWhiteSpace(systemNode) ? "не найден" : systemNode,
                systemVersion));

            node = EngineNodeInstall.Ensure(
                paths.NodeDir,
                systemNode,
                systemVersion,
                EngineNode.StrictestLock(locks),
                EngineNodeDoors.Real(new Update.HttpUpdateDownload()),
                log);

            return node;
        }

        // СВОЙ СЕРВЕР РАБОТАЛ ДО ОБНОВЛЕНИЯ — его и вернём на новую версию: человек просил
        // обновить движок, а не остаться без сервера. НЕ работал — не поднимаем: «поднять сервер
        // без просьбы» панель не имеет права (красная линия), и это не наше дело.
        var wasOurs = false;

        return new EngineUpdateDoors(
            StopOwnEngine: (port, _) =>
            {
                var (ok, was) = StopOwn(server(), log);

                if (was) wasOurs = true;

                return ok;
            },
            CopyProfile: (version, _) =>
            {
                if (Copy(paths, version, log)) return true;

                // ОБНОВЛЕНИЕ НЕ НАЧАЛОСЬ, А ДВИЖОК УЖЕ ПОГАШЕН: возвращаем панель в то состояние,
                // в каком её застали. Прежняя версия на месте и запись про неё не тронута — значит
                // поднять заново есть что; оставить человека без сервера после отказа нельзя.
                if (wasOurs) Restart(server(), log, PanelStrings.EngineUpdateEngineRestoredLog);

                return false;
            },
            Install: (version, _) =>
            {
                var installed = Install(paths, locks, hints(), Node, version, log);

                if (!installed.Ok && wasOurs)
                    Restart(server(), log, PanelStrings.EngineUpdateEngineRestoredLog);

                return installed;
            },
            Probe: (version, _) => Probe(paths, Node, version, log, server, wasOurs));
    }

    /// <summary>Сколько ждём подъёма сервера заново. Замер: холодный запуск движка — 5,0 с.</summary>
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(240);

    /// <summary>
    /// ПОГАСИТЬ СВОЙ СЕРВЕР — и только свой. Согласие человека уже получено вопросом
    /// (<see cref="EngineUpdateStepKind.AskHuman"/>), поэтому подтверждение контроллеру даётся:
    /// второй раз спрашивать то же самое значило бы спрашивать дважды об одном.
    ///
    /// ⚠️ Чужой сервер (взятый под управление) **не гасится**: «не смогла» останавливает путь
    /// обновления словами, а не подменяет версию под чужой работой.
    ///
    /// Отвечает ДВУМЯ признаками: получилось ли и был ли это НАШ работающий сервер — второй нужен,
    /// чтобы после удачной пробы вернуть панель в то состояние, в каком её застали.
    /// </summary>
    private static (bool Ok, bool Was) StopOwn(ServerController? controller, Action<string> log)
    {
        if (controller is null) return (true, false); // сервера нет вовсе — гасить нечего

        var state = controller.Refresh();

        if (!state.IsRunning) return (true, false);

        if (controller.Owner != ServerOwner.Panel)
        {
            log(PanelStrings.EngineUpdateStopForeignLog);
            return (false, false);
        }

        var after = controller.Stop(confirmed: true);

        return (!after.IsRunning, true);
    }

    /// <summary>
    /// ПРЕДОХРАНИТЕЛЬНАЯ КОПИЯ ПРОФИЛЯ: тем же письмом, что копии панели (дверью 7-Zip), в папку
    /// копий, именем из семейства `dsh2-before-engine-` (ротация такие файлы не удаляет).
    ///
    /// **«Либо целая, либо её нет».** Неудача упаковки — это `false`, и путь обновления на этом
    /// останавливается: обновление без копии вернуться к прежней работе не даст.
    /// </summary>
    internal static bool Copy(AppPaths paths, string version, Action<string> log)
    {
        var plan = EngineProfileCopy.Decide(paths.DshHome, paths.BackupsDir, DateTimeOffset.Now);

        if (!plan.Any)
        {
            log(EngineInstallPhrases.UpdateRefusal(plan.RefusalKey));
            return false;
        }

        log(string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.EngineUpdateCopyProfileFormat, version) + " → " + Shell.DisplayMask.Path(plan.Target));

        // КОНЕЧНОЕ ИМЯ — ТОЛЬКО ГОТОВЫМ (Н-2, t14): пишем под `<конечное имя>.part` и публикуем одним
        // переименованием. Прежде архив обновления писался прямо в `plan.Target`, то есть человек
        // (и панель) видел в папке копий недоделанный архив под именем готовой копии.
        var partial = plan.Target + ".part";
        var published = false;

        try
        {
            var created = ZipWriter.Write(
                partial,
                new[] { new BackupSource(paths.DshHome, string.Empty, Everything: true) },
                SevenZip.Find(),
                progress: log);

            log(created.Summary());

            if (!created.Ok) return false;

            try
            {
                File.Move(partial, plan.Target, overwrite: true);
                published = true;
            }
            catch (Exception exception)
            {
                log(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.ZipCreateFailedFormat,
                    Shell.DisplayMask.Path(plan.Target), exception.Message));

                return false;
            }

            return true;
        }
        finally
        {
            // ГАРАНТИЯ ПОСТРОЕНИЯ (t14): при любом отказе — упаковка, переименование, исключение —
            // временного файла не остаётся, а конечного имени без готового архива не появляется.
            if (!published)
            {
                try
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
                catch
                {
                    // Недописанный архив не предмет обновления: о неудаче уже сказано строкой журнала.
                }
            }
        }
    }

    /// <summary>
    /// ПОСТАВИТЬ ПО ЗАМКУ. Доводы Node берутся дверью <paramref name="node"/> (она уже добыла свой,
    /// если системный не годится): установщик зовёт её САМ, когда системный не подошёл, — так он
    /// решает, а не мы за него.
    /// </summary>
    private static EngineInstallResult Install(
        AppPaths paths,
        EngineTreeLocks locks,
        EngineHints hints,
        Func<EngineNodeResult> node,
        string version,
        Action<string> log)
    {
        // ⚠️ ДОРОГА ВЫБИРАЕТСЯ ПО ГЛАВНОМУ ЗАМКУ (правило уточнено 19.10.2026, выпуск 2.3.0):
        // главная дорога — когда названная версия и есть версия ГЛАВНОГО замка, и тогда установщик
        // сверит её с подсказками («две правды об одном дереве»). Всякая другая версия — дорога
        // отдельной просьбы: у неё свой замок, и проверяется он. Проверенных версий теперь ДВЕ
        // (0.2.1 — текущая, 0.1.5 — прежняя для отката), поэтому развилка по отметке «проверена»
        // отправила бы главную кнопку ставить 0.1.5, хотя подсказки рекомендуют 0.2.1.
        var requested = EngineInstallDecisions.RequestedFor(locks.Primary, version);

        return EngineInstaller.Install(
            locks.Primary,
            hints,
            paths.EngineDir,
            DshEngine.FindNode(),
            npmPath: null,
            EngineInstallDoors.Real,
            log,
            ownNode: node,
            recordPath: paths.EngineStateFile,
            locks: locks,
            requested: requested);
    }

    /// <summary>
    /// ПРОВЕРИТЬ ЖИВЬЁМ. Движок поднимается на **свободном** порту в **своём временном доме**
    /// (`state\engine-probe-<версия>`, потом убирается) и опрашивается той же пробой, что
    /// `--server-selftest`: объявления, вход, 84 имени, 8 умений.
    ///
    /// ⚠️ Порт владельца и профиль владельца не трогаются: проба не занимает 3080/3081 и не пишет
    /// в `~/.dsh`. Папка движка при этом остаётся на месте — её уборкой занимается уборка версий.
    /// </summary>
    private static bool Probe(
        AppPaths paths,
        Func<EngineNodeResult> node,
        string version,
        Action<string> log,
        Func<ServerController?> server,
        bool wasOurs)
    {
        if (!ProbeEngine(paths, node, version, log)) return false;

        // ВЕРНУТЬ ПАНЕЛЬ КАК БЫЛА: сервер работал до обновления — поднимаем его на новой версии.
        // Подъём идёт ПОСЛЕ удачной пробы (движок уже подтверждён живьём) и говорит о себе словами:
        // не поднялся — это не «обновление не сделано», но человек обязан это увидеть.
        if (wasOurs) Restart(server(), log, PanelStrings.EngineUpdateEngineStartedAgain);

        return true;
    }

    /// <summary>
    /// ПОДНЯТЬ СВОЙ СЕРВЕР ЗАНОВО — на той версии, что стоит сейчас (прежней, если обновление
    /// не началось; новой, если проба прошла). «Поднялось» проверяется состоянием, а не отсутствием
    /// исключения: движок мог отказать, и тогда об этом говорится словами.
    /// </summary>
    private static void Restart(ServerController? controller, Action<string> log, string words)
    {
        if (controller is null) return;

        ServerState state;

        try
        {
            state = controller.Start(RestartTimeout);
        }
        catch (Exception error)
        {
            log(PanelStrings.EngineUpdateEngineStartFailed + " " + error.GetType().Name);
            return;
        }

        log(state.IsRunning ? words : PanelStrings.EngineUpdateEngineStartFailed);
    }

    /// <summary>
    /// ПРОБА ВЕРСИИ БЕЗ ВОЗНИ С СЕРВЕРОМ: поднимает движок этой версии на свободном порту в своём
    /// временном доме и опрашивает той же пробой, что `--server-selftest`. Нужна и обновлению (перед
    /// подъёмом сервера заново), и откату (ДО всякой подмены: вернуться в нерабочее нельзя).
    /// </summary>
    private static bool ProbeEngine(AppPaths paths, Func<EngineNodeResult> node, string version, Action<string> log)
    {
        var bin = OwnEngine.BinPath(paths.EngineDir, version);

        if (bin.Length == 0 || !File.Exists(bin))
        {
            log(string.Format(CultureInfo.CurrentCulture, PanelStrings.EngineUpdateProbeNoEngineLogFormat, version));
            return false;
        }

        var ensured = node();
        var nodePath = ensured.Ok ? ensured.NodePath : DshEngine.FindNode();

        if (string.IsNullOrWhiteSpace(nodePath))
        {
            log(PanelStrings.EngineUpdateProbeNoNode);
            return false;
        }

        var home = Path.Combine(paths.StateDir, "engine-probe-" + version);

        var probe = EngineLiveProbe.Run(nodePath, bin, home, paths, EngineLiveProbeDoors.Real, log);

        // ОТВЕТ ПРОБЫ — В ЖУРНАЛ ВСЕГДА, а не только при неудаче: он говорит ещё и о том, убрала ли
        // проба за собой временный дом. «Успех с брошенным домом» человек обязан видеть.
        log(probe.Detail);

        return probe.Ok;
    }

    /// <summary>
    /// НАСТОЯЩИЕ ДВЕРИ ОТКАТА (пункт 4 плана E3.3): погасить свой движок, проверить прежнюю версию
    /// живьём, переписать запись «стоит сейчас» — и поднять сервер заново на той версии, что записана.
    ///
    /// **Порядок держит исполнитель** (<see cref="EngineRollbackRun"/>): проба ДО подмены, запись
    /// последней. Здесь только две вещи, которые об исполнителе знать не должны:
    ///
    /// 1. **откат ничего не устанавливает**: папка прежней версии уже на диске (её держит уборка
    ///    `EngineRetention` — «текущая и прежняя»), поэтому замок для неё не нужен вовсе;
    /// 2. **сервер поднимается заново ПОСЛЕ записи** и только если он работал: «поднять сервер
    ///    без просьбы» панель права не имеет, а оставить человека без сервера после отката — нельзя.
    /// </summary>
    public static EngineRollbackDoors Rollback(
        AppPaths paths,
        Func<ServerController?> server,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(log);

        var locks = EngineTreeLocks.Load();

        EngineNodeResult? node = null;

        EngineNodeResult Node()
        {
            if (node is not null) return node;

            var systemNode = DshEngine.FindNode();
            var systemVersion = DshPanel.Platform.VersionProbe.Run(systemNode, "--version");

            node = EngineNodeInstall.Ensure(
                paths.NodeDir,
                systemNode,
                systemVersion,
                EngineNode.StrictestLock(locks),
                EngineNodeDoors.Real(new Update.HttpUpdateDownload()),
                log);

            return node;
        }

        var wasOurs = false;

        return new EngineRollbackDoors(
            StopOwnEngine: port =>
            {
                var (ok, was) = StopOwn(server(), log);

                if (was) wasOurs = true;

                return ok;
            },
            Probe: version => ProbeEngine(paths, Node, version, log),
            WriteRecord: version =>
            {
                var before = EngineInstallRecord.Read(paths.EngineStateFile);
                var ensured = Node();

                var record = EngineInstallRecord.After(
                    version,
                    OwnEngine.VersionDir(paths.EngineDir, version),
                    ensured.Ok ? ensured : null,
                    before);

                if (!record.Write(paths.EngineStateFile, out var why))
                {
                    log(EngineInstallPhrases.RecordFailed(why));
                    return false;
                }

                // ЗАПИСЬ СТОИТ — ПОДНИМАЕМ СЕРВЕР ЗАНОВО на той версии, что записана теперь.
                if (wasOurs)
                    Restart(
                        server(),
                        log,
                        string.Format(CultureInfo.CurrentCulture, PanelStrings.EngineRollbackStartedAgainFormat, version));

                return true;
            });
    }
}
