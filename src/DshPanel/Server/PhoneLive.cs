using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// МАСКИРОВКА ПУТЕЙ И ИМЕНИ МАШИНЫ В ТОМ, ЧТО УЕЗЖАЕТ ТЕЛЕФОНУ (S1).
///
/// **От какого случая.** Движок кладёт домашний каталог и рабочие пути в собственные поля
/// (`cwd`, `projections.workingDirectory`, `tool/call.arguments`, `system/message`) — замер
/// `docs\PHONE-LIVE.md` §7. Калитка эти поля телефону не отдаёт вовсе (сборка по белому списку,
/// <see cref="PhoneList"/>, <see cref="PhoneLiveFrames"/>), но текст разговора и текст инструментов
/// человек может написать сам — а путь при этом остаётся путём. Поэтому последний рубеж — здесь:
/// что бы ни попало в строку, домашний каталог становится <c>~</c>, а имя машины — <c>***</c>.
///
/// ⚠️ **Это маскировка ДЛЯ ПОКАЗА, а не подмена работы:** маскируется только то, что уже уходит
/// телефону; на диск и в движок ничего не переписывается.
/// </summary>
public static class PhoneMasks
{
    /// <summary>Чем заменяется домашний каталог: так же, как в окнах и журнале (<see cref="DisplayMask"/>).</summary>
    public const string Home = "~";

    /// <summary>Чем заменяется имя машины. Три звезды — честная пометка «здесь было скрыто».</summary>
    public const string Machine = "***";

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string MachineName = Environment.MachineName ?? string.Empty;

    /// <summary>
    /// ЗАМАСКИРОВАТЬ СТРОКУ. Понимает ОБЕ формы пути — обычную (<c>C:\Users\имя</c>) и
    /// JSON-ЭКРАНИРОВАННУЮ (<c>C:\\Users\\имя</c>): тело ответа движка правится СЫРЫМ текстом, а в нём
    /// обратные косые удвоены. Без второй формы маскировка «сработала бы» и не скрыла ничего —
    /// ровно тот вид тихой ошибки, который ищут проверкой с маркером.
    /// </summary>
    public static string Mask(string? text)
    {
        var value = text ?? string.Empty;

        if (value.Length == 0) return string.Empty;

        value = Replace(value, Profile);

        if (MachineName.Length >= 3) value = value.Replace(MachineName, Machine, StringComparison.OrdinalIgnoreCase);

        return value;
    }

    /// <summary>Обрезать текст до предела и сказать об этом многоточием (предел — <see cref="MaxText"/>).</summary>
    public static string Cut(string? text, int max)
    {
        var value = text ?? string.Empty;

        if (max <= 0) return string.Empty;
        if (value.Length <= max) return value;

        return value[..(max - 1)] + "…";
    }

    /// <summary>Предел одного текста в кадре: 20 000 знаков (событие SSE ≤ 64 КиБ).</summary>
    public const int MaxText = 20000;

    private static string Replace(string value, string needle)
    {
        if (needle.Length < 2) return value;

        value = value.Replace(needle, Home, StringComparison.OrdinalIgnoreCase);

        var escaped = needle.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (!string.Equals(escaped, needle, StringComparison.Ordinal))
            value = value.Replace(escaped, Home, StringComparison.OrdinalIgnoreCase);

        var slashed = needle.Replace('\\', '/');
        if (!string.Equals(slashed, needle, StringComparison.Ordinal))
            value = value.Replace(slashed, Home, StringComparison.OrdinalIgnoreCase);

        return value;
    }
}

/// <summary>
/// СПИСОК СЕССИЙ ДЛЯ ТЕЛЕФОНА (S1, контракт §2.1) — СБОРКА СВОИХ ОБЪЕКТОВ ПО БЕЛОМУ СПИСКУ.
///
/// **Почему не «переписать ответ движка».** Прежняя дверь (`/api/session/list`) переносила строки
/// движка СЫРЫМ текстом, и телефону уезжали `cwd` и `projections.workingDirectory` — то есть путь
/// владельца (поправка §0.4 контракта). Новые поля движка появлялись бы у телефона сами по себе.
/// Здесь наоборот: телефон видит РОВНО те поля, которые названы ниже, и новая строка движка
/// не появится у него по умолчанию.
///
/// **Что разрешено:** `sessionId`, `title`, `updatedAt`, `running`, `blank`, `question`,
/// `stats{turns,steps}`, `outline[последние 3]`, `inbox{nextTurn,nextStep}` — и ничего больше.
/// Подчинённые сессии отбрасываются (<see cref="PhoneSessions.Subordinate"/>), как и раньше.
/// ⚠️ `running` корневой строки = свой флаг ИЛИ работающая подчинённая (слово владельца 11.11.2026:
/// «Работает, пока работают подчинённые») — подчинённые строки при этом в ответ НЕ попадают.
/// </summary>
public static class PhoneList
{
    /// <summary>Сколько строк телефону за один раз (контракт §2.1): больше — только «показаны последние N».</summary>
    public const int MaxRows = 50;

    /// <summary>Предел ответа списка — 256 КиБ (контракт §2.1). Больше — урезаем, но НЕ отдаём 500.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Предел названия сессии: 200 знаков (контракт §2.1).</summary>
    public const int MaxTitle = 200;

    /// <summary>Сколько последних записей очерка показывать (контракт §2.1).</summary>
    public const int OutlineRows = 3;

    /// <summary>Предел названия после урезания под размер ответа.</summary>
    public const int ShortTitle = 80;

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// ЧТО ПОЛУЧИЛОСЬ. <see cref="Error"/> — движок отказал (код для журнала, не для телефона);
    /// <see cref="Shape"/> — ответ не той формы, какую мы мерили (тогда списка нет, а не «пусто»);
    /// <see cref="Hidden"/> — сколько подчинённых убрано; <see cref="Archived"/> — сколько архивных
    /// (слово владельца 11.11.2026); <see cref="Dropped"/> — сколько строк
    /// выброшено как не-объекты; <see cref="Trimmed"/> — сколько очерков урезано под размер.
    /// </summary>
    public sealed record Built(
        string Json,
        int Total,
        int Shown,
        int Hidden,
        int Archived,
        int Dropped,
        int Trimmed,
        string Error,
        string Shape)
    {
        /// <summary>Ответ не той формы: телефону говорится «список не прочитался», а не «пусто».</summary>
        public bool ShapeBroken => Shape.Length > 0;

        /// <summary>Список собран и его можно отдавать.</summary>
        public bool Ok => Error.Length == 0 && Shape.Length == 0;
    }

