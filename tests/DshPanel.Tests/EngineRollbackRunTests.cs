using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ИСПОЛНИТЕЛЬ ОТКАТА (пункт 4 плана E3.3): порядок и последствия.
///
/// **Порядок здесь — не вкус, а защита, и каждая проверка стережёт свою половину:**
///
/// * прежняя версия проверяется **живьём ДО всякой подмены** — вернуться в нерабочее нельзя;
/// * свой движок гасится **после** пробы (погасить и не суметь поднять — худший исход);
/// * запись «стоит сейчас» переписывается **последней**, и не переписалась — панель остаётся
///   на прежней (текущей) версии.
///
/// Здесь всё идёт **подставными дверями**: ни одного процесса, ни одного порта, ни одной установки.
/// </summary>
public class EngineRollbackRunTests
{
    private static EngineRollbackPlan Plan(string current = "0.2.0-rc.2", string target = "0.1.5-rc.2") =>
        new(true, current, target, EngineRollbackRefusal.None, string.Empty);

    private sealed class Stand
    {
        public List<string> Order { get; } = new();
        public List<int> StopPorts { get; } = new();

        public bool Probe { get; set; } = true;
        public bool Stop { get; set; } = true;
        public bool Record { get; set; } = true;

        public EngineRollbackDoors Doors() => new(
            StopOwnEngine: port =>
            {
                Order.Add("погасить " + port);
                StopPorts.Add(port);
                return Stop;
            },
            Probe: version =>
            {
                Order.Add("проба " + version);
                return Probe;
            },
            WriteRecord: version =>
            {
                Order.Add("запись " + version);
                return Record;
            });
    }

    /// <summary>ВСЁ ПРОШЛО: порядок — проба, гашение, запись; версия в итоге — прежняя.</summary>
    [Fact]
    public void Откат_проходит_целиком_и_в_правильном_порядке()
    {
        var log = new List<string>();
        var stand = new Stand();

        var result = EngineRollbackRun.Run(Plan(), ownPort: 3081, stand.Doors(), log.Add);

        Assert.True(result.Ok);
        Assert.Equal(EngineRollbackOutcome.RolledBack, result.Outcome);
        Assert.Equal("0.1.5-rc.2", result.Version);
        Assert.Equal(new[] { "проба 0.1.5-rc.2", "погасить 3081", "запись 0.1.5-rc.2" }, stand.Order);

        // Последняя строка — одна строка ПОСЛЕ дела, и она говорит про сессии: их журнал новее.
        Assert.Contains("0.1.5-rc.2", log[^1], StringComparison.Ordinal);
        Assert.Contains("0.2.0-rc.2", log[^1], StringComparison.Ordinal);
        Assert.Contains("сессии", log[^1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠️ ПРЕЖНЯЯ ВЕРСИЯ НЕ ПОДНЯЛАСЬ — НИЧЕГО НЕ МЕНЯЕМ: ни гашения, ни записи. Так «вернулись
    /// в нерабочее» становится невозможным.
    /// </summary>
    [Fact]
    public void Проба_не_прошла_значит_ничего_не_меняли()
    {
        var stand = new Stand { Probe = false };

        var result = EngineRollbackRun.Run(Plan(), ownPort: 3081, stand.Doors());

        Assert.False(result.Ok);
        Assert.Equal(EngineRollbackOutcome.ProbeFailed, result.Outcome);
        Assert.Equal("0.2.0-rc.2", result.Version);
        Assert.Equal(new[] { "проба 0.1.5-rc.2" }, stand.Order);
        Assert.Contains(result.Lines, line => line.Contains("остаёмся на версии 0.2.0-rc.2", StringComparison.Ordinal));
    }

    /// <summary>СВОЙ ДВИЖОК НЕ ПОГАС — ЗАПИСЬ НЕ ТРОГАЕМ: иначе панель записала бы версию, которой не работает.</summary>
    [Fact]
    public void Движок_не_погас_значит_запись_не_трогали()
    {
        var stand = new Stand { Stop = false };

        var result = EngineRollbackRun.Run(Plan(), ownPort: 3081, stand.Doors());

        Assert.Equal(EngineRollbackOutcome.StopFailed, result.Outcome);
        Assert.Equal(new[] { "проба 0.1.5-rc.2", "погасить 3081" }, stand.Order);
        Assert.Equal(new[] { 3081 }, stand.StopPorts);
        Assert.Contains(result.Lines, line => line == EngineInstallPhrases.UpdateStopFailed());
    }

    /// <summary>ЗАПИСЬ НЕ ПЕРЕПИСАЛАСЬ — панель остаётся на текущей версии, и это сказано словами.</summary>
    [Fact]
    public void Запись_не_переписалась_остаёмся_на_текущей()
    {
        var stand = new Stand { Record = false };

        var result = EngineRollbackRun.Run(Plan(), ownPort: 3081, stand.Doors());

        Assert.Equal(EngineRollbackOutcome.RecordFailed, result.Outcome);
        Assert.Equal("0.2.0-rc.2", result.Version);
        Assert.Equal(new[] { "проба 0.1.5-rc.2", "погасить 3081", "запись 0.1.5-rc.2" }, stand.Order);
        Assert.Contains(result.Lines, line => line.Contains("запись не переписалась", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ОТКАЗ «НЕКУДА» МАШИНУ НЕ ТРОГАЕТ ВОВСЕ: ни одной двери не спросили.</summary>
    [Fact]
    public void Отказ_машину_не_трогает()
    {
        var stand = new Stand();
        var plan = new EngineRollbackPlan(false, "0.2.0-rc.2", string.Empty, EngineRollbackRefusal.NoPrevious, EngineRollbackDecisions.Words.NoPrevious);

        var result = EngineRollbackRun.Run(plan, ownPort: 3081, stand.Doors());

        Assert.Equal(EngineRollbackOutcome.Refused, result.Outcome);
        Assert.Empty(stand.Order);
        Assert.Contains(result.Lines, line => line == EngineInstallPhrases.UpdateRefusal(EngineRollbackDecisions.Words.NoPrevious));
    }

    /// <summary>
    /// СЛОВА ДО ДЕЛА И ОДНА СТРОКА ПОСЛЕ: у каждого важного шага сначала слово, потом дело,
    /// и после — одна строка о том, что сделано (решение владельца «важная магия — с предупреждением»).
    /// </summary>
    [Fact]
    public void Слова_идут_до_дела_а_после_дела_одна_строка()
    {
        var log = new List<string>();
        var stand = new Stand();

        EngineRollbackRun.Run(Plan(), ownPort: 3081, stand.Doors(), log.Add);

        var probeWord = log.FindIndex(line => line.Contains("проверяю живьём", StringComparison.OrdinalIgnoreCase));
        var stopWord = log.FindIndex(line => line == EngineInstallPhrases.UpdateStopped());
        var recordWord = log.FindIndex(line => line.Contains("переписываю запись", StringComparison.OrdinalIgnoreCase));
        var done = log.Count - 1;

        Assert.True(probeWord >= 0, "слова перед пробой нет");
        Assert.True(stopWord >= 0, "строки после гашения нет");
        Assert.True(recordWord >= 0, "слов перед записью нет");
        Assert.True(probeWord < stopWord && stopWord < recordWord && recordWord < done,
            "слова и дела идут не в том порядке: " + string.Join(" | ", log));
    }
}
