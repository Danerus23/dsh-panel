using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// НИТКА ИНТЕРФЕЙСА — ОДНА ДВЕРЬ (`Shell\UiThread`), и это не украшение, а лечение флейка.
///
/// **Что было.** 09.10.2026 полный набор проверок падал не всегда и не одной проверкой: то
/// `SettingsUnsavedTests`, то `SettingsTests`, в CI — `SettingsUnsavedTests` (прогон `37972801441`,
/// попытка 1). Падала УБОРКА чужой проверки, стек — `HeadlessUnitTestSession.EnsureIsolatedApplication`
/// → `AvaloniaHeadlessPlatform.Initialize` → `new Compositor` → `DefaultRenderLoop.Add` →
/// `Dispatcher.VerifyAccess` → «The calling thread cannot access this object because a different
/// thread owns it». То есть падала не проверка, а оснастка вида.
///
/// **Почему.** У `Dispatcher.UIThread` в Avalonia есть свойство, о котором легко не знать:
/// диспетчер СОЗДАЁТСЯ тем, кто спросил первым, если его в процессе ещё нет
/// (`UIThread => s_uiThread ?? CurrentDispatcher`, а конструктор диспетчера делает
/// `s_uiThread ??= this`). Оснастка проверок вида сбрасывает состояние диспетчера между
/// проверками; в этот промежуток фоновая нитка панели (ответ фоновой задачи, таймер контроллера)
/// спрашивала `Dispatcher.UIThread` — и объявляла ниткой интерфейса СЕБЯ.
///
/// **Замер, а не догадка (09.10.2026, эта машина):**
/// * полный набор падал 1 раз из 5 прогонов — и каждый раз другой проверкой, что и говорит
///   «дело в оснастке»;
/// * проба «`new DispatcherTimer { … }` с нитки без диспетчера»: диспетчер до — нет, после — ЕСТЬ;
/// * проба «`Dispatcher.UIThread.Post(…)` с нитки без диспетчера»: то же самое.
///
/// **Лечение.** Все обращения панели к нитке интерфейса и все её таймеры идут через дверь
/// <see cref="UiThread"/>, а дверь диспетчер НЕ создаёт — берёт только тот, что уже есть у нитки,
/// и помнит тот, что панель узнала на своём интерфейсе. Нужное двери добавляется в неё, а не
/// на месте: это первая проверка ниже, и она читает ИСХОДНИКИ.
/// </summary>
public class UiThreadTests
{
    // ---- ядро: что считается прямым обращением к нитке интерфейса (чистая функция) -----------

    /// <summary>
    /// Строки, которыми панель имеет право спрашивать нитку интерфейса ТОЛЬКО в двери.
    /// <c>Dispatcher.UIThread</c> — сам вопрос; <c>new DispatcherTimer</c> — вопрос через
    /// конструктор: таймер берёт тот же диспетчер и создаёт его ровно так же.
    /// </summary>
    private static readonly string[] Forbidden =
    {
        @"Dispatcher\s*\.\s*UIThread",
        @"new\s+DispatcherTimer",
    };

    /// <summary>
    /// ВЫКИНУТЬ ПОЯСНЕНИЯ: ищем в КОДЕ, а не в рассказе о нём. Правило проекта — объяснять
    /// случай рядом с кодом; запрет на прямое обращение при этом не должен запрещать о нём
    /// написать (в двери и в её пояснениях эти слова встречаются намеренно).
    ///
    /// ⚠️ Строку с «://» не режем: там `//` — часть адреса, и обрезав её, проверка потеряла бы
    /// настоящий код в той же строке.
    /// </summary>
    public static string WithoutComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var noBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        var lines = noBlocks
            .Split('\n')
            .Select(line => line.Contains("://", StringComparison.Ordinal) ? line : StripLineComment(line));

