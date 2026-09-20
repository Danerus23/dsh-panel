using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshTray;

public sealed class BalanceResult
{
    public bool Ok { get; set; }
    public string Summary { get; set; } = "—";
    public string Detail { get; set; } = "";
    public bool Available { get; set; }
    public string Error { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";
    public decimal? TotalValue { get; set; }
    public string Currency { get; set; } = "";
}

/// <summary>
/// Баланс аккаунта DeepSeek по ключу из хранилища dsh (~\.dsh\.credentials.yaml).
/// Ключ не выводится ни в интерфейс, ни в журнал. Запрос идёт через HttpClient,
/// а если TLS в системе не работает (Schannel на этой машине отдаёт
/// SEC_E_NO_CREDENTIALS), — через node, у которого свой OpenSSL.
/// </summary>
public sealed class BalanceService
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private static readonly Regex KeyRegex = new(@"^\s*DEEPSEEK_API_KEY:\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string _httpError = Loc.T("balance.notRequested");
    private string _nodeError = Loc.T("balance.notRequested");

    public BalanceService(AppPaths paths)
    {
        _paths = paths;
    }

    public BalanceResult Query()
    {
        var result = new BalanceResult();

        string key;
        try
        {
            key = ReadApiKey();
        }
        catch (Exception error)
        {
            result.Error = error.Message;
            return result;
        }

        var direct = TryHttp(key);
        if (direct != null)
        {
            result.Source = Loc.T("balance.viaHttp");
            return Fill(result, direct.Value);
        }

        var viaNode = TryNode();
        if (viaNode != null)
        {
            result.Source = Loc.T("balance.viaNode");
            return Fill(result, viaNode.Value);
        }

        result.Error = Loc.T("balance.failed", _httpError, _nodeError);
        result.Error = Hide(result.Error, key);
        return result;
    }

    /// <summary>Ключ не должен попасть ни в журнал, ни в интерфейс, ни в подсказку трея.</summary>
    private static string Hide(string text, string key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key) || key.Length < 8) return text;
        return text.Replace(key, "***");
    }

    private string ReadApiKey()
    {
        var path = AppPaths.CredentialsPath;
        if (!File.Exists(path))
        {
            // Человеку говорим по-человечески: «хранилище … не найдено» ничего не объясняло.
            // Ключ и путь к файлу остаются в журнале — там они нужны для разбора.
            throw new InvalidOperationException(Loc.T("balance.keyNotSet") + " (" + AppPaths.Display(path) + ")");
        }

        var raw = File.ReadAllText(path, Encoding.UTF8);
        var match = KeyRegex.Match(raw);
        if (!match.Success)
        {
            throw new InvalidOperationException(Loc.T("balance.keyMissing") + " (" + AppPaths.Display(path) + ")");
        }
        return match.Groups[1].Value;
    }

    private JsonElement? TryHttp(string key)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = _http.Send(request);
            var body = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
            if (!response.IsSuccessStatusCode)
            {
                _httpError = $"HTTP {(int)response.StatusCode}";
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception error)
        {
            _httpError = error.Message;
            return null;
        }
    }

    private JsonElement? TryNode()
    {
        if (!File.Exists(_paths.BalanceScriptPath))
        {
            _nodeError = Loc.T("balance.scriptMissing", _paths.BalanceScriptPath);
            return null;
        }

        try
        {
            var node = NodeLocator.ResolveNode();
            var psi = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_paths.BalanceScriptPath) ?? _paths.BaseDir,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add(_paths.BalanceScriptPath);

            using var process = Process.Start(psi);
            if (process == null)
            {
                _nodeError = Loc.T("balance.nodeFailed");
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                _nodeError = Loc.T("balance.timeout");
                return null;
            }
            if (process.ExitCode != 0)
            {
                _nodeError = Loc.T("balance.exitCode", process.ExitCode, Cut(stderr));
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                _nodeError = root.TryGetProperty("error", out var reason) ? reason.GetString() : Loc.T("balance.unknownError");
                return null;
            }
            if (!root.TryGetProperty("body", out var body))
            {
                _nodeError = Loc.T("balance.noBody");
                return null;
            }
            return body.Clone();
        }
        catch (Exception error)
        {
            _nodeError = error.Message;
            return null;
        }
    }

    private static string Cut(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var trimmed = text.Trim().Replace(Environment.NewLine, " ");
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private static BalanceResult Fill(BalanceResult result, JsonElement payload)
    {
        result.Ok = true;
        result.CheckedAt = DateTime.Now;
        result.Available = payload.TryGetProperty("is_available", out var available)
                           && available.ValueKind == JsonValueKind.True;

        if (!payload.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
        {
            result.Summary = Loc.T("balance.noData");
            return result;
        }

        var summaries = new List<string>();
        var details = new List<string>();
        foreach (var info in infos.EnumerateArray())
        {
            var currency = Text(info, "currency", "?");
            var total = Text(info, "total_balance", "?");
            summaries.Add($"{total} {currency}");

            // Числовое значение нужно для предупреждения о низком балансе.
            if (result.TotalValue == null &&
                decimal.TryParse(total, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                result.TotalValue = parsed;
                result.Currency = currency;
            }

            var parts = new List<string>();
            if (info.TryGetProperty("topped_up_balance", out _)) parts.Add(Loc.T("balance.toppedUp", Text(info, "topped_up_balance", "?")));
            if (info.TryGetProperty("granted_balance", out _)) parts.Add(Loc.T("balance.granted", Text(info, "granted_balance", "?")));
            if (parts.Count > 0) details.Add(string.Join(" · ", parts));
        }

        if (summaries.Count == 0)
        {
            result.Summary = Loc.T("balance.noData");
            return result;
        }

        result.Summary = string.Join(" + ", summaries);
        result.Detail = string.Join(" | ", details);
        return result;
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
