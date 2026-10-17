using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// «НИ ОДНОГО ЛИШНЕГО ОКНА»: каждый запуск чужого процесса в панели обязан прятать окно.
///
/// **Откуда правило.** Панель запускает чужие программы сама: 7-Zip (копия), node/npm/pnpm
/// (проба версий), движок, сценарий замены файлов, гасильщик процессов. Каждый такой запуск —
/// это чёрный прямоугольник поверх работы человека, если окно не спрятать; а прогон проверки
/// на машине владельца мигал бы чужими окнами прямо у него на экране.
///
/// **Две законные формы, и обе явные:** либо `CreateNoWindow = true` (окно не показывается),
/// либо `UseShellExecute = true` — ОСОЗНАННЫЙ запуск **браузера**: браузер человеку и нужен,
/// и окно там — не помеха, а цель. Всё остальное — нарушение, и проверка ниже читает ИСХОДНИКИ
/// (`src\DshPanel\**\*.cs`), а не верит слову: ровно так же устроены ворота `UiThreadTests`.
///
/// Замер 10.10.2026 (разведка этапа B): таких запусков в панели **десять** — восемь с
/// `CreateNoWindow` (7-Zip, самотест копий, самотесты `Program`, накат, проба версий, гасильщик,
/// запуск движка, запуск сценария замены) и два с `UseShellExecute` (браузер: `AgentBrowser`
/// и `ProductLinks`).
/// </summary>
public class WindowlessTests
{
    /// <summary>
    /// Запуск процесса — по этим словам и ищем.
    ///
    /// **Настройка запуска** (`new ProcessStartInfo…`) — это и есть место, где окно прячут или нет:
    /// правило (1) требует признака от КАЖДОЙ такой настройки.
    ///
    /// **Строковая перегрузка** (`Process.Start("cmd.exe")`) — правило (2): настроек не строит,
    /// спрятать окно нечем, и окно мелькнёт. Запуск через ПЕРЕМЕННУЮ с настройками
    /// (`Process.Start(start)`) нарушением не считается: настройку судит правило (1), а не вызов.
    /// </summary>
    private static readonly string[] BuildWords = { @"new\s+(System\.Diagnostics\.)?ProcessStartInfo" };

    /// <summary>Строковая перегрузка запуска: первый довод — строка в кавычках.</summary>
    private const string ShellStartPattern = @"Process\.Start\(\s*(\$|@)?""";

    /// <summary>Законные признаки: окно спрятано ИЛИ это осознанный запуск браузера.</summary>
    private static readonly string[] Markers =
    {
        @"CreateNoWindow\s*=\s*true",
        @"UseShellExecute\s*=\s*true",
    };

    /// <summary>
    /// ОПЕРАТОР, в котором стоит запуск: от слова запуска до `;` верхнего уровня.
    ///
    /// ⚠️ Почему не «просто окно в 400 знаков»: тогда признак соседнего оператора спасал бы
    /// нарушителя, и проверка зеленела бы там, где окно мелькает. Поэтому идём по знакам и следим
    /// за кавычками и скобками — `;` внутри строки (`Arguments = "/c a;b"`) оператора не кончает.
    /// </summary>
    public static string StatementAt(string code, int from)
    {
        ArgumentNullException.ThrowIfNull(code);

        var depth = 0;
        var inString = false;
        var verbatim = false;

        for (var i = from; i < code.Length; i++)
        {
            var c = code[i];

            if (inString)
            {
                if (verbatim)
                {
                    // В дословной строке кавычка удваивается, а обратная косая — не экранирует.
                    if (c == '"' && i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"') { inString = true; verbatim = true; i++; continue; }
            if (c == '"') { inString = true; continue; }

            if (c is '(' or '{' or '[') depth++;
            if (c is ')' or '}' or ']') depth--;
            if (c == ';' && depth <= 0) return code[from..(i + 1)];
        }

        return code[from..];
    }

    /// <summary>
    /// Файлы, в КОДЕ которых есть запуск процесса БЕЗ признака «окно спрятано». Пусто — правило
    /// соблюдено. Пояснения выкидываются (<see cref="UiThreadTests.WithoutComments"/>): рассказать
    /// о правиле рядом с запуском не значит нарушить его.
    /// </summary>
    public static IReadOnlyList<string> NakedStarts(IEnumerable<SourceStringsTests.SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var found = new List<string>();

        foreach (var file in files)
        {
            var code = UiThreadTests.WithoutComments(file.Text);

            // (1) Каждая построенная настройка запуска обязана нести признак.
            foreach (var word in BuildWords)
            {
                foreach (Match match in Regex.Matches(code, word))
                {
                    var statement = StatementAt(code, match.Index);

                    if (Markers.Any(marker => Regex.IsMatch(statement, marker)))
                        continue;

                    if (!found.Contains(file.Path)) found.Add(file.Path);
                }
            }

            // (2) Строковая перегрузка `Process.Start("…")` настроек не строит — спрятать окно нечем.
            foreach (Match match in Regex.Matches(code, ShellStartPattern))
            {
                _ = match;
                if (!found.Contains(file.Path)) found.Add(file.Path);
            }
        }

        return found;
    }

    // ---- ядро проверяется прогоном, который умеет падать ------------------------------------

    /// <summary>
    /// ЯДРО ВИДИТ ГОЛЫЙ ЗАПУСК И ПРОПУСКАЕТ ЗАКОННЫЕ — обе половины правила, на подложенных
    /// исходниках. Без второй половины проверка ругалась бы на КАЖДЫЙ запуск, включая браузер.
    /// </summary>
    [Fact]
    public void Ядро_видит_голый_запуск_и_пропускает_спрятанный()
    {
        var files = new[]
        {
            // Голый запуск: окно покажется.
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Bad\One.cs",
                "var p = Process.Start(new ProcessStartInfo(\"cmd.exe\") { Arguments = \"/c dir\" });"),

            // Спрятанный — законен.
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Good\Two.cs",
                "var p = Process.Start(new ProcessStartInfo(\"cmd.exe\") { UseShellExecute = false, CreateNoWindow = true });"),

            // Браузер — законен: окно тут цель, а не помеха.
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Good\Three.cs",
                "using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });"),

            // В рассказе о правиле запуска нет вовсе.
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Good\Four.cs",
                "// здесь был Process.Start без прятания окна — и это только рассказ\nvar x = 1;"),
        };

