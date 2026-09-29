using DshPanel.Backup;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Решение окна копий по вопросу «сервер работает — погасить?» — БЕЗ экрана и без сервера.
///
/// Эти проверки появились потому, что в окне это решение уже было неверным: «Погасить и продолжить»
/// ничего не гасило, но движку сообщалось, что сервер не работает, и оговорка «копия снята на ходу»
/// из отчёта пропадала. Найдено рабочим-подагентом 26.09.2026 при закрытии дыры покрытия у окна
/// <see cref="BackupStopWindow"/>; здесь закреплено ПОВЕДЕНИЕ, а не намерение.
/// </summary>
public class BackupStopDecisionsTests
{
    [Fact]
    public void Сервер_не_работал_вопрос_не_задаётся_и_оговорки_нет()
    {
        // Даже если каким-то чудом выбор пришёл, он ничего не значит: гасить нечего.
        foreach (var choice in new[] { StopChoice.Cancel, StopChoice.ContinueLive, StopChoice.StopAndContinue })
        {
            var outcome = BackupStopDecisions.Decide(serverRunning: false, choice, stopSucceeded: false);

            Assert.True(outcome.Proceed, "работа обязана идти: сервер не работает, мешать нечему — " + choice);
            Assert.False(outcome.LiveCopy, "оговорка «на ходу» без работающего сервера — ложь — " + choice);
        }
    }

    [Fact]
    public void Отказ_человека_останавливает_работу()
    {
        var outcome = BackupStopDecisions.Decide(serverRunning: true, StopChoice.Cancel, stopSucceeded: false);

        Assert.False(outcome.Proceed, "«Отмена» обязана значить «ничего не делать»");
    }

    [Fact]
    public void Копия_на_ходу_это_работа_и_честная_оговорка()
    {
        var outcome = BackupStopDecisions.Decide(serverRunning: true, StopChoice.ContinueLive, stopSucceeded: false);

        Assert.True(outcome.Proceed);
        Assert.True(outcome.LiveCopy, "человек выбрал копию на ходу — оговорка обязана быть в отчёте");
    }

    [Fact]
    public void Погасили_работа_идёт_и_оговорка_снимается()
    {
        var outcome = BackupStopDecisions.Decide(serverRunning: true, StopChoice.StopAndContinue, stopSucceeded: true);

        Assert.True(outcome.Proceed);
        Assert.False(outcome.LiveCopy, "сервер погашен — копия не «на ходу», и это можно утверждать");
    }

    /// <summary>
    /// ГЛАВНАЯ проверка этой работы. Сервер жив, человек просил погасить, погасить не удалось:
    /// работу не делаем и НЕ выдаём живую копию за цельную. Старое поведение было обратным —
    /// оговорка снималась, и отчёт утверждал то, чего не было.
    /// </summary>
    [Fact]
    public void Не_погасили_работа_не_делается_и_живая_копия_не_выдаётся_за_целую()
    {
        var outcome = BackupStopDecisions.Decide(serverRunning: true, StopChoice.StopAndContinue, stopSucceeded: false);

        Assert.False(outcome.Proceed, "обещание «копия получается целой» не выполнено — работу начинать нельзя");
        Assert.True(outcome.LiveCopy, "сервер работает — движок обязан назвать это в отчёте, а не молчать");
    }

    /// <summary>
    /// Разбор покрывает ВСЕ сочетания: ни один исход из перечня не оставлен без ответа.
    /// Если в перечне появится четвёртый исход, эта проверка обязана потребовать ответа и на него.
    /// </summary>
    [Fact]
    public void Разбор_отвечает_на_каждое_сочетание_перечня()
    {
        var answered = 0;

        foreach (var running in new[] { true, false })
        foreach (var choice in System.Enum.GetValues<StopChoice>())
        foreach (var stopped in new[] { true, false })
        {
            var outcome = BackupStopDecisions.Decide(running, choice, stopped);

            // «Делать работу» и «работа идёт по живому серверу» несовместимы только в одну сторону:
            // если работу не делаем, оговорка в отчёт не попадёт вовсе.
            if (!outcome.Proceed && !running)
                Assert.Fail($"отказ при неработающем сервере — это решение за человека: {choice}");

            if (outcome.Proceed && running && choice == StopChoice.StopAndContinue)
                Assert.False(outcome.LiveCopy, "работу делаем по просьбе «погасить» — значит погасили, и это проверяемо");

            answered++;
        }

        Assert.Equal(12, answered);
    }
}
