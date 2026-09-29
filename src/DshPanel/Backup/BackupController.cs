using Avalonia.Threading;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// Чем кончилась попытка снять копию в ЭТОМ прогоне: отказ прогона (<see cref="Error"/>) либо
/// результат движка копии (<see cref="Result"/>). Отдельная запись нужна не ради красоты:
/// у отказа нет ни архива, ни размеров, и выдавать его за результат движка значило бы врать.
/// </summary>
/// <param name="Rotation">
/// Строка ротации: что удалено после этой копии (<see cref="BackupRotation"/>). Пусто — удалять
/// было нечего. Нужна окну: человек, только что снявший копию, обязан видеть, что панель заодно
/// убрала, а не узнать об этом из журнала.
/// </param>
public sealed record CopyOutcome(bool Ok, string Error, BackupRunResult? Result, string Rotation = "")
{
    public string Summary() => Result is not null
        ? Result.Summary()
        : (Error.Length > 0 ? Error : PanelStrings.BackupUnknown);
}

/// <summary>Чем кончился накат: отказ прогона либо результат движка наката.</summary>
public sealed record RestoreOutcome(bool Ok, string Error, RestoreRunResult? Result)
{
    public string Summary() => Result is not null
        ? Result.Summary()
        : (Error.Length > 0 ? Error : PanelStrings.BackupUnknown);
}

/// <summary>
/// Что окно копий знает и что ему разрешено. Ровно то, что нужно экрану: папка, список,
/// снятие копии и накат. Ни одного решения о данных человека здесь нет — решения о сервере
/// принимает окно вместе с человеком, а движки копии и наката только исполняют.
/// </summary>
public interface IBackupControl
{
    /// <summary>Действующая папка копий: настройка, а пустая — умолчание v1.</summary>
    string Folder { get; }

    /// <summary>Папка взята из умолчания, а не названа человеком.</summary>
    bool FolderFromDefault { get; }

    /// <summary>Можно ли в этом прогоне снимать и раскладывать копии.</summary>
    bool Writable { get; }

    /// <summary>Готовые копии в папке, свежие сверху.</summary>
    IReadOnlyList<BackupEntry> Entries { get; }

    /// <summary>Почему список может быть неполным (пусто — всё в порядке).</summary>
    string ListProblem { get; }

    /// <summary>Версия движка, который стоит на машине сейчас (для показа перед накатом, находка В5).</summary>
    string InstalledEngineVersion { get; }

    /// <summary>Кладутся ли ключи в копию — действующее значение из настроек (по умолчанию НЕТ).</summary>
    bool WithKeys { get; }

    /// <summary>Можно ли менять состав копии в этом прогоне (право настроек: прогон проверки — нельзя).</summary>
    bool CanChangeComposition { get; }

    /// <summary>
    /// Режим объёма копии — действующее значение из настроек: автоматический, полный или свой фильтр
    /// (решение владельца для v2.2). Выбирается в окне копий, живёт в файле настроек.
    /// </summary>
    BackupScope Scope { get; }

    /// <summary>Лишние имена папок для режима «свой фильтр» — из настроек, действуют на все корни.</summary>
    IReadOnlyList<string> ExtraExclusions { get; }

    /// <summary>Сменить режим объёма. Значение сохраняется: единственное место правды — настройки.</summary>
    bool SetScope(BackupScope scope);

    /// <summary>Заменить список лишних имён папок — тем же путём, что и режим.</summary>
    bool SetExtraExclusions(IReadOnlyList<string> names);

    /// <summary>
    /// Сколько примерно займут ДАННЫЕ копии в этом режиме. Нужно окну: у полного режима человек
    /// обязан увидеть размер ДО нажатия, а не после (копия бывает в разы больше автоматической).
    /// В прогоне проверки ответ — «не знаю»: обход каталогов там не делается вовсе.
    /// </summary>
    BackupEstimate Estimate(BackupScope scope);

    /// <summary>
    /// Идёт ли копия или накат прямо сейчас. ОДНА дверь на всю панель: второй работы с копиями
    /// не начинаем ни по кнопке, ни по расписанию — копия и накат пишут в одни и те же файлы.
    /// </summary>
    bool Busy { get; }

    /// <summary>
    /// Папка перечитана НЕ по просьбе окна — например, копию сняло расписание. Окну это нужно:
    /// открытое окно копий иначе показывало бы список без только что снятой копии, и человек
    /// нажал бы «создать» второй раз.
    /// </summary>
    event Action? Changed;

    /// <summary>Каталоги ключей, которые попадут в копию: имя группы в архиве и путь с маской.</summary>
    IReadOnlyList<string> KeyDirectories { get; }

    /// <summary>Каталоги ключей, найденные у человека и ещё не взятые: предлагаем кнопкой.</summary>
    IReadOnlyList<KeyDirSuggestion> KeySuggestions { get; }

    /// <summary>Изменить разрешение на ключи. Значение сохраняется: единственное место правды — настройки.</summary>
    bool SetWithKeys(bool value);

    /// <summary>Добавить каталог ключей в настройки (кнопка «Добавить»).</summary>
    bool AddKeyDirectory(string path);

    /// <summary>Перечитать папку. Только чтение.</summary>
    void Refresh();

