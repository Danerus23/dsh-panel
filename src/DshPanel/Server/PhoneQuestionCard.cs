using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// КАРТОЧКА ВОПРОСА АГЕНТА ДЛЯ ТЕЛЕФОНА (S2, контракт §2.3) — СВОЙ ОБЪЕКТ, А НЕ КАДР ДВИЖКА.
///
/// **Почему свой.** В кадре вопроса есть `intent` (например, `plan-review`), служебные поля движка
/// и `agentId`; телефону не нужно ни то, ни другое, ни третье — а `intent` мы ещё и **не толкуем**
/// (названное ограничение). Поэтому наружу уходит ровно разрешённое:
/// <c>{id, question, detail?, header?, options?:[{label, description?}], multiSelect?}</c>.
///
/// **Вариантов может не быть вовсе** — тогда человек отвечает своими словами; это НЕ «вопрос
/// не прочитался» и не повод его выбросить (старый разбор <see cref="PhonePage.Parse"/> выбрасывал
/// такие вопросы, потому что рисовал форму, где ответить было нечем).
///
/// ⚠️ **Пределы названы и проверяются, а не обрезаются.** Обрезанный вопрос — это НЕВЕРНЫЙ вопрос,
/// и ответить на него человек может не то, что спрашивают. Не уложилось — состояние
/// <c>unreadable</c> (контракт §2.3), и это честнее, чем молча отрезать.
/// </summary>
public static class PhoneQuestionCards
{
    /// <summary>Предел числа вопросов в одном запросе агента.</summary>
    public const int MaxQuestions = 20;

    /// <summary>Предел текста вопроса (или подписи варианта — см. <see cref="MaxLabel"/>).</summary>
    public const int MaxText = 2000;

    /// <summary>Предел числа вариантов у одного вопроса.</summary>
    public const int MaxOptions = 20;

    /// <summary>Предел подписи одного варианта.</summary>
    public const int MaxLabel = 200;

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// СОБРАТЬ КАРТОЧКИ ИЗ JSON ВОПРОСОВ. <c>false</c> — не уложилось в пределы (причина — в
    /// <paramref name="problem"/>, она для журнала, а не для телефона).
    /// </summary>
    public static bool TryBuild(string? questionsJson, out string json, out string problem)
    {
        json = "[]";
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(questionsJson))
        {
            problem = "вопросов нет";
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(questionsJson);
        }
        catch (JsonException)
        {
            problem = "не разобрано";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                problem = "не массив";
                return false;
            }

