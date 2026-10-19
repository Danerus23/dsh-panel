using System;
using System.IO;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// БЛОК ДВИЖКА: РЕКОМЕНДУЕМАЯ И СВЕЖАЯ ВЕРСИИ + УБОРКА ЛИШНИХ СВОИХ NODE
/// (пункты 2, 3 и 5 плана E3.2; решения владельца `6565ac9` и `f09da5d`).
///
/// Здесь проверяются ЧИСТЫЕ решения блока движка и уборка своих Node: окно и путь обновления
/// (гашение движка, скачивание, предохранительная копия) пишутся следующим куском, и до него
/// это единственное, что можно проверить без окна, сети и живого движка.
/// </summary>
public class EngineUpdateDecisionsTests
{
    private static EngineHints Hints(string verified, string unverified) => EngineHints.Parse(
        "{\"format\":1,\"versions\":[" +
        "{\"version\":\"" + verified + "\",\"verified\":true,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}," +
        "{\"version\":\"" + unverified + "\",\"verified\":false,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

    /// <summary>
    /// ⚠️ САМОЕ ВАЖНОЕ ПРАВИЛО БЛОКА: **одним щелчком «Обновить» непроверенная версия не ставится
    /// НИКОГДА.** Главная кнопка ставит рекомендованную — даже когда свежая новее (а она новее
    /// в замере: реестр зовёт свежей `0.2.0-rc.2`, панель рекомендует `0.1.5-rc.2`).
    /// </summary>
    [Fact]
    public void Главная_кнопка_ставит_рекомендуемую_а_не_свежую()
    {
        var hints = Hints(verified: "0.1.5-rc.2", unverified: "0.2.0-rc.2");

        var recommended = EngineUpdateDecisions.Recommended(hints);

        Assert.Equal("0.1.5-rc.2", recommended);
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.0-rc.2", recommended), "свежая обязана быть новее — иначе случай не тот");
        Assert.False(EngineUpdateDecisions.MainButtonInstallsFresh, "главная кнопка снова ставит непроверенную версию");

        // Рекомендуемой нет (подсказок нет или ни одна не проверена) — обновление не предлагается,
        // а не берётся «что посвежее».
        Assert.Equal(string.Empty, EngineUpdateDecisions.Recommended(null));
        var noneVerified = EngineHints.Parse(
            "{\"format\":1,\"versions\":[" +
            "{\"version\":\"0.1.0\",\"verified\":false,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}," +
            "{\"version\":\"0.2.0\",\"verified\":false,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

        Assert.Equal(string.Empty, EngineUpdateDecisions.Recommended(noneVerified));
    }

    /// <summary>
    /// ОТДЕЛЬНАЯ ДВЕРЬ ГОДИТСЯ ТОЛЬКО ДЛЯ НОВЕЕ: «поставить то же самое, только страшнее» не бывает.
    /// </summary>
    [Fact]
    public void Отдельная_дверь_годится_только_для_новой_версии()
    {
        Assert.True(EngineUpdateDecisions.MayInstallFresh("0.2.0-rc.2", "0.1.5-rc.2"));
        Assert.False(EngineUpdateDecisions.MayInstallFresh("0.1.5-rc.2", "0.1.5-rc.2"));
        Assert.False(EngineUpdateDecisions.MayInstallFresh("0.1.4-rc.2", "0.1.5-rc.2"));
        Assert.False(EngineUpdateDecisions.MayInstallFresh(null, "0.1.5-rc.2"));
        Assert.False(EngineUpdateDecisions.MayInstallFresh("0.2.0-rc.2", null));

        // И слова подтверждения зависят от того, что ставим: у непроверенной — своё предупреждение.
        Assert.Equal(EngineInstallRefusals.FreshUnverifiedWarning, EngineUpdateDecisions.Confirmation(fresh: true));
        Assert.Equal(EngineInstallRefusals.RecommendedUpdate, EngineUpdateDecisions.Confirmation(fresh: false));
    }

    /// <summary>
    /// КНОПКА «Поставить непроверенную…» — ТОЛЬКО ЕСЛИ СВЕЖАЯ НОВЕЕ И РЕКОМЕНДУЕМОЙ, И СТОЯЩЕЙ
    /// (находка F-1, слово владельца 20.10.2026; дефект с его скрина — стояла `0.2.1-rc.2`, свежая
    /// была та же `0.2.1-rc.2`, а кнопка была видна и «что-то делала»).
    ///
    /// Первая половина правила (новее рекомендуемой) приходит готовым ответом двери —
    /// <paramref name="freshIsNewer"/>; вторая (новее стоящей) считается здесь, тем же правилом
    /// панели, что и первая.
    ///
    /// ⚠️ **Свои строки — свои мутации:** (а) стоящая = свежей → нет; (б) стоящая новее свежей →
    /// нет; (в) свежая новее обеих → есть. Мутация «убрать сравнение со стоящей» роняет (а) и (б),
    /// а (в) остаётся зелёной — она положительный контроль, а не доказательство.
    ///
    /// ⚠️ **С 20.10.2026 (задача F-1b) хвост `-rc` РАЗЛИЧАЕТСЯ** — сравнение стало SemVer
    /// (`EngineVersionOrder`), и строка «стоящая `0.2.1-rc.2`, свежая `0.2.1-rc.3`» теперь ДА:
    /// ради этого случая задача и делалась (новый rc-выпуск движка обязан показывать кнопку).
    /// Обратная сторона тоже здесь: финальный выпуск выше своего предрелиза, `alpha` ниже `rc`,
    /// «rc.10» новее «rc.9», а мусорная версия кнопки не даёт.
    /// </summary>
    [Theory]
    [InlineData("0.2.1-rc.2", "0.2.1-rc.2", true, false)]   // (а) стоящая = свежей: ставить нечего
    [InlineData("0.2.2-rc.2", "0.2.1-rc.2", true, false)]   // (б) стоящая НОВЕЕ свежей
    [InlineData("0.1.4-rc.2", "0.2.0-rc.2", true, true)]    // (в) свежая новее и стоящей, и рекомендуемой
    [InlineData("0.1.4-rc.2", "0.2.0-rc.2", false, false)]  // свежая не новее РЕКОМЕНДУЕМОЙ
    [InlineData("0.2.1-rc.2", "0.2.1-rc.3", true, true)]    // (г) F-1b: rc.3 НОВЕЕ rc.2 — кнопка есть
    [InlineData("0.2.1", "0.2.1-rc.3", true, false)]        // финальный выпуск выше своего предрелиза
    [InlineData("0.2.1-rc.2", "0.2.1-alpha.5", true, false)] // alpha ниже rc
    [InlineData("0.2.1-rc.9", "0.2.1-rc.10", true, true)]   // номера — как числа: 10 > 9
    [InlineData("не-версия", "0.2.1-rc.3", true, false)]    // стоящая не разбирается — «новее» не доказано
    [InlineData("0.2.1-rc.2", "не-версия", true, false)]    // свежая не разбирается — то же самое
    [InlineData("", "0.2.0-rc.2", true, false)]             // стоящая не прочитана — «новее» не доказано
    [InlineData("0.1.4-rc.2", "", true, false)]             // свежей нет — показывать нечего
    public void Кнопка_непроверенной_только_когда_свежая_новее_и_рекомендуемой_и_стоящей(
        string installed, string fresh, bool freshIsNewer, bool expected)
    {
        Assert.Equal(expected, EngineUpdateDecisions.FreshWorthShowing(freshIsNewer, installed, fresh));
    }

    /// <summary>
    /// ПОРЯДОК ВЕРСИЙ С ПРЕДРЕЛИЗНОЙ ЧАСТЬЮ (задача F-1b, слово владельца 20.10.2026):
    /// `0.2.1 > 0.2.1-rc.3 > 0.2.1-rc.2 > 0.2.1-alpha.5`.
    ///
    /// ⚠️ **И рядом — ДРУГОЕ решение, которое менять было нельзя:** объявление о новой версии
    /// (`EngineUpdateNotice`) сравнивает ПО ЧИСЛАМ, без хвоста (`FreshIsNewerByNumber`): слова
    /// владельца — «решает проба, а не хвост номера». Одна и та же пара даёт РАЗНЫЕ ответы, и оба
    /// ответа верные для своего решения: `0.1.5` против `0.1.5-rc.2` — «новее» для движка и
    /// «не новее» для объявления.
    /// </summary>
    [Fact]
    public void Версии_сравниваются_с_предрелизной_частью_а_объявление_по_числам()
    {
        // SemVer: финал > rc.3 > rc.2 > alpha.5
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.1", "0.2.1-rc.3"));
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.1-rc.3", "0.2.1-rc.2"));
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.1-rc.2", "0.2.1-alpha.5"));
        Assert.False(EngineUpdateDecisions.FreshIsNewer("0.2.1-alpha.5", "0.2.1-rc.2"));
        Assert.False(EngineUpdateDecisions.FreshIsNewer("0.2.1-rc.3", "0.2.1"));

        // Числа — как числа, а не как строки; хвост смотрится только при их равенстве.
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.1-rc.10", "0.2.1-rc.9"));
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.2.10-rc.1", "0.2.9-rc.9"));

        // А объявление — по числам: финал против своего rc для него «не новее».
        Assert.False(EngineUpdateDecisions.FreshIsNewerByNumber("0.1.5", "0.1.5-rc.2"));
        Assert.True(EngineUpdateDecisions.FreshIsNewer("0.1.5", "0.1.5-rc.2"));
        Assert.True(EngineUpdateDecisions.FreshIsNewerByNumber("0.2.0-rc.2", "0.1.5-rc.2"));
    }

    /// <summary>
    /// КАРТОЧКА ТОЖЕ СРАВНИВАЕТ С ХВОСТОМ (F-1b): стоящая `0.2.1-rc.3` против рекомендуемой
    /// `0.2.1-rc.2` — это «своя новее», а не «обновлять нечего»; и наоборот, стоящая rc против
    /// рекомендуемого финала — «своя старше», кнопка обновления есть.
    /// </summary>
    [Fact]
    public void Стоящая_и_рекомендуемая_сравниваются_с_хвостом()
    {
        Assert.Equal(
            EngineVersionStanding.Newer,
            EngineUpdateDecisions.Standing("0.2.1-rc.3", "0.2.1-rc.2"));

        Assert.Equal(
            EngineVersionStanding.Older,
            EngineUpdateDecisions.Standing("0.2.1-rc.2", "0.2.1"));

        Assert.Equal(
            EngineVersionStanding.Same,
            EngineUpdateDecisions.Standing("0.2.1-rc.2", "0.2.1-rc.2"));
    }

    /// <summary>
    /// ПОДТВЕРЖДЕНИЕ ГОВОРИТ ПРО СЕССИИ **СЛОВАМИ**, а не одними числами (требование владельца):
    /// сессии, созданные новой версией, старый движок не откроет — журнал v4. Числа замера в строке
    /// тоже есть, но сами по себе они человеку ничего не говорят.
    /// </summary>
    [Fact]
    public void Подтверждение_говорит_про_сессии_словами()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var warning = DshPanel.Localization.Loc.TIn(language, EngineInstallRefusals.FreshUnverifiedWarning, "0.2.0-rc.2");

            Assert.DoesNotContain("{", warning, StringComparison.Ordinal);
            Assert.NotEqual(EngineInstallRefusals.FreshUnverifiedWarning, warning);
        }

        var ru = DshPanel.Localization.Loc.TIn("ru", EngineInstallRefusals.FreshUnverifiedWarning, "0.2.0-rc.2");

        Assert.Contains("не откроет", ru, StringComparison.Ordinal);
        // ⚠️ Признак — СЛОВА о журнале, а не номер версии журнала: «v4» человеку ничего не говорит,
        // и он убран из строки (полировка 13.10.2026). Смысл предупреждения — «вернуться не выйдет».
        Assert.Contains("журнал новее", ru, StringComparison.Ordinal);
        // ⚠️ ЧИСЕЛ ЗАМЕРА В СТРОКЕ БОЛЬШЕ НЕТ (S-06, пачка B): каким способом панель проверяла
        // версию, человеку не нужно. Смысл предупреждения держат слова о сессиях и журнале.
        Assert.Contains("панель её не проверяла", ru, StringComparison.Ordinal);
    }

