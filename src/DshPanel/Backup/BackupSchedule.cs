using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// Решение часов автокопии: снимать ли копию СЕЙЧАС и почему — словами для журнала.
///
/// Причина едет рядом с решением не «для красоты»: панель, которая молча не сняла копию,
/// неотличима от панели, у которой всё хорошо. Человек, открывший журнал, обязан прочитать
/// «рано: прошло 3 ч из 24» или «расписание выключено» — а не гадать.
/// </summary>
public sealed record BackupDue(bool Take, string Reason);

/// <summary>
/// РАСПИСАНИЕ АВТОМАТИЧЕСКИХ КОПИЙ — чистое решение «пора или нет».
///
/// Решение владельца (не «ежедневно в час X»): панель смотрит, **сколько прошло с прошлой копии**,
/// и снимает новую; если панель была выключена — снимает при запуске, то есть догоняет пропущенное.
/// Поэтому у функции нет ни таймера, ни памяти: **момент прошлой копии приходит из ИМЕНИ самого
/// свежего архива** (<see cref="BackupNaming"/>) — файл, а не переменная в памяти, переживает
/// перезапуск панели и не даёт «сбить отсчёт» кнопкой «создать копию сейчас».
///
/// Три вещи, которые здесь решаются и каждая из случая:
///
/// 1. **часы переводили** — прошлая копия может оказаться помеченной БУДУЩИМ временем
///    (перевод часов назад, смена пояса). Считать «прошло минус три часа» и ждать сутки значило бы
///    не снять копию вовсе; поэтому такой случай — «пора», и он назван словами, а не проглочен;
/// 2. **панель только что запустилась** — на решение не влияет (интервал есть интервал), но
///    в причину добавляется «догоняем пропущенное»: человеку видно, ПОЧЕМУ копия снимается
///    сразу после запуска, а не выглядит это случайностью;
/// 3. **защита от долбёжки** — если попытка не удалась, следующая не раньше, чем через
///    <see cref="RetryGuard"/>. Без неё панель с недоступной папкой или кончившимся местом
///    пыталась бы снова на каждом такте часов — то есть каждые пять минут, круглые сутки,
///    и журнал превратился бы в одну и ту же строку.
/// </summary>
public static class BackupSchedule
{
    /// <summary>
    /// Наименьший сторож после неудачной попытки — 15 минут. Причина: наименьший интервал
    /// расписания — 1 час, и четверть от него равна как раз 15 минутам, то есть за интервал
    /// выходит не больше четырёх попыток. Меньший сторож означал бы попытку каждые пять минут —
    /// 288 попыток в сутки на одном и том же отказе.
    /// </summary>
    public static readonly TimeSpan MinRetry = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Наибольший сторож — 6 часов. Причина: при недельном интервале (168 ч) четверть равна
    /// 42 часам, и о том, что копия не снимается, человек узнал бы только через двое суток.
    /// Для автокопии это слишком долго: шесть часов — ещё «сегодня», а двое суток — уже «когда-нибудь».
    /// </summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(6);

    /// <summary>
    /// Сторож после неудачной попытки: четверть интервала, зажатая между
    /// <see cref="MinRetry"/> и <see cref="MaxRetry"/>. Доводы — у обоих полей.
    /// </summary>
    public static TimeSpan RetryGuard(int everyHours)
    {
        var quarter = TimeSpan.FromMinutes(Math.Max(1, everyHours) * 15.0);

        if (quarter < MinRetry) return MinRetry;
        if (quarter > MaxRetry) return MaxRetry;

        return quarter;
    }

