using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТРИ РЕЖИМА ОБЪЁМА КОПИИ (v2.2) — формулировка владельца из `PROJECT.md` §3.3.
///
/// Критерий владельца звучит так: **«три режима дают РАЗНЫЕ объёмы»**. Здесь он и доказывается —
/// ЗАМЕРОМ на подставном дереве, а не рассуждением: дерево-фикстура собирается заново в каждой
/// проверке (мусор: <c>node_modules</c>, <c>bin</c>, <c>obj</c>, <c>logs</c>, <c>.git</c>, плюс
/// <c>vendor</c> и <c>target</c> для своего фильтра), и для каждого режима считаются
/// и НАБОР каталогов, и СУММА размеров.
///
/// Данные владельца при этом не читаются вовсе: всё под <see cref="AppPaths.Under"/> —
/// у изолированного прогона своего корня нет ничего снаружи.
/// </summary>
public class BackupScopeTests
{
    // --- замер на подставном дереве -----------------------------------------

    private sealed record Measured(BackupPlan Plan, IReadOnlyList<string> Directories, long Bytes);

    /// <summary>Режим ногой по дереву: план, набор каталогов и сумма размеров данных копии.</summary>
    private static Measured Measure(AppPaths paths, string working, BackupScope scope, params string[] extras)
    {
        var plan = BackupPlanner.Full(
            paths, working, engine: null, withEngine: false, scope: scope, extraExclusions: extras);

        var directories = new List<string>();

        foreach (var root in plan.Roots)
        {
            foreach (var directory in ZipWriter.Directories(root.Source, ZipWriter.DefaultSkipDirectoryNames))
            {
                directories.Add(root.Prefix + "/" + Path.GetRelativePath(root.Directory, directory).Replace('\\', '/'));
            }
        }

        return new Measured(plan, directories, ZipWriter.EstimateBytes(plan.Sources));
    }

    /// <summary>
    /// СУММЫ ТРЁХ РЕЖИМОВ — с точностью до байта, на НАЗВАННОЙ фикстуре
    /// (<see cref="BuildTree"/>). Эти числа цитируются в документах, поэтому они и проверяются,
    /// а не «примерно больше/меньше»: числа из отчёта обязаны воспроизводиться прогоном.
    ///
    /// **Состав фикстуры (12 файлов, 264 350 Б):**
    ///
    /// | файл | байт |
    /// |---|---|
    /// | `projects/app/src/main.cs` | 100 |
    /// | `projects/app/node_modules/dep/index.js` | 4 000 |
    /// | `projects/app/bin/out.dll` | 8 000 |
    /// | `projects/app/obj/tmp.o` | 16 000 |
    /// | `projects/app/logs/run.log` | 32 000 |
    /// | `projects/app/vendor/lib.php` | 64 000 |
    /// | `projects/app/target/debug.bin` | 128 000 |
    /// | `projects/app/.git/objects/aa` | 2 000 |
    /// | `projects/app/.git/logs/HEAD` | 500 |
    /// | `dsh-home/settings.yaml` | 50 |
    /// | `dsh-home/sessions/s1.json` | 700 |
    /// | `dsh-home/node_modules/pkg/index.js` | 9 000 |
    ///
    /// ⚠️ **Корней ТРИ, и третий — корень прогона целиком** (`AppPaths.Under`: папка панели,
    /// внутри которой лежат и `projects`, и `dsh-home`). Поэтому суммы считаются по всем трём,
    /// и любая правка фикстуры меняет их сразу в нескольких местах. Именно поэтому фикстура названа
    /// здесь числами: другая фикстура даёт другие суммы, и расхождение чисел в отчёте с прогоном
    /// означает разошедшуюся фикстуру, а не «устаревший отчёт» (разбор — 26.09.2026, вопрос
    /// холодного проверяющего о 510,0 КБ против 516,3 КБ).
    ///
    /// Откуда числа: автоматический = 9 750 (домашний каталог движка, `node_modules` там содержимое)
    /// + 194 600 (проекты без мусора 60 000) + 195 350 (корень прогона без мусора 69 000);
    /// полный = 9 750 + 254 600 + 264 350; свой фильтр = 9 750 + 2 600 + 3 350.
    /// </summary>
    [Fact]
    public void Суммы_трёх_режимов_сходятся_с_замером_на_названной_фикстуре()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var working = BuildTree(paths);

            var auto = Measure(paths, working, BackupScope.Auto);
            var full = Measure(paths, working, BackupScope.Full);
            var custom = Measure(paths, working, BackupScope.Custom, "vendor", "target");

