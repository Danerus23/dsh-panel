using System;
using System.Collections.Generic;
using DshPanel.Agents;
using DshPanel.Autostart;
using DshPanel.Backup;
using DshPanel.Balance;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Restore;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;

namespace DshPanel.Shell;

/// <summary>
/// ПОДСТАВНЫЕ ДВЕРИ СЪЁМКИ — ОБРАЗЦОВЫЕ ДАННЫЕ ВМЕСТО ДАННЫХ ВЛАДЕЛЬЦА.
///
/// **Зачем.** Кадр обязан показывать **законченный вид**: сервер работает, баланс числом, копии
/// в списке, цены и пики заполнены. Прогон проверки этого показать не может по устройству — он
/// **не читает** ни настроек владельца, ни его копий, ни его ключа. Значит двери, которыми
/// пользуется окно, подменяются здесь — **литералами** (<see cref="ShotSamples"/>).
///
/// ⚠️ **Чего эти двери НЕ делают и делать не могут:**
/// * не читают машину — ни одного обращения к файлам, реестру, сети или процессам;
/// * не пишут ничего: «сохранить» отвечает успехом, но записывать некуда и незачем;
/// * не выдаются никому, кроме ветви съёмки `Program.Shot`. Обычный запуск и прогон проверки
///   получают настоящие контроллеры — правило изоляции не ослаблено ни на шаг.
///
/// ⚠️ **Почему «сохранить» отвечает успехом.** Панель на кадре должна выглядеть как рабочая:
/// при отказе записи окно честно показывает «настройки только для чтения» и серые органы —
/// ровно то, на что владелец смотрел в публичной витрине и говорил «кривые фото с пустыми окнами».
/// Запись при этом не происходит: <see cref="ShotSettings"/> ничего не хранит.
/// </summary>
public static class ShotDrawers
{
    /// <summary>Двери для кадра: настройки, сервер, баланс, телефон.</summary>
    public static (ISettingsControl Settings, IServerControl Server, IBalanceControl Balance, IPhoneSection Phone) All()
    {
        var settings = new ShotSettings();

        return (settings, new ShotServer(), new ShotBalance(settings), new ShotPhone());
    }

    /// <summary>Дверь копий для кадра: три образцовые копии, ничего не читается и не снимается.</summary>
    public static IBackupControl Backup() => new ShotBackup();

    /// <summary>
    /// ДВЕРЬ АВТОЗАПУСКА ДЛЯ КАДРА — ОБРАЗЦОВАЯ ЗАПИСЬ, А НЕ МАШИННАЯ (находка C1, пачка A).
    ///
    /// **От какого случая.** До этой правки съёмка читала НАСТОЯЩУЮ запись <c>HKCU\...\Run</c>
    /// (только на чтение): у владельца кадр показывал «автозапуск включён» с его путём, а на
    /// машине без записи — «автозапуск выключен». Один и тот же кадр выходил разным, то есть
    /// кадр не был воспроизводим, а его текст зависел от машины. Теперь состояние автозапуска
    /// на кадре — ЛИТЕРАЛ (<see cref="ShotSamples.AutostartPath"/>), и реестр не читается вовсе.
    ///
    /// ⚠️ Выдаётся только ветви съёмки; обычный запуск человека по-прежнему получает настоящий
    /// <c>AutostartController</c> — правило изоляции не ослаблено ни на шаг.
    /// </summary>
    public static IAutostartControl Autostart() => new ShotAutostart();

    /// <summary>Дверь блока движка для кадра: проверенная версия, заметки и адрес выпусков.</summary>
    public static IEngineUpdateSection Engine() => new ShotEngine(ShotSamples.EngineVersion);

    /// <summary>
    /// ДВЕРЬ ФАКТОВ ОКНА ПЕРВОГО ЗАПУСКА ДЛЯ КАДРА (A5): ХУДШИЙ случай — ни сервера DSH, ни ключа.
    ///
    /// **Зачем образцовая, а не настоящая.** Настоящие факты сделали бы кадр невоспроизводимым:
    /// у владельца всё стоит, у проверяющего нет — и один и тот же кадр выходил бы разным.
    /// Это ровно тот класс дефекта, который в пачке A закрыт для автозапуска (A3).
    ///
    /// ⚠️ Ничего не читает: ни диска, ни ключа, ни процессов. Значения ключа здесь нет и быть
    /// не может — дверь отдаёт только «найден / не найден / не прочитан».
    /// </summary>
    public static IFirstRunFacts FirstRun() => new FirstRunFactsDoor(() => ShotFirstRunFacts);

