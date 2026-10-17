using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DshPanel.Isolation;
using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// Самотест сервера на собранном exe. Отвечает на вопросы, которые иначе проверяются только
/// человеком с секундомером:
///
/// 1. **панель действительно поднимает DSH и действительно его гасит** (было с этапа 2);
/// 2. **панель не съезжает на другой порт молча**: занятый порт — это внятный отказ;
/// 3. **панель находит сервер, который поднимала не она** — по приметам владения из v1
///    (путь к <c>bin.js</c>, подкоманда <c>web</c>, порт в командной строке) и отпечатку;
/// 4. **без подтверждения встроенный сервер не гасится**, а с подтверждением — гасится;
/// 5. **поверхность RPC движка не сменилась** — закреплённый список имён сверяется и с описью,
///    которую движок объявляет у себя, и с живым движком, плюс один настоящий вызов
///    (см. <see cref="CheckEngineRpc"/>, список — <see cref="EngineRpcSurface"/>).
///
/// Идёт ТОЛЬКО в изоляции и только на своих свободных портах. Это не перестраховка: сервер
/// пишет в домашний каталог движка (профиль, кэш, состояние), и прогон без изоляции писал бы
/// в `~/.dsh` владельца. В v1 ровно так изолированный накат погасил его рабочий сервер.
///
/// Проверки 3 и 4 идут на НАСТОЯЩЕМ движке и на настоящем процессе — не на подставном: приметы
/// владения читаются из живой командной строки настоящего node, отпечаток берётся по-настоящему,
/// а гашение действительно убивает процесс. Гасится при этом только тот сервер, который поднял
/// сам самотест, в изолированном `DSH_HOME` и на своём порту.
/// </summary>
public static class ServerSelfTest
{
    /// <summary>
    /// Сколько ждать ответа сервера. С запасом: первый запуск в чистом профиле достраивает его
    /// около минуты (замер v1), а на медленной машине дольше.
    /// </summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(240);

