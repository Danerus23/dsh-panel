using System.Globalization;
using System.Text;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// Вид корня копии. Нужен решениям («в копии есть движок?»), отчёту — словами, а НАКАТУ — тем,
/// что по виду он понимает, куда раскладывать: записанные в описи пути принадлежат машине-источнику
/// и на другой машине не годятся. Поэтому вид уезжает в опись (`BackupManifest.Kinds`).
/// </summary>
public enum BackupRootKind
{
    /// <summary>Домашний каталог движка: ключи агентов, настройки, плагины, сессии и история.</summary>
    DshHome,

    /// <summary>Рабочая папка со всеми проектами.</summary>
    WorkingFolder,

    /// <summary>Своя папка панели: настройки, журнал, состояние.</summary>
    Panel,

    /// <summary>Глобальные пакеты npm: движок и шимы.</summary>
    EnginePackages,

    /// <summary>Node, которым поднимается движок.</summary>
    EngineNode,

    /// <summary>
    /// Каталог ключей: каталог SSH текущего пользователя или свой каталог, названный человеком.
    /// Появляется в копии только по отдельному разрешению (решение владельца 26.09.2026, «как в v1»),
    /// и у него своё правило возврата — см. <c>Restore\RestoreEngine.KeyTarget</c>.
    /// </summary>
    KeyRing,
}

/// <summary>
/// Виды групп в описи — устойчивыми словами, а не именами перечисления: опись читает и человек,
/// и (возможно) другая версия панели, а переименование члена перечисления сломало бы чтение молча.
/// </summary>
public static class BackupRootKinds
{
    public const string DshHome = "dsh-home";

    public const string WorkingFolder = "working-folder";

    public const string Panel = "panel";

    public const string EnginePackages = "engine-packages";

    public const string EngineNode = "engine-node";

    /// <summary>
    /// Каталог ключей. Слово устойчивое: по нему накат узнаёт, что к такой группе нужны СВОИ
    /// правила — отдельное согласие и своя цель (см. <c>Restore\RestoreEngine</c>).
    /// </summary>
    public const string KeyRing = "key-ring";

    public static string Token(BackupRootKind kind) => kind switch
    {
        BackupRootKind.DshHome => DshHome,
        BackupRootKind.WorkingFolder => WorkingFolder,
        BackupRootKind.Panel => Panel,
        BackupRootKind.EnginePackages => EnginePackages,
        BackupRootKind.EngineNode => EngineNode,
        _ => KeyRing,
    };

    /// <summary>Разбор вида из описи. Неизвестное слово — <c>null</c>: накат скажет об этом словами.</summary>
    public static BackupRootKind? Parse(string? token) => (token ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        DshHome => BackupRootKind.DshHome,
        WorkingFolder => BackupRootKind.WorkingFolder,
        Panel => BackupRootKind.Panel,
        EnginePackages => BackupRootKind.EnginePackages,
        EngineNode => BackupRootKind.EngineNode,
        KeyRing => BackupRootKind.KeyRing,
        _ => null,
    };

    /// <summary>Движок ли это (любая из двух его частей) — по этому решается «копия самодостаточна?».</summary>
    public static bool IsEngine(BackupRootKind kind) =>
        kind is BackupRootKind.EnginePackages or BackupRootKind.EngineNode;

    /// <summary>Каталог ключей ли это — по этому решается отдельное согласие на накате.</summary>
    public static bool IsKeyRing(BackupRootKind kind) => kind == BackupRootKind.KeyRing;
}

/// <summary>Один корень копии: вид (для решений) и словами — зачем он здесь (для отчёта и журнала).</summary>
public sealed record BackupRoot(BackupSource Source, BackupRootKind Kind, string Purpose)
{
    public string Prefix => Source.Prefix;

    public string Directory => Source.Directory;

    public string Describe() => $"«{Prefix}» ← {DisplayMask.Path(Directory)} ({Purpose})";
}

/// <summary>
/// Что класть в копию: корни, замечания и — если так нельзя — причина отказа.
/// <see cref="Notes"/> не пустоты ради: каждый пропуск обязан быть назван, иначе он выглядит
/// тихой потерей (правило проекта, оплаченное случаем с пропущенными ISO в v1).
/// </summary>
public sealed record BackupPlan(IReadOnlyList<BackupRoot> Roots, IReadOnlyList<string> Notes, string Error)
{
    public bool Ok => Error.Length == 0 && Roots.Count > 0;