    /// <summary>Факты кадра первого запуска: сервера DSH нет, ключа модели нет — ровно две строки.</summary>
    public static FirstRunFacts ShotFirstRunFacts { get; } = new(
        EnginePresent: false,
        ServerProblem: null,
        KeyFound: false,
        KeyReadable: true);

    /// <summary>
    /// ДВЕРЬ ЗАМЕНЫ ФАЙЛОВ ДЛЯ КАДРА: право есть (кадр показывает рабочую панель, а не прогон
    /// проверки), а сами двери приготовления и запуска — ОТКАЗ СЛОВАМИ.
    ///
    /// ⚠️ **Это не заглушка «пока», а правило:** съёмка не готовит и не запускает замену файлов —
    /// она снимает окно. Нажатие на кадре всё равно некому сделать, а тихо «получить успех»
    /// значило бы, что дверь соврала бы о работе, которой не было.
    /// </summary>
    public static IUpdateInstall Install() => new ShotInstall();

    /// <summary>
    /// ДВЕРЬ ЦЕН ДЛЯ КАДРА «ПИКИ И ТАРИФЫ» — ОБРАЗЦОВЫЙ СНИМОК, СЕТЬ НЕ СПРАШИВАЕТСЯ ВОВСЕ (t13, 18.10.2026).
    ///
    /// **От какого случая.** Кадр «пиков» получал НАСТОЯЩИЙ контроллер цен, а он в прогоне проверки
    /// прав ходить в сеть не имеет — и блок «Стоимость» выходил ПУСТЫМ со словами «Цены не читались:
    /// это прогон проверки — в сеть панель не ходит». Владелец увидел на витрине нашу механику
    /// вместо вида продукта. Лечится образцовой дверью: окно не меняется ни строкой, оно и так
    /// рисует таблицу по тому, что ему дали.
    ///
    /// ⚠️ **Ни одного сетевого выхода и ни одного файла владельца:** строки — литералы
    /// (<see cref="ShotSamples.PricingSnapshotSample"/>), история — та же строка, что у кадра
    /// «История цен». Выдаётся только ветви съёмки.
    /// </summary>
    public static IPricingControl Pricing() => new ShotPricing();
}

/// <summary>
/// ЦЕНЫ НА КАДРЕ «ПИКИ И ТАРИФЫ»: образцовый снимок и слова ЖИВОЙ двери.
///
/// ⚠️ **Слова собираются ТЕМИ ЖЕ строками словаря и теми же решениями, что у
/// <see cref="PricingController"/>** (`PriceSourceFormat`, `PriceTableTitleFormat`,
/// `PricingDecisions.CheckedText`): иначе кадр показывал бы текст, которого человек не увидит,
/// и витрина расходилась бы с продуктом. Проверяется это тестом кадров
/// (`ShotSampleDoorsTests`), а не глазами.
/// </summary>
public sealed class ShotPricing : IPricingControl
{
    private readonly PricingSnapshot _snapshot = ShotSamples.PricingSnapshotSample();

    /// <summary>Снимок образцовый, но форма — та же, что у удавшегося разбора.</summary>
    public PricingResult Result =>
        new(true, string.Empty, _snapshot.SourceUrl, _snapshot.Language, _snapshot.Model, _snapshot.Rows, _snapshot.Windows, _snapshot.At);

    public bool Busy => false;

    /// <summary>На кадре право есть: человек видит рабочую панель, а не запрет прогона.</summary>
    public bool Allowed => true;

    public string PriceHeadingText =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.PriceTableTitleFormat,
            _snapshot.Model);

    public string PriceSourceText =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.PriceSourceFormat,
            _snapshot.SourceUrl,
            PricingDecisions.CheckedText(PricingDecisions.Stamp(_snapshot.At)));

    /// <summary>Спрашивать нечего: на кадре цены уже есть. Пусто — и это правило, а не заглушка.</summary>
    public void Refresh()
    {
    }

    /// <summary>История — та же образцовая строка, что у кадра «История цен».</summary>
    public PricingHistory History { get; } = PricingHistoryStore.Decode(ShotSamples.PricingHistoryText());

    /// <summary>Слушателей на кадре нет: цены не меняются.</summary>
    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// ЗАМЕНА ФАЙЛОВ НА КАДРЕ: право показано, работа — отказ словами.