    /// <summary>
    /// Пора ли снимать копию.
    /// </summary>
    /// <param name="enabled">Включено ли расписание. Выключенное — «нет», и это тоже причина словами.</param>
    /// <param name="everyHours">Интервал в часах (границы настройки — 1…168; здесь он зажимается снизу).</param>
    /// <param name="lastCopy">
    /// Момент ПРОШЛОЙ копии — из имени самого свежего НАШЕГО архива <c>dsh2-backup-*</c>. <c>null</c> —
    /// копий ещё не было вовсе. ⚠️ Копии панели 1.x сюда не попадают: папка копий у двух панелей общая,
    /// и чужой архив не должен отменять нашу копию — правило записано и в
    /// <see cref="BackupNaming.TimeFromName"/>, и в том, кто это значение считает
    /// (<c>BackupController.NewestCopyTime</c>).
    /// </param>
    /// <param name="now">Текущий момент. Приходит параметром: функцию проверяют на любых часах.</param>
    /// <param name="justStarted">Панель только что запустилась — это видно в причине (см. класс).</param>
    /// <param name="lastAttempt">
    /// Когда была ПОСЛЕДНЯЯ попытка снять копию в этом прогоне (удачная или нет) — только ради
    /// <see cref="RetryGuard"/>. <c>null</c> — попыток не было.
    /// </param>
    public static BackupDue Due(
        bool enabled,
        int everyHours,
        DateTimeOffset? lastCopy,
        DateTimeOffset now,
        bool justStarted = false,
        DateTimeOffset? lastAttempt = null)
    {
        if (!enabled) return new BackupDue(false, PanelStrings.BackupScheduleOff);

        var interval = TimeSpan.FromHours(Math.Max(1, everyHours));
        BackupDue decision;

        if (lastCopy is null)
        {
            decision = new BackupDue(true, PanelStrings.BackupScheduleNoCopies);
        }
        else if (lastCopy.Value > now)
        {
            decision = new BackupDue(
                true,
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.BackupScheduleFutureFormat,
                    lastCopy.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)));
        }
        else
        {
            var elapsed = now - lastCopy.Value;

            decision = elapsed >= interval
                ? new BackupDue(
                    true,
                    string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.BackupScheduleDueFormat,
                        Span(elapsed), Span(interval)))
                : new BackupDue(
                    false,
                    string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.BackupScheduleEarlyFormat,
                        Span(elapsed), Span(interval)));
        }

        // Сторож применяется к «пора» и только к нему: «рано» и без него значит «рано».
        // Минус в разности (часы перевели назад после попытки) сторожем и закрывается —
        // иначе одна попытка с последующим переводом часов давала бы копию на каждом такте.
        if (decision.Take && lastAttempt is not null && now - lastAttempt.Value < RetryGuard(everyHours))
        {
            return new BackupDue(
                false,
                string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BackupScheduleRetryFormat,
                    Span(now - lastAttempt.Value), Span(RetryGuard(everyHours))));
        }

        return justStarted && decision.Take
            ? decision with { Reason = decision.Reason + PanelStrings.BackupScheduleStartedSuffix }
            : decision;
    }

    /// <summary>
    /// Промежуток словами: «3 ч 5 мин», «25 ч», «40 мин», «меньше минуты».
    ///
    /// Строки берутся ОБЩИЕ (<see cref="PanelStrings.SpanHoursFormat"/> и соседние), а не свои:
    /// длительность словами — одна сущность на всю панель. Дословные двойники у этих строк уже
    /// заводились (26.09.2026), и первым их поймал не компилятор, а переводчик: правило простое —
    /// **готовая строка переиспользуется, а не дублируется**.
    ///
    /// Числа — <c>InvariantCulture</c>, как и размер копии (<see cref="BackupFormat"/>):
    /// разделитель приходит из строки словаря, а не из чисел.
    /// </summary>
    public static string Span(TimeSpan value)
    {
        var span = value < TimeSpan.Zero ? TimeSpan.Zero : value;

        var hours = (long)span.TotalHours;
        var minutes = span.Minutes;

        // Меньше минуты — «меньше минуты»: «0 мин» читалось бы как ошибка счёта, а попытка снять
        // копию бывает и за секунды до такта часов.
        if (span.TotalMinutes < 1) return PanelStrings.SpanLessMinute;

        if (hours == 0)
            return string.Format(CultureInfo.InvariantCulture, PanelStrings.SpanMinutesFormat, minutes);

        return minutes == 0
            ? string.Format(CultureInfo.InvariantCulture, PanelStrings.SpanHoursFormat, hours)
            : string.Format(CultureInfo.InvariantCulture, PanelStrings.SpanHoursMinutesFormat, hours, minutes);
    }
}
