using DshPanel.Files;
using DshPanel.Isolation;

namespace DshPanel.Server;

/// <summary>Поднятый на пробу движок: удалось ли, на каком порту, какая ссылка входа, чем кончилось, хвост вывода.</summary>
public sealed record EngineLiveProbeRun(
    bool Started,
    int Port,
    string EntryUrl,
    string Detail,
    IReadOnlyList<string> Tail);

/// <summary>ДВЕРИ ЖИВОЙ ПРОБЫ: поднять движок на свободном порту с временным домом и погасить его.</summary>
public sealed record EngineLiveProbeDoors(
    Func<string, string, string, EngineLiveProbeRun> Start,  // (nodePath, binPath, homeRoot) -> поднять
    Action<int> Stop)                                        // порт -> погасить и убрать за собой
{
    /// <summary>Настоящие двери: свой ServerController, свободный порт, временный дом под homeRoot.</summary>
    public static EngineLiveProbeDoors Real { get; } = EngineLiveProbe.RealDoors();
}

/// <summary>Итог живой пробы.</summary>
public sealed record EngineLiveProbeResult(
    bool Ok,
    string Detail,
    string Version,
    int Recognised,
    int Total,
    IReadOnlyList<string> Lines);

/// <summary>
/// ЖИВАЯ ПРОБА ПОСТАВЛЕННОГО ДВИЖКА (этап E3.2): панель после установки новой версии обязана
/// проверить её ЖИВЬЁМ — тем же способом, что <c>--server-selftest</c> (нужные имена поверхности — 77,
/// и 8 умений), — но **не трогая ни профиль владельца, ни его порт**.
///
/// **Как это устроено.** Движок поднимается на **свободном** порту со **своим временным домом**
/// (<c>DSH_HOME</c> = <paramref name="homeRoot"/>, который даёт вызывающий), прогоняется
/// <see cref="EngineRpcProbe.Measure"/>, и всё за собой убирается. Порт владельца (3080) и его
/// профиль (<c>~/.dsh</c>) не трогаются НИКОГДА — это красная линия проекта, и она держится не
/// обещанием, а устройством: порт приходит из <see cref="FreePort"/>, дом — доводом, а файлы
/// состояния пробы лежат под этим домом (<see cref="ServerStateFiles.Under"/>), а не под файлами
/// панели. Иначе проба перезаписала бы <c>state\entry-link.txt</c> и <c>state\own-server.txt</c>
/// человека — ровно та беда, ради которой файлы состояния и сделаны делегатами.
///
/// **Почему проба, а не «поставилось и ладно».** Установка — единственное место, где панель
/// кладёт на машину чужой код. «Поставлено» без живой проверки означало бы, что о несовместимой
/// версии человек узнаёт работой, а не панелью; здесь же версия либо подтверждается пробой, либо
/// панель честно говорит «не проверено».
///
/// ⚠️ **Исключений наружу НЕ бросает: беда — это ответ.** И уборка за собой — тоже ответ:
/// дом убран или назван словами в <see cref="EngineLiveProbeResult.Detail"/>, а не забыт молча.
/// </summary>
public static class EngineLiveProbe
{
    /// <summary>
    /// Сколько строк журнала контроллера проба помнит для объяснения неудачи. Держать больше
    /// незачем: человеку нужна последняя причина, а не весь журнал запуска.
    /// </summary>
    private const int RememberedLogLines = 40;

    /// <summary>
    /// Двери, которые действительно поднимают движок. Живут здесь, а не в <see cref="EngineLiveProbeDoors"/>,
    /// по одной причине: им нужны и общее место пробы, и способ уборки — а это одно решение, и
    /// разойтись двум его половинам нельзя.
    /// </summary>
    internal static EngineLiveProbeDoors RealDoors() => new(RealStart, RealStop);

    /// <summary>Поднятые пробы: порт → дом и контроллер. Нужны ровно затем, чтобы <c>Stop</c> знал, что гасить.</summary>
    private sealed record Held(string Home, ServerController Controller);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Held> HeldByPort = new();

