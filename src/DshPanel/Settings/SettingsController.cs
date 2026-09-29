using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>Что окно настроек знает и что ему разрешено.</summary>
public interface ISettingsControl
{
    /// <summary>Действующие настройки. Если их читать нельзя — умолчания (см. <see cref="Writable"/>).</summary>
    PanelSettings Settings { get; }

    /// <summary>Можно ли читать и менять настройки в этом прогоне.</summary>
    bool Writable { get; }

    /// <summary>Почему настройки показаны умолчаниями (пусто — всё в порядке).</summary>
    string LoadProblem { get; }

    /// <summary>Каталог, из которого панель поднимает сервер: настройка, а если она пуста — папка панели.</summary>
    string WorkDirInUse { get; }

    /// <summary>Рабочая папка, которую человек уже использует, — предложить, ничего не подставляя молча.</summary>
    IReadOnlyList<WorkDirSuggestion> Suggestions();

    bool Save(PanelSettings settings);

    /// <summary>
    /// Изменить ОДНО решение настроек и сохранить его — тем же путём, что и кнопка «Сохранить».
    ///
    /// Нужно окну копий: галочка «класть ключи в копию» живёт там, а значение у неё одно на всю
    /// панель — файл настроек. Второго места правды быть не должно: иначе одно окно молча
    /// перезапишет решение, принятое в другом.
    /// </summary>
    bool SetBackupWithKeys(bool value);

    /// <summary>
    /// Сменить активного агента — тем же путём и по той же причине, что галочки копий: значение
    /// одно на всю панель и живёт в файле настроек.
    ///
    /// Нужно главному окну: решение владельца 27.09.2026 — активный агент меняется там, где
    /// показан баланс. Второго способа записи у этого решения нет и быть не должно: иначе
    /// переключение в главном окне и выбор в настройках начали бы затирать друг друга.
    /// </summary>
    bool SetActiveAgent(string agentId);

    /// <summary>Заменить список своих каталогов ключей (кнопка «Добавить» в окне копий).</summary>
    bool SetBackupKeyDirs(IReadOnlyList<string> directories);

    /// <summary>
    /// Запомнить согласие человека на найденный сервер DSH (порт) или забыть его (значение 0).
    ///
    /// Нужно серверу: человек один раз нажал «Взять под управление» — и панель больше не
    /// переспрашивает (решение владельца 26.09.2026). Живёт это решение в файле настроек,
    /// а не в памяти контроллера, иначе не пережило бы перезапуск панели. Тем же путём
    /// записываются и остальные одиночные решения — второго места правды не заводим.
    ///
    /// Возвращает <c>false</c>, если записать не удалось (прогон без права писать настройки):
    /// тогда согласия нет, и панель спрашивает подтверждение как раньше.
    /// </summary>
    bool SetAdoptedServer(int port);

    /// <summary>
    /// Запомнить размер и положение главного окна (в пикселях экрана) — по той же причине, что
    /// и согласие на сервер: это решение человека, и оно обязано пережить перезапуск панели.
    ///
    /// Окно само настроек не читает и не пишет: их спрашивает связка при построении окна
    /// (<c>App.StartPanel</c>), иначе `--shot` — а он строит окно напрямую — читал бы личные
    /// настройки владельца и показывал их в кадре.
    ///
    /// Возвращает <c>false</c>, если записать не удалось (прогон без права писать настройки):
    /// тогда геометрия просто не запоминается, и о причине говорит строка журнала.
    /// </summary>
    bool SetWindowPlacement(int x, int y, int width, int height);

    /// <summary>
    /// Показать тему СЕЙЧАС, не записывая её в файл: человек выбирает тему глазами и должен
    /// видеть её сразу. В `settings.json` она попадёт по кнопке «Сохранить».
    /// </summary>
    void PreviewTheme(string theme);

    /// <summary>
    /// Что панель видит на этой машине — ГРУППАМИ: папки, чем поднимается сервер, движок.
    /// Это то, что показывает раздел «Сервер» (просьба владельца 28.09.2026: группировка,
    /// пути и версии).
    /// </summary>
    EnvironmentReport EnvironmentInfo();

