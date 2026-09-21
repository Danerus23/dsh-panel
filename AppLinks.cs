namespace DshTray;

/// <summary>
/// Ссылки продукта: репозиторий выпусков и ссылка для донатов.
///
/// Донат-ссылка заполнена (lava.top), поэтому строка «Поддержать проект» есть в «О панели».
/// Пустая ссылка — это «возможности нет»: строка из окна исчезает совсем, лучше ничего, чем
/// кнопка, ведущая в никуда. Меняешь адрес — поправь AppLinks.cs, три README и
/// .github/FUNDING.yml разом. Варианты площадок: Boosty (удобно из России), DonationAlerts,
/// ЮMoney; GitHub Sponsors получателям из России не подключается.
/// Переменная окружения DSH_PANEL_DONATE подменяет значение для проверки.
/// </summary>
internal static class AppLinks
{
    /// <summary>Ссылка на страницу поддержки. Пусто — строки в «О панели» нет.</summary>
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
