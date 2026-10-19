namespace DshPanel.Server;

/// <summary>Как движок объявил метод: одиночный вызов или поток (у потока свой канал, `/api/remote.mux`).</summary>
public enum EngineRpcMode
{
    Unary,
    Stream,
}

/// <summary>
/// Один метод поверхности RPC: имя, вид вызова и признак «только чтение» (для отчёта и для
/// будущей калитки телефона: писать через неё можно не всё).
/// </summary>
public sealed record EngineRpcMethod(string Endpoint, EngineRpcMode Mode, bool ReadOnly);

/// <summary>
/// ПОВЕРХНОСТЬ RPC НАШЕГО ДВИЖКА — ЗАКРЕПЛЁННЫЙ СПИСОК, ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ.
///
/// **Зачем список вообще.** Панель перед выпуском обязана заметить, что движок сменил имена
/// или форму вызова, — ДО людей, а не после. Способ один: держать список того, на что панель
/// опирается, и **падать**, когда движка он больше не устраивает. Тот же список — будущий
/// контракт калитки телефона (этап F): мобильная поверхность опирается на те же имена, и
/// второго списка быть не должно.
///
/// ⚠️ **СПИСОК СНЯТ НА НАШЕЙ ВЕРСИИ, А НЕ ВЗЯТ У СОСЕДЕЙ.** У соседей движок `0.2.0-rc.2`,
/// и их список для нас не источник вовсе. Наш замер — 10.10.2026, на установленном пакете
/// `@deepseek-ai/dsh` **<see cref="MeasuredOnVersion"/>** (тот, который находит
/// <see cref="DshEngine.Locate"/>). Снят ТРЕМЯ источниками, которые сошлись посимвольно:
/// объявления, которые движок генерирует у себя (`lib/typert.remote-client.d.ts` в пятнадцати
/// пакетах), реестр дескрипторов в `dsh-api-remotes`, и **живая проба каждого имени** на
/// настоящем движке — **84 из 84 ответили `200` с конвертом, ни одного `404`**, а из 56
/// выдуманных и похожих имён отказ получили 55 (единственное попадание — настоящее
/// `pluginInventory/list`).
///
/// ⚠️ **Имена у НАШЕЙ версии — со СЛЭШЕМ** (`session/list`), а не через точку: это ровно тот
/// переезд, о котором предупреждали соседи («методы переехали на `&lt;namespace&gt;/&lt;method&gt;`
/// целиком»). Прежняя запись в `docs\ENGINE.md` (§8) говорила `session.*` — она этому замеру
/// больше не соответствует и поправлена.
///
/// **Линия 0.2.x — второй снимок, а не второй список.** Панель 2.2.0 переезжает на 0.2.x, и её
/// проба обязана отличать «движок стал другим и это беда» от «движок стал другим, и мы это знали»:
/// у `0.2.1-rc.2` объявлено 144 имени — 77 наших живы, 7 сняты с надобности (`Retired`, живьём
/// не зовутся), 67 новых (`KnownAdded`, панели не нужны). Проверяется ЖИВЬЁМ только `Required`
/// (77), отсутствие снятых законно, а НЕЗНАКОМОЕ имя — по-прежнему провал. Замер, числа и правило —
/// в блоке констант ниже и в `docs\ENGINE.md`; отчёт замера — `_team\220\a-poverhnost-imen.md`.
///
/// **Что здесь НЕ утверждается.** Список говорит «эти имена у движка ЕСТЬ», и ничего про то,
/// что каждое из них панель уже использует: сегодня панель ходит в движок только запуском
/// процесса, а RPC не зовёт вовсе. Список — договор на будущее и сторож на сегодня.
/// </summary>
public static class EngineRpcSurface
{
    /// <summary>Версия движка, на которой список снят замером (установленный пакет).</summary>
    public const string MeasuredOnVersion = "0.1.5-rc.2";

    /// <summary>Когда снят замер.</summary>
    public const string MeasuredOn = "10.10.2026";

