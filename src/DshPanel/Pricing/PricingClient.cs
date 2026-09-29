using System.Net.Http;
using System.Text;

namespace DshPanel.Pricing;

/// <summary>
/// Цены по HTTP — ОДНА дверь в сеть за страницей цен.
///
/// Устроено как <c>Balance\HttpBalanceClient</c> и по той же причине: живой путь отделён уговором
/// (<see cref="IPricingClient"/>), поэтому проверки подставляют готовый ответ и в сеть не ходят
/// вовсе, а панель ходит ровно там, где имеет право.
///
/// ⚠️ **Ключа здесь нет вовсе.** Страница цен публична: панель не отправляет ни ключа модели,
/// ни ссылки входа. Поэтому и вырезать из ошибок нечего — в отличие от баланса, где ключ уходит
/// в заголовке.
///
/// ⚠️ **Адрес ЗАВИСИТ ОТ ЯЗЫКА ПАНЕЛИ** (<see cref="PricingDecisions.Url"/>) и берётся у решения,
/// а не собирается на месте: китайская страница — отдельный документ с числами в юанях, и подмена
/// адреса превратила бы показанные цены в неправду.
/// </summary>
public sealed class HttpPricingClient : IPricingClient
{
    private readonly HttpClient _http;

    public HttpPricingClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public PricingResult Query(string language, DateTimeOffset now)
    {
        var url = PricingDecisions.Url(language);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var response = _http.Send(request);

            if (!response.IsSuccessStatusCode)
            {
                return PricingResult.Failed(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", url, language, now);
            }

            using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
            return PricingPage.Parse(reader.ReadToEnd(), language, url, now);
        }
        catch (Exception ex)
        {
            return PricingResult.Failed($"{ex.GetType().Name}: {ex.Message}", url, language, now);
        }
    }
}
