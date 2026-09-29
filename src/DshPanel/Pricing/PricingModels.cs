using DshPanel.Agents;

namespace DshPanel.Pricing;

/// <summary>
/// ОДНА СТРОКА ЦЕНЫ со страницы цен: за какую единицу и сколько стоит вне пика и в пик.
///
/// Название единицы — СЛОВАМИ САМОЙ СТРАНИЦЫ («1M INPUT TOKENS (CACHE HIT)», «百万tokens输入
/// （缓存命中）»), а не наши: страница — источник правды о своём составе, и перевести её
/// заголовки мы не вправе (машинный перевод в подписи тарифа — это уже выдумывание).
/// Панель переводит только СВОИ подписи колонок.
/// </summary>
public readonly record struct PriceRow(string Item, string OffPeak, string Peak);

/// <summary>
/// Что панель узнала со страницы цен. Записи, а не класса: состояние показывается в окне
/// и печатается в отчёте проверки целиком.
///
/// <see cref="Rows"/> — ТОЛЬКО УЖЕ ГОТОВЫЕ К ПОКАЗУ строки: число и знак валюты сведены вместе
/// (<c>$0.003</c>, <c>¥0.02</c>). Знак берётся по языку панели и по странице ТОГО ЖЕ языка
/// (см. <see cref="PricingDecisions.Currency"/>) — пересчёта по курсу здесь нет и быть не может:
/// курса панель не знает.
/// </summary>
public sealed record PricingResult(
    bool Ok,
    string Error,
    string SourceUrl,
    string Language,
    string Model,
    IReadOnlyList<PriceRow> Rows,
    IReadOnlyList<PeakWindow> Windows,
    DateTimeOffset CheckedAt)
{
    /// <summary>Строк цены нет: не читали, не разобралось или прогон без права ходить в сеть.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Цены ещё не спрашивали (или спрашивать нельзя в этом прогоне).</summary>
    public static PricingResult NotRequested(string language) =>
        new(false, string.Empty, string.Empty, language, string.Empty,
            Array.Empty<PriceRow>(), Array.Empty<PeakWindow>(), default);

    /// <summary>Спросили — и не получилось. Причина называется словами.</summary>
    public static PricingResult Failed(string error, string url, string language, DateTimeOffset at) =>
        new(false, error, url, language, string.Empty,
            Array.Empty<PriceRow>(), Array.Empty<PeakWindow>(), at);
}

/// <summary>
/// Откуда панель берёт цены. Отдельный уговор — чтобы окно и решения проверялись БЕЗ СЕТИ:
/// проверка подставляет свой ответ, а живой запрос делается только обычным запуском панели
/// человеком или по его явной просьбе (кнопка «Обновить информацию»).
///
/// ⚠️ **Сети в проверках не бывает вовсе.** Это не пожелание, а то же правило, по которому
/// устроен баланс: прогон проверки не имеет права ходить в интернет от имени владельца.
/// </summary>
public interface IPricingClient
{
    PricingResult Query(string language, DateTimeOffset now);
}
