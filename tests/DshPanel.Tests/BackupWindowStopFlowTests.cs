using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DshPanel.Backup;
using DshPanel.Restore;
using DshPanel.Server;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВЯЗКА окна копий с сервером: что происходит после ответа на вопрос «сервер работает — погасить?».
///
/// Зачем эти проверки. Решение по этому вопросу жило в обработчике нажатия, где его не мог достать
/// ни один прогон без экрана, — и было НЕВЕРНЫМ: выбор «Погасить и продолжить» не гасил ничего
/// (вызов гашения не делался вовсе, метод лежал мёртвым), а движку при этом сообщалось, что сервер
/// не работает. Значит из отчёта пропадала честная оговорка «копия снята на ходу», и человек читал
/// «копия целая» там, где снималась копия с живого процесса. Нашёл рабочий-подагент 26.09.2026,
/// закрывая дыру покрытия у окна вопроса.
///
/// Работа идёт ТЕМ ЖЕ путём, что у человека: кнопки нажимаются по-настоящему (<c>RaiseEvent</c>),
/// копия выбирается в списке по-настоящему. Отдано проверкам ровно одно — ответ на диалог вопроса:
/// в проверке без экрана настоящий диалог всплывает сам и отвечает не то, что решил бы человек.
/// </summary>
public class BackupWindowStopFlowTests
{
    /// <summary>
    /// Дождаться условия, ПРОКРУЧИВАЯ очередь диспетчера. Без этого продолжение после первого
    /// <c>await</c> не исполняется вовсе: окно возвращается в поток интерфейса, а в проверке без
    /// экрана этого потока никто не крутит — и проверка падала бы «сама по себе», хотя код верен.
    /// </summary>
    private static bool WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 800 && !condition(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>Подставной сервер: ведёт журнал вызовов гашения и отвечает по заказу.</summary>
    private sealed class StubServer : IServerControl
    {
        public StubServer(
            bool runs, ServerOwner owner = ServerOwner.Panel, bool stopSucceeds = true,
            bool consentRemembered = false)
        {
            Runs = runs;
            Owner = owner;
            StopSucceeds = stopSucceeds;
            ConsentRemembered = consentRemembered;
            State = runs
                ? new ServerState(ServerPresence.Running, 3081, 4242, "node", "отвечает")
                : ServerState.Stopped(3081);
        }

        public bool Runs { get; }
        public bool StopSucceeds { get; }
        public int StopCalls => Stops.Count;
        public List<(bool Confirmed, ServerPresence Presence)> Stops { get; } = new();
        public ServerState State { get; private set; }
        public ServerOwner Owner { get; }

        /// <summary>Запомнено ли согласие человека на этот сервер (решение владельца 26.09.2026).</summary>
        public bool ConsentRemembered { get; }

        /// <summary>Ссылка входа (в ней токен) — окну копий она не нужна, и здесь её нет.</summary>
        public string EntryLink => string.Empty;

        public ServerState Refresh() => State;
        public ServerState Start(TimeSpan timeout) => State;
        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;
        public DiscoveryResult Scan() => new(TableReadable: false, Array.Empty<FoundServer>());
        public ServerState Adopt(FoundServer found) => State;
        public ServerState Detach() => State;

        public ServerState Stop(bool confirmed)
        {
            Stops.Add((confirmed, State.Presence));
            if (!StopSucceeds) return State;
            State = ServerState.Stopped(3081);
            return State;
        }
    }

    /// <summary>
    /// Подставной домен копий: помнит, с каким словом о сервере его позвали.
    ///
    /// <c>internal</c>, а не <c>private</c>: этим же подставным доменом пользуются проверки
    /// содержимого окна копий (<c>WindowContentTests</c>: полоса прокрутки и прокрутка к плану
    /// наката, п. 4–5 и 8 `docs\DESIGN.md`). Второй такой же подмены в прогоне быть не должно:
    /// IBackupControl широкий, и две разошедшиеся подмены проверяли бы разные окна.
    /// </summary>
    internal sealed class StubBackups : IBackupControl
    {
        public int CreateCalls { get; private set; }
        public bool? LastServerRunning { get; private set; }

        /// <summary>С каким решением о передаче позвали копию: <c>null</c> — ещё не звали вовсе.</summary>
        public bool? LastShareable { get; private set; }

        public int RestoreCalls { get; private set; }
        public bool? LastRestoreServerRunning { get; private set; }

        public string Folder => Path.GetTempPath();
        public bool FolderFromDefault => true;
        public bool Writable => true;

        /// <summary>
        /// Одна готовая копия — чтобы окно могло показать план наката, как это делает человек.
        /// Список ОДИН и тот же на всех чтениях по умолчанию: окно перечитывает его при каждой
        /// отрисовке, а новый экземпляр на каждый вызов заставлял таблицу пересобираться заново —
        /// и окно уходило в лавину перерисовок (проверено: переполнение стека в прогоне, 26.09.2026).
        ///
        /// ⚠️ Свойство ЗАПИСЫВАЕМОЕ: проверке счётчика и таблицы нужен список подлиннее, и второй
        /// такой же подмены в прогоне быть не должно — <c>IBackupControl</c> широкий, и две
        /// разошедшиеся подмены проверяли бы разные окна.
        /// </summary>
        private static readonly IReadOnlyList<BackupEntry> OneReadyCopy = new[]
        {
            new BackupEntry
            {
                Path = Path.Combine(Path.GetTempPath(), "stub-copy.zip"),
                CreatedAt = DateTime.Now,
                Bytes = 1024,
                Files = 3,
                Readable = true,
            },
        };

        public IReadOnlyList<BackupEntry> Entries { get; set; } = OneReadyCopy;

        public string ListProblem => string.Empty;
        public string InstalledEngineVersion => "0.0.0-test";

        /// <summary>
        /// Кладутся ли ключи в копию. Свойство ЗАПИСЫВАЕМОЕ: проверке «сочетание с приватными
        /// ключами запрещено» нужно состояние, в котором ключи уже кладутся, — а настоящее
        /// разрешение живёт в настройках, которых у подставного домена нет.
        /// </summary>
        public bool WithKeys { get; set; }

        public bool CanChangeComposition => true;
        public IReadOnlyList<string> KeyDirectories => Array.Empty<string>();
        public IReadOnlyList<KeyDirSuggestion> KeySuggestions => Array.Empty<KeyDirSuggestion>();

        /// <summary>
        /// Режим объёма и лишние имена (v2.2) — как у настоящего контроллера: подставной домен
        /// ПОМНИТ то, что записали, и отдаёт записанное. Иначе окно показывало бы не то, что
        /// сохранилось, и проверка «выбор человека доходит до настроек» была бы слепой.
        /// </summary>
        public BackupScope Scope { get; set; } = BackupScope.Auto;

        public IReadOnlyList<string> ExtraExclusions { get; private set; } = Array.Empty<string>();

        /// <summary>Сколько «насчитал» подставной замер: число задаёт проверка.</summary>
        public long EstimateBytes { get; set; } = 12_345_678;

        public bool Busy => false;

        public bool SetScope(BackupScope scope)
        {
            Scope = scope;
            return true;
        }

        public bool SetExtraExclusions(IReadOnlyList<string> names)
        {
            ExtraExclusions = names;
            return true;
        }

        public BackupEstimate Estimate(BackupScope scope) => new(true, EstimateBytes);

        /// <summary>Событие «папка перечитана»: у подставного домена его поднимает проверка.</summary>
        public event Action? Changed;

        public void RaiseChanged() => Changed?.Invoke();

        public bool SetWithKeys(bool value) => true;
        public bool AddKeyDirectory(string path) => true;
        public void Refresh() { }

        public string NextArchivePath() =>
            Path.Combine(Path.GetTempPath(), "stub-copy-" + Guid.NewGuid().ToString("N")[..8] + ".zip");

        public CopyOutcome CreateCopy(
            string archivePath, bool serverRunning, bool shareable, Action<string>? progress = null)
        {
            CreateCalls++;
            LastServerRunning = serverRunning;

            // Решение о передаче запоминается НАСТОЯЩИМ значением, а не «позвали»: проверке окна
            // нужно доказать, что галочка доехала до копии именно как решение про ОДНУ копию.
            LastShareable = shareable;

            return new CopyOutcome(true, string.Empty, null);
        }

        /// <summary>План наката — настоящего типа, чтобы окно показывало его как обычно.</summary>
        public RestorePlan PlanRestore(string archivePath, bool withEngine, bool withPanel, bool withKeys) =>
            new(
                Ok: true,
                Error: string.Empty,
                ArchivePath: archivePath,
                Manifest: new BackupManifest { Machine = "TEST-MACHINE", User = "tester" },
                WorkingDirectory: Path.GetTempPath(),
                Groups: Array.Empty<RestoreGroup>(),
                Notes: Array.Empty<string>(),
                UnsafeEntries: 0);

        public RestoreOutcome RunRestore(
            RestorePlan plan, bool withEngine, bool withPanel, bool withKeys, bool serverRunning,
            Action<string>? progress = null)
        {
            RestoreCalls++;
            LastRestoreServerRunning = serverRunning;
            return new RestoreOutcome(true, string.Empty, null);
        }
    }

    private static (BackupWindow Window, StubBackups Backups, StubServer Server) Stand(
        bool runs, StopChoice answer, bool stopSucceeds = true, ServerOwner owner = ServerOwner.Panel,
        bool consentRemembered = false)
    {
        var backups = new StubBackups();
        var server = new StubServer(runs, owner, stopSucceeds, consentRemembered);

        var window = new BackupWindow();
        window.Attach(backups, server);

        // Единственное, что отдано проверке: ответ на диалог вопроса.
        window.StopChoiceForTests = () => answer;

        return (window, backups, server);
    }

    /// <summary>Нажать настоящую кнопку окна — тот же путь, каким идёт человек.</summary>
    private static void Нажать(BackupWindow window, string buttonName) =>
        window.FindControl<Button>(buttonName)!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Выбрать копию — ровно то, что делает человек перед восстановлением. Зовётся та же дверь,
    /// которую дёргает щелчок по строке таблицы (<c>BackupWindow.ChooseEntry</c>).
    /// </summary>
    private static void ВыбратьКопию(BackupWindow window)
    {
        Assert.True(window.ListedCount > 0, "в таблице нет ни одной копии — выбирать нечего");
        window.ChooseEntry(0);
    }

    // ---------------------------------------------------------------- копия

    [AvaloniaFact]
    public void Сервер_не_работает_гашения_нет_и_движку_сказано_что_сервер_не_работает()
    {
        var (window, backups, server) = Stand(runs: false, StopChoice.ContinueLive);

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => backups.CreateCalls == 1), "копия не началась");
        Assert.Equal(0, server.StopCalls);
        Assert.False(backups.LastServerRunning, "сервер не работал — движок не должен говорить «на ходу»");
    }

    [AvaloniaFact]
    public void Отмена_не_гасит_и_не_снимает_копию()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.Cancel);

        Нажать(window, "CreateButton");

        Assert.False(WaitFor(() => backups.CreateCalls > 0), "«Отмена» обязана значить «ничего не делать»");
        Assert.Equal(0, server.StopCalls);
    }

    /// <summary>
    /// ГЛАВНАЯ проверка. Человек выбрал «Погасить и продолжить» — окно ОБЯЗАНО позвать гашение
    /// и только после успеха сказать движку, что сервер не работает.
    /// </summary>
    [AvaloniaFact]
    public void Погасить_и_продолжить_гасит_сервер_и_только_тогда_снимает_оговорку()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.StopAndContinue, stopSucceeds: true);

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => backups.CreateCalls == 1), "копия не началась после гашения");
        Assert.Equal(1, server.StopCalls);
        Assert.False(backups.LastServerRunning, "сервер погашен — копия не «на ходу», и это правда");
    }

    /// <summary>
    /// Сервер жив, человек просил погасить, погасить НЕ удалось. Копию снимать нельзя (обещание
    /// «копия получается целой» не выполнено), но и молчать об этом движку нельзя: если бы копия
    /// снималась, оговорка «на ходу» обязана была остаться. Старое поведение снимало её — и врало.
    /// </summary>
    [AvaloniaFact]
    public void Не_удалось_погасить_копия_не_снимается()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.StopAndContinue, stopSucceeds: false);

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => server.StopCalls == 1), "гашение обязано быть хотя бы попробовано");
        Assert.False(WaitFor(() => backups.CreateCalls > 0), "обещание «целой копии» не выполнено — снимать нельзя");
    }

    [AvaloniaFact]
    public void Копия_на_ходу_не_гасит_и_называет_это_в_отчёте()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.ContinueLive);

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => backups.CreateCalls == 1), "копия не началась");
        Assert.Equal(0, server.StopCalls);
        Assert.True(backups.LastServerRunning, "человек выбрал копию на ходу — движок обязан назвать это");
    }

    /// <summary>
    /// СВОЙ сервер панели гасится без вопроса: подтверждение ему не нужно, и окно обязано передать
    /// в контроллер ИМЕННО «согласие есть». Смысл имени здесь легко перепутать: <c>confirmed</c>
    /// значит «разрешение на гашение», а не «человек подтвердил».
    ///
    /// ⚠️ Случай «взятый под управление» (там подтверждение обязательно) в этом файле НЕ проверяется:
    /// его спрашивает настоящий диалог <see cref="ConfirmStopWindow"/>, а в проверке без экрана он
    /// всплывает сам и отвечает не то, что решил бы человек. Держится на <c>ServerTests</c>
    /// (<see cref="ServerDecisions.NeedsStopConfirmation"/>) — а здесь важно, что окно зовёт гашение
    /// и говорит движку правду о сервере.
    /// </summary>
    [AvaloniaFact]
    public void Свой_сервер_гасится_без_вопроса_и_копия_снимается_с_погашенного()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.StopAndContinue, owner: ServerOwner.Panel);

        Assert.Equal(ServerPresence.Running, server.State.Presence);
        Assert.False(ServerDecisions.NeedsStopConfirmation(
            ServerOwner.Panel, ServerPresence.Running, consentRemembered: false));

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => server.StopCalls == 1), "свой сервер не погашен");
        Assert.True(WaitFor(() => backups.CreateCalls == 1), "после гашения копия не началась");

        var call = Assert.Single(server.Stops);
        Assert.True(call.Confirmed, "своему серверу панель вправе дать согласие сама — вопроса человеку нет");
        Assert.False(backups.LastServerRunning, "сервер погашен — движок не должен говорить «на ходу»");
    }

    /// <summary>
    /// ВЗЯТЫЙ ПОД УПРАВЛЕНИЕ сервер с ЗАПОМНЕННЫМ согласием гасится без вопроса — и здесь,
    /// в окне копий, тоже. Решение владельца 26.09.2026: он уже ответил «беру под управление»,
    /// и спрашивать второй раз нечего.
    ///
    /// Проверка идёт через само состояние: диалог подтверждения в этом случае НЕ создаётся вовсе.
    /// Если бы он создавался, работа не дошла бы до конца — <c>WaitFor</c> не дождался бы копии,
    /// и проверка упала бы. Именно так этот случай и отличается от «встроен без согласия»:
    /// тот спрашивает подтверждение (<c>ServerTests</c>), и здесь его не подделать.
    /// </summary>
    [AvaloniaFact]
    public void Взятый_под_управление_с_запомненным_согласием_гасится_без_вопроса()
    {
        var (window, backups, server) = Stand(
            runs: true, StopChoice.StopAndContinue, owner: ServerOwner.Adopted, consentRemembered: true);

        Assert.False(ServerDecisions.NeedsStopConfirmation(
            ServerOwner.Adopted, ServerPresence.Running, consentRemembered: true));

        Нажать(window, "CreateButton");

        Assert.True(WaitFor(() => server.StopCalls == 1), "сервер с запомненным согласием не погашен");
        Assert.True(WaitFor(() => backups.CreateCalls == 1), "после гашения копия не началась");

        var call = Assert.Single(server.Stops);
        Assert.True(call.Confirmed, "согласие запомнено — подтверждение уже дано человеком однажды");
        Assert.False(backups.LastServerRunning, "сервер погашен — движок не должен говорить «на ходу»");
    }

    // ---------------------------------------------------------------- накат

    /// <summary>
    /// ТА ЖЕ развилка, но у НАКАТА. Отдельные проверки, потому что это второй, независимый путь
    /// с той же бедой: накат поверх работающего сервера не заменит занятые файлы. Дыру нашёл
    /// холодный проверяющий 26.09.2026 — мутация «убрать гашение в StartRestore» **выжила**,
    /// то есть ветка наката не была покрыта ничем.
    /// </summary>
    [AvaloniaFact]
    public void Накат_по_просьбе_погасить_гасит_сервер_и_снимает_оговорку()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.StopAndContinue, owner: ServerOwner.Panel);
        ВыбратьКопию(window);

        Assert.NotNull(window.Plan);   // без плана окно накат не начинает — и проверка была бы слепой

        Нажать(window, "RunRestoreButton");

        Assert.True(WaitFor(() => server.StopCalls == 1), "накат не погасил сервер по просьбе человека");
        Assert.True(WaitFor(() => backups.RestoreCalls == 1), "накат не начался после гашения");
        Assert.False(backups.LastRestoreServerRunning, "сервер погашен — накат не «на ходу», и это правда");

        var call = Assert.Single(server.Stops);
        Assert.True(call.Confirmed, "своему серверу панель вправе дать согласие сама");
    }

    [AvaloniaFact]
    public void Накат_на_ходу_не_гасит_и_называет_это_в_отчёте()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.ContinueLive);
        ВыбратьКопию(window);

        Нажать(window, "RunRestoreButton");

        Assert.True(WaitFor(() => backups.RestoreCalls == 1), "накат на ходу не начался");
        Assert.Equal(0, server.StopCalls);
        Assert.True(backups.LastRestoreServerRunning, "человек выбрал накат на ходу — движок обязан это назвать");
    }

    /// <summary>
    /// Накат с отменой: ни гашения, ни работы. План при этом показан — значит пустой исход
    /// получается именно из-за отмены, а не потому, что окну нечего было делать.
    /// </summary>
    [AvaloniaFact]
    public void Накат_по_отмене_не_гасит_и_не_делается()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.Cancel);
        ВыбратьКопию(window);

        Assert.NotNull(window.Plan);

        Нажать(window, "RunRestoreButton");

        Assert.False(WaitFor(() => backups.RestoreCalls > 0), "«Отмена» обязана значить «ничего не делать»");
        Assert.Equal(0, server.StopCalls);
    }

    /// <summary>
    /// Накат, который НЕ удалось начать из-за неудавшегося гашения: работы нет, а оговорка о живом
    /// сервере была бы правдой — её и требует движок, если бы накат всё-таки пошёл.
    /// </summary>
    [AvaloniaFact]
    public void Накат_не_удалось_погасить_не_делается()
    {
        var (window, backups, server) = Stand(runs: true, StopChoice.StopAndContinue, stopSucceeds: false);
        ВыбратьКопию(window);

        Нажать(window, "RunRestoreButton");

        Assert.True(WaitFor(() => server.StopCalls == 1), "гашение обязано быть хотя бы попробовано");
        Assert.False(WaitFor(() => backups.RestoreCalls > 0), "сервер жив — накат поверх него не начинается");
    }
}
