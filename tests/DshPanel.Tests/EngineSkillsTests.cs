using System;
using System.Linq;
using System.Reflection;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// УМЕНИЯ ПАНЕЛИ С ЭТИМ ДВИЖКОМ (этап E2 плана `ROADMAP-BORROWED.md`).
///
/// **Зачем это отдельно от поверхности RPC.** Проба поверхности отвечает на вопрос «какие ВЫЗОВЫ
/// движок признаёт»; умения отвечают на другой — **что из этого панель умеет**. Разница не
/// косметическая: имена могут быть признаны, а канала управления не быть, ключа модели не быть,
/// раскладки профиля не быть. Здесь проверяется ВЫВОД вердиктов из измеренных фактов: обе стороны
/// (умение есть · умения нет) на подставных фактах, без сети и без машины.
///
/// Проба живым движком — отдельно: `--server-selftest` в изоляции (её гоняет дирижёр при приёмке,
/// потому что она поднимает настоящий движок).
/// </summary>
public sealed class EngineSkillsTests
{
    /// <summary>Все умения подтверждены — когда подтверждено всё, чем они добываются.</summary>
    private static SkillFacts Everything() => new()
    {
        EntryLinkParsed = true,
        SessionListAnswered = true,
        RecognisedEndpoints = new[] { "session/prompt", "session/cancel", "session/control", "session/list" },
        ControlStreamOpened = true,
        ModelKeyFile = true,
        ProfileLayout = true,
        EngineVersion = "0.1.5-rc.2",
        Probed = true,
    };

    /// <summary>
    /// ⚠️ ЖИВАЯ ПРОБА ПОМЕЧАЕТ СЕБЯ ЖИВОЙ — ДАЖЕ КОГДА ДВИЖОК МОЛЧИТ.
    ///
    /// Дефект, найденный прогоном `--server-selftest` в изоляции 10.10.2026: проба честно спросила
    /// движок и получила ответ (список сессий, рукопожатие канала), но живое умение показывалось
    /// как «не проверялась» — потому что «проба была» не выставлялось вовсе, и выходило «5 из 8».
    /// Здесь проба идёт на **закрытый** порт: движка нет, ответа нет — и всё равно `Probed` обязан
    /// быть истинным, а живое умение — читаться как БЕДА с причиной, а не как «не проверялась».
    /// </summary>
    [Fact]
    public void Проба_на_закрытый_порт_всё_равно_помечает_себя_живой()
    {
        // Порт, на котором никого нет: соединение отбивается сразу, в сеть не ходим.
        using var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var paths = AppPaths.Under(@"C:\Проба\панель");
        var facts = EngineSkillProbe.Gather(
            new SkillTarget($"http://127.0.0.1:{port}", string.Empty, port, $"http://127.0.0.1:{port}/?token=***"),
            paths,
            "0.1.5-rc.2",
            Array.Empty<string>(),
            exists: _ => false,
            directoryExists: _ => false,
            timeout: TimeSpan.FromSeconds(2));

        Assert.True(facts.Probed, "«проба была» обязана быть выставлена самим фактом пробы");

        var verdicts = EngineSkills.Measure(facts);

        Assert.Equal(0, EngineSkills.Unchecked(verdicts));

        // Ответа нет — значит беда, и она НАЗВАНА причиной из словаря, а не пустым местом.
        var list = verdicts.Single(v => v.Skill == EngineSkill.SessionList);

        Assert.Equal(SkillState.Missing, list.State);
        Assert.Equal(SkillKeys.Reason(EngineSkill.SessionList), list.ReasonKey);
    }

    [Fact]
    public void Когда_всё_на_месте_подтверждены_все_умения()
    {
        var verdicts = EngineSkills.Measure(Everything());

        Assert.Equal(EngineSkills.All.Count, verdicts.Count);
        Assert.All(verdicts, v => Assert.True(v.Ok, $"умение «{v.Skill}» не подтвердилось: {v.ReasonKey}"));
        Assert.Equal(verdicts.Count, EngineSkills.Confirmed(verdicts));
        Assert.Empty(EngineSkills.Missing(verdicts));
    }

