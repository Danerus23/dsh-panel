using System.Diagnostics;

namespace DshPanel.Update;

/// <summary>
/// ХОД ПОДГОТОВКИ, КАКИМ ЕГО ВИДИТ ЧЕЛОВЕК: сколько уже скачано, сколько всего, с какой средней
/// скоростью и сколько осталось. Отдельной записью от <see cref="UpdateProgress"/>, а не его
/// полями: <see cref="UpdateProgress"/> приходит от загрузчика и называет ТОЛЬКО факт (этап,
/// имя, принято, всего), а скорость и остаток — это СЧЁТ панели по монотонным часам, и жить он
/// должен там, где часы, а не в чужом ответе.
///
/// <see cref="Total"/> меньше нуля — размер файла сервер не назвал; тогда <see cref="Percent"/>
/// равен -1, и «0 %» вместо честного «неизвестно» не показывается.
/// </summary>
public sealed record UpdateProgressView(
    UpdateStage Stage,
    string Name,
    long Done,
    long Total,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    /// <summary>Сколько процентов уже есть; -1 — размер неизвестен.</summary>
    public int Percent => Total > 0
        ? (int)Math.Clamp(Done * 100L / Total, 0L, 100L)
        : -1;
}

/// <summary>
/// СКОРОСТЬ И ОСТАТОК — ЧИСТЫЕ ПРАВИЛА (DEF-060, 2.4.2).
///
/// **От какого случая.** Строка хода показывала проценты ОДНОГО файла, и человек не знал ни
/// общего объёма, ни скорости, ни того, сколько ещё ждать; на двух файлах (архив, потом
/// установщик) полоса дважды начиналась с нуля. Владелец 24.10.2026: «34 % второго файла после
/// полного первого».
///
/// **Почему средняя, а не мгновенная скорость.** Мгновенная скачет на каждом куске и читается
/// как «зависло»: то 12 МБ/с, то 0,4. Средняя за попытку — ровное число, по которому остаток
/// и считается. Часы монотонные (<see cref="Stopwatch"/>), не системное время: перевод часов
/// или переход на летнее время не должен врать про остаток.
///
/// **Чего эти функции НЕ делают.** Не ходят на диск и в сеть, не знают про этапы и слова: им
/// дают числа, они возвращают числа. Поэтому проверяются перебором, а мутация «скорость = 0» /
/// «остаток врёт» падает названной проверкой, а не «на глаз».
/// </summary>
public static class UpdateProgressMath
{
    /// <summary>
    /// СРЕДНЯЯ СКОРОСТЬ ПОПЫТКИ: принятые байты, делённые на прошедшее время. Ноль — когда
    /// считать нечего (ничего не принято или время ещё не шло): выдумывать «0.0 МБ/с» вместо
    /// «скорость пока не посчитана» нельзя.
    /// </summary>
    public static double BytesPerSecond(long done, TimeSpan elapsed) =>
        done > 0 && elapsed > TimeSpan.Zero ? done / elapsed.TotalSeconds : 0;

    /// <summary>
    /// СКОЛЬКО ЕЩЁ ЖДАТЬ при этой скорости. <c>null</c> — считать нечем (размер файла не назван
    /// или скорость неизвестна): пустое место в строке честнее выдуманного «0 с».
    /// Уже принятое сверх названного размера даёт ноль, а не отрицательное время.
    /// </summary>
    public static TimeSpan? Remaining(long total, long done, double bytesPerSecond)
    {
        if (total <= 0 || bytesPerSecond <= 0) return null;

        var left = Math.Max(0L, total - done);

        return TimeSpan.FromSeconds(left / bytesPerSecond);
    }
}

/// <summary>
/// СЧЁТЧИК ХОДА: монотонные часы ОДНОЙ попытки подготовки → снимок для показа.
///
/// Один на попытку: <see cref="Reset"/> зовётся на старте подготовки, а этап меняет часы сам —
/// средняя скорость этапа не должна включать время другого этапа (файл сумм 167 Б и архив 44,7 МБ
/// качаются разное время, и общая средняя сказала бы про архив неправду).
/// </summary>
public sealed class UpdateProgressMeter
{
    private readonly Stopwatch _clock = new();
    private UpdateStage? _stage;

    /// <summary>Начать заново: новая попытка подготовки — новые часы.</summary>
    public void Reset()
    {
        _clock.Reset();
        _stage = null;
    }

    /// <summary>Снять снимок хода: объём, скорость за попытку этапа и остаток.</summary>
    public UpdateProgressView Sample(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (_stage != progress.Stage)
        {
            _clock.Restart();
            _stage = progress.Stage;
        }
        else if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        var rate = UpdateProgressMath.BytesPerSecond(progress.Done, _clock.Elapsed);

        return new UpdateProgressView(
            progress.Stage,
            progress.Name,
            progress.Done,
            progress.Total,
            rate,
            UpdateProgressMath.Remaining(progress.Total, progress.Done, rate));
    }
}
