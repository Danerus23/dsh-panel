namespace DshPanel.Server;

/// <summary>Чем кончилось чтение реестра: версия и причина, если не вышло.</summary>
public sealed record EngineRegistryFetch(string Version, string Detail)
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
    /// <summary>Прочитать, какую версию реестр зовёт свежей. Исключений не бросает.</summary>
    EngineRegistryFetch Latest();
}

/// <summary>
/// «СВЕЖАЯ» ВЕРСИЯ ИЗ РЕЕСТРА — то, что реестр зовёт `latest`. Это **не рекомендация**: панель
/// показывает её ОТДЕЛЬНОЙ строкой («новее, но нами не проверена»), а ставит только по отдельной
/// просьбе человека с подтверждением (решения владельца `6565ac9`, `f09da5d`).
///
/// **Почему реестр, а не наш файл подсказок.** Замер разведки E3.2 (10.10.2026): реестр зовёт свежей
/// `0.2.0-rc.2` (`latest` = `next`, всего 31 версия), а проверена и рекомендована `0.1.5-rc.2` —
/// именно поэтому строк ДВЕ, и они честно разные.
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
    /// СВЕЖАЯ ВЕРСИЯ ИЗ ОТВЕТА РЕЕСТРА: `dist-tags.latest`. Чего-то нет, ответ не объект, версия
    /// не строка или пуста — пустая строка, то есть «свежую не узнали»: выдумывать её нельзя,
    /// по ней человек принимает решение.
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

            var version = latest.GetString()?.Trim() ?? string.Empty;

            // Хвост вида `0.2.0-rc.2` сохраняется КАК ЕСТЬ: это версия, которую человек увидит
            // в строке, и подменять её «очищенной» значило бы показывать не то, что в реестре.
            return version.Length == 0
                ? EngineRegistryFetch.Failed("реестр назвал пустую версию")
                : new EngineRegistryFetch(version, string.Empty);
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
