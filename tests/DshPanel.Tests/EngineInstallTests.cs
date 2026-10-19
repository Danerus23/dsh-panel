using System;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КАКУЮ ВЕРСИЮ ДВИЖКА СТАВИТЬ (этап E3.1).
///
/// Решение владельца 10.10.2026, дословно: *«версию для установки бери из файла подсказок (сейчас
/// там проверена 0.1.5-rc.2; 0.2.0-rc.2 ставить вместо неё нельзя)»*. Значит версия приходит
/// **из подсказок**, а замок говорит, что панель поставит **фактически**. Расходятся — ставить
/// нельзя ни то, ни другое: выбор «поверим замку» был бы догадкой там, где нужен замер.
/// </summary>
public class EngineInstallTests
{
    private const string EngineDir = @"C:\Проба\панель\engine";

    /// <summary>Замок на одну версию — как настоящий, только короткий (две записи, как объявлено).</summary>
    private static EngineTreeLock LockFor(string version, bool summed = true) => EngineTreeLock.Parse(
        "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"" + version + "\"}," +
        "\"product\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\"},\"packages\":2," +
        "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
        "\"\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\",\"dependencies\":{\"@deepseek-ai/dsh\":\"" + version + "\"}}," +
        "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"" + version + "\",\"integrity\":\"sha512-A\"," +
        "\"resolved\":\"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-" + version + ".tgz\"}," +
        "\"node_modules/a\":{\"version\":\"1.0.0\"" + (summed ? ",\"integrity\":\"sha512-B\"" : string.Empty) + "," +
        "\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}" +
        "}}}");

    /// <summary>Подсказки с одной записью: версия и отметка «проверена».</summary>
    private static EngineHints HintsFor(string version, bool verified) => EngineHints.Parse(
        "{\"format\":1,\"versions\":[{\"version\":\"" + version + "\",\"verified\":" + (verified ? "true" : "false") + "," +
        "\"notes\":{\"ru\":[\"заметка\"]},\"troubles\":{\"ru\":[\"беда\"]}}]}");

    /// <summary>
    /// ВЕРСИЯ БЕРЁТСЯ ИЗ ПОДСКАЗОК И СВЕРЯЕТСЯ С ЗАМКОМ: сошлись — ставим, и путь назван сразу
    /// (папка версии внутри своей папки движка).
    /// </summary>
    [Fact]
    public void Версия_берётся_из_подсказок_и_сходится_с_замком()
    {
        var plan = EngineInstallDecisions.Decide(
            LockFor("0.1.5-rc.2"), HintsFor("0.1.5-rc.2", verified: true), EngineDir);

        Assert.True(plan.Any);
        Assert.Equal("0.1.5-rc.2", plan.Version);
        Assert.Equal(EngineInstallRefusal.None, plan.Refusal);
        Assert.Equal(string.Empty, plan.RefusalKey);

        // Путь — из ОДНОЙ двери (`OwnEngine.BinPath`): второй сборки того же пути быть не должно.
        Assert.Equal(OwnEngine.BinPath(EngineDir, "0.1.5-rc.2"), plan.BinPath);
    }

    /// <summary>
    /// ОТДЕЛЬНАЯ ПРОСЬБА: ВЕРСИЯ НАЗВАНА ЧЕЛОВЕКОМ, У НЕЁ ЕСТЬ СВОЙ ЗАМОК — СТАВИМ ЕЁ (задача `t4`,
    /// блок B). Это дорога линии 0.2.x: она ещё НЕ проверена по подсказкам, но замок у неё свой
    /// (683 пакета), и «ставить по замку» от непроверенности не перестаёт быть верным.
    /// </summary>
    [Fact]
    public void Отдельная_просьба_ставит_версию_у_которой_есть_замок()
    {
        var locks = new EngineTreeLocks(new[] { LockFor("0.1.5-rc.2"), LockFor("0.2.1-rc.2") });

        var plan = EngineInstallDecisions.DecideFor(locks, "0.2.1-rc.2", EngineDir);

        Assert.True(plan.Any);
        Assert.Equal("0.2.1-rc.2", plan.Version);
        Assert.Equal(EngineInstallRefusal.None, plan.Refusal);
        Assert.Equal(string.Empty, plan.RefusalKey);
        Assert.Equal(OwnEngine.BinPath(EngineDir, "0.2.1-rc.2"), plan.BinPath);

        // Сверка версии — та же, что у главной дороги: предрелизный хвост ЗНАЧИМ, поэтому «0.2.1»
        // и «0.2.1-rc.2» — разные версии (правило панели, см. `EngineLockForVersionTests`).
        Assert.True(EngineInstallDecisions.HasLockForVersion(locks, "0.2.1-rc.2"));
        Assert.False(EngineInstallDecisions.HasLockForVersion(locks, "0.2.1"));
        Assert.False(EngineInstallDecisions.HasLockForVersion(locks, "0.2.0-rc.2"));
        Assert.False(EngineInstallDecisions.HasLockForVersion(null, "0.2.1-rc.2"));
    }

