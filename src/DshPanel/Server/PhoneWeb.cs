using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПУЛЬТ S1+S2 ВНУТРИ EXE: оболочка, её ассеты и слова, которые она читает из `data-boot`.
///
/// **Почему внутри, а не файлами рядом.** Панель раздаётся ОДНИМ exe (так же, как словари и значок):
/// файл, который может потеряться или не уехать в архив, для интерфейса не годится. Поэтому
/// `src\web` собирается в `src\DshPanel\WebAssets`, а проект кладёт его в ресурсы сборки
/// (`EmbeddedResource` в `DshPanel.csproj`), и имена ресурсов заданы ЯВНО (`LogicalName`): имя,
/// собранное из пути точками, ломается от переименования папки, а опечатка в нём ищется прогоном.
///
/// **Что здесь есть и чего нет:**
/// * <see cref="Shell"/> — оболочка с подставленными словами, языком и `data-boot` (JSON, экранированный
///   как значение атрибута);
/// * <see cref="Asset"/> — ОДИН файл ассетов по имени из <c>assets.json</c> (никакого обхода каталога:
///   имя сравнивается с именами ресурсов один в один);
/// * <see cref="Csp"/> — политика новой поверхности. `style-src` без `unsafe-inline`: полу
///   (Semi) не нужны ни `<style>`-вставки, ни `style=`-атрибуты от нас — проверено чтением
///   собранного бандла (ни `createElement("style")`, ни `insertRule`); если живой прогон скажет иное,
///   это будет НАЗВАННЫМ отступлением, а не «политику ослабили на всякий случай».
///
/// ⚠️ **Никаких внешних ресурсов.** В бандле нет сетевых адресов (проверка прогоном), шрифты и
/// картинки — `data:` внутри CSS; поэтому `default-src 'none'` и `img-src/font-src 'self' data:`.
/// </summary>
public static class PhoneWeb
{
    /// <summary>Ресурс оболочки (имя задано в csproj через `LogicalName`).</summary>
    public const string ShellResource = "DshPanel.WebAssets.shell.html";

    /// <summary>Ресурс карты ассетов (её пишет сборка веба: `js`, `css`, `placeholders`).</summary>
    public const string MapResource = "DshPanel.WebAssets.assets.json";

    /// <summary>Начало имён ресурсов ассетов: `DshPanel.WebAssets.assets.<файл>`.</summary>
    public const string AssetResourcePrefix = "DshPanel.WebAssets.assets.";

    /// <summary>Адрес, по которому телефон получает файл ассетов.</summary>
    public const string AssetPath = "/assets/";

    /// <summary>Сколько живёт ассет в кэше телефона: имя файла содержит хеш, значит содержимое не меняется.</summary>
    public const string AssetCache = "public, max-age=31536000, immutable";