        var found = NakedStarts(files);

        var only = Assert.Single(found);
        Assert.Contains(@"Bad\One.cs", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОПЕРАТОР ОБРЕЗАЕТСЯ ПО СВОЕЙ ТОЧКЕ С ЗАПЯТОЙ — и это проверяется ПРЯМО, а не через
    /// сценарий с соседом.
    ///
    /// ⚠️ Зачем отдельная проверка. Мутация «оператор не обрезается» (разбор идёт до конца файла)
    /// на сценарии с соседом **ВЫЖИЛА**: маркер, стоящий ПОСЛЕ нарушителя, всё равно попадал в его
    /// «оператор», потому что хвост тянется вперёд. Проверка была слабее своего названия — нашла
    /// мутация, а не чтение (разбор — `HISTORY.md`, 10.10.2026, пункт (в)).
    /// </summary>
    [Fact]
    public void Оператор_обрезается_по_своей_точке_с_запятой()
    {
        const string code =
            "var b = Process.Start(new ProcessStartInfo(\"two.exe\") { Arguments = \"--x\" });\n" +
            "var a = 1;";

        var statement = StatementAt(code, code.IndexOf("ProcessStartInfo", StringComparison.Ordinal));

        Assert.Contains("--x", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("var a = 1", statement, StringComparison.Ordinal);
        Assert.EndsWith(";", statement.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРИЗНАК СОСЕДНЕГО ОПЕРАТОРА НЕ СПАСАЕТ: нарушитель назван, даже когда рядом (ПОСЛЕ него)
    /// стоит законный запуск с признаком.
    /// </summary>
    [Fact]
    public void Признак_соседнего_оператора_не_спасает()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Bad\Neighbour.cs",
                "var b = Process.Start(new ProcessStartInfo(\"two.exe\") { Arguments = \"--x\" });\n" +
                "var a = Process.Start(new ProcessStartInfo(\"one.exe\") { CreateNoWindow = true });"),
        };

        var only = Assert.Single(NakedStarts(files));
        Assert.Contains("Neighbour.cs", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// ТОЧКА С ЗАПЯТОЙ ВНУТРИ СТРОКИ оператора не кончает: `Arguments = "/c a;b"` — это ещё тот же
    /// запуск, и признак, стоящий после такой строки, обязан быть виден.
    /// </summary>
    [Fact]
    public void Точка_с_запятой_внутри_строки_оператора_не_кончает()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Good\Semicolon.cs",
                "var p = Process.Start(new ProcessStartInfo(\"cmd.exe\")\n{\n    Arguments = \"/c echo a;b\",\n    UseShellExecute = false,\n    CreateNoWindow = true,\n});"),
        };

        Assert.Empty(NakedStarts(files));
    }

    // ---- ворота по исходникам ---------------------------------------------------------------

    /// <summary>
    /// В ПАНЕЛИ НЕТ НИ ОДНОГО ЗАПУСКА ЧУЖОГО ПРОЦЕССА БЕЗ ПРЯТАНИЯ ОКНА. Проверка читает
    /// `src\DshPanel\**\*.cs` и падает, называя файлы.
    /// </summary>
    [Fact]
    public void Запуск_чужого_процесса_всегда_прячет_окно()
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

        var found = NakedStarts(files);

