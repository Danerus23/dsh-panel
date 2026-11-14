using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// ССЫЛКА НА ОТКРЫТЫЙ ПОТОК <c>session/control</c>. Калитке нужно ровно одно: жива ли ссылка —
/// чтобы не ждать baseline у мёртвого потока.
/// </summary>
public interface IPhoneQueueLink : IDisposable
{
    /// <summary>Жив ли поток прямо сейчас: <c>false</c> — движок закончил или связь пропала.</summary>
    bool Alive { get; }
}

/// <summary>
/// ДВЕРЬ ЧТЕНИЯ ОЧЕРЕДИ — <c>session/control</c>, отдельной дверью (контракт §5.1).
///
/// **Почему дверью, а не вызовом.** Очередь лежит в ПОТОКЕ живого состояния: <c>session/control</c>
/// начинает кадром <c>baseline</c>, а не отвечает одним вызовом. Значит дверь обязана уметь открыть
/// поток и закрыть его — и проверка подставляет свои кадры, не поднимая движок (как у живой ленты S1).
///
/// ⚠️ **Это ЧТЕНИЕ, а не новое имя движка:** <c>session/control</c> уже в белом списке
/// (<see cref="PhoneDecisions.Allowed"/>); S3 не расширяет список (§1.2).
/// </summary>
public sealed record PhoneControlDoors(
    Func<Action<string>, IPhoneQueueLink?> Open,
    Action<string> Log,
    Func<bool>? CanOpen = null)
{
    /// <summary>Есть ли вход СЕЙЧАС — вопрос, а не снимок (та же дисциплина, что у подписки S1).</summary>
    public bool Present => CanOpen?.Invoke() ?? true;

    /// <summary>Двери нет: открывать нечего и не появится.</summary>
    public static PhoneControlDoors None { get; } = new(_ => null, _ => { }, () => false);

    /// <summary>Дверь, которая открывается, когда появляется вход (ссылка входа спрашивается в миг открытия).</summary>
    public static PhoneControlDoors When(
        Func<bool> canOpen,
        Func<Action<string>, IPhoneQueueLink?> open,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);

        return new PhoneControlDoors(open, log, canOpen);
    }
}

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ ЧТЕНИЯ ОЧЕРЕДИ: один <c>WS</c> мультиплексора на одно чтение, поток
/// <c>session/control</c> со своим <c>streamId</c> <c>phone-queue-N</c>. Адрес и cookie движка
/// спрашиваются В МОМЕНТ открытия — после перезапуска движка старое чтение больше не значит ничего.
///
/// **Доводы пустые** (<c>{}</c>): у <c>session/control</c> их нет вовсе (замер §2 `PHONE-LIVE.md`),
/// и попытка завернуть их в <c>request</c> даёт <c>gateway/arguments-invalid</c> (§0.7).
/// </summary>
public static class EngineControl
{
    /// <summary>Имя потока в мультиплексоре: <c>phone-queue-N</c>, у каждого чтения своё.</summary>
    public const string StreamPrefix = "phone-queue-";

    private static int _next;

    /// <summary>Следующий номер потока. Целый: номера только растут и не переиспользуются.</summary>
    public static int NextStream() => Interlocked.Increment(ref _next);

    /// <summary>Настоящие двери чтения очереди — тем же порядком, что у дверей событий и ленты.</summary>
    public static PhoneControlDoors Doors(
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

        return PhoneControlDoors.When(
            canOpen,
            onFrame => Open(baseUrl(), cookie(), timeout, onFrame, log),
            log);
    }

    /// <summary>ОТКРЫТЬ ПОТОК. <c>null</c> — не открылся; причина остаётся в журнале, телефону — слова.</summary>
    public static IPhoneQueueLink? Open(
        string? baseUrl,
        string? cookieHeader,
        TimeSpan timeout,
        Action<string> onFrame,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        ArgumentNullException.ThrowIfNull(log);

        return EngineMux.OpenStream(
            baseUrl,
            cookieHeader,
            timeout,
            StreamPrefix + NextStream().ToString(CultureInfo.InvariantCulture),
            PhoneDecisions.SessionControlEndpoint,
            "{}",
            onFrame,
            log,
            announceLoss: false);
    }
}

