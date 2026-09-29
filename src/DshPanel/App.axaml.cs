using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DshPanel.Autostart;
using DshPanel.Backup;
using DshPanel.Balance;
using DshPanel.Issue;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Update;
using DshPanel.Views;

namespace DshPanel;

public partial class App : Application
{
    private PanelShell? _shell;
    private ServerController? _server;
    private AutostartController? _autostart;
    private SettingsController? _settings;
    private BalanceController? _balance;
    private PricingController? _pricing;

    /// <summary>
    /// ОБНОВЛЕНИЕ ПАНЕЛИ — первый срез: проверка выпусков и извещение. Скачивания и замены
    /// файлов здесь нет вовсе, их делает второй срез; поле заведено сейчас, потому что часы
    /// и права у проверки свои.
    /// </summary>
    private UpdateController? _update;

    /// <summary>
    /// ДВИЖОК УСТАНОВКИ обновления — то, чем окно готовит замену файлов и запускает сценарий.
    /// Живёт рядом с проверкой и строится тем же запуском: у обоих один предмет (выпуск панели),
    /// и разводить их по разным местам значило бы искать право готовить замену в двух местах.
    /// </summary>
    private UpdateInstallController? _updateInstall;

    private BackupController? _backups;
    private TrayIconHost? _tray;
    private InstanceSignal? _signal;

    /// <summary>Журнал панели. Подменяется самотестом связки на список в памяти.</summary>
    private Action<string> _log = _ => { };

    /// <summary>
    /// Последняя НОРМАЛЬНАЯ геометрия главного окна (в пикселях экрана). Хранится здесь, а не
    /// спрашивается у окна в момент записи: свёрнутое окно отдало бы «свёрнуто», и панель
    /// открылась бы свёрнутой в углу.
    /// </summary>
    private PixelRect? _lastNormalPlacement;

