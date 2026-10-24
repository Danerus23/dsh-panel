using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DshPanel.Server;

namespace DshPanel.Shell;

/// <summary>
/// ЖИВАЯ ПРОБА ВЛАДЕНИЯ ОКНАМИ (u14, 2.4.0) — <c>--windows-selftest</c>.
///
/// **Зачем она, если есть `SatelliteWindowsTests`.** Те проверки идут БЕЗ ОКОН: они смотрят
/// свойства (`ShowInTaskbar`, `IsVisible`) и то, кто кого позвал. А два обещания варианта 2
/// (`95-windows.md` §3.2) — про СОСТОЯНИЕ ОС, а не про свойства окна:
///
/// 1. **кнопка панели задач одна** — это число кнопок, которые Windows нарисовала;
/// 2. **спутник поверх панели** — это z-порядок, а `IsActive` в безоконном прогоне ложен всегда.
///
/// Поэтому здесь НАСТОЯЩИЕ `PanelWindow`, `SingleWindowSlot`, `SatelliteSet` и настоящие окна
/// на рабочем столе — ровно те классы, что работают у человека. Ни значка в трее, ни сервера
/// проба не поднимает: значок — предмет `--tray-selftest`, сервер — `--server-selftest`.
///
/// **Правила, и каждое из красной линии.**
/// * Прогон короткий (≤10 с) и САМ УБИРАЕТ за собой: в конце закрываются все окна, и вердикт
///   отдельно считает, что видимых окон процесса не осталось НИ ОДНОГО.
/// * Данных владельца не читает: контроллер сервера строится с `mayOccupyOwnerPort: false`
///   и ни разу не запускается; `Home` берётся под корнем прогона (`RunContext`), а порт 3080
///   не занимается вовсе (сервер не поднимается).
/// * В изолированном прогоне (`--run-root`) отказывается сам: изолированный прогон владельцу
///   не показывает ничего, а проба без окон нечего и проверять (код 2, как у `--shell-selftest`).
/// </summary>
public sealed class WindowsSelfTest
{
    private readonly List<string> _log = new();
    private readonly List<string> _report = new();

    private bool _failed;
    private int _finished;

    private PanelWindow? _panel;
    private Window? _panelWindow;
    private SingleWindowSlot? _slot;
    private SatelliteSet? _satellites;
    private Window? _satelliteWindow;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    private int _hiddenCount;
    private int _restoredCount;
    private int _phase;

    /// <summary>Сколько раз панель пряталась и показывалась — для вердикта и отчёта.</summary>
    public int Phases => _phase;

    /// <summary>Куда связка пишет журнал в этом прогоне. Список в памяти, а не файл владельца.</summary>
    public void CollectLog(string line) => _log.Add(line);

