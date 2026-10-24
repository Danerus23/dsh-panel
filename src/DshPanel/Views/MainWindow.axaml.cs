using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DshPanel.Agents;
using DshPanel.Balance;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// Окно панели. Показывает состояние сервера, кнопки управления им, предложение встроиться
/// в найденный сервер, баланс и тариф активного агента и двери в другие окна.
///
/// **Где какая дверь** (решения владельца 27.09.2026). В ШАПКЕ справа — «Копии и восстановление…»
/// и «Настройки…», а в карточке сервера остались только действия с сервером: прежде «Настройки»
/// стояли в одном ряду с «Запустить», и ряд управления сервером мешался с рядом дверей.
/// **«О программе…» из этого окна УБРАНА** («„о программе“ не туда воткнул. Предлагаю перенести
/// вообще в настройки») — дверь переехала в <see cref="SettingsWindow"/>, а окно осталось одно
/// на всю панель: его держит слот связки (<see cref="PanelShell.OpenAbout"/>).
///
/// **Контент прокручивается, а размер окна — нет** (дефект Д2, живой просмотр владельца
/// 26.09.2026). С найденным чужим сервером карточка состояния удлиняет содержимое, и на
/// минимальной высоте окна низ уезжал за край — кнопки нижнего ряда становились недостижимы
/// вовсе. Прокрутка это лечит, а 860x720 остаются решением владельца, а не «подгонкой».
/// Карточки копий в окне при этом нет: она дублировала раздел настроек, а дверь осталась.
///
/// **Настроек здесь нет** — решение владельца 24.09.2026: главное окно функциональное и про
/// состояние, а настройки живут в настройках. Галочка автозапуска переехала отсюда в
/// <see cref="SettingsWindow"/>: одно место для одного решения. **Исключение ровно одно
/// и названо владельцем** (27.09.2026): активный агент меняется здесь же, где показан его
/// баланс, — но ЗАПИСЫВАЕТ смену владелец настроек, а не это окно.
///
/// Размер и положение окна окно тоже НЕ читает и не сохраняет: это дело связки
/// (<see cref="WindowPlacement"/> + <c>App.StartPanel</c>). Иначе `--shot`, который строит
/// окно напрямую, читал бы личные настройки человека и показывал их в кадре.
///
/// Работа с сервером идёт в ФОНЕ, а окно только показывает. Это не украшение: первый запуск
/// движка занимает до минуты (он достраивает профиль), а разведка портов читает командные строки
/// чужих процессов, — синхронный вызов заморозил бы окно, и человек решил бы, что панель повисла.
/// Пока идёт запуск, остановка или встраивание, кнопки заблокированы, а строка состояния честно
/// говорит, чего ждём.
///
/// Окно ничего не решает само: тексты и доступность кнопок берутся из <see cref="ServerPanelModel"/>,
/// а тот — из чистых предикатов. Поэтому поведение проверяется тестами без экрана, а не глазами.
///
/// Единственное решение, которое живёт ЗДЕСЬ, — спросить подтверждение перед остановкой
/// встроенного сервера. И оно не «в окне»: спрашивать велит предикат
/// (<see cref="ServerPanelModel.NeedsStopConfirmation"/>), а гасить без подтверждения не даёт
/// контроллер — там это и проверяется тестом.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Сколько панель ждёт ответа сервера, нажав «Запустить». Столько же, сколько самотест:
    /// первый запуск в чистом профиле достраивается около минуты, и обрывать его раньше
    /// означало бы ровно ту ошибку, на которой v1 потеряла 11 прогонов.
    /// </summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(240);

    private ServerPanelModel _model = new();
    private DiscoveryResult _discovery = DiscoveryResult.Nothing;

    private IServerControl? _server;
    private IBalanceControl? _balance;
    private ISettingsControl? _agentSettings;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _refreshing;
    private bool _updatingAgent;
    private string _busyText = string.Empty;

    /// <summary>
    /// Набор портов найденных серверов, для которого уже собраны кнопки в списке. Нужен, чтобы
    /// не пересобирать их каждую секунду: осмотр идёт раз в секунду, а пересборка съедала бы фокус.
    /// </summary>
    private string _renderedFoundPorts = string.Empty;

    /// <summary>
    /// СВОЯ нитка интерфейса, взятая при постройке окна (t11 цикла 2.1.2 — тот же класс, что Н-1).
    ///
    /// ⚠️ Спрашивать её заново в каждом ответе из фона НЕЛЬЗЯ: память общей двери (`Shell\UiThread`)
    /// одна на процесс, и её очищает проверка «нитки нет вовсе» (`UiThreadTests.ForgetForTests`),
    /// идущая в полном прогоне ПАРАЛЛЕЛЬНО с окнами. Тогда ответ терялся, а занятость оставалась
    /// навсегда: `_busy` держал кнопки «Запустить»/«Отвязать»/«Остановить» заблокированными,
    /// а `_refreshing` выключал опрос состояния целиком. Окно свою нитку знает — и ответ доходит.
    /// </summary>
    private readonly Dispatcher _ui;

    /// <summary>
    /// НИТКА ИНТЕРФЕЙСА ОКНА ИЛИ ГРОМКИЙ ОТКАЗ (требование владельца, t11).
    ///
    /// **Почему отказ, а не пустое поле.** Окно без диспетчера своей нитки не умеет возвращать
    /// ответы из фона: `_busy` и `_refreshing` остались бы занятыми НАВСЕГДА — кнопки
    /// «Запустить»/«Отвязать»/«Остановить» заблокированы, опрос состояния выключен, и человек
    /// видит мёртвое окно без единого слова. «Иногда так бывает» здесь нет: окно живёт на нитке
    /// интерфейса по определению.
    ///
    /// **Почему это отдельный член, а не строка в конструкторе.** Окно на нитке БЕЗ диспетчера
    /// построить нельзя вовсе: базовый конструктор `Window` требует платформу и падает первым
    /// («Unable to locate 'Avalonia.Platform.IWindowingPlatform'», замер t11). Поэтому решение
    /// проверяется напрямую — оно же и есть то, что зовёт конструктор; подменять проверку
    /// осмотром нельзя. См. `MainWindowBackgroundTests.Окно_без_нитки_интерфейса_отказывается_строиться`.
    /// </summary>
    internal static Dispatcher RequireUiThread(Dispatcher? captured) =>
        UiThread.RequireWindow(captured, "ГЛАВНОЕ ОКНО");

    /// <summary>
    /// ОТПРАВИТЬ РАБОТУ НА СВОЮ НИТКУ ОКНА (t13). Так App возвращает занятость автоподъёма:
    /// `BeginServerStart` и `EndServerStart` — состояние ЭТОГО окна, и они обязаны дойти, даже если
    /// память нитки в общей двери к тому моменту забыта. Потеря второго означала бы «запускаю
    /// сервер…» навсегда с заблокированными кнопками.
    ///
    /// ⚠️ Ветка `false` **недостижима по конструкции**: `_ui` взят в конструкторе и non-nullable,
    /// `RequireUiThread` пустого значения не даёт. И всё же она названа вслух строкой журнала —
    /// и не исключением: это ответ фоновой задачи, и бросить оттуда значит уронить процесс, что хуже
    /// залипшей кнопки.
    /// </summary>
    public void PostToOwnUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (UiThread.PostTo(_ui, action)) return;

        PanelLog.Write(
            "ГЛАВНОЕ ОКНО: ответ из фона не дошёл — нитка интерфейса окна недоступна; " +
            "занятость окна могла остаться навсегда");
    }

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает
    /// поля, объявленные в разметке через x:Name. С Load(this) окно рисуется, а поля остаются
    /// null — и первое же обращение к кнопке падает. Поймал тест окна 24.09.2026.
    /// </summary>
    public MainWindow()
    {
        // 1) НИТКА ИНТЕРФЕЙСА — ДО РАЗМЕТКИ, и это не перестановка ради красоты (t11): отказ
        // «нет нитки» обязан зависеть от НИТКИ, а не от того, поднялась ли платформа. Проверено
        // прогоном: на нитке без диспетчера `InitializeComponent` падает первым и СВОИМ текстом,
        // и проверка отказа получала бы чужую причину.
        //
        // Окно живёт на нитке интерфейса — она и есть нитка панели для общей двери
        // (`Shell\UiThread`): фоновые опросы сервера возвращаются сюда, и дверь обязана
        // знать, куда именно. Без этого фоновый ответ спрашивал бы `Dispatcher.UIThread`
        // и в прогоне проверок ЗАБИРАЛ бы нитку интерфейса себе.
        UiThread.Remember();

        // ...и ЗДЕСЬ же она берётся на память самому окну: оба ответа из фона (нажатие и опрос)
        // идут через `UiThread.PostTo(_ui, …)` и не зависят от того, помнит ли дверь нитку (t11).
        // Пустая нитка — отказ с названной причиной, а не тихое поле: см. `RequireUiThread`.
        _ui = RequireUiThread(UiThread.Capture());

        // 2) Разметка
        InitializeComponent();

        // Значок окна — из одной точки правды (`Shell\PanelIcon.cs`): тот же рисунок лежит
        // в самом exe, и расходиться им нельзя.
        Icon = PanelIcon.Window;

        // В разметке НЕТ ни одного текста: подписи ставятся здесь из строк, а те — из словаря.
        // Так перенос на три языка не превращается в поиск текста по разметке, и «строка,
        // которую забыли перевести» видна в описи ключей, а не глазами.
        Title = PanelStrings.AppName;
        AppTitleText.Text = PanelStrings.AppTitle;

        // Монограмма знака — латинская во всех трёх языках: имя изделия (DSH) не переводят,
        // а подпись рядом переводится (`AppTitle`). Поэтому на китайском кадре знак остаётся
        // «DSH», а рядом стоит «DSH 面板 2.0».
        LogoMonogramText.Text = PanelStrings.AppMonogram;

        ServerStatusText.Text = PanelStrings.ServerUnbound;

        // Пока контроллера нет, состояние — «панель не знает», и это СЕРЫЙ тон: тот же, что
        // у огонька значка в таком же положении. Ставится здесь, а не только в Render: без него
        // кружок состояния остался бы без цвета (то есть невидимым), а строка — чёрной, и окно
        // до первого Render выглядело бы как «сервер не работает» вместо «панель не знает».
        PanelLook.Tone(ServerStatusText, TrayTone.Neutral);
        PanelLook.Tone(ServerDot, TrayTone.Neutral);

        // ЗНАЧКИ НА КНОПКАХ (запрос владельца 28.09.2026: *«Можно ли на кнопках основного окна
        // как то приукрасить? Может значки какие то предложишь»*). Значок — векторная геометрия
        // того же вида, что у разделов настроек (`PanelGlyph`): ни картинки, ни шрифтового значка,
        // потому что панель раздаётся одним exe, а шрифтовой значок на чужой машине окажется
        // пустым квадратом.
        //
        // ⚠️ ПОДПИСЬ ОСТАЁТСЯ РЯДОМ со значком, а не заменяется им: значение имеет и слово.
        // Значок для того, чтобы глаз находил кнопку быстрее, а не чтобы её называть.
        WithIcon(StartServerButton, PanelGlyph.Play, PanelStrings.StartButton);
        WithIcon(RestartServerButton, PanelGlyph.Restart, PanelStrings.RestartButton);
        WithIcon(StopServerButton, PanelGlyph.Stop, PanelStrings.StopButton);
        DetachServerButton.Content = PanelStrings.DetachButton;
        WithIcon(SettingsButton, PanelGlyph.Section(0), PanelStrings.SettingsMenuText);
        WithIcon(BackupsButton, PanelGlyph.Section(1), PanelStrings.BackupsButton);
        WithIcon(OpenAgentButton, PanelGlyph.External, PanelStrings.OpenAgentButton);
        WithIcon(RefreshBalanceButton, PanelGlyph.Circle, PanelStrings.BalanceRefreshButton);
        WithIcon(PeakButton, PanelGlyph.Section(2), PanelStrings.PeakButton);
        AdoptServerButton.Content = PanelStrings.AdoptButton;
        BalanceTitleText.Text = PanelStrings.BalanceTitle;
        BalanceStatusText.Text = PanelStrings.BalanceNotRequested;

        // «Открыть агента»: САМОЙ ССЫЛКИ нет и быть не может — в ней токен (красная линия 7).
        // Постоянной надписи под кнопкой тоже нет (замечание владельца 28.09.2026): причина
        // недоступности живёт в ПОДСКАЗКЕ кнопки, а она ставится ниже — в Render, из модели.

        // Кнопка настроек подписывается ЗДЕСЬ, а не в Attach: она не имеет отношения к серверу,
        // и работать обязана независимо от того, подключён он или нет.
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        // «Открыть панель» — третий вид просьбы: окно не открывает браузер само, оно сообщает
        // связке. Так у двери остаётся ОДИН путь — тот же, что у пункта меню значка, и он же
        // проверяется общим предохранителем изоляции.
        OpenAgentButton.Click += (_, _) => OpenAgentRequested?.Invoke();

        // Кнопка копий — как и кнопка настроек: она не имеет отношения к серверу и обязана
        // работать независимо от того, подключён он или нет.
        BackupsButton.Click += (_, _) => BackupsRequested?.Invoke();

        // Дверь в окно «Пики и тарифы» — тоже просьба, а не действие: своего окна панель
        // не открывает, и связка решает, где оно и одно ли оно. Работает независимо от сервера.
        PeakButton.Click += (_, _) => PeaksRequested?.Invoke();

        // Пояснение к встраиванию берётся из строк, а не из разметки: строка про подтверждение
        // должна меняться в одном месте вместе с остальными.
        AdoptNoteText.Text = PanelStrings.AdoptNote;

        // Дверь в копии осталась, а карточка копий из главного окна ушла (решение владельца
        // 26.09.2026): здесь оперативная информация, а карточка дублировала раздел настроек.
        // Подпись кнопки уже поставлена вместе со значком (см. выше), и второй её установки
        // здесь нет намеренно: подпись и значок ставятся ОДНИМ движением.

        // АКТИВНЫЙ АГЕНТ: подпись, список и пояснение — из строк и из ОДНОГО списка агентов
        // панели (`AgentCatalog`). Второго перечня агентов для этого окна не заводится: он
        // разошёлся бы с тем, по которому панель считает баланс и окна пика.
        // Подписи ставим здесь, а выбор и запись — в AttachAgentChoice: окно без владельца
        // настроек (съёмка кадра, проверка) обязано честно сказать, что менять нечем.
        ActiveAgentLabel.Text = PanelStrings.SettingsActiveAgentLabel;
        AgentBox.ItemsSource = AgentCatalog.All.Select(agent => agent.Title).ToArray();
        RenderAgent();

        // ПОДСКАЗКИ КНОПОК. Постоянные ставятся здесь, изменчивые (у «Запустить», «Остановить»,
        // «Открыть агента» и обновления баланса) — в Render: у них подсказка обязана называть
        // ПРИЧИНУ недоступности, а причина меняется вместе с состоянием сервера.
        PanelToolTip.Set(SettingsButton, PanelStrings.TipSettingsButton);
        PanelToolTip.Set(BackupsButton, PanelStrings.TipBackupsButton);
        PanelToolTip.Set(DetachServerButton, PanelStrings.TipDetachServerButton);

        // У «Перезапустить» — та же подсказка-запаска, что у обновления баланса: до Attach
        // (съёмка кадра, окно без сервера) кнопка обязана не молчать, а называть своё действие.
        // Настоящую подсказку с причиной ставит Render.
        PanelToolTip.Set(RestartServerButton, PanelStrings.TipRestartServerButton);

        // Дверь в пики доступна ВСЕГДА (таблица считается из профиля агента, ни ключа, ни сети
        // не нужно), поэтому её подсказка постоянная — и объяснять причину отказа нечего.
        PanelToolTip.Set(PeakButton, PanelStrings.TipPeakButton);

        // У обновления баланса подсказка ставится и здесь, а не только в RenderBalance: баланс
        // к окну подключает связка, и до её вызова кнопка обязана быть не «молчащей», а хотя бы
        // называть своё действие. RenderBalance потом заменит её на подсказку с причиной.
        PanelToolTip.Set(RefreshBalanceButton, PanelStrings.TipRefreshBalanceButton);

        // У выбора агента подсказка — та же строка, что стоит под ним: объяснение у списка одно,
        // и второй такой же текст разошёлся бы с первым. Ставится постоянно, потому что список
        // бывает недоступен (прогон проверки, один агент) — а у недоступного органа подсказка
        // и нужнее всего.
        PanelToolTip.Set(AgentBox, PanelStrings.SettingsActiveAgentNote);
    }

    /// <summary>Человек попросил настройки. Окно панели не решает, где они, — оно только сообщает.</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// Человек попросил окно копий. Решение владельца 26.09.2026: копии живут в ОТДЕЛЬНОМ окне,
    /// а дверь в него — здесь. Окно панели по-прежнему ничего не решает: оно сообщает о просьбе.
    /// </summary>
    public event Action? BackupsRequested;

    /// <summary>
    /// Человек попросил окно «Пики и тарифы». Окно панели само его не открывает и даже не знает,
    /// где оно: как и остальные двери, оно только сообщает о просьбе связке. Так у двери один путь,
    /// и связка решает и про единственный экземпляр, и про изоляцию.
    /// </summary>
    public event Action? PeaksRequested;

    /// <summary>
    /// Человек нажал «Открыть панель». Окно браузер НЕ открывает: оно сообщает о просьбе связке,
    /// и та идёт тем же путём, что пункт меню значка. Так у двери один путь, и он же проверяется
    /// предохранителем изоляции: прогон проверки не открывает браузер никогда.
    /// </summary>
    public event Action? OpenAgentRequested;

    /// <summary>
    /// Состояние сервера разложено по строкам окна — то есть известно ТОЧНО и СВЕЖО.
    ///
    /// Событие заведено ради огонька на значке в трее (решение владельца 27.09.2026): значок
    /// обязан менять цвет вместе с состоянием, а состояние панель узнаёт здесь — опросом раз
    /// в секунду и после каждого действия человека. Гасить лишние обновления — забота значка
    /// (с тем же тоном он оболочку не трогает), а не этого события: окно не знает, что там
    /// снаружи, и решать за него не должно.
    /// </summary>
    public event Action? StateRendered;

    /// <summary>Связать окно с сервером. До этого окно показывает «не подключён».</summary>
    public void Attach(IServerControl server)
    {
        ArgumentNullException.ThrowIfNull(server);

        _server = server;
        RebuildModel();

        // НАДПИСЬ ЗАНЯТОСТИ — ПО ПРИЗНАКУ «ПЕРВЫЙ ЛИ ЗАПУСК» (t15, 18.10.2026): обещание минуты
        // достраивания профиля здесь стояло всегда и было неправдой на обычном подъёме.
        StartServerButton.Click += (_, _) => RunInBackground(
            () => server.Start(StartTimeout),
            EngineFirstRun.StartingLabel(server.FirstRunLooks));

        StopServerButton.Click += (_, _) => RequestStop();
        RestartServerButton.Click += (_, _) => RequestRestart();
        DetachServerButton.Click += (_, _) => RunInBackground(
            () => server.Detach(),
            PanelStrings.ServerDetaching);

        AdoptServerButton.Click += (_, _) => AdoptCandidate();
        RecoverOwnServerButton.Click += (_, _) => RequestRecoverOwnServer();

        Render();
        StartPolling();
    }

    /// <summary>Перечитать состояние сейчас же (проверки зовут это вместо ожидания таймера).</summary>
    public void RefreshNow()
    {
        if (_server is null) return;

        _server.Refresh();
        _discovery = _server.Scan();
        RebuildModel();
        Render();
    }

    /// <summary>
    /// Показать, что панель САМА ПОДНИМАЕТ сервер, — то же состояние, что при нажатии «Запустить».
    ///
    /// Зачем это нужно человеку. Автоподъём идёт в ФОНЕ и держит замок контроллера, поэтому окно
    /// до конца подъёма не обновляется: первый запуск в чистом профиле достраивается около минуты,
    /// и всё это время человек видел бы «Сервер не запущен / панель ещё не запускала сервер» —
    /// то есть выглядело бы как провал. После перезагрузки он смотрит ровно на это.
    ///
    /// ⚠️ Зовётся ТОЛЬКО с нитки интерфейса (занятость — состояние окна) и гасится
    /// <see cref="EndServerStart"/> в <c>finally</c>: состояние занятости, пережившее неудачу,
    /// оставило бы кнопки заблокированными навсегда.
    /// </summary>
    public void BeginServerStart(string busyText)
    {
        if (_busy) return;

        _busy = true;
        _busyText = busyText;
        Render();
    }

    /// <summary>Подъём кончился (чем бы он ни кончился) — окно снова показывает настоящее состояние.</summary>
    public void EndServerStart()
    {
        if (!_busy) return;

        _busy = false;
        _busyText = string.Empty;
        Render();
    }

    /// <summary>
    /// Связать окно с балансом активного агента. Баланс и пик показывает КОНТРОЛЛЕР — окно только
    /// раскладывает строки: ни сети, ни ключа, ни решений о сообщениях здесь нет.
    /// </summary>
    public void AttachBalance(IBalanceControl balance)
    {
        ArgumentNullException.ThrowIfNull(balance);

        _balance = balance;
        balance.Changed += RenderBalance;
        RefreshBalanceButton.Click += (_, _) => balance.Refresh();

        RenderBalance();
    }

    /// <summary>Что сейчас в блоке баланса — для проверок без экрана.</summary>
    public IBalanceControl? Balance => _balance;

    /// <summary>
    /// Подключить смену активного агента — БЕЗ второго способа записи.
    ///
    /// Решение владельца 27.09.2026: «активный агент так же должен быстро меняться в главном окне,
    /// где указан баланс, наверное там должен быть выпадающий список». Записывает смену
    /// <paramref name="settings"/> — тот же владелец настроек, что стоит за кнопкой «Сохранить»
    /// и за окном настроек: у значения одно место правды (файл настроек), и второе место записи
    /// однажды затёрло бы решения, принятые в другом окне.
    ///
    /// Окну для этого не нужно ничего, кроме владельца: баланс и тариф перечитывает КОНТРОЛЛЕР
    /// (<see cref="IBalanceControl.AgentChanged"/>), а окно только показывает то, что он отдал.
    ///
    /// Без этого вызова (съёмка кадра, проверка, панель без настроек) список ЧЕСТНО недоступен
    /// и говорит об этом словами под собой: выпадающий список, который ничего не записывает,
    /// обещал бы выбор, которого нет.
    /// </summary>
    public void AttachAgentChoice(ISettingsControl settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _agentSettings = settings;
        AgentBox.SelectionChanged += OnAgentChanged;
        RenderAgent();
    }

    /// <summary>Активный агент сменился в списке: записать его владельцем настроек и перечитать баланс.</summary>
    private void OnAgentChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Подстановка состояния сама поднимает это событие (RenderAgent ставит выбранное
        // значение): без предохранителя панель записывала бы в настройки то, что только что
        // прочитала, — и делала бы это на каждой перерисовке баланса.
        if (_updatingAgent) return;

        ChooseAgent(AgentBox.SelectedIndex);
    }

    /// <summary>
    /// Смена активного агента по номеру в списке — ОДНА дверь смены, и её же зовёт обработчик
    /// списка.
    ///
    /// Отдельным методом, а не телом обработчика, по двум причинам. Первая: пока в каталоге ОДИН
    /// агент, сменить выбор щелчком нельзя вовсе (список недоступен), и проверить эту дверь
    /// прогоном иначе нечем — а дверь обязана быть проверенной ДО того, как появится второй агент.
    /// Вторая: так видно, что окно САМО ничего не записывает — оно зовёт владельца настроек
    /// тем же путём, что кнопка «Сохранить».
    ///
    /// Возвращает <c>true</c>, если выбор действительно записан (и контроллеру сказано перечитать
    /// баланс). Неизвестный номер даёт агента по умолчанию — тем же правилом, что и файл настроек.
    /// </summary>
    internal bool ChooseAgent(int index)
    {
        if (_agentSettings is null) return false;

        var chosen = AgentCatalog.IdAt(index);

        if (string.Equals(chosen, _agentSettings.Settings.ActiveAgent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_agentSettings.SetActiveAgent(chosen))
        {
            // Записать не удалось — говорим ПРЯМО и возвращаем список на то, что стоит
            // в настройках: список, оставшийся на несохранённом значении, показывал бы выбор,
            // которого панель не приняла.
            //
            // ⚠️ Порядок именно такой: сперва вернуть список (RenderAgent переписывает подсказку
            // на «почему менять нельзя»), и только потом сказать про отказ записи. Наоборот
            // человек про отказ не узнал бы вовсе.
            RenderAgent();
            AgentNoteText.Text = PanelStrings.SettingsSaveFailed;
            return false;
        }

        // Смена записана — контроллер перечитывает и баланс (сети не будет, если прогон
        // не имеет права читать ключ), и окна пика нового агента; окно покажет то, что он отдаст.
        _balance?.AgentChanged();
        RenderAgent();
        return true;
    }

    /// <summary>
    /// Показать, кто сейчас активный агент, и честно сказать, можно ли его менять.
    ///
    /// Два случая, когда менять НЕЛЬЗЯ, и оба называются словами, а не серой кнопкой:
    ///
    /// * **настройки недоступны** (прогон проверки, съёмка кадра, панель без владельца настроек) —
    ///   тогда подсказка говорит это прямо (<see cref="PanelStrings.SettingsReadOnlyNote"/>);
    /// * **агент один**: живой список из одного пункта обещал бы выбор, которого нет, поэтому
    ///   он недоступен, а под списком стоит готовая строка о том, что список будет расти.
    /// </summary>
    private void RenderAgent()
    {
        _updatingAgent = true;
        AgentBox.SelectedIndex = AgentCatalog.IndexOf(_agentSettings?.Settings.ActiveAgent);
        _updatingAgent = false;

        var writable = _agentSettings is { Writable: true };

        AgentBox.IsEnabled = writable && AgentCatalog.HasChoice;
        AgentNoteText.Text = writable
            ? PanelStrings.SettingsActiveAgentNote
            : PanelStrings.SettingsReadOnlyNote;
    }

    /// <summary>
    /// Поставить окно туда, где его оставил человек. Прямоугольник приходит УЖЕ приведённым
    /// к видимому (<see cref="WindowPlacement"/>): окно ничего не решает и файла настроек
    /// не читает — оно только применяет то, что ему дали. Читает настройки связка.
    ///
    /// Размеры окна считаются в DIP, а рабочие области экранов — в пикселях, поэтому масштаб
    /// учитывается ЗДЕСЬ: это единственное место, где эти две величины встречаются.
    /// </summary>
    public void AttachPlacement(PixelRect frame)
    {
        var scale = Scaling();

        Width = frame.Width / scale;
        Height = frame.Height / scale;
        Position = new PixelPoint(frame.X, frame.Y);
    }

    /// <summary>
    /// Где окно стоит СЕЙЧАС — в пикселях экрана, в том же виде, в каком это принимает
    /// <see cref="AttachPlacement"/>. Связка запоминает это при закрытии окна.
    ///
    /// Перевод DIP в пиксели живёт в <see cref="WindowPlacement.Frame"/> — в ОДНОМ месте
    /// с тем же переводом для места вспомогательных окон: разойдись они, окно встало бы «рядом»
    /// с местом, которого панель не занимала.
    /// </summary>
    public PixelRect CurrentFrame() => WindowPlacement.Frame(Position, ClientSize, RenderScaling);

    /// <summary>
    /// Масштаб экрана под окном. Ноль (окно ещё не мерилось) — «как есть»: делить на него нельзя,
    /// а окно в этот момент показывает ровно то, что задано в разметке.
    /// </summary>
    private double Scaling() => RenderScaling > 0 ? RenderScaling : 1;

    /// <summary>Разложить баланс и пик по строкам. Ничего не читает — только то, что уже есть.</summary>
    private void RenderBalance()
    {
        if (_balance is null) return;

        // Активный агент показывается ЗДЕСЬ ЖЕ: он читается из настроек, а настройки могли
        // смениться и в окне настроек — тогда список обязан показать новое значение, а не то,
        // что человек выбирал в прошлый раз.
        RenderAgent();

        BalanceTitleText.Text = _balance.Agent.Title + " · " + PanelStrings.BalanceTitle;
        BalanceStatusText.Text = _balance.StatusText;
        BalanceDetailText.Text = _balance.DetailText;
        PeakText.Text = _balance.PeakText;
        PeakNextText.Text = _balance.NextText;

        // ДВЕ ДАТЫ — ДВЕ СТРОКИ: своя (когда цены разбирала панель) и профильная с подписью
        // источника. Одна строка на обе дала бы человеку неверное чтение (F-5, 20.10.2026).
        PeakScheduleText.Text = _balance.PeakCheckedText;
        PeakProfileText.Text = _balance.PeakProfileCheckedText;
        PeakProfileText.IsVisible = PeakProfileText.Text.Length > 0;

        RefreshBalanceButton.IsEnabled = _balance.Allowed && !_balance.Busy;

        // ЦВЕТ СОСТОЯНИЯ: тон берётся у тех же решений, что красят строки меню значка
        // (`TrayStatus`), а кисть — у темы (`PanelLook`). Своих «похожих» оттенков в окне нет.
        PanelLook.Tone(BalanceStatusText, _balance.Tone);
        PanelLook.Tone(PeakText, TrayStatus.PeakTone(_balance.Peak.InPeak));

        // Подсказка кнопки обновления: что произойдёт и — когда нажать нельзя — почему.
        // У недоступной кнопки другого способа объяснить себя нет вовсе.
        PanelToolTip.Set(
            RefreshBalanceButton,
            !_balance.Allowed
                ? PanelStrings.TipRefreshBalanceLocked
                : _balance.Busy
                    ? PanelStrings.TipRefreshBalanceBusy
                    : PanelStrings.TipRefreshBalanceButton);
    }

    /// <summary>Что сейчас на кнопках и в строках — для проверок без экрана.</summary>
    public ServerPanelModel Model => _model;

    /// <summary>Что нашла разведка на последнем осмотре — для проверок без экрана.</summary>
    public DiscoveryResult Discovery => _discovery;

    /// <summary>
    /// Подсказка кнопки «Запустить»: что произойдёт по нажатию и — когда нажать нельзя — почему.
    ///
    /// Причина берётся у СОСТОЯНИЯ, а не подбирается по «кнопка серая»: «сервер уже отвечает»
    /// и «порт занят чужой программой» требуют от человека разного, а серая кнопка без причины
    /// читается как поломка панели.
    /// </summary>
    internal string StartTip()
    {
        if (_server is null) return PanelStrings.TipServerUnbound;

        if (_model.CanStart) return PanelStrings.TipStartServerButton;

        return _model.State.Presence == ServerPresence.Running
            ? PanelStrings.TipStartServerRunning
            : PanelStrings.TipStartServerPortBusy;
    }

    /// <summary>Подсказка кнопки «Остановить» — по тому же правилу, что у «Запустить».</summary>
    internal string StopTip()
    {
        if (_server is null) return PanelStrings.TipServerUnbound;

        if (_model.CanStop)
        {
            // У найденного сервера кнопка сначала спросит — и подсказка говорит это ДО нажатия,
            // а не после: вопрос без предупреждения выглядит как неожиданность.
            return _model.NeedsStopConfirmation
                ? PanelStrings.TipStopServerAsks
                : PanelStrings.TipStopServerButton;
        }

        return _model.State.Presence switch
        {
            ServerPresence.BusyByOther => PanelStrings.TipStopServerForeign,

            // Сервер отвечает, но панель им не управляет: гасить чужое она не имеет права,
            // и подсказка называет, что для этого сделать.
            ServerPresence.Running => PanelStrings.TipStopServerNotMine,
            _ => PanelStrings.TipStopServerNothing,
        };
    }

    /// <summary>
    /// Подсказка кнопки «Перезапустить» — по образцу <see cref="StartTip"/> и <see cref="StopTip"/>:
    /// что произойдёт по нажатию и, когда нажать нельзя, ПОЧЕМУ.
    ///
    /// У найденного сервера кнопка сначала спросит — и подсказка говорит это ДО нажатия: вопрос
    /// без предупреждения выглядит как неожиданность. Три причины отказа — три разных ответа:
    /// «порт занят чужой программой», «панель этим сервером не управляет» и «сервер не работает».
    /// </summary>
    internal string RestartTip()
    {
        if (_server is null) return PanelStrings.TipServerUnbound;

        if (_model.CanRestart)
        {
            return _model.NeedsStopConfirmation
                ? PanelStrings.TipRestartServerAsks
                : PanelStrings.TipRestartServerButton;
        }

        return _model.State.Presence switch
        {
            ServerPresence.BusyByOther => PanelStrings.TipRestartServerForeign,
            ServerPresence.Running => PanelStrings.TipRestartServerNotMine,
            _ => PanelStrings.TipRestartServerNothing,
        };
    }

    private void RebuildModel() => _model = new ServerPanelModel(_server, _discovery);

    /// <summary>Взять найденный сервер под управление. Кандидата выбирает модель, не окно.</summary>
    private void AdoptCandidate()
    {
        if (_server is null || _busy) return;

        var candidate = _model.Candidate;
        if (candidate is null)
        {
            // Кнопка была видна, а кандидат уже пропал: между показом и щелчком мир изменился.
            // Ничего не делаем и перечитываем состояние — «нажал, и ничего не произошло»
            // человек увидит как изменившуюся строку, а не как молчание.
            RefreshNow();
            return;
        }

        RunInBackground(() => _server.Adopt(candidate.Value), PanelStrings.ServerAdopting);
    }

    /// <summary>
    /// ВЗЯТЬ ПОД УПРАВЛЕНИЕ ИМЕННО ЭТОТ СЕРВЕР — а не «первый найденный». Нужно, когда найденных
    /// два: у каждого своя кнопка, и нажатие на неё и есть согласие человека взять **этот**.
    /// </summary>
    private void AdoptFound(FoundServer found)
    {
        if (_server is null || _busy) return;

        RunInBackground(() => _server.Adopt(found), PanelStrings.ServerAdopting);
    }

    /// <summary>
    /// КНОПКА НА КАЖДЫЙ НАЙДЕННЫЙ СЕРВЕР, когда их несколько.
    ///
    /// ⚠️ **Пересобирается только когда сменился НАБОР портов.** Осмотр идёт раз в секунду, и
    /// пересборка кнопок каждую секунду съедала бы фокус и мигала бы под рукой у человека.
    /// Подсказка у каждой кнопки та же, что у одиночной: согласие даётся нажатием на **свой**
    /// сервер, и слова о том, что панель берёт и как это отменить, — общие.
    /// </summary>
    private void RenderFoundServers()
    {
        var candidates = _model.ManyCandidates ? _model.Candidates : Array.Empty<FoundServer>();
        var ports = string.Join(",", candidates.Select(found => found.Port));

        if (ports == _renderedFoundPorts) return;
        _renderedFoundPorts = ports;

        FoundServersList.Children.Clear();

        foreach (var found in candidates)
        {
            var button = new Button
            {
                Content = _model.AdoptLabel(found),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !_busy,
            };

            var server = found;
            button.Click += (_, _) => AdoptFound(server);
            PanelToolTip.Set(button, PanelStrings.AdoptNote);

            FoundServersList.Children.Add(button);
        }
    }

    /// <summary>
    /// Остановка. Для СВОЕГО сервера — сразу; для встроенного — сначала подтверждение, и каждый
    /// раз заново (решение владельца 24.09.2026). Решение о том, нужно ли спрашивать, принимает
    /// предикат, а не эта строка кода.
    /// </summary>
    private async void RequestStop()
    {
        if (_server is null || _busy) return;

        var confirmed = !_model.NeedsStopConfirmation;

        if (!confirmed)
        {
            var state = _server.State;
            var dialog = new ConfirmStopWindow();
            dialog.Attach(state.Port, state.Pid, state.ProcessName);

            confirmed = await dialog.ShowDialog<bool>(this);

            // Отказ — это не «попробуем без подтверждения»: панель не трогает сервер вовсе.
            if (!confirmed) return;
        }

        RunInBackground(() => _server.Stop(confirmed), PanelStrings.ServerStopping);
    }

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем ответить на вопрос «перезапускать найденный сервер?».
    ///
    /// В жизни ответ даёт человек в <see cref="ConfirmStopWindow"/> — том же окне, что у «Остановить»:
    /// подтверждение у перезапуска ТО ЖЕ САМОЕ (и это не совпадение, а правило: <see cref="ServerPanelModel.CanRestart"/>
    /// спрашивает <see cref="ServerPanelModel.CanStop"/>, а подпись — <see cref="ServerPanelModel.NeedsStopConfirmation"/>).
    /// В проверке без экрана этот диалог всплыл бы сам и ответил не то, что решил бы человек,
    /// поэтому проверка подставляет свой ответ — ровно как <c>SettingsWindow.UnsavedChoiceForTests</c>.
    /// У обычного запуска это свойство <c>null</c>.
    /// </summary>
    internal Func<bool>? RestartConfirmedForTests { get; set; }

    /// <summary>
    /// ПЕРЕЗАПУСК — как «Остановить», только после гашения панель поднимает сервер заново.
    ///
    /// Подтверждение спрашивается по ТОМУ ЖЕ предикату, что у остановки
    /// (<see cref="ServerPanelModel.NeedsStopConfirmation"/>), и отказ значит «ничего не произошло»:
    /// панель не трогает чужой сервер вовсе. Решение о том, спрашивать ли, принимает предикат,
    /// а не эта строка кода; и без подтверждения контроллер встроенный сервер всё равно не погасит —
    /// предохранитель живёт там (<see cref="IServerControl.Restart"/>).
    /// </summary>
    private async void RequestRestart()
    {
        if (_server is null || _busy) return;

        var confirmed = !_model.NeedsStopConfirmation;

        if (!confirmed)
        {
            if (RestartConfirmedForTests is not null)
            {
                confirmed = RestartConfirmedForTests();
            }
            else
            {
                var state = _server.State;
                var dialog = new ConfirmStopWindow();
                dialog.Attach(state.Port, state.Pid, state.ProcessName);

                confirmed = await dialog.ShowDialog<bool>(this);
            }

            // Отказ — это не «перезапустим без подтверждения»: панель не трогает сервер вовсе.
            if (!confirmed) return;
        }

        // Многоточие и текст «Перезапускаю…» человек видит в строке состояния — как у соседних
        // кнопок. Ссылка входа обновляется сама: её ловит Start, своего пути для неё нет.
        RunInBackground(() => _server.Restart(StartTimeout, confirmed), PanelStrings.ServerRestarting);
    }

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем ответить на вопрос «поднять свой сервер заново?».
    /// У обычного запуска это свойство <c>null</c>, и ответ даёт человек в
    /// <see cref="ConfirmStopWindow"/> — том же окне, что у «Остановить» и «Перезапустить».
    /// </summary>
    internal Func<bool>? RecoverConfirmedForTests { get; set; }

    /// <summary>
    /// ПОДНЯТЬ СВОЙ СЕРВЕР ЗАНОВО — единственный путь из состояния «сервер поднят не панелью»
    /// (решение владельца 13.10.2026, ответы 1 и 2).
    ///
    /// ⚠️ **СПРАШИВАЕТ ВСЕГДА, и это не тот же предикат, что у «Остановить».** Там спрашивают
    /// про ВСТРОЕННЫЙ сервер, и запомненное согласие снимает вопрос; здесь гасится сервер, которым
    /// панель НЕ управляет, и согласия на него не бывает по определению — человек отвечает на это
    /// нажатие. Вопрос задаётся ДО гашения: через найденный сервер может идти его работа.
    ///
    /// Отказ — «ничего не произошло»: панель не трогает чужой процесс. Предохранитель всё равно
    /// в контроллере (<see cref="IServerControl.RecoverOwnServer"/>): окно спрашивает, решает он.
    /// </summary>
    private async void RequestRecoverOwnServer()
    {
        if (_server is null || _busy) return;

        var confirmed = false;

        if (RecoverConfirmedForTests is not null)
        {
            confirmed = RecoverConfirmedForTests();
        }
        else
        {
            var state = _server.State;
            var dialog = new ConfirmStopWindow();

            // ВОПРОС У ЭТОГО ДЕЙСТВИЯ СВОЙ (решение владельца 13.10.2026, ответ 2): гасится
            // сервер, которым панель НЕ управляет, и человек обязан услышать, что через него
            // может идти его работа, что панель поднимет вместо него своё и что это необратимо.
            dialog.AttachQuestion(
                PanelStrings.SrvRecoverTitle,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.SrvRecoverConfirmFormat, state.Port, state.ProcessName, state.Pid));

            confirmed = await dialog.ShowDialog<bool>(this);
        }

        if (!confirmed) return;

        RunInBackground(
            () => _server.RecoverOwnServer(StartTimeout, confirmed: true),
            PanelStrings.ServerRestarting);
    }

    private void StartPolling()
    {
        _timer ??= UiThread.NewTimer(TimeSpan.FromSeconds(1));

        // Нитки интерфейса нет — опроса не будет: `new DispatcherTimer` на чужой нитке СОЗДАЛ бы
        // диспетчер и забрал нитку интерфейса у прогона проверок (см. `Shell\UiThread`).
        // Молчать об этом нечем: окно в таком прогоне не живёт.
        if (_timer is null) return;

        if (_timer.IsEnabled) return;

        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>
    /// Опрос состояния раз в секунду — ТОЖЕ в фоне: у чужой программы на порту панель может
    /// ждать ответа до двух секунд, а разведка читает командные строки чужих процессов.
    /// На потоке интерфейса это выглядело бы как залипание.
    /// </summary>
    private void Poll()
    {
        if (_server is null || _busy || _refreshing) return;

        _refreshing = true;
        var server = _server;

        Task.Run(() =>
        {
            server.Refresh();
            return server.Scan();
        }).ContinueWith(task => UiThread.PostTo(_ui, () =>
        {
            _refreshing = false;

            if (task.IsCompletedSuccessfully) _discovery = task.Result;

            RebuildModel();
            Render();
        }));
    }

    private void RunInBackground(Func<ServerState> work, string busyText)
    {
        if (_server is null || _busy) return;

        _busy = true;
        _busyText = busyText;
        Render();

        Task.Run(work).ContinueWith(_ => UiThread.PostTo(_ui, () =>
        {
            _busy = false;
            _busyText = string.Empty;
            Render();
        }));
    }

    /// <summary>Разложить состояние по строкам и кнопкам. Ничего не читает — только то, что уже есть.</summary>
    private void Render()
    {
        if (_busy)
        {
            ServerStatusText.Text = _busyText;
            ServerDetailText.Text = PanelStrings.ServerStartingDetail;
            StartServerButton.IsEnabled = false;
            RestartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = false;
            DetachServerButton.IsVisible = false;
            OpenAgentButton.IsEnabled = false;
            FoundServerPanel.IsVisible = false;

            // Причина у всех четырёх кнопок одна: панель занята, и до конца работы сервер
            // не трогается. Подсказка и здесь обязана быть — недоступная кнопка без причины
            // читается как поломка.
            PanelToolTip.Set(StartServerButton, PanelStrings.TipServerBusy);
            PanelToolTip.Set(RestartServerButton, PanelStrings.TipServerBusy);
            PanelToolTip.Set(StopServerButton, PanelStrings.TipServerBusy);
            PanelToolTip.Set(OpenAgentButton, PanelStrings.TipServerBusy);

            // Состояние ещё НЕ УСТОЯЛОСЬ, поэтому и цвет у него не «зелёный/красный», а янтарный:
            // покрасить строку в «работает» до того, как сервер действительно ответил, значило бы
            // соврать цветом. Тот же довод, по которому в этой ветке не дёргается огонёк значка.
            PanelLook.Tone(ServerStatusText, TrayTone.Warning);
            PanelLook.Tone(ServerDot, TrayTone.Warning);
            return;
        }

        // СЕРВЕР ПОДНЯТ НЕ ПАНЕЛЬЮ — СВОИМИ СЛОВАМИ, а не общим «сервер работает» (решение
        // владельца 13.10.2026, ответ 1): у этого положения ДРУГОЙ смысл, чем у «взятого чужого».
        // Крупная строка называет, что случилось и на каком порту; подробность под ней говорит,
        // что за этим следует (телефон и обновление сервера DSH).
        ServerStatusText.Text = _model.NotStartedByPanel ? _model.NotStartedByPanelTitle : _model.StatusText;
        ServerDetailText.Text = _model.NotStartedByPanel ? _model.NotStartedByPanelNote : _model.DetailText;

        // ПОДПИСИ КНОПОК СЕРВЕРА МЕНЯЮТСЯ по состоянию («Перезапустить» и «Остановить» просят
        // подтверждения, когда сервер найден и взят под управление), поэтому они ставятся ЗДЕСЬ
        // и тем же движением, что значок: прямое присваивание `Content` стёрло бы значок и
        // оставило голый текст — ровно та ошибка, ради которой `WithIcon` и заведён.
        WithIcon(StartServerButton, PanelGlyph.Play, _model.StartButtonText);
        WithIcon(RestartServerButton, PanelGlyph.Restart, _model.RestartButtonText);
        WithIcon(StopServerButton, PanelGlyph.Stop, _model.StopButtonText);

        StartServerButton.IsEnabled = _model.CanStart;
        RestartServerButton.IsEnabled = _model.CanRestart;
        StopServerButton.IsEnabled = _model.CanStop;

        // ЦВЕТ СОСТОЯНИЯ СЕРВЕРА — тот же тон, что у строки сервера в меню значка и у огонька
        // на нём: зелёный «работает», красный «не работает», серый «панель не знает».
        // «Не знает» здесь — окно без контроллера (съёмка кадра, проверка), и это не «не работает».
        var tone = TrayStatus.ServerTone(_server is not null, _model.State.Presence);

        PanelLook.Tone(ServerStatusText, tone);
        PanelLook.Tone(ServerDot, tone);

        PanelToolTip.Set(StartServerButton, StartTip());
        PanelToolTip.Set(RestartServerButton, RestartTip());
        PanelToolTip.Set(StopServerButton, StopTip());

        // Кнопка панели: подпись МОЖЕТ меняться (доступность и текст даёт модель), поэтому она
        // ставится тем же движением, что и значок: иначе повторная установка `Content` стёрла бы
        // значок и оставила один текст.
        WithIcon(OpenAgentButton, PanelGlyph.External, _model.OpenAgentButtonText);
        OpenAgentButton.IsEnabled = _model.CanOpenAgent;

        // ПОДСКАЗКА КНОПКИ — единственное место, где сказана причина недоступности: постоянной
        // надписи под кнопкой нет (замечание владельца 28.09.2026). Строка приходит из модели
        // и меняется вместе с состоянием сервера — «ссылки ещё нет», «доступна», «сервер не
        // работает» — поэтому у недоступной кнопки причина всё равно названа.
        PanelToolTip.Set(OpenAgentButton, _model.OpenAgentHint);

        DetachServerButton.Content = _model.DetachButtonText;
        DetachServerButton.IsVisible = _model.CanDetach;

        // «СЕРВЕР ПОДНЯТ НЕ ПАНЕЛЬЮ» — своё положение, свои слова и своя кнопка (решение владельца
        // 13.10.2026, ответ 1). Строка называет состояние и говорит, что за ним следует; кнопка
        // даёт ОДИН путь назад. У «взятого чужого» этого нет: там «Отвязаться» (вернуть как было).
        RecoverOwnServerButton.Content = _model.RecoverOwnServerButtonText;
        RecoverOwnServerButton.IsVisible = _model.CanRecoverOwnServer && !_busy;

        // ПОДСКАЗКА ОБЯЗАТЕЛЬНА: это действие ГАСИТ найденный сервер, и «что произойдёт по нажатию»
        // человек обязан видеть до щелчка, а не узнавать из вопроса (ворота `ButtonTooltipTests`).
        PanelToolTip.Set(RecoverOwnServerButton, PanelStrings.TipRecoverOwnServerButton);
        NotStartedByPanelText.Text = _model.NotStartedByPanelNote;
        NotStartedByPanelText.IsVisible = _model.NotStartedByPanel;

        var found = _model.FoundText;
        FoundServerPanel.IsVisible = found.Length > 0;
        FoundServerText.Text = found;

        // ОДИН найденный сервер — прежняя кнопка без порта: обычный случай менять нечем.
        // НЕСКОЛЬКО — своя кнопка на каждого (её подпись называет порт), и общая кнопка прячется:
        // без порта она была бы загадкой «какой из двух».
        AdoptServerButton.Content = _model.AdoptButtonText;
        AdoptServerButton.IsEnabled = _model.CanAdopt && !_busy;
        AdoptServerButton.IsVisible = _model.CanAdopt && !_model.ManyCandidates;
        RenderFoundServers();

        // Подсказка «Взять под управление» — тоже готовая строка: она уже говорит словами,
        // что именно панель берёт и как это отменить.
        PanelToolTip.Set(AdoptServerButton, PanelStrings.AdoptNote);

        // Состояние разложено — оно известно СВЕЖИМ. Сообщаем об этом ТОЛЬКО здесь, а не в ветке
        // «идёт действие»: в ней состояние ещё не устоялось, и красить значок в цвет «работает»
        // до того, как сервер действительно ответил, значило бы врать цветом.
        StateRendered?.Invoke();
    }

    /// <summary>
    /// ПОСТАВИТЬ НА КНОПКУ ЗНАЧОК И ПОДПИСЬ — ОДНИМ ДВИЖЕНИЕМ.
    ///
    /// Зачем отдельным движением, а не двумя присвоениями. `Content` у кнопки ОДИН, и второе
    /// присваивание (`Content = PanelStrings.…`) затирает первое: кнопка молча осталась бы без
    /// значка. Здесь текст и значок кладутся вместе, поэтому «подпись поставили, значок забыли»
    /// и наоборот — одна и та же ошибка, и её не бывает.
    ///
    /// ⚠️ Подпись НЕ заменяется значком: значение имеет и слово, значок лишь помогает глазу
    /// найти кнопку. Поэтому кнопка остаётся кнопкой с текстом, а не иконкой-загадкой.
    ///
    /// ⚠️ **ДОСТУПНОЕ ИМЯ — ЗДЕСЬ ЖЕ, ГДЕ ПОДПИСЬ (DEF-038, 2.4.0).** `Content` у этих кнопок —
    /// `StackPanel` (значок + `TextBlock`), и читалка экрана, не найдя у кнопки ни текста, ни
    /// `AutomationProperties.Name`, объявляет её «кнопкой» без названия: восемь кнопок главного
    /// окна были безымянными (замер: `AutomationProperties.Name` — 0 применений на панель).
    /// Имя ставится ТУТ ЖЕ, а не отдельной строкой рядом: `WithIcon` зовут и при смене состояния
    /// сервера (подпись «Запустить» ↔ «Запускается…»), и второй источник имени разошёлся бы
    /// с первой же сменой текста.
    /// </summary>
    private static void WithIcon(Button button, Geometry glyph, string text)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(glyph);

        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new PathIcon
                {
                    Data = glyph,
                    Width = 13,
                    Height = 13,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = text ?? string.Empty,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            },
        };

        // ИМЯ = ПОДПИСЬ. Одно правило на все восемь кнопок с картинкой, и его стережёт
        // `MainWindowIconTests.Доступное_имя_кнопки_с_картинкой_равно_её_подписи`.
        AutomationProperties.SetName(button, text ?? string.Empty);
    }
}