    /// <summary>
    /// Сколько имён в списке на момент замера. Число сторожит ПОПОЛНЕНИЕ И ПРОПАЖУ: список,
    /// из которого молча вычеркнули строку, перестал бы её и проверять.
    /// </summary>
    public const int MeasuredCount = 84;

    // ---------------------------------------------------------------------------------------
    // ВТОРОЙ СНИМОК ПОВЕРХНОСТИ — ЛИНИЯ 0.2.x (замер 18.10.2026, задача t3 плана 2.2.0)
    //
    // Зачем второй снимок. Панель 2.2.0 переезжает на движок 0.2.x, а её закреплённый список
    // снят на 0.1.5-rc.2. Без второго снимка проба на 0.2.x честно кричала бы «поверхность
    // изменилась» — и действительно менялась бы, но не в ту сторону, которая ломает панель.
    //
    // Что измерено (своим разбором объявлений движка, 29 файлов `typert.remote-client.d.ts`,
    // сошлось с разведкой E4 до единицы):
    //   * у `0.2.1-rc.2` объявлено 144 имени: 77 наших живы, 7 наших пропали, 67 новых в 16 семействах;
    //   * 7 пропавших — ТОЛЬКО в каталоге панели, живьём ни одно из них не зовётся (разведка E4 §5:
    //     каждое встречается в `src` ровно один раз — в этом файле), поэтому потеря ничего не ломает;
    //   * 67 новых панели НЕ НУЖНЫ — они названы затем, чтобы «поверхность изменилась» было
    //     посчитанным фактом, а не тревогой с чужого слова.
    //
    // Отсюда правило пробы (см. `EngineRpcProbe`): обязательны 77 нужных имён (`Required`),
    // отсутствие 7 снятых с надобности (`Retired`) — законно, а НЕЗНАКОМОЕ имя у движка —
    // по-прежнему тревога: следующая версия обязана быть замечена до людей.
    // ---------------------------------------------------------------------------------------

    /// <summary>Версия движка, на которой снят второй снимок (линия 0.2.x).</summary>
    public const string KnownOnVersion = "0.2.1-rc.2";

    /// <summary>Когда снят второй снимок.</summary>
    public const string KnownOn = "18.10.2026";

    /// <summary>Сколько имён объявляет `0.2.1-rc.2`: 77 нужных панели + 67 новых.</summary>
    public const int KnownOnDeclaredCount = 144;

    /// <summary>Сколько новых имён у линии 0.2.x — тех, что панели не нужны.</summary>
    public const int KnownAddedCount = 67;

    /// <summary>Сколько семейств у этих 67 новых имён.</summary>
    public const int KnownAddedFamilies = 16;

    /// <summary>Сколько наших имён живо у линии 0.2.x (и обязаны быть живы).</summary>
    public const int RequiredCountOnKnown = 77;

    /// <summary>
    /// СЕМЬ ИМЁН, КОТОРЫЕ У ЛИНИИ 0.2.x ПРОПАЛИ, — И ЭТО ЗАКОННО.
    ///
    /// **Почему законно.** Разведка E4 проверила по репозиторию: каждое из семи встречается в `src`
    /// **ровно один раз** — в этом файле; ни один живой вызов панели их не зовёт (ни телефонная
    /// калитка, ни проба умений, ни одна дверь). То есть это не «потерянные вызовы», а строки
    /// каталога, снятые с надобности: у 0.1.5 они есть, у 0.2.x их нет, и панели всё равно.
    ///
    /// ⚠️ **Список НЕ вычёркивается молча, и это важно:** `Required` = список минус эти семь,
    /// а сама семёрка остаётся названной — иначе через полгода никто не ответит, почему в списке
    /// 84 имени, а проверяется 77. Запись в `engine-hints.json` про 0.2.1 называет их же — сверка
    /// стережёт `EngineHintsTests`.
    /// </summary>
    public static IReadOnlyList<string> Retired { get; } = new[]
    {
        // dsh-agent-presets
        "agentPresets/copy",
        "agentPresets/deletePreset",

        // dsh-api-settings-controller (вместе с credentials)
        "settings/canOpenAgentPresetDirectory",
        "settings/openAgentPresetDirectory",

        // dsh-subagent · dsh-api-workspace-files
        "subagents/list",
        "workspaceFiles/readAll",
        "workspaceFiles/readRelated",
    };