    /// <summary>
    /// ПОДНЯТЬ ДВИЖОК НА ПРОБУ — и НИКОГДА не бросить исключение: отказ здесь тоже ответ.
    ///
    /// Дом пробы сначала **расчищается**: имя папки у вызывающего постоянное
    /// (<c>state\engine-probe-&lt;версия&gt;</c>), и брошенный прошлой пробой дом (панель убили
    /// посреди пробы) иначе остался бы — движок увидел бы чужой профиль, а проба проверяла бы
    /// не то, что поставила. Убирает ОДНА дверь проекта — <see cref="TreeDeletion"/>.
    ///
    /// Контроллер заводится на СВОБОДНОМ порту (<see cref="FreePort"/>) и с правом «поднимать
    /// рядом»: прогон изолирован своим домом, а на машине в это время работает сервер человека.
    /// Права на порт владельца у пробы нет вовсе (<c>mayOccupyOwnerPort: false</c>) — и это не
    /// формальность: без права контроллер привёл бы 3080 к 3081, а проба не имеет права трогать
    /// и его.
    /// </summary>
    internal static EngineLiveProbeRun RealStart(string nodePath, string binPath, string homeRoot)
    {
        var log = new List<string>();

        void Note(string line)
        {
            log.Add(line);
            if (log.Count > RememberedLogLines) log.RemoveAt(0);
        }

        var home = (homeRoot ?? string.Empty).Trim();
        ServerController? controller = null;
        var port = 0;

        try
        {
            if (home.Length == 0)
                return new EngineLiveProbeRun(false, 0, string.Empty, "места для временного дома пробы не названо", log);

            var swept = TreeDeletion.Remove(home);
            if (!swept.Ok)
            {
                return new EngineLiveProbeRun(
                    false, 0, string.Empty,
                    $"временный дом пробы не расчистился: {swept.Reason}", log);
            }

            Directory.CreateDirectory(home);

            var probePaths = AppPaths.Under(home);
            var files = ServerStateFiles.Under(probePaths, Note);

            port = FreePort.Find();
            if (port == 0)
                return new EngineLiveProbeRun(false, 0, string.Empty, "свободного порта для пробы не нашлось", log);

            // ЗАПРОШЕННЫЙ порт закрепляется здесь же: контроллер спрашивает его функцией в момент
            // запуска, а двери обязаны помнить ТОТ ЖЕ номер — по нему Stop и находит, что гасить.
            var requested = port;

            controller = new ServerController(
                home,                                          // DSH_HOME — свой временный дом
                () => Directory.CreateDirectory(probePaths.DataDir).FullName,
                Note,
                mayOccupyOwnerPort: false,
                locateEngine: () => new DshEngine(nodePath, binPath),
                requestedPort: () => requested,
                allowParallelStart: true,
                files: files);

            // Запоминается ДО запуска: если запуск сорвётся на середине (движок уже поднимается),
            // гасить и убирать всё равно надо, а узнать об этом можно только отсюда.
            lock (Gate) HeldByPort[port] = new Held(home, controller);

            var state = controller.Start(ServerSelfTest.DefaultTimeout);

            // ССЫЛКА ВХОДА: движок печатает её строкой вывода, а читает отдельная нитка процесса —
            // поэтому ждём ПОЯВЛЕНИЯ ФАЙЛА, ровно как это делает самотест сервера.
            for (var wait = 0; wait < 50 && !File.Exists(probePaths.EntryLinkFile); wait++) Thread.Sleep(100);

            var entry = EntryLinkDecisions.ReadFile(ServerStateFiles.ReadText(probePaths.EntryLinkFile));

            if (state.IsRunning)
                return new EngineLiveProbeRun(true, port, entry.Url, state.Detail, controller.EngineTail());

            // НЕ ПОДНЯЛСЯ: слова контроллера плюс последняя строка его журнала — по ней видно
            // причину (движка нет, порт занят, движок отказал), и Run повторит это человеку.
            var why = state.Detail;
            if (log.Count > 0) why = why.Length > 0 ? why + "; " + log[^1] : log[^1];

            return new EngineLiveProbeRun(false, port, string.Empty, why, controller.EngineTail());
        }
        catch (Exception error)
        {
            // Проба, которой не удалось даже завести контроллер, убирает за собой САМА: у Run тогда
            // нет порта, а значит и Stop не найдёт, что гасить.
            if (controller is null)
            {
                try { TreeDeletion.Remove(home); } catch { /* уборка уже не удалась — скажет Run */ }
            }

            return new EngineLiveProbeRun(
                false, port, string.Empty,
                $"поднять движок на пробу не удалось: {error.GetType().Name} — {error.Message}", log);
        }
    }