    /// <summary>
    /// ПОСТРОИТЬ ОКНА И ПРОЙТИ ПРОБУ. Зовётся из <see cref="App"/> после подъёма платформы:
    /// окно нельзя создать до неё (`Unable to locate IWindowingPlatform`, поймано самотестом
    /// связки 24.09.2026).
    /// </summary>
    public void Run(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        StartWatchdog();

        var paths = Isolation.RunContext.Current.Paths;

        // Панель — НАСТОЯЩЕЕ главное окно, как у человека. Контроллер сервера нужен окну ровно
        // затем, чтобы оно построило модель; он ничего не поднимает и порт владельца не занимает.
        var server = new ServerController(
            paths.DshHome,
            () => paths.DataDir,
            line => _log.Add(line),
            mayOccupyOwnerPort: false);

        var main = new Views.MainWindow();
        main.Attach(server);

        var panel = new PanelWindow(main, isolated: false, log: CollectLog);

        // Спутник — НАСТОЯЩЕЕ окно настроек (одно из пяти спутников панели).
        var satelliteWindow = new Views.SettingsWindow();
        var slot = new SingleWindowSlot();
        var satellites = new SatelliteSet(slot);

        _panel = panel;
        _panelWindow = main;
        _slot = slot;
        _satellites = satellites;
        _satelliteWindow = satelliteWindow;

        // ПОДПИСКИ — те же ДВЕ строки, что у связки (`PanelShell`): больше мест, где окно панели
        // исчезает или появляется, в панели нет.
        panel.Hidden = () =>
        {
            _hiddenCount = satellites.HideAll();
            _log.Add($"панель ушла в трей: спрятано спутников {_hiddenCount}");
        };

        panel.Shown = () =>
        {
            _restoredCount = satellites.Restore();
            _log.Add($"панель показалась: возвращено спутников {_restoredCount}");
        };

        Check("до показа панель не видна", !panel.IsVisible);

        // 1) Панель на экране, спутник открыт ТОЙ ЖЕ дверью, что у человека: слот + место
        //    (`PanelShell.Place` — единственное место, где ставится `ShowInTaskbar = false`).
        Check("панель показалась", panel.Show());

        slot.Open(
            () => satelliteWindow,
            place: window =>
            {
                PanelShell.Place(
                    window,
                    panel: null,
                    cursor: null,
                    screens: Array.Empty<PixelRect>(),
                    scale: 1);

                // Место — чтобы окно не мигало в углу по умолчанию; проверяем не его.
                window.Position = new PixelPoint(320, 220);
            });

        var timer = UiThread.NewTimer(TimeSpan.FromMilliseconds(350));

        if (timer is null)
        {
            Fail("нет нитки интерфейса — живой пробе нечем мерить окна");
            Cleanup();
            return;
        }

        timer.Tick += (_, _) =>
        {
            try
            {
                Next(timer);
            }
            catch (Exception ex)
            {
                Fail($"ОШИБКА пробы: {ex.GetType().Name} — {ex.Message}");
                timer.Stop();
                Cleanup();
            }
        };

        timer.Start();
    }

    /// <summary>
    /// Шаги пробы. Каждый шаг — одно обещание; между шагами 350 мс, чтобы Windows успела
    /// перестроить z-порядок и панель задач (окно показывается асинхронно, и «сразу» здесь
    /// означало бы «померили до того, как показали»).
    /// </summary>
    private void Next(DispatcherTimer timer)
    {
        var panel = _panel!;
        var satellite = _satelliteWindow!;
        var satellites = _satellites!;

        switch (_phase++)
        {
            case 0:
                // ОКНО НАРИСОВАНО И СПУТНИК ВИДЕН.
                Check("спутник (настройки) показан", satellite.IsVisible);

                // ЗДЕСЬ ИЗМЕРЯЕТСЯ ПЕРВОЕ ОБЕЩАНИЕ: кнопок на панели задач ОДНА.
                var buttons = TaskbarWindows();
                Check($"кнопок на панели задач = {buttons.Count} (ожидается 1)", buttons.Count == 1);
                Note("окна, дающие кнопку: " + string.Join("; ", buttons));

                // ...и второе: спутник ПОВЕРХ панели. Это z-порядок ОС, а не `IsActive`.
                var above = IsAbove(satellite, _panelWindow!);
                Check($"спутник поверх панели (z-порядок) = {above}", above);

                // «Поверх» достигается подъёмом, а не перебиванием чужих окон (норма О-12).
                Check("ни панель, ни спутник не Topmost",
                    !satellite.Topmost && _panelWindow is { Topmost: false });
                break;

            case 1:
                // СВЕРНУТЬ ПАНЕЛЬ В ТРЕЙ → СПУТНИКИ СКРЫТЫ.
                panel.CloseAsUser();

                Check("панель спряталась по крестику", !panel.IsVisible);
                Check($"спрятано спутников = {_hiddenCount} (ожидается 1)", _hiddenCount == 1);
                Check("спутник скрыт вместе с панелью", !satellite.IsVisible);

                // Слот помнит окно: спрятали, а не закрыли.
                Check("спутник не закрыт, а спрятан", _slot!.IsOpen);
                break;

            case 2:
                // ВЕРНУТЬ ПАНЕЛЬ → ВОЗВРАЩАЮТСЯ ТОЛЬКО СПРЯТАННЫЕ.
                Check("панель показалась снова", panel.Show());

                Check($"возвращено спутников = {_restoredCount} (ожидается 1)", _restoredCount == 1);
                Check("спутник вернулся вместе с панелью", satellite.IsVisible);

                // И кнопка задач по-прежнему ОДНА: спрятанное-возвращённое окно не заводит вторую.
                var after = TaskbarWindows();
                Check($"после возврата кнопок на панели задач = {after.Count} (ожидается 1)", after.Count == 1);
                Note("окна, дающие кнопку после возврата: " + string.Join("; ", after));
                break;

            case 3:
                // ЗАКРЫТОЕ ЧЕЛОВЕКОМ НЕ ВСПЛЫВАЕТ. Человек закрывает спутник, пока панель в трее.
                satellite.Close();
                Check("спутник закрыт человеком", !_slot!.IsOpen);

                panel.CloseAsUser();
                Check("панель снова в трее", !panel.IsVisible);

                // Спутников не осталось видимых, а прятать было нечего.
                Check($"прятать было нечего (спрятано = {_hiddenCount})", _hiddenCount == 0);
                break;

            case 4:
                // Показать панель: закрытое НЕ возвращается.
                panel.Show();
                Check($"возвращено = {_restoredCount} (ожидается 0)", _restoredCount == 0);
                Check("закрытое человеком окно не всплыло", !satellite.IsVisible);

                timer.Stop();
                Cleanup();
                break;
        }
    }