    /// <summary>
    /// План ПУСТ, и это НЕ отказ: настоящей причины отказа нет (<see cref="Error"/> пуст),
    /// а корней не нашлось потому, что ни одного из мест копии на диске нет.
    ///
    /// Так выглядит ЧИСТАЯ машина — та самая, ради которой и делается накат: ни домашнего каталога
    /// движка, ни рабочей папки, ни папки панели. Терять на ней нечего, значит предохранительная
    /// копия перед накатом не нужна, и об этом надо сказать словами. Отличать это от «план
    /// не собрался» обязан вызывающий (<c>RestoreEngine.Run</c>): там отказ остаётся отказом.
    /// </summary>
    public bool NothingToLose => Error.Length == 0 && Roots.Count == 0;

    public IReadOnlyList<BackupSource> Sources => Roots.Select(root => root.Source).ToArray();

    public static BackupPlan Refuse(string error, IReadOnlyList<string>? notes = null) =>
        new(Array.Empty<BackupRoot>(), notes ?? Array.Empty<string>(), error);

    /// <summary>
    /// Строка для отчёта: сколько корней и что в них.
    ///
    /// ⚠️ Пустой причины у отказа БЫТЬ НЕ ДОЛЖНО (дефект Д2): до 26.09.2026 при пустом
    /// <see cref="Error"/> и нуле корней эта строка выходила пустой, и человек читал
    /// «копию так не собрать: » — ноль вместо слов. Оба случая пустого плана названы здесь
    /// словами, а не отданы вызывающему.
    /// </summary>
    public string Summary()
    {
        if (Error.Length > 0) return string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupPlanCannotBuildFormat, Error);
        if (NothingToLose) return NothingToLoseText;

        var text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.BackupRootsFormat,
            Roots.Count, string.Join("; ", Roots.Select(root => root.Describe())));
        return Notes.Count == 0 ? text : text + " · " + string.Join(" · ", Notes);
    }

    /// <summary>
    /// Слова про пустой план. Одно место на всю панель: их читают и отчёт копии, и отчёт наката,
    /// и разойтись они не должны — иначе про одно и то же состояние человек услышит две разные вещи.
    /// </summary>
    public static string NothingToLoseText => PanelStrings.BackupNothingToLose;
}

/// <summary>
/// СОСТАВ КОПИИ — то, чего до 25.09.2026 в документах не было списком, а в коде не было вовсе.
///
/// Состав полной копии решён владельцем и записан в `ROADMAP.md` («Состав полной копии решён
/// явно»), повторён в `PROJECT.md` §3.3 и здесь **не выдумывается заново**: домашний каталог
/// движка целиком (ключи всех агентов, настройки, плагины, сессии и история), рабочая папка
/// со всеми проектами, свой движок и Node, настройки панели, и `.git` **остаётся**.
///
/// **Имена групп берутся у папок на диске, а не выдумываются.** Причина — не вкус, а проба
/// 25.09.2026: 7-Zip хранит путь ровно так, как тот задан относительно его рабочего каталога,
/// и другого имени дать не умеет (<see cref="ZipLayout"/>). Имена понятий, как в v1
/// (<c>dsh-home</c>, <c>engine/npm</c>), закрыли бы быстрый путь на всякой полной копии —
/// то есть решение владельца «создаёт 7-Zip» осталось бы на бумаге. А раскладку для наката
/// всё равно несёт опись: <c>Paths</c> отдаёт пару «имя группы → путь на диске».
///
/// ⚠️ **Отступление от v1, названное явно.** v1 пропускала <c>node_modules</c> в домашнем
/// каталоге движка и добирала его обратно отдельными корнями на каждый профиль; отсюда и
/// вложенные имена групп. В 2.0 <c>node_modules</c> в домашнем каталоге **не пропускается**:
/// свои зависимости профиля едут в копию сами, а самоссылающиеся junction-ссылки всё равно
/// не разворачиваются (обход их не идёт), зато попадают в опись и создаются заново накатом.
/// Так раскладка остаётся плоской, а содержимое — тем же.
///
/// ⚠️ **Ключи — по отдельному разрешению** (решение владельца 26.09.2026, «как в v1»): каталог SSH
/// текущего пользователя и названные человеком каталоги входят в копию ТОЛЬКО при включённом
/// <c>BackupWithKeys</c>, по умолчанию выключенном. Включённое разрешение делает ключом доступа
/// сам архив, и об этом сказано словами и в настройках, и в окне копий, и в описи.
///
/// ⚠️ **Чего здесь НЕТ и почему** (чтобы это не выглядело забытым): установщик панели
/// (<c>dsh-panel-setup.exe</c>), который v1 брала в полную копию, в 2.0 брать неоткуда:
/// установщика ещё нет (этап 5).
/// </summary>
public static class BackupPlanner
{
    /// <summary>
    /// ОТКАЗ ОТ СОЧЕТАНИЯ «приватные ключи + копия для передачи» — или пусто, когда сочетание
    /// допустимо. Решение владельца 29.09.2026 (п. 12 <c>docs\DESIGN.md</c>): такой архив унёс бы
    /// приватные ключи человека тому, кому его отдают, и назван при этом «для передачи».
    ///
    /// ⚠️ **ОДНО место на всю панель.** Эту же дверь спрашивают:
    /// * <see cref="Full"/> — то есть всякий путь копии (окно, расписание, <c>--backup</c>);
    /// * окно копий — чтобы показать причину СЛОВАМИ до начала работы, а не после неё
    ///   (<c>Views\BackupWindow.axaml.cs</c>);
    /// * отчёт режима без окна (<c>Headless\HeadlessDecisions.CompositionLines</c>) — чтобы человек
    ///   прочитал отказ раньше, чем будет ждать копию, которой не будет.
    ///
    /// Своей копии запрет не касается: она снимается как раньше, со всем, включая ключ модели.
    /// </summary>
    public static string ShareableWithKeysRefusal(bool withKeys, bool shareable) =>
        withKeys && shareable ? PanelStrings.BackupShareableWithKeysRefused : string.Empty;

