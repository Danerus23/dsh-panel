using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПУСТАЯ МАШИНА — то состояние, ради которого накат и делается (дефект Д1), — и то, чем оно
/// отличается от «план не собрался».
///
/// Здесь проверяются три вещи, каждая из которых до 26.09.2026 была сломана:
///
/// 1. **Предохранительная копия на пустой машине.** Корней нет ни одного, план пуст — и это
///    НЕ отказ, а «терять нечего». До правки накат на чистую машину был невозможен вовсе:
///    <c>BackupEngine</c> отвечал «копию так не собрать: » (пустой причиной), а <c>RestoreEngine</c>
///    на это отвечал «накат без пути назад не делаем». Приёмка на откатанном снимке дала ровно
///    этот отказ — при том что терять на снимке было нечего.
/// 2. **Причина отказа не пуста.** «копию так не собрать: » — ноль вместо слов.
/// 3. **Ложь про группы (Д5).** Записи НЕВЫБРАННЫХ групп назывались «запись без группы в описи»:
///    при выключенном согласии на движок отчёт печатал «записей без группы в описи: 29 898»,
///    и настоящий сигнал (запись, у которой вида нет вовсе) в этом шуме тонул.
///
/// Стенд — временный каталог в <c>%TEMP%</c> (с пробелом в пути, как требует правило проекта):
/// ни одного касания каталогов владельца. Архивы собираются вручную — случаи здесь такие, какие
/// движок копии создать не может (запись без группы вовсе).
/// </summary>
public class RestoreEmptyMachineTests
{
    // --- стенд ---------------------------------------------------------------

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh empty " + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string MakeArchive(string root, string name, BackupManifest manifest, Action<ZipArchive> fill)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            fill(zip);
            AddText(zip, ZipLayout.ManifestEntry, JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
        }

