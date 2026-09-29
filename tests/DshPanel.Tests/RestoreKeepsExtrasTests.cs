using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Накат НЕ УДАЛЯЕТ того, чего нет в копии.
///
/// Отдельный файл, а не строка в <c>RestoreTests</c>, по находке холодной ревизии 26.09.2026:
/// утверждение «накат не удаляет» держалось там ТОЛЬКО чтением кода — среди проверок наката
/// не было ни одной, которая упала бы, если бы накат начал чистить лишнее. Обещание без проверки,
/// умеющей упасть, — это ровно тот случай, на котором проект уже спотыкался дважды.
///
/// Почему это важно человеку: накат возвращает СТАРУЮ копию на РАБОТАЮЩУЮ машину. Файлы, которых
/// в копии нет (свежие сессии, новые проекты, дописанные журналы), — не мусор: если бы накат их
/// удалял, «восстановление» само стирало бы работу, которую человек не терял.
/// </summary>
public class RestoreKeepsExtrasTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh keep " + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

    /// <summary>Уборка не идёт за ссылками — правило проекта, выросшее из сноса чужого каталога.</summary>
    private static void Cleanup(string root)
    {
        static void Remove(string directory)
        {
            string[] children;
            try { children = Directory.GetFileSystemEntries(directory); }
            catch { return; }

            foreach (var child in children)
            {
                try
                {
                    if (Directory.Exists(child))
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(child);
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
                    // уборка стенда не должна ронять проверку
                }
            }
        }

        try { Remove(root); Directory.Delete(root); } catch { }
    }

    private static string AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        return name;
    }

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
    /// Проверка, которая УМЕЕТ упасть: на месте наката лежат файлы, которых в копии нет, — и рядом
    /// с ними файл, который в копии ЕСТЬ. После наката обязаны быть все три: вернувшийся из копии
    /// и оба «лишних», нетронутыми по содержимому.
    /// </summary>
    [Fact]
    public void Накат_не_трогает_то_чего_нет_в_копии()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            // То, что было на машине ДО наката и в копию не попадало.
            WriteFile(Path.Combine(paths.DshHome, "sessions", "свежая-сессия.jsonl"), "работа, которой нет в копии\n");
            WriteFile(Path.Combine(paths.DshHome, "sessions", "2026", "ветка", "лишний.jsonl"), "лишний в подкаталоге\n");

            // Файл, который в копии есть: накат обязан его вернуть.
            WriteFile(Path.Combine(paths.DshHome, "sessions", "2026", "ветка", "вернувшийся.jsonl"), "старое содержимое\n");

            var manifest = OwnManifest((".dsh", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh"));
            var archive = MakeArchive(root, "keep.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/sessions/2026/ветка/вернувшийся.jsonl", "из копии\n");
            });

            var plan = RestoreEngine.Plan(archive, paths, null);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);

            Assert.True(run.Ok, run.Error);

            // Вернувшееся — вернулось.
            Assert.Equal(
                "из копии\n",
                File.ReadAllText(Path.Combine(paths.DshHome, "sessions", "2026", "ветка", "вернувшийся.jsonl")));

            // А лишнее — на месте, и именно с прежним содержимым.
            Assert.True(
                File.Exists(Path.Combine(paths.DshHome, "sessions", "свежая-сессия.jsonl")),
                "накат удалил файл, которого не было в копии");
            Assert.Equal(
                "работа, которой нет в копии\n",
                File.ReadAllText(Path.Combine(paths.DshHome, "sessions", "свежая-сессия.jsonl")));

            Assert.True(
                File.Exists(Path.Combine(paths.DshHome, "sessions", "2026", "ветка", "лишний.jsonl")),
                "накат удалил лишний файл из каталога, куда писал");
            Assert.Equal(
                "лишний в подкаталоге\n",
                File.ReadAllText(Path.Combine(paths.DshHome, "sessions", "2026", "ветка", "лишний.jsonl")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Тот же вопрос, но про КАТАЛОГ: каталог, которого в копии нет, но который лежит там, куда
    /// накат пишет, обязан остаться вместе с содержимым. Отдельная проверка нужна потому, что
    /// расчистка каталогов — второй способ потерять чужое, и от первого он не зависит.
    /// </summary>
    [Fact]
    public void Накат_не_трогает_каталоги_которых_нет_в_копии()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var extraDir = Path.Combine(paths.DshHome, "storages", "чужая-память");
            WriteFile(Path.Combine(extraDir, "memory.db"), "память\n");

            var manifest = OwnManifest((".dsh", BackupRootKinds.DshHome, @"C:\Users\tester\.dsh"));
            var archive = MakeArchive(root, "keep2.zip", manifest, zip =>
            {
                AddText(zip, ".dsh/storages/моя.json", "{}\n");
            });

            var plan = RestoreEngine.Plan(archive, paths, null);
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);

            Assert.True(run.Ok, run.Error);
            Assert.True(File.Exists(Path.Combine(paths.DshHome, "storages", "моя.json")), "файл из копии не лёг");
            Assert.True(Directory.Exists(extraDir), "накат снёс каталог, которого нет в копии");
            Assert.Equal("память\n", File.ReadAllText(Path.Combine(extraDir, "memory.db")));
        }
        finally
        {
            Cleanup(root);
        }
    }
}