            Assert.Equal(399_700, auto.Bytes);
            Assert.Equal(528_700, full.Bytes);
            Assert.Equal(15_700, custom.Bytes);

            // Те же числа словами — ровно так они и стоят в отчёте и в документах.
            Assert.Equal(390.3, Math.Round(auto.Bytes / 1024.0, 1));
            Assert.Equal(516.3, Math.Round(full.Bytes / 1024.0, 1));
            Assert.Equal(15.3, Math.Round(custom.Bytes / 1024.0, 1));

            // И корней ровно три у каждого режима: домашний каталог движка, проекты и корень прогона.
            Assert.Equal(3, auto.Plan.Roots.Count);
            Assert.Equal(3, full.Plan.Roots.Count);
            Assert.Equal(3, custom.Plan.Roots.Count);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// РЕЖИМЫ ОБЪЁМА
    ///
    /// Критерий владельца: **«три режима дают РАЗНЫЕ объёмы»**. Здесь он доказывается тем, что
    /// наборы каталогов и суммы РАЗЛИЧАЮТСЯ по существу (точные суммы — в соседней проверке).
    /// </summary>
    [Fact]
    public void Три_режима_дают_разные_наборы_и_разные_суммы()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var working = BuildTree(paths);

            var auto = Measure(paths, working, BackupScope.Auto);
            var full = Measure(paths, working, BackupScope.Full);
            var custom = Measure(paths, working, BackupScope.Custom, "vendor", "target");

            // Суммы: полный БОЛЬШЕ автоматического, свой фильтр — МЕНЬШЕ автоматического.
            // Три разных числа, а не «примерно одинаковые»: ровно этого владелец и требовал.
            Assert.True(
                full.Bytes > auto.Bytes,
                $"полный режим обязан быть больше: {full.Bytes} против {auto.Bytes}");
            Assert.True(
                auto.Bytes > custom.Bytes,
                $"свой фильтр обязан быть меньше автоматического: {auto.Bytes} против {custom.Bytes}");

            // Наборы каталогов тоже разные, и это видно по именам, а не по числам.
            Assert.Contains("projects/app/node_modules", full.Directories);
            Assert.DoesNotContain("projects/app/node_modules", auto.Directories);
            Assert.Contains("projects/app/bin", full.Directories);
            Assert.DoesNotContain("projects/app/bin", auto.Directories);
            Assert.Contains("projects/app/obj", full.Directories);
            Assert.DoesNotContain("projects/app/obj", auto.Directories);
            Assert.Contains("projects/app/logs", full.Directories);
            Assert.DoesNotContain("projects/app/logs", auto.Directories);

            // `.git` остаётся во ВСЕХ режимах — это отдельная часть решения владельца,
            // и терять её при «своём фильтре» было бы тихой потерей истории. Внутренний `logs`
            // (reflog) едет вместе с ним: иначе история сохранилась бы не вся.
            Assert.Contains("projects/app/.git", auto.Directories);
            Assert.Contains("projects/app/.git/logs", auto.Directories);
            Assert.Contains("projects/app/.git", full.Directories);
            Assert.Contains("projects/app/.git/logs", full.Directories);
            Assert.Contains("projects/app/.git", custom.Directories);

            // Свой фильтр убирает ровно названное — и ничего сверх.
            Assert.Contains("projects/app/vendor", auto.Directories);
            Assert.DoesNotContain("projects/app/vendor", custom.Directories);
            Assert.Contains("projects/app/target", auto.Directories);
            Assert.DoesNotContain("projects/app/target", custom.Directories);

            // Домашний каталог движка: в автоматическом режиме node_modules там СОДЕРЖИМОЕ,
            // а не мусор, — иначе копия профиля перестала бы работать после наката.
            Assert.Contains("dsh-home/node_modules", auto.Directories);
            Assert.Contains("dsh-home/node_modules", custom.Directories);
            Assert.Contains("dsh-home/node_modules", full.Directories);