    /// <summary>
    /// УБОРКА ЛИШНИХ СВОИХ NODE: держим РОВНО ту версию, с которой панель поднимается; остальные
    /// свои версии убираем; **человеческую папку не трогаем**; освобождённое место называем числом.
    /// </summary>
    [Fact]
    public void Лишние_свои_Node_убираются_а_чужая_папка_остаётся()
    {
        var root = NewFolder();

        try
        {
            // Держим 22.23.3; рядом лежат старая своя 22.11.0 и папка, которую завёл человек.
            Directory.CreateDirectory(Path.Combine(root, "22.23.3"));
            Directory.CreateDirectory(Path.Combine(root, "22.11.0"));
            Directory.CreateDirectory(Path.Combine(root, "моя-папка"));

            File.WriteAllBytes(Path.Combine(root, "22.11.0", "node.exe"), new byte[4096]);

            Assert.Equal(new[] { "22.11.0" }, EngineNodeRetention.ToRemove("22.23.3", new[] { "22.23.3", "22.11.0", "моя-папка" }));

            var removed = EngineNodeRetention.Remove(root, "22.23.3", out var freed);

            Assert.Equal(new[] { "22.11.0" }, removed);
            Assert.True(freed >= 4096, $"освобождённое место не названо числом: {freed}");
            Assert.False(Directory.Exists(Path.Combine(root, "22.11.0")), "лишняя своя версия осталась");
            Assert.True(Directory.Exists(Path.Combine(root, "22.23.3")), "держимую версию убрали — панель не поднимется");
            Assert.True(Directory.Exists(Path.Combine(root, "моя-папка")), "уборка тронула папку, которую панель не заводила");

            // Системный Node панель не трогает вовсе: у неё нет пути к нему в этом решении.
            Assert.Equal(0, EngineNodeRetention.Size(Path.Combine(root, "моя-папка")));
        }
        finally
        {
            Files.TreeDeletion.Remove(root);
        }
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh-nodes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }
}
