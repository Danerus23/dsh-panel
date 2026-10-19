using System;
using System.IO;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВА ЗАМКА ДВИЖКА В ОДНОЙ СБОРКЕ (задача `t4` плана 2.2.0, блок B).
///
/// **Зачем их два.** Переезд на 0.2.x означает ДВЕ версии на диске (решение владельца: держим
/// текущую и прежнюю), а ставить каждую надо по своему набору: у `0.1.5-rc.2` — **588** пакетов,
/// у `0.2.1-rc.2` — **683**. Один замок другую версию не поставит, и «замок есть» обязано значить
/// «есть для ЭТОЙ версии».
///
/// **Что здесь стережётся.** Оба замка читаются из сборки и КАЖДЫЙ сверяется со СВОЕЙ закреплённой
/// суммой; суммы разные и перекрёстно не годятся (иначе подмена ресурса прошла бы молча); поиск идёт
/// правилом панели (числовая часть версии) и чужие версии не «сходят»; числа пакетов названы —
/// они же и есть доказательство, что замки сняты с РАЗНЫХ наборов.
/// </summary>
public class EngineTreeLocksTests
{
    /// <summary>ОБА ВСТРОЕННЫХ ЗАМКА НА МЕСТЕ И ОБА ГОДНЫ: целы, с суммами, с официального реестра.</summary>
    [Fact]
    public void Встроенных_замков_два_и_оба_годны()
    {
        var locks = EngineTreeLocks.Load();

        Assert.True(locks.Any, "ни один встроенный замок не прочитался — проверка была бы слепой");
        Assert.Equal(2, locks.All.Count);

        // Старшая версия первой: глазом читается «текущая/прежняя», на решения это не влияет.
        Assert.Equal(
            new[] { EngineRpcSurface.KnownOnVersion, EngineRpcSurface.MeasuredOnVersion },
            locks.Versions.ToArray());

        Assert.All(locks.All, known =>
        {
            Assert.True(known.Complete, $"замок {known.EngineVersion} обрезан");
            Assert.True(known.FullySummed, $"у замка {known.EngineVersion} не у всех записей есть сумма");
            Assert.True(known.AllFromRegistry, $"в замке {known.EngineVersion} есть пакет не с официального реестра");
        });

        // Числа пакетов — доказательство, что наборы РАЗНЫЕ, а не один и тот же файл дважды.
        Assert.Equal(588, locks.For(EngineRpcSurface.MeasuredOnVersion).Declared);
        Assert.Equal(683, locks.For(EngineRpcSurface.KnownOnVersion).Declared);
    }

    /// <summary>
    /// ГЛАВНЫЙ ЗАМОК — НАШ (тот, на котором панель мерилась): от него живут главная кнопка обновления,
    /// порог Node и все прежние двери. Второй замок его не подменяет.
    /// </summary>
    [Fact]
    public void Главный_замок_это_тот_на_котором_панель_мерилась()
    {
        var locks = EngineTreeLocks.Load();

        Assert.Equal(EngineRpcSurface.MeasuredOnVersion, locks.Primary.EngineVersion);
        Assert.Equal(EngineTreeLock.PinnedSum, EngineTreeLock.PinnedSumOf(locks.Primary.EngineVersion));
    }

