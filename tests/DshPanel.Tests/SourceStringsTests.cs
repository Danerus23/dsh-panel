using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки САМИХ ИСХОДНИКОВ панели — то, чего не видят ни словари, ни отражение.
///
/// Две беды, каждая уже случалась:
///
/// 1. **Мёртвая строка.** Член <c>PanelStrings</c> объявлен, переведён на три языка и никем не
///    вызван: работа сделана дважды (объявление и три перевода), а человек не видит ничего.
///    Так были найдены и убраны вручную <c>RestorePickFirst</c> и <c>JunctionRefusedFormat</c>.
///    Автоматики, которая поймает следующую такую, до этой проверки не существовало вовсе.
/// 2. **Текст в разметке.** Правило проекта «ни одного текста в разметке»: подписи ставятся
///    кодом из <c>PanelStrings</c>, а тот берёт их из словаря. Литерал в <c>.axaml</c> остаётся
///    русским на английском и китайском кадре — и на трёх языках окно выглядит разноязычным.
///
/// Ядро обеих проверок — ЧИСТЫЕ функции (<see cref="UnusedMembers"/>,
/// <see cref="ProductSourceText"/>, <see cref="TextsInMarkup"/>): подставной список имён и
/// подставной текст исходников. Поэтому проверку можно проверить, не уродуя дерево
/// (проверки «Ядро…» ниже), а не «сломать настоящий файл и посмотреть».
/// </summary>
public class SourceStringsTests
{
    // ---- ядро: что считается вызовом строки ------------------------------------------------

    /// <summary>Файл исходников: путь нужен правилам исключения, текст — поиску.</summary>
    public sealed record SourceFile(string Path, string Text);

    /// <summary>
    /// Какие из имён НЕ встречаются в переданном тексте как СЛОВО. Это и есть ядро проверки 1:
    /// ни файлов, ни диска оно не знает — значит его можно прогнать на подставных данных.
    /// </summary>
    /// <remarks>
    /// Именно слово (<c>\bИмя\b</c>), а не подстрока: <c>BackupListLabel</c> и
    /// <c>BackupListLabelFormat</c> — РАЗНЫЕ строки, и «нашлась подстрока» не значит «строка
    /// используется». На этой подмене проверка замолчала бы целиком.
    /// </remarks>
    public static IReadOnlyList<string> UnusedMembers(IEnumerable<string> names, string sources)
    {
        ArgumentNullException.ThrowIfNull(names);

        var haystack = sources ?? string.Empty;
        var unused = new List<string>();

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;

            var pattern = @"\b" + Regex.Escape(name) + @"\b";

            if (!Regex.IsMatch(haystack, pattern, RegexOptions.CultureInvariant))
            {
                unused.Add(name);
            }
        }