///
/// **От какого случая.** Без этой двери окно писало «В этом прогоне панель замену файлов
/// не готовит» — строку, которую видит ТОЛЬКО прогон проверки. Она уезжала на публичную витрину
/// и читалась человеком как «панель чего-то не умеет».
/// </summary>
public sealed class ShotInstall : IUpdateInstall
{
    /// <summary>Право ЕСТЬ: кадр показывает рабочую панель, у которой замена файлов доступна.</summary>
    public bool Allowed => true;

    public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null) =>
        new(
            Ok: false,
            Refusal: UpdateRefusal.NotAllowed,
            Detail: string.Empty,
            Version: release,
            StagedFolder: string.Empty,
            BackupFolder: string.Empty,
            TargetFolder: string.Empty,
            ScriptPath: string.Empty,
            LogPath: string.Empty,
            CommandLine: string.Empty);

    public string Launch(UpdatePreparation preparation) => PanelStrings.UpdateInstallUnavailable;
}

/// <summary>
/// АВТОЗАПУСК НА КАДРЕ: образцовая запись «включён для этой копии» с путём ПОД МАСКОЙ.
///
/// ⚠️ **Ни реестра, ни файла, ни одного чтения с машины:** состояние — литерал. Запись на кадре
/// не меняется: <see cref="Set"/> отвечает успехом, но записывать некуда и незачем.
/// </summary>
public sealed class ShotAutostart : IAutostartControl
{
    /// <summary>
    /// Состояние на кадре — «включён для этой копии»: так человек видит РАБОЧУЮ панель,
    /// а не пустую галочку прогона проверки (то же правило, что у остальных образцовых дверей).
    /// </summary>
    public AutostartState State { get; } =
        new(AutostartWhere.Self, ShotSamples.AutostartPath, ShotSamples.AutostartPath);

    /// <summary>Право записи показано: кадр показывает рабочую панель. Запись при этом не идёт.</summary>
    public bool Writable => true;

    public void Refresh()
    {
        // Перечитывать нечего и неоткуда: состояние — литерал.
    }

    public bool Set(bool enabled) => true;
}

/// <summary>
/// БЛОК ДВИЖКА НА КАДРЕ: проверенная версия, свежая версия, заметки и адрес выпусков.
///
/// **От какого случая.** Без этой двери кадр раздела «Обновление» говорил «обновлять некуда:
/// проверенной версии в подсказках нет» и «заметок нет: подсказки не прочитаны» — две серые
/// строки о том, чего панель НЕ СМОГЛА. По такому кадру нельзя судить о виде блока вовсе,
/// а он уезжает в публичную витрину.
///
/// ⚠️ **Ничего не ставит и не откатывает:** установка на кадре не нажимается (нажимать некому),
/// а обе двери отвечают строкой «это съёмка». Ни одного обращения к машине здесь нет.
/// </summary>
public sealed class ShotEngine : IEngineUpdateSection
{
    private readonly string _recommended;

    public ShotEngine(string recommended)
    {
        _recommended = recommended;
    }

    public bool Available => true;

    public string RefusalKey => string.Empty;

    public string Recommended => _recommended;

    /// <summary>
    /// СВОЯ СТОЯЩАЯ ВЕРСИЯ НА КАДРЕ — та же, что рекомендуемая: так кадр показывает состояние
    /// «обновлять нечего» (решение владельца 14.10.2026: своя версия первой строкой, и сравнение
    /// с рекомендуемой вместо вечной кнопки).
    /// </summary>
    public string Installed => _recommended;

    /// <summary>На кадре сервер DSH есть: иначе карточка предлагала бы установку вместо обновления.</summary>
    public bool EnginePresent => true;

    /// <summary>Свежая версия новее проверенной — так и на кадре: «есть свежее, но не проверено».</summary>
    public string Fresh => "0.2.0-rc.2";

    public bool FreshIsNewer => true;

    /// <summary>Свежая не ставится: замка на неё нет — и окно говорит это словами, а не серой кнопкой.</summary>
    public bool FreshInstallable => false;

    public string Run(bool freshWanted) => PanelStrings.EngineUpdateNoRecommended;

    public void Refresh()
    {
        // Спрашивать на кадре нечего и не у кого: версии — литералы.
    }

