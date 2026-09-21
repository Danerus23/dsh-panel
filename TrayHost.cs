using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Владелец значка в трее, окна панели и всех служб. Один экземпляр на процесс.
/// </summary>
public sealed class TrayHost : ApplicationContext
{
    private const int TooltipLimit = 127;

    private readonly AppPaths _paths;
    private ServerController _server;
    private readonly PeakService _peak;
    private readonly BalanceService _balance;
    private readonly PricingUpdateService _pricing;
    private BackupService _backups;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _startupTimer;
    private readonly EventWaitHandle _showEvent;
    private readonly bool _startHidden;

    private readonly ToolStripMenuItem _hdrServer = new() { Enabled = false };
    private readonly ToolStripMenuItem _hdrTariff = new() { Enabled = false };
    private readonly ToolStripMenuItem _hdrBalance = new() { Enabled = false };
    private readonly ToolStripMenuItem _hdrVersion = new() { Enabled = false };
    private readonly ToolStripMenuItem _mnuPanel = new(Loc.T("tray.openPanel"));
    private readonly ToolStripMenuItem _mnuBrowser = new(Loc.T("main.open"));
    private readonly ToolStripMenuItem _mnuStart = new(Loc.T("tray.startServer"));
    private readonly ToolStripMenuItem _mnuRestart = new(Loc.T("tray.restartServer"));
    private readonly ToolStripMenuItem _mnuStop = new(Loc.T("tray.stopServer"));
    private readonly ToolStripMenuItem _mnuLog = new(Loc.T("main.log"));
    private readonly ToolStripMenuItem _mnuFolder = new(Loc.T("main.dshFolder"));
    private readonly ToolStripMenuItem _mnuSettings = new(Loc.T("tray.settings"));
    private readonly ToolStripMenuItem _mnuBackups = new(Loc.T("tray.backups"));
    private readonly ToolStripMenuItem _mnuAutostart = new(Loc.T("tray.autostart")) { CheckOnClick = false };
    private readonly ToolStripMenuItem _mnuAutostartTake = new(Loc.T("tray.autostartTake"));
    private Autostart.State _autostartState = Autostart.State.Off;
    private readonly ToolStripMenuItem _mnuChangelog = new(Loc.T("tray.changelog"));
    private readonly ToolStripMenuItem _mnuExit = new(Loc.T("tray.exit"));

    private bool _busy;
    private bool _exiting;
    private bool _balloonShown;
    private bool _balanceRunning;
    private bool _balanceWarned;
    private bool _backupRunning;
    private bool _onboarding;
    private bool _onboardingShown;
    private bool _onboardRequested;
    private PricingCheckResult _pendingPricing;
    private bool _balloonShowsPrices;
    private DateTime _nextPeakCheck = DateTime.MaxValue;
    private DateTime _nextBackupCheck = DateTime.MaxValue;
    private string _iconKey = "";
    private Icon _icon;
    private ServerStatus _status = new();
    private PeakState _peakState = new();
    private BalanceResult _balanceResult;
    private DateTime _nextBalanceCheck = DateTime.Now.AddDays(1);

