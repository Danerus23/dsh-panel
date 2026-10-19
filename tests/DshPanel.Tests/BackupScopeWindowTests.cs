using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЭКРАНЫ v2.2: выбор режима объёма и лишние имена — в ОКНЕ КОПИЙ (это решение о действии),
/// расписание и предел хранения — в НАСТРОЙКАХ (это настройки). Решение владельца:
/// «настройка — настройкой, действие — действием».
///
/// Проверяется то, что видит человек, и то, что за нажатием остаётся в файле настроек:
/// выбор режима доходит до файла, предупреждение о размере видно ДО копии, а строка-совет
/// о копии на ходу читается словами человека.
///
/// ⚠️ Отдельная проверка здесь — про ГРАБЛЮ, оплаченную приёмкой 26.09.2026: окно настроек пишет
/// файл ЦЕЛИКОМ, и поле, которого оно не знает, обнулялось бы каждым нажатием «Сохранить».
/// Тогда человек, выбравший «полный» режим в окне копий и зашедший в настройки, молча терял бы
/// своё решение. Ровно это стережёт <c>Окно_настроек_не_забывает_режим_и_лишние_имена</c>.
/// </summary>
public class BackupScopeWindowTests
{
    /// <summary>
    /// Прокрутить очередь диспетчера, дожидаясь условия: замер размера и копия идут в фоне.
    ///
    /// ⚠️ **Бюджет — это СЕКУНДЫ, а не число проходов** (Н-1, задача D3 цикла 2.1.2): прежде счётчик
    /// прибавлял по 5 на каждом проходе, а проход под нагрузкой стоит ~15 мс — и «20 секунд» на деле
    /// были **1 min 2 s** (ровно столько шла упавшая проверка 2.1.1). Теперь время берут часы,
    /// и число в бюджете значит то, что написано: ждать дольше двадцати секунд проверка не станет.
    /// </summary>
    private static bool WaitFor(Func<bool> condition, int milliseconds = 20000)
    {
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < milliseconds && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();

        return condition();
    }

    private static void Нажать(Window window, string buttonName) =>
        window.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Text(Window window, string name) => window.FindControl<TextBlock>(name)!.Text ?? string.Empty;

    /// <summary>
    /// Стенд: настоящие настройки на своём файле и настоящий контроллер копий при них. Файл нужен
    /// потому, что «сохранилось» проверяется ЧТЕНИЕМ файла, а не памятью окна.
    /// </summary>
    private static (SettingsController Settings, BackupController Backups, AppPaths Paths, string File) Stand()
    {
        // Корень — из TempRoots (правило `tests\AGENTS.md`, задача D2 этого же цикла): у каждого
        // прогона свой, и снимает его сам прогон при выходе. Прежде путь сеялся прямо в `%TEMP%`,
        // и упавшая проверка оставляла каталог навсегда — замер D3: один такой (0,04 МБ) остался.
        var root = TempRoots.For("scope-ui");
        Directory.CreateDirectory(root);

        var paths = AppPaths.Under(root);
        Directory.CreateDirectory(paths.DshHome);
        Directory.CreateDirectory(paths.Root);

        File.WriteAllText(Path.Combine(paths.Root, "заметка.txt"), "данные прогона");
        Directory.CreateDirectory(Path.Combine(paths.Root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(paths.Root, "node_modules", "pkg", "index.js"), new string('x', 40_000));

        var settings = new SettingsController(
            new SettingsStore(paths.SettingsFile),
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: () => null,
            applyTheme: _ => { },
            log: _ => { });

        // Папка копий — под корнем прогона: ни копий владельца, ни его «Документов» эта проверка
        // не касается (красная линия 4).
        var seeded = SettingsStore.Clean(settings.Settings);
        seeded.BackupFolder = paths.BackupsDir;
        Assert.True(settings.Save(seeded));

        var backups = new BackupController(
            paths,
            () => settings.Settings,
            allowed: true,
            mayTakeMachineEngineData: false,
            locateEngine: () => null,
            clock: () => new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3)),
            log: _ => { },
            settingsOwner: settings);