            if (root.GetArrayLength() > MaxQuestions)
            {
                problem = "вопросов " + root.GetArrayLength();
                return false;
            }

            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, Writer))
            {
                writer.WriteStartArray();

                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        problem = "вопрос не объект";
                        return false;
                    }

                    // БЕЗ ИМЕНИ ВОПРОСА ОТВЕТ УЙДЁТ В НИКУДА: движок ищет вопрос по `id`.
                    var id = Text(element, "id");

                    if (id.Length == 0 || id.Length > 128)
                    {
                        problem = "id вопроса пуст или длинен";
                        return false;
                    }

                    // ТЕКСТ — ИЗ КАРТОЧКИ, НО ПУТЬ В НЁМ ВСЁ РАВНО ПУТЬ: маска та же, что у кадров
                    // ленты (S1) и у списка: домашний каталог → `~`, имя машины → `***`.
                    var question = PhoneMasks.Mask(Text(element, "question"));
                    var detail = PhoneMasks.Mask(Text(element, "detail"));
                    var header = PhoneMasks.Mask(Text(element, "header"));

                    if (question.Length > MaxText || detail.Length > MaxText || header.Length > MaxText)
                    {
                        problem = "текст вопроса длиннее " + MaxText;
                        return false;
                    }

                    var multi = element.TryGetProperty("multiSelect", out var multiple)
                        && multiple.ValueKind == JsonValueKind.True;

                    if (!Options(element, out var options, out problem)) return false;

                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    writer.WriteString("question", question);

                    if (detail.Length > 0) writer.WriteString("detail", detail);
                    if (header.Length > 0) writer.WriteString("header", header);

                    if (options.Count > 0)
                    {
                        writer.WriteStartArray("options");

                        foreach (var option in options)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("label", option.Label);

                            if (option.Description is { Length: > 0 }) writer.WriteString("description", option.Description);

                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    }

                    // ⚠️ `multiSelect` пишется ТОЛЬКО когда он есть и включён: телефон по умолчанию
                    // рисует один выбор, и лишнее `false` — это лишнее поле в карточке.
                    if (multi) writer.WriteBoolean("multiSelect", true);

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            json = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
    }

    /// <summary>ВАРИАНТЫ: объекты с `label` (и необязательным `description`). Пределы — здесь же.</summary>
    private static bool Options(JsonElement element, out List<PhoneOptionCard> options, out string problem)
    {
        options = new List<PhoneOptionCard>();
        problem = string.Empty;

        if (!element.TryGetProperty("options", out var list)) return true;

        // «Поля нет» и «поле null» — разное только для движка; телефону оба значат «вариантов нет».
        if (list.ValueKind == JsonValueKind.Null) return true;

        if (list.ValueKind != JsonValueKind.Array)
        {
            problem = "options не массив";
            return false;
        }

        if (list.GetArrayLength() > MaxOptions)
        {
            problem = "вариантов " + list.GetArrayLength();
            return false;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                problem = "вариант не объект";
                return false;
            }

            var label = PhoneMasks.Mask(Text(item, "label"));

            if (label.Length == 0 || label.Length > MaxLabel)
            {
                problem = "подпись варианта пуста или длиннее " + MaxLabel;
                return false;
            }

            var description = PhoneMasks.Mask(Text(item, "description"));

            if (description.Length > MaxText)
            {
                problem = "описание варианта длиннее " + MaxText;
                return false;
            }

            options.Add(new PhoneOptionCard(label, description.Length > 0 ? description : null));
        }

        return true;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>Один вариант ответа в том виде, в каком его видит телефон.</summary>
public sealed record PhoneOptionCard(string Label, string? Description);

/// <summary>
/// СОБРАТЬ ОТВЕТ ТЕЛЕФОНА ЗАНОВО (S2, контракт §2.5): только <c>id</c>, <c>selected</c> и <c>custom</c>.
///
/// **Зачем.** Машинный путь ответа существует с F2 и передаёт `answers` движку КАК ЕСТЬ. Телефону
/// это больше не годится: React-пульт шлёт свой JSON, и всё, что в нём окажется, уехало бы
/// в движок. Здесь — тот же приём, что у кадров: свои поля по списку, лишние отбрасываются,
/// пределы проверяются.
/// </summary>
public static class PhoneAnswerCards
{
    /// <summary>Сколько ответов принимаем за один раз (по числу вопросов в карточке).</summary>
    public const int MaxAnswers = 20;

    /// <summary>Предел длины `id` вопроса.</summary>
    public const int MaxId = 128;

    /// <summary>Сколько подписей можно выбрать в одном вопросе.</summary>
    public const int MaxSelected = 20;

    /// <summary>Предел подписи выбранного варианта.</summary>
    public const int MaxLabel = 200;

    /// <summary>Предел «своих слов» (контракт §2.5).</summary>
    public const int MaxCustom = 500;

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Собрать нормализованный массив ответов. <c>false</c> — не уложилось в пределы.</summary>
    public static bool TryBuild(string? answersJson, out string json, out string problem)
    {
        json = "[]";
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(answersJson))
        {
            problem = "ответов нет";
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(answersJson);
        }
        catch (JsonException)
        {
            problem = "ответы не разобраны";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                problem = "ответы не массив";
                return false;
            }