        return string.Join('\n', lines);
    }

    private static string StripLineComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    /// <summary>
    /// Файлы, в КОДЕ которых есть прямое обращение к нитке интерфейса. Пусто — правило соблюдено.
    /// <paramref name="door"/> — путь двери: ей одной это и позволено.
    /// </summary>
    public static IReadOnlyList<string> DirectUses(IEnumerable<SourceStringsTests.SourceFile> files, string door)
    {
        ArgumentNullException.ThrowIfNull(files);

        var found = new List<string>();

        foreach (var file in files)
        {
            if (file.Path.EndsWith(door, StringComparison.OrdinalIgnoreCase)) continue;

            var code = WithoutComments(file.Text);

            if (Forbidden.Any(pattern => Regex.IsMatch(code, pattern, RegexOptions.CultureInvariant)))
                found.Add(file.Path);
        }

        return found;
    }

    // ---- ядро проверяется прогоном, который умеет падать ------------------------------------

    /// <summary>
    /// Ядро видит прямое обращение и НЕ считает нарушением дверь — обе половины правила.
    /// </summary>
    [Fact]
    public void Ядро_видит_прямое_обращение_и_пропускает_дверь()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Views\MainWindow.axaml.cs",
                "Dispatcher.UIThread.Post(() => Render());"),
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Balance\BalanceController.cs",
                "var timer = new DispatcherTimer { Interval = Tick };"),
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Shell\UiThread.cs",
                "var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);"),
            new SourceStringsTests.SourceFile(@"C:\repo\src\DshPanel\Views\Clean.axaml.cs",
                "// здесь был Dispatcher.UIThread.Post — и это только рассказ\nUiThread.Post(Render);"),
        };

        var found = DirectUses(files, @"Shell\UiThread.cs");

        Assert.Equal(2, found.Count);
        Assert.Contains(@"Views\MainWindow.axaml.cs", found[0], StringComparison.Ordinal);
        Assert.Contains(@"Balance\BalanceController.cs", found[1], StringComparison.Ordinal);
    }

    // ---- ворота по исходникам ---------------------------------------------------------------

    /// <summary>
    /// В ПАНЕЛИ НЕТ НИ ОДНОГО ДРУГОГО ОБРАЩЕНИЯ К НИТКЕ ИНТЕРФЕЙСА. Проверка читает исходники
    /// `src\DshPanel\**\*.cs` (пояснения выкидываются) и падает, называя файлы.
    /// </summary>
    [Fact]
    public void К_нитке_интерфейса_обращается_только_дверь()
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

        var found = DirectUses(files, Path.Combine("Shell", "UiThread.cs"));

        Assert.True(
            found.Count == 0,
            "к нитке интерфейса обращаются напрямую (нужное двери — в Shell\\UiThread.cs):\n" +
            string.Join("\n", found));
    }

    // ---- сама дверь ------------------------------------------------------------------------

    /// <summary>
    /// БЕЗ НИТКИ ИНТЕРФЕЙСА ДВЕРЬ НИЧЕГО НЕ ДЕЛАЕТ И НИЧЕГО НЕ СОЗДАЁТ. Это и есть лечение флейка,
    /// проверенное делом: нитка xunit диспетчера не имеет, и после всех обращений к двери
    /// он у неё НЕ ПОЯВЛЯЕТСЯ. На прежнем коде (прямое `Dispatcher.UIThread`) эта проверка падает —
    /// доказано подменой.
    /// </summary>
    [Fact]
    public void Без_нитки_интерфейса_дверь_ничего_не_создаёт()
    {
        UiThread.ForgetForTests();

        Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));

        Assert.False(UiThread.CheckAccess());
        Assert.False(UiThread.Post(() => { }));
        Assert.Null(UiThread.NewTimer(TimeSpan.FromSeconds(1)));

        UiThread.RunJobs();

        Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));
    }

    /// <summary>
    /// НА СВОЕЙ НИТКЕ ДВЕРЬ РАБОТАЕТ: помнит её, доносит работу до очереди и заводит часы.
    /// Без этой половины «ничего не создаёт» зеленело бы и у двери, которая не делает ничего.
    /// </summary>
    [AvaloniaFact]
    public void На_нитке_интерфейса_дверь_помнит_её_и_доносит_работу()
    {
        UiThread.Remember();

        Assert.True(UiThread.CheckAccess());

        var done = false;

        Assert.True(UiThread.Post(() => done = true));

        UiThread.RunJobs();

        Assert.True(done, "работа не дошла до нитки интерфейса");

        var timer = UiThread.NewTimer(TimeSpan.FromSeconds(1));

        Assert.NotNull(timer);
        Assert.Equal(TimeSpan.FromSeconds(1), timer!.Interval);
    }
}