/// <summary>
/// ОДИН ПУНКТ ОЧЕРЕДИ В ТОМ ВИДЕ, В КАКОМ ЕГО ВИДИТ ТЕЛЕФОН (контракт §5.2):
/// <c>id</c> (адрес пункта для «убрать»), превью текста, полная длина и место.
/// </summary>
public sealed record PhoneQueueItem(string Id, string Text, int Chars, string Placement)
{
    /// <summary>Место «пойдёт в текущий ход» (движок: <c>next-step</c>, <c>steer()</c>).</summary>
    public const string Steering = "steering";

    /// <summary>Место «ждёт следующего хода» (движок: <c>next-turn</c>, <c>followup()</c>).</summary>
    public const string Queued = "queued";
}

/// <summary>
/// ЧТО ПРОЧИТАЛОСЬ ИЗ BASELINE. <see cref="State"/> — <c>ok</c>, <c>no-live</c> или <c>error</c>;
/// <see cref="Error"/> — машинная причина для журнала (телефону уходят слова, не код);
/// счётчики отброшенного — для журнала (контракт §9.3).
/// </summary>
public sealed record PhoneQueueView(
    string Json,
    string State,
    int Total,
    int Shown,
    bool HasMore,
    int LiveSessions,
    int DroppedShape,
    int DroppedService,
    int Trimmed,
    string Error)
{
    public bool Ok => State is "ok" or "no-live";
}

/// <summary>
/// ОЧЕРЕДЬ ОДНОЙ СЕССИИ ИЗ ПРОЕКЦИИ <c>inbox</c> (контракт §5).
///
/// ⚠️ **ИСТОЧНИК РОВНО ОДИН, и это поправка §0.1.** Baseline <c>session/control</c> в 0.2.1 отдаёт
/// <c>{projections}</c> и НЕ содержит <c>queues</c>/<c>jobs</c>. Читать <c>baseline.value.queues</c>
/// значило бы получить пустую очередь ВСЕГДА (мутация M1). Очередь — это
/// <c>baseline.value.projections[&lt;sessionId&gt;].values.inbox</c> =
/// <c>{"next-turn":[…],"next-step":[…]}</c>.
///
/// **Место пункта ВЫЧИСЛЯЕТСЯ:** <c>next-turn</c> → <c>queued</c>, <c>next-step</c> → <c>steering</c>
/// (§0.1). Порядок ответа — <c>steering</c> первой (внутри группы — порядок движка).
///
/// **Только слова человека:** <c>source.kind == "user"</c> (§0.9). Служебные сообщения
/// (<c>user-approval</c> и подобные) телефону не видны и их <c>id</c> наружу не уезжает — иначе
/// телефон мог бы убрать чужой внутренний ввод.
/// </summary>
public static class PhoneQueue
{
    /// <summary>Сколько пунктов отдаём за один раз (контракт §5.3): больше — только «показаны первые N».</summary>
    public const int MaxItems = 20;

    /// <summary>Предел превью текста пункта: 300 знаков + многоточие (контракт §5.2).</summary>
    public const int MaxPreview = 300;

    /// <summary>Предел ОТВЕТА двери — 64 КиБ (контракт §5.3). Не влезло — режем с хвоста.</summary>
    public const int MaxBytes = 64 * 1024;

    public const string StateOk = "ok";
    public const string StateNoLive = "no-live";
    public const string StateError = "error";