    /// <summary>Убрать за собой ВСЁ: окна, слоты и цикл приложения.</summary>
    private void Cleanup()
    {
        try
        {
            _slot?.Close();
            _panel?.CloseForExit();
        }
        catch (Exception ex)
        {
            Note($"уборка: {ex.GetType().Name} — {ex.Message}");
        }

        _desktop?.Shutdown();
    }

    /// <summary>
    /// ИТОГ. Зовётся ПОСЛЕ того, как цикл приложения завершился: значит окна действительно
    /// закрыты, а не остались на рабочем столе.
    /// </summary>
    public int Verdict()
    {
        Interlocked.Exchange(ref _finished, 1);

        // НИ ОДНОГО ОКНА НЕ ОСТАЛОСЬ — считаем по ОС, а не по нашим ссылкам.
        var visible = VisibleWindows();
        var total = TopLevelWindows();

        Check($"видимых окон процесса после прогона = {visible} (ожидается 0)", visible == 0);
        Note($"всего окон процесса (включая служебные) = {total}");

        foreach (var line in _report) Console.WriteLine("WINDOWS| " + line);
        foreach (var line in _log) Console.WriteLine("ЖУРНАЛ| " + line);
        Console.WriteLine(_failed ? "WINDOWS ПРОВАЛ" : "WINDOWS УСПЕХ");
        return _failed ? 1 : 0;
    }

    private void Check(string what, bool ok) => Report($"{what} = {ok}", ok);