    /// <summary>
    /// 67 ИМЁН, КОТОРЫЕ У ЛИНИИ 0.2.x ПОЯВИЛИСЬ. Панели они не нужны — но перечислены, потому что
    /// «у движка что-то появилось» обязано быть фактом каталога, а не неожиданностью на пробе.
    ///
    /// Вид вызова (одиночный/поток) у новых имён **не размечался намеренно**: панель их не зовёт,
    /// а догадка о виде потока стала бы утверждением без замера. Понадобятся — размечаются вместе
    /// с первым живым вызовом.
    /// </summary>
    public static IReadOnlyList<string> KnownAdded { get; } = new[]
    {
        // pluginManager = 12
        "pluginManager/cancelInstall", "pluginManager/inspect", "pluginManager/installBundle",
        "pluginManager/listBundles", "pluginManager/listPlugins", "pluginManager/listVersionExemptions",
        "pluginManager/registries", "pluginManager/removeBundle", "pluginManager/setBundleEnabled",
        "pluginManager/setPluginEnabled", "pluginManager/setVersionExemption", "pluginManager/waitForInstall",

        // account = 11
        "account/ackBonusNotified", "account/cancelSignIn", "account/getBalance", "account/getProfile",
        "account/getState", "account/getUnnotifiedBonuses", "account/hasRunningAccountTasks",
        "account/signOut", "account/startSignIn", "account/watch", "account/watchExpiry",

        // terminal = 8
        "terminal/attach", "terminal/close", "terminal/create", "terminal/environment",
        "terminal/list", "terminal/rename", "terminal/retain", "terminal/shells",

        // speech = 6
        "speech/cancelPreparation", "speech/catalog", "speech/configure", "speech/follow",
        "speech/prepare", "speech/transcribe",

        // schedule = 5
        "schedule/catalog", "schedule/delete", "schedule/history", "schedule/list", "schedule/update",

        // workspace = 4
        "workspace/initializeDefault", "workspace/pinSession", "workspace/unarchiveSession",
        "workspace/unpinSession",

        // job · pluginMarket · productAnalytics · session = по 3
        "job/follow", "job/kill", "job/list",
        "pluginMarket/listIndexes", "pluginMarket/search", "pluginMarket/setIndexes",
        "productAnalytics/enabled", "productAnalytics/report", "productAnalytics/watchPolicy",
        "session/initializeDefaultModel", "session/projections", "session/workspacePathApplications",

        // cotTranslation · officeToPdf · userQuestions = по 2
        "cotTranslation/limits", "cotTranslation/translate",
        "officeToPdf/generation", "officeToPdf/render",
        "userQuestions/answer", "userQuestions/attachWait",

        // permissionPresets · pluginRegistryProbe · remoteAccess = по 1
        "permissionPresets/catalog",
        "pluginRegistryProbe/fastest",
        "remoteAccess/follow",
    };

    /// <summary>
    /// Внутренний адрес движка, который пробой НЕ проверяется намеренно: `$events/result`
    /// у движка занят всегда (`200` + `gateway/internal`), но это его служебная дверь для
    /// событий приложения, а не поверхность для панели. Названо здесь, чтобы «мы его пропустили»
    /// не выглядело забывчивостью.
    /// </summary>
    public const string InternalEndpoint = "$events/result";

    /// <summary>
    /// Имя, которого у движка быть НЕ МОЖЕТ. Им проверяется обратная сторона правила: если
    /// движок «признаёт» и его, значит признак «признал» не значит ничего, и вся проба — самообман.
    /// </summary>
    public const string BogusEndpoint = "dshPanelProbe/noSuchMethod";