            // И причина в отчёте: у полного режима сказано, что он ничего не пропускает.
            Assert.Contains(PanelStrings.BackupScopeFullNote, full.Plan.Notes);
            Assert.Contains(
                string.Format(PanelStrings.BackupScopeCustomNoteFormat, "vendor, target"),
                custom.Plan.Notes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Полный режим ничего не пропускает: у КАЖДОГО корня нет ни своего списка пропуска,
    /// ни списка «берём целиком» — «всё как есть» сказано одинаково для всех корней, и одного
    /// забытого корня быть не может.
    /// </summary>
    [Fact]
    public void Полный_режим_не_пропускает_ничего_ни_в_одном_корне()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var working = BuildTree(paths);

            var full = BackupPlanner.Full(paths, working, engine: null, withEngine: false, scope: BackupScope.Full);

            Assert.NotEmpty(full.Roots);
            foreach (var item in full.Roots)
            {
                Assert.True(item.Source.Everything, $"{item.Prefix}: полный режим обязан обходить корень целиком");
                Assert.Null(item.Source.SkipDirectoryNames);
                Assert.Null(item.Source.WholeDirectoryNames);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Умолчание — АВТОМАТИЧЕСКИЙ режим, и это проверяется не по подписи, а по вызову без режима:
    /// так зовёт планировщик предохранительная копия перед накатом, и её поведение меняться
    /// не должно (иначе накат потяжелел бы в разы на ровном месте).
    /// </summary>
    [Fact]
    public void Без_режима_работает_автоматический()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var working = BuildTree(paths);

            var byDefault = BackupPlanner.Full(paths, working, engine: null, withEngine: false);
            var explicitly = BackupPlanner.Full(
                paths, working, engine: null, withEngine: false, scope: BackupScope.Auto);

            Assert.Equal(explicitly.Sources.Count, byDefault.Sources.Count);

            for (var i = 0; i < byDefault.Roots.Count; i++)
            {
                Assert.Equal(explicitly.Roots[i].Source.SkipDirectoryNames, byDefault.Roots[i].Source.SkipDirectoryNames);
                Assert.Equal(explicitly.Roots[i].Source.Everything, byDefault.Roots[i].Source.Everything);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Полный режим и названные лишние имена вместе: фильтр НЕ действует, и план говорит об этом
    /// словами. Молча выключенный фильтр выглядел бы забытым.
    /// </summary>
    [Fact]
    public void Полный_режим_говорит_что_лишние_имена_не_действуют()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.DshHome);

            var plan = BackupPlanner.Full(
                paths, null, engine: null, withEngine: false,
                scope: BackupScope.Full, extraExclusions: new[] { "vendor" });

            Assert.Contains(PanelStrings.BackupScopeExtrasIgnored, plan.Notes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Замер ДО копии не выдумывает число: у плана без корней он честно даёт ноль, а не «примерно
    /// сколько-нибудь». Так выглядит ЧИСТАЯ машина — та, ради которой делается накат, — и обещать
    /// на ней размер было бы враньём.
    /// </summary>
    [Fact]
    public void Замер_пустого_плана_даёт_ноль()
    {
        // Корня НЕТ вовсе: ни домашнего каталога движка, ни папки панели, ни рабочей папки.
        var missing = Path.Combine(Path.GetTempPath(), "dsh-scope-нет-" + Guid.NewGuid().ToString("N"));

        var plan = BackupPlanner.Full(AppPaths.Under(missing), null, engine: null, withEngine: false);

        Assert.True(plan.NothingToLose);
        Assert.Empty(plan.Sources);
        Assert.Equal(0, ZipWriter.EstimateBytes(plan.Sources));
    }

    // --- имена режимов в файле настроек -------------------------------------

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("Full", "full")]
    [InlineData("custom", "custom")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData("полный", "auto")]
    [InlineData("thorough", "auto")]
    [InlineData(null, "auto")]
    public void Мусор_в_режиме_означает_автоматический(string? value, string expected)
    {
        Assert.Equal(expected, PanelSettings.NormalizeBackupScope(value));
        Assert.Equal(expected, BackupScopeDecisions.Normalize(value));
    }

    [Theory]
    [InlineData("full", BackupScope.Full)]
    [InlineData("custom", BackupScope.Custom)]
    [InlineData("мусор", BackupScope.Auto)]
    public void Режим_разбирается_из_значения(string value, BackupScope expected)
    {
        Assert.Equal(expected, BackupScopeDecisions.Parse(value));
    }

    /// <summary>Номер режима в списке окна и обратный ход — одно место на всю панель.</summary>
    [Fact]
    public void Номера_режимов_сходятся_с_порядком_списка()
    {
        Assert.Equal(0, BackupScopeDecisions.IndexOf(BackupScope.Auto));
        Assert.Equal(1, BackupScopeDecisions.IndexOf(BackupScope.Full));
        Assert.Equal(2, BackupScopeDecisions.IndexOf(BackupScope.Custom));

        Assert.Equal(BackupScope.Auto, BackupScopeDecisions.FromIndex(0));
        Assert.Equal(BackupScope.Full, BackupScopeDecisions.FromIndex(1));
        Assert.Equal(BackupScope.Custom, BackupScopeDecisions.FromIndex(2));

        // Номер вне списка — автоматический, а не исключение в окне.
        Assert.Equal(BackupScope.Auto, BackupScopeDecisions.FromIndex(99));
        Assert.Equal(BackupScope.Auto, BackupScopeDecisions.FromIndex(-1));
    }

    // --- мусор в настройках --------------------------------------------------

    [Theory]
    [InlineData(24, 24)]
    [InlineData(1, 1)]
    [InlineData(168, 168)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(169, 168)]
    [InlineData(100000, 168)]
    public void Часы_приводятся_к_границам(int written, int expected)
    {
        Assert.Equal(expected, PanelSettings.ClampBackupHours(written));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(21, 20)]
    public void Предел_хранения_приводится_к_границам(int written, int expected)
    {
        Assert.Equal(expected, PanelSettings.ClampBackupKeep(written));
    }

    /// <summary>
    /// Лишние имена чистятся как каталоги ключей: пустые выбрасываются, пробелы снимаются,
    /// повторы (без учёта регистра) — тоже, а ПОРЯДОК сохраняется: его видит человек в окне.
    /// </summary>
    [Fact]
    public void Лишние_имена_чистятся_но_порядок_сохраняется()
    {
        var clean = PanelSettings.NormalizeExtraExclusions(new[]
        {
            "  vendor ", "", "   ", "TARGET", "target", ".venv", "vendor",
        });

        Assert.Equal(new[] { "vendor", "TARGET", ".venv" }, clean);
        Assert.Empty(PanelSettings.NormalizeExtraExclusions(null));
        Assert.Empty(PanelSettings.NormalizeExtraExclusions(Array.Empty<string>()));
    }

    /// <summary>
    /// Та же нормализация стоит на двери хранилища: и чтение, и запись файла проходят через
    /// <c>SettingsStore.Clean</c>, поэтому «в файле мусор, в памяти — границы» разойтись не могут.
    /// </summary>
    [Fact]
    public void Файл_настроек_с_мусором_читается_в_границах()
    {
        var root = NewRoot();

        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));

            File.WriteAllText(
                store.Describe,
                """
                {
                  "backupScheduleEnabled": true,
                  "backupEveryHours": 99999,
                  "backupKeepCount": -4,
                  "backupScope": "полный",
                  "backupExtraExclusions": [" vendor ", "vendor", ""]
                }
                """);

            var load = store.Load();

            Assert.True(load.Ok, load.Problem);
            Assert.Equal(PanelSettings.BackupHoursMax, load.Settings.BackupEveryHours);
            Assert.Equal(PanelSettings.BackupKeepMin, load.Settings.BackupKeepCount);
            Assert.Equal(BackupScopeDecisions.Auto, load.Settings.BackupScope);
            Assert.Equal(new[] { "vendor" }, load.Settings.BackupExtraExclusions);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Записанное и прочитанное сходится: режим, список и расписание переживают круг «сохранить →
    /// прочитать файл». Без этого «сохранил» означало бы только «не упало».
    /// </summary>
    [Fact]
    public void Режим_и_расписание_переживают_круг_файла()
    {
        var root = NewRoot();

        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));