    /// <summary>Одна строка в том виде, в каком её собираем (свои поля, не движка).</summary>
    private sealed record Row(
        string SessionId,
        string Title,
        long UpdatedAt,
        bool Running,
        bool Blank,
        bool Question,
        string? Stats,
        IReadOnlyList<string> Outline,
        string? Inbox);

    /// <summary>
    /// СОБРАТЬ СПИСОК ИЗ ОТВЕТА <c>session/list</c>. <paramref name="hasQuestion"/> — «ждёт ли ответа
    /// эта сессия» (у калитки один висящий вопрос; вопрос подчинённого к корневой — дело S2).
    /// </summary>
    public static Built Build(
        string? body,
        Func<string, bool> hasQuestion,
        Func<string, bool>? archived = null,
        bool stale = false,
        int maxRows = MaxRows,
        int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(hasQuestion);

        if (maxRows <= 0) maxRows = MaxRows;
        if (maxBytes <= 0) maxBytes = MaxBytes;

        if (string.IsNullOrWhiteSpace(body)) return Broken("empty", "пустой ответ");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body!);
        }
        catch (JsonException)
        {
            return Broken("not-json", "ответ не JSON");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return Broken("root", "корень не объект");

            if (!root.TryGetProperty(PhoneSessions.ResultField, out var result) || result.ValueKind != JsonValueKind.Object)
                return Broken("result", "нет result");

            // ОТКАЗ ДВИЖКА — это не «пустой список»: телефону говорится «не прочитался», а код
            // (gateway/…) уходит в журнал, но не в слова человеку.
            if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return new Built(
                    string.Empty, 0, 0, 0, 0, 0, 0,
                    Text(error, "code") is { Length: > 0 } code ? code : "error", string.Empty);

            var total = 0;
            var hidden = 0;
            var archivedHidden = 0;
            var dropped = 0;
            var rows = new List<Row>();
            var trimmed = 0;

            // ПУСТОЙ СПИСОК ПРИХОДИТ БЕЗ КОНТЕЙНЕРА ВОВСЕ (замер, обе линии): `result = {ok}`.
            // Это законный пустой ответ, а не поломка.
            if (result.TryGetProperty(PhoneSessions.ValueField, out var value)
                && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(PhoneSessions.ItemsField, out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                // ПЕРВЫЙ ПРОХОД — «КТО РАБОТАЕТ ИЗ ПОДЧИНЁННЫХ» (слово владельца 11.11.2026: «Мне
                // нравится работает пока работают подчиненные»). Движок сортирует строки по updatedAt,
                // и подчинённая может прийти ПОСЛЕ своего корня — поэтому родители собираются ДО сборки
                // строк, а не «когда встретили»: иначе ответ зависел бы от порядка строк движка.
                // Подчинённая без parentSessionId (только `origin: subagent`) — относить не к кому:
                // скрытой она остаётся, как и раньше, и на корень не влияет.
                var workingParents = new HashSet<string>(StringComparer.Ordinal);

                foreach (var session in items.EnumerateArray())
                {
                    if (session.ValueKind != JsonValueKind.Object) continue;
                    if (!PhoneSessions.Subordinate(session)) continue;
                    if (!Flag(session, "running")) continue;

                    var parent = Text(session, PhoneSessions.ParentField);

                    if (parent.Length > 0) workingParents.Add(parent);
                }

                foreach (var session in items.EnumerateArray())
                {
                    // НЕ-ОБЪЕКТ — это мусор в ответе, а не сессия: его место в счёте отброшенных,
                    // и спрашивать у него про родителя нельзя (это было бы исключение в потоке).
                    if (session.ValueKind != JsonValueKind.Object)
                    {
                        dropped++;
                        continue;
                    }

                    if (PhoneSessions.Subordinate(session))
                    {
                        hidden++;
                        continue;
                    }

                    // АРХИВНАЯ СЕССИЯ — СКРЫТА, НО СЧИТАНА ОТДЕЛЬНО (слово владельца 11.11.2026).
                    // Набор приходит из workspace/follow; `null` — набора нет, и тогда поведение
                    // не меняется ни на байт (R1 §3.2). Считается ПОСЛЕ подчинённых: одна
                    // строка не может быть скрыта дважды.
                    if (archived is not null && Text(session, "sessionId") is { Length: > 0 } archivedId
                        && archived(archivedId))
                    {
                        archivedHidden++;
                        continue;
                    }

                    var row = RowOf(session, hasQuestion, workingParents);

                    if (row is null)
                    {
                        dropped++;
                        continue;
                    }

                    total++;

                    if (rows.Count < maxRows) rows.Add(row);
                }
            }
            else if (result.TryGetProperty(PhoneSessions.ValueField, out var other) && other.ValueKind == JsonValueKind.Object)
            {
                return Broken("value", "нет items");
            }
            else
            {
                // «ПУСТО» — ЭТО РОВНО `result = {ok}` (замер: 62 знака у чистой установки).
                // Прочие ключи вместо списка («sessions», «rows», …) — ответ НЕ ТОЙ ФОРМЫ:
                // там могут быть строки движка с `cwd`, и отдать их телефону нельзя.
                foreach (var field in result.EnumerateObject())
                {
                    if (!string.Equals(field.Name, "ok", StringComparison.Ordinal))
                        return Broken("result", "незнакомое «" + field.Name + "»");
                }
            }

            var json = Render(rows, total, stale, out var bytes);

            // ПРЕДЕЛ ОТВЕТА: сперва снимаем очерки у САМЫХ СТАРЫХ строк (движок отсортировал
            // по updatedAt убыв., значит старые — в конце), потом у всех, и только потом режем
            // названия. 500 не отдаём: список — не повод остаться без списка.
            if (bytes > maxBytes)
            {
                for (var index = rows.Count - 1; index >= 0 && bytes > maxBytes; index--)
                {
                    if (rows[index].Outline.Count == 0) continue;

                    rows[index] = rows[index] with { Outline = Array.Empty<string>() };
                    trimmed++;
                    json = Render(rows, total, stale, out bytes);
                }
            }

            if (bytes > maxBytes)
            {
                for (var index = 0; index < rows.Count && bytes > maxBytes; index++)
                {
                    if (rows[index].Title.Length <= ShortTitle) continue;

                    rows[index] = rows[index] with { Title = PhoneMasks.Cut(rows[index].Title, ShortTitle) };
                    trimmed++;
                    json = Render(rows, total, stale, out bytes);
                }
            }

            return new Built(json, total, rows.Count, hidden, archivedHidden, dropped, trimmed, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// СТРОКА СПИСКА — СВОЯ, А НЕ ДВИЖКОВА. `null` — строка не объект или без адреса: её место
    /// в счёте отброшенных, а не в ответе (адрес телефона — обязательное поле).
    /// </summary>
    private static Row? RowOf(
        JsonElement session,
        Func<string, bool> hasQuestion,
        IReadOnlySet<string> workingParents)
    {
        if (session.ValueKind != JsonValueKind.Object) return null;

        var id = Text(session, "sessionId");

        if (id.Length == 0) return null;

        // НАЗВАНИЕ ЖИВЁТ В ПРОЕКЦИЯХ (`projections.values.title`), а не в самой строке: у движка
        // 0.2.1 поле `title` — проекция. Верхнее `title` оставлено запасным (обе линии).
        var title = PhoneMasks.Cut(PhoneMasks.Mask(Text(session, "title")), MaxTitle);
        var updated = session.TryGetProperty("updatedAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number
            ? stamp.GetInt64()
            : 0L;

        // КОРЕНЬ РАБОТАЕТ, ЕСЛИ РАБОТАЕТ ОН САМ ИЛИ ЕГО ПОДЧИНЁННЫЙ (слово владельца 11.11.2026).
        // Подчинённые строки в ответ не входят, но их работа — это работа корня для человека: иначе
        // команда, ждущая своих субагентов, показывалась бы зелёной галочкой «Закончила».
        var running = Flag(session, "running") || workingParents.Contains(id);
        var blank = Flag(session, "blank");

        // ПРОЕКЦИИ — ТОЛЬКО ЧЕТЫРЕ, и каждая собирается заново (см. §1.6 контракта):
        // `title` уже взят, `sessionStats` — два числа, `turnOutline` — последние три,
        // `inbox` — ДЛИНЫ очередей (содержимое не отдаётся: в нём бывают вложения).
        string? stats = null;
        var outline = new List<string>();
        string? inbox = null;

        if (session.TryGetProperty("projections", out var projections)
            && projections.ValueKind == JsonValueKind.Object
            && projections.TryGetProperty("values", out var values)
            && values.ValueKind == JsonValueKind.Object)
        {
            if (title.Length == 0) title = PhoneMasks.Cut(PhoneMasks.Mask(Text(values, "title")), MaxTitle);

            stats = Stats(values);
            inbox = Inbox(values);

            if (values.TryGetProperty("turnOutline", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var from = Math.Max(0, list.GetArrayLength() - OutlineRows);

                for (var index = from; index < list.GetArrayLength(); index++)
                {
                    var item = list[index];

                    if (item.ValueKind != JsonValueKind.Object) continue;

                    outline.Add(OutlineJson(item));
                }
            }
        }

        return new Row(id, title, updated, running, blank, hasQuestion(id), stats, outline, inbox);
    }

    private static string? Stats(JsonElement values)
    {
        if (!values.TryGetProperty("sessionStats", out var stats) || stats.ValueKind != JsonValueKind.Object) return null;

        var turns = Number(stats, "turns");
        var steps = Number(stats, "steps");

        if (turns < 0 && steps < 0) return null;

        return "{\"turns\":" + Math.Max(0, turns) + ",\"steps\":" + Math.Max(0, steps) + "}";
    }

    private static string? Inbox(JsonElement values)
    {
        if (!values.TryGetProperty("inbox", out var inbox) || inbox.ValueKind != JsonValueKind.Object) return null;

        return "{\"nextTurn\":" + Length(inbox, "next-turn") + ",\"nextStep\":" + Length(inbox, "next-step") + "}";
    }

    private static int Length(JsonElement inbox, string name) =>
        inbox.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;

    private static string OutlineJson(JsonElement item)
    {
        var builder = new StringBuilder("{\"turn\":");

        builder.Append(Number(item, "turn")).Append(",\"seq\":").Append(Number(item, "seq"));
        builder.Append(",\"prompt\":").Append(Quote(PhoneMasks.Mask(Text(item, "prompt"))));
        builder.Append(",\"response\":").Append(Quote(PhoneMasks.Mask(Text(item, "response"))));
        builder.Append('}');

        return builder.ToString();
    }

    /// <summary>СОБРАТЬ ОТВЕТ ЦЕЛИКОМ: <c>{"ok":true,"total":N,"shown":M,"items":[…]}</c>.</summary>
    private static string Render(IReadOnlyList<Row> rows, int total, bool stale, out int bytes)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Writer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("total", total);
            writer.WriteNumber("shown", rows.Count);

            // «СПИСОК МОГ ИЗМЕНИТЬСЯ» — ОДИН БИТ (S4a, R1 §5.2): поставлен кадром
            // api-session/status из уже открытого $events и снят при выдаче. Ни адреса сессии,
            // ни «идёт ли ход» здесь нет — только «перечитай».
            if (stale) writer.WriteBoolean("stale", true);

            writer.WriteStartArray("items");

            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", row.SessionId);
                writer.WriteString("title", row.Title);
                writer.WriteNumber("updatedAt", row.UpdatedAt);
                writer.WriteBoolean("running", row.Running);
                writer.WriteBoolean("blank", row.Blank);
                writer.WriteBoolean("question", row.Question);

                // ⚠️ СЫРОЕ ЗНАЧЕНИЕ — ВСЁ РАВНО ЗНАЧЕНИЕ: имя свойства пишется ДО него, иначе
                // `Utf8JsonWriter` падает «cannot write a value within an object without a property name»
                // (найдено проверкой белого списка: у строки с `sessionStats` и `inbox` ответ рвался).
                if (row.Stats is not null)
                {
                    writer.WritePropertyName("stats");
                    writer.WriteRawValue(row.Stats, skipInputValidation: true);
                }

                if (row.Inbox is not null)
                {
                    writer.WritePropertyName("inbox");
                    writer.WriteRawValue(row.Inbox, skipInputValidation: true);
                }

                if (row.Outline.Count > 0)
                {
                    writer.WriteStartArray("outline");

                    foreach (var item in row.Outline) writer.WriteRawValue(item, skipInputValidation: true);

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        bytes = (int)stream.Length;

        return json;
    }

    private static Built Broken(string shape, string reason) =>
        new(string.Empty, 0, 0, 0, 0, 0, 0, string.Empty, shape + "(" + reason + ")");

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : -1;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string Quote(string? text) => JsonSerializer.Serialize(text ?? string.Empty, Readable);

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>Что кадр живого потока значит для телефона (S1, контракт §2.2).</summary>
public enum PhoneLiveKind
{
    /// <summary>Кадр не из белого списка: телефону не отправляется, счёт — в журнал.</summary>
    Ignored,

    /// <summary>Открывающее окно: заголовок, курсор, нормализованные записи, черновик ответа.</summary>
    Snapshot,

    /// <summary>Одна журнальная запись в нормализованном виде.</summary>
    Record,

    /// <summary>Процессный кадр недописанного ответа (текст по мере поступления).</summary>
    Assistant,

    /// <summary>Поток движка сломался или закрылся: телефону — событие <c>error</c>.</summary>
    Failed,

    /// <summary>Движок закончил поток штатно: телефону — событие <c>error</c> и переподключение.</summary>
    Ended,
}

/// <summary>
/// ОДИН КАДР ЖИВОГО ПОТОКА, УЖЕ ГОТОВЫЙ ТЕЛЕФОНУ: имя события SSE и его <c>data</c> одной строкой
/// JSON. Разбор — ЧИСТАЯ функция (<see cref="PhoneLiveFrames.Translate"/>), поэтому проверяется
/// подставными кадрами, а не живым движком — ровно как разбор <c>$events</c>.
/// </summary>
public sealed record PhoneLiveFrame(
    PhoneLiveKind Kind,
    string Name,
    string Json,
    int Dropped = 0,
    string Detail = "",
    int Trimmed = 0);

/// <summary>
/// ПЕРЕВОД КАДРОВ <c>session/follow</c> В КАДРЫ ТЕЛЕФОНА (S1, контракт §2.2).
///
/// **Форма кадра — замер по исходникам 0.2.1-rc.2:** поток отдаёт
/// <c>{type:'item',streamId,value:{type:'snapshot'|'event'|'assistant-stream'}}</c>, а внутри
/// <c>value.event</c> лежит <c>{type,seq,time,data}</c> — журнальный факт движка.
/// <c>data</c> — ровно значение из карты событий: у <c>user/message</c> это само сообщение
/// (<c>data.source.kind</c>), у <c>assistant/message</c> — обёртка с <c>message</c>.
///
/// **Чего здесь НЕТ и не появится:** `cwd`, `workingDirectory`, `parentSession`, `origin`,
/// `isSeeded`, `delegationDepth`, `agentPreset` из заголовка; `system/message`,
/// `developer/message`, `request/header`, `request/context`, `compaction/*`, `assistant/attempt`,
/// `step/*` — из журнала; `arguments` и `message/meta` — из инструментов; `reasoning`,
/// `replayState`, `usage`, `tool-call-delta` — из процессных кадров. Всё это отбрасывается
/// и считается (<see cref="PhoneLiveFrame.Dropped"/>), а не «проходит, потому что мы забыли».
/// </summary>
public static class PhoneLiveFrames
{
    /// <summary>Довод `maxMessages` окна (контракт §2.2).</summary>
    public const int MaxMessages = 50;

    /// <summary>Предел ОДНОГО события SSE — 64 КиБ (контракт §2.2, §5.2).</summary>
    public const int MaxEventBytes = 64 * 1024;

    /// <summary>
    /// ГОДИТСЯ ЛИ АДРЕС СЕССИИ. Формат назван контрактом: 1…128 знаков из
    /// <c>[A-Za-z0-9._:-]</c>. Проверка нужна ДО движка: адрес идёт в доводы вызова.
    /// </summary>
    public static bool ValidSession(string? sessionId)
    {
        var value = (sessionId ?? string.Empty).Trim();

        if (value.Length is 0 or > 128) return false;

        foreach (var symbol in value)
        {
            var ok = symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-';

            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// ПЕРЕВЕСТИ КАДР ДВИЖКА. Непонятное — <see cref="PhoneLiveKind.Ignored"/>: поток чужой работы
    /// не имеет права ронять ни калитку, ни телефон.
    /// </summary>
    public static PhoneLiveFrame Translate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new PhoneLiveFrame(PhoneLiveKind.Ignored, string.Empty, string.Empty);

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(text!);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Ignored("не объект");

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Ignored("не разобран");
        }

        var type = Text(root, "type");

        if (string.Equals(type, "error", StringComparison.Ordinal)) return new PhoneLiveFrame(PhoneLiveKind.Failed, string.Empty, string.Empty, Detail: ErrorCode(root));

        if (string.Equals(type, "end", StringComparison.Ordinal)) return new PhoneLiveFrame(PhoneLiveKind.Ended, string.Empty, string.Empty);

        // РАЗВЁРНУТЫЙ КАДР (без обёртки `item`) принимается тоже: дверь потока может отдать
        // и сам `value` — форма транспорта не должна решать, показывать ли человеку ленту.
        if (string.Equals(type, "item", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                return Ignored("кадр без value");

            root = value;
            type = Text(root, "type");
        }

        // ПОТОЛОК ОДНОГО СОБЫТИЯ — НА ЛЮБОМ КАДРЕ (DEF-071). Обрезка делается там, где текст
        // попадает в JSON (<see cref="TextJson"/>), а это — последняя дверь: кадр больше 64 КиБ
        // не уедет ни при какой правке выше.
        var frame = type switch
        {
            "snapshot" => Snapshot(root),
            "event" => Record(root),
            "assistant-stream" => Assistant(root),
            _ => Ignored(type.Length > 0 ? type : "без вида"),
        };

        return Fit(frame);
    }

    /// <summary>
    /// ПОТОЛОК ОДНОГО СОБЫТИЯ — 64 КиБ, И ЭТО НЕ ПОЖЕЛАНИЕ (DEF-071).
    ///
    /// Снимок режется по записям, текстовые кадры — по тексту. Здесь последняя дверь: кадр больше
    /// потолка НЕ ОТПРАВЛЯЕТСЯ ВОВСЕ. Пропустить его значило бы пообещать потолок, которого нет:
    /// телефон, прочитавший двести килобайт в одном событии, платит за это памятью и разбором,
    /// а `EventSource` такого не прощает.
    /// </summary>
    private static PhoneLiveFrame Fit(PhoneLiveFrame frame)
    {
        if (frame.Json.Length == 0) return frame;

        if (Encoding.UTF8.GetByteCount(frame.Json) <= MaxEventBytes) return frame;

        return new PhoneLiveFrame(PhoneLiveKind.Ignored, string.Empty, string.Empty, 1, "сверх потолка");
    }

    /// <summary>Сколько байт события оставлено под поля вокруг текста: обёртка, seq, время, имя.</summary>
    private const int EventRoom = MaxEventBytes - 1024;

    /// <summary>
    /// ТЕКСТ В JSON С ПОТОЛКОМ ПО БАЙТАМ (DEF-071).
    ///
    /// **Почему по байтам, а не по знакам.** Потолок события — 64 КиБ, а знак бывает дороже
    /// одного байта: управляющий знак `\u0001` занимает в JSON ШЕСТЬ байт. Замер случая из
    /// дефекта: 20 000 управляющих знаков дают кадр около 120 КБ — то есть проверка «по знакам»
    /// прошла бы, а поток уехал бы за потолок. Поэтому здесь режется ТЕКСТ, пока его JSON
    /// не влезет в отведённую комнату; многоточие говорит человеку, что текст не весь.
    /// </summary>
    private static string TextJson(string? text, out bool trimmed, int room = EventRoom)
    {
        var value = text ?? string.Empty;
        var json = Quote(value);

        trimmed = false;

        if (Encoding.UTF8.GetByteCount(json) <= room) return json;

        var low = 0;
        var high = value.Length;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (Encoding.UTF8.GetByteCount(Quote(value[..middle] + "…")) <= room) low = middle;
            else high = middle - 1;
        }

        trimmed = true;

        return Quote(value[..low] + "…");
    }

    /// <summary>ОТКРЫВАЮЩЕЕ ОКНО: что видно сразу и с какого места лента (курсор — для догона).</summary>
    private static PhoneLiveFrame Snapshot(JsonElement root)
    {
        var header = root.TryGetProperty("header", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var cursor = root.TryGetProperty("cursor", out var cursorValue) && cursorValue.ValueKind == JsonValueKind.Number
            ? cursorValue.GetInt64()
            : 0L;
        var hasMore = root.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;

        var title = string.Empty;

        if (root.TryGetProperty("projections", out var projections)
            && projections.ValueKind == JsonValueKind.Object
            && projections.TryGetProperty("values", out var values)
            && values.ValueKind == JsonValueKind.Object)
            title = Text(values, "title");

        var records = new List<string>();
        var dropped = 0;
        var trimmedTexts = 0;

        if (root.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in list.EnumerateArray())
            {
                // В СНИМКЕ записи лежат В ОБЁРТКЕ (`{type:'event',event:{…}}`) — так их отдаёт движок
                // (замер §1.2). Без разворачивания снимок пришёл бы пустым:
                // `type` у обёртки — "event", а не вид журнала.
                var wire = record;

                if (record.ValueKind == JsonValueKind.Object
                    && record.TryGetProperty("event", out var inner)
                    && inner.ValueKind == JsonValueKind.Object)
                    wire = inner;

                var json = RecordJson(wire, out var trimmedText);

                if (json is null)
                {
                    dropped++;

                    continue;
                }

                // ОБРЕЗКА ТЕКСТА ВНУТРИ ЗАПИСИ — тоже счёт (DEF-071): обрезка молча — тихая потеря.
                if (trimmedText) trimmedTexts++;

                records.Add(json);
            }
        }

        var draft = Draft(root);

        // ПРЕДЕЛ ОДНОГО СОБЫТИЯ — 64 КиБ (контракт §2.2). Снимок несёт окно целиком и может
        // оказаться больше: тогда выкидываем САМЫЕ СТАРЫЕ записи (догон перечитыванием всё равно
        // даёт их страницей), а не режем текст на живом экране.
        string snapshotJson;

        while (true)
        {
            snapshotJson = RenderSnapshot(records, header, title, cursor, hasMore, draft);

            if (Encoding.UTF8.GetByteCount(snapshotJson) <= MaxEventBytes || records.Count == 0) break;

            records.RemoveAt(0);
            dropped++;
        }

        return new PhoneLiveFrame(PhoneLiveKind.Snapshot, "snapshot", snapshotJson, dropped, Trimmed: trimmedTexts);
    }

    /// <summary>СОБРАТЬ СНИМОК ОДНОЙ СТРОКОЙ JSON: записи уже нормализованы и идут сырым текстом.</summary>
    private static string RenderSnapshot(
        IReadOnlyList<string> records,
        JsonElement header,
        string title,
        long cursor,
        bool hasMore,
        string? draft)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Writer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("header");
            writer.WriteString("id", Text(header, "id"));
            writer.WriteNumber("createdAt", Number(header, "createdAt"));
            // НАЗВАНИЕ — НАШЕ: в движковом `header` его нет, оно лежит в проекциях.
            writer.WriteString("title", PhoneMasks.Cut(PhoneMasks.Mask(title), PhoneList.MaxTitle));
            writer.WriteEndObject();
            writer.WriteNumber("cursor", cursor);
            writer.WriteBoolean("hasMore", hasMore);

            writer.WriteStartArray("records");

            foreach (var record in records) writer.WriteRawValue(record, skipInputValidation: true);

            writer.WriteEndArray();

            if (draft is null)
            {
                writer.WriteNull("assistantDraft");
            }
            else
            {
                writer.WritePropertyName("assistantDraft");
                writer.WriteRawValue(draft, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>ОДНА ЖУРНАЛЬНАЯ ЗАПИСЬ: телефону — нормализованный вид, а не событие движка.</summary>
    private static PhoneLiveFrame Record(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var wire) || wire.ValueKind != JsonValueKind.Object)
            return Ignored("event без события");

        var json = RecordJson(wire, out var trimmed);

        return json is null
            ? new PhoneLiveFrame(PhoneLiveKind.Ignored, string.Empty, string.Empty, 1, Text(wire, "type"))
            : new PhoneLiveFrame(
                PhoneLiveKind.Record, "event", "{\"record\":" + json + "}", Trimmed: trimmed ? 1 : 0);
    }

    /// <summary>
    /// ОДНА ЗАПИСЬ В ВИДЕ ТЕЛЕФОНА. `null` — вид не из белого списка.
    ///
    /// Виды ровно эти (контракт §2.2): ход, слово человека (только `source.kind = user`),
    /// слово агента (только текстовые блоки), вызов инструмента (без доводов), исход инструмента
    /// (без текста и `meta`), конец хода (с причиной — её <c>kind</c>).
    /// </summary>
    private static string? RecordJson(JsonElement wire, out bool trimmed)
    {
        trimmed = false;
        if (wire.ValueKind != JsonValueKind.Object) return null;

        var type = Text(wire, "type");
        var seq = Number(wire, "seq");
        var time = Number(wire, "time");

        if (!wire.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        switch (type)
        {
            case "turn/start":
                return "{\"type\":\"turn\",\"seq\":" + seq + ",\"time\":" + time + ",\"turn\":" + Number(data, "turn") + "}";

            case "user/message":
            {
                // ТОЛЬКО СЛОВО ЧЕЛОВЕКА: `source.kind = user`. Синтетические user-сообщения
                // (инструкции, цели, cron) — служебный текст движка, телефону не показываются.
                if (!data.TryGetProperty("source", out var source)
                    || source.ValueKind != JsonValueKind.Object
                    || !string.Equals(Text(source, "kind"), "user", StringComparison.Ordinal))
                    return null;

                // ПОТОЛОК ПО БАЙТАМ (DEF-071): длинный текст режется ЗДЕСЬ, до сборки кадра, —
                // иначе 20 000 управляющих знаков дали бы событие вдвое больше потолка.
                return "{\"type\":\"user\",\"seq\":" + seq + ",\"time\":" + time + ",\"text\":"
                    + TextJson(PhoneMasks.Cut(PhoneMasks.Mask(Content(data)), PhoneMasks.MaxText), out trimmed) + "}";
            }

                case "assistant/message":
                {
                    if (!data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                        return null;

                    // DEF-093: ШАГ БЕЗ ТЕКСТА — НЕ ЗАПИСЬ. Размышление (Think) приходит без текстовых
                    // блоков, и раньше телефону уезжала запись с `text:""`: на экране рисовался
                    // ПУСТОЙ пузырь. Пустая строка — это не слово агента, а отсутствие слова, и
                    // место ей — в счёте отброшенного (журнал), а не в ленте человека.
                    var words = PhoneMasks.Cut(PhoneMasks.Mask(Content(message)), PhoneMasks.MaxText);

                    if (words.Trim().Length == 0) return null;

                    return "{\"type\":\"assistant\",\"seq\":" + seq + ",\"time\":" + time + ",\"text\":"
                        + TextJson(words, out trimmed) + "}";
                }
            case "tool/call":
                return "{\"type\":\"tool-call\",\"seq\":" + seq + ",\"time\":" + time
                    + ",\"name\":" + Quote(PhoneMasks.Cut(PhoneMasks.Mask(Text(data, "name")), 200))
                    + ",\"callId\":" + Quote(Text(data, "callId")) + "}";

            case "tool/result":
            {
                var failed = false;
                var callId = string.Empty;

                if (data.TryGetProperty("message", out var result) && result.ValueKind == JsonValueKind.Object)
                {
                    failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
                    callId = Text(result, "toolCallId");
                }

                if (callId.Length == 0) callId = Text(data, "callId");

                return "{\"type\":\"tool-result\",\"seq\":" + seq + ",\"time\":" + time
                    + ",\"callId\":" + Quote(callId) + ",\"failed\":" + (failed ? "true" : "false") + "}";
            }

            case "turn/end":
            {
                var reason = string.Empty;

                if (data.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.Object)
                    reason = Text(value, "kind");

                var ok = string.Equals(reason, "completed", StringComparison.Ordinal);

                return "{\"type\":\"turn-end\",\"seq\":" + seq + ",\"time\":" + time + ",\"turn\":" + Number(data, "turn")
                    + ",\"ok\":" + (ok ? "true" : "false") + ",\"reason\":" + Quote(reason) + "}";
            }

            default:
                // system/message, developer/message, request/header, request/context, compaction/*,
                // assistant/attempt, step/start, step/end, tool/… прочее — телефону не нужны.
                return null;
        }
    }

    /// <summary>
    /// ПРОЦЕССНЫЙ КАДР НЕДОПИСАННОГО ОТВЕТА. Телефону едет только ФАЗА и ТЕКСТ: `start`, куски
    /// `text-delta` и `end` с исходом. `reasoning`, `tool-call-delta`, `usage`, `finish.replayState`
    /// не отправляются (контракт §2.2, поправка §0.8).
    /// </summary>
    private static PhoneLiveFrame Assistant(JsonElement root)
    {
        if (!root.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Object)
            return Ignored("assistant-stream без frame");

        var attemptId = Text(frame, "attemptId");
        var phase = Text(frame, "type");

        switch (phase)
        {
            case "start":
                return new PhoneLiveFrame(
                    PhoneLiveKind.Assistant, "assistant",
                    "{\"phase\":\"start\",\"attemptId\":" + Quote(attemptId) + "}");

            case "chunk":
            {
                if (!frame.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object)
                    return Ignored("chunk без chunk");

                if (!string.Equals(Text(chunk, "type"), "text-delta", StringComparison.Ordinal))
                    return Ignored("chunk " + Text(chunk, "type"));

                var textJson = TextJson(PhoneMasks.Cut(PhoneMasks.Mask(Text(chunk, "text")), PhoneMasks.MaxText), out var trimmed);

                return new PhoneLiveFrame(
                    PhoneLiveKind.Assistant, "assistant",
                    "{\"phase\":\"text\",\"attemptId\":" + Quote(attemptId)
                    + ",\"index\":" + Number(frame, "index")
                    + ",\"text\":" + textJson + "}",
                    Trimmed: trimmed ? 1 : 0);
            }

            case "end":
            {
                var outcome = string.Empty;

                if (frame.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.Object)
                    outcome = Text(value, "kind");

                return new PhoneLiveFrame(
                    PhoneLiveKind.Assistant, "assistant",
                    "{\"phase\":\"end\",\"attemptId\":" + Quote(attemptId) + ",\"outcome\":" + Quote(outcome) + "}");
            }

            default:
                return Ignored("assistant " + phase);
        }
    }

    /// <summary>
    /// ЧЕРНОВИК ИЗ СНИМКА: активная попытка, текст собран из упакованных дорожек
    /// (<c>text-chunks.texts</c>) и сырых <c>chunk.type = text-delta</c>. Всё остальное
    /// (<c>reasoning</c>, <c>tool-call</c>, <c>finish</c>) в текст не входит.
    /// </summary>
    private static string? Draft(JsonElement root)
    {
        if (!root.TryGetProperty("assistantStream", out var baseline) || baseline.ValueKind != JsonValueKind.Object) return null;

        if (!baseline.TryGetProperty("activeAttempt", out var attempt) || attempt.ValueKind != JsonValueKind.Object) return null;

        var text = new StringBuilder();

        if (attempt.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in stream.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object) continue;

                var type = Text(record, "type");

                if (string.Equals(type, "text-chunks", StringComparison.Ordinal))
                {
                    if (!record.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array) continue;

                    foreach (var piece in texts.EnumerateArray())
                        if (piece.ValueKind == JsonValueKind.String) text.Append(piece.GetString());
                }
                else if (string.Equals(type, "chunk", StringComparison.Ordinal))
                {
                    if (!record.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object) continue;

                    if (string.Equals(Text(chunk, "type"), "text-delta", StringComparison.Ordinal))
                        text.Append(Text(chunk, "text"));
                }
            }
        }

        var assembled = PhoneMasks.Cut(PhoneMasks.Mask(text.ToString()), PhoneMasks.MaxText);

        return "{\"attemptId\":" + Quote(Text(attempt, "attemptId"))
            + ",\"turn\":" + Number(attempt, "turn")
            + ",\"step\":" + Number(attempt, "step")
            + ",\"startedAfterSeq\":" + Number(attempt, "startedAfterSeq")
            + ",\"text\":" + Quote(assembled) + "}";
    }

    /// <summary>ТЕКСТ СООБЩЕНИЯ — блоки <c>type = text</c>. Картинки и вложения текстом не считаются.</summary>
    private static string Content(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return string.Empty;

        var text = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            if (string.Equals(Text(block, "type"), "text", StringComparison.Ordinal)) text.Append(Text(block, "text"));
        }

        return text.ToString();
    }

    private static string ErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return "ошибка потока";

        var code = Text(error, "code");

        return code.Length > 0 ? code : "ошибка потока";
    }

    private static PhoneLiveFrame Ignored(string detail) => new(PhoneLiveKind.Ignored, string.Empty, string.Empty, 1, detail);

    private static string Quote(string? text) => JsonSerializer.Serialize(text ?? string.Empty, Readable);

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    /// <summary>
    /// ОДНА ЖУРНАЛЬНАЯ ЗАПИСЬ В ВИДЕ ТЕЛЕФОНА — для догона (`session/page`), которому нужен тот же
    /// белый список, что и живой ленте. `null` — вид не из списка (и тогда он не едет телефону).
    /// </summary>
    public static string? Normalize(JsonElement wireEvent) => RecordJson(wireEvent, out _);

    /// <summary>Доводы открытия <c>session/follow</c> — ОДНОЙ строкой и одним местом.</summary>
    public static string FollowArgs(string sessionId) =>
        "{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":" + JsonSerializer.Serialize(sessionId, Readable)
        + "},\"maxMessages\":" + MaxMessages + ",\"assistantStream\":true}}";

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>
/// ФОРМАТ SSE — ОДНИМ МЕСТОМ. Событие едет одной строкой <c>data:</c> (JSON без переводов строк),
/// потому что разбирать несколько <c>data:</c> телефону незачем; тело — чанки HTTP/1.1
/// (<c>Transfer-Encoding: chunked</c>), <c>Content-Length</c> у потока нет.
/// </summary>
public static class PhoneSse
{
    /// <summary>Голова потока: без <c>Content-Length</c>, с теми же запретами, что у остальных ответов.</summary>
    public const string Head =
        "HTTP/1.1 200 OK\r\n"
        + "Content-Type: text/event-stream; charset=utf-8\r\n"
        + "Transfer-Encoding: chunked\r\n"
        + "Connection: close\r\n"
        + "Cache-Control: no-store\r\n"
        + "X-Content-Type-Options: nosniff\r\n"
        + "Content-Security-Policy: frame-ancestors 'none'\r\n"
        + "\r\n";

    /// <summary>Последний чанк: <c>0\r\n\r\n</c> — так chunked-поток кончается, а не обрывом.</summary>
    public static readonly byte[] Done = Encoding.ASCII.GetBytes("0\r\n\r\n");

    /// <summary>Одно событие В ЧАНКЕ: размер, событие, конец чанка.</summary>
    public static byte[] Chunk(string name, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var payload = Encoding.UTF8.GetBytes("event: " + name + "\ndata: " + (json ?? string.Empty) + "\n\n");
        var head = Encoding.ASCII.GetBytes(payload.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        var tail = Encoding.ASCII.GetBytes("\r\n");

        var chunk = new byte[head.Length + payload.Length + tail.Length];

        head.CopyTo(chunk, 0);
        payload.CopyTo(chunk, head.Length);
        tail.CopyTo(chunk, head.Length + payload.Length);

        return chunk;
    }

    /// <summary>Событие-сердцебиение: настоящее событие, а не комментарий (комментарии `EventSource` не отдаёт).</summary>
    public static string Ping(long milliseconds) =>
        "{\"t\":" + milliseconds.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>Событие ошибки: код машинный, слова — человеку (контракт §2.11).</summary>
    public static string Error(string code, string message) =>
        "{\"code\":" + JsonSerializer.Serialize(code ?? string.Empty, Readable)
        + ",\"message\":" + JsonSerializer.Serialize(message ?? string.Empty, Readable) + "}";

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>
/// ЖИВАЯ ПОДСКАЗКА СПИСКА — <c>event: stale</c>, И БОЛЬШЕ НИЧЕГО (G1, слово владельца 11.11.2026).
///
/// **Зачем отдельным классом.** Форма кадра — это ровно то, чем дверь отвечает наружу, поэтому она
/// названа одним местом и проверяется прямой сверкой строки: в кадре нет ни id сессии, ни признака
/// хода, ни пути — только имя события и пустой объект. Своего словаря и своих слов у подсказки нет:
/// телефону нечего читать, он перечитывает список.
/// </summary>
public static class PhoneListLive
{
    /// <summary>Имя кадра: «список мог измениться». Одно имя на дверь и на телефон.</summary>
    public const string StaleEvent = "stale";

    /// <summary>
    /// ТЕЛО КАДРА. Пустой объект — не заглушка, а граница: `data:` у SSE обязана быть строкой, и
    /// пустой объект говорит ровно «ничего», тогда как любое поле стало бы данными о сессии.
    /// </summary>
    public const string StaleJson = "{}";
}

/// <summary>
/// ЧТО ЖИВАЯ ПОДСКАЗКА СПИСКА БЕРЁТ ИЗ <c>$events</c>: ОДИН БИТ И «КАНАЛ СЛОМАЛСЯ».
///
/// **Почему канал на один элемент.** Частые <c>api-session/status</c> — это один и тот же бит; копить
/// их незачем, и старый бит ничего не теряет: перечитывание списка одно и то же. Поэтому канал
/// ограничен одним элементом с вытеснением старого, а срок склейки называет калитка
/// (<see cref="PhoneGate.ListLiveCoalesce"/>).
///
/// **Чего здесь нет.** Ни id сессии, ни <c>running</c>, ни тела кадра: разбор — общий
/// (<see cref="PhoneEventFrames.Parse"/>), а из разобранного берётся только ВИД кадра. Значит
/// содержимое сессий не может уехать наружу ни по внимательности, ни по ошибке — его здесь нет.
/// </summary>
internal sealed class PhoneListLiveFeed
{
    private readonly Channel<bool> _hits = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private volatile bool _broken;

    /// <summary>Очередь битов: читает её нитка SSE, пишут нитки мультиплексора.</summary>
    public ChannelReader<bool> Hits => _hits.Reader;

    /// <summary>Канал сломался (кадр `error`/`end`): телефону пора закрывать поток и переподключаться.</summary>
    public bool Broken => _broken;

    /// <summary>Кадр из <c>$events</c>. Всё, что не смена состояния и не обрыв, — молча мимо.</summary>
    public void Frame(string? text)
    {
        var frame = PhoneEventFrames.Parse(text);

        switch (frame.Kind)
        {
            case PhoneEventKind.Status:
                _hits.Writer.TryWrite(true);
                break;

            case PhoneEventKind.Failure:
                _broken = true;
                _hits.Writer.TryWrite(false);
                break;
        }
    }
}

/// <summary>
/// ССЫЛКА НА ОТКРЫТЫЙ ПОТОК <c>session/follow</c>. Калитке нужно ровно одно: жива ли ссылка,
/// чтобы закрыть SSE, когда движок закончил или оборвался.
/// </summary>
public interface IPhoneFollowLink : IDisposable
{
    /// <summary>Жив ли поток прямо сейчас: <c>false</c> — движок закончил или связь пропала.</summary>
    bool Alive { get; }
}

/// <summary>
/// ДВЕРЬ ЖИВОГО ПОТОКА — делегатами, как и остальные двери калитки. По той же причине: проверка
/// подставляет свои кадры <c>snapshot</c>/<c>event</c>/<c>assistant-stream</c> и не поднимает движок.
///
/// ⚠️ **Это ЧТЕНИЕ, а не новая дверь записи:** открывается <c>session/follow</c>, имя которого
/// уже в белом списке (<see cref="PhoneDecisions.Allowed"/>). Новых имён движка S1 не заводит.
/// </summary>
public sealed record PhoneFollowDoors(
    Func<string, Action<string>, IPhoneFollowLink?> Open,
    Action<string> Log,
    Func<bool>? CanOpen = null)
{
    /// <summary>Есть ли вход СЕЙЧАС — тот же вопрос-а-не-снимок, что у подписки на события.</summary>
    public bool Present => CanOpen?.Invoke() ?? true;

    /// <summary>Двери нет: открывать нечего и не появится.</summary>
    public static PhoneFollowDoors None { get; } = new((_, _) => null, _ => { }, () => false);

    /// <summary>Дверь, которая открывается, когда появляется вход (ссылка входа спрашивается в миг открытия).</summary>
    public static PhoneFollowDoors When(
        Func<bool> canOpen,
        Func<string, Action<string>, IPhoneFollowLink?> open,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);

        return new PhoneFollowDoors(open, log, canOpen);
    }
}

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ ЖИВОГО ПОТОКА: один <c>WS</c> мультиплексора на одно SSE-соединение, поток
/// <c>session/follow</c> со своим <c>streamId</c>. Адрес и cookie движка спрашиваются В МОМЕНТ
/// открытия — после перезапуска движка подписка не должна открываться старым входом.
/// </summary>
public static class EngineFollow
{
    /// <summary>Имя потока в мультиплексоре: <c>phone-live-N</c>, у каждого экрана своё.</summary>
    public const string StreamPrefix = "phone-live-";

    private static int _next;

    /// <summary>Следующий номер потока. Целый, потому что номера только растут и не переиспользуются.</summary>
    public static int NextStream() => System.Threading.Interlocked.Increment(ref _next);

    /// <summary>Настоящие двери живого потока — тем же порядком, что у дверей событий.</summary>
    public static PhoneFollowDoors Doors(
        Func<bool> canOpen,
        Func<string> baseUrl,
        Func<string> cookie,
        TimeSpan timeout,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(log);

        return PhoneFollowDoors.When(
            canOpen,
            (sessionId, onFrame) => Open(baseUrl(), cookie(), timeout, sessionId, onFrame, log),
            log);
    }

    /// <summary>ОТКРЫТЬ ПОТОК. <c>null</c> — не открылся; причина остаётся в журнале, телефону — слова.</summary>
    public static IPhoneFollowLink? Open(
        string? baseUrl,
        string? cookieHeader,
        TimeSpan timeout,
        string sessionId,
        Action<string> onFrame,
        Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onFrame);
        ArgumentNullException.ThrowIfNull(log);

        return EngineMux.OpenStream(
            baseUrl,
            cookieHeader,
            timeout,
            StreamPrefix + NextStream().ToString(CultureInfo.InvariantCulture),
            PhoneDecisions.SessionFollowEndpoint,
            PhoneLiveFrames.FollowArgs(sessionId),
            onFrame,
            log,
            announceLoss: false);
    }
}