    /// <summary>
    /// ОТДЕЛЬНАЯ ПРОСЬБА БЕЗ ЗАМКА — ОТКАЗ СЛОВАМИ, а не установка соседней версии: без этой
    /// проверки панель поставила бы по главному замку 0.1.5 и объявила 0.2.1, то есть сказала бы
    /// неправду. Два разных отказа: «замков нет вовсе» и «замка нет для этой версии».
    /// </summary>
    [Fact]
    public void Отдельная_просьба_без_замка_отказывает_словами()
    {
        var only = new EngineTreeLocks(new[] { LockFor("0.1.5-rc.2") });

        var plan = EngineInstallDecisions.DecideFor(only, "0.2.1-rc.2", EngineDir);

        Assert.False(plan.Any);
        Assert.Equal(EngineInstallRefusal.NoLockForVersion, plan.Refusal);
        Assert.Equal(EngineInstallRefusals.NoLockForVersion, plan.RefusalKey);

        var none = EngineInstallDecisions.DecideFor(EngineTreeLocks.Empty, "0.2.1-rc.2", EngineDir);

        Assert.False(none.Any);
        Assert.Equal(EngineInstallRefusal.NoLock, none.Refusal);
        Assert.Equal(EngineInstallRefusals.NoLock, none.RefusalKey);
    }

