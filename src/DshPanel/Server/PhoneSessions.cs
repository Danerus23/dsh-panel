using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// СЕССИИ В ОТВЕТЕ ДВИЖКА: КОГО ТЕЛЕФОНУ НЕ ПОКАЗЫВАТЬ (блок D плана 2.2.0, форма перемерена блоком E).
///
/// **От какого замера.** `_team\220\l2-migraciya.md` (18.10.2026, копия профиля владельца): из
/// **197** сессий **179** — подчинённые (`subagent`), и `session/page` по ним отвечает
/// `{code:"session/agent-busy", message:"subagent Sessions require their durable parent address"}`.
/// Телефон, открывая список, увидел бы **179 неоткрываемых строк** — то есть список, в котором
/// больше нельзя ничего открыть, чем можно.
///
/// **ГДЕ ЛЕЖИТ СПИСОК — ЗАМЕР, А НЕ АНАЛОГИЯ (перемер блока E, 18.10.2026, живые двигатели обеих
/// линий на копии `sessions\`: 198 сессий, 180 подчинённых — числа совпали на 0.1.5 и на 0.2.1):**
/// <code>
/// result = { ok: true, value: { items: [ {sessionId, updatedAt, …, parentSessionId, origin, cwd} … ] } }
/// </code>
/// То есть путь до списка — **`result.value.items`**. До этого фильтр дважды ошибался, и оба раза
/// по одной причине — по аналогии: первая редакция искала `result.value` (как у `userQuestions/answer`),
/// вторая — `result.items` (по подставному ответу САМОЙ панели, который эту форму и «подтверждал»).
/// Живой прогон с копией профиля показал настоящую форму, и теперь она закреплена здесь.
///
/// ⚠️ **Поля сессии по линиям** (тот же замер): у 0.1.5 — `sessionId, updatedAt, running, blank,
/// parentSessionId, origin, cwd`; у 0.2.1 к ним добавляются `agentAvailable` и `formatStatus`.
/// Значит признаки подчинённости (`parentSessionId`, `origin`) есть на ОБЕИХ линиях, а `formatStatus`
/// — только на новой.
///
/// ⚠️ **Пустой список приходит БЕЗ контейнера вовсе** (`result = {ok}`, 62 знака): у чистой установки
/// прятать нечего, и фильтр обязан ничего не делать.
///
/// ⚠️ **Граница, которую этот фильтр не переходит.** Он не зовёт движок, не открывает сессии и не
/// трогает белый список: он **переписывает ОДИН уже разрешённый ответ**. И если форма не та, какую
/// мы мерили, ответ уходит телефону **КАК ЕСТЬ**, а причина называется в журнале: отфильтровать
/// наугад хуже, чем не отфильтровать.
/// </summary>
public static class PhoneSessions
{
    /// <summary>Первое звено пути: `result`.</summary>
    public const string ResultField = "result";

    /// <summary>Второе звено: `value` — контейнер ответа (в нём лежит список).</summary>
    public const string ValueField = "value";

    /// <summary>Третье звено: `items` — сам массив сессий (замер 18.10.2026, обе линии).</summary>
    public const string ItemsField = "items";

    /// <summary>Путь до списка одной строкой — им же подписан отказ в журнале.</summary>
    public const string ItemsPath = ResultField + "." + ValueField + "." + ItemsField;

    /// <summary>Поле «чей это подчинённый»: непустое — сессия подчинённая (замер `l2`).</summary>
    public const string ParentField = "parentSessionId";

    /// <summary>Поле происхождения сессии: `subagent` — подчинённая (объявление движка).</summary>
    public const string OriginField = "origin";

    /// <summary>Значение <see cref="OriginField"/> у подчинённой сессии.</summary>
    public const string SubagentOrigin = "subagent";

    /// <summary>Поле формата журнала — есть только у линии 0.2.x (замер блока E).</summary>
    public const string FormatField = "formatStatus";

    /// <summary>
    /// ЧТО СТАЛО С ОТВЕТОМ: тело (переписанное или исходное), сколько сессий оставлено и сколько
    /// убрано. <see cref="Shape"/> — причина, по которой ответ НЕ переписан (пусто — переписан или
    /// переписывать было нечего).
    /// </summary>
    public sealed record Filtered(string Body, int Kept, int Dropped, string Shape)
    {
        /// <summary>Телефону не показали ни одной сессии — об этом надо сказать в журнале.</summary>
        public bool Hides => Dropped > 0;

        /// <summary>Форма ответа не та, какую мы мерили: ответ отдан как есть.</summary>
        public bool ShapeBroken => Shape.Length > 0;
    }