            // ПУСТОЙ МАССИВ — ОТКАЗ (контракт §2.5: ответов 1…20): «ничего не выбрано» — это
            // НЕ форма ответа. Движок трактовал бы пустой набор как «вопрос пропущен», и человек
            // ушёл бы дальше без своего ответа; а отказ «нет вопроса» (403) остаётся за дверью вопроса.
            if (root.GetArrayLength() is 0 or > MaxAnswers)
            {
                problem = "ответов " + root.GetArrayLength();
                return false;
            }

            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, Writer))
            {
                writer.WriteStartArray();

                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        problem = "ответ не объект";
                        return false;
                    }

                    var id = Text(element, "id");

                    if (id.Length == 0 || id.Length > MaxId)
                    {
                        problem = "id ответа пуст или длинен";
                        return false;
                    }

                    if (!Selected(element, out var selected, out problem)) return false;

                    var custom = Text(element, "custom");

                    if (custom.Length > MaxCustom)
                    {
                        problem = "свои слова длиннее " + MaxCustom;
                        return false;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("id", id);

                    writer.WriteStartArray("selected");

                    foreach (var label in selected) writer.WriteStringValue(label);

                    writer.WriteEndArray();

                    if (custom.Length > 0) writer.WriteString("custom", custom);

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            json = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
    }

    private static bool Selected(JsonElement element, out List<string> selected, out string problem)
    {
        selected = new List<string>();
        problem = string.Empty;

        if (!element.TryGetProperty("selected", out var list)) return true;

        if (list.ValueKind == JsonValueKind.Null) return true;

        if (list.ValueKind != JsonValueKind.Array)
        {
            problem = "selected не массив";
            return false;
        }

        if (list.GetArrayLength() > MaxSelected)
        {
            problem = "выбрано " + list.GetArrayLength();
            return false;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                problem = "подпись выбора не строка";
                return false;
            }

            var label = item.GetString() ?? string.Empty;

            if (label.Length > MaxLabel)
            {
                problem = "подпись выбора длиннее " + MaxLabel;
                return false;
            }

            selected.Add(label);
        }

        return true;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// ДОГОН ЛЕНТЫ (<c>session/page</c>) ДЛЯ ТЕЛЕФОНА — ПЕРЕСБОРКА ЗАПИСЕЙ ПО БЕЛОМУ СПИСКУ.
///
/// **От какого случая (поправка §0.5 контракта).** Прежняя дверь отдавала тело движка КАК ЕСТЬ:
/// в записях ехали `system/message` (полный системный запрос с путями), `request/header`,
/// `developer/message`, `tool/call.arguments` (командная строка) и `tool/result.message` (вывод
/// инструментов). Догон обязан идти тем же белым списком, что и живая лента
/// (<see cref="PhoneLiveFrames"/>) — иначе S1 это не «список сессий», а утечка путей.
/// </summary>
public static class PhoneOlder
{
    /// <summary>Предел ответа догона — 512 КиБ (контракт §2.4).</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Предел `maxMessages`; больше — УРЕЗАЕМ, а не отказываем (контракт §2.4).</summary>
    public const int MaxMessages = 50;

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Что получилось: <c>Ok</c> — можно отдавать; иначе причина для журнала.</summary>
    public sealed record Rebuild(bool Ok, string Json, string Problem);

