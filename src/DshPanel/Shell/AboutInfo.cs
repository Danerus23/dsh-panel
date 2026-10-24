using System.Reflection;
using DshPanel.Update;

namespace DshPanel.Shell;

/// <summary>
/// СОДЕРЖАНИЕ «О ПРОГРАММЕ» — ЧИСТЫЕ РЕШЕНИЯ, ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ (u14, 2.4.0).
///
/// **От какого случая.** До 2.4.0 «О программе» было отдельным ОКНОМ
/// (окно «О программе», удалённое в 2.4.0), и решения о его строках жили в нём. Решение владельца
/// 27.09.2026 (`docs\DECISIONS.md:32`) требует перенести ответ в РАЗДЕЛ настроек, а окно убрать;
/// при переносе два свойства (<see cref="PanelVersion"/>, <see cref="NumericVersion"/>) нужны
/// не только разделу: версию панели называют отчёт о проблеме, итог обновления и витрина кадра.
/// Второй копии этих правил в панели быть не должно — разойдясь, панель показала бы в настройках
/// один номер, а в отчёте другой.
///
/// ⚠️ Здесь НЕТ ни одного текста и ни одной двери: слова приходят из <c>PanelStrings</c> (три
/// языка), а показ и щелчки — дело окна настроек. Класс отвечает ровно на «какой номер версии»
/// и «показывать ли строку», и оба ответа проверяются прогоном, который умеет падать.
/// </summary>
public static class AboutInfo
{
    /// <summary>
    /// Язык-источник интерфейса. Ему раскрывать про машинный перевод нечего — переводов
    /// на него не делали, и строка <see cref="PanelStrings.AboutTranslationNote"/> по-русски
    /// не показывается (решение владельца, `docs\LOCALIZATION.md` §6).
    /// </summary>
    public const string SourceLanguage = "ru";

    /// <summary>
    /// Показывать ли строку о том, что перевод машинный: ВСЕМ, кроме русского.
    /// Незнакомый язык (пусто) — показываем: не раскрыть машинный перевод хуже, чем раскрыть зря.
    /// </summary>
    public static bool ShowsTranslationNote(string? language) =>
        !string.Equals(language, SourceLanguage, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Показывать ли блок «Сообщить о проблеме». Чистой функцией от наличия двери — чтобы это
    /// решение проверялось прогоном, а не держалось на одном присваивании.
    /// </summary>
    public static bool ShowIssueBlock(bool doorAvailable) => doorAvailable;

    /// <summary>
    /// Версия панели ЧИСЛОВОЙ частью: <c>2.0.0+abc1234</c> → <c>2.0.0</c>. Хеш коммита дописывает
    /// сам .NET в <c>ProductVersion</c>, и показывать его человеку незачем.
    ///
    /// ⚠️ Реализация здесь БОЛЬШЕ НЕ ЖИВЁТ: правило одно на панель и лежит в ядре обновления
    /// (<see cref="UpdateDecisions.Numeric"/>) — там же, где им сравниваются версии.
    /// </summary>
    public static string NumericVersion(string? productVersion) => UpdateDecisions.Numeric(productVersion);

    /// <summary>
    /// Версия панели для раздела: сначала <c>ProductVersion</c> (у панели, собранной в репозитории,
    /// она с хешем коммита), затем — если её почему-то нет — версия сборки.
    /// </summary>
    public static string PanelVersion
    {
        get
        {
            var assembly = typeof(AboutInfo).Assembly;

            var product = NumericVersion(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

            return product.Length > 0 ? product : NumericVersion(assembly.GetName().Version?.ToString());
        }
    }
}