    /// <summary>
    /// ЗАМОК ИЩЕТСЯ ПО ВЕРСИИ — правилом панели (числовая часть) — и НЕ находится для чужой.
    /// Пустая версия и пустой набор не открывают ничего: «замка нет» — это ответ, а не догадка.
    /// </summary>
    [Fact]
    public void Замок_ищется_по_версии_и_чужая_не_сходится()
    {
        var locks = EngineTreeLocks.Load();

        Assert.True(locks.Has(EngineRpcSurface.KnownOnVersion));
        Assert.True(locks.Has(EngineRpcSurface.MeasuredOnVersion));

        // ⚠️ Правило панели: числовая часть БЕЗ предрелизного хвоста — значит «0.2.1» и «0.2.1-rc.2»
        // РАЗНЫЕ версии (то же правило сторожит `EngineLockForVersionTests`). Замок, снятый
        // с предрелиза, «чистую» версию не открывает — и это не дыра, а честность замера.
        Assert.Equal(string.Empty, locks.For("0.2.1").EngineVersion);
        Assert.False(locks.Has("0.2.1"));

        // Соседние версии и вовсе чужие — не сходятся.
        Assert.False(locks.Has("0.2.0-rc.2"), "замка для 0.2.0 в сборке нет — и он не должен «находиться»");
        Assert.False(locks.Has("0.9.9"));
        Assert.False(locks.Has(string.Empty));
        Assert.False(locks.Has(null));

        Assert.False(EngineTreeLocks.Empty.Has(EngineRpcSurface.KnownOnVersion));
        Assert.False(EngineTreeLocks.Empty.Any);
    }

    /// <summary>
    /// СУММА — У КАЖДОГО ЗАМКА СВОЯ, и перекрёстно они не годятся: подмена одного ресурса другим
    /// (или правка файла без правки суммы) обязана кончиться «этого замка нет», а не установкой
    /// неизвестно чего. Заодно проверяется, что файлы в дереве и закреплённые суммы — одно и то же.
    /// </summary>
    [Fact]
    public void Суммы_замков_разные_и_перекрёстно_не_годятся()
    {
        var repository = RepositoryRoot();
        var ours = File.ReadAllText(Path.Combine(repository, "engine-lock.json"));
        var known = File.ReadAllText(Path.Combine(repository, "engine-lock-0.2.1-rc.2.json"));

        Assert.True(EngineTreeLock.MatchesPinned(ours, EngineRpcSurface.MeasuredOnVersion));
        Assert.True(EngineTreeLock.MatchesPinned(known, EngineRpcSurface.KnownOnVersion));

        // Перекрёстно — НЕ годятся: у каждого замка своя строка в закреплённых суммах.
        Assert.False(EngineTreeLock.MatchesPinned(ours, EngineRpcSurface.KnownOnVersion));
        Assert.False(EngineTreeLock.MatchesPinned(known, EngineRpcSurface.MeasuredOnVersion));

        // Один испорченный байт — и замка нет.
        Assert.False(EngineTreeLock.MatchesPinned(
            known.Replace("0.2.1-rc.2", "0.2.1-rc.9", StringComparison.Ordinal),
            EngineRpcSurface.KnownOnVersion));

        // Закреплённые суммы названы, различаются и не выдумываются для версий без замка.
        Assert.Equal(EngineTreeLock.PinnedSum, EngineTreeLock.PinnedSumOf(EngineRpcSurface.MeasuredOnVersion));
        Assert.Equal(EngineTreeLock.PinnedSumOnKnown, EngineTreeLock.PinnedSumOf(EngineRpcSurface.KnownOnVersion));
        Assert.NotEqual(EngineTreeLock.PinnedSum, EngineTreeLock.PinnedSumOnKnown);
        Assert.Equal(string.Empty, EngineTreeLock.PinnedSumOf("0.2.0-rc.2"));
        Assert.Equal(string.Empty, EngineTreeLock.PinnedSumOf(null));
    }

    /// <summary>
    /// КАЖДЫЙ ЗАМОК ОПИСЫВАЕТ СВОЙ ДВИЖОК: версия пакета в замке — та же, что объявлена в оболочке.
    /// Это стережёт подмену набора «похожим» файлом: разбор такой замок отвергает, а здесь
    /// проверяется, что оба наших — про те версии, которые панель и держит.
    /// </summary>
    [Fact]
    public void Каждый_замок_про_свой_движок()
    {
        var locks = EngineTreeLocks.Load();

        foreach (var expected in new[] { EngineRpcSurface.MeasuredOnVersion, EngineRpcSurface.KnownOnVersion })
        {
            var known = locks.For(expected);

            Assert.True(known.Any, $"встроенного замка для {expected} нет");
            Assert.Equal(expected, known.EngineVersion);
            Assert.Equal("@deepseek-ai/dsh", known.EngineName);
            Assert.Equal(expected, known.Find("@deepseek-ai/dsh")?.Version);
        }
    }