    /// <summary>Каталог, который берётся целиком, хотя общее правило мусора его выбрасывает.</summary>
    public const string GitDirectory = ".git";

    /// <summary>`.git` берётся целиком — включая свой <c>logs</c> (reflog), который общее правило съело бы.</summary>
    public static readonly IReadOnlyList<string> WholeGit = new[] { GitDirectory };

    /// <summary>
    /// Список пропуска для дерева, где <c>node_modules</c> — СОДЕРЖИМОЕ, а не мусор:
    /// домашний каталог движка и корни движка с Node.
    /// </summary>
    public static readonly IReadOnlyList<string> KeepModulesTrash = TrashWithout("node_modules");

    /// <summary>Обычный список пропуска (тот же, что у записи архива) — для проектов и панели.</summary>
    public static readonly IReadOnlyList<string> ProjectTrash = TrashWithout();

    /// <summary>Список мусора без названных имён. Нужен там, где общее правило не годится целиком.</summary>
    public static IReadOnlyList<string> TrashWithout(params string[] keep) =>
        ZipWriter.DefaultSkipDirectoryNames
            .Where(name => !keep.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// Состав копии — тот, который решён владельцем.
    ///
    /// **Три режима объёма (v2.2)** приходят сюда параметром <paramref name="scope"/> и НЕ меняют
    /// состав корней: корни одни и те же, меняется только то, что внутри них пропускается.
    /// Формулировка владельца (<c>PROJECT.md</c> §3.3): «автоматический — всё нужное без
    /// восстановимого мусора, <c>.git</c> остаётся; полный — всё как есть, ничего не пропускать;
    /// свой фильтр — названные лишние имена сверх автоматического списка». Умолчание —
    /// автоматический: так вызывающие, которые о режимах не знают, получают прежнее поведение,
    /// и предохранительная копия перед накатом снимается ровно так же, как раньше.
    /// </summary>
    /// <param name="paths">Единственный источник путей панели (изоляция: свой корень — свои пути).</param>
    /// <param name="workingDirectory">
    /// Рабочая папка сервера — то, что человек считает своими проектами. Пусто — папки этой
    /// в копии нет (и это будет названо замечанием, а не умолчано).
    /// </param>
    /// <param name="engine">Найденный движок и Node. Не найден — копия собирается без них, с замечанием.</param>
    /// <param name="withEngine">
    /// Брать ли движок и Node. <c>false</c> — ТОНКАЯ копия, и так берётся предохранительная копия
    /// перед накатом (как в v1): вернуть надо ДАННЫЕ, а движок возвращается установкой. Замечания
    /// об отсутствии движка в этом случае не будет: это не «не нашли», а решение вызывающего.
    /// </param>
    /// <param name="withKeys">
    /// Брать ли каталоги приватных ключей. По умолчанию <c>false</c> — как было и в v1: «по умолчанию
    /// в архив не попадает ни один приватный ключ». Решение владельца 26.09.2026: ключи снова
    /// входят в копию, но именно **по отдельному разрешению**, и оно приходит отсюда.
    /// </param>
    /// <param name="keyDirectories">
    /// Свои каталоги ключей сверх каталога SSH текущего пользователя (<c>AppPaths.SshDir</c>).
    /// Пусто — только каталог SSH. Несуществующие пропускаются; названные человеком и не найденные
    /// называются замечанием, а не исчезают молча.
    /// </param>
    /// <param name="shareable">
    /// «Копия для передачи»: файл ключей доступа (<c>AppPaths.CredentialsFileName</c> — ключ модели
    /// и секрет подписи cookie входа) в архив <b>не кладётся</b>. Решение владельца 26.09.2026:
    /// копией делятся с другим человеком, и ключ модели уехал бы вместе с ней. По умолчанию
    /// <c>false</c> — своя копия снимается как раньше, со всем, включая ключ.
    ///
    /// ⚠️ Именно НЕ КЛАДЁТСЯ, а не бланкируется: пустое значение в этом файле роняет движок
    /// целиком (урок записан в <c>docs\HISTORY.md</c>).
    /// </param>
    /// <param name="scope">
    /// РЕЖИМ ОБЪЁМА — сколько данных человек кладёт в копию (v2.2, формулировка владельца
    /// в <c>PROJECT.md</c> §3.3). Умолчание — <see cref="BackupScope.Auto"/>, то есть ровно то,
    /// что панель делала до появления режимов: вызывающие, которые о режиме не знают
    /// (предохранительная копия перед накатом, режимы без окна), получают прежнее поведение.
    /// </param>
    /// <param name="extraExclusions">
    /// Лишние ИМЕНА папок для режима «свой фильтр» (<see cref="BackupScope.Custom"/>): то, что
    /// человек назвал мусором СВЕРХ автоматического списка (<c>vendor</c>, <c>target</c>,
    /// <c>.venv</c>). Действуют на ВСЕ корни — как и решено владельцем; на корни движка, Node
    /// и каталоги ключей тоже, потому что имя папки не значит «это только про проекты».
    ///
    /// В режиме <see cref="BackupScope.Full"/> список не действует вовсе (там не пропускается
    /// ничего), и об этом план говорит замечанием — иначе выключенный фильтр выглядел бы забытым.
    /// </param>
    public static BackupPlan Full(
        AppPaths paths,
        string? workingDirectory = null,
        DshEngine? engine = null,
        bool withEngine = true,
        bool withKeys = false,
        IEnumerable<string>? keyDirectories = null,
        bool shareable = false,
        BackupScope scope = BackupScope.Auto,
        IEnumerable<string>? extraExclusions = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // ЗАПРЕТ СОЧЕТАНИЯ — ПЕРВЫМ делом и до всякого чтения диска. Решение владельца 29.09.2026
        // (п. 12 `docs\DESIGN.md`, выбран вариант «запрещать», а не «только объяснять»): вместе
        // приватные ключи и «копия для передачи» дают архив с ключами человека и без ключа модели —
        // ровно тот, который передавать нельзя, а он назван «для передачи».
        //
        // ⚠️ Здесь и только здесь: через эту дверь идут ВСЕ пути — окно копий, расписание, накат
        // предохранительной копии и `--backup`. Обойти запрет нельзя ни одним из них.
        var refused = ShareableWithKeysRefusal(withKeys, shareable);
        if (refused.Length > 0) return BackupPlan.Refuse(refused);

        var roots = new List<BackupRoot>();
        var notes = new List<string>();

        // Режим объёма разворачивается ЗДЕСЬ и один раз: дальше по коду о нём знают только две
        // местные функции, и «полный» не может случайно примениться к одному корню и не примениться
        // к другому — а это ровно та тихая потеря, ради которой режимы и разделены.
        var full = scope == BackupScope.Full;
        var extras = BackupScopeDecisions.NormalizeExclusions(extraExclusions);

        // «Всё как есть» — это everything: true, то есть ОТСУТСТВИЕ списка пропуска и списка
        // «берём целиком»: при полном обходе `.git` сохраняется сам собой.
        IReadOnlyList<string>? Skip(IReadOnlyList<string>? normal) =>
            full ? null : BackupScopeDecisions.Merge(normal, extras);

        // «Копия для передачи»: файл ключей доступа и файл ССЫЛКИ ВХОДА не кладём ВООБЩЕ.
        // И это правило про ФАЙЛ, а не про группу: урок уже записан в `docs\HISTORY.md`
        // («пустой файл ключа приезжал ДРУГОЙ группой») — когда корни пересекаются, один и тот же
        // файл едет ДВУМЯ группами. Поэтому пропуск получает КАЖДЫЙ корень, внутри которого
        // этот файл лежит: в изолированном прогоне домашний каталог движка лежит внутри корня
        // панели, и без этого ключ уехал бы второй раз.
        //
        // ⚠️ Ссылка входа попала сюда решением 26.09.2026 вместе с самой ссылкой: в ней ТОКЕН,
        // и «копия для передачи» уезжает другому человеку. В СВОЕЙ копии она остаётся — иначе
        // развёрнутая на той же машине панель потеряла бы кнопку «Открыть панель».
        IReadOnlyList<string>? Restricted(string? directory)
        {
            if (!shareable) return null;

            var names = new List<string>();

            if (BackupPlanner.IsInside(paths.CredentialsPath, directory))
                names.Add(AppPaths.CredentialsFileName);

            if (BackupPlanner.IsInside(paths.EntryLinkFile, directory))
                names.Add(AppPaths.EntryLinkFileName);

            return names.Count == 0 ? null : names;
        }

        // 1. Домашний каталог движка: ключи всех агентов, настройки, плагины, сессии и история.
        //    У «копии для передачи» из него выпадает ровно ОДИН файл — ключи доступа.
        Add(roots, notes, paths.DshHome, BackupRootKind.DshHome,
            PanelStrings.BackupRootDshHome,
            everything: full, skip: Skip(KeepModulesTrash), whole: full ? null : WholeGit,
            files: Restricted(paths.DshHome));

        if (shareable)
        {
            notes.Add(PanelStrings.BackupShareableNote);

            // Замечание про ссылку — ОТДЕЛЬНОЕ и только тогда, когда файл в копируемом дереве есть:
            // иначе это выглядело бы как «мы что-то пропустили», хотя пропускать было нечего.
            if (BackupPlanner.IsInside(paths.EntryLinkFile, paths.Root))
                notes.Add(PanelStrings.BackupEntryLinkNote);
        }

        // 2. Рабочая папка со всеми проектами — требование владельца дословно.
        //    Если рабочая папка — домашний каталог человека, файл ключей лежит и внутри неё:
        //    тогда правило срабатывает и здесь (см. `Restricted`), а не только у движка.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            Add(roots, notes, workingDirectory, BackupRootKind.WorkingFolder,
                PanelStrings.BackupRootWorkingFolder,
                everything: full, skip: Skip(ProjectTrash), whole: full ? null : WholeGit,
                files: Restricted(workingDirectory));
        }
        else
        {
            notes.Add(PanelStrings.BackupWorkDirNotSet);
        }

