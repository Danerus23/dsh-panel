namespace DshTray;

/// <summary>
/// Ссылки продукта: репозиторий выпусков и (когда появится) ссылка для донатов.
///
/// Донат-ссылку заполняет владелец: пока она пустая, строки в «О панели» просто нет —
/// лучше ничего, чем кнопка, ведущая в никуда. Варианты площадок: Boosty (удобно из России),
/// DonationAlerts, GitHub Sponsors (мимо него не пройдёт никто из зарубежных), ЮMoney.
/// Переменная окружения DSH_PANEL_DONATE подменяет значение для проверки.
/// </summary>
internal static class AppLinks
{
    /// <summary>Ссылка на страницу поддержки. Пусто — возможности нет.</summary>
    public const string DonateUrl = "https://app.lava.top/3686297587";

    public static string Donate
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_DONATE");
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
            return DonateUrl;
        }
    }

    /// <summary>Страница репозитория: исходники, выпуски, обновления.</summary>
    public static string RepositoryUrl => "https://github.com/" + UpdateService.Repository;

    /// <summary>Открывает ссылку в браузере. Возвращает текст ошибки или пустую строку.</summary>
    public static string Open(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return Loc.T("about.noLink");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return "";
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }
}