    private static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// ЭТО КАДР BASELINE? Дверь ждёт именно его и не разбирает чужие кадры потока
    /// (<c>ready</c>, <c>item</c> с журналом и т. п.) как очередь.
    /// </summary>
    public static bool IsBaseline(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return false;

        try
        {
            using var document = JsonDocument.Parse(frame!);

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;

            var value = Unwrap(root);

            return value.ValueKind == JsonValueKind.Object
                && string.Equals(Text(value, "type"), "baseline", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// ПРОЧИТАТЬ ОЧЕРЕДЬ ИЗ BASELINE. <paramref name="noLiveWords"/> — слова для состояния
    /// <c>no-live</c> (их печатает калитка на языке телефона, а не этот файл).
    /// </summary>
    public static PhoneQueueView Read(string? frame, string sessionId, string noLiveWords, int maxBytes = MaxBytes)
    {
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(frame ?? string.Empty);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Failed("frame");

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failed("frame");
        }

        var baseline = Unwrap(root);

        if (baseline.ValueKind != JsonValueKind.Object) return Failed("baseline");

        if (!baseline.TryGetProperty("value", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return Failed("baseline-value");

        if (!payload.TryGetProperty("projections", out var projections) || projections.ValueKind != JsonValueKind.Object)
            return Failed("projections");

        var live = 0;

        foreach (var _ in projections.EnumerateObject()) live++;

        // БЛОКА СЕССИИ НЕТ — это НЕ «очередь пуста» (контракт §5.3): baseline содержит только ЖИВЫЕ
        // сессии, и закрытый экран честно даёт «неизвестна», а не пустой список.
        if (!projections.TryGetProperty(sessionId, out var box) || box.ValueKind != JsonValueKind.Object)
            return NoLive(sessionId, live, noLiveWords);

        if (!box.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            return Failed("values");

        JsonElement? inbox = null;

        if (values.TryGetProperty("inbox", out var inboxBox))
        {
            // Базовая форма: `inbox` — объект. Ноль и отсутствие различить не нужно (оба — пусто),
            // а ЛЮБАЯ другая форма — это не очередь: молчаливая пустота была бы ложью.
            if (inboxBox.ValueKind == JsonValueKind.Object) inbox = inboxBox;
            else if (inboxBox.ValueKind != JsonValueKind.Null) return Failed("inbox");
        }

        return Queue(inbox, sessionId, live, maxBytes);
    }

    /// <summary>
    /// СОБРАТЬ ОТВЕТ ИЗ ПРОЕКЦИИ <c>inbox</c> — ОДНО МЕСТО НА ДВА ВХОДА: разовое чтение двери D4
    /// (<see cref="Read"/>) и живой кадр очереди в ленте (<see cref="Live"/>). Разойдись они —
    /// телефон читал бы одну форму с D4 и другую из SSE на одной и той же очереди.
    /// <paramref name="inbox"/> — <c>null</c>: очередь пуста (проекции нет или она нулевая).
    /// </summary>
    private static PhoneQueueView Queue(JsonElement? inbox, string sessionId, int live, int maxBytes)
    {
        if (maxBytes <= 0) maxBytes = MaxBytes;

        var items = new List<PhoneQueueItem>();
        var total = 0;
        var droppedShape = 0;
        var droppedService = 0;

        if (inbox is { } box)
        {
            // STEERING ПЕРВОЙ: «пойдёт в текущий ход» стоит выше «ждёт следующего хода» — это вид
            // группировки, а не обещание порядка (контракт §5.2).
            Collect(box, "next-step", PhoneQueueItem.Steering, items, ref total, ref droppedShape, ref droppedService);
            Collect(box, "next-turn", PhoneQueueItem.Queued, items, ref total, ref droppedShape, ref droppedService);
        }

        var shown = items.Count;
        var hasMore = total > shown;
        var json = Render(sessionId, total, items, hasMore);

        // ПРЕДЕЛ ОТВЕТА — 64 КиБ. Режем С ХВОСТА (дальние пункты телефон увидит, перечитав очередь),
        // а не режем текст пункта: пункт с обрезанным текстом человек прочитал бы как полный.
        var trimmed = 0;

        while (items.Count > 0 && Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            items.RemoveAt(items.Count - 1);
            trimmed++;
            hasMore = true;

            json = Render(sessionId, total, items, hasMore);
        }

        return new PhoneQueueView(json, StateOk, total, items.Count, hasMore, live, droppedShape, droppedService, trimmed, string.Empty);
    }

    /// <summary>
    /// ЧТО ПРИНЁС ЖИВОЙ КАДР: очередь этой сессии, чужая проекция или «не наше».
    /// <see cref="Projection"/> — кадр был проекцией (для счёта отброшенного по образцу белого списка).
    /// </summary>
    public readonly record struct PhoneQueueLive(PhoneQueueView? View, bool Projection);

    /// <summary>
    /// ОЧЕРЕДЬ ИЗ ЖИВОГО КАДРА <c>session/control</c> — ДЛЯ ЛЕНТЫ (S4a, R1 §2.2).
    ///
    /// **Два входа и оба нужны:** первый кадр потока — <c>baseline</c>, и очередь в нём лежит в
    /// <c>value.projections[&lt;sessionId&gt;].values.inbox</c>; дальше приходят кадры
    /// <c>{type:'projection',sessionId,key,value,seq}</c>, и очередь — это <c>key='inbox'</c>.
    ///
    /// ⚠️ **Фильтр по двум полям сразу — <c>sessionId</c> И <c>key</c>.** В одном потоке лежат
    /// проекции ВСЕХ живых сессий и всех ключей: без фильтра телефону уехали бы
    /// <c>workingDirectory</c> (путь владельца) и <c>turnOutline</c> (тексты ходов). Чужое
    /// отбрасывается ДО сборки JSON, а не после.
    ///
    /// <c>null</c> в <see cref="PhoneQueueLive.View"/> — очередь молчит: кадр не про эту сессию,
    /// не про этот ключ или не той формы. Ложного «пусто» здесь нет.
    /// </summary>
    public static PhoneQueueLive Live(
        string? frame, string sessionId, string noLiveWords, int maxBytes = MaxBytes)
    {
        if (string.IsNullOrWhiteSpace(frame)) return default;

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(frame!);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return default;

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }

        var value = Unwrap(root);

        if (value.ValueKind != JsonValueKind.Object) return default;

        var type = Text(value, "type");

        if (string.Equals(type, "baseline", StringComparison.Ordinal))
        {
            if (!value.TryGetProperty("value", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return default;

            if (!payload.TryGetProperty("projections", out var projections) || projections.ValueKind != JsonValueKind.Object)
                return default;

            var live = 0;

            foreach (var _ in projections.EnumerateObject()) live++;

            // БЛОКА СЕССИИ НЕТ — «неизвестна», а не «пусто» (та же дисциплина, что у D4).
            if (!projections.TryGetProperty(sessionId, out var box) || box.ValueKind != JsonValueKind.Object)
                return new PhoneQueueLive(NoLive(sessionId, live, noLiveWords), false);

            if (!box.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
                return default;

            return new PhoneQueueLive(Inbox(values, sessionId, live, maxBytes), false);
        }

        // КАДР ПРОЕКЦИИ: чужое — отброшено, но СЧИТАЕТСЯ (журнал говорит числом, без имён).
        if (!string.Equals(type, "projection", StringComparison.Ordinal)) return default;

        if (!string.Equals(Text(value, "sessionId"), sessionId, StringComparison.Ordinal)
            || !string.Equals(Text(value, "key"), "inbox", StringComparison.Ordinal))
            return new PhoneQueueLive(null, true);

        if (!value.TryGetProperty("value", out var inbox)) return new PhoneQueueLive(null, true);

        if (inbox.ValueKind == JsonValueKind.Object)
            return new PhoneQueueLive(Queue(inbox, sessionId, 0, maxBytes), true);

        // `inbox` стал нулём — очередь честно пуста.
        return inbox.ValueKind == JsonValueKind.Null
            ? new PhoneQueueLive(Queue(null, sessionId, 0, maxBytes), true)
            : new PhoneQueueLive(null, true);
    }

    /// <summary>Проекция <c>inbox</c> ИЗ <c>values</c> — одна форма на baseline и на кадр изменения.
    /// <c>null</c> — проекция не той формы: очередь молчит, а не врёт пустотой.</summary>
    private static PhoneQueueView? Inbox(JsonElement values, string sessionId, int live, int maxBytes)
    {
        if (!values.TryGetProperty("inbox", out var inbox)) return Queue(null, sessionId, live, maxBytes);

        return inbox.ValueKind switch
        {
            JsonValueKind.Object => Queue(inbox, sessionId, live, maxBytes),
            JsonValueKind.Null => Queue(null, sessionId, live, maxBytes),
            _ => null,
        };
    }

    private static void Collect(
        JsonElement inbox,
        string group,
        string placement,
        List<PhoneQueueItem> items,
        ref int total,
        ref int droppedShape,
        ref int droppedService)
    {
        if (!inbox.TryGetProperty(group, out var list) || list.ValueKind != JsonValueKind.Array) return;

        foreach (var message in list.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                droppedShape++;
                continue;
            }

            var id = Text(message, "id");

            if (!PhoneTaskBody.ValidId(id))
            {
                droppedShape++;
                continue;
            }

            // ТОЛЬКО СЛОВО ЧЕЛОВЕКА (§0.9): служебный ввод движка телефону не принадлежит.
            if (!IsUser(message))
            {
                droppedService++;
                continue;
            }

            var text = Content(message);

            total++;

            if (items.Count < MaxItems)
                items.Add(new PhoneQueueItem(id, PhoneMasks.Cut(PhoneMasks.Mask(text), MaxPreview), text.Length, placement));
        }
    }

    /// <summary>Слово человека или служебное сообщение движка. Нет <c>source.kind</c> — служебное.</summary>
    private static bool IsUser(JsonElement message) =>
        message.TryGetProperty("source", out var source)
        && source.ValueKind == JsonValueKind.Object
        && string.Equals(Text(source, "kind"), "user", StringComparison.Ordinal);

    /// <summary>Текст сообщения: блоки <c>content</c> типа <c>text</c>, склеенные переводом строки.</summary>
    private static string Content(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var text = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            if (!string.Equals(Text(block, "type"), "text", StringComparison.Ordinal)) continue;

            if (!block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) continue;

            if (text.Length > 0) text.Append('\n');

            text.Append(value.GetString());
        }

        return text.ToString().Trim();
    }

    private static string Render(
        string sessionId, int total, IReadOnlyList<PhoneQueueItem> items, bool hasMore)
    {
        var json = new StringBuilder();

        json.Append("{\"ok\":true,\"state\":\"").Append(StateOk).Append("\",\"sessionId\":").Append(Quote(sessionId));
        json.Append(",\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"shown\":").Append(items.Count.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"hasMore\":").Append(hasMore ? "true" : "false");
        json.Append(",\"items\":[");

        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0) json.Append(',');

            var item = items[index];

            json.Append("{\"id\":").Append(Quote(item.Id));
            json.Append(",\"text\":").Append(Quote(item.Text));
            json.Append(",\"chars\":").Append(item.Chars.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"placement\":").Append(Quote(item.Placement));
            json.Append('}');
        }

        json.Append("]}");

        return json.ToString();
    }

    private static PhoneQueueView NoLive(string sessionId, int live, string words) =>
        new(
            "{\"ok\":true,\"state\":\"" + StateNoLive + "\",\"sessionId\":" + Quote(sessionId)
            + ",\"total\":0,\"shown\":0,\"hasMore\":false,\"items\":[],\"message\":" + Quote(words) + "}",
            StateNoLive,
            0,
            0,
            false,
            live,
            0,
            0,
            0,
            string.Empty);

    private static PhoneQueueView Failed(string problem) =>
        new(string.Empty, StateError, 0, 0, false, 0, 0, 0, 0, problem);

    /// <summary>Развернуть кадр потока: <c>{type:'item',value:{…}}</c> → <c>value</c>.</summary>
    private static JsonElement Unwrap(JsonElement root)
    {
        if (string.Equals(Text(root, "type"), "item", StringComparison.Ordinal)
            && root.TryGetProperty("value", out var value))
            return value;

        return root;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Quote(string? text) => JsonSerializer.Serialize(text ?? string.Empty, Readable);
}
