using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// «ОТКРЫТЬ ФАЙЛ НАСТРОЕК» — ВТОРАЯ ДВЕРЬ ТОГО ЖЕ ВИДА, ЧТО ССЫЛКА АГЕНТА, И ПО ТОМУ ЖЕ УГОГОВОРУ:
/// право приходит ОТКРЫТЫМ параметром, отказ называет причину словами, шов не зовут без права,
/// а наружу не летит ни одного исключения.
///
/// Главное, что здесь доказывается, — **в прогоне проверки проводник на рабочем столе владельца
/// не открывается НИКОГДА**. Проверяется это швом (<c>opener</c>): настоящее открытие показало бы
/// человеку окно, поэтому проверка подставляет счётчик и требует, чтобы его НЕ ТРОНУЛИ.
///
/// ⚠️ **Путь здесь НЕПУСТОЙ и существующий намеренно** (в отличие от проверок ссылки агента):
/// пустым путём предохранитель проверялся бы случайностью — дверь не открыла бы проводник просто
/// потому, что открывать нечего, и «право не спрашивают» осталось бы непроверенным.
///
/// ⚠️ И второе, ради чего эта проверка заведена: у двери ДВА разных права и две разные причины
/// отказа. Слей их в одну — и один из двух предохранителей держался бы случайностью.
/// </summary>
public class SettingsFolderDoorTests
{
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

