using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТВЕТЫ ИЗ ФОНА ДОХОДЯТ ДО ГЛАВНОГО ОКНА (t11 цикла 2.1.2) — тот же класс, что Н-1 в окне копий.
///
/// **От какого случая.** `MainWindow` возвращает ответы из фона двумя путями — по нажатию
/// («Запустить», «Отвязать», «Остановить», «Перезапустить», «Взять под управление») и по опросу
/// состояния раз в секунду. Оба шли через `UiThread.Post`, то есть через ПАМЯТЬ ДВЕРИ, которая
/// одна на процесс и которую очищает проверка «нитки интерфейса нет вовсе»
/// (`UiThreadTests.ForgetForTests`), идущая в полном прогоне **параллельно** с этим окном.
/// Ответ не доходил — и занятость оставалась навсегда: `_busy` держал кнопки заблокированными,
/// а `_refreshing` выключал опрос целиком (следующий `Poll()` выходил на первой строке).
///
/// **Как закрыто.** Окно берёт свою нитку в конструкторе (`UiThread.Capture`) и отвечает через
/// `UiThread.PostTo` — как `BackupWindow` после D3. Дверь остаётся единственным местом, которое
/// знает про диспетчер.
///
/// Проверки детерминированные: гонки с настоящей `UiThreadTests` здесь нет — она воспроизведена
/// прямым `ForgetForTests`, ровно как в D3. Подстановка сервера ничего не поднимает и каталогов
/// владельца не касается.
/// </summary>
public class MainWindowBackgroundTests
{
    /// <summary>
    /// Подставной сервер со счётчиками: по ним видно, дошёл ли ответ (окно разблокировалось)
    /// и идёт ли опрос (сколько раз его спросили).
    /// </summary>
    private sealed class CountingServer : IServerControl
    {
        public ServerState State { get; set; } = ServerState.Stopped(0);

        public ServerOwner Owner => ServerOwner.None;

        public bool ConsentRemembered => false;

        public string EntryLink => string.Empty;

        public int Refreshes { get; private set; }

        public int Scans { get; private set; }

        public int Starts { get; private set; }

        public ServerState Refresh()
        {
            Refreshes++;
            return State;
        }

        public ServerState Start(TimeSpan timeout)
        {
            Starts++;
            return State;
        }

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public ServerState RecoverOwnServer(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan()
        {
            Scans++;
            return new DiscoveryResult(true, Array.Empty<FoundServer>());
        }

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }

    /// <summary>Дать вёрстке и очереди диспетчера отработать: без этого органы окна не наши.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Прокручивать очередь диспетчера, пока условие не выполнится. Бюджет — СЕКУНДЫ по часам
    /// (тот же урок, что в D3: счётчик проходов под нагрузкой — не время).
    /// </summary>
    private static bool PumpUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < milliseconds && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();

        return condition();
    }

    private static Button FindButton(MainWindow window, string name) => window.FindControl<Button>(name)!;

    private static TextBlock FindText(MainWindow window, string name) => window.FindControl<TextBlock>(name)!;

    /// <summary>
    /// ПУТЬ НАЖАТИЯ (`RunInBackground`): ответ фонового запуска доходит до окна и после того, как
    /// память двери забыли. Без правки кнопка «Запустить» остаётся заблокированной навсегда.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_фонового_запуска_доходит_до_окна_и_после_забывания_двери()
    {
        var server = new CountingServer();
        var window = new MainWindow();

        try
        {
            window.Attach(server);
            window.Show();
            Settle();

            var start = FindButton(window, "StartServerButton");
            var status = FindText(window, "ServerStatusText");

            Assert.True(start.IsEnabled, "«Запустить» недоступна ещё до нажатия — проверка не через то состояние");

            // Чужая проверка двери очищает память нитки интерфейса — ровно это и происходит
            // в полном прогоне, когда UiThreadTests идёт параллельно с окнами.
            UiThread.ForgetForTests();

            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Нажатие занимает окно СРАЗУ (это делает RunInBackground до всякого фона) — и это
            // не «иногда»: если занятости нет, проверять нечего.
            Assert.False(start.IsEnabled, "окно не занялось запуском сервера — нажатие не дошло");

            Assert.True(
                PumpUntil(() => start.IsEnabled),
                "ответ фонового запуска не дошёл до окна: «Запустить» осталась заблокированной, строка состояния «"
                + status.Text + "»");

            // Строка занятости снята — окно снова показывает настоящее состояние, а не «Запускаю…».
            Assert.Equal(PanelStrings.ServerStopped, status.Text);
            Assert.Equal(1, server.Starts);
        }
        finally
        {
            UiThread.Remember();
        }
    }