    /// <summary>
    /// Загрузить разметку приложения И объявить ресурсы вида (тень карточек, кисти тонов
    /// состояния). Ресурсы ставятся ЗДЕСЬ, а не в разметке, по одной причине: кисти тонов
    /// зависят от темы (на светлой — тёмная зелень, на тёмной — светлая), и собрать их
    /// из чужой палитры (<see cref="TrayPalette"/>) разметка не умеет. Разметка берёт готовое
    /// через <c>DynamicResource</c> и переключается вместе с темой сама.
    ///
    /// ⚠️ Порядок важен: <see cref="AvaloniaXamlLoader.Load"/> — первым. Стили, объявленные
    /// в <c>App.axaml</c>, ссылаются на эти ресурсы, и объявить их до разметки значило бы
    /// положить ресурсы в словарь, который разметка тут же заменит.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        PanelLook.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (ProgramOptions.SkipDefaultUi)
            {
                // Режим проверки строит значок и окно сам (`--tray-selftest`), и второй
                // значок ему мешал бы.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            else
            {
                StartPanel(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Применить тему ко ВСЕМУ приложению. Тема — свойство приложения, а не окна: иначе
    /// настройки были бы тёмными, а панель светлой.
    ///
    /// <c>ThemeVariant.Default</c> означает «как в Windows» — это и есть умолчание, и оно
    /// то же, что было в v1 (`themeMode`: <c>auto</c>).
    /// </summary>
    public static void ApplyTheme(PanelTheme theme)
    {
        if (Current is null) return;

        Current.RequestedThemeVariant = theme switch
        {
            PanelTheme.Light => ThemeVariant.Light,
            PanelTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>
    /// Обычный запуск — то, что видит человек, открыв панель. Панель живёт в трее:
    /// при старте ставится значок, и **окно не показывается** — его открывает щелчок по значку
    /// (или пункт меню). Так панель не кладёт окно поверх работы владельца при каждом запуске,
    /// в том числе при автозапуске.
    ///
    /// Завершение приложения — только явное: <see cref="ShutdownMode.OnExplicitShutdown"/>.
    /// Иначе закрытие окна (которое у панели означает «спрятаться») гасило бы панель целиком.
    ///
    /// Строится здесь, а не в колбэке <c>StartWithClassicDesktopLifetime</c>, намеренно:
    /// тот колбэк вызывается ДО подъёма платформы, и первое же окно падает с «Unable to locate
    /// 'Avalonia.Platform.IWindowingPlatform'». Поймал самотест связки 24.09.2026.
    /// </summary>
    private void StartPanel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Самотест связки подменяет журнал списком в памяти: прогон проверки не пишет владельцу
        // в его каталоги (красная линия 4), но проверяет, что нужные строки в журнал легли.
        var selfTest = ProgramOptions.ShellSelfTest;
        Action<string> log = selfTest is null ? PanelLog.Write : selfTest.CollectLog;
        _log = log;

        var paths = RunContext.Current.Paths;
        Directory.CreateDirectory(paths.DataDir);

        // Право «это обычный запуск панели человеком». Одно на всю панель, и его не выдаёт
        // ни одна проверка: им открываются и запись автозапуска в реестр, и чтение окружения
        // прежней панели. Подробности — у самого свойства.
        var human = ProgramOptions.HumanLaunch;
        var isolated = RunContext.Current.IsIsolated;

        // Показано ли окно панели при этом запуске. Обычный запуск окна НЕ показывает (панель
        // живёт в трее), и это единственный случай, о котором человеку надо сказать словами:
        // молчаливый уход в трей он понимает как «панель не запустилась» (замечание владельца
        // 29.09.2026). Флаг поднимается в ОДНОМ месте, где окно действительно показано.
        var windowShown = false;

        // НАСТРОЙКИ — ДО ОКОН. Тема применяется сразу при чтении, а рабочая папка сервера
        // спрашивается у настроек в момент запуска сервера. Поэтому контроллер настроек
        // строится раньше и окна, и сервера.
        _settings = new SettingsController(
            new SettingsStore(paths.SettingsFile),
            paths,
            SettingsController.For(RunContext.Current, human),
            canReadOwnerEnvironment: human && !isolated,
            locateEngine: () => DshEngine.Locate(),
            () => _server,
            ApplyThemeFromAnyThread,
            log);

        var window = new MainWindow();
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Размер и положение окна — просьба человека, и она обязана пережить перезапуск панели.
        // Читаются ЗДЕСЬ, явным вызовом при построении окна, а не внутри окна: `--shot` строит
        // MainWindow напрямую, без настроек и без права читать файл владельца, — личное в кадр
        // попадать не должно (красная линия 7).
        RestorePlacement(window);

        // Панель показывает состояние сервера и умеет его поднять и погасить. Контроллер берёт
        // пути прогона: у человека это его собственные каталоги панели, в изолированном прогоне —
        // под его корнем. Домашний каталог движка приходит отсюда же, поэтому «свой DSH_HOME» —
        // не обещание, а следствие одного источника путей.
        //
        // Рабочий каталог — ФУНКЦИЯ: это настройка, и она читается в момент запуска сервера,
        // чтобы её смена применялась без перезапуска панели. Порт — по той же причине функция:
        // человек меняет его в настройках, и следующий запуск сервера обязан взять новый.
        //
        // Запомненное согласие (решение владельца 26.09.2026) приходит сюда ДВУМЯ делегатами,
        // а не ссылкой на настройки: контроллер сервера про них не знает вовсе. Чтение — из
        // действующих настроек, запись — тем же путём, что кнопка «Сохранить»: если записать
        // нельзя (прогон проверки без права), согласия просто не будет, и панель спросит
        // подтверждение, как раньше.
        //
        // Право «поднимать рядом» — изолированный прогон и только он: там свой корень и свои
        // данные, а в обычном запуске рядом с найденным сервером панель подняла бы второй движок
        // на том же `~/.dsh` (находка 25.09.2026). Право выдаётся здесь открытым текстом.
        //
        // Право ЗАНЯТЬ ПОРТ ВЛАДЕЛЬЦА (3080) — решение владельца 26.09.2026: 2.0 встаёт на место
        // 1.x, и её сервер обязан подняться на том же адресе. Снят запрет ТОЛЬКО для обычного
        // запуска человеком: через 3080 идёт канал этой сессии, и прогон проверки займёт 3081
        // (`RunFallbackPort`). Право тоже приходит открытым текстом и без умолчания.
        var mayOccupyOwnerPort = RunRights.MayOccupyOwnerPort(RunContext.Current, human);

        // ФАЙЛЫ СОСТОЯНИЯ СЕРВЕРА (ссылка входа и запись «этот сервер наш») — тоже по праву.
        // Прогон проверки идёт ОБЫЧНЫМ путём панели, и, знай контроллер пути сам, он прочитал бы
        // настоящие файлы владельца: подсмотрел бы его состояние и мог бы признать ЕГО сервер своим.
        // Поэтому пути знает только это место, а прогон без права получает пустышки.
        var fileRights = RunRights.LocalData(RunContext.Current, human);
        var stateFiles = fileRights ? ServerStateFiles.Under(paths, log) : ServerStateFiles.None;

        if (!fileRights)
        {
            // Подавленное решение обязано быть видно в журнале — иначе «панель ничего не запомнила»
            // нечем объяснить. Одной строкой, а не на каждое обращение: состояние спрашивают
            // раз в секунду.
            log(PanelStrings.ServerStateFilesSuppressedLog);
        }

        _server = new ServerController(
            paths.DshHome,
            () => _settings!.WorkDirInUse,
            log,
            mayOccupyOwnerPort: mayOccupyOwnerPort,
            requestedPort: () => _settings!.Settings.ServerPort,
            adoptedPort: () => _settings!.Settings.AdoptedServerPort,
            rememberAdoptedPort: port => _settings!.SetAdoptedServer(port),
            allowParallelStart: isolated,
            files: stateFiles,

            // Запасной источник ссылки входа — файл панели 1.x, ТОЛЬКО ЧТЕНИЕ. Своей ссылки
            // у найденного сервера нет, а человеку, взявшему его под управление, кнопка нужна.
            // Чужой каталог читает только обычный запуск панели человеком: право то же, что
            // у машинного окружения, и в прогоне проверки источник молчит.
            foundServerEntryLink: RunRights.MachineData(RunContext.Current, human)
                ? () => ServerStateFiles.ReadText(AppPaths.V1.EntryLinkFile)
                : null);

        window.Attach(_server);
        window.SettingsRequested += () => _shell?.OpenSettings();

        // «Открыть панель» — четвёртая дверь главного окна, и она ведёт туда же, куда пункт меню
        // значка: в связку, а та уже спрашивает предохранитель изоляции. Двух путей к браузеру
        // в панели быть не должно — один из них однажды открыл бы окно в прогоне проверки.
        window.OpenAgentRequested += () => _shell?.OpenEntryLink();

        // Состояние сервера перечитано (опрос раз в секунду и каждое действие человека) —
        // обновляем ОГОНЁК на значке тем же решением, что и строку в меню. Повторы гасит сам
        // значок: с тем же тоном он оболочку не трогает вовсе (см. UpdateTrayIcon).
        window.StateRendered += UpdateTrayIcon;

        // Автозапуск: сверка записи с ЭТОЙ копией при старте. Запись хранит абсолютный путь,
        // а копий панели на машине бывает несколько и папку переносят — без сверки после входа
        // в Windows поднимается чужая копия, а галочка показывает «включено».
        //
        // Машинную запись меняет ТОЛЬКО обычный запуск человеком: у прогона проверки хранилище
        // «только на чтение», у изолированного — файл под его корнем. Права на запись даёт
        // Program (ProgramOptions.HumanLaunch), и ни одна проверка их не даёт.
        _autostart = new AutostartController(
            AutostartStores.For(RunContext.Current, human),
            AutostartStores.SelfExe(),
            log);

        _autostart.Repair();

        // Баланс и окна пика АКТИВНОГО агента. Ключ читается в момент запроса из файла ключей
        // движка, и только если прогон имеет на это право: в прогоне проверки контроллер
        // не тронет ни файла, ни сети (`RunRights.LocalData`).
        _balance = new BalanceController(
            new HttpBalanceClient(),
            paths.CredentialsPath,
            () => _settings!.Settings,
            allowed: RunRights.LocalData(RunContext.Current, human),
            clock: () => DateTimeOffset.Now,
            dispatch: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: log,
            notify: Notify,
            isolatedRun: isolated);

        window.AttachBalance(_balance);

        // ЦЕНЫ СО СТРАНИЦЫ ЦЕН — второй, отдельный уговор (см. `Pricing\PricingController`).
        // Часы у него свои: разбор идёт раз в сутки, а не раз в полминуты, как баланс.
        //
        // Право то же, что у баланса (`RunRights.LocalData`), и это не «на всякий случай»:
        // страница цен публична, но СЕТЬ от имени владельца — его право, и прогон проверки
        // в неё не ходит вовсе. Работа по расписанию закрыта отдельно и общим предикатом
        // изоляции внутри контроллера.
        _pricing = new PricingController(
            new HttpPricingClient(),
            () => _settings!.Settings,
            () => Loc.Language,
            allowed: RunRights.LocalData(RunContext.Current, human),
            clock: () => DateTimeOffset.Now,
            dispatch: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: log,
            rememberCheckedAt: stamp => _settings!.SetPricingCheckedAt(stamp),
            rememberWindows: json => _settings!.SetPricingPeakWindows(json),
            rememberLast: json => _settings!.SetPricingLast(json),

            // ИСТОРИЯ ЦЕН — свой файл состояния рядом с остальными (`state\pricing-history.json`),
            // и путь к нему даёт ОДНА дверь `AppPaths`: второго места, где вычисляются пути панели,
            // нет. Право писать — то же, что у самой истории: прогон без права получил бы
            // `PricingHistoryFiles.None`, а не путь владельца.
            historyFiles: PricingHistoryFiles.Under(paths),

            // Шарик об изменении тарифа идёт ОБЩЕЙ дверью сообщений (`Notify` ниже): она одна
            // решает, показывать ли сообщение в этом прогоне (`IsolationRules.ShouldNotify`),
            // и своего пути к рабочему столу владельца у истории цен нет.
            notify: Notify,
            isolatedRun: isolated);

        // Часы разбора страницы цен. В прогоне проверки они не запускаются, и контроллер
        // сам говорит об этом строкой в журнал — «панель молчала» должно быть видно словами.
        _pricing.Start();

        // ОБНОВЛЕНИЕ ПАНЕЛИ — ПЕРВЫЙ СРЕЗ: проверка выпусков публичного репозитория и извещение
        // шариком. **Ни скачивания, ни замены файлов здесь нет** — их делает второй срез.
        //
        // Право — то же, что у баланса и цен (`RunRights.LocalData`), и это не «на всякий случай»:
        // выпуски публичны, но СЕТЬ от имени владельца — его право, и прогон проверки в неё
        // не ходит вовсе. Автоматическая работа по расписанию закрыта отдельно и общим предикатом
        // изоляции внутри решения (`UpdateDecisions.ShouldCheck`), а в прогоне проверки часы
        // не запускаются и говорят об этом строкой в журнал.
        //
        // Текущая версия для сравнения берётся у САМОЙ СБОРКИ, а не из настроек: версия — факт
        // о файле панели, и второй правды о ней быть не должно. Числовая часть вырезается
        // решением ядра (единая реализация — см. `UpdateDecisions.Numeric`).
        _update = new UpdateController(
            new HttpUpdateClient(),
            () => _settings!.Settings,
            () => Loc.Language,
            () => Views.AboutWindow.PanelVersion,
            allowed: RunRights.LocalData(RunContext.Current, human),
            clock: () => DateTimeOffset.Now,
            dispatch: action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            log: log,
            remember: traces => _settings!.SetUpdateChecked(traces),
            rememberCheckedAt: stamp => _settings!.SetUpdateCheckedAt(stamp),
            isolatedRun: isolated);

        _update.Start();

        // ДВИЖОК УСТАНОВКИ — то, чем окно обновления готовит замену файлов. Право готовить
        // приходит ИЗ ОДНОГО МЕСТА (`UpdateInstall.For`, тот же расчёт, что у копий): изолированный
        // прогон (там всё под своим корнем) или обычный запуск человеком. Прогон проверки без корня
        // замену файлов не готовит вовсе — и это не «постараемся», а отказ движка.
        //
        // Папка панели, которую заменит сценарий, — папка ЭТОГО exe: второй поиск «где лежит панель»
        // однажды нашёл бы не ту папку, а заменять чужие файлы панель не имеет права.
        _updateInstall = new UpdateInstallController(
            paths,
            () => Views.AboutWindow.PanelVersion,
            target: System.IO.Path.GetDirectoryName(AutostartStores.SelfExe()) ?? string.Empty,
            allowed: UpdateInstall.For(RunContext.Current, human),
            startArgument: string.Empty,
            mutexName: InstanceSignal.DefaultMutexName,
            log: log);

        // ИТОГ ПРОШЛОГО ОБНОВЛЕНИЯ — ПРИ СТАРТЕ. Сценарий замены оставляет в состоянии панели
        // журнал и папку сборки, а сам ничего не рассказывает; в панели 1.x этот журнал однажды
        // не читал ни один файл, и человек видел «обновление готово» там, где файлы не заменились.
        // Поэтому итог читается здесь — словами и один раз.
        ShowUpdateOutcome(paths, human, log);

        // АКТИВНЫЙ АГЕНТ МЕНЯЕТСЯ И В ГЛАВНОМ ОКНЕ (решение владельца 27.09.2026: «активный агент
        // так же должен быстро меняться в главном окне, где указан баланс» — выпадающим списком).
        // Смену ЗАПИСЫВАЕТ владелец настроек — тот же, что стоит за кнопкой «Сохранить»
        // и за окном настроек: у значения одно место правды, и второго способа записи в панели
        // нет. Окну для этого не нужно ничего, кроме владельца: о новой сумме ему скажет контроллер.
        window.AttachAgentChoice(_settings!);

        _balance.Changed += UpdateTrayTooltip;

        // КОПИИ: окно открывается кнопкой в главном окне (решение владельца 26.09.2026). Строится
        // по просьбе — по той же причине, что и настройки: панель живёт в трее месяцами, и второе
        // окно, которое человек открывает раз в месяц, незачем держать построенным.
        //
        // Право снимать и раскладывать копии даёт красная линия 5: изолированный прогон (там все
        // пути под своим корнем) или обычный запуск человеком. Прогон проверки без корня копий
        // не снимает вовсе — его архив лёг бы в каталог владельца; контроллер отказывает, а не молчит.
        //
        // Владелец настроек передаётся сюда ОДИН И ТОТ ЖЕ, что и окну настроек: решение о ключах
        // человек принимает в двух окнах, а значение у него одно — файл настроек. Через этот
        // владелец окно копий меняет его тем же путём, что и кнопка «Сохранить» в настройках.
        _backups = new BackupController(
            paths,
            () => _settings!.Settings,
            allowed: BackupController.For(RunContext.Current, human),
            locateEngine: () => DshEngine.Locate(),
            clock: () => DateTimeOffset.Now,
            log: log,
            settingsOwner: _settings,

            // Изолированный прогон не раскладывает движок и Node за пределы своего корня: там
            // лежит глобальная установка машины, а не данные прогона. У обычного запуска человеком
            // (а окно бывает только у него) значение false — поведение не меняется.
            confineToRunRoot: RunContext.Current.IsIsolated,

            // Автоматической копии в изолированном прогоне не бывает: фоновые задачи в изоляции
            // не работают (красная линия), и расписание там даже не заводит часов — но говорит
            // об этом строкой в журнал, а не молчанием.
            isolatedRun: isolated,

            // Правда о сервере — только для предупреждения «копия снята на ходу». Гасить его
            // ради копии панель не станет: решение владельца 26.09.2026 (агент работает ночью,
            // и остановка погубила бы его работу).
            serverRunning: () => _server?.State.Presence == ServerPresence.Running,

            // Сообщения — через общую дверь изоляции (см. Notify ниже), а возврат из фоновой
            // копии — в нитку интерфейса: и сообщение, и журнал принадлежат панели.
            notify: Notify,
            dispatch: action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        window.BackupsRequested += () => _shell?.OpenBackups();

        // ПИКИ И ТАРИФЫ: окно открывается кнопкой рядом с «Обновить баланс» — там же, где человек
        // видит состояние тарифа (решение дирижёра 27.09.2026, названо владельцу). Дверь у него
        // ТА ЖЕ, что у настроек, копий и «О программе»: просьбу принимает связка, окно строится
        // по требованию и живёт в своём слоте, поэтому второго такого окна не бывает.
        window.PeaksRequested += () => _shell?.OpenPeaks();

        // ШАРИК О НОВОМ ВЫПУСКЕ. Решение «говорить ли» принято контроллером (один раз на версию
        // и никогда для пропущенной — `UpdateDecisions.ShouldAnnounce`), а показывает сообщение
        // ОБЩАЯ дверь панели (`Notify`): она же закрывает все автоматические сообщения в
        // изолированном прогоне (`IsolationRules.ShouldNotify`), и второго пути к шарику
        // в панели быть не должно.
        _update.Announce += version => Notify(
            NoticeKind.UpdateAvailable,
            PanelStrings.TrayToolTip,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateAvailableFormat,
                version));

        // ДВЕРЬ В ОКНО ОБНОВЛЕНИЯ ПЕРЕЕХАЛА В НАСТРОЙКИ (замечание владельца 28.09.2026: кнопку
        // обновления с главной панели убрать, а в настройках завести отдельный пункт). Здесь её
        // больше нет; путь тот же — просьбу принимает связка `_shell.OpenUpdate`, и у окна
        // по-прежнему ОДИН слот, поэтому второго окна обновления не бывает.

        // «О программе» — дверь ПЕРЕЕХАЛА в настройки (решение владельца 27.09.2026: «„о программе“
        // не туда воткнул. Предлагаю перенести вообще в настройки»). Окну для неё не нужно ничего:
        // ни контроллеров, ни настроек, ни данных человека, — поэтому оно строится позже всех
        // и ни от чего не зависит (см. CreateAboutWindow), а дверь открывает связка: у окна
        // «О программе» от этого по-прежнему ОДИН слот, и второго такого окна не бывает.

        // Главным окном приложения окно НЕ объявляем — и это не мелочь. Каркас показывает своё
        // главное окно сам, когда запускает цикл, ещё до всякого щелчка: панель вылезала бы
        // поверх работы владельца при каждом запуске. Поймал самотест связки 24.09.2026
        // строкой «до щелчка окно спрятано = False»; пока объявление было на месте, проверка
        // щелчка была слепой — она зеленела и при полностью отключённом показе окна.
        // Ниже, в отказе без значка, окно главным объявляется осознанно: там оно единственное,
        // что у панели есть, и закрытие окна обязано её завершать.
        desktop.Exit += (_, _) =>
        {
            // Геометрия окна запоминается и здесь: панель гасят и из значка, когда окно спрятано
            // и события Closing не будет вовсе. Делается это ДО того, как владелец настроек
            // обнулится, — иначе запоминать было бы некуда.
            SavePlacement(window);

            // Сервер при выходе НЕ гасим: он живёт своей жизнью, как и в v1 — иначе закрытие
            // панели обрывало бы человеку работу. Освобождаем только дескриптор.
            if (_signal is not null && _shell is not null) _signal.ShowPanelRequested -= _shell.ShowFromSignal;
            _signal = null;
            _server?.Dispose();
            _server = null;
            _autostart = null;
            _balance?.Dispose();
            _balance = null;

            // Часы разбора страницы цен гасятся по той же причине, что часы автокопий: таймер,
            // переживший выход, дёргал бы сеть у завершающегося приложения.
            _pricing?.Dispose();
            _pricing = null;

            // Часы проверки выпусков гасятся здесь же и по той же причине: таймер, переживший
            // выход, дёргал бы сеть у завершающегося приложения.
            _update?.Dispose();
            _update = null;
            _updateInstall = null;

            // Часы автокопии гасятся вместе с панелью: таймер, переживший выход, дёргал бы
            // движок копий у завершающегося приложения.
            _backups?.Dispose();
            _backups = null;
            _settings = null;
            _shell?.Dispose();
            _shell = null;
            _tray = null;
        };

        var panelWindow = new PanelWindow(window, isolated, log);

        // Запись геометрии — при «окно спрятано»: и крестик, и «Выход» проходят через Closing,
        // а это последнее мгновение, когда окно стоит там, где его оставил человек.
        panelWindow.BeforeClosing = () => SavePlacement(window);
        _tray = new TrayIconHost(PanelStrings.TrayToolTip);

        // Изоляцию связка получает ОТКРЫТО и без умолчания: это она решает, показывать ли окно
        // «О программе» (в прогоне проверки — никогда, красная линия 8).
        _shell = new PanelShell(
            _tray,
            panelWindow,
            desktop,
            log,
            isolated,

            // Право открыть браузер с работой агента — ТО ЖЕ, что у остальных «человеческих»
            // дверей, и спрашивается у RunRights, а не выводится из одной изолированности:
            // --shell-selftest идёт обычным путём панели и не изолирован (находка холодной
            // проверки 27.09.2026 — по прежнему условию он имел право открыть браузер).
            mayOpenBrowser: RunRights.MayOpenBrowser(RunContext.Current, human),
            entryLink: () => _server?.EntryLink ?? string.Empty,
            trayStatus: TrayStatusNow,
            notify: Notify,
            createAbout: CreateAboutWindow,
            createIssue: CreateIssueWindow,

            // Пункт меню «Проверить обновления» — тот же путь, что кнопка «Проверить сейчас»
            // в окне обновления: щелчок человека идёт МИМО суточного гейта
            // (`UpdateController.Check`). Второго способа попросить проверку в панели нет.
            checkUpdate: () => _update?.Check(),

            createSettings: CreateSettingsWindow,
            createBackups: CreateBackupWindow,
            createPeaks: CreatePeakWindow,

            // Окну обновления нужны ДВА домена: проверка выпусков и движок установки.
            createUpdate: CreateUpdateWindow,

            // Окну истории цен нужен ОДИН домен — контроллер цен: он же владелец истории,
            // и разойтись им нечем.
            createPricingHistory: CreatePricingHistoryWindow);

        try
        {
            _shell.Start();
        }
        catch (Exception ex)
        {
            // Панель, у которой не встал значок, невидима и не имеет способа себя закрыть.
            // Поэтому в этом случае она работает «окном без значка»: окно показано, и закрытие
            // окна её завершает. Молчаливое «ничего не показали» выглядело бы работающей панелью.
            log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PanelLogTrayIconFailedFormat, ex.GetType().Name, ex.Message));
            panelWindow.HideOnClose = false;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            panelWindow.Show();
            windowShown = true;
        }

        selfTest?.Attach(tray: _tray, panelWindow, _shell, _server);

        // Часы баланса и пиков. В прогоне проверки они не запускаются: ключ владельца читать
        // в проверке нечего, и об этом контроллер сам пишет строку в журнал.
        _balance.Start();
        UpdateTrayTooltip();

        // Первый огонёк на значке — сразу после того, как значок встал: состояние сервера
        // к этому мгновению уже известно, и человек обязан увидеть его цветом, не открывая меню.
        UpdateTrayIcon();

        // Часы автокопий. Здесь же видно вторую красную линию: в прогоне проверки и в изолированном
        // прогоне копий по расписанию не бывает, и контроллер пишет об этом строку в журнал.
        _backups.Start();

        // АВТОПОДЪЁМ СЕРВЕРА — решение владельца 26.09.2026: 2.0 поднимает свой сервер сама, без
        // кнопки, иначе переезд с 1.x не имеет смысла. Идёт ПОСЛЕДНИМ и в ФОНЕ: `Start` ждёт ответа
        // сервера до 240 секунд, и на нитке интерфейса панель всё это время не отвечала бы.
        AutoStartServer(window, log, isolated, human);

        // Второй запуск ярлыка стучится в окно-приёмник — показываем окно тем же путём,
        // что и по щелчку по значку. Подписка снимается при выходе: замок общий на процесс,
        // и обработчик на нём не должен переживать панель.
        _signal = ProgramOptions.InstanceSignal;
        if (_signal is not null) _signal.ShowPanelRequested += _shell.ShowFromSignal;

        // ПАРОВОЗ ПОСЛЕДНИМ: «панель запущена — я в трее». Требование владельца 29.09.2026:
        // обычный запуск окна не показывает, и человек не понимает, работает панель или нет.
        // Решение — чистый предикат, а шарик идёт общей дверью изоляции (см. NotifyPanelStarted).
        NotifyPanelStarted(windowShown);
    }

    /// <summary>
    /// Сказать человеку, что панель запустилась и ушла в трей. <see cref="NoticeKind.PanelStarted"/>.
    ///
    /// ⚠️ Это ПЕРВЫЙ шарик, который показывается при обычном запуске БЕЗ просьбы человека.
    /// Прежнее правило было «шарик только когда есть что сказать по делу»; здесь оно нарушено
    /// сознательно, и потому названо в `DESIGN.md` п. 37 и в <see cref="NoticeKind.PanelStarted"/>.
    /// </summary>
    private void NotifyPanelStarted(bool windowShown)
    {
        if (!IsolationRules.ShouldAnnouncePanelStarted(windowShown, RunContext.Current.IsIsolated)) return;

        Notify(NoticeKind.PanelStarted, PanelStrings.NotifyPanelStartedTitle, PanelStrings.NotifyPanelStartedText);
    }

    /// <summary>
    /// Применить тему из ЛЮБОЙ нитки. Единственное место в панели, где живёт <c>Dispatcher</c>
    /// для темы: сам вызов может прийти из фона (встраивание в найденный сервер идёт в фоне,
    /// и оно записывает настройки), а <c>RequestedThemeVariant</c> — объект нитки интерфейса.
    /// Что именно произошло 26.09.2026 в 23:10:12 без этой двери — в <see cref="ThemeApply"/>.
    /// </summary>
    private void ApplyThemeFromAnyThread(PanelTheme theme) => ThemeApply.Apply(
        () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
        ApplyTheme,
        action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
        theme,

        // Сбой применения темы говорим СВОЕЙ строкой: настройки к этому мгновению уже записаны,
        // и «настройки не сохранились» было бы неправдой.
        error => _log(ThemeApply.FailedLogLine(error)));

    /// <summary>
    /// АВТОПОДЪЁМ СВОЕГО СЕРВЕРА при старте панели — решение владельца 26.09.2026.
    ///
    /// Две вещи здесь важнее всего остального:
    ///
    /// 1. **Право спрашивается ДО всякой работы** (<see cref="RunRights.MayAutoStartServer"/>).
    ///    Прогон проверки — в том числе <c>--shell-selftest</c>, который идёт ОБЫЧНЫМ путём
    ///    панели, — не поднимает НИЧЕГО: он работает на машине владельца, и его сервер помешал бы
    ///    человеку. У изолированного прогона права нет по той же причине плюс своя: его осиротевший
    ///    <c>node</c> держал бы занятый порт.
    /// 2. **Подъём идёт ПОСЛЕ первой разведки портов.** Иначе на машине с чужим работающим DSH
    ///    панель подняла бы второй движок на тех же данных — ровно та находка 25.09.2026, ради
    ///    которой поставлен замок.
    ///
    /// Идёт в ФОНЕ: <c>Start</c> ждёт ответа сервера до 240 с, а первый запуск в чистом профиле
    /// достраивается около минуты. В нитке интерфейса панель всё это время не отвечала бы.
    ///
    /// Отказ («рядом работает найденный DSH») контроллер выдаёт САМ и своими словами — здесь
    /// он не дублируется. Здесь называется только решение автоподъёма: поднимаю или пропускаю
    /// и почему.
    ///
    /// И третье, ради чего сюда приходит окно: **пока идёт подъём, человек должен ВИДЕТЬ, что
    /// панель работает.** Подъём держит замок контроллера, поэтому окно до его конца не обновляется,
    /// а первый запуск в чистом профиле достраивается около минуты — без этого человек минуту
    /// смотрел бы на «Сервер не запущен» и решил бы, что панель сломалась. Окно показывает то же,
    /// что при нажатии «Запустить», и снимает занятость в <c>finally</c> — чем бы подъём ни кончился.
    /// </summary>
    private void AutoStartServer(MainWindow window, Action<string> log, bool isolated, bool human)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!RunRights.MayAutoStartServer(RunContext.Current, human))
        {
            // Права нет — и об этом одной строкой в журнал: «панель не подняла сервер» обязано
            // быть объяснимо, иначе это выглядит как забывчивость.
            log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.AutoStartRefusedLogFormat,
                isolated ? PanelStrings.AutoStartRefusedIsolated : PanelStrings.AutoStartRefusedCheckRun));

