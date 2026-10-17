using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DshPanel.Files;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// УБОРКА УБИРАЕТ — И ЭТО ПРОВЕРЕНО, А НЕ ОБЕЩАНО (этап D плана `ROADMAP-BORROWED.md`).
///
/// **Что здесь стережётся, и почему именно это.** Замер 10.10.2026 на этой машине дал три ответа
/// на три дерева, удалявшиеся прежним способом панели (`Directory.Delete(path, recursive: true)`
/// в пустом `catch`):
///
///   * **не-ASCII имена** — убралось, отсутствие подтверждено. Беды соседа (`C:\Users\数据项素\…`)
///     в .NET НЕТ, и она закрыта словами, а не кодом;
///   * **junction внутри дерева** — исключение `Access to the path '…' is denied`, **дерево
///     осталось**. Сама ссылка снята, цель и её файл целы: внутрь ссылки .NET не идёт — опасность
///     не «уносит чужое» (это грабля PowerShell 5.1 и `Remove-Item -Recurse`), а «не удаляет
///     вовсе, отвечая успехом»;
///   * **файл «только для чтения» внутри дерева** — то же самое: исключение, дерево осталось.
///     Этот случай нашёлся замером ЭТОЙ работы, и он не про имена вовсе.
///
/// Поэтому здесь проверяются ОБЕ половины нового правила: дерево обязано **исчезнуть** (проверка
/// после уборки) и цель ссылки обязана **уцелеть** (внутрь не ходим). Проба наивного помощника
/// роняет первую половину на junction, арифметика на «снять атрибут» — вторую.
///
/// Файловый корень пробы — свой временный каталог; каталогов владельца эта проверка не касается.
/// </summary>
public sealed class TreeDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dsh-tree-" + Guid.NewGuid().ToString("N"));

    public TreeDeletionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TreeDeletion.Remove(_root);

    // ---- дерево исчезает: три случая замера ------------------------------------------------

    /// <summary>Не-ASCII имена (русские каталоги и файлы) — тот случай, из-за которого сосед
    /// завёл весь пункт. У нас он убирается, и это подтверждается отсутствием, а не молчанием.</summary>
    [Fact]
    public void НеASCII_дерево_убирается()
    {
        var tree = Path.Combine(_root, "Проба-панель-Ω");
        var nested = Path.Combine(tree, "вложенный каталог");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "файл-1.txt"), "данные");

        Assert.True(Directory.Exists(tree));

        var removal = TreeDeletion.Remove(tree);

        Assert.True(removal.Ok, removal.Reason);
        Assert.False(Directory.Exists(tree));
        Assert.Equal(1, removal.Files);
    }

    /// <summary>
    /// ЗАМЕР, РАДИ КОТОРОГО ЗАВЕДЁН ПОМОЩНИК: файл «только для чтения» внутри дерева.
    /// Прежний способ падал на нём (`Access to the path '…' is denied`), дерево оставалось,
    /// а пустой `catch` отвечал на это успехом.
    ///
    /// Атрибут снимается ТОЛЬКО у файлов внутри удаляемого дерева: без этого копия панели,
    /// снятая через `File.Copy` (а он переносит атрибуты), делала бы уборку невозможной навсегда.
    /// </summary>
    [Fact]
    public void Файл_только_для_чтения_не_оставляет_дерево()
    {
        var tree = Path.Combine(_root, "с-чтением");
        var file = Path.Combine(tree, "только-чтение.txt");
        Directory.CreateDirectory(tree);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var removal = TreeDeletion.Remove(tree);

        Assert.True(removal.Ok, removal.Reason);
        Assert.False(Directory.Exists(tree));
        Assert.False(File.Exists(file));
    }

    /// <summary>Пустой вложенности нет: дерево в дереве тоже исчезает целиком.</summary>
    [Fact]
    public void Вложенные_каталоги_убираются_целиком()
    {
        var tree = Path.Combine(_root, "верх");
        Directory.CreateDirectory(Path.Combine(tree, "а", "б", "в"));
        File.WriteAllText(Path.Combine(tree, "а", "б", "в", "файл.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "верхний.txt"), "x");

        var removal = TreeDeletion.Remove(tree);

        Assert.True(removal.Ok, removal.Reason);
        Assert.False(Directory.Exists(tree));
        Assert.Equal(2, removal.Files);
    }

    // ---- ссылка снимается, а цель остаётся целой -------------------------------------------

    /// <summary>
    /// JUNCTION ВНУТРИ ДЕРЕВА: помощник снимает САМУ ссылку, а цель и её файл остаются целыми.
    /// Наивный помощник (`Directory.Delete(recursive: true)`) роняет эту проверку: он бросает
    /// исключение, и дерево остаётся на месте — то есть `Ok` был бы `false`, а `Directory.Exists`
    /// истиной.
    /// </summary>
    [Fact]
    public void Ссылка_снимается_а_цель_остаётся_целой()
    {
        var tree = Path.Combine(_root, "дерево");
        var target = Path.Combine(_root, "цель");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(target);

        var foreign = Path.Combine(target, "чужое.txt");
        File.WriteAllText(foreign, "чужое");

        var link = Path.Combine(tree, "ссылка");
        Assert.True(MakeJunction(link, target), "не удалось завести junction — проба ничего не проверила бы");
        Assert.True(
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "ссылка завелась, но точкой разбора не является — проба проверяла бы не то");

        var removal = TreeDeletion.Remove(tree);

        Assert.True(removal.Ok, removal.Reason);
        Assert.Equal(1, removal.Links);
        Assert.False(Directory.Exists(tree));
        Assert.True(Directory.Exists(target), "цель ссылки пропала — уборка ушла внутрь ссылки");
        Assert.Equal("чужое", File.ReadAllText(foreign));
    }

    /// <summary>
    /// ССЫЛКА БЕЗ ЦЕЛИ (цель уже удалена) тоже снимается, а не остаётся мусором.
    ///
    /// ⚠️ Замер 10.10.2026: сломанная junction для `Directory.Exists` **видна** — точка разбора
    /// сама по себе каталог, и «раз каталога нет, значит и ссылки нет» на ней не работает. Поэтому
    /// здесь проверяется не «Exists молчит», а оба ФАКТА: цели нет, а ссылка на диске есть
    /// (`File.GetAttributes` видит точку разбора) — и помощник обязан снять её.
    /// </summary>
    [Fact]
    public void Ссылка_без_цели_снимается()
    {
        var tree = Path.Combine(_root, "дерево-сломанное");
        var target = Path.Combine(_root, "цель-сломанная");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(target);

        var link = Path.Combine(tree, "ссылка");
        Assert.True(MakeJunction(link, target), "не удалось завести junction — проба ничего не проверила бы");

        Directory.Delete(target, recursive: true);

        Assert.False(Directory.Exists(target), "цель не удалилась — ссылка не сломана, проба проверяла бы не то");
        Assert.True(TreeDeletion.Exists(link), "ссылка без цели не видна — помощник прошёл бы мимо неё");
        Assert.True(
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "ссылка без цели перестала быть точкой разбора");

        var removal = TreeDeletion.Remove(tree);

        Assert.True(removal.Ok, removal.Reason);
        Assert.Equal(1, removal.Links);
        Assert.False(TreeDeletion.Exists(link), "ссылка осталась мусором");
        Assert.False(Directory.Exists(tree));
    }

    // ---- неудача называется, а не выдаётся за успех -----------------------------------------

    /// <summary>
    /// ЗАНЯТОЕ ДЕРЕВО: убрать нельзя — и помощник говорит об этом причиной, а не молчанием.
    /// Ради этой половины и заведена проверка после уборки: «команда не бросила исключение»
    /// успехом не считается.
    /// </summary>
    [Fact]
    public void Занятое_дерево_называет_причину()
    {
        var tree = Path.Combine(_root, "занятое");
        var file = Path.Combine(tree, "файл.txt");
        Directory.CreateDirectory(tree);
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var removal = TreeDeletion.Remove(tree);

            Assert.False(removal.Ok);
            Assert.NotEmpty(removal.Reason);
            Assert.True(Directory.Exists(tree), "дерево исчезло, хотя файл был занят — проверка после уборки слепа");
        }
    }

    /// <summary>Убирать нечего — это тоже законный ответ, а не отказ.</summary>
    [Fact]
    public void Пустой_путь_и_отсутствующий_каталог_это_успех()
    {
        Assert.True(TreeDeletion.Remove(null).Ok);
        Assert.True(TreeDeletion.Remove("   ").Ok);
        Assert.True(TreeDeletion.Remove(Path.Combine(_root, "нет-такого")).Ok);
    }

    // ---- ворота по исходникам ---------------------------------------------------------------

    /// <summary>
    /// УДАЛЕНИЕ ДЕРЕВА ЖИВЁТ ОДНИМ МЕСТОМ. Проверка читает `src\DshPanel\**\*.cs` и падает,
    /// называя файл, в котором снова завелось `Directory.Delete(…, recursive: true)`: именно этот
    /// вызов оставлял дерево на junction и на файле «только для чтения».
    ///
    /// Пояснения выкидываются: рассказать о правиле рядом с вызовом не значит нарушить его
    /// (в самом помощнике такой рассказ как раз и стоит).
    /// </summary>
    [Fact]
    public void Удаление_дерева_живёт_одним_местом()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var source = Path.Combine(root, "src", "DshPanel");

        if (!Directory.Exists(source))
            Assert.Fail($"нет каталога исходников панели: «{source}»");

        var files = Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => new SourceStringsTests.SourceFile(path, File.ReadAllText(path)))
            .ToList();

        Assert.True(files.Count > 100, $"прочитано файлов исходников: {files.Count} — это не дерево панели");

        var helper = Path.Combine(source, "Files", "TreeDeletion.cs");
        Assert.True(File.Exists(helper), $"нет самого помощника уборки: «{helper}»");

        var guilty = files
            .Where(file => file.Path != helper)
            .Where(file => UiThreadTests.WithoutComments(file.Text).Contains("recursive: true", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.True(
            guilty.Count == 0,
            "удаление дерева завелось мимо `Files\\TreeDeletion.cs` — там, где ссылка разворачивается, "
                + "а неудача выдаётся за успех:\n" + string.Join("\n", guilty));

        // И вторая половина правила: помощник обязан СНИМАТЬ ссылку и ПРОВЕРЯТЬ отсутствие.
        var text = UiThreadTests.WithoutComments(File.ReadAllText(helper));
        Assert.Contains("ReparsePoint", text, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReadOnly", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ссылка заводится junction-ом через `mklink /J`: он не требует прав администратора
    /// (в отличие от символьной ссылки) и работает на обычной машине. Возвращает признак успеха —
    /// проба обязана УПАСТЬ, если ссылку завести не удалось, а не пройти мимо.
    /// </summary>
    private static bool MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null) return false;

        process.WaitForExit();

        // Мало «команда сказала 0»: ссылка обязана быть ТОЧКОЙ РАЗБОРА, иначе проба проверяла бы
        // обычный каталог и наивный помощник прошёл бы её не заметив.
        return process.ExitCode == 0
            && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
    }
}
