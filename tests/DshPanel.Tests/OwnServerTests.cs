using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВОЙ СЕРВЕР, УЗНАННЫЙ ПОСЛЕ ПЕРЕЗАПУСКА ПАНЕЛИ, и ССЫЛКА ВХОДА.
///
/// Оба держатся на файлах под корнем панели, и оба устроены так, что панель проверки их
/// НЕ ЧИТАЕТ: контроллер не знает путей вовсе — чтение и запись приходят делегатами
/// (<see cref="ServerStateFiles"/>). Проверки ниже подставляют вместо файлов переменные:
/// так видно и то, что панель записала, и то, что она прочитала.
///
/// Ни одна проверка здесь не поднимает настоящий сервер и не занимает чужой порт: разведка
/// и убийца подставные, а порт берётся свободный. Единственное место, где запускается настоящий
/// процесс, — «согласие забывается при подъёме своего сервера»: подъём без процесса не проверить,
/// и там запускается <c>cmd.exe</c>, который сразу завершается. Ни данных владельца, ни сети
/// он не касается, а домашний каталог движка ему подставляется временный.
/// </summary>
public class OwnServerTests
{
    private const long Ticks = 63_812_345_678_901_234;

    private const string RealDshCommandLine =
        "\"C:\\Program Files\\nodejs\\node.exe\" " +
        "C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js " +
        "web --no-open --port ";

    /// <summary>Файлы состояния сервера вместо диска: видно и чтение, и запись, и уборку.</summary>
    private sealed class Files
    {
        public string Entry { get; set; } = string.Empty;
        public string Own { get; set; } = string.Empty;

        /// <summary>Последнее, что панель записала в запись о своём сервере, — даже если потом сняла.</summary>
        public string LastOwnWritten { get; private set; } = string.Empty;

        public int OwnWrites { get; private set; }
        public int EntryClears { get; private set; }
        public int OwnClears { get; private set; }

        /// <summary>Чужой источник — файл панели 1.x. Только чтение, как и в жизни.</summary>
        public string Version1 { get; set; } = string.Empty;

        public ServerStateFiles StateFiles => new(
            ReadEntryLink: () => Entry,
            WriteEntryLink: url => Entry = url,
            ClearEntryLink: () => { Entry = string.Empty; EntryClears++; },
            ReadOwnServer: () => Own,
            WriteOwnServer: text => { Own = text; LastOwnWritten = text; OwnWrites++; },
            ClearOwnServer: () => { Own = string.Empty; OwnClears++; });
    }

    private sealed class Killer : IProcessKiller
    {
        private readonly Action? _after;

        public Killer(Action? after = null) => _after = after;

        public List<(int Pid, long StartedAt)> Calls { get; } = new();

        public bool Fail { get; set; }

        public string Error { get; set; } = "процесс не дался";

        public bool Kill(int pid, long startedAtTicks, out string error)
        {
            Calls.Add((pid, startedAtTicks));
            error = Fail ? Error : string.Empty;
            if (!Fail) _after?.Invoke();
            return !Fail;
        }
    }

    /// <summary>
    /// Подставная дверь к системе. Слушающий процесс можно «погасить», поменяв
    /// <see cref="Listener"/>: так проверяется, что после гашения порт действительно освободился.
    /// Второй слушатель (<see cref="OtherPort"/>) нужен, чтобы увидеть: разведка исключает
    /// РОВНО свой процесс, а не «всё, что нашлось».
    /// </summary>
    private sealed class World
    {
        public int Port { get; init; } = 3097;
        public int Listener { get; set; } = 4242;
        public long ListenerTicks { get; set; } = Ticks;
        public bool Fingerprint { get; set; } = true;
        public bool TableReadable { get; set; } = true;

        public int OtherPort { get; init; }
        public int OtherPid { get; init; }

