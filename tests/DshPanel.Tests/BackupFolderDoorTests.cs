using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕРЬ «ОТКРЫТЬ ПАПКУ КОПИЙ» (п. 31 <c>docs\DESIGN.md</c>) — та же форма, что у двери каталога
/// настроек (<c>SettingsFolderDoorTests</c>), и по тому же уговору: право приходит ОТКРЫТЫМ
/// параметром, отказ называет причину СЛОВАМИ, шов <c>opener</c> без права не зовут вовсе,
/// а наружу не летит ни одного исключения.
///
/// Что здесь доказывается, и каждое — прогоном:
///
/// 1. **в прогоне проверки счётчик вызовов остаётся ПУСТЫМ.** Настоящее открытие показало бы
///    проводник на рабочем столе владельца (красная линия 8: прогон проверки не показывает окон),
///    поэтому проверка подставляет счётчик и требует, чтобы его не тронули. Есть и встречная
///    проверка: тот же счётчик, позванный руками, вызов записывает — иначе «пусто» держалось бы
///    случайностью вроде несломанного шва;
/// 2. **открывается РОВНО папка копий** (<c>IBackupControl.Folder</c>) и ровно один раз;
/// 3. **папки может не быть** (до первой копии её нет): тогда честный отказ словами, а не пустой
///    проводник — шов при этом тоже не зовут;
/// 4. **слова отказа — про папку КОПИЙ, а не про каталог настроек.** Две двери делят форму, но не
///    слова: прочитав «каталог настроек неизвестен» на кнопке папки копий, человек искал бы беду
///    не там. Это сторожится сравнением с настройками словами, а не «содержит слово папка»;
/// 5. **кнопка стоит ВПЛОТНУЮ к строке с путём** и в окне (настоящем), а не только в разметке.
///
/// ⚠️ Чего эта проверка НЕ покрывает и не притворяется: настоящего проводника. Открывается он
/// швом, а не оболочкой: оболочка показала бы окно человеку. Дверь до оболочки
/// (<see cref="AgentBrowser.OpenWithShell"/>) проверена отдельно и раньше — теми же словами
/// о том, что отказ оболочки не роняет панель.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class BackupFolderDoorTests
{
    /// <summary>Счётчик вместо проводника: что позвали и с каким путём.</summary>
    private sealed class Opener
    {
        public List<string> Calls { get; } = new();

        public string Result { get; set; } = string.Empty;

        public string Open(string folder)
        {
            Calls.Add(folder);
            return Result;
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-backup-door", Guid.NewGuid().ToString("N"));

    /// <summary>Папка копий, которая СУЩЕСТВУЕТ: настоящий временный каталог, а не выдуманный путь.</summary>
    private static string ExistingFolder(string root)
    {
        var folder = Path.Combine(root, "Копии");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Папка, которой НЕТ: так выглядит папка копий до первой копии.</summary>
    private static string MissingFolder(string root) => Path.Combine(root, "копий-ещё-нет");

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Каталог занят — это не беда проверки.
        }
    }

    /// <summary>
    /// Владелец копий ПРОГОНА ПРОВЕРКИ: без права снимать копии и без владельца настроек —
    /// ровно так его строит кадр `--shot backup` (`Program.Shot`).
    /// </summary>
    private static BackupController ReadOnly(string root, string folder) =>
        new(
            AppPaths.Under(root),
            () => new PanelSettings { BackupFolder = folder },
            allowed: false,
            locateEngine: () => null);

    /// <summary>
    /// Владелец копий ОБЫЧНОГО запуска: и право снимать копии, и СВОИ настройки (тем же
    /// владельцем настроек, что у окна настроек, — так проверяется настоящий путь значения).
    /// Папка копий названа явно: умолчание — «Документы\…», и проверка не должна его касаться.
    /// </summary>
    private static BackupController WithOwner(string root, string folder)
    {
        var paths = AppPaths.Under(root);
        var owner = new SettingsController(
            new SettingsStore(paths.SettingsFile),
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: () => null,
            applyTheme: _ => { },
            log: _ => { });

        owner.Save(new PanelSettings { BackupFolder = folder });

        return new BackupController(
            paths,
            () => owner.Settings,
            allowed: true,
            locateEngine: () => null,
            log: _ => { },
            settingsOwner: owner);
    }

    // ---- дверь -----------------------------------------------------------------------------

    /// <summary>
    /// В ПРОГОНЕ ПРОВЕРКИ ШОВ НЕ ЗОВУТ ВОВСЕ, и причина названа словами ПРО ПАПКУ КОПИЙ.
    /// Путь здесь НЕПУСТОЙ и существующий намеренно: пустым путём предохранитель проверялся бы
    /// случайностью — дверь не открылась бы просто потому, что открывать нечего.
    /// </summary>
    [Fact]
    public void В_прогоне_проверки_дверь_не_зовёт_шов_и_называет_папку_копий()
    {
        var root = NewRoot();
        var opener = new Opener();
        var log = new List<string>();

        try
        {
            var folder = ExistingFolder(root);

            var opened = AgentBrowser.TryOpenFolder(
                mayOpenFolder: false,
                folder,
                AgentBrowser.FolderDoorWords.Backups,
                mustExist: true,
                opener.Open,
                log.Add,
                out var error);

            Assert.False(opened);
            Assert.Empty(opener.Calls);
            Assert.Equal(PanelStrings.BackupFolderRefusedLog, error);

            // СЛОВА — ПРО ПАПКУ КОПИЙ. Сверка идёт с настоящими словами соседней двери, а не
            // с «похоже на правильное»: слей двери в одну — и это сравнение упадёт.
            Assert.NotEqual(PanelStrings.SettingsFolderRefusedLog, error);
            Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));

            // И встречная проверка: шов рабочий — позванный руками, он записывает вызов.
            // Без неё «пусто» означало бы и «не позвали», и «шов сломан».
            Assert.Equal(string.Empty, opener.Open(folder));
            Assert.Equal(new[] { folder }, opener.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>С ПРАВОМ открывается РОВНО папка копий и ровно один раз.</summary>
    [Fact]
    public void С_правом_открывается_ровно_папка_копий()
    {
        var root = NewRoot();
        var opener = new Opener();
        var log = new List<string>();

        try
        {
            var folder = ExistingFolder(root);

            var opened = AgentBrowser.TryOpenFolder(
                mayOpenFolder: true,
                folder,
                AgentBrowser.FolderDoorWords.Backups,
                mustExist: true,
                opener.Open,
                log.Add,
                out var error);

            Assert.True(opened);
            Assert.Equal(string.Empty, error);
            Assert.Equal(new[] { folder }, opener.Calls);
            Assert.Contains(log, line => line.Contains("по щелчку человека", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ПАПКИ НЕТ — ОТКРЫВАТЬ НЕЧЕГО, и это НЕ «открылось»: пустой путь и несуществующий каталог
    /// дают честный отказ словами, а шов не зовут. Пустой проводник человек прочитал бы как
    /// «панель сломалась», хотя копий просто ещё не было.
    /// </summary>
    [Fact]
    public void Папки_нет_открывать_нечего_и_пустой_проводник_не_открывается()
    {
        var root = NewRoot();
        var opener = new Opener();
        var log = new List<string>();

        try
        {
            foreach (var none in new[] { string.Empty, "   ", null, MissingFolder(root) })
            {
                var opened = AgentBrowser.TryOpenFolder(
                    mayOpenFolder: true,
                    none,
                    AgentBrowser.FolderDoorWords.Backups,
                    mustExist: true,
                    opener.Open,
                    log.Add,
                    out var error);

                Assert.False(opened);
                Assert.Equal(PanelStrings.BackupFolderMissing, error);
            }

            Assert.Empty(opener.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ОТКАЗ ОБОЛОЧКИ (нет проводника, снятая ассоциация каталогов) не роняет панель: причина
    /// уходит и в журнал, и вызывающему, а исключение из шва превращается в ту же строку отказа.
    /// </summary>
    [Fact]
    public void Отказ_оболочки_называется_словами_и_не_роняет_панель()
    {
        var root = NewRoot();
        var opener = new Opener { Result = "Win32Exception — нет приложения для каталогов" };
        var log = new List<string>();

        try
        {
            var opened = AgentBrowser.TryOpenFolder(
                mayOpenFolder: true,
                ExistingFolder(root),
                AgentBrowser.FolderDoorWords.Backups,
                mustExist: true,
                opener.Open,
                log.Add,
                out var error);

            Assert.False(opened);
            Assert.Contains("нет приложения", error, StringComparison.Ordinal);

            Assert.Contains(
                log,
                line => line.Contains("Открыть папку копий не удалось", StringComparison.Ordinal));

            var throwing = AgentBrowser.TryOpenFolder(
                mayOpenFolder: true,
                ExistingFolder(root),
                AgentBrowser.FolderDoorWords.Backups,
                mustExist: true,
                _ => throw new InvalidOperationException("шов упал"),
                log.Add,
                out var thrown);

            Assert.False(throwing);
            Assert.Contains("шов упал", thrown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- владелец копий --------------------------------------------------------------------

    /// <summary>
    /// ВЛАДЕЛЕЦ КОПИЙ ПРОГОНА ПРОВЕРКИ ОТКАЗЫВАЕТ САМ, и папка при этом СУЩЕСТВУЕТ: значит отказ
    /// держится ПРАВОМ, а не тем, что открывать нечего. Это и есть проверка «прогон проверки
    /// не открывает ничего».
    /// </summary>
    [Fact]
    public void Владелец_копий_без_права_отказывает_и_ничего_не_открывает()
    {
        var root = NewRoot();
        var opener = new Opener();

        try
        {
            var controller = ReadOnly(root, ExistingFolder(root));
            controller.FolderOpener = opener.Open;

            Assert.False(controller.Writable);
            Assert.False(controller.CanChangeComposition);

            Assert.False(controller.OpenFolder(out var error));
            Assert.Equal(PanelStrings.BackupFolderRefusedLog, error);
            Assert.Empty(opener.Calls);

            // Шов настроен и рабочий: «пусто» выше — это «не позвали», а не «позвать нечем».
            Assert.Equal(string.Empty, opener.Open(controller.Folder));
            Assert.Single(opener.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// С ПРАВОМ открывается ИМЕННО папка копий этого прогона, а когда её нет — честный отказ
    /// словами, и второй раз шов не зовут.
    /// </summary>
    [Fact]
    public void Владелец_копий_с_правом_открывает_свою_папку_и_называет_её_отсутствие()
    {
        var root = NewRoot();
        var opener = new Opener();

        try
        {
            var folder = ExistingFolder(root);
            var controller = WithOwner(root, folder);
            controller.FolderOpener = opener.Open;

            Assert.True(controller.Writable);
            Assert.True(controller.CanChangeComposition);

            Assert.True(controller.OpenFolder(out var error));
            Assert.Equal(string.Empty, error);
            Assert.Equal(new[] { controller.Folder }, opener.Calls);
            Assert.Equal(folder, controller.Folder);

            // Папку убрали — так выглядит состояние ДО первой копии.
            Directory.Delete(folder, recursive: true);

            Assert.False(controller.OpenFolder(out var gone));
            Assert.Equal(PanelStrings.BackupFolderMissing, gone);
            Assert.Single(opener.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ПОДМЕНА ДОМЕНА КОПИЙ ПУТЕЙ НЕ ЗНАЕТ И ЧЕСТНО ОТКАЗЫВАЕТ: член добавлен в интерфейс
    /// С реализацией, и реализация по умолчанию не притворяется, что папку открыли. Иначе подмена
    /// соврала бы человеку строкой в окне.
    /// </summary>
    [Fact]
    public void Подмена_домена_копий_честно_отказывает()
    {
        IBackupControl control = new BackupWindowStopFlowTests.StubBackups();

        Assert.False(control.OpenFolder(out var error));
        Assert.Equal(PanelStrings.BackupFolderMissing, error);
    }

    // ---- окно ------------------------------------------------------------------------------

    /// <summary>
    /// КНОПКА В ОКНЕ НАЗЫВАЕТ ПРИЧИНУ ОТКАЗА СЛОВАМИ, А НЕ МОЛЧИТ. Прогон проверки
    /// (<c>Writable == false</c>) — ровно то состояние, в котором отказ и возможен, и здесь он
    /// проверяется через НАСТОЯЩУЮ кнопку настоящего окна: щелчок зовёт домен, тот отказывает,
    /// и причина встаёт в строку состояния. Шов при этом не тронут.
    ///
    /// ⚠️ Папка на диске СУЩЕСТВУЕТ, и это не небрежность: с несуществующей папкой проверка
    /// держалась бы случайностью (отказ пришёл бы от «открывать нечего»), а с существующей она
    /// доказывает именно право. Проводник при этом не открывается даже при сломанном праве:
    /// в контроллере стоит счётчик вместо оболочки.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_в_окне_в_прогоне_проверки_называет_отказ_и_шов_не_зовут()
    {
        var root = NewRoot();
        var opener = new Opener();

        try
        {
            var folder = ExistingFolder(root);
            var controller = ReadOnly(root, folder);
            controller.FolderOpener = opener.Open;

            var window = new BackupWindow();
            window.Attach(controller);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                // Кнопка стоит ВПЛОТНУЮ к строке с путём: в том же месте карточки, что и путь.
                var button = window.FindControl<Button>("OpenFolderButton");
                var folderText = window.FindControl<TextBlock>("FolderText");

                Assert.NotNull(button);
                Assert.NotNull(folderText);
                Assert.Equal(PanelStrings.BackupOpenFolderButton, button!.Content);
                Assert.Equal(DisplayMask.Path(folder), folderText!.Text);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PanelTestStand.Settle();

                Assert.Empty(opener.Calls);
                Assert.Equal(PanelStrings.BackupFolderRefusedLog, window.Status);
                Assert.NotEqual(PanelStrings.SettingsFolderRefusedLog, window.Status);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// С ПРАВОМ КНОПКА ОТКРЫВАЕТ ПАПКУ КОПИЙ — и НИЧЕГО об этом в окне не пишет: то же решение,
    /// что у двери каталога настроек (замечание владельца 27.09.2026: *«Я думаю это лишнее»*).
    /// Проводник человек и так видит открытым, а строка о щелчке не отвечает ни на один вопрос.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_в_окне_с_правом_открывает_папку_копий_и_молчит_об_удаче()
    {
        var root = NewRoot();
        var opener = new Opener();

        try
        {
            var folder = ExistingFolder(root);
            var controller = WithOwner(root, folder);
            controller.FolderOpener = opener.Open;

            var window = new BackupWindow();
            window.Attach(controller);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                var button = window.FindControl<Button>("OpenFolderButton");
                Assert.NotNull(button);

                button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PanelTestStand.Settle();

                Assert.Equal(new[] { controller.Folder }, opener.Calls);
                Assert.Equal(string.Empty, window.Status);
                Assert.DoesNotContain(PanelStrings.BackupFolderOpenedLog, window.Status, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }
}
