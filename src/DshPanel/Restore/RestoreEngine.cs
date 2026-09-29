using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Restore;

/// <summary>Группа наката: что в архиве, куда ляжет и почему именно так.</summary>
public sealed record RestoreGroup(string Prefix, string Kind, string Recorded, string Target, int Files, string Note)
{
    /// <summary>Раскладывается ли эта группа. Пустая цель — не «ошибка», а решение (см. <see cref="Note"/>).</summary>
    public bool Selected => Target.Length > 0;

    public bool IsEngine => Kind is BackupRootKinds.EnginePackages or BackupRootKinds.EngineNode;

    /// <summary>
    /// Каталог ключей: у такой группы СВОЯ цель и своё согласие (<c>KeyTarget</c>) — по этому
    /// признаку считается и «вернём ли ключи», и «что закрыть на владельца после раскладки».
    /// </summary>
    public bool IsKeyRing => Kind == BackupRootKinds.KeyRing;

    // Разделитель « · » остаётся литералом: букв в нём нет, и переводить его нечего — так же
    // сделано в Backup\ZipReader.cs. Текст строки живёт в словаре, ключ — её имя.
    public string Describe() => Selected
        ? string.Format(
              CultureInfo.CurrentCulture, PanelStrings.RestoreGroupLandsFormat,
              Prefix, DisplayMask.Path(Target), Files)
          + (Note.Length > 0 ? " · " + Note : string.Empty)
        : string.Format(
              CultureInfo.CurrentCulture, PanelStrings.RestoreGroupNotLaidOutFormat, Prefix, Note);
}

/// <summary>
/// ПЛАН НАКАТА: что в копии, куда ляжет и чего в ней нет. Собирается ДО наката, чтобы человек
/// видел, что именно он собирается наложить, и мог отказаться (правило v1, оплаченное случаем:
/// накат без плана — это накат вслепую).
///
/// План только читает: ни одного файла он не трогает.
/// </summary>
public sealed record RestorePlan(
    bool Ok,
    string Error,
    string ArchivePath,
    BackupManifest? Manifest,
    string WorkingDirectory,
    IReadOnlyList<RestoreGroup> Groups,
    IReadOnlyList<string> Notes,
    int UnsafeEntries)
{
    public int Files => Groups.Sum(group => group.Files);

    public IReadOnlyList<RestoreGroup> Selected => Groups.Where(group => group.Selected).ToArray();

    /// <summary>
    /// СКОЛЬКО ФАЙЛОВ ЛЯЖЕТ НА САМОМ ДЕЛЕ — сумма ТОЛЬКО по выбранным группам.
    ///
    /// ⚠️ Отдельным членом, и это не украшение: разница с <see cref="Files"/> неочевидна, а путаница
    /// между ними — прямая ложь человеку. <see cref="Files"/> считает ВСЕ распознанные группы архива,
    /// включая те, что НЕ раскладываются (движок без согласия, настройки панели без согласия, ключи
    /// без согласия, чужая рабочая папка): у такой группы файлы в описи есть, а цели нет. Рядом
    /// с таблицей «что вернём», где у неё стоит «не вернётся», общее число спорило бы со строкой
    /// под ней: человек прочитал бы «к восстановлению: 25 000 файл(ов)», а вернулось бы 300.
    /// Нашёл дирижёр 29.09.2026 — дефект старше таблицы, но видимым его сделала именно она.
    ///
    /// Оба числа честные, и у каждого свой вопрос: «сколько в архиве» (<see cref="Files"/>,
    /// на него опираются проверки вроде <c>RestoreOldPanelTests</c>) и «сколько ляжет»
    /// (<see cref="RestoringFiles"/>). Читатели — <see cref="Summary"/> и таблица плана
    /// (<c>Views\RestoreTableView.cs</c>) — берут ВТОРОЕ, чтобы окно и режим без окна
    /// не расходились.
    /// </summary>
    public int RestoringFiles => Selected.Sum(group => group.Files);

    public bool HasEngine => Groups.Any(group => group.IsEngine);

    public string Summary()
    {
        // Причина отказа тоже называет свои причины: «накат невозможен» без «почему» — отписка,
        // а пропуск, о котором молчат, человек читает как потерю данных.
        if (Error.Length > 0)
        {
            return Notes.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, PanelStrings.RestorePlanImpossibleFormat, Error)
                : string.Format(CultureInfo.CurrentCulture, PanelStrings.RestorePlanImpossibleFormat, Error)
                  + " · " + string.Join(" · ", Notes);
        }

        var text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.RestorePlanReadyFormat, RestoringFiles, Selected.Count,
            string.Join("; ", Selected.Select(group => group.Describe())));

        if (UnsafeEntries > 0)
            text += string.Format(CultureInfo.CurrentCulture, PanelStrings.RestorePlanUnsafeSkippedFormat, UnsafeEntries);

        return Notes.Count == 0 ? text : text + " · " + string.Join(" · ", Notes);
    }
}

/// <summary>
/// Что человек разрешил и как. Все поля со смыслом по умолчанию — и умолчания НЕ подменяют
/// то, что у человека уже работает:
/// движок и настройки панели возвращаются только по явному согласию, предохранительная копия
/// снимается всегда, если её не выключили.
/// </summary>
/// <param name="WithEngine">
/// Возвращать ли движок и Node. По умолчанию НЕТ: на машине, где движок уже стоит, подменять его
/// копией — значит ломать работающее. (Оговорка честности: v1 группу npm возвращала по умолчанию,
/// а свой Node распаковывала только туда, где его не было; расхождение названо, а не унаследовано.)
/// </param>
/// <param name="WithPanel">
/// Возвращать ли настройки и состояние панели. По умолчанию НЕТ — и это тоже осознанно: накат
/// поверх работающей панели переписал бы её же файл настроек, а панель об этом не узнала бы.
/// </param>
/// <param name="SafetyCopy">
/// Снять ли копию текущего состояния ПЕРЕД накатом. По умолчанию да, и она **тонкая** (без движка
/// и Node — как в v1): предохранительная копия нужна, чтобы вернуть ДАННЫЕ, а движок возвращается
/// установкой. Не снялась — накат не начинается: накат без пути назад необратим.
/// </param>
/// <param name="ServerRunning">
/// Работает ли сервер в момент наката. Названо вызывающим явно: живой движок держит файлы сессий
/// и дописывает их, поэтому «накат на ходу» — допущение, и оно попадает в отчёт словами.
/// </param>
/// <param name="WithKeys">
/// Возвращать ли каталоги ключей. По умолчанию <b>НЕТ</b> — и это правило v1: «по отдельному
/// согласию». Ключи — то, ради чего архив и становится секретом, поэтому про них человек решает
/// отдельно от «вернуть данные», а не пачкой с ними.
/// </param>
/// <param name="SafetyFolder">Куда положить предохранительную копию. Пусто — рядом с самим архивом.</param>
/// <param name="ConfineToRunRoot">
/// Держать раскладку ВНУТРИ КОРНЯ прогона. Так ставит изолированный прогон, и это не осторожность,
/// а обещание изоляции: движок и Node на этой машине лежат в её глобальной установке — ВНЕ корня
/// (`%APPDATA%\npm` и каталог Node), — и накат по этому месту переписал бы установку владельца,
/// хотя прогон называется изолированным (`docs\ISOLATION.md` §3). Правило — в
/// <see cref="RestoreConfine"/>, одно на всю панель.
///
/// По умолчанию <c>false</c>: у обычного запуска человеком (окно копий) поведение НЕ меняется —
/// там движок возвращается по своему месту, как и было решено.
/// </param>
public sealed record RestoreOptions(
    bool WithEngine = false,
    bool WithPanel = false,
    bool SafetyCopy = true,
    bool ServerRunning = false,
    string? SafetyFolder = null,
    bool WithKeys = false,
    bool ConfineToRunRoot = false);

