using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DshPanel.Isolation;
using DshPanel.Platform;
using DshPanel.Tray;

namespace DshPanel.Shell;

/// <summary>
/// Панель целиком: значок в трее, окно, окно настроек, окно копий, окно «Пики и тарифы»,
/// окно «О программе», окно «Обновление панели» и окно «История цен» — меню между ними.
///
/// Здесь и только здесь эти семеро знают друг о друге. Значок не знает про окно, окно не знает
/// про значок — каждое из них проверяется отдельно, а связка собирается в одном месте,
/// которое человек видит целиком за полминуты.
///
/// **Шесть вспомогательных окон устроены одинаково, и это осознанно.** Все шесть строятся
/// по просьбе (функцией, а не готовым окном): панель живёт в трее месяцами, а окна, которые
/// человек открывает раз в месяц, держать построенными незачем. Все шесть живут в своём слоте
/// (<see cref="SingleWindowSlot"/>), поэтому повторная просьба выводит на передний план уже
/// открытое окно, а не заводит второе.
/// </summary>
public sealed class PanelShell : IDisposable
{
    private readonly TrayIconHost _tray;
    private readonly PanelWindow _window;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly Action<string> _log;
    private readonly bool _isolated;
    private readonly bool _mayOpenBrowser;
    private readonly Func<string> _entryLink;
    private readonly Func<TrayStatusLines> _trayStatus;
    private readonly Action<NoticeKind, string, string> _notify;
    private readonly Action _checkUpdate;
    private readonly Func<Window>? _createSettings;
    private readonly Func<Window>? _createBackups;
    private readonly Func<Window>? _createPeaks;
    private readonly Func<Window> _createAbout;

    /// <summary>
    /// Построитель окна «Сообщить о проблеме». Пусто — окна нет: движок может его не иметь
    /// (кадровая съёмка --shot about), и тогда панель говорит об этом строкой в журнал,
    /// а не показывает кнопку в никуда.
    /// </summary>
    private readonly Func<Window>? _createIssue;
    private readonly Func<Window>? _createUpdate;
    private readonly Func<Window>? _createPricingHistory;
    private readonly SingleWindowSlot _settings = new();
    private readonly SingleWindowSlot _backups = new();
    private readonly SingleWindowSlot _peaks = new();
    private readonly SingleWindowSlot _about = new();

    /// <summary>Шестое вспомогательное окно — «Сообщить о проблеме» (<c>DESIGN.md</c> п. 38).</summary>
    private readonly SingleWindowSlot _issue = new();
    private readonly SingleWindowSlot _update = new();
    private readonly SingleWindowSlot _pricingHistory = new();

    private bool _disposed;