    /// <summary>
    /// ПОРОГ NODE — НЕ СЛАБЕЕ САМОГО СТРОГОГО ИЗ ДВУХ ЗАМКОВ. Один Node обслуживает ОБЕ версии,
    /// которые панель держит на диске; если брать порог только у той, что ставим сейчас, откат мог бы
    /// поднять прежнюю версию на Node, которого её замок не допускает.
    /// </summary>
    [Fact]
    public void Порог_Node_берётся_по_самому_строгому_замку()
    {
        var locks = EngineTreeLocks.Load();

        var need = EngineNode.StrictestFrom(locks);

        Assert.False(string.IsNullOrWhiteSpace(need), "оба замка молчат о Node — такого быть не может");
        Assert.True(EngineNode.StrictestFrom((EngineTreeLocks?)null).Length == 0);

        // Каждый замок требует НЕ БОЛЬШЕ общего порога — иначе выбор был бы слабее нужного.
        Assert.All(locks.All, known => Assert.True(EngineNode.Satisfies(need, EngineNode.RequiredFrom(known))));

        // Источник порога — тот замок, который требует больше всех, и он годится под этот порог.
        var strictest = EngineNode.StrictestLock(locks);

        Assert.True(strictest.Any);
        Assert.Equal(need, EngineNode.StrictestFrom(strictest));
        Assert.True(EngineNode.Fits(need, strictest));
    }
    /// <summary>
    /// САМЫЙ СТРОГИЙ ПОРОГ — ИЗ ДВУХ РАЗНЫХ ТРЕБОВАНИЙ. У настоящих замков порог совпал (`&gt;=22.19.0`),
    /// и «взять первый попавшийся» прошло бы молча; здесь требования РАЗНЫЕ, и порог обязан быть
    /// старшим — иначе свой Node мог бы не годиться для одной из версий.
    /// </summary>
    [Fact]
    public void Порог_Node_берёт_старшее_из_разных_требований()
    {
        var mild = Mini("0.1.5-rc.2", ">=20.0.0");
        var strict = Mini("0.2.1-rc.2", ">=22.19.0");
        var locks = new EngineTreeLocks(new[] { mild, strict });

        Assert.Equal("22.19.0", EngineNode.StrictestFrom(locks));
        Assert.Equal("22.19.0", EngineNode.RequiredFrom(EngineNode.StrictestLock(locks)));
        Assert.True(EngineNode.Fits("22.19.0", EngineNode.StrictestLock(locks)));
        Assert.False(EngineNode.Fits("22.11.0", EngineNode.StrictestLock(locks)));

        // Обратный порядок в наборе ничего не меняет: решает требование, а не место в списке.
        var reversed = new EngineTreeLocks(new[] { strict, mild });
        Assert.Equal("22.19.0", EngineNode.StrictestFrom(reversed));
    }

    /// <summary>Короткий замок с объявленным порогом Node — для проверки выбора порога.</summary>
    private static EngineTreeLock Mini(string version, string node) => EngineTreeLock.Parse(
        "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"" + version + "\"},\"packages\":2," +
        "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
        "\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":\"" + version + "\"}}," +
        "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"" + version + "\",\"integrity\":\"sha512-A\"," +
        "\"resolved\":\"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-" + version + ".tgz\",\"engines\":{\"node\":\"" + node + "\"}}," +
        "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-B\"," +
        "\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}}}}");
    /// <summary>Корень репозитория — вверх от каталога сборки по файлу <c>DshPanel.slnx</c>.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DshPanel.slnx"))) return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        return string.Empty;
    }
}