    /// <summary>
    /// БИТЫЙ ЗАМОК НЕ СТАВИТСЯ И ПО ОТДЕЛЬНОЙ ПРОСЬБЕ: замок без сумм — это список версий, а не замок.
    /// Проверка та же, что у главной дороги, и это намеренно: «человек попросил» не делает набор целым.
    /// </summary>
    [Fact]
    public void Отдельная_просьба_не_принимает_замок_без_сумм()
    {
        var cut = new EngineTreeLocks(new[] { LockFor("0.2.1-rc.2", summed: false) });

        var plan = EngineInstallDecisions.DecideFor(cut, "0.2.1-rc.2", EngineDir);

        Assert.False(plan.Any);
        Assert.Equal(EngineInstallRefusal.Incomplete, plan.Refusal);
        Assert.Equal(EngineInstallRefusals.Incomplete, plan.RefusalKey);
    }
    /// <summary>
    /// РАЗВИЛКА ДОРОГ ИДЁТ ПО ГЛАВНОМУ ЗАМКУ, А НЕ ПО ОТМЕТКЕ «ПРОВЕРЕНА» (выпуск 2.3.0).
    ///
    /// **От какого случая.** Проверенных версий стало ДВЕ: `0.2.1-rc.2` — текущая по умолчанию,
    /// `0.1.5-rc.2` — прежняя, к которой возвращает откат. Прежнее правило («проверена → главная
    /// дорога») отправило бы главную кнопку на главную дорогу, и та поставила бы по ГЛАВНОМУ замку
    /// `0.1.5`, хотя подсказки рекомендуют `0.2.1`: карточка говорила бы одно, а ставилось другое.
    ///
    /// **Правило теперь.** Главная дорога — ровно тогда, когда названная версия И ЕСТЬ версия
    /// главного замка (её установщик сверяет с подсказками: «две правды об одном дереве»). Всякая
    /// другая версия — своей дорогой, со своим замком; так главная кнопка и ставит 0.2.1.
    ///
    /// ⚠️ Мутация «вернуть развилку по подсказкам» роняет именно эту проверку.
    /// </summary>
    [Fact]
    public void Дорога_выбирается_по_главному_замку_а_не_по_отметке_в_подсказках()
    {
        var primary = LockFor("0.1.5-rc.2");

        // Версия главного замка — главная дорога; сверка идёт ЧИСЛОВОЙ частью, как везде в панели.
        Assert.Null(EngineInstallDecisions.RequestedFor(primary, "0.1.5-rc.2"));
        Assert.Null(EngineInstallDecisions.RequestedFor(primary, " 0.1.5-rc.2 "));

        // ⚠️ ПРЕДРЕЛИЗНЫЙ ХВОСТ ЗНАЧИМ и здесь (правило панели `UpdateDecisions.Numeric`):
        // `0.1.5` — НЕ версия замка `0.1.5-rc.2`, и набора для финального `0.1.5` в сборке нет.
        // Такая версия идёт своей дорогой, где замок честно ответит «набора для этой версии нет»,
        // а не будет принята за предрелиз.
        Assert.Equal("0.1.5", EngineInstallDecisions.RequestedFor(primary, "0.1.5"));

        // Проверенная 0.2.1 — уже НЕ версия главного замка: у неё свой замок, и она идёт своей
        // дорогой. Это и есть «главная кнопка ставит 0.2.x» после выпуска 2.3.0.
        Assert.Equal("0.2.1-rc.2", EngineInstallDecisions.RequestedFor(primary, "0.2.1-rc.2"));

        // Пустая версия — не просьба вовсе. Замка нет — решать нечего: версия идёт своей дорогой,
        // и там откажет уже замок («ставить не по чему»), а не догадка.
        Assert.Null(EngineInstallDecisions.RequestedFor(primary, string.Empty));
        Assert.Null(EngineInstallDecisions.RequestedFor(primary, null));
        Assert.Equal("0.1.5-rc.2", EngineInstallDecisions.RequestedFor(EngineTreeLock.Empty, "0.1.5-rc.2"));
        Assert.Equal("0.1.5-rc.2", EngineInstallDecisions.RequestedFor(null, "0.1.5-rc.2"));
    }
    /// <summary>
    /// ⚠️ САМ СЛУЧАЙ ИЗ СЛОВ ВЛАДЕЛЬЦА: подсказки говорят «проверена 0.2.0-rc.2», а замок снят
    /// с `0.1.5-rc.2`. Ставить **нельзя ни то, ни другое** — и панель говорит, что именно не так.
    /// </summary>
    [Fact]
    public void Разные_версии_в_подсказках_и_замке_значит_ставить_нечего()
    {
        var plan = EngineInstallDecisions.Decide(
            LockFor("0.1.5-rc.2"), HintsFor("0.2.0-rc.2", verified: true), EngineDir);

        Assert.False(plan.Any);
        Assert.Equal(EngineInstallRefusal.Mismatch, plan.Refusal);
        Assert.Equal(EngineInstallRefusals.Mismatch, plan.RefusalKey);
        Assert.Equal(string.Empty, plan.Version);
        Assert.Equal(string.Empty, plan.BinPath);
    }

    /// <summary>
    /// ПРОВЕРЕННАЯ ВЕРСИЯ СВЕРЯЕТСЯ ПРАВИЛОМ ПАНЕЛИ (числовая часть): у движка бывают хвосты,
    /// и `v0.1.5-rc.2` в подсказках — та же версия, что `0.1.5-rc.2` в замке.
    /// </summary>
    [Theory]
    [InlineData("0.1.5-rc.2")]
    [InlineData("v0.1.5-rc.2")]
    public void Хвост_записи_версии_не_мешает(string told)
    {
        var plan = EngineInstallDecisions.Decide(LockFor("0.1.5-rc.2"), HintsFor(told, verified: true), EngineDir);

        Assert.True(plan.Any);
        Assert.Equal(told, plan.Version);
    }

