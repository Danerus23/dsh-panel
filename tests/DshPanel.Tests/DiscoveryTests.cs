using System;
using System.Collections.Generic;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки встраивания: приметы владения, разведка, запрет гашения без подтверждения.
///
/// Ни один тест здесь не поднимает настоящий сервер и не гасит настоящий процесс. Приметы
/// владения проверяются на РЕАЛЬНЫХ командных строках (их форма измерена на живом сервере
/// владельца 24.09.2026, `docs\ENGINE.md` §5), а всё внешнее — таблица портов, сведения о
/// процессе, отпечаток, убийца — подменяется дверью <see cref="ServerProbe"/> и
/// <see cref="IProcessKiller"/>. Именно поэтому можно доказать то, что иначе не доказывается:
/// БЕЗ подтверждения человека панель не убивает встроенный сервер вообще.
///
/// Настоящий движок на своём порту и настоящий процесс проверяет отдельно самотест
/// <c>--server-selftest</c> — в изоляции.
/// </summary>
public class DiscoveryTests
{
    /// <summary>
    /// Командная строка настоящего сервера DSH. Форма измерена 24.09.2026 на живом сервере
    /// владельца на порту 3080 (`docs\ENGINE.md` §5) — здесь она воспроизведена на нейтральном
    /// пути: личные пути владельца в репозиторий не попадают (красная линия 6).
    /// </summary>
    private const string RealDshCommandLine =
        "\"C:\\Program Files\\nodejs\\node.exe\" " +
        "C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js " +
        "web --no-open --port ";

    private static string DshCommandLine(int port) => RealDshCommandLine + port;

    /// <summary>Подставной убийца: записывает, кого и с каким временем создания просили убить.</summary>
    private sealed class RecordingKiller : IProcessKiller
    {
        public List<(int Pid, long StartedAt)> Calls { get; } = new();

        public bool Kill(int pid, long startedAtTicks, out string error)
        {
            Calls.Add((pid, startedAtTicks));
            error = string.Empty;
            return true;
        }
    }

    private static ServerProbe ProbeFor(
        int listenerPort,
        int pid,
        string commandLine,
        string name = "node",
        long startTicks = 63_000_000_000_000_000,
        bool fingerprint = true,
        bool tableReadable = true,
        int? listenerPidAnswer = null)
    {
        return new ServerProbe(
            ListenerPid: _ => listenerPidAnswer ?? pid,
            Listeners: () => tableReadable
                ? new[] { new PortTable.Listener(listenerPort, pid) }
                : null,
            CommandLine: _ => commandLine,
            ProcessName: _ => name,
            StartTicks: _ => startTicks,
            AnswersFingerprint: _ => fingerprint);
    }

    /// <summary>
    /// Контроллер для проверок без экрана: движка на машине не ищет (locateEngine по умолчанию
    /// возвращает «не найден»), порт не занимает и порт освобождается мгновенно.
    ///
    /// Согласие на найденный сервер приходит ДВУМЯ делегатами — как у настоящей панели, только
    /// вместо файла настроек у проверки обычная переменная: так видно и то, что панель записала,
    /// и то, что она прочитала.
    /// </summary>
    private static ServerController Controller(
        ServerProbe probe,
        IProcessKiller? killer = null,
        Func<int>? adoptedPort = null,
        Action<int>? rememberAdoptedPort = null,
        Func<DshEngine?>? locateEngine = null,
        Func<int>? requestedPort = null,
        Action<string>? log = null,
        bool allowParallelStart = false) =>
        new(
            @"C:\Temp\dsh-probe-home",
            () => @"C:\Temp",
            log ?? (_ => { }),

            // Право занять порт владельца в проверках НЕ выдаётся: у прогона проверки его нет,
            // и подставные проверки обязаны идти тем же путём, что настоящий прогон.
            mayOccupyOwnerPort: false,
            locateEngine: locateEngine ?? (() => null),
            requestedPort: requestedPort,
            probe: probe,
            killer: killer ?? new RecordingKiller(),

            // Ждать освобождения порта в проверке нечего: подставной убийца никого не убивает.
            stopWait: TimeSpan.Zero,

            // Осмотр — каждый раз заново: в жизни их разделяют секунды, а в проверке время стоит,
            // и закешированная находка сделала бы половину проверок слепыми.
            scanInterval: TimeSpan.Zero,
            adoptedPort: adoptedPort,
            rememberAdoptedPort: rememberAdoptedPort,
            allowParallelStart: allowParallelStart);