    /// <summary>
    /// ПОЛИТИКА НОВОЙ ПОВЕРХНОСТИ. `frame-ancestors 'none'` здесь тоже: оболочку нельзя вставлять
    /// в чужую рамку так же, как и остальные ответы калитки.
    /// </summary>
    public const string Csp =
        "default-src 'none'; "
        + "script-src 'self'; "
        + "style-src 'self'; "
        + "img-src 'self' data:; "
        + "font-src 'self' data:; "
        + "connect-src 'self'; "
        + "manifest-src 'self'; "
        + "base-uri 'none'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    /// <summary>
    /// КЛЮЧИ СЛОВ, КОТОРЫЕ ЗОВЁТ ПУЛЬТ (число НЕ пишем: оно врало — список рос, а счёт нет).
    /// Список ОДИН на калитку и сверяется проверкой с
    /// `src\web\src\i18n.ts` (там он же для TypeScript): разойдись они — экран покажет имя ключа,
    /// и это ловится прогоном, а не глазами.
    ///
    /// ⚠️ Каждое имя стоит здесь как `nameof(PanelStrings.X)`: это и есть место вызова для
    /// `SourceStringsTests` (строка, которую не зовёт ни один продуктовый файл, — мёртвая).
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        nameof(PanelStrings.PhoneWebListTitle),
        nameof(PanelStrings.PhoneWebListCountFormat),
        nameof(PanelStrings.PhoneWebListShownFormat),
        nameof(PanelStrings.PhoneWebListEmptyTitle),
        nameof(PanelStrings.PhoneWebListEmpty),
        nameof(PanelStrings.PhoneWebStateWaiting),
        nameof(PanelStrings.PhoneWebStateRunning),
        nameof(PanelStrings.PhoneWebStateDone),
        nameof(PanelStrings.PhoneWebStateBlank),
        nameof(PanelStrings.PhoneWebUntitled),
        nameof(PanelStrings.PhoneWebInboxFormat),
        nameof(PanelStrings.PhoneWebStatsFormat),
        nameof(PanelStrings.PhoneWebBackToList),
        nameof(PanelStrings.PhoneWebRefresh),
        nameof(PanelStrings.PhoneWebSessionMissingTitle),
        nameof(PanelStrings.PhoneWebSessionMissing),
        nameof(PanelStrings.PhoneWebSessionEmptyTitle),
        nameof(PanelStrings.PhoneWebSessionEmpty),
        nameof(PanelStrings.PhoneWebLiveConnecting),
        nameof(PanelStrings.PhoneWebLiveLost),
        // S4a / D1: слово владельца «Переподключиться», панель «Ждут ответа» и её счёт.
        // Без этих четырёх имён в списке (и в трёх словарях) экран показал бы ИМЯ КЛЮЧА.
        nameof(PanelStrings.PhoneWebReconnect),
        nameof(PanelStrings.PhoneWebWaitingTitle),
        nameof(PanelStrings.PhoneWebWaitingEmpty),
        nameof(PanelStrings.PhoneWebWaitingCountFormat),
        // Хвост S4a (P2 §2): орган «к последнему сообщению» — имя при счёте 0 и имя со счётом.
        // Оба имени нужны экрану: видимого слова на кнопке нет, а цифра в значке — aria-hidden.
        nameof(PanelStrings.PhoneWebToLast),
        nameof(PanelStrings.PhoneWebToLastCountFormat),
        // Fix-заход 11.11.2026 (D1 §1, §9): ОДИН новый ключ — имя верхней кнопки пары
        // «вверх/вниз». Ключ без языка валит проверку, а без этого списка экран показал бы имя.
        nameof(PanelStrings.PhoneWebToTop),
        nameof(PanelStrings.PhoneWebShowEarlier),
        nameof(PanelStrings.PhoneWebOlderFailed),
        nameof(PanelStrings.PhoneWebTurnFormat),
        nameof(PanelStrings.PhoneWebTurnError),
        nameof(PanelStrings.PhoneWebToolRunningFormat),
        nameof(PanelStrings.PhoneWebToolDoneFormat),
        nameof(PanelStrings.PhoneWebToolFailedFormat),
        nameof(PanelStrings.PhoneWebAttachmentNote),
        nameof(PanelStrings.PhoneWebQuestionElsewhere),
        nameof(PanelStrings.PhoneWebOpen),
        nameof(PanelStrings.PhoneWebFromSubagent),
        nameof(PanelStrings.PhoneWebAnswerSending),
        nameof(PanelStrings.PhoneWebNoScriptTitle),
        nameof(PanelStrings.PhoneWebNoScript),
        nameof(PanelStrings.PhoneWebBootFallback),
        nameof(PanelStrings.PhoneWebLoading),
        nameof(PanelStrings.PhoneWebCrash),
        nameof(PanelStrings.PhoneWebListUnreadable),
        nameof(PanelStrings.PhoneWebTooManyScreens),
        nameof(PanelStrings.PhoneWebSubagentRefused),
        nameof(PanelStrings.PhonePageCancelYes),
        nameof(PanelStrings.PhonePageCancelNo),
        nameof(PanelStrings.PhonePageTitle),
        nameof(PanelStrings.PhonePageIntro),
        nameof(PanelStrings.PhonePageCodeLabel),
        nameof(PanelStrings.PhonePageSubmit),
        nameof(PanelStrings.PhonePageError),
        nameof(PanelStrings.PhonePageDeviceFormat),
        nameof(PanelStrings.PhonePageDeviceFallback),
        nameof(PanelStrings.PhonePageQuestionTitle),
        nameof(PanelStrings.PhonePageProblemTitle),
        nameof(PanelStrings.PhonePageCheckButton),
        nameof(PanelStrings.PhonePageRepeatButton),
        nameof(PanelStrings.PhonePageCancelButton),
        nameof(PanelStrings.PhonePageCancelWarning),
        nameof(PanelStrings.PhonePageAnswerButton),
        // W10 (D11 §5.2, DEF-146): подпись главной кнопки, пока есть неотвеченные карточки, —
        // «Далее». Едет в `boot.words` тем же путём, что и «Ответить».
        nameof(PanelStrings.PhonePageNextButton),
        nameof(PanelStrings.PhonePageChooseAll),
        // «Вопрос {0} из {1}» — ключ уже печатала серверная страница; с S3e его показывает пейджер
        // карточки вопроса в React (`src\web\src\i18n.ts`), поэтому он обязан ехать в `boot.words`.
        nameof(PanelStrings.PhonePageQuestionCountFormat),
        nameof(PanelStrings.PhonePageOwnLabel),
        nameof(PanelStrings.PhonePageOwnPlaceholder),
        nameof(PanelStrings.PhonePageAnswered),
        nameof(PanelStrings.PhonePageTaken),
        nameof(PanelStrings.PhonePageCancelled),
        nameof(PanelStrings.PhonePageUnreadable),
        nameof(PanelStrings.PhonePageWaitingTitle),
        nameof(PanelStrings.PhonePageAnsweredNext),
        nameof(PanelStrings.PhonePageTakenNext),
        nameof(PanelStrings.PhonePageCancelledNext),
        nameof(PanelStrings.PhonePageAnswerFailedFormat),
        nameof(PanelStrings.PhonePageCancelFailedFormat),
        nameof(PanelStrings.PhoneGateQuestionNoAnswerFormat),
        nameof(PanelStrings.PhoneGateNoQuestion),
        nameof(PanelStrings.PhoneGateStaleQuestion),
        nameof(PanelStrings.PhoneGateAnswerNotTaken),
        nameof(PanelStrings.PhoneGateNoChannel),
        nameof(PanelStrings.PhonePageOwnLabelRequired),
        nameof(PanelStrings.PhoneWebTaskTitle),
        nameof(PanelStrings.PhoneWebTaskLabel),
        nameof(PanelStrings.PhoneWebTaskHint),
        nameof(PanelStrings.PhoneWebTaskCounterFormat),
        nameof(PanelStrings.PhoneWebTaskSend),
        nameof(PanelStrings.PhoneWebTaskNew),
        nameof(PanelStrings.PhoneWebTaskNewNote),
        nameof(PanelStrings.PhoneWebDraftRestored),
        nameof(PanelStrings.PhoneWebDraftDiscard),
        nameof(PanelStrings.PhoneWebDraftUnavailable),
        nameof(PanelStrings.PhoneWebQueueTitle),
        nameof(PanelStrings.PhoneWebQueueEmpty),
        nameof(PanelStrings.PhoneWebQueueMoreFormat),
        nameof(PanelStrings.PhoneWebQueueRemove),
        nameof(PanelStrings.PhoneWebQueueRemoveAsk),
        nameof(PanelStrings.PhoneWebQueueRemoveCost),
        nameof(PanelStrings.PhoneWebQueueKeep),
        nameof(PanelStrings.PhoneWebQueueRemoveYes),
        nameof(PanelStrings.PhoneWebPlacementQueued),
        nameof(PanelStrings.PhoneWebPlacementSteering),
        nameof(PanelStrings.PhoneWebStop),
        nameof(PanelStrings.PhoneWebStopWarning),
        // S3: эти фразы печатает калитка, а пульт повторяет их, когда ответ пришёл без `message`
        // (обрыв, таймаут) — слова берутся из boot, поэтому ключи обязаны быть в списке.
        nameof(PanelStrings.PhoneQueueAlreadyGone),
        nameof(PanelStrings.PhoneQueueBusy),
        nameof(PanelStrings.PhoneQueueNoLive),
        nameof(PanelStrings.PhoneQueueRemoved),
        nameof(PanelStrings.PhoneQueueRemoveFailedFormat),
        nameof(PanelStrings.PhoneQueueUnreadable),
        nameof(PanelStrings.PhoneStopDone),
        nameof(PanelStrings.PhoneStopFailedFormat),
        nameof(PanelStrings.PhoneStopIdle),
        nameof(PanelStrings.PhoneTaskAccepted),
        nameof(PanelStrings.PhoneTaskBusy),
        nameof(PanelStrings.PhoneTaskEmpty),
        nameof(PanelStrings.PhoneTaskNewAccepted),
        nameof(PanelStrings.PhoneTaskNewFailedFormat),
        nameof(PanelStrings.PhoneTaskNotTakenFormat),
        nameof(PanelStrings.PhoneTaskReasonNoConnection),
        nameof(PanelStrings.PhoneTaskTooLongFormat),

    };

    private static readonly Lazy<string?> ShellTemplate = new(ReadText(ShellResource));

    private static readonly Lazy<AssetMap> Assets = new(ReadMap);

    /// <summary>КАРТА АССЕТОВ (`js`, `css` из `assets.json`) — ею проверяется, что сборка веба доехала.</summary>
    public static AssetMap Map => Assets.Value;

    /// <summary>Оболочка пульта: язык телефона, вход и `data-boot` со словами.</summary>
    public sealed record AssetMap(string Js, string Css);

    /// <summary>ЧТО ВИДИТ ТЕЛЕФОН: HTML оболочки. `null` — ресурс оболочки не вложился (сборка неполна).</summary>
    public static string? Shell(string language, bool authed, string device)
    {
        var template = ShellTemplate.Value;

        if (template is null) return null;

        var map = Assets.Value;

        return template
            .Replace("__LANG__", Html(language), StringComparison.Ordinal)
            .Replace("__TITLE__", Html(Loc.TIn(language, nameof(PanelStrings.PhonePageTitle))), StringComparison.Ordinal)
            .Replace("__BOOT__", Html(Boot(language, authed, device)), StringComparison.Ordinal)
            .Replace("__LOADING__", Html(Loc.TIn(language, nameof(PanelStrings.PhoneWebLoading))), StringComparison.Ordinal)
            .Replace(
                "__BOOT_FALLBACK__",
                Html(Loc.TIn(language, nameof(PanelStrings.PhoneWebBootFallback))),
                StringComparison.Ordinal)
            .Replace(
                "__NOSCRIPT_TITLE__",
                Html(Loc.TIn(language, nameof(PanelStrings.PhoneWebNoScriptTitle))),
                StringComparison.Ordinal)
            .Replace(
                "__NOSCRIPT_TEXT__",
                Html(Loc.TIn(language, nameof(PanelStrings.PhoneWebNoScript))),
                StringComparison.Ordinal)
            .Replace("__JS__", Html(map.Js), StringComparison.Ordinal)
            .Replace("__CSS__", Html(map.Css), StringComparison.Ordinal);
    }

    /// <summary>
    /// `data-boot` — ОДНА строка JSON: язык, вход, имя устройства и СЛОВА (`Loc.TIn` на нужном языке).
    /// Значение уходит в атрибут, поэтому экранируется как атрибут, а не как текст.
    /// </summary>
    public static string Boot(string language, bool authed, string device)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Readable))
        {
            writer.WriteStartObject();
            writer.WriteString("lang", language);
            writer.WriteBoolean("authed", authed);
            writer.WriteString("device", device ?? string.Empty);
            writer.WriteStartObject("words");

            foreach (var key in Keys) writer.WriteString(key, Loc.TIn(language, key));

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// ФАЙЛ АССЕТОВ ПО ИМЕНИ: `main-abc.js`. <c>null</c> — такого файла нет (и это НЕ повод
    /// искать «похожий»: имя сравнивается с именами ресурсов один в один, обхода каталога нет).
    /// </summary>
    public static string? Asset(string name, out string contentType)
    {
        contentType = "application/octet-stream";

        if (string.IsNullOrWhiteSpace(name)) return null;

        // Только безопасные имена: ни пути, ни вида «..», ни обратной косой, ни NUL.
        foreach (var symbol in name)
        {
            var ok = symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';

            if (!ok) return null;
        }

        var resource = AssetResourcePrefix + name;

        if (!typeof(PhoneWeb).Assembly.GetManifestResourceNames()
                .Any(found => string.Equals(found, resource, StringComparison.Ordinal)))
            return null;

        contentType = ContentType(name);

        return ReadText(resource);
    }

    /// <summary>ИМЕНА ВЛОЖЕННЫХ АССЕТОВ — для проверки «сборка веба доехала до exe».</summary>
    public static IReadOnlyList<string> AssetNames { get; } = typeof(PhoneWeb).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(AssetResourcePrefix, StringComparison.Ordinal))
        .Select(name => name[AssetResourcePrefix.Length..])
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>ТИП ФАЙЛА по расширению. Всё незнакомое — байты, а не «на угад HTML».</summary>
    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream",
    };

    private static string? ReadText(string resource)
    {
        using var stream = typeof(PhoneWeb).Assembly.GetManifestResourceStream(resource);

        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static AssetMap ReadMap()
    {
        var text = ReadText(MapResource);

        // Сборка веба кладёт карту рядом с оболочкой; если её нет — пульт не соберётся, и это
        // должно быть видно сразу (адреса пустые → оболочка без скрипта), а не «страница на месте».
        if (string.IsNullOrWhiteSpace(text)) return new AssetMap(string.Empty, string.Empty);

        try
        {
            using var document = JsonDocument.Parse(text);

            return new AssetMap(
                Text(document.RootElement, "js"),
                Text(document.RootElement, "css"));
        }
        catch (JsonException)
        {
            return new AssetMap(string.Empty, string.Empty);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>ЭКРАНИРОВАТЬ ЗНАЧЕНИЕ ДЛЯ HTML: и текст, и атрибут — `&lt;`, `&amp;`, кавычки.</summary>
    private static string Html(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static readonly JsonWriterOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
