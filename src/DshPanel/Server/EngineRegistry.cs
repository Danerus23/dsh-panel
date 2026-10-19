namespace DshPanel.Server;

/// <summary>Чем кончилось чтение реестра: САМАЯ СВЕЖАЯ известная версия и причина, если не вышло.</summary>
/// <param name="Version">
/// Самая свежая из двух меток (`latest` и `next`) по правилу сравнения версий панели. Это НЕ всегда
/// `latest`: у npm `next` бывает новее (замер 18.10.2026: `latest` = `0.2.0-rc.2`, `next` = `0.2.1-rc.2`),
/// и показывать человеку `latest` значило бы сказать «новее нет» там, где оно есть.
/// </param>
/// <param name="Latest">Что реестр зовёт `latest` — как есть (для честной строки и для проверок).</param>
/// <param name="Next">Что реестр зовёт `next`; пусто — метки нет или она не строка.</param>
public sealed record EngineRegistryFetch(string Version, string Detail, string Latest = "", string Next = "")
{
    /// <summary>Прочиталось ли: версия непустая — да.</summary>
    public bool Ok => Version.Length > 0;

    public static EngineRegistryFetch Failed(string detail) => new(string.Empty, detail);
}

/// <summary>
/// СВЕЖАЯ ВЕРСИЯ ДВИЖКА ИЗ РЕЕСТРА npm — уговор, как у подсказок и выпусков: живой путь отделён,
/// поэтому проверки подставляют готовый ответ и в сеть не ходят вовсе.
/// </summary>
public interface IEngineRegistryClient
{
    /// <summary>
    /// Прочитать САМУЮ СВЕЖУЮ версию из реестра: старшую из меток `latest` и `next` (см.
    /// <see cref="EngineRegistry.Parse"/>). Исключений не бросает.
    /// </summary>
    EngineRegistryFetch Latest();
}

/// <summary>
/// САМАЯ СВЕЖАЯ ВЕРСИЯ ИЗ РЕЕСТРА — по ДВУМ меткам (`latest` и `next`), а не по одной. Это
/// **не рекомендация**: панель показывает её ОТДЕЛЬНОЙ строкой («новее, но нами не проверена»),
/// а ставит только по отдельной просьбе человека с подтверждением (решения владельца `6565ac9`,
/// `f09da5d`).
///
/// **Почему реестр, а не наш файл подсказок.** Замер 18.10.2026 (задача t3 плана 2.2.0): всего
/// **36** версий, `latest` = **`0.2.0-rc.2`**, `next` = **`0.2.1-rc.2`**, `alpha` = `0.2.1-alpha.5`.
/// Прежняя редакция этого пояснения говорила «`latest` = `next`, всего 31 версия» — это устарело
/// (замер E3.2 10.10.2026), и как раз поэтому читать ОДНУ метку нельзя: более свежая версия жила
/// в `next`, то есть была для панели невидимой.
///
/// ⚠️ **Разбор терпимый, а сеть — отдельно.** Здесь только разбор ответа реестра (чистая функция):
/// его проверяют подставными ответами. Живое чтение — <see cref="HttpEngineRegistryClient"/>,
/// и оно нужно ровно затем, чтобы показать человеку свежую версию, а не чтобы что-то поставить.
/// </summary>
public static class EngineRegistry
{
    /// <summary>Адрес сведений о пакете движка в официальном реестре.</summary>
    public const string PackageUrl = "https://registry.npmjs.org/@deepseek-ai%2Fdsh";

    /// <summary>
    /// САМАЯ СВЕЖАЯ ВЕРСИЯ ИЗ ОТВЕТА РЕЕСТРА: сравниваются `dist-tags.latest` и `dist-tags.next`
    /// правилом панели (<see cref="Update.UpdateDecisions.IsNewer"/>) и берётся старшая. Чего-то
    /// нет, ответ не объект, `latest` не строка или пуста — пустая строка, то есть «свежую не
    /// узнали»: выдумывать её нельзя, по ней человек принимает решение.
    ///
    /// ⚠️ `next` при этом НЕОБЯЗАТЕЛЕН: реестр может его не иметь или назвать не строкой — тогда
    /// свежая берётся из `latest`, как раньше. Отсутствие `next` — не повод не знать свежую.
    /// </summary>
    public static EngineRegistryFetch Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EngineRegistryFetch.Failed("пустой ответ реестра");

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return EngineRegistryFetch.Failed("ответ реестра не объект");

            if (!root.TryGetProperty("dist-tags", out var tags) || tags.ValueKind != System.Text.Json.JsonValueKind.Object)
                return EngineRegistryFetch.Failed("в ответе реестра нет dist-tags");

            if (!tags.TryGetProperty("latest", out var latest) || latest.ValueKind != System.Text.Json.JsonValueKind.String)
                return EngineRegistryFetch.Failed("в ответе реестра нет latest");

            var latestVersion = latest.GetString()?.Trim() ?? string.Empty;

            // Хвост вида `0.2.0-rc.2` сохраняется КАК ЕСТЬ: это версия, которую человек увидит
            // в строке, и подменять её «очищенной» значило бы показывать не то, что в реестре.
            if (latestVersion.Length == 0)
                return EngineRegistryFetch.Failed("реестр назвал пустую версию");

            var nextVersion = tags.TryGetProperty("next", out var next) && next.ValueKind == System.Text.Json.JsonValueKind.String
                ? next.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            var freshest = nextVersion.Length > 0 && Update.UpdateDecisions.IsNewer(nextVersion, latestVersion)
                ? nextVersion
                : latestVersion;

            return new EngineRegistryFetch(freshest, string.Empty, latestVersion, nextVersion);
        }
        catch (Exception error)
        {
            return EngineRegistryFetch.Failed(error.GetType().Name);
        }
    }
}

/// <summary>
/// РЕЕСТР npm ПО HTTP. Устроено как <see cref="HttpEngineHintsClient"/> и по тем же причинам:
/// <c>User-Agent</c> обязателен, срок короткий, наружу не летит ни одно исключение — недоступность
/// это ОТВЕТ («свежую не узнали»), а не падение окна.
///
/// ⚠️ Читается ТОЛЬКО `dist-tags` одной страницы пакета: ни списка версий, ни вложений панель здесь
/// не тянет. Свежая строка — подсказка человеку, и всё.
/// </summary>
public sealed class HttpEngineRegistryClient : IEngineRegistryClient
{
    private readonly System.Net.Http.HttpClient _http;

    public HttpEngineRegistryClient(System.Net.Http.HttpClient? http = null) =>
        _http = http ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public EngineRegistryFetch Latest()
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, EngineRegistry.PackageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", Shell.ProductLinks.UserAgent);

            using var response = _http.Send(request, System.Net.Http.HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode)
                return EngineRegistryFetch.Failed("HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));

            return EngineRegistry.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception error)
        {
            return EngineRegistryFetch.Failed(error.GetType().Name);
        }
    }
}