    /// <summary>
    /// ПОКАЗАТЬ ПАПКУ КОПИЙ в проводнике — по щелчку человека (п. 31 <c>docs\DESIGN.md</c>:
    /// *«добавить возможность открыть папку через проводник рядом с указанием пути»*).
    ///
    /// Право: открывает только тот прогон, КОТОРОМУ ЭТИ НАСТРОЙКИ ПРИНАДЛЕЖАТ, — то же право,
    /// которым в этом домене меняется состав копии (<see cref="Writable"/> и
    /// <see cref="CanChangeComposition"/>). Прогон проверки не открывает ничего: красная линия 8
    /// («изолированный прогон не должен показывать ничего») и кадр `--shot`, который уезжает в README.
    ///
    /// Папки может не быть вовсе — до первой копии её нет: тогда честный отказ СЛОВАМИ, а не
    /// пустой проводник (см. <c>Shell\AgentBrowser.TryOpenFolder</c>, <c>mustExist</c>).
    ///
    /// ⚠️ **Реализация по умолчанию ОТКАЗЫВАЕТ, и это не заглушка «пока».** Подмена
    /// <see cref="IBackupControl"/> (её в проверках не одна) путей не знает: притвориться, что
    /// папка открыта, значило бы соврать человеку строкой в окне. Отказ называет причину теми же
    /// словами, что отказ прогона без права, — и окно показывает его как есть. Ровно поэтому член
    /// добавлен С реализацией: подмены трогать не пришлось.
    /// </summary>
    /// <param name="error">Причина отказа словами (пусто, если открылось).</param>
    /// <returns>Открылась ли папка.</returns>
    bool OpenFolder(out string error)
    {
        error = PanelStrings.BackupFolderMissing;
        return false;
    }

    /// <summary>Куда ляжет новая копия — имя собирается здесь, по правилу v1.</summary>
    string NextArchivePath();

    /// <summary>
    /// Снять копию. <paramref name="shareable"/> — РАЗОВОЕ решение про ЭТУ копию: файл ключей
    /// доступа (<c>~/.dsh/.credentials.yaml</c> — ключ модели и секрет входа) в архив не кладётся
    /// вовсе. Решение владельца 27.09.2026 (п. 11 <c>docs\DESIGN.md</c>): передача — отдельная
    /// фича на одну копию, а не настройка, которую наследуют ночные копии.
    ///
    /// ⚠️ Параметр ОБЯЗАТЕЛЬНЫЙ и приходит от вызывающего, а не берётся из недр: решения о данных
    /// человека в панели приходят открытыми (тот же уговор, что у права прогона). Расписание
    /// передаёт <c>false</c> всегда — ночная копия передаваемой не бывает НИКОГДА; окно — по своей
    /// галочке; режим без окна — по ключу <c>--shareable</c>.
    ///
    /// Сочетание с приватными ключами запрещено и отвергается планом
    /// (<see cref="BackupPlanner.ShareableWithKeysRefusal"/>) — здесь запрет не повторяется, чтобы
    /// существовать в одном месте.
    /// </summary>
    CopyOutcome CreateCopy(string archivePath, bool serverRunning, bool shareable, Action<string>? progress = null);

    /// <summary>Прочитать архив и показать, что он вернёт. <paramref name="withKeys"/> — согласие на ключи.</summary>
    RestorePlan PlanRestore(string archivePath, bool withEngine, bool withPanel, bool withKeys);

    RestoreOutcome RunRestore(
        RestorePlan plan,
        bool withEngine,
        bool withPanel,
        bool withKeys,
        bool serverRunning,
        Action<string>? progress = null);
}

