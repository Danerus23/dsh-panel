using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки СНЯТИЯ КОПИИ: состав корней, имена групп, опись внутри архива, права и проверка.
///
/// Стенд — временный каталог в <c>%TEMP%</c> в форме ИЗОЛИРОВАННОГО прогона
/// (<see cref="AppPaths.Under"/>): все пути выведены из одного корня, и ни один шаг проверки
/// не касается каталогов владельца. Настоящая копия его данных — дело самотеста на своём корне,
/// а не модульных проверок.
/// </summary>
public class BackupEngineTests
{
    // --- стенд ---------------------------------------------------------------

    /// <summary>
    /// Стенд ставится на путь С ПРОБЕЛОМ — это правило проекта, и оно оплачено случаем:
    /// панель ставится в «%LOCALAPPDATA%\Programs\DSH Panel», и однажды путь с пробелом
    /// и хвостовым разделителем съел закрывающую кавычку в командной строке. Здесь через
    /// командную строку идут и 7-Zip, и <c>mklink</c>.
    /// </summary>
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh copy " + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// Junction делает сама Windows: без него не проверить ни список ссылок в описи, ни то,
    /// что обход не уходит внутрь ссылки на десятки уровней.
    /// </summary>
    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(start);
        process?.WaitForExit();
    }

    private sealed record Stand(string Root, AppPaths Paths, DshEngine Engine)
    {
        public string Work => Path.Combine(Root, "projects");

        public string Archive => Path.Combine(Paths.BackupsDir, "copy.zip");

        public string Link => Path.Combine(Paths.DshHome, "profiles", "node_modules");

        public string LinkTarget => Path.Combine(Root, "global-packages");

        public string PanelPrefix => Path.GetFileName(Paths.Root);
    }

    private static Stand CreateStand()
    {
        var root = NewRoot();
        var paths = AppPaths.Under(root);

        // Домашний каталог движка: файл ключей, сессии, профиль со СВОИМИ зависимостями
        // и ссылка-джункция рядом с ними.
        WriteFile(Path.Combine(paths.DshHome, ".credentials.yaml"), "token: TESTKEY\n");
        WriteFile(Path.Combine(paths.DshHome, "sessions", "2026", "session.jsonl"), "{}\n");
        WriteFile(Path.Combine(paths.DshHome, "profiles", "web", "node_modules", "plugin", "index.js"), "module\n");
        WriteFile(Path.Combine(paths.DshHome, "profiles", "web", "package.json"), "{\"name\":\"web\"}\n");

        WriteFile(Path.Combine(root, "global-packages", "some-pkg", "index.js"), "pkg\n");
        Directory.CreateDirectory(Path.Combine(paths.DshHome, "profiles"));
        Junction(Path.Combine(paths.DshHome, "profiles", "node_modules"), Path.Combine(root, "global-packages"));

        // Рабочая папка: проект с историей git и с мусором сборки.
        WriteFile(Path.Combine(root, "projects", "proj", ".git", "logs", "HEAD"), "reflog\n");
        WriteFile(Path.Combine(root, "projects", "proj", ".git", "config"), "[core]\n");
        WriteFile(Path.Combine(root, "projects", "proj", "src", "a.txt"), "A\n");
        WriteFile(Path.Combine(root, "projects", "proj", "bin", "trash.dll"), "trash\n");
        WriteFile(Path.Combine(root, "projects", "proj", "node_modules", "dep", "index.js"), "dep\n");

        // Движок и Node в ОДНОЙ папке — так выглядит обычная установка: пакеты npm лежат
        // под node\node_modules, а сам node.exe рядом.
        var nodeDir = Path.Combine(root, "nodejs");
        var bin = Path.Combine(nodeDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        WriteFile(bin, "// engine\n");
        WriteFile(Path.Combine(nodeDir, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            "{\"version\":\"0.1.5-rc.3\"}\n");
        WriteFile(Path.Combine(nodeDir, "dsh.cmd"), "@echo off\n");
        WriteFile(Path.Combine(nodeDir, "node.exe"), "MZ\n");

        // Настройки панели и прежняя копия в её же папке копий.
        WriteFile(Path.Combine(paths.Root, "settings.json"), "{}\n");
        WriteFile(Path.Combine(paths.BackupsDir, "old-copy.zip"), "old\n");

        return new Stand(root, paths, new DshEngine(Path.Combine(nodeDir, "node.exe"), bin));
    }

    /// <summary>
    /// Уборка снимает junction ЗВЕНОМ, а не деревом: рекурсивное удаление по каталогу со ссылкой
    /// внутри — известный способ снести не то, что собирался (правило проекта).
    /// </summary>
    private static void Cleanup(Stand stand)
    {
        try
        {
            if (Directory.Exists(stand.Link)) Directory.Delete(stand.Link);
        }
        catch
        {
            // Ссылки может уже не быть — это не повод падать в уборке.
        }

        try
        {
            if (Directory.Exists(stand.Root)) Directory.Delete(stand.Root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }

    private static BackupRunResult Copy(Stand stand, string? archive = null, bool verify = false, string? sevenZip = "") =>
        BackupEngine.Run(
            BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
            new BackupRequest(archive ?? stand.Archive, SevenZipPath: sevenZip, Verify: verify),
            stand.Paths,
            stand.Engine);

    // --- состав корней и имена групп -----------------------------------------

    /// <summary>
    /// Имя группы берётся у папки на диске, и это НЕ косметика: имена понятий (как в v1 —
    /// <c>dsh-home</c>, <c>engine/npm</c>) закрыли бы быстрый путь через 7-Zip на всякой полной
    /// копии, то есть решение владельца «создаёт 7-Zip» осталось бы на бумаге.
    /// </summary>
    [Fact]
    public void Имена_групп_берутся_у_папок_и_быстрый_путь_остаётся_открыт()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(
                new[] { "dsh-home", "projects", stand.PanelPrefix, "nodejs" },
                plan.Roots.Select(root => root.Prefix).ToArray());

            // И именно поэтому 7-Zip может взять эту копию: имя группы = имя папки.
            var fit = ZipLayout.CanSevenZip(plan.Sources);
            Assert.True(fit.Can, fit.Reason);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Движок и Node в одной папке (обычная установка) берутся ОДНИМ корнем: два корня на одну
    /// папку — это дерево, положенное в архив дважды.
    /// </summary>
    [Fact]
    public void Движок_и_node_в_одной_папке_берутся_один_раз()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);

            Assert.Single(plan.Roots, root => root.Prefix == "nodejs");
            Assert.Contains(plan.Notes, note => note.Contains("та же папка"));
            Assert.Contains(plan.Roots, root => BackupRootKinds.IsEngine(root.Kind));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Два разных корня с одним именем группы — ОТКАЗ. Свой способ записи положил бы их записи
    /// под одним именем, то есть смешал бы два дерева; это потеря данных, а не «неудобная
    /// раскладка».
    /// </summary>
    [Fact]
    public void Две_папки_с_одним_именем_дают_отказ()
    {
        var stand = CreateStand();
        var other = NewRoot();
        try
        {
            var twin = Path.Combine(other, stand.PanelPrefix);
            WriteFile(Path.Combine(twin, "file.txt"), "x\n");

            var plan = BackupPlanner.Full(stand.Paths, twin, stand.Engine);

            Assert.False(plan.Ok);
            Assert.Contains("одинаково", plan.Error);
            Assert.Contains("переименуйте", plan.Error);
        }
        finally
        {
            Cleanup(stand);
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>
    /// Рабочая папка не задана — проектов в копии не будет, и это НАЗВАНО: пропуск, о котором
    /// молчат, выглядит как «всё унёс».
    /// </summary>
    [Fact]
    public void Пустая_рабочая_папка_называется_замечанием()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, workingDirectory: null, engine: stand.Engine);

            Assert.True(plan.Ok, plan.Error);
            Assert.DoesNotContain(plan.Roots, root => root.Kind == BackupRootKind.WorkingFolder);
            Assert.Contains(plan.Notes, note => note.Contains("рабочая папка не задана"));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Панель содержит другие корни — они поедут в копии дважды. Потерей это не грозит, но знать
    /// об этом надо: в изолированном прогоне форма именно такая (<c>AppPaths.Under</c> кладёт
    /// домашний каталог движка внутрь корня панели).
    /// </summary>
    [Fact]
    public void Повтор_корней_внутри_панели_называется()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);

            Assert.Contains(plan.Notes, note => note.Contains("содержит"));
            Assert.Contains(plan.Notes, note => note.Contains("дважды"));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- архив не кладёт сам себя --------------------------------------------

    /// <summary>
    /// Папка копий может оказаться внутри копируемого дерева — и тогда без правила каждая копия
    /// тащила бы все прежние (урок v1). Пропуск обязан быть НАЗВАН, а не сделан молча.
    /// </summary>
    [Fact]
    public void Папка_копий_внутри_панели_выкидывается_с_замечанием()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);
            var prepared = BackupPlanner.WithoutArchive(plan, stand.Archive);

            Assert.True(prepared.Ok, prepared.Error);
            Assert.Contains(prepared.Notes, note => note.Contains("папка копий") && note.Contains("backups"));

            var panel = prepared.Roots.Single(root => root.Kind == BackupRootKind.Panel);
            Assert.Contains("backups", panel.Source.SkipFor(ZipWriter.DefaultSkipDirectoryNames)!);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    [Fact]
    public void Архив_в_самом_корне_называется_словами()
    {
        var stand = CreateStand();
        try
        {
            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);
            var prepared = BackupPlanner.WithoutArchive(plan, Path.Combine(stand.Root, "copy.zip"));

            Assert.True(prepared.Ok, prepared.Error);
            Assert.Contains(prepared.Notes, note => note.Contains("в самом корне"));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// То же правило внутри корня, который обходится ЦЕЛИКОМ (движок и Node). Такой корень
    /// не применяет ОБЩИЙ список пропуска — но свой, дописанный ему явно, обязан действовать:
    /// иначе правило «папка копий не едет в копию» молча не сработало бы именно там, где копий
    /// может лежать больше всего.
    /// </summary>
    [Fact]
    public void Папка_копий_внутри_корня_движка_тоже_не_едет()
    {
        var stand = CreateStand();
        try
        {
            var archive = Path.Combine(stand.Root, "nodejs", "backups", "copy.zip");
            WriteFile(Path.Combine(stand.Root, "nodejs", "backups", "old.zip"), "old\n");

            var plan = BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine);
            var prepared = BackupPlanner.WithoutArchive(plan, archive);

            var engine = prepared.Roots.Single(root => root.Prefix == "nodejs");
            Assert.True(engine.Source.Everything);
            Assert.Contains("backups", engine.Source.SkipFor(ZipWriter.DefaultSkipDirectoryNames)!);

            var result = BackupEngine.Run(
                plan, new BackupRequest(archive, SevenZipPath: "", Verify: false), stand.Paths, stand.Engine);

            Assert.True(result.Ok, result.Error);

            var read = ZipReader.Read(archive);
            Assert.DoesNotContain(read.Entries, entry => entry.Name.Contains("backups"));

            // При этом остальное содержимое корня на месте: node_modules движка не пропускается.
            Assert.Contains(read.Entries, entry => entry.Name.Contains("@deepseek-ai"));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- копия целиком: дерево, опись, права ---------------------------------

    [Fact]
    public void Копия_кладёт_дерево_опись_и_закрывает_права()
    {
        var stand = CreateStand();
        try
        {
            var result = Copy(stand);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(ZipWriterKind.BuiltIn, result.Writer);   // проверка гоняет свой путь: без 7-Zip
            Assert.Contains("копия снята", result.Summary());

            var read = ZipReader.Read(stand.Archive);
            Assert.True(read.Ok, read.Error);
            Assert.Equal(ManifestState.Read, read.State);
            Assert.Equal(string.Empty, read.Note);

            var names = read.Entries.Select(entry => entry.Name).ToArray();

            // .git остаётся ЦЕЛИКОМ — вместе со своим logs (reflog), который общее правило
            // мусора съело бы: «просто не пропускать .git» для этого мало.
            Assert.Contains("projects/proj/.git/logs/HEAD", names);
            Assert.Contains("projects/proj/.git/config", names);

            // А мусор сборки — не остаётся.
            Assert.DoesNotContain(names, name => name.EndsWith("trash.dll", StringComparison.Ordinal));
            Assert.DoesNotContain(names, name => name.Contains("/bin/", StringComparison.Ordinal));

            // node_modules домашнего каталога движка НЕ пропускается: там свои зависимости
            // профиля, без которых плагин не восстановить без интернета (отступление от v1).
            Assert.Contains("dsh-home/profiles/web/node_modules/plugin/index.js", names);

            // Сессии и файл ключей — на месте.
            Assert.Contains("dsh-home/sessions/2026/session.jsonl", names);
            Assert.Contains("dsh-home/.credentials.yaml", names);

            // Архив не тащит сам себя и прежние копии.
            Assert.DoesNotContain(names, name => name.Contains("backups", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            var manifest = read.Manifest!;
            Assert.Equal(BackupManifest.KindValue, manifest.Kind);
            Assert.Equal(BackupManifest.SupportedFormat, manifest.Format);
            Assert.Equal(BackupEngine.AppValue, manifest.App);
            Assert.True(manifest.WithEngine);
            Assert.True(manifest.WithSessions);
            Assert.True(manifest.WithCredentials);
            Assert.Equal(stand.Paths.DshHome, manifest.Paths["dsh-home"]);
            Assert.Equal(stand.Work, manifest.Paths["projects"]);
            Assert.Equal("0.1.5-rc.3", manifest.Versions["dsh"]);

            // Опись описывает РОВНО то, что в архиве есть: записи перечислены из самого архива,
            // и опись — такая же запись, поэтому она в это число и не входит.
            Assert.Equal(read.Files - 1, manifest.TotalFiles);
            Assert.Equal(manifest.Files.Count, manifest.TotalFiles);
            Assert.All(manifest.Files, file => Assert.Contains(file.Path, names));
            Assert.Equal(manifest.Files.Sum(file => file.Size), manifest.TotalBytes);

            // Ссылка-джункция уехала СПИСКОМ, а не содержимым: внутри неё абсолютный путь
            // машины-источника, и на другой машине он мёртв.
            var link = Assert.Single(manifest.Links);
            Assert.Equal("dsh-home/profiles/node_modules", link.Path);
            Assert.Contains("global-packages", link.Target);

            // Права закрыты: в v1 это проверялось только у каталога ключей при накате.
            Assert.True(result.Restricted);
            Assert.Contains("права: только владелец", result.Summary());
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Архив содержит файл ключей агентов, поэтому создаваться он обязан закрытым: посторонняя
    /// учётная запись не должна получить к нему доступ. Проверяется НАСТОЯЩИЙ список доступа
    /// файла, а не факт вызова функции.
    /// </summary>
    [Fact]
    public void Права_архива_только_владельцу_и_SYSTEM()
    {
        // Страж платформы выражен так, чтобы его видела и проверка, и разбор кода: список доступа —
        // понятие Windows, и на другой системе проверять нечего.
        if (!OperatingSystem.IsWindows()) return;

        var stand = CreateStand();
        try
        {
            var result = Copy(stand);
            Assert.True(result.Ok, result.Error);
            Assert.True(result.Restricted);

            var security = new FileInfo(stand.Archive).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, "наследование прав не снято");

            var owner = WindowsIdentity.GetCurrent().User!;
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            Assert.NotEmpty(rules);

            // Разрешено ровно двум: владельцу и самой системе. Ни «всем» (S-1-1-0),
            // ни «проверенным пользователям» (S-1-5-11), ни группе «Пользователи» (S-1-5-32-545).
            foreach (var rule in rules)
            {
                var identity = rule.IdentityReference.Value;

                Assert.True(
                    rule.IdentityReference.Equals(owner) || identity == "S-1-5-18",
                    $"в правах архива лишняя запись: {identity}");

                Assert.False(
                    identity is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545",
                    $"архив с ключами доступен другим учётным записям: {identity}");
            }
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Причина, по которой отказ закрыть права НЕ выдаётся за успех: файл с ключами, доступный
    /// другим, — это не «копия с мелочью», и в отчёте он назван открытым.
    /// </summary>
    [Fact]
    public void Незакрытые_права_видны_в_отчёте_а_не_спрятаны()
    {
        var result = new BackupRunResult(
            true, string.Empty, @"C:\Temp\copy.zip", 10, 1, 10, ZipWriterKind.BuiltIn, "своими силами",
            TimeSpan.FromSeconds(1), Restricted: false, Verified: null, Notes: Array.Empty<string>());

        Assert.Contains("ВНИМАНИЕ", result.Summary());
        Assert.Contains("в открытом виде", result.Summary());

        // «Проверить нечем» никогда не выдаётся за «проверено».
        Assert.Contains("проверить нечем", result.Summary());
    }

    // --- быстрый путь: 7-Zip с описью ----------------------------------------

    /// <summary>
    /// Самое рискованное место нового кода: на быстром пути опись кладётся ОТДЕЛЬНЫМ запуском
    /// 7-Zip уже после дерева (в память архив не тянем — копия бывает в сотни мегабайт).
    /// Проверка идёт настоящим 7-Zip и читает опись обратно.
    /// </summary>
    [Fact]
    public void Быстрый_путь_кладёт_ту_же_опись_и_проходит_проверку()
    {
        var sevenZip = SevenZip.Find();
        Assert.SkipWhen(sevenZip is null, "7-Zip на этой машине не найден — быстрый путь проверить нечем");

        var stand = CreateStand();
        try
        {
            var result = Copy(stand, verify: true, sevenZip: sevenZip);

            Assert.True(result.Ok, result.Error);
            Assert.True(result.Writer == ZipWriterKind.SevenZip,
                $"7-Zip не взял раскладку: {result.Reason}");
            Assert.True(result.Verified);

            var read = ZipReader.Read(stand.Archive);
            Assert.Equal(ManifestState.Read, read.State);

            var manifest = read.Manifest!;
            Assert.Equal(stand.Paths.DshHome, manifest.Paths["dsh-home"]);
            Assert.Equal(read.Files - 1, manifest.TotalFiles);
            Assert.Contains("projects/proj/.git/logs/HEAD", read.Entries.Select(entry => entry.Name));

            // Проверка через 7-Zip — единственная, что ловит поломку, невидимую для .NET.
            Assert.Contains("проверка 7-Zip прошла", result.Summary());
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Копия на ходу: сессии пишет живой процесс, поэтому допущение обязано быть названо
    /// (находка В2 в `ROADMAP.md`), а не подразумеваться.
    /// </summary>
    [Fact]
    public void Копия_на_ходу_называет_допущение()
    {
        var stand = CreateStand();
        try
        {
            var result = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, ServerRunning: true, SevenZipPath: "", Verify: false),
                stand.Paths,
                stand.Engine);

            Assert.True(result.Ok, result.Error);
            Assert.Contains(result.Notes, note => note.Contains("на ходу"));
            Assert.Contains("на ходу", result.Summary());
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// План, который нельзя собрать (две папки с одним именем), не превращается в «пустой архив»:
    /// копия не начинается, и причина названа.
    /// </summary>
    [Fact]
    public void Несобираемый_план_не_пишет_архив()
    {
        var stand = CreateStand();
        var other = NewRoot();
        try
        {
            var twin = Path.Combine(other, stand.PanelPrefix);
            WriteFile(Path.Combine(twin, "file.txt"), "x\n");

            var plan = BackupPlanner.Full(stand.Paths, twin, stand.Engine);
            var result = BackupEngine.Run(plan, new BackupRequest(stand.Archive), stand.Paths, stand.Engine);

            Assert.False(result.Ok);
            Assert.Contains("одинаково", result.Error);
            Assert.False(File.Exists(stand.Archive));
        }
        finally
        {
            Cleanup(stand);
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }
}