    /// <summary>
    /// У КАЖДОГО УМЕНИЯ ЕСТЬ ИМЯ И СПОСОБ ПРОВЕРКИ, и способ именно тот, каким его добывают:
    /// живой вызов · по объявлению движка · по файлу · по версии. Способ — не украшение: «есть»
    /// по объявлению и «есть» живым вызовом значат разное, и панель обязана говорить, какое это.
    /// </summary>
    [Fact]
    public void У_каждого_умения_есть_имя_и_способ_проверки()
    {
        var verdicts = EngineSkills.Measure(Everything());

        Assert.Equal(
            new[] { SkillKeys.HowLive, SkillKeys.HowDeclared, SkillKeys.HowLive },
            verdicts.Where(v => v.Skill is EngineSkill.SessionList or EngineSkill.TaskPrompt or EngineSkill.QuestionAnswer)
                .Select(v => v.HowKey)
                .ToArray());

        Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(SkillKeys.Name(v.Skill))));
        Assert.All(verdicts, v => Assert.Contains(
            v.HowKey,
            new[] { SkillKeys.HowLive, SkillKeys.HowDeclared, SkillKeys.HowFile, SkillKeys.HowVersion }));

        // Постановка задачи и отмена — ПО ОБЪЯВЛЕНИЮ, и это названо: позвать их значит начать
        // или прервать работу агента, проба этого не делает.
        Assert.Equal(SkillKeys.HowDeclared, verdicts.Single(v => v.Skill == EngineSkill.TaskPrompt).HowKey);
        Assert.Equal(SkillKeys.HowDeclared, verdicts.Single(v => v.Skill == EngineSkill.TaskCancel).HowKey);
    }

    /// <summary>
    /// КАЖДОЕ УМЕНИЕ ОТВЕЧАЕТ САМО ЗА СЕБЯ: нет ключа модели — панель всё равно подтверждает
    /// сессии, задачу, отмену, раскладку и версию. Решение владельца: «нет умения → функция
    /// недоступна и объясняет словами, остальное живёт».
    /// </summary>
    [Theory]
    [InlineData(EngineSkill.EntryLink)]
    [InlineData(EngineSkill.SessionList)]
    [InlineData(EngineSkill.TaskPrompt)]
    [InlineData(EngineSkill.TaskCancel)]
    [InlineData(EngineSkill.QuestionAnswer)]
    [InlineData(EngineSkill.ModelKey)]
    [InlineData(EngineSkill.BackupLayout)]
    [InlineData(EngineSkill.EngineVersion)]
    public void Без_одного_умения_остальные_живут(EngineSkill missing)
    {
        var facts = Everything();

        facts = missing switch
        {
            EngineSkill.EntryLink => facts with { EntryLinkParsed = false },
            EngineSkill.SessionList => facts with { SessionListAnswered = false },
            EngineSkill.TaskPrompt => facts with { RecognisedEndpoints = facts.RecognisedEndpoints.Where(e => e != "session/prompt").ToList() },
            EngineSkill.TaskCancel => facts with { RecognisedEndpoints = facts.RecognisedEndpoints.Where(e => e != "session/cancel").ToList() },
            EngineSkill.QuestionAnswer => facts with { ControlStreamOpened = false },
            EngineSkill.ModelKey => facts with { ModelKeyFile = false },
            EngineSkill.BackupLayout => facts with { ProfileLayout = false },
            EngineSkill.EngineVersion => facts with { EngineVersion = string.Empty },
            _ => facts,
        };

        var verdicts = EngineSkills.Measure(facts);

        var failed = verdicts.Single(v => v.Skill == missing);
        Assert.False(failed.Ok);
        Assert.False(string.IsNullOrWhiteSpace(failed.ReasonKey), "у отказа нет причины — человеку нечего прочитать");

        Assert.Equal(verdicts.Count - 1, EngineSkills.Confirmed(verdicts));
        Assert.Single(EngineSkills.Missing(verdicts));
    }

    /// <summary>
    /// ОТВЕТ НА ВОПРОС АГЕНТА ТРЕБУЕТ ДВУХ ВЕЩЕЙ: имени канала управления И того, что канал
    /// открывается. Одного имени мало — потоковый вызов одиночным запросом не отвечает вовсе.
    /// Причины у этих двух случаев РАЗНЫЕ: человеку важно, чего именно не хватает.
    /// </summary>
    [Fact]
    public void Ответ_на_вопрос_требует_и_канала_и_рукопожатия()
    {
        var withoutChannel = Everything() with
        {
            RecognisedEndpoints = new[] { "session/prompt", "session/cancel" },
            ControlStreamOpened = true,
        };

        var withoutStream = Everything() with { ControlStreamOpened = false };

        var first = EngineSkills.Measure(withoutChannel).Single(v => v.Skill == EngineSkill.QuestionAnswer);
        var second = EngineSkills.Measure(withoutStream).Single(v => v.Skill == EngineSkill.QuestionAnswer);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.NotEqual(first.ReasonKey, second.ReasonKey);
    }

    /// <summary>
    /// БЕЗ ДВИЖКА УМЕНИЯ НЕ ВЫДУМЫВАЮТСЯ: подтверждается только то, что видно на диске (ключ
    /// модели и раскладка профиля), остальное — «нет» с причиной. Проверяется на своём временном
    /// каталоге: каталогов владельца эта проверка не касается.
    /// </summary>
    [Fact]
    public void Без_движка_подтверждается_только_видимое_на_диске()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-skills-" + Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Under(root);
            var facts = EngineSkillProbe.None(paths);
            var verdicts = EngineSkills.Measure(facts);

            Assert.False(verdicts.Single(v => v.Skill == EngineSkill.SessionList).Ok);
            Assert.False(verdicts.Single(v => v.Skill == EngineSkill.ModelKey).Ok);
            Assert.False(verdicts.Single(v => v.Skill == EngineSkill.BackupLayout).Ok);

            // Появляется раскладка профиля и файл ключа — умения про них подтверждаются.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(paths.DshHome, "profiles"));
            System.IO.Directory.CreateDirectory(paths.DshHome);
            System.IO.File.WriteAllText(paths.CredentialsPath, "probe: true");

            var after = EngineSkills.Measure(EngineSkillProbe.None(paths));

            Assert.True(after.Single(v => v.Skill == EngineSkill.ModelKey).Ok);
            Assert.True(after.Single(v => v.Skill == EngineSkill.BackupLayout).Ok);
            Assert.False(after.Single(v => v.Skill == EngineSkill.EntryLink).Ok);
        }
        finally
        {
            try { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// КЛЮЧИ УМЕНИЙ — ЧЛЕНЫ `PanelStrings`. Опечатка в имени ключа иначе дошла бы до человека
    /// именем ключа в окне (такой дефект в проекте уже был): проверка берёт имена отражением
    /// и падает, если члена нет.
    /// </summary>
    [Fact]
    public void Ключи_умений_объявлены_в_PanelStrings()
    {
        var members = typeof(PanelStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var keys = new[]
        {
            SkillKeys.Title, SkillKeys.ValueFormat, SkillKeys.LineBareFormat, SkillKeys.SummaryFormat,
            SkillKeys.SummaryLabel, SkillKeys.VersionsLabel, SkillKeys.VersionsValueFormat, SkillKeys.VersionsNone,
            SkillKeys.Checked, SkillKeys.NotChecked, SkillKeys.Trouble, SkillKeys.ReasonNotChecked,
            SkillKeys.UncheckedFormat, SkillKeys.VersionsFormat, SkillKeys.VersionUnknown,
            SkillKeys.HowLive, SkillKeys.HowDeclared, SkillKeys.HowFile, SkillKeys.HowVersion,
        }
        .Concat(EngineSkills.All.Select(SkillKeys.Name))
        .Concat(new[] { true, false }.SelectMany(known => EngineSkills.All.Select(skill => SkillKeys.Reason(skill, known))))
        .Distinct(StringComparer.Ordinal)
        .ToList();

        Assert.Equal(36, keys.Count);

        foreach (var key in keys)
        {
            Assert.True(members.Contains(key), $"ключа «{key}» нет среди членов PanelStrings — человек увидел бы имя ключа");
        }

        // И КЛЮЧИ РАЗЛИЧИМЫ. Имя, повторённое у двух умений, человек прочитает не про то;
        // причины, слитые в одну, не скажут, чего именно не хватает. Девять причин — это восемь
        // умений плюс второй случай у ответа на вопрос агента (нет канала · канал не открылся).
        Assert.Equal(
            EngineSkills.All.Count,
            EngineSkills.All.Select(SkillKeys.Name).Distinct(StringComparer.Ordinal).Count());

        var reasons = EngineSkills.All
            .SelectMany(skill => new[] { true, false }.Select(known => SkillKeys.Reason(skill, known)))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(9, reasons.Count);
    }
}
