using System;
using DshPanel.Backup;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧАСЫ АВТОКОПИЙ — чистое решение «пора или нет» (v2.2). Проверяется ЗДЕСЬ и только здесь,
/// потому что от него зависит, снимется ли копия вообще: ошибка в одну сторону оставляет человека
/// без копий (и он узнает об этом, когда потеряет работу), ошибка в другую — долбит диск копиями.
///
/// Все случаи взяты из жизни, а не из кода: панель выключали на неделю, часы переводили назад,
/// папка копий была недоступна (попытка не удалась), копий ещё не было вовсе.
/// </summary>
public class BackupScheduleTests
{
    /// <summary>«Сейчас» для проверок: местное время, как и всё в расписании.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3));

    // --- выключено и впервые -------------------------------------------------

    [Fact]
    public void Выключенное_расписание_не_снимает_копию_и_говорит_об_этом()
    {
        var due = BackupSchedule.Due(enabled: false, 24, Now.AddHours(-100), Now);

        Assert.False(due.Take);
        Assert.Equal(PanelStrings.BackupScheduleOff, due.Reason);
    }

    /// <summary>
    /// Копий ещё не было — снимаем первую. Это первый запуск панели у человека и обычный случай
    /// после смены папки копий: ждать сутки было бы неправильно.
    /// </summary>
    [Fact]
    public void Копий_ещё_не_было_снимаем_первую()
    {
        var due = BackupSchedule.Due(enabled: true, 24, lastCopy: null, Now);

        Assert.True(due.Take);
        Assert.Equal(PanelStrings.BackupScheduleNoCopies, due.Reason);
    }

    // --- пора и рано ---------------------------------------------------------

    [Fact]
    public void Прошло_больше_интервала_пора()
    {
        var due = BackupSchedule.Due(enabled: true, 24, Now.AddHours(-25), Now);

        Assert.True(due.Take);
        Assert.Contains("25", due.Reason, StringComparison.Ordinal);
        Assert.Contains("24", due.Reason, StringComparison.Ordinal);
        Assert.Equal(
            string.Format(PanelStrings.BackupScheduleDueFormat, "25 ч", "24 ч"),
            due.Reason);
    }

    [Fact]
    public void Прошло_меньше_интервала_рано()
    {
        var due = BackupSchedule.Due(enabled: true, 24, Now.AddHours(-3), Now);

        Assert.False(due.Take);
        Assert.Equal(
            string.Format(PanelStrings.BackupScheduleEarlyFormat, "3 ч", "24 ч"),
            due.Reason);
    }

    /// <summary>
    /// Ровно интервал — уже пора: «прошло 24 ч из 24» обязано снимать копию, иначе на границе
    /// расписание зависало бы на один такт часов.
    /// </summary>
    [Fact]
    public void Ровно_интервал_это_уже_пора()
    {
        Assert.True(BackupSchedule.Due(enabled: true, 24, Now.AddHours(-24), Now).Take);
        Assert.False(BackupSchedule.Due(enabled: true, 24, Now.AddHours(-24).AddMinutes(1), Now).Take);
    }