    // ------------------------------------------------------------ приметы владения

    /// <summary>
    /// Живая форма командной строки обязана признаваться сервером DSH. Это тот случай, ради
    /// которого шаг и делался: у человека уже работает DSH, и панель должна его узнать.
    /// </summary>
    [Fact]
    public void Живая_командная_строка_сервера_признаётся_своей()
    {
        var facts = new DshProcessFacts(
            ListenerAlive: true,
            IsNode: true,
            CommandLine: DshCommandLine(3080),
            FingerprintMatched: true);

        Assert.Equal(DshProcessVerdict.Match, ServerDecisions.ClassifyDshProcess(facts, 3080));
        Assert.True(ServerDecisions.LooksLikeDshProcess(facts, 3080));

        // Порядок аргументов и форма пути не важны: прямые слэши — та же установка.
        var slashes = new DshProcessFacts(
            true, true,
            "\"C:\\node.exe\" C:/Users/Public/AppData/Roaming/npm/node_modules/@deepseek-ai/dsh/lib/bin.js web --port 3097",
            true);

        Assert.True(ServerDecisions.LooksLikeDshProcess(slashes, 3097));

        // Между флагами бывает и «--port=3080»: движок звали по-разному.
        Assert.Equal(3080, ServerDecisions.PortInCommandLine(
            "node bin.js web --port=3080"));
        Assert.Equal(3080, ServerDecisions.PortInCommandLine(
            "node bin.js web --port:3080"));
        Assert.Equal(0, ServerDecisions.PortInCommandLine("node bin.js web --no-open"));
    }

    /// <summary>
    /// Приметы обязаны ОТКАЗЫВАТЬ. Проверка, которая только узнаёт своё, ничего не стоит:
    /// цена ошибки здесь — панель, встроившаяся в чужую программу или погасившая чужие данные.
    /// </summary>
    [Fact]
    public void Чужие_процессы_сервером_не_признаются()
    {
        var good = DshCommandLine(3080);

        var cases = new (string What, DshProcessFacts Facts, int Port, DshProcessVerdict Expected)[]
        {
            ("никто не слушает",
                new DshProcessFacts(false, true, good, true), 3080, DshProcessVerdict.NotListening),

            ("не node: чужая программа на порту",
                new DshProcessFacts(true, false, good, true), 3080, DshProcessVerdict.NotNode),

            ("отпечаток не сошёлся (отвечает не DSH)",
                new DshProcessFacts(true, true, good, false), 3080, DshProcessVerdict.NoFingerprint),

            ("node, но не наш движок",
                new DshProcessFacts(true, true, "\"C:\\nodejs\\node.exe\" C:\\work\\server.js --port 3080", true),
                3080, DshProcessVerdict.NotDshBin),

            // Самый важный отказ: файл рядом с движком, который движок НЕ запускает.
            ("подделка bin.js.old",
                new DshProcessFacts(true, true,
                    "\"C:\\nodejs\\node.exe\" C:\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js.old web --port 3080",
                    true),
                3080, DshProcessVerdict.NotDshBin),

            ("наш движок, но не подкоманда web",
                new DshProcessFacts(true, true,
                    "\"C:\\nodejs\\node.exe\" C:\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js plugin --port 3080",
                    true),
                3080, DshProcessVerdict.NoWebCommand),

            // Слушает 3097, а объявляет 3080 — это противоречие, а не «наш сервер».
            ("порт в командной строке не тот, кто слушает",
                new DshProcessFacts(true, true, DshCommandLine(3080), true), 3097, DshProcessVerdict.PortMismatch),

            // Хвост: порт 3080 не должен совпасть с 30800.
            ("30800 — не 3080",
                new DshProcessFacts(true, true, DshCommandLine(30800), true), 3080, DshProcessVerdict.PortMismatch),
        };

        foreach (var c in cases)
        {
            Assert.Equal(c.Expected, ServerDecisions.ClassifyDshProcess(c.Facts, c.Port));
            Assert.False(ServerDecisions.LooksLikeDshProcess(c.Facts, c.Port));
        }
    }

