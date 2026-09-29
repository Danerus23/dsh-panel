using System.Globalization;

namespace DshPanel.Server;

/// <summary>
/// Запись «этот сервер — НАШ»: порт, номер процесса и время его создания.
///
/// Зачем она нужна. Панель при выходе сервер НЕ гасит (так решено: закрытие окна не должно
/// обрывать работу человека). Значит после перезапуска панели её собственный сервер продолжал бы
/// работать — а живого дескриптора процесса у новой панели нет, и он выглядел бы «найденным чужим»:
/// панель предложила бы в него «встроиться», а «Остановить» отказывалась бы его гасить.
///
/// Три числа, а не одно: **номера процессов переиспользуются**, и по одному номеру панель однажды
/// погасила бы чужой процесс (урок v1). Поэтому процесс признаётся своим только тогда, когда
/// совпало И время его создания.
///
/// Формат — одна строка <c>порт;PID;время создания</c>. Пишется он при успешном запуске СВОЕГО
/// процесса и снимается при остановке; при выходе панели НЕ снимается — ровно ради этого случая.
/// </summary>
public readonly record struct OwnServerRecord(int Port, int Pid, long StartTicks)
{
    /// <summary>Строка файла. Инвариантная культура: файл читает и машина, и человек.</summary>
    public string Format() => string.Join(
        ';',
        Port.ToString(CultureInfo.InvariantCulture),
        Pid.ToString(CultureInfo.InvariantCulture),
        StartTicks.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Разбор строки файла. Мусор, обрезанная строка и пустое дают <c>null</c> — «записи нет»,
    /// а не «запись с нулями»: по нулевой записи панель не признала бы своим ничего, но и
    /// сказать о ней было бы нечего.
    /// </summary>
    public static OwnServerRecord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(';');
        if (parts.Length < 3) return null;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return null;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return null;

        if (port is <= 0 or > 65535 || pid <= 0 || ticks <= 0) return null;

        return new OwnServerRecord(port, pid, ticks);
    }

    /// <summary>
    /// Тот же ли это процесс, что записан. Сверяются ОБА числа: порт и время создания.
    /// Номер процесса один — по нему сын чужой работы признавался бы своим.
    /// </summary>
    public bool Matches(int listenerPid, long listenerStartTicks) =>
        listenerPid == Pid && listenerStartTicks != 0 && listenerStartTicks == StartTicks;
}