        return unused;
    }

    /// <summary>
    /// Считается ли этот файл местом ВЫЗОВА строки. Два исключения, и оба — не формальность:
    /// </summary>
    /// <remarks>
    /// * <c>PanelStrings.cs</c> — это объявление: имя стоит там в <c>nameof(...)</c> и в
    ///   документации. Забудь исключить — и «осиротевшая» строка навсегда выглядит живой,
    ///   потому что сама себя упоминает.
    /// * тесты — строку, которую использует ТОЛЬКО проверка, в продукте никто не показывает:
    ///   это ровно та же мёртвая строка, просто с зелёной проверкой рядом.
    /// </remarks>
    public static bool CountsAsCallSite(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/');
        if (normalized.Length == 0) return false;

        if (string.Equals(Path.GetFileName(normalized), "PanelStrings.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = normalized.Split('/');

        return !segments.Any(segment => string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Склейка текста только тех файлов, которые считаются местом вызова.</summary>
    public static string ProductSourceText(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = new StringBuilder();

        foreach (var file in files)
        {
            if (!CountsAsCallSite(file.Path)) continue;

            builder.AppendLine(file.Text);
        }

        return builder.ToString();
    }

    // ---- ядро: текст в разметке ------------------------------------------------------------

    private static readonly Regex MarkupComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Значение атрибута, а не любое слово «Text» в файле: за именем обязан идти знак «=».
    // Слева запрещены буква, цифра, «_», «.», «:» и «-» — иначе `TextWrapping=` или
    // `x:Name="NoText"` считались бы текстом.
    private static readonly Regex DoubleQuotedAttribute = new(
        "(?<![A-Za-z0-9_.:-])(?:Text|Content|Title|Watermark|Header)\\s*=\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant);

    private static readonly Regex SingleQuotedAttribute = new(
        "(?<![A-Za-z0-9_.:-])(?:Text|Content|Title|Watermark|Header)\\s*=\\s*'(?<value>[^']*)'",
        RegexOptions.CultureInvariant);

    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Кириллица в значениях подписывающих атрибутов разметки. Комментарии разметки не считаются:
    /// их не трогаем и не запрещаем — они не показываются человеку.
    /// </summary>
    public static IReadOnlyList<string> TextsInMarkup(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var found = new List<string>();

        foreach (var file in files)
        {
            var text = file.Text ?? string.Empty;
            var withoutComments = WithoutComments(text);

            foreach (var attribute in new[] { DoubleQuotedAttribute, SingleQuotedAttribute })
            {
                foreach (Match match in attribute.Matches(withoutComments))
                {
                    if (!Cyrillic.IsMatch(match.Groups["value"].Value)) continue;

                    found.Add($"{file.Path}:{Line(text, match.Index)}: {Collapse(match.Value)}");
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Комментарий заменяется ПРОБЕЛАМИ той же длины (переводы строк остаются на месте):
    /// смещения и номера строк не съезжают, а <c>&lt;!-- … --&gt;</c> перестаёт быть текстом.
    /// </summary>
    public static string WithoutComments(string text) =>
        MarkupComment.Replace(
            text ?? string.Empty,
            match => new string(match.Value.Select(character => character == '\n' ? '\n' : ' ').ToArray()));

    private static int Line(string text, int index)
    {
        var line = 1;

        for (var position = 0; position < index && position < text.Length; position++)
        {
            if (text[position] == '\n') line++;
        }

        return line;
    }

    private static string Collapse(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Первые <paramref name="first"/> имён и «и ещё N» — иначе падение читается полотном.</summary>
    public static string NameList(IEnumerable<string> names, int first = 20)
    {
        var list = names.ToList();
        var head = string.Join(", ", list.Take(first));
        var tail = list.Count > first ? $", и ещё {list.Count - first}" : string.Empty;

        return $"{list.Count}: {head}{tail}";
    }

    // ---- проверка 1: каждая строка панели где-то используется -------------------------------

    [Fact]
    public void Каждая_строка_панели_где_то_используется()
    {
        var root = RequireRepositoryRoot();
        var sources = ProductSourceText(ReadProductSources(root));

        var members = PanelStringMembers();
        Assert.True(members.Count > 200, $"членов PanelStrings: {members.Count} — отражение вернуло не тот тип");

        var unused = UnusedMembers(members, sources);

        Assert.True(
            unused.Count == 0,
            "мёртвые строки PanelStrings — объявлены и переведены, но в панели никем не вызываются. " +
            $"Имена ({NameList(unused)}). " +
            "Проверка только называет их: удалять строку или заводить ей вызов — решение дирижёра, " +
            "и принимать его молча эта проверка не даёт.");
    }

    private static List<string> PanelStringMembers() =>
        typeof(PanelStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    // ---- проверка 2: в разметке нет текста --------------------------------------------------

    [Fact]
    public void В_разметке_нет_текста()
    {
        var root = RequireRepositoryRoot();

        var markup = ReadProductSources(root)
            .Where(file => file.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(markup.Count > 0, "разметки панели не нашлось вовсе — похоже, прочитан не тот каталог");

        var found = TextsInMarkup(markup);

        Assert.True(
            found.Count == 0,
            "правило проекта «ни одного текста в разметке»: подписи ставятся кодом из PanelStrings, " +
            "а тот берёт их из словаря. Литерал в .axaml остаётся русским на английском и китайском " +
            "кадре. Нашлось:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    // ---- корень репозитория и чтение исходников ---------------------------------------------

    /// <summary>
    /// Корень репозитория — вверх от каталога сборки по файлу <c>DshPanel.slnx</c>.
    /// Пусто значит «не нашли»: это не тихий пропуск, а падение — решает вызывающий.
    /// </summary>
    public static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DshPanel.slnx"))) return directory.FullName;
        }

        return string.Empty;
    }

    /// <summary>
    /// Не нашли корень — ПАДАЕМ ГРОМКО. Проверка, которая молча пропускается, ничего не стоит:
    /// именно так неполный словарь притворялся сошедшимся (см. <c>LocalizationTests</c>).
    /// </summary>
    private static string RequireRepositoryRoot()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
        {
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");
        }

        return root;
    }

    /// <summary>Исходники панели: <c>src\DshPanel\**\*.cs</c> и <c>*.axaml</c>.</summary>
    private static List<SourceFile> ReadProductSources(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            Assert.Fail("корень репозитория пуст — эта проверка обязана падать, а не пропускаться");
        }

        var directory = Path.Combine(root, "src", "DshPanel");

        if (!Directory.Exists(directory))
        {
            Assert.Fail($"нет каталога исходников панели: «{directory}» (корень репозитория: «{root}»)");
        }

        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            // Сборка и промежуточные файлы не считаются: имя, живущее только в сгенерированном
            // коде, — такая же мёртвая строка, а не вызов.
            .Where(path => !IsGenerated(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new SourceFile(Path.GetRelativePath(root, path), File.ReadAllText(path)))
            .ToList();

        Assert.True(files.Count > 50, $"исходников панели прочитано: {files.Count} — похоже, каталог не тот");

        return files;
    }

    private static bool IsGenerated(string path) =>
        path.Replace('\\', '/')
            .Split('/')
            .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                         || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    // ---- проверяемость самой проверки (подставные данные, а не порча дерева) ----------------

    [Fact]
    public void Ядро_находит_ровно_неиспользованные_имена()
    {
        const string sources =
            "var text = PanelStrings.ServerRunning;\n" +
            "var other = nameof(PanelStrings.StartButton);\n" +
            "var list = new[] { PanelStrings.FoundServerFormat };\n";

        var unused = UnusedMembers(new[] { "ServerRunning", "StartButton", "FoundServerFormat", "НикемНеЗванная" }, sources);

        Assert.Equal(new[] { "НикемНеЗванная" }, unused);
    }

    [Fact]
    public void Имя_ищется_словом_а_не_подстрокой()
    {
        // Живая строка рядом отличается окончанием: подстрочный поиск объявил бы мёртвую строку живой.
        var unused = UnusedMembers(new[] { "BackupListLabel" }, "PanelStrings.BackupListLabelFormat");

        Assert.Equal(new[] { "BackupListLabel" }, unused);
    }

    [Fact]
    public void Объявление_в_PanelStrings_не_считается_вызовом()
    {
        var files = new[]
        {
            new SourceFile(@"src\DshPanel\Shell\PanelStrings.cs", "public static string Dead => Loc.T(nameof(Dead));"),
            new SourceFile(@"src\DshPanel\Views\MainWindow.axaml.cs", "label.Text = PanelStrings.Alive;"),
        };

        var unused = UnusedMembers(new[] { "Alive", "Dead" }, ProductSourceText(files));

        Assert.Equal(new[] { "Dead" }, unused);
    }

    [Fact]
    public void Использование_только_в_тестах_не_считается_вызовом()
    {
        var files = new[]
        {
            new SourceFile(@"src\DshPanel\Views\MainWindow.axaml.cs", "label.Text = PanelStrings.Alive;"),
            new SourceFile(@"tests\DshPanel.Tests\WhateverTests.cs", "Assert.Equal(..., PanelStrings.OnlyInTest);"),
        };

        var unused = UnusedMembers(new[] { "Alive", "OnlyInTest" }, ProductSourceText(files));

        Assert.Equal(new[] { "OnlyInTest" }, unused);
    }

    [Fact]
    public void Ядро_разметки_видит_текст_и_не_считает_комментарий()
    {
        var files = new[]
        {
            new SourceFile(@"Views\Bad.axaml", "<TextBlock Text=\"Привет\" />"),
            new SourceFile(
                @"Views\Fine.axaml",
                "<!-- Text=\"Привет\" — это комментарий, его не трогаем -->\n" +
                "<TextBlock x:Name=\"NoText\" TextWrapping=\"Wrap\" Text=\"{Binding Name}\" Content=\"\" />"),
        };

        var found = TextsInMarkup(files);

        var single = Assert.Single(found);
        Assert.Contains("Bad.axaml", single, StringComparison.Ordinal);
        Assert.Contains("Привет", single, StringComparison.Ordinal);
    }

    [Fact]
    public void Ядро_разметки_называет_строку_файла()
    {
        var files = new[]
        {
            new SourceFile(@"Views\Bad.axaml", "<Window>\n  <Button Content=\"Остановить\"/>\n</Window>"),
        };

        var single = Assert.Single(TextsInMarkup(files));

        Assert.Contains("Bad.axaml:2", single, StringComparison.Ordinal);
        Assert.Contains("Остановить", single, StringComparison.Ordinal);
    }

    [Fact]
    public void Номера_имён_в_отчёте_не_теряются()
    {
        var names = Enumerable.Range(1, 25).Select(index => "Name" + index).ToArray();

        var report = NameList(names);

        Assert.StartsWith("25: Name1", report, StringComparison.Ordinal);
        Assert.Contains("Name20", report, StringComparison.Ordinal);
        Assert.Contains("и ещё 5", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Name21", report, StringComparison.Ordinal);
    }
}