    /// <summary>
    /// Тот же отчёт ТЕКСТОМ — для журнала и отчёта самотеста. Собирается из
    /// <see cref="EnvironmentInfo"/>: второго способа описать окружение быть не должно.
    /// </summary>
    string EnvironmentText();

    /// <summary>
    /// Показать человеку КАТАЛОГ ФАЙЛА НАСТРОЕК — в проводнике, по его щелчку.
    ///
    /// Нужно окну настроек: второе, что человек делает с настройками, — правит файл руками
    /// (пути, порт, каталоги ключей), и «где этот файл лежит» он до сих пор узнавал из строки
    /// журнала. Дверь ровно одна — <see cref="Shell.AgentBrowser.TryOpenFolder"/>.
    ///
    /// ⚠️ **Реализация по умолчанию ОТКАЗЫВАЕТ, и это не заглушка «пока».**
    /// Подмена <see cref="ISettingsControl"/> (а их в проверках две) путей не знает вовсе:
    /// притвориться, что каталог открыт, значило бы соврать человеку строкой в окне. Отказ
    /// называет причину теми же словами, что отказ владельца настроек без права, — и окно
    /// показывает его как есть. Ровно поэтому этот член добавлен с реализацией: 17 мест
    /// <c>new SettingsController(...)</c> и две подмены трогать не пришлось.
    /// </summary>
    /// <param name="error">Причина отказа словами (пусто, если открылось).</param>
    /// <returns>Открылся ли каталог.</returns>
    bool OpenSettingsFolder(out string error)
    {
        error = PanelStrings.SettingsFolderMissing;
        return false;
    }

    /// <summary>
    /// Запомнить, КОГДА страница цен разбиралась в последний раз (<see cref="PanelSettings.PricingCheckedAt"/>).
    ///
    /// Нужно контроллеру цен: именно этой отметкой закрыто решение владельца 27.09.2026 «разбирать
    /// при запуске панели, но НЕ чаще раза в сутки». Отметка в памяти означала бы разбор при каждом
    /// запуске, то есть ровно то, что решением запрещено.
    ///
    /// Реализация по умолчанию возвращает <c>false</c>: подмена владельца настроек писать не умеет,
    /// и притвориться, что запомнила, значило бы врать о соблюдении суточного правила.
    /// </summary>
    bool SetPricingCheckedAt(string stamp) => false;

    /// <summary>
    /// Запомнить ОКНА ПИКА, взятые со страницы цен (<see cref="PanelSettings.PricingPeakWindows"/>).
    ///
    /// Нужно кнопке «Обновить информацию»: её щелчок и есть подтверждение, которого требует правило
    /// панели 1.x («окна применяются только по подтверждению, цены — показ»). Автоматический разбор
    /// сюда не ходит вовсе.
    ///
    /// Реализация по умолчанию возвращает <c>false</c> — по той же причине, что у отметки времени.
    /// </summary>
    bool SetPricingPeakWindows(string json) => false;

    /// <summary>
    /// Запомнить ПОСЛЕДНЮЮ ПРОЧИТАННУЮ ТАБЛИЦУ ЦЕН (<see cref="PanelSettings.PricingLast"/>).
    ///
    /// Нужно контроллеру цен по той же причине, что и отметка времени, только с другой стороны:
    /// отметка закрывает суточный гейт, а память о таблице даёт ЧТО ПОКАЗАТЬ, когда гейт закрыт.
    /// Без неё таблица «Стоимость» пуста до нажатия «Обновить информацию» (дефект, найденный
    /// владельцем 28.09.2026).
    ///
    /// Реализация по умолчанию возвращает <c>false</c> — по той же причине, что у соседей.
    /// </summary>
    bool SetPricingLast(string json) => false;

    /// <summary>
    /// Запомнить, КОГДА выпуски панели проверялись в последний раз
    /// (<see cref="PanelSettings.UpdateCheckedAt"/>).
    ///
    /// Нужно контроллеру обновления: именно этой отметкой закрыто решение владельца «проверять
    /// при запуске панели, но НЕ чаще раза в сутки». Отметка в памяти означала бы проверку при
    /// каждом запуске, то есть ровно то, что решением запрещено.
    ///
    /// Реализация по умолчанию возвращает <c>false</c>: подмена владельца настроек писать
    /// не умеет, и притвориться, что запомнила, значило бы врать о соблюдении суточного правила.
    /// </summary>
    bool SetUpdateCheckedAt(string stamp) => false;

