using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РЕГРЕССИЯ ПОВЕРХНОСТИ ДВИЖКА (блок E плана 2.2.0).
///
/// **Зачем отдельный класс.** С блоком A у панели стало **ДВА снимка** поверхности: наша линия
/// (0.1.5-rc.2: 84 объявленных, из них 77 нужных и 7 снятых с надобности) и линия переезда
/// (0.2.1-rc.2: 144 объявленных = 77 нужных + 67 знакомых новых в 16 семействах). Числа и списки
/// живут в ОДНОМ месте (<see cref="EngineRpcSurface"/>), но именно поэтому они и уязвимы: список
/// можно поправить «на один пункт», и тогда перестанут сходиться все четыре счёта сразу, а проба
/// начнёт либо падать на живом движке, либо пропускать потерю.
///
/// **Что здесь стерегутся — арифметика снимка, а не её сегодняшнее значение:**
/// * 84 = 77 + 7, и список нужных — это ровно снимок МИНУС снятые (а не отдельный список, который
///   однажды разойдётся со снимком);
/// * 144 = 77 + 67, и семейств у знакомых новых ровно 16;
/// * в снимке нет повторов, в снятых и знакомых новых — тоже, и **ни одно имя не стоит в двух
///   списках сразу**: имя, снятое с надобности, не может одновременно считаться нужным;
/// * **чужие имена** (то, чего нет ни где) правило `UnknownAdded` называет, а знакомые новые —
///   нет: иначе «незнакомое имя у движка» перестало бы ронять пробу;
/// * семейства знакомых новых — те же, что у самих имён, и их число совпадает с объявленным.
///
/// ⚠️ **Почему числа в проверке — литералы, а не константы продукта.** Проверка, читающая ожидание
/// из того же места, где лежит факт, не может упасть от правки факта (на этом в блоке D выжила
/// мутация «сменить префикс копии»). Здесь литералы — это и есть договор: 84/77/7/144/67/16.
///
/// **Мутации, которые обязаны упасть здесь:** «убрать имя из снятых» · «добавить имя в знакомые
/// новые» · «сделать снятое имя нужным» · «сломать семейства» · «вернуть снятое имя в пробу
/// (Required)».
/// </summary>
public class EngineSurfaceRegressionTests
{
    /// <summary>
    /// СНИМОК НАШЕЙ ЛИНИИ: 84 = 77 нужных + 7 снятых, и нужные — это РОВНО разность, а не второй
    /// список. Проверяется и то, что снимок не пуст: пустой «сходится» с чем угодно.
    /// </summary>
    [Fact]
    public void Снимок_нашей_линии_сходится_в_четырёх_числах()
    {
        Assert.Equal(84, EngineRpcSurface.MeasuredCount);
        Assert.Equal(84, EngineRpcSurface.Methods.Count);
        Assert.Equal(7, EngineRpcSurface.Retired.Count);
        Assert.Equal(77, EngineRpcSurface.RequiredTotal);

        Assert.Equal(EngineRpcSurface.MeasuredCount, EngineRpcSurface.RequiredTotal + EngineRpcSurface.Retired.Count);

        // Нужные — ЭТО СНИМОК МИНУС СНЯТЫЕ, и ни одного второго списка: проверяется по именам.
        var expected = EngineRpcSurface.Methods
            .Select(method => method.Endpoint)
            .Where(name => !EngineRpcSurface.Retired.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, EngineRpcSurface.Required.Select(m => m.Endpoint).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// СНИМОК ЛИНИИ ПЕРЕЕЗДА: 144 = 77 + 67, семейств 16, и каждое имя из «знакомых новых» лежит
    /// в одном из этих семейств. Проба при этом зовёт ТОЛЬКО нужные 77 — потерю семи она называет
    /// строкой, а не провалом, и это записано отдельным фактом ниже.
    /// </summary>
    [Fact]
    public void Снимок_линии_переезда_сходится_и_зовёт_только_нужные()
    {
        Assert.Equal("0.2.1-rc.2", EngineRpcSurface.KnownOnVersion);
        Assert.Equal(144, EngineRpcSurface.KnownOnDeclaredCount);
        Assert.Equal(67, EngineRpcSurface.KnownAddedCount);
        Assert.Equal(16, EngineRpcSurface.KnownAddedFamilies);
        Assert.Equal(77, EngineRpcSurface.RequiredCountOnKnown);

        Assert.Equal(EngineRpcSurface.KnownOnDeclaredCount, EngineRpcSurface.RequiredCountOnKnown + EngineRpcSurface.KnownAddedCount);

        Assert.Equal(EngineRpcSurface.KnownAddedCount, EngineRpcSurface.KnownAdded.Count);
        Assert.Equal(EngineRpcSurface.KnownAddedFamilies, EngineRpcSurface.KnownAddedNamespaces.Count);

        // Каждое знакомое новое имя — с семейством, и семейство одно на имя (никаких `a/b/c`).
        Assert.All(EngineRpcSurface.KnownAdded, name =>
        {
            Assert.Contains("/", name, StringComparison.Ordinal);
            Assert.Equal(2, name.Split('/').Length);
        });
    }

    /// <summary>
    /// НИ ОДНО ИМЯ НЕ СТОИТ В ДВУХ СПИСКАХ СРАЗУ: снятое не может быть нужным, знакомое новое —
    /// ни тем ни другим. Иначе один и тот же вызов окажется «и потерян, и нужен», и проба
    /// перестанет понимать, чего она требует от движка.
    /// </summary>
    [Fact]
    public void Ни_одно_имя_не_стоит_в_двух_списках()
    {
        foreach (var (left, leftName, right, rightName) in new[]
                 {
                     (EngineRpcSurface.Retired, "снятые", EngineRpcSurface.KnownAdded, "знакомые новые"),
                     (EngineRpcSurface.Required.Select(m => m.Endpoint).ToList(), "нужные", EngineRpcSurface.KnownAdded, "знакомые новые"),
                     (EngineRpcSurface.Retired, "снятые", EngineRpcSurface.Required.Select(m => m.Endpoint).ToList(), "нужные"),
                 })
        {
            var common = left.Intersect(right, StringComparer.Ordinal).ToArray();

            Assert.True(common.Length == 0, $"{leftName} и {rightName} пересекаются: {string.Join(", ", common)}");
        }

        // И повторов внутри самих списков нет.
        foreach (var (list, name) in new[]
                 {
                     (EngineRpcSurface.Retired, "снятые"),
                     (EngineRpcSurface.KnownAdded, "знакомые новые"),
                     (EngineRpcSurface.Methods.Select(m => m.Endpoint).ToList(), "снимок"),
                 })
        {
            var duplicates = list.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

            Assert.True(duplicates.Length == 0, $"{name}: повторы {string.Join(", ", duplicates)}");
        }
    }

    /// <summary>
    /// ПРАВИЛО ПРОБЫ НА ЖИВОМ ДВИЖКЕ: нужные имена проверяются (их отсутствие — потеря), снятые
    /// с надобности названы строкой, знакомые новые узнаются, а **незнакомое имя по-прежнему роняет
    /// пробу**. Последнее — самое важное: «умное прощение» незнакомых имён означало бы, что проба
    /// перестала замечать, что движок поменялся.
    /// </summary>
    [Fact]
    public void Правила_потери_и_незнакомого_имени_работают_как_замерено()
    {
        // Движок отдал ровно снимок нашей линии: потерь нет, снятые на месте, знакомых новых 0.
        var ours = EngineRpcSurface.Methods.Select(m => m.Endpoint).ToList();

        Assert.Empty(EngineRpcSurface.MissingRequired(ours));
        Assert.Empty(EngineRpcSurface.LostKnown(ours));
        Assert.Equal(0, EngineRpcSurface.KnownAddedPresent(ours));

        // Движок отдал снимок линии переезда: нужные все на месте, снятых нет вовсе (это НЕ провал —
        // панель говорит о них строкой), знакомых новых — все 67.
        var moved = EngineRpcSurface.Required
            .Select(m => m.Endpoint)
            .Concat(EngineRpcSurface.KnownAdded)
            .ToList();

        Assert.Empty(EngineRpcSurface.MissingRequired(moved));
        Assert.Equal(EngineRpcSurface.Retired.Count, EngineRpcSurface.LostKnown(moved).Count);
        Assert.Equal(EngineRpcSurface.KnownAddedCount, EngineRpcSurface.KnownAddedPresent(moved));
        Assert.Empty(EngineRpcSurface.UnknownAdded(moved));

        // А вот НЕЗНАКОМОЕ имя правило называет — и на нём проба обязана упасть.
        var withStranger = moved.Concat(new[] { "dshPanelProbe/suchMethod" }).ToList();

        Assert.Equal(new[] { "dshPanelProbe/suchMethod" }, EngineRpcSurface.UnknownAdded(withStranger));
    }

    /// <summary>
    /// ПОТЕРЯ НУЖНОГО ИМЕНИ — ЭТО ПОТЕРЯ, а не «движок чуть другой»: список недостающих называется
    /// поимённо. Этот факт стережёт то, ради чего в блоке A снимок и заводился.
    /// </summary>
    [Fact]
    public void Потеря_нужного_имени_называется_поимённо()
    {
        var withoutOne = EngineRpcSurface.Methods
            .Select(m => m.Endpoint)
            .Where(name => !string.Equals(name, EngineRpcSurface.RealCallEndpoint, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(new[] { EngineRpcSurface.RealCallEndpoint }, EngineRpcSurface.MissingRequired(withoutOne));
    }

    /// <summary>
    /// СНЯТЫЕ С НАДОБНОСТИ НЕ ЗОВУТСЯ ЖИВЬЁМ И НЕ ПОПАДАЮТ В ТЕЛЕФОН: они живут только в каталоге.
    /// Проверка держит границу блока A — «проба зовёт 77, а не 84» — и заодно не даёт вернуть снятое
    /// имя в белый список телефона (там ровно 13 своих вызовов, и семь снятых — не среди них).
    /// </summary>
    [Fact]
    public void Снятые_имена_не_зовутся_пробой_и_не_попали_в_телефон()
    {
        var probeCalls = EngineRpcSurface.Required.Select(m => m.Endpoint).ToList();

        Assert.All(EngineRpcSurface.Retired, name => Assert.DoesNotContain(name, probeCalls, StringComparer.Ordinal));

        // Белый список телефона — ровно 13 имён, и ни одного снятого среди них.
        Assert.Equal(13, DshPanel.Server.PhoneDecisions.Allowed.Count);
        Assert.All(
            EngineRpcSurface.Retired,
            name => Assert.DoesNotContain(name, DshPanel.Server.PhoneDecisions.Allowed.Select(c => c.Endpoint), StringComparer.Ordinal));
    }
}
