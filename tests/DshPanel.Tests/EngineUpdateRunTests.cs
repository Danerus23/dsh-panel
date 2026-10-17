using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ИСПОЛНИТЕЛЬ ПУТИ ОБНОВЛЕНИЯ (пункт 4 плана E3.2).
///
/// Машину он трогает ЧЕРЕЗ ДВЕРИ, поэтому здесь проверяется то, что важнее всего и без живой машины:
/// **слова идут ДО дела**, **после дела одна строка**, и **ничего не начинается, если предыдущий
/// важный шаг не удался**. Последнее — не вежливость: «полуархива не бывает», и проверка смотрит,
/// что строк установки в журнале **просто нет**.
/// </summary>
public class EngineUpdateRunTests
{
    private static EngineHints Hints() => EngineHints.Parse(
        "{\"format\":1,\"versions\":[{\"version\":\"0.1.5-rc.2\",\"verified\":true,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

    private static EngineUpdatePlan Plan(int port = 3081) =>
        EngineUpdateFlow.Plan(Hints(), null, freshWanted: false, ownPort: port);

    private static EngineInstallResult Installed(string version) => new(
        EngineInstallOutcome.Installed, version, string.Empty, string.Empty, "поставлено", null);

    /// <summary>
    /// СЛОВА ДО ДЕЛА И ОДНА СТРОКА ПОСЛЕ: предупреждение о гашении стоит в журнале **раньше**, чем
    /// дверь погасила движок, а копия профиля — раньше установки. Двери это и фиксируют: каждая
    /// записывает в общий список, что уже было сказано к её приходу.
    /// </summary>
    [Fact]
    public void Слова_идут_до_дела_а_после_дела_одна_строка()
    {
        var said = new List<string>();
        var order = new List<string>();

        var doors = new EngineUpdateDoors(
            (port, step) => { order.Add($"стоп|сказано={string.Join(";", said)}"); return true; },
            (version, step) => { order.Add($"копия|сказано={string.Join(";", said)}"); return true; },
            (version, step) => { order.Add($"установка|сказано={string.Join(";", said)}"); return Installed(version); },
            (version, step) => { order.Add($"проба|сказано={string.Join(";", said)}"); return true; });

        var result = EngineUpdateRun.Run(Plan(), doors, said.Add);

        Assert.True(result.Ok, result.Detail);

        var stop = order.Single(line => line.StartsWith("стоп|", StringComparison.Ordinal));
        var copy = order.Single(line => line.StartsWith("копия|", StringComparison.Ordinal));
        var install = order.Single(line => line.StartsWith("установка|", StringComparison.Ordinal));

        // Перед гашением сказано предупреждение С ПОРТОМ, и оно раньше самого гашения.
        Assert.Contains("3081", stop, StringComparison.Ordinal);
        Assert.Contains("прервётся", stop, StringComparison.Ordinal);

        // Перед копией сказано, что снимаем, и раньше установки.
        Assert.Contains("копия", copy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("копия", install, StringComparison.OrdinalIgnoreCase);

        // После каждого дела — своя строка, и последняя говорит, что движок обновлён и проверен.
        Assert.Contains(result.Lines, line => line.Contains("погашен", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Lines, line => line.Contains("проба", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("обновлён", result.Lines[^1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠️ «ПОЛУАРХИВА НЕ БЫВАЕТ»: копия не снялась — **установки НЕ БЫЛО**. Проверка смотрит на
    /// журнал и на двери: строки установки в нём нет, и дверь установки не звалась.
    /// </summary>
    [Fact]
    public void Без_копии_установка_не_начинается()
    {
        var installed = false;

        var doors = new EngineUpdateDoors(
            (_, _) => true,
            (_, _) => false,
            (version, _) => { installed = true; return Installed(version); },
            (_, _) => true);

        var result = EngineUpdateRun.Run(Plan(), doors);

        Assert.False(result.Ok);
        Assert.Equal(EngineUpdateOutcome.CopyFailed, result.Outcome);
        Assert.False(installed, "установка пошла, хотя копии профиля нет");
        Assert.DoesNotContain(result.Lines, line => line.Contains("ставлю движок", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Lines, line => line.Contains("не начинаю", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>И движок не погасился — тоже не начинаем: подменять версию под работающим движком нельзя.</summary>
    [Fact]
    public void Без_гашения_движка_тоже_не_начинаем()
    {
        var copied = false;

        var doors = new EngineUpdateDoors(
            (_, _) => false,
            (_, _) => { copied = true; return true; },
            (version, _) => Installed(version),
            (_, _) => true);

        var result = EngineUpdateRun.Run(Plan(3080), doors);

        Assert.Equal(EngineUpdateOutcome.StopFailed, result.Outcome);
        Assert.False(copied, "копию снимали, хотя движок не погашен");
        Assert.Contains(result.Lines, line => line.Contains("не удалось", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ПРОБА НЕ ПРОШЛА — «ГОТОВО» ПАНЕЛЬ НЕ ГОВОРИТ. Поставлено, копия снята, но подтверждения нет:
    /// это `ProbeFailed`, а не `Updated` — иначе человек услышал бы «обновлено» про непроверенное.
    /// </summary>
    [Fact]
    public void Проба_не_прошла_значит_не_готово()
    {
        var doors = new EngineUpdateDoors(
            (_, _) => true,
            (_, _) => true,
            (version, _) => Installed(version),
            (_, _) => false);

        var result = EngineUpdateRun.Run(Plan(), doors);

        Assert.False(result.Ok);
        Assert.Equal(EngineUpdateOutcome.ProbeFailed, result.Outcome);
        Assert.DoesNotContain(result.Lines, line => line.Contains("обновлён и проверен", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Lines, line => line.Contains("НЕ прошла", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>План, который отказывает, до машины не доходит вовсе.</summary>
    [Fact]
    public void Отказ_плана_машину_не_трогает()
    {
        var touched = false;
        var refused = EngineUpdateFlow.Plan(null, "0.2.0-rc.2", freshWanted: false, ownPort: 3080);

        var doors = new EngineUpdateDoors(
            (_, _) => { touched = true; return true; },
            (_, _) => { touched = true; return true; },
            (version, _) => { touched = true; return Installed(version); },
            (_, _) => { touched = true; return true; });

        var result = EngineUpdateRun.Run(refused, doors);

        Assert.Equal(EngineUpdateOutcome.Refused, result.Outcome);
        Assert.False(touched, "при отказе плана панель всё равно тронула машину");
        Assert.Single(result.Lines);
    }
}
