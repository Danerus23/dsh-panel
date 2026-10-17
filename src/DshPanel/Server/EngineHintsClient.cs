namespace DshPanel.Server;

/// <summary>Чем кончилась попытка прочитать подсказки: текст файла и причина, если не вышло.</summary>
public sealed record HintsFetch(bool Ok, string Json, string Detail)
{
    public static HintsFetch Failed(string detail) => new(false, string.Empty, detail);
}

/// <summary>
/// ПОДСКАЗКИ О ВЕРСИЯХ ИЗ СЕТИ — уговор, как у выпусков и цен: живой путь отделён, поэтому
/// проверки подставляют готовый ответ и в сеть не ходят вовсе.
/// </summary>
public interface IEngineHintsClient
{
    /// <summary>Прочитать файл подсказок. Исключений не бросает: недоступность — это ответ.</summary>
    HintsFetch Read();
}

/// <summary>
/// ФАЙЛ ПОДСКАЗОК ПО HTTP. Устроено как <c>Update\HttpUpdateClient</c> и по тем же причинам:
///
/// 1. **<c>User-Agent</c> обязателен** — без него GitHub отвечает 403;
/// 2. **таймаут короткий:** подсказка нужна человеку сейчас, а не через полминуты ожидания;
/// 3. **наружу не летит ни одно исключение:** нет сети, нет файла, отказ сервера — всё это
///    <c>Ok = false</c> с причиной, и панель работает дальше.
///
/// ⚠️ **Файл читается как ПОДСКАЗКА, а не как источник правды.** Файл обновляется одним коммитом
/// без выпуска панели, поэтому доверять ему можно ровно в одном: «на какой версии панель уже
/// проверяли и что про неё известно». Умения панель проверяет сама — пробой.
/// </summary>
public sealed class HttpEngineHintsClient : IEngineHintsClient
{
    private readonly HttpClient _http;
    private readonly string _url;

    public HttpEngineHintsClient(string url, HttpClient? http = null)
    {
        _url = (url ?? string.Empty).Trim();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public HintsFetch Read()
    {
        if (_url.Length == 0) return HintsFetch.Failed("адрес файла подсказок не задан");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.TryAddWithoutValidation("User-Agent", Shell.ProductLinks.UserAgent);

            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode)
                return HintsFetch.Failed("HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));

            return new HintsFetch(true, response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), string.Empty);
        }
        catch (Exception error)
        {
            // Причина от .NET остаётся причиной: слова-обрамление живут в словарях, а это значение.
            return HintsFetch.Failed($"{error.GetType().Name}: {error.Message}");
        }
    }
}
