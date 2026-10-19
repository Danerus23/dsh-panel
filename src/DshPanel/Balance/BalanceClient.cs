using System.Globalization;
using System.Text;
using System.Text.Json;
using DshPanel.Agents;
using DshPanel.Shell;

namespace DshPanel.Balance;

/// <summary>
/// Что панель узнала про баланс. Записи, а не класса: состояние показывается в окне и печатается
/// в отчёте проверки целиком.
/// </summary>
public sealed record BalanceResult(
    bool Ok,
    bool Available,
    decimal? Total,
    string Currency,
    string Summary,
    string Detail,
    string Error,
    DateTimeOffset CheckedAt,
    string AgentId)
{
    /// <summary>
    /// Сервер ОТВЕРГ ключ (401/403) — отдельно от «счёт недоступен».
    ///
    /// ⚠️ Зачем отдельное поле (находка владельца 30.09.2026). Он показал окно, где на счёте было
    /// **−0.01**, а панель писала «Счёт недоступен: **ключ не принят**» — то есть называла не ту
    /// причину. Пришло это из DeepSeek: на исчерпанном счёте сервис отвечает `is_available: false`
    /// (это про ДЕНЬГИ), а ключ при этом принят — HTTP 200 и данные баланса на месте. Настоящее
    /// «ключ не принят» — это 401/403, и раньше оно показывалось словами «баланс не запрашивался»
    /// (<c>StatusText</c>), то есть две правды стояли МЕСТАМИ. Теперь у каждой своё место.
    /// </summary>
    public bool Rejected { get; init; }

    /// <summary>Запрос ещё не делался (или его нельзя делать в этом прогоне).</summary>
    public static BalanceResult NotRequested(string agentId) =>
        new(false, false, null, string.Empty, PanelStrings.BalanceNotRequested, string.Empty, string.Empty,
            default, agentId);

    public static BalanceResult Failed(string error, string agentId, DateTimeOffset at) =>
        new(false, false, null, string.Empty, PanelStrings.BalanceNotRequested, string.Empty, error, at, agentId);
}

/// <summary>Откуда панель берёт баланс. Отдельный интерфейс — чтобы окно и решения проверялись
/// без сети и без ключа владельца.</summary>
public interface IBalanceClient
{
    BalanceResult Query(AgentProfile agent, string key, DateTimeOffset now);
}

/// <summary>
/// Баланс агента по HTTP.
///
/// ✅ **TLS на этой машине проверен 24.09.2026:** запрос к <c>api.deepseek.com</c> без ключа
/// отвечает <c>401</c>, то есть соединение проходит. В v1 к этому сервису приделан обходной путь
/// через <c>node</c> — «если TLS в системе не работает (Schannel отдаёт SEC_E_NO_CREDENTIALS)»,
/// и он был нужен на той машине и в то время. Здесь обходного пути НЕТ намеренно: непроверенный
/// запасной код живёт годами и ломается тихо, а ошибка TLS обязана быть видна человеку словами.
///
/// Ключ идёт только в заголовке запроса. В текст ошибки он не попадает: ответ и исключения
/// проходят через <see cref="Redact"/>, потому что HttpClient любит вставлять полный адрес,
/// а исключения — произвольные строки.
/// </summary>
public sealed class HttpBalanceClient : IBalanceClient
{
    private readonly HttpClient _http;

    public HttpBalanceClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public BalanceResult Query(AgentProfile agent, string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, agent.BalanceUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = _http.Send(request);
            var body = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 — это про КЛЮЧ, остальные коды — про связь и сервис. Правды разные:
                // «ключ не принят» человек чинит одним способом, «ответ не получен» — другим.
                return BalanceResult.Failed(
                    Redact($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", key), agent.Id, now)
                    with { Rejected = (int)response.StatusCode is 401 or 403 };
            }

            using var document = JsonDocument.Parse(body);
            return Parse(document.RootElement, agent.Id, now);
        }
        catch (Exception ex)
        {
            return BalanceResult.Failed(Redact($"{ex.GetType().Name}: {ex.Message}", key), agent.Id, now);
        }
    }

    /// <summary>
    /// Разбор ответа — ОТДЕЛЬНО и публично: он проверяется тестами на заранее записанных ответах,
    /// в том числе на пустом и на неожиданном. Формат: <c>is_available</c> и <c>balance_infos</c>
    /// со списком валют (у DeepSeek их может быть несколько — «+» в строке, как в v1).
    /// </summary>
    public static BalanceResult Parse(JsonElement payload, string agentId, DateTimeOffset now)
    {
        var available = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("is_available", out var flag)
            && flag.ValueKind == JsonValueKind.True;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("balance_infos", out var infos)
            || infos.ValueKind != JsonValueKind.Array)
        {
            return new BalanceResult(true, available, null, string.Empty, PanelStrings.BalanceNoData,
                string.Empty, string.Empty, now, agentId);
        }

        var summaries = new List<string>();
        var details = new List<string>();
        decimal? total = null;
        var currency = string.Empty;

        foreach (var info in infos.EnumerateArray())
        {
            var infoCurrency = Text(info, "currency", "?");
            var amount = Text(info, "total_balance", "?");
            summaries.Add($"{amount} {infoCurrency}");

            // Числовое значение нужно для предупреждения о низком балансе: строка «12.34» годится
            // для показа, но сравнивать с порогом её нельзя.
            if (total is null
                && decimal.TryParse(amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                total = parsed;
                currency = infoCurrency;
            }

            var parts = new List<string>();
            if (info.TryGetProperty("topped_up_balance", out _))
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BalanceToppedUpFormat, Text(info, "topped_up_balance", "?")));
            }

            if (info.TryGetProperty("granted_balance", out _))
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BalanceGrantedFormat, Text(info, "granted_balance", "?")));
            }

            if (parts.Count > 0)
            {
                // ⚠️ ПОДПИСЬ ВАЛЮТЫ У КАЖДОЙ ГРУППЫ — ВСЕГДА (задача F-4, слово владельца 20.10.2026).
                // Прежде группы «пополнено … · бонус …» склеивались через « | » БЕЗ валюты, и человек
                // угадывал, где USD, а где CNY (скрин владельца: «пополнено 2.78 · бонус 1.08 |
                // пополнено 0.00 · бонус 4.73»). Подпись ставится и при ОДНОЙ валюте — правило не
                // зависит от их числа. Пустая группа не подписывается вовсе (parts.Count == 0 сюда
                // не доходит): висящей валюты без подробностей не бывает.
                details.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.BalanceCurrencyDetailsFormat,
                    infoCurrency,
                    string.Join(" · ", parts)));
            }
        }

        if (summaries.Count == 0)
        {
            return new BalanceResult(true, available, null, string.Empty, PanelStrings.BalanceNoData,
                string.Empty, string.Empty, now, agentId);
        }

        return new BalanceResult(
            Ok: true,
            Available: available,
            Total: total,
            Currency: currency,
            Summary: string.Join(" + ", summaries),
            Detail: string.Join(" | ", details),
            Error: string.Empty,
            CheckedAt: now,
            AgentId: agentId);
    }

    /// <summary>Ключ не должен попасть ни в журнал, ни в окно, ни в отчёт проверки.</summary>
    public static string Redact(string text, string key)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (string.IsNullOrEmpty(key) || key.Length < 8) return text;

        return text.Replace(key, "***", StringComparison.Ordinal);
    }

    private static string Text(JsonElement element, string name, string fallback)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback,
        };
    }
}
