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

    /// <summary>Только те методы, что ничего не меняют. Калитке телефона (этап F) нужны прежде всего они.</summary>
    public static IReadOnlyList<EngineRpcMethod> Reading =>
        Methods.Where(m => m.ReadOnly).ToList();
}
