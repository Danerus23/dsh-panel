using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// ТЕЛО ЗАПРОСА ПЯТИ ДВЕРЕЙ S3 — РАЗБОР ОДНИМ МЕСТОМ И БЕЗ ДОВЕРИЯ К ТЕЛЕФОНУ.
///
/// **Зачем отдельный разбор.** Двери S3 принимают не «тело как есть», а четыре поля
/// (<c>sessionId</c>, <c>requestId</c>, <c>text</c>, <c>itemId</c>) — и только их. Доводы движка
/// собирает калитка, поэтому всё, чего нет в этом списке, в движок не уходит вовсе: лишнее поле
/// в теле телефона игнорируется, а не пересылается «на проверку».
///
/// **Порядок проверок назван контрактом (§2.4):** тело (объект? есть поле?) → форма
/// <c>sessionId</c>/<c>requestId</c>/<c>itemId</c> → текст (пусто? длиннее 4000?).
/// NUL в теле — «не разобран»: строка с нулём внутри не бывает задачей человека.
/// </summary>
public static class PhoneTaskBody
{
    /// <summary>Предел текста задачи — наш, а не движка: 4000 знаков (контракт §7.3).</summary>
    public const int MaxText = 4000;

    /// <summary>Предел длины идентификатора (<c>sessionId</c>, <c>requestId</c>, <c>itemId</c>).</summary>
    public const int MaxId = 128;

    public const string SessionField = "sessionId";
    public const string RequestField = "requestId";
    public const string TextField = "text";
    public const string ItemField = "itemId";

    /// <summary>Чем кончился разбор. <see cref="Problem"/> — ПУСТО, когда разобрано.</summary>
    public sealed record Fields(
        bool Ok,
        string Problem,
        string SessionId,
        string RequestId,
        string Text,
        string ItemId)
    {
        public static Fields Bad(string problem) => new(false, problem, string.Empty, string.Empty, string.Empty, string.Empty);

        public static Fields Good(string sessionId, string requestId, string text, string itemId) =>
            new(true, string.Empty, sessionId, requestId, text, itemId);
    }

    /// <summary>Вид отказа разбора: <c>request</c> (тело), <c>session</c>, <c>empty</c>, <c>long</c>, <c>item</c>.</summary>
    public const string ProblemRequest = "request";
    public const string ProblemSession = "session";
    public const string ProblemEmpty = "empty";
    public const string ProblemLong = "long";
    public const string ProblemItem = "item";

    /// <summary>
    /// РАЗОБРАТЬ ТЕЛО. <paramref name="session"/>, <paramref name="request"/>, <paramref name="text"/>,
    /// <paramref name="item"/> — какие поля ЭТА дверь вообще принимает: у D3 сессии хватает,
    /// у D2 <c>sessionId</c> не принимается (его ещё нет), у D5 нужен пункт.
    /// </summary>
    public static Fields Parse(string? body, bool session, bool request, bool text, bool item)
    {
        if (string.IsNullOrWhiteSpace(body)) return Fields.Bad(ProblemRequest);
        if (body!.Contains('\0', StringComparison.Ordinal)) return Fields.Bad(ProblemRequest);

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Fields.Bad(ProblemRequest);

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Fields.Bad(ProblemRequest);
        }

        var sessionId = string.Empty;
        var requestId = string.Empty;
        var textValue = string.Empty;
        var itemId = string.Empty;

        if (session)
        {
            if (!TryField(root, SessionField, out sessionId)) return Fields.Bad(ProblemRequest);

            sessionId = sessionId.Trim();

            if (!ValidId(sessionId)) return Fields.Bad(ProblemSession);
        }

        if (request)
        {
            if (!TryField(root, RequestField, out requestId)) return Fields.Bad(ProblemRequest);

            requestId = requestId.Trim();

            if (!ValidId(requestId)) return Fields.Bad(ProblemRequest);
        }

        if (text)
        {
            if (!TryField(root, TextField, out textValue)) return Fields.Bad(ProblemRequest);

            textValue = textValue.Trim();

            if (textValue.Length == 0) return Fields.Bad(ProblemEmpty);
            if (textValue.Length > MaxText) return Fields.Bad(ProblemLong);
        }