            return;
        }

        // Контроллера может не быть вовсе — панель уже завершается: поднимать тогда нечего
        // и незачем. Ссылка берётся ЗДЕСЬ и больше не спрашивается: `Exit` обнуляет поле,
        // а фоновая задача живёт дольше.
        if (_server is not { } server) return;

        Task.Run(() =>
        {
            log(PanelStrings.AutoStartEnabledLog);

            // ПЕРВАЯ РАЗВЕДКА ПОРТОВ. Обязательна до подъёма: без неё панель не знает, что рядом
            // уже работает чужой DSH, и подняла бы второй движок на тех же данных.
            var found = server.Scan();
            var state = server.Refresh();

            if (!ServerDecisions.ShouldAutoStart(server.Owner, state.Presence, found.Any))
            {
                log(SkipReason(server.Owner, state, found));
                return;
            }

            log(PanelStrings.AutoStartStartingLog);

            // Занятость окна — ТОЛЬКО с нитки интерфейса: это состояние окна.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => window.BeginServerStart(PanelStrings.ServerStarting));

            try
            {
                server.Start(MainWindow.StartTimeout);
            }
            finally
            {
                // Чем бы подъём ни кончился — отказ, исключение, успех, — кнопки обязаны
                // разблокироваться: занятость, пережившая неудачу, оставила бы окно мёртвым.
                Avalonia.Threading.Dispatcher.UIThread.Post(window.EndServerStart);
            }
        });
    }

    /// <summary>
    /// Почему автоподъём пропущен — словами и по делу. Три повода, и человеку они говорят разное:
    /// сервер уже работает, панель уже чем-то управляет, рядом найден чужой DSH.
    /// </summary>
    private static string SkipReason(ServerOwner owner, ServerState state, DiscoveryResult found)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        if (owner != ServerOwner.None) return PanelStrings.AutoStartSkipManagedLog;

        if (found.Any) return string.Format(culture, PanelStrings.AutoStartSkipForeignFormat, found.First!.Value.Port);

        return string.Format(culture, PanelStrings.AutoStartSkipBusyFormat, state.Port);
    }

    // --- маленькие файлы состояния панели ----------------------------------
    //
    // Чтение и запись файлов, которые знает только это место: ссылки входа и записи «этот сервер
    // наш». Оба — секреты или почти секреты, поэтому ссылке сразу ставятся права «только владелец»
    // тем же способом, каким закрывается архив копии с ключами. Сами делегаты живут
    // в `Server\ServerStateFiles.cs`: пути вычисляются в ОДНОМ месте — у `AppPaths`.


    /// <summary>
    /// Поставить главное окно туда, где его оставил человек. Настройки читаются здесь, а окно
    /// только применяет готовый прямоугольник: иначе `--shot` (он строит окно напрямую) читал бы
    /// файл владельца.
    ///
    /// Экраны спрашиваются у самого окна: у Windows их несколько, они бывают и слева от нуля,
    /// и разного размера, — поэтому прямоугольник приводит к видимому <see cref="WindowPlacement"/>,
    /// а не «ставим как записали».
    /// </summary>
    private void RestorePlacement(MainWindow window)
    {
        if (_settings is null) return;

        var areas = window.Screens.All.Select(screen => screen.WorkingArea).ToArray();
        if (areas.Length == 0) return;

        var saved = _settings.Settings;
        var frame = WindowPlacement.Restore(
            saved.WindowX, saved.WindowY, saved.WindowWidth, saved.WindowHeight, areas);

        window.AttachPlacement(frame);
        _lastNormalPlacement = frame;
    }

    /// <summary>
    /// Запомнить размер и положение главного окна.
    ///
    /// Свёрнутое (и развёрнутое) окно геометрии не отдаёт — держим последнюю НОРМАЛЬНУЮ: иначе
    /// в файл уехало бы «свёрнуто», и следующая панель открылась бы свёрнутой в углу.
    ///
    /// Отказ хранилища панель не роняет и не врёт: строку отказа пишет владелец настроек
    /// (<c>SettingsLockedLog</c> в прогоне без права читать и писать), и исключений здесь нет.
    /// </summary>
    private void SavePlacement(MainWindow window)
    {
        if (_settings is null) return;

        if (window.WindowState == WindowState.Normal)
        {
            var frame = window.CurrentFrame();

            // Окно ещё не мерилось — запоминать нечего: нулевая геометрия в файле была бы
            // «окно в углу экрана», а не «не задано».
            if (frame.Width > 0 && frame.Height > 0) _lastNormalPlacement = frame;
        }

        var current = _lastNormalPlacement;
        if (current is null) return;

        _settings.SetWindowPlacement(current.Value.X, current.Value.Y, current.Value.Width, current.Value.Height);
    }

    /// <summary>
    /// Построить окно настроек. Оно же подключается к домену: настройки, автозапуск.
    /// Строится по просьбе (пункт меню или кнопка), а не при старте: панель живёт в трее
    /// месяцами, и держать окно, которое человек откроет раз в месяц, незачем.
    ///
    /// **Дверь в «О программе» стоит ЗДЕСЬ** (решение владельца 27.09.2026). Окно по-прежнему одно
    /// на всю панель: просьбу принимает связка и её слот (<see cref="PanelShell.OpenAbout"/>),
    /// поэтому дверей у него может быть сколько угодно, а окно остаётся одно.
    /// </summary>
    private Window CreateSettingsWindow()
    {
        var window = new SettingsWindow();

        // РАЗДЕЛУ «ОБНОВЛЕНИЕ» нужны те же два домена, что окну обновления: состояние выпусков
        // и движок установки. Дверью он больше не связан (она стала разделом списка слева),
        // поэтому обновление приходит прямо сюда — решение владельца 28.09.2026: «это будет
        // просто нормальный пункт меню как и остальные во вкладке настройки».
        window.Attach(
            _settings!,
            _autostart,
            _balance,
            _pricing,
            _update,
            _updateInstall,
            skipUpdate: version => _settings?.SetUpdateSkippedVersion(version) ?? false,
            exitForUpdate: () => _shell?.Exit());

        window.AboutRequested += () => _shell?.OpenAbout();
        return window;
    }

    /// <summary>
    /// Построить окно копий. Подключается к домену копий и, если панель связана, к серверу:
    /// сервер нужен окну ровно для одного вопроса — «он работает, погасить перед копией?»
    /// (решение владельца 26.09.2026). Гасит его окно по общему правилу: свой — свободно,
    /// найденный и взятый под управление — только по отдельному подтверждению каждый раз.
    /// </summary>
    private Window CreateBackupWindow()
    {
        var window = new BackupWindow();
        window.Attach(_backups!, _server);
        return window;
    }

    /// <summary>
    /// Построить окно «Пики и тарифы». Подключается к ДВУМ доменам, и оба ему нужны: у баланса
    /// он берёт окна пика активного агента и состояние тарифа, у контроллера цен — таблицу
    /// «Стоимость» и кнопку «Обновить информацию». Ни сервера, ни настроек, ни копий ему не нужно.
    ///
    /// Строится по просьбе (кнопка в главном окне) и попадает в свой слот связки
    /// (<see cref="PanelShell.OpenPeaks"/>) — поэтому второго такого окна не бывает, как
    /// у настроек, копий и «О программе».
    /// </summary>
    private Window CreatePeakWindow()
    {
        var window = new PeakWindow();
        window.Attach(_balance!, _pricing);

        // ИСТОРИЯ ЦЕН: просьбу поднимает кнопка «История» в этом окне (решение владельца
        // 28.09.2026 — история живёт ОТДЕЛЬНЫМ окном рядом с «Обновить информацию», а не
        // переключателем дат внутри «Пиков и тарифов»). Дверь та же, что у остальных окон: просьбу
        // принимает связка, окно строится по требованию и живёт в своём слоте — второго окна
        // истории не бывает, а окно пиков о нём ничего не знает.
        window.HistoryRequested += () => _shell?.OpenPricingHistory();

        return window;
    }

    /// <summary>
    /// Построить окно «История цен». Подключается к ОДНОМУ домену — контроллеру цен: история
    /// изменений принадлежит ему (он же её и записывает), поэтому окно и панель не могут
    /// разойтись в том, что было изменением.
    ///
    /// Строится по просьбе (кнопка «История» в окне «Пики и тарифы») и попадает в свой слот связки
    /// (<see cref="PanelShell.OpenPricingHistory"/>) — поэтому второго такого окна не бывает.
    /// </summary>
    private Window CreatePricingHistoryWindow()
    {
        var window = new PricingHistoryWindow();
        window.Attach(_pricing!);
        return window;
    }

    /// <summary>
    /// Построить окно «Обновление панели». Подключается к ДВУМ доменам, и оба ему нужны: у проверки
    /// выпусков он берёт состояние и кнопку «Проверить сейчас», у движка установки — подготовку
    /// замены и запуск сценария.
    ///
    /// **Три двери, и каждая ведёт в своё место:**
    /// * «запомнить пропуск» — владельцу настроек, тем же путём, что остальные одиночные решения
    ///   (второго места правды у `updateSkippedVersion` нет);
    /// * «открыть на GitHub» — окно открывает САМО, ровно по щелчку человека;
    /// * «обновить и перезапустить панель» — связке: только она умеет закрыть панель целиком
    ///   (<see cref="PanelShell.Exit"/>), и только после запуска сценария.
    ///
    /// ⚠️ Строится по просьбе (кнопка в шапке главного окна, пункт меню значка) и попадает в свой
    /// слот связки (<see cref="PanelShell.OpenUpdate"/>) — поэтому второго такого окна не бывает.
    /// </summary>
    private Window CreateUpdateWindow()
    {
        var window = new UpdateWindow();

        window.Attach(
            _update,
            _updateInstall,
            skip: version => _settings?.SetUpdateSkippedVersion(version) ?? false,
            exitRequested: () => _shell?.Exit());

        return window;
    }

    /// <summary>
    /// ЧЕМ КОНЧИЛОСЬ ПРОШЛОЕ ОБНОВЛЕНИЕ — сказать человеку при запуске панели.
    ///
    /// Три ответа, и молчание позволено ровно одному:
    ///
    /// * **применилось** — короткая строка в журнал (человеку говорить нечего: панель и так
    ///   обновилась). Тихая строка в журнале нужна потому, что «обновление прошло» — это факт,
    ///   и по нему потом разбирают, почему версия сменилась;
    /// * **не применилось или откатилось** — человеку СЛОВАМИ через общую дверь сообщений
    ///   (<see cref="Notify"/>): причина приходит ключом от ядра (`UpdateOutcome.ReasonKey`),
    ///   а фразу даёт словарь (`UpdateReasonLines`). Молчание здесь было бы худшим из ответов:
    ///   человек ждал обновления и не знает, что его нет;
    /// * **понять не удалось** — тоже словами: нечитаемое состояние панель молчанием не прячет
    ///   (таково решение ядра, названное в `UpdateOutcome`).
    ///
    /// ⚠️ Право читать и убирать состояние обновления — то же, что у движка установки
    /// (<see cref="UpdateInstall.For"/>): прогон проверки не трогает здесь ничего и получает
    /// <c>null</c>, то есть молчит и ничего не удаляет.
    /// </summary>
    private void ShowUpdateOutcome(AppPaths paths, bool human, Action<string> log)
    {
        var outcome = UpdateOutcomes.StartupNotice(
            paths,
            Views.AboutWindow.PanelVersion,
            UpdateInstall.For(RunContext.Current, human));

        if (outcome is null) return;

        log(UpdateOutcomeLines.JournalLine(outcome));

        // «Молчать или говорить» решает ЧИСТАЯ функция (`UpdateOutcomeLines.Notice`), а не условие
        // здесь: у Applied она пуста (человеку нечего сказать — панель и так обновилась),
        // у остальных называет и версию, и причину словами.
        var notice = UpdateOutcomeLines.Notice(outcome);

        if (notice.Length > 0) Notify(NoticeKind.UpdateOutcome, PanelStrings.UpdateNoticeTitle, notice);
    }

    /// <summary>
    /// Построить окно «О программе». Единственное окно панели, которому не нужно НИЧЕГО:
    /// ни контроллеров, ни настроек, ни данных человека — только строки текущего языка и
    /// ссылки продукта. Поэтому оно не может «не построиться» из-за недоступного домена,
    /// и поэтому же его построитель обязателен в <see cref="PanelShell"/>.
    /// </summary>
    private Window CreateAboutWindow()
    {
        var about = new AboutWindow();

        // Дверь в отчёт о проблеме даёт СВЯЗКА: отчёту нужны журнал панели и состояние сервера,
        // а «О программе» их не знает и знать не должно. Ссылка берётся через поле, а не напрямую:
        // к этому мгновению связка уже построена, но окно может быть построено и проверкой.
        about.AttachIssueDoor(() => _shell?.OpenIssue());

        return about;
    }

    /// <summary>
    /// ОКНО «СООБЩИТЬ О ПРОБЛЕМЕ»: собрать факты и отдать окну. Требование владельца 29.09.2026
    /// (DESIGN.md п. 38).
    ///
    /// ⚠️ **Здесь собирается РОВНО технический минимум, и каждая вещь названа:** версия панели
    /// и ревизия сборки, версия Windows, версии движка и Node, как панель запущена, состояние
    /// сервера, последние строки журнала. **Чего здесь нет и не будет:** ключа модели, баланса,
    /// ссылки входа, содержимого ~/.dsh, настроек человека, имён его копий. Их нет не потому,
    /// что «постарались не взять», а потому, что в сборке фактов их нет вовсе — и чистка
    /// (IssueReport.Clean) на них не рассчитывает.
    ///
    /// Журнал читается ПОД СВОИМ КОРОМ (RunContext.Current.Paths.LogFile): панель вообще
    /// не имеет способа прочитать чужой журнал, и это свойство изоляции, а не аккуратность.
    /// </summary>
    private Window CreateIssueWindow()
    {
        var report = _settings?.EnvironmentInfo();
        var window = new IssueWindow();

        window.Attach(new IssueFacts(
            PanelVersion: Views.AboutWindow.PanelVersion,
            Revision: Revision(),
            System: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            EngineVersion: Value(report, "EnvEngine"),
            NodeVersion: Value(report, "EnvNode"),
            LaunchMode: LaunchModeText(),
            ServerState: ServerStateText(),
            ServerPort: _server?.State.Port ?? 0,
            LogTail: LogTail(60)));

        return window;
    }

    /// <summary>Ревизия сборки: хеш коммита, который .NET дописал к <c>ProductVersion</c>.</summary>
    private static string Revision()
    {
        var informational = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(Views.AboutWindow).Assembly)?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return string.Empty;

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus + 1 >= informational.Length) return string.Empty;

        var hash = informational[(plus + 1)..].Trim();
        return hash.Length > 12 ? hash[..12] : hash;
    }

    /// <summary>
    /// Строка отчёта окружения по её члену <see cref="PanelStrings"/> — БЕЗ повтора слов в коде:
    /// у отчёта один источник, и второй список названий здесь разошёлся бы с ним.
    /// </summary>
    private static string Value(Settings.EnvironmentReport? report, string member)
    {
        if (report is null) return string.Empty;

        var what = typeof(PanelStrings).GetProperty(member)?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(what)) return string.Empty;

        foreach (var group in report.Groups)
        foreach (var line in group.Lines)
            if (string.Equals(line.What, what, StringComparison.Ordinal) && line.Result.Length > 0)
                return line.Result;

        return string.Empty;
    }

    /// <summary>Как панель запущена — словами, теми же, что в остальных объяснениях.</summary>
    private static string LaunchModeText()
    {
        var context = RunContext.Current;

        if (context.IsIsolated) return PanelStrings.EnvLaunchIsolated;
        if (ProgramOptions.HumanLaunch) return PanelStrings.EnvLaunchHuman;

        return PanelStrings.EnvLaunchCheck;
    }

    /// <summary>
    /// Состояние сервера словами — ТОЙ ЖЕ строкой, что в меню значка (TrayStatus.Build), а не своей.
    /// Второй формулировки одного состояния проекту не нужно: разойдись они, отчёт говорил бы
    /// «работает», а меню «поднят не панелью», и человек не знал бы, чему верить.
    ///
    /// ⚠️ Первая редакция этого метода ЛЕЗЛА В ServerPresence СВОИМИ ИМЕНАМИ (Own, Foreign, Busy),
    /// которых в перечислении нет вовсе, — сборка не прошла. Это ровно тот случай, ради которого
    /// строку надо брать у того, кто её уже умеет строить, а не собирать второй раз.
    /// </summary>
    private string ServerStateText()
    {
        return TrayStatusNow().Server.Text;
    }

    /// <summary>
    /// Хвост журнала панели. Читается терпимо: нет файла или не прочитался — пустой хвост,
    /// и отчёт честно покажет пустой раздел вместо падения окна.
    /// Чистит его <see cref="Issue.IssueReport"/> — здесь ни одна строка не правится, иначе
    /// чистка жила бы в двух местах.
    /// </summary>
    private static IReadOnlyList<string> LogTail(int lineCount)
    {
        try
        {
            var path = RunContext.Current.Paths.LogFile;
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = File.ReadAllLines(path);
            return lines.Length <= lineCount ? lines : lines[(lines.Length - lineCount)..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Подсказка значка: агент, баланс, тариф. Значок — единственное, что человек видит,
    /// не открывая окна, поэтому баланс и пик попадают именно сюда.
    /// </summary>
    private void UpdateTrayTooltip()
    {
        if (_tray is null || _balance is null) return;

        _tray.SetToolTip(TrayTooltip.Build(
            _balance.Agent.Title,
            BalanceSummary(),
            _balance.PeakText));
    }

    /// <summary>
    /// Сумма баланса — или пусто, если её сейчас нет. ОДНО место на подсказку значка и на меню:
    /// две выемки одного значения однажды разошлись бы, и подсказка говорила бы «нет данных»,
    /// когда в меню стоит сумма.
    /// </summary>
    private string BalanceSummary()
    {
        var balance = _balance;

        return balance is not null && balance.Result.Ok && balance.Result.Available
            ? balance.Result.Summary
            : string.Empty;
    }

    /// <summary>
    /// Четыре строки состояния для меню значка — СВЕЖИЕ, на момент показа меню (см.
    /// <see cref="PanelShell"/> и <c>TrayIconHost.SetMenuProvider</c>).
    ///
    /// ⚠️ Порт берётся у СОСТОЯНИЯ (<c>State.Port</c>), а не у <c>ServerController.Port</c>:
    /// то свойство берёт замок контроллера, а его держит подъём сервера — до 240 секунд.
    /// Меню, открытое сразу после нажатия «Запустить», замерло бы на это время.
    ///
    /// ⚠️ Строка обновления приходит ГОТОВОЙ от контроллера: там лежит состояние проверки,
    /// и второе решение о «новее или нет» здесь заводить нельзя. Контроллера нет (окно панели
    /// без домена обновления — так его видят проверки) — строка честно говорит «ещё не проверяли».
    /// </summary>
    private TrayStatusLines TrayStatusNow()
    {
        var server = _server;
        var update = _update;

        return TrayStatus.Build(
            connected: server is not null,
            presence: server?.State.Presence ?? ServerPresence.Stopped,
            owner: server?.Owner ?? ServerOwner.None,
            port: server?.State.Port ?? 0,
            agentTitle: _balance?.Agent.Title ?? string.Empty,
            balanceSummary: BalanceSummary(),
            inPeak: _balance?.Peak.InPeak ?? false,
            update: update is not null
                ? update.TrayLine
                : new TrayStatusLine(PanelStrings.UpdateNeverChecked, TrayTone.Neutral));
    }

    /// <summary>
    /// Тон огонька на значке в трее — по тому же состоянию сервера, что и строка в меню
    /// (решение владельца 27.09.2026: зелёный, когда сервер отвечает; красный, когда не работает;
    /// серый, когда панель сервера не знает вовсе).
    ///
    /// **Оболочка не дёргается зря.** Состояние перечитывается раз в секунду (опрос в
    /// <c>MainWindow</c>), а значок пересобирается только когда тон СМЕНИЛСЯ: повторный
    /// <see cref="TrayIconHost.SetIconTone"/> с тем же тоном не трогает ни Windows, ни дескриптор,
    /// и значок от этого не мигает. Повторение этой двери здесь — не украшение: именно она отличает
    /// «обновляем состояние» от «каждую секунду шлём оболочке новую иконку».
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_tray is null) return;

        var server = _server;
        var tone = TrayStatus.IconTone(connected: server is not null, presence: server?.State.Presence ?? ServerPresence.Stopped);

        var line = _tray.SetIconTone(tone);
        if (line.Length > 0) _log(line);
    }

    /// <summary>
    /// Показать автоматическое сообщение. Решение принимает ОБЩАЯ дверь изоляции
    /// (<see cref="IsolationRules.ShouldNotify"/>), и она одна на все виды: подавленное решение
    /// уходит строкой в журнал, иначе «панель молчала» ничем не объяснить.
    /// </summary>
    private void Notify(NoticeKind kind, string title, string text)
    {
        if (!IsolationRules.ShouldNotify(kind, RunContext.Current.IsIsolated))
        {
            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.NotifySuppressedLogFormat, kind));
            return;
        }

        _log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.NotifyShownLogFormat, title));

        _tray?.ShowNotification(title, text);
    }
}