    /// <summary>
    /// Доводы, которые заведомо не годятся ни одному методу (поля с таким именем у движка нет).
    /// Зачем так: проверка доводов идёт ПОСЛЕ поиска метода, поэтому «доводы не те» — это
    /// доказательство, что ИМЯ есть, а исполнить бизнес-код при этом движок не станет.
    ///
    /// ⚠️ Это ЗНАЧЕНИЕ <c>payload.args</c>, а не весь конверт: движок берёт доводы оттуда
    /// (замер 10.10.2026), и конверт без `args` он разбирает как `gateway/bad-request` — то есть
    /// ответ «конверт плох», из которого признания метода НЕ следует.
    /// </summary>
    public const string InvalidPayload = "{\"dshPanelProbe\":true}";

    /// <summary>
    /// Настоящий вызов пробы: чтение списка сессий. Вызов ничего не меняет, а доказательство даёт
    /// сильное: наш конверт движок принимает ЦЕЛИКОМ, а не только узнаёт имена.
    ///
    /// ⚠️ Довод назван `_request` не по вкусу, а по ЗАМЕРУ: у движка `session/list` объявлен как
    /// `(_request: SessionListRequest, signal?)`, и доводы на проводе именованные. Ответ на этот
    /// вызов (замер): `{"result":{"ok":true,"value":{"items":[]}}}` — пусто, потому что дом движка
    /// у прогона одноразовый.
    /// </summary>
    public const string RealCallEndpoint = "session/list";

    public const string RealCallPayload = "{\"_request\":{}}";