        if (item)
        {
            if (!TryField(root, ItemField, out itemId)) return Fields.Bad(ProblemRequest);

            itemId = itemId.Trim();

            if (!ValidId(itemId)) return Fields.Bad(ProblemItem);
        }

        return Fields.Good(sessionId, requestId, textValue, itemId);
    }

    /// <summary>
    /// ГОДИТСЯ ЛИ ИДЕНТИФИКАТОР. Форма названа контрактом (§2.1): 1…128 знаков из
    /// <c>[A-Za-z0-9._:-]</c> — та же, что у адреса сессии в живой ленте.
    /// </summary>
    public static bool ValidId(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length is 0 or > MaxId) return false;

        foreach (var symbol in text)
        {
            var ok = symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-';

            if (!ok) return false;
        }

        return true;
    }

    private static bool TryField(JsonElement root, string name, out string value)
    {
        value = string.Empty;

        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;

        value = field.GetString() ?? string.Empty;

        return true;
    }
}

/// <summary>
/// ЗАЩЁЛКА «ОДНА ОТПРАВКА В ПОЛЁТЕ» (контракт §7.2, DEF-072).
///
/// **Зачем.** Двойное нажатие «Отправить» — не редкость на телефоне (палец, перерисовка, две
/// вкладки). Без защёлки два одновременных запроса дают два вызова движка: у D1 это два сообщения
/// в одном разговоре, у D2 — ДВА разговора. Защёлка отвечает второй отправке <c>403</c> словами,
/// НЕ ЗОВЯ движок.
///
/// **Ключ — тот, по которому двойное нажатие действительно двойное** (контракт §7.2): у D1 это
/// <c>sessionId</c> (два человека в одну сессию), у D2 — устройство (разговора ещё нет, а два разных
/// <c>requestId</c> от двойного нажатия родили бы два разговора).
///
/// **Срок 25 с — ДОЛЬШЕ СРОКА СВОЕЙ ДВЕРИ (находка t7, N-1).** Защёлка снимается в <c>finally</c>;
/// срок нужен на случай, когда вызов завис и <c>finally</c> не случился: иначе одна беда заперла бы
/// сессию (или телефон) навсегда.
///
/// ⚠️ **Почему не 20 с, как было.** Дверь D2 вправе работать 22 с (<see cref="PhoneGate.NewDoorTimeout"/>):
/// два вызова движка по 10 с. С защёлкой на 20 с в окне 20…22 с первая отправка ЕЩЁ ШЛА БЫ, а
/// защёлка уже истекла бы — и вторая отправка с ДРУГИМ <c>requestId</c> (двойное нажатие, вторая
/// вкладка) заняла бы освободившееся место и позвала <c>session/create</c>, то есть родила ВТОРОЙ
/// разговор ровно там, ради чего защёлка и заведена. Срок защёлки теперь строго больше срока двери,
/// с запасом 3 с: пока дверь может работать, защёлка жива.
/// </summary>
public sealed class PhoneTaskLatch
{
    /// <summary>
    /// Сколько живёт защёлка сама, если её не сняли (контракт §7.2). Больше срока двери D2 на 3 с:
    /// см. объяснение выше (N-1). Одно место — здесь; двери зовут только <see cref="TryTake"/>.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(25);

    private readonly Dictionary<string, DateTimeOffset> _busy = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Занять место. <c>false</c> — отправка уже идёт, и вторая получает отказ словами.</summary>
    public bool TryTake(string key, DateTimeOffset now)
    {
        if (key.Length == 0) return true;

        lock (_gate)
        {
            Purge(now);

            if (_busy.ContainsKey(key)) return false;

            _busy[key] = now;

            return true;
        }
    }

    /// <summary>Вернуть место. Зовётся ВСЕГДА — и на успехе, и на отказе (в <c>finally</c>).</summary>
    public void Release(string key)
    {
        if (key.Length == 0) return;

        lock (_gate) _busy.Remove(key);
    }

    /// <summary>Сколько отправок в полёте прямо сейчас (число для журнала и проверок).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _busy.Count;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        if (_busy.Count == 0) return;

        List<string>? stale = null;

        foreach (var pair in _busy)
            if (now - pair.Value > Lifetime)
                (stale ??= new List<string>()).Add(pair.Key);

        if (stale is null) return;