        return (settings, backups, paths, paths.SettingsFile);
    }

    private static void Cleanup(AppPaths paths)
    {
        try
        {
            Directory.Delete(paths.Root, true);
        }
        catch
        {
            // Уборка подставного корня не имеет права уронить проверку.
        }
    }

    // --- окно копий: режим объёма -------------------------------------------

    /// <summary>
    /// Выбор режима доходит до ФАЙЛА настроек, а пояснение меняется вместе с ним. У «полного»
    /// режима это предупреждение о размере — и оно видно ДО начала копии, как и решил владелец.
    /// </summary>
    [AvaloniaFact]
    public void Выбор_режима_доходит_до_настроек_и_меняет_пояснение()
    {
        var (settings, backups, paths, file) = Stand();

        try
        {
            var window = new BackupWindow();
            window.Attach(backups);

            // Окно ПОКАЗЫВАЕТСЯ: замер размера считается только у показанного окна — ответ приходит
            // из фоновой нити, а невидимое окно имеет право уже никому не принадлежать.
            window.Show();

            var scope = window.FindControl<ComboBox>("ScopeBox")!;
            Assert.Equal(0, scope.SelectedIndex);

            // Пояснение автоматического режима — первое, что видит человек; ниже к нему
            // дописывается размер (он считается в фоне, поэтому проверка идёт по началу строки).
            Assert.StartsWith(PanelStrings.BackupScopeAutoNote, Text(window, "ScopeHintText"), StringComparison.Ordinal);

            scope.SelectedIndex = 1;

            Assert.Equal(BackupScopeDecisions.Full, settings.Settings.BackupScope);
            Assert.Equal(BackupScopeDecisions.Full, new SettingsStore(file).Load().Settings.BackupScope);

            // Предупреждение о размере — в подсказке режима, и оно называет, чем копия больше.
            Assert.Contains(PanelStrings.BackupScopeFullWarning, Text(window, "ScopeHintText"));
            Assert.Contains("node_modules", Text(window, "ScopeHintText"), StringComparison.Ordinal);

            // Список лишних имён в этом режиме скрыт: он к нему не относится.
            Assert.False(window.FindControl<TextBox>("ScopeExtraBox")!.IsVisible);

            // Число приходит из фонового замера: без него предупреждение осталось бы словами.
            Assert.True(
                WaitFor(() => Text(window, "ScopeHintText").Contains("Примерно", StringComparison.Ordinal)),
                "размер копии в окне так и не появился");
        }
        finally
        {
            Cleanup(paths);
        }
    }

    /// <summary>
    /// «Свой фильтр»: поле лишних имён появляется, названное сохраняется в файл в чистом виде
    /// (без пустых строк и повторов), а замер режима после этого пересчитывается.
    /// </summary>
    [AvaloniaFact]
    public void Свой_фильтр_сохраняет_лишние_имена()
    {
        var (settings, backups, paths, file) = Stand();

        try
        {
            var window = new BackupWindow();
            window.Attach(backups);

            window.FindControl<ComboBox>("ScopeBox")!.SelectedIndex = 2;

            var box = window.FindControl<TextBox>("ScopeExtraBox")!;
            Assert.True(box.IsVisible);
            Assert.True(window.FindControl<Button>("SaveExtrasButton")!.IsVisible);

            box.Text = "vendor\n  target  \n\nvendor";
            Нажать(window, "SaveExtrasButton");

            Assert.Equal(new[] { "vendor", "target" }, settings.Settings.BackupExtraExclusions);
            Assert.Equal(new[] { "vendor", "target" }, new SettingsStore(file).Load().Settings.BackupExtraExclusions);

            // Список виден человеку и после сохранения — тем, что записалось, а не тем, что набрали.
            Assert.Equal("vendor" + Environment.NewLine + "target", box.Text);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    /// <summary>
    /// Фоновый замер размера заканчивается в любой момент — в том числе когда человек уже печатает
    /// имена, — и зовёт перерисовку окна. Перерисовка НЕ имеет права стирать набранное: иначе
    /// «сохранить» записало бы пустой список, а человек потерял бы введённое и не понял, куда оно
    /// делось. Дефект найден проверкой окна 26.09.2026 (она падала через раз) — и это была
    /// настоящая потеря введённого, а не мигание.
    /// </summary>
    [AvaloniaFact]
    public void Перерисовка_не_стирает_набранные_лишние_имена()
    {
        var (settings, backups, paths, _) = Stand();

        try
        {
            var window = new BackupWindow();
            window.Attach(backups);
            window.FindControl<ComboBox>("ScopeBox")!.SelectedIndex = 2;

            var box = window.FindControl<TextBox>("ScopeExtraBox")!;
            box.Text = "vendor\n  target  ";

            // Ровно то, что делает пришедший из фона замер размера.
            window.Render();

            Assert.Equal("vendor\n  target  ", box.Text);

            Нажать(window, "SaveExtrasButton");

            Assert.Equal(new[] { "vendor", "target" }, settings.Settings.BackupExtraExclusions);
            Assert.Equal("vendor" + Environment.NewLine + "target", box.Text);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    // --- окно настроек: расписание, хранение и грабля «поле не забыто» -------

    /// <summary>
    /// Окно настроек пишет файл целиком, поэтому режим объёма и лишние имена обязаны пройти через
    /// его сохранение НЕТРОНУТЫМИ. Иначе человек, выбравший режим в окне копий, потерял бы его,
    /// зайдя в настройки, — ровно та беда, что была с запомненным согласием на сервер.
    /// </summary>
    [AvaloniaFact]
    public void Окно_настроек_не_забывает_режим_и_лишние_имена()
    {
        var (settings, backups, paths, file) = Stand();

        try
        {
            // Человек выбрал «свой фильтр» и назвал имена — в ОКНЕ КОПИЙ.
            Assert.True(backups.SetScope(BackupScope.Custom));
            Assert.True(backups.SetExtraExclusions(new[] { "vendor", "target" }));

            // Пришёл в настройки и поменял расписание.
            var window = new SettingsWindow();
            window.Attach(settings, autostart: null);

            window.FindControl<CheckBox>("BackupScheduleCheck")!.IsChecked = false;
            window.FindControl<NumericUpDown>("BackupEveryBox")!.Value = 6;
            window.FindControl<NumericUpDown>("BackupKeepBox")!.Value = 5;

            Нажать(window, "SaveButton");
            Assert.True(window.Saved);

            var saved = new SettingsStore(file).Load().Settings;

            Assert.False(saved.BackupScheduleEnabled);
            Assert.Equal(6, saved.BackupEveryHours);
            Assert.Equal(5, saved.BackupKeepCount);

            // А это — то, ради чего проверка написана: чужое решение не затёрто.
            Assert.Equal(BackupScopeDecisions.Custom, saved.BackupScope);
            Assert.Equal(new[] { "vendor", "target" }, saved.BackupExtraExclusions);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    /// <summary>Мусор в часах приводится к границам в том же нажатии, а не «когда-нибудь потом».</summary>
    [AvaloniaFact]
    public void Мусор_в_часах_приводится_к_границам_при_сохранении()
    {
        var (settings, _, paths, file) = Stand();

        try
        {
            var window = new SettingsWindow();
            window.Attach(settings, autostart: null);

            var every = window.FindControl<NumericUpDown>("BackupEveryBox")!;
            var keep = window.FindControl<NumericUpDown>("BackupKeepBox")!;

            // В поле стоит УЖЕ приведённое значение: файл правят руками, и «100000 часов» в окне
            // выглядело бы как принятое, хотя панель возьмёт неделю.
            every.Value = 100000;
            keep.Value = 0;

            Нажать(window, "SaveButton");

            var saved = new SettingsStore(file).Load().Settings;

            Assert.Equal(PanelSettings.BackupHoursMax, saved.BackupEveryHours);
            Assert.Equal(PanelSettings.BackupKeepMin, saved.BackupKeepCount);
            Assert.Equal(PanelSettings.BackupHoursMax, (int)every.Value!);
            Assert.Equal(PanelSettings.BackupKeepMin, (int)keep.Value!);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    // --- слова человека о копии на ходу -------------------------------------

    /// <summary>
    /// Довод владельца (26.09.2026) обязан читаться на экране ТЕМИ ЖЕ словами: копия по расписанию
    /// снимается НА ХОДУ, работающий агент не прерывается, а для целостной копии её снимают ВРУЧНУЮ,
    /// когда работа не идёт. Сказано и там, где расписание включают, и там, где копию снимают.
    /// </summary>
    [AvaloniaFact]
    public void Строка_о_копии_на_ходу_читается_словами_владельца()
    {
        var (settings, backups, paths, _) = Stand();

        try
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Attach(settings, autostart: null);

            var note = Text(settingsWindow, "BackupLiveNoteText");

            Assert.Equal(PanelStrings.SettingsBackupLiveNote, note);
            Assert.Contains("на ходу", note, StringComparison.Ordinal);
            Assert.Contains("не прерыва", note, StringComparison.Ordinal);
            Assert.Contains("вручную", note, StringComparison.Ordinal);
            Assert.Contains("работа не идёт", note, StringComparison.Ordinal);

            var backupWindow = new BackupWindow();
            backupWindow.Attach(backups);

            var advice = Text(backupWindow, "LiveAdviceText");

            Assert.Equal(PanelStrings.BackupLiveAdvice, advice);
            Assert.Contains("не прерыва", advice, StringComparison.Ordinal);
            Assert.Contains("вручную", advice, StringComparison.Ordinal);
            Assert.Contains("работа не идёт", advice, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    /// <summary>
    /// Пока идёт копия, режим менять нечем: список режимов и кнопки гаснут. Это не украшение —
    /// иначе человек выбрал бы режим для копии, которая уже снимается по-другому, и решил бы,
    /// что панель его не послушала.
    /// </summary>
    [AvaloniaFact]
    public void Во_время_работы_режим_не_меняется()
    {
        var (_, backups, paths, _) = Stand();

        try
        {
            var window = new BackupWindow();
            window.Attach(backups);

            // Сервера у стенда нет, поэтому вопрос «погасить?» не задаётся вовсе: копия начинается
            // тем же нажатием, каким её начинает человек.
            Нажать(window, "CreateButton");

            Assert.True(window.Busy, "окно не занялось копией");
            Assert.False(window.FindControl<ComboBox>("ScopeBox")!.IsEnabled);
            Assert.False(window.FindControl<Button>("CreateButton")!.IsEnabled);

            Assert.True(WaitFor(() => !window.Busy), "окно не вернулось в рабочее состояние");
            Assert.True(window.FindControl<ComboBox>("ScopeBox")!.IsEnabled);
        }
        finally
        {
            Cleanup(paths);
        }
    }

    /// <summary>
    /// Н-1 (наблюдение цикла 2.1.1, закрывается задачей D3 цикла 2.1.2): ответ фоновой копии доходит
    /// до СВОЕГО окна даже тогда, когда память нитки интерфейса в двери успели забыть.
    ///
    /// **От какого случая.** 1 падение из 5 полных прогонов под нагрузкой: копия снималась, окно было
    /// занято, и «окно не вернулось в рабочее состояние» — сама проверка шла **1 min 2 s**. Память
    /// нитки интерфейса в <see cref="UiThread"/> — ОДНА на процесс, а проверка «нитки нет вовсе»
    /// (`UiThreadTests`) очищает её (`ForgetForTests`). Классы идут ПАРАЛЛЕЛЬНО (разные коллекции),
    /// и очистка может лечь ровно между постройкой окна и ответом фоновой задачи: `UiThread.Post`
    /// честно отвечает `false`, а вызывающий это `false` терял — окно оставалось занятым навсегда,
    /// без единого слова человеку.
    ///
    /// Проверка воспроизводит это **без гонки**: окно построено (свою нитку оно знает), дверь забыта,
    /// нажатие — и ответ обязан дойти. На коде без правки она падает «ответ копии не дошёл до окна»
    /// (замер — `_team\c6\rd3-n1.md`), с правкой — зелёная.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_фоновой_копии_доходит_до_окна_и_после_забывания_двери()
    {
        var (_, backups, paths, _) = Stand();

        try
        {
            var window = new BackupWindow();
            window.Attach(backups);

            // Чужая проверка двери очищает память нитки интерфейса — ровно это и происходит
            // в полном прогоне, когда UiThreadTests идёт параллельно с этим классом.
            UiThread.ForgetForTests();

            Нажать(window, "CreateButton");

            Assert.True(window.Busy, "окно не занялось копией");
            Assert.True(WaitFor(() => !window.Busy), "ответ копии не дошёл до окна");
        }
        finally
        {
            UiThread.Remember();
            Cleanup(paths);
        }
    }

    /// <summary>
    /// ОКНО КОПИЙ БЕЗ НИТКИ ИНТЕРФЕЙСА — ГРОМКИЙ ОТКАЗ (t13). Так же, как у главного окна: пустое
    /// значение не прячется в поле, а отказ называет СВОЁ окно. Строить окно без платформы нельзя
    /// (базовый `Window` падает первым), поэтому проверяется ровно решение, которое зовёт конструктор.
    /// </summary>
    [Fact]
    public void Окно_копий_без_нитки_интерфейса_отказывается_строиться()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => BackupWindow.RequireUiThread(null));

        Assert.Contains("ОКНО КОПИЙ БЕЗ НИТКИ ИНТЕРФЕЙСА", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Стройте окно на нитке интерфейса", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТЛОЖЕННАЯ РАБОТА ДОХОДИТ ДО СВОЕЙ НИТКИ И ПОСЛЕ ЗАБЫВАНИЯ ДВЕРИ (t13) — механизм, которым
    /// пользуется отложенная прокрутка плана (`ShowPlanOnScreen`, `DispatcherPriority.Loaded`).
    ///
    /// ⚠️ **Чего эта проверка НЕ доказывает:** что прокрутка плана сработала. Её следствие — позиция
    /// прокрутки, и отдельного наблюдения за ней в наборе нет. Названо непокрытым в отчёте, а не
    /// выдано за проверенное.
    /// </summary>
    [AvaloniaFact]
    public void Отложенная_работа_доходит_до_своей_нитки_после_забывания_двери()
    {
        var ui = UiThread.Capture();

        Assert.NotNull(ui);

        try
        {
            UiThread.ForgetForTests();

            var done = false;

            Assert.True(
                UiThread.PostTo(ui, () => done = true, DispatcherPriority.Loaded),
                "работа не отправлена в захваченную нитку интерфейса");

            Dispatcher.UIThread.RunJobs();

            Assert.True(done, "отложенная работа не дошла до нитки интерфейса");
        }
        finally
        {
            UiThread.Remember();
        }
    }
}