    /// <summary>
    /// Вся поверхность нашего движка. Порядок — по пакетам-владельцам (как в объявлениях движка),
    /// чтобы глазом было видно, чего пропало.
    /// </summary>
    public static IReadOnlyList<EngineRpcMethod> Methods { get; } = new[]
    {
        // dsh-agent-presets
        new EngineRpcMethod("agentPresets/copy", EngineRpcMode.Unary, false),
        new EngineRpcMethod("agentPresets/deletePreset", EngineRpcMode.Unary, false),
        new EngineRpcMethod("agentPresets/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("agentPresets/read", EngineRpcMode.Unary, true),
        new EngineRpcMethod("agentPresets/select", EngineRpcMode.Unary, false),

        // dsh-api-session-controller (вместе с fileReferences и skills)
        new EngineRpcMethod("fileReferences/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/attachment", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/cancel", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/canOpenWorkspacePath", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/control", EngineRpcMode.Stream, true),
        new EngineRpcMethod("session/create", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/follow", EngineRpcMode.Stream, true),
        new EngineRpcMethod("session/fork", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/modelCatalog", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/openWorkspacePath", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/page", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/prompt", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/rename", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/search", EngineRpcMode.Unary, true),
        new EngineRpcMethod("session/selectModel", EngineRpcMode.Unary, false),
        new EngineRpcMethod("session/updateQueue", EngineRpcMode.Unary, false),
        new EngineRpcMethod("skills/list", EngineRpcMode.Unary, true),

        // dsh-api-settings-controller (вместе с credentials)
        new EngineRpcMethod("credentials/describe", EngineRpcMode.Unary, true),
        new EngineRpcMethod("credentials/set", EngineRpcMode.Unary, false),
        new EngineRpcMethod("credentials/unset", EngineRpcMode.Unary, false),
        new EngineRpcMethod("settings/canOpenAgentPresetDirectory", EngineRpcMode.Unary, true),
        new EngineRpcMethod("settings/describe", EngineRpcMode.Unary, true),
        new EngineRpcMethod("settings/mutate", EngineRpcMode.Unary, false),
        new EngineRpcMethod("settings/openAgentPresetDirectory", EngineRpcMode.Unary, false),
        new EngineRpcMethod("settings/openSettingsDocument", EngineRpcMode.Unary, false),
        new EngineRpcMethod("settings/replace", EngineRpcMode.Unary, false),
        new EngineRpcMethod("settings/update", EngineRpcMode.Unary, false),

        // dsh-api-workspace-controller (вместе с directoryPicker)
        new EngineRpcMethod("directoryPicker/createDirectory", EngineRpcMode.Unary, false),
        new EngineRpcMethod("directoryPicker/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("directoryPicker/pick", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/archiveSession", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/create", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/delete", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/follow", EngineRpcMode.Stream, true),
        new EngineRpcMethod("workspace/insertBefore", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/insertSessionBefore", EngineRpcMode.Unary, false),
        new EngineRpcMethod("workspace/rename", EngineRpcMode.Unary, false),

        // dsh-api-workspace-files
        new EngineRpcMethod("workspaceFiles/changes", EngineRpcMode.Stream, true),
        new EngineRpcMethod("workspaceFiles/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("workspaceFiles/read", EngineRpcMode.Unary, true),
        new EngineRpcMethod("workspaceFiles/readAll", EngineRpcMode.Unary, true),
        new EngineRpcMethod("workspaceFiles/readBytes", EngineRpcMode.Unary, true),
        new EngineRpcMethod("workspaceFiles/readRelated", EngineRpcMode.Unary, true),
        new EngineRpcMethod("workspaceFiles/stat", EngineRpcMode.Unary, true),

        // dsh-client-file-upload
        new EngineRpcMethod("fileUploads/upload", EngineRpcMode.Unary, false),

        // dsh-command-feedback
        new EngineRpcMethod("sessionFeedback/record", EngineRpcMode.Unary, false),

        // dsh-commands
        new EngineRpcMethod("commands/execute", EngineRpcMode.Unary, false),
        new EngineRpcMethod("commands/list", EngineRpcMode.Unary, true),

        // dsh-cordis-host-runner
        new EngineRpcMethod("dynamicCordisRunner/getClientCode", EngineRpcMode.Unary, true),
        new EngineRpcMethod("dynamicCordisRunner/inventory", EngineRpcMode.Unary, true),
        new EngineRpcMethod("dynamicCordisRunner/invoke", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/reportClientGuardFailure", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/reportRenderFailure", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/resolveInspectQuery", EngineRpcMode.Unary, true),
        new EngineRpcMethod("dynamicCordisRunner/resolveRequestRun", EngineRpcMode.Unary, true),
        new EngineRpcMethod("dynamicCordisRunner/runHostHalf", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/settleUserRun", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/stopFromPanel", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/syncInspectManifest", EngineRpcMode.Unary, false),
        new EngineRpcMethod("dynamicCordisRunner/undefineFromPanel", EngineRpcMode.Unary, false),

        // dsh-goal
        new EngineRpcMethod("goals/clear", EngineRpcMode.Unary, false),
        new EngineRpcMethod("goals/complete", EngineRpcMode.Unary, false),
        new EngineRpcMethod("goals/create", EngineRpcMode.Unary, false),
        new EngineRpcMethod("goals/edit", EngineRpcMode.Unary, false),
        new EngineRpcMethod("goals/get", EngineRpcMode.Unary, true),
        new EngineRpcMethod("goals/pause", EngineRpcMode.Unary, false),
        new EngineRpcMethod("goals/resume", EngineRpcMode.Unary, false),

        // dsh-host-plugin-inventory
        new EngineRpcMethod("pluginInventory/list", EngineRpcMode.Unary, true),

        // dsh-llm
        new EngineRpcMethod("llm/discoverModels", EngineRpcMode.Unary, true),
        new EngineRpcMethod("llm/listConfigurableProviders", EngineRpcMode.Unary, true),
        new EngineRpcMethod("llm/listProviders", EngineRpcMode.Unary, true),

        // dsh-message-feedback
        new EngineRpcMethod("messageFeedback/delete", EngineRpcMode.Unary, false),
        new EngineRpcMethod("messageFeedback/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("messageFeedback/put", EngineRpcMode.Unary, false),

        // dsh-session-reference
        new EngineRpcMethod("sessionReferenceResolver/candidates", EngineRpcMode.Unary, true),

        // dsh-subagent
        new EngineRpcMethod("subagents/interruptByParent", EngineRpcMode.Unary, false),
        new EngineRpcMethod("subagents/list", EngineRpcMode.Unary, true),
        new EngineRpcMethod("subagents/prompt", EngineRpcMode.Unary, false),
    };

    /// <summary>Семейства имён, какие есть у движка: по ним видно, чего в списке не стало.</summary>
    public static IReadOnlyList<string> Namespaces =>
        Methods.Select(m => m.Endpoint.Split('/')[0]).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Семейства 67 новых имён линии 0.2.x — их 16, и это тоже посчитанный факт.</summary>
    public static IReadOnlyList<string> KnownAddedNamespaces =>
        KnownAdded.Select(n => n.Split('/')[0]).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Только те методы, что ничего не меняют. Калитке телефона (этап F) нужны прежде всего они.</summary>
    public static IReadOnlyList<EngineRpcMethod> Reading =>
        Methods.Where(m => m.ReadOnly).ToList();

    /// <summary>
    /// ЧТО ПРОБА ОБЯЗАНА НАЙТИ У ДВИЖКА — список 84 минус снятые с надобности семь (77 имён).
    ///
    /// ⚠️ Проверяется ЖИВЬЁМ именно этот список, а не все 84: имя, которое панель не зовёт, не даёт
    /// права провалить пробу и объявить годный движок негодным. Зато пропажа НУЖНОГО имени —
    /// по-прежнему провал (см. `EngineRpcProbe`).
    /// </summary>
    public static IReadOnlyList<EngineRpcMethod> Required { get; } =
        Methods.Where(m => !Retired.Contains(m.Endpoint, StringComparer.Ordinal)).ToList();

    /// <summary>Сколько имён пробе обязательно (77 на замере 18.10.2026).</summary>
    public static int RequiredTotal => Required.Count;

    /// <summary>
    /// ЧЕГО У ДВИЖКА НЕТ ИЗ НУЖНОГО. Пусто — все нужные имена движок объявляет.
    /// </summary>
    public static IReadOnlyList<string> MissingRequired(IReadOnlyCollection<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var have = declared.ToHashSet(StringComparer.Ordinal);
        return Required.Select(m => m.Endpoint).Where(name => !have.Contains(name)).ToList();
    }

    /// <summary>
    /// СКОЛЬКО ИЗ СЕМИ СНЯТЫХ С НАДОБНОСТИ ИМЁН ДВИЖОК БОЛЬШЕ НЕ ОБЪЯВЛЯЕТ. Ноль — движок старой
    /// линии (0.1.5: все семь на месте); семь — линия 0.2.x. Провалом не является НИ ОДНО значение:
    /// панель этих имён не зовёт.
    /// </summary>
    public static IReadOnlyList<string> LostKnown(IReadOnlyCollection<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var have = declared.ToHashSet(StringComparer.Ordinal);
        return Retired.Where(name => !have.Contains(name)).ToList();
    }

    /// <summary>
    /// ИМЕНА, С КОТОРЫМИ ПАНЕЛЬ НЕ ЗНАКОМА ВООБЩЕ. Непустой список — провал пробы: поверхность
    /// движка изменилась, и панель обязана это заметить до людей, а не после.
    /// </summary>
    public static IReadOnlyList<string> UnknownAdded(IReadOnlyCollection<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in Methods) known.Add(method.Endpoint);
        foreach (var name in KnownAdded) known.Add(name);

        return declared.Where(name => !known.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Сколько знакомых новых имён движок действительно объявляет (на 0.2.1 — 67 из 67).</summary>
    public static int KnownAddedPresent(IReadOnlyCollection<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var have = declared.ToHashSet(StringComparer.Ordinal);
        return KnownAdded.Count(name => have.Contains(name));
    }
}
