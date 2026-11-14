using System.Globalization;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// ССЫЛКА НА ОТКРЫТЫЙ ПОТОК <c>workspace/follow</c>. Калитке нужно ровно одно: жива ли ссылка —
/// чтобы не ждать baseline у мёртвого потока. Кадры потока телефону НЕ пересылаются вовсе.
/// </summary>
public interface IPhoneWorkspaceLink : IDisposable
{
    /// <summary>Жив ли поток прямо сейчас: <c>false</c> — движок закончил или связь пропала.</summary>
    bool Alive { get; }
}

/// <summary>
/// ДВЕРЬ ЧТЕНИЯ АРХИВА — <c>workspace/follow</c>, 14-е имя белого списка (слово владельца
/// 11.11.2026, <c>_team\s3e\owner-slovo-14-2026-11-11.md</c>).
///
/// **Почему дверью, а не вызовом.** У движка нет унарного списка рабочих пространств и архива:
/// <c>workspace/follow</c> — поток, и первый его кадр <c>baseline</c> и есть данные (замер R1 §1.1,
/// §6.1). Значит проверка подставляет свои кадры, не поднимая движок, — как у живой ленты S1.
///
/// ⚠️ **Открытое имя — ТОЛЬКО ЧТЕНИЕ.** Ни одной двери записи здесь нет и не появится: дверь
/// открывает поток, читает первый кадр и закрывает его; доводов у потока нет вовсе.
/// </summary>
public sealed record PhoneWorkspaceDoors(
    Func<Action<string>, IPhoneWorkspaceLink?> Open,
    Action<string> Log,
    Func<bool>? CanOpen = null)
{
    /// <summary>Есть ли вход СЕЙЧАС — вопрос, а не снимок (та же дисциплина, что у подписки S1).</summary>
    public bool Present => CanOpen?.Invoke() ?? true;

    /// <summary>Двери нет: открывать нечего и не появится.</summary>
    public static PhoneWorkspaceDoors None { get; } = new(_ => null, _ => { }, () => false);

    /// <summary>Дверь, которая открывается, когда появляется вход (ссылка входа спрашивается в миг открытия).</summary>
    public static PhoneWorkspaceDoors When(
        Func<bool> canOpen,
        Func<Action<string>, IPhoneWorkspaceLink?> open,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);

        return new PhoneWorkspaceDoors(open, log, canOpen);
    }
}

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ АРХИВА: один <c>WS</c> мультиплексора на одно чтение, поток
/// <c>workspace/follow</c> со своим <c>streamId</c> <c>phone-workspace-N</c>. Адрес и cookie движка
/// спрашиваются В МОМЕНТ открытия — после перезапуска движка старое чтение больше не значит ничего.
///
/// **Доводы пустые** (<c>{}</c>): у <c>workspace/follow</c> их нет вовсе (замер R1 §1.1).
/// </summary>
public static class EngineWorkspace
{
    /// <summary>Имя потока в мультиплексоре: <c>phone-workspace-N</c>, у каждого чтения своё.</summary>
    public const string StreamPrefix = "phone-workspace-";

    private static int _next;

    /// <summary>Следующий номер потока. Целый: номера только растут и не переиспользуются.</summary>
    public static int NextStream() => Interlocked.Increment(ref _next);

    /// <summary>Настоящие двери архива — тем же порядком, что у дверей событий, ленты и очереди.</summary>
    public static PhoneWorkspaceDoors Doors(
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

        return PhoneWorkspaceDoors.When(
            canOpen,
            onFrame => Open(baseUrl(), cookie(), timeout, onFrame, log),
            log);
    }

    /// <summary>ОТКРЫТЬ ПОТОК. <c>null</c> — не открылся; причина остаётся в журнале, телефону — слова.</summary>
    public static IPhoneWorkspaceLink? Open(
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
            PhoneDecisions.WorkspaceFollowEndpoint,
            "{}",
            onFrame,
            log,
            announceLoss: false);
    }
}