        return path;
    }

    /// <summary>
    /// Архив ЛЕЖИТ НА ПУСТОЙ МАШИНЕ: мест копии на ней нет вовсе, и сборка архива их не создаёт.
    ///
    /// ⚠️ Собирать архив внутри корня прогона здесь НЕЛЬЗЯ, и это не придирка: <c>AppPaths.Under</c>
    /// объявляет «папкой панели» сам корень, а архив, лежащий в нём, делает этот корень
    /// существующим — то есть предохранительная копия честно снимается, и состояние дефекта
    /// не достигается вовсе. На приёмке корнем был <c>C:\Lab</c> — каталог с перенесённым архивом,
    /// а папки панели и домашнего каталога движка на снимке не было ни одной.
    ///
    /// Поэтому архив собирается в стороне, а прогон зовётся на корне, которого НЕТ: тогда
    /// ни одного из трёх мест копии на диске не существует.
    /// </summary>
    private static string ArchiveOutside(string area, string name, BackupManifest manifest, Action<ZipArchive> fill)
    {
        var made = MakeArchive(Path.Combine(area, "раздача"), name, manifest, fill);
        var path = Path.Combine(area, name);
        File.Move(made, path);
        Directory.Delete(Path.Combine(area, "раздача"), recursive: true);
        return path;
    }

    // --- Д1 и Д2: пустой план ------------------------------------------------

    /// <summary>
    /// План при отсутствующих корнях: настоящей причины отказа нет, корней нет — значит «терять
    /// нечего». Проверка идёт ЧЕРЕЗ ЭТО СОСТОЯНИЕ, а не через подставной план: каталоги
    /// <see cref="AppPaths.Under"/> не создаются вовсе.
    /// </summary>
    [Fact]
    public void План_при_отсутствующих_корнях_говорит_терять_нечего()
    {
        var root = NewRoot();
        try
        {
            var plan = BackupPlanner.Full(AppPaths.Under(root), workingDirectory: null, engine: null);

            Assert.Empty(plan.Roots);
            Assert.Equal(string.Empty, plan.Error);

            Assert.True(plan.NothingToLose, "пустой план без причины обязан зваться «терять нечего»");
            Assert.False(plan.Ok, "снимать копию не из чего: план не годен");

            // Причина отказа без слов не существует ни в одном из двух видов строки.
            var summary = plan.Summary();
            Assert.NotEqual(string.Empty, summary);
            Assert.DoesNotContain("копию так не собрать: ", summary, StringComparison.Ordinal);
            Assert.Contains("терять", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Д2: движок копии не подставляет пустую причину. Строка «копию так не собрать: » с нулём
    /// слов — это то, что читал человек; теперь причина названа, и она называет машину пустой.
    /// </summary>
    [Fact]
    public void Движок_копии_на_пустой_машине_называет_причину_словами()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var plan = BackupPlanner.Full(paths, workingDirectory: null, engine: null);

            var result = BackupEngine.Run(
                plan,
                new BackupRequest(Path.Combine(root, "copy.zip"), SevenZipPath: "", Verify: false),
                paths);

            Assert.False(result.Ok);
            Assert.Contains("копию так не собрать", result.Error, StringComparison.Ordinal);

            // Ноль вместо слов — ровно то, чего быть не должно.
            Assert.DoesNotContain("собрать: »", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("собрать: \"", result.Error, StringComparison.Ordinal);
            Assert.Contains("терять", result.Error, StringComparison.OrdinalIgnoreCase);

            // Архива на пустой машине не появилось: «нечего копировать» — не «сняли пустую копию».
            Assert.False(File.Exists(Path.Combine(root, "copy.zip")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- Д1: накат на пустую машину доходит до конца --------------------------

    /// <summary>
    /// Накат на ПУСТУЮ машину: корней для предохранительной копии нет, и это не повод отказать.
    /// Проверка проходит через само состояние дефекта: ни домашнего каталога движка, ни рабочей
    /// папки, ни папки панели — их создаёт САМ НАКАТ.
    ///
    /// До правки здесь был отказ «предохранительную копию снять не удалось (копию так не собрать: )»,
    /// то есть критерий этапа 4 («копия развёрнута на ЧИСТУЮ машину») был невыполним.
    /// </summary>
    [Fact]
    public void Накат_на_пустую_машину_не_требует_предохранительной_копии()
    {
        var area = NewRoot();
        var runRoot = Path.Combine(area, "машина");

        try
        {
            Directory.CreateDirectory(area);

            // Пустая машина: корня прогона, домашнего каталога движка и папки панели нет вовсе.
            var paths = AppPaths.Under(runRoot);

            Assert.False(Directory.Exists(paths.Root), "папка панели существует — это не пустая машина");
            Assert.False(Directory.Exists(paths.DshHome), "домашний каталог движка существует — не пустая машина");

            // Прежде чем что-либо делать: корней нет ни одного, и это «терять нечего».
            var before = BackupPlanner.Full(paths, workingDirectory: null, engine: null, withEngine: false);
            Assert.Empty(before.Roots);
            Assert.True(before.NothingToLose, before.Summary());

            var archive = ArchiveOutside(area, "copy.zip", Manifest("dsh-home", paths.DshHome), zip =>
                AddText(zip, "dsh-home/sessions/probe/session.v3.jsonl.zstd", "{\"turn\":1}\n"));

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);
            Assert.True(plan.Ok, plan.Error);

            var result = RestoreEngine.Run(plan, new RestoreOptions(), paths);

            Assert.True(result.Ok, result.Error);

            // Файл из копии лёг: накат действительно сделан, а не «пропущен целиком».
            var landed = Path.Combine(paths.DshHome, "sessions", "probe", "session.v3.jsonl.zstd");
            Assert.True(File.Exists(landed), "файл копии не лёг на место");

            // Предохранительной копии НЕТ — и об этом сказано словами в отчёте наката.
            Assert.Null(result.SafetyPath);
            Assert.Contains(result.Notes, note =>
                note.Contains("предохранительная копия не нужна", StringComparison.Ordinal));
            Assert.Contains(result.Notes, note =>
                note.Contains("терять", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(area);
        }
    }

    /// <summary>
    /// Обратная сторона Д1: предохранительная копия снимается ТАМ, ГДЕ ЕСТЬ ЧТО ТЕРЯТЬ. Здесь
    /// домашний каталог движка уже существует — значит путь назад обязан быть, и замечания
    /// «терять нечего» в отчёте появиться не должно.
    /// </summary>
    [Fact]
    public void Предохранительная_копия_снимается_когда_корни_есть()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            WriteFile(Path.Combine(paths.DshHome, "sessions", "a.txt"), "A\n");

            var archive = MakeArchive(root, "copy.zip", Manifest("dsh-home", paths.DshHome), zip =>
                AddText(zip, "dsh-home/sessions/probe.txt", "B\n"));

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);
            Assert.True(plan.Ok, plan.Error);

            var result = RestoreEngine.Run(plan, new RestoreOptions(), paths);

            Assert.True(result.Ok, result.Error);
            Assert.NotNull(result.SafetyPath);
            Assert.True(File.Exists(result.SafetyPath));
            Assert.DoesNotContain(result.Notes, note =>
                note.Contains("предохранительная копия не нужна", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- Д5: невыбранная группа — не «без группы» ---------------------------

    /// <summary>
    /// Записи группы, которую человек НЕ выбрал (движок без согласия), обязаны называться своим
    /// именем, а настоящий сигнал — запись, которой в описи нет вовсе, — остаётся отдельным.
    /// До правки оба случая печатались одной строкой «запись без группы в описи», и на живом
    /// прогоне это дало «29 898» — ложь, потому что группа в описи ЕСТЬ.
    /// </summary>
    [Fact]
    public void Невыбранная_группа_не_называется_записью_без_группы()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var manifest = Manifest("engine-npm", @"C:\Users\Кто-тоДругой\AppData\Roaming\npm");
            manifest.Kinds["engine-npm"] = BackupRootKinds.EnginePackages;
            manifest.Paths["dsh-home"] = paths.DshHome;
            manifest.Kinds["dsh-home"] = BackupRootKinds.DshHome;

            var archive = MakeArchive(root, "copy.zip", manifest, zip =>
            {
                // Группа движка: в описи она ЕСТЬ, но не выбрана (согласия нет) — три записи.
                AddText(zip, "engine-npm/node_modules/@deepseek-ai/dsh/lib/bin.js", "// engine\n");
                AddText(zip, "engine-npm/node_modules/@deepseek-ai/dsh/package.json", "{}\n");
                AddText(zip, "engine-npm/dsh.cmd", "@echo off\n");

                // Группа выбрана и раскладывается.
                AddText(zip, "dsh-home/sessions/probe.txt", "S\n");

                // А это НАСТОЯЩИЙ сигнал: группы «scratch» в описи нет вовсе.
                AddText(zip, "scratch/junk.txt", "J\n");
            });

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);
            Assert.True(plan.Ok, plan.Error);

            var result = RestoreEngine.Run(plan, new RestoreOptions(), paths);
            Assert.True(result.Ok, result.Error);

            var report = result.Summary();

            // Невыбранная группа названа своим именем и своим числом.
            Assert.Contains("записей невыбранных групп: 3", report, StringComparison.Ordinal);

            // Настоящий сигнал — отдельной строкой и отдельным числом.
            Assert.Contains("записей БЕЗ ГРУППЫ в описи: 1", report, StringComparison.Ordinal);

            // Ложная строка, которой называли ОБА случая, больше не появляется ни в одном виде.
            Assert.DoesNotContain("записей без группы в описи", report, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Та же развязка на уровне ПЛАНА, без раскладки вовсе: движок в копии есть, согласия нет —
    /// и в плане это названо причиной, по которой группа не раскладывается, а не пропажей группы.
    /// </summary>
    [Fact]
    public void План_объясняет_почему_группа_движка_не_раскладывается()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var manifest = Manifest("engine-npm", @"C:\Users\Кто-тоДругой\AppData\Roaming\npm");
            manifest.Kinds["engine-npm"] = BackupRootKinds.EnginePackages;
            manifest.Paths["dsh-home"] = paths.DshHome;
            manifest.Kinds["dsh-home"] = BackupRootKinds.DshHome;

            var archive = MakeArchive(root, "copy.zip", manifest, zip =>
            {
                AddText(zip, "engine-npm/bin.js", "// engine\n");
                AddText(zip, "dsh-home/sessions/probe.txt", "S\n");
            });

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);

            var engine = plan.Groups.Single(group => group.Prefix == "engine-npm");
            Assert.False(engine.Selected);
            Assert.Contains("только по отдельному согласию", engine.Note, StringComparison.Ordinal);
            // Домашний каталог движка раскладывается всегда — иначе накат был бы бессмысленным.
            Assert.True(plan.Groups.Single(group => group.Prefix == "dsh-home").Selected);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- мелочи --------------------------------------------------------------

    private static BackupManifest Manifest(string prefix, string recorded) => new()
    {
        CreatedAt = "2026-09-26 12:00:00",
        Machine = "ДРУГАЯ-МАШИНА",
        User = "Кто-то-Другой",
        Paths = { [prefix] = recorded },
    };
}
