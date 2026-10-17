using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ВОРОТА НА КЛАСС: НАСТОЯЩИЙ ПУТЬ, ИЗ КОТОРОГО СТРОИТСЯ ОЖИДАНИЕ ДЛЯ ПОКАЗА, ОБЯЗАН БЫТЬ ПОД МАСКОЙ.
///
/// **От какого случая (находка приёмки пачки A, F-1).** Проверка `AutostartTests.Удачная_попытка_…`
/// собирала ожидание подписи из `Environment.ProcessPath` **без** `DisplayMask`, а продукт эту
/// подпись маскирует. В рабочем дереве (`C:\Harness\…` — вне профиля пользователя) маска ничего
/// не меняет, и набор был зелёным; в чистой копии `%TEMP%` (путь ПОД профилем) та же проверка
/// падала ВСЕГДА. То есть проверка мерила место, где лежит дерево, а не то, что видит человек.
/// Второй такой случай за цикл (первый — `WordingRulesTests.движк` в A5/F-2) пришёл бы так же тихо,
/// поэтому здесь правило, а не разовая правка.
///
/// **Два правила, и оба проверяются по исходникам проверок:**
/// 1. каждое вхождение `Environment.ProcessPath` / `AppContext.BaseDirectory` в КОДЕ (не в
///    пояснении) обязано быть либо под маской (`DisplayMask.Path`), либо помечено прямо в строке
///    обоснованием <c>// КОРЕНЬ: …</c> — «это путь к файлу для чтения/поиска, в текст человеку он
///    не попадает»; поиск корня репозитория (`FindRepositoryRoot(AppContext.BaseDirectory)`,
///    `new DirectoryInfo(AppContext.BaseDirectory)`) — законный служебный случай и обоснования
///    не требует;
/// 2. если такое значение попало в ЧЕЛОВЕЧЕСКИЙ формат (`string.Format` со строкой словаря
///    `PanelStrings.*` или `Loc.T(...)`), оно обязано быть в нём под маской.
///
/// **Мутации, которые обязаны упасть здесь:** «снять маску в ожидании подписи автозапуска» ·
/// «взять настоящий путь для показа в новом месте без обоснования».
///
/// ⚠️ Файл самой этой проверки из обхода исключён: в нём имена ищутся как СТРОКИ
/// (`codeLine.Contains("…")`), и он честно поймал бы сам себя. Тот же приём и по той же причине,
/// что в `TreeDeletionTests.Удаление_дерева_живёт_одним_местом` (там из обхода исключён помощник).
/// </summary>
public class TestPathMaskTests
{
    /// <summary>Пометка-обоснование: «настоящий путь здесь не для показа».</summary>
    public const string Justification = "// КОРЕНЬ:";

    /// <summary>Объявление локальной переменной, в которую попал настоящий путь машины.</summary>
    private static readonly Regex PathLocal = new(
        @"\bvar\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=[^;]*(?:Environment\.ProcessPath|AppContext\.BaseDirectory)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Один вызов <c>string.Format(…)</c> целиком — до конца оператора.</summary>
    private static readonly Regex FormatStatement = new(
        @"string\.Format\([^;]*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void Пути_для_показа_в_проверках_под_маской()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var folder = Path.Combine(root, "tests", "DshPanel.Tests");
        var self = Path.Combine(folder, nameof(TestPathMaskTests) + ".cs");

        var files = Directory
            .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Сторож ослепшего правила: пустой список файлов не «правило соблюдено», а «ничего не прочитано».
        Assert.True(files.Count > 100, $"прочитано файлов проверок: {files.Count} — это не дерево проверок");

        var naked = new List<string>();
        var displayed = new List<string>();

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var raw = File.ReadAllText(path);

            // Пояснения выкидываются тем же членом, что у остальных ворот по исходникам: рассказать
            // о правиле рядом с кодом не значит нарушить его. Строки при этом не съезжают.
            var code = UiThreadTests.WithoutComments(raw);
            var rawLines = raw.Split('\n');
            var codeLines = code.Split('\n');

            var pathLocals = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < codeLines.Length && index < rawLines.Length; index++)
            {
                var codeLine = codeLines[index];
                var rawLine = rawLines[index];

                var hasProcessPath = codeLine.Contains("Environment.ProcessPath", StringComparison.Ordinal);
                var hasBaseDirectory = codeLine.Contains("AppContext.BaseDirectory", StringComparison.Ordinal);

                if (!hasProcessPath && !hasBaseDirectory) continue;

                var local = PathLocal.Match(codeLine);
                if (local.Success) pathLocals.Add(local.Groups["name"].Value);

                // ДИАГНОСТИКА — НЕ КЛАСС. Строка провала (`Assert.Fail($"…{путь}…")`) печатается
                // только тогда, когда проверка УЖЕ упала, и человеку-пользователю не показывается;
                // от переезда дерева она ничего не ломает. Класс — это ожидание для показа.
                if (codeLine.Contains("Assert.Fail(", StringComparison.Ordinal)) continue;

                // ОБОСНОВАНИЕ — НА СТРОКЕ ИЛИ СТРОКОЙ ВЫШЕ: писать длинное объяснение в хвост кода
                // значит уродовать код; комментарий перед строкой читается так же однозначно.
                var justified = rawLine.Contains(Justification, StringComparison.Ordinal)
                                || (index > 0 && rawLines[index - 1].Contains(Justification, StringComparison.Ordinal));

                var allowed =
                    codeLine.Contains("DisplayMask.Path(", StringComparison.Ordinal)
                    || justified
                    || codeLine.Contains("FindRepositoryRoot(AppContext.BaseDirectory)", StringComparison.Ordinal)
                    || codeLine.Contains("new DirectoryInfo(AppContext.BaseDirectory)", StringComparison.Ordinal);

                if (!allowed) naked.Add($"{name}:{index + 1}: {rawLine.Trim()}");
            }

            foreach (Match statement in FormatStatement.Matches(code))
            {
                var text = statement.Value;

                // Человеческий формат — тот, чья строка пришла из словаря. Отчётные литералы сюда
                // не входят: у них нет ни ключа, ни нашей фразы рядом.
                if (!text.Contains("PanelStrings.", StringComparison.Ordinal)
                    && !text.Contains("Loc.T(", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var local in pathLocals)
                {
                    var used = Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(local)}(?!\w)", RegexOptions.CultureInvariant);

                    if (!used) continue;
                    if (text.Contains($"DisplayMask.Path({local})", StringComparison.Ordinal)) continue;

                    displayed.Add(
                        $"{name}: «{local}» в человеческом формате без маски — "
                        + Regex.Replace(text, @"\s+", " ").Trim());
                }
            }
        }

        Assert.True(
            naked.Count == 0,
            "настоящий путь машины взят в проверке без маски и без обоснования — такой случай ломается "
            + "при смене места дерева (в чистой копии `%TEMP%` он падает):\n"
            + string.Join("\n", naked)
            + $"\n\nГодится: `DisplayMask.Path(…)` или пометка `{Justification} <что это и почему без маски>`.");

        Assert.True(
            displayed.Count == 0,
            "ожидание для ЧЕЛОВЕКА строится из настоящего пути без маски — в дереве вне профиля это "
            + "невидимо, а в чистой копии `%TEMP%` проверка падает:\n"
            + string.Join("\n", displayed));
    }
}
