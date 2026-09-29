using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КНОПКА «ПЕРЕЗАПУСТИТЬ» (запрос владельца 27.09.2026) — между «Запустить» и «Остановить».
///
/// Обещаний у неё четыре, и каждое проверяется СВОИМ путём:
///
/// 1. **место в ряду** — СТРОГО между «Запустить» и «Остановить», и меряется это ГЕОМЕТРИЕЙ
///    (по X), а не только родством в дереве: «в дереве рядом» и «на экране рядом» — разные вещи;
/// 2. **доступность** — ровно тогда, когда доступна «Остановить», и это ЗАКОН, а не совпадение:
///    перезапуск = «остановить» + «запустить» (<see cref="ServerPanelModel.CanRestart"/>);
/// 3. **подпись** — у найденного сервера с многоточием («сначала спрошу»), у своего — без;
/// 4. **подсказка** — у недоступной кнопки называет ПРИЧИНУ, и причины у разных положений дел
///    РАЗНЫЕ, а в занятости — одна на все кнопки.
///
/// И отдельно — предохранитель, ради которого весь шаг и делался: БЕЗ подтверждения контроллер
/// не гасит встроенный сервер (проверка с подставным убийцей, как в <c>DiscoveryTests</c>),
/// а окно в этом случае не зовёт перезапуск вовсе.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class RestartServerTests
{
    // ---------------------------------------------------------------- подставной сервер окна

    /// <summary>
    /// Подставной сервер окна: помнит, с каким подтверждением его просили перезапустить.
    /// Настоящий движок в этих проверках не поднимается вовсе.
    /// </summary>
    private sealed class RecordingServer : IServerControl
    {
        public RecordingServer(ServerState state, ServerOwner owner = ServerOwner.None, bool consent = false)
        {
            State = state;
            Owner = owner;
            ConsentRemembered = consent;
        }

        public ServerState State { get; set; }

        public ServerOwner Owner { get; }

        public bool ConsentRemembered { get; }

        public string EntryLink => string.Empty;

        /// <summary>Все вызовы перезапуска: время ожидания и подтверждение.</summary>
        public List<(TimeSpan Timeout, bool Confirmed)> Restarts { get; } = new();

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed)
        {
            Restarts.Add((timeout, confirmed));
            return State;
        }

        public DiscoveryResult Scan() => new(true, Array.Empty<FoundServer>());

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }

    private static readonly ServerState Running =
        new(ServerPresence.Running, 3080, 4242, "node", "сервер DSH отвечает на порту 3080");

    private static readonly ServerState ForeignPort =
        new(ServerPresence.BusyByOther, 3080, 77, "chrome", "порт занят другой программой");

    /// <summary>Окно с заданным состоянием сервера — показанное, потому что меряется геометрия.</summary>
    private static MainWindow Stand(ServerState state, ServerOwner owner = ServerOwner.None, bool consent = false)
    {
        var window = PanelTestStand.MainWith(new RecordingServer(state, owner, consent));
        window.Show();
        PanelTestStand.Settle();

        return window;
    }

    private static Button Require(Window window, string name)
    {
        var button = window.FindControl<Button>(name);
        Assert.NotNull(button);

        return button!;
    }

    // ---------------------------------------------------------------- 1. место в ряду

    /// <summary>
    /// КНОПКА ПЕРЕЗАПУСКА СТОИТ В РЯДУ СЕРВЕРА МЕЖДУ «ЗАПУСТИТЬ» И «ОСТАНОВИТЬ» — и это видно
    /// на экране, а не только в дереве.
    ///
    /// Почему ГЕОМЕТРИЯ, а не дерево. Порядок в <c>WrapPanel</c> можно случайно переставить,
    /// и «в дереве рядом» ничего не сказало бы о том, что человек видит: при переносе ряда
    /// порядок по X и есть то единственное, что он видит. Мерятся КРАЙНИЕ ТОЧКИ по X, а не
    /// «есть в списке соседей».
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_перезапуска_стоит_между_запустить_и_остановить()
    {
        var window = Stand(ServerState.Stopped(0));

        var start = Require(window, "StartServerButton");
        var restart = Require(window, "RestartServerButton");
        var stop = Require(window, "StopServerButton");

        // Ряд один и тот же — тот же переносимый WrapPanel, что у соседей.
        var row = start.Parent as WrapPanel;
        Assert.NotNull(row);
        Assert.Same(row, restart.Parent);
        Assert.Same(row, stop.Parent);

        // И порядок в самом ряду: перезапуск ровно между ними.
        var order = new List<string?>();
        foreach (var child in row!.Children) order.Add((child as Button)?.Name);

        var atStart = order.IndexOf("StartServerButton");
        var atRestart = order.IndexOf("RestartServerButton");
        var atStop = order.IndexOf("StopServerButton");

        Assert.True(atStart >= 0 && atRestart >= 0 && atStop >= 0, "кнопка сервера пропала из ряда: " + string.Join(", ", order));
        Assert.True(atStart < atRestart, $"«Перезапустить» не после «Запустить»: {string.Join(", ", order)}");
        Assert.True(atRestart < atStop, $"«Перезапустить» не перед «Остановить»: {string.Join(", ", order)}");

        var startX = start.TranslatePoint(new Point(0, 0), window);
        var restartX = restart.TranslatePoint(new Point(0, 0), window);
        var stopX = stop.TranslatePoint(new Point(0, 0), window);

        Assert.NotNull(startX);
        Assert.NotNull(restartX);
        Assert.NotNull(stopX);

        var where =
            $"«Запустить» X={startX!.Value.X:0.#}, «Перезапустить» X={restartX!.Value.X:0.#}, " +
            $"«Остановить» X={stopX!.Value.X:0.#}";

        Assert.True(startX.Value.X < restartX.Value.X, $"«Перезапустить» не правее «Запустить» — {where}");
        Assert.True(restartX.Value.X < stopX.Value.X, $"«Перезапустить» не левее «Остановить» — {where}");

        // И все три — в ОДНОЙ строке ряда: сравнение верхних краёв ничего бы не значило
        // (у кнопок разной ширины разная высота), поэтому сравниваются полосы по вертикали.
        var startTop = startX.Value.Y;
        var startBottom = startTop + start.Bounds.Height;
        var restartTop = restartX.Value.Y;
        var restartBottom = restartTop + restart.Bounds.Height;

        Assert.True(
            restartTop < startBottom && startTop < restartBottom,
            $"«Перезапустить» не в одной строке с «Запустить» — {where}");

        window.Close();
    }

    // ---------------------------------------------------------------- 2. доступность

    /// <summary>
    /// ПЕРЕЗАПУСК ДОСТУПЕН РОВНО ТОГДА, КОГДА ДОСТУПНА «ОСТАНОВИТЬ» — и проверяется это
    /// ПЕРЕБОРОМ состояний, а не одним примером: перезапуск = «остановить» + «запустить»,
    /// и право у него то же самое.
    ///
    /// ⚠️ Перебор обязан пройти через ОБА ответа. Иначе «доступности равны» доказывалось бы
    /// на одних «нет» — то есть проверка была бы зелёной и на кнопке, которая не работает никогда.
    /// </summary>
    [AvaloniaFact]
    public void Перезапуск_доступен_ровно_тогда_когда_доступна_остановка()
    {
        var cases = new (string Where, ServerState State, ServerOwner Owner, bool Consent)[]
        {
            ("свой сервер работает", Running, ServerOwner.Panel, false),
            ("встроенный, согласия нет", Running, ServerOwner.Adopted, false),
            ("встроенный, согласие запомнено", Running, ServerOwner.Adopted, true),
            ("сервер не работает", ServerState.Stopped(0), ServerOwner.None, false),
            ("порт занят чужой программой", ForeignPort, ServerOwner.None, false),
            ("сервер отвечает, но панель им не управляет", Running, ServerOwner.None, false),
        };

        Assert.Equal(6, cases.Length);

        var seen = new List<bool>();

        foreach (var (where, state, owner, consent) in cases)
        {
            var window = Stand(state, owner, consent);

            var restart = Require(window, "RestartServerButton");
            var stop = Require(window, "StopServerButton");

            // Модель: два предиката об одном и том же обязаны отвечать одинаково.
            Assert.True(
                window.Model.CanRestart == window.Model.CanStop,
                $"{where}: CanRestart={window.Model.CanRestart}, CanStop={window.Model.CanStop}");

            // И окно: то, что человек видит, совпадает с предикатом.
            Assert.True(
                restart.IsEnabled == stop.IsEnabled,
                $"{where}: «Перезапустить» доступна={restart.IsEnabled}, «Остановить» доступна={stop.IsEnabled}");
            Assert.True(
                restart.IsEnabled == window.Model.CanStop,
                $"{where}: IsEnabled={restart.IsEnabled}, CanStop={window.Model.CanStop}");

            seen.Add(restart.IsEnabled);

            window.Close();
        }

        Assert.Contains(true, seen);
        Assert.Contains(false, seen);
    }

    // ---------------------------------------------------------------- 3. подпись

    /// <summary>
    /// ПОДПИСЬ: у НАЙДЕННОГО сервера — с многоточием (кнопка сначала спросит), у своего — без.
    /// И многоточие стоит ровно по тому же признаку, что у «Остановить»: два предиката об одном
    /// и том же однажды разошлись бы.
    /// </summary>
    [AvaloniaFact]
    public void Подпись_перезапуска_спрашивает_только_у_найденного()
    {
        var own = Stand(Running, ServerOwner.Panel);
        Assert.Equal(PanelStrings.RestartButton, PanelTestStand.Label(Require(own, "RestartServerButton")));
        Assert.Equal(PanelStrings.StopButton, PanelTestStand.Label(Require(own, "StopServerButton")));
        own.Close();

        var adopted = Stand(Running, ServerOwner.Adopted);
        Assert.Equal(PanelStrings.RestartButtonAsks, PanelTestStand.Label(Require(adopted, "RestartServerButton")));
        Assert.Equal(PanelStrings.StopButtonAsks, PanelTestStand.Label(Require(adopted, "StopServerButton")));
        adopted.Close();

        // Согласие запомнено — человек уже ответил, спрашивать нечего: многоточия нет.
        var remembered = Stand(Running, ServerOwner.Adopted, consent: true);
        Assert.Equal(PanelStrings.RestartButton, PanelTestStand.Label(Require(remembered, "RestartServerButton")));
        Assert.Equal(PanelStrings.StopButton, PanelTestStand.Label(Require(remembered, "StopServerButton")));
        remembered.Close();

        // Само многоточие — не украшение, а обещание вопроса: строки обязаны отличаться.
        Assert.NotEqual(PanelStrings.RestartButton, PanelStrings.RestartButtonAsks);
        Assert.EndsWith("…", PanelStrings.RestartButtonAsks);
        Assert.EndsWith("…", PanelStrings.StopButtonAsks);
    }

    // ---------------------------------------------------------------- 4. подсказка

    /// <summary>
    /// ПОДСКАЗКА НЕДОСТУПНОЙ КНОПКИ НАЗЫВАЕТ ПРИЧИНУ, и причины у разных положений дел РАЗНЫЕ:
    /// «сервер не работает», «порт занят чужой программой» и «панель этим сервером не управляет»
    /// требуют от человека разного. Плюс занятость панели — своя причина, одна на все кнопки.
    ///
    /// Подсказка берётся у НАСТОЯЩЕЙ кнопки показанного окна: свойство можно выставить и не
    /// увидеть ничего.
    /// </summary>
    [AvaloniaFact]
    public void Подсказка_перезапуска_называет_причину()
    {
        string RestartTip(ServerState state, ServerOwner owner, bool consent = false)
        {
            var window = Stand(state, owner, consent);
            var tip = ToolTip.GetTip(Require(window, "RestartServerButton")) as string ?? string.Empty;
            window.Close();

            return tip;
        }

        var nothing = RestartTip(ServerState.Stopped(0), ServerOwner.None);
        var foreign = RestartTip(ForeignPort, ServerOwner.None);
        var notMine = RestartTip(Running, ServerOwner.None);

        Assert.Equal(PanelStrings.TipRestartServerNothing, nothing);
        Assert.Equal(PanelStrings.TipRestartServerForeign, foreign);
        Assert.Equal(PanelStrings.TipRestartServerNotMine, notMine);

        // Три РАЗНЫХ ответа на три разных положения дел — иначе «называет причину» было бы
        // одной и той же фразой на всё.
        Assert.NotEqual(nothing, foreign);
        Assert.NotEqual(foreign, notMine);
        Assert.NotEqual(nothing, notMine);

        // Доступная кнопка говорит, что произойдёт: у своего — сразу, у найденного — со вопросом.
        Assert.Equal(PanelStrings.TipRestartServerButton, RestartTip(Running, ServerOwner.Panel));
        Assert.Equal(PanelStrings.TipRestartServerAsks, RestartTip(Running, ServerOwner.Adopted));

        // ПАНЕЛЬ ЗАНЯТА: причина одна на все кнопки сервера, и она про занятость, а не про сервер.
        var busy = Stand(ServerState.Stopped(0));
        busy.BeginServerStart(PanelStrings.ServerStarting);
        PanelTestStand.Settle();

        foreach (var name in new[] { "StartServerButton", "RestartServerButton", "StopServerButton" })
        {
            var button = Require(busy, name);

            Assert.False(button.IsEnabled, $"«{name}» доступна, хотя панель занята");
            Assert.Equal(PanelStrings.TipServerBusy, ToolTip.GetTip(button) as string);
        }

        busy.EndServerStart();
        busy.Close();
    }

    // ---------------------------------------------------------------- 5. окно зовёт перезапуск

    /// <summary>
    /// ОКНО ЗОВЁТ ПЕРЕЗАПУСК С ПОДТВЕРЖДЕНИЕМ, А ОТКАЗ ЗНАЧИТ «НИЧЕГО НЕ ПРОИЗОШЛО».
    ///
    /// Диалог в проверке без экрана поднять нечем (<c>ConfirmStopWindow</c> всплыл бы сам и ответил
    /// не то), поэтому ответ человека подставляет шов <see cref="MainWindow.RestartConfirmedForTests"/> —
    /// тот же приём, что у окна настроек с несохранённой правкой.
    ///
    /// Проверяются ТРИ положения дел, и вместе они и доказывают правило:
    ///
    /// * найденный сервер, человек согласился — вызов ОДИН, подтверждение <c>true</c>, время
    ///   ожидания то же, что у «Запустить», и человек видит «Перезапускаю…»;
    /// * найденный сервер, человек ОТКАЗАЛСЯ — вызова НЕТ вовсе, сервер не тронут;
    /// * свой сервер — спрашивать нечего: шов НЕ зовётся, а вызов всё равно уходит с <c>true</c>.
    /// </summary>
    [AvaloniaFact]
    public void Окно_зовёт_перезапуск_с_подтверждением_и_отказ_ничего_не_делает()
    {
        // --- найденный сервер, человек согласился ------------------------------------------
        var server = new RecordingServer(Running, ServerOwner.Adopted);
        var window = PanelTestStand.MainWith(server);

        var asked = 0;
        window.RestartConfirmedForTests = () => { asked++; return true; };

        window.Show();
        PanelTestStand.Settle();

        Require(window, "RestartServerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Строку состояния окно ставит СРАЗУ и синхронно: работа уходит в фон, а человеку уже
        // сказано, чего панель ждёт (многоточие в подписи и здесь же — словами).
        Assert.Equal(
            PanelStrings.ServerRestarting,
            window.FindControl<TextBlock>("ServerStatusText")!.Text);

        WaitFor(() => server.Restarts.Count > 0);

        Assert.Equal(1, asked);
        var call = Assert.Single(server.Restarts);
        Assert.True(call.Confirmed, "найденный сервер перезапускают без подтверждения человека");
        Assert.Equal(MainWindow.StartTimeout, call.Timeout);

        window.Close();

        // --- найденный сервер, человек отказался: ничего не происходит ----------------------
        var refused = new RecordingServer(Running, ServerOwner.Adopted);
        var refusedWindow = PanelTestStand.MainWith(refused);

        var refusedAsked = 0;
        refusedWindow.RestartConfirmedForTests = () => { refusedAsked++; return false; };

        refusedWindow.Show();
        PanelTestStand.Settle();

        refusedWindow.FindControl<Button>("RestartServerButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Settle();

        Assert.Equal(1, refusedAsked);
        Assert.Empty(refused.Restarts);
        Assert.Equal(
            PanelStrings.ServerRunning,
            refusedWindow.FindControl<TextBlock>("ServerStatusText")!.Text);

        refusedWindow.Close();

        // --- свой сервер: спрашивать нечего ------------------------------------------------
        var own = new RecordingServer(Running, ServerOwner.Panel);
        var ownWindow = PanelTestStand.MainWith(own);

        var ownAsked = 0;
        ownWindow.RestartConfirmedForTests = () => { ownAsked++; return false; };

        ownWindow.Show();
        PanelTestStand.Settle();

        ownWindow.FindControl<Button>("RestartServerButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitFor(() => own.Restarts.Count > 0);

        Assert.Equal(0, ownAsked);
        Assert.True(Assert.Single(own.Restarts).Confirmed);

        ownWindow.Close();
    }

    // ---------------------------------------------------------------- 6. предохранитель контроллера

    /// <summary>
    /// ЖИВАЯ ФОРМА КОМАНДНОЙ СТРОКИ сервера DSH — та же, что измерена на живом сервере владельца
    /// (`docs\ENGINE.md` §5), но на нейтральном пути: личные пути в репозиторий не попадают.
    /// </summary>
    private const string RealDshCommandLine =
        "\"C:\\Program Files\\nodejs\\node.exe\" " +
        "C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js " +
        "web --no-open --port ";

    private static string DshCommandLine(int port) => RealDshCommandLine + port;

    /// <summary>Подставной убийца: запоминает, кого просили убить. Настоящий убил бы свой процесс.</summary>
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

    private static ServerProbe ProbeFor(int port, int pid, string commandLine) =>
        new(
            ListenerPid: _ => pid,
            Listeners: () => new[] { new PortTable.Listener(port, pid) },
            CommandLine: _ => commandLine,
            ProcessName: _ => "node",
            StartTicks: _ => 63_000_000_000_000_000,
            AnswersFingerprint: _ => true);

    /// <summary>Контроллер без экрана: движка не ищет, порт не занимает, порт «освобождается» мгновенно.</summary>
    private static ServerController Controller(
        ServerProbe probe,
        IProcessKiller killer,
        Func<int>? adoptedPort = null,
        Action<int>? rememberAdoptedPort = null) =>
        new(
            @"C:\Temp\dsh-restart-home",
            () => @"C:\Temp",
            _ => { },
            mayOccupyOwnerPort: false,
            locateEngine: () => null,
            probe: probe,
            killer: killer,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            adoptedPort: adoptedPort,
            rememberAdoptedPort: rememberAdoptedPort);

    /// <summary>
    /// БЕЗ ПОДТВЕРЖДЕНИЯ КОНТРОЛЛЕР НЕ ГАСИТ ВСТРОЕННЫЙ СЕРВЕР — и перезапуск здесь не исключение,
    /// а то же правило. Проверка смотрит на УБИЙЦУ: он не должен быть позван ни разу, а сервер —
    /// остаться под управлением панели и живым.
    ///
    /// Вторая половина — про исключение, названное владельцем 26.09.2026: ЗАПОМНЕННОЕ согласие
    /// («беру под управление» сказано один раз) снимает вопрос, и тогда перезапуск гасит
    /// найденный процесс. Без этой половины проверка была бы зелёной и у перезапуска, который
    /// не делает ничего никогда.
    /// </summary>
    [Fact]
    public void Без_подтверждения_перезапуск_не_гасит_встроенный_сервер()
    {
        var probe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var killer = new RecordingKiller();

        using var controller = Controller(probe, killer);
        controller.Adopt(controller.Scan().Found[0]);

        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.False(controller.ConsentRemembered, "согласия нет, а панель считает его запомненным");

        var refused = controller.Restart(TimeSpan.Zero, confirmed: false);

        Assert.Empty(killer.Calls);
        Assert.True(refused.IsRunning, "сервер погашен без подтверждения человека");
        Assert.Equal(ServerOwner.Adopted, controller.Owner);
        Assert.Contains("подтвержд", refused.Detail, StringComparison.OrdinalIgnoreCase);

        // --- а с ЗАПОМНЕННЫМ согласием — гасит: человек уже ответил -------------------------
        var rememberedProbe = ProbeFor(3080, 19804, DshCommandLine(3080));
        var rememberedKiller = new RecordingKiller();
        var rememberedPort = 0;

        using var allowed = Controller(
            rememberedProbe,
            rememberedKiller,
            adoptedPort: () => rememberedPort,
            rememberAdoptedPort: port => rememberedPort = port);

        allowed.Adopt(allowed.Scan().Found[0]);

        Assert.True(allowed.ConsentRemembered);

        allowed.Restart(TimeSpan.Zero, confirmed: false);

        Assert.Single(rememberedKiller.Calls);
        Assert.Equal(19804, rememberedKiller.Calls[0].Pid);
    }

    // ---------------------------------------------------------------- служебное

    private static void Settle() => PanelTestStand.Settle();

    /// <summary>
    /// Дождаться условия, прокручивая очередь диспетчера: работа окна идёт в ФОНЕ, и без прокрутки
    /// её продолжение не выполнится никогда. Не дождались — ПАДАЕМ, а не «ну ладно».
    /// </summary>
    private static void WaitFor(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            PanelTestStand.Settle();

            if (condition()) return;

            Thread.Sleep(5);
        }

        Assert.Fail("окно не довело работу до конца за отведённое время");
    }
}