    /// <summary>
    /// ПОГАСИТЬ И УБРАТЬ ЗА СОБОЙ. Порт — тот, что назвала <see cref="RealStart"/>.
    ///
    /// Порядок обязателен: сначала гасится движок (иначе он держал бы файлы своего дома и уборка
    /// не удалась бы), потом снимается дерево — и только <see cref="TreeDeletion"/>, потому что
    /// второе место удаления дерева в проекте запрещено сторожем.
    ///
    /// ⚠️ **Не удалось убрать — это ОТКАЗ, а не молчание.** Делегат не умеет вернуть ответ, поэтому
    /// о неудаче уборки двери говорят исключением, а <see cref="Run"/> ловит его и превращает
    /// в строку <see cref="EngineLiveProbeResult.Detail"/>: брошенный временный дом на диске —
    /// это то, о чём человек обязан узнать.
    /// </summary>
    internal static void RealStop(int port)
    {
        Held? held;

        lock (Gate)
        {
            if (!HeldByPort.TryGetValue(port, out held)) return;
            HeldByPort.Remove(port);
        }

        try
        {
            held.Controller.Stop(confirmed: false);
        }
        finally
        {
            held.Controller.Dispose();
        }

        var removed = TreeDeletion.Remove(held.Home);
        if (!removed.Ok)
            throw new IOException($"временный дом пробы не убран ({held.Home}): {removed.Reason}");
    }