    /// <summary>
    /// ПУТЬ ОПРОСА (`Poll`): ответ опроса доходит до окна и после забывания двери, поэтому опрос
    /// НЕ выключается. Без правки `_refreshing` остаётся `true`, и второй опрос не начинается —
    /// главное окно перестаёт обновляться совсем.
    ///
    /// ⚠️ Публичного входа у `Poll()` нет — его зовёт таймер (`StartPolling`), а `DispatcherTimer`
    /// в безголовом прогоне НЕ тикает от `RunJobs()`: замер — 8 с прокрутки очереди, `Scans=0`.
    /// Поэтому здесь крутится НАСТОЯЩИЙ цикл диспетчера (`MainLoop` с отменой по часам): только
    /// в нём тикают таймеры. Внутренности окна проверка не трогает.
    /// </summary>
    [AvaloniaFact]
    public void Опрос_возобновляется_после_забывания_двери()
    {
        var server = new CountingServer();
        var window = new MainWindow();

        try
        {
            window.Attach(server);
            window.Show();
            Settle();

            UiThread.ForgetForTests();

            // 4 секунды настоящего цикла при таймере в 1 секунду — это несколько опросов.
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                Dispatcher.UIThread.MainLoop(stop.Token);
            }

            // С непотерянным ответом опрос идёт дальше; с потерянным — ровно один и стоп.
            Assert.True(
                server.Scans >= 2,
                "опрос не возобновился: `_refreshing` остался true после потерянного ответа (Scans="
                + server.Scans + ", Refreshes=" + server.Refreshes + ")");
        }
        finally
        {
            UiThread.Remember();
        }
    }

    /// <summary>
    /// ОКНО БЕЗ НИТКИ ИНТЕРФЕЙСА — ГРОМКИЙ ОТКАЗ, а не тихое поле (требование владельца, t11).
    ///
    /// **От какого случая.** Если `UiThread.Capture()` вернул пусто, а это молча спрятать в поле,
    /// окно построится «полудохлым»: оба ответа из фона некуда доставить, `_busy`/`_refreshing`
    /// останутся занятыми навсегда, и человек увидит мёртвые кнопки без объяснения. Поэтому окно
    /// ОТКАЗЫВАЕТСЯ строиться — с названной причиной (`MainWindow.RequireUiThread`).
    ///
    /// ⚠️ **Почему проверяется решение, а не постройка окна.** Окно на нитке без диспетчера
    /// построить нельзя вовсе: базовый конструктор `Window` требует платформу и падает ПЕРВЫМ —
    /// «Unable to locate 'Avalonia.Platform.IWindowingPlatform'» (замер t11, прогон на нитке xunit).
    /// Тогда проверка получала бы чужую причину и ничего не доказывала. `RequireUiThread` — ровно
    /// то, что зовёт конструктор, поэтому проверка бьёт по настоящему решению; а сам вызов из
    /// конструктора стережёт ещё и тип: поле объявлено НЕnullable, и присвоить туда `Capture()`
    /// без проверки нельзя — сборка даёт предупреждение, а предупреждения в проекте ворота.
    /// </summary>
    [Fact]
    public void Окно_без_нитки_интерфейса_отказывается_строиться()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => MainWindow.RequireUiThread(null));

        Assert.Contains("ГЛАВНОЕ ОКНО БЕЗ НИТКИ ИНТЕРФЕЙСА", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Стройте окно на нитке интерфейса", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// АВТОПОДЪЁМ ПРИ ЗАПУСКЕ: ЗАНЯТОСТЬ ВОЗВРАЩАЕТСЯ ОКНУ И ПОСЛЕ ЗАБЫВАНИЯ ДВЕРИ (t13).
    ///
    /// App возвращает занятость главного окна через `window.PostToOwnUi(...)` (`BeginServerStart`
    /// и `EndServerStart`, App.axaml.cs:1118/:1128). Потеря ВТОРОГО ответа — «запускаю сервер…»
    /// навсегда с заблокированными кнопками. Проверка детерминированная: окно построено (свою нитку
    /// знает) → дверь забыта → оба ответа отправлены ЕЁ членом → занятость появилась и снялась.
    /// На `UiThread.Post` (без правки) оба ответа теряются — проверка красная.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_фонового_подъёма_идёт_на_нитку_окна_и_после_забывания_двери()
    {
        var window = new MainWindow();
        var start = FindButton(window, "StartServerButton");
        var status = FindText(window, "ServerStatusText");

        try
        {
            window.Attach(new CountingServer());
            window.Show();
            Settle();

            UiThread.ForgetForTests();

            // ⚠️ Зовём член ИЗ ФОНОВОЙ НИТКИ — как App: автоподъём идёт в фоне. На нитке
            // интерфейса память двери нашлась бы сама (Dispatcher.FromThread), и подмена
            // «через дверь» осталась бы незамеченной.
            Task.Run(() => window.PostToOwnUi(() => window.BeginServerStart(PanelStrings.ServerStarting)))
                .Wait();

            Assert.True(PumpUntil(() => !start.IsEnabled), "окно не занялось подъёмом сервера");
            Assert.Equal(PanelStrings.ServerStarting, status.Text);

            // Второй — «подъём кончился». Его потеря и была бы мёртвым окном.
            Task.Run(() => window.PostToOwnUi(window.EndServerStart)).Wait();

            Assert.True(PumpUntil(() => start.IsEnabled), "ответ о конце подъёма не дошёл до окна");
        }
        finally
        {
            UiThread.Remember();
        }
    }
}