    /// <summary>
    /// Запомнить то, что панель узнала о последнем выпуске (версия, дата, адрес, заметки
    /// и сырое тело) — по той же причине, что отметку времени: эти значения видит окно,
    /// и они обязаны пережить перезапуск панели.
    ///
    /// Принимает ОДНУ запись (<see cref="Update.UpdateTraces"/>), а не пять аргументов:
    /// у полей один смысл — «что вышло из последней проверки», — и записаться они обязаны вместе.
    ///
    /// Реализация по умолчанию возвращает <c>false</c> — как у соседей.
    /// </summary>
    bool SetUpdateChecked(Update.UpdateTraces traces) => false;

    /// <summary>
    /// Запомнить версию, которую человек ПРОПУСТИЛ («пропустить эту версию» в окне обновления) —
    /// или забыть её (пустая строка).
    ///
    /// Нужно окну обновления: этим ответом закрыто правило «о пропущенной версии не напоминаем»
    /// (<see cref="Update.UpdateDecisions.ShouldAnnounce"/>). Живёт он в файле настроек, а не
    /// в памяти окна: ответ человека обязан пережить перезапуск панели, иначе шарик про ту же
    /// версию вернулся бы завтра.
    ///
    /// Реализация по умолчанию возвращает <c>false</c> — как у соседей: подмена владельца настроек
    /// писать не умеет, и притвориться, что пропуск запомнен, значило бы обещать человеку молчание,
    /// которого не будет.
    /// </summary>
    bool SetUpdateSkippedVersion(string version) => false;
}

/// <summary>
/// Настройки панели: чтение при старте, сохранение по кнопке и отчёт о том, что панель видит
/// вокруг себя.
///
/// Кто имеет право читать и писать — то же правило, что у автозапуска, и по той же причине:
///
/// | Прогон | Настройки |
/// |---|---|
/// | изолированный (`--run-root`) | под его корнем — можно всё: до каталогов владельца пути нет |
/// | обычный запуск человеком | настоящий `settings.json` — можно всё |
/// | прогон проверки без корня | **только умолчания**: показать чужие настройки значит вынести их в кадр или отчёт, а менять — тем более нельзя |
///
/// Третья строка — не формальность: `--shot` пишет PNG, который уезжает в README, и рабочая папка
/// человека в нём была бы личными данными.
/// </summary>
public sealed class SettingsController : ISettingsControl
{
    private readonly SettingsStore _store;
    private readonly AppPaths _paths;
    private readonly Func<DshEngine?> _locateEngine;
    private readonly Func<IServerControl?>? _server;
    private readonly Action<PanelTheme> _applyTheme;
    private readonly Action<string> _log;

    /// <param name="allowed">
    /// Можно ли читать и писать настройки: изолированный прогон или обычный запуск человеком.
    /// Значение считает <see cref="For"/> — в одном месте на всю панель.
    /// </param>
    /// <param name="canReadOwnerEnvironment">
    /// Можно ли читать окружение ПРЕЖНЕЙ панели (её `settings.json`) — только настоящий прогон
    /// человеком: там лежит путь, который человек настроил, и это его данные.
    /// </param>
    /// <param name="server">
    /// Откуда взять состояние сервера для отчёта. Провайдер, а не готовый контроллер:
    /// контроллер сервера создаётся ПОСЛЕ настроек (он берёт у них рабочую папку),
    /// и связывать их жёстко значило бы завести круг в построении.
    /// </param>
    public SettingsController(
        SettingsStore store,
        AppPaths paths,
        bool allowed,
        bool canReadOwnerEnvironment,
        Func<DshEngine?>? locateEngine,
        Func<IServerControl?>? server,
        Action<PanelTheme> applyTheme,
        Action<string> log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _locateEngine = locateEngine ?? (() => DshEngine.Locate());
        _server = server;
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Writable = allowed;
        CanReadOwnerEnvironment = canReadOwnerEnvironment;

        var load = allowed
            ? _store.Load()
            : new SettingsLoad(PanelSettings.Default, true, string.Empty);

        Settings = load.Settings;
        LoadProblem = allowed ? load.Problem : PanelStrings.SettingsReadOnlyNote;

        if (LoadProblem.Length > 0) _log(PanelStrings.SettingsLoadLog + " " + LoadProblem);

        // Тема применяется сразу при чтении: иначе человек видел бы не свою тему до первого
        // открытия настроек, а панель в трее живёт без окна месяцами.
        ApplyThemeSafely(Settings.Theme);
    }