            var written = SettingsStore.Clean(new PanelSettings
            {
                BackupScheduleEnabled = false,
                BackupEveryHours = 6,
                BackupKeepCount = 5,
                BackupScope = "custom",
                BackupExtraExclusions = new List<string> { "vendor", "target" },
            });

            Assert.True(store.Save(written));

            var load = store.Load();
            Assert.True(load.Ok, load.Problem);
            Assert.False(load.Settings.BackupScheduleEnabled);
            Assert.Equal(6, load.Settings.BackupEveryHours);
            Assert.Equal(5, load.Settings.BackupKeepCount);
            Assert.Equal("custom", load.Settings.BackupScope);
            Assert.Equal(new[] { "vendor", "target" }, load.Settings.BackupExtraExclusions);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Умолчания — те, что обещаны человеку, перешедшему с 1.x: автокопия включена, каждые 24 часа,
    /// хранить 2 последних (формулировка v1), режим — автоматический.
    /// </summary>
    [Fact]
    public void Умолчания_совпадают_с_v1()
    {
        var fresh = PanelSettings.Default;

        Assert.True(fresh.BackupScheduleEnabled);
        Assert.Equal(24, fresh.BackupEveryHours);
        Assert.Equal(2, fresh.BackupKeepCount);
        Assert.Equal(BackupScopeDecisions.Auto, fresh.BackupScope);
        Assert.Empty(fresh.BackupExtraExclusions);
    }