    /// <summary>Каталог, который «существует»: настоящий временный, а не выдуманный путь.</summary>
    private static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-settings-door", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// В ПРОГОНЕ ПРОВЕРКИ ШОВ ОТКРЫТИЯ НЕ ЗОВУТ ВОВСЕ, и причина названа словами: «каталог настроек
    /// не открываем: это прогон проверки». Право приходит значением <c>false</c> — так его и считает
    /// владелец настроек, у которого настройки в этом прогоне не его.
    /// </summary>
    [Fact]
    public void В_прогоне_проверки_проводник_не_открывается_и_шов_не_зовут()
    {
        var folder = TempFolder();
        var opener = new Opener();
        var log = new List<string>();

        var opened = AgentBrowser.TryOpenFolder(
            mayOpenFolder: false, folder, opener.Open, log.Add, out var error);

        Assert.False(opened);
        Assert.Empty(opener.Calls);
        Assert.Equal(PanelStrings.SettingsFolderRefusedLog, error);
        Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));
    }

    /// <summary>
    /// С ПРАВОМ открывается РОВНО каталог настроек и ровно один раз, а в журнал уходит строка
    /// об удаче. Путь не попадает ни в текст отказа, ни в лишние вызовы.
    /// </summary>
    [Fact]
    public void С_правом_открывается_каталог_настроек()
    {
        var folder = TempFolder();
        var opener = new Opener();
        var log = new List<string>();

        Directory.CreateDirectory(folder);

        try
        {
            var opened = AgentBrowser.TryOpenFolder(
                mayOpenFolder: true, folder, opener.Open, log.Add, out var error);

            Assert.True(opened);
            Assert.Equal(string.Empty, error);
            Assert.Equal(new[] { folder }, opener.Calls);
            Assert.Contains(log, line => line.Contains("по щелчку человека", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// ОТКРЫВАТЬ НЕЧЕГО — это НЕ «открылось»: пустой и отсутствующий путь дают честный отказ
    /// словами, и шов при этом не зовут. Окно покажет эти слова человеку в строке состояния.
    /// </summary>
    [Fact]
    public void Без_каталога_открывать_нечего()
    {
        var opener = new Opener();
        var log = new List<string>();

        foreach (var none in new[] { string.Empty, "   ", null })
        {
            var opened = AgentBrowser.TryOpenFolder(
                mayOpenFolder: true, none, opener.Open, log.Add, out var error);

            Assert.False(opened);
            Assert.Equal(PanelStrings.SettingsFolderMissing, error);
        }

        Assert.Empty(opener.Calls);
    }

    /// <summary>
    /// ОТКАЗ ОБОЛОЧКИ (нет проводника, снятая ассоциация каталогов) не роняет панель: причина
    /// уходит и в журнал, и вызывающему. Исключение из шва тоже не летит наружу — оно превращается
    /// в ту же строку отказа.
    /// </summary>
    [Fact]
    public void Отказ_оболочки_называется_словами_и_не_роняет_панель()
    {
        var opener = new Opener { Result = "Win32Exception — нет приложения для каталогов" };
        var log = new List<string>();

        var opened = AgentBrowser.TryOpenFolder(
            mayOpenFolder: true, TempFolder(), opener.Open, log.Add, out var error);

        Assert.False(opened);
        Assert.Contains("нет приложения", error, StringComparison.Ordinal);

        Assert.Contains(
            log,
            line => line.Contains("Открыть каталог настроек не удалось", StringComparison.Ordinal));

        var throwing = AgentBrowser.TryOpenFolder(
            mayOpenFolder: true,
            TempFolder(),
            _ => throw new InvalidOperationException("шов упал"),
            log.Add,
            out var thrown);

        Assert.False(throwing);
        Assert.Contains("шов упал", thrown, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВЛАДЕЛЕЦ НАСТРОЕК ПРОГОНА ПРОВЕРКИ ОТКАЗЫВАЕТ САМ: каталог открывает только тот прогон,
    /// КОТОРОМУ ЭТИ НАСТРОЙКИ ПРИНАДЛЕЖАТ. Прогон без права видит умолчания, а не настройки
    /// человека, — и открывать ему нечего.
    ///
    /// ⚠️ **Проверяется только отказ, и это намеренно:** у прогона С правом дверь пошла бы
    /// в настоящий проводник на рабочем столе владельца. Путь «с правом» проверен выше — на шве,
    /// где открытие подставлено.
    /// </summary>
    [Fact]
    public void Владелец_настроек_без_права_отказывает_и_ничего_не_открывает()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-settings-door", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Under(dir);

        try
        {
            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile), paths, allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Assert.False(controller.Writable);
            Assert.False(controller.OpenSettingsFolder(out var error));
            Assert.Equal(PanelStrings.SettingsFolderRefusedLog, error);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// УДАЧА В ОКНЕ НЕ ПОКАЗЫВАЕТСЯ НИЧЕМ — решение дирижёра по замечанию владельца 27.09.2026,
    /// который смотрел панель живьём: *«Я думаю это лишнее, видимо субагент добавил»*.
    ///
    /// Прежде в строке состояния вставала та же фраза, что уходит в журнал: «каталог настроек
    /// открыт по щелчку человека». Человеку она не отвечает ни на один его вопрос — проводник
    /// он и так видит открытым, — а строка журнала при этом остаётся: она полезна для разбора.
    ///
    /// ⚠️ Проверка сторожит ИМЕННО ЭТО: строка состояния пуста, и это НЕ строка журнала. Сверять
    /// со <see cref="PanelStrings.SettingsFolderOpenedLog"/> мало — важно, что в окне её нет.
    /// </summary>
    [AvaloniaFact]
    public void Открытие_каталога_не_оставляет_в_окне_строки_про_щелчок_человека()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-settings-door", Guid.NewGuid().ToString("N"));

        try
        {
            var window = new SettingsWindow();
            window.Attach(new OpeningControl(), autostart: null);
            window.Show();
            PanelTestStand.Settle();

            window.OpenSettingsFolder();
            PanelTestStand.Settle();

            var status = window.FindControl<TextBlock>("StatusText");

            Assert.NotNull(status);
            Assert.Equal(string.Empty, status!.Text ?? string.Empty);
            Assert.DoesNotContain(PanelStrings.SettingsFolderOpenedLog, status.Text ?? string.Empty, StringComparison.Ordinal);

            window.CloseQuietly();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Владелец настроек, который каталог «открывает» и НИЧЕГО не делает: настоящая дверь
    /// показала бы проводник на рабочем столе владельца. Здесь проверяется окно, а не оболочка,
    /// и подстановка нужна ровно затем, чтобы открытие было безвредным.
    /// </summary>
    private sealed class OpeningControl : ISettingsControl
    {
        public PanelSettings Settings => PanelSettings.Default;

        public bool Writable => true;

        public string LoadProblem => string.Empty;

        public string WorkDirInUse => string.Empty;

        public IReadOnlyList<WorkDirSuggestion> Suggestions() => Array.Empty<WorkDirSuggestion>();

        public bool Save(PanelSettings settings) => true;

        public bool SetBackupWithKeys(bool value) => true;

        public bool SetActiveAgent(string agentId) => true;


        public bool SetBackupKeyDirs(IReadOnlyList<string> directories) => true;

        public bool SetAdoptedServer(int port) => true;

        public bool SetWindowPlacement(int x, int y, int width, int height) => true;

        public void PreviewTheme(string theme)
        {
        }

        public EnvironmentReport EnvironmentInfo() => new(Array.Empty<EnvironmentGroup>());
        public string EnvironmentText() => string.Empty;

        /// <summary>«Открыл» — и ничего не тронул: проводник владельцу эта проверка не показывает.</summary>
        public bool OpenSettingsFolder(out string error)
        {
            error = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// ПОДМЕНА ВЛАДЕЛЬЦА НАСТРОЕК (её в проверках две) ПУТЕЙ НЕ ЗНАЕТ И ЧЕСТНО ОТКАЗЫВАЕТ: член
    /// добавлен в интерфейс С РЕАЛИЗАЦИЕЙ, и реализация по умолчанию не притворяется, что открыла
    /// каталог. Иначе подмена соврала бы человеку строкой в окне.
    /// </summary>
    [Fact]
    public void Подмена_владельца_настроек_честно_отказывает()
    {
        ISettingsControl control = new PathlessControl();

        Assert.False(control.OpenSettingsFolder(out var error));
        Assert.Equal(PanelStrings.SettingsFolderMissing, error);
    }

    /// <summary>
    /// ОКНО ПОКАЗЫВАЕТ ОТКАЗ СЛОВАМИ, А НЕ МОЛЧАНИЕМ КНОПКИ. Прогон проверки (<c>Writable == false</c>)
    /// — ровно то состояние, в котором отказ и возможен, и здесь он проверяется через НАСТОЯЩУЮ
    /// кнопку окна: щелчок зовёт владельца настроек, тот отказывает, и причина встаёт в строку
    /// состояния. Ничего при этом не открывается: право не выдано.
    ///
    /// ⚠️ **Каталога на диске НЕТ и не будет — и это не небрежность, а предохранитель.**
    /// Здесь работает НАСТОЯЩАЯ дверь (<c>AgentBrowser.OpenWithShell</c>), а не шов. Если однажды
    /// право в двери сломают (мутация, ошибка правки), щелчок дойдёт до оболочки — и открыл бы
    /// проводник на рабочем столе владельца. Несуществующий каталог закрывает и этот случай:
    /// оболочке нечего открывать, она отказывает. Ради этого путь и не создаётся.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_в_окне_называет_причину_отказа()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-settings-door", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Under(dir);

        try
        {
            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile), paths, allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);
            window.Show();
            PanelTestStand.Settle();

            // Состояние то самое, в котором отказ и возможен: каталога нет, права нет.
            Assert.False(Directory.Exists(dir));

            var open = window.FindControl<Button>("OpenFolderButton");
            Assert.NotNull(open);

            open!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            PanelTestStand.Settle();

            var status = window.FindControl<TextBlock>("StatusText");

            Assert.NotNull(status);
            Assert.Equal(PanelStrings.SettingsFolderRefusedLog, status!.Text);

            window.CloseQuietly();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Владелец настроек, который о каталоге ничего не знает: реализует интерфейс БЕЗ
    /// <c>OpenSettingsFolder</c> — то есть проверяет именно реализацию по умолчанию.
    /// </summary>
    private sealed class PathlessControl : ISettingsControl
    {
        public PanelSettings Settings => PanelSettings.Default;

        public bool Writable => false;

        public string LoadProblem => string.Empty;

        public string WorkDirInUse => string.Empty;

        public IReadOnlyList<WorkDirSuggestion> Suggestions() => Array.Empty<WorkDirSuggestion>();

        public bool Save(PanelSettings settings) => false;

        public bool SetBackupWithKeys(bool value) => false;

        public bool SetActiveAgent(string agentId) => false;


        public bool SetBackupKeyDirs(IReadOnlyList<string> directories) => false;

        public bool SetAdoptedServer(int port) => false;

        public bool SetWindowPlacement(int x, int y, int width, int height) => false;

        public void PreviewTheme(string theme)
        {
        }

        public EnvironmentReport EnvironmentInfo() => new(Array.Empty<EnvironmentGroup>());
        public string EnvironmentText() => string.Empty;
    }
}