/// <summary>
/// Переключатели, которыми проверки отделяют себя от обычного запуска человеком.
/// Вынесены отдельно, чтобы не разбрасывать статические поля по классам.
/// </summary>
internal static class ProgramOptions
{
    /// <summary>
    /// Не строить значок и окно общим путём: их строит сам режим проверки
    /// (<c>--tray-selftest</c>), и второй значок ему мешал бы. Обычный запуск этого значения
    /// не выставляет — общий путь остаётся единственным у панели, которую открыл человек.
    /// </summary>
    public static bool SkipDefaultUi { get; set; }

    /// <summary>
    /// Прогон самотеста связки: панель строится ОБЫЧНЫМ путём (иначе проверялось бы не то,
    /// что видит человек), а проверка подключается к ней и распоряжается ею.
    /// </summary>
    public static PanelShellSelfTest? ShellSelfTest { get; set; }

    /// <summary>
    /// Замок единого экземпляра с окном-приёмником. Его ставит только обычный запуск: режимы
    /// проверок и изолированные прогоны его не трогают — у них нет ни второй панели, ни человека,
    /// которому надо показать окно.
    /// </summary>
    public static InstanceSignal? InstanceSignal { get; set; }

    /// <summary>
    /// ЭТО обычный запуск панели человеком — единственный, кому позволено трогать машинное
    /// окружение: менять запись автозапуска (<c>HKCU\...\Run</c>) и читать настройки ПРЕЖНЕЙ
    /// панели (оттуда берётся предложение рабочей папки).
    ///
    /// Значение по умолчанию — «нельзя», и это главное в нём. В v1 признак был обратным:
    /// «это прогон проверки» вычислялось из подмен переменных окружения, и проверка замены файлов
    /// однажды перевела боевую запись владельца на свою временную копию — после перезагрузки
    /// поднялась бы она, папку удалили бы, и автозапуск молча перестал бы работать.
    /// Право выдаётся ЯВНО и ровно в одном месте — <c>Program.Main</c> перед обычным запуском.
    /// Забыть его выдать безопасно (панель просто ничего не меняет); забыть запретить было бы
    /// опасно.
    /// </summary>
    public static bool HumanLaunch { get; set; }
}