/// <summary>
/// Копии и накат для окна: папка, список, запуск. Собирает РОВНО то, что уже умеют движки
/// (<see cref="BackupEngine"/> и <c>Restore\RestoreEngine</c>), и ничего не решает за человека.
///
/// Что закреплено за этим классом и только за ним:
///
/// 1. **Право прогона.** Полная копия и накат допустимы в ИЗОЛИРОВАННОМ прогоне (там все пути
///    под своим корнем) или в обычном запуске человеком (он для этого панель и открыл). Прогон
///    проверки без корня не снимает копий вовсе — его архив лёг бы в каталог владельца
///    (красная линия 5). Право не «предупреждение»: без него методы отказывают.
/// 2. **Где папка.** Настройка, а пустая — умолчание v1; раскрытие переменных окружения живёт
///    в <see cref="BackupNaming"/>.
/// 3. **Имя архива.** Правило v1 (<see cref="BackupNaming.ArchiveName"/>): полная копия сейчас
///    одна, тонкая появится вместе с тремя режимами объёма (v2.2), и имя уже знает оба вида.
/// 4. **Режим объёма** (v2.2): <c>auto</c> / <c>full</c> / <c>custom</c> и лишние имена папок
///    приходят из настроек и уезжают в план (<see cref="BackupPlanner.Full"/>). Решение о режиме
///    принимает человек в окне копий; здесь оно только читается и исполняется.
/// 5. **Ротация** (v2.2): после УДАЧНОЙ копии — и ручной, и по расписанию — старая лишняя копия
///    удаляется (<see cref="BackupRotation"/>), и в журнал ложится, что именно удалено и почему.
/// 6. **Сериализация.** Копия и накат пишут в одни и те же файлы, поэтому вторую работу не
///    начинаем, пока идёт первая — ни по кнопке, ни по расписанию. Отказ называет причину словами.
/// 7. **Расписание** (v2.2): часы, решение «пора» (<see cref="BackupSchedule"/>) и снятие копии
///    НА ХОДУ, без гашения сервера — решение владельца 26.09.2026, разбор ниже.
///
/// ⚠️ **Чего здесь нет:** гашения сервера. Работает ли сервер в момент копии и гасить ли его —
/// решение человека (вопрос 10, отвечено владельцем 26.09.2026), и оно приходит сюда готовым
/// ответом — так же, как в движки копии и наката.
///
/// ⚠️ **Работающий сервер ради копии НЕ гасится** — решение владельца 26.09.2026, и довод его:
/// «если у пользователя сервер работает не просто, а работает агент ночью, пока его нет,
/// остановка сервера может погубить действующую работу — агент у пользователя живёт постоянно,
/// как ассистент». Прежнее требование аудита «перед копированием останавливать сервер» (находка В2)
/// этим **отменено**. Копия по расписанию снимается на ходу, но человек об этом ПРЕДУПРЕЖДАЕТСЯ
/// (сообщение и строка отчёта: работающий процесс мог не дописать файлы), и ему сказано словами,
/// где включается расписание: целостную копию снимают вручную, когда работа не идёт.
///
/// ⚠️ **Ручной путь не меняется.** Перед копией по кнопке панель по-прежнему ПРЕДЛАГАЕТ погасить
/// сервер (<c>Views\BackupStopWindow</c>) — это выбор человека, а не автоматика. Расписание
/// спрашивать не у кого: оно работает, когда человека нет.
/// </summary>
public sealed class BackupController : IBackupControl, IDisposable
{
    /// <summary>
    /// Как часто часы автокопии смотрят, пора ли снимать копию. Пять минут выбраны по самой
    /// грубой из величин, которые здесь есть: минимальный интервал расписания — час, а сторож
    /// после неудачной попытки — четверть часа. Смотреть чаще незачем (копия не появится
    /// «точнее»), а часы расходуют на это чтение папки копий.
    /// </summary>
    public static readonly TimeSpan ScheduleTick = TimeSpan.FromMinutes(5);

    private readonly AppPaths _paths;
    private readonly Func<PanelSettings> _settings;
    private readonly Func<DshEngine?> _locateEngine;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;

    /// <summary>
    /// Работает ли сервер ПРЯМО СЕЙЧАС. Нужно ровно для одного: копия по расписанию снимается
    /// на ходу, и об этом человека надо предупредить. Контроллер гасить сервер не станет —
    /// это решение человека (см. комментарий класса); здесь только правда о нём.
    /// </summary>
    private readonly Func<bool> _serverRunning;

    /// <summary>
    /// Сообщение человеку. Решение о показе принимает вызывающий: там стоит общая дверь изоляции
    /// (<see cref="IsolationRules.ShouldNotify"/>), и в изолированном прогоне она молчит.
    /// </summary>
    private readonly Action<NoticeKind, string, string> _notify;

    /// <summary>
    /// Куда вернуться после работы в фоне (нитка интерфейса). Фоновая копия заканчивается
    /// в чужой нити, а и сообщение, и журнал принадлежат панели.
    /// </summary>
    private readonly Action<Action> _dispatch;

    /// <summary>
    /// Замок сериализации: 0 — свободно, 1 — идёт копия или накат. Именно счётчиком, а не полем
    /// <c>bool</c>: работу начинают и нитка интерфейса (кнопка), и нитка расписания, поэтому
    /// «проверить и занять» обязано быть одной неделимой операцией (<see cref="Interlocked"/>).
    /// </summary>
    private int _busy;

    private DispatcherTimer? _timer;

    /// <summary>Когда была последняя ПОПЫТКА (удачная или нет) в этом прогоне — для сторожа повторов.</summary>
    private DateTimeOffset? _lastAttempt;

    /// <summary>Сколько тактов часов прошло: первый такт после запуска подписывается «догоняем пропущенное».</summary>
    private int _ticks;

    /// <summary>
    /// Кто владеет настройками. Нужен ровно для одного: положить в них решение о ключах,
    /// принятое в окне копий. Значение живёт в файле настроек, и владелец у него ОДИН —
    /// <see cref="SettingsController"/>; держать здесь второй экземпляр настроек значило бы
    /// завести два места правды, которые однажды разойдутся (одно окно перезапишет другое).
    ///
    /// Пусто — состав копии менять нельзя (прогон проверки, тесты): окно покажет значение
    /// и погасит галочки.
    /// </summary>
    private readonly ISettingsControl? _settingsOwner;

    /// <summary>
    /// Держать раскладку внутри корня прогона (правило — <c>Restore\RestoreConfine.cs</c>). Ставит
    /// изолированный прогон: цели групп движка и Node считаются от ЭТОЙ машины и лежат в её
    /// глобальной установке, то есть вне корня. У обычного запуска человеком — <c>false</c>,
    /// и там движок возвращается по своему месту, как и было решено (окно копий).
    /// </summary>
    private readonly bool _confineToRunRoot;