        // 3. Настройки панели: вся её папка. Она мала, а рядом с настройками лежит журнал,
        //    по которому разбираются, что панель делала.
        Add(roots, notes, paths.Root, BackupRootKind.Panel,
            PanelStrings.BackupRootPanel,
            everything: full, skip: Skip(ProjectTrash), whole: full ? null : WholeGit,
            files: Restricted(paths.Root));

        // 4. Свой движок и Node — иначе копия несамодостаточна, а критерий этапа
        //    «копия → чистая ВМ → работает».
        if (!withEngine)
        {
            // Тонкая копия: так снимается предохранительная копия перед накатом. Молча — потому
            // что это решение вызывающего, а не «движка не нашли».
        }
        else if (engine is null)
        {
            notes.Add(PanelStrings.BackupEngineNotFound);
        }
        else
        {
            var npm = NpmDirectory(engine);
            if (npm is null)
            {
                notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupNpmDirUnknownFormat, DisplayMask.Path(engine.BinPath)));
            }
            else
            {
                // Шимы dsh/npm/pnpm лежат рядом с пакетами: без них восстановленная система
                // не знает команду dsh (урок v1).
                Add(roots, notes, npm, BackupRootKind.EnginePackages,
                    PanelStrings.BackupRootEnginePackages,
                    everything: true, skip: Skip(null), whole: null);
            }

            var node = Path.GetDirectoryName(engine.NodePath);
            Add(roots, notes, node, BackupRootKind.EngineNode,
                PanelStrings.BackupRootNode,
                everything: true, skip: Skip(null), whole: null);
        }

        AddKeyRoots(roots, notes, paths, withKeys, keyDirectories, Skip(null));

        // Режим называют ЗАМЕЧАНИЕМ, а не только подписью в окне: отчёт копии читают и в журнале,
        // и в описи после наката, и «почему копия такая большая» должно быть видно там же, где
        // виден её размер.
        if (full) notes.Add(PanelStrings.BackupScopeFullNote);
        else if (extras.Count > 0)
            notes.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupScopeCustomNoteFormat, string.Join(", ", extras)));

        if (full && extras.Count > 0) notes.Add(PanelStrings.BackupScopeExtrasIgnored);

        return Tidy(roots, notes);
    }

    /// <summary>
    /// Выкидывает из копии сам архив и папку, в которой он лежит.
    ///
    /// Это не придирка, а урок v1: папка копий может оказаться внутри копируемого дерева,
    /// и без этого правила каждая новая копия тащила бы все прежние — то есть копия росла бы
    /// вдвое с каждым разом. v1 спасал пропуск по имени папки; здесь то же, но **названное
    /// в отчёте**: пропуск обязан быть виден.
    /// </summary>
    public static BackupPlan WithoutArchive(BackupPlan plan, string archivePath)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(archivePath)) return plan;

        var archive = Path.GetFullPath(archivePath);
        var folder = Path.GetDirectoryName(archive);
        if (string.IsNullOrEmpty(folder)) return plan;

        var roots = new List<BackupRoot>();
        var notes = new List<string>(plan.Notes);

        foreach (var root in plan.Roots)
        {
            if (!IsInside(archive, root.Directory))
            {
                roots.Add(root);
                continue;
            }

            if (string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Directory)), StringComparison.OrdinalIgnoreCase))
            {
                // Архив лежит в самом корне: папку не выкинуть, и в копию не попадёт ровно
                // файл архива — это делает запись (см. ZipWriter.Write).
                notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupArchiveInRootFormat, root.Prefix));
                roots.Add(root);
                continue;
            }

            var name = Path.GetFileName(folder);
            var skip = root.Source.SkipFor(ZipWriter.DefaultSkipDirectoryNames) ?? Array.Empty<string>();
            if (skip.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
                continue;
            }

            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupFolderInsideRootFormat, name, root.Prefix));

            roots.Add(root with
            {
                Source = root.Source with { SkipDirectoryNames = skip.Concat(new[] { name }).ToArray() },
            });
        }

        return plan with { Roots = roots, Notes = notes };
    }

    /// <summary>
    /// Каталог глобальных пакетов npm: там лежит движок. Ищем от файла движка вверх до каталога
    /// <c>node_modules</c> и берём его родителя — так же, как v1 (`BackupService.NpmRoot`).
    /// Уровней не меньше четырёх (<c>lib</c> → <c>dsh</c> → <c>@scope</c> → <c>node_modules</c>):
    /// на меньшем числе поиск не доходит и возвращает «не найдено».
    /// </summary>
    public static string? NpmDirectory(DshEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetDirectoryName(engine.BinPath) ?? string.Empty);
        }
        catch
        {
            return null;
        }

        for (var level = 0; level < 8 && directory is not null; level++)
        {
            if (string.Equals(directory.Name, "node_modules", StringComparison.OrdinalIgnoreCase))
                return directory.Parent?.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    // --- каталоги ключей (решение владельца 26.09.2026: «как в v1») ----------
    //
    // Ключи входят в копию ТОЛЬКО по отдельному разрешению, и это не осторожность, а правило v1:
    // «по умолчанию в архив не попадает ни один приватный ключ». Причина простая — включённое
    // разрешение делает ключом доступа сам архив.

    /// <summary>
    /// Что именно копировать при включённом разрешении: каталог SSH текущего пользователя
    /// (<see cref="AppPaths.SshDir"/>) и названные человеком каталоги.
    ///
    /// Перенесено из v1 «как есть» (<c>BackupService.KeyDirectories</c>): пустые выбрасываются,
    /// переменные окружения раскрываются, несуществующие пропускаются, повторы (без учёта
    /// регистра) отбрасываются, а ПОРЯДОК сохраняется — он задаёт номера групп в архиве.
    ///
    /// <paramref name="notes"/> — куда сказать про каталог, который человек НАЗВАЛ, а его нет
    /// на диске: про умолчание (<c>.ssh</c>) молчим — его отсутствие значит «ключей нет вовсе»,
    /// а названный руками путь обязан быть назван.
    /// </summary>
    public static IReadOnlyList<string> KeyDirectories(
        AppPaths paths, IEnumerable<string>? configured, List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        var candidates = new List<(string Path, bool Named)> { (paths.SshDir, false) };
        candidates.AddRange((configured ?? Enumerable.Empty<string>())
            .Select(item => (Path: item ?? string.Empty, Named: true)));

        foreach (var (candidate, named) in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
            }
            catch
            {
                NameMissing(notes, named, candidate);
                continue;
            }

            if (!SafeDirectoryExists(expanded))
            {
                NameMissing(notes, named, expanded);
                continue;
            }

            if (!seen.Add(expanded)) continue;
            found.Add(expanded);
        }

        return found;
    }

    /// <summary>Сказать про названный человеком каталог, которого нет: молчание тут — потеря.</summary>
    private static void NameMissing(List<string>? notes, bool named, string path)
    {
        if (!named || notes is null) return;

        notes.Add(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.BackupKeyDirMissingFormat, DisplayMask.Path(path)));
    }

    /// <summary>
    /// Имена групп для каталогов ключей: <c>keys/&lt;номер&gt;-&lt;slug&gt;</c>.
    /// Перенесено из v1 «как есть» (<c>BackupService.KeyRoots</c>), включая добавление «x» до
    /// уникальности: в v1 имя бралось из массива на четыре элемента, и пятый каталог получал уже
    /// занятое имя — одноимённые файлы (<c>id_rsa</c>, <c>known_hosts</c>) смешивались, и накат
    /// терял часть ключей. Здесь этого не случится ни при каком числе каталогов.
    ///
    /// ⚠️ Вложенное имя группы (<c>keys/1-ssh</c>) закрывает быстрый путь через 7-Zip — и это
    /// НОРМАЛЬНО, а не потеря: такие корни пишутся своим обходом (<see cref="ZipLayout"/>).
    /// Имя взято у v1 намеренно: по нему человек читает архив, и менять его значит менять
    /// привычный вид копии.
    /// </summary>
    public static IReadOnlyList<(string Directory, string Prefix)> KeyRoots(
        AppPaths paths, IEnumerable<string>? configured, List<string>? notes = null)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Directory, string Prefix)>();
        var index = 0;

        foreach (var candidate in KeyDirectories(paths, configured, notes))
        {
            index++;
            var slug = Slug(Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate)));
            var name = slug.Length > 0
                ? index.ToString(CultureInfo.InvariantCulture) + "-" + slug
                : index.ToString(CultureInfo.InvariantCulture);

            while (!used.Add(name)) name += "x";

            roots.Add((candidate, "keys/" + name));
        }

        return roots;
    }

    /// <summary>
    /// Короткое имя каталога для группы в архиве: только латиница (в нижнем регистре) и цифры,
    /// остальные символы — дефис, обрезка до 20 знаков, пустой результат — «имени нет».
    /// Перенесено из v1 (<c>BackupService.Slug</c>) дословно.
    /// </summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder();

        foreach (var symbol in name ?? string.Empty)
        {
            if (char.IsLetterOrDigit(symbol) && symbol < 128) builder.Append(char.ToLowerInvariant(symbol));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var text = builder.ToString().Trim('-');
        return text.Length > 20 ? text[..20] : text;
    }

    /// <summary>
    /// Добавить каталоги ключей к плану — или сказать, что их в копии нет.
    ///
    /// Разрешение приходит параметром, а не читается здесь из настроек: состав копии — ЧИСТОЕ
    /// решение, а настройки читает вызывающий (<c>Backup\BackupController</c>). Так правило
    /// перебирается проверками, не поднимая файл настроек.
    /// </summary>
    /// <param name="extraSkip">
    /// Лишние имена папок из режима «свой фильтр» — действуют и здесь: владелец назвал их как
    /// «не класть в копию», и каталог ключей не исключение из его слова. В режиме «полный» сюда
    /// приходит <c>null</c> (там не пропускается ничего).
    /// </param>
    private static void AddKeyRoots(
        List<BackupRoot> roots,
        List<string> notes,
        AppPaths paths,
        bool withKeys,
        IEnumerable<string>? configured,
        IReadOnlyList<string>? extraSkip)
    {
        if (!withKeys)
        {
            WarnAboutSshKeys(notes, paths);
            return;
        }

        var keyRoots = KeyRoots(paths, configured, notes);

        if (keyRoots.Count == 0)
        {
            notes.Add(PanelStrings.BackupKeysEmpty);
            return;
        }

        foreach (var (directory, prefix) in keyRoots)
        {
            // Внутри каталога ключей не пропускается НИЧЕГО из общего списка мусора
            // (everything: true): человек разрешил взять каталог целиком, и общее правило мусора
            // тут не судья — оно про сборки и кэши, а здесь лежат файлы, ради которых разрешение
            // и давалось. Названные ЧЕЛОВЕКОМ имена — другое дело: они приходят сюда явно.
            Add(roots, notes, directory, BackupRootKind.KeyRing,
                PanelStrings.BackupRootKeys,
                everything: true, skip: extraSkip, whole: null, groupName: prefix);
        }

        notes.Add(string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.BackupKeysIncludedFormat,
            string.Join(", ", keyRoots.Select(root => $"«{root.Prefix}»"))));
    }

    // --- сборка корней и проверки раскладки ----------------------------------

    private static void Add(
        List<BackupRoot> roots,
        List<string> notes,
        string? directory,
        BackupRootKind kind,
        string purpose,
        bool everything,
        IReadOnlyList<string>? skip,
        IReadOnlyList<string>? whole,
        IReadOnlyList<string>? files = null,
        string? groupName = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        if (!SafeDirectoryExists(directory))
        {
            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupPlanKeyDirMissingFormat, DisplayMask.Path(directory), purpose));
            return;
        }

        // Имя группы: либо названное вызывающим (каталоги ключей — у них своё правило,
        // `keys/1-ssh`), либо имя папки на диске (так решено для остальных корней: см.
        // комментарий класса и ограничение 7-Zip).
        var prefix = groupName ?? ZipLayout.Leaf(directory);
        if (prefix.Length == 0)
        {
            // Корень диска: имени папки у него нет. Копировать весь диск целиком — это не
            // «рабочая папка с проектами», а весь диск; отказываем и говорим словами.
            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupKeyDirIsDriveRootFormat, DisplayMask.Path(directory)));
            return;
        }

        roots.Add(new BackupRoot(
            new BackupSource(Path.GetFullPath(directory), prefix, everything, skip, whole, files),
            kind,
            purpose));
    }

    /// <summary>
    /// Убирает точные повторы и называет пересечения, проверяет имена групп. Всё это — ЧИСТЫЕ
    /// решения: они не касаются диска, поэтому их можно перебрать проверками, а не верить на слово.
    ///
    /// * **одна и та же папка дважды** — второй раз не берём. Иначе два корня получили бы одно
    ///   имя группы, а свой способ записи положил бы их записи под одним именем, то есть смешал бы
    ///   два дерева. Так выглядит обычная установка: пакеты npm лежат под папкой Node;
    /// * **папка внутри другой** (или содержащая её) — берём ОБЕ и называем это замечанием.
    ///   Отбрасывать вложенную нельзя: у корня есть не только содержимое, но и смысл — именно
    ///   группа говорит накату, что это домашний каталог движка, а что рабочая папка.
    ///   ⚠️ И правило не должно зависеть от ПОРЯДКА корней: «кто первый встал» решало бы,
    ///   что попадёт в копию, — а это уже тихая потеря;
    /// * **два разных корня с одним именем группы** — ОТКАЗ: записи легли бы под одним именем
    ///   и смешались, а это потеря данных, а не «неудобная раскладка».
    /// </summary>
    private static BackupPlan Tidy(List<BackupRoot> roots, List<string> notes)
    {
        var kept = new List<BackupRoot>();

        foreach (var root in roots)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Directory));

            var same = kept.FirstOrDefault(other =>
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.Directory)), full,
                    StringComparison.OrdinalIgnoreCase));

            if (same is not null)
            {
                notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupSameFolderFormat, root.Prefix, same.Prefix));
                continue;
            }

            var outer = kept.FirstOrDefault(other => IsInside(full, other.Directory));
            var inner = kept.FirstOrDefault(other => IsInside(other.Directory, full));

            if (outer is not null)
                notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupFolderNestedFormat, root.Prefix, outer.Prefix));

            if (inner is not null)
                notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupFolderContainsFormat, root.Prefix, inner.Prefix));

            kept.Add(root);
        }

        var conflict = kept
            .GroupBy(root => root.Prefix, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (conflict is not null)
        {
            var names = string.Join(", ", conflict.Select(root => DisplayMask.Path(root.Directory)));
            return new BackupPlan(kept, notes,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupSameGroupNameFormat, conflict.Key, names));
        }

        return new BackupPlan(kept, notes, string.Empty);
    }

    /// <summary>
    /// Ключей в копии НЕТ — и человеку об этом стоит сказать, а не оставлять его думать, что
    /// «всё унёс». Замечание появляется, только если каталог ключей на диске действительно есть
    /// и не пуст: пустой каталог ничего не теряет, и пугать им незачем.
    ///
    /// Каталог берётся у <see cref="AppPaths.SshDir"/>, а не по домашнему пути: в изолированном
    /// прогоне это ЕГО каталог, и проверка не должна заглядывать к владельцу.
    /// </summary>
    private static void WarnAboutSshKeys(List<string> notes, AppPaths paths)
    {
        try
        {
            if (SafeDirectoryExists(paths.SshDir) && Directory.EnumerateFileSystemEntries(paths.SshDir).Any())
                notes.Add(PanelStrings.BackupKeysNotIncluded);
        }
        catch
        {
            // Нет каталога или нет прав его посмотреть — замечания не будет: это не про копию.
        }
    }

    private static bool SafeDirectoryExists(string? directory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Лежит ли путь внутри каталога. Сверка по границе пути, а не по подстроке.</summary>
    public static bool IsInside(string? candidate, string? parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent)) return false;

        try
        {
            var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
            var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;

            return inside.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
