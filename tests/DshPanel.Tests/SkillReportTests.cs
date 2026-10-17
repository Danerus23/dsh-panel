using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТАБЛИЦА УМЕНИЙ В ОТЧЁТЕ ОБ ОКРУЖЕНИИ (этап E2, шаг 3).
///
/// Решение владельца: **новых окон не заводить** — таблица живёт в том же отчёте, который
/// показывает раздел «Сервер», и в том же тексте, что уходит в журнал и в отчёт самотеста.
/// Строка на умение, вердикт словами владельца (**проверена · не проверялась · известна беда**),
/// версия движка и список проверенных версий.
///
/// ⚠️ **«Не проверялась» — не беда.** Панель не спрашивала движок (живой пробы отчёт не делает
/// намеренно), и так и говорит. Назвать это бедой значило бы выдумать поломку, а назвать
/// «проверена» — соврать.
/// </summary>
public sealed class SkillReportTests
{
    private static IReadOnlyList<SkillVerdict> NotProbed() =>
        EngineSkills.Measure(EngineSkillProbe.None(AppPaths.Under(@"C:\Проба\панель")));

    /// <summary>ТРИ СОСТОЯНИЯ — ТРИ РАЗНЫХ СТРОКИ, и каждая называет своё.</summary>
    [Fact]
    public void Три_состояния_дают_три_разных_строки()
    {
        var checkedLine = SkillPhrases.Line(SkillVerdict.Yes(EngineSkill.SessionList, SkillKeys.HowLive));
        var troubleLine = SkillPhrases.Line(SkillVerdict.No(EngineSkill.SessionList, SkillKeys.Reason(EngineSkill.SessionList), SkillKeys.HowLive));
        var unknownLine = SkillPhrases.Line(SkillVerdict.Unknown(EngineSkill.SessionList, SkillKeys.HowLive));

        Assert.Contains(Loc.T(SkillKeys.Checked), checkedLine, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.HowLive), checkedLine, StringComparison.Ordinal);

