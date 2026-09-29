using System;
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
    /// <summary>Прокрутить очередь диспетчера, дожидаясь условия: замер размера идёт в фоне.</summary>
    private static bool WaitFor(Func<bool> condition, int milliseconds = 20000)
    {
        for (var waited = 0; waited < milliseconds && !condition(); waited += 5)
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
        var root = Path.Combine(Path.GetTempPath(), "dsh-scope-ui-" + Guid.NewGuid().ToString("N"));
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
            Assert.StartsWith(PanelStrings.BackupScopeAutoHint, Text(window, "ScopeHintText"), StringComparison.Ordinal);

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
}
