using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DshPanel.Backup;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки копий: показ размера и длительности, правила раскладки архива, поиск 7-Zip.
///
/// Ни одна из них не касается диска владельца: правила раскладки — чистые решения, поиск
/// 7-Zip — на подставных «файл есть» и «переменная среды», а размер и длительность — числа.
/// Настоящая копия и настоящий 7-Zip проверяются отдельно, самотестом на изолированном корне.
/// </summary>
public class BackupTests
{
    // --- показ размера и длительности (первый артефакт этапа 4) --------------

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1024, "1.0 КБ")]
    [InlineData(1048575, "1024.0 КБ")]
    [InlineData(1048576, "1.0 МБ")]
    [InlineData(1073741823, "1024.0 МБ")]
    [InlineData(1073741824, "1.00 ГБ")]
    [InlineData(5915586560, "5.51 ГБ")]
    public void Размер_показывается_по_порогам_как_в_v1(long bytes, string expected) =>
        Assert.Equal(expected, BackupFormat.Size(bytes));

    [Theory]
    [InlineData(0, "0 мс")]
    [InlineData(500, "500 мс")]
    [InlineData(999, "999 мс")]
    [InlineData(1000, "1.0 с")]
    [InlineData(59_900, "59.9 с")]
    [InlineData(60_000, "1.0 мин")]
    [InlineData(150_000, "2.5 мин")]
    public void Длительность_показывается_по_порогам_как_в_v1(int milliseconds, string expected) =>
        Assert.Equal(expected, BackupFormat.Duration(TimeSpan.FromMilliseconds(milliseconds)));

    // --- правила раскладки архива -------------------------------------------

    [Fact]
    public void Имя_папки_берётся_без_пути_и_без_хвостового_разделителя()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-layout", ".dsh");
        Assert.Equal(".dsh", ZipLayout.Leaf(root));
        Assert.Equal(".dsh", ZipLayout.Leaf(root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void У_корня_диска_имени_папки_нет()
    {
        var system = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(string.Empty, ZipLayout.Leaf(system));
    }

    [Fact]
    public void Семь_зип_годится_когда_имя_группы_совпадает_с_именем_папки()
    {
        var fit = ZipLayout.CanSevenZip(new[]
        {
            new BackupSource(Path.Combine(Path.GetTempPath(), "dsh-home"), "dsh-home"),
            new BackupSource(Path.Combine(Path.GetTempPath(), "Harness"), "Harness"),
        });

        Assert.True(fit.Can);
        Assert.Equal(string.Empty, fit.Reason);
    }

    [Fact]
    public void Своё_имя_группы_закрывает_быстрый_путь_через_семь_зип()
    {
        var fit = ZipLayout.CanSevenZip(new[]
        {
            new BackupSource(Path.Combine(Path.GetTempPath(), ".dsh"), "dsh-home"),
        });

        Assert.False(fit.Can);
        Assert.Contains("dsh-home", fit.Reason);
        Assert.Contains(".dsh", fit.Reason);
    }

    [Fact]
    public void Вложенное_имя_группы_закрывает_быстрый_путь()
    {
        var fit = ZipLayout.CanSevenZip(new[]
        {
            new BackupSource(Path.Combine(Path.GetTempPath(), "keys-01"), "keys/01"),
        });

        Assert.False(fit.Can);
    }

    [Fact]
    public void Две_группы_с_одним_именем_закрывают_быстрый_путь()
    {
        var fit = ZipLayout.CanSevenZip(new[]
        {
            new BackupSource(Path.Combine(Path.GetTempPath(), "one", "keys"), "keys"),
            new BackupSource(Path.Combine(Path.GetTempPath(), "two", "keys"), "keys"),
        });

        Assert.False(fit.Can);
        Assert.Contains("одним именем", fit.Reason);
    }

    [Fact]
    public void Корень_диска_закрывает_быстрый_путь()
    {
        var system = Path.GetPathRoot(Path.GetTempPath())!;
        var fit = ZipLayout.CanSevenZip(new[] { new BackupSource(system, "disk") });

        Assert.False(fit.Can);
    }

    /// <summary>
    /// Регистр сверяется ТОЧНО, и это не придирка: 7-Zip кладёт запись под именем с диска,
    /// а опись ищет группу под своим именем. Расхождение только в регистре потеряло бы
    /// целый корень копии — молча, при «успешной» съёмке.
    /// </summary>
    [Fact]
    public void Регистр_имени_группы_сверяется_точно()
    {
        var fit = ZipLayout.CanSevenZip(new[]
        {
            new BackupSource(Path.Combine(Path.GetTempPath(), "dsh-home"), "DSH-HOME"),
        });

        Assert.False(fit.Can);
    }

    [Fact]
    public void Без_источников_архивировать_нечего()
    {
        var fit = ZipLayout.CanSevenZip(Array.Empty<BackupSource>());
        Assert.False(fit.Can);
    }

    [Fact]
    public void Способ_записи_выбирается_по_наличию_семь_зип_и_по_раскладке()
    {
        var fitting = new[] { new BackupSource(Path.Combine(Path.GetTempPath(), "Harness"), "Harness") };
        var notFitting = new[] { new BackupSource(Path.Combine(Path.GetTempPath(), ".dsh"), "dsh-home") };

        var безSevenZip = ZipLayout.ChooseWriter(fitting, null);
        Assert.Equal(ZipWriterKind.BuiltIn, безSevenZip.Writer);
        Assert.Contains("не найден", безSevenZip.Reason);

        var поРаскладке = ZipLayout.ChooseWriter(notFitting, @"C:\Program Files\7-Zip\7z.exe");
        Assert.Equal(ZipWriterKind.BuiltIn, поРаскладке.Writer);

        var быстрый = ZipLayout.ChooseWriter(fitting, @"C:\Program Files\7-Zip\7z.exe");
        Assert.Equal(ZipWriterKind.SevenZip, быстрый.Writer);
    }

    // --- поиск 7-Zip --------------------------------------------------------

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) map[name] = value;
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Тот самый случай, ради которого поиск идёт по кандидатам: 7-Zip УСТАНОВЛЕН, но в PATH
    /// его нет. Проверено на этой машине 25.09.2026 — `Get-Command 7z` пусто, а файл на месте.
    /// </summary>
    [Fact]
    public void Семь_зип_находится_когда_его_нет_в_PATH()
    {
        var expected = @"C:\Program Files\7-Zip\7z.exe";

        var found = SevenZip.Find(
            fileExists: path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase),
            environment: Env(("ProgramFiles", @"C:\Program Files")),
            pathDirectories: () => Array.Empty<string>());

        Assert.Equal(expected, found);
    }

    [Fact]
    public void Подмена_пути_к_семь_зип_идёт_первой()
    {
        var expected = @"D:\portable\7z.exe";
        var обычный = @"C:\Program Files\7-Zip\7z.exe";

        // Существуют ОБА: иначе проверка утверждала бы «нашёл подмену», ничего не говоря
        // о порядке. А порядок здесь и есть предмет проверки — подмена главнее установки.
        var found = SevenZip.Find(
            fileExists: path =>
                string.Equals(path, expected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, обычный, StringComparison.OrdinalIgnoreCase),
            environment: Env(
                (SevenZip.EnvOverride, expected),
                ("ProgramFiles", @"C:\Program Files")),
            pathDirectories: () => Array.Empty<string>());

        Assert.Equal(expected, found);
    }

    [Fact]
    public void PATH_просматривается_последним()
    {
        var изPATH = @"C:\tools\7z.exe";
        var обычный = @"C:\Program Files\7-Zip\7z.exe";

        // Оба на месте — проверяем именно ПОРЯДОК: известное место главнее PATH.
        var found = SevenZip.Find(
            fileExists: path =>
                string.Equals(path, изPATH, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, обычный, StringComparison.OrdinalIgnoreCase),
            environment: Env(("ProgramFiles", @"C:\Program Files")),
            pathDirectories: () => new[] { @"C:\ничего", @"C:\tools" });

        Assert.Equal(обычный, found);
    }

    [Fact]
    public void Семь_зип_находится_в_PATH_когда_известных_мест_нет()
    {
        var изPATH = @"C:\tools\7z.exe";

        var found = SevenZip.Find(
            fileExists: path => string.Equals(path, изPATH, StringComparison.OrdinalIgnoreCase),
            environment: Env(("ProgramFiles", @"C:\Program Files")),
            pathDirectories: () => new[] { @"C:\ничего", @"C:\tools" });

        Assert.Equal(изPATH, found);
    }

    [Fact]
    public void Когда_семь_зип_нигде_нет_ответ_пустой()
    {
        var found = SevenZip.Find(
            fileExists: _ => false,
            environment: Env(("ProgramFiles", @"C:\Program Files")),
            pathDirectories: () => new[] { @"C:\tools" });

        Assert.Null(found);
    }

    [Fact]
    public void Строка_о_семь_зип_не_выносит_имя_пользователя()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(profile, "scoop", "shims", "7z.exe");

        var text = SevenZip.Describe(path);

        Assert.Contains("~", text);
        Assert.DoesNotContain(profile, text);
    }

    // --- запись архива: оба способа обязаны дать одно и то же дерево ---------

    /// <summary>
    /// Стенд: вложенность, скрытый файл, ПУСТОЙ файл и САМОССЫЛАЮЩИЙСЯ junction — ровно та
    /// ловушка, что лежит в пакете движка (`docs\ARCHIVE-MEASUREMENT.md`, находка 1).
    /// Junction делает сама Windows: воспроизвести эту ловушку иначе нечем.
    /// </summary>
    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-zip-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "src");

        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "B");
        File.WriteAllText(Path.Combine(source, ".hidden"), "H");
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), Array.Empty<byte>());

        var link = Path.Combine(source, "self");
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{source}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(start);
        process?.WaitForExit();

        return root;
    }

    /// <summary>
    /// Уборка снимает junction ЗВЕНОМ, а не деревом: рекурсивное удаление по каталогу со ссылкой
    /// внутри — известный способ снести не то, что собирался (в v1 это записано отдельным правилом).
    /// </summary>
    private static void Cleanup(string root)
    {
        try
        {
            var link = Path.Combine(root, "src", "self");
            if (Directory.Exists(link)) Directory.Delete(link);
        }
        catch
        {
            // Ссылки может уже не быть — это не повод падать в уборке.
        }

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP% — если не убрался, это не провал проверки.
        }
    }

    private static string[] FilesInside(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);

        return zip.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(name => !name.EndsWith('/'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Распаковка средствами .NET — так же, как это будет делать накат: без 7-Zip.</summary>
    private static SortedDictionary<string, byte[]> Unpack(string archive, string target)
    {
        ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);

        var map = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(target, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return map;
    }

    [Fact]
    public void Свой_обход_кладёт_дерево_и_не_уходит_в_самоссылку()
    {
        var root = CreateFixture();
        try
        {
            var source = Path.Combine(root, "src");
            var archive = Path.Combine(root, "built-in.zip");

            var result = ZipWriter.Write(archive, new[] { new BackupSource(source, "src") });

            Assert.True(result.Ok, result.Error);
            Assert.Equal(ZipWriterKind.BuiltIn, result.Writer);

            // Ровно четыре файла: junction в обход не пошёл. Пойди он туда — файлов были бы сотни.
            Assert.Equal(4, result.Files);
            Assert.Equal(
                new[] { "src/.hidden", "src/a.txt", "src/empty.bin", "src/sub/b.txt" },
                FilesInside(archive));

            var tree = Unpack(archive, Path.Combine(root, "out"));
            Assert.Equal("A", System.Text.Encoding.UTF8.GetString(tree["src/a.txt"]));
            Assert.Empty(tree["src/empty.bin"]);

            // Разделитель в записях — прямая косая черта: так требует формат zip, и по этому
            // соглашению опись и накат ищут группы внутри архива.
            using (var zip = ZipFile.OpenRead(archive))
            {
                Assert.All(zip.Entries, entry => Assert.DoesNotContain("\\", entry.FullName));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Мусор_в_копию_не_попадает()
    {
        var root = CreateFixture();
        try
        {
            var source = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(source, "bin"));
            Directory.CreateDirectory(Path.Combine(source, "node_modules"));
            File.WriteAllText(Path.Combine(source, "bin", "мусор.dll"), "мусор");
            File.WriteAllText(Path.Combine(source, "node_modules", "мусор.js"), "мусор");

            var archive = Path.Combine(root, "skip.zip");
            var result = ZipWriter.Write(archive, new[] { new BackupSource(source, "src") });

            Assert.True(result.Ok, result.Error);
            Assert.DoesNotContain(FilesInside(archive), name => name.Contains("мусор"));

            // А корень, который обходится ЦЕЛИКОМ (движок, Node), тот же node_modules кладёт:
            // там он и есть содержимое, и выбрасывать его нельзя.
            var whole = Path.Combine(root, "whole.zip");
            var complete = ZipWriter.Write(whole, new[] { new BackupSource(source, "src", Everything: true) });

            Assert.True(complete.Ok, complete.Error);
            Assert.Contains(FilesInside(whole), name => name.Contains("мусор"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Главная проверка решения владельца: оба способа — быстрый через 7-Zip и свой — кладут ОДНО
    /// дерево, и накат средствами .NET возвращает его одинаково. Плюс архив своего обхода проходит
    /// стороннюю проверку `7z t`: встроенный распаковщик контрольных сумм НЕ сверяет, и поломку
    /// видит только чужой валидатор (<c>docs\ARCHIVE-MEASUREMENT.md</c>, разбор пустых файлов).
    /// </summary>
    [Fact]
    public void Оба_способа_кладут_одно_и_то_же_дерево()
    {
        var sevenZip = SevenZip.Find();
        Assert.SkipWhen(sevenZip is null, "7-Zip на этой машине не найден — быстрый путь проверить нечем");

        var root = CreateFixture();
        try
        {
            var source = Path.Combine(root, "src");
            var sources = new[] { new BackupSource(source, "src") };
            var slow = Path.Combine(root, "slow.zip");
            var quick = Path.Combine(root, "quick.zip");

            var builtIn = ZipWriter.Write(slow, sources);
            var fast = ZipWriter.Write(quick, sources, sevenZipPath: sevenZip);

            Assert.True(builtIn.Ok, builtIn.Error);
            Assert.True(fast.Ok, fast.Error);
            Assert.Equal(ZipWriterKind.BuiltIn, builtIn.Writer);
            Assert.Equal(ZipWriterKind.SevenZip, fast.Writer);
            Assert.Equal(builtIn.Files, fast.Files);

            var left = Unpack(slow, Path.Combine(root, "out-slow"));
            var right = Unpack(quick, Path.Combine(root, "out-quick"));

            Assert.Equal(
                new[] { "src/.hidden", "src/a.txt", "src/empty.bin", "src/sub/b.txt" },
                left.Keys.ToArray());
            Assert.Equal(left.Keys, right.Keys);
            foreach (var name in left.Keys) Assert.Equal(left[name], right[name]);

            var verify = SevenZip.Verify(sevenZip!, slow);
            Assert.True(verify.Ok, "7-Zip нашёл поломку в архиве, снятом своими силами: " + verify.Output);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// 7-Zip нашёлся, но подвёл: копия всё равно обязана сняться. Проверяем на заведомо негодном
    /// пути к программе — так же ведёт себя сломанная или чужой версии установка.
    /// </summary>
    [Fact]
    public void Если_семь_зип_подвёл_копия_всё_равно_снимается()
    {
        var root = CreateFixture();
        try
        {
            var source = Path.Combine(root, "src");
            var archive = Path.Combine(root, "fallback.zip");

            var result = ZipWriter.Write(
                archive,
                new[] { new BackupSource(source, "src") },
                sevenZipPath: Path.Combine(root, "нет-такой-программы", "7z.exe"));

            Assert.True(result.Ok, result.Error);
            Assert.Equal(ZipWriterKind.BuiltIn, result.Writer);
            Assert.Contains("не справился", result.Reason);
            Assert.Equal(4, result.Files);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
