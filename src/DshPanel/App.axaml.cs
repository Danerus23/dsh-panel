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
    /// Раздел «Обновление движка» (этап E3.2). Строится в СТОРОНЕ от нитки окна: его двери читают
    /// сеть (подсказки и реестр npm), а окно настроек не имеет права открываться через минуту
    /// ожидания. Нет дверей (прогон проверки, изоляция) — поле остаётся пустым или «пустым» сервисом,
    /// и раздел говорит это словами.
    /// </summary>
    private Shell.IEngineUpdateSection? _engineUpdate;

    /// <summary>
    /// Про какую версию движка панель уже сказала шариком ЗА ЭТОТ ЗАПУСК: второй раз о том же
    /// не пристаём (правило «один раз на версию» — как у обновления самой панели).
    /// </summary>
    private string _engineAnnounced = string.Empty;

    /// <summary>
    /// ДВИЖОК УСТАНОВКИ обновления — то, чем окно готовит замену файлов и запускает сценарий.
    /// Живёт рядом с проверкой и строится тем же запуском: у обоих один предмет (выпуск панели),
    /// и разводить их по разным местам значило бы искать право готовить замену в двух местах.
    /// </summary>
    private UpdateInstallController? _updateInstall;

    /// <summary>
    /// СОСТОЯНИЕ ПОДГОТОВКИ ОБНОВЛЕНИЯ — ОДИН ОБЪЕКТ НА ПРОЦЕСС (DEF-058, 2.4.2).
    ///
    /// **Почему здесь, а не в окне.** Подготовка переживает окно: человек запускает её из настроек,
    /// закрывает их, а загрузка идёт (и шарик о ней говорит). Поля окна умирали вместе с ним, и
    /// новое окно показывало «ничего не идёт» и разрешало вторую подготовку в те же файлы.
    /// Время жизни этого объекта — панель, как у движка установки, и создаётся он рядом с ним.
    /// Окно получает объект ДОВОДОМ (<c>SettingsWindow.Attach</c>) и только показывает.
    /// </summary>
    private PanelUpdateState? _updateState;

    private BackupController? _backups;
    private TrayIconHost? _tray;

    /// <summary>
    /// ДВЕРЬ ЩЕЛЧКА ПО ШАРИКУ (DEF-046, 2.4.0). Живёт рядом со значком и помнит, о чём было
    /// ПОКАЗАННОЕ сообщение: щелчок открывает то окно, где этот факт виден человеку. Подписка
    /// одна на процесс — второго пути к шарику в панели нет (общая дверь <see cref="Notify"/>).
    /// </summary>
    private NoticeClickDoor? _noticeClicks;

    private InstanceSignal? _signal;

    /// <summary>
    /// КАЛИТКА ТЕЛЕФОНА (этап F1, кусок «слушатель»). Заводится ЗДЕСЬ и только по праву
    /// <see cref="RunRights.MayOpenPhoneGate"/> — обычным запуском панели человеком, который сам
    /// включил телефон. Прогон проверки и изолированный прогон калитку НЕ ПОДНИМАЮТ: это следствие
    /// права, а не отдельная проверка «а не прогон ли это». Право спрашивается дважды — здесь и
    /// внутри <see cref="PhoneGate.Start"/>, — потому что цена ошибки здесь выше обычной: открытый
    /// в сеть порт на машине владельца.
    /// </summary>
    private PhoneGate? _phoneGate;

    /// <summary>
    /// ДВЕРЬ БРАНДМАУЭРА (этап F4, кусок 1). Заводится ЗДЕСЬ и только по праву
    /// <see cref="RunRights.MayChangeFirewall"/> — обычным запуском панели человеком.
    ///
    /// ⚠️ **Это ЕДИНСТВЕННОЕ место, где создаётся настоящая дверь** (`ElevatedFirewallDoor`),
    /// и это стережёт проверка по исходникам: у прогона проверки двери нет вовсе, поэтому правило
    /// брандмауэра не может ни появиться, ни исчезнуть от прогона. Право спрашивается дважды —
    /// здесь и внутри изменения (<see cref="ElevatedFirewallDoor"/>), — потому что цена ошибки
    /// здесь выше обычной: разрешение, которое получает входящее соединение из сети.
    ///
    /// ⚠️ **Ничего не делается при старте.** Дверь — это только возможность: правило ставится
    /// и снимается ЩЕЛЧКОМ человека в разделе «Телефон», и ни при открытии калитки, ни при запуске
    /// панели панель к брандмауэру не лезет.
    /// </summary>
    private IFirewallDoor? _firewall;

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
        // Нитка интерфейса запоминается ПЕРВЫМ делом: дальше панель берёт диспетчер только
        // через дверь (`Shell\UiThread`), а дверь обязана знать, чья нитка интерфейс. Спросить
        // её здесь можно: `Initialize` идёт на нитке платформы, и диспетчер у неё уже есть.
        UiThread.Remember();

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
                // значок ему мешал бы. Живая проба владения (`--windows-selftest`) строит
                // СВОИ настоящие окна здесь же: до подъёма платформы окно не создаётся.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                ProgramOptions.WindowsSelfTest?.Run(desktop);
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
        // Второй раз, и не для перестраховки: `Initialize` могла пройти до того, как платформа
        // обзавелась диспетчером, и тогда запоминать было нечего. Здесь панель уже живёт на
        // своей нитке интерфейса — это её последний и верный случай.
        UiThread.Remember();

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
            locateEngine: () => OwnEngine.Locate(paths),
            () => _server,
            ApplyThemeFromAnyThread,
            log);

        // ЖИВАЯ ПРОБА УМЕНИЙ ДЛЯ ОТЧЁТА ОБ ОКРУЖЕНИИ (задание владельца 14.10.2026, §2) — только
        // у обычного запуска человеком: прогон проверки сети не трогает вовсе, и дверь остаётся
        // умолчанием «пробы не было».
        //
        // ⚠️ Проба ходит в сеть и зовёт движок по всем НУЖНЫМ именам (77), поэтому она ИДЁТ В ФОНЕ и не чаще,
        // чем один раз на свой сервер (`LiveSkillProbe` помнит пару «порт + процесс»). Отчёт
        // читает последние ГОТОВЫЕ факты и называет их момент — так «проверено здесь и сейчас»
        // не превращается в «панель замерла на секунды».
        if (human && !isolated)
        {
            var settings = _settings;

            if (settings is not null)
            {
                var skillProbe = new LiveSkillProbe(
                    paths,
                    () => settings.SkillTargetNow(),
                    () => OwnEngine.Locate(paths)?.BinPath,
                    log);

                settings.SkillProbe = skillProbe.Door;
            }
        }

        // ПОДСКАЗКИ О ВЕРСИЯХ ДВИЖКА (этап E2) — ОДИН раз в фоне и только у обычного запуска
        // человеком. Файл публичный и читается без входа; неудача — «не проверялась», и панель
        // работает: подсказка не имеет права ничего ронять. В прогоне проверки сети нет вовсе.
        if (human && !isolated)
        {
            Task.Run(() =>
            {
                var fetch = new HttpEngineHintsClient(ProductLinks.EngineHintsUrl).Read();

                if (!fetch.Ok)
                {
                    log(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        PanelStrings.EngineHintsUnavailableLog, fetch.Detail));
                    return;
                }

                var hints = EngineHints.Parse(fetch.Json);
                _settings.Hints = hints;

                log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.EngineHintsLoadedLog, hints.Versions.Count));
            });
        }

        // РАЗДЕЛ «ОБНОВЛЕНИЕ ДВИЖКА» (этап E3.2) — дверями панели: контроллер сервера, пути
        // и подсказки. Строится В СТОРОНЕ от нитки окна: двери читают сеть (подсказки и реестр npm).
        // Право на это — `LocalData` (свои данные панели): у прогона проверки его нет, и раздел
        // честно говорит словами, что обновление здесь недоступно, а в сеть не ходит вовсе.
        var engineUpdateHints = () => _settings?.Hints ?? EngineHints.Empty;

        Func<EngineHints> readEngineHints = () =>
        {
            // Подсказки уже прочитаны при старте — второй раз в сеть не идём. Читаем только если
            // их нет вовсе (старт не успел или прогон без сети): тогда это честная попытка.
            if (engineUpdateHints().Versions.Count > 0) return engineUpdateHints();

            var fetch = new HttpEngineHintsClient(ProductLinks.EngineHintsUrl).Read();

            if (!fetch.Ok)
            {
                log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.EngineHintsUnavailableLog, fetch.Detail));
                return EngineHints.Empty;
            }

            var hints = EngineHints.Parse(fetch.Json);
            if (_settings is not null) _settings.Hints = hints;

            return hints;
        };

        Task.Run(() =>
        {
            _engineUpdate = RunRights.LocalData(RunContext.Current, human)
                ? new EngineUpdateService(
                    new EngineUpdateSectionDoors(
                        ReadHints: readEngineHints,
                        ReadFresh: () => new HttpEngineRegistryClient().Latest().Version,
                        OwnPort: () => _server?.Port ?? 0,
                        Run: EngineUpdateRealDoors.Update(paths, () => _server, engineUpdateHints, log))
                    {
                        // ЗАМОК — ВСТРОЕННЫЙ РЕСУРС СБОРКИ, и он один решает, какую версию панель
                        // вообще умеет поставить: свежая без замка не ставится, и окно говорит это
                        // словами ДО нажатия, а не подменяет её проверенной.
                        HasLockFor = version => EngineInstallDecisions.HasLockForVersion(EngineTreeLocks.Load(), version),

                        // ОТКАТ ПО КНОПКЕ (E3.3): что стоит сейчас, какие папки версий есть и чем
                        // вернуться. Папки читает уборка версий — она же держит текущую и прежнюю.
                        ReadRecord = () => EngineInstallRecord.Read(paths.EngineStateFile),
                        ReadFolders = () => EngineRetention.Installed(paths.EngineDir),

                        // ЧТО СТОИТ СЕЙЧАС — своя версия сервера DSH, из ТОЙ ЖЕ двери, что у шарика
                        // о новой версии (`EngineUpdateNotice.Installed`): второго способа узнать
                        // свою версию в панели быть не должно, иначе карточка и шарик однажды
                        // показали бы разные номера.
                        ReadInstalled = () => EngineUpdateNotice.Installed(paths),

                        // СЕРВЕР DSH ЕСТЬ НА МАШИНЕ ВООБЩЕ — ОТДЕЛЬНЫЙ ФАКТ (A5 §7). Именно он,
                        // а не пустая строка версии, отвечает на «движка нет вовсе»: пустая версия
                        // значит и «нет», и «не прочитали», и на чистой машине карточка раньше
                        // не показывала ни одной кнопки — дорога к установке была тупиком.
                        ReadPresent = () => OwnEngine.Locate(paths) is not null,
                        RollbackDoors = EngineUpdateRealDoors.Rollback(paths, () => _server, log),

                        // ЧИСЛА ЖУРНАЛА С ДИСКА (блок C плана 2.2.0): сколько сессий и в каком они
                        // формате — `v3` или `v4`. Читаются ТОЛЬКО ИМЕНА файлов (`session.vN…`),
                        // содержимое сессий не открывается вовсе. Из этих чисел панель говорит,
                        // сколько сессий прежний движок не увидит после отката: сам движок об этом
                        // молчит (замер `l2`: 197 → 196 без поля и без слова).
                        ReadJournal = () => EngineJournalFormat.Read(EngineJournalFormat.SessionsDir(paths.DshHome)),
                    },
                    log)
                : new EngineUpdateService(EngineUpdateSectionDoors.None, log);

            // ШАРИК О НОВОЙ ВЕРСИИ ДВИЖКА — «авто-проверка как у панели» (пункт 7 плана E3.2).
            // Решение чистое (`EngineUpdateNotice`: рекомендованная новее поставленной и про неё
            // ещё не говорили), а показывает ОБЩАЯ дверь панели — она же молчит в прогоне проверки.
            if (_engineUpdate is null) return;

            var installed = EngineUpdateNotice.Installed(paths);
            var recommended = _engineUpdate.Recommended;

            if (!EngineUpdateNotice.ShouldAnnounce(installed, recommended, _engineAnnounced)) return;

            _engineAnnounced = recommended;

            Notify(
                NoticeKind.UpdateAvailable,
                PanelStrings.NotifyEngineUpdateTitle,
                EngineUpdateNotice.Message(recommended));
        });

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

        // УБОРКА МЁРТВЫХ ЗАМКОВ ДВИЖКА — тоже право, и то же, что у файлов состояния панели:
        // это УДАЛЕНИЕ ЧУЖОГО ФАЙЛА в доме движка. Убитый владелец оставляет замок навсегда,
        // и поднятый движок упрётся в него первой же записью (замер 10.10.2026), поэтому обычный
        // запуск человеком и изолированный прогон убирают мёртвые замки, а прогон проверки
        // на машине владельца не трогает ничего. Живой замок не трогается НИКАКИМ правом.
        var mayClearEngineLocks = RunRights.MayClearEngineLocks(RunContext.Current, human);

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

            // ПРЕДУПРЕЖДЕНИЕ ПЕРЕД ГАШЕНИЕМ (решение владельца 10.10.2026): панель называет,
            // какой сервер останавливает и что прервётся. Показ идёт общей дверью `Notify`,
            // поэтому изолированный прогон об этом молчит, как и о любом своём сообщении.
            notify: Notify,

            // Своя папка движка — НЕ право на файлы состояния, а факт машины: путь к своей папке
            // движка не секрет и не чужие данные (этап E3). Поэтому он приходит отдельным доводом.
            ownEngineDir: () => paths.EngineDir,

            // КАКОЙ ДВИЖОК ПОДНИМАТЬ (пункт 7 плана E3.2): сначала СВОЙ поставленный (из записи
            // `state\engine.json`), и только потом общая установка человека. Без этого обновление
            // ставило бы версию, которую панель тут же перестала бы использовать.
            locateEngine: () => OwnEngine.Locate(paths),

            // Запасной источник ссылки входа — файл панели 1.x, ТОЛЬКО ЧТЕНИЕ. Своей ссылки
            // у найденного сервера нет, а человеку, взявшему его под управление, кнопка нужна.
            // Чужой каталог читает только обычный запуск панели человеком: право то же, что
            // у машинного окружения, и в прогоне проверки источник молчит.
            foundServerEntryLink: RunRights.MachineData(RunContext.Current, human)
                ? () => ServerStateFiles.ReadText(AppPaths.V1.EntryLinkFile)
                : null,
            mayClearEngineLocks: mayClearEngineLocks);

        window.Attach(_server);
        window.SettingsRequested += () => _shell?.OpenSettings();

        // СВОЙ СЕРВЕР УПАЛ САМ — ТРИ КАНАЛА (DEF-052/053, 2.4.0): журнал пишет контроллер
        // (`EngineExitReport`), событие — `Crashed`, а шарик показывает этот подписчик.
        // ⚠️ НИ КНОПКИ, НИ АВТО-ПОДЪЁМА: панель сообщает о падении и ждёт человека (решение
        // владельца). Второй канал из этого подписчика не делается — здесь только показ.
        _server.Crashed += crash => Notify(
            NoticeKind.ServerCrashed,
            PanelStrings.NotifyServerCrashedTitle,
            ServerCrashDecisions.NoticeText(crash));

        // ДВЕРЬ БРАНДМАУЭРА (F4). Заводится ПЕРВОЙ, и это не порядок ради порядка: узкое правило
        // решает, МОЖНО ли поднимать калитку (уточнение владельца 11.10.2026 — «правило ДО двери»:
        // без него Windows задаёт свой вопрос и открывает ШИРОКИЕ правила, что владелец и увидел
        // живьём). Сама дверь ничего не делает: она только даёт разделу «Телефон» поставить или
        // снять своё узкое правило по щелчку человека. Прогон проверки её не получает вовсе —
        // и тогда раздел честно говорит, что брандмауэром тут не управляют.
        //
        // ⚠️ Путь к самой панели берётся у процесса (`Environment.ProcessPath`) — правило называет
        // ИМЕННО ЭТУ программу, а не «любую». Пустого пути не бывает у запущенного exe; если бы
        // случился, дверь отказала бы словами, а не поставила правило «всем».
        if (RunRights.MayChangeFirewall(RunContext.Current, human))
        {
            _firewall = new ElevatedFirewallDoor(
                Environment.ProcessPath ?? string.Empty,
                human,
                RunContext.Current,
                log);
        }
        else
        {
            log(Localization.Loc.T(PanelStrings.FirewallNoRightLog));
        }

        // КАЛИТКА ТЕЛЕФОНА (этап F1; уточнено 11.10.2026 после живого прогона). Двери у неё
        // НАСТОЯЩИЕ и собраны в одном месте (`PhoneGateRealDoors`): движок по ссылке входа, его
        // поток событий, файл пропуска с правами владельца, часы и журнал. Порт — из настроек
        // (умолчание `43127`), адрес — ТОТ ЖЕ, что показывает раздел: считается одним решением
        // (`PhoneDecisions.GateAddress`) в момент запуска, а не при сборке дверей.
        //
        // ⚠️ ВЛАДЕНИЕ И СЛУШАНИЕ РАЗВЕДЕНЫ. Калитка заводится по праву владения
        // (`MayOwnPhoneGate`) — то есть И ПРИ ВЫКЛЮЧЕННОМ ТЕЛЕФОНЕ: иначе щелчок рубильника не мог
        // бы её поднять, а код сопряжения — выпуститься. Слушает она только по `Start()`, и здесь
        // он зовётся лишь тогда, когда телефон включён И узкое правило брандмауэра уже стоит.
        // Прогон проверки не владеет ею НИКОГДА, и в журнал уходит строка отказа. Ключ
        // `--phone-selftest` (F1.e) соберёт свою калитку на своём свободном порту.
        var phoneEnabled = _settings!.Settings.PhoneEnabled;

        if (RunRights.MayOwnPhoneGate(RunContext.Current, human))
        {
            _phoneGate = CreatePhoneGate(
                RunContext.Current,
                human,
                PhoneGateRealDoors.Build(
                    paths,
                    () => _server?.EntryLink ?? string.Empty,
                    () => PhoneDecisions.GateAddress(
                        NetworkAddresses.Collect(RealNetworkAddresses.Read()),
                        _settings!.Settings.PhoneChosenAddress),
                    _settings!.Settings.PhonePort,
                    address => RememberPhoneAddress(_settings!, address),
                    log,

                    // ДВЕРЬ СЕРВЕРА — ЧТОБЫ КАЛИТКА ЗНАЛА, ПРОЧИТАНО ЛИ ЕГО СОСТОЯНИЕ, И СПРОСИЛА
                    // ПОДПИСКУ ЗАНОВО, КОГДА ОНО СТАНЕТ ИЗВЕСТНО (задание владельца 14.10.2026, §1).
                    // Калитка спрашивает подписку при запуске — раньше, чем панель прочитает
                    // состояние, — и прежняя редакция писала в журнал «связи с сервером DSH нет»
                    // про работающий СВОЙ сервер, а следующая попытка шла через пять секунд.
                    server: () => _server),
                () => _settings!.Settings.PhoneEnabled,
                log);

            if (_phoneGate is not null && phoneEnabled && FirewallLetsGateOpen())
            {
                var gateStart = _phoneGate.Start();

                // УДАЧА сказана самой калиткой (одной строкой в журнал). Здесь говорится только
                // НЕУДАЧА: «дверь не открылась» человек обязан увидеть, иначе выключенный телефон
                // выглядел бы работающим.
                if (!gateStart.Opened)
                {
                    log(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        PanelStrings.PhoneGateRefusedLogFormat, gateStart.Text));
                }
            }
            else if (phoneEnabled)
            {
                // «Телефон разрешён, а дверь не открыта» — сказано словами И в журнале: причина
                // у этого состояния одна и она исполнимая (поставить узкое правило щелчком).
                log(Localization.Loc.T(PanelStrings.PhoneGateBlockedByFirewall));
            }
        }
        else
        {
            log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PhoneGateRefusedLogFormat,
                Localization.Loc.T(PanelStrings.PhoneGateNoRightLog)));
        }

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

        // ОПИСЬ ТОГО, ЧТО ПАНЕЛЬ ПОСТАВИЛА НА МАШИНУ (этап D плана, п. 3): каталоги, файлы, ветка
        // реестра, ярлыки. Нужна будущему удалению панели — чтобы снималось ПОСТАВЛЕННОЕ, а не
        // «как помнится». Пишется только по праву на свои данные: прогон проверки файла владельца
        // не трогает, изолированный пишет свою опись под своим корнем. Неудача описи запуск
        // не роняет — она называет себя строкой в журнале (причина — ТИП ошибки, без пути).
        if (fileRights)
        {
            // Движок и свой Node попадают в опись из ЗАПИСИ «что стоит сейчас», а не из имён папок:
            // папку мог завести человек, а системный Node — не наш и в описи появляться не должен.
            if (InstalledInventory.Write(
                    paths, AutostartStores.SelfExe(), _autostart.State.Enabled, out var inventoryReason,
                    out var inventoryEntries, exists: null, engine: EngineInstallRecord.Read(paths.EngineStateFile)))
            {
                log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.InstalledInventoryWrittenLog, inventoryEntries));
            }
            else
            {
                log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.InstalledInventoryFailedFormat, inventoryReason));
            }
        }

        // УМЕНИЯ ДЛЯ ГЕЙТА ПО УМЕНИЯМ (этап E2). Здесь берутся ТОЛЬКО ФАЙЛОВЫЕ умения (ключ модели,
        // раскладка профиля) и только по праву на свои данные: живой пробы тут нет намеренно —
        // её зовут телефон и обновление движка, которым нужны вызовы, а не значок и кнопки.
        // Проверка идёт в момент, когда человек зовёт функцию, а не при старте панели.
        var skillFacts = fileRights ? EngineSkillProbe.None(paths) : new SkillFacts();

        string SkillBlock(PanelFunction function) =>
            SkillGate.Decide(function, EngineSkills.Measure(skillFacts)).Text();

        // Баланс и окна пика АКТИВНОГО агента. Ключ читается в момент запроса из файла ключей
        // движка, и только если прогон имеет на это право: в прогоне проверки контроллер
        // не тронет ни файла, ни сети (`RunRights.LocalData`).
        _balance = new BalanceController(
            new HttpBalanceClient(),
            paths.CredentialsPath,
            () => _settings!.Settings,
            allowed: RunRights.LocalData(RunContext.Current, human),
            clock: () => DateTimeOffset.Now,
            dispatch: action => UiThread.Post(action),
            log: log,
            notify: Notify,
            isolatedRun: isolated,
            skillsBlock: () => SkillBlock(PanelFunction.Balance));

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
            dispatch: action => UiThread.Post(action),
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
            () => AboutInfo.PanelVersion,
            allowed: RunRights.LocalData(RunContext.Current, human),
            clock: () => DateTimeOffset.Now,
            dispatch: action => UiThread.Post(action),
            log: log,
            remember: traces => _settings!.SetUpdateChecked(traces),
            rememberCheckedAt: stamp => _settings!.SetUpdateCheckedAt(stamp),
            isolatedRun: isolated,
            // ⚠️ ПОПЫТКА ПИШЕТСЯ В ФАЙЛ, А НЕ ТОЛЬКО В ПАМЯТЬ: отметка удачной проверки при отказе
            // не ставится (иначе панель соврала бы «проверено» и заперла проверку на сутки), поэтому
            // память о попытке — единственное, на чём держится отступ после отказа по лимиту GitHub.
            // Жила бы она в памяти — следующий запуск панели снова пошёл бы в сеть.
            rememberAttempt: attempt => _settings!.SetUpdateAttempt(attempt));

        _update.Start();

        // ДВИЖОК УСТАНОВКИ — то, чем РАЗДЕЛ «Обновление» настроек готовит замену файлов
        // (другого места у неё нет: отдельное окно обновления убрано 09.10.2026). Право готовить
        // приходит ИЗ ОДНОГО МЕСТА (`UpdateInstall.For`, тот же расчёт, что у копий): изолированный
        // прогон (там всё под своим корнем) или обычный запуск человеком. Прогон проверки без корня
        // замену файлов не готовит вовсе — и это не «постараемся», а отказ движка.
        //
        // Папка панели, которую заменит сценарий, — папка ЭТОГО exe: второй поиск «где лежит панель»
        // однажды нашёл бы не ту папку, а заменять чужие файлы панель не имеет права.
        // СНАЧАЛА СОСТОЯНИЕ, ПОТОМ ДВИЖОК: движку и окнам нужен ОДИН объект на процесс (DEF-058).
        _updateState = new PanelUpdateState();

        _updateInstall = new UpdateInstallController(
            paths,
            () => AboutInfo.PanelVersion,
            target: System.IO.Path.GetDirectoryName(AutostartStores.SelfExe()) ?? string.Empty,
            allowed: UpdateInstall.For(RunContext.Current, human),
            startArgument: string.Empty,
            mutexName: InstanceSignal.DefaultMutexName,
            log: log);

        // ОДИН ШАРИК НА ПОПЫТКУ ОБНОВЛЕНИЯ (решение капитана 24.10.2026 по u2, DEF-046):
        // «идёт скачивание», без чисел и полосы — они живут в разделе «Обновление», и щелчок
        // по шарику ведёт туда (карта видов — `NoticeTargets`). Шарик идёт общей дверью
        // (`Notify`) и потому молчит в прогоне по изоляции.
        _updateInstall.Preparing += () => Notify(
            NoticeKind.UpdatePreparing,
            PanelStrings.NotifyUpdatePreparingTitle,
            PanelStrings.NotifyUpdatePreparingText);

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

            // ФАЙЛЫ МАШИННОЙ УСТАНОВКИ ДВИЖКА — только по праву (находка C1 CH-08): у человека
            // право есть (его копия должна быть самодостаточной — критерий этапа 4), у прогона
            // проверки и у изолированного прогона нет, и их копия собирается без машинного движка
            // с НАЗВАННЫМ отказом.
            mayTakeMachineEngineData: RunRights.MachineEngineData(RunContext.Current, human),

            locateEngine: () => OwnEngine.Locate(paths),
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
            dispatch: action => UiThread.Post(action));

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

        // ДВЕРЬ В ОБНОВЛЕНИЕ ПЕРЕЕХАЛА В НАСТРОЙКИ (замечание владельца 28.09.2026: кнопку
        // обновления с главной панели убрать, а в настройках завести отдельный пункт), а
        // 09.10.2026 отдельного ОКНА обновления не стало вовсе (слова владельца: «Я не хочу
        // отдельно вызываемое окно»): весь ответ живёт разделом «Обновление» окна настроек,
        // и пункт меню значка открывает ТО ЖЕ окно на этом разделе — связка делает это дверью
        // `_shell.OpenUpdateSection`, а раздел выбирается у окна его же уговором
        // (`IUpdateSectionWindow`). Поэтому второго окна с ответом об обновлении в панели нет
        // ни одного, и построителя такого окна здесь тоже нет.

        // «О ПРОГРАММЕ» — РАЗДЕЛ НАСТРОЕК, А НЕ ОКНО (u14, 2.4.0). Решение владельца 27.09.2026
        // («„о программе“ не туда воткнул. Предлагаю перенести вообще в настройки») исполнено
        // до конца: отдельного окна нет, построителя у связки нет, а пункт меню значка открывает
        // настройки на седьмом разделе (`PanelShell.OpenAboutSection`).

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

            // КАЛИТКА ГАСНЕТ ВМЕСТЕ С ПАНЕЛЬЮ: слушатель, переживший приложение, держал бы порт
            // 43127 и пускал бы телефон в дверь, за которой уже никого нет.
            _phoneGate?.Dispose();
            _phoneGate = null;

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
            _updateState = null;

            // Часы автокопии гасятся вместе с панелью: таймер, переживший выход, дёргал бы
            // движок копий у завершающегося приложения.
            _backups?.Dispose();
            _backups = null;
            _settings = null;
            _noticeClicks?.Dispose();
            _noticeClicks = null;
            _shell?.Dispose();
            _shell = null;
            _tray = null;
        };

        var panelWindow = new PanelWindow(window, isolated, log);

        // Запись геометрии — при «окно спрятано»: и крестик, и «Выход» проходят через Closing,
        // а это последнее мгновение, когда окно стоит там, где его оставил человек.
        panelWindow.BeforeClosing = () => SavePlacement(window);
        _tray = new TrayIconHost(PanelStrings.TrayToolTip);

        // ШАРИК — ДВЕРЬ, А НЕ КАРТИНКА (DEF-046, банк `60-states.md` §5а). Подписка стоит здесь,
        // рядом со значком: второго пути к шарику в панели нет, а «клик ничего не делает» жил
        // ровно потому, что этого места не было вовсе. Цель выбирается картой видов
        // (`NoticeTargets`), а открывает окна связка — у неё слоты и права прогона.
        _noticeClicks = new NoticeClickDoor(_tray, OpenNoticeTarget, log);

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
            createIssue: CreateIssueWindow,

            // Пункт меню «Проверить обновления» — тот же путь, что кнопка «Проверить сейчас»
            // в разделе «Обновление» настроек: щелчок человека идёт МИМО суточного гейта
            // (`UpdateController.Check`). Второго способа попросить проверку в панели нет.
            checkUpdate: () => _update?.Check(),

            createSettings: CreateSettingsWindow,
            createBackups: CreateBackupWindow,
            createPeaks: CreatePeakWindow,

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
        var autoStart = AutoStartServer(window, log, isolated, human);

        // Второй запуск ярлыка стучится в окно-приёмник — показываем окно тем же путём,
        // что и по щелчку по значку. Подписка снимается при выходе: замок общий на процесс,
        // и обработчик на нём не должен переживать панель.
        _signal = ProgramOptions.InstanceSignal;
        if (_signal is not null) _signal.ShowPanelRequested += _shell.ShowFromSignal;

        // ОКНО ПЕРВОГО ЗАПУСКА — ОДИН РАЗ (A5, вариант «а», находка C1 N-01).
        //
        // ⚠️ Порядок назван и проверяется: окно показывается ДО шарика «панель запущена» — если
        // человек увидел окно, шарик о том же запуске молчит (два сообщения об одном — шум).
        // Решение «показывать ли» — чистая функция (`FirstRunDecisions.ShouldShow`), поэтому
        // проверка перебирает его без окна, а не смотрит глазами.
        //
        // ⚠️ ИДЁТ СТАРТ — ОКНО ЖДЁТ (DEF-054, 2.4.0): проверка не говорит «не поднялся» о том,
        // что поднимается. Реальную неудачу назовёт повторная проверка после попытки —
        // `ShowFirstRunAfterStart`.
        var firstRunShown = ShowFirstRunOnce(paths, log, human, isolated);

        ShowFirstRunAfterStart(autoStart, paths, log, human, isolated);

        // ПАРОВОЗ ПОСЛЕДНИМ: «панель запущена — я в трее». Требование владельца 29.09.2026:
        // обычный запуск окна не показывает, и человек не понимает, работает панель или нет.
        // Решение — чистый предикат, а шарик идёт общей дверью изоляции (см. NotifyPanelStarted).
        NotifyPanelStarted(windowShown || firstRunShown);
    }

    /// <summary>
    /// ПОКАЗАТЬ ОКНО ПЕРВОГО ЗАПУСКА — ЕСЛИ ЕМУ ЕСТЬ ЧТО СКАЗАТЬ И ЧЕЛОВЕК ЕГО ЕЩЁ НЕ ВИДЕЛ (A5).
    ///
    /// **Что здесь решается и что нет.** Чистое решение — в <see cref="FirstRunDecisions"/>:
    /// «человек × изолированность × отметка × есть ли строки». Здесь только показ, двери окна
    /// и ОТМЕТКА — и она пишется ПРАВОМ <c>RunRights.LocalData</c> (тем же, каким панель пишет
    /// свои настройки), ТОЛЬКО когда окно действительно показано.
    ///
    /// ⚠️ **Сбой записи не ломает запуск:** прогон уже показал окно, и «панель не запустилась
    /// из-за отметки» было бы хуже, чем «окно покажется в следующий раз». Строка об этом уходит
    /// в журнал самим сохранением (`SettingsController.Save`).
    ///
    /// ⚠️ **Факты — только «есть / нет»:** значения ключа, путей и имени пользователя здесь нет
    /// ни одного, поэтому окно (и его кадр) личных данных не содержит по построению.
    /// </summary>
    private bool ShowFirstRunOnce(AppPaths paths, Action<string> log, bool human, bool isolated)
    {
        if (!human || isolated) return false;

        var shownAt = _settings?.Settings.WelcomeShownAt ?? string.Empty;
        var door = new FirstRunFactsDoor(() => FirstRunFactsNow(paths));

        // ОДИН СНИМОК ФАКТОВ — И НА РЕШЕНИЕ, И НА ПОКАЗ (DEF-064). Прежде дверь читалась дважды
        // (решением и окном), и второе чтение приходило уже с другим ответом: окно открывалось
        // по непустому списку, а рисовалось пустым, оставляя в журнале «показано: » без слов.
        var decision = FirstRunDecisions.Decide(human, isolated, shownAt, door);

        if (!decision.Show) return false;

        var window = new FirstRunWindow();

        // ОКНО ПОЛУЧАЕТ ТОТ ЖЕ СНИМОК и подписку на состояние сервера (DEF-064): пока идёт старт —
        // оно молчит, сервер ответил — закрывается само, настоящая неудача — перерисовывается.
        // Пустой снимок окно не показывает: строк нет — говорить нечего, и пустой строки журнала
        // «окно первого запуска показано: » больше не бывает.
        if (!window.Attach(door, decision.Facts, _server)) return false;

        // ДВЕРИ ОКНА — существующие: раздел «Обновление» у уже открытого окна настроек
        // (`PanelShell.OpenUpdateSection` — второго окна настроек не бывает) и окно панели.
        window.UpdateSectionRequested += () => _shell?.OpenUpdateSection();
        window.PanelRequested += () => _shell?.ShowPanel();

        window.Show();

        // Строку журнала собирает САМО окно из названий показанных строк: путей и значения ключа
        // в ней быть не может (в `{0}` уходят только слова словаря).
        log(window.ShownLogLine);

        // ОТМЕТКА — ПО ФАКТУ ПОКАЗА, и только теперь.
        if (_settings is not null)
        {
            _settings.Settings.WelcomeShownAt = FirstRunDecisions.Stamp(DateTimeOffset.Now);
            _settings.Save(_settings.Settings);
        }

        return true;
    }

    /// <summary>
    /// ФАКТЫ ДЛЯ ОКНА ПЕРВОГО ЗАПУСКА — ТРЕМЯ ОТДЕЛЬНЫМИ ВОПРОСАМИ, как велит спецификация.
    ///
    /// 1. **Сервер DSH есть?** — у поиска СВОЕГО движка (<see cref="OwnEngine.Locate"/>), а не
    ///    у строки версии: строка пуста и когда сервера нет, и когда версию не прочитали.
    ///    Слова окна при этом говорят «не найден», а не «не установлен»: поиск пуст и когда
    ///    пакет есть, а Node нет.
    /// 2. **Сервер поднялся?** — только УСТОЙЧИВЫЙ факт: «порт занят чужой программой» или
    ///    «сервер был наш и его нет». «Не отвечает прямо сейчас» в окно не попадает: в первые
    ///    секунды после старта панель и так пишет «связи нет», хотя связь есть.
    /// 3. **Ключ модели найден и читается?** — файл ключей и его читаемость; САМО ЗНАЧЕНИЕ
    ///    не читается вовсе, и в окно уходит только «есть / нет / не прочитан».
    /// </summary>
    private FirstRunFacts FirstRunFactsNow(AppPaths paths)
    {
        var enginePresent = OwnEngine.Locate(paths) is not null;

        // ФАКТЫ О СЕРВЕРЕ — ОДНИМ СНИМКОМ И В ПРАВИЛЬНОМ ПОРЯДКЕ (DEF-064): сперва `TryRefresh`
        // (он выставляет «процесс поднят, но ещё не ответил»), и только потом `Starting`.
        // Наоборот — и решение получало устаревшее «не поднялся», пока сервер поднимался;
        // порядок назван в `FirstRunDecisions.ServerSnapshot` и проверяется подставным сервером.
        //
        // Состояние читается БЕЗ ОЖИДАНИЯ ЗАМКА: занят — значит прямо сейчас идёт работа
        // (старт, гашение), и это тот же факт «в движении», а не «не поднялся».
        var snapshot = FirstRunDecisions.ServerSnapshot(_server);

        // Причина считается ЧИСТЫМ решением: «не отвечает прямо сейчас» причиной не является,
        // и окно не имеет права выдать одно за другое.
        var problem = FirstRunDecisions.Reason(
            enginePresent, snapshot.State.IsRunning, snapshot.Starting,
            snapshot.State.Presence, _server?.Owner ?? ServerOwner.None, snapshot.State.Detail);

        var agent = Agents.AgentCatalog.Find(_settings?.Settings.ActiveAgent ?? string.Empty);

        return new FirstRunFacts(
            EnginePresent: enginePresent,
            ServerProblem: problem,
            KeyFound: CredentialsKey.Read(paths.CredentialsPath, agent.KeyName).Found,
            KeyReadable: CredentialsKey.Readable(paths.CredentialsPath));
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
        UiThread.CheckAccess,
        ApplyTheme,
        action => UiThread.Post(action),
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
    /// ⚠️ **Возвращает задачу попытки** (DEF-054, 2.4.0): проверка первого запуска ждёт её конца
    /// и только потом говорит «не поднялся». <c>null</c> — попытки не будет вовсе (нет права или
    /// контроллера), и ждать нечего.
    /// </summary>
    private Task? AutoStartServer(MainWindow window, Action<string> log, bool isolated, bool human)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!RunRights.MayAutoStartServer(RunContext.Current, human))
        {
            // Права нет — и об этом одной строкой в журнал: «панель не подняла сервер» обязано
            // быть объяснимо, иначе это выглядит как забывчивость.
            log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.AutoStartRefusedLogFormat,
                isolated ? PanelStrings.AutoStartRefusedIsolatedLog : PanelStrings.AutoStartRefusedCheckRun));

            return null;
        }

        // Контроллера может не быть вовсе — панель уже завершается: поднимать тогда нечего
        // и незачем. Ссылка берётся ЗДЕСЬ и больше не спрашивается: `Exit` обнуляет поле,
        // а фоновая задача живёт дольше.
        if (_server is not { } server) return null;

        return Task.Run(() =>
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
            //
            // НАДПИСЬ — ПО ПРИЗНАКУ «ПЕРВЫЙ ЛИ ЗАПУСК» (t15, 18.10.2026, слово владельца):
            // обещание «первый запуск достраивает профиль» здесь стояло безусловно и было
            // неправдой (профиль DSH лежал с 26.09). Признак считает контроллер по каталогам
            // своего дома движка, слова выбирает `EngineFirstRun`; окно по-прежнему ничего
            // не решает.
            window.PostToOwnUi(() => window.BeginServerStart(EngineFirstRun.StartingLabel(server.FirstRunLooks)));

            try
            {
                server.Start(MainWindow.StartTimeout);
            }
            finally
            {
                // Чем бы подъём ни кончился — отказ, исключение, успех, — кнопки обязаны
                // разблокироваться: занятость, пережившая неудачу, оставила бы окно мёртвым.
                window.PostToOwnUi(window.EndServerStart);
            }
        });
    }

    /// <summary>
    /// ПОВТОРНАЯ ПРОВЕРКА ПЕРВОГО ЗАПУСКА — ПОСЛЕ ПОПЫТКИ СТАРТА (DEF-054, 2.4.0).
    ///
    /// **От какого случая.** При старте ПК панель поднимает сервер в фоне, и проверка первого
    /// запуска работала в тот же миг: в журнале 23.10.2026 10:07:42 рядом стоят «Сервер DSH
    /// запущен: node PID 27040» и «окно первого запуска показано: Сервер DSH не поднялся».
    /// Первое прочтение уже исправлено (`FirstRunDecisions.Reason` молчит, пока идёт попытка),
    /// но молчание до конца попытки означало бы, что настоящую неудачу этого запуска человек
    /// не увидит вовсе: окно показывается один раз, и второго случая не будет.
    ///
    /// Поэтому после попытки проверка спрашивает ИСТИНУ заново — тем же путём (`ShowFirstRunOnce`):
    /// если подъём удался или пропущен, говорить нечего и окна нет; если сорвался — причина уже
    /// устойчивая, и окно показывается как обычно (с отметкой «показано»).
    ///
    /// ⚠️ Попытки нет вовсе (нет права или контроллера) — ждать нечего: <c>null</c> значит
    /// «повторной проверки не будет», а не «подожди и посмотри».
    /// </summary>
    private void ShowFirstRunAfterStart(
        Task? autoStart, AppPaths paths, Action<string> log, bool human, bool isolated)
    {
        if (autoStart is null || !human || isolated) return;

        _ = autoStart.ContinueWith(
            _ => UiThread.Post(() =>
            {
                // Панель могла завершиться, пока шёл подъём: показывать окно некому, а трогать
                // настройки (отметка «показано») после выхода незачем.
                if (_settings is null || _server is null) return;

                ShowFirstRunOnce(paths, log, human, isolated);
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
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

        // Числа в журнал: по ним видно, что панель ПРИМЕНИЛА и не подменила ли это зажатие по экрану.
        _log(WindowPlacement.Describe(
            PanelStrings.PanelLogWindowPlacementApplied, frame, window.WindowState.ToString(), areas,
            window.RenderScaling));

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

        // ⚠️ ЧТО ЗДЕСЬ БЫЛО НЕ ТАК (находка 09.10.2026 по журналу, а не по догадке).
        // Прежде показание окна принималось по одному условию — «состояние Normal». Но скрытое
        // в трее окно тоже отвечает «Normal» и при этом отдаёт ЧУЖУЮ геометрию: в журнале есть
        // запись «0,0 589x677» из момента, когда окно было спрятано (закрытие шло через замену
        // файлов). Место при этом соврало — а соври оно про РАЗМЕР, в файл уехало бы «во весь
        // экран», и следующая панель открылась бы широкой. Ровно это владелец и видел после
        // перезагрузки. Правило вынесено в чистую дверь: у невидимого окна места не спрашивают.
        var reading = window.CurrentFrame();
        if (WindowPlacement.WorthRemembering(
                window.IsVisible, window.WindowState == WindowState.Normal, reading))
        {
            _lastNormalPlacement = reading;
        }

        var current = _lastNormalPlacement;
        if (current is null) return;

        // ⚠️ Числа в журнал — ДО записи: именно они решают спор о широком окне после полной
        // перезагрузки. В строке видно и то, что панель записывает, и состояние окна в этот миг.
        _log(WindowPlacement.Describe(
            PanelStrings.PanelLogWindowPlacementSaved,
            current.Value,
            window.WindowState.ToString() + (window.IsVisible ? string.Empty : " (окно скрыто)"),
            window.Screens.All.Select(screen => screen.WorkingArea).ToArray(),
            window.RenderScaling));

        _settings.SetWindowPlacement(current.Value.X, current.Value.Y, current.Value.Width, current.Value.Height);
    }

    /// <summary>
    /// СОБРАТЬ КАЛИТКУ ТЕЛЕФОНА — ОТДЕЛЬНЫМ ЧЛЕНОМ, ЧТОБЫ ЭТО МОЖНО БЫЛО ПРОВЕРИТЬ
    /// (уточнение владельца 11.10.2026 после живого прогона).
    ///
    /// ⚠️ **Владение, а не слушание.** Калитка заводится по праву ВЛАДЕНИЯ
    /// (<see cref="RunRights.MayOwnPhoneGate"/>) — то есть **и при выключенном телефоне**: иначе
    /// щелчок рубильника не мог бы её поднять (калитки нет вовсе), а код сопряжения — выпуститься.
    /// Слушает она только после <see cref="PhoneGate.Start"/>, и тот спрашивает право ОТКРЫТИЯ
    /// и рубильник заново. Прежде право было одно, и выключенный телефон означал «калитки нет» —
    /// ровно оттуда и взялся дефект живого прогона.
    ///
    /// Прогону проверки и изолированному прогону не даётся НИ владения, НИ слушания: <c>null</c>.
    /// </summary>
    internal static PhoneGate? CreatePhoneGate(
        RunContext context,
        bool humanLaunch,
        PhoneGateDoors doors,
        Func<bool> enabled,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(log);

        return RunRights.MayOwnPhoneGate(context, humanLaunch)
            ? new PhoneGate(doors, enabled, humanLaunch, context)
            : null;
    }

    /// <summary>
    /// МОЖНО ЛИ ПОДНИМАТЬ КАЛИТКУ ПО БРАНДМАУЭРУ (уточнение владельца 11.10.2026: «узкое правило
    /// ДО открытия калитки»). Три ответа, и все три названы:
    ///
    /// * двери брандмауэра нет (прогон проверки, панель не у человека) — требовать правило не
    ///   с кого, и калитка встаёт;
    /// * правило прочитано как «наше и такое, как надо» (<see cref="FirewallPresence.Ready"/>) —
    ///   встаёт;
    /// * во всех прочих случаях (правила нет · оно не такое · их несколько · прочитать не удалось) —
    ///   НЕ встаёт: «не прочиталось» никогда не значит «правила нет», и открывать дверь в надежде
    ///   значило бы получить тот самый вопрос Windows и широкие правила.
    /// </summary>
    private bool FirewallLetsGateOpen()
    {
        if (_firewall is null) return true;

        var port = PanelSettings.NormalizePhonePort(_settings!.Settings.PhonePort);

        return _firewall.Read(port).Presence == FirewallPresence.Ready;
    }

    /// <summary>
    /// СЛЕД АДРЕСА, НА КОТОРОМ ВСТАЛА КАЛИТКА. Пишется ОБРАТНОЙ дверью калитки
    /// (<see cref="PhoneGateDoors.Bound"/>), а не догадкой окна: «по какому адресу телефон найдёт
    /// панель» — то, что калитка действительно сделала, и это единственный честный источник.
    ///
    /// Копия настроек берётся у действующих и сохраняется целиком — тем же приёмом, что у раздела
    /// «Телефон»: собери её здесь заново, и решение, принятое в другом окне, потерялось бы.
    ///
    /// ⚠️ **Не записалось — строки в журнале не будет, и это названо, а не спрятано.** Запись эта —
    /// след, а не решение: раздел показывает адрес, посчитанный тем же правилом заново
    /// (<see cref="Shell.PhoneSectionInfo.LastAddress"/> — только предподстановка в списке), а сама
    /// калитка стоит на своём адресе независимо от файла. Отдельного слова в словаре под этот
    /// случай владелец не просил, а выдумывать строку молча — хуже, чем её не иметь.
    ///
    /// ⚠️ **Член СТАТИЧЕСКИЙ и принимает владельца настроек доводом** — ровно затем, чтобы этот
    /// шов можно было проверить живым путём (<c>PhoneWiringTests</c>): «после Start() в файле
    /// настроек стоит тот адрес, на котором калитка встала» — утверждение о ПРОДУКТЕ, и собирать
    /// его в проверке заново значило бы мерить свою выдумку.
    /// </summary>
    internal static void RememberPhoneAddress(ISettingsControl settings, string address)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var text = (address ?? string.Empty).Trim();

        if (text.Length == 0) return;

        var next = SettingsStore.Clean(settings.Settings);
        next.PhoneLastAddress = text;

        settings.Save(next);
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

        // РАЗДЕЛУ «ОБНОВЛЕНИЕ» нужны ДВА домена: состояние выпусков и движок установки —
        // других мест, где панель готовит замену файлов, не осталось вовсе (отдельного окна
        // обновления нет с 09.10.2026). Дверью он больше не связан (он стал разделом списка слева),
        // поэтому обновление приходит прямо сюда — решение владельца 28.09.2026: «это будет
        // просто нормальный пункт меню как и остальные во вкладке настройки».
        window.Attach(
            _settings!,
            _autostart,
            _balance,
            _pricing,
            _update,
            _updateInstall,

            // СОСТОЯНИЕ ПОДГОТОВКИ — ТО ЖЕ, ЧТО У ПАНЕЛИ (DEF-058): окно его не заводит себе,
            // а показывает. Закрытие окна состояние не трогает.
            updateState: _updateState,
            skipUpdate: version => _settings?.SetUpdateSkippedVersion(version) ?? false,
            exitForUpdate: () => _shell?.Exit(),

            // РАЗДЕЛ «ТЕЛЕФОН» (F1.d): дверь к решениям о телефоне и дверь к адресам машины.
            // Обе — доводами, а не полями окна: проверка подставляет свои ответы, а продукт
            // идёт ровно этим путём (см. `SettingsWindow.Attach`).
            phone: new PhoneSection(
                _settings!,
                () => _phoneGate,
                message => _log(message),
                () => DateTimeOffset.Now),
            phoneAddresses: RealNetworkAddresses.Read,

            // ПРАВИЛО БРАНДМАУЭРА (F4) — СВОЯ дверь, и у прогона проверки её нет вовсе
            // (`_firewall` остаётся пустым): раздел скажет словами, что тут им не управляют.
            firewall: _firewall);

        // БЛОК ДВИЖКА: дверь даёт `App` (в прогоне проверки её нет вовсе) — окно только показывает.
        if (_engineUpdate is not null) window.AttachEngineUpdate(_engineUpdate);

        // «О ПРОГРАММЕ» — РАЗДЕЛ ЭТОГО ОКНА (u14, 2.4.0). Отдельного окна больше нет: решение
        // владельца 27.09.2026 (`docs\DECISIONS.md:32`) исполнено до конца. Дверь в отчёт
        // о проблеме остаётся, и даёт её связка: отчёту нужны журнал панели и состояние сервера,
        // а разделу они не нужны и знать их он не должен.
        window.AttachIssueDoor(() => _shell?.OpenIssue());
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
            AboutInfo.PanelVersion,
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
    /// ОКНО «СООБЩИТЬ О ПРОБЛЕМЕ»: собрать факты и отдать окну. Требование владельца 29.09.2026
    /// (DESIGN.md п. 38). Дверь в него стоит в разделе «О программе» (u14, 2.4.0).
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
            PanelVersion: AboutInfo.PanelVersion,
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
            typeof(AboutInfo).Assembly)?.InformationalVersion;

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
                : new TrayStatusLine(PanelStrings.UpdateNeverChecked, TrayTone.Neutral),

            // СТРОКА ТЕЛЕФОНА (F1.d). Решение о ней — ЧИСТОЕ и живёт в `PhoneDecisions.TrayLine`:
            // не разрешён — пустой текст, и меню не покажет строки ВОСЕМЬ («ждёт» или «подключён»
            // тоже решаются там). Здесь только два факта: разрешён ли телефон (настройки)
            // и подключён ли (калитка). Второе спросить больше негде: движок не знает, кто к нему
            // подключён вовсе (замер `docs\PHONE-F0.md` §5.4).
            phone: PhoneDecisions.TrayLine(
                _settings?.Settings.PhoneEnabled ?? false,
                _phoneGate?.Connected == true));
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
    ///
    /// **Показ защищён ДВЕРЬЮ (<see cref="NotificationDoor"/>, 2.2.1).** До неё исключение из
    /// <c>TrayIconHost.ShowNotification</c> (значок не поставлен, оболочка отказала) уходило на
    /// нитку интерфейса и роняло процесс — так панель умирала на стартовой автокопии
    /// (<c>_team\220\defekt-start-crash.md</c>). Теперь отказ назван в журнале
    /// (<see cref="PanelStrings.NotifyFailedLogFormat"/>), а строка «показано» пишется только после
    /// успешного показа: до 2.2.1 она писалась ДО вызова и врала при разборе.
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

        var tray = _tray;

        if (tray is null)
        {
            // Показывать нечем: значок не построен (прогон проверки, отказ оболочки на старте).
            // Это тоже отказ и он тоже называется словами — иначе «показано» было бы неправдой.
            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.NotifyFailedLogFormat, title, nameof(NotificationDoor), PanelStrings.NotifyNoTrayReason));
            return;
        }

        // Дверь сама пишет в журнал, чем кончилось: «показано» — только после успешного показа,
        // иначе — причину отказа. Исключение из показа наверх не уходит (2.2.1).
        var shown = NotificationDoor.Announce(() => tray.ShowNotification(title, text), _log, title);

        // ⚠️ ЩЕЛЧОК ВЕДЁТ В ЦЕЛЬ ТОЛЬКО У ПОКАЗАННОГО ШАРИКА (DEF-046). Не показали — кликать
        // не по чему: оставленная цель открыла бы человеку окно «ни с того ни с сего», а в прогоне
        // проверки ещё и помешала бы ему. Отказ показа уже назван строкой выше (дверь показа).
        if (_noticeClicks is null) return;

        if (shown) _noticeClicks.Remember(kind);
        else _noticeClicks.Forget();
    }

    /// <summary>
    /// ОТКРЫТЬ ТО, О ЧЁМ БЫЛ ШАРИК (DEF-046). Возвращает <c>false</c>, если открывать нечем —
    /// связка не построена или у цели нет окна: тогда дверь щелчка пишет строку в журнал и
    /// молчит. Это и есть «дверь или ничего» (банк `60-states.md` §5а): окно «наугад» хуже,
    /// чем честное ничего.
    ///
    /// ⚠️ Двери связки вызываются прямо, а не через `UiThread.Post`: щелчок по шарику приходит
    /// в насос сообщений трея, а он живёт на нитке интерфейса (тот же путь, что у щелчка по
    /// значку — `PanelShell` открывает окна оттуда же).
    /// </summary>
    private bool OpenNoticeTarget(NoticeTarget target)
    {
        var shell = _shell;
        if (shell is null) return false;

        switch (target)
        {
            case NoticeTarget.Panel:
                shell.ShowPanel();
                return true;

            case NoticeTarget.Settings:
                shell.OpenSettings();
                return true;

            case NoticeTarget.UpdateSection:
                shell.OpenUpdateSection();
                return true;

            case NoticeTarget.Backups:
                shell.OpenBackups();
                return true;

            case NoticeTarget.Peaks:
                shell.OpenPeaks();
                return true;

            case NoticeTarget.PricingHistory:
                shell.OpenPricingHistory();
                return true;

            default:
                return false;
        }
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
    /// ЖИВАЯ ПРОБА ВЛАДЕНИЯ ОКНАМИ (<c>--windows-selftest</c>, u14, 2.4.0). Окна строит `App`
    /// после подъёма платформы — до неё окно падает «Unable to locate IWindowingPlatform»
    /// (поймано самотестом связки 24.09.2026). Проба короткая и сама убирает за собой.
    /// </summary>
    public static WindowsSelfTest? WindowsSelfTest { get; set; }

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
