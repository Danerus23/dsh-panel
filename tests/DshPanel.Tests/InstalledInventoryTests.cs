using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DshPanel.Isolation;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОПИСЬ ТОГО, ЧТО ПАНЕЛЬ ПОСТАВИЛА НА МАШИНУ (этап D плана `ROADMAP-BORROWED.md`, п. 3).
///
/// **Зачем опись и что здесь стережётся.** Будущее удаление панели (этап G) снимет ровно то,
/// что записано; значит опись обязана, во-первых, называть поставленное (каталоги, файлы, ветка
/// реестра, ярлыки), а во-вторых — **не видеть чужого**: ни домашнего каталога движка, ни дерева
/// Node (их панель не ставила, и красная линия запрещает их удалять), ни описи владельца
/// в изолированном прогоне.
///
/// **Проверки идут на СВОЁМ временном корне** (`AppPaths.Under`) — каталогов владельца эта проба
/// не касается, и опись владельца не читает. Единственное обращение к настоящему корню —
/// сравнение путей в проверке изоляции, и оно ничего не пишет.
/// </summary>
public sealed class InstalledInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dsh-installed-" + Guid.NewGuid().ToString("N"));

    public InstalledInventoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Мусор в %TEMP% — не повод рушить прогон.
        }
    }

    /// <summary>Опись называет и каталоги, и файлы, и ветку реестра, и ярлык — по одному на вид.</summary>
    [Fact]
    public void Опись_называет_каталоги_файлы_реестр_и_ярлыки()
    {
        var paths = AppPaths.Under(_root);
        var shortcut = Path.Combine(_root, "ярлык.lnk");

        var entries = InstalledInventory.Collect(paths, @"C:\Panel\DshPanel.exe", autostart: true, new[] { shortcut });

        Assert.Contains(entries, entry => entry.Kind == InstalledInventory.KindDirectory && entry.Path == paths.Root);
        Assert.Contains(entries, entry => entry.Kind == InstalledInventory.KindDirectory && entry.Path == paths.BackupsDir);
        Assert.Contains(entries, entry => entry.Kind == InstalledInventory.KindFile && entry.Path == paths.InstalledFile);
        Assert.Contains(entries, entry => entry.Kind == InstalledInventory.KindFile && entry.Path == @"C:\Panel\DshPanel.exe");
        Assert.Contains(entries, entry => entry.Kind == InstalledInventory.KindShortcut && entry.Path == shortcut);

        var registry = Assert.Single(entries, entry => entry.Kind == InstalledInventory.KindRegistry);
        Assert.Contains("Run", registry.Path, StringComparison.Ordinal);
        Assert.Contains("DSHPanel2", registry.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// АВТОЗАПУСК ВЫКЛЮЧЕН — ВЕТКИ РЕЕСТРА В ОПИСИ НЕТ: панель туда ничего не писала, и опись
    /// «на всякий случай» была бы выдумкой, за которую будущее удаление сняло бы чужую запись
    /// (или соседнюю живую панель 1.x — она живёт в той же ветке под именем `DSHPanel`).
    /// </summary>
    [Fact]
    public void Без_автозапуска_ветки_реестра_нет()
    {
        var entries = InstalledInventory.Collect(AppPaths.Under(_root), "exe", autostart: false, Array.Empty<string>());

        Assert.DoesNotContain(entries, entry => entry.Kind == InstalledInventory.KindRegistry);
        Assert.NotEmpty(entries);
    }

    /// <summary>
    /// ОПИСЬ НЕ НАЗЫВАЕТ ЧУЖОГО: ни домашнего каталога движка, ни ключей, ни дерева Node.
    /// Это половина красной линии — «удалять что-либо из `~/.dsh` и дерева движка нельзя»;
    /// вторая половина в том, что списка путей для удаления здесь вообще нет, а есть опись факта.
    /// </summary>
    [Fact]
    public void Опись_не_называет_дом_движка_и_ключи()
    {
        var paths = AppPaths.Under(_root);
        var entries = InstalledInventory.Collect(paths, "exe", autostart: true, Array.Empty<string>());

        Assert.NotEmpty(entries);
        Assert.DoesNotContain(entries, entry => entry.Path == paths.DshHome);
        Assert.DoesNotContain(entries, entry => entry.Path == paths.CredentialsPath);
        Assert.DoesNotContain(entries, entry => entry.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ИЗОЛИРОВАННЫЙ ПРОГОН НЕ ВИДИТ ОПИСИ ВЛАДЕЛЬЦА — приёмка пункта 3, обе половины:
    /// путь изолированного прогона лежит под его корнем и НЕ совпадает с путём владельца,
    /// а всё, что изолированный прогон называет, — под его корнем (кроме ветки реестра: она
    /// не файл, и её в изолированном прогоне заменяет свой файл, см. `AppPaths.AutostartFile`).
    /// </summary>
    [Fact]
    public void Изолированный_прогон_не_видит_описи_владельца()
    {
        var isolated = AppPaths.Under(_root);
        var owner = AppPaths.ForOwner();

        Assert.NotEqual(owner.InstalledFile, isolated.InstalledFile);
        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar,
            isolated.InstalledFile,
            StringComparison.OrdinalIgnoreCase);

        // Исполняемый файл панели лежит ТАМ, ОТКУДА ЕЁ ЗАПУСТИЛИ, — то есть вне корня прогона,
        // и это не дыра: панель не ставит себя в свой корень данных. В описи он записан фактом
        // (путь передан аргументом), а не сложен из корня, — поэтому здесь он и исключён.
        // КОРЕНЬ: путь к своему exe для описи поставленного; сравнивается как путь, а не показывается.
        var exe = Environment.ProcessPath ?? string.Empty;
        Assert.NotEmpty(exe);

        var entries = InstalledInventory.Collect(isolated, exe, autostart: true, Array.Empty<string>());
        Assert.NotEmpty(entries);

        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        var canonical = Path.TrimEndingDirectorySeparator(_root);

        foreach (var entry in entries)
        {
            // Ветка реестра — не путь файловой системы: её подмена у изолированного прогона своя
            // (`AutostartFile`) и проверяется отдельно, изоляцией настроек. Здесь — только пути.
            if (!Path.IsPathRooted(entry.Path)) continue;
            if (string.Equals(entry.Path, exe, StringComparison.OrdinalIgnoreCase)) continue;

            // Корень «внутри» самого себя не лежит — он ему равен; допустимы оба случая
            // (то же послабление, что в `IsolationTests`).
            var inside = string.Equals(
                    Path.TrimEndingDirectorySeparator(entry.Path), canonical, StringComparison.OrdinalIgnoreCase)
                || entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            Assert.True(inside, $"опись назвала путь вне корня изолированного прогона: «{entry.Path}»");
        }
    }

    /// <summary>
    /// ОПИСЬ ПИШЕТСЯ И ЧИТАЕТСЯ: файл лежит там, где сказано (`state\installed.json`), это JSON
    /// с видами и путями, и он БЕЗ BOM — правило проекта для `.json` (иначе его ломает
    /// PowerShell 5.1 при чтении глазами).
    /// </summary>
    [Fact]
    public void Опись_пишется_и_читается()
    {
        var paths = AppPaths.Under(_root);

        var ok = InstalledInventory.Write(
            paths, @"C:\Panel\DshPanel.exe", autostart: true, out var reason, out var entries, exists: _ => false);

        Assert.True(ok, reason);
        Assert.True(entries > 0, "запись прошла, а строк в описи нет");
        Assert.True(File.Exists(paths.InstalledFile), $"нет файла описи: «{paths.InstalledFile}»");

        var bytes = File.ReadAllBytes(paths.InstalledFile);
        Assert.True(bytes.Length > 2, "файл описи пуст");
        Assert.False(
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "опись записана с BOM — правило проекта для .json обратное");

        using var document = JsonDocument.Parse(File.ReadAllText(paths.InstalledFile));
        var rows = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(entries, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True(row.TryGetProperty("Kind", out _), "в строке описи нет вида");
            Assert.True(row.TryGetProperty("Path", out _), "в строке описи нет пути");
        });
    }

    /// <summary>
    /// НЕУДАЧА ОПИСИ НАЗЫВАЕТ ТИП, А НЕ ПУТЬ: строка отказа уходит в журнал, а в сообщениях .NET
    /// бывает путь с именем человека. Здесь путь занят файлом — запись обязана не бросить
    /// исключение, а вернуть отказ.
    /// </summary>
    [Fact]
    public void Неудача_описи_называет_тип_и_не_выносит_путь()
    {
        var paths = AppPaths.Under(_root);
        File.WriteAllText(paths.StateDir, "занято файлом");

        var ok = InstalledInventory.Write(
            paths, "exe", autostart: false, out var reason, out _, exists: _ => false);

        Assert.False(ok);
        Assert.NotEmpty(reason);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, reason);
        Assert.DoesNotContain(_root, reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ворота по исходникам ---------------------------------------------------------------

    /// <summary>
    /// ИМЯ ПРОДУКТА НЕ ВЫДУМЫВАЕТСЯ ВТОРОЙ РАЗ. Опись ищет ярлыки по имени, которое берёт
    /// у сборки; то же имя стоит в проекте (`&lt;Product&gt;`) и в установщике (`#define AppName`).
    /// Проверка читает исходник описи и падает, если имя вернулось в код строкой: две копии имени
    /// однажды разойдутся, и ярлыки будут искаться по устаревшему.
    /// </summary>
    [Fact]
    public void Имя_продукта_берётся_у_сборки_а_не_пишется_строкой()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var project = Path.Combine(root, "src", "DshPanel", "DshPanel.csproj");
        var inventory = Path.Combine(root, "src", "DshPanel", "Isolation", "InstalledInventory.cs");

        Assert.True(File.Exists(project), $"нет проекта панели: «{project}»");
        Assert.True(File.Exists(inventory), $"нет исходника описи: «{inventory}»");

        var product = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(project), @"<Product>([^<]+)</Product>");

        Assert.True(product.Success, "в проекте панели нет <Product> — имя продукта стало ниоткуда");

        var name = product.Groups[1].Value.Trim();
        Assert.NotEmpty(name);

        // В описи имя встречается только как часть имён вида `ProductName` — строкой его быть не должно.
        Assert.DoesNotContain($"\"{name}\"", File.ReadAllText(inventory), StringComparison.Ordinal);
    }
}