    public PanelQuestion UpdateQuestion => new(
        PanelStrings.EngineUpdateAskTitle,
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.EngineUpdateAskFormat,
            _recommended),
        PanelStrings.EngineUpdateAskYes,
        PanelStrings.EngineUpdateAskNo,
        PanelStrings.TipEngineUpdateAskYes,
        PanelStrings.TipEngineUpdateAskNo);

    /// <summary>
    /// ВЫЖИМКА ПРО ВЕРСИЮ — ОБРАЗЦОВАЯ, и это ЧЕСТНО НАЗВАНО: строки описывают вымышленную версию
    /// вымышленными словами. Ни одного личного значения здесь нет и быть не может.
    /// </summary>
    public IReadOnlyList<string> Notes => new[]
    {
        PanelStrings.EngineNotesTroubles + " " + PanelStrings.EngineUpdateMainHint,
        PanelStrings.EngineNotesForFormat.Replace("{0}", _recommended),
    };

    public string ReleasesAddress => ProductLinks.EngineReleasesUrl;

    public bool RollbackAvailable => false;

    public string RollbackTarget => string.Empty;

    public PanelQuestion RollbackQuestion => UpdateQuestion;

    /// <summary>
    /// ВОПРОС ПОД КОНКРЕТНУЮ ВЕРСИЮ НА КАДРЕ — тот же, что и обычный: образцовая дверь диска не
    /// имеет, поэтому чисел о журнале сессий здесь не бывает, а выдумывать их нельзя (тем же
    /// правилом, что у образцового автозапуска: кадр обязан быть воспроизводимым). Кадр кнопку
    /// не нажимает, а тексты вопроса проверяются на живом пути — `SettingsEngineBlockTests`.
    /// </summary>
    public PanelQuestion UpdateQuestionFor(bool freshWanted) => UpdateQuestion;

    public string Rollback() => PanelStrings.EngineUpdateNoRecommended;
}

/// <summary>
/// КОПИИ НА КАДРЕ: три образцовые строки (полная, полная, перед накатом) — и ничего больше.
///
/// ⚠️ Папка владельца НЕ читается вовсе, копия не снимается и накат не запускается: у двери нет
/// ни путей, ни права. Прежде кадр показывал окно копий ПУСТЫМ («как прогон проверки») — и по нему
/// нельзя было судить ни о таблице, ни о строках, ни о ширине колонок.
/// </summary>
public sealed class ShotBackup : IBackupControl
{
    public string Folder => ShotSamples.BackupFolder;

    /// <summary>Папка названа человеком, а не взята по умолчанию: на кадре видно рабочую панель.</summary>
    public bool FolderFromDefault => false;

    public bool Writable => true;

    public IReadOnlyList<BackupEntry> Entries { get; } = ShotSamples.Backups();

    public string ListProblem => string.Empty;

    public string InstalledEngineVersion => ShotSamples.EngineVersion;

    public bool WithKeys => true;

    public bool CanChangeComposition => true;

    public BackupScope Scope => BackupScope.Auto;

    public IReadOnlyList<string> ExtraExclusions => Array.Empty<string>();

    public bool SetScope(BackupScope scope) => true;

    public bool SetExtraExclusions(IReadOnlyList<string> names) => true;

    public BackupEstimate Estimate(BackupScope scope) => new(true, 0);

    public bool Busy => false;

    /// <summary>
    /// Член двери, который на кадре не наступает никогда. Подавляем предупреждение осознанно:
    /// у съёмки нет ни расписания, ни копии, стало быть и «изменилось» поднимать некому.
    /// </summary>
#pragma warning disable CS0067
    public event Action? Changed;
#pragma warning restore CS0067

    public IReadOnlyList<string> KeyDirectories => Array.Empty<string>();

    public IReadOnlyList<KeyDirSuggestion> KeySuggestions => Array.Empty<KeyDirSuggestion>();

    public bool SetWithKeys(bool value) => true;

    public bool AddKeyDirectory(string path) => true;

    public void Refresh() { }

    public string NextArchivePath() => ShotSamples.BackupFolder + "\\dsh2-backup-2026-10-13-0300.zip";

    /// <summary>
    /// Снять копию на кадре НЕЛЬЗЯ, и это говорится словами: у двери нет ни путей владельца,
    /// ни права. Нажимать в кадре некому — но и «сделала вид, что сняла» тут быть не может.
    /// </summary>
    public CopyOutcome CreateCopy(string archivePath, bool serverRunning, bool shareable, Action<string>? progress = null) =>
        new(false, PanelStrings.BackupFolderMissing, null);