    /// <summary>
    /// БЕЗ ЗАМКА, БЕЗ ПОДСКАЗОК И БЕЗ ПРОВЕРЕННОЙ ВЕРСИИ — три РАЗНЫХ отказа, и каждый назван:
    /// «одна общая ошибка установки» не сказала бы человеку, что поправить.
    /// </summary>
    [Fact]
    public void Каждый_отказ_назван_по_своему()
    {
        var cases = new (EngineTreeLock Lock, EngineHints Hints, EngineInstallRefusal Expected)[]
        {
            (EngineTreeLock.Empty, HintsFor("0.1.5-rc.2", true), EngineInstallRefusal.NoLock),
            (LockFor("0.1.5-rc.2"), EngineHints.Empty, EngineInstallRefusal.NoHints),
            (LockFor("0.1.5-rc.2"), HintsFor("0.1.5-rc.2", false), EngineInstallRefusal.NotVerified),
        };

        foreach (var item in cases)
        {
            var plan = EngineInstallDecisions.Decide(item.Lock, item.Hints, EngineDir);

            Assert.False(plan.Any);
            Assert.Equal(item.Expected, plan.Refusal);
            Assert.NotEqual(string.Empty, plan.RefusalKey);

            // И у отказа ЕСТЬ слова на трёх языках — иначе человек увидел бы имя ключа.
            Assert.NotEqual(plan.RefusalKey, Loc.T(plan.RefusalKey));
        }

        // Отказы РАЗЛИЧИМЫ: у каждого свой ключ, и ключ называется ключом же.
        Assert.Equal(3, cases.Select(item => EngineInstallRefusals.Key(item.Expected)).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// ПОДМЕНЁННЫЙ, ОБРЕЗАННЫЙ И БЕЗ СУММ ЗАМОК — тоже отказ, и до выбора версии: ставить по такому
    /// набору значило бы поставить неизвестно что (пакет не с официального реестра), половину дерева
    /// или вложения, ни у одного из которых не сверена сумма.
    /// </summary>
    [Fact]
    public void Подменённый_обрезанный_и_бессуммовый_замок_не_пускают_установку()
    {
        var foreign = EngineTreeLock.Parse(
            "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.2\"},\"packages\":1," +
            "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
            "\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":\"0.1.5-rc.2\"}}," +
            "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"0.1.5-rc.2\",\"integrity\":\"sha512-A\"," +
            "\"resolved\":\"https://злой-зеркало.example/dsh.tgz\"}}}}");

        var foreignPlan = EngineInstallDecisions.Decide(foreign, HintsFor("0.1.5-rc.2", true), EngineDir);

        Assert.Equal(EngineInstallRefusal.ForeignSource, foreignPlan.Refusal);

        // Объявлено три пакета, а записей две — замок обрезан.
        var cut = EngineTreeLock.Parse(
            "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.2\"},\"packages\":3," +
            "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
            "\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":\"0.1.5-rc.2\"}}," +
            "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"0.1.5-rc.2\",\"integrity\":\"sha512-A\"," +
            "\"resolved\":\"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-0.1.5-rc.2.tgz\"}," +
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-B\"," +
            "\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}}}}");

        var cutPlan = EngineInstallDecisions.Decide(cut, HintsFor("0.1.5-rc.2", true), EngineDir);

        Assert.Equal(EngineInstallRefusal.Incomplete, cutPlan.Refusal);

        // Суммы у одного пакета нет — «замок без сумм» это список версий, а не замок.
        var sumless = EngineInstallDecisions.Decide(LockFor("0.1.5-rc.2", summed: false), HintsFor("0.1.5-rc.2", true), EngineDir);

        Assert.False(sumless.Any);
        Assert.Equal(EngineInstallRefusal.Incomplete, sumless.Refusal);
    }

    /// <summary>
    /// КЛЮЧИ ОТКАЗОВ И СЛОВ УСТАНОВКИ — ЧЛЕНЫ `PanelStrings` И ЕСТЬ В СЛОВАРЯХ. Опечатка в имени
    /// ключа иначе дошла бы до человека именем ключа в окне (такой дефект в проекте уже был).
    ///
    /// Проверяются ВСЕ ключи установки, а не только решения: установка говорит с человеком ещё
    /// ходом и расхождениями сверки, и «строка есть» должно быть верно для каждого из них.
    /// </summary>
    [Fact]
    public void Ключи_отказов_установки_объявлены()
    {
        var members = typeof(PanelStrings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var keys = new[]
        {
            EngineInstallRefusals.NoLock, EngineInstallRefusals.NoHints, EngineInstallRefusals.NotVerified,
            EngineInstallRefusals.Mismatch, EngineInstallRefusals.ForeignSource, EngineInstallRefusals.Incomplete,
            EngineInstallRefusals.NodeMissing, EngineInstallRefusals.NodeOld, EngineInstallRefusals.NpmMissing,
            EngineInstallRefusals.NpmFailed, EngineInstallRefusals.TooLong, EngineInstallRefusals.NoPlace,
            EngineInstallRefusals.TreeWrong,
        };

        Assert.Equal(13, keys.Distinct(StringComparer.Ordinal).Count());

        foreach (var key in keys)
        {
            Assert.True(members.Contains(key), $"ключа «{key}» нет среди членов PanelStrings — человек увидел бы имя ключа");
            Assert.NotEqual(key, Loc.T(key));
        }

        Assert.Equal(string.Empty, EngineInstallRefusals.Key(EngineInstallRefusal.None));
    }

    /// <summary>
    /// СЛОВА УСТАНОВКИ СОБИРАЮТСЯ ОДНОЙ ДВЕРЬЮ И ЕСТЬ НА ТРЁХ ЯЗЫКАХ. Строка-шаблон, показанная
    /// человеку как есть (с `{0}` вместо числа), — это дефект, а не строка, поэтому подстановка
    /// проверяется на каждом языке (`Loc.TIn`, чтобы не зависеть от языка, выбранного соседним
    /// классом проверок).
    /// </summary>
    [Fact]
    public void Слова_установки_подставляют_числа_на_всех_языках()
    {
        var keys = new[]
        {
            "EngineInstallNodeOld", "EngineInstallNpmFailed", "EngineInstallTooLong", "EngineInstallNoPlace",
            "EngineInstallTreeWrong", "EngineInstallStartedFormat", "EngineInstallDoneFormat",
            "EngineTreeNumbersFormat", "EngineTreeProblemMissingFormat", "EngineTreeProblemVersionFormat",
            "EngineTreeProblemExtraFormat", "EngineTreeProblemsMoreFormat",
        };

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in keys)
            {
                var text = Loc.TIn(language, key, "1", "2", "3", "4");

                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }

        // И сами помощники: расхождения называются поимённо, а остаток — числом.
        var report = new EngineTreeReport(
            true,
            new[]
            {
                new EngineTreeProblem(EngineTreeProblemKind.Missing, "a", "1.0.0", "node_modules/a"),
                new EngineTreeProblem(EngineTreeProblemKind.Version, "b", "2.0.0", "2.5.0"),
                new EngineTreeProblem(EngineTreeProblemKind.Extra, "node_modules/c", string.Empty, "3.0.0"),
            },
            7);

        var named = EngineInstallPhrases.TreeProblems(report);

        Assert.Contains("a", named, StringComparison.Ordinal);
        Assert.Contains("b", named, StringComparison.Ordinal);
        Assert.DoesNotContain("{", named, StringComparison.Ordinal);
        Assert.DoesNotContain("{", EngineInstallPhrases.Numbers(report), StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРАВДА О ЗАМКЕ: версия для установки — та, что в замке, и она же проверена пробой
    /// (`EngineRpcSurface.MeasuredOnVersion`). Это связывает три места: подсказки, замок и замер.
    /// </summary>
    [Fact]
    public void Настоящие_замок_и_подсказки_дают_плановую_версию()
    {
        var engineLock = EngineTreeLock.Load();

        Assert.True(engineLock.Any, "встроенный замок не прочитался");

        // Подсказки берём такие же, какими их увидит панель: проверенная версия — версия замка.
        var hints = EngineHints.Parse(
            "{\"format\":1,\"versions\":[{\"version\":\"" + engineLock.EngineVersion + "\",\"verified\":true," +
            "\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

        var plan = EngineInstallDecisions.Decide(engineLock, hints, EngineDir);

        Assert.True(plan.Any);
        Assert.Equal(EngineRpcSurface.MeasuredOnVersion, plan.Version);
        Assert.Equal(OwnEngine.BinPath(EngineDir, plan.Version), plan.BinPath);
    }
}
