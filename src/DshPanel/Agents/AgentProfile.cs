namespace DshPanel.Agents;

/// <summary>
/// Тарифное окно агента: дни недели (0 = воскресенье, как <see cref="DayOfWeek"/>) и границы
/// в минутах от полуночи **UTC** — так их публикует сам агент.
/// </summary>
public readonly record struct PeakWindow(IReadOnlyList<int> Days, int FromMinutes, int ToMinutes);

/// <summary>
/// Профиль агента: всё, что панели нужно знать, чтобы показать ЕГО баланс и ЕГО окна пика.
///
/// Зачем отдельная сущность. Решение владельца 24.09.2026: «баланс, тарифы и окна пика —
/// на каждого агента», а сейчас агент один (DeepSeek). Разница между агентами не в оформлении,
/// а в четырёх вещах: откуда взять ключ, куда идти за балансом, откуда берутся окна пика
/// и как называется то, что человек видит. Всё это собрано здесь, в одной записи, — добавить
/// агента значит добавить запись, а не искать по коду места, где что-то подставлено.
///
/// Ключ модели панель НЕ хранит: в профиле лежит только ИМЯ ключа в файле движка
/// (<c>~/.dsh/.credentials.yaml</c>). Значение читается в момент запроса и никуда не пишется.
/// </summary>
public sealed record AgentProfile(
    string Id,
    string Title,
    string KeyName,
    string BalanceUrl,
    string PricingSource,
    string PricingChecked,
    IReadOnlyList<PeakWindow> PeakWindows)
{
    /// <summary>Название провайдера для окна и подсказки значка.</summary>
    public string TitleWithKey => Title;
}

/// <summary>
/// Список агентов, которых панель знает. Сейчас он из одного — DeepSeek, то есть сам движок,
/// через который идёт работа.
///
/// ⚠️ **Список будет расти** (решение владельца 24.09.2026): «баланс у нас будет отображаться
/// с переключателем на несколько агентов, цена и пики должны будут тянуться каждый в свой агент».
/// Поэтому переключатель активного агента в настройках есть уже сейчас — с одним вариантом, —
/// и данные баланса и пиков устроены так, что второй агент это новая запись здесь, а не переделка.
/// Отдельное окно со сводкой по всем агентам — задача следующих версий.
/// </summary>
public static class AgentCatalog
{
    /// <summary>Агент по умолчанию — тот, что был у человека всегда.</summary>
    public const string DefaultId = "deepseek";

    /// <summary>
    /// Тарифные окна DeepSeek и дата проверки.
    ///
    /// ✅ **Проверено 24.09.2026** по официальной странице
    /// (<c>https://api-docs.deepseek.com/quick_start/pricing</c>), дословно: «Peak hours are
    /// 01:00 - 04:00 and 06:00 - 10:00 UTC, Monday through Friday… All other hours are off-peak,
    /// including weekends and Chinese public holidays in full», «Off-peak rates are half of the
    /// peak rates». Таблица совпала с той, по которой работала v1 (<c>pricing.json</c>).
    ///
    /// ⚠️ **Чего таблица не знает: китайских праздников.** На странице они исключены из пика
    /// («excluding Chinese public holidays»), а у нас расписание недельное — значит в праздничные
    /// дни панель может сказать «пик», когда на самом деле дешевле. Это честное ограничение,
    /// а не ошибка расчёта; убрать его можно только списком праздников, которого у нас нет.
    /// </summary>
    public static AgentProfile DeepSeek { get; } = new(
        Id: DefaultId,
        Title: "DeepSeek",
        KeyName: "DEEPSEEK_API_KEY",
        BalanceUrl: "https://api.deepseek.com/user/balance",
        PricingSource: "https://api-docs.deepseek.com/quick_start/pricing",
        PricingChecked: "2026-09-24",
        PeakWindows: new[]
        {
            new PeakWindow(new[] { 1, 2, 3, 4, 5 }, 60, 240),    // пн–пт 01:00–04:00 UTC
            new PeakWindow(new[] { 1, 2, 3, 4, 5 }, 360, 600),   // пн–пт 06:00–10:00 UTC
        });

    public static IReadOnlyList<AgentProfile> All { get; } = new[] { DeepSeek };

    /// <summary>
    /// Есть ли у человека ВЫБОР агента. Пока агент один, выпадающий список врал бы: он выглядит
    /// как выбор, а выбрать нечего (решение владельца 27.09.2026 — список агентов будет расти).
    ///
    /// Отдельным решением, а не «сравнением длины списка в каждом окне»: по нему список
    /// становится недоступным и в главном окне, и в настройках, и оба окна обязаны ответить
    /// одинаково.
    /// </summary>
    public static bool HasChoice => All.Count > 1;

    /// <summary>
    /// Агент по имени. Неизвестное имя (в том числе пустое) даёт агента по умолчанию, а не ошибку:
    /// имя приходит из файла настроек, который человек правит руками.
    /// </summary>
    public static AgentProfile Find(string? id)
    {
        foreach (var agent in All)
        {
            if (string.Equals(agent.Id, id, StringComparison.OrdinalIgnoreCase)) return agent;
        }

        return DeepSeek;
    }

    /// <summary>Номера для списка в настройках — по ним окно ставит выбор, не сравнивая строки.</summary>
    public static int IndexOf(string? id)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return 0;
    }

    public static string IdAt(int index) =>
        index >= 0 && index < All.Count ? All[index].Id : DefaultId;
}