/// <summary>Итог наката: что вернули, что пропустили и куда легла предохранительная копия.</summary>
/// <param name="Links">Сколько ссылок создано заново.</param>
/// <param name="SafetyPath">Куда легла предохранительная копия. <c>null</c> — либо её не снимали,
/// либо снимать было нечего («терять нечего», дефект Д1) — что именно, сказано в <see cref="Notes"/>.</param>
/// <param name="LinksNotCreated">
/// Сколько ссылок НЕ легло (находка В6). Число нужно отдельно от <see cref="Skipped"/>: по нему
/// накат не выглядит успешным, когда часть профиля движка не заработает. Причина при этом уже
/// названа одной строкой отчёта — с ответом Windows и файловой системой тома.
/// </param>
public sealed record RestoreRunResult(
    bool Ok,
    string Error,
    string ArchivePath,
    int Files,
    long Bytes,
    TimeSpan Took,
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Notes,
    int Links,
    string? SafetyPath,
    int LinksNotCreated = 0)
{
    /// <summary>
    /// Строка для отчёта и журнала. Личных данных не выносит: имена машины и пользователя
    /// из описи здесь не появляются (красная линия 7) — только пути через маскировку.
    ///
    /// ⚠️ Отказ наката тоже называет подробности (находка В6): на exFAT отчёт говорил 482 раза
    /// «создать не удалось» и заканчивался словами «накат сделан». Теперь итог неполного наката
    /// называет и причину, и число, и то, что данные всё-таки легли, — а краткая строка
    /// «накат не сделан» остаётся только там, где показывать больше нечего (ничего не легло).
    /// </summary>
    public string Summary()
    {
        if (!Ok && (Files == 0 || Restored.Count == 0))
            return string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreNotDoneFormat, Error);

        var text = Ok
            ? string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreDoneSummaryFormat,
                Files, BackupFormat.Size(Bytes), BackupFormat.Duration(Took))
            : string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreIncompleteSummaryFormat,
                Files, BackupFormat.Size(Bytes), BackupFormat.Duration(Took), Error);

        if (Links > 0)
            text += string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreLinksCreatedFormat, Links);

        if (SafetyPath is not null)
            text += string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreSafetyCopyPathFormat, DisplayMask.Path(SafetyPath));

        if (Restored.Count > 0) text += " · " + string.Join("; ", Restored);
        if (Skipped.Count > 0)
            text += string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreSkippedFormat, string.Join("; ", Skipped));

        if (Notes.Count > 0) text += " · " + string.Join(" · ", Notes);

        return text;
    }
}

/// <summary>
/// НАКАТ КОПИИ: разбор архива, предохранительная копия, раскладка по местам и возврат ссылок.
///
/// Правила, за которые этот класс отвечает (каждое выросло из случая, а не из осторожности):
///
/// 1. **Ни одна запись не выходит за свой каталог.** Архив — чужой файл: запись <c>../../…</c>
///    внутри него означает запись мимо места распаковки. Такие записи отсеиваются, и пропуск
///    ВИДЕН в отчёте, а не выглядит тихой потерей (урок v1 про пропущенные ISO).
/// 2. **Ни одна запись не пишется СКВОЗЬ точку повторной обработки.** В профиле движка почти весь
///    <c>node_modules</c> — junction-ссылки на глобальную установку; накат, пошедший «сквозь»
///    такую ссылку, переписал бы ГЛОБАЛЬНЫЙ движок вместо копии. Поэтому путь каждой записи
///    проверяется по каталогам: если среди них есть ссылка — файл не кладём и говорим об этом.
/// 3. **Файл ключа, который не хранит ни одного ключа, не возвращается** — на таком файле движок
///    падает целиком и сервер не поднимается вовсе (разбор — <see cref="CredentialsFile"/>).
/// 4. **Накат не удаляет то, чего нет в копии.** Вернуть — не значит «сделать как было»:
///    вычищать чужие файлы из живого каталога панель не имеет права, а «мусор» (сборки, кэши)
///    в копии и не лежит — его выбрасывает снятие.
/// 5. **Чужой архив не трогаем вовсе.** Нет нашей описи, опись чужого вида, опись новее
///    понимаемой — накат не начинается, и причина названа.
///
/// ⚠️ **Чего здесь нет и почему.** Правка путей внутри возвращённых настроек (v1 чистила
/// несуществующие пути чужой копии) не делается: в настройках 2.0 абсолютный путь один —
/// рабочая папка, — и о её несовпадении накат говорит словами (находка В3), а предлагает
/// заменить её окно настроек (`WorkDirSuggestions`). Путь к своему `node.exe` в настройки
/// не пишется вовсе: в 2.0 движок ищется сам (`Server\DshEngine`), поля «путь к Node» нет.
/// </summary>
public static class RestoreEngine
{
    /// <summary>
    /// Разбирает архив и решает, куда что ляжет на ЭТОЙ машине. Ничего не трогает.
    /// </summary>
    /// <param name="workingDirectory">
    /// Рабочая папка сервера на этой машине (из настроек). Нужна не как цель для группы проектов,
    /// а чтобы СРАВНИТЬ её с той, с которой снята копия: сессии движка лежат в
    /// <c>~/.dsh/sessions\&lt;слаг рабочей папки&gt;</c>, то есть привязаны к пути, — и об этом
    /// надо сказать до наката (находка В3).
    /// </param>
    public static RestorePlan Plan(
        string archivePath,
        AppPaths paths,
        string? workingDirectory,
        RestoreOptions? options = null,
        DshEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        options ??= new RestoreOptions();
        var working = PanelSettings.NormalizeWorkDir(workingDirectory ?? string.Empty);
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(archivePath))
            return Refuse(PanelStrings.RestoreArchivePathNotSet, archivePath, working);

        var read = ZipReader.Read(archivePath);
        if (!read.Ok) return Refuse(read.Error, archivePath, working);

