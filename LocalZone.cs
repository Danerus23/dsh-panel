using System.Globalization;

namespace DshTray;

/// <summary>
/// Местное время для показа окон пика. Смещение берётся у системы
/// (<see cref="TimeZoneInfo.Local"/>), а не из pricing.json: файл везёт окна в UTC
/// и не знает, в каком поясе его открыли. Раньше в нём лежало московское «+3»,
/// и у любого другого пояса время переключения тарифа показывалось неверно.
/// Переменная DSH_TRAY_TZ_OFFSET (часы, можно дробные: «5.5», «-3») — для проверок
/// и для тех, у кого часы машины выставлены не в том поясе.
/// </summary>
internal static class LocalZone
{
    /// <summary>Смещение местного времени в минутах на указанный момент UTC.</summary>
    public static int OffsetMinutes(DateTime utc)
    {
        var forced = Environment.GetEnvironmentVariable("DSH_TRAY_TZ_OFFSET");
        if (!string.IsNullOrWhiteSpace(forced)
            && double.TryParse(forced.Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
        {
            return (int)Math.Round(hours * 60);
        }

        try
        {
            var moment = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(moment).TotalMinutes);
        }
        catch
        {
            // Пояс не определился (экзотика) — считаем, что время уже местное.
            return 0;
        }
    }

    /// <summary>Подпись пояса по смещению: «UTC», «UTC+03:00», «UTC−05:30».</summary>
    public static string Label(int offsetMinutes)
    {
        if (offsetMinutes == 0) return "UTC";

        var sign = offsetMinutes > 0 ? "+" : "−";
        var abs = Math.Abs(offsetMinutes);
        return string.Format(CultureInfo.InvariantCulture, "UTC{0}{1:00}:{2:00}", sign, abs / 60, abs % 60);
    }

    /// <summary>Время суток по минутам от полуночи, с заворотом через сутки.</summary>
    public static string Clock(int minutes)
    {
        var wrapped = minutes % 1440;
        if (wrapped < 0) wrapped += 1440;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", wrapped / 60, wrapped % 60);
    }

    /// <summary>Время суток момента времени: «04:00».</summary>
    public static string Clock(DateTime moment) =>
        moment.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>На сколько суток сдвинулось окно после перевода в местное время.</summary>
    public static int DayShift(int minutes) => (int)Math.Floor(minutes / 1440.0);

    /// <summary>Дни недели после сдвига на сутки (0=вс … 6=сб).</summary>
    public static int[] ShiftDays(int[] days, int dayShift)
    {
        if (days == null || days.Length == 0 || dayShift == 0) return days ?? Array.Empty<int>();
        return days.Select(day => ((day + dayShift) % 7 + 7) % 7).ToArray();
    }
}
