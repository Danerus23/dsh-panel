using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТКАТ ПО КНОПКЕ — КУДА ВОЗВРАЩАЕМСЯ (пункт 4 плана E3.3, чистое решение).
///
/// **Откат — это не установка.** Панель держит текущую и прежнюю версию (решение владельца
/// 10.10.2026), поэтому возврат — подъём сервера из соседней папки и запись «стоит сейчас» про неё.
/// В замке второй версии для этого не нужно.
///
/// Три правила выбора, и все три — из красных линий: только то, что ставила ПАНЕЛЬ; только то, что
/// ЕСТЬ на диске; самая НОВАЯ из прежних.
/// </summary>
public class EngineRollbackDecisionsTests
{
    private static EngineInstallRecord Record(string current, params string[] installed)
    {
        var known = installed.Length > 0
            ? installed.ToList()
            : new List<string> { current };

        if (!known.Contains(current, StringComparer.OrdinalIgnoreCase)) known.Add(current);

        return new EngineInstallRecord(current, "C:\\проба\\engine\\" + current, "22.23.3", "C:\\проба\\node.exe", true, known);
    }

    /// <summary>ВОЗВРАЩАЕМСЯ К САМОЙ НОВОЙ ИЗ ПРЕЖНИХ — не к первой в списке и не к самой старой.</summary>
    [Fact]
    public void Возвращаемся_к_самой_новой_из_прежних()
    {
        var record = Record("0.2.0-rc.2", "0.1.0", "0.1.4", "0.1.5-rc.2");
        var folders = new[] { "0.1.0", "0.1.4", "0.1.5-rc.2", "0.2.0-rc.2" };

        var plan = EngineRollbackDecisions.Decide(record, folders);

        Assert.True(plan.Any, plan.RefusalKey);
        Assert.Equal("0.2.0-rc.2", plan.Current);
        Assert.Equal("0.1.5-rc.2", plan.Target);
    }

    /// <summary>
    /// ⚠️ ЧУЖАЯ ПАПКА В ОТКАТ НЕ ПОПАДАЕТ: панель возвращается только к тому, что СТАВИЛА САМА.
    /// Папка, заведённая человеком (или другой программой), — не наша установка, и трогать её нельзя.
    /// </summary>
    [Fact]
    public void Чужая_папка_в_откат_не_попадает()
    {
        var record = Record("0.2.0-rc.2", "0.1.5-rc.2");

        // «0.9.9» лежит на диске и даже «новее» — но панель её не ставила.
        var plan = EngineRollbackDecisions.Decide(record, new[] { "0.1.5-rc.2", "0.2.0-rc.2", "0.9.9" });

        Assert.True(plan.Any, plan.RefusalKey);
        Assert.Equal("0.1.5-rc.2", plan.Target);
    }

    /// <summary>ПРЕЖНЕЙ ВЕРСИИ НА ДИСКЕ НЕТ — «возвращаться некуда», а не подъём в пустоту.</summary>
    [Fact]
    public void Прежней_версии_на_диске_нет_возвращаться_некуда()
    {
        var record = Record("0.2.0-rc.2", "0.1.5-rc.2");

        var plan = EngineRollbackDecisions.Decide(record, new[] { "0.2.0-rc.2" });

        Assert.False(plan.Any);
        Assert.Equal(EngineRollbackDecisions.Words.NoPrevious, plan.RefusalKey);
        Assert.Equal(EngineRollbackRefusal.NoPrevious, plan.Refusal);
    }

    /// <summary>ЗАПИСИ НЕТ — панель не знает, что ставила, и возвращаться некуда.</summary>
    [Fact]
    public void Записи_нет_возвращаться_некуда()
    {
        var folders = new[] { "0.1.5-rc.2", "0.2.0-rc.2" };

        var missing = EngineRollbackDecisions.Decide(null, folders);
        Assert.False(missing.Any);
        Assert.Equal(EngineRollbackDecisions.Words.NoRecord, missing.RefusalKey);
        Assert.Equal(EngineRollbackRefusal.NoRecord, missing.Refusal);

        var empty = EngineRollbackDecisions.Decide(EngineInstallRecord.Empty, folders);
        Assert.False(empty.Any);
        Assert.Equal(EngineRollbackDecisions.Words.NoRecord, empty.RefusalKey);

        // Запись есть, но папок не назвали вовсе — возвращаться некуда (а не «поставим в пустоту»).
        var nowhere = EngineRollbackDecisions.Decide(Record("0.2.0-rc.2", "0.1.5-rc.2"), null);
        Assert.False(nowhere.Any);
        Assert.Equal(EngineRollbackDecisions.Words.NoPrevious, nowhere.RefusalKey);
    }

    /// <summary>ТЕКУЩАЯ ВЕРСИЯ ЦЕЛЬЮ НЕ БЫВАЕТ: «вернуться туда, где стоим» — не откат.</summary>
    [Fact]
    public void Текущая_версия_целью_не_бывает()
    {
        var record = Record("0.1.5-rc.2", "0.1.5-rc.2");

        var plan = EngineRollbackDecisions.Decide(record, new[] { "0.1.5-rc.2" });

        Assert.False(plan.Any);
        Assert.Equal(EngineRollbackDecisions.Words.NoPrevious, plan.RefusalKey);
    }
}