    /// <summary>
    /// ДОВОДЫ ДОГОНА — СВОИ, ЗАНОВО. <paramref name="foreign"/> — адрес не «обычная сессия»
    /// (подчинённая или чужой вид): телефону открыты только обычные (403, контракт §2.4).
    /// <c>maxMessages</c> урезается до <see cref="MaxMessages"/>, а не отвергается.
    /// </summary>
    public static bool TryArgs(string? body, out string args, out string problem, out bool foreign)
    {
        args = string.Empty;
        problem = string.Empty;
        foreign = false;

        if (string.IsNullOrWhiteSpace(body))
        {
            problem = "нет тела";
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            problem = "тело не JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("args", out var argsElement)
                || argsElement.ValueKind != JsonValueKind.Object
                || !argsElement.TryGetProperty("request", out var request)
                || request.ValueKind != JsonValueKind.Object)
            {
                problem = "нет args.request";
                return false;
            }

            if (!request.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object)
            {
                problem = "нет address";
                return false;
            }

            var kind = Text(address, "kind");

            if (!string.Equals(kind, "session", StringComparison.Ordinal))
            {
                foreign = true;
                problem = "address.kind=" + (kind.Length > 0 ? kind : "нет");
                return false;
            }

            var sessionId = Text(address, "sessionId");

            if (!PhoneLiveFrames.ValidSession(sessionId))
            {
                problem = "sessionId не той формы";
                return false;
            }

            if (!Number(request, "throughSeq", out var through) || through < 0 || through > 1_000_000_000)
            {
                problem = "throughSeq";
                return false;
            }

            var before = -1L;

            if (request.TryGetProperty("beforeSeq", out _))
            {
                if (!Number(request, "beforeSeq", out before) || before < 0 || before > 1_000_000_000)
                {
                    problem = "beforeSeq";
                    return false;
                }
            }

            var messages = MaxMessages;

            if (request.TryGetProperty("maxMessages", out _)
                && Number(request, "maxMessages", out var given)
                && given is > 0 and <= MaxMessages)
                messages = (int)given;

            var builder = new StringBuilder("{\"request\":{\"address\":{\"kind\":\"session\",\"sessionId\":");
            builder.Append(JsonSerializer.Serialize(sessionId, Readable));
            builder.Append("},\"throughSeq\":").Append(through.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"maxMessages\":").Append(messages.ToString(CultureInfo.InvariantCulture));

            if (before >= 0) builder.Append(",\"beforeSeq\":").Append(before.ToString(CultureInfo.InvariantCulture));

            builder.Append("}}");
            args = builder.ToString();

            return true;
        }
    }

    /// <summary>
    /// СОБРАТЬ ОТВЕТ ДОГОНА: <c>{ok:true,records:[…],hasMore:bool}</c>. Записи — те же
    /// нормализованные виды, что у живой ленты; всё остальное отбрасывается и считается.
    /// </summary>
    public static Rebuild Build(string? engineBody, int maxBytes = MaxBytes)
    {
        if (maxBytes <= 0) maxBytes = MaxBytes;

        if (string.IsNullOrWhiteSpace(engineBody)) return new Rebuild(false, string.Empty, "пустой ответ");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(engineBody);
        }
        catch (JsonException)
        {
            return new Rebuild(false, string.Empty, "ответ не JSON");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(PhoneSessions.ResultField, out var result)
                || result.ValueKind != JsonValueKind.Object)
                return new Rebuild(false, string.Empty, "нет result");

            if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = Text(error, "code");

                return new Rebuild(false, string.Empty, code.Length > 0 ? code : "error");
            }

            if (!result.TryGetProperty(PhoneSessions.ValueField, out var value) || value.ValueKind != JsonValueKind.Object)
                return new Rebuild(false, string.Empty, "нет value");

            var records = new List<string>();
            var dropped = 0;

            if (value.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in list.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object
                        || !record.TryGetProperty("event", out var wire)
                        || wire.ValueKind != JsonValueKind.Object)
                    {
                        dropped++;
                        continue;
                    }

                    var json = PhoneLiveFrames.Normalize(wire);

                    if (json is null)
                    {
                        dropped++;
                        continue;
                    }

                    records.Add(json);
                }
            }

            var hasMore = value.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;
            var text = Render(records, hasMore);

            // ПРЕДЕЛ ОТВЕТА: урезаем САМЫЕ СТАРЫЕ записи и говорим «есть ещё» — иначе телефон
            // решил бы, что лента кончилась, и не позвал бы догон снова.
            while (Encoding.UTF8.GetByteCount(text) > maxBytes && records.Count > 0)
            {
                records.RemoveAt(0);
                dropped++;
                hasMore = true;

                text = Render(records, hasMore);
            }

            return new Rebuild(true, text, dropped > 0 ? "dropped " + dropped : string.Empty);
        }
    }

    private static string Render(IReadOnlyList<string> records, bool hasMore)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Writer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartArray("records");

            foreach (var record in records) writer.WriteRawValue(record, skipInputValidation: true);

            writer.WriteEndArray();
            writer.WriteBoolean("hasMore", hasMore);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool Number(JsonElement element, string name, out long value)
    {
        value = 0;

        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetInt64(out value);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>
/// КТО СПРОСИЛ: КОРНЕВАЯ СЕССИЯ ИЛИ ПОДЧИНЁННАЯ. Разбор `session/list` — ЧИСТАЯ функция.
///
/// **Зачем.** Вопрос подчинённого агента телефону показывается КАРТОЧКОЙ в корневой сессии
/// (контракт §2.3), а `agentId` у такого вопроса — сессия подчинённого. Значит калитка обязана
/// найти ближайшего корневого предка по `parentSessionId`; само поле телефону не уходит никогда —
/// оно живёт только здесь, внутри.
/// </summary>
public sealed class PhoneSessionIndex
{
    /// <summary>Предел глубины подъёма: защита от петли в чужом ответе.</summary>
    private const int MaxDepth = 32;

    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    private readonly HashSet<string> _subagent = new(StringComparer.Ordinal);

    /// <summary>Индекс, в котором нет ни одной сессии: «знания нет», а не «подчинённых нет».</summary>
    public static PhoneSessionIndex Empty { get; } = new();

    /// <summary>Сколько сессий знает индекс (для журнала).</summary>
    public int Count => _parent.Count;

    /// <summary>РАЗОБРАТЬ `session/list`: адрес → родитель и признак подчинённости.</summary>
    public static PhoneSessionIndex Parse(string? body)
    {
        var index = new PhoneSessionIndex();

        if (string.IsNullOrWhiteSpace(body)) return index;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return index;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(PhoneSessions.ResultField, out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty(PhoneSessions.ValueField, out var value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty(PhoneSessions.ItemsField, out var items)
                || items.ValueKind != JsonValueKind.Array)
                return index;

            foreach (var session in items.EnumerateArray())
            {
                if (session.ValueKind != JsonValueKind.Object) continue;

                var id = Text(session, "sessionId");

                if (id.Length == 0) continue;

                var parent = Text(session, PhoneSessions.ParentField);

                index._parent[id] = parent;

                if (parent.Length > 0 || PhoneSessions.Subordinate(session)) index._subagent.Add(id);
            }
        }

        return index;
    }

    /// <summary>
    /// НАЙТИ КОРЕНЬ. <c>false</c> — сессии в индексе нет (знания не хватает, а не «она корневая»).
    /// <paramref name="subagent"/> — поднялись ли по пути хоть к одному подчинённому.
    /// </summary>
    public bool TryRoot(string? sessionId, out string root, out bool subagent)
    {
        root = string.Empty;
        subagent = false;

        var current = (sessionId ?? string.Empty).Trim();

        if (current.Length == 0 || !_parent.ContainsKey(current)) return false;

        var seen = 0;

        while (true)
        {
            if (_subagent.Contains(current)) subagent = true;

            var parent = _parent.TryGetValue(current, out var found) ? found : string.Empty;

            if (parent.Length == 0 || ++seen > MaxDepth)
            {
                root = current;
                return true;
            }

            if (!_parent.ContainsKey(parent))
            {
                // Родителя нет в окне списка — корнем назвать его нельзя (мы про него ничего
                // не знаем), но и текущую сессию корневой считать нельзя: она подчинённая.
                subagent = true;
                root = string.Empty;

                return true;
            }

            current = parent;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