        Assert.Contains(Loc.T(SkillKeys.Trouble), troubleLine, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.Reason(EngineSkill.SessionList)), troubleLine, StringComparison.Ordinal);

        Assert.Contains(Loc.T(SkillKeys.NotChecked), unknownLine, StringComparison.Ordinal);

        Assert.NotEqual(checkedLine, troubleLine);
        Assert.NotEqual(checkedLine, unknownLine);
        Assert.NotEqual(troubleLine, unknownLine);

        // И имя умения есть в каждой строке: строка «проверена» без имени не говорит ничего.
        var name = Loc.T(SkillKeys.Name(EngineSkill.SessionList));
        Assert.All(new[] { checkedLine, troubleLine, unknownLine }, line => Assert.Contains(name, line, StringComparison.Ordinal));

        Assert.All(new[] { checkedLine, troubleLine, unknownLine }, line => Assert.DoesNotContain("{0}", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// РЕШЕНИЕ РАЗЛИЧАЕТ «НЕ СПРАШИВАЛИ» И «СПРОСИЛИ И НЕ ВЫШЛО»: без живой пробы живые умения
    /// стоят «не проверялась», а файловые проверяются всегда — они и так под рукой.
    /// </summary>
    [Fact]
    public void Без_пробы_живые_умения_не_проверялись_а_файловые_проверены()
    {
        var verdicts = NotProbed();

        var live = verdicts.Where(v => v.Skill is EngineSkill.EntryLink or EngineSkill.SessionList or EngineSkill.QuestionAnswer).ToList();
        Assert.All(live, v =>
        {
            Assert.Equal(SkillState.Unknown, v.State);
            Assert.False(v.Ok);
            Assert.False(v.Broken);
            Assert.Equal(SkillKeys.ReasonNotChecked, v.ReasonKey);
        });

        // Версия читается из пакета, ключ и раскладка — с диска: они проверены и без движка.
        Assert.True(verdicts.Single(v => v.Skill == EngineSkill.EngineVersion).State is SkillState.Ok or SkillState.Missing);
        Assert.False(verdicts.Single(v => v.Skill == EngineSkill.ModelKey).State == SkillState.Unknown);

        Assert.Equal(3, EngineSkills.Unchecked(verdicts));

        // «Беда» без движка — это про то, чего движок не объявляет (задача и отмена) и чего не
        // прочитать (версия). Живые умения сюда НЕ попадают: о них не спрашивали, и это «не
        // проверялась», а не поломка.
        var liveSkills = new[] { EngineSkill.EntryLink, EngineSkill.SessionList, EngineSkill.QuestionAnswer };

        Assert.Equal(
            EngineSkills.All.Where(skill => !liveSkills.Contains(skill)).ToArray(),
            EngineSkills.Broken(verdicts).Select(v => v.Skill).ToArray());
    }

    /// <summary>
    /// ДВА ОТВЕТА НЕ ВЫДАЮТСЯ ЗА ОДИН: «проверено В ЛАБОРАТОРИИ на
    /// этой версии» и «проверено ЗДЕСЬ И СЕЙЧАС» — разные утверждения
    /// (находка владельца 14.10.2026, ночь).
    ///
    /// **Что было на экране.** Рядом стояли вердикт файла подсказок
    /// («Панель проверена на этой версии целиком: все 8 умений подтверждены живым
    /// прогоном в изоляции») и сводка таблицы умений («умений подтверждено: 2 из 8»).
    /// Человек читал проверенную версию как полурабочую.
    ///
    /// Мутация, которая обязана упасть здесь: **убрать момент из сводки**
    /// (`SkillPhrases.Summary` без `Moment`) → сводка снова читается как ответ про версию.
    /// </summary>
    [Fact]
    public void Сводка_называет_МОМЕНТ_и_не_выдаёт_себя_за_запись_о_версии()
    {
        var paths = AppPaths.Under(@"C:\Проба\панель");

        // (1) ПРОБЫ НЕ БЫЛО — ТАК И СКАЗАНО: сводка НЕ молчит и НЕ выдаёт
        // вердикты файла подсказок за свои.
        var notProbed = SkillPhrases.Summary(EngineSkills.Measure(EngineSkillProbe.None(paths)));

        Assert.Contains(Loc.T(SkillKeys.MomentNotAsked), notProbed, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.SummaryFormat).Split('{')[0].Trim(), notProbed, StringComparison.Ordinal);

        // (2) ПРОБА БЫЛА — НАЗВАН ЕЁ МОМЕНТ, и назван ЧИТАЕМОЙ формой
        // панели (та же дверь, что у «Разрешение выдано»).
        var at = new DateTimeOffset(2026, 10, 14, 0, 12, 0, TimeSpan.FromHours(3));
        var probed = SkillPhrases.Summary(
            EngineSkills.Measure(EngineSkillProbe.None(paths)),
            EngineSkillProbe.None(paths) with { MeasuredAt = at });

        Assert.Contains(Pricing.PricingDecisions.CheckedText(Pricing.PricingDecisions.Stamp(at)), probed, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(SkillKeys.MomentNotAsked), probed, StringComparison.Ordinal);

        // И ОБА ОТВЕТА РАЗНЫЕ СЛОВАМИ: сводка с моментом и без — не одна строка.
        // Иначе «пробы не было» и «проба была в 00:12» читались бы одинаково.
        Assert.NotEqual(notProbed, probed);
    }

    /// <summary>Сводка называет и подтверждённые, и непроверенные — двумя числами, а не словами.</summary>
    [Fact]
    public void Сводка_называет_подтверждённые_и_непроверенные()
    {
        var verdicts = NotProbed();
        var summary = SkillPhrases.Summary(verdicts);

        Assert.Contains(EngineSkills.Confirmed(verdicts).ToString(System.Globalization.CultureInfo.InvariantCulture), summary, StringComparison.Ordinal);
        Assert.Contains(verdicts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), summary, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.UncheckedFormat, EngineSkills.Unchecked(verdicts)), summary, StringComparison.Ordinal);
    }

    /// <summary>Строка версий: версия движка и список проверенных; без списка — «ни на одной версии».</summary>
    [Fact]
    public void Строка_версий_называет_версию_и_проверенные()
    {
        var withoutList = SkillPhrases.Versions("0.1.5-rc.2", string.Empty);
        Assert.Contains("0.1.5-rc.2", withoutList, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.VersionsNone), withoutList, StringComparison.Ordinal);

        var withList = SkillPhrases.Versions("0.1.5-rc.2", "0.1.5-rc.2, 0.1.7-rc.2");
        Assert.Contains("0.1.7-rc.2", withList, StringComparison.Ordinal);
        Assert.DoesNotContain("{" + "0}", withList, StringComparison.Ordinal);

        var unknown = SkillPhrases.Versions(string.Empty, string.Empty);
        Assert.Contains(Loc.T(SkillKeys.VersionUnknown), unknown, StringComparison.Ordinal);

        // ЗНАЧЕНИЕ ДЛЯ ОТЧЁТА — то же самое, но без названия строки: отчёт показывает название
        // и значение разными столбцами, и название внутрь значения попадать не должно.
        var value = SkillPhrases.VersionsValue("0.1.5-rc.2", "0.1.4");
        Assert.Contains("0.1.5-rc.2", value, StringComparison.Ordinal);
        Assert.Contains("0.1.4", value, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T(SkillKeys.VersionsLabel), value, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.VersionsNone), SkillPhrases.VersionsValue("0.1.5-rc.2", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ ФОРМА СТРОКИ УМЕНИЯ (решение владельца 14.10.2026 по рендеру): имя — в «что», **слово
    /// состояния ВНУТРИ метки**, короткая подробность — рядом. Прежний столбец «значение» со всей
    /// строкой внутри больше не нужен: состояние несёт метка, и она не одна — со словом.
    ///
    /// Дефект, найденный 10.10.2026 живым кадром раздела «Сервер»: вся строка целиком лежала
    /// в «что», и вердикт вставал приглушённым текстом в узкой колонке слева, а правая колонка
    /// оставалась пустой. Проверка держит форму, а не только наличие слов.
    /// </summary>
    [Fact]
    public void Вердикт_лежит_в_значении_а_имя_в_что()
    {
        var paths = AppPaths.Under(@"C:\Проба\панель");

        var report = EnvironmentReport.Build(
            engine: null,
            dshVersion: "0.1.5-rc.2",
            nodeVersion: string.Empty,
            npmVersion: string.Empty,
            pnpmVersion: string.Empty,
            paths: paths,
            workDir: @"C:\Проба\работа",
            workDirConfigured: true,
            serverPort: 3081,
            portState: PanelStrings.EnvPortFree,
            skills: EngineSkills.Measure(EngineSkillProbe.None(paths)));

        var group = Assert.Single(report.Groups, g => g.Title == Loc.T(SkillKeys.Title));

        var words = new[] { SkillKeys.Checked, SkillKeys.NotChecked, SkillKeys.Trouble, SkillKeys.VersionUnread }
            .Select(key => Loc.T(key))
            .ToList();

        foreach (var skill in EngineSkills.All)
        {
            var name = Loc.T(SkillKeys.Name(skill));
            var row = Assert.Single(group.Lines, line => line.What == name);

            // 1) в «что» — ТОЛЬКО имя: иначе вся строка уезжает в узкую колонку;
            Assert.DoesNotContain(":", row.What, StringComparison.Ordinal);

            // 2) СОСТОЯНИЕ — МЕТКОЙ СО СЛОВОМ, и слово это из словаря слов, а не выдумано кодом;
            Assert.True(row.Word.Length > 0, $"у умения «{name}» нет слова в метке состояния");
            Assert.Contains(row.Word, words);
            Assert.NotEqual(EnvLineState.None, row.State);

            // 3) и ПОДРОБНОСТЬ рядом — причина или способ проверки, а не пустое место.
            Assert.True(row.Detail.Length > 0, $"у умения «{name}» нет подробности рядом с меткой");
        }

        // ЖИВАЯ ПРОБА и строка версий — тоже под своими названиями, а не целиком в «что».
        var probe = Assert.Single(group.Lines, line => line.What == Loc.T(SkillKeys.ProbeLabel));

        Assert.True(probe.Detail.Length > 0, "у строки живой пробы нет ответа «а это про сейчас?»");

        Assert.Single(group.Lines, line => line.What == Loc.T(SkillKeys.VersionsLabel) && line.Detail.Length > 0);
    }

    /// <summary>
    /// ОТЧЁТ ПОКАЗЫВАЕТ ФАКТЫ ПРОБЫ, А НЕ ДОГАДКУ (задание владельца 14.10.2026, §2).
    ///
    /// **От какого случая.** Таблица умений обязана быть ответом «что у меня СЕЙЧАС» и
    /// пересчитываться, когда сервер стал своим. Дверь `<see cref="SettingsController.SkillProbe"/>`
    /// — ровно это место: отчёт берёт из неё факты, а живая проба (`LiveSkillProbe`) кладёт туда
    /// результат, снятый В ФОНЕ и не чаще одного раза на свой сервер.
    ///
    /// ⚠️ Здесь проверяется ШОВ, а не сеть: сеть в прогоне проверки не трогается вовсе
    /// (дверь по умолчанию `None`), поэтому факты подставляются готовые — и отчёт обязан
    /// показать ИМЕННО ИХ. Так «дверь подключена» видно без живого движка.
    ///
    /// Мутация, которая обязана упасть здесь: **отчёт снова считает умения сам**
    /// (`EngineSkillProbe.None` вместо `SkillProbe.Facts()`) → проба «в 00:12» исчезает из сводки.
    /// </summary>
    [Fact]
    public void Отчёт_показывает_факты_пробы_а_не_догадку()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-probe-door", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Under(dir);

            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            // ДВЕРЬ МОЛЧИТ — и так и говорится.
            var quiet = SkillPhrases.Summary(
                EngineSkills.Measure(controller.SkillProbe.Facts()), controller.SkillProbe.Facts());

            Assert.Contains(Loc.T(SkillKeys.MomentNotAsked), quiet, StringComparison.Ordinal);

            // ПРОБА БЫЛА — и отчёт обязан показать ЕЁ момент. Момент живёт подробностью строки
            // «Живая проба умений» (решение владельца 14.10.2026 по рендеру: сводка-полотно ушла,
            // а вопрос «а это про СЕЙЧАС?» остался — и на него отвечает эта строка).
            var at = new DateTimeOffset(2026, 10, 14, 0, 12, 0, TimeSpan.FromHours(3));
            var facts = EngineSkillProbe.None(paths) with { MeasuredAt = at };

            controller.SkillProbe = SkillProbeDoor.Ready(facts);

            var group = Assert.Single(
                controller.EnvironmentInfo().Groups, g => g.Title == Loc.T(SkillKeys.Title));

            var probe = Assert.Single(
                group.Lines, line => line.What == Loc.T(SkillKeys.ProbeLabel));

            var when = Pricing.PricingDecisions.CheckedText(Pricing.PricingDecisions.Stamp(at));

            Assert.Contains(when, probe.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(Loc.T(SkillKeys.MomentNotAsked), probe.Detail, StringComparison.Ordinal);

            // И проба БЫЛА — значит строка помечена зелёной меткой «проверено», а не серым.
            Assert.Equal(EnvLineState.Ok, probe.State);
            Assert.Equal(Loc.T(SkillKeys.Checked), probe.Word);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Каталог занят — это не ошибка проверки.
            }
        }
    }

    /// <summary>
    /// ТАБЛИЦА ЖИВЁТ В ТОМ ЖЕ ОТЧЁТЕ, ЧТО И ОСТАЛЬНЫЕ ГРУППЫ: новых окон не заведено, и это
    /// проверяется тем, что группа приходит из того же разбора.
    /// </summary>
    [Fact]
    public void Таблица_умений_приходит_тем_же_отчётом()
    {
        var paths = AppPaths.Under(@"C:\Проба\панель");
        var verdicts = EngineSkills.Measure(EngineSkillProbe.None(paths));

        var report = EnvironmentReport.Build(
            engine: null,
            dshVersion: "0.1.5-rc.2",
            nodeVersion: string.Empty,
            npmVersion: string.Empty,
            pnpmVersion: string.Empty,
            paths: paths,
            workDir: @"C:\Проба\работа",
            workDirConfigured: true,
            serverPort: 3081,
            portState: PanelStrings.EnvPortFree,
            skills: verdicts);

        var group = Assert.Single(report.Groups, g => g.Title == Loc.T(SkillKeys.Title));

        // Строка на каждое умение плюс сводка и строка версий.
        Assert.Equal(verdicts.Count + 2, group.Lines.Count);

        foreach (var skill in EngineSkills.All)
        {
            var name = Loc.T(SkillKeys.Name(skill));
            Assert.Contains(group.Lines, line => line.Text().Contains(name, StringComparison.Ordinal));
        }

        Assert.Contains(group.Lines, line => line.Text().Contains("0.1.5-rc.2", StringComparison.Ordinal));
        Assert.Contains(group.Lines, line => line.Text().Contains(Loc.T(SkillKeys.NotChecked), StringComparison.Ordinal));

        // И отчёт остаётся текстом: таблица уходит и в журнал, и в отчёт самотеста.
        Assert.Contains(Loc.T(SkillKeys.Title), report.Text(), StringComparison.Ordinal);
    }

    /// <summary>Без умений группы нет вовсе — пустой таблицы в отчёте быть не должно.</summary>
    [Fact]
    public void Без_умений_таблицы_в_отчёте_нет()
    {
        var report = EnvironmentReport.Build(
            engine: null,
            dshVersion: string.Empty,
            nodeVersion: string.Empty,
            npmVersion: string.Empty,
            pnpmVersion: string.Empty,
            paths: AppPaths.Under(@"C:\Проба\панель"),
            workDir: @"C:\Проба\работа",
            workDirConfigured: true,
            serverPort: 3081,
            portState: PanelStrings.EnvPortFree);

        Assert.DoesNotContain(report.Groups, g => g.Title == Loc.T(SkillKeys.Title));
    }

    /// <summary>
    /// ТАБЛИЦА ДОХОДИТ ДО ОКНА ТОЙ ЖЕ ДВЕРЬЮ. Раздел «Сервер» берёт отчёт ровно одним вызовом
    /// (<see cref="SettingsController.EnvironmentInfo"/>) — и если умения не передать там, человек
    /// таблицы не увидит вовсе, хотя разбор её умеет.
    ///
    /// ⚠️ **Это и был непокрытый шов, найденный 10.10.2026 живым кадром:** проверок на сам разбор
    /// хватало, а на то, что он получает умения от ЕДИНСТВЕННОЙ двери окна, не было — и таблицы
    /// в разделе «Сервер» не было. Проверка идёт через настоящий контроллер под своим временным
    /// корнем (красная линия 4: чужие каталоги не читаются).
    /// </summary>
    [Fact]
    public void Отчёт_окна_приходит_с_умениями()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-skills-door", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Under(dir);
            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            var report = controller.EnvironmentInfo();
            var group = Assert.Single(report.Groups, g => g.Title == Loc.T(SkillKeys.Title));

            // Строка на КАЖДОЕ умение плюс итог и строка версий.
            Assert.Equal(EngineSkills.All.Count + 2, group.Lines.Count);
            Assert.Contains(group.Lines, line => line.What == Loc.T(SkillKeys.Name(EngineSkill.SessionList)));
            Assert.Contains(group.Lines, line => line.What == Loc.T(SkillKeys.VersionsLabel));

            // И текст отчёта — тот же: он уходит в журнал и в отчёт самотеста.
            Assert.Contains(Loc.T(SkillKeys.Title), controller.EnvironmentText(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Уборка временного корня не удалась — это не ошибка проверки.
            }
        }
    }
}