        Assert.True(
            found.Count == 0,
            "запуск процесса без прятания окна (нужно CreateNoWindow = true, а для браузера — " +
            "осознанное UseShellExecute = true):\n" + string.Join("\n", found));
    }

    // ---- дверь сообщений окон не показывает -------------------------------------------------

    /// <summary>ПОКАЗ ОКНА — по этим словам и ищем. `ShowNotification` сюда не попадает:
    /// у него другой хвост имени, и шарик — законный способ сказать человеку о новом выпуске.</summary>
    private static readonly string[] WindowShowing =
    {
        @"\.Show\s*\(",
        @"ShowDialog\s*\(",
        @"new\s+\w*Window\b",
        @"\.Activate\s*\(",
    };

    /// <summary>
    /// ТЕЛО МЕТОДА ПО ЕГО ОБЪЯВЛЕНИЮ: от первого `{` после объявления до парной `}`. Скобки внутри
    /// строк и пояснений не считаются — иначе тело кончалось бы на первой же строке с `"{"`.
    /// </summary>
    public static string BodyAt(string code, string signature)
    {
        ArgumentNullException.ThrowIfNull(code);

        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var open = code.IndexOf('{', start);
        if (open < 0) return string.Empty;

        var depth = 0;
        var inString = false;
        var verbatim = false;
        var character = false;

        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];

            if (inString)
            {
                if (verbatim)
                {
                    if (c == '"' && i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (character)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') character = false;
                    continue;
                }

                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"') { inString = true; verbatim = true; i++; continue; }
            if (c == '$' && i + 1 < code.Length && code[i + 1] == '"') { inString = true; i++; continue; }
            if (c == '"') { inString = true; continue; }

            if (c == '{') depth++;
            if (c == '}')
            {
                depth--;
                if (depth == 0) return code[(open + 1)..i];
            }
        }

        return code[(open + 1)..];
    }

    /// <summary>Показы окон, найденные в теле, — САМИМ ТЕКСТОМ (`_window.Show()`), чтобы падение
    /// называло, что именно показывают. Пусто — дверь окон не показывает.</summary>
    public static IReadOnlyList<string> WindowCalls(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var found = new List<string>();

        foreach (var pattern in WindowShowing)
        {
            foreach (Match match in Regex.Matches(body, pattern, RegexOptions.CultureInvariant))
                found.Add(match.Value);
        }

        return found;
    }

    /// <summary>
    /// ЯДРО ВИДИТ ПОКАЗ ОКНА И ПРОПУСКАЕТ ШАРИК — обе половины правила, на подложенных телах.
    /// Без второй половины проверка ругалась бы на `ShowNotification`, то есть на сам шарик.
    /// </summary>
    [Fact]
    public void Ядро_двери_видит_показ_окна_и_пропускает_шарик()
    {
        const string showing =
            "private void Notify()\n{\n    _tray?.ShowNotification(\"a\", \"b\");\n" +
            "    _window.Show();\n}";

        const string clean =
            "private void Notify()\n{\n    _tray?.ShowNotification(\"a\", \"b\");\n}";

        Assert.Contains(".Show(", Assert.Single(WindowCalls(BodyAt(showing, "void Notify("))), StringComparison.Ordinal);
        Assert.Empty(WindowCalls(BodyAt(clean, "void Notify(")));
        // Тело берётся ЦЕЛИКОМ, а не до первой скобки: иначе показ окна во второй строке
        // остался бы невидимым.
        Assert.Contains("_window.Show()", BodyAt(showing, "void Notify("), StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВЕРЬ АВТОМАТИЧЕСКИХ СООБЩЕНИЙ ОКОН НЕ ПОКАЗЫВАЕТ (хвост этапа C, прицеплен к этапу D).
    ///
    /// Зачем отдельная проверка при живых соседях. Правило панели — «автоматическое сообщение
    /// показывается шариком и только им»; шарик же и есть то, чего человек не хочет видеть окном
    /// поверх своей работы. Проверка читает НАСТОЯЩУЮ дверь (`App.Notify`) и держит обе половины:
    /// окон она не показывает, а шарик и решение двери изоляции в ней на месте — иначе «дверь
    /// молчит» выглядело бы зелёным.
    /// </summary>
    [Fact]
    public void Дверь_сообщений_окон_не_показывает()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var file = Path.Combine(root, "src", "DshPanel", "App.axaml.cs");

        if (!File.Exists(file))
            Assert.Fail($"нет файла с дверью сообщений: «{file}»");

        var body = BodyAt(UiThreadTests.WithoutComments(File.ReadAllText(file)), "void Notify(");

        Assert.False(string.IsNullOrWhiteSpace(body), "тело двери `Notify` не найдено — проверять нечего");

        var calls = WindowCalls(body);

        Assert.True(
            calls.Count == 0,
            "дверь автоматических сообщений показывает окно: " + string.Join(", ", calls));

        Assert.Contains("ShowNotification", body, StringComparison.Ordinal);
        Assert.Contains("IsolationRules.ShouldNotify", body, StringComparison.Ordinal);
    }
}
