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
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки НАКАТА и того, на чём он стоит.
///
/// Стенд — временные каталоги в <c>%TEMP%</c> (и с пробелом в пути, как требует правило проекта):
/// ни одного касания каталогов владельца. Архивы собираются прямо здесь — и своим движком копии
/// (сквозные проверки), и вручную (случаи, которые движок копии создать не может: запись с выходом
/// за каталог, ссылка на чужой каталог, копия прежней панели).
/// </summary>
public class RestoreTests
{
    // --- стенд ---------------------------------------------------------------

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh restore " + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// Уборка НЕ следует за ссылками: рекурсивное удаление по дереву со ссылкой внутри — известный
    /// способ снести не то, что собирался (правило проекта). Сначала снимаем саму ссылку звеном,
    /// потом удаляем дерево.
    /// </summary>
    private static void Cleanup(string root)
    {
        static void Remove(string directory)
        {
            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(directory);
            }
            catch
            {
                return;
            }

            foreach (var child in children)
            {
                try
                {
                    if (Directory.Exists(child))
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(child);   // снимаем звено, внутрь не идём
                            continue;
                        }

                        Remove(child);
                        Directory.Delete(child);
                    }
                    else
                    {
                        File.Delete(child);
                    }
                }
                catch
                {
                    // Не убралось — не провал проверки.
                }
            }
        }

        try
        {
            if (!Directory.Exists(root)) return;
            Remove(root);
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }

    private static string AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        return name;
    }

    /// <summary>Архив, собранный вручную: опись плюс записи.</summary>
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

    /// <summary>Копия, снятая СВОИМ движком: так проверяется, что накат понимает свою же опись.</summary>
    private sealed record Stand(string Root, AppPaths Paths, DshEngine Engine, string Work, string Archive);

    private static Stand CreateCopy()
    {
        var root = NewRoot();
        var paths = AppPaths.Under(root);

        WriteFile(Path.Combine(paths.DshHome, ".credentials.yaml"), "token: REALKEY\n");
        WriteFile(Path.Combine(paths.DshHome, "sessions", "2026", "session.jsonl"), "{\"turn\":1}\n");
        WriteFile(Path.Combine(paths.DshHome, "profiles", "web", "package.json"), "{\"name\":\"web\"}\n");
        WriteFile(Path.Combine(root, "projects", "proj", "src", "a.txt"), "A\n");
        WriteFile(Path.Combine(root, "projects", "proj", ".git", "logs", "HEAD"), "reflog\n");
        WriteFile(Path.Combine(paths.Root, "settings.json"), "{}\n");

        var nodeDir = Path.Combine(root, "nodejs");
        var bin = Path.Combine(nodeDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        WriteFile(bin, "// engine\n");
        WriteFile(Path.Combine(nodeDir, "node.exe"), "MZ\n");

        var engine = new DshEngine(Path.Combine(nodeDir, "node.exe"), bin);
        var archive = Path.Combine(root, "backup.zip");

        return new Stand(root, paths, engine, Path.Combine(root, "projects"), archive);
    }

    // --- файл ключа, который не хранит ни одного ключа ------------------------

    [Theory]
    [InlineData("token: \"\"", true)]
    [InlineData("token: ''", true)]
    [InlineData("token:", true)]
    [InlineData("token: ~", true)]
    [InlineData("token: null", true)]
    [InlineData("token: \"\" # комментарий", true)]
    [InlineData("---\ntoken: \"\"", true)]
    [InlineData("token: REALKEY", false)]
    [InlineData("token: \" \"", false)]
    [InlineData("token: \"\"#x", false)]
    [InlineData("# только комментарий", false)]
    [InlineData("version: 1\nrefs:", false)]
    [InlineData("version: 1\nrefs:\n  token: \"\"", true)]
    [InlineData("version: 1\nrefs:\n  token: KEY", false)]
    [InlineData("version: 2\nrefs:\n  token: \"\"", false)]
    [InlineData("refs: {token: \"\"}", true)]
    [InlineData("refs: {token: \"\", other: \"\"}", true)]
    [InlineData("refs: {token: \"x\"}", false)]
    [InlineData("records:\n  token: \"\"", true)]
    [InlineData("records: {a: 1}", false)]
    [InlineData("- список", false)]
    [InlineData("%YAML 1.2\ntoken: \"\"", false)]
    [InlineData("", true)]
    public void Файл_ключа_без_ключей_узнаётся_строго(string text, bool storesNothing) =>
        Assert.Equal(storesNothing, CredentialsFile.StoresNothing(text));

    [Fact]
    public void Файла_нет_это_не_повод_его_трогать()
    {
        var root = NewRoot();
        try
        {
            var missing = Path.Combine(root, ".credentials.yaml");
            Assert.False(CredentialsFile.ExistsAndStoresNothing(missing));

            WriteFile(missing, "token: \"\"");
            Assert.True(CredentialsFile.ExistsAndStoresNothing(missing));

            WriteFile(missing, "token: KEY");
            Assert.False(CredentialsFile.ExistsAndStoresNothing(missing));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- чужой профиль в пути ------------------------------------------------

    [Fact]
    public void Чужой_профиль_в_пути_заменяется_на_свой()
    {
        var profile = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Профили", "Свой");

        Assert.Equal(
            Path.Combine(profile, ".dsh"),
            RestoreEngine.Remap(Path.Combine("C:", "Users", "Прежний", ".dsh"), profile));

        // Путь вне профиля не трогаем: место Node на машине от имени пользователя не зависит.
        var programFiles = Path.Combine("C:", "Program Files", "nodejs");
        Assert.Equal(programFiles, RestoreEngine.Remap(programFiles, profile));

        Assert.Equal(string.Empty, RestoreEngine.Remap("", profile));
        Assert.Equal(string.Empty, RestoreEngine.Remap("   ", profile));
    }

    // --- план: куда что ляжет ------------------------------------------------

    private static BackupManifest OwnManifest(params (string Prefix, string Kind, string Path)[] groups)
    {
        var manifest = new BackupManifest { Machine = "TEST-MACHINE", User = "tester" };

        foreach (var (prefix, kind, path) in groups)
        {
            manifest.Paths[prefix] = path;
            manifest.Kinds[prefix] = kind;
        }

        return manifest;
    }

    /// <summary>
    /// Вид группы в описи — это то, чем накат на ДРУГОЙ машине отличается от гадания: записанный
    /// путь принадлежит машине-источнику. Домашний каталог движка и папка панели ложатся на СВОИ
    /// места, а не туда, откуда сняты.
    /// </summary>
    [Fact]
    public void Данные_движка_ложатся_на_своё_место_а_не_по_чужому_пути()
    {
        var root = NewRoot();
        var other = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var archive = MakeArchive(other, "copy.zip",
                OwnManifest(
                    (".dsh", BackupRootKinds.DshHome, @"C:\Users\Прежний\.dsh"),
                    ("Harness", BackupRootKinds.WorkingFolder, @"C:\Harness")),
                zip =>
                {
                    AddText(zip, ".dsh/sessions/2026/s.jsonl", "{}");
                    AddText(zip, "Harness/proj/src/a.txt", "A");
                });

            var plan = RestoreEngine.Plan(archive, paths, @"C:\Projects");

            Assert.True(plan.Ok, plan.Error);

            var home = plan.Groups.Single(group => group.Prefix == ".dsh");
            Assert.Equal(paths.DshHome, home.Target);
            Assert.Equal(BackupRootKinds.DshHome, home.Kind);

            // Рабочая папка возвращается по записанному пути (проекты — туда, откуда взяты),
            // но расхождение с нынешней настройкой названо и объяснено (находка В3).
            var work = plan.Groups.Single(group => group.Prefix == "Harness");
            Assert.Equal(@"C:\Harness", work.Target);
            Assert.Contains("сессии движка привязаны к пути", work.Note);
            Assert.Contains("C:\\Projects", work.Note);
            Assert.Contains("рабочую папку в настройках надо указать", work.Note);
        }
        finally
        {
            Cleanup(root);
            Cleanup(other);
        }
    }

    /// <summary>
    /// Настройки панели и движок — отдельные согласия, и по умолчанию их НЕ возвращают: подменить
    /// работающую панель и её движок копией значит сломать то, что уже работает. Согласие дано —
    /// группы раскладываются.
    /// </summary>
    [Fact]
    public void Настройки_панели_и_движок_возвращаются_только_по_согласию()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var manifest = OwnManifest(
                (".dsh", BackupRootKinds.DshHome, @"C:\Users\Прежний\.dsh"),
                ("DshPanel2", BackupRootKinds.Panel, @"C:\Users\Прежний\AppData\Local\DshPanel2"),
                ("npm", BackupRootKinds.EnginePackages, @"C:\Users\Прежний\AppData\Roaming\npm"),
                ("nodejs", BackupRootKinds.EngineNode, @"C:\Program Files\nodejs"));

            var archive = MakeArchive(root, "copy.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/sessions/2026/s.jsonl", "{}");
                AddText(zip, "DshPanel2/settings.json", "{}");
                AddText(zip, "npm/dsh.cmd", "@echo off");
                AddText(zip, "nodejs/node.exe", "MZ");
            });

            var without = RestoreEngine.Plan(archive, paths, @"C:\Harness");
            Assert.False(without.Groups.Single(group => group.Prefix == "DshPanel2").Selected);
            Assert.False(without.Groups.Single(group => group.Prefix == "npm").Selected);
            Assert.False(without.Groups.Single(group => group.Prefix == "nodejs").Selected);
            Assert.Contains(without.Notes, note => note.Contains("движок и Node в копии есть"));
            Assert.Contains("отдельное согласие", without.Summary());

            var with = RestoreEngine.Plan(archive, paths, @"C:\Harness",
                new RestoreOptions(WithEngine: true, WithPanel: true));
            Assert.Equal(paths.Root, with.Groups.Single(group => group.Prefix == "DshPanel2").Target);
            Assert.True(with.Groups.Single(group => group.Prefix == "npm").Selected);
            Assert.True(with.Groups.Single(group => group.Prefix == "nodejs").Selected);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ИЗОЛЯЦИЯ И ГРУППЫ ДВИЖКА. Цели этих групп накат берёт не из корня прогона, а с МАШИНЫ:
    /// движок и Node лежат в её глобальной установке, то есть ВНЕ корня. Пока копию раскладывал
    /// человек из окна, это было решением о его же машине; с ключом `--restore --with-engine`
    /// то же самое делал бы скрипт из изолированного прогона — и переписал бы установку владельца,
    /// хотя прогон называется изолированным (`Restore\RestoreConfine.cs`).
    ///
    /// Поэтому в изоляции группа НЕ раскладывается и говорит об этом словами, а у человека
    /// поведение не меняется. Проверяются ОБЕ ветки цели: движка на машине нет (путь из описи)
    /// и движок есть (место этой машины).
    /// </summary>
    [Fact]
    public void В_изоляции_движок_и_Node_за_пределы_корня_не_раскладываются()
    {
        var root = NewRoot();
        var machine = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var manifest = OwnManifest(
                (".dsh", BackupRootKinds.DshHome, @"C:\Users\Прежний\.dsh"),
                ("npm", BackupRootKinds.EnginePackages, @"C:\Users\Прежний\AppData\Roaming\npm"),
                ("nodejs", BackupRootKinds.EngineNode, @"C:\Program Files\nodejs"));

            var archive = MakeArchive(root, "copy.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/sessions/2026/s.jsonl", "{}");
                AddText(zip, "npm/dsh.cmd", "@echo off");
                AddText(zip, "nodejs/node.exe", "MZ");
            });

            // Ветка первая: движка на машине нет, цели берутся из описи (чужой профиль заменяется
            // своим — и всё равно это путь ВНЕ корня прогона).
            var noEngine = RestoreEngine.Plan(
                archive, paths, string.Empty, new RestoreOptions(WithEngine: true, ConfineToRunRoot: true));

            Assert.True(noEngine.Ok, noEngine.Error);
            Assert.False(noEngine.Groups.Single(group => group.Prefix == "npm").Selected);
            Assert.False(noEngine.Groups.Single(group => group.Prefix == "nodejs").Selected);
            Assert.Contains("ВНЕ корня", noEngine.Groups.Single(group => group.Prefix == "npm").Note);

            // Данные движка при этом ложатся: изоляция не запрещает накат, она держит его под корнем.
            Assert.True(noEngine.Groups.Single(group => group.Prefix == ".dsh").Selected);

            // Пропуск НАЗВАН и виден в плане, который человек читает до наката: строка группы
            // говорит «не раскладывается» и называет причину.
            Assert.Contains(
                "не раскладывается",
                noEngine.Groups.Single(group => group.Prefix == "npm").Describe());
            Assert.Contains(
                "ВНЕ корня",
                noEngine.Groups.Single(group => group.Prefix == "npm").Describe());

            // Ветка вторая: движок на машине ЕСТЬ — и он тоже вне корня (второй временный каталог
            // изображает глобальную установку машины, не касаясь настоящей).
            var nodeDir = Path.Combine(machine, "nodejs");
            var bin = Path.Combine(nodeDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            WriteFile(bin, "// engine\n");
            WriteFile(Path.Combine(nodeDir, "node.exe"), "MZ\n");

            var engine = new DshEngine(Path.Combine(nodeDir, "node.exe"), bin);

            var found = RestoreEngine.Plan(
                archive, paths, string.Empty,
                new RestoreOptions(WithEngine: true, ConfineToRunRoot: true), engine);

            Assert.False(found.Groups.Single(group => group.Prefix == "npm").Selected);
            Assert.False(found.Groups.Single(group => group.Prefix == "nodejs").Selected);
            Assert.Contains("ВНЕ корня", found.Groups.Single(group => group.Prefix == "nodejs").Note);

            // А ЧЕЛОВЕКУ — как было: без правила изоляции те же группы раскладываются по своему месту.
            var human = RestoreEngine.Plan(
                archive, paths, string.Empty, new RestoreOptions(WithEngine: true), engine);

            Assert.Equal(nodeDir, human.Groups.Single(group => group.Prefix == "nodejs").Target);
            Assert.Equal(nodeDir, human.Groups.Single(group => group.Prefix == "npm").Target);
            Assert.True(human.Groups.Single(group => group.Prefix == "npm").Selected);
            Assert.True(human.Groups.Single(group => group.Prefix == "nodejs").Selected);
        }
        finally
        {
            Cleanup(root);
            Cleanup(machine);
        }
    }

    /// <summary>
    /// Копия ПРЕЖНЕЙ панели не раскладывается, и это не «осторожность», а названная причина:
    /// в v1 опись держала имена понятий (dshHome, npmRoot), а группы в архиве звались иначе
    /// (dsh-home, engine/npm). Разложить такую копию «по догадке» значит потерять данные.
    /// </summary>
    [Fact]
    public void Копия_прежней_панели_не_раскладывается_и_причина_названа()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            // Опись v1: ключи — имена понятий, вида групп нет вовсе.
            var legacy = new BackupManifest { Machine = "TEST-MACHINE", User = "tester" };
            legacy.Paths["dshHome"] = @"C:\Users\tester\.dsh";
            legacy.Paths["npmRoot"] = @"C:\Users\tester\AppData\Roaming\npm\node_modules";

            var archive = MakeArchive(root, "old.zip", legacy, zip =>
            {
                AddText(zip, "dsh-home/sessions/2026/s.jsonl", "{}");
                AddText(zip, "engine/npm/dsh.cmd", "@echo off");
            });

            var plan = RestoreEngine.Plan(archive, paths, @"C:\Harness");

            Assert.False(plan.Ok);
            Assert.Contains("копия прежней панели", plan.Error);
            Assert.Contains("потерять данные", plan.Error);

            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);
            Assert.False(run.Ok);
            Assert.Contains("восстановление не начато", run.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Чужой_архив_и_опись_новее_понимаемой_не_накатываются()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var foreign = MakeArchive(root, "foreign.zip", new BackupManifest { Kind = "other-app" },
                zip => AddText(zip, "dsh-home/a.txt", "A"));
            Assert.Contains("чужого вида", RestoreEngine.Plan(foreign, paths, null).Error);

            var noManifest = Path.Combine(root, "plain.zip");
            using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create)) AddText(zip, "a.txt", "A");
            Assert.Contains("без нашей описи", RestoreEngine.Plan(noManifest, paths, null).Error);

            Assert.Contains("путь к архиву не задан", RestoreEngine.Plan("", paths, null).Error);

            var future = new BackupManifest { Format = BackupManifest.SupportedFormat + 1 };
            future.Paths[".dsh"] = @"C:\Users\tester\.dsh";
            future.Kinds[".dsh"] = BackupRootKinds.DshHome;
            var futureArchive = MakeArchive(root, "future.zip", future, zip => AddText(zip, ".dsh/a.txt", "A"));

            var plan = RestoreEngine.Plan(futureArchive, paths, null);
            Assert.False(plan.Ok);
            Assert.Contains("обновите панель", plan.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Опасные_записи_видны_в_отчёте_а_не_исчезают()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var manifest = OwnManifest((".dsh", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh"));

            var archive = MakeArchive(root, "slip.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/ok.txt", "OK");
                AddText(zip, "../up.txt", "UP");
                AddText(zip, "/abs.txt", "ABS");
            });

            var plan = RestoreEngine.Plan(archive, paths, null);

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(2, plan.UnsafeEntries);
            Assert.Contains(plan.Notes, note => note.Contains("с выходом за каталог"));
            Assert.Contains("опасных записей пропущено: 2", plan.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- накат: раскладка ----------------------------------------------------

    /// <summary>
    /// Сквозная проверка: копию снял СВОЙ движок, её же разложил накат. Файл, удалённый после
    /// копии, возвращается — и именно с тем содержимым, что было.
    /// </summary>
    [Fact]
    public void Своя_копия_раскладывается_обратно()
    {
        var stand = CreateCopy();
        try
        {
            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);

            Assert.True(made.Ok, made.Error);

            var sessions = Path.Combine(stand.Paths.DshHome, "sessions", "2026", "session.jsonl");
            Assert.True(File.Exists(sessions));
            File.Delete(sessions);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            Assert.True(plan.Ok, plan.Error);

            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.True(File.Exists(sessions), "удалённый файл не вернулся");
            Assert.Equal("{\"turn\":1}\n", File.ReadAllText(sessions));
            Assert.Contains("восстановление сделано", run.Summary());
            Assert.Contains(run.Restored, line => line.Contains("dsh-home"));
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    /// <summary>
    /// Тот же пустой файл ключа, но приехавший ДРУГОЙ группой. Корни пересекаются: у изолированного
    /// прогона домашний каталог движка лежит внутри корня панели, а у человека так бывает, если
    /// рабочая папка — его домашний каталог. Правило обязано быть про ФАЙЛ, а не про группу:
    /// нашлось на самотесте копий 25.09.2026 — пустой файл уехал через группу панели, и движок
    /// на нём не поднялся бы вовсе (ровно случай ВМ 23.09.2026).
    /// </summary>
    [Fact]
    public void Пустой_файл_ключа_не_приезжает_и_другой_группой()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            // Корень панели прогона — это его корень, а домашний каталог движка лежит ВНУТРИ него:
            // значит запись группы «панель» ляжет ровно на тот же файл ключа.
            var manifest = OwnManifest(
                ("dsh-home", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh"),
                ("panel", BackupRootKinds.Panel, @"C:\Users\tester\AppData\Local\DshPanel2"));

            var inside = Path.GetFileName(paths.DshHome);

            var archive = MakeArchive(root, "twice.zip", manifest, zip =>
            {
                AddText(zip, "dsh-home/sessions/_no-cwd/probe-0001/session.jsonl", "{}");
                AddText(zip, "dsh-home/.credentials.yaml", "token: \"\"");
                AddText(zip, $"panel/{inside}/.credentials.yaml", "token: \"\"");
            });

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithPanel: true));
            Assert.True(plan.Ok, plan.Error);

            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);

            Assert.True(run.Ok, run.Error);
            Assert.False(File.Exists(paths.CredentialsPath), "пустой файл ключа приехал другой группой");
            Assert.Contains(run.Skipped, line => line.Contains("не хранит ни одного ключа"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Файл ключа, который не хранит ни одного ключа, НЕ возвращается: на таком файле движок
    /// падает целиком, и сервер после наката не поднимается вовсе (прогон в ВМ 23.09.2026).
    /// Пропуск при этом назван в отчёте, а не сделан молча.
    /// </summary>
    [Fact]
    public void Пустой_файл_ключа_не_возвращается()
    {
        var stand = CreateCopy();
        try
        {
            // В копии лежит именно такой файл — 11 байт, на которых движок падает.
            WriteFile(stand.Paths.CredentialsPath, "token: \"\"\n");

            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            File.Delete(stand.Paths.CredentialsPath);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.False(File.Exists(stand.Paths.CredentialsPath), "пустой файл ключа вернулся на место");
            Assert.Contains(run.Skipped, line => line.Contains("не хранит ни одного ключа"));
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    [Fact]
    public void Содержательный_файл_ключа_возвращается()
    {
        var stand = CreateCopy();
        try
        {
            WriteFile(stand.Paths.CredentialsPath, "token: REALKEY\n");

            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            File.Delete(stand.Paths.CredentialsPath);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.Equal("token: REALKEY\n", File.ReadAllText(stand.Paths.CredentialsPath));
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    /// <summary>
    /// Запись не пишется СКВОЗЬ точку повторной обработки: в профиле движка почти весь
    /// <c>node_modules</c> — это ссылки на глобальную установку, и накат «сквозь» них переписал бы
    /// ЧУЖОЙ каталог вместо места распаковки. Проверяется на чужом каталоге рядом со стендом.
    /// </summary>
    [Fact]
    public void Запись_не_идёт_сквозь_ссылку()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var outside = Path.Combine(Path.GetTempPath(), "dsh outside " + Guid.NewGuid().ToString("N")[..8]);
            var trap = Path.Combine(paths.DshHome, "profiles", "trap");

            // Родитель ссылки обязан существовать: без него mklink отказывает МОЛЧА, ловушка
            // не появляется, и проверка становится слепой (на этом она один раз и споткнулась).
            Directory.CreateDirectory(Path.Combine(paths.DshHome, "profiles"));
            Directory.CreateDirectory(outside);
            WriteFile(Path.Combine(outside, "keep.txt"), "чужое\n");

            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{trap}\" \"{outside}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using (var process = System.Diagnostics.Process.Start(start)) process?.WaitForExit();

            Assert.True(Directory.Exists(trap), "ссылка-ловушка не создана — проверять нечего");
            Assert.True((File.GetAttributes(trap) & FileAttributes.ReparsePoint) != 0, "на месте ловушки обычный каталог");

            var manifest = OwnManifest((".dsh", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh"));
            var archive = MakeArchive(root, "trap.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/profiles/trap/пришло.txt", "из архива\n");
                AddText(zip, ".dsh/ok.txt", "обычный файл\n");
            });

            var plan = RestoreEngine.Plan(archive, paths, null);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);

            Assert.True(run.Ok, run.Error);
            Assert.Equal("обычный файл\n", File.ReadAllText(Path.Combine(paths.DshHome, "ok.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "пришло.txt")), "файл ушёл за ссылку — в чужой каталог");
            Assert.Equal("чужое\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.Contains(run.Skipped, line => line.Contains("точку повторной обработки"));

            try { Directory.Delete(trap); } catch { }
            Cleanup(outside);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- накат: ссылки и предохранительная копия ------------------------------

    /// <summary>
    /// Ссылки-джункции создаются заново по описи: архив их содержимым не несёт и не может —
    /// внутри ссылки абсолютный путь машины-источника.
    /// </summary>
    [Fact]
    public void Ссылки_создаются_заново_по_описи()
    {
        var stand = CreateCopy();
        try
        {
            var link = Path.Combine(stand.Paths.DshHome, "profiles", "node_modules");
            var target = Path.Combine(stand.Root, "global-packages");
            WriteFile(Path.Combine(target, "some-pkg", "index.js"), "pkg\n");
            Directory.CreateDirectory(Path.Combine(stand.Paths.DshHome, "profiles"));

            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using (var process = System.Diagnostics.Process.Start(start)) process?.WaitForExit();
            Assert.True(Directory.Exists(link));

            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            Directory.Delete(link);   // как будто ссылки на новой машине нет

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.Equal(1, run.Links);
            Assert.True(Directory.Exists(link), "ссылка не создана");
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "на месте ссылки обычный каталог");
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    /// <summary>
    /// Предохранительная копия снимается ДО наката, и накат без неё не начинается вовсе:
    /// накат необратим, и пути назад без копии нет.
    /// </summary>
    [Fact]
    public void Без_предохранительной_копии_накат_не_начинается()
    {
        var stand = CreateCopy();
        try
        {
            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            // Каталог для предохранительной копии занят файлом: снять её невозможно.
            var blocked = Path.Combine(stand.Root, "blocked");
            WriteFile(blocked, "я файл, а не каталог\n");

            var sessions = Path.Combine(stand.Paths.DshHome, "sessions", "2026", "session.jsonl");
            File.Delete(sessions);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(
                plan, new RestoreOptions(SafetyCopy: true, SafetyFolder: Path.Combine(blocked, "внутри")),
                stand.Paths, stand.Engine);

            Assert.False(run.Ok);
            Assert.Contains("предохранительную копию снять не удалось", run.Error);
            Assert.False(File.Exists(sessions), "накат всё-таки начался без пути назад");
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    [Fact]
    public void Предохранительная_копия_ложится_рядом_и_названа_в_отчёте()
    {
        var stand = CreateCopy();
        try
        {
            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: true), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.NotNull(run.SafetyPath);
            Assert.True(File.Exists(run.SafetyPath));
            // Имя предохранительной копии — НАШ префикс (dsh2-…): папка копий общая с панелью 1.x
            // (решение владельца 26.09.2026), поэтому «dsh-before-restore-…» означало бы, что 2.0
            // называет свою копию чужим именем — и её же ротация не признала бы своей.
            Assert.Contains(BackupNaming.SafetyPrefix, Path.GetFileName(run.SafetyPath));
            Assert.StartsWith(BackupNaming.SafetyPrefix, Path.GetFileName(run.SafetyPath), StringComparison.Ordinal);
            Assert.Contains("предохранительная копия", run.Summary());

            // Предохранительная копия — тонкая: движка и Node в ней нет (вернуть надо данные).
            var read = ZipReader.Read(run.SafetyPath!);
            Assert.Equal(ManifestState.Read, read.State);
            Assert.False(read.Manifest!.WithEngine);
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    /// <summary>
    /// Накат на ходу: сессии пишет живой процесс, поэтому допущение обязано быть названо словами,
    /// а не подразумеваться (находка В2).
    /// </summary>
    [Fact]
    public void Накат_на_ходу_называет_допущение()
    {
        var stand = CreateCopy();
        try
        {
            var made = BackupEngine.Run(
                BackupPlanner.Full(stand.Paths, stand.Work, stand.Engine),
                new BackupRequest(stand.Archive, SevenZipPath: "", Verify: false),
                stand.Paths, stand.Engine);
            Assert.True(made.Ok, made.Error);

            var plan = RestoreEngine.Plan(stand.Archive, stand.Paths, stand.Work);
            var run = RestoreEngine.Run(
                plan, new RestoreOptions(SafetyCopy: false, ServerRunning: true), stand.Paths, stand.Engine);

            Assert.True(run.Ok, run.Error);
            Assert.Contains(run.Notes, note => note.Contains("сервер DSH работает"));
            Assert.Contains("сервер DSH работает", run.Summary());
        }
        finally
        {
            Cleanup(stand.Root);
        }
    }

    /// <summary>
    /// Копия снята на другой машине — накат говорит об этом фактом, но БЕЗ имён: имя машины
    /// и пользователя из описи наружу не выносим (красная линия 7).
    /// </summary>
    [Fact]
    public void Чужая_машина_названа_без_имён()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var archive = MakeArchive(root, "other.zip",
                OwnManifest((".dsh", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh")),
                zip => AddText(zip, ".dsh/a.txt", "A"));

            // В описи этой проверки машина TEST-MACHINE и пользователь tester — они и есть «чужие».
            var plan = RestoreEngine.Plan(archive, paths, null);

            Assert.Contains(plan.Notes, note => note.Contains("на другой машине или другим пользователем"));
            Assert.DoesNotContain("TEST-MACHINE", plan.Summary());
            Assert.DoesNotContain("tester", plan.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }
}
