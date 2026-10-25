using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// Как показываются размер копии и её длительность.
///
/// Это ПЕРВЫЙ артефакт этапа 4 и перенесён он первым не случайно
/// (<c>docs\MIGRATION.md</c> §5, шаг 9; §7.4): класс чистый, его ждали четыре файла,
/// а лежал он внутри <c>BackupService.cs</c> — из-за чего возникала ложная зависимость
/// «установщик Node знает про сервис копий».
///
/// Пороги и раскладка чисел сохранены из v1 ДОСЛОВНО, и это не косметика: в v1 по этим
/// строкам человек читал размер копии в окне, и «4,2 МБ» вместо «4,20 МБ» — привычный
/// ему вид. **Числа форматируются культурой ЯЗЫКА ПАНЕЛИ, а не инвариантной** (DEF-060, 2.4.2;
/// банк `_design-library\70-writing.md` §10.3, Р-6): до правки русское окно писало «44.7 МБ»
/// с точкой, потому что культура была инвариантной. Культуру даёт ОДНА дверь
/// (<see cref="DateText.Culture(string?)"/>) — та же, что у дат: культура у языка, а не у машины.
/// </summary>
public static class BackupFormat
{
    /// <summary>
    /// Размер: гигабайты с двумя знаками, мегабайты и килобайты — с одним, байты — как есть.
    /// Границы взяты из v1 (<c>BackupService.cs:110</c>) без изменений.
    /// </summary>
    public static string Size(long bytes)
    {
        var culture = DateText.Culture();

        if (bytes >= 1024L * 1024 * 1024)
            return string.Format(culture, PanelStrings.BackupUnitGb,
                (bytes / 1024.0 / 1024 / 1024).ToString("0.00", culture));

        if (bytes >= 1024 * 1024)
            return string.Format(culture, PanelStrings.BackupUnitMb,
                (bytes / 1024.0 / 1024).ToString("0.0", culture));

        if (bytes >= 1024)
            return string.Format(culture, PanelStrings.BackupUnitKb,
                (bytes / 1024.0).ToString("0.0", culture));

        return string.Format(culture, PanelStrings.BackupUnitB, bytes.ToString(culture));
    }

    /// <summary>
    /// Длительность: до секунды — миллисекунды, до минуты — секунды, дальше — минуты.
    /// Границы и форматы из v1 (<c>BackupService.cs:118</c>) без изменений; числа — культурой
    /// языка панели, как и у размера.
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        var culture = DateText.Culture();

        return span.TotalSeconds < 1
            ? string.Format(culture, PanelStrings.BackupTookMs, span.TotalMilliseconds.ToString("0", culture))
            : span.TotalSeconds < 60
                ? string.Format(culture, PanelStrings.BackupTookSec, span.TotalSeconds.ToString("0.0", culture))
                : string.Format(culture, PanelStrings.BackupTookMin, span.TotalMinutes.ToString("0.0", culture));
    }
}
