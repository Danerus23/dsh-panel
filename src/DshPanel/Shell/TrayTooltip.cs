using System.Globalization;

namespace DshPanel.Shell;

/// <summary>
/// Подсказка значка в трее: агент, баланс, тариф.
///
/// Отдельной чистой функцией — потому что это единственное, что человек видит, НЕ открывая окно:
/// подсказка висит над значком весь день, и «баланс не получен» в ней обязано отличаться
/// от пустого места. Собирается из готовых строк: сам значок не знает ни про баланс,
/// ни про пики — он только показывает то, что ему дали.
/// </summary>
public static class TrayTooltip
{
    public static string Build(string agentTitle, string balanceSummary, string peakText)
    {
        var balance = string.IsNullOrWhiteSpace(balanceSummary)
            ? PanelStrings.TrayToolTipNoBalance
            : balanceSummary.Trim();

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.TrayToolTipFormat,
            string.IsNullOrWhiteSpace(agentTitle) ? PanelStrings.TrayToolTip : agentTitle.Trim(),
            balance,
            peakText ?? string.Empty).TrimEnd(' ', '·');
    }
}
