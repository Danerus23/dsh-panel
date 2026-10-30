using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СТОРОЖ СЕРВЕРА, ПРИНЯТОГО ПО ЗАПИСИ (DEF-057, 2.4.1).
///
/// **От какого случая (журнал владельца 24.10.2026, 12:20:16 и 12:30:50).** Панель перезапустили,
/// а её сервер продолжал работать: панель узнала его по записи <c>state\own-server.txt</c> и взяла
/// под управление. Дальше сервер умер ВНЕШНЕ — и панель промолчала: ни шарика, ни строки журнала,
/// ни кода выхода. Сторож падения был подписан только на процесс, который панель подняла САМА
/// (<c>_process</c>), а у принятого по записи такого процесса нет.
///
/// **Что проверяется.** Что панель открывает по номеру принятого сервера НАСТОЯЩИЙ дескриптор
/// (<see cref="Process.GetProcessById"/> + <see cref="Process.EnableRaisingEvents"/>) и замечает
/// внешний выход: ровно одна строка «завершился САМ» с кодом выхода, ровно одно событие
/// <c>Crashed</c>, запись и ссылка входа сняты, состояние «остановлен», авто-подъёма нет.
/// И что код, который спросить не удалось, называется «неизвестен», а не нулём.
///
/// **На чём.** Подставной сервер — НАСТОЯЩИЙ процесс (<c>cmd.exe /c ping.exe -n 30 …</c>, ровно
/// как в <c>OwnServerTests.LiveEngine</c>: он живёт, пока его не убьют), время его создания —
/// настоящее (<see cref="ProcessFacts.StartTicks"/>), запись — настоящий файл <c>own-server.txt</c>
/// во временном корне проверки. Подменена только дверь к системе (<see cref="ServerProbe"/>):
/// иначе проверка искала бы на машине настоящий сервер и зависела бы от того, что там запущено.
///
/// **Мутации, которые обязаны упасть:**
/// M1 «не открывать дескриптор у принятого» → красная <c>Внешний_выход…</c> (код выхода становится
/// «неизвестен» вместо настоящего) и <c>Код_неизвестен…</c> меняется на честный код;
/// M2 «выдумать код 0» → красная <c>Код_неизвестен_когда_дескриптора_нет</c> и
/// <c>Шарик_не_выдумывает_ноль…</c>;
/// M3 «не снимать запись и ссылку» → красная <c>Внешний_выход…</c> (файлы остаются);
/// M4 «сторож пишет и при панельном гашении» → красная <c>Гашение_панелью…</c>.
/// </summary>
public class ServerCrashByRecordTests
{
    /// <summary>Момент, который панель видит часами проверки: по нему сверяется строка журнала.</summary>
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 30, 50, TimeSpan.FromHours(3));

    /// <summary>Порт, который «занят» подставным сервером. Не 3080 и не 3081: чужие порты не трогаем.</summary>
    private const int Port = 3097;

    /// <summary>Ссылка входа — секрет: проверка подаёт её, чтобы увидеть, что панель её СНЯЛА.</summary>
    private const string EntryLink = "https://127.0.0.1:3097/?token=SECRET-ENTRY-LINK";

    /// <summary>
    /// ПОДСТАВНОЙ СЕРВЕР — НАСТОЯЩИЙ ПРОЦЕСС, живущий ≈30 секунд.
    ///
    /// ⚠️ Не <c>timeout /t 30</c>, как в задаче, и вот почему: <c>timeout.exe</c> отказывается
    /// работать при перенаправленном входе («ERROR: Input redirection is not supported»), а прогон
    /// проверки вход не оставляет. <c>ping</c> в петлю занят делом и не завершается ни при каком
    /// вводе — тот же приём и по той же причине, что в <c>OwnServerTests.LiveEngine</c> (замер
    /// 30.09.2026: <c>cmd /c</c> без дела живёт 22 мс). Убитый <c>cmd</c> оставляет <c>ping</c>
    /// ребёнком; он доживает свои секунды и уходит сам.
    /// </summary>
    private static Process StartFakeServer()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(start) ?? throw new InvalidOperationException("подставной процесс не поднялся");

        // «Жив» проверка обязана видеть фактом: время создания читается только у живого процесса.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && ProcessFacts.StartTicks(process.Id) == 0) Thread.Sleep(10);

        Assert.False(process.HasExited, "подставной процесс умер, не начав жить — проверка ослепла");
        return process;
    }

    /// <summary>
    /// Дверь к системе вместо настоящей: слушающий порт — НАШ подставной процесс, пока он жив
    /// (или пока проверке нужно, чтобы проба «видела» его живым — так проверяется ветвь без
    /// дескриптора). Время создания и отпечаток отдаются настоящие/заданные.
    /// </summary>
    private sealed class World
    {
        public int Pid { get; set; }
        public long Ticks { get; set; }
        public bool Fingerprint { get; set; } = true;

        /// <summary>Порт занят по мнению пробы.</summary>
        public bool Listens { get; set; } = true;

        /// <summary>
        /// Порт «занят» даже мёртвым процессом. Нужно ровно одному случаю: принятие по записи
        /// состоялось, а дескриптор по номеру уже не открывается — прав на процесс не дали.
        /// </summary>
        public bool ListensWhileDead { get; set; }

        /// <summary>Командная строка слушающего процесса (третья примета владения — путь движка).</summary>
        public string CommandLineText { get; set; } = string.Empty;

        public ServerProbe Probe => new(
            ListenerPid: port => port == Port && Listens && (ListensWhileDead || ProcessFacts.IsAlive(Pid)) ? Pid : 0,
            Listeners: () => new List<PortTable.Listener> { new(Port, Pid) },
            CommandLine: _ => CommandLineText,
            ProcessName: _ => "cmd",
            StartTicks: _ => Pid > 0 ? Ticks : 0,
            AnswersFingerprint: _ => Fingerprint);
    }

    /// <summary>
    /// Контроллер под своим временным корнем, с часами проверки и подставной дверью к системе.
    /// Движка на машине проверка не ищет: поднимать настоящий сервер ей нечем и незачем.
    /// </summary>
    private static ServerController Build(
        string root,
        World world,
        ServerStateFiles files,
        List<string> logged,
        string engineDir = "",
        Action? onLocateEngine = null) =>
        new(
            root,
            () => root,
            line => { lock (logged) logged.Add(line); },
            mayOccupyOwnerPort: false,
            locateEngine: () =>
            {
                onLocateEngine?.Invoke();
                return null;
            },
            requestedPort: () => Port,
            probe: world.Probe,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            files: files,
            ownEngineDir: () => engineDir,
            clock: () => At);

    /// <summary>
    /// НАСТОЯЩИЕ файлы состояния под корнем проверки: запись <c>own-server.txt</c> со своей строкой
    /// и <c>entry-link.txt</c> со ссылкой входа. Так «запись снята» доказывается отсутствием файла,
    /// а не полем подставного объекта.
    /// </summary>
    private static ServerStateFiles StateFilesUnder(string root, OwnServerRecord record, out string ownPath, out string entryPath)
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);

        // Пути — в ЛОКАЛЬНЫЕ переменные: `out`-параметр в лямбду не пустит компилятор (CS1628),
        // а делегатам нужны именно пути.
        var own = Path.Combine(state, "own-server.txt");
        var entry = Path.Combine(state, "entry-link.txt");

        ownPath = own;
        entryPath = entry;

        File.WriteAllText(own, record.Format(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(entry, EntryLink, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ServerStateFiles(
            ReadEntryLink: () => File.Exists(entry) ? File.ReadAllText(entry) : string.Empty,
            WriteEntryLink: url => File.WriteAllText(entry, url),
            ClearEntryLink: () => { if (File.Exists(entry)) File.Delete(entry); },
            ReadOwnServer: () => File.Exists(own) ? File.ReadAllText(own) : string.Empty,
            WriteOwnServer: text => File.WriteAllText(own, text),
            ClearOwnServer: () => { if (File.Exists(own)) File.Delete(own); });
    }

    private static string[] Lines(List<string> logged)
    {
        lock (logged) return logged.ToArray();
    }

    /// <summary>Строки журнала, в которых панель назвала внешний выход.</summary>
    private static string[] ItselfLines(List<string> logged) =>
        Lines(logged).Where(line => line.Contains("завершился САМ", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// ВНЕШНИЙ ВЫХОД ПРИНЯТОГО ПО ЗАПИСИ СЕРВЕРА — РОВНО ОДИН ОТЧЁТ, С КОДОМ, И СНЯТАЯ ЗАПИСЬ.
    ///
    /// **Что делает проверка ЖИВЫМ путём.** Поднимает настоящий процесс, кладёт настоящую запись
    /// <c>own-server.txt</c> с его номером и временем создания, даёт панели принять его
    /// (<c>Refresh</c>) — а потом убивает процесс руками, как это сделал бы диспетчер задач.
    /// Следующий <c>Refresh</c> обязан рассказать о выходе словами и фактами.
    ///
    /// **Мутации, которые обязаны упасть:** M1 (сторож не открыл дескриптор — код стал бы
    /// «неизвестен»), M3 (запись и ссылка не сняты).
    /// </summary>
    [Fact]
    public void Внешний_выход_принятого_по_записи_сервера_сообщается_один_раз()
    {
        var root = TempRoots.For("server-crash-by-record");
        var logged = new List<string>();
        var crashes = new List<ServerCrash>();
        var engineLookups = 0;
        Process? server = null;

        try
        {
            Directory.CreateDirectory(root);

            server = StartFakeServer();
            var pid = server.Id;
            var ticks = ProcessFacts.StartTicks(pid);

            Assert.True(ticks > 0, "время создания настоящего процесса не прочиталось — проверка ослепла");

            var world = new World { Pid = pid, Ticks = ticks };
            var record = new OwnServerRecord(Port, pid, ticks);
            var files = StateFilesUnder(root, record, out var ownPath, out var entryPath);

            using var controller = Build(root, world, files, logged, onLocateEngine: () => engineLookups++);
            controller.Crashed += crash => { lock (crashes) crashes.Add(crash); };

            var accepted = controller.Refresh();

            // ПРИНЯТ ПО ЗАПИСИ — и под наблюдением: без дескриптора смерть была бы тишиной.
            Assert.Equal(ServerOwner.Panel, controller.Owner);
            Assert.True(accepted.IsRunning);
            Assert.Equal(pid, accepted.Pid);
            Assert.True(controller.WatchingRecordedOwn, "дескриптор принятого сервера не открыт — сторож не завёлся");
            Assert.True(File.Exists(ownPath), "запись о своём сервере не на месте — проверять снятие нечего");

            // ВНЕШНЕЕ УБИЙСТВО — не панелью: она об этом ещё ничего не знает.
            server.Kill();
            server.WaitForExit();

            // Код выхода, с каким процесс ДЕЙСТВИТЕЛЬНО ушёл: спрашиваем у своего дескриптора
            // проверки, чтобы утверждение о журнале не было выдумкой.
            var code = server.ExitCode;

            var state = controller.Refresh();

            // КАНАЛ 1 — ЖУРНАЛ: ровно одна строка «завершился САМ», и в ней PID, код, время и хвост.
            var expected = string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.SrvEngineExitItselfLogFormat,
                pid,
                code.ToString(CultureInfo.InvariantCulture),
                EngineExitReport.Moment(At),
                PanelStrings.SrvEngineExitNoTailLog);

            var itself = ItselfLines(logged);
            Assert.True(itself.Length == 1, $"строк «завершился САМ» — {itself.Length}, а обязана быть одна");
            Assert.Equal(expected, itself[0]);

            // КАНАЛ 2 — СОБЫТИЕ: ровно одно, с тем же кодом и моментом (шарик подписан в App).
            var crash = Assert.Single(crashes);
            Assert.Equal(pid, crash.Pid);
            Assert.Equal(code, crash.ExitCode);
            Assert.False(crash.CodeUnknown);
            Assert.Equal(At, crash.At);

            // КАНАЛ 3 — запись и ссылка входа СНЯТЫ, состояние «остановлен», авто-подъёма нет.
            Assert.False(File.Exists(ownPath), "запись o своём сервере осталась — мёртвый адрес");
            Assert.False(File.Exists(entryPath), "ссылка входа осталась — мёртвый адрес");
            Assert.Equal(ServerPresence.Stopped, state.Presence);
            Assert.Equal(0, engineLookups);
            Assert.False(controller.WatchingRecordedOwn, "дескриптор ушедшего процесса не отпущен");
        }
        finally
        {
            server?.Dispose();
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// КОД ВЫХОДА СПРОСИТЬ НЕ УДАЛОСЬ — ПАНЕЛЬ ГОВОРИТ «НЕИЗВЕСТЕН», А НЕ НОЛЬ (DEF-057).
    ///
    /// **Случай настоящий:** сервер принят по записи, а дескриптор по его номеру не открывается
    /// (прав на процесс не дали; здесь — процесс уже ушёл, и открывать нечего). Тогда единственный
    /// признак выхода — опустевший порт, и кода у панели нет вовсе.
    ///
    /// **Почему это отдельная проверка.** Ноль — ТОЖЕ КОД, и человек прочитал бы «код выхода: 0»
    /// как «сервер вышел чисто». Поэтому третий ответ называется словами, и в шарике тоже
    /// (<see cref="ServerCrashDecisions.NoticeText"/>).
    ///
    /// **Мутации, которые обязаны упасть:** M2 (выдуманный ноль вместо «неизвестен»).
    /// </summary>
    [Fact]
    public void Код_неизвестен_когда_дескриптора_нет()
    {
        var root = TempRoots.For("server-crash-by-record-unknown");
        var logged = new List<string>();
        var crashes = new List<ServerCrash>();
        Process? gone = null;

        try
        {
            Directory.CreateDirectory(root);

            // Процесс, который УЖЕ УШЁЛ: его номера в таблице процессов больше нет.
            gone = StartFakeServer();
            var pid = gone.Id;
            var ticks = ProcessFacts.StartTicks(pid);

            gone.Kill();
            gone.WaitForExit();

            Assert.True(ticks > 0, "время создания настоящего процесса не прочиталось — проверка ослепла");

            // Проба «видит» слушателя, которого уже нет: так проверяется принятие по записи
            // в тот миг, когда дескриптор по номеру открыть нельзя.
            var world = new World { Pid = pid, Ticks = ticks, Listens = true, ListensWhileDead = true };
            var record = new OwnServerRecord(Port, pid, ticks);
            var files = StateFilesUnder(root, record, out var ownPath, out var entryPath);

            using var controller = Build(root, world, files, logged);
            controller.Crashed += crash => { lock (crashes) crashes.Add(crash); };

            controller.Refresh();

            // Принят — но дескриптора нет: это и есть ветвь «код неизвестен».
            Assert.Equal(ServerOwner.Panel, controller.Owner);
            Assert.False(controller.WatchingRecordedOwn, "дескриптор ушедшего процесса открылся — проверка не про этот случай");

            // Порт опустел и МОЛЧИТ (и слушателя, и отпечатка нет) — второго способа узнать
            // о выходе у панели нет. ⚠ `Fingerprint = false` обязателен: живой порт, который
            // ОТВЕЧАЕТ сервером DSH, панель смертью не считает (DEF-074, §4.2) — там запись
            // и ссылка входа остаются, а говорится «не удалось узнать, кто слушает».
            world.Listens = false;
            world.Fingerprint = false;

            var state = controller.Refresh();

            var expected = string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.SrvEngineExitItselfLogFormat,
                pid,
                PanelStrings.SrvEngineExitCodeUnknownLog,
                EngineExitReport.Moment(At),
                PanelStrings.SrvEngineExitNoTailLog);

            var itself = ItselfLines(logged);
            Assert.True(itself.Length == 1, $"строк «завершился САМ» — {itself.Length}, а обязана быть одна");
            Assert.Equal(expected, itself[0]);

            var crash = Assert.Single(crashes);
            Assert.Null(crash.ExitCode);
            Assert.True(crash.CodeUnknown);

            // ШАРИК: «неизвестен», а не ноль. Сверяется собранной строкой, а не поиском цифры:
            // в тексте есть время, и «0» нашёлся бы в нём.
            var notice = ServerCrashDecisions.NoticeText(crash);
            Assert.Equal(
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.NotifyServerCrashedFormat,
                    PanelStrings.SrvEngineExitCodeUnknownLog,
                    EngineExitReport.Moment(At)),
                notice);

            Assert.False(File.Exists(ownPath));
            Assert.False(File.Exists(entryPath));
            Assert.Equal(ServerPresence.Stopped, state.Presence);
        }
        finally
        {
            gone?.Dispose();
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// ГАШЕНИЕ ПАНЕЛЬЮ СВОЕГО СЕРВЕРА, ПРИНЯТОГО ПО ЗАПИСИ, — «ОСТАНОВЛЕН ПАНЕЛЬЮ», И НИ ОДНОЙ
    /// СТРОКИ «ЗАВЕРШИЛСЯ САМ» (DEF-057).
    ///
    /// **От какого случая.** Сторож и кнопка «Остановить» смотрят на один процесс. Если сторожа
    /// не заглушить ДО гашения, его сообщение о выходе придёт ПОСЛЕ панельного — и человек
    /// прочитает «сервер завершился САМ» про процесс, который погасил сам.
    ///
    /// ⚠️ **Ждём нарочно:** сообщение `Exited` приходит своей ниткой, и «сразу после Stop» его
    /// ещё нет. Ожидание ограничено секундой — этого хватает, а прогон не растягивает.
    ///
    /// **Мутация, которая обязана упасть:** M4 (не глушить сторожа перед `Kill`).
    /// </summary>
    [Fact]
    public void Гашение_панелью_не_называет_выход_самостоятельным()
    {
        var root = TempRoots.For("server-crash-by-record-panel");
        var logged = new List<string>();
        var crashes = new List<ServerCrash>();
        Process? server = null;

        try
        {
            Directory.CreateDirectory(root);

            server = StartFakeServer();
            var pid = server.Id;
            var ticks = ProcessFacts.StartTicks(pid);

            Assert.True(ticks > 0, "время создания настоящего процесса не прочиталось — проверка ослепла");

            var world = new World { Pid = pid, Ticks = ticks };
            var record = new OwnServerRecord(Port, pid, ticks);
            var files = StateFilesUnder(root, record, out var ownPath, out var entryPath);

            using var controller = Build(root, world, files, logged);
            controller.Crashed += crash => { lock (crashes) crashes.Add(crash); };

            controller.Refresh();
            Assert.Equal(ServerOwner.Panel, controller.Owner);
            Assert.True(controller.WatchingRecordedOwn);

            var state = controller.Stop(confirmed: true);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (DateTime.UtcNow < deadline && ItselfLines(logged).Length == 0) Thread.Sleep(20);

            var lines = Lines(logged);

            // Панель сказала СВОЁ слово — и это слово про гашение.
            Assert.Contains(lines, line => line.Contains("остановлен панелью", StringComparison.Ordinal));

            // И ни одного «завершился САМ» — ни сейчас, ни через мгновение.
            Assert.DoesNotContain(lines, line => line.Contains("завершился САМ", StringComparison.Ordinal));
            Assert.Empty(crashes);

            // Сторож снят гашением, а не сообщением о выходе.
            Assert.False(controller.WatchingRecordedOwn, "сторож пережил гашение панелью");

            Assert.Equal(ServerPresence.Stopped, state.Presence);
            Assert.False(File.Exists(ownPath));
            Assert.False(File.Exists(entryPath));
        }
        finally
        {
            server?.Dispose();
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// ЗАКРЫТИЕ ПАНЕЛИ ОТПУСКАЕТ ДЕСКРИПТОР И НЕ ГАСИТ СЕРВЕР (DEF-057).
    ///
    /// **Почему это проверка, а не обещание.** Дескриптор процесса — ресурс системы; закрытая
    /// панель не имеет права держать его вечно, и «сообщать о выходе после закрытия» некому.
    /// Сервер при этом живёт: закрытие окна не обрывает работу человека (правило с самого начала).
    ///
    /// **Мутация, которая обязана упасть:** убрать `StopWatching()` из `Dispose`.
    /// </summary>
    [Fact]
    public void Dispose_отпускает_дескриптор_и_не_гасит_сервер()
    {
        var root = TempRoots.For("server-crash-by-record-dispose");
        var logged = new List<string>();
        var crashes = new List<ServerCrash>();
        Process? server = null;

        try
        {
            Directory.CreateDirectory(root);

            server = StartFakeServer();
            var pid = server.Id;
            var ticks = ProcessFacts.StartTicks(pid);

            Assert.True(ticks > 0, "время создания настоящего процесса не прочиталось — проверка ослепла");

            var world = new World { Pid = pid, Ticks = ticks };
            var record = new OwnServerRecord(Port, pid, ticks);
            var files = StateFilesUnder(root, record, out var ownPath, out _);

            var controller = Build(root, world, files, logged);
            controller.Crashed += crash => { lock (crashes) crashes.Add(crash); };

            controller.Refresh();
            Assert.True(controller.WatchingRecordedOwn);

            controller.Dispose();

            // Дескриптор отпущен, сервер ЖИВ, запись на месте: следующая панель узнает его по ней.
            Assert.False(controller.WatchingRecordedOwn);
            Assert.False(server.HasExited, "закрытие панели погасило сервер — так нельзя");
            Assert.True(File.Exists(ownPath), "закрытие панели сняло запись — следующая панель не узнает свой сервер");

            // И о выходе после закрытия уже не сообщается: сообщать некому.
            server.Kill();
            server.WaitForExit();
            Thread.Sleep(200);

            Assert.Empty(crashes);
            Assert.DoesNotContain(Lines(logged), line => line.Contains("завершился САМ", StringComparison.Ordinal));
        }
        finally
        {
            server?.Dispose();
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// ПОДПИСКА СВЕРЯЕТ ВРЕМЯ СОЗДАНИЯ: ЧУЖОЙ ПРОЦЕСС С ТЕМ ЖЕ НОМЕРОМ НЕ СТЕРЕЖЁТСЯ (DEF-057).
    ///
    /// **От какого урока (v1).** Номера процессов переиспользуются. Если панель подпишется на
    /// номер, не сверив время создания, она однажды назовёт падением СВОЕГО сервера смерть чужой
    /// программы — и погасит не то. Гашение такую сверку делает с самого начала
    /// (<see cref="TreeProcessKiller"/>); сторож обязан делать ту же.
    ///
    /// **Как это ловится.** Сервер принят по пути движка (запись ему не пара), а проба называет
    /// время создания, которого у живого процесса нет. Подписки быть не должно.
    ///
    /// **Мутация, которая обязана упасть:** убрать сверку `StartTime.Ticks` в `WatchRecordedOwn`.
    /// </summary>
    [Fact]
    public void Подписка_не_стережёт_чужой_процесс_с_тем_же_номером()
    {
        var root = TempRoots.For("server-crash-by-record-reused-pid");
        var logged = new List<string>();
        Process? server = null;

        try
        {
            Directory.CreateDirectory(root);

            server = StartFakeServer();
            var pid = server.Id;
            var realTicks = ProcessFacts.StartTicks(pid);

            Assert.True(realTicks > 0, "время создания настоящего процесса не прочиталось — проверка ослепла");

            const string engineDir = @"C:\Temp\dsh-panel-tests\own-engine";
            var ownBin = OwnEngine.BinPath(engineDir, "0.2.1-rc.2");

            // Запись — про ДРУГОЙ номер, а время создания проба называет чужое: настоящий процесс
            // с этим номером поднят не в то мгновение, которое записано.
            var record = new OwnServerRecord(Port, 4242, realTicks + 1, ownBin, "0.2.1-rc.2");

            var world = new World
            {
                Pid = pid,
                Ticks = realTicks + 1,
                CommandLineText = "\"C:\\Program Files\\nodejs\\node.exe\" " + ownBin + " web --no-open --port " + Port,
            };

            var files = StateFilesUnder(root, record, out _, out _);

            using var controller = Build(root, world, files, logged, engineDir: engineDir);

            var accepted = controller.Refresh();

            // Сервер признан своим (по пути движка) — иначе проверять нечего.
            Assert.Equal(ServerOwner.Panel, controller.Owner);
            Assert.True(accepted.IsRunning);

            // Но наблюдение за ним НЕ заведено: время создания не сошлось.
            Assert.False(controller.WatchingRecordedOwn, "панель подписалась на процесс, не сверив время создания");
        }
        finally
        {
            server?.Dispose();
            TempRoots.Remove(root);
        }
    }
}