    /// <summary>Накат на кадре тоже невозможен: архива нет, читать нечего.</summary>
    public RestorePlan PlanRestore(string archivePath, bool withEngine, bool withPanel, bool withKeys) =>
        new(
            false,
            PanelStrings.BackupFolderMissing,
            archivePath,
            null,
            ShotSamples.WorkDir,
            Array.Empty<RestoreGroup>(),
            Array.Empty<string>(),
            0);

    public RestoreOutcome RunRestore(
        RestorePlan plan,
        bool withEngine,
        bool withPanel,
        bool withKeys,
        bool serverRunning,
        Action<string>? progress = null) =>
        new(false, PanelStrings.BackupFolderMissing, null);
}

/// <summary>
/// НАСТРОЙКИ НА КАДРЕ: образцовые значения, «право записи есть», ничего не хранится.
///
/// ⚠️ <see cref="Settings"/> — ОДИН И ТОТ ЖЕ объект на всё время кадра: окно читает его при каждой
/// перерисовке, и новый объект на каждый вызов означал бы, что человек (или проверка) видит
/// не то, что записал.
/// </summary>
public sealed class ShotSettings : ISettingsControl
{
    private readonly PanelSettings _settings = ShotSamples.Settings();

    public PanelSettings Settings => _settings;

    /// <summary>Право записи — ЕСТЬ: кадр показывает рабочую панель, а не прогон проверки.</summary>
    public bool Writable => true;

    /// <summary>Никакой беды загрузки на кадре нет — и это тоже часть образцового вида.</summary>
    public string LoadProblem => string.Empty;

    /// <summary>
    /// ПРОБЫ УМЕНИЙ НА КАДРЕ НЕТ — и это не забывчивость: настоящая проба ходит в сеть и зовёт
    /// движок по 84 именам, а съёмка не запускает ни движка, ни сети. Кадр показывает ВИД таблицы,
    /// и она честно говорит «панель сейчас не спрашивала этот сервер DSH».
    /// </summary>
    public Server.SkillProbeDoor SkillProbe => Server.SkillProbeDoor.None;

    public string WorkDirInUse => ShotSamples.WorkDir;

    /// <summary>Предложений нет: они берутся с машины, а съёмка машину не читает.</summary>
    public IReadOnlyList<WorkDirSuggestion> Suggestions() => Array.Empty<WorkDirSuggestion>();

    /// <summary>«Сохранено» — но записывать некуда и незачем: кадр живёт один прогон.</summary>
    public bool Save(PanelSettings settings) => true;

    public bool SetBackupWithKeys(bool value) => true;

    public bool SetActiveAgent(string agentId) => true;

    public bool SetBackupKeyDirs(IReadOnlyList<string> directories) => true;

    public bool SetAdoptedServer(int port) => true;

    public bool SetWindowPlacement(int x, int y, int width, int height) => true;

    public void PreviewTheme(string theme)
    {
        // Тема на кадре задаётся ключом `--theme` (Program.Shot), а не предпросмотром из окна.
        // Ничего не делаем — и это честно: кадр не «применяет» то, что человек не нажимал.
    }

    public EnvironmentReport EnvironmentInfo() => ShotSamples.Environment();

    public string EnvironmentText() => ShotSamples.Environment().Text();
}

/// <summary>
/// СЕРВЕР НА КАДРЕ: работает, наш, на своём порту. Ни один вызов ничего не запускает и не гасит —
/// кадр снимается без единого процесса, а «работающий сервер» здесь ЛИТЕРАЛ.
/// </summary>
public sealed class ShotServer : IServerControl
{
    public ServerState State { get; private set; } = ShotSamples.Server();

    public ServerOwner Owner => ServerOwner.Panel;

    public bool ConsentRemembered => false;

    public ServerState Refresh() => State;

    public ServerState Start(TimeSpan timeout) => State;

    public ServerState Stop(bool confirmed) => State;

    public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

    /// <summary>Ничего не найдено: на кадре главного окна показывается СВОЙ работающий сервер,
    /// а «два найденных сервера» — отдельный кадр со своей подставной пробой.</summary>
    public DiscoveryResult Scan() => DiscoveryResult.Nothing;

    public ServerState Adopt(FoundServer found) => State;

    public ServerState Detach() => State;

