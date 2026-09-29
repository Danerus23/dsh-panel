using System.Globalization;
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
/// Пороги и формат чисел сохранены из v1 ДОСЛОВНО, и это не косметика: в v1 по этим
/// строкам человек читал размер копии в окне, и «4,2 МБ» вместо «4,20 МБ» — привычный
/// ему вид. Числа форматируются с точкой (<see cref="CultureInfo.InvariantCulture"/>),
/// как и было в v1; разделитель приходит из строки, а не из чисел.
/// </summary>
public static class BackupFormat
{
    /// <summary>
    /// Размер: гигабайты с двумя знаками, мегабайты и килобайты — с одним, байты — как есть.
    /// Границы взяты из v1 (<c>BackupService.cs:110</c>) без изменений.
    /// </summary>
    public static string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupUnitGb,
                (bytes / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture));

        if (bytes >= 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupUnitMb,
                (bytes / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture));

        if (bytes >= 1024)
            return string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupUnitKb,
                (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture));

        return string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupUnitB,
            bytes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Длительность: до секунды — миллисекунды, до минуты — секунды, дальше — минуты.
    /// Границы и форматы из v1 (<c>BackupService.cs:118</c>) без изменений.
    /// </summary>
    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 1
            ? string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupTookMs,
                span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            : span.TotalSeconds < 60
                ? string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupTookSec,
                    span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                : string.Format(CultureInfo.InvariantCulture, PanelStrings.BackupTookMin,
                    span.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture));
}