    // --- подсказка поля лишних имён: короткая и одинаковая во всех языках --------------------

    /// <summary>
    /// Подсказка поля лишних имён — ПРИМЕР, а не второе объяснение режима (решение дирижёра
    /// 26.09.2026: пояснение режима и подсказка поля видны одновременно и говорили одно и то же).
    ///
    /// Проверка держит два свойства, и оба нужны, потому что **значения словарей ничем не
    /// сверяются**: ворота смотрят ключи и подстановки, а не смысл, и «в одном языке осталась
    /// старая длинная подсказка» прошло бы молча (проверено мутацией 26.09.2026 — ни
    /// `LocalizationTests`, ни `--lang-selftest` этого не видят):
    ///
    /// 1. **коротка** во всех трёх языках — подсказка не может быть объяснением;
    /// 2. **не повторяет** оговорку про «ни в одном корне»: она принадлежит пояснению режима,
    ///    и в подсказке поля её быть не должно. Фразы записаны таблицей, а не выведены формулой:
    ///    формула повторяла бы текст и не заметила бы, что его поменяли.
    /// </summary>
    [Fact]
    public void Подсказка_поля_лишних_имён_коротка_и_не_повторяет_режим()
    {
        var name = nameof(PanelStrings.BackupScopeExtraHint);

        foreach (var language in DshPanel.Localization.Loc.Known)
        {
            var text = DshPanel.Localization.Loc.TIn(language, name);

            Assert.NotEqual(name, text);
            Assert.True(text.Length <= 60, $"{language}: подсказка поля разрослась до {text.Length} знаков — «{text}»");
        }

        // Оговорка «ни в одном корне / in every root / 任何根目录» — дело пояснения режима.
        foreach (var (language, clause) in new[]
                 {
                     ("ru", "ни в одном корне"),
                     ("en", "in every root"),
                     ("zh", "任何根目录"),
                 })
        {
            Assert.DoesNotContain(clause, DshPanel.Localization.Loc.TIn(language, name), StringComparison.Ordinal);
        }

        // И примеры имён папок остаются латиницей во всех языках: их пишут в поле руками.
        foreach (var language in DshPanel.Localization.Loc.Known)
        {
            var text = DshPanel.Localization.Loc.TIn(language, name);

            Assert.Contains("vendor", text, StringComparison.Ordinal);
            Assert.Contains("target", text, StringComparison.Ordinal);
        }
    }

    // --- дерево-фикстура -----------------------------------------------------

    /// <summary>
    /// Подставное дерево: рабочая папка с проектом, домашний каталог движка и мусор в обоих.
    /// Размеры файлов РАЗНЫЕ и нарочно непохожие — по сумме видно, какой мусор откуда выпал.
    /// </summary>
    private static string BuildTree(AppPaths paths)
    {
        var working = Path.Combine(paths.Root, "projects");
        var project = Path.Combine(working, "app");

        Write(Path.Combine(project, "src", "main.cs"), 100);
        Write(Path.Combine(project, "node_modules", "dep", "index.js"), 4000);
        Write(Path.Combine(project, "bin", "out.dll"), 8000);
        Write(Path.Combine(project, "obj", "tmp.o"), 16000);
        Write(Path.Combine(project, "logs", "run.log"), 32000);
        Write(Path.Combine(project, "vendor", "lib.php"), 64000);
        Write(Path.Combine(project, "target", "debug.bin"), 128000);

        // `.git` берётся ЦЕЛИКОМ, включая свой `logs` (reflog) — иначе история сохранилась бы не вся.
        Write(Path.Combine(project, ".git", "objects", "aa"), 2000);
        Write(Path.Combine(project, ".git", "logs", "HEAD"), 500);

        // Домашний каталог движка: node_modules здесь — СОДЕРЖИМОЕ профиля, а не мусор.
        Write(Path.Combine(paths.DshHome, "settings.yaml"), 50);
        Write(Path.Combine(paths.DshHome, "sessions", "s1.json"), 700);
        Write(Path.Combine(paths.DshHome, "node_modules", "pkg", "index.js"), 9000);

        return working;
    }

    private static void Write(string path, int bytes)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, new string('x', bytes));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch
        {
            // Уборка подставного дерева не имеет права уронить проверку.
        }
    }
}
