using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Update;

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
/// И третье обещание того же решения — **«панель не закрывается»** (DEF-039): его безоконные
/// проверки видят только косвенно (в двери размещения нет `Hide`/`Close`), а здесь спрашивается
/// само окно панели ПОСЛЕ открытия спутника — ровно то состояние, в котором человек и находится.
///
/// **DEF-061 (2.4.2).** Здесь же проверяется четвёртое обещание, которого до 2.4.2 не было вовсе:
/// **свернул панель — спутники спрятались, развернул — вернулись те же**, и кнопка на панели задач
/// ДО и ПОСЛЕ одна. Спрашивается состояние ОС (`WindowState` настоящего окна и число кнопок),
/// потому что безоконная проверка свойств этого не видит.
///
/// **DEF-058/059 (T4, 2.4.2) — ЖИВАЯ СЦЕНА «ЗАКРЫЛ НА ХОДУ — ОТКРЫЛ».** Слово владельца
/// 25.10.2026: пока живой сценарий не пройден НАСТОЯЩИМ окном, V1 его не подписывает. Поэтому
/// в конце пробы (шаги 7–13) открывается настоящее окно настроек через ТОТ ЖЕ
/// <see cref="SingleWindowSlot"/>, запускается подготовка МЕДЛЕННЫМ ПОДСТАВНЫМ движком
/// (<see cref="ProbeUpdateInstall"/>: ни сети, ни каталогов владельца) на состоянии панели
/// (<see cref="PanelUpdateState"/>), окно закрывается слотом и открывается снова — и
/// спрашивается, что видит человек: тот же ход (числа до и после названы в отчёте), отказ
/// второго старта СЛОВАМИ и готовая сборка. Безоконная проверка того же поведения есть
/// (<c>SettingsUpdateSectionTests.Два_окна_на_одном_состоянии_видят_ход_и_готовность</c>),
/// но она не показывает НАСТОЯЩЕГО окна: сцена владельца шла в живом окне, и подписывает
/// её живое окно.
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

    // ---------------------------------------------- живая сцена подготовки обновления (T4, DEF-058/059)

    /// <summary>
    /// ОДНО СОСТОЯНИЕ ПОДГОТОВКИ НА ПРОБУ — ровно так у человека оно одно на процесс
    /// (<c>App</c> создаёт и отдаёт окну доводом). Второго объекта у пробы нет намеренно:
    /// именно этим она и спрашивает «пережило ли состояние закрытие окна».
    /// </summary>
    private readonly PanelUpdateState _updateState = new();

    /// <summary>Медленный подставной движок: ничего не качает и ничего не пишет, ход ведёт проба.</summary>
    private readonly ProbeUpdateInstall _updateInstall = new();

    /// <summary>Вымышленный выпуск: проба доказывает поведение окна, а не работу с GitHub.</summary>
    private readonly ProbeUpdateControl _updateProbe = new();

    /// <summary>Настоящее окно настроек, открытое слотом на шагах 7–13 (на закрытом — ссылка на закрытое).</summary>
    private Views.SettingsWindow? _updateWindow;

    /// <summary>Числа хода В МОМЕНТ закрытия окна: с ними сверяется ход в окне, открытом заново.</summary>
    private long _progressDoneBefore;
    private long _progressTotalBefore;

    /// <summary>Строка хода, которую человек видел ДО закрытия окна (её же он обязан увидеть после).</summary>
    private string _progressTextBefore = string.Empty;

    /// <summary>Строка карточки замены во втором окне: её же обязано назвать третье.</summary>
    private string _preparedTextShown = string.Empty;

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

        // ПОДПИСКИ — те же ЧЕТЫРЕ строки, что у связки (`PanelShell`): больше мест, где окно панели
        // исчезает, появляется, сворачивается или разворачивается, в панели нет.
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

        // DEF-061: сворачивание и разворачивание — отдельные двери, и живьём они проверяются
        // так же, как у человека: меняется `WindowState` настоящего окна.
        panel.Minimized = () =>
        {
            _hiddenCount = satellites.HideAll();
            _log.Add($"панель свернулась: спрятано спутников {_hiddenCount}");
        };

        panel.Restored = () =>
        {
            _restoredCount = satellites.Restore();
            _log.Add($"панель развернулась: возвращено спутников {_restoredCount}");
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

                // И ПАНЕЛЬ ПРИ ОТКРЫТОМ СПУТНИКЕ ОСТАЁТСЯ ВИДИМОЙ — вторая половина обещания
                // «окно поверх панели, ПАНЕЛЬ НЕ ЗАКРЫВАЕТСЯ» (DEF-039, решение владельца
                // 29.09.2026). До этой строки проверялось только, что виден СПУТНИК: панель могла
                // уйти следом, и проба этого не заметила бы. Спрашивается само окно панели —
                // то же, что видит человек, а не обёртка.
                Check("панель видима при открытом спутнике", _panelWindow!.IsVisible);

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
                // СВЕРНУТЬ ПАНЕЛЬ (кнопка «свернуть») → СПУТНИКИ ПРЯЧУТСЯ, КНОПКА ЗАДАЧ ОДНА.
                // Это DEF-061: до 2.4.2 на минимизацию не было реакции вовсе, и спутники висели
                // развёрнутыми поверх свёрнутой панели (воспроизведено владельцем 24.10.2026).
                _panelWindow!.WindowState = WindowState.Minimized;

                Check("панель свернулась", _panelWindow.WindowState == WindowState.Minimized);
                Check($"спрятано спутников при сворачивании = {_hiddenCount} (ожидается 1)", _hiddenCount == 1);
                Check("спутник скрыт вместе со свёрнутой панелью", !satellite.IsVisible);

                // Слот помнит окно: спрятали, а не закрыли — при развороте вернётся ТО ЖЕ окно.
                Check("спутник спрятан, а не закрыт (сворачивание)", _slot!.IsOpen);

                // И кнопка на панели задач по-прежнему ОДНА: свёрнутое окно её не теряет,
                // а спрятанные спутники её и не давали (`О-7`).
                var minimized = TaskbarWindows();
                Check($"у свёрнутой панели кнопок на панели задач = {minimized.Count} (ожидается 1)", minimized.Count == 1);
                Note("окна, дающие кнопку у свёрнутой панели: " + string.Join("; ", minimized));
                break;

            case 4:
                // РАЗВЕРНУТЬ ПАНЕЛЬ КНОПКОЙ НА ПАНЕЛИ ЗАДАЧ — это `WindowState`, а НЕ `Show()`:
                // именно этот путь человек и проходит, и именно его до 2.4.2 не было.
                _panelWindow!.WindowState = WindowState.Normal;

                Check($"возвращено спутников при развороте = {_restoredCount} (ожидается 1)", _restoredCount == 1);
                Check("спутник вернулся вместе с панелью", satellite.IsVisible);
                Check("вернулось то же окно (слот помнит)", _slot!.IsOpen);

                var restoredButtons = TaskbarWindows();
                Check($"после разворота кнопок на панели задач = {restoredButtons.Count} (ожидается 1)", restoredButtons.Count == 1);
                Note("окна, дающие кнопку после разворота: " + string.Join("; ", restoredButtons));
                break;

            case 5:
                // ЗАКРЫТОЕ ЧЕЛОВЕКОМ НЕ ВСПЛЫВАЕТ. Человек закрывает спутник, пока панель в трее.
                satellite.Close();
                Check("спутник закрыт человеком", !_slot!.IsOpen);

                panel.CloseAsUser();
                Check("панель снова в трее", !panel.IsVisible);

                // Спутников не осталось видимых, а прятать было нечего.
                Check($"прятать было нечего (спрятано = {_hiddenCount})", _hiddenCount == 0);
                break;

            case 6:
                // Показать панель: закрытое НЕ возвращается.
                panel.Show();
                Check($"возвращено = {_restoredCount} (ожидается 0)", _restoredCount == 0);
                Check("закрытое человеком окно не всплыло", !satellite.IsVisible);
                break;

            // ------------------------------------------ DEF-058/059: «закрыл на ходу — открыл» (T4)

            case 7:
                // ОТКРЫТЬ НАСТОЯЩЕЕ ОКНО НАСТРОЕК И НАЧАТЬ ПОДГОТОВКУ. Окно строит ТОТ ЖЕ слот,
                // каким его открывает человек (`PanelShell`), раздел — «Обновление».
                OpenUpdateWindow();

                Check("окно настроек открыто слотом (раздел «Обновление»)", _updateWindow!.IsVisible);
                Check("до нажатия подготовки хода нет", _updateState.Progress is null);

                _updateWindow.PrepareUpdate();

                Check("подготовка идёт: занятость в состоянии панели", _updateState.Busy);
                Check(
                    $"кнопка называет работу: «{PrepareButton(_updateWindow).Content}»",
                    Equals(PrepareButton(_updateWindow).Content, PanelStrings.UpdatePreparingButton));
                Check("кнопка подготовки недоступна на ходу", !PrepareButton(_updateWindow).IsEnabled);
                break;

            case 8:
                // ХОД ПОШЁЛ — И ОКНО ЗАКРЫВАЮТ СЛОТОМ, РОВНО КАК ЧЕЛОВЕК КРЕСТИКОМ.
                var running = _updateState.Progress;

                Check(
                    $"ход подготовки пошёл: {running?.Done} из {running?.Total}",
                    running is not null && running.Done > 0 && running.Total > 0);

                _progressDoneBefore = running?.Done ?? 0;
                _progressTotalBefore = running?.Total ?? 0;
                _progressTextBefore = ProgressShown(_updateWindow!);

                Check($"окно показывает ход словами: «{_progressTextBefore}»", _progressTextBefore.Length > 0);

                _slot!.Close();

                Check("окно закрыто слотом", !_slot.IsOpen && !_updateWindow!.IsVisible);
                Check("закрытие окна не сбросило занятость (DEF-058)", _updateState.Busy);
                Check(
                    $"закрытие окна не сбросило ход: {_updateState.Progress?.Done}",
                    _updateState.Progress?.Done == _progressDoneBefore);
                break;

            case 9:
                // ОТКРЫТЬ СНОВА ТЕМ ЖЕ СЛОТОМ: человека встречает ТО ЖЕ состояние, а не пустое место.
                OpenUpdateWindow();

                Check("окно открыто заново тем же слотом", _updateWindow!.IsVisible);

                var afterReopen = _updateState.Progress;

                Check(
                    $"ход тот же (до/после закрытия): {_progressDoneBefore} → {afterReopen?.Done}",
                    afterReopen is not null
                    && afterReopen.Done == _progressDoneBefore
                    && afterReopen.Total == _progressTotalBefore);

                var shownAgain = ProgressShown(_updateWindow);

                Check("во втором окне ТА ЖЕ строка хода", shownAgain.Length > 0 && shownAgain == _progressTextBefore);
                Check("во втором окне видна полоса хода", Find<ProgressBar>(_updateWindow, "UpdateProgressBar")!.IsVisible);
                Check(
                    $"во втором окне кнопка называет работу: «{PrepareButton(_updateWindow).Content}»",
                    Equals(PrepareButton(_updateWindow).Content, PanelStrings.UpdatePreparingButton));
                Check("во втором окне кнопка подготовки недоступна", !PrepareButton(_updateWindow).IsEnabled);
                break;

            case 10:
                // ВТОРОЙ СТАРТ ПОВЕРХ ИДУЩЕЙ ПОДГОТОВКИ: дверь одна — состояние панели (DEF-058),
                // значит и второе окно её не обходит. Отказ обязан быть СЛОВАМИ на экране.
                _updateWindow!.PrepareUpdate();

                Check(
                    $"второй старт отказан словами: «{_updateWindow.UpdateStatusLine}»",
                    _updateWindow.UpdateStatusLine == PanelStrings.UpdatePreparationBusy);
                Check(
                    "отказ виден САМИМ окном, а не только в состоянии",
                    StatusShown(_updateWindow) == PanelStrings.UpdatePreparationBusy);

                // И РАБОТА НЕ НАЧАЛАСЬ ЗАНОВО. Проверка БЕЗ ГОНКИ: она спрашивает не «успел ли
                // второй движок войти» (фоновая задача могла ещё не успеть), а «сброшен ли ход
                // первого» — дверь старта в разрешённом случае очистила бы его, и числа на экране
                // начались бы с нуля. Гонку поймал прогон мутации M3: счётчик входа отставал.
                Check(
                    $"второй старт не сбросил ход: {_updateState.Progress?.Done}",
                    _updateState.Progress?.Done == _progressDoneBefore);

                // РАЗРЕШИТЬ ВТОРОЙ ШАГ ХОДА: он приходится уже на ВТОРОЕ окно.
                _updateInstall.NextStep();
                break;

            case 11:
                // ХОД ВЫРОС НА ГЛАЗАХ У ВТОРОГО ОКНА: подписка на состояние живая, а не одноразовая.
                var grown = _updateState.Progress;

                Check(
                    $"ход вырос в открытом окне: {_progressDoneBefore} → {grown?.Done}",
                    grown is not null && grown.Done > _progressDoneBefore);

                // ВТОРОЙ СТАРТ ДО ДВИЖКА НЕ ДОШЁЛ: за 350 мс фоновая задача второго нажатия
                // уже вошла бы в работу, будь дверь открыта.
                Check(
                    $"движок вошёл в подготовку {_updateInstall.Prepared} раз (ожидается 1)",
                    _updateInstall.Prepared == 1);

                var grownText = ProgressShown(_updateWindow!);

                Check("второе окно показало выросший ход", grownText.Length > 0 && grownText != _progressTextBefore);

                Note($"ХОД ДО закрытия = {_progressDoneBefore} из {_progressTotalBefore} · ХОД ПОСЛЕ открытия = {grown?.Done} из {grown?.Total}");
                Note($"строка хода ДО: «{_progressTextBefore}»");
                Note($"строка хода ПОСЛЕ: «{grownText}»");

                // РАЗРЕШИТЬ ОТВЕТ ГОТОВНОСТЬЮ: работа кончается уже после закрытия первого окна.
                _updateInstall.Finish();
                break;

            case 12:
                // ГОТОВНОСТЬ, ПРИШЕДШАЯ ВО ВТОРОЕ ОКНО: сборка видна, ход убран, занятость снята.
                Check("подготовка закончилась", !_updateState.Busy);
                Check("подготовленность пережила закрытие окна (DEF-058)", _updateState.Prepared is { Ok: true });
                Check(
                    "во втором окне видна карточка замены",
                    Find<Control>(_updateWindow!, "UpdateReplaceCard")!.IsVisible);

                _preparedTextShown = PreparedShown(_updateWindow!);

                Check(
                    $"во втором окне названа подготовленная сборка: «{_preparedTextShown}»",
                    _preparedTextShown.Length > 0);
                Check("строка хода с экрана ушла", Find<ProgressBar>(_updateWindow!, "UpdateProgressBar")!.IsVisible == false);

                _slot!.Close();

                Check("окно закрыто после готовности", !_slot.IsOpen && !_updateWindow!.IsVisible);
                break;

            case 13:
                // ТРЕТЬЕ ОКНО: готовность — в состоянии панели, значит видна и в новом окне.
                OpenUpdateWindow();

                Check("третье окно показывает готовность без подготовки", _updateState.Prepared is { Ok: true });
                Check(
                    "третье окно называет ТУ ЖЕ подготовленную сборку",
                    PreparedShown(_updateWindow!).Length > 0 && PreparedShown(_updateWindow!) == _preparedTextShown);
                Check("третье окно не показывает ход", ProgressShown(_updateWindow!).Length == 0);

                // КРАСНЫЕ ЛИНИИ САМИ: ни сети, ни замены файлов живая проба не касалась.
                Check($"проверок выпусков в сети = {_updateProbe.Checks} (ожидается 0)", _updateProbe.Checks == 0);
                Check($"запусков замены файлов = {_updateInstall.Launched} (ожидается 0)", _updateInstall.Launched == 0);

                _slot!.Close();

                timer.Stop();
                Cleanup();
                break;
        }
    }

    /// <summary>
    /// ОТКРЫТЬ НАСТОЯЩЕЕ ОКНО НАСТРОЕК НА РАЗДЕЛЕ «ОБНОВЛЕНИЕ» — ТОЙ ЖЕ ДВЕРЬЮ, ЧТО У ЧЕЛОВЕКА:
    /// слот плюс место. Фабрика строит НОВОЕ окно при каждом открытии (закрытое окно показать
    /// заново нельзя), а состояние подготовки у всех окон ОДНО — оно живёт в панели.
    /// </summary>
    private void OpenUpdateWindow()
    {
        Views.SettingsWindow? built = null;

        _slot!.Open(
            () => built = BuildUpdateWindow(),
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

        if (built is not null) _updateWindow = built;
    }

    /// <summary>
    /// ОКНО НАСТРОЕК ДЛЯ ЖИВОЙ СЦЕНЫ ПОДГОТОВКИ. Настройки — ОБРАЗЦОВЫЕ (<see cref="ShotSettings"/>):
    /// ни файла владельца, ни пробы машины, ни записи. Двери обновления — подставные
    /// (<see cref="ProbeUpdateControl"/>, <see cref="ProbeUpdateInstall"/>): сети и диска у пробы нет.
    /// </summary>
    private Views.SettingsWindow BuildUpdateWindow()
    {
        var window = new Views.SettingsWindow();

        window.Attach(
            new ShotSettings(),
            autostart: null,
            update: _updateProbe,
            install: _updateInstall,
            updateState: _updateState);

        window.ShowSection(Views.SettingsWindow.UpdateSection);

        return window;
    }

    /// <summary>Орган окна по имени — тот же путь, каким его находит человек глазами.</summary>
    private static T? Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name);

    /// <summary>Строка хода, КОТОРУЮ ВИДИТ ЧЕЛОВЕК в этом окне (а не значение в состоянии).</summary>
    private static string ProgressShown(Window window) =>
        Find<TextBlock>(window, "UpdateProgressText")?.Text ?? string.Empty;

    /// <summary>Строка состояния раздела, как она стоит на экране.</summary>
    private static string StatusShown(Window window) =>
        Find<TextBlock>(window, "UpdateStatusText")?.Text ?? string.Empty;

    /// <summary>Строка карточки замены: путь подготовленной сборки — на экране.</summary>
    private static string PreparedShown(Window window) =>
        Find<TextBlock>(window, "UpdatePreparedText")?.Text ?? string.Empty;

    /// <summary>Кнопка «Скачать и подготовить» — та, которую нажимает человек.</summary>
    private static Button PrepareButton(Window window) => Find<Button>(window, "UpdatePrepareButton")!;

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
    /// СТОРОЖ: проба не имеет права длиться дольше 25 с (шагов четырнадцать по 350 мс плюс пауза
    /// подставного движка и калитки подготовки — запас всё равно огромный: на медленную машину
    /// и на ожидание показа окна). Сторож спит в СВОЁМ потоке: таймер живёт в том же цикле
    /// сообщений, который и может застрять. Код 3 — «проверка не завершилась», и он отличается
    /// от 1 («провал») и 2 («неприменимо»).
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