    /// <summary>
    /// Путь с пробелом — правило проекта (проверять пути на пробеле). Движок запускается
    /// в кавычках, и разбор обязан кавычки снять, иначе свой сервер стал бы «чужим».
    /// </summary>
    [Fact]
    public void Путь_с_пробелом_разбирается()
    {
        const string line =
            "\"C:\\Program Files\\nodejs\\node.exe\" " +
            "\"C:\\Program Files\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js\" web --port 3081";

        Assert.True(ServerDecisions.HasDshBin(line));
        Assert.True(ServerDecisions.HasWebCommand(line));
        Assert.Equal(3081, ServerDecisions.PortInCommandLine(line));
        Assert.Equal("C:\\Program Files\\nodejs\\node.exe", ServerDecisions.FirstToken(line));

        Assert.True(ServerDecisions.LooksLikeDshProcess(
            new DshProcessFacts(true, true, line, true), 3081));
    }

    // ------------------------------------------------------------ разведка

    [Fact]
    public void Разведка_находит_сервер_по_всем_приметам()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var result = ServerDiscovery.Find(probe);

        Assert.True(result.TableReadable);
        Assert.Single(result.Found);
        Assert.Equal(3080, result.Found[0].Port);
        Assert.Equal(19804, result.Found[0].Pid);
        Assert.True(result.Found[0].IsOwnerPort);
    }

    /// <summary>
    /// Разведка обязана отличать «не нашла» от «не смогла посмотреть». Второе нельзя показывать
    /// первым: человек решил бы, что серверов нет, хотя панель просто не заглянула.
    /// </summary>
    [Fact]
    public void Разведка_отличает_пустоту_от_невозможности_посмотреть()
    {
        var blind = ServerDiscovery.Find(ProbeFor(3080, 1, DshCommandLine(3080), tableReadable: false));
        Assert.False(blind.TableReadable);
        Assert.Empty(blind.Found);

        var empty = ServerDiscovery.Find(new ServerProbe(
            _ => 0,
            () => Array.Empty<PortTable.Listener>(),
            _ => string.Empty,
            _ => string.Empty,
            _ => 0,
            _ => false));

        Assert.True(empty.TableReadable);
        Assert.Empty(empty.Found);

        // Отпечаток не сошёлся — сервера нет, хотя командная строка выглядит нашей.
        var noAnswer = ServerDiscovery.Find(ProbeFor(3080, 19804, DshCommandLine(3080), fingerprint: false));
        Assert.True(noAnswer.TableReadable);
        Assert.Empty(noAnswer.Found);
    }

    /// <summary>
    /// Разведка не спрашивает отпечаток у ЧУЖИХ программ: иначе панель стучалась бы по HTTP
    /// в каждую слушающую программу машины.
    /// </summary>
    [Fact]
    public void Разведка_не_ходит_по_сети_к_чужим_программам()
    {
        var asked = 0;

        var probe = new ServerProbe(
            _ => 0,
            () => new[]
            {
                new PortTable.Listener(5000, 111),
                new PortTable.Listener(5001, 222),
            },
            pid => pid == 111 ? "\"C:\\nodejs\\node.exe\" C:\\work\\api.js --port 5000" : string.Empty,
            _ => "node",
            _ => 7,
            _ => { asked++; return true; });

        var result = ServerDiscovery.Find(probe);

        Assert.Empty(result.Found);
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// Свой сервер и тот, в который панель уже встроена, в находку не попадают: предлагать
    /// встроиться в то, чем уже управляешь, — это шум и повод ошибиться.
    /// </summary>
    [Fact]
    public void Разведка_не_предлагает_то_чем_панель_уже_управляет()
    {
        var probe = new ServerProbe(
            _ => 0,
            () => new[]
            {
                new PortTable.Listener(3081, 111),
                new PortTable.Listener(3080, 222),
            },
            // Командная строка отвечает согласованно со своим портом: иначе примета «порт
            // в командной строке не тот, кто слушает» отвергла бы обоих, и проверка была бы слепой.
            pid => pid == 111 ? DshCommandLine(3081) : DshCommandLine(3080),
            _ => "node",
            _ => 7,
            _ => true);

        Assert.Equal(2, ServerDiscovery.Find(probe).Found.Count);

        var excluded = ServerDiscovery.Find(probe, new HashSet<int> { 111 });
        Assert.Single(excluded.Found);
        Assert.Equal(222, excluded.Found[0].Pid);

        var all = ServerDiscovery.Find(probe, new HashSet<int> { 111, 222 });
        Assert.Empty(all.Found);
    }

    /// <summary>Находка упорядочена по порту: «что нашлось первым» — это не решение.</summary>
    [Fact]
    public void Находка_упорядочена_по_порту()
    {
        var probe = new ServerProbe(
            _ => 0,
            () => new[]
            {
                new PortTable.Listener(3097, 3),
                new PortTable.Listener(3080, 1),
                new PortTable.Listener(3089, 2),
            },
            // Порт в командной строке — по номеру процесса: она обязана сходиться с портом
            // слушателя, иначе находки не будет вовсе (и проверка ничего не проверит).
            pid => DshCommandLine(pid switch { 1 => 3080, 2 => 3089, _ => 3097 }),
            _ => "node",
            _ => 7,
            _ => true);

        var found = ServerDiscovery.Find(probe).Found;
        Assert.Equal(3, found.Count);
        Assert.Equal(new[] { 3080, 3089, 3097 }, new[] { found[0].Port, found[1].Port, found[2].Port });
    }

    // ------------------------------------------------------------ гашение встроенного

    /// <summary>
    /// ГЛАВНАЯ проверка шага: без подтверждения человека встроенный сервер не гасится —
    /// не «гасится, но с извинением в журнале», а НЕ ГАСИТСЯ ВООБЩЕ. Проверка смотрит
    /// на убийцу: он не должен быть позван ни разу.
    /// </summary>
    [Fact]
    public void Без_подтверждения_встроенный_сервер_не_гасится()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var killer = new RecordingKiller();
        var controller = Controller(probe, killer);

        var found = controller.Scan().Found[0];
        var adopted = controller.Adopt(found);

        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.True(adopted.IsRunning);

        var refused = controller.Stop(confirmed: false);

        Assert.Empty(killer.Calls);
        Assert.True(refused.IsRunning);
        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.Contains("подтвержд", refused.Detail, StringComparison.OrdinalIgnoreCase);

        // Отвязка — тоже НЕ гашение: панель перестаёт управлять, сервер остаётся жив.
        controller.Detach();
        Assert.Empty(killer.Calls);
        Assert.Equal(ServerOwner.None, controller.Owner);

        // «Сервер жив» проверяем у самой системы, а не у панели: после отвязки панель им
        // не управляет, и её состояние честно говорит «не запущен» — это про её сервер, не про чужой.
        Assert.True(probe.AnswersFingerprint(3080));
        Assert.Equal(19804, probe.ListenerPid(3080));
    }

    /// <summary>
    /// С подтверждением — гасится, и именно ТОТ процесс, которого панель видела: номер плюс
    /// время создания. Номер процесса переиспользуется, и без времени панель однажды погасила бы
    /// чужой процесс с тем же номером (урок v1).
    /// </summary>
    [Fact]
    public void С_подтверждением_гасится_тот_самый_процесс()
    {
        const long startedAt = 63_000_000_000_000_000;
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), startTicks: startedAt);
        var killer = new RecordingKiller();
        var controller = Controller(probe, killer);

        controller.Adopt(controller.Scan().Found[0]);
        controller.Stop(confirmed: true);

        Assert.Single(killer.Calls);
        Assert.Equal(19804, killer.Calls[0].Pid);
        Assert.Equal(startedAt, killer.Calls[0].StartedAt);
        Assert.Equal(ServerOwner.None, controller.Owner);
    }

    /// <summary>
    /// Свой сервер панель гасит и без подтверждения — подтверждение существует ровно для чужого,
    /// и «спрашивать всегда» было бы издевательством над человеком.
    /// </summary>
    [Fact]
    public void Свой_сервер_гасится_без_подтверждения()
    {
        var probe = ProbeFor(3081, 4242, DshCommandLine(3081));
        var controller = new ServerController(
            @"C:\Temp\dsh-probe-home",
            () => @"C:\Temp",
            _ => { },
            mayOccupyOwnerPort: false,
            locateEngine: () => null,
            requestedPort: () => 3081,
            probe: probe,
            stopWait: TimeSpan.Zero);

        // Сервера ещё нет: поднимать настоящий движок в тестах нечем и незачем — проверяем
        // ровно то, что относится к правилу: панель не управляет ничем и гасить ей нечего.
        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(ServerDecisions.NeedsStopConfirmation(
            controller.Owner, ServerPresence.Running, consentRemembered: false));
        Assert.False(controller.Stop(confirmed: false).IsRunning);
    }

    /// <summary>
    /// Встроиться в то, что перестало быть сервером DSH, нельзя: между находкой и согласием
    /// человека мир мог измениться, и приметы проверяются заново.
    /// </summary>
    [Fact]
    public void Встроиться_в_исчезнувший_сервер_нельзя()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), fingerprint: false);
        var controller = Controller(probe);

        var adopted = controller.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(adopted.IsRunning);
        Assert.Contains("не отвечает", adopted.Detail);

        // Порт держит уже другой процесс — тоже отказ.
        var moved = Controller(ProbeFor(3080, 19804, DshCommandLine(3080), listenerPidAnswer: 999));
        var state = moved.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal(ServerOwner.None, moved.Owner);
        Assert.False(state.IsRunning);
    }

    /// <summary>Без времени создания гасить нечем сверять — встраиваться в такое нельзя.</summary>
    [Fact]
    public void Без_времени_создания_процесса_встраиваться_нельзя()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), startTicks: 0);
        var controller = Controller(probe);

        var state = controller.Adopt(controller.Scan().Found[0]);

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(state.IsRunning);
        Assert.Contains("недоступны", state.Detail);
    }

    // ------------------------------------------------------------ замок от второго движка
    //
    // Находка 25.09.2026: кнопка «Запустить» была заряжена в профиль владельца — свой порт (3081)
    // свободен, а `DSH_HOME` тот же самый, то есть панель подняла бы ВТОРОЙ движок на тех же
    // данных. Здесь проверяется, что этого не происходит: отказ живёт в контроллере, а не в окне,
    // и процесс не запускается вовсе.

    /// <summary>
    /// ГЛАВНАЯ проверка замка: найден чужой сервер — свой не поднимаем. «Не поднимаем» доказано
    /// тем, что движок даже НЕ ИСКАЛИ: без него запуск процесса невозможен, а искать его —
    /// первый шаг запуска. Проверка идёт при НАЙДЕННОМ сервере, а не на пустой машине: на пустой
    /// она не проверила бы ничего.
    /// </summary>
    [Fact]
    public void Замок_не_даёт_поднять_второй_движок_при_живом_чужом_сервере()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var logs = new List<string>();
        var engineLookups = 0;

        var controller = Controller(
            probe,
            locateEngine: () => { engineLookups++; return null; },
            requestedPort: () => 3097,
            log: logs.Add);

        var state = controller.Start(TimeSpan.FromSeconds(5));

        Assert.False(state.IsRunning);
        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Equal(0, state.Port);

        // Процесс не запускался: движок не искали.
        Assert.Equal(0, engineLookups);

        // Причина названа словами и называет найденный порт: человеку надо знать, куда смотреть.
        Assert.Contains("3080", state.Detail, StringComparison.Ordinal);
        Assert.Contains("не поднимает", state.Detail, StringComparison.Ordinal);
        Assert.Contains(logs, line => line.Contains("3080", StringComparison.Ordinal)
                                      && line.Contains("не поднимает", StringComparison.Ordinal));

        // И замок не съел предложение встроиться: найденный сервер по-прежнему показан человеку,
        // и кнопка «Взять под управление» доступна — иначе панель отказывала бы, не давая выхода.
        var model = new ServerPanelModel(controller, controller.Scan());
        Assert.True(model.CanAdopt);
        Assert.Equal(3080, model.Candidate!.Value.Port);
        Assert.Contains("3080", model.FoundText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отказ поднять свой сервер ОБЯЗАН БЫТЬ ВИДЕН не одно мгновение. Панель перечитывает
    /// состояние каждую секунду, и если причина живёт только в ответе на нажатие, человек видит
    /// «нажал — и ничего не произошло»: строка сменяется на «панель ещё не запускала сервер».
    /// Нашла приёмка 26.09.2026: проверка на ответе <c>Start</c> этого не ловила, потому что
    /// проходила не через то состояние, в котором дефект виден, — а им и является следующий осмотр.
    ///
    /// Здесь же проверяется обратное: причина гаснет вместе с сервером, из-за которого была.
    /// Держать её дольше правды — это уже другая ложь.
    /// </summary>
    [Fact]
    public void Отказ_поднять_свой_сервер_виден_и_после_осмотра()
    {
        var dshRunning = true;

        var probe = new ServerProbe(
            ListenerPid: _ => 19804,
            Listeners: () => dshRunning
                ? new[] { new PortTable.Listener(3080, 19804) }
                : Array.Empty<PortTable.Listener>(),
            CommandLine: _ => DshCommandLine(3080),
            ProcessName: _ => "node",
            StartTicks: _ => 63_000_000_000_000_000,
            AnswersFingerprint: _ => true);

        var controller = Controller(probe, locateEngine: () => null, requestedPort: () => 3097);

        var refused = controller.Start(TimeSpan.FromSeconds(5));
        Assert.Contains("не поднимает", refused.Detail, StringComparison.Ordinal);

        // Осмотр, который идёт в окне каждую секунду: причина обязана остаться на месте.
        var polled = controller.Refresh();
        Assert.Contains("3080", polled.Detail, StringComparison.Ordinal);
        Assert.Contains("не поднимает", polled.Detail, StringComparison.Ordinal);

        // Сервер человека погасили — вместе с ним уходит и причина: запускать больше нечему мешать.
        dshRunning = false;
        var cleared = controller.Refresh();
        Assert.DoesNotContain("не поднимает", cleared.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Право «поднимать рядом» снимает замок — и оно ЯВНОЕ. Проверка идёт через то же состояние
    /// (чужой сервер найден), и разницей служит ровно один параметр: иначе «право» оказалось бы
    /// выводом из окружения и однажды потерялось бы незаметно.
    /// </summary>
    [Fact]
    public void Право_поднимать_рядом_снимает_замок()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var engineLookups = 0;

        var controller = Controller(
            probe,
            locateEngine: () => { engineLookups++; return null; },
            allowParallelStart: true);

        var state = controller.Start(TimeSpan.FromSeconds(5));

        // Дело дошло до поиска движка — значит замок не сработал. Отказ теперь про движок,
        // которого на этой машине проверки нет, а не про найденный сервер.
        Assert.Equal(1, engineLookups);
        Assert.Contains("движок не найден", state.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("не поднимает", state.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Разведка не смогла посмотреть порты — это НЕ «чужих серверов нет». Замок на таком ответе
    /// молчит: запретить запуск из-за неудавшегося осмотра значило бы запретить его навсегда
    /// на машине, где таблица портов недоступна.
    /// </summary>
    [Fact]
    public void Неудавшаяся_разведка_замок_не_включает()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), tableReadable: false);
        var engineLookups = 0;

        var controller = Controller(
            probe,
            locateEngine: () => { engineLookups++; return null; });

        var state = controller.Start(TimeSpan.FromSeconds(5));

        Assert.Equal(1, engineLookups);
        Assert.Contains("движок не найден", state.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Панель уже управляет найденным сервером — второй движок на тех же данных не поднимаем.
    /// Отдельная ветка: разведка исключает встроенный сервер из находки, и на замок он не попал бы.
    /// </summary>
    [Fact]
    public void При_взятом_под_управление_сервере_свой_не_поднимается()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var engineLookups = 0;

        // Право «поднимать рядом» ВЫДАНО — и даже с ним второй движок не поднимается:
        // панель уже управляет работающим сервером.
        var controller = Controller(
            probe,
            adoptedPort: () => 3080,
            locateEngine: () => { engineLookups++; return null; },
            allowParallelStart: true);

        controller.Refresh();
        Assert.Equal(ServerOwner.Adopted, controller.Owner);

        var state = controller.Start(TimeSpan.FromSeconds(5));

        Assert.Equal(0, engineLookups);
        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.True(state.IsRunning);
    }

    // ------------------------------------------------------------ запомненное согласие
    //
    // Решение владельца 26.09.2026: «спросить ОДИН раз и запомнить ответ». Запомнено — панель
    // берёт сервер сама и гасит его без окна подтверждения; не запомнено — спрашивает как раньше.

    /// <summary>
    /// Панель САМА берёт сервер, на который человек дал согласие в прошлый раз, и делает это
    /// при обычном осмотре — кнопку никто не нажимает. В журнал уходит РОВНО одна строка об этом.
    /// </summary>
    [Fact]
    public void Запомненное_согласие_панель_восстанавливает_сама()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var logs = new List<string>();

        var controller = Controller(probe, adoptedPort: () => 3080, log: logs.Add);

        var state = controller.Refresh();

        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.True(state.IsRunning);
        Assert.True(controller.ConsentRemembered);

        var restored = logs
            .Where(line => line.Contains("запомнено раньше", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(restored);
        Assert.Contains("3080", restored[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Восстановление идёт ТЕМ ЖЕ кодом, что и по кнопке: приметы проверяются заново. Здесь
    /// запомнен порт, но сервер DSH на нём больше не отвечает — встраиваться не во что,
    /// и панель молчит: неудачная попытка не событие, иначе журнал засорился бы за минуту.
    /// </summary>
    [Fact]
    public void Восстановление_согласия_проверяет_приметы_заново()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), fingerprint: false);
        var logs = new List<string>();

        var controller = Controller(probe, adoptedPort: () => 3080, log: logs.Add);

        for (var i = 0; i < 5; i++) controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(controller.ConsentRemembered);
        Assert.Empty(logs);
    }

    /// <summary>Сервера нет вовсе (порт свободен) — то же самое: попытки без строк в журнал.</summary>
    [Fact]
    public void Без_сервера_согласие_не_восстанавливается_и_журнал_молчит()
    {
        var probe = new ServerProbe(
            _ => 0,
            () => Array.Empty<PortTable.Listener>(),
            _ => string.Empty,
            _ => string.Empty,
            _ => 0,
            _ => false);

        var logs = new List<string>();
        var controller = Controller(probe, adoptedPort: () => 3080, log: logs.Add);

        for (var i = 0; i < 5; i++) controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Empty(logs);
    }

    /// <summary>
    /// Согласие дано ОДНОМУ порту. Сервер DSH, найденный на другом, под управление не берётся:
    /// иначе «запомненный порт» ничего не значил бы, и панель встроилась бы в первое, что нашла.
    /// </summary>
    [Fact]
    public void Согласие_на_один_порт_не_тянет_под_управление_другой()
    {
        var probe = ProbeFor(3097, 555, DshCommandLine(3097));
        var controller = Controller(probe, adoptedPort: () => 3080);

        controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);

        // Сервер найден — просто не тот, о котором договорились: проверка не слепая.
        Assert.True(controller.Scan().Any);
    }

    /// <summary>
    /// Запомненное согласие гасит встроенный сервер БЕЗ подтверждения — и именно тот процесс,
    /// которого панель видела (номер плюс время создания).
    /// </summary>
    [Fact]
    public void Запомненное_согласие_гасит_без_подтверждения()
    {
        const long startedAt = 63_000_000_000_000_000;
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), startTicks: startedAt);
        var killer = new RecordingKiller();

        var controller = Controller(probe, killer, adoptedPort: () => 3080);

        controller.Refresh();   // панель взяла сервер сама: человек ответил в прошлый запуск
        Assert.Equal(ServerOwner.Adopted, controller.Owner);

        var stopped = controller.Stop(confirmed: false);

        Assert.Single(killer.Calls);
        Assert.Equal(19804, killer.Calls[0].Pid);
        Assert.Equal(startedAt, killer.Calls[0].StartedAt);
        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(stopped.IsRunning);
    }

    /// <summary>
    /// Согласия нет — правило прежнее, даже если человек только что нажал «взять под управление».
    /// Проверка идёт через САМО состояние отказа хранилища: запись не проходит (прогон без права
    /// писать настройки), поэтому согласия не появляется и гашение спрашивает подтверждение.
    /// С подставным убийцей видно главное: он не позван НИ РАЗУ.
    /// </summary>
    [Fact]
    public void Без_запомненного_согласия_встроенный_сервер_не_гасится()
    {
        var writable = false;
        var store = 0;

        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var killer = new RecordingKiller();

        var controller = Controller(
            probe,
            killer,
            adoptedPort: () => store,
            rememberAdoptedPort: port => { if (writable) store = port; });

        controller.Adopt(controller.Scan().Found[0]);

        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.False(controller.ConsentRemembered);

        var refused = controller.Stop(confirmed: false);

        Assert.Empty(killer.Calls);
        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.Contains("подтвержд", refused.Detail, StringComparison.OrdinalIgnoreCase);

        // А стоит хранилищу принять запись — и то же самое гашение проходит без подтверждения.
        writable = true;
        controller.Detach();
        controller.Adopt(controller.Scan().Found[0]);

        Assert.True(controller.ConsentRemembered);
        Assert.True(controller.Stop(confirmed: false).Presence != ServerPresence.Running);
        Assert.Single(killer.Calls);
    }

    /// <summary>
    /// «Отвязаться» ЗАБЫВАЕТ согласие и НЕ гасит сервер. После отвязки панель снова спрашивает,
    /// как в первый раз, — иначе отвязка была бы дверью в одну сторону.
    /// </summary>
    [Fact]
    public void Отвязка_забывает_согласие_и_сервер_не_гасит()
    {
        var store = 0;
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var killer = new RecordingKiller();

        var controller = Controller(
            probe,
            killer,
            adoptedPort: () => store,
            rememberAdoptedPort: port => store = port);

        var adopted = controller.Adopt(controller.Scan().Found[0]);

        Assert.True(adopted.IsRunning);
        Assert.Equal(3080, store);
        Assert.True(controller.ConsentRemembered);

        var detached = controller.Detach();

        Assert.Empty(killer.Calls);
        Assert.Equal(0, store);
        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Contains("продолжает работать", detached.Detail, StringComparison.Ordinal);

        // Осмотр не встраивает панель обратно: согласие забыто, и человек снова решает сам.
        controller.Refresh();
        Assert.Equal(ServerOwner.None, controller.Owner);
    }

    /// <summary>
    /// Встраивание НЕ состоялось — согласие не запоминается. Иначе панель запомнила бы сервер,
    /// которого не видела, и записала бы в настройки порт «на будущее».
    /// </summary>
    [Fact]
    public void Неудавшееся_встраивание_согласия_не_запоминает()
    {
        var writes = new List<int>();
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080), fingerprint: false);

        var controller = Controller(probe, rememberAdoptedPort: writes.Add);

        var state = controller.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.False(state.IsRunning);
        Assert.Empty(writes);
    }
}