    public PanelSettings Settings { get; private set; }

    public bool Writable { get; }

    public bool CanReadOwnerEnvironment { get; }

    public string LoadProblem { get; }

    public string WorkDirInUse
    {
        get
        {
            var configured = PanelSettings.NormalizeWorkDir(Settings.ServerWorkingDir);
            return configured.Length > 0 ? configured : _paths.DataDir;
        }
    }

    /// <summary>
    /// Право читать и писать настройки в этом прогоне — ОДНО место на всю панель.
    /// Изолированный прогон работает со своим корнем, обычный запуск человеком — со своим
    /// каталогом, прогон проверки — ни с чем.
    /// </summary>
    public static bool For(RunContext context, bool humanLaunch)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.IsIsolated || humanLaunch;
    }

    /// <summary>
    /// Предложения рабочей папки. Уже действующая папка в список не попадает: предлагать
    /// человеку то, что и так стоит, — это шум, а не забота.
    /// </summary>
    public IReadOnlyList<WorkDirSuggestion> Suggestions()
    {
        var found = WorkDirSuggestions.Find(
            CanReadOwnerEnvironment,
            Path.Combine(AppPaths.V1.DataDir, "settings.json"),
            Directory.Exists);

        var current = Path.TrimEndingDirectorySeparator(SettingsServerWorkingDir());
        return found
            .Where(s => !string.Equals(
                Path.TrimEndingDirectorySeparator(s.Path), current, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public bool Save(PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Writable)
        {
            _log(PanelStrings.SettingsLockedLog);
            return false;
        }

        var clean = SettingsStore.Clean(settings);
        if (!_store.Save(clean))
        {
            _log(PanelStrings.SettingsSaveFailedLog + " " + _store.Describe);
            return false;
        }

        Settings = clean;

        _log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.SettingsSavedLogFormat,
            Settings.Theme,
            Settings.ServerWorkingDir.Length > 0 ? DisplayMask.Path(Settings.ServerWorkingDir) : PanelStrings.WorkDirFromPanel));

        // Тема применяется ЗДЕСЬ, но её сбой сохранение НЕ отменяет: файл уже записан, значение
        // в памяти новое — и отчитаться «сохранить не удалось» значило бы соврать (живой случай
        // владельца 26.09.2026 в 23:10:12: встраивание в найденный сервер шло в фоновой нитке,
        // тема трогала объект Avalonia, и в журнал легло ложное «запомнить согласие не удалось»).
        // Сбой темы говорит своей строкой, а Save возвращает true: настройки действительно целы.
        ApplyThemeSafely(Settings.Theme);

        return true;
    }

    /// <summary>
    /// Применить тему, не роняя панель и не выдавая сбой темы за сбой настроек. Одна дверь на все
    /// три места, где тема применяется (чтение, сохранение, предпросмотр в окне настроек).
    ///
    /// ⚠️ Сбой здесь возможен и при исправной обвязке: применение уходит в нитку интерфейса,
    /// а тема — объект Avalonia. Поэтому причина называется строкой, а не проглатывается молча.
    /// </summary>
    private void ApplyThemeSafely(string theme)
    {
        try
        {
            _applyTheme(PanelSettings.ToTheme(theme));
        }
        catch (Exception ex)
        {
            _log(ThemeApply.FailedLogLine(ex));
        }
    }

    /// <summary>
    /// Запомнить размер и положение главного окна — тем же путём, что и остальные одиночные
    /// решения: копия действующих настроек, правка четырёх полей, сохранение.
    ///
    /// Своей строки в журнал здесь нет намеренно: удачную запись уже называет <see cref="Save"/>
    /// («настройки сохранены»), а строка на каждое закрытие окна была бы шумом. При отказе причину
    /// тоже называет <see cref="Save"/> (<c>SettingsLockedLog</c> / <c>SettingsSaveFailedLog</c>) —
    /// молчания не остаётся ни в одном случае.
    /// </summary>
    public bool SetWindowPlacement(int x, int y, int width, int height)
    {
        var next = SettingsStore.Clean(Settings);
        next.WindowX = x;
        next.WindowY = y;
        next.WindowWidth = width;
        next.WindowHeight = height;

        return Save(next);
    }

    public void PreviewTheme(string theme) => ApplyThemeSafely(theme);

    /// <summary>
    /// Показать каталог файла настроек в проводнике — через ОБЩУЮ дверь
    /// (<see cref="AgentBrowser.TryOpenFolder"/>) и по праву, которое панель уже проверила
    /// для себя: каталог открывает только тот прогон, КОТОРОМУ ЭТИ НАСТРОЙКИ ПРИНАДЛЕЖАТ
    /// (<see cref="Writable"/>). Прогон проверки без корня видит умолчания, а не настройки
    /// человека, — и открывать ему нечего: он показал бы чужой каталог или ничего.
    ///
    /// Путь берётся у <see cref="AppPaths"/>, а не собирается здесь: каталог настроек — свойство
    /// раскладки панели, и второе его вычисление однажды разошлось бы с тем, куда файл пишется.
    /// </summary>
    public bool OpenSettingsFolder(out string error) =>
        AgentBrowser.TryOpenFolder(
            Writable,
            Path.GetDirectoryName(_paths.SettingsFile),
            AgentBrowser.OpenWithShell,
            _log,
            out error);

    /// <summary>
    /// Сменить АКТИВНОГО агента — и сохранить это тем же путём, что кнопка «Сохранить».
    ///
    /// Нужно ГЛАВНОМУ окну (решение владельца 27.09.2026: активный агент меняется там, где
    /// показан баланс, — выпадающим списком в карточке баланса). Записывает смену ВЛАДЕЛЕЦ
    /// настроек, а не окно: у значения одно место правды — файл настроек, — и второй способ
    /// записи однажды перезаписал бы решения, принятые в другом окне. Ровно по этой причине
    /// здесь же живут галочки копий (<see cref="SetBackupWithKeys"/> и соседи).
    ///
    /// Неизвестное имя (в том числе пустое) приводит к агенту по умолчанию нормализация
    /// хранилища, а не эта дверь: она одна на чтение и на запись.
    ///
    /// Своей строки в журнал здесь нет намеренно: удачную запись называет <see cref="Save"/>
    /// («настройки сохранены»), а строка на каждое переключение была бы шумом.
    /// </summary>
    public bool SetActiveAgent(string agentId)
    {
        var next = SettingsStore.Clean(Settings);
        next.ActiveAgent = agentId ?? string.Empty;

        return Save(next);
    }

    /// <summary>
    /// Изменить одно решение и сохранить. Копия действующих настроек берётся из <see cref="Settings"/>,
    /// а не собирается заново: так решение, принятое в другом окне, не теряется.
    /// </summary>
    public bool SetBackupWithKeys(bool value)
    {
        var next = SettingsStore.Clean(Settings);
        next.BackupWithKeys = value;

        _log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.BackupKeysSavedLogFormat,
            value ? PanelStrings.BackupKeysCheck : PanelStrings.BackupKeysOff));

        return Save(next);
    }

    /// <summary>Заменить список своих каталогов ключей (кнопка «Добавить» в окне копий).</summary>
    public bool SetBackupKeyDirs(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var next = SettingsStore.Clean(Settings);
        next.BackupKeyDirs = PanelSettings.NormalizeKeyDirs(directories);

        return Save(next);
    }

    /// <summary>
    /// Запомнить время разбора страницы цен. Своей строки в журнал здесь нет: исход разбора
    /// называет контроллер цен (<c>PanelStrings.PriceLogFormat</c>), и вторая строка об одном
    /// и том же событии была бы шумом.
    /// </summary>
    public bool SetPricingCheckedAt(string stamp)
    {
        var next = SettingsStore.Clean(Settings);
        next.PricingCheckedAt = stamp ?? string.Empty;

        return Save(next);
    }

    /// <summary>
    /// Запомнить окна пика со страницы цен — тем же путём, что и остальные одиночные решения.
    /// Дверь приводится к сравнимому виду (<see cref="Pricing.PricingWindows.Normalize"/>): мусор
    /// становится пустой строкой, то есть «как в профиле агента», а не «расписание пропало».
    /// </summary>
    public bool SetPricingPeakWindows(string json)
    {
        var next = SettingsStore.Clean(Settings);
        next.PricingPeakWindows = Pricing.PricingWindows.Normalize(json);

        return Save(next);
    }

    /// <summary>
    /// Запомнить последнюю прочитанную таблицу цен — тем же путём, что окна пика. Дверь приводит
    /// запись к сравнимому виду (<see cref="Pricing.PricingMemory.Normalize"/>): мусор становится
    /// пустой строкой, то есть «панель ещё не читала цены», а не «таблица пропала».
    /// </summary>
    public bool SetPricingLast(string json)
    {
        var next = SettingsStore.Clean(Settings);
        next.PricingLast = Pricing.PricingMemory.Normalize(json);

        return Save(next);
    }

    /// <summary>
    /// Запомнить время проверки выпусков. Своей строки в журнал здесь нет: исход проверки
    /// называет контроллер обновления (<c>PanelStrings.UpdateLogFormat</c>), и вторая строка
    /// об одном и том же событии была бы шумом.
    /// </summary>
    public bool SetUpdateCheckedAt(string stamp)
    {
        var next = SettingsStore.Clean(Settings);
        next.UpdateCheckedAt = stamp ?? string.Empty;

        return Save(next);
    }

    /// <summary>
    /// Запомнить то, что панель узнала о выпуске, — ОДНОЙ записью и тем же путём, что остальные
    /// одиночные решения: копия действующих настроек, правка полей, сохранение.
    ///
    /// Пять полей правятся вместе намеренно: записанные по одному, они однажды разошлись бы —
    /// и человек увидел бы «доступна версия 2.1.0» с заметками от 2.0.0.
    /// </summary>
    public bool SetUpdateChecked(Update.UpdateTraces traces)
    {
        var next = SettingsStore.Clean(Settings);
        next.UpdateLatest = traces.Latest ?? string.Empty;
        next.UpdatePublished = traces.Published ?? string.Empty;
        next.UpdatePageUrl = traces.PageUrl ?? string.Empty;
        next.UpdateNotes = traces.Notes ?? string.Empty;
        next.UpdateNotesRaw = traces.NotesRaw ?? string.Empty;

        return Save(next);
    }

    /// <summary>
    /// ЗАПОМНИТЬ ПРОПУЩЕННУЮ ВЕРСИЮ — ответ человека на «пропустить эту версию», и он живёт
    /// в файле настроек, а не в памяти окна: иначе шарик о той же версии вернулся бы завтра,
    /// то есть панель не исполнила бы его решение.
    ///
    /// Версия приходит тегом выпуска как есть («v2.1.0»): сравнивают её по числам, а не по строкам
    /// (<see cref="Update.UpdateDecisions.IsNewer"/>), поэтому приводить её здесь нечем и незачем.
    /// Пустая строка — «пропуск забыт»: так значение и снимается.
    ///
    /// Строка в журнал пишется ТОЛЬКО после удачной записи и только когда версия непустая:
    /// сказать «запомнил», а потом не суметь — это ложь, из-за которой человек ждал бы молчания,
    /// которого не будет.
    /// </summary>
    public bool SetUpdateSkippedVersion(string version)
    {
        var next = SettingsStore.Clean(Settings);
        next.UpdateSkippedVersion = (version ?? string.Empty).Trim();

        if (!Save(next)) return false;

        if (next.UpdateSkippedVersion.Length > 0)
        {
            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateSkipSavedLogFormat,
                next.UpdateSkippedVersion));
        }

        return true;
    }

    /// <summary>
    /// Запомнить согласие на найденный сервер (порт) или забыть его (0) — тем же путём и по той же
    /// причине, что остальные одиночные решения: значение одно на всю панель и живёт в файле.
    ///
    /// Строка в журнал пишется ТОЛЬКО после удачной записи: сказать «запомнил», а потом не суметь
    /// записать — это ложь, из-за которой человек ждал бы свободного гашения, которого не будет.
    /// При неудаче причину называет <see cref="Save"/> (<c>SettingsLockedLog</c> /
    /// <c>SettingsSaveFailedLog</c>), и поведение остаётся прежним: подтверждение спрашивается.
    /// </summary>
    public bool SetAdoptedServer(int port)
    {
        var next = SettingsStore.Clean(Settings);
        next.AdoptedServerPort = PanelSettings.NormalizeAdoptedPort(port);

        if (!Save(next)) return false;

        _log(next.AdoptedServerPort > 0
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.AdoptedConsentSavedLogFormat,
                next.AdoptedServerPort)
            : PanelStrings.AdoptedConsentForgottenLog);

        return true;
    }

    /// <summary>
    /// ОТЧЁТ ОКРУЖЕНИЯ ГРУППАМИ — то, что показывает раздел «Сервер» (просьба владельца
    /// 28.09.2026: *«нормальная группировка с указанием пути; версия сейчас DSH, версия node
    /// и так далее»*). Собирается ОДНИМ разбором (<see cref="EnvironmentReport.Build"/>),
    /// из которого получается и текстовая версия для журнала и отчёта самотеста.
    /// </summary>
    public EnvironmentReport EnvironmentInfo()
    {
        var engine = _locateEngine();
        var npmRoot = engine is null ? null : Backup.BackupPlanner.NpmDirectory(engine);

        return EnvironmentReport.Build(
            engine,
            dshVersion: DshVersion(engine),
            nodeVersion: engine is null ? string.Empty : Platform.VersionProbe.Run(engine.NodePath, "--version"),
            npmVersion: EnvironmentReport.VersionOf(
                npmRoot is null ? null : Path.Combine(npmRoot, "node_modules", "npm")),
            pnpmVersion: EnvironmentReport.VersionOf(
                npmRoot is null ? null : Path.Combine(npmRoot, "node_modules", "pnpm")),
            paths: _paths,
            workDir: WorkDirInUse,
            workDirConfigured: PanelSettings.NormalizeWorkDir(Settings.ServerWorkingDir).Length > 0,
            serverPort: PanelSettings.NormalizeServerPort(Settings.ServerPort),
            serverText: ServerLine());
    }

    /// <summary>
    /// ВЕРСИЯ ДВИЖКА. Спрашивается у самого пакета (<c>package.json</c> глобальной установки) —
    /// тем же способом и той же дверью, что у копий (<c>BackupEngine.InstalledVersion</c>):
    /// второго способа узнать версию в панели быть не должно, иначе окно и опись однажды покажут
    /// разные номера.
    /// </summary>
    private static string DshVersion(Server.DshEngine? engine) =>
        Backup.BackupEngine.InstalledVersion(engine);

    /// <summary>Отчёт окружения ТЕКСТОМ — им пользуются журнал и отчёт самотеста.</summary>
    public string EnvironmentText() => EnvironmentInfo().Text();

    private string ServerLine()
    {
        var server = _server?.Invoke();
        if (server is null) return PanelStrings.ServerUnbound;

        var state = server.State;
        var format = System.Globalization.CultureInfo.CurrentCulture;

        return state.Presence switch
        {
            ServerPresence.Running => server.Owner switch
            {
                ServerOwner.Panel => string.Format(format, PanelStrings.ServerRunningOwnFormat, state.Port),
                ServerOwner.Adopted => string.Format(format, PanelStrings.ServerAdoptedFormat, state.Port),
                _ => string.Format(format, PanelStrings.ServerRunningForeignFormat, state.Port),
            },
            ServerPresence.BusyByOther => string.Format(format, PanelStrings.ServerPortBusyFormat, state.Port),
            _ => PanelStrings.ServerNotStartedWithPort,
        };
    }

    private string SettingsServerWorkingDir() => PanelSettings.NormalizeWorkDir(Settings.ServerWorkingDir);
}