        public ServerProbe Probe => new(
            ListenerPid: port => port == OtherPort ? OtherPid : Listener,
            Listeners: () =>
            {
                if (!TableReadable) return null;

                var table = new List<PortTable.Listener> { new(Port, Listener) };
                if (OtherPort > 0) table.Add(new PortTable.Listener(OtherPort, OtherPid));
                return table;
            },
            CommandLine: pid => RealDshCommandLine + (pid == OtherPid ? OtherPort : Port),
            ProcessName: _ => "node",
            StartTicks: _ => ListenerTicks,
            AnswersFingerprint: _ => Fingerprint);
    }

    private static ServerController Controller(
        World world,
        Files files,
        Killer? killer = null,
        Func<int>? adoptedPort = null,
        Action<int>? rememberAdoptedPort = null,
        List<string>? log = null,
        Func<DshEngine?>? locateEngine = null,
        Func<int>? requestedPort = null,
        Func<string, LinkProbe>? linkProbe = null) =>
        new(
            @"C:\Temp\dsh-own-home",
            () => @"C:\Temp",
            log is null ? _ => { } : log.Add,
            mayOccupyOwnerPort: false,

            // Движка на машине проверка не ищет: поднимать настоящий сервер ей нечем и незачем.
            locateEngine: locateEngine ?? (() => null),
            requestedPort: requestedPort ?? (() => world.Port),
            killer: killer,
            probe: world.Probe,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            adoptedPort: adoptedPort,
            rememberAdoptedPort: rememberAdoptedPort,
            files: files.StateFiles,
            foundServerEntryLink: () => files.Version1,
            linkProbe: linkProbe);

    // ---------------------------------------------------------------- узнаём свой сервер

    /// <summary>
    /// Панель перезапустили, а её сервер жив. По записи он признаётся СВОИМ: иначе панель
    /// предложила бы в него «встроиться», а «Остановить» отказывалась бы гасить собственный сервер.
    /// </summary>
    [Fact]
    public void Свой_сервер_узнаётся_по_записи_после_перезапуска()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var record = new OwnServerRecord(3097, 4242, Ticks);
        var files = new Files { Own = record.Format() };
        var log = new List<string>();

        using var controller = Controller(world, files, log: log);

        // До осмотра панель ничем не управляет — и это то состояние, в котором дефект и возможен.
        Assert.Equal(ServerOwner.None, controller.Owner);

        var state = controller.Refresh();

        Assert.Equal(ServerOwner.Panel, controller.Owner);
        Assert.True(state.IsRunning);
        Assert.Equal(3097, state.Port);
        Assert.Equal(4242, state.Pid);