    public TrayHost(bool startHidden, EventWaitHandle showEvent, int? port = null, bool autoStart = true,
        bool onboard = false)
    {
        _startHidden = startHidden;
        _onboardRequested = onboard;
        _paths = new AppPaths(AppContext.BaseDirectory);
        Settings = AppSettings.Load(_paths.SettingsPath, _paths.LegacySettingsPath);

        // Порт: ключ командной строки важнее настройки, настройка — значения по умолчанию.
        _server = new ServerController(_paths, port ?? Settings.ServerPort)
        {
            WorkingDirectory = Settings.ServerWorkingDir,
        };
        _peak = new PeakService(_paths.PricingPath);
        _balance = new BalanceService(_paths);
        _pricing = new PricingUpdateService(_paths);
        _backups = new BackupService(_paths, Settings);
        _showEvent = showEvent;

        _form = new MainForm(this);

        BuildMenu();
        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = AppVersion.Title,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowPanel();
        };
        _tray.DoubleClick += (_, _) => ShowPanel();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonShowsPrices) OpenSettings();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => Tick();

        // Одноразовый таймер: к его срабатыванию цикл сообщений уже крутится,
        // поэтому асинхронные обновления возвращаются на поток интерфейса.
        _startupTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer.Stop();

            // Автозапуск хранит абсолютный путь к exe, поэтому его надо сверить с собой при
            // каждом старте: иначе после входа в Windows поднимается чужая (обычно старая)
            // копия, а панель показывает автозапуск включённым. Разбор — в Autostart.cs.
            CheckAutostart();

            // Итог прошлого обновления: панель могла закрыться «на замену файлов», а замены не
            // случиться (панель не вышла, мьютекс занят, откат). Раньше об этом не говорил никто:
            // журнал сценария лежал непрочитанным, а человек считал, что обновился.
            CheckUpdateOutcome();

            // Версия в «Программах и компонентах»: панель обновляет себя сама, установщик писал
            // версию один раз — без этой сверки список программ врёт, а winget может предложить
            // «обновление» на выпуск старее работающего. Разбор — в PanelRegistration.cs.
            if (PanelRegistration.RefreshVersion())
            {
                AppLog.Write(_paths, "версия в списке программ обновлена: " + AppVersion.Short);
            }

            // Первый запуск: сначала мастер настройки, потом всё остальное — иначе панель
            // начнёт поднимать сервер в чужой папке и на чужом порту. Решение принимается
            // в MaybeShowOnboarding (и только там): раньше мастер показывал ещё и Program,
            // и на первом запуске открывались два окна мастера.
            MaybeShowOnboarding();

            RefreshState();
            StartInitialServer();
            // Первую проверку счёта делаем всегда, дальше — по настройке.
            _ = RefreshBalanceAsync(force: false, reason: "при запуске");

            // Окна пика проверяем вскоре после старта (даём серверу подняться).
            _nextPeakCheck = DateTime.Now.AddSeconds(45);

            // Просроченную копию делаем не сразу, а через полминуты после старта.
            _nextBackupCheck = NextBackupAt();

            // Спрашиваем GitHub о новой версии при КАЖДОМ запуске панели: ничего не скачиваем сами,
            // только узнаём номер и показываем шарик. Раньше проверка шла не чаще раза в сутки, из-за
            // чего выпуск, вышедший утром, можно было не заметить до следующего дня. От повторных
            // шариков защищает UpdateService.ShouldAnnounce: одно сообщение на версию.
            _ = CheckUpdatesQuietAsync();
        };

        // Главное окно нас не держит: приложение живёт, пока жив значок в трее.
        _form.ShowInTaskbar = !startHidden;

        // Окно может ни разу не показаться (автозапуск в трей), а Handle нужен
        // потоку, который показывает панель по повторному запуску ярлыка.
        _ = _form.Handle;

        RefreshState();

        if (!autoStart) return;

        // Пока мастер первой настройки не пройден, окно не показываем: через мгновение
        // откроется мастер, и панель не должна стоять за ним. Решение и изоляция — внутри.
        if (!startHidden) ShowPanelOnStart();

        _timer.Start();
        _startupTimer.Start();
        StartShowEventThread();
    }

    public AppSettings Settings { get; }

    public bool Exiting => _exiting;

    public bool ServerRunning => _status.Running;

    public bool HasAuthenticatedUrl => _server.GetAuthenticatedUrl(skipRunningCheck: true).Length > 0;

    // --- сообщения человеку ------------------------------------------------

    /// <summary>
    /// Виды сообщений, которые панель показывает человеку САМА, без его действия. Перечисление
    /// нужно самопроверке: она обходит ВСЕ виды и убеждается, что в изолированном прогоне молчит
    /// каждый, — а не только тот, о котором вспомнили (шарик автозапуска владелец уже получил).
    /// Появляется новый вид автоматического сообщения — он обязан появиться и здесь, иначе
    /// останется вне общего предохранителя и самопроверка этого не заметит.
    /// </summary>
    internal enum NoticeKind
    {
        /// <summary>На GitHub вышла версия новее этой.</summary>
        UpdateAvailable,

        /// <summary>Прошлое обновление не применилось: панель не вышла, мьютекс занят, откат.</summary>
        UpdateOutcome,

        /// <summary>Окна пика на странице цен разошлись с нашими.</summary>
        PeakWindows,

        /// <summary>Баланс опустился ниже порога из настроек.</summary>
        BalanceLow,

        /// <summary>Резервная копия не удалась.</summary>
        BackupFailed,

        /// <summary>Ошибка, которую человеку надо показать, а окна панели под рукой нет.</summary>
        Error,

        /// <summary>Запись автозапуска не удалось починить, и сам человек этого не увидит.</summary>
        AutostartBroken,
    }

    /// <summary>
    /// Единственное правило: панель говорит человеку что-либо по своей инициативе, только когда
    /// это его собственный прогон. Изолированный прогон (<paramref name="isolatedRun"/> — это
    /// <see cref="Autostart.IsIsolatedRun"/>: подменены каталоги панели и/или ветка автозапуска)
    /// — это наши же проверки на машине владельца: они запускают собранную копию с тем же номером
    /// версии, что стоит у него, и однажды он уже получил от такой проверки шарик про собственный
    /// автозапуск. Такой прогон не показывает ему НИЧЕГО — ни окон, ни шариков.
    ///
    /// Признак <paramref name="kind"/> в правиле не участвует намеренно: исключений нет ни для
    /// одного вида автоматического сообщения, а нужен он, чтобы самопроверка обошла все виды,
    /// а не один. Мастер первой настройки видом сообщения не является — это запрошенное окно,
    /// и изоляция его не касается (<c>tools\check-onboarding.ps1</c> считает именно его окна).
    /// </summary>
    internal static bool ShouldNotify(NoticeKind kind, bool isolatedRun) => !isolatedRun;

    /// <summary>
    /// Показывать ли мастер первой настройки. Два законных повода, и только они:
    /// <paramref name="requested"/> — человек (или проверка) попросил мастер ключом <c>--onboard</c>,
    /// это <c>tools\check-onboarding.ps1</c> считает именно его окна; либо профиль настоящий
    /// (<paramref name="isolated"/> = <see cref="Autostart.IsIsolatedRun"/> ложно) и мастер в нём
    /// ещё не пройден. В изолированном прогоне мастер сам не открывается: наши проверки поднимают
    /// панель без ключей на машине владельца (так делает <c>tools\check-update-apply.ps1</c>),
    /// и модальное окно мастера оказывалось на его рабочем столе. Правило чистым предикатом —
    /// чтобы самопроверка перебрала ВСЕ восемь сочетаний, а не поверила коду на слово.
    /// </summary>
    internal static bool ShouldAutoShowOnboarding(bool onboarded, bool requested, bool isolated) =>
        requested || (!onboarded && !isolated);

    /// <summary>
    /// Открывать ли браузер самому, когда панель поднимает сервер при старте. В изолированном
    /// прогоне — нет: ни один наш прогон не имеет права открыть окно браузера на рабочем столе
    /// владельца. Сервер при этом поднимается как обычно: проверка приёмки считает именно
    /// поднятый сервер и ссылку входа, а не окно браузера.
    /// </summary>
    internal static bool ShouldAutoOpenBrowser(bool isolated) => !isolated;

    /// <summary>
    /// Показывать ли окно панели при старте. Окно открывается только у настоящего прогона с
    /// пройденным мастером: в изолированном прогоне панель живёт значком в трее и не кладёт
    /// окно поверх работы владельца.
    /// </summary>
    internal static bool ShouldShowWindowOnStart(bool onboarded, bool isolated) => onboarded && !isolated;

    /// <summary>
    /// Единственная дверь автоматических сообщений: всё, что панель говорит человеку по своей
    /// инициативе, идёт через неё, и правило изоляции живёт здесь, а не в семи местах. В
    /// изолированном прогоне сообщение не показывается, но остаётся в журнале — иначе проверка
    /// не сможет объяснить, почему панель молчала.
    ///
    /// Шарики в ответ на действие человека (окно свёрнуто в трей, окна пика только что применены)
    /// через эту дверь не идут: они показываются в ответ на то, что человек сам сделал в этом же
    /// прогоне, и без него не появляются.
    /// </summary>
    private void NotifyBalloon(NoticeKind kind, string title, string text, ToolTipIcon icon, int milliseconds)
    {
        if (!ShouldNotify(kind, Autostart.IsIsolatedRun))
        {
            AppLog.Write(_paths, $"сообщение «{title}» не показано (изолированный прогон): {text}");
            return;
        }

        try
        {
            // Клик по шарику открывает «Настройки» только у сообщения об окнах пика.
            _balloonShowsPrices = kind == NoticeKind.PeakWindows;
            _tray.ShowBalloonTip(milliseconds, title, text, icon);
        }
        catch
        {
            // Всплывающая подсказка — не повод падать.
        }
    }

    // --- меню трея ---------------------------------------------------------

    private void BuildMenu()
    {
        _menu.ShowImageMargin = false;
        // Место под галочку нужно оставить: иначе отметка у пункта автозапуска
        // просто не рисуется (она рисуется в области значков).
        _menu.ShowCheckMargin = true;
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _hdrVersion,
            new ToolStripSeparator(),
            _hdrServer,
            _hdrTariff,
            _hdrBalance,
            new ToolStripSeparator(),
            _mnuPanel,
            _mnuBrowser,
            new ToolStripSeparator(),
            _mnuStart,
            _mnuRestart,
            _mnuStop,
            new ToolStripSeparator(),
            _mnuLog,
            _mnuFolder,
            _mnuSettings,
            _mnuBackups,
            new ToolStripSeparator(),
            _mnuAutostart,
            _mnuAutostartTake,
            new ToolStripSeparator(),
            _mnuChangelog,
            _mnuExit,
        });

        _mnuPanel.Font = new Font(_menu.Font, FontStyle.Bold);

        _mnuPanel.Click += (_, _) => ShowPanel();
        _mnuBrowser.Click += async (_, _) => await RequestOpenBrowserAsync();
        _mnuStart.Click += (_, _) => RequestStart();
        _mnuRestart.Click += async (_, _) => await RequestRestartAsync();
        _mnuStop.Click += async (_, _) => await RequestStopAsync();
        _mnuLog.Click += (_, _) => OpenLog();
        _mnuFolder.Click += (_, _) => OpenDshFolder();
        _mnuSettings.Click += (_, _) => OpenSettings();
        _mnuBackups.Click += (_, _) => OpenBackups();
        _mnuAutostart.Click += (_, _) => SetAutostart(!Autostart.IsEnabled());
        _mnuAutostartTake.Click += (_, _) =>
        {
            if (!Autostart.Retarget())
            {
                ShowError(Loc.T("err.autostart"));
                return;
            }

            _autostartState = Autostart.Inspect();
            RefreshState();
        };
        _mnuChangelog.Click += (_, _) => AppVersion.OpenChangelog(_paths.BaseDir);
        _mnuExit.Click += (_, _) => ExitApp();
    }

    // --- окно --------------------------------------------------------------

    public void ShowPanel()
    {
        try
        {
            if (!_form.Visible)
            {
                _form.ShowInTaskbar = true;
                _form.Show();
            }
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }
        catch
        {
            // Окно уже могло быть закрыто при выходе.
        }
    }

    public void HideToTray()
    {
        _form.Hide();
        _form.ShowInTaskbar = false;

        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _balloonShowsPrices = false;
            _tray.BalloonTipTitle = Loc.T("app.title");
            _tray.BalloonTipText = Loc.T("tray.hidden");
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }
        catch
        {
            // Подсказка — необязательная любезность.
        }
    }

    private void StartShowEventThread()
    {
        if (_showEvent == null) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                if (!_showEvent.WaitOne()) continue;
                try
                {
                    _form.BeginInvoke(new Action(ShowPanel));
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "dsh-tray-show-panel",
        };
        thread.Start();
    }

    // --- состояние ---------------------------------------------------------

    private void Tick()
    {
        if (_exiting) return;
        RefreshState();
    }

    private void RefreshState()
    {
        try
        {
            _status = _server.GetStatus();
            _peakState = _peak.GetState();
        }
        catch
        {
            // Разовые сбои опроса не должны ломать панель.
        }

        _form.ApplyStatus(_status, _peakState, _balanceResult, _busy, Autostart.IsEnabled());
        UpdateTray();
        UpdateMenu();

        if (Settings.BalanceAutoRefresh && !_busy && !_balanceRunning && DateTime.Now >= _nextBalanceCheck)
        {
            _nextBalanceCheck = DateTime.Now.AddMinutes(Math.Max(1, Settings.BalanceRefreshMinutes));
            _ = RefreshBalanceAsync(force: false);
        }

        if (Settings.PeakAutoCheck && !_busy && DateTime.Now >= _nextPeakCheck)
        {
            _nextPeakCheck = DateTime.Now.AddHours(Math.Max(1, Settings.PeakCheckHours));
            _ = CheckPeakWindowsAsync(quiet: true);
        }

        if (Settings.BackupEnabled && !_busy && !_backupRunning && DateTime.Now >= _nextBackupCheck)
        {
            _nextBackupCheck = NextBackupAt();
            _ = RunBackupAsync("по расписанию");
        }
    }

    /// <summary>
    /// Когда делать следующую копию. Срок считается от прошлой копии, поэтому
    /// расписание переживает перезапуск приложения; просроченная делается вскоре
    /// после запуска, а не мгновенно — чтобы не мешать старту.
    /// </summary>
    private DateTime NextBackupAt()
    {
        if (!Settings.BackupEnabled) return DateTime.MaxValue;

        var interval = TimeSpan.FromHours(Math.Max(1, Settings.BackupIntervalHours));
        var last = Settings.BackupLastAt;
        if (last == null || last.Value.Add(interval) <= DateTime.Now) return DateTime.Now.AddSeconds(30);

        return last.Value.Add(interval);
    }

    /// <summary>Копия «себя»: наши проекты, данные DSH и настройки панели.</summary>
    public async Task<BackupResult> RunBackupAsync(string reason)
    {
        if (_backupRunning) return null;
        _backupRunning = true;

        BackupResult result;
        try
        {
            result = await Task.Run(() => _backups.Create());
        }
        catch (Exception error)
        {
            result = new BackupResult { Ok = false, Error = error.Message };
        }
        finally
        {
            _backupRunning = false;
        }

        AppLog.Write(_paths, $"резервная копия ({reason}): " + result.Summary());

        // Срок следующей копии считаем после записи отметки о сделанной: иначе
        // просроченная копия, у которой отметка осталась старой, повторилась бы
        // ещё раз через полминуты. При сбое пробуем снова через час, а не чаще.
        _nextBackupCheck = result.Ok ? NextBackupAt() : DateTime.Now.AddHours(1);

        if (!result.Ok)
        {
            NotifyBalloon(NoticeKind.BackupFailed,
                Loc.T("app.title") + " — " + Loc.T("tray.backupFailedTitle"),
                Loc.T("tray.backupFailedText", result.Error), ToolTipIcon.Warning, 10000);
        }

        return result;
    }

    /// <summary>Окно «Резервные копии»: расписание, состав, папка и список копий.</summary>
    public void OpenBackups()
    {
        try
        {
            using var dialog = new BackupForm(Settings, _paths, OpenRestore);
            if (_form.Visible) dialog.ShowDialog(_form);
            else dialog.ShowDialog();

            Settings.Save(_paths.SettingsPath);
            _nextBackupCheck = NextBackupAt();
            RefreshState();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>
    /// Окно наката копии. Панель сама останавливает свой сервер перед накатом: без этого
    /// половина файлов DSH занята, и копия легла бы наполовину.
    /// </summary>
    private void OpenRestore(string archive)
    {
        using var dialog = new RestoreForm(_paths, Settings, _backups, () => ServerRunning, StopServerForRestore, archive);
        if (_form.Visible) dialog.ShowDialog(_form);
        else dialog.ShowDialog();

        AfterRestore();
    }

    /// <summary>Остановка сервера перед накатом. Возвращает false, если порт всё ещё занят.</summary>
    private bool StopServerForRestore()
    {
        try
        {
            _server.Stop();
            _status = _server.GetStatus();
            _form.ApplyStatus(_status, _peakState, _balanceResult, _busy, Autostart.IsEnabled());
            UpdateTray();
            UpdateMenu();
            AppLog.Write(_paths, "сервер остановлен перед накатом копии");
            return !_status.Running;
        }
        catch (Exception error)
        {
            AppLog.Write(_paths, "не удалось остановить сервер перед накатом: " + error.Message);
            return false;
        }
    }

    /// <summary>
    /// Возвращает панель в согласие с данными на диске после наката: копия могла заменить
    /// настройки, движок и данные DSH, а на руках у панели остались прежние значения.
    /// </summary>
    private void AfterRestore()
    {
        try
        {
            Settings.Reload(_paths.SettingsPath);
            NodeLocator.ResetCache();
            NodeLocator.ApplyOverrides(Settings.NodePath, Settings.DshBinPath);
            ApplyLanguage(Settings.Language);
        }
        catch (Exception error)
        {
            AppLog.Write(_paths, "после наката настройки перечитать не удалось: " + error.Message);
        }

        // Управление сервером собираем заново: порт и рабочая папка могли приехать из копии.
        _server = new ServerController(_paths, Settings.ServerPort)
        {
            WorkingDirectory = Settings.ServerWorkingDir,
        };

        _backups = new BackupService(_paths, Settings);
        _nextBackupCheck = NextBackupAt();
        _nextPeakCheck = DateTime.Now.AddSeconds(45);
        RefreshState();
    }

    /// <summary>
    /// Проверка окон пика на официальной странице. Тихо — значит только записать
    /// результат и, если окна разошлись, предложить обновление в трее; само
    /// обновление применяется лишь после подтверждения в окне «Тариф и цены».
    /// </summary>
    public async Task CheckPeakWindowsAsync(bool quiet)
    {
        PricingCheckResult result;
        try
        {
            result = await Task.Run(() => _pricing.Check());
        }
        catch (Exception error)
        {
            result = new PricingCheckResult { Ok = false, Error = error.Message };
        }

        AppLog.Write(_paths, result.Ok
            ? (result.Differs
                ? $"тариф: окна пика на странице отличаются (сейчас {result.CurrentText}; предлагают {result.FoundText})"
                : "тариф: окна пика совпадают со страницей")
            : "тариф: проверить страницу не удалось — " + result.Error
              + (result.Diag.Length > 0 ? " (" + result.Diag + ")" : ""));

        if (!result.Ok || !result.Differs)
        {
            _pendingPricing = null;
            UpdateMenu();
            return;
        }

        _pendingPricing = result;
        UpdateMenu();

        NotifyBalloon(NoticeKind.PeakWindows,
            Loc.T("app.title") + " — " + Loc.T("tray.pricingChangedTitle"),
            Loc.T("tray.pricingChangedText", result.CurrentText, result.FoundText),
            ToolTipIcon.Warning, 15000);
    }

    /// <summary>
    /// Окно настроек: счёт и тариф. Если известно о новых окнах пика, окно
    /// сразу показывает их и ждёт подтверждения.
    /// </summary>
    /// <summary>
    /// Смена языка из «Настроек»: применяется сразу — пересобираются подписи окна панели,
    /// меню трея и подсказка. Язык сохраняется, поэтому переживает перезапуск панели.
    /// </summary>
    public void ApplyLanguage(string language)
    {
        try
        {
            Settings.Language = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim().ToLowerInvariant();
            Settings.Save(_paths.SettingsPath);
            Loc.Init(Settings.Language);

            _form.Retext();
            UpdateMenu();
            _tray.Text = BuildTooltip();
            AppLog.Write(_paths, "язык интерфейса: " + Settings.Language);
        }
        catch (Exception error)
        {
            AppLog.Write(_paths, "не удалось сменить язык: " + error.Message);
        }
    }

    /// <summary>
    /// Мастер первой настройки. Возвращает true, если человек его прошёл: тогда настройки
    /// перечитаны и сервер поднимается уже с новым портом и рабочей папкой.
    /// </summary>
    /// <summary>
    /// Перезапуск панели после мастера первой настройки.
    ///
    /// Зачем: панель, которая провела мастер, сама же ставила Node и движок — и её окружение
    /// остаётся таким, каким было до установки. На приёмке из-за этого сервер не поднимался
    /// с первого раза, а помогал только «выйти из трея и запустить панель заново». Поэтому
    /// после мастера панель поднимает себя заново: новая копия работает уже с готовым движком.
    ///
    /// Перезапуск делаем через cmd с задержкой: текущий процесс должен успеть выйти, иначе
    /// новая копия увидит занятый мьютекс одной копии и молча завершится. В проверках и
    /// CLI-режимах не перезапускаемся — там это лишнее и мешало бы стенду.
    /// </summary>
    private void RestartSelf()
    {
        try
        {
            if (Environment.GetCommandLineArgs().Length > 1) return;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_PANEL_INSTANCE"))) return;

            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = "/c timeout /t 3 /nobreak >nul & start \"\" \"" + exe + "\"",
            });

            AppLog.Write(_paths, "перезапускаю панель после мастера первой настройки");
            _form.BeginInvoke(new Action(() => Application.Exit()));
        }
        catch (Exception error)
        {
            // Не вышло — панель просто продолжит работать как раньше.
            AppLog.Write(_paths, "перезапустить панель не удалось: " + error.Message);
        }
    }
    /// <summary>
    /// Решение о мастере при старте панели — отдельным методом, потому что его обязан видеть
    /// <c>--selftest</c> (структурный кейс «Гейты ок» ищет ровно этот метод и требует, чтобы он
    /// спрашивал предикат <see cref="ShouldAutoShowOnboarding"/> и признак
    /// <see cref="Autostart.IsIsolatedRun"/>). Подавленный мастер не исчезает бесследно: в журнал
    /// идёт строка, по которой проверка объясняет, почему панель молчала.
    /// </summary>
    private void MaybeShowOnboarding()
    {
        if (!ShouldAutoShowOnboarding(Settings.Onboarded, _onboardRequested, Autostart.IsIsolatedRun))
        {
            // Мастер не показываем ТОЛЬКО тогда, когда его и не просили, и профиль изолирован:
            // если профиль настоящий, его покажет ветка ниже, а если мастер просили — тоже она.
            if (!Settings.Onboarded && !_onboardRequested)
            {
                AppLog.Write(_paths, "мастер первой настройки не показан (изолированный прогон): "
                                     + "профиль не пройден, ключа --onboard нет");
            }

            return;
        }

        _onboardRequested = false;
        ShowOnboarding();

        // Мастер пройден (или закрыт) — человек ждёт панель, а не пустой рабочий стол.
        if (!_startHidden) ShowPanel();
    }

    /// <summary>
    /// Окно панели при старте. Своим решением, а не побочным эффектом конструктора: так его
    /// видит <c>--selftest</c> (кейс «Гейты ок») и так изолированный прогон не кладёт окно на
    /// рабочий стол владельца.
    /// </summary>
    private void ShowPanelOnStart()
    {
        if (!ShouldShowWindowOnStart(Settings.Onboarded, Autostart.IsIsolatedRun))
        {
            if (Settings.Onboarded)
            {
                AppLog.Write(_paths, "окно панели при старте не показано (изолированный прогон)");
            }

            return;
        }

        try
        {
            _form.Show();
            _form.Activate();
        }
        catch
        {
            // Окно могло не построиться — панель всё равно живёт в трее.
        }
    }

    public bool ShowOnboarding()
    {
        // Мастер показывается один раз за запуск: это первая настройка, и второе окно поверх
        // первого человек воспринимает как поломку (так и было на приёмке в ВМ). Плюс строб
        // от повторного вызова: мастер модальный, и пока он открыт, таймеры WinForms
        // продолжают срабатывать.
        if (_onboarding || _onboardingShown) return false;

        try
        {
            _onboarding = true;
            _onboardingShown = true;
            var wasVisible = _form.Visible;
            if (wasVisible) _form.Hide();

            using var wizard = new OnboardingForm(_paths, Settings, ApplyLanguage);
            wizard.ShowDialog(wasVisible ? _form : null);
            if (!wizard.Completed) return false;

            // Порт и рабочая папка могли измениться — собираем управление сервером заново.
            _server = new ServerController(_paths, Settings.ServerPort)
            {
                WorkingDirectory = Settings.ServerWorkingDir,
            };

            _nextBackupCheck = NextBackupAt();
            _tray.Text = BuildTooltip();
            if (wasVisible) ShowPanel();
            AppLog.Write(_paths, "мастер первой настройки пройден");
              RestartSelf();
            return true;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return false;
        }
        finally
        {
            _onboarding = false;
        }
    }

    /// <summary>
    /// Тихо спрашивает GitHub о новой версии: результат кладём в настройки (его покажет
    /// вкладка «Обновления»), а если версия новее — показываем шарик. Скачивание всегда
    /// за человеком: обновление ставится только по кнопке.
    /// </summary>
    private async Task CheckUpdatesQuietAsync()
    {
        try
        {
            var check = await Task.Run(UpdateService.Check);

            Settings.UpdateCheckedAt = check.CheckedAt;
            if (check.Ok)
            {
                Settings.UpdateLatest = check.Latest;
                Settings.UpdatePublished = check.PublishedAt == default ? "" : check.PublishedAt.ToString("yyyy-MM-dd");
                Settings.UpdatePageUrl = check.PageUrl;
                // Сырое тело храним отдельно от выбранного блока: язык интерфейса может
                // смениться до следующей проверки, и заметки должны пересобраться сразу.
                Settings.UpdateNotesRaw = UpdateService.RawNotes(check.Notes);
                Settings.UpdateNotes = UpdateService.PickNotesForPanel(Settings.UpdateNotesRaw, Loc.Language);
            }

            Settings.Save(_paths.SettingsPath);
            AppLog.Write(_paths, "обновления: " + check.Summary());

            // Шарик показываем один раз на версию: проверка идёт при каждом запуске, и без этой
            // проверки человек видел бы одно и то же сообщение на каждом старте, пока не обновится.
            // Версию помечаем объявленной и тогда, когда шарик мог не дойти: ShowBalloonTip ничего
            // не сообщает о том, показался ли он (выключенные уведомления, «не беспокоить», политика
            // «No balloon tips»), поэтому повторить сообщение при следующем запуске нельзя обещать —
            // повтор будет только со следующим выпуском. Та же новость лежит в видимом месте:
            // «Настройки → Обновления» показывают версию и заметки независимо от шарика.
            if (check.Ok && UpdateService.ShouldAnnounce(check.Latest, Settings.UpdateAnnounced, AppVersion.Short))
            {
                NotifyBalloon(NoticeKind.UpdateAvailable, AppVersion.Title,
                    Loc.T("tray.updateAvailable", check.Latest), ToolTipIcon.Info, 8000);

                Settings.UpdateAnnounced = check.Latest;
                Settings.Save(_paths.SettingsPath);
            }
        }
        catch (Exception error)
        {
            AppLog.Write(_paths, "обновления: проверить не удалось — " + error.Message);
        }
    }

    /// <summary>
    /// Обновление подготовлено: запускаем сценарий замены файлов и выходим. Сценарий ждёт,
    /// пока панель закроется (иначе файлы заняты), копирует новую версию и запускает панель.
    /// Настройки при этом не трогаются — они лежат в профиле, а не в папке программы.
    /// </summary>
    private void OnUpdateReady(UpdateDownload download)
    {
        try
        {
            if (download == null || !download.Ok || !File.Exists(download.ScriptPath))
            {
                ShowError(Loc.T("update.scriptMissing", download?.ScriptPath ?? ""));
                return;
            }

            AppLog.Write(_paths, "обновление: запускаю сценарий замены файлов — " + download.ScriptPath);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + download.CommandLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(download.ScriptPath) ?? _paths.BaseDir,
            });

            ExitApp();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    public void OpenSettings()
    {
        try
        {            var pending = _pendingPricing;
            using var dialog = new SettingsForm(Settings, _paths, _pricing, () => Loc.T("main.windows", _peak.GetState().ScheduleText, _peak.GetState().Checked), pending, ApplyLanguage, OnUpdateReady);

            var result = _form.Visible ? dialog.ShowDialog(_form) : dialog.ShowDialog();

            Settings.Save(_paths.SettingsPath);
            _nextPeakCheck = Settings.PeakAutoCheck
                ? DateTime.Now.AddHours(Math.Max(1, Settings.PeakCheckHours))
                : DateTime.MaxValue;
            _nextBalanceCheck = Settings.BalanceAutoRefresh
                ? DateTime.Now.AddMinutes(Math.Max(1, Settings.BalanceRefreshMinutes))
                : DateTime.MaxValue;

            if (dialog.Applied)
            {
                _pendingPricing = null;
                _peakState = _peak.GetState();
                RefreshState();

                try
                {
                    _tray.BalloonTipTitle = Loc.T("app.title");
                    _tray.BalloonTipText = Loc.T("tray.peaksApplied", _peakState.WindowText);
                    _tray.BalloonTipIcon = ToolTipIcon.Info;
                    _tray.ShowBalloonTip(6000);
                }
                catch
                {
                    // Ничего.
                }
            }
            else if (result is DialogResult.OK or DialogResult.Cancel)
            {
                RefreshState();
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void UpdateTray()
    {
        var visual = _status.Running
            ? ServerVisual.Running
            : _status.PortBusyByOther
                ? ServerVisual.BusyOther
                : ServerVisual.Stopped;

        var key = $"{visual}|{_peakState.InPeak}";
        if (key != _iconKey)
        {
            _iconKey = key;
            var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            var created = TrayIconFactory.Create(visual, _peakState.InPeak, size);
            _tray.Icon = created;
            var previous = _icon;
            _icon = created;
            previous?.Dispose();
        }

        _tray.Text = BuildTooltip();
    }

    private string BuildTooltip()
    {
        var text = new StringBuilder();
        text.Append(AppVersion.Title).Append(" — ").Append(_status.StateText);
        if (_status.Running && _status.Pid > 0) text.Append(" (PID ").Append(_status.Pid).Append(')');
        if (_status.PortBusyByOther) text.Append(" — ").Append(_status.ProcessName);

        text.AppendLine();
        text.Append(Loc.T("tray.headerTariff", _peakState.StateText
            + (string.IsNullOrEmpty(_peakState.NextText) ? "" : " · " + _peakState.NextText)));

        if (_balanceResult != null && _balanceResult.Ok)
        {
            text.AppendLine();
            text.Append(Loc.T("tray.headerBalance", _balanceResult.Summary
                + (IsBalanceLow(_balanceResult) ? Loc.T("tray.tooltipLow") : "")));
        }

        var rendered = text.ToString();
        return rendered.Length <= TooltipLimit ? rendered : rendered[..(TooltipLimit - 1)] + "…";
    }

    private void UpdateMenu()
    {
        _hdrVersion.Text = AppVersion.Title;
        _hdrServer.Text = Loc.T("tray.headerServer", _status.StateText
                            + (_status.Pid > 0 ? $" (PID {_status.Pid})" : ""));
        _hdrTariff.Text = Loc.T("tray.headerTariff", _peakState.StateText);
        _hdrBalance.Text = Loc.T("tray.headerBalance", _balanceResult == null
            ? Loc.T("tray.balanceNotChecked")
            : _balanceResult.Ok
                ? _balanceResult.Summary
                : Loc.T("tray.balanceNoData"));

        var autostart = Autostart.IsEnabled();
        var elsewhere = _autostartState.Where == Autostart.Where.Other;
        _mnuAutostart.Checked = autostart;

        // При записи на чужую копию «включено» — правда, но не вся: запустится не эта панель.
        // Поэтому пункт говорит, для кого автозапуск включён, а не просто «включено».
        _mnuAutostart.Text = Loc.T(elsewhere
            ? "tray.autostartOtherOn"
            : autostart ? "tray.autostartOn" : "tray.autostartOff");

        // «Перевести на эту копию» показываем только в одном случае: запись ведёт на другую
        // ЖИВУЮ копию той же или более новой версии. Всё остальное панель чинит сама при старте,
        // а отбирать автозапуск у соседней копии молча нельзя — это решение человека.
        _mnuAutostartTake.Visible = elsewhere;
        _mnuAutostartTake.Enabled = elsewhere && !_busy;

        var enabled = !_busy;
        _mnuStart.Enabled = enabled && !_status.Running && !_status.PortBusyByOther;
        _mnuStop.Enabled = enabled && _status.Running;
        _mnuRestart.Enabled = enabled && _status.Running;
        _mnuBrowser.Enabled = enabled;
        _mnuPanel.Enabled = true;
        _mnuLog.Enabled = true;
        _mnuFolder.Enabled = true;
        _mnuSettings.Enabled = !_busy;
        _mnuSettings.Text = Loc.T(_pendingPricing != null ? "tray.settingsUpdate" : "tray.settings");
        _mnuBackups.Enabled = !_busy;
        _mnuBackups.Text = Loc.T(Settings.BackupEnabled ? "tray.backupsOn" : "tray.backups");
        _mnuAutostart.Enabled = enabled;
        _mnuExit.Enabled = true;
    }

    // --- команды -----------------------------------------------------------

    public void RequestStart()
    {
        if (_busy) return;
        if (_status.Running)
        {
            ShowPanel();
            return;
        }
        _ = RunActionAsync(Loc.T("busy.startServer"), () => _server.Start(Settings.OpenBrowserOnStart));
    }

    public async Task RequestRestartAsync()
    {
        if (_busy) return;
        await RunActionAsync(Loc.T("busy.restartServer"), () => _server.Restart(Settings.OpenBrowserOnStart));
    }

    public async Task RequestStopAsync()
    {
        if (_busy) return;
        await RunActionAsync(Loc.T("busy.stopServer"), () => _server.Stop());
    }

    public async Task RequestOpenBrowserAsync()
    {
        if (_busy) return;
        if (!_status.Running)
        {
            await RunActionAsync(Loc.T("busy.startServer"), () => _server.Start(openBrowser: true));
            return;
        }

        await RunActionAsync(Loc.T("busy.openBrowser"), () =>
        {
            if (!_server.OpenWebWhenReady(timeoutSec: 10))
            {
                throw new InvalidOperationException(
                    Loc.T("tray.linkUnknown"));
            }
            return _server.GetStatus();
        });
    }

    public async Task RefreshBalanceAsync(bool force, string reason = null)
    {
        if (_balanceRunning) return;
        if (force && _busy) return;

        reason ??= force ? "по кнопке" : "по расписанию";

        _balanceRunning = true;
        try
        {
            var result = await Task.Run(() => _balance.Query());
            _balanceResult = result;
            _nextBalanceCheck = DateTime.Now.AddMinutes(Math.Max(1, Settings.BalanceRefreshMinutes));
        }
        catch (Exception error)
        {
            _balanceResult = new BalanceResult { Ok = false, Error = error.Message };
        }
        finally
        {
            _balanceRunning = false;
        }

        AppLog.Write(_paths, $"счёт ({reason}): "
                             + (_balanceResult.Ok ? _balanceResult.Summary : "не получен — " + _balanceResult.Error));

        WarnIfBalanceLow();

        _form.ApplyStatus(_status, _peakState, _balanceResult, _busy, Autostart.IsEnabled());
        UpdateTray();
        UpdateMenu();
    }

    /// <summary>Порог предупреждения строкой — тем же стилем, что и ответ API.</summary>
    public string ThresholdText => Settings.BalanceWarnThreshold.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Баланс ниже порога из настроек.</summary>
    public bool IsBalanceLow(BalanceResult balance)
    {
        if (balance == null || !balance.Ok || balance.TotalValue == null) return false;
        if (!Settings.BalanceWarnEnabled) return false;
        return balance.TotalValue.Value < Settings.BalanceWarnThreshold;
    }

    /// <summary>
    /// Предупреждает о низком балансе один раз: пока баланс не поднимется выше
    /// порога, повторных всплывающих сообщений не будет.
    /// </summary>
    private void WarnIfBalanceLow()
    {
        var balance = _balanceResult;
        if (balance == null || !balance.Ok || balance.TotalValue == null) return;
        if (!Settings.BalanceWarnEnabled) return;

        var value = balance.TotalValue.Value;
        if (value >= Settings.BalanceWarnThreshold)
        {
            _balanceWarned = false;
            return;
        }

        if (_balanceWarned) return;
        _balanceWarned = true;

        var text = Loc.T("tray.balanceLow",
            value.ToString("0.##", CultureInfo.InvariantCulture), balance.Currency, ThresholdText);
        AppLog.Write(_paths, "предупреждение о балансе: " + text);

        NotifyBalloon(NoticeKind.BalanceLow,
            AppVersion.Title + " — " + Loc.T("tray.balanceLowTitle"), text, ToolTipIcon.Warning, 10000);
    }

    private async Task RunActionAsync(string busyText, Func<ServerStatus> work)
    {
        if (_busy) return;

        _busy = true;
        _form.SetBusy(busyText);
        UpdateMenu();

        try
        {
            var status = await Task.Run(work);

            // Панель могла отказаться останавливать сервер (порт занят чужой программой) —
            // говорим об этом человеку, а не молчим.
            if (status != null && status.Refusal.Length > 0) ShowError(status.Refusal);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _busy = false;
            _form.ClearBusy();
            RefreshState();
        }
    }

    private void ShowError(string message, bool background = false)
    {
        try
        {
            // Имя продукта — из словаря: раньше здесь было зашито прежнее «DeepSeek Harness»,
            // и ошибка на английском интерфейсе подписывалась чужим именем.
            var title = Loc.T("app.title");

            // Ошибка в ответ на действие человека при открытом окне панели — окном, как и раньше.
            // Фоновая ошибка (сервер не поднялся при старте) окном не показывается никогда: она
            // приходит не в ответ на действие, и окно поверх чужой работы — это и есть «панель
            // мешает». Дальше — единственная дверь автоматических сообщений.
            if (!background && _form.Visible)
            {
                MessageBox.Show(_form, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            NotifyBalloon(NoticeKind.Error, title, message, ToolTipIcon.Warning, 5000);
        }
        catch
        {
            // Сообщение не показалось — не повод падать.
        }
    }

    public void OpenLog() => Guard(() => _server.OpenLog());

    /// <summary>Личный кабинет DeepSeek: расход и пополнение счёта.</summary>
    public void OpenPlatform() => Guard(() =>
        Process.Start(new ProcessStartInfo("https://platform.deepseek.com/usage") { UseShellExecute = true }));

    public void OpenDshFolder() => Guard(() => _server.OpenDshFolder());

    public void SaveSettings() => Settings.Save(_paths.SettingsPath);

    public void SetAutostart(bool enabled)
    {
        if (_busy) return;
        _form.SetBusy(Loc.T("busy.autostart"));
        try
        {
            if (!Autostart.Set(enabled))
            {
                throw new InvalidOperationException(Loc.T("err.autostart"));
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _form.ClearBusy();
            _autostartState = Autostart.Inspect();
            _form.SetAutostartNote(_autostartState);
            RefreshState();
        }
    }

    /// <summary>
    /// Сверяет запись автозапуска с этой копией и чинит её, если она ведёт на отсутствующий
    /// файл, на более старую копию или под прежним именем. Про живую соседнюю копию панель
    /// не молчит: без этого человек уверен, что при входе поднимется именно эта панель.
    /// </summary>
    private void CheckAutostart()
    {
        try
        {
            var fix = Autostart.Repair();
            _autostartState = fix.After;
            _form.SetAutostartNote(_autostartState);

            if (fix.Changed)
            {
                AppLog.Write(_paths, "автозапуск: " + DescribeAutostartFix(fix)
                    + (fix.Before.Path.Length > 0 ? $", было: {fix.Before.Path}" : ""));
            }

            // Шарик — только про то, что панель не смогла починить сама: правило про состояние
            // записи («живую соседнюю копию шариком не трогаем») живёт в Autostart.ShouldNotify.
            // Изоляцию проверяют оба — и ShouldNotify (это его контракт, и его проверяет
            // самопроверка), и дверь NotifyBalloon (она общая для всех автоматических сообщений);
            // на классе дефекта, из-за которого владелец получил шарик, лишняя проверка не лишняя.
            if (Autostart.ShouldNotify(fix.After.Where, Autostart.IsIsolatedRun))
            {
                NotifyBalloon(NoticeKind.AutostartBroken, Loc.T("tray.autostartBrokenTitle"),
                    Loc.T("tray.autostartBrokenText", fix.After.Path), ToolTipIcon.Warning, 10000);
            }
        }
        catch
        {
            // Реестр недоступен — панель работает как раньше, без автозапуска.
        }
    }

    /// <summary>
    /// Итог прошлого обновления. Панель закрылась «на замену файлов», а замены могло не
    /// случиться (панель не вышла, мьютекс занят, откат) — тогда человек обязан узнать об этом,
    /// иначе он считает, что обновился, и ждёт новых возможностей от старой версии.
    /// </summary>
    private void CheckUpdateOutcome()
    {
        try
        {
            // Итог разбирается и пишется в журнал ВСЕГДА, вместе с изолированным прогоном:
            // это делает сам UpdateService.StartupNotice до возврата (и «не применилось», и
            // «применилось»), поэтому предохранитель стоит только перед показом человеку.
            var outcome = UpdateService.StartupNotice(_paths);
            if (outcome == null || !outcome.Failed) return;

            NotifyBalloon(NoticeKind.UpdateOutcome, Loc.T("update.title"), outcome.Message,
                ToolTipIcon.Warning, 15000);
        }
        catch
        {
            // Итог разобрать не удалось — обновление и без того работает как работает.
        }
    }

    /// <summary>Что именно сделала починка автозапуска — строкой для журнала (журнал по-русски).</summary>
    private static string DescribeAutostartFix(Autostart.Fix fix)
    {
        var text = fix.Action switch
        {
            "dedup" => "убрал вторую запись (прежнее имя), осталась одна",
            "migrated" => "перенёс запись прежнего имени на эту копию",
            "normalized" => "дописал --tray в запись (панель открывалась окном)",
            "missing" => "запись вела на отсутствующий файл — перевёл на эту копию",
            "older" => "запись вела на более старую копию — перевёл на эту",
            "failed" => "не удалось записать автозапуск в реестр — осталось как было",
            _ => fix.Action,
        };

        return fix.HadDuplicate && fix.Action != "dedup"
            ? "убрал вторую запись (прежнее имя); " + text
            : text;
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch
        {
            // Ничего.
        }
        try
        {
            _form.Close();
            _form.Dispose();
        }
        catch
        {
            // Ничего.
        }
        ExitThread();
    }

    private void StartInitialServer()
    {
        // Как и прежняя панель: если сервера нет — поднимаем его сами.
        // Значения снимаем на потоке интерфейса, работаем — в фоне.
        //
        // Браузер при этом — единственное, что человек УВИДЕЛ БЫ, не сделав ничего, поэтому он
        // и глушится в изолированном прогоне. Сам сервер глушить нельзя: приёмка
        // (tools\acceptance.ps1) считает именно поднятый на её порту сервер и ссылку входа,
        // а не окно браузера. Граница простая: проверяемое — работает, видимое — молчит.
        var wantsBrowser = !_startHidden && Settings.OpenBrowserOnStart;
        var openBrowser = wantsBrowser && ShouldAutoOpenBrowser(Autostart.IsIsolatedRun);
        if (wantsBrowser && !openBrowser)
        {
            AppLog.Write(_paths, "браузер при старте не открыт (изолированный прогон)");
        }

        Task.Run(() =>
        {
            try
            {
                var status = _server.GetStatus();
                if (status.Running || status.PortBusyByOther) return;
                _server.Start(openBrowser);
            }
            catch (Exception error)
            {
                // Фоновая ветка: сервер поднимался сам, человека рядом с этим действием нет.
                ShowError(error.Message, background: true);
            }
            finally
            {
                try
                {
                    _form.BeginInvoke(new Action(RefreshState));
                }
                catch
                {
                    // Окно уже закрыто.
                }
            }
        });
    }

    /// <summary>Отчёт самопроверки: окно собирается и рисует состояние, ничего не запуская.</summary>
    public string SelfTestReport()
    {
        ApplyToForm();
        _form.CreateControl();
        _form.PerformLayout();
        var size = _form.ClientSize;
        return string.Join(Environment.NewLine, new[]
        {
            $"Окно: {size.Width}×{size.Height}, состояния применены",
            $"Подсказка трея: {BuildTooltip().Replace(Environment.NewLine, " | ")}",
            $"Пункты меню: {_menu.Items.Count}",
            $"Иконка трея: {( _tray.Icon == null ? "нет" : (_iconKey + ", " + _tray.Icon.Width + "px") )}",
        });
    }

    /// <summary>Снимок окна в PNG — для проверки внешнего вида без показа окна.</summary>
    public void SavePanelShot(string path, BalanceResult balance)
    {
        if (balance != null) _balanceResult = balance;
        ApplyToForm();

        // Контролы рисуются только у показанного окна, поэтому показываем его
        // далеко за пределами экрана и сразу убираем.
        var previousStart = _form.StartPosition;
        var previousTaskbar = _form.ShowInTaskbar;
        _form.StartPosition = FormStartPosition.Manual;
        _form.Location = new Point(-4000, -4000);
        _form.ShowInTaskbar = false;
        _form.Show();
        Application.DoEvents();
        Thread.Sleep(250);
        Application.DoEvents();
        _form.Refresh();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // DrawToBitmap рисует окно целиком, вместе с заголовком и рамкой,
        // поэтому снимаем всё окно и вырезаем из него клиентскую область.
        var origin = _form.PointToScreen(Point.Empty);
        var offset = new Point(origin.X - _form.Location.X, origin.Y - _form.Location.Y);
        using (var window = new Bitmap(_form.Width, _form.Height))
        {
            _form.DrawToBitmap(window, new Rectangle(0, 0, window.Width, window.Height));
            using var client = window.Clone(new Rectangle(offset, _form.ClientSize), window.PixelFormat);
            client.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        _form.Hide();
        _form.StartPosition = previousStart;
        _form.ShowInTaskbar = previousTaskbar;
    }

    private void ApplyToForm()
    {
        _form.ApplyStatus(_status, _peakState, _balanceResult, _busy, Autostart.IsEnabled());
        UpdateTray();
        UpdateMenu();
    }

    /// <summary>Снимок окна настроек в PNG — для проверки вида без показа окна.</summary>
    public void SaveSettingsShot(string path, bool stretched = false)
    {
        using var dialog = new SettingsForm(Settings, _paths, _pricing,
            () => _peak.GetState().ScheduleText + " · данные от " + _peak.GetState().Checked)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };

        // Растянутый вид: проверка, что окно можно развернуть и блок цен растёт.
        if (stretched)
        {
            dialog.ClientSize = new Size(dialog.ClientSize.Width + 240, dialog.ClientSize.Height + 200);
        }

        RenderShot(dialog, path);
    }

    /// <summary>Снимок большого окна с ценами — для проверки вида.</summary>
    public void SavePricesShot(string path, PricingCheckResult result)
    {
        using var window = new PricesWindow(result, result?.SourceUrl)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };

        RenderShot(window, path);
    }

    /// <summary>Снимок окна «Резервные копии» — для проверки вида без показа окна.</summary>
    public void SaveBackupsShot(string path)
    {
        using var dialog = new BackupForm(Settings, _paths)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };

        RenderShot(dialog, path);
    }

    /// <summary>Снимок меню трея — для проверки вида, что галочка на месте.</summary>
    public void SaveMenuShot(string path)
    {
        try
        {
            UpdateMenu();
            _menu.Show(new Point(-4000, -4000));
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();

            var size = _menu.GetPreferredSize(Size.Empty);
            var width = Math.Max(size.Width, _menu.Width);
            var height = Math.Max(size.Height, _menu.Height);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var bitmap = new Bitmap(width, height))
            {
                _menu.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally
        {
            _menu.Close();
        }
    }

    /// <summary>
    /// Проверка вёрстки всех окон панели: не обрезан ли где-нибудь текст. Нужна потому,
    /// что окна собраны на точных координатах, а подписи приходят из словарей — на английском
    /// и китайском строка может оказаться шире отведённого места, и глазами это видно только
    /// на снимках.
    /// </summary>
    public List<string> LayoutIssues(PricingUpdateService pricing, PricingCheckResult sample)
    {
        var issues = new List<string>();
        issues.AddRange(LayoutCheck.Inspect("панель", _form));

        try
        {
            using var settings = new SettingsForm(Settings, _paths, pricing,
                () => _peak.GetState().ScheduleText, null, null);
            issues.AddRange(LayoutCheck.Inspect("настройки", settings));
        }
        catch (Exception error)
        {
            issues.Add("настройки: окно не построилось — " + error.Message);
        }

        try
        {
            using var prices = new PricesWindow(sample, sample?.SourceUrl);
            issues.AddRange(LayoutCheck.Inspect("цены", prices));
        }
        catch (Exception error)
        {
            issues.Add("цены: окно не построилось — " + error.Message);
        }

        try
        {
            using var backups = new BackupForm(Settings, _paths);
            issues.AddRange(LayoutCheck.Inspect("копии", backups));
        }
        catch (Exception error)
        {
            issues.Add("копии: окно не построилось — " + error.Message);
        }

        try
        {
            using var wizard = new OnboardingForm(_paths, Settings);
            issues.AddRange(LayoutCheck.Inspect("мастер", wizard));
        }
        catch (Exception error)
        {
            issues.Add("мастер: окно не построилось — " + error.Message);
        }

        try
        {
            // Накат показывает самую свежую копию из папки: если копий нет, проверяется
            // пустое окно, а полное — в приёмке, где копия к этому моменту уже сделана.
            using var restore = new RestoreForm(_paths, Settings, _backups, () => false, () => true);
            issues.AddRange(LayoutCheck.Inspect("накат", restore));
        }
        catch (Exception error)
        {
            issues.Add("накат: окно не построилось — " + error.Message);
        }

        return issues;
    }

    /// <summary>Снимок окна наката — для проверки вида без показа окна.</summary>
    public void SaveRestoreShot(string path)
    {
        using var window = new RestoreForm(_paths, Settings, _backups, () => false, () => true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };

        RenderShot(window, path);
    }

    public void SaveOnboardingShot(string path, int step = 0)
    {
        // Единственный снимок, который раньше показывался по центру экрана: OnboardingForm сам
        // ставит CenterScreen и ShowInTaskbar, а соседние снимки уводят окно за экран. Съёмка
        // идёт в изолированном прогоне на машине владельца (рецепт — в AGENTS.md), поэтому окно
        // уводим так же, как у соседей: пиксели те же, а на рабочем столе ничего не мелькает.
        using var wizard = new OnboardingForm(_paths, Settings)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            ShowInTaskbar = false,
        };

        wizard.ShowStepForCheck(step);
        RenderShot(wizard, path);
    }

    private static void RenderShot(Form dialog, string path)
    {
        dialog.Show();
        Application.DoEvents();
        Thread.Sleep(250);
        Application.DoEvents();
        dialog.Refresh();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var origin = dialog.PointToScreen(Point.Empty);
        var offset = new Point(origin.X - dialog.Location.X, origin.Y - dialog.Location.Y);
        using (var window = new Bitmap(dialog.Width, dialog.Height))
        {
            dialog.DrawToBitmap(window, new Rectangle(0, 0, window.Width, window.Height));
            using var client = window.Clone(new Rectangle(offset, dialog.ClientSize), window.PixelFormat);
            client.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        dialog.Hide();
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Открытие проводника или блокнота — не критичная операция.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
            _tray?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