    /// <summary>
    /// СТОРОЖ: проба не имеет права длиться дольше 25 с (шагов пять по 350 мс, запас огромный —
    /// на медленную машину и на ожидание показа окна). Сторож спит в СВОЁМ потоке: таймер живёт
    /// в том же цикле сообщений, который и может застрять. Код 3 — «проверка не завершилась»,
    /// и он отличается от 1 («провал») и 2 («неприменимо»).
    /// </summary>
    private void StartWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(25));
            if (Volatile.Read(ref _finished) != 0) return;

            Console.WriteLine("WINDOWS| ПРОВЕРКА НЕ ЗАВЕРШИЛАСЬ за 25 с — цикл панели не погас");
            Console.WriteLine("WINDOWS ЗАВИС");
            Environment.Exit(3);
        })
        {
            IsBackground = true,
            Name = "windows-selftest-watchdog",
        };

        watchdog.Start();
    }

    private void Fail(string line) => Report(line, ok: false);

    private void Note(string line) => Console.WriteLine("WINDOWS| " + line);

    private void Report(string line, bool ok)
    {
        if (!ok) _failed = true;
        _report.Add(line);
        Console.WriteLine("WINDOWS| " + line);
    }

    // ------------------------------------------------------------------ измерение ОС (Win32)

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x0000_0080;
    private const long WsExAppWindow = 0x0004_0000;
    private const uint GwOwner = 4;
    private const uint GwHwndNext = 2;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, char[] buffer, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] buffer, int length);

    /// <summary>
    /// ТЕ ОКНА, ЧТО ДАЮТ КНОПКУ ПАНЕЛИ ЗАДАЧ, — ПОИМЁННО, чтобы «кнопок 2» было чем объяснить.
    ///
    /// **Правило панели задач — НЕ «есть ли `WS_EX_TOOLWINDOW`».** Кнопку получает видимое окно
    /// верхнего уровня, у которого **есть** `WS_EX_APPWINDOW` **или** (нет `WS_EX_TOOLWINDOW`
    /// и **нет владельца**). Второе и есть механизм Avalonia из банка (`95` `О-8`): «чтобы убрать
    /// кнопку не-owned окна, его привязывают к скрытому служебному родителю» — `ShowInTaskbar=false`
    /// ставит `GWL_HWNDPARENT`, а НЕ `TOOLWINDOW` (замер живой пробы 24.10.2026: у спутника
    /// `ex=0x200100`, владелец есть, `TOOLWINDOW` нет). Считать только по `TOOLWINDOW` значило бы
    /// объявить вторую кнопку там, где её нет. Считаются только окна нашего процесса.
    /// </summary>
    private static List<string> TaskbarWindows()
    {
        var found = new List<string>();
        var pid = (uint)Environment.ProcessId;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != pid) return true;
            if (!IsWindowVisible(window)) return true;

            var exStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();

            var appWindow = (exStyle & WsExAppWindow) != 0;
            var toolWindow = (exStyle & WsExToolWindow) != 0;
            var owned = GetWindow(window, GwOwner) != IntPtr.Zero;

            if (!appWindow && (toolWindow || owned)) return true;

            found.Add($"«{Title(window)}» class={Class(window)} ex=0x{exStyle:X} владелец={owned}");

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string Title(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetWindowText(window, buffer, buffer.Length);

        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string Class(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);

        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>Видимых окон процесса — для вердикта «ни одного не осталось».</summary>
    private static int VisibleWindows()
    {
        var count = 0;
        var pid = (uint)Environment.ProcessId;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid && IsWindowVisible(window)) count++;
            return true;
        }, IntPtr.Zero);

        return count;
    }

    /// <summary>Все окна верхнего уровня процесса, включая служебные: печатается для честности.</summary>
    private static int TopLevelWindows()
    {
        var count = 0;
        var pid = (uint)Environment.ProcessId;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid) count++;
            return true;
        }, IntPtr.Zero);

        return count;
    }

    /// <summary>
    /// СТОИТ ЛИ <paramref name="above"/> ВЫШЕ <paramref name="below"/> В Z-ПОРЯДКЕ. Обход идёт
    /// от вершины (`GetTopWindow`) вниз (`GW_HWNDNEXT`), как их перечисляет Windows.
    /// Если одного из окон в цепочке нет — ответ «нет»: окно не показано.
    /// </summary>
    private static bool IsAbove(Window above, Window below)
    {
        var top = above.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var bottom = below.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        if (top == IntPtr.Zero || bottom == IntPtr.Zero) return false;

        var sawTop = false;

        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, GwHwndNext))
        {
            if (window == top) sawTop = true;
            else if (window == bottom) return sawTop;
        }

        return false;
    }
}