        // Запись НЕ снята: сервер жив, и запись про него верна.
        Assert.Equal(record.Format(), files.Own);
        Assert.Equal(0, files.OwnClears);
        Assert.Contains(log, line => line.Contains("узнан по записи", StringComparison.Ordinal));
    }

    /// <summary>
    /// «PID совпал, время создания другое» — процесс ДРУГОЙ: номера переиспользуются, и по одному
    /// номеру панель однажды погасила бы чужой процесс. Запись при этом снимается: своего сервера
    /// на порту уже нет, и верить записи нечем.
    /// </summary>
    [Fact]
    public void Номер_совпал_а_процесс_другой_значит_не_свой()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks + 1 };
        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        using var controller = Controller(world, files);
        var state = controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Equal(string.Empty, files.Own);
        Assert.Equal(1, files.OwnClears);

        // Сервер на порту при этом жив и не тронут — панель его просто не присвоила.
        Assert.Equal(4242, world.Listener);
        Assert.Equal(ServerPresence.Stopped, state.Presence);
    }

    /// <summary>Запись про порт, на котором никого нет, снимается: держать её незачем.</summary>
    [Fact]
    public void Запись_про_ушедший_сервер_снимается()
    {
        var world = new World { Port = 3097, Listener = 0 };
        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        using var controller = Controller(world, files);
        controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Equal(string.Empty, files.Own);
    }

    /// <summary>
    /// «Не смог узнать, кто слушает» — НЕ то же самое, что «никого нет»: запись не трогаем.
    /// Правило проверяется отдельно, потому что врать человеку нельзя ни в одну сторону.
    /// </summary>
    [Fact]
    public void Непрочитанная_таблица_портов_запись_не_снимает()
    {
        var world = new World { Port = 3097, Listener = -1 };
        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        using var controller = Controller(world, files);
        controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.NotEqual(string.Empty, files.Own);
        Assert.Equal(0, files.OwnClears);
    }

    /// <summary>
    /// Свой сервер, узнанный по записи, разведка НЕ показывает как находку: иначе панель нашла бы
    /// САМА СЕБЯ и предложила в себя «встроиться». А чужой сервер на СОСЕДНЕМ порту обязан
    /// остаться в находке — исключается ровно свой процесс, а не «всё, что нашлось».
    /// </summary>
    [Fact]
    public void Узнанный_свой_сервер_исключается_из_находки_а_чужой_остаётся()
    {
        var world = new World
        {
            Port = 3097,
            Listener = 4242,
            ListenerTicks = Ticks,
            OtherPort = 3098,
            OtherPid = 5150,
        };

        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        using var controller = Controller(world, files);
        controller.Refresh();

        Assert.Equal(ServerOwner.Panel, controller.Owner);

        var found = controller.Scan();

        Assert.True(found.TableReadable);
        var single = Assert.Single(found.Found);
        Assert.Equal(3098, single.Port);
        Assert.Equal(5150, single.Pid);
    }

    // ---------------------------------------------------------------- гасим свой сервер по записи

    /// <summary>
    /// «Остановить» обязана гасить свой сервер И БЕЗ ДЕСКРИПТОРА — тем же способом и с той же
    /// сверкой, что встроенный: номер процесса ВМЕСТЕ со временем создания. Проверка сторожит
    /// и то, что убийце ушло именно ЗАПИСАННОЕ время: «текущее» не доказывало бы ничего — по нему
    /// панель погасила бы любой процесс с тем же номером.
    /// </summary>
    [Fact]
    public void Свой_сервер_гасится_по_записи_и_запись_снимается()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files
        {
            Own = new OwnServerRecord(3097, 4242, Ticks).Format(),
            Entry = "http://127.0.0.1:3097/?token=SECRET",
        };

        // Убийца «освобождает» порт: после него слушать перестают.
        var killer = new Killer(() => world.Listener = 0);
        var log = new List<string>();

        using var controller = Controller(world, files, killer, log: log);
        controller.Refresh();

        Assert.Equal(ServerOwner.Panel, controller.Owner);

        var state = controller.Stop(confirmed: false);

        var call = Assert.Single(killer.Calls);
        Assert.Equal(4242, call.Pid);
        Assert.Equal(Ticks, call.StartedAt);

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Equal(ServerPresence.Stopped, state.Presence);
        Assert.Equal(string.Empty, files.Own);
        Assert.Equal(1, files.OwnClears);

        // Ссылка живёт ровно столько, сколько живёт сервер: сервер погашен — ссылки нет.
        Assert.Equal(string.Empty, files.Entry);
        Assert.Equal(1, files.EntryClears);

        Assert.Contains(log, line => line.Contains("гашу свой сервер по записи", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("ссылка входа забыта", StringComparison.Ordinal));
    }

    /// <summary>Ошибка гашения — словами и в журнал, и в состояние: молчание тут недопустимо.</summary>
    [Fact]
    public void Отказ_гашения_по_записи_называется_словами()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        var killer = new Killer { Fail = true, Error = "номер успел достаться другому" };
        var log = new List<string>();

        using var controller = Controller(world, files, killer, log: log);
        controller.Refresh();

        var state = controller.Stop(confirmed: false);

        Assert.Contains("номер успел достаться другому", state.Detail, StringComparison.Ordinal);
        Assert.Contains(log, line => line.Contains("не удалось", StringComparison.Ordinal));

        // Запись НЕ снята: сервер жив, и панель по-прежнему знает, что он её.
        Assert.NotEqual(string.Empty, files.Own);
        Assert.Equal(ServerOwner.Panel, controller.Owner);
    }

    /// <summary>
    /// А вот когда ссылки НЕ БЫЛО, журнал о ней и не говорит: у сервера, который ещё не напечатал
    /// ссылку (или у встроенного — своей ссылки у него не бывает вовсе), снимать нечего. Строка
    /// «ссылка входа забыта» на нём — ложь о том, чего не было; нашёл её живой прогон самотеста.
    ///
    /// Проверка парная к предыдущей: без неё «строка есть» доказывало бы лишь то, что она есть ВСЕГДА.
    /// </summary>
    [Fact]
    public void Без_ссылки_журнал_о_ней_не_говорит()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files { Own = new OwnServerRecord(3097, 4242, Ticks).Format() };

        var killer = new Killer(() => world.Listener = 0);
        var log = new List<string>();

        using var controller = Controller(world, files, killer, log: log);
        controller.Refresh();

        Assert.Equal(ServerOwner.Panel, controller.Owner);

        var state = controller.Stop(confirmed: false);

        Assert.Equal(ServerPresence.Stopped, state.Presence);
        Assert.Equal(ServerOwner.None, controller.Owner);

        // Запись о сервере была — о ней сказано. А ссылки не было — и молчим.
        Assert.Contains(log, line => line.Contains("запись о своём сервере снята", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("ссылка входа забыта", StringComparison.Ordinal));
    }

    /// <summary>
    /// Тот же запрет на ложь — и на ВТОРОМ пути гашения: когда панель гасит свой сервер, поднятый
    /// ЕЮ в этом же прогоне (живой дескриптор есть). Ссылки входа и тут может не быть — движок
    /// её ещё не напечатал, — и строка «ссылка входа забыта» была бы такой же ложью.
    ///
    /// ⚠️ Проверка появилась потому, что мутация «говорить о ссылке всегда» на первом пути
    /// НЕ уронила ничего: парная проверка выше идёт через гашение ПО ЗАПИСИ, а этот путь — свой,
    /// с дескриптором. Нашлось прогоном мутации, а не чтением.
    /// </summary>
    [Fact]
    public void Гашение_своего_сервера_без_ссылки_о_ней_не_говорит()
    {
        var port = FreePort.Find();
        var world = new World { Port = 0, Listener = 0, TableReadable = false };
        var files = new Files();
        var log = new List<string>();

        // `/k`: поддельный движок обязан дожить до гашения — иначе панель признала бы его ушедшим
        // и пошла бы другой веткой.
        var engine = new DshEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/k");

        using var controller = Controller(
            world, files, log: log, locateEngine: () => engine, requestedPort: () => port);

        controller.Start(TimeSpan.Zero);
        Assert.Equal(ServerOwner.Panel, controller.Owner);

        // Ссылки не было вовсе — движок её не печатал.
        Assert.Equal(string.Empty, files.Entry);

        controller.Stop(confirmed: true);

        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.DoesNotContain(log, line => line.Contains("ссылка входа забыта", StringComparison.Ordinal));
    }

    /// <summary>
    /// Панель ничем не управляет (записи нет) — «Остановить» не гасит найденный сервер и говорит,
    /// что он не её. Это прежнее правило, и оно не должно поехать от новой ветки гашения.
    /// </summary>
    [Fact]
    public void Без_записи_панель_чужой_сервер_не_гасит()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files();
        var killer = new Killer();
        var log = new List<string>();

        using var controller = Controller(world, files, killer, log: log);
        controller.Refresh();

        Assert.Equal(ServerOwner.None, controller.Owner);

        var state = controller.Stop(confirmed: false);

        Assert.Empty(killer.Calls);
        Assert.Equal(ServerOwner.None, controller.Owner);
        Assert.Equal(ServerPresence.Stopped, state.Presence);

        // Чужой сервер при этом ЖИВ: панель его не тронула, а просто сказала, что он не её.
        Assert.Equal(4242, world.Listener);
    }

    // ---------------------------------------------------------------- ссылка входа

    /// <summary>
    /// Ссылка ловится из СЫРОЙ строки вывода движка (её приносит <c>Remember</c>) и уходит
    /// в файл ЦЕЛИКОМ — с токеном: из «token=***» ссылку уже не собрать.
    ///
    /// И тут же ГЛАВНОЕ: в журнал токен не попадает. Этой проверкой ловится ровно та ошибка,
    /// из-за которой ссылку в v1 и считали секретом.
    /// </summary>
    [Fact]
    public void Ссылка_входа_сохраняется_а_в_журнал_уходит_без_токена()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files();
        var log = new List<string>();

        using var controller = Controller(world, files, log: log);

        controller.Remember("dsh web: http://127.0.0.1:3097/?token=SECRET-TOKEN-42 хвост");

        // До подъёма панель ничем не управляет — ссылку она не присваивает.
        Assert.Equal(string.Empty, files.Entry);

        // Строка вывода прошла в журнал — и БЕЗ токена.
        Assert.Contains(log, line => line.Contains("token=***", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN-42", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ссылка НА ДРУГОЙ ПОРТ не принимается: вывод движка мог остаться от прежнего запуска,
    /// и открыть человеку вчерашний адрес хуже, чем не открыть.
    /// </summary>
    [Fact]
    public void Ссылка_про_чужой_порт_не_принимается()
    {
        var world = new World { Port = 3097, Listener = 4242, ListenerTicks = Ticks };
        var files = new Files();
        var log = new List<string>();

        using var controller = Controller(world, files, log: log);

        controller.Remember("dsh web: http://127.0.0.1:9999/?token=SECRET-TOKEN-42");

        Assert.Equal(string.Empty, files.Entry);
        Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN-42", StringComparison.Ordinal));
    }

    /// <summary>
    /// У НАЙДЕННОГО сервера своей ссылки нет — панель его не поднимала и вывода движка не видела.
    /// Запасной источник — файл панели 1.x, и берётся он только когда порт совпал И порт принимает
    /// ссылку. Живой замер 27.09.2026: рабочий ответ — 303 с cookie; 401 значит «токена нет».
    /// </summary>
    [Fact]
    public void Найденному_серверу_ссылка_берётся_у_панели_1x_только_по_живому_ответу()
    {
        var files = new Files { Version1 = "http://127.0.0.1:3080/?token=FROM-V1" };

        string FromAdopted(Func<string, LinkProbe> probe)
        {
            var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };

            using var controller = Controller(world, files, linkProbe: probe);
            controller.Refresh();
            controller.Adopt(new FoundServer(3080, 19804, "node"));

            Assert.Equal(ServerOwner.Adopted, controller.Owner);
            return controller.EntryLink;
        }

        // Порт молчит — чужой ссылке верить нельзя.
        Assert.Equal(string.Empty, FromAdopted(_ => LinkProbe.Failed2));

        // Порт ответил 401 — это ссылка БЕЗ токена, открывать нечего.
        Assert.Equal(string.Empty, FromAdopted(_ => LinkProbe.Answer(401, setCookie: false)));

        // А вот 303 с cookie — тот самый ответ, ради которого правило и переписано после замера.
        Assert.Equal("http://127.0.0.1:3080/?token=FROM-V1", FromAdopted(_ => LinkProbe.Answer(303, true)));

        // И 200 без cookie тоже не годится: отвечает не вход в панель.
        Assert.Equal(string.Empty, FromAdopted(_ => LinkProbe.Answer(200, setCookie: false)));
    }

    /// <summary>
    /// Ссылка панели 1.x бывает и про ДРУГОЙ порт (файл остался от прежнего запуска) — тогда она
    /// не годится, каким бы живым порт ни был.
    /// </summary>
    [Fact]
    public void Чужая_ссылка_про_другой_порт_не_берётся()
    {
        var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };
        var files = new Files { Version1 = "http://127.0.0.1:3099/?token=FROM-V1" };

        using var controller = Controller(world, files, linkProbe: _ => LinkProbe.Answer(303, true));

        controller.Refresh();
        controller.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal(string.Empty, controller.EntryLink);
    }

    /// <summary>
    /// ⚠️ ПО ЧУЖОЙ ССЫЛКЕ ЗАПРОС НЕ ДЕЛАЕТСЯ ВОВСЕ — и это про утечку токена (красная линия 7).
    ///
    /// Файл панели 1.x чужой: в нём может стоять любой адрес, а в ссылке — токен входа. Спроси
    /// панель сеть по такому адресу, токен уже ушёл бы на чужой хост, и «не годна» было бы сказано
    /// ПОСЛЕ утечки. Поэтому проверка хоста идёт до сети, и счётчик подставного зонда обязан
    /// остаться нулевым. Нашла холодная проверка 27.09.2026 прогоном на собранной сборке.
    /// </summary>
    [Fact]
    public void По_чужой_ссылке_запрос_не_делается_вовсе()
    {
        foreach (var foreign in new[]
                 {
                     "http://example.com:3080/?token=STOLEN",
                     "http://192.168.1.5:3080/?token=STOLEN",
                 })
        {
            var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };
            var files = new Files { Version1 = foreign };
            var probes = 0;

            using var controller = Controller(
                world, files, linkProbe: _ => { probes++; return LinkProbe.Answer(303, setCookie: true); });

            controller.Refresh();
            controller.Adopt(new FoundServer(3080, 19804, "node"));

            Assert.Equal(string.Empty, controller.EntryLink);
            Assert.Equal(0, probes);
        }
    }

    /// <summary>
    /// А СВОЯ ссылка (loopback) запрос получает — иначе проверка выше доказывала бы лишь то,
    /// что панель не спрашивает сеть НИКОГДА.
    /// </summary>
    [Fact]
    public void По_своей_ссылке_запрос_делается()
    {
        var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };
        var files = new Files { Version1 = "http://127.0.0.1:3080/?token=OURS" };
        var probes = 0;

        using var controller = Controller(
            world, files, linkProbe: _ => { probes++; return LinkProbe.Answer(303, setCookie: true); });

        controller.Refresh();
        controller.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal("http://127.0.0.1:3080/?token=OURS", controller.EntryLink);
        Assert.Equal(1, probes);
    }

    /// <summary>
    /// ЖУРНАЛ ГОВОРИТ ПРАВДУ О СУДЬБЕ ССЫЛКИ — на обоих исходах.
    ///
    /// Зачем отдельная проверка. Инверсия признака <c>Persists</c> в сохранении ссылки оставляла
    /// набор ЗЕЛЁНЫМ: обе строки остаются в деле (ворота «строка где-то используется» молчат),
    /// а сравнение «что в журнале = что в словаре» берёт текст из одного источника. То есть панель
    /// могла врать в обе стороны — говорить «сохранена», не сохранив, и «не сохраняю», сохранив.
    /// Нашла холодная проверка 27.09.2026.
    ///
    /// Проверяются ТРИ вещи на каждом исходе: что в журнале ровно ожидаемая строка, что второй
    /// строки нет и что запись на диск зовётся (или НЕ зовётся) — счётчиком в подставном делегате.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Журнал_говорит_правду_о_судьбе_ссылки(bool persists)
    {
        var port = FreePort.Find();
        var world = new World { Port = 0, Listener = 0, TableReadable = false };
        var log = new List<string>();
        var writes = 0;

        var stateFiles = new ServerStateFiles(
            ReadEntryLink: () => string.Empty,
            WriteEntryLink: _ => writes++,
            ClearEntryLink: () => { },
            ReadOwnServer: () => string.Empty,
            WriteOwnServer: _ => { },
            ClearOwnServer: () => { })
        {
            Persists = persists,
        };

        // `/k`: поддельный движок обязан дожить до конца проверки — иначе панель признала бы его
        // ушедшим и сняла ссылку раньше, чем мы её посмотрим.
        var engine = new DshEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/k");

        using var controller = new ServerController(
            @"C:\Temp\dsh-own-home",
            () => @"C:\Temp",
            log.Add,
            mayOccupyOwnerPort: false,
            locateEngine: () => engine,
            requestedPort: () => port,
            killer: new Killer(),
            probe: world.Probe,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            files: stateFiles);

        try
        {
            controller.Start(TimeSpan.Zero);
            Assert.Equal(ServerOwner.Panel, controller.Owner);

            var url = $"http://127.0.0.1:{port}/?token=SECRET-TOKEN-42";
            controller.Remember($"dsh web: {url}");

            var redacted = EntryLinkDecisions.Redacted(url);
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var saved = string.Format(culture, PanelStrings.SrvEntryLinkFoundLogFormat, redacted);
            var inMemory = string.Format(culture, PanelStrings.SrvEntryLinkFoundInMemoryLogFormat, redacted);

            if (persists)
            {
                Assert.Equal(1, writes);
                Assert.Contains(saved, log);
                Assert.DoesNotContain(inMemory, log);
            }
            else
            {
                // Писать некуда — и на диск НЕ пишем, и «сохранена» не говорим.
                Assert.Equal(0, writes);
                Assert.Contains(inMemory, log);
                Assert.DoesNotContain(saved, log);
            }

            // Ни в одном исходе токен в журнал не попадает.
            Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN-42", StringComparison.Ordinal));
        }
        finally
        {
            controller.Stop(confirmed: true);
        }
    }

    /// <summary>
    /// Ссылка НАЙДЕННОГО сервера сюда не достаётся: она про сервер, который панель поднимала сама,
    /// и подставить её чужому значило бы открыть человеку не тот адрес.
    /// </summary>
    [Fact]
    public void Своя_ссылка_чужому_серверу_не_достаётся()
    {
        var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };
        var files = new Files
        {
            Entry = "http://127.0.0.1:3080/?token=OURS",
            Version1 = string.Empty,
        };

        using var controller = Controller(world, files, linkProbe: _ => LinkProbe.Answer(303, true));

        controller.Refresh();
        controller.Adopt(new FoundServer(3080, 19804, "node"));

        Assert.Equal(string.Empty, controller.EntryLink);
    }

    /// <summary>
    /// ССЫЛКА СВОЕГО СЕРВЕРА: ловится из вывода движка, ложится в файл ЦЕЛИКОМ (с токеном — иначе
    /// кнопка не заработает после перезапуска панели) и достаётся из контроллера тем же целым видом.
    ///
    /// И тут же главное: **в журнал токен не попадает ни одной строкой**. Проверка идёт через
    /// НАСТОЯЩИЙ путь сохранения (сервер поднят, ссылка пришла, файл записан) — без этого правило
    /// «в журнал без токена» проверялось бы там, где сохранения не происходит вовсе, и мутация
    /// «печатать ссылку как есть» осталась бы незамеченной.
    /// </summary>
    [Fact]
    public void Ссылка_своего_сервера_ложится_в_файл_а_в_журнал_не_целиком()
    {
        var port = FreePort.Find();
        var world = new World { Port = 0, Listener = 0, TableReadable = false };
        var files = new Files();
        var log = new List<string>();

        // `/k`, а не `/c`: поддельный движок обязан ПЕРЕЖИТЬ проверку, иначе панель успела бы
        // признать процесс ушедшим и снять ссылку раньше, чем мы её посмотрим.
        var engine = new DshEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/k");

        using var controller = Controller(
            world,
            files,
            log: log,
            locateEngine: () => engine,
            requestedPort: () => port);

        try
        {
            controller.Start(TimeSpan.Zero);
            Assert.Equal(ServerOwner.Panel, controller.Owner);

            var url = $"http://127.0.0.1:{port}/?token=SECRET-TOKEN-42";
            controller.Remember($"dsh web: {url} и хвост строки");

            // В файле — ссылка ЦЕЛИКОМ: из «token=***» кнопка не заработала бы.
            Assert.Equal(url, files.Entry);

            // И из контроллера она выходит той же целой ссылкой.
            Assert.Equal(url, controller.EntryLink);

            // А в журнале токена НЕТ: там только обезвреженные строки — и ссылки, и вывода движка.
            Assert.Contains(log, line => line.Contains("token=***", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN-42", StringComparison.Ordinal));
        }
        finally
        {
            // Поддельный движок — НАШ процесс, и он не должен пережить проверку.
            controller.Stop(confirmed: true);
        }
    }

    // ---------------------------------------------------------------- согласие и строка состояния

    /// <summary>
    /// Порт стал НАШИМ — прежнее согласие на найденный сервер больше не верно и обязано быть
    /// забыто. Иначе однажды панель приняла бы за «своё окружение» то, что придёт на этот порт
    /// после неё.
    ///
    /// Подъём идёт на <c>cmd.exe</c>, который сразу завершается: настоящий движок проверке не нужен,
    /// а «панель действительно подняла процесс» — нужен, потому что забывание согласия стоит именно
    /// там. Домашний каталог движка у этого процесса — временный путь, а не `~/.dsh`.
    /// </summary>
    [Fact]
    public void При_подъёме_своего_сервера_согласие_на_этот_порт_забывается()
    {
        var port = FreePort.Find();
        Assert.NotEqual(0, port);

        var world = new World { Port = 0, Listener = 0, TableReadable = false };
        var files = new Files();

        // Согласие — ровно на тот порт, который панель собирается занять.
        var remembered = port;
        var log = new List<string>();

        var engine = new DshEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c");

        using var controller = Controller(
            world,
            files,
            adoptedPort: () => remembered,
            rememberAdoptedPort: value => remembered = value,
            log: log,
            locateEngine: () => engine,
            requestedPort: () => port);

        controller.Start(TimeSpan.Zero);

        // Сервер поднят панелью — значит порт наш, и согласие про него больше не верно.
        Assert.Equal(ServerOwner.Panel, controller.Owner);
        Assert.Equal(0, remembered);
        Assert.Contains(log, line => line.Contains("теперь наш", StringComparison.Ordinal));

        // Запись о своём сервере сделана — по ней его узнают после перезапуска панели.
        Assert.Equal(1, files.OwnWrites);
        Assert.Equal(port, OwnServerRecord.Parse(files.LastOwnWritten)!.Value.Port);

        controller.Stop(confirmed: true);
    }

    /// <summary>
    /// Согласие на ДРУГОЙ порт подъём своего сервера не трогает: оно про другое окружение,
    /// и стирать его значило бы заставлять человека отвечать заново.
    /// </summary>
    [Fact]
    public void Согласие_на_другой_порт_остаётся()
    {
        var port = FreePort.Find();
        var world = new World { Port = 0, Listener = 0, TableReadable = false };
        var files = new Files();

        var remembered = 3080;
        var engine = new DshEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c");

        using var controller = Controller(
            world,
            files,
            adoptedPort: () => remembered,
            rememberAdoptedPort: value => remembered = value,
            locateEngine: () => engine,
            requestedPort: () => port);

        controller.Start(TimeSpan.Zero);

        Assert.Equal(ServerOwner.Panel, controller.Owner);
        Assert.Equal(3080, remembered);

        controller.Stop(confirmed: true);
    }

    /// <summary>
    /// Строка состояния встроенного сервера. Пока согласие НЕ запомнено — «панель встроена в него,
    /// поднят не панелью»: человеку важно знать, что это не её сервер. Как только согласие
    /// запомнено (решение владельца 26.09.2026: «спросить один раз»), строка становится обычной:
    /// называть его же решение чужим панель не должна.
    ///
    /// Правда при этом не теряется — она остаётся в отчёте окружения в настройках и в карточке
    /// найденного сервера; там строки не менялись.
    /// </summary>
    [Fact]
    public void Строка_встроенного_сервера_меняется_только_по_запомненному_согласию()
    {
        var world = new World { Port = 3080, Listener = 19804, ListenerTicks = Ticks };
        var files = new Files();
        var remembered = 0;

        using var controller = Controller(
            world,
            files,
            adoptedPort: () => remembered,
            rememberAdoptedPort: value => remembered = value,
            linkProbe: _ => LinkProbe.Failed2);

        controller.Refresh();
        controller.Adopt(new FoundServer(3080, 19804, "node"));

        // Встраивание записало согласие — значит строка обычная.
        Assert.Equal(3080, remembered);
        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture, PanelStrings.SrvAnswersFormat, 3080),
            controller.Refresh().Detail);

        // Согласия нет — прежняя строка: панель его не поднимала.
        remembered = 0;

        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture, PanelStrings.SrvAnswersAdoptedFormat, 3080),
            controller.Refresh().Detail);
    }
}
