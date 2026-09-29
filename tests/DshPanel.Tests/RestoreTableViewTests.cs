using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТАБЛИЦА «ЧТО ВЕРНЁМ» (п. 32 <c>docs\DESIGN.md</c>) — то, ради чего план наката переделан
/// из абзаца в строки.
///
/// Слова владельца: *«Что вернём, копия? Текст очень неструктурированный, просто сплошной,
/// и читать невозможно»*. Отсюда обещания, и каждое проверяется там, где его видно:
///
/// 1. **строки идут из СТРУКТУРНОГО плана** (<c>RestorePlan.Groups</c>), а не разбором готового
///    текста: в таблице ровно те группы, что есть в плане, и под своими колонками;
/// 2. **пустые группы не показаны вовсе** — группа с нулём файлов места не занимает (это и была
///    «простыня»: пять групп из шести ничего не сообщали);
/// 3. **видно и то, что НЕ вернётся, и почему** — со словами причины из плана;
/// 4. **путь цели под маской** (<c>Shell\DisplayMask</c>), а не сырым: имя пользователя в кадре,
///    журнале и чужом отчёте — личные данные;
/// 5. **счётчик под таблицей считает ФАЙЛЫ ВЫБРАННЫХ ГРУПП**, а не весь архив: иначе он спорил бы
///    со своими же строками («не вернётся» рядом с числом, куда эта группа входит);
/// 6. **путь отказа плана не потерян**: причина и примечания словами, как было до таблицы.
///
/// ⚠️ **Живой путь, а не подставной план.** Окно строится настоящее (<see cref="BackupWindow"/>),
/// копия снимается НАСТОЯЩИМ движком под своим временным корнем (<see cref="PanelTestStand.TempDir"/>),
/// а таблица читается с органов окна после выбора строки копии — тем же путём, каким идёт человек.
/// Ни одного каталога владельца: папка копий задана явно и лежит под временным корнем.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class RestoreTableViewTests
{
    private readonly ITestOutputHelper _output;

    public RestoreTableViewTests(ITestOutputHelper output) => _output = output;

    /// <summary>Стенд живой проверки: временный корень, пути прогона и контроллер копий под ним.</summary>
    private sealed record Stand(string Root, AppPaths Paths, BackupController Controller, string EmptyPrefix);

    private static SettingsController Owner(AppPaths paths) =>
        new(new SettingsStore(paths.SettingsFile),
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: () => null,
            applyTheme: _ => { },
            log: _ => { });

    /// <summary>
    /// ЖИВОЙ СТЕНД: настоящая копия настоящим движком.
    ///
    /// Состав подобран так, чтобы таблице было что показать и что СКРЫТЬ:
    /// * домашний каталог движка — два файла: группа, которая вернётся;
    /// * каталог ключей — один файл: группа, которая БЕЗ согласия не вернётся (и причина названа);
    /// * рабочая папка — ПУСТАЯ: так в описи появляется группа БЕЗ единого файла, и таблица обязана
    ///   её не показывать.
    /// </summary>
    private static Stand Live()
    {
        var root = PanelTestStand.TempDir();
        var paths = AppPaths.Under(root);

        Directory.CreateDirectory(paths.DshHome);
        File.WriteAllText(Path.Combine(paths.DshHome, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshHome, "history.jsonl"), "{}");

        Directory.CreateDirectory(paths.SshDir);
        File.WriteAllText(Path.Combine(paths.SshDir, "id_test"), "ключ");

        var work = Path.Combine(root, "работа");
        Directory.CreateDirectory(work);

        // Владелец настроек живёт под СВОИМ подкорнем: так файл настроек не попадает в дерево,
        // которое копируется, и состав групп остаётся предсказуемым.
        var owner = Owner(AppPaths.Under(Path.Combine(root, "seed")));

        owner.Save(new PanelSettings
        {
            BackupFolder = paths.BackupsDir,
            ServerWorkingDir = work,
            BackupWithKeys = true,
        });

        var controller = new BackupController(
            paths,
            () => owner.Settings,
            allowed: true,
            locateEngine: () => null,
            log: _ => { },
            settingsOwner: owner);

        var copy = controller.CreateCopy(controller.NextArchivePath(), serverRunning: false, shareable: false);
        Assert.True(copy.Ok, "настоящая копия не снята: " + copy.Summary());

        controller.Refresh();
        Assert.True(controller.Entries.Count > 0, "снятая копия не попала в список: " + controller.ListProblem);

        return new Stand(root, paths, controller, ZipLayout.Leaf(work));
    }

    /// <summary>Окно с живым контроллером и выбранной копией — ровно то, что делает человек.</summary>
    private static BackupWindow Window(Stand stand)
    {
        var window = new BackupWindow();
        window.Attach(stand.Controller);
        window.Show();
        PanelTestStand.Settle();

        // Выбор строки копии — та же дверь, которую дёргает щелчок по таблице копий.
        window.ChooseEntry(0);
        PanelTestStand.Settle();

        return window;
    }

    /// <summary>Тексты ячеек одной строки — в порядке колонок.</summary>
    private static string[] Cells(Border line)
    {
        var grid = Assert.IsType<Grid>(line.Child);

        return grid.Children.OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToArray();
    }

    /// <summary>Строки таблицы БЕЗ шапки (шапка — первая).</summary>
    private static IReadOnlyList<Border> Body(Grid table) => RestoreTableView.Lines(table).Skip(1).ToList();

    private static Grid Table(BackupWindow window)
    {
        var table = window.FindControl<Grid>("PlanTable");

        Assert.NotNull(table);
        Assert.True(table!.IsVisible, "таблица плана не показана человеку");

        return table;
    }

    // ---- 1-2. строки из плана, пустые группы скрыты ------------------------------------------

    /// <summary>
    /// СТРОКА НА КАЖДУЮ НЕПУСТУЮ ГРУППУ И ШАПКА КОЛОНОК, а пустая группа не показана вовсе.
    /// Число ячеек у шапки и у строк одно: иначе значения стояли бы не под своими подписями.
    /// </summary>
    [AvaloniaFact]
    public void Строки_идут_из_плана_а_пустые_группы_не_показаны()
    {
        var stand = Live();

        try
        {
            var window = Window(stand);

            try
            {
                var plan = window.Plan;
                Assert.NotNull(plan);
                Assert.True(plan!.Ok, "план не собрался: " + plan.Error);

                var groups = RestoreTableView.Rows(plan);

                Assert.DoesNotContain(groups, group => group.Files == 0);
                Assert.DoesNotContain(
                    groups,
                    group => string.Equals(group.Prefix, stand.EmptyPrefix, StringComparison.Ordinal));

                // Пустая группа в архиве ЕСТЬ — иначе «не показана» проверялось бы на пустом месте.
                Assert.Contains(plan.Groups, group => group.Files == 0 && group.Prefix == stand.EmptyPrefix);

                var table = Table(window);
                var lines = RestoreTableView.Lines(table);

                Assert.Equal(groups.Count + 1, lines.Count);

                Assert.Equal(
                    new[]
                    {
                        PanelStrings.RestoreTableGroup,
                        PanelStrings.RestoreTableTarget,
                        PanelStrings.RestoreTableFiles,
                        PanelStrings.RestoreTableState,
                    },
                    Cells(lines[0]));

                // И в каждой строке — СВОЯ группа, в том же порядке, что в плане.
                Assert.Equal(
                    groups.Select(group => group.Prefix).ToArray(),
                    Body(table).Select(line => Cells(line)[0]).ToArray());
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(stand.Root);
        }
    }

    // ---- 3-4. что НЕ вернётся, и цель под маской --------------------------------------------

    /// <summary>
    /// НЕВЫБРАННАЯ ГРУППА НАЗВАНА ПРИЧИНОЙ, ЦЕЛИ У НЕЁ НЕТ, А ЦЕЛЬ ВЕРНУВШЕЙСЯ — ПОД МАСКОЙ.
    /// Молчащая клетка читалась бы как «всё в порядке», а сырой путь вынес бы имя пользователя
    /// в кадр и в чужой отчёт.
    /// </summary>
    [AvaloniaFact]
    public void Невыбранная_группа_названа_причиной_а_цель_вернувшейся_под_маской()
    {
        var stand = Live();

        try
        {
            var window = Window(stand);

            try
            {
                var plan = window.Plan;
                Assert.NotNull(plan);

                var table = Table(window);
                var home = plan!.Groups.Single(group => group.Prefix == ZipLayout.Leaf(stand.Paths.DshHome));
                var keys = plan.Groups.Single(group => group.IsKeyRing);

                // Файлы у невыбранной группы ЕСТЬ — иначе проверка была бы про пустую строку.
                Assert.True(keys.Files > 0, "в архиве нет ни одного файла ключей — проверять нечего");
                Assert.False(keys.Selected);

                var keyCells = Cells(Body(table).Single(line => Cells(line)[0] == keys.Prefix));

                Assert.Equal(string.Empty, keyCells[1]);
                Assert.Equal(keys.Note, keyCells[3]);
                Assert.NotEqual(PanelStrings.RestoreTableLands, keyCells[3]);

                var homeCells = Cells(Body(table).Single(line => Cells(line)[0] == home.Prefix));

                Assert.True(home.Selected);
                Assert.Contains(PanelStrings.RestoreTableLands, homeCells[3], StringComparison.Ordinal);

                // Путь цели — РОВНО тот, что даёт маска панели, и не сырой путь.
                Assert.Equal(DisplayMask.Path(home.Target), homeCells[1]);

                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (!string.IsNullOrEmpty(profile)
                    && home.Target.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.StartsWith("~", homeCells[1], StringComparison.Ordinal);
                    Assert.DoesNotContain(profile, homeCells[1], StringComparison.OrdinalIgnoreCase);
                }

                // Число файлов — из описи архива, а не пересказом.
                Assert.Equal(BackupTableView.FilesText(home.Files), homeCells[2]);
                Assert.True(home.Files > 0);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(stand.Root);
        }
    }

    // ---- 5. счётчик считает то, что ляжет ----------------------------------------------------

    /// <summary>
    /// СЧЁТЧИК ПОД ТАБЛИЦЕЙ СЧИТАЕТ ФАЙЛЫ ВЫБРАННЫХ ГРУПП, А НЕ ВЕСЬ АРХИВ.
    ///
    /// У невыбранной группы (ключи без согласия) файлы в описи ЕСТЬ, и общее число по архиву
    /// отличалось бы от того, что ляжет. Строка «не вернётся» стоит рядом в таблице: счётчик,
    /// посчитанный по архиву, спорил бы с собственной таблицей — то есть врал бы человеку ровно
    /// там, где он спрашивает «что вернём». Нашёл дирижёр 29.09.2026.
    ///
    /// Здесь же проверяется, что строка режима БЕЗ ОКНА (`RestorePlan.Summary`, `--restore`) считает
    /// тем же числом: у счёта одно место правды.
    /// </summary>
    [AvaloniaFact]
    public void Счётчик_под_таблицей_считает_файлы_выбранных_групп_а_не_весь_архив()
    {
        var stand = Live();

        try
        {
            var window = Window(stand);

            try
            {
                var plan = window.Plan;
                Assert.NotNull(plan);

                var restoring = plan!.Selected.Sum(group => group.Files);

                // Невыбранная группа с файлами ЕСТЬ, и числа расходятся — иначе проверка слепа.
                Assert.Contains(plan.Groups, group => !group.Selected && group.Files > 0);
                Assert.True(
                    plan.Files > restoring,
                    $"в архиве {plan.Files} файл(ов), а ляжет {restoring} — числа обязаны расходиться");

                var summary = window.FindControl<TextBlock>("PlanSummaryText");

                Assert.NotNull(summary);
                Assert.True(summary!.IsVisible, "счётчик под таблицей не показан");

                // ⚠️ Группы считаются ПО СТРОКАМ таблицы (решение дирижёра 29.09.2026): выбранная,
                // но ПУСТАЯ группа — рабочая папка в этом наборе — в таблицу не попадает, и счётчик
                // обязан называть то, что человек ВИДИТ. Без неё проверка была бы слепой: числа
                // совпадали бы при любом правиле счёта.
                var lands = RestoreTableView.Rows(plan).Count(group => group.Selected);

                Assert.Contains(plan.Groups, group => group.Selected && group.Files == 0);
                Assert.True(
                    lands < plan.Selected.Count,
                    $"в наборе нет выбранной пустой группы — разницы не видно ({lands} против {plan.Selected.Count})");

                Assert.Equal(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.RestoreTableSummaryFormat,
                        restoring,
                        lands),
                    summary.Text);

                // И ТО ЖЕ число называет строка режима без окна: двух правд о счёте быть не должно.
                Assert.Contains(
                    restoring.ToString("N0", CultureInfo.InvariantCulture),
                    plan.Summary(),
                    StringComparison.Ordinal);

                // А прежняя склейка всех выбранных групп в один `TextBlock` из окна УБРАНА.
                Assert.Null(window.FindControl<TextBlock>("PlanText"));

                // Счётчик — КОРОТКАЯ строка, а не абзац: имён групп в нём нет.
                Assert.DoesNotContain(ZipLayout.Leaf(stand.Paths.DshHome), summary.Text!, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(stand.Root);
        }
    }

    // ---- 6. путь отказа плана ----------------------------------------------------------------

    /// <summary>
    /// ПЛАН НЕ СОБРАЛСЯ — ОКНО ПОКАЗЫВАЕТ ПРИЧИНУ И ПРИМЕЧАНИЯ СЛОВАМИ, а таблицы не показывает:
    /// раскладывать нечего, и строки без решения читались бы как готовый план.
    ///
    /// Отказ здесь НАСТОЯЩИЙ: копия снята настоящим движком, но в ней одни ключи, а согласия на них
    /// нет — «ни одна группа не выбрана к раскладке», и рядом примечание о ключах. Придуманного
    /// плана в этой проверке нет: окно получает его тем же путём, что и человек.
    /// </summary>
    [AvaloniaFact]
    public void План_отказа_показывает_причину_и_примечания_словами()
    {
        var root = PanelTestStand.TempDir();

        try
        {
            // Папка копий и каталог ключей — ВНЕ корня данных: так в архиве не оказывается ничего,
            // кроме ключей, и план честно отказывает «ни одна группа не выбрана».
            var copies = Path.Combine(root, "копии");
            var keys = Path.Combine(root, "ключи");
            Directory.CreateDirectory(copies);
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "id_test"), "ключ");

            var owner = Owner(AppPaths.Under(Path.Combine(root, "seed")));

            var settings = new PanelSettings
            {
                BackupFolder = copies,
                BackupWithKeys = true,
                BackupKeyDirs = new List<string> { keys },
            };

            var controller = new BackupController(
                AppPaths.Under(Path.Combine(root, "данные")),
                () => settings,
                allowed: true,
                locateEngine: () => null,
                log: _ => { },
                settingsOwner: owner);

            var copy = controller.CreateCopy(controller.NextArchivePath(), serverRunning: false, shareable: false);
            Assert.True(copy.Ok, "настоящая копия не снята: " + copy.Summary());

            controller.Refresh();
            Assert.True(controller.Entries.Count > 0, "снятая копия не попала в список: " + controller.ListProblem);

            var window = new BackupWindow();
            window.Attach(controller);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                window.ChooseEntry(0);
                PanelTestStand.Settle();

                var plan = window.Plan;
                Assert.NotNull(plan);
                Assert.False(plan!.Ok, "план неожиданно собрался: " + plan.Summary());
                Assert.NotEqual(string.Empty, plan.Error);
                Assert.NotEmpty(plan.Notes);

                var problem = window.FindControl<TextBlock>("PlanProblemText");
                var notes = window.FindControl<TextBlock>("PlanNotesText");

                Assert.NotNull(problem);
                Assert.NotNull(notes);

                Assert.True(problem!.IsVisible, "причина отказа не показана");
                Assert.Equal(
                    string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.RestorePlanImpossibleFormat, plan.Error),
                    problem.Text);

                Assert.True(notes!.IsVisible, "примечания плана не показаны");
                Assert.All(plan.Notes, note => Assert.Contains(note, notes.Text, StringComparison.Ordinal));

                // Таблицы при отказе нет: раскладывать нечего.
                var table = window.FindControl<Grid>("PlanTable");
                Assert.NotNull(table);
                Assert.False(table!.IsVisible, "таблица показана на несостоявшемся плане");

                var summary = window.FindControl<TextBlock>("PlanSummaryText");
                Assert.NotNull(summary);
                Assert.False(summary!.IsVisible, "счётчик показан на несостоявшемся плане");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(root);
        }
    }

    // ---- вид ---------------------------------------------------------------------------------

    /// <summary>
    /// ВИД ТАБЛИЦЫ МОЖНО ПОСМОТРЕТЬ ГЛАЗАМИ: проверка сохраняет кадр окна в PNG и называет путь
    /// в отчёте. Зелёная проверка не отвечает на вопрос «а как это выглядит человеку», а таблицу
    /// переделывали ровно ради чтения.
    ///
    /// ⚠️ Кадр уходит в <c>%TEMP%</c> и НИКОГДА в репозиторий, <c>dist</c> или <c>upd-build</c>:
    /// в нём пути и состав копий этого прогона.
    /// </summary>
    [AvaloniaFact]
    public void Вид_таблицы_что_вернём_сохраняется_кадром()
    {
        var stand = Live();

        try
        {
            var window = Window(stand);

            try
            {
                Assert.NotNull(window.Plan);

                var path = PanelTestStand.SaveFrame(window, "plan-" + Guid.NewGuid().ToString("N")[..8]);

                _output.WriteLine("кадр таблицы «что вернём»: " + path);

                Assert.True(File.Exists(path), "кадр не сохранён: " + path);
                Assert.True(new FileInfo(path).Length > 0, "кадр пуст: " + path);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(stand.Root);
        }
    }
}