        foreach (var key in stale) _busy.Remove(key);
    }
}

/// <summary>
/// КАРТА ПОВТОРОВ D2: <c>requestId → sessionId</c> (контракт §3.5).
///
/// **Почему карта, а не довод <c>sessionId</c>.** `session/create` с нашим <c>sessionId</c> перестаёт
/// быть идемпотентным сразу после первого хода: движок переиспользует сохранённый разговор только
/// пока тот пуст, иначе чеканит новый (§0.2). Значит повтор после «принято» создал бы ВТОРОЙ
/// разговор. Карта помнит, какой разговор уже создан этим <c>requestId</c>, и повтор уходит в него.
///
/// **Границы названы:** 10 минут, не больше 64 записей (вытесняется самая старая), память только
/// в процессе — после перезапуска панели повтор создаст второй разговор (§13 контракта).
/// </summary>
public sealed class PhoneNewMap
{
    /// <summary>Сколько помним пару <c>requestId → sessionId</c>.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Сколько записей держим: телефон не ставит по разговору в секунду, а память не бесконечна.</summary>
    public const int MaxEntries = 64;

    private readonly Dictionary<string, (string SessionId, DateTimeOffset At)> _map = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Вспомнить разговор по <c>requestId</c>. <c>false</c> — пары нет или она протухла.</summary>
    public bool TryGet(string requestId, DateTimeOffset now, out string sessionId)
    {
        sessionId = string.Empty;

        if (requestId.Length == 0) return false;

        lock (_gate)
        {
            if (!_map.TryGetValue(requestId, out var entry)) return false;

            if (now - entry.At > Lifetime)
            {
                _map.Remove(requestId);

                return false;
            }

            sessionId = entry.SessionId;

            return true;
        }
    }

    /// <summary>
    /// ЗАПОМНИТЬ ПАРУ. Ставится СРАЗУ после <c>session/create</c> — до <c>session/prompt</c>:
    /// если задача не принялась, повтор обязан уйти в ЭТОТ разговор, а не создать второй (§3.4).
    /// </summary>
    public void Put(string requestId, string sessionId, DateTimeOffset now)
    {
        if (requestId.Length == 0 || sessionId.Length == 0) return;

        lock (_gate)
        {
            Purge(now);

            if (!_map.ContainsKey(requestId) && _map.Count >= MaxEntries)
            {
                var oldest = _map.MinBy(pair => pair.Value.At).Key;

                _map.Remove(oldest);
            }

            _map[requestId] = (sessionId, now);
        }
    }

    /// <summary>Сколько записей в карте — число в журнал (контракт §3.6).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _map.Count;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        if (_map.Count == 0) return;

        List<string>? stale = null;

        foreach (var pair in _map)
            if (now - pair.Value.At > Lifetime)
                (stale ??= new List<string>()).Add(pair.Key);

        if (stale is null) return;

        foreach (var key in stale) _map.Remove(key);
    }
}

/// <summary>
/// ОТВЕТ ДВИЖКА, ПРОЧИТАННЫЙ ПО СМЫСЛУ (а не пересланный телефону).
///
/// <see cref="Ok"/> — движок ответил и результат разобран; <see cref="Accepted"/> — он сказал
/// <c>accepted:true</c>; <see cref="Code"/> — код отказа (<c>session/not-found</c> и т. п.) — он
/// уходит ТОЛЬКО в журнал, человеку его не показывают (контракт §1.4);
/// <see cref="Reason"/> — машинная причина из <c>details.reason</c>, нужна для одного различения:
/// подчинённая сессия против занятости по другой причине.
/// </summary>
public sealed record PhoneEngineOutcome(bool Ok, bool Accepted, string SessionId, string Code, string Reason)
{
    /// <summary>Разобрать нечего: движок молчал или ответил не той формы.</summary>
    public static PhoneEngineOutcome Unreadable { get; } = new(false, false, string.Empty, string.Empty, string.Empty);

    /// <summary>Код отказа движка говорит «этого больше нет» (<c>session/not-found</c>).</summary>
    public bool SessionGone => string.Equals(Code, "session/not-found", StringComparison.Ordinal);

    /// <summary>Код отказа движка говорит «этот пункт уже не ждёт» (<c>session/queue-item-not-found</c>).</summary>
    public bool ItemGone => string.Equals(Code, "session/queue-item-not-found", StringComparison.Ordinal);