    /// <summary>
    /// Проверить поставленный движок живьём. Исключений НЕ бросает: беда — это ответ.
    ///
    /// <paramref name="doors"/> — шов для проверок: ими подставляется и отказ подъёма, и подложный
    /// движок. <paramref name="progress"/> — куда говорить ход пробы (окно панели показывает его
    /// человеку); те же строки возвращаются в <see cref="EngineLiveProbeResult.Lines"/>.
    /// </summary>
    public static EngineLiveProbeResult Run(
        string? nodePath,
        string? binPath,
        string? homeRoot,
        AppPaths paths,
        EngineLiveProbeDoors? doors = null,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        doors ??= EngineLiveProbeDoors.Real;

        var lines = new List<string>();

        void Say(string text)
        {
            lines.Add(text);
            progress?.Invoke(text);
        }

        // (1) НЕЧЕМ ПРОВЕРЯТЬ: пустой довод — это ответ, и до дверей дело не доходит вовсе.
        if (string.IsNullOrWhiteSpace(nodePath)) return Refuse("нечем проверять: нет Node", lines);
        if (string.IsNullOrWhiteSpace(binPath)) return Refuse("нечем проверять: нет движка", lines);
        if (string.IsNullOrWhiteSpace(homeRoot)) return Refuse("нечем проверять: нет места", lines);

        // (1а) МЕСТО ПРОБЫ — СВОЁ, И ЭТО НЕ ПРИДИРКА. Проба ВЫЧИЩАЕТ своё место перед подъёмом
        // (иначе брошенный прошлой пробой дом дал бы движку чужой профиль) и удаляет его целиком
        // после. Значит место пробы не имеет права быть корнем панели, домом движка или их предком:
        // одна опечатка в вызывающем снесла бы человеку профиль. Цена проверки — три строки,
        // цена ошибки — данные человека, поэтому проверка стоит здесь, а не в «осторожном» вызове.
        var place = homeRoot.Trim();

        if (!OwnPlace(place, paths))
        {
            var why = "место пробы не годится: это корень панели, дом движка или их предок — пробе нужно СВОЁ место";

            Say(why);
            return new EngineLiveProbeResult(false, why, string.Empty, 0, EngineRpcSurface.RequiredTotal, lines);
        }

        // (2) ОБЪЯВЛЕНИЯ: движок не читается — проверять нечего, и поднимать его незачем.
        //     Версия при этом возвращается: «объявлений нет» — это про имена, а не про версию.
        var declaration = EngineRpcDeclarations.Read(binPath);

        if (declaration.Endpoints.Count == 0)
        {
            var unreadable = "нечем проверять: движок не читается — объявлений поверхности нет";

            Say($"{unreadable} ({declaration.Detail})");
            return new EngineLiveProbeResult(
                false,
                unreadable,
                declaration.Version,
                0,
                EngineRpcSurface.RequiredTotal,
                lines);
        }

        var home = homeRoot.Trim();
        var version = declaration.Version;
        var recognised = 0;
        var total = EngineRpcSurface.RequiredTotal;
        var ok = false;
        var port = 0;
        var detail = string.Empty;
        var cleanup = string.Empty;

        try
        {
            EngineLiveProbeRun run;

            try
            {
                run = doors.Start(nodePath.Trim(), binPath.Trim(), home);
            }
            catch (Exception error)
            {
                // Двери обязаны отвечать, а не падать; но проба не имеет права уронить панель даже
                // тогда — беда двери читается как отказ пробы.
                run = new EngineLiveProbeRun(
                    false, 0, string.Empty,
                    $"поднять движок на пробу не удалось: {error.GetType().Name} — {error.Message}",
                    Array.Empty<string>());
            }

            port = run.Port;

            foreach (var line in run.Tail) Say("движок| " + line);

            if (!run.Started)
            {
                // ПОДНЯТЬСЯ НЕ УДАЛОСЬ: слова двери (и её хвост) — это ответ, а не повод молчать.
                detail = run.Detail.Length > 0 ? run.Detail : "движок на пробу не поднялся";
            }
            else
            {
                Say($"движок поднят на свободном порту {run.Port}; временный дом: {home}");

                if (run.EntryUrl.Length > 0)
                    Say("ссылка входа получена: " + EntryLinkDecisions.Redacted(run.EntryUrl));

                var measured = EngineRpcProbe.Measure(binPath, run.EntryUrl, run.Port, paths);
                foreach (var line in measured.Lines) Say(line);

                ok = measured.Ok;
                recognised = measured.Recognised;
                total = measured.Total;

                // ВЕРСИЯ — ИЗ ОБЪЯВЛЕНИЙ, и это не придирка: объявления читает и панель (по ним она
                // решает, какую версию ставить), и проба. Разошлись — говорим об этом строкой,
                // а не выбираем молча то, что удобнее.
                if (measured.Version.Length > 0 && !string.Equals(measured.Version, version, StringComparison.Ordinal))
                {
                    Say($"версия объявлений и версия пробы разошлись: «{version}» против «{measured.Version}» — беру версию объявлений");
                }

                detail = ok
                    ? $"движок проверен живьём на порту {run.Port}: версия «{version}», признано {recognised} из {total}"
                    : $"проба поверхности не прошла на порту {run.Port}: признано {recognised} из {total}";
            }
        }
        catch (Exception error)
        {
            // Сюда попадает только то, чего мы не ждали: Measure исключений не бросает, а двери —
            // обязаны. Проба всё равно отвечает, а не падает.
            ok = false;
            detail = $"проба не состоялась: {error.GetType().Name} — {error.Message}";
        }
        finally
        {
            // УБОРКА — ВСЕГДА, и в первую очередь после НЕУДАЧНОГО подъёма: не погашенный движок
            // остался бы на машине чужим процессом, а временный дом — вторым профилем на диске.
            try
            {
                doors.Stop(port);
            }
            catch (Exception error)
            {
                cleanup = $"убрать за собой не удалось: {error.GetType().Name} — {error.Message}";
            }

            // «Убрали» — ПРОВЕРЕННЫЙ факт, а не отсутствие исключения: дом уборки либо исчез,
            // либо нет, и это разные правды (ровно так же судит об уборке `TreeDeletion`).
            var left = Directory.Exists(home);

            if (left)
            {
                cleanup = cleanup.Length > 0
                    ? cleanup + $"; временный дом остался: {home}"
                    : $"временный дом пробы остался на месте: {home}";
            }

            Say($"временный дом пробы убран = {!left}");
            if (cleanup.Length > 0) Say(cleanup);
        }

        // Уборка — часть ответа: она в `Detail`, даже когда сама проба прошла.
        if (cleanup.Length > 0) detail = detail.Length > 0 ? detail + "; " + cleanup : cleanup;

        return new EngineLiveProbeResult(ok, detail, version, recognised, total, lines);
    }

    /// <summary>Отказ «нечем проверять»: строка — она же причина, и до машины дело не доходит.</summary>
    private static EngineLiveProbeResult Refuse(string detail, List<string> lines)
    {
        lines.Add(detail);
        return new EngineLiveProbeResult(
            false, detail, string.Empty, 0, EngineRpcSurface.RequiredTotal, lines);
    }

    /// <summary>
    /// МЕСТО ПРОБЫ СВОЁ: не корень панели, не дом движка и не предок ни того, ни другого (предок —
    /// потому что уборка снимает дерево целиком, а вместе с предком исчезло бы и всё внутри него).
    /// </summary>
    private static bool OwnPlace(string home, Isolation.AppPaths paths) =>
        !Covers(home, paths.Root) && !Covers(home, paths.DshHome);

    /// <summary><paramref name="home"/> равен <paramref name="target"/> или лежит выше него.</summary>
    private static bool Covers(string? home, string? target)
    {
        var outer = Full(home);
        var inner = Full(target);

        if (outer.Length == 0 || inner.Length == 0) return false;
        if (string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase)) return true;

        return inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Полный путь без хвостового разделителя. Не разобрался — пусто (тогда не «покрывает»).</summary>
    private static string Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return string.Empty;
        }
    }
}
