using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СОБСТВЕННЫЙ ЖУРНАЛ ПАНЕЛИ В КОПИЮ НЕ ИДЁТ (2.2.1).
///
/// **От какого случая.** В 2.2.0 в копию попадал `state\panel.log` — тот самый файл, который
/// писатель журнала держит без общего доступа. Копия читала его и отказывала
/// (`IOException: being used by another process`), отчёт говорил «копия не снята», отказ вёл
/// к уведомлению («Копия не снята») — и до 2.2.1 это уведомление роняло панель
/// (<c>_team\220\defekt-start-crash.md</c>). Наблюдено дважды: 1 из 6 первых запусков в t17
/// и 1 из 1 в детерминированном опыте t18.
///
/// **Что проверяется.** (1) План копии называет файл в пропуске и говорит об этом человеку
/// замечанием; (2) НАСТОЯЩАЯ копия снимается, даже когда журнал заперт так же, как его держит
/// панель, и журнала в архиве нет, а настройки есть. Мутация «убрать пропуск журнала из плана»
/// валит обе проверки: первая — по пропуску, вторая — по отказу копии (файл занят).
/// </summary>
public class BackupPanelLogTests
{
    private const string Family = "backup-panel-log";

    private static AppPaths NewRoot(out string root)
    {
        root = TempRoots.For(Family);
        var paths = AppPaths.Under(root);

        // Настройки панели и её состояние — то, что обязано остаться в копии.
        Directory.CreateDirectory(paths.StateDir);
        File.WriteAllText(Path.Combine(paths.Root, "settings.json"), "{}\n");
        File.WriteAllText(Path.Combine(paths.StateDir, "installed.json"), "{}\n");
        File.WriteAllText(paths.LogFile, "2026-10-19 09:00:00 строка журнала панели\n");

        return paths;
    }

    private static BackupPlan PlanFor(AppPaths paths, string root) =>
        BackupPlanner.Full(
            paths,
            workingDirectory: Path.Combine(root, "projects"),
            engine: null,
            withEngine: false);

    [Fact]
    public void План_копии_называет_журнал_панели_в_пропуске_и_говорит_об_этом_человеку()
    {
        var paths = NewRoot(out var root);
        try
        {
            var plan = PlanFor(paths, root);
            Assert.True(plan.Ok, plan.Error);

            var panel = plan.Roots.Single(r => r.Kind == BackupRootKind.Panel);

            Assert.NotNull(panel.Source.SkipFileNames);
            Assert.True(
                panel.Source.SkipFileNames!.Any(name => string.Equals(name, AppPaths.LogFileName, StringComparison.OrdinalIgnoreCase)),
                "в пропуске корня панели нет журнала панели: " + string.Join(", ", panel.Source.SkipFileNames!));

            // И человеку это сказано словами: журнал не вернётся накатом, история начнётся заново.
            Assert.Contains(PanelStrings.BackupPanelLogNotCopiedNote, plan.Notes);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// Настоящая копия при ЗАПЕРТОМ журнале: так его держит панель. До 2.2.1 копия на этом
    /// отказывала — проверка обязана проходить через то состояние, в котором дефект возможен.
    /// </summary>
    [Fact]
    public void Копия_снимается_когда_журнал_панели_заперт_а_самого_журнала_в_архиве_нет()
    {
        var paths = NewRoot(out var root);
        try
        {
            var plan = PlanFor(paths, root);
            Assert.True(plan.Ok, plan.Error);

            var archive = Path.Combine(paths.BackupsDir, "copy.zip");
            Directory.CreateDirectory(paths.BackupsDir);

            // Писатель журнала держит файл без общего доступа — это и есть условие дефекта 2.2.0.
            using (var locked = new FileStream(paths.LogFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = BackupEngine.Run(
                    plan,
                    new BackupRequest(archive, SevenZipPath: string.Empty, Verify: false),
                    paths);

                Assert.True(result.Ok, result.Error);
                Assert.True(File.Exists(archive), "архив копии не создан");

                using var zip = ZipFile.OpenRead(archive);
                var names = zip.Entries.Select(e => e.FullName).ToArray();

                Assert.True(
                    !names.Any(name => name.EndsWith("/" + AppPaths.LogFileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, AppPaths.LogFileName, StringComparison.OrdinalIgnoreCase)),
                    "журнал панели всё-таки попал в архив: " + string.Join(", ", names));

                // Данные человека в копии остались: пропуск касается ТОЛЬКО журнала панели.
                Assert.True(names.Any(name => name.EndsWith("/settings.json", StringComparison.OrdinalIgnoreCase)), "в архиве нет настроек: " + string.Join(", ", names));
                Assert.True(names.Any(name => name.EndsWith("/installed.json", StringComparison.OrdinalIgnoreCase)), "в архиве нет состояния: " + string.Join(", ", names));

                // И запертый файл не остался на диске занятым нашим же чтением.
                Assert.True(locked.CanRead);
            }
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }
}

