using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки ЧТЕНИЯ архива — то, на чём стоит накат.
///
/// Работают на временных каталогах в <c>%TEMP%</c> и на архивах, собранных прямо здесь:
/// ни данных владельца, ни 7-Zip для этого не нужно — читатель обязан уметь читать без него,
/// и именно это решение владельца 25.09.2026 («создаёт 7-Zip, распаковывает сама панель»).
/// Настоящая копия целиком проверяется отдельно, самотестом на изолированном корне.
/// </summary>
public class BackupReaderTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh-read-" + Guid.NewGuid().ToString("N")[..8]);

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string CreateArchive(string root, string name, Action<ZipArchive> fill)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            fill(zip);
        }

        return path;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP% — если не убрался, это не провал проверки.
        }
    }

    // --- перечисление записей ------------------------------------------------

    [Fact]
    public void Файлы_перечисляются_а_каталоги_не_считаются()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "a.zip", zip =>
            {
                AddText(zip, "src/a.txt", "A");
                AddText(zip, "src/sub/b.txt", "B");

                // Запись-каталог: zip помечает такие хвостовой чертой. В описи файлов её нет,
                // и в копии она смысла не несёт — восстанавливать пустые папки нечего.
                zip.CreateEntry("src/sub/");
            });

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(2, read.Files);
            Assert.Equal(
                new[] { "src/a.txt", "src/sub/b.txt" },
                read.Entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(1, read.Entries.Single(entry => entry.Name == "src/a.txt").Length);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Разделитель нормализуется: запись с обратной чертой — это запись под именем <c>src\back.txt</c>,
    /// и без нормализации группа «src» выглядела бы пустой при живом файле внутри.
    /// </summary>
    [Fact]
    public void Обратный_разделитель_приводится_к_прямому()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "back.zip", zip => AddText(zip, @"src\back.txt", "B"));

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(new[] { "src/back.txt" }, read.Entries.Select(entry => entry.Name).ToArray());
            Assert.True(ZipReader.Group(read.Entries, "src").Any);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- опись: три разных ответа --------------------------------------------

    [Fact]
    public void Описи_нет_и_это_не_сломанный_архив()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "no-manifest.zip", zip => AddText(zip, "src/a.txt", "A"));

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(ManifestState.Missing, read.State);
            Assert.Null(read.Manifest);
            Assert.Contains("manifest.json", read.Note);
            Assert.Contains("описи нет", read.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Опись_читается_и_разбирается()
    {
        var root = NewRoot();
        try
        {
            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "TEST-MACHINE",
                User = "tester",
                WithEngine = true,
                WithSessions = true,
                TotalFiles = 2,
                TotalBytes = 3,
            };

            manifest.Paths["dsh-home"] = @"C:\Users\tester\.dsh";
            manifest.Files.Add(new BackupFileEntry { Path = "dsh-home/a.txt", Size = 1, Sha256 = "abc" });
            manifest.Files.Add(new BackupFileEntry { Path = "dsh-home/b.txt", Size = 2 });

            var archive = CreateArchive(root, "with-manifest.zip", zip =>
            {
                AddText(zip, "dsh-home/a.txt", "A");
                AddText(zip, "dsh-home/b.txt", "BB");
                AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
            });

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(ManifestState.Read, read.State);
            Assert.Equal(string.Empty, read.Note);

            var parsed = read.Manifest!;
            Assert.Equal(BackupManifest.KindValue, parsed.Kind);
            Assert.Equal("TEST-MACHINE", parsed.Machine);
            Assert.True(parsed.WithEngine);
            Assert.True(parsed.WithSessions);
            Assert.Equal(2, parsed.TotalFiles);
            Assert.Equal(@"C:\Users\tester\.dsh", parsed.Paths["dsh-home"]);
            Assert.Equal("abc", parsed.Files[0].Sha256);
            Assert.Null(parsed.Files[1].Sha256);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Опись пишется ПОД ТЕМИ ЖЕ ИМЕНАМИ, что в v1. Это не косметика: на именах полей стоит
    /// чтение старой копии, и перевод их в camelCase превратил бы «копия наша, данные целы»
    /// в «опись не разбирается» — то есть в потерянные данные при живом архиве.
    /// </summary>
    [Fact]
    public void Опись_пишется_под_именами_v1()
    {
        var text = JsonSerializer.Serialize(new BackupManifest { TotalFiles = 4 }, BackupManifest.WriteOptions);

        Assert.Contains("\"Kind\": \"dsh-backup\"", text);
        Assert.Contains("\"Format\": 1", text);
        Assert.Contains("\"TotalFiles\": 4", text);
        Assert.Contains("\"WithEngine\": false", text);

        // Контрольной суммы у движка и Node нет — поля в описи быть не должно.
        Assert.DoesNotContain("Sha256", text);
    }

    [Fact]
    public void Битая_опись_отличается_от_отсутствующей()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "broken-manifest.zip", zip =>
            {
                AddText(zip, "src/a.txt", "A");
                AddText(zip, "manifest.json", "{ это не опись, а мусор");
            });

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(ManifestState.Unreadable, read.State);
            Assert.Null(read.Manifest);
            Assert.Contains("не разбирается", read.Note);
            Assert.DoesNotContain("мусор", read.Note);
            Assert.Contains("опись не разбирается", read.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Опись_под_другим_именем_находится_и_об_этом_сказано()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "odd-case.zip", zip =>
                AddText(zip, "Manifest.json", JsonSerializer.Serialize(new BackupManifest(), BackupManifest.WriteOptions)));

            var read = ZipReader.Read(archive);

            Assert.Equal(ManifestState.Read, read.State);
            Assert.Contains("под именем", read.Note);
            Assert.Contains("Manifest.json", read.Note);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Опись_новой_версии_читается_но_помечается_словами()
    {
        var root = NewRoot();
        try
        {
            var manifest = new BackupManifest { Format = BackupManifest.SupportedFormat + 1 };
            var archive = CreateArchive(root, "future.zip", zip =>
                AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions)));

            var read = ZipReader.Read(archive);

            Assert.Equal(ManifestState.Read, read.State);
            Assert.Contains("версии 2", read.Note);
            Assert.Contains("понимает 1", read.Note);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Опись_чужого_вида_помечается()
    {
        var root = NewRoot();
        try
        {
            var manifest = new BackupManifest { Kind = "other-app" };
            var archive = CreateArchive(root, "foreign.zip", zip =>
                AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions)));

            var read = ZipReader.Read(archive);

            Assert.Equal(ManifestState.Read, read.State);
            Assert.Contains("чужого вида", read.Note);
            Assert.Contains("other-app", read.Note);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- архив, который не читается ------------------------------------------

    [Fact]
    public void Сломанный_архив_не_открывается()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "broken.zip");
            File.WriteAllText(archive, "это вовсе не zip");

            var read = ZipReader.Read(archive);

            Assert.False(read.Ok);
            Assert.NotEqual(string.Empty, read.Error);
            Assert.Equal(0, read.Files);
            Assert.Contains("архив не читается", read.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Отсутствующий_архив_называется_и_путь_маскируется()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var missing = Path.Combine(profile, "dsh-нет-такого-" + Guid.NewGuid().ToString("N")[..8] + ".zip");

        var read = ZipReader.Read(missing);

        Assert.False(read.Ok);
        Assert.Contains("~", read.Error);
        Assert.DoesNotContain(profile, read.Error);
    }

    // --- группы: точная сверка и причина пустоты ------------------------------

    [Fact]
    public void Группа_находится_точно_и_соседние_имена_не_прихватывает()
    {
        var entries = new[]
        {
            new ZipEntry("src/a.txt", 1),
            new ZipEntry("SRC/b.txt", 1),
            new ZipEntry("src-old/c.txt", 1),
            new ZipEntry("appdata/d.txt", 1),
        };

        var group = ZipReader.Group(entries, "src");

        Assert.True(group.Any);
        Assert.Equal(new[] { "src/a.txt" }, group.Files.Select(entry => entry.Name).ToArray());
        Assert.Equal(string.Empty, group.Note);

        // «app» — не начало «appdata»: сверяется ЧАСТЬ пути, а не подстрока.
        Assert.False(ZipReader.Group(entries, "app").Any);
    }

    /// <summary>
    /// Та самая тихая потеря, из-за которой закрыт быстрый путь через 7-Zip: файлы в архиве есть,
    /// а группа названа иначе. Пустая группа без причины выглядела бы как «копия без корня»,
    /// поэтому причина называется, да ещё и с настоящим именем.
    /// </summary>
    [Fact]
    public void Чужой_регистр_имени_группы_называется_словами_а_не_сглаживается()
    {
        var entries = new[] { new ZipEntry("Dsh-Home/a.txt", 1) };

        var group = ZipReader.Group(entries, "dsh-home");

        Assert.False(group.Any);
        Assert.Contains("Dsh-Home", group.Note);
        Assert.Contains("точно", group.Note);
        Assert.Contains("пусто", group.Summary());
    }

    [Fact]
    public void Отсутствующая_группа_отвечает_что_её_нет()
    {
        var entries = new[] { new ZipEntry("src/a.txt", 1) };

        var group = ZipReader.Group(entries, "dsh-home");

        Assert.False(group.Any);
        Assert.Contains("нет", group.Note);
    }

    [Fact]
    public void Пустое_имя_группы_искать_нечем()
    {
        var group = ZipReader.Group(new[] { new ZipEntry("a.txt", 1) }, "  ");

        Assert.False(group.Any);
        Assert.Contains("не задано", group.Note);
    }

    // --- записи с выходом за каталог -----------------------------------------

    /// <summary>
    /// Архив — ЧУЖОЙ файл: запись <c>../../…</c> внутри него означает запись мимо места распаковки.
    /// Такие записи не попадают в список файлов, но и не исчезают: они перечислены отдельно,
    /// и пропуск виден в отчёте, а не выглядит тихой потерей.
    /// </summary>
    [Fact]
    public void Записи_с_выходом_за_каталог_отсеиваются_но_видны()
    {
        var root = NewRoot();
        try
        {
            var archive = CreateArchive(root, "slip.zip", zip =>
            {
                AddText(zip, "src/ok.txt", "OK");
                AddText(zip, "../up.txt", "UP");
                AddText(zip, "/abs.txt", "ABS");
                AddText(zip, "C:/drive.txt", "DRIVE");
                AddText(zip, "src/../../deep.txt", "DEEP");
            });

            var read = ZipReader.Read(archive);

            Assert.True(read.Ok, read.Error);
            Assert.Equal(new[] { "src/ok.txt" }, read.Entries.Select(entry => entry.Name).ToArray());
            Assert.Equal(4, read.Unsafe.Count);
            Assert.Contains("подозрительных записей: 4", read.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("src/a.txt", true)]
    [InlineData("a.txt", true)]
    [InlineData("src/sub/.hidden", true)]
    [InlineData("../a.txt", false)]
    [InlineData("src/../../a.txt", false)]
    [InlineData("/a.txt", false)]
    [InlineData("C:/a.txt", false)]
    [InlineData("", false)]
    public void Безопасность_имени_записи_решается_по_выходу_за_каталог(string name, bool safe) =>
        Assert.Equal(safe, ZipReader.IsSafe(name));

    [Theory]
    [InlineData("src/", true)]
    [InlineData(@"src\", true)]
    [InlineData("src/a.txt", false)]
    public void Запись_каталога_узнаётся_по_хвостовой_черте(string name, bool directory) =>
        Assert.Equal(directory, ZipReader.IsDirectory(name));

    [Theory]
    [InlineData("src/a.txt", "src")]
    [InlineData("a.txt", "a.txt")]
    [InlineData(@"src\sub\b.txt", "src")]
    [InlineData("", "")]
    public void Имя_группы_берётся_как_первая_часть_пути(string name, string head) =>
        Assert.Equal(head, ZipReader.Head(name));
}