        if (read.State != ManifestState.Read || read.Manifest is null)
            return Refuse(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreManifestMissingFormat, read.Note),
                archivePath, working);

        var manifest = read.Manifest;

        if (!BackupManifest.IsOurKind(manifest.Kind))
            return Refuse(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreForeignKindFormat, manifest.Kind),
                archivePath, working);

        if (manifest.Format > BackupManifest.SupportedFormat)
        {
            return Refuse(
                string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreManifestNewerFormat,
                    manifest.Format, BackupManifest.SupportedFormat),
                archivePath, working);
        }

        var groups = new List<RestoreGroup>();
        var profiles = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var (prefix, recorded) in manifest.Paths)
        {
            var kind = manifest.Kinds.TryGetValue(prefix, out var token) ? BackupRootKinds.Parse(token) : null;
            var files = ZipReader.Group(read.Entries, prefix).Files.Count;

            var (target, note) = Target(prefix, kind, recorded, paths, working, options, engine, profiles, manifest);

            if (recorded.Length == 0)
                note = Join(note, PanelStrings.RestoreSourcePathNotRecorded);

            groups.Add(new RestoreGroup(prefix, KindToken(kind), recorded, target, files, note));
        }

        if (groups.Count == 0)
            return Refuse(PanelStrings.RestoreNoGroups, archivePath, working);

        // --- сторож чужой раскладки ------------------------------------------
        // Так выглядит копия ПРЕЖНЕЙ панели. В v1 опись держала имена понятий (dshHome, npmRoot),
        // а группы в архиве звались иначе (dsh-home, engine/npm) — и накат 2.0 по такой карте
        // разложил бы всё мимо.
        //
        // ⚠️ Прежнее условие было «ни одна запись не попала в группы описи». Оно СЛЕПО там, где
        // хоть одна группа совпала по имени, а у копии v1 так и есть: каталоги ключей и там и там
        // зовутся `keys/<номер>-<слаг>` (v1 — `..\dsh-tray\BackupService.cs`, `"keys/" + name`).
        // На живом прогоне 26.09.2026 это дало «к накату: 3 файл(ов) в 6 групп(ах)» на настоящей
        // копии в 31 215 файлов: группа ключей набирала файлы, условие «все группы по нулю»
        // не срабатывало — а с согласием «вернуть ключи» группа становится ВЫБРАННОЙ, план
        // делается годным, накат раскладывает три файла и отчитывается об успехе, тогда как
        // сессии, история, настройки и проекты (31 000 файлов) из архива не раскладываются вовсе.
        // Ложный успех в инструменте резервных копий — ровно то, ради чего сторож и написан.
        //
        // Поэтому чужой раскладкой считается архив, в котором есть записи, не попавшие ни в одну
        // известную группу. Записи-каталоги сюда не доходят (читатель архива их отбрасывает,
        // `ZipReader.Read`), а файл описи исключён по имени: он не данные, он описывает копию.
        var known = groups.Select(group => group.Prefix).ToArray();

        var unexplained = read.Entries.Count(entry =>
            !string.Equals(entry.Name, ZipLayout.ManifestEntry, StringComparison.OrdinalIgnoreCase)
            && ZipReader.GroupOf(entry.Name, known).Length == 0);

        // Группы описи, которых в архиве нет вовсе.
        var missing = groups.Count(group => group.Files == 0);

        // ⚠️ Одних «непонятых записей» мало, и это не осторожность, а исполнение уже принятого
        // правила (Д5): запись, которой нет места в описи, у архива с ПОНЯТОЙ раскладкой
        // называется в отчёте наката («записей БЕЗ ГРУППЫ в описи») и раскладку не отменяет
        // (`RestoreEmptyMachineTests.Невыбранная_группа_не_называется_записью_без_группы`).
        // Чужая раскладка — это когда опись обещает корни, которых в архиве НЕТ ВОВСЕ, а в архиве
        // лежат корни, которых нет в описи: опись и архив говорят о РАЗНЫХ наборах корней,
        // и «разложить по догадке» значит потерять данные.
        if (unexplained > 0 && missing > 0)
        {
            return Refuse(PanelStrings.RestoreOldPanelArchive, archivePath, working);
        }

        // Архив, в котором нет ни одной записи данных (одна опись), раскладывать нечем: «план
        // на 0 файл(ов)» не должен выглядеть готовым к накату. Прежнее условие остаётся и здесь —
        // оно и было про это, а не только про чужую раскладку.
        if (missing == groups.Count && read.Entries.Count > 0)
        {
            return Refuse(PanelStrings.RestoreOldPanelArchive, archivePath, working);
        }

        if (read.Unsafe.Count > 0)
            notes.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreUnsafeEntriesFormat, read.Unsafe.Count));

        if (manifest.Links.Count > 0)
            notes.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreLinksInManifestFormat, manifest.Links.Count));

        if (!options.WithEngine && groups.Any(group => group.IsEngine))
        {
            notes.Add(PanelStrings.RestoreEngineNotReturned);
        }

        if (!options.WithKeys && groups.Any(group => group.IsKeyRing))
        {
            // Правило про ключи — ОДИН текст на всю панель (RestoreKeysNeedConsent): он же стоит
            // и причиной нераскладки группы, поэтому подставляется, а не пересказывается.
            notes.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreKeysNotReturnedFormat,
                PanelStrings.RestoreKeysNeedConsent));
        }

        if (manifest.WithCredentials)
        {
            notes.Add(PanelStrings.RestoreCredentialsFileNote);
        }

        WarnAboutMachine(manifest, notes);

        var selected = groups.Any(group => group.Selected);
        return new RestorePlan(
            selected, selected ? string.Empty : PanelStrings.RestoreNoGroupSelected,
            Path.GetFullPath(archivePath), manifest, working, groups, notes, read.Unsafe.Count);
    }

    /// <summary>
    /// Раскладывает копию по местам. Сервер к этому моменту должен быть остановлен — это забота
    /// вызывающего, и он же говорит об этом полем <see cref="RestoreOptions.ServerRunning"/>.
    /// </summary>
    public static RestoreRunResult Run(
        RestorePlan plan,
        RestoreOptions options,
        AppPaths paths,
        DshEngine? engine = null,
        Action<string>? progress = null,
        Func<string, string, JunctionAttempt>? junction = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        var started = Stopwatch.StartNew();
        var notes = new List<string>(plan.Notes);
        var skipped = new List<string>();
        var restored = new List<string>();
        var failed = new List<string>();

        if (!plan.Ok)
            return Failed(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreNotStartedFormat, plan.Error),
                plan.ArchivePath, started.Elapsed, notes);

        if (options.ServerRunning)
        {
            notes.Add(PanelStrings.RestoreServerRunningNote);
        }

        // Предохранительная копия — ДО первой записи: накат необратим, и пути назад без неё нет.
        string? safetyPath = null;
        if (options.SafetyCopy)
        {
            progress?.Invoke(PanelStrings.RestoreProgressSafetyCopy);

            var folder = string.IsNullOrWhiteSpace(options.SafetyFolder)
                ? paths.BackupsDir
                : options.SafetyFolder!;

            // Имя предохранительной копии берётся у ОДНОГО места на всю панель, а не пишется здесь
            // строкой: 26.09.2026 у панели 2.0 появился свой префикс (папка копий у неё общая
            // с панелью 1.x), и второй литерал в этом файле разошёлся бы с правилом имени молча —
            // то есть ротация 2.0 не признала бы свою же предохранительную копию своей.
            var safety = Path.Combine(folder, BackupNaming.SafetyPrefix +
                DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".zip");

            // Тонкая копия (без движка и Node) — как в v1: вернуть надо ДАННЫЕ.
            var planBefore = BackupPlanner.Full(paths, plan.WorkingDirectory, engine, withEngine: false);

            // Д1 (блокирующий дефект). Пустой план — это НЕ отказ, а «терять нечего»: так выглядит
            // ЧИСТАЯ машина, ради которой накат и делается. До 26.09.2026 здесь вызывался движок
            // копии, тот отказывал («копию так не собрать»), и накат на чистую машину был
            // невозможен вовсе — при том что терять на ней было нечего по определению.
            //
            // Отказ остаётся отказом только тогда, когда план не собрался по НАСТОЯЩЕЙ причине
            // (непустой Error) — эту ветку разбирает сам движок копии ниже.
            if (planBefore.NothingToLose)
            {
                notes.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreSafetyCopyNotNeededFormat,
                    BackupPlan.NothingToLoseText));
            }
            else
            {
                var backup = BackupEngine.Run(
                    planBefore,
                    new BackupRequest(safety, ServerRunning: options.ServerRunning, Verify: true),
                    paths,
                    engine,
                    progress);

                if (!backup.Ok)
                {
                    return Failed(
                        string.Format(
                            CultureInfo.CurrentCulture, PanelStrings.RestoreSafetyCopyFailedFormat, backup.Error),
                        plan.ArchivePath, started.Elapsed, notes);
                }

                safetyPath = backup.ArchivePath;
            }
        }

        var targets = plan.Groups
            .Where(group => group.Selected)
            .ToDictionary(group => group.Prefix, group => group.Target, StringComparer.Ordinal);

        var linkNames = plan.Manifest!.Links.Select(link => link.Path).ToHashSet(StringComparer.Ordinal);

        // Файл ключа, который не хранит ни одного ключа, возвращать нельзя — и правило это про
        // ФАЙЛ, а не про группу. Нашлось на самотесте копий 25.09.2026: один и тот же файл может
        // приехать ДВУМЯ группами, когда корни пересекаются (у изолированного прогона домашний
        // каталог движка лежит внутри корня панели; у человека так бывает, если рабочая папка —
        // его домашний каталог). Тогда проверка «это группа домашнего каталога» пропускала файл
        // через вторую группу, и он ложился на место — ровно тот случай, из-за которого движок
        // 23.09.2026 не поднялся вовсе. Поэтому сверяем КУДА файл ляжет, а не откуда он приехал.
        var credentialsTarget = Path.Combine(paths.DshHome, ".credentials.yaml");

        var files = 0;
        long bytes = 0;
        var locked = 0;
        var emptyCredentials = 0;
        var avoidedLinks = 0;
        var dangerEntries = 0;

        // Записи, не попавшие ни в одну группу описи. Раньше такие записи исчезали МОЛЧА
        // (поиск группы шёл по первому отрезку пути и вложенные имена не находил) — теперь
        // их число и первые имена называются в отчёте.
        var unmatched = 0;
        var unmatchedSample = new List<string>();

        // Записи ВЫКЛЮЧЕННЫХ групп — отдельный случай, и путать его с первым нельзя (дефект Д5).
        // До 26.09.2026 они попадали в targets (там только выбранные группы), не находили группы
        // и назывались «запись без группы в описи»: при выключенном согласии на движок отчёт
        // печатал «записей без группы в описи: 29 898» — прямую ложь, потому что группа в описи
        // ЕСТЬ, просто она не выбрана. Настоящий сигнал (запись, у которой вида нет вовсе)
        // в этом шуме тонул.
        var unselected = 0;
        var unselectedSample = new List<string>();

        var landed = new Dictionary<string, int>(StringComparer.Ordinal);
        var linkCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var zip = ZipFile.OpenRead(plan.ArchivePath);

            foreach (var entry in zip.Entries)
            {
                var name = ZipReader.Normalize(entry.FullName);
                if (ZipReader.IsDirectory(name)) continue;

                // Опись — не данные: она описывает копию, а не восстанавливается из неё.
                if (string.Equals(name, ZipLayout.ManifestEntry, StringComparison.OrdinalIgnoreCase)) continue;

                if (!ZipReader.IsSafe(name))
                {
                    dangerEntries++;
                    continue;
                }

                // Группа записи — САМОЕ ДЛИННОЕ имя из описи, которому путь принадлежит: имена
                // бывают вложенными (keys/1-ssh), и «первый отрезок пути» нашёл бы только «keys»,
                // то есть ни одной группы, — а записи исчезали бы МОЛЧА. Теперь пропуск назван.
                var group = ZipReader.GroupOf(name, targets.Keys);
                if (group.Length == 0)
                {
                    // Группы нет среди ВЫБРАННЫХ — но, может быть, она есть в описи: тогда запись
                    // не «без группы», а из группы, которую человек не выбрал. Две причины — две
                    // разные строки отчёта, и обе называют себя словами.
                    if (ZipReader.GroupOf(name, plan.Groups.Select(one => one.Prefix)).Length > 0)
                    {
                        unselected++;
                        if (unselected <= 2) unselectedSample.Add(name);
                    }
                    else
                    {
                        unmatched++;
                        if (unmatched <= 2) unmatchedSample.Add(name);
                    }

                    continue;
                }

                var root = targets[group];
                var relative = name.Length > group.Length ? name[(group.Length + 1)..] : string.Empty;
                if (relative.Length == 0) continue;

                // Ссылки (junction) в архиве лежат пустыми записями; их место — Links в описи,
                // и создаются они заново ПОСЛЕ раскладки: ссылку нельзя «распаковать».
                if (linkNames.Contains(name)) continue;

                var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!BackupPlanner.IsInside(full, root))
                {
                    dangerEntries++;
                    continue;
                }

                if (GoesThroughLink(root, full, linkCache))
                {
                    avoidedLinks++;
                    continue;
                }

                if (string.Equals(full, credentialsTarget, StringComparison.OrdinalIgnoreCase)
                    && CredentialsFile.StoresNothing(ReadText(entry)))
                {
                    emptyCredentials++;
                    continue;
                }

                try
                {
                    var directory = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    entry.ExtractToFile(full, overwrite: true);
                    files++;
                    bytes += entry.Length;

                    landed.TryGetValue(group, out var count);
                    landed[group] = count + 1;

                    if (files % 25 == 0) progress?.Invoke(name);
                }
                catch (Exception error)
                {
                    // Файл может быть занят: движок держит node, антивирус проверяет, человек открыл
                    // его в проводнике. Один такой файл — не повод бросать весь накат, но и молчать
                    // о нём нельзя: «легло всё» и «легло почти всё» — разные вещи.
                    locked++;
                    if (failed.Count < 5) failed.Add($"{relative} ({error.GetType().Name})");
                }
            }
        }
        catch (Exception error)
        {
            return Failed($"{error.GetType().Name}: {error.Message}", plan.ArchivePath, started.Elapsed, notes);
        }

        // Ссылки — после файлов: им нужны каталоги на месте.
        var (links, linkSkipped, linksFailed) = RestoreLinks(plan, targets, paths, engine, progress, junction);
        skipped.AddRange(linkSkipped);

        // Восстановленные каталоги ключей закрываем на владельца — ровно так же, как закрыт сам
        // архив (правило v1). Это не украшение: каталог с приватными ключами, оставшийся открытым
        // для соседних учётных записей, — это находка В4 в её втором виде.
        //
        // ⚠️ Закрывается и СОДЕРЖИМОЕ (дефект Д8): иначе файлы ключей остаются с пустым списком
        // доступа, и владелец не может прочитать собственный ключ.
        foreach (var group in plan.Groups.Where(group => group.IsKeyRing && group.Selected))
        {
            var tightened = BackupEngine.RestrictToOwner(group.Target);
            if (tightened.Ok) continue;

            skipped.Add(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.RestoreKeysNotTightenedFormat,
                DisplayMask.Path(group.Target),
                tightened.Reason));
        }

        foreach (var group in plan.Groups.Where(group => group.Selected))
        {
            landed.TryGetValue(group.Prefix, out var count);

            // Тот же ключ, что и у строки группы в плане: человек видит ОДНУ и ту же строку
            // до наката и после — расходиться им нечем.
            restored.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreGroupLandsFormat,
                group.Prefix, DisplayMask.Path(group.Target), count));
        }

        if (dangerEntries > 0)
            skipped.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreSkippedUnsafeEntriesFormat, dangerEntries));

        if (avoidedLinks > 0)
            skipped.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreSkippedThroughLinkFormat, avoidedLinks));

        if (emptyCredentials > 0) skipped.Add(PanelStrings.RestoreSkippedEmptyCredentials);

        // Две разные причины — две разные строки (дефект Д5). Первая — НАСТОЯЩИЙ сигнал: запись,
        // которой в описи нет вовсе. Вторая — не потеря, а решение человека: группа в описи есть,
        // просто не выбрана, и об этом уже сказано в плане.
        if (unmatched > 0)
        {
            skipped.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreUnmatchedEntriesFormat, unmatched));

            // Отступ — не текст, а место в списке: в словаре он был бы первым, что срежет
            // переводчик, и имена перестали бы читаться как примеры.
            foreach (var name in unmatchedSample)
                skipped.Add("  " + string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreUnmatchedEntryFormat, name));
        }

        if (unselected > 0)
        {
            skipped.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreUnselectedEntriesFormat, unselected));

            foreach (var name in unselectedSample)
                skipped.Add("  " + string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreUnselectedEntryFormat, name));
        }

        if (locked > 0)
        {
            skipped.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreLockedFilesFormat, locked));

            foreach (var line in failed) skipped.Add("  " + line);
        }

        var took = started.Elapsed;

        if (files == 0)
            return Failed(PanelStrings.RestoreNothingLanded, plan.ArchivePath, took, notes, skipped);

        // Ссылки не легли — накат успешным НЕ ВЫГЛЯДИТ (находка В6): без них часть профиля движка
        // не заработает. Причина и число уже названы одной строкой в пропущенных; здесь — отметка
        // в самом итоге, чтобы «накат сделан» не читалось как «всё на месте».
        //
        // ⚠️ «Неполный» — НЕ «ничего не сделано»: данные легли, и это сказано тем же итогом
        // (группы и число файлов). Иначе человек, увидев только слово «неполный», пошёл бы
        // накатывать заново, не зная, что восстанавливать уже нечего.
        if (linksFailed > 0)
        {
            notes.Add(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.RestoreLinksNotCreatedFormat,
                linksFailed,
                linksFailed + links));

            // Отдельный ключ, а не тот же, что у замечания выше: там «ссылок не легло» — факт,
            // здесь «ссылок создать не удалось» — причина отказа, и на неё смотрит проверка
            // (BackupJunctionTests считает вхождения «создать не удалось»).
            return new RestoreRunResult(false,
                string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreLinksCreateFailedFormat,
                    linksFailed, linksFailed + links),
                plan.ArchivePath, files, bytes, took, restored, skipped, notes, links, safetyPath, linksFailed);
        }

        if (locked > 0)
        {
            return new RestoreRunResult(false,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreLockedFilesFailedFormat, locked),
                plan.ArchivePath, files, bytes, took, restored, skipped, notes, links, safetyPath);
        }

        return new RestoreRunResult(true, string.Empty, plan.ArchivePath, files, bytes, took,
            restored, skipped, notes, links, safetyPath);
    }

    // --- показ плана ---------------------------------------------------------

    /// <summary>
    /// Строка про версию движка, которой снята копия, — находка В5: копию, снятую одной версией
    /// движка, другая версия может не открыть, и сказать об этом надо ДО наката, а не после того,
    /// как у человека не открылись сессии.
    ///
    /// Живёт здесь, а не в окне, потому что пользователей у правила ДВА: окно копий и режим наката
    /// без окна (`--restore`). Второе место неминуемо разошлось бы с первым — а расходиться тут
    /// нельзя: строка обещает человеку, что́ его ждёт.
    ///
    /// Ответов ТРИ, и третий появился из живого прогона наката на чистой машине (дефект Д3):
    /// версия копии названа, а нынешняя неизвестна, потому что движка на машине нет. До 26.09.2026
    /// пустая нынешняя версия попадала в ветку «той же версии, что стоит сейчас» — прямая ложь.
    /// </summary>
    public static string VersionNote(string? copyVersion, string? installedVersion)
    {
        var copy = (copyVersion ?? string.Empty).Trim();
        var current = (installedVersion ?? string.Empty).Trim();

        if (copy.Length == 0) return PanelStrings.RestoreVersionUnknown;

        // Три ответа, а не два (дефект Д3): «движка нет вовсе» — это НЕ «та же версия».
        // До 26.09.2026 пустая нынешняя версия попадала в ветку «той же версии, что стоит
        // сейчас», и человек на чистой машине читал прямую ложь: сравнивать было не с чем.
        if (current.Length == 0)
            return string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreVersionNoEngineFormat, copy);

        return string.Equals(copy, current, StringComparison.OrdinalIgnoreCase)
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreVersionSameFormat, copy)
            : string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreVersionDiffersFormat, copy, current);
    }

    // --- куда что ложится ----------------------------------------------------

    /// <summary>
    /// Куда раскладывается группа на этой машине и почему так.
    ///
    /// Записанный в описи путь — путь МАШИНЫ-ИСТОЧНИКА, поэтому сам по себе он годится не всегда.
    /// Правила по видам групп, и все они названы словами в отчёте:
    ///
    /// * домашний каталог движка и папка панели — **свои, текущие** (единственный источник путей,
    ///   `AppPaths`): «вернуть сессии» значит положить их в свой <c>~/.dsh</c>, а не в чужой;
    /// * рабочая папка — путь из описи, но с заменой чужого профиля на свой (проекты возвращаются
    ///   туда, откуда взяты); расхождение с нынешней настройкой называется отдельно (находка В3);
    /// * движок и Node — своё место на этой машине, если движок найден, иначе путь из описи
    ///   с заменой профиля. И только по отдельному согласию;
    /// * группа без вида (опись писала не эта панель) — путь из описи с заменой профиля,
    ///   и об этом сказано.
    /// </summary>
    private static (string Target, string Note) Target(
        string prefix,
        BackupRootKind? kind,
        string recorded,
        AppPaths paths,
        string working,
        RestoreOptions options,
        DshEngine? engine,
        string profiles,
        BackupManifest manifest)
    {
        switch (kind)
        {
            case BackupRootKind.DshHome:
                return (paths.DshHome, string.Empty);

            case BackupRootKind.Panel:
                if (!options.WithPanel)
                    return (string.Empty, PanelStrings.RestorePanelNeedsConsent);

                return (paths.Root, string.Empty);

            case BackupRootKind.WorkingFolder:
            {
                var target = Remap(recorded, profiles);
                if (target.Length == 0)
                    return (string.Empty, PanelStrings.RestoreWorkingFolderPathEmpty);

                // Находка В3: сессии движка лежат в `~/.dsh/sessions\<слаг рабочей папки>`,
                // то есть привязаны к пути. Разошёлся — скажем, и не только «скажем»: назовём,
                // что именно поправить в настройках, чтобы сессии нашлись.
                if (working.Length > 0 && !string.Equals(working, target, StringComparison.OrdinalIgnoreCase))
                {
                    return (target, string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.RestoreWorkingFolderDiffersFormat,
                        DisplayMask.Path(target),
                        DisplayMask.Path(working),
                        DisplayMask.Path(target)));
                }

                return (target, string.Empty);
            }

            case BackupRootKind.EnginePackages:
            case BackupRootKind.EngineNode:
            {
                if (!options.WithEngine)
                    return (string.Empty, PanelStrings.RestoreEngineNeedsConsent);

                var own = kind == BackupRootKind.EnginePackages
                    ? engine is null ? null : BackupPlanner.NpmDirectory(engine)
                    : engine is null ? null : Path.GetDirectoryName(engine.NodePath);

                // Цель — место движка на ЭТОЙ машине (или записанный путь, если движка нет).
                var target = string.IsNullOrWhiteSpace(own) ? Remap(recorded, profiles) : own!;

                if (target.Length == 0)
                    return (string.Empty, PanelStrings.RestoreEnginePathEmpty);

                // Изолированный прогон НЕ раскладывает движок и Node за пределы своего корня: там
                // лежит глобальная установка машины, а не данные прогона (правило — RestoreConfine).
                // Группа просто не выбирается, и причина называется словами: пропуск, о котором
                // молчат, человек читает как потерю данных.
                if (!RestoreConfine.Allows(target, paths, options.ConfineToRunRoot))
                {
                    return (string.Empty, string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.RestoreEngineOutsideRootFormat,
                        DisplayMask.Path(target),
                        DisplayMask.Path(paths.Root)));
                }

                return string.IsNullOrWhiteSpace(own)
                    ? (target, PanelStrings.RestoreEngineAbsentUsesRecordedPath)
                    : (target, PanelStrings.RestoreEnginePresentUsesOwnPath);
            }

            case BackupRootKind.KeyRing:
            {
                // Ключи возвращаются только по ОТДЕЛЬНОМУ согласию — как в v1: это то, ради чего
                // архив и становится секретом, и решение про них человек принимает отдельно.
                if (!options.WithKeys)
                    return (string.Empty, PanelStrings.RestoreKeysNeedConsent);

                return KeyTarget(prefix, recorded, manifest, paths);
            }

            default:
            {
                var target = Remap(recorded, profiles);
                if (target.Length == 0) return (string.Empty, PanelStrings.RestoreKindUnrecordedPathEmpty);

                return (target, PanelStrings.RestoreKindUnrecordedUsesRecordedPath);
            }
        }
    }

    /// <summary>
    /// Куда возвращать каталог ключей. Перенесено из v1 (<c>RestoreService.KeyTarget</c>) «как есть»,
    /// потому что правило здесь — не вкус, а две настоящие опасности: на НОВОЙ машине записанный
    /// путь увёл бы ключи в чужой профиль, а у СВОЕЙ копии ключи могли лежать вне профиля
    /// (например на другом диске) — и тогда записанный путь как раз родной.
    ///
    /// Порядок решений: путь из описи берём, если он существует и это не корень диска, и при этом
    /// либо копия снята на этой машине этим пользователем, либо путь лежит внутри текущего профиля.
    /// Иначе: каталог SSH возвращаем в СВОЙ <c>.ssh</c> (у каждого пользователя он свой), а незнакомый
    /// чужой каталог НЕ восстанавливаем и говорим об этом в отчёте.
    /// </summary>
    private static (string Target, string Note) KeyTarget(
        string prefix, string recorded, BackupManifest manifest, AppPaths paths)
    {
        var original = (recorded ?? string.Empty).Trim();

        if (original.Length > 0)
        {
            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(original).TrimEnd('\\', '/');
            }
            catch
            {
                expanded = string.Empty;
            }

            // Корень диска ключами не засеваем даже по своей описи.
            if (expanded.Length > 2 && Directory.Exists(expanded))
            {
                if (IsSameMachine(manifest)) return (expanded, PanelStrings.RestoreKeysToOwnCopy);

                var profiles = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (BackupPlanner.IsInside(expanded, profiles)) return (expanded, string.Empty);
            }
        }

        // Незнакомый чужой каталог не восстанавливаем — о нём скажем в отчёте. Исключение —
        // каталог SSH: у текущего пользователя он свой, и ключи должны лечь туда.
        if (IsSshGroup(prefix, original))
            return (paths.SshDir, PanelStrings.RestoreKeysToCurrentSsh);

        return (string.Empty, original.Length == 0
            ? PanelStrings.RestoreKeysForeignNoPath
            : string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.RestoreKeysForeignFormat,
                DisplayMask.Path(original)));
    }

    /// <summary>
    /// Группа ключей, которая в копии была каталогом <c>.ssh</c>. Имя группы —
    /// <c>keys/&lt;номер&gt;-&lt;имя&gt;</c> (например <c>keys/1-ssh</c>), поэтому смотрим и на имя
    /// группы, и на записанный путь: знать, что это именно SSH, нужно, чтобы вернуть ключи в свой
    /// <c>.ssh</c> на новой машине.
    /// </summary>
    private static bool IsSshGroup(string group, string original)
    {
        if (group.EndsWith("/ssh", StringComparison.OrdinalIgnoreCase)) return true;
        if (group.EndsWith("-ssh", StringComparison.OrdinalIgnoreCase)) return true;

        var path = (original ?? string.Empty).TrimEnd('\\', '/');
        return path.EndsWith("\\.ssh", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/.ssh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Копия снята на ЭТОМ компьютере ЭТИМ пользователем — путям из описи можно верить.</summary>
    private static bool IsSameMachine(BackupManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.Machine)
        && string.Equals(manifest.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.User, Environment.UserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Заменяет ЧУЖОЙ профиль в начале пути на свой: <c>C:\Users\Old\…</c> → <c>C:\Users\Свой\…</c>.
    ///
    /// Зачем: копию уносят на другую машину, и «вернуть сессии и ключи» там значит положить их
    /// в профиль ТОГО, кто восстанавливает. Пути вне профиля (например, <c>C:\Program Files\nodejs</c>)
    /// остаются как есть — их место на машине не зависит от имени пользователя.
    /// </summary>
    public static string Remap(string? recorded, string? currentProfile)
    {
        var path = (recorded ?? string.Empty).Trim();
        if (path.Length == 0) return string.Empty;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }

        var profile = (currentProfile ?? string.Empty).Trim();
        if (profile.Length == 0) return path;

        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Ожидаемая форма: «C:», «Users», «<кто-то>», дальше что угодно.
        if (parts.Length < 4) return path;
        if (!string.Equals(parts[1], "Users", StringComparison.OrdinalIgnoreCase)) return path;

        var tail = string.Join(Path.DirectorySeparatorChar, parts.Skip(3));
        return tail.Length == 0 ? profile : Path.Combine(profile, tail);
    }

    // --- ссылки --------------------------------------------------------------

    /// <summary>
    /// Создаёт ссылки (junction) заново из описи. Архив их не содержит содержимым — и не может:
    /// внутри ссылки записан абсолютный путь машины-источника, и на другой машине он мёртв
    /// (`docs\ARCHIVE-MEASUREMENT.md`, находка 3).
    ///
    /// Осторожностей три, и все три про чужой архив:
    /// * путь ссылки обязан лежать ВНУТРИ своей группы (иначе ссылка заведёт файлы наружу);
    /// * цель ссылки обязана указывать в понятное место (движок, Node, профиль, свои каталоги
    ///   панели) — иначе чужой архив создал бы ссылку на что угодно;
    /// * существующий непустой каталог на месте ссылки не трогаем: данные важнее красоты.
    /// </summary>
    private static (int Created, List<string> Skipped, int Failed) RestoreLinks(
        RestorePlan plan,
        IReadOnlyDictionary<string, string> targets,
        AppPaths paths,
        DshEngine? engine,
        Action<string>? progress,
        Func<string, string, JunctionAttempt>? junction = null)
    {
        var make = junction ?? CreateJunction;
        var created = 0;
        var skipped = new List<string>();
        var links = plan.Manifest?.Links ?? new List<BackupLink>();

        var allowed = AllowedTargets(paths, engine);

        // Первое и последнее: почему не легла ссылка и что ответила Windows. Держим ЗДЕСЬ, а не
        // в Skipped по строке на ссылку: одинаковых строк было 482, и настоящий сигнал в них тонул.
        var failed = 0;
        var examples = new List<string>();
        var reason = string.Empty;
        var advice = false;

        foreach (var link in links)
        {
            var head = ZipReader.Head(link.Path);
            if (!targets.TryGetValue(head, out var root)) continue;   // группа не раскладывается

            var relative = link.Path.Length > head.Length ? link.Path[(head.Length + 1)..] : string.Empty;
            if (relative.Length == 0) continue;

            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!BackupPlanner.IsInside(path, root))
            {
                skipped.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreLinkOutsideRootFormat, relative));
                continue;
            }

            var target = ResolveTarget(link.Target, paths, engine, allowed);
            if (target.Length == 0)
            {
                skipped.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreLinkTargetMissingFormat,
                    relative, DisplayMask.Path(link.Target)));
                continue;
            }

            try
            {
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    continue;   // ссылка уже на месте — второй раз не создаём

                if (Directory.Exists(path))
                {
                    if (Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        skipped.Add(string.Format(
                            CultureInfo.CurrentCulture, PanelStrings.RestoreLinkFolderNotEmptyFormat, relative));
                        continue;
                    }

                    Directory.Delete(path);
                }

                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                progress?.Invoke(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreProgressLinkFormat, relative));

                var made = make(path, target);
                if (made.Ok)
                {
                    created++;
                    continue;
                }

                // Отказ: причина уходит в ОДНУ строку отчёта (ниже), а здесь копятся число и примеры.
                failed++;
                if (examples.Count < 4) examples.Add(relative);
                if (reason.Length == 0) reason = JunctionRefusal(made);
                advice |= made.NotNtfs;
            }
            catch (Exception error)
            {
                failed++;
                if (examples.Count < 4) examples.Add(relative);
                if (reason.Length == 0) reason = $"{error.GetType().Name}: {error.Message}";
            }
        }

        if (failed > 0)
        {
            var names = string.Join(", ", examples.Select(name => "«" + name + "»"));
            if (failed > examples.Count)
            {
                names += " " + string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.RestoreLinksFailedMoreFormat, failed - examples.Count);
            }

            skipped.Add(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.RestoreLinksFailedFormat,
                failed,
                reason,
                advice ? PanelStrings.JunctionAdvice : string.Empty).TrimEnd() + " " +
                string.Format(CultureInfo.CurrentCulture, PanelStrings.RestoreLinksFailedNamesFormat, names));
        }

        return (created, skipped, failed);
    }

    /// <summary>
    /// Причина отказа создания ссылки, названная ЧИТАЕМО: сначала ответ системы (он у находки В6
    /// был в руках и выбрасывался), затем — если том заведомо не NTFS — ещё и это, потому что
    /// «exFAT» человеку объясняет больше, чем ответ Windows.
    ///
    /// <c>public</c> — не «на всякий случай»: это то самое место, где причина становится словами,
    /// и проверка обязана пройти через все три его ветви (ответ есть / тома нет / ответа нет).
    /// </summary>
    public static string JunctionRefusal(JunctionAttempt attempt)
    {
        var answer = attempt.Answer.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.JunctionWindowsFormat, attempt.Answer)
            : PanelStrings.JunctionNoAnswer;

        return attempt.NotNtfs
            ? string.Format(
                CultureInfo.CurrentCulture, PanelStrings.JunctionNotNtfsFormat, attempt.FileSystem) +
              " (" + answer + ")"
            : answer;
    }

    /// <summary>
    /// Чем кончилась попытка создать junction: получилось ли, что ответила Windows и, если видно,
    /// на какой файловой системе лежит том.
    ///
    /// Отдельная запись, а не <c>bool</c>, — потому что причина обязана доехать до человека
    /// (находка В6): на exFAT Windows отвечает «Для завершения операции требуются локальные тома
    /// NTFS», и до 26.09.2026 этот ответ читался и выбрасывался, а отчёт писал одно и то же
    /// «создать не удалось» на каждую из 482 ссылок.
    /// </summary>
    public sealed record JunctionAttempt(bool Ok, string Answer, string FileSystem, bool NotNtfs);

    /// <summary>
    /// Создаёт junction. Windows умеет это только через <c>mklink /J</c>, и ответ системы
    /// (поток вывода и поток ошибок) теперь ВОЗВРАЩАЕТСЯ, а не выбрасывается.
    /// </summary>
    public static JunctionAttempt CreateJunction(string path, string target)
    {
        try
        {
            // ⚠️ Ответ `cmd` приходит в КОДИРОВКЕ КОНСОЛИ (OEM, у русской Windows — 866), и читать
            // его как UTF-8 значит получить мусор. А ответ — это и есть причина (находка В6), её
            // нельзя терять дважды. Поэтому читаем ПОТОКИ и разбираем их тем, чем система пишет
            // (см. <see cref="ReadAnswer"/>); `chcp 65001` для этого не годится — проверено 26.09.2026:
            // встроенная команда `mklink` отвечает в кодировке консоли, как её ни перенастрой.
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // ⚠️ Читаем БАЙТ-В-БАЙТ. По умолчанию <c>Process</c> разбирает вывод кодировкой
                // консоли вызывающего, и кириллица ответа превращается в «?» — то есть причина
                // теряется окончательно. Latin1 переводит байты в знаки один в один, и настоящая
                // кодировка восстанавливается в <see cref="ReadAnswer"/>.
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding = Encoding.Latin1,
            };

            using var process = Process.Start(start);
            if (process is null) return JunctionFailed(path, string.Empty);

            // Оба потока читаются ОДНОВРЕМЕННО: последовательное чтение двух переполненных
            // каналов — известный способ повесить процесс на взаимной блокировке.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return JunctionFailed(path, PanelStrings.JunctionMklinkTimeout);
            }

            var answer = Both(ReadAnswer(output.GetAwaiter().GetResult()), ReadAnswer(error.GetAwaiter().GetResult()));

            if (process.ExitCode == 0 && Directory.Exists(path))
                return new JunctionAttempt(true, answer, string.Empty, false);

            return JunctionFailed(path, answer);
        }
        catch (Exception error)
        {
            return JunctionFailed(path, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Ответ системы — в строку: переводы строк и метка убираются, слова не пересказываются.
    /// Здесь же восстанавливается кодировка: string, прочитанный как Latin1, байт-в-байт равен
    /// исходному, поэтому байты берутся обратно и разбираются той кодировкой, которой пишет
    /// консоль системы (OEM), а если её не оказалось — CP866: русская Windows и есть тот случай,
    /// ради которого ответ вообще понадобился.
    /// </summary>
    private static string ReadAnswer(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        try
        {
            // Провайдер кодовых страниц регистрируется один раз на процесс: без него .NET Core
            // знает только UTF и Latin1, а OEM-кодировка Windows (866 у русской) — не знает.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            text = oem.GetString(Encoding.Latin1.GetBytes(text));
        }
        catch
        {
            try
            {
                text = Encoding.GetEncoding(866).GetString(Encoding.Latin1.GetBytes(text));
            }
            catch
            {
                // Ни одной подходящей кодировки нет — отдаём как прочитали, но не молчим об этом:
                // строка ниже всё равно скажет, что слова системы могут быть нечитаемы.
            }
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();

        // cmd подмешивает метку UTF-8 в начало потока — в отчёте она выглядит мусором.
        if (text.StartsWith('\uFEFF')) text = text[1..].TrimStart();

        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>
    /// Отказ плюс файловая система тома: <c>DriveInfo</c> называет её словами («NTFS», «exFAT»),
    /// и по ней решается, добавлять ли довод про NTFS. Ошибка определения — не отказ отчёта:
    /// тогда причиной остаётся ровно ответ Windows.
    /// </summary>
    private static JunctionAttempt JunctionFailed(string path, string answer)
    {
        var format = string.Empty;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) format = drive.DriveFormat;
            }
        }
        catch
        {
            // Тома не видно — говорим то, что ответила Windows, и ничего не выдумываем.
        }

        var notNtfs = format.Length > 0 && !format.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        return new JunctionAttempt(false, answer, format, notNtfs);
    }

    /// <summary>Поток вывода и поток ошибок в одну строку: пустой не добавляет лишнего пробела.</summary>
    private static string Both(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + " " + right;

    /// <summary>
    /// Цель ссылки на ЭТОЙ машине: прежняя (с заменой профиля), а если её нет — такой же пакет
    /// в нынешней глобальной установке npm. Пусто — цели нет, и ссылку создавать нельзя: мёртвая
    /// ссылка хуже отсутствующей (движок пойдёт по ней и не найдёт пакета).
    /// </summary>
    public static string ResolveTarget(string? recorded, AppPaths paths, DshEngine? engine, IReadOnlyList<string>? allowed = null)
    {
        var target = Remap(recorded, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var allow = allowed ?? AllowedTargets(paths, engine);

        if (target.Length > 0 && Directory.Exists(target) && IsAllowed(target, allow)) return target;

        var npm = engine is null ? null : BackupPlanner.NpmDirectory(engine);
        if (npm is not null)
        {
            var marker = Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar;
            var recordedFull = (recorded ?? string.Empty);
            var at = recordedFull.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var candidate = Path.Combine(npm, recordedFull[(at + marker.Length)..].Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate) && IsAllowed(candidate, allow)) return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Куда ссылке разрешено указывать: движок и Node, профиль пользователя, каталоги панели.
    /// Чужой архив не должен уметь создать ссылку на <c>C:\Windows</c>.
    /// </summary>
    public static IReadOnlyList<string> AllowedTargets(AppPaths paths, DshEngine? engine)
    {
        var allowed = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            paths.Root, paths.DshHome, paths.DataDir, paths.StateDir, paths.BackupsDir,
        };

        if (engine is not null)
        {
            var npm = BackupPlanner.NpmDirectory(engine);
            if (!string.IsNullOrWhiteSpace(npm)) allowed.Add(npm!);

            var node = Path.GetDirectoryName(engine.NodePath);
            if (!string.IsNullOrWhiteSpace(node)) allowed.Add(node!);
        }

        return allowed.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsAllowed(string target, IReadOnlyList<string> allowed)
    {
        foreach (var root in allowed)
        {
            if (BackupPlanner.IsInside(target, root)) return true;

            try
            {
                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Кривой путь — не разрешаем: сомнение решается в пользу «нельзя».
            }
        }

        return false;
    }

    // --- мелкие решения ------------------------------------------------------

    /// <summary>
    /// Идёт ли путь к файлу через точку повторной обработки — и не является ли ею сам файл.
    ///
    /// Проверяются КАЖДЫЙ существующий каталог на пути от корня группы и сам файл, если он уже
    /// есть. Ответы запоминаются: каталогов на порядок меньше, чем файлов, поэтому проверка
    /// не превращается в обход дерева на каждый файл.
    /// </summary>
    private static bool GoesThroughLink(string root, string full, Dictionary<string, bool> cache)
    {
        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder)) return false;

        if (IsLink(folder, cache)) return true;

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, folder);
        }
        catch
        {
            return true;   // не разобрали путь — не пишем
        }

        if (relative.Length == 0 || relative == ".") return false;

        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;

            current = Path.Combine(current, part);
            if (IsLink(current, cache)) return true;
        }

        return false;
    }

    private static bool IsLink(string directory, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(directory, out var known)) return known;

        bool link;
        try
        {
            link = Directory.Exists(directory)
                   && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            link = false;
        }

        cache[directory] = link;
        return link;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            // Не прочитали — считаем запись содержательной и кладём как есть (правило v1).
            return "?";
        }
    }

    /// <summary>
    /// Говорит ли опись, что копия снята на другой машине или другим человеком — и только это.
    /// Имена в отчёт не выносим: это личные данные (красная линия 7), а для решения хватает факта.
    /// </summary>
    private static void WarnAboutMachine(BackupManifest manifest, List<string> notes)
    {
        try
        {
            var sameMachine = string.Equals(manifest.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            var sameUser = string.Equals(manifest.User, Environment.UserName, StringComparison.OrdinalIgnoreCase);
            if (sameMachine && sameUser) return;

            notes.Add(PanelStrings.RestoreOtherMachineNote);
        }
        catch
        {
            // Не смогли сравнить — не страшно: молчание здесь ничего не ломает.
        }
    }

    private static string KindToken(BackupRootKind? kind) => kind is null ? string.Empty : BackupRootKinds.Token(kind.Value);

    private static string Join(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + "; " + right;

    private static RestorePlan Refuse(string error, string archivePath, string working) =>
        new(false, error, archivePath, null, working, Array.Empty<RestoreGroup>(), Array.Empty<string>(), 0);

    private static RestoreRunResult Failed(
        string error, string archivePath, TimeSpan took, IReadOnlyList<string> notes,
        IReadOnlyList<string>? skipped = null) =>
        new(false, error, archivePath, 0, 0, took, Array.Empty<string>(), skipped ?? Array.Empty<string>(),
            notes, 0, null);
}