    /// <param name="isolated">
    /// Прогон проверки, а не человек за панелью. Нужен здесь ровно для одной двери — окна
    /// «О программе»: в изолированном прогоне владельцу не показывают ничего (красная линия 8),
    /// и это единственное окно, которое панель открывает по просьбе меню, а не построением
    /// главного окна. Параметр обязателен и без умолчания намеренно: забыть его выдать значило бы
    /// однажды показать окно в прогоне проверки, а это ровно тот случай, ради которого
    /// предохранитель и заведён.
    /// </param>
    /// <param name="createSettings">
    /// Как построить окно настроек. Функция, а не готовое окно: настройки открывают редко,
    /// и держать второе окно построенным всё время незачем. Панель при этом не знает,
    /// какое именно окно ей вернут, — знает только, что это окно.
    /// </param>
    /// <param name="createBackups">
    /// Как построить окно копий — по той же причине и с тем же уговором. Отдельный параметр,
    /// а не «одно окно на оба случая»: решения о данных человека живут в копиях, и смешивать
    /// их с настройками не надо.
    /// </param>
    /// <param name="createAbout">
    /// Как построить окно «О программе» — по той же причине, но БЕЗ права быть отсутствующим:
    /// этому окну не нужен ни домен копий, ни настройки, и повода «панель построена без него»
    /// у него нет. Обязательный параметр здесь ещё и потому, что строка «окно недоступно»
    /// под него не заведена, а выдумывать текст вместо перевода нельзя.
    /// </param>
    /// <param name="createPeaks">
    /// Как построить окно «Пики и тарифы» — по той же причине, что настройки и копии: это окно
    /// открывают редко, и держать его построенным незачем. Отдельным параметром, а не «одно окно
    /// на оба случая»: там таблица окон пика и цены, и смешивать их с настройками не надо.
    /// Необязательный — у панели без домена баланса (прогоны) его может не быть, и тогда просьба
    /// честно уходит строкой в журнал, а не молчанием.
    /// </param>
    /// <param name="mayOpenBrowser">
    /// Право ЭТОГО прогона открыть браузер с работой агента (<see cref="RunRights.MayOpenBrowser"/>).
    /// Своей копии решения связка НЕ заводит: право считается в <c>App</c> у <see cref="RunRights"/>,
    /// и параметр обязателен без значения по умолчанию. Холодная проверка 27.09.2026 показала,
    /// чем кончается копия: по условию «прогон не изолирован» право открыть браузер было и
    /// у <c>--shell-selftest</c>, а не открывался он лишь потому, что ссылка в том прогоне пуста.
    /// </param>
    /// <param name="entryLink">
    /// Ссылка входа к работе агента (в ней ТОКЕН) или пусто, если панель её не знает. Обязательный
    /// параметр и без умолчания: пункт меню «Открыть агента» обязан отвечать человеку СЛОВАМИ,
    /// когда ссылки нет, а провайдер-пустышка превратил бы этот ответ в молчание.
    /// Наружу отсюда ссылка не выходит: она уезжает только в браузер.
    /// </param>
    /// <param name="notify">
    /// Куда сказать человеку то, что он обязан услышать (ссылки нет, браузер не открылся).
    /// Общая дверь сообщений, та же, что у копий и баланса: она же и решает, показывать ли
    /// сообщение в этом прогоне (<see cref="IsolationRules.ShouldNotify"/>).
    /// </param>
    /// <param name="trayStatus">
    /// Четыре строки состояния для меню значка на момент показа (сервер, агент с балансом, тариф,
    /// обновление). Функция, а не готовые строки: меню открывают и через час после старта панели,
    /// а состояние меняется — снимок врал бы про сервер весь день. Обязательный параметр и без
    /// умолчания: пустая шапка в меню выглядела бы как «панель ничего не знает», а это неправда.
    /// </param>
    /// <param name="checkUpdate">
    /// Спросить выпуски СЕЙЧАС — пункт меню «Проверить обновления». Это тот же путь, что кнопка
    /// «Проверить сейчас» в окне обновления, и он идёт МИМО суточного гейта: человек попросил.
    /// Обязательный параметр: пункт меню, который ничего не делает, выглядит сломанным.
    /// </param>
    /// <param name="createUpdate">
    /// Как построить окно «Обновление панели» — по той же причине, что настройки и копии. Окну
    /// нужны два домена (проверка выпусков и движок установки), и построить его панель может
    /// не всегда: в прогоне без права замену файлов готовить нечем, и тогда просьба честно уходит
    /// строкой в журнал, а не молчанием.
    /// </param>
    /// <param name="createPricingHistory">
    /// Как построить окно «История цен» — по той же причине: записи об изменениях читают редко,
    /// и держать окно построенным незачем. Необязательный — у панели без истории цен (прогоны)
    /// его может не быть, и тогда просьба честно уходит строкой в журнал.
    /// </param>
    public PanelShell(
        TrayIconHost tray,
        PanelWindow window,
        IClassicDesktopStyleApplicationLifetime lifetime,
        Action<string> log,
        bool isolated,
        bool mayOpenBrowser,
        Func<string> entryLink,
        Func<TrayStatusLines> trayStatus,
        Action<NoticeKind, string, string> notify,
        Func<Window> createAbout,

        /// <summary>
        /// Построитель окна «Сообщить о проблеме». Необязательный: у кадровой съёмки его нет,
        /// и тогда панель честно говорит, что окна в этом прогоне нет.
        /// </summary>
        Func<Window>? createIssue,
        Action checkUpdate,
        Func<Window>? createSettings = null,
        Func<Window>? createBackups = null,
        Func<Window>? createPeaks = null,
        Func<Window>? createUpdate = null,
        Func<Window>? createPricingHistory = null)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isolated = isolated;
        _mayOpenBrowser = mayOpenBrowser;
        _entryLink = entryLink ?? throw new ArgumentNullException(nameof(entryLink));
        _trayStatus = trayStatus ?? throw new ArgumentNullException(nameof(trayStatus));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _createAbout = createAbout ?? throw new ArgumentNullException(nameof(createAbout));
        _createIssue = createIssue;
        _checkUpdate = checkUpdate ?? throw new ArgumentNullException(nameof(checkUpdate));
        _createSettings = createSettings;
        _createBackups = createBackups;
        _createPeaks = createPeaks;
        _createUpdate = createUpdate;
        _createPricingHistory = createPricingHistory;
    }

    /// <summary>Поставить значок и связать его с окном. Окно при этом НЕ показывается.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Значок — НАШ, из сборки (решение владельца 26.09.2026): панель раздаётся одним exe,
        // и рядом с ней .ico не лежит. Если свой значок не собрался, трей не падает: остаётся
        // системный запасной, а причина уходит строкой в журнал — трей обязан работать всегда.
        // Без значка вовсе значок рисуется пустым квадратом — так и выглядит «панель, которая
        // не поставилась».
        _log(_tray.SetOwnIcon());

        // Меню ставится ПРОВАЙДЕРОМ, а не снимком: в шапке — состояние (сервер, агент, тариф,
        // обновление), и оно обязано быть свежим на каждый показ. Снимок, собранный здесь,
        // показывал бы состояние панели на момент её запуска — то есть врал бы весь день.
        _tray.SetMenuProvider(() => PanelMenu.Build(
            status: _trayStatus,
            show: ShowPanel,
            openAgent: OpenEntryLink,
            settings: OpenSettings,
            about: OpenAbout,
            checkUpdate: _checkUpdate,
            updateWindow: OpenUpdate,
            exit: Exit));

        // Щелчок и двойной щелчок делают одно и то же: у панели одно окно, и человек,
        // щёлкнувший по значку, ждёт его, а не меню.
        _tray.Clicked += ShowPanel;
        _tray.DoubleClicked += ShowPanel;

        _tray.Show();
        _log(PanelStrings.PanelLogTrayPlaced);
    }

    /// <summary>
    /// Открыть настройки. Второго окна настроек не бывает: повторная просьба (пункт меню,
    /// кнопка в окне панели) выводит на передний план уже открытое. Иначе человек, щёлкнувший
    /// дважды, получил бы два окна с одними и теми же полями — и не знал бы, какое из них главное.
    /// </summary>
    public void OpenSettings()
    {
        if (_disposed) return;

        if (_createSettings is null)
        {
            _log(PanelStrings.PanelLogSettingsUnavailable);
            return;
        }

        _log(_settings.Open(_createSettings, Place)
            ? PanelStrings.SettingsOpenedLog
            : PanelStrings.SettingsRaisedLog);
    }

    /// <summary>
    /// Открыть окно копий. Правило то же, что у настроек: второго окна копий не бывает.
    /// Это не мелочь — два окна копий означали бы две кнопки «Создать копию сейчас» над одной
    /// папкой, и человек не знал бы, какое из них показывает правду о том, что уже снялось.
    /// </summary>
    public void OpenBackups()
    {
        if (_disposed) return;

        if (_createBackups is null)
        {
            _log(PanelStrings.PanelLogBackupsUnavailable);
            return;
        }

        _log(_backups.Open(_createBackups, Place)
            ? PanelStrings.BackupsOpenedLog
            : PanelStrings.BackupsRaisedLog);
    }

    /// <summary>Окно настроек сейчас открыто — для проверок без экрана.</summary>
    public bool SettingsOpen => _settings.IsOpen;

    /// <summary>Окно копий сейчас открыто — для проверок без экрана.</summary>
    public bool BackupsOpen => _backups.IsOpen;

    /// <summary>
    /// Открыть окно «Пики и тарифы» — правило то же, что у настроек и копий: второго такого окна
    /// не бывает, повторная просьба выводит на передний план уже открытое. Это и есть дверь,
    /// ради которой окно сделано ОКНОМ, а не выезжающей панелью: у панели уже принят порядок
    /// «дверь открывает своё окно» (`docs\DESIGN.md`, решение дирижёра 27.09.2026).
    /// </summary>
    public void OpenPeaks()
    {
        if (_disposed) return;

        if (_createPeaks is null)
        {
            _log(PanelStrings.PanelLogPeaksUnavailable);
            return;
        }

        _log(_peaks.Open(_createPeaks, Place)
            ? PanelStrings.PeakOpenedLog
            : PanelStrings.PeakRaisedLog);
    }

    /// <summary>Окно «Пики и тарифы» сейчас открыто — для проверок без экрана.</summary>
    public bool PeaksOpen => _peaks.IsOpen;

    /// <summary>
    /// ОТКРЫТЬ ОКНО «ИСТОРИЯ ЦЕН» — правило ТО ЖЕ, что у остальных пяти: второго такого окна
    /// не бывает, повторная просьба выводит на передний план уже открытое, а у панели без
    /// построителя просьба честно уходит строкой в журнал.
    ///
    /// Дверь поднимает окно «Пики и тарифы» своей кнопкой «История» — окно истории отдельное
    /// (решение владельца 28.09.2026: не переключатель дат внутри «Пиков и тарифов»).
    /// </summary>
    public void OpenPricingHistory()
    {
        if (_disposed) return;

        if (_createPricingHistory is null)
        {
            _log(PanelStrings.PanelLogPricingHistoryUnavailable);
            return;
        }

        _log(_pricingHistory.Open(_createPricingHistory, Place)
            ? PanelStrings.PricingHistoryOpenedLog
            : PanelStrings.PricingHistoryRaisedLog);
    }

    /// <summary>Окно «История цен» сейчас открыто — для проверок без экрана.</summary>
    public bool PricingHistoryOpen => _pricingHistory.IsOpen;

    /// <summary>Окно «О программе» сейчас открыто — для проверок без экрана.</summary>
    public bool AboutOpen => _about.IsOpen;

    /// <summary>Окно «Обновление панели» сейчас открыто — для проверок без экрана.</summary>
    public bool UpdateOpen => _update.IsOpen;

    /// <summary>
    /// Открыть окно «Обновление панели». Правило то же, что у остальных четырёх окон: второго
    /// такого окна не бывает, повторная просьба выводит на передний план уже открытое.
    ///
    /// Прогон проверки владельцу не показывает ничего (красная линия 8) — тем же предикатом,
    /// что и окно «О программе»: это вторая дверь, которую открывает меню, а не построение
    /// главного окна, и предохранитель у неё обязан быть тот же.
    /// </summary>
    public void OpenUpdate()
    {
        if (_disposed) return;

        if (_createUpdate is null)
        {
            _log(PanelStrings.PanelLogUpdateWindowUnavailable);
            return;
        }

        if (!ShouldShowAbout(_isolated))
        {
            _log(PanelStrings.WindowSuppressedLog);
            return;
        }

        _log(_update.Open(_createUpdate, Place)
            ? PanelStrings.UpdateOpenedLog
            : PanelStrings.UpdateRaisedLog);
    }

    /// <summary>
    /// Показывать ли окно «О программе» в этом прогоне.
    ///
    /// Правило берётся у ОБЩЕЙ двери изоляции, а не решается здесь: в изолированном прогоне
    /// владельцу не показывают ничего (красная линия 8) — ни окна панели, ни этого. Отдельным
    /// ЧИСТЫМ предикатом, а не условием внутри ветки, потому что связку со значком в тестах
    /// не поднять (значок встаёт на рабочий стол владельца), а решение обязано быть проверяемым
    /// и ломаться мутацией.
    /// </summary>
    public static bool ShouldShowAbout(bool isolatedRun) =>
        IsolationRules.ShouldShowPanelOnSignal(isolatedRun);

    /// <summary>
    /// Открыть окно «О программе». Правило то же, что у настроек и копий: второго окна не бывает,
    /// повторная просьба (пункт меню, кнопка в главном окне) выводит на передний план уже
    /// открытое. Второго такого окна быть не должно: в нём одни и те же ссылки, и человек
    /// не понял бы, какое из двух «настоящее».
    /// </summary>
    /// <summary>
    /// Открыть окно «Сообщить о проблеме». Правило то же, что у «О программе»: второго окна
    /// не бывает, повторная просьба выводит на передний план уже открытое. Отчёт собирается
    /// на каждый показ — он про СЕЙЧАС, а не про то, что было при первом открытии.
    ///
    /// Прогон проверки владельцу не показывает ничего (красная линия 8): отчёт — это окно,
    /// и подавленное решение уходит строкой в журнал.
    /// </summary>
    public void OpenIssue()
    {
        if (_disposed) return;

        if (_createIssue is null)
        {
            _log(PanelStrings.PanelLogIssueUnavailable);
            return;
        }

        if (_isolated)
        {
            _log(PanelStrings.WindowSuppressedLog);
            return;
        }

        _log(_issue.Open(_createIssue, Place)
            ? PanelStrings.IssueOpenedLog
            : PanelStrings.IssueRaisedLog);
    }

    public void OpenAbout()
    {
        if (_disposed) return;

        // Прогон проверки владельцу не показывает НИЧЕГО (красная линия 8), и подавленное
        // решение уходит строкой в журнал: иначе «панель молчала» нечем объяснить.
        if (!ShouldShowAbout(_isolated))
        {
            _log(PanelStrings.WindowSuppressedLog);
            return;
        }

        _log(_about.Open(_createAbout, Place)
            ? PanelStrings.AboutOpenedLog
            : PanelStrings.AboutRaisedLog);
    }

    /// <summary>
    /// ПОСТАВИТЬ ВСПОМОГАТЕЛЬНОЕ ОКНО РЯДОМ С ПАНЕЛЬЮ — одна дверь на все четыре окна
    /// (настройки, копии, «Пики и тарифы», «О программе»).
    ///
    /// Слова владельца 28.09.2026: *«Немного бесящий факт, что когда открываешь окно, оно
    /// открывается далеко от основной панели и приходится тянуться мышью»*. Решение принято
    /// владельцем без изменений: панель на экране — окно рядом с ней со смещением (не закрывая её);
    /// панель убрана в трей — окно у курсора; и всегда в пределах рабочего стола того экрана,
    /// где оно появляется.
    ///
    /// ⚠️ **Решение считается в ОДНОМ месте** — здесь и в чистой функции
    /// <see cref="WindowPlacement.NearPanel"/>. Прежде место задавалось разметкой каждого окна
    /// (<c>WindowStartupLocation</c>), то есть четырьмя разными ответами на один вопрос; теперь
    /// его в разметке этих окон нет вовсе, и это сторожит проверка.
    ///
    /// Размер окна берётся из его же разметки (DIP) и переводится в пиксели масштабом того экрана,
    /// на котором окно окажется: рабочие области приходят в пикселях, и «пиксели = точки» верно
    /// только при масштабе 100 %. У окна с <c>SizeToContent</c> (это «О программе») высота до показа
    /// неизвестна, и берётся умолчание — окно ставится ВЕРХНИМ ЛЕВЫМ УГЛОМ, а запас по высоте
    /// только уводит его от края экрана, то есть ошибается в безопасную сторону.
    /// </summary>
    private void Place(Window window)
    {
        var panel = _window.ScreenFrame;
        var cursor = SystemCursor.Position();

        // Масштаб спрашивается у того экрана, куда окно попадёт: панель на экране — у её экрана,
        // панель в трее — у экрана под курсором. Неизвестно ни то, ни другое — «как есть».
        var scale = panel is { } frame
            ? _window.ScaleAt(new PixelPoint(frame.X, frame.Y))
            : cursor is { } point ? _window.ScaleAt(point) : 1;

        Place(window, panel, cursor, _window.ScreenAreas, scale);
    }

    /// <summary>
    /// Тот же расчёт, но живыми фактами НЕ связанный: панель, курсор и экраны приходят
    /// параметрами. Отдельным методом — чтобы правило «окно встаёт туда, куда решило ядро,
    /// и решение принимается ДО показа» можно было проверить прогоном, а не глазами:
    /// ни панели, ни курсора, ни второго монитора у проверки нет.
    /// </summary>
    internal static void Place(
        Window window,
        PixelRect? panel,
        PixelPoint? cursor,
        IReadOnlyList<PixelRect> screens,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(window);

        var spot = WindowPlacement.NearPanel(
            WindowPlacement.Pixels(window.Width, window.Height, scale), panel, cursor, screens);

        // Место задаётся ЯВНО, а не «по умолчанию Windows»: у окна, оставленного каркасу,
        // собственное правило («по центру владельца») сработало бы поверх нашего — владельца
        // у этих окон нет вовсе, и центр вышел бы произвольным.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(spot.X, spot.Y);
    }

    private void ShowPanel()
    {
        if (_window.Show()) _log(PanelStrings.WindowShownLog);
    }

    /// <summary>
    /// Открыть панель в браузере — ОДНА дверь на кнопку главного окна и на пункт меню значка.
    ///
    /// Три случая, и все три названы словами:
    ///
    /// * ссылки нет → человеку уходит сообщение, а не молчание: пункт меню, который ничего
    ///   не делает и ничего не говорит, выглядит сломанным;
    /// * прогон проверки → не открывается ничего и никто не уведомляется (общая дверь изоляции
    ///   подавляет сообщение, а подавленное решение уходит строкой в журнал);
    /// * браузер отказал → причина уходит и в журнал, и человеку.
    ///
    /// ⚠️ Сама ссылка наружу не выходит ни одним из этих путей: она уезжает только в браузер.
    /// </summary>
    public void OpenEntryLink()
    {
        if (_disposed) return;

        var url = _entryLink();

        if (url.Length == 0)
        {
            _log(PanelStrings.OpenAgentNoLinkHint);
            _notify(NoticeKind.Error, PanelStrings.OpenAgentNoLinkTitle, PanelStrings.OpenAgentNoLinkHint);
            return;
        }

        OpenAndTell(url, _mayOpenBrowser, AgentBrowser.OpenWithShell, _log, _notify);
    }

    /// <summary>
    /// Открыть ссылку и, если не вышло, СКАЗАТЬ человеку — а не только записать в журнал.
    ///
    /// Отдельным методом, а не строкой внутри <see cref="OpenEntryLink"/>, по той же причине,
    /// что и <see cref="ShouldShowAbout"/>: сам значок в тестах не поднять (он встаёт на рабочий
    /// стол владельца), а обещание «отказ виден и в журнале, И человеку» обязано проверяться
    /// прогоном, который может упасть. Шов на открытие (<paramref name="opener"/>) позволяет
    /// подставить падающий браузер и убедиться, что сообщение ушло и что ССЫЛКИ в нём нет.
    /// </summary>
    /// <param name="mayOpenBrowser">
    /// Право ЭТОГО прогона открыть браузер (<see cref="RunRights.MayOpenBrowser"/>) — открытый
    /// параметр без значения по умолчанию: забыть выдать право безопасно (браузер не откроется),
    /// забыть запретить — нет.
    /// </param>
    internal static bool OpenAndTell(
        string url,
        bool mayOpenBrowser,
        Func<string, string> opener,
        Action<string> log,
        Action<NoticeKind, string, string> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);

        if (AgentBrowser.TryOpen(mayOpenBrowser, url, opener, log, out var error)) return true;

        // Не открылось — и об этом узнаёт ЧЕЛОВЕК, а не только журнал: он нажал кнопку и ждёт
        // ответа. Текст отказа приходит от двери открытия и ссылки не содержит.
        notify(NoticeKind.Error, PanelStrings.OpenAgentFailedTitle, error);
        return false;
    }

    /// <summary>
    /// Показать окно по просьбе второго запуска ярлыка.
    ///
    /// Открыто наружу, потому что просьбу принимает не связка, а окно-приёмник единого экземпляра.
    /// Решение о показе всё равно принимает предикат изоляции — внутри окна и тем же путём,
    /// что и при щелчке по значку: второго способа показать панель в проекте быть не должно.
    /// </summary>
    public void ShowFromSignal()
    {
        _log(PanelStrings.ShowFromSignalLog);
        ShowPanel();
    }

    /// <summary>Завершить панель: снять значок, закрыть окна, погасить приложение.</summary>
    public void Exit()
    {
        // Порядок важен: сначала убираем значок, потом закрываем окна, и только потом
        // завершаем приложение. Иначе на рабочем столе владельца на миг остаётся
        // «мёртвый» значок, который исчезнет только после перерисовки.
        _tray.Dispose();

        // Вспомогательные окна закрываем первыми и молча: их закрытие не должно выглядеть
        // как «человек закрыл панель».
        _settings.Close();
        _backups.Close();
        _peaks.Close();
        _about.Close();
        _issue.Close();
        _update.Close();
        _pricingHistory.Close();

        _window.CloseForExit();
        _log(PanelStrings.PanelLogExit);
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tray.Clicked -= ShowPanel;
        _tray.DoubleClicked -= ShowPanel;
        _tray.Dispose();
        _settings.Close();
        _backups.Close();
        _peaks.Close();
        _about.Close();
        _issue.Close();
        _update.Close();
        _pricingHistory.Close();
        _window.Dispose();
    }
}