    public BackupController(
        AppPaths paths,
        Func<PanelSettings> settings,
        bool allowed,
        Func<DshEngine?>? locateEngine = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        ISettingsControl? settingsOwner = null,
        bool confineToRunRoot = false,
        bool isolatedRun = false,
        Func<bool>? serverRunning = null,
        Action<NoticeKind, string, string>? notify = null,
        Action<Action>? dispatch = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _locateEngine = locateEngine ?? (() => DshEngine.Locate());
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log ?? (_ => { });
        _settingsOwner = settingsOwner;
        _confineToRunRoot = confineToRunRoot;
        _serverRunning = serverRunning ?? (() => false);
        _notify = notify ?? ((_, _, _) => { });
        _dispatch = dispatch ?? (action => action());

        Writable = allowed;
        IsolatedRun = isolatedRun;
        Entries = Array.Empty<BackupEntry>();
    }

    /// <summary>
    /// Прогон изолированный: автоматической работы по расписанию в нём нет (красная линия —
    /// «фоновые задачи в изоляции не работают»). Право снимать копии у него при этом есть:
    /// их просит человек или прогон проверки, а не часы.
    /// </summary>
    public bool IsolatedRun { get; }

    /// <summary>
    /// Право снимать и раскладывать копии в этом прогоне. Тело то же, что у настроек
    /// (<see cref="SettingsController.For"/>), но имя — про свой домен: правило то же, причина
    /// своя (красная линия 5 — «полная копия и накат только в изоляции или человеком»).
    /// </summary>
    public static bool For(RunContext context, bool humanLaunch)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.IsIsolated || humanLaunch;
    }

    public bool Writable { get; }

    /// <summary>
    /// Чем открывать папку копий в проводнике. Шов ТОЛЬКО для проверок: настоящее открытие
    /// показало бы окно на рабочем столе владельца, а проверить нужно ровно одно — что дверь
    /// зовёт открытие С ПУТЁМ ПАПКИ КОПИЙ и ровно один раз, а без права не зовёт вовсе.
    /// У обычного запуска здесь всегда <see cref="AgentBrowser.OpenWithShell"/>.
    /// </summary>
    internal Func<string, string> FolderOpener { get; set; } = AgentBrowser.OpenWithShell;

    public IReadOnlyList<BackupEntry> Entries { get; private set; }

    public string ListProblem { get; private set; } = string.Empty;

    public string Folder => BackupNaming.Folder(_settings().BackupFolder);

    public bool FolderFromDefault => BackupNaming.IsDefault(_settings().BackupFolder);

    public string InstalledEngineVersion => BackupEngine.InstalledVersion(_locateEngine());

    /// <summary>Кладутся ли ключи в копию — действующее значение из настроек (по умолчанию НЕТ).</summary>
    public bool WithKeys => _settings().BackupWithKeys;

    /// <summary>
    /// Можно ли менять состав копии в этом прогоне: право то же, что у настроек
    /// (<see cref="ISettingsControl.Writable"/>). Прогон проверки менять не может — и это важно
    /// не «для порядка»: в кадр README не должно попасть ни одного каталога ключей владельца.
    /// </summary>
    public bool CanChangeComposition => _settingsOwner?.Writable == true;

    /// <summary>
    /// Режим объёма из настроек. Незнакомое значение в файле даёт автоматический режим
    /// (<see cref="BackupScopeDecisions.Parse"/>) — то поведение, которое было у панели
    /// до появления режимов.
    /// </summary>
    public BackupScope Scope => BackupScopeDecisions.Parse(_settings().BackupScope);

    /// <summary>Лишние имена папок из настроек — в сравнимом виде, без пустых и повторов.</summary>
    public IReadOnlyList<string> ExtraExclusions =>
        BackupScopeDecisions.NormalizeExclusions(_settings().BackupExtraExclusions);

    /// <summary>Идёт ли копия или накат прямо сейчас — одна дверь на всю панель.</summary>
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Сменить режим объёма. Значение уходит владельцу настроек — то есть в файл, — поэтому
    /// второго места правды не появляется: и окно копий, и окно настроек показывают одно и то же.
    ///
    /// Общий <see cref="ISettingsControl.Save"/> берётся осознанно, без отдельного метода на каждый
    /// ключ: значений у режима два (сам режим и список имён), и оба обязаны сохраняться ОДНИМ
    /// нажатием, а не двумя записями файла подряд.
    /// </summary>
    public bool SetScope(BackupScope scope)
    {
        var saved = Save(settings =>
        {
            settings.BackupScope = BackupScopeDecisions.Token(scope);
        });

        _log(saved
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScopeSavedLogFormat, ScopeText(scope))
            : PanelStrings.BackupKeysLockedLog);

        return saved;
    }

    /// <summary>Заменить список лишних имён папок — тем же путём и по той же причине, что режим.</summary>
    public bool SetExtraExclusions(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var clean = BackupScopeDecisions.NormalizeExclusions(names);

        var saved = Save(settings =>
        {
            settings.BackupExtraExclusions = clean;
        });

        _log(saved
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScopeExtraSavedLogFormat,
                clean.Count, string.Join(", ", clean))
            : PanelStrings.BackupKeysLockedLog);

        return saved;
    }

    /// <summary>
    /// Сколько примерно займут данные копии в этом режиме. Ответ «не знаю» — это тоже ответ,
    /// и он честный: в прогоне проверки обход каталогов не делается вовсе (иначе `--shot` читал бы
    /// данные владельца), а обход чужого дерева может и не пережить отказ доступа. Врать числом
    /// здесь нельзя: по этой цифре человек решает, снимать ли полную копию.
    /// </summary>
    public BackupEstimate Estimate(BackupScope scope)
    {
        if (!Writable) return new BackupEstimate(false, 0);

        try
        {
            var plan = BuildPlan(_settings(), _locateEngine(), scope, shareable: false);
            if (!plan.Ok) return new BackupEstimate(false, 0);

            return new BackupEstimate(true, ZipWriter.EstimateBytes(plan.Sources));
        }
        catch
        {
            return new BackupEstimate(false, 0);
        }
    }

    /// <summary>
    /// Изменить настройки ОДНИМ сохранением и вернуть, удалось ли. Пусто — владельца настроек нет
    /// (прогон проверки, проверка без экрана): тогда менять нечего, и это не ошибка человека.
    /// </summary>
    private bool Save(Action<PanelSettings> change)
    {
        if (_settingsOwner is null || !_settingsOwner.Writable) return false;

        var next = SettingsStore.Clean(_settingsOwner.Settings);
        change(next);

        return _settingsOwner.Save(next);
    }

    /// <summary>
    /// Каталоги ключей, которые попадут в копию: имя группы в архиве и путь через маску.
    /// В прогоне проверки — пусто, и каталоги владельца не читаются вовсе (как и список копий).
    /// </summary>
    public IReadOnlyList<string> KeyDirectories
    {
        get
        {
            if (!Writable || !WithKeys) return Array.Empty<string>();

            return BackupPlanner.KeyRoots(_paths, _settings().BackupKeyDirs)
                .Select(root => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.BackupKeysItemFormat,
                    root.Prefix,
                    DisplayMask.Path(root.Directory)))
                .ToArray();
        }
    }

    /// <summary>
    /// Каталоги ключей, найденные у человека и ещё не взятые в настройки. Показываются кнопкой
    /// «Добавить»: найденное предлагаем, а не подставляем молча.
    /// </summary>
    public IReadOnlyList<KeyDirSuggestion> KeySuggestions =>
        Writable
            ? KeyDirSuggestions.Find(Directory.Exists, _settings().BackupKeyDirs)
            : Array.Empty<KeyDirSuggestion>();

    /// <summary>
    /// Показать папку копий в проводнике — через ОБЩУЮ дверь (<see cref="AgentBrowser.TryOpenFolder"/>)
    /// и по праву, которое панель уже проверила для себя: папка приходит ИЗ НАСТРОЕК, поэтому мало
    /// права снимать копии (<see cref="Writable"/>) — нужно ещё, чтобы эти настройки были свои
    /// (<see cref="CanChangeComposition"/>). Прогон проверки видит умолчания, а не настройки
    /// человека, и открывать ему нечего.
    ///
    /// Путь берётся у <see cref="Folder"/>, а не собирается здесь: папка копий — одно место правды
    /// на всю панель, и второе её вычисление однажды разошлось бы с тем, куда архивы пишутся.
    /// </summary>
    public bool OpenFolder(out string error) =>
        AgentBrowser.TryOpenFolder(
            Writable && CanChangeComposition,
            Folder,
            AgentBrowser.FolderDoorWords.Backups,
            mustExist: true,
            FolderOpener,
            _log,
            out error);

    /// <summary>
    /// Перечитать папку. Причина недоступности едет рядом со списком, а не теряется.
    ///
    /// ⚠️ **Прогон проверки не читает папку копий вовсе.** Дело не в аккуратности: `--shot` снимает
    /// кадр, который уезжает в README, и список настоящих копий владельца (даты, размеры, состав)
    /// был бы в нём личными данными. Это то же правило, по которому съёмка баланса идёт без права
    /// читать ключ (красные линии 4 и 7).
    /// </summary>
    public void Refresh()
    {
        if (!Writable)
        {
            Entries = Array.Empty<BackupEntry>();
            ListProblem = PanelStrings.BackupNotAllowed;
            return;
        }

        var list = BackupFolder.List(Folder);
        Entries = list.Entries;
        ListProblem = list.Problem;
    }

    /// <summary>
    /// Изменить разрешение на ключи. Значение уходит владельцу настроек — то есть в файл, —
    /// поэтому второго места правды не появляется: и окно настроек, и окно копий показывают
    /// одно и то же значение.
    /// </summary>
    public bool SetWithKeys(bool value)
    {
        if (_settingsOwner is null) return false;

        var ok = _settingsOwner.SetBackupWithKeys(value);

        _log(ok
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupKeysSavedLogFormat,
                value ? PanelStrings.BackupKeysCheck : PanelStrings.BackupKeysOff)
            : PanelStrings.BackupKeysLockedLog);

        return ok;
    }

    /// <summary>Добавить каталог ключей в настройки — тем же путём (кнопка «Добавить»).</summary>
    public bool AddKeyDirectory(string path)
    {
        if (_settingsOwner is null || string.IsNullOrWhiteSpace(path)) return false;

        var next = PanelSettings.NormalizeKeyDirs(_settingsOwner.Settings.BackupKeyDirs);
        next.Add(path);

        return _settingsOwner.SetBackupKeyDirs(next);
    }

    public string NextArchivePath() =>
        Path.Combine(Folder, BackupNaming.ArchiveName(_clock(), withEngine: true));

    /// <summary>
    /// Название режима объёма словами — ОДНО место на всю панель: то же слово человек читает
    /// в окне копий и в журнале. Двух списков подписей быть не должно: они однажды разойдутся,
    /// и «полный» в журнале перестанет означать «полный» в окне.
    /// </summary>
    public static string ScopeText(BackupScope scope) => scope switch
    {
        BackupScope.Full => PanelStrings.BackupScopeFull,
        BackupScope.Custom => PanelStrings.BackupScopeCustom,
        _ => PanelStrings.BackupScopeAuto,
    };

    /// <summary>
    /// Состав копии: корни (<see cref="BackupPlanner.Full"/>) плюс режим объёма и лишние имена.
    /// ОДНА сборка плана на всю панель — её берут и снятие копии, и замер размера, поэтому
    /// обещанный в окне размер не может разойтись с тем, что действительно копируется.
    ///
    /// <paramref name="shareable"/> приходит ПАРАМЕТРОМ, а не берётся из настроек: «копия для
    /// передачи» — разовое решение на одну копию (п. 11 <c>docs\DESIGN.md</c>), и замер размера
    /// обязан считать по тому же решению, что и сама копия. Замеру передаётся <c>false</c>:
    /// он про объём, а исключение одного файла ключа объёма не меняет.
    /// </summary>
    private BackupPlan BuildPlan(PanelSettings settings, DshEngine? engine, BackupScope scope, bool shareable) =>
        BackupPlanner.Full(
            _paths,
            settings.ServerWorkingDir,
            engine,
            withKeys: settings.BackupWithKeys,
            keyDirectories: settings.BackupKeyDirs,
            shareable: shareable,
            scope: scope,
            extraExclusions: settings.BackupExtraExclusions);

    /// <summary>
    /// Снять копию. <paramref name="serverRunning"/> — ПРАВДА О СЕРВЕРЕ на момент работы: работает ли
    /// он сейчас. Движок копии пишет это в отчёт словами (находка В2), поэтому передавать сюда
    /// намерение человека нельзя: до 26.09.2026 окно при выборе «погасить» сообщало <c>false</c>,
    /// не погасив сервера, — и из отчёта пропадала честная оговорка «копия снята на ходу».
    /// Гасить сервер этот метод не станет никогда: гасит вызывающий и только по решению человека.
    ///
    /// <paramref name="shareable"/> — разовое решение про ЭТУ копию (см. <see cref="IBackupControl.CreateCopy"/>).
    /// </summary>
    public CopyOutcome CreateCopy(
        string archivePath, bool serverRunning, bool shareable, Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!Writable)
        {
            _log(PanelStrings.BackupNotAllowed);
            return new CopyOutcome(false, PanelStrings.BackupNotAllowed, null);
        }

        // Сериализация: копия и накат пишут в одни и те же файлы. Вторая работа не начинается —
        // ни по кнопке, ни по расписанию, — и отказ НАЗЫВАЕТ причину словами, а не молчит.
        if (!TryBegin())
        {
            _log(PanelStrings.BackupBusy);
            return new CopyOutcome(false, PanelStrings.BackupBusy, null);
        }

        _lastAttempt = _clock();

        try
        {
            var engine = _locateEngine();
            var settings = _settings();

            // Состав копии называется СТРОКОЙ В ЖУРНАЛЕ: своя копия уносит ключ доступа, копия для
            // передачи — нет, и это разные вещи для человека. Слово берётся из словаря, а не
            // сочиняется здесь: тем же словом копия для передачи называется в отчёте движка.
            _log(shareable ? PanelStrings.BackupShareableNote : PanelStrings.BackupShareableOff);

            // Режим объёма и лишние имена приходят ИЗ НАСТРОЕК и передаются в план: состав копии —
            // ЧИСТОЕ решение, а настройки читает вызывающий. Передача — наоборот, решение этого
            // вызова: она приходит параметром и в настройках не живёт вовсе.
            var plan = BuildPlan(settings, engine, BackupScopeDecisions.Parse(settings.BackupScope), shareable);

            var result = BackupEngine.Run(
                plan,
                new BackupRequest(archivePath, ServerRunning: serverRunning, Verify: true),
                _paths,
                engine,
                progress);

            // Ротация — ТОЛЬКО после удачной копии: уборка не имеет права забрать старую копию
            // ради новой, которой не получилось. Идёт и за ручной копией, и за копией по расписанию:
            // путь один (см. комментарий класса).
            var rotation = result.Ok ? Rotate(archivePath) : string.Empty;

            return new CopyOutcome(result.Ok, result.Error, result, rotation);
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Занять дверь работы с копиями. <c>false</c> — уже занято: второй работы не начинаем.
    /// </summary>
    private bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    private void End() => Interlocked.Exchange(ref _busy, 0);

    /// <summary>
    /// РОТАЦИЯ: хранить N последних копий, старые удалять. Возвращает строку для окна
    /// (пусто — удалять было нечего) и всегда пишет в журнал, ЧТО удалено и почему.
    ///
    /// Правила — в <see cref="BackupRotation"/>: свежую не удаляем никогда, предохранительные
    /// копии перед накатом не трогаем вовсе, чужие файлы не наши. Здесь только чтение папки,
    /// вызов и журнал.
    /// </summary>
    private string Rotate(string archivePath)
    {
        try
        {
            var plan = BackupRotation.Plan(
                BackupRotation.List(Folder),
                _settings().BackupKeepCount,
                Path.GetFileName(archivePath));

            // Причина ложится в журнал ВСЕГДА, даже когда удалять нечего: «сколько копий храним
            // и почему ничего не убрали» — это ответ на вопрос человека, а не подробность.
            _log(plan.Reason);

            if (plan.Delete.Count == 0) return string.Empty;

            var removal = BackupRotation.Apply(plan);
            var text = removal.Summary();

            if (text.Length > 0) _log(text);

            return text;
        }
        catch (Exception error)
        {
            // Уборка не имеет права уронить уже снятую копию: причина называется, копия остаётся.
            var text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupRotationFailedFormat, 0, BackupFormat.Size(0), error.GetType().Name);

            _log(text);
            return string.Empty;
        }
    }

    /// <summary>
    /// Прочитать архив и решить, что он вернёт на ЭТОЙ машине. Ничего не трогает — это и есть
    /// «показать человеку до наката».
    /// </summary>
    public RestorePlan PlanRestore(string archivePath, bool withEngine, bool withPanel, bool withKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var options = new RestoreOptions(
            WithEngine: withEngine,
            WithPanel: withPanel,
            WithKeys: withKeys,
            SafetyCopy: true,
            ConfineToRunRoot: _confineToRunRoot);

        return RestoreEngine.Plan(archivePath, _paths, _settings().ServerWorkingDir, options, _locateEngine());
    }

    /// <summary>
    /// Разложить копию. Предохранительная копия снимается всегда — она и есть путь назад,
    /// и её отсутствие останавливает накат внутри движка, а не здесь.
    ///
    /// Дверь работы та же, что у копии: накат поверх идущей копии — это два писателя в одних
    /// и тех же файлах, и второй не начинается (см. <see cref="Busy"/>).
    /// </summary>
    public RestoreOutcome RunRestore(
        RestorePlan plan,
        bool withEngine,
        bool withPanel,
        bool withKeys,
        bool serverRunning,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!Writable)
        {
            _log(PanelStrings.BackupNotAllowed);
            return new RestoreOutcome(false, PanelStrings.BackupNotAllowed, null);
        }

        if (!TryBegin())
        {
            _log(PanelStrings.BackupBusy);
            return new RestoreOutcome(false, PanelStrings.BackupBusy, null);
        }

        try
        {
            var options = new RestoreOptions(
                WithEngine: withEngine,
                WithPanel: withPanel,
                SafetyCopy: true,
                ServerRunning: serverRunning,
                WithKeys: withKeys,
                ConfineToRunRoot: _confineToRunRoot);

            var result = RestoreEngine.Run(plan, options, _paths, _locateEngine(), progress);

            return new RestoreOutcome(result.Ok, result.Error, result);
        }
        finally
        {
            End();
        }
    }

    // --- расписание автокопий (v2.2) -----------------------------------------
    //
    // Устройство скопировано у часов баланса (<c>Balance\BalanceController</c>) намеренно: там
    // уже есть и таймер, и чистое решение «пора ли», и общая дверь изоляции. Второе устройство
    // тех же часов в панели означало бы второе место, где решается «панель делает это сама».

    /// <summary>
    /// Сколько последних копий хранить — из настроек (нужно проверкам и журналу).
    /// </summary>
    public int KeepCount => _settings().BackupKeepCount;

    /// <summary>
    /// Запустить часы автокопии. В прогоне проверки и в изолированном прогоне часов не будет
    /// вовсе, и это в журнал: автоматической копии там не бывает (красная линия — «фоновые задачи
    /// в изоляции не работают»). Подавленное решение уходит СТРОКОЙ, а не молчанием: «панель
    /// ничего не сняла» обязано быть объяснимо.
    /// </summary>
    public void Start()
    {
        if (!Writable)
        {
            _log(PanelStrings.BackupNotAllowed);
            return;
        }

        if (!IsolationRules.ShouldRunScheduledWork(IsolatedRun))
        {
            _log(PanelStrings.BackupScheduleSuppressedLog);
            return;
        }

        _timer ??= new DispatcherTimer { Interval = ScheduleTick };
        if (_timer.IsEnabled) return;

        _timer.Tick += (_, _) => Tick(_clock());
        _timer.Start();
        _log(PanelStrings.BackupScheduleClockLog);

        // И сразу первый такт: панель только что запустилась, и если копии не было сутки —
        // она снимается СЕЙЧАС, а не через пять минут (решение владельца: догоняем пропущенное).
        Tick(_clock());
    }

    /// <summary>
    /// Один такт часов: решить, пора ли, и — если пора — снять копию В ФОНЕ.
    ///
    /// Возвращает решение (для проверок и журнала): «пора» без запущенной копии бывает, и это
    /// не ошибка — например, если прямо сейчас идёт другая работа с копиями.
    /// </summary>
    public BackupDue Tick(DateTimeOffset now)
    {
        // Красная линия: в изоляции и в прогоне проверки фоновые задачи не работают. Строка об этом
        // уже легла в журнал при Start(); здесь молча выходим — иначе журнал получал бы её каждые
        // пять минут, и настоящие строки в нём потерялись бы.
        if (!IsolationRules.ShouldRunScheduledWork(IsolatedRun) || !Writable)
        {
            return new BackupDue(false, PanelStrings.BackupScheduleSuppressedLog);
        }

        // Идёт копия или накат — вторую работу не начинаем (сериализация). Проверка стоит ДО
        // чтения папки: во время копии её незачем и читать.
        if (Busy) return new BackupDue(false, PanelStrings.BackupBusy);

        var justStarted = _ticks++ == 0;
        var settings = _settings();

        var due = BackupSchedule.Due(
            settings.BackupScheduleEnabled,
            settings.BackupEveryHours,
            NewestCopyTime(),
            now,
            justStarted,
            _lastAttempt);

        // Решение словами — в журнал: на первом такте после запуска ВСЕГДА (человек обязан видеть,
        // почему копии нет), дальше только когда пора. Иначе журнал заливался бы одной и той же
        // строкой «рано: прошло 3 ч из 24» каждые пять минут.
        if (due.Take || justStarted)
        {
            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScheduleLogFormat, due.Reason));
        }

        if (due.Take) StartScheduledCopy();

        return due;
    }

    /// <summary>
    /// Момент ПРОШЛОЙ копии — из ИМЕНИ самого свежего НАШЕГО архива <c>dsh2-backup-*</c> в папке копий.
    ///
    /// Именно из имени, и это не мелочь: память не переживает перезапуск панели, а файл — да.
    /// Поэтому «создать копию сейчас» кнопкой не сбивает отсчёт: следующая копия по расписанию
    /// считается от неё так же, как от любой другой. Опись для этого не читается: чтобы узнать
    /// момент, пришлось бы открывать каждый архив на каждом такте часов.
    ///
    /// ⚠️ **Копия панели 1.x отсчёт не сдвигает** (<see cref="BackupFileKind.Legacy"/> отсеивается
    /// здесь, и второй раз — в <see cref="BackupNaming.TimeFromName"/>). Папка копий у двух панелей
    /// общая, и ночью 26.09.2026 это уже вышло боком: 2.0 отсчитала свои сутки от ЧУЖОГО архива
    /// («автокопия: рано: прошло 23 ч 34 мин из 24 ч») и не сняла свою копию. Фильтр стоит ДВАЖДЫ
    /// намеренно: здесь — чтобы было видно, о чём речь, в правиле имени — чтобы чужие часы не сбились
    /// ни у какого другого читателя.
    /// </summary>
    private DateTimeOffset? NewestCopyTime()
    {
        DateTimeOffset? newest = null;

        foreach (var file in BackupRotation.List(Folder))
        {
            if (file.Kind != BackupFileKind.Copy) continue;

            var moment = file.CreatedAt;
            if (moment is null) continue;

            if (newest is null || moment.Value > newest.Value) newest = moment;
        }

        return newest;
    }

    /// <summary>
    /// Снять копию по расписанию — В ФОНЕ и НА ХОДУ.
    ///
    /// Ни гашения сервера, ни вопроса: расписание работает, когда человека нет, и останавливать
    /// работающего агента ради копии запрещено решением владельца 26.09.2026. Но человек о копии
    /// на ходу ПРЕДУПРЕЖДАЕТСЯ — сообщением, и оговорка «на ходу» остаётся в отчёте движка
    /// (<see cref="BackupRequest.ServerRunning"/> — правда о сервере, а не намерение).
    ///
    /// ⚠️ <c>shareable: false</c> — НАВСЕГДА и не «по умолчанию». Решение владельца 27.09.2026
    /// (п. 11): передача — разовое действие человека на одну копию, и ночная копия передаваемой
    /// не бывает НИКОГДА. Прежде её таковой делала настройка, унаследованная расписанием, — именно
    /// это и было бедой, названной владельцем (*«смешалась с настройками резервных копий»*).
    /// </summary>
    private void StartScheduledCopy()
    {
        var archive = NextArchivePath();
        var live = _serverRunning();
        var running = this;

        Task.Run(() => running.CreateCopy(archive, live, shareable: false))
            .ContinueWith(task => _dispatch(() => ScheduledDone(task, live, archive)));
    }

    /// <summary>
    /// Копия по расписанию закончилась: строка в журнал, сообщение человеку — по общей двери
    /// изоляции (её держит вызывающий, см. <c>App.Notify</c>) — и перечитывание папки, чтобы
    /// окно копий, если оно открыто, показало новую копию.
    /// </summary>
    private void ScheduledDone(Task<CopyOutcome> task, bool live, string archive)
    {
        if (!task.IsCompletedSuccessfully)
        {
            var failure = task.Exception?.GetBaseException().Message ?? PanelStrings.BackupUnknown;

            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScheduleFailedFormat, failure));

            _notify(
                NoticeKind.BackupFailed,
                PanelStrings.NotifyBackupFailedTitle,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.NotifyBackupFailedFormat, failure));

            return;
        }

        var outcome = task.Result;

        _log(outcome.Ok
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScheduleDoneFormat, outcome.Summary())
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupScheduleFailedFormat, outcome.Summary()));

        if (!outcome.Ok)
        {
            _notify(
                NoticeKind.BackupFailed,
                PanelStrings.NotifyBackupFailedTitle,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.NotifyBackupFailedFormat, outcome.Summary()));
        }
        else if (live)
        {
            // Копия снята на ходу — и это ровно тот случай, ради которого человеку говорят словами:
            // работающий процесс мог не дописать файлы, а панель об этом знала и смолчала бы.
            _notify(
                NoticeKind.BackupLiveCopy,
                PanelStrings.NotifyLiveCopyTitle,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.NotifyLiveCopyFormat, Path.GetFileName(archive)));
        }

        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Папка перечитана после копии по расписанию — окно копий обязано это увидеть.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