/// <summary>
/// ЧТО ПРОЧИТАЛОСЬ ИЗ BASELINE АРХИВА. <see cref="Archived"/> — набор адресов архивных сессий
/// (<c>null</c> — не прочитался, и тогда список отдаётся КАК ЕСТЬ); <see cref="Error"/> — машинная
/// причина для журнала (без имён, без путей); <see cref="Dropped"/> — сколько адресов не принято
/// по форме.
/// </summary>
public sealed record PhoneArchiveView(IReadOnlySet<string>? Archived, int Dropped, string Error)
{
    public bool Ok => Archived is not null;
}

/// <summary>
/// АРХИВ РАБОЧИХ ПРОСТРАНСТВ: из кадра <c>baseline</c> берётся РОВНО <c>archivedSessionIds</c>.
///
/// ⚠️ **Чего здесь нет и не будет.** Baseline <c>workspace/follow</c> несёт ещё
/// <c>items[{workspaceId,path,title,sessionIds,…}]</c> и <c>pinnedSessionIds</c> (замер R1 §1.1).
/// Наружу (телефону) и в журнал они не уходят НИКОГДА: `path` — канонический путь каталога
/// владельца, `title` выводится из него, а `sessionIds` — ровно то, что мы скрываем (R1 §1.2).
/// Поэтому разбор ЧИТАЕТ ТОЛЬКО <c>archivedSessionIds</c>, а не «всё, что попалось».
///
/// **Не прочиталось — не фильтруем.** Правило то же, что у <see cref="PhoneSessions"/>:
/// отфильтровать наугад хуже, чем не отфильтровать, — человек остался бы без списка из-за архива.
/// </summary>
public static class PhoneArchive
{
    /// <summary>Сколько адресов принимаем максимум. На профиле владельца их 34 (замер R1 §1.3).</summary>
    public const int MaxIds = 4096;

    /// <summary>
    /// ЭТО КАДР BASELINE? Дверь ждёт именно его и не разбирает чужие кадры потока
    /// (<c>ready</c>, <c>item</c> с журналом и т. п.) как архив.
    /// </summary>
    public static bool IsBaseline(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return false;

        try
        {
            using var document = JsonDocument.Parse(frame!);

            var value = Unwrap(document.RootElement);

            return value.ValueKind == JsonValueKind.Object
                && string.Equals(Text(value, "type"), "baseline", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// ПРОЧИТАТЬ АРХИВ ИЗ BASELINE. Ни одного поля, кроме <c>archivedSessionIds</c>, не читается —
    /// поэтому путь и название рабочего пространства не могут уехать даже по ошибке в журнал.
    /// </summary>
    public static PhoneArchiveView Read(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return Failed("frame");

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(frame!);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Failed("frame");

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failed("frame");
        }

        var baseline = Unwrap(root);

        if (baseline.ValueKind != JsonValueKind.Object) return Failed("baseline");

        // КАДР АРХИВА `<c>{type:'archived',archivedSessionIds:[…]}</c>` — ЦЕЛЫЙ набор, не дельта
        // (R1 §1.1). Он приходит ПОСЛЕ baseline, и калитка его не ждёт: набор перечитывается
        // новым открытием потока через кэш. Но если он пришёл первым — это тоже архив, и он годен.
        if (string.Equals(Text(baseline, "type"), "archived", StringComparison.Ordinal))
            return Harvest(baseline);

        if (!baseline.TryGetProperty("value", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return Failed("baseline-value");

        return Harvest(payload);
    }

    private static PhoneArchiveView Harvest(JsonElement payload)
    {
        if (!payload.TryGetProperty("archivedSessionIds", out var list) || list.ValueKind != JsonValueKind.Array)
            return Failed("archived");

        var archived = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                dropped++;
                continue;
            }

            var id = (item.GetString() ?? string.Empty).Trim();

            // АДРЕС ПРОВЕРЯЕТСЯ ТОЙ ЖЕ ФОРМОЙ, ЧТО И АДРЕС СЕССИИ В ЛЕНТЕ: мусор в наборе не должен
            // ни скрывать чужие строки, ни раздувать кэш.
            if (!PhoneLiveFrames.ValidSession(id) || archived.Count >= MaxIds)
            {
                dropped++;
                continue;
            }

            archived.Add(id);
        }

        return new PhoneArchiveView(archived, dropped, string.Empty);
    }

    private static PhoneArchiveView Failed(string problem) => new(null, 0, problem);

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
}