    public static int Run(TimeSpan timeout)
    {
        var context = RunContext.Current;
        var report = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        if (!context.IsIsolated)
        {
            Console.WriteLine(
                "СЕРВЕР ПРОВАЛ: самотест поднимает настоящий движок, а тот пишет в домашний каталог " +
                "движка. Без изоляции это каталог владельца (~/.dsh). Запустите с «--run-root <свой путь>».");
            return 2;
        }

        var log = new List<string>();
        Func<string> workDir = () => Directory.CreateDirectory(context.Paths.DataDir).FullName;

        // Порт прогона: свободный. Так проверка не зависит от того, свободен ли 3081 на этой
        // машине, и не отбирает порт ни у человека, ни у соседнего прогона.
        Func<int> freePort = () =>
        {
            var port = FreePort.Find();
            return port == 0 ? ServerDecisions.DefaultServerPort : port;
        };

        var engine = DshEngine.Locate();
        Check("движок найден (node и @deepseek-ai/dsh)", engine is not null);
        if (engine is not null) report.Add($"  node: {engine.NodePath}");
        if (engine is not null) report.Add($"  движок: {engine.BinPath}");

        if (engine is null)
        {
            // Движка на машине нет — это НЕ поломка панели, а «проверять нечего»: проверка
            // по определению про запуск DSH, а запускать нечего. Код 2 — «неприменимо»,
            // так же, как у проверок, которым нужен рабочий стол человека.
            // Разница важна: 1 значит «панель сломана», и путать её с «чистой машиной» нельзя.
            report.Add("на этой машине нет Node и пакета @deepseek-ai/dsh — запускать нечего");
            Print(report, log);
            Console.WriteLine("СЕРВЕР НЕПРИМЕНИМО");
            return 2;
        }

        // --- (1) свой сервер: поднять и погасить -------------------------------------
        //
        // Право «поднимать рядом» здесь ПРОСЯТ ОТКРЫТЫМ ТЕКСТОМ, и это не формальность: прогон
        // изолированный (свой корень, свои данные) и поднимает движок рядом с тем, что уже
        // работает на машине. В обычном запуске такого права у панели нет — там она отказывается
        // (см. проверку 6).
        // ФАЙЛЫ СОСТОЯНИЯ СЕРВЕРА даются НАСТОЯЩИЕ, под корнем прогона, — ровно те, что получил бы
        // изолированный запуск панели. Это не «чтобы было»: без них самотест не проверил бы ни
        // записи «этот сервер наш», ни ссылки входа, а строка «сохранено» в журнале была бы ложью.
        var files = ServerStateFiles.Under(context.Paths, log.Add);

        var controller = new ServerController(
            context.Paths.DshHome, workDir, log.Add, mayOccupyOwnerPort: false,
            requestedPort: freePort, allowParallelStart: true, files: files);

        using (controller)
        {
            report.Add($"домашний каталог движка = {controller.DshHome}");
            Check("домашний каталог движка лежит под корнем прогона",
                controller.DshHome.StartsWith(
                    Path.TrimEndingDirectorySeparator(context.Paths.Root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

            var startedAt = Stopwatch.StartNew();
            var afterStart = controller.Start(timeout);
            startedAt.Stop();

            report.Add($"запуск занял {startedAt.Elapsed.TotalSeconds:F1} с, порт {afterStart.Port}");
            Check("сервер отвечает отпечатком", afterStart.IsRunning);
            Check($"порт не порт владельца ({ServerDecisions.OwnerPort})",
                ServerDecisions.IsAllowedPort(afterStart.Port, mayOccupyOwnerPort: false));
            Check("порт взят свободный и занят нашим процессом", afterStart.Pid > 0);
            Check("панель считает сервер своим", controller.Owner == ServerOwner.Panel);

            var port = afterStart.Port;

            if (!afterStart.IsRunning)
            {
                report.Add("состояние: " + afterStart.Detail);
                foreach (var line in controller.EngineTail()) report.Add("движок| " + line);
                Print(report, log);
                Console.WriteLine("СЕРВЕР ПРОВАЛ");
                return 1;
            }

            // Настоящая проверка «сервер жив»: тот же отпечаток, только глазами проверки.
            Check("состояние перечитывается как «работает»", controller.Refresh().IsRunning);

            // Токен запуска не должен оказаться в журнале панели: журнал — файл на диске,
            // и он уезжает в резервную копию.
            var leaked = log.Any(line => line.Contains("token=", StringComparison.OrdinalIgnoreCase)
                                         && !line.Contains("token=***", StringComparison.OrdinalIgnoreCase));
            Check("токен запуска в журнал не утёк", !leaked);

            // --- (1а) запись «этот сервер наш» и ССЫЛКА ВХОДА: настоящие файлы ------------
            //
            // Проверяется по ФАЙЛАМ, а не по словам: сервер поднят настоящий, движок напечатал
            // настоящую ссылку — значит и запись, и ссылка обязаны лежать на диске, под корнем
            // прогона. Это единственная проверка шага, которая видит секрет целиком и при этом
            // ничего из него не печатает.
            //
            // Ссылку движок печатает строкой вывода, а её читает отдельная нитка процесса: ждём
            // появления файла. Сервер к этому мгновению уже отвечает, так что ждать долго нечего.
            for (var wait = 0; wait < 50 && !File.Exists(context.Paths.EntryLinkFile); wait++) Thread.Sleep(100);

            var own = OwnServerRecord.Parse(
                File.Exists(context.Paths.OwnServerFile) ? File.ReadAllText(context.Paths.OwnServerFile) : string.Empty);
            Check("запись «этот сервер наш» сделана", own is not null);
            Check("в записи — порт и процесс поднятого сервера",
                own is { } record && record.Port == port && record.Pid == afterStart.Pid);

            var entry = EntryLinkDecisions.ReadFile(
                File.Exists(context.Paths.EntryLinkFile) ? File.ReadAllText(context.Paths.EntryLinkFile) : string.Empty);
            Check("ссылка входа сохранена и она про НАШ порт", !entry.IsEmpty && entry.Port == port);
            report.Add($"  ссылка входа: {EntryLinkDecisions.Redacted(entry.Url)}");

            // В памяти контроллера — та же ссылка, и она про этот порт: кнопка «Открыть панель»
            // берёт её отсюда, а не с диска.
            Check("контроллер отдаёт ту же ссылку", controller.EntryLink == entry.Url);

            // --- (1б) ПОВЕРХНОСТЬ RPC: закреплённый список против объявлений и живого движка ---
            //
            // Идёт ПОКА СЕРВЕР ЖИВ и сразу после ссылки входа: вход нужен телу пробы (cookie),
            // а ссылка живёт ровно столько, сколько живёт сервер (проверка 3 это и стережёт).
            var rpc = CheckEngineRpc(engine.BinPath, entry.Url, port, context.Paths);
            report.AddRange(rpc.Lines);
            if (!rpc.Ok) failed = true;

            // --- (2) разведка и встраивание: тот же сервер, но глазами ЧУЖОЙ панели -----
            //
            // «Чужая панель» — это второй контроллер, который сам ничего не поднимал: он ищет
            // сервер по приметам владения так же, как панель ищет DSH, поднятый человеком.
            var stranger = new ServerController(context.Paths.DshHome, workDir, log.Add, mayOccupyOwnerPort: false);
            using (stranger)
            {
                var found = stranger.Scan();
                Check("разведка прочитала таблицу портов", found.TableReadable);

                var mine = found.Found.FirstOrDefault(f => f.Port == port);
                Check($"разведка нашла сервер на порту {port} по приметам владения", mine.Port == port);
                report.Add($"  найдено серверов: {found.Found.Count}; приметы: PID {mine.Pid}, {mine.ProcessName}");

                if (mine.Port == port)
                {
                    var adopted = stranger.Adopt(mine);
                    Check("встраивание: панель считает сервер встроенным",
                        stranger.Owner == ServerOwner.Adopted);
                    Check("встраивание: сервер отвечает, состояние «работает»", adopted.IsRunning);

                    var refusedDoStop = stranger.Stop(confirmed: false);
                    Check("без подтверждения встроенный сервер НЕ гасится", refusedDoStop.IsRunning);
                    Check("без подтверждения панель говорит про подтверждение",
                        refusedDoStop.Detail.Contains("подтвержд", StringComparison.OrdinalIgnoreCase));
                    Check("без подтверждения порт держит прежний процесс",
                        PortTable.GetListenerPid(port) == adopted.Pid);

                    stranger.Detach();
                    Check("отвязка: панель больше ничем не управляет",
                        stranger.Owner == ServerOwner.None);
                    Check("отвязка сервер НЕ гасит: он всё ещё отвечает",
                        ServerDiscovery.AnswersFingerprint(port));
                }
            }

            // --- (3) свой сервер гасится свободно, без подтверждения -------------------
            var afterStop = controller.Stop(confirmed: false);
            Check("после остановки порт свободен",
                afterStop.Presence == ServerPresence.Stopped && PortTable.GetListenerPid(port) == 0);
            report.Add("состояние после остановки: " + afterStop.Detail);

            // Ссылка живёт ровно столько, сколько живёт сервер, и запись «этот сервер наш» — тоже:
            // мёртвый адрес и «свой» сервер, которого нет, человеку показывать незачем.
            Check("после остановки записи о своём сервере нет",
                !File.Exists(context.Paths.OwnServerFile));
            Check("после остановки ссылки входа нет",
                !File.Exists(context.Paths.EntryLinkFile));
        }

        // --- (4) занятый порт: отказ, а не молчаливый переезд ------------------------

        var busy = CheckBusyPortRefusal(context.Paths.DshHome, workDir, log.Add);
        report.AddRange(busy.Lines);
        if (!busy.Ok) failed = true;

        // --- (5) встроенный сервер гасится ТОЛЬКО с подтверждением -------------------

        var adoptedRun = CheckAdoptedStop(context.Paths.DshHome, workDir, log.Add, timeout, freePort);
        report.AddRange(adoptedRun.Lines);
        if (!adoptedRun.Ok) failed = true;

        // --- (6) замок от второго движка: при найденном чужом DSH свой не поднимается ---

        var lockRun = CheckParallelStartLock(context.Paths.DshHome, workDir, log.Add, timeout, freePort);
        report.AddRange(lockRun.Lines);
        if (!lockRun.Ok) failed = true;

        // --- (7) ЗАМКИ ДВИЖКА: мёртвый убран, живой не тронут, без права не трогаем ничего ---

        var engineLocks = CheckEngineLocks(context.Paths.DshHome, workDir, log.Add, freePort, files);
        report.AddRange(engineLocks.Lines);
        if (!engineLocks.Ok) failed = true;

        Print(report, log);

        // Проверяем, что проверка умеет падать: без этого «зелено» ничего не стоит.
        Console.WriteLine(failed ? "СЕРВЕР ПРОВАЛ" : "СЕРВЕР УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// ПОВЕРХНОСТЬ RPC ДВИЖКА — проба вынесена в ОДНУ дверь (<see cref="EngineRpcProbe.Measure"/>):
    /// её же прогоняет всякий, кто поставил НОВЫЙ движок (<see cref="EngineLiveProbe"/>), и второго
    /// списка проверок быть не должно. Здесь остался ровно вызов: порядок, тексты и смысл строк
    /// живут теперь в <see cref="EngineRpcProbe"/>, и вывод прогона от выноса не меняется ни на слово.
    /// </summary>
    private static (bool Ok, List<string> Lines) CheckEngineRpc(string binPath, string entryUrl, int port, AppPaths paths)
    {
        var result = EngineRpcProbe.Measure(binPath, entryUrl, port, paths);
        return (result.Ok, result.Lines.ToList());
    }

    /// <summary>
    /// Занятый порт должен давать внятный отказ и НЕ приводить к молчаливому переезду на другой
    /// порт: человек ищет свой сервер по адресу, и «порт тот, а сервера нет» хуже отказа.
    ///
    /// Движок при этом не запускается вовсе — отказ обязан случиться до него, и это тоже часть
    /// проверки. Порт занимаем у себя же, поэтому чужого ничего не трогаем.
    /// </summary>
    private static (bool Ok, List<string> Lines) CheckBusyPortRefusal(
        string dshHome, Func<string> workDir, Action<string> log)
    {
        var lines = new List<string>();
        var ok = true;

        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;

        try
        {
            var controller = new ServerController(
                dshHome, workDir, log, mayOccupyOwnerPort: false, requestedPort: () => port, allowParallelStart: true);

            using (controller)
            {
                var state = controller.Start(TimeSpan.FromSeconds(10));

                var refused = state.Presence != ServerPresence.Running
                              && state.Port == 0
                              && state.Detail.Contains("занят", StringComparison.OrdinalIgnoreCase);

                lines.Add($"занятый порт {port}: ответ «{state.Detail}»");
                lines.Add($"  отказ вместо переезда на другой порт = {refused}");
                lines.Add($"  порт в состоянии не назван (0) = {state.Port == 0}");
                if (!refused) ok = false;
            }
        }
        finally
        {
            busy.Stop();
        }

        return (ok, lines);
    }

    /// <summary>
    /// Полный путь встраивания на НАСТОЯЩЕМ сервере: поднять свой, найти его чужими глазами,
    /// встроиться и погасить — но только с подтверждением. Это и есть проверка решения владельца
    /// 24.09.2026 «гасить встроенный сервер только по отдельному подтверждению каждый раз».
    ///
    /// Без подтверждения проверка остановки написана отдельно, на подставном убийце (тесты без
    /// экрана): с настоящим она убила бы сервер, который проверке ещё нужен.
    /// </summary>
    private static (bool Ok, List<string> Lines) CheckAdoptedStop(
        string dshHome, Func<string> workDir, Action<string> log, TimeSpan timeout, Func<int> freePort)
    {
        var lines = new List<string>();
        var ok = true;

        var owner = new ServerController(
            dshHome, workDir, log, mayOccupyOwnerPort: false, requestedPort: freePort, allowParallelStart: true);

        var stranger = new ServerController(dshHome, workDir, log, mayOccupyOwnerPort: false);

        using (owner)
        using (stranger)
        {
            var started = owner.Start(timeout);
            if (!started.IsRunning)
            {
                lines.Add($"встроенный сервер поднять не удалось: {started.Detail}");
                return (false, lines);
            }

            var port = started.Port;
            var found = stranger.Scan().Found.FirstOrDefault(f => f.Port == port);

            if (found.Port != port)
            {
                lines.Add($"разведка не нашла сервер на порту {port} — встроиться не во что");
                owner.Stop(confirmed: false);
                return (false, lines);
            }

            var adopted = stranger.Adopt(found);
            lines.Add($"встроился в сервер на порту {port} (PID {adopted.Pid}) с подтверждения не спрашивая");

            var killed = stranger.Stop(confirmed: true);
            var released = PortTable.GetListenerPid(port) == 0;

            lines.Add($"после подтверждённой остановки: {killed.Detail}; порт свободен = {released}");
            lines.Add($"панель больше ничем не управляет = {stranger.Owner == ServerOwner.None}");

            if (killed.IsRunning || !released || stranger.Owner != ServerOwner.None) ok = false;

            // Сервер уже погашен через встроенный путь — своему контроллеру остаётся прибрать
            // дескриптор. Порт при этом обязан быть свободен и по его мнению.
            var afterOwnerStop = owner.Stop(confirmed: false);
            if (PortTable.GetListenerPid(port) != 0 || afterOwnerStop.Presence == ServerPresence.Running)
                ok = false;
        }

        return (ok, lines);
    }

    /// <summary>
    /// ЗАМОК ОТ ВТОРОГО ДВИЖКА на настоящем сервере: пока рядом работает найденный сервер DSH,
    /// панель свой не поднимает — и НЕ ЗАПУСКАЕТ процесс.
    ///
    /// «Чужой» сервер здесь — свой же сервер прогона, поднятый на своём свободном порту: так
    /// проверка не зависит от того, что сейчас запущено на машине, и на чистой ВМ проверяет
    /// ровно то же самое. Замок у второго контроллера — ЗНАЧЕНИЕ ПО УМОЛЧАНИЮ, права «поднимать
    /// рядом» ему никто не давал: проверяется именно то, что видит человек в обычном запуске.
    ///
    /// «Процесс не запускался» доказывается тем, что движок даже НЕ ИСКАЛИ: поиск движка стоит
    /// в запуске после замка, и найти его — обязательное условие запуска процесса. Считать это
    /// «наверное, не запустился» нельзя, поэтому счётчик попыток и стоит здесь.
    ///
    /// ⚠️ Чего эта проверка НЕ покрывает (названо, а не спрятано): обратного хода — «рядом никого,
    /// значит поднимаем». На этой машине его проверить нельзя: живой сервер владельца на 3080
    /// находится всегда, и замок честно срабатывает. Обратный ход проверяется тестами без экрана
    /// (подставная разведка), а на настоящем движке — правом `allowParallelStart` в проверках 1 и 5.
    /// </summary>
    private static (bool Ok, List<string> Lines) CheckParallelStartLock(
        string dshHome, Func<string> workDir, Action<string> log, TimeSpan timeout, Func<int> freePort)
    {
        var lines = new List<string>();
        var ok = true;

        var holder = new ServerController(
            dshHome, workDir, log, mayOccupyOwnerPort: false, requestedPort: freePort, allowParallelStart: true);

        using (holder)
        {
            var started = holder.Start(timeout);
            if (!started.IsRunning)
            {
                lines.Add($"сервер-держатель поднять не удалось: {started.Detail}");
                return (false, lines);
            }

            var port = started.Port;
            var engineLookups = 0;

            var locked = new ServerController(
                dshHome,
                workDir,
                log,
                mayOccupyOwnerPort: false,
                locateEngine: () => { engineLookups++; return null; },
                requestedPort: freePort);

            using (locked)
            {
                var refused = locked.Start(TimeSpan.FromSeconds(15));

                var named = refused.Detail.Contains("найден работающий DSH", StringComparison.OrdinalIgnoreCase);
                var saysNoSecond = refused.Detail.Contains("не поднимает", StringComparison.OrdinalIgnoreCase);
                var neverTriedToStart = engineLookups == 0;
                var holderAlive = ServerDiscovery.AnswersFingerprint(port);

                lines.Add($"при работающем сервере на порту {port} свой поднять: {refused.Detail}");
                lines.Add($"  отказ вместо второго движка = {!refused.IsRunning && named && saysNoSecond}");
                lines.Add($"  запуск даже не начинали: движок не искали = {neverTriedToStart}");
                lines.Add($"  найденный сервер жив и не тронут = {holderAlive}");

                if (refused.IsRunning || !named || !saysNoSecond || !neverTriedToStart || !holderAlive)
                    ok = false;

                if (locked.Owner != ServerOwner.None) ok = false;
            }

            var stopped = holder.Stop(confirmed: false);
            lines.Add($"держатель погашен: {stopped.Detail}");

            if (PortTable.GetListenerPid(port) != 0) ok = false;
        }

        return (ok, lines);
    }

    /// <summary>
    /// ЗАМКИ ДВИЖКА — на НАСТОЯЩИХ файлах в доме движка под корнем прогона: **мёртвый замок убран,
    /// живой не тронут, без права не трогаем ничего.**
    ///
    /// **Откуда правило (замер разведки этапа B, 10.10.2026).** Движок запирает запись файлом-соседом
    /// `&lt;файл&gt;.lock` с номером процесса внутри и снимает его сам — но только если процесс дожил
    /// до конца записи. Убитый владелец оставляет замок навсегда, и следующий писатель получает
    /// отказ по времени; сам движок свой замок не убирает НИКОГДА («a contender never deletes
    /// a lock… an operator removes it only after verifying that no writer still owns it»).
    ///
    /// **Почему проверяется ЗАПУСКОМ контроллера, а не вызовом помощника напрямую.** Уборка стоит
    /// в начале запуска сервера (<c>ServerController.Start</c>) — том самом месте, где её видит
    /// человек; помощник, позванный в обход, доказал бы, что умеет убирать, но не что панель это
    /// делает. Подделок здесь нет ни в чём: файлы настоящие, живость процессов настоящая,
    /// дом движка — под корнем прогона.
    /// </summary>
    private static (bool Ok, List<string> Lines) CheckEngineLocks(
        string dshHome, Func<string> workDir, Action<string> log, Func<int> freePort, ServerStateFiles files)
    {
        var lines = new List<string>();
        var ok = true;

        // МЁРТВЫЙ владелец — номер, которого не может быть ни у какого процесса. Так замок
        // «убитого держателя» получается детерминированным, без гонки за переиспользованный номер.
        var deadPid = int.MaxValue;

        // ЖИВОЙ владелец — свой процесс: он заведомо жив, и замок его неприкосновенен.
        var livePid = Environment.ProcessId;

        var deadPath = EngineLocks.LockPath(dshHome, "settings.yaml");
        var livePath = EngineLocks.LockPath(dshHome, ".credentials.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(deadPath)!);

        var deadNumber = deadPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var liveNumber = livePid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        lines.Add($"мёртвый владелец замка: номер {deadNumber} (жив = {ProcessFacts.LivenessOf(deadPid)})");
        lines.Add($"живой владелец замка: номер {liveNumber} (жив = {ProcessFacts.LivenessOf(livePid)})");

        try
        {
            // --- С ПРАВОМ: мёртвый убран, живой не тронут -------------------------------------
            File.WriteAllText(deadPath, deadNumber);
            File.WriteAllText(livePath, liveNumber);

            var allowed = new List<string>();

            using (var controller = new ServerController(
                       dshHome, workDir, allowed.Add, mayOccupyOwnerPort: false, requestedPort: freePort,
                       stopWait: TimeSpan.Zero, scanInterval: TimeSpan.Zero, files: files,
                       allowParallelStart: true, mayClearEngineLocks: true))
            {
                controller.Start(TimeSpan.FromSeconds(3));
                controller.Stop(confirmed: true);
            }

            var deadGone = !File.Exists(deadPath);
            var liveKept = File.Exists(livePath)
                           && File.ReadAllText(livePath).Trim() == liveNumber;

            // ⚠️ СЛОВА БЕРУТСЯ ИЗ СЛОВАРЯ, А НЕ ЛИТЕРАЛОМ (дефект «проверка не может пройти», пачка D
            // цикла C2). Здесь стояли строки «замок движка снят» и «замок движка не тронут», а
            // словарь давно говорит «проверенный набор версий сервера DSH снят / не тронут» — то
            // есть проверка сверяла фразу, КОТОРОЙ В СЛОВАРЕ НЕТ, и не могла пройти ни на одной
            // машине. Из-за неё же падала итоговая строка «проба на поставленном движке прошла»,
            // хотя сама установка и проба 84/8 проходили. Теперь сверяется НАЧАЛО той строки,
            // которой продукт и говорит (`EngineLockStaleRemovedLogFormat` / `EngineLockLiveLogFormat`):
            // поменяются слова — проверка пойдёт за ними, а не разойдётся с ними молча.
            var removedHead = Shell.PanelStrings.EngineLockStaleRemovedLogFormat.Split("{0}")[0].Trim().TrimEnd(':', '—').Trim();
            var liveHead = Shell.PanelStrings.EngineLockLiveLogFormat.Split("{0}")[0].Trim().TrimEnd(':', '—').Trim();

            var deadNamed = allowed.Any(line =>
                line.Contains(removedHead, StringComparison.Ordinal)
                && line.Contains(deadPath, StringComparison.Ordinal)
                && line.Contains(deadNumber, StringComparison.Ordinal));
            var liveNamed = allowed.Any(line =>
                line.Contains(liveHead, StringComparison.Ordinal)
                && line.Contains(livePath, StringComparison.Ordinal)
                && line.Contains(liveNumber, StringComparison.Ordinal));

            lines.Add($"  мёртвый замок УБРАН = {deadGone}");
            lines.Add($"  живой замок НЕ ТРОНУТ (в нём тот же номер) = {liveKept}");
            lines.Add($"  о мёртвом сказано словами: путь и номер = {deadNamed}");
            lines.Add($"  о живом сказано словами: путь и номер = {liveNamed}");

            if (!deadGone || !liveKept || !deadNamed || !liveNamed) ok = false;

            // --- БЕЗ ПРАВА: не трогаем ничего, и об этом сказано ------------------------------
            File.WriteAllText(deadPath, deadNumber);

            var blind = new List<string>();

            using (var controller = new ServerController(
                       dshHome, workDir, blind.Add, mayOccupyOwnerPort: false, requestedPort: freePort,
                       stopWait: TimeSpan.Zero, scanInterval: TimeSpan.Zero, files: files,
                       allowParallelStart: true, mayClearEngineLocks: false))
            {
                controller.Start(TimeSpan.FromSeconds(3));
                controller.Stop(confirmed: true);
            }

            var keptWithoutRight = File.Exists(deadPath)
                                   && File.ReadAllText(deadPath).Trim() == deadNumber;
            var saidNoRight = blind.Any(line =>
                line.Contains("права на данные панели", StringComparison.Ordinal));

            lines.Add($"  без права мёртвый замок ОСТАВЛЕН на месте = {keptWithoutRight}");
            lines.Add($"  без права об этом сказано строкой = {saidNoRight}");

            if (!keptWithoutRight || !saidNoRight) ok = false;
        }
        catch (Exception error)
        {
            lines.Add($"  опыт не состоялся: {error.GetType().Name} — {error.Message}");
            ok = false;
        }
        finally
        {
            // Прибрать за собой: файлы замков — наши, и держать их после проверки незачем.
            try { File.Delete(deadPath); } catch { }
            try { File.Delete(livePath); } catch { }
        }

        return (ok, lines);
    }

    private static void Print(List<string> report, List<string> log)
    {
        foreach (var line in report) Console.WriteLine("СЕРВЕР| " + line);
        foreach (var line in log) Console.WriteLine("ЖУРНАЛ| " + line);
    }
}