    /// <summary>Отказ по подчинённой сессии: её ведёт родитель, телефону вход туда закрыт.</summary>
    public bool SubagentBound =>
        string.Equals(Code, "session/agent-busy", StringComparison.Ordinal)
        && Reason.Contains("use subagent delivery", StringComparison.Ordinal);
}

/// <summary>
/// ЧТЕНИЕ ОТВЕТА ДВИЖКА. Форма — замер <c>docs\PHONE-LIVE.md</c> §2:
/// <c>{"result":{"ok":true,"value":{…}}}</c> или <c>{"result":{"error":{"code":"…","details":{…}}}}</c>.
/// Тело наружу НЕ отдаётся: калитка собирает свои объекты (контракт §9.1).
/// </summary>
public static class PhoneEngineResult
{
    public static PhoneEngineOutcome Read(PhoneEngineAnswer answer)
    {
        if (answer is null || answer.Status == 0) return PhoneEngineOutcome.Unreadable;

        if (string.IsNullOrWhiteSpace(answer.Body)) return PhoneEngineOutcome.Unreadable;

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(answer.Body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return PhoneEngineOutcome.Unreadable;

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return PhoneEngineOutcome.Unreadable;
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return PhoneEngineOutcome.Unreadable;

        if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = Text(error, "code");

            return new PhoneEngineOutcome(false, false, string.Empty, code.Length > 0 ? code : "error", Reason(error));
        }

        if (!result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return PhoneEngineOutcome.Unreadable;

        var accepted = value.TryGetProperty("accepted", out var flag) && flag.ValueKind == JsonValueKind.True;

        return new PhoneEngineOutcome(true, accepted, Text(value, "sessionId"), string.Empty, string.Empty);
    }

    /// <summary>Причина отказа словами машины: <c>details.reason</c>, иначе <c>message</c>.</summary>
    private static string Reason(JsonElement error)
    {
        if (error.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reason", out var reason)
            && reason.ValueKind == JsonValueKind.String)
            return reason.GetString() ?? string.Empty;

        return Text(error, "message");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// ДОВОДЫ ДВИЖКУ — СОБИРАЕТ КАЛИТКА, И РОВНО ЭТИ (контракт §9.2).
///
/// **Почему одним местом.** Доводы — часть белого списка: телефон присылает четыре поля, а в движок
/// уходит конверт названной формы. Собери его в двух местах — и однажды в одном окажется поле
/// телефона (то самое, чего быть не должно: M5 контракта).
/// </summary>
public static class PhoneTaskArguments
{
    /// <summary>Режим отправки — ВСЕГДА <c>queue</c>: <c>steer</c> и <c>edit</c> в S3 не входят (§13).</summary>
    public const string Mode = "queue";

    /// <summary>Доводы <c>session/prompt</c>: тот же <c>requestId</c>, что дал телефон (идемпотентность движка).</summary>
    public static string Prompt(string sessionId, string requestId, string text) =>
        "{\"request\":{\"requestId\":" + Json(requestId)
        + ",\"sessionId\":" + Json(sessionId)
        + ",\"mode\":\"" + Mode + "\""
        + ",\"content\":[{\"type\":\"text\",\"text\":" + Json(text) + "}]}}";

    /// <summary>Доводы <c>session/create</c>: ПУСТО — ни папки, ни пресета, ни рабочей области (§3.2).</summary>
    public static string Create() => "{\"request\":{}}";

    /// <summary>Доводы <c>session/cancel</c>: только адрес сессии.</summary>
    public static string Cancel(string sessionId) =>
        "{\"request\":{\"sessionId\":" + Json(sessionId) + "}}";

    /// <summary>Доводы <c>session/updateQueue</c>: <c>action</c> — ОБЪЕКТ с <c>kind=remove</c> (§6.2).</summary>
    public static string Remove(string sessionId, string itemId) =>
        "{\"request\":{\"sessionId\":" + Json(sessionId)
        + ",\"itemId\":" + Json(itemId)
        + ",\"action\":{\"kind\":\"remove\"}}}";

    private static string Json(string? text) =>
        JsonSerializer.Serialize(text ?? string.Empty, Readable);

    private static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