    /// <summary>
    /// УБРАТЬ ПОДЧИНЁННЫЕ СЕССИИ ИЗ ОТВЕТА `session/list`.
    ///
    /// Ничего не меняет, когда: тело пустое, это не JSON, форма не та или подчинённых нет вовсе
    /// (последнее — обычный случай у чистой установки, и он обязан быть бесплатным).
    /// </summary>
    public static Filtered Filter(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new Filtered(body ?? string.Empty, 0, 0, "empty");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body!);
        }
        catch (JsonException)
        {
            return new Filtered(body!, 0, 0, "not-json");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return new Filtered(body!, 0, 0, "root-" + Kind(root));

            if (!root.TryGetProperty(ResultField, out var result) || result.ValueKind != JsonValueKind.Object)
                return new Filtered(body!, 0, 0, "result-" + Kind(root, ResultField));

            if (!result.TryGetProperty(ValueField, out var value))
                return new Filtered(body!, 0, 0, "result-fields(" + Names(result) + ")");

            if (value.ValueKind != JsonValueKind.Object)
                return new Filtered(body!, 0, 0, ValueField + "-" + Kind(value));

            if (!value.TryGetProperty(ItemsField, out var items))
                return new Filtered(body!, 0, 0, "value-fields(" + Names(value) + ")");

            if (items.ValueKind != JsonValueKind.Array)
                return new Filtered(body!, 0, 0, ItemsField + "-" + Kind(items));

            var kept = new List<string>();
            var dropped = 0;

            foreach (var session in items.EnumerateArray())
            {
                if (Subordinate(session))
                {
                    dropped++;
                    continue;
                }

                kept.Add(session.GetRawText());
            }

            // Ни одной подчинённой — тело НЕ переписывается вовсе: так ответ остаётся байт в байт
            // тем, что сказал движок, и проверять «переписали ли мы то, чего не просили» не нужно.
            if (dropped == 0) return new Filtered(body!, kept.Count, 0, string.Empty);

            return new Filtered(Rebuild(root, kept), kept.Count, dropped, string.Empty);
        }
    }

    // ---- ОСМОТР ФОРМЫ СПИСКА (блок E плана 2.2.0: перемер у 0.2.x) --------------------------

    /// <summary>
    /// ЧТО ИМЕННО ПРИСЛАЛ ДВИЖОК В СПИСКЕ — форма, а не содержимое: где массив (путём), сколько
    /// в нём сессий и подчинённых, и какие из трёх измеренных полей у сессий есть.
    ///
    /// **Зачем отдельно от фильтра.** Фильтр умеет одну форму (`result.value.items`), и это ПРАВИЛЬНО:
    /// он не имеет права угадывать. А вот ИЗМЕРИТЬ форму обязан кто-то другой — иначе «переложили
    /// в другое место» выясняется тогда, когда телефон молча показал 179 неоткрываемых строк.
    /// Осмотр зовётся живым самотестом (`--phone-selftest`) и печатает путь, числа и имена полей,
    /// поэтому смена формы видна прогоном, а не человеком.
    ///
    /// ⚠️ Содержимого в отчёте нет: только имена полей и числа.
    /// </summary>
    public sealed record Examined(
        bool Found,
        string Container,
        int Sessions,
        int Subordinate,
        bool ParentField,
        bool OriginField,
        bool FormatField,
        string Fields)
    {
        /// <summary>Форма та, что ждёт фильтр: путь до списка совпал с измеренным.</summary>
        public bool Expected => Found && string.Equals(Container, ItemsPath, StringComparison.Ordinal);
    }

    /// <summary>ОСМОТРЕТЬ ОТВЕТ: где массив, сколько в нём и какие поля. Ничего не меняет.</summary>
    public static Examined Examine(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Empty("empty");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body!);
        }
        catch (JsonException)
        {
            return Empty("not-json");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return Empty("root-" + Kind(root));

            if (!root.TryGetProperty(ResultField, out var result) || result.ValueKind != JsonValueKind.Object)
                return Empty("result-" + Kind(root, ResultField));

            // СНАЧАЛА ИЗМЕРЕННЫЙ ПУТЬ, ПОТОМ ЛЮБОЙ МАССИВ ВГЛУБЬ НА ДВА ЗВЕНА: осмотр обязан назвать
            // то, что пришло, даже если это не то, чего мы ждём. Иначе «переложили» выглядело бы как
            // «списка нет» — а это два разных ответа человеку и разработчику.
            var container = string.Empty;
            var list = default(JsonElement);

            if (result.TryGetProperty(ValueField, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty(ItemsField, out var measured) && measured.ValueKind == JsonValueKind.Array)
                {
                    container = ItemsPath;
                    list = measured;
                }
                else
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array) continue;

                        container = ResultField + "." + ValueField + "." + property.Name;
                        list = property.Value;
                        break;
                    }
                }
            }

            if (container.Length == 0)
            {
                foreach (var property in result.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) continue;

                    container = ResultField + "." + property.Name;
                    list = property.Value;
                    break;
                }
            }

            if (container.Length == 0) return Empty("result-fields(" + Names(result) + ")");

            var sessions = 0;
            var subordinate = 0;
            var parent = false;
            var origin = false;
            var format = false;
            var fields = string.Empty;

            foreach (var session in list.EnumerateArray())
            {
                sessions++;

                if (Subordinate(session)) subordinate++;

                if (session.ValueKind != JsonValueKind.Object) continue;

                if (fields.Length == 0) fields = Names(session, limit: 10);

                parent |= session.TryGetProperty(ParentField, out _);
                origin |= session.TryGetProperty(OriginField, out _);
                format |= session.TryGetProperty(FormatField, out _);
            }

            return new Examined(true, container, sessions, subordinate, parent, origin, format, fields);
        }
    }

    private static Examined Empty(string container) =>
        new(false, container, 0, 0, false, false, false, string.Empty);

    /// <summary>
    /// КАКАЯ ФОРМА ПРИШЛА — КОРОТКО И БЕЗ СОДЕРЖИМОГО. В строку журнала уходит ЭТО, а не тело:
    /// имена разговоров и пути — данные человека, а форма — факт о движке.
    /// </summary>
    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object(" + Names(value) + ")",
        JsonValueKind.Array => "array[" + value.GetArrayLength() + "]",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "none",
    };

    /// <summary>Имя формы поля, если нужного звена в нём нет: `result-object(ok)`.</summary>
    private static string Kind(JsonElement root, string wanted) =>
        root.TryGetProperty(wanted, out var value) ? Kind(value) : "нет поля";

    /// <summary>
    /// ИМЕНА СВОЙСТВ — по ним видно, куда движок переложил список и какие поля есть у сессии.
    /// По умолчанию пять (журнальная строка короткая), осмотру формы — десять.
    /// </summary>
    private static string Names(JsonElement value, int limit = 5)
    {
        if (value.ValueKind != JsonValueKind.Object) return string.Empty;

        var names = new List<string>();

        foreach (var property in value.EnumerateObject())
        {
            if (names.Count == limit)
            {
                names.Add("…");
                break;
            }

            names.Add(property.Name);
        }

        return string.Join(",", names);
    }

    /// <summary>
    /// ПОДЧИНЁННАЯ ЛИ СЕССИЯ. Два признака из объявления движка — непустой `parentSessionId`
    /// и `origin: "subagent"`; хватает любого. Оба поля есть на ОБЕИХ линиях (замер блока E).
    /// </summary>
    public static bool Subordinate(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return false;

        if (session.TryGetProperty(OriginField, out var origin)
            && origin.ValueKind == JsonValueKind.String
            && string.Equals((origin.GetString() ?? string.Empty).Trim(), SubagentOrigin, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!session.TryGetProperty(ParentField, out var parent)) return false;

        return parent.ValueKind switch
        {
            JsonValueKind.String => (parent.GetString() ?? string.Empty).Trim().Length > 0,
            JsonValueKind.Number => true,
            _ => false,
        };
    }

    /// <summary>
    /// СОБРАТЬ ОТВЕТ ЗАНОВО — и потерять при этом как можно меньше: всё, чего мы не трогали
    /// (тип конверта, `rpcId`, `ok`, поля `result` и `value`, поля самих сессий), переносится
    /// СЫРЫМ текстом, поэтому формат значений, порядок и незнакомые нам поля остаются движковыми.
    /// </summary>
    private static string Rebuild(JsonElement root, IReadOnlyList<string> kept)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();

            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);

                if (!string.Equals(property.Name, ResultField, StringComparison.Ordinal))
                {
                    property.Value.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();

                foreach (var inner in property.Value.EnumerateObject())
                {
                    writer.WritePropertyName(inner.Name);

                    if (!string.Equals(inner.Name, ValueField, StringComparison.Ordinal))
                    {
                        inner.Value.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();

                    foreach (var third in inner.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(third.Name);

                        if (!string.Equals(third.Name, ItemsField, StringComparison.Ordinal))
                        {
                            third.Value.WriteTo(writer);
                            continue;
                        }

                        writer.WriteStartArray();

                        foreach (var raw in kept) writer.WriteRawValue(raw, skipInputValidation: true);

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