    /// <summary>
    /// Панель была выключена, и копия просрочена: при запуске она снимается СРАЗУ, а причина
    /// называет это словами — «догоняем пропущенное». Иначе выглядело бы случайностью.
    /// </summary>
    [Fact]
    public void Панель_только_запустилась_и_догоняет_пропущенное()
    {
        var due = BackupSchedule.Due(enabled: true, 24, Now.AddHours(-72), Now, justStarted: true);

        Assert.True(due.Take);
        Assert.EndsWith(PanelStrings.BackupScheduleStartedSuffix, due.Reason, StringComparison.Ordinal);
        Assert.Contains("72", due.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Флаг «только что запустилась» на РЕШЕНИЕ не влияет: интервал есть интервал. Он добавляет
    /// слова к причине, и на «рано» их не добавляет — иначе «рано» выглядело бы как «пора».
    /// </summary>
    [Fact]
    public void Запуск_панели_не_отменяет_интервал()
    {
        var due = BackupSchedule.Due(enabled: true, 24, Now.AddHours(-3), Now, justStarted: true);

        Assert.False(due.Take);
        Assert.DoesNotContain(PanelStrings.BackupScheduleStartedSuffix, due.Reason, StringComparison.Ordinal);
    }

    // --- часы переводили -----------------------------------------------------

    /// <summary>
    /// Прошлая копия помечена БУДУЩИМ временем: часы перевели назад (или сменили пояс). Ждать
    /// «минус три часа из суток» значило бы не снять копию вовсе — поэтому «пора», и причина
    /// называет, почему решение такое.
    /// </summary>
    [Fact]
    public void Прошлая_копия_в_будущем_снимаем_сейчас()
    {
        var due = BackupSchedule.Due(enabled: true, 24, Now.AddHours(3), Now);

        Assert.True(due.Take);
        Assert.Contains("26.09.2026", due.Reason, StringComparison.Ordinal);
    }

    // --- защита от долбёжки --------------------------------------------------

    /// <summary>
    /// Попытка только что не удалась — второй раз не долбим: копия в 167 МБ на неудаче каждые
    /// пять минут — это не забота, а издевательство над диском.
    /// </summary>
    [Fact]
    public void После_неудачной_попытки_ждём_сторож()
    {
        var due = BackupSchedule.Due(
            enabled: true, 24, Now.AddHours(-30), Now, lastAttempt: Now.AddMinutes(-5));

        Assert.False(due.Take);
        Assert.Contains("5", due.Reason, StringComparison.Ordinal);
        Assert.Contains("6 ч", due.Reason, StringComparison.Ordinal);
    }

    /// <summary>Сторож кончился — попытка снова разрешена.</summary>
    [Fact]
    public void Сторож_кончился_попытка_разрешена()
    {
        Assert.True(BackupSchedule
            .Due(enabled: true, 24, Now.AddHours(-30), Now, lastAttempt: Now.AddHours(-7))
            .Take);

        // Граница: ровно сторож — уже можно (сверка идёт на «меньше сторожа»).
        Assert.True(BackupSchedule
            .Due(enabled: true, 24, Now.AddHours(-30), Now, lastAttempt: Now.AddHours(-6))
            .Take);
    }

    /// <summary>
    /// Сторож не трогает «рано»: там решение и без него «рано», и подменять причину нельзя —
    /// человек по причине понимает, чего ждёт панель.
    /// </summary>
    [Fact]
    public void Сторож_не_подменяет_причину_рано()
    {
        var due = BackupSchedule.Due(
            enabled: true, 24, Now.AddHours(-3), Now, lastAttempt: Now.AddMinutes(-1));

        Assert.False(due.Take);
        Assert.Equal(
            string.Format(PanelStrings.BackupScheduleEarlyFormat, "3 ч", "24 ч"),
            due.Reason);
    }

    /// <summary>
    /// Часы перевели назад ПОСЛЕ попытки: разность отрицательная, и без сторожа копия снималась бы
    /// на каждом такте часов — то есть круглые сутки.
    /// </summary>
    [Fact]
    public void Часы_перевели_назад_после_попытки_сторож_держит()
    {
        var due = BackupSchedule.Due(
            enabled: true, 24, Now.AddHours(-30), Now, lastAttempt: Now.AddHours(2));

        Assert.False(due.Take);
    }

    /// <summary>Границы сторожа: четверть интервала, зажатая между 15 минутами и 6 часами.</summary>
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(4, 60)]
    [InlineData(24, 360)]
    [InlineData(48, 360)]
    [InlineData(168, 360)]
    public void Сторож_это_четверть_интервала_в_зажатых_границах(int hours, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), BackupSchedule.RetryGuard(hours));
    }

    /// <summary>Мусор в интервале не должен давать ни нулевого сторожа, ни деления на ноль.</summary>
    [Fact]
    public void Мусор_в_интервале_не_ломает_сторож()
    {
        Assert.Equal(BackupSchedule.MinRetry, BackupSchedule.RetryGuard(0));
        Assert.Equal(BackupSchedule.MinRetry, BackupSchedule.RetryGuard(-5));
    }

    // --- промежуток словами --------------------------------------------------

    [Theory]
    [InlineData(0, 40, "40 мин")]
    [InlineData(3, 0, "3 ч")]
    [InlineData(3, 5, "3 ч 5 мин")]
    [InlineData(25, 0, "25 ч")]
    [InlineData(25, 30, "25 ч 30 мин")]
    public void Промежуток_называется_словами(int hours, int minutes, string expected)
    {
        Assert.Equal(expected, BackupSchedule.Span(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>
    /// Меньше минуты (и «минус» от перевода часов) — «меньше минуты», а не «0 мин»: ноль минут
    /// читался бы как ошибка счёта, а попытка снять копию бывает и за секунды до такта часов.
    /// Строка берётся ОБЩАЯ — та же, которой говорит панель о пиках.
    /// </summary>
    [Fact]
    public void Промежуток_меньше_минуты_называется_словами()
    {
        Assert.Equal(PanelStrings.SpanLessMinute, BackupSchedule.Span(TimeSpan.Zero));
        Assert.Equal(PanelStrings.SpanLessMinute, BackupSchedule.Span(TimeSpan.FromSeconds(30)));
        Assert.Equal(PanelStrings.SpanLessMinute, BackupSchedule.Span(TimeSpan.FromHours(-3)));
    }
}