    /// <summary>
    /// «Поднять свой сервер заново» на кадре — ничего не делает: кадр показывает ВИД, а не действие,
    /// и гасить чужой процесс съёмка не имеет права ни при каких обстоятельствах.
    /// </summary>
    public ServerState RecoverOwnServer(TimeSpan timeout, bool confirmed) => State;

    /// <summary>
    /// ССЫЛКА ВХОДА НА КАДРЕ — ПУСТАЯ. Это не забывчивость: ссылка содержит токен и в кадр
    /// (а он уезжает в публичную витрину) попадать не имеет права — красная линия 7.
    /// </summary>
    public string EntryLink => string.Empty;
}

/// <summary>
/// БАЛАНС НА КАДРЕ: число, подробность и окна пика — образцовые.
///
/// ⚠️ **Счёта владельца здесь нет и быть не может:** <see cref="Result"/> — литерал, ключ модели
/// не читается вовсе, в сеть никто не ходит. Окна пика считаются ЧИСТОЙ функцией по расписанию
/// агента — им сеть и не нужна.
///
/// ⚠️ **Дат проверки — ДВЕ, как и в живом окне** (замечание владельца 20.10.2026, F-5):
/// своя — образцовая отметка разбора страницы цен (<see cref="ShotSamples.CheckedAt"/>, формат
/// <c>dd.MM.yyyy</c> через настоящее решение), профильная — дата из образцового профиля агента,
/// с подписью источника.
/// </summary>
public sealed class ShotBalance : IBalanceControl
{
    private readonly ISettingsControl _settings;

    public ShotBalance(ISettingsControl settings)
    {
        _settings = settings;
        Peak = ShotSamples.Peak();
    }

    public AgentProfile Agent => AgentCatalog.Find(_settings.Settings.ActiveAgent);

    public BalanceResult Result { get; } = ShotSamples.BalanceResult();

    public PeakState Peak { get; }

    public bool Busy => false;

    public bool Allowed => true;

    public void Refresh()
    {
        // Нажимать на кадре некому, а живой запрос из съёмки был бы сетью от имени владельца.
    }

    public void AgentChanged() { }

    public void Tick(DateTimeOffset now) { }

    /// <summary>
    /// Событие объявлено потому, что его требует дверь. ⚠️ Подавляем предупреждение «никогда
    /// не используется» **осознанно**: у съёмки нет ни часов, ни запросов, стало быть и «изменилось»
    /// не наступает никогда — поднимать его было бы враньём окну. Убрать член нельзя: он в двери.
    /// </summary>
#pragma warning disable CS0067
    public event Action? Changed;
#pragma warning restore CS0067

    public string StatusText => Result.Summary;

    public string DetailText => Result.Detail;

    public string PeakText => PeakTable.NowText(Peak);

    public string NextText => PeakTable.NextText(Peak);

    /// <summary>Своя дата на кадре — из образцовой отметки, тем же решением, что и в живом окне.</summary>
    public string PeakCheckedText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        PanelStrings.PeakCheckedFormat,
        PricingDecisions.CheckedDateText(PricingDecisions.Stamp(ShotSamples.CheckedAt)));

    /// <summary>Дата из профиля движка — отдельной строкой и с подписью источника, как в окне.</summary>
    public string PeakProfileCheckedText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        PanelStrings.PeakProfileCheckedFormat,
        PeakDecisions.CheckedText(Agent));

    public TrayTone Tone => TrayTone.Good;
}

/// <summary>
/// ТЕЛЕФОН НА КАДРЕ: разрешён, калитка слушает, два устройства, живой код, адрес под маской.
///
/// ⚠️ **Настоящего пропуска у этой двери НЕТ** — она и не читает его: устройства и код выдуманы.
/// QR рисуется НАСТОЯЩЕЙ библиотекой по строке, которую дало окно: подделывать картинку значило бы
/// показывать на кадре не тот код, который увидит человек.
/// </summary>
public sealed class ShotPhone : IPhoneSection
{
    public PhoneSectionInfo PhoneInfo() => ShotSamples.Phone();

    public bool SetPhoneAllowed(bool allowed) => true;

    public bool StartPhoneGate() => true;

    public string NewPhoneCode() => ShotSamples.PhoneCode;

    public string PhoneCode() => ShotSamples.PhoneCode;

    public bool ForgetPhoneDevice(string deviceId) => true;

    public bool ChoosePhoneAddress(string address) => true;

    public byte[] QrPng(string address) => PhoneQr.Png(address);
}
