using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Agents;
using DshPanel.Autostart;
using DshPanel.Isolation;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки настроек и окна настроек.
///
/// Ни одна из них не трогает каталоги владельца: временный каталог свой, а файл прежней панели
/// (откуда берётся предложение рабочей папки) подставляется свой же. Настоящий `settings.json`
/// панели и настоящие настройки v1 читает только проба на собранном exe (`--settings-selftest`).
/// </summary>
public class SettingsTests
{
    /// <summary>Настройки в памяти: настоящий контроллер без файла и без применения темы к приложению.</summary>
    private sealed class FakeSettings : ISettingsControl
    {
        public FakeSettings(PanelSettings? settings = null, bool writable = true)
        {
            Settings = settings ?? PanelSettings.Default;
            Writable = writable;
        }

        public PanelSettings Settings { get; set; }

        public bool Writable { get; }

        public string LoadProblem { get; set; } = string.Empty;

        public string WorkDirInUse => PanelSettings.NormalizeWorkDir(Settings.ServerWorkingDir).Length > 0
            ? PanelSettings.NormalizeWorkDir(Settings.ServerWorkingDir)
            : @"C:\Панель\data";

        public PanelSettings? SavedSettings { get; private set; }

        public List<string> PreviewedThemes { get; } = new();

        public IReadOnlyList<WorkDirSuggestion> Suggest { get; set; } = Array.Empty<WorkDirSuggestion>();

        public bool SaveResult { get; set; } = true;

        public IReadOnlyList<WorkDirSuggestion> Suggestions() => Suggest;

        public bool Save(PanelSettings settings)
        {
            if (!Writable) return false;

            SavedSettings = settings;
            Settings = SettingsStore.Clean(settings);
            return SaveResult;
        }

        public void PreviewTheme(string theme) => PreviewedThemes.Add(theme);

        /// <summary>
        /// Отчёт окружения у подмены — ПУСТОЙ по путям, но с настоящими группами и подписями
        /// (собирается тем же разбором, что у панели). Так проверка видит ровно то, что ей нужно:
        /// группы доехали до окна, а выдуманных находок на пустом отчёте нет.
        /// </summary>
        public EnvironmentReport EnvironmentInfo() => EnvironmentReport.Build(
            engine: null,
            dshVersion: string.Empty,
            nodeVersion: string.Empty,
            npmVersion: string.Empty,
            pnpmVersion: string.Empty,
            paths: AppPaths.Under(@"C:\Проба\панель"),
            workDir: @"C:\Проба\работа",
            workDirConfigured: true,
            serverPort: 3080,
            serverText: PanelStrings.ServerNotStartedWithPort);

        public string EnvironmentText() => EnvironmentInfo().Text();

        /// <summary>
        /// Изменение одного решения — тем же путём, что и у настоящего контроллера: копия текущих
        /// настроек, правка одного поля, сохранение. Так заглушка не расходится с оригиналом
        /// в поведении, которое проверяется окнами.
        /// </summary>
        public bool SetBackupWithKeys(bool value)
        {
            var next = SettingsStore.Clean(Settings);
            next.BackupWithKeys = value;
            return Save(next);
        }

        public bool SetBackupKeyDirs(IReadOnlyList<string> directories)
        {
            var next = SettingsStore.Clean(Settings);
            next.BackupKeyDirs = PanelSettings.NormalizeKeyDirs(directories);
            return Save(next);
        }


        /// <summary>Согласие на найденный сервер — тем же путём: копия настроек, правка, сохранение.</summary>
        public bool SetAdoptedServer(int port)
        {
            var next = SettingsStore.Clean(Settings);
            next.AdoptedServerPort = PanelSettings.NormalizeAdoptedPort(port);
            return Save(next);
        }

        /// <summary>Размер и положение окна — тем же путём и по той же причине, что согласие.</summary>
        public bool SetWindowPlacement(int x, int y, int width, int height)
        {
            var next = SettingsStore.Clean(Settings);
            next.WindowX = x;
            next.WindowY = y;
            next.WindowWidth = width;
            next.WindowHeight = height;
            return Save(next);
        }

        /// <summary>Смена активного агента — тем же путём: копия настроек, правка поля, сохранение.</summary>
        public bool SetActiveAgent(string agentId)
        {
            var next = SettingsStore.Clean(Settings);
            next.ActiveAgent = agentId ?? string.Empty;
            return Save(next);
        }
    }

    /// <summary>Хранилище автозапуска в памяти — для проверок галочки в окне настроек.</summary>
    private sealed class FakeAutostartStore : IAutostartStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string Describe => "память";

        public bool Writable { get; init; } = true;

        public List<string> Written { get; } = new();
        public List<string> Deleted { get; } = new();

        public AutostartRecord Read(string name) =>
            _values.TryGetValue(name, out var value) ? new AutostartRecord(true, value) : AutostartRecord.Missing;

        public bool Write(string name, string value)
        {
            Written.Add(name);
            if (!Writable) return false;

            _values[name] = value;
            return true;
        }

        public bool Delete(string name)
        {
            Deleted.Add(name);
            if (!Writable) return false;

            _values.Remove(name);
            return true;
        }

        public void Seed(string name, string value) => _values[name] = value;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-settings-tests", Guid.NewGuid().ToString("N"));

    private static void RemoveTemp(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
        }
        catch
        {
            // Родитель занят другой проверкой — это не ошибка.
        }
    }

    // ---------------------------------------------------------------- хранилище

    /// <summary>
    /// Файл настроек — единственное место, где живут настройки. Проверяем запись и чтение
    /// целиком: и что значение вернулось, и что **метки BOM в файле нет** (её не терпит Node —
    /// на этом уже спотыкались в области), и что временного файла не осталось.
    /// </summary>
    [Fact]
    public void Настройки_пишутся_и_читаются_а_метки_BOM_в_файле_нет()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));

            Assert.False(store.Exists);
            Assert.True(store.Load().Ok);
            Assert.Equal(string.Empty, store.Load().Settings.ServerWorkingDir);

            var folder = Path.Combine(dir, "Моя папка");
            Assert.True(store.Save(new PanelSettings
            {
                ServerWorkingDir = folder + Path.DirectorySeparatorChar,
                Theme = PanelSettings.ThemeDark,
            }));

            var back = store.Load();
            Assert.True(back.Ok);
            Assert.Equal(PanelSettings.ThemeDark, back.Settings.Theme);
            Assert.Equal(folder, back.Settings.ServerWorkingDir);

            var bytes = File.ReadAllBytes(store.Describe);
            Assert.True(bytes.Length >= 3);
            Assert.False(
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "файл настроек записан с BOM: Node такой JSON не разберёт");

            Assert.False(File.Exists(store.Describe + ".tmp"), "остался временный файл записи");

            // Вторая запись не ломает первую: файл перезаписывается целиком.
            Assert.True(store.Save(new PanelSettings { Theme = PanelSettings.ThemeLight }));
            Assert.Equal(PanelSettings.ThemeLight, store.Load().Settings.Theme);
            Assert.Equal(string.Empty, store.Load().Settings.ServerWorkingDir);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Файл настроек человек может править руками — и испортить. Панель обязана это пережить:
    /// умолчания плюс честная причина, а не исключение при старте.
    /// </summary>
    [Fact]
    public void Испорченный_файл_даёт_умолчания_и_честную_причину()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            Directory.CreateDirectory(dir);

            File.WriteAllText(path, "{ это не json", new UTF8Encoding(false));
            var broken = store.Load();

            Assert.False(broken.Ok);
            Assert.NotEqual(string.Empty, broken.Problem);
            Assert.Equal(PanelSettings.ThemeSystem, broken.Settings.Theme);

            // Неизвестная тема — тоже «как в Windows», а не падение.
            File.WriteAllText(path, "{\"theme\":\"неон\",\"serverWorkingDir\":\"  \"}", new UTF8Encoding(false));
            var weird = store.Load();

            Assert.True(weird.Ok);
            Assert.Equal(PanelSettings.ThemeSystem, weird.Settings.Theme);
            Assert.Equal(string.Empty, weird.Settings.ServerWorkingDir);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    [Fact]
    public void Тема_перебирается_по_номерам_и_неизвестная_даёт_умолчание()
    {
        Assert.Equal(0, PanelSettings.ThemeIndex(PanelSettings.ThemeSystem));
        Assert.Equal(1, PanelSettings.ThemeIndex(PanelSettings.ThemeLight));
        Assert.Equal(2, PanelSettings.ThemeIndex(PanelSettings.ThemeDark));

        // Мусор — «как в Windows», а не исключение: значение приходит из файла, который правят руками.
        Assert.Equal(0, PanelSettings.ThemeIndex("неон"));
        Assert.Equal(0, PanelSettings.ThemeIndex(null));
        Assert.Equal(PanelSettings.ThemeSystem, PanelSettings.ThemeAt(-1));
        Assert.Equal(PanelSettings.ThemeSystem, PanelSettings.ThemeAt(99));

        foreach (var value in PanelSettings.ThemeValues)
            Assert.Equal(value, PanelSettings.ThemeAt(PanelSettings.ThemeIndex(value)));

        Assert.Equal(PanelTheme.Light, PanelSettings.ToTheme(PanelSettings.ThemeLight));
        Assert.Equal(PanelTheme.Dark, PanelSettings.ToTheme("DARK"));
        Assert.Equal(PanelTheme.System, PanelSettings.ToTheme(""));
        Assert.Equal(PanelSettings.ThemeDark, PanelSettings.ThemeValue(PanelTheme.Dark));
    }

    // ---------------------------------------------------------------- право

    /// <summary>
    /// Право читать и писать настройки — то же правило, что у автозапуска: изолированный прогон
    /// работает со своим корнем, человек — со своим каталогом, а прогон проверки не читает
    /// и не меняет ничего. Иначе рабочая папка человека уехала бы в кадр и в отчёт.
    /// </summary>
    [Fact]
    public void Право_на_настройки_есть_у_изоляции_и_у_человека_и_нет_у_прогона_проверки()
    {
        var root = TempDir();
        var isolated = RunContext.Create(
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, _ => null));
        var owner = RunContext.Create(RunRequest.Owner());

        Assert.True(SettingsController.For(isolated, humanLaunch: false));
        Assert.True(SettingsController.For(isolated, humanLaunch: true));
        Assert.True(SettingsController.For(owner, humanLaunch: true));
        Assert.False(SettingsController.For(owner, humanLaunch: false));
    }

    [Fact]
    public void Прогон_проверки_видит_умолчания_и_сохранить_не_может()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new PanelSettings { ServerWorkingDir = @"C:\Работа", Theme = PanelSettings.ThemeDark });

            var log = new List<string>();
            var controller = new SettingsController(
                store, AppPaths.Under(dir), allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: log.Add);

            Assert.False(controller.Writable);
            Assert.Equal(string.Empty, controller.Settings.ServerWorkingDir);
            Assert.Equal(PanelSettings.ThemeSystem, controller.Settings.Theme);
            Assert.NotEqual(string.Empty, controller.LoadProblem);
            Assert.False(controller.Save(new PanelSettings { Theme = PanelSettings.ThemeDark }));

            Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));
            Assert.Equal(PanelSettings.ThemeDark, store.Load().Settings.Theme);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- контроллер

    /// <summary>
    /// Тема применяется сразу при чтении и при сохранении, а рабочая папка берётся из настроек.
    /// Пустая настройка означает папку панели — умолчание v1, и оно остаётся умолчанием 2.0.
    /// </summary>
    [Fact]
    public void Контроллер_применяет_тему_и_берёт_рабочую_папку_из_настроек()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            var applied = new List<PanelTheme>();
            var log = new List<string>();

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: applied.Add, log: log.Add);

            // Тема применяется при старте: иначе человек видел бы не свою тему до первого
            // открытия настроек, а панель в трее живёт без окна месяцами.
            Assert.Single(applied);
            Assert.Equal(PanelTheme.System, applied[0]);

            Assert.Equal(paths.DataDir, controller.WorkDirInUse);

            Assert.True(controller.Save(new PanelSettings
            {
                ServerWorkingDir = paths.BackupsDir,
                Theme = PanelSettings.ThemeLight,
            }));

            Assert.Equal(paths.BackupsDir, controller.WorkDirInUse);
            Assert.Equal(2, applied.Count);
            Assert.Equal(PanelTheme.Light, applied[1]);

            // Сохранённое переживает новое чтение: это и есть «настройка сохранилась».
            var again = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Assert.Equal(paths.BackupsDir, again.WorkDirInUse);
            Assert.Equal(PanelSettings.ThemeLight, again.Settings.Theme);
            Assert.Contains(log, line => line.Contains("настройки сохранены", StringComparison.Ordinal));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Предложение рабочей папки: берём у прежней панели, и **только** когда её читать можно.
    /// Изолированный прогон и прогон проверки туда не смотрят — там путь, который настроил человек.
    /// </summary>
    [Fact]
    public void Предложение_рабочей_папки_берётся_у_прежней_панели_и_только_с_правом()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var previous = Path.Combine(dir, "v1-settings.json");
            var folder = Path.Combine(dir, "Работа");

            File.WriteAllText(
                previous,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["serverWorkingDir"] = folder }),
                new UTF8Encoding(false));

            var found = WorkDirSuggestions.Find(mayReadOwnerEnvironment: true, previous, _ => true);
            Assert.Single(found);
            Assert.Equal(folder, found[0].Path);
            Assert.Equal(PanelStrings.WorkDirSourcePreviousPanel, found[0].Source);

            // Без права не читаем вовсе — даже если файл на месте.
            Assert.Empty(WorkDirSuggestions.Find(mayReadOwnerEnvironment: false, previous, _ => true));

            // Каталога нет — предлагать нечего.
            Assert.Empty(WorkDirSuggestions.Find(mayReadOwnerEnvironment: true, previous, _ => false));

            // Мусор в файле прежней панели не роняет панель.
            File.WriteAllText(previous, "не json", new UTF8Encoding(false));
            Assert.Empty(WorkDirSuggestions.Find(mayReadOwnerEnvironment: true, previous, _ => true));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>Действующая рабочая папка не предлагается: это шум, а не забота.</summary>
    [Fact]
    public void Действующая_рабочая_папка_не_предлагается()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Assert.True(controller.Save(new PanelSettings { ServerWorkingDir = paths.BackupsDir }));

            // Контроллер смотрит на НАСТОЯЩИЙ файл прежней панели, а он на этой машине может
            // быть, а может и не быть: проверяем само правило — уже выбранная папка не в списке.
            foreach (var suggestion in controller.Suggestions())
            {
                Assert.False(
                    string.Equals(
                        Path.TrimEndingDirectorySeparator(paths.BackupsDir),
                        Path.TrimEndingDirectorySeparator(suggestion.Path),
                        StringComparison.OrdinalIgnoreCase),
                    "уже выбранная рабочая папка попала в предложения");
            }
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>Путь человека в показе маскируется: кадр и журнал уезжают наружу.</summary>
    [Fact]
    public void Путь_человека_маскируется_в_показе()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var masked = DisplayMask.Path(Path.Combine(profile, "Заметки"));
        Assert.StartsWith("~", masked);
        Assert.DoesNotContain(profile, masked, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("~", DisplayMask.Path(profile));
        Assert.Equal(@"D:\Работа", DisplayMask.Path(@"D:\Работа"));
        Assert.DoesNotContain("Другой", DisplayMask.Path(@"C:\Users\Другой\папка"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, DisplayMask.Path(null));
    }

    // ---------------------------------------------------------------- окно настроек

    [AvaloniaFact]
    public void Окно_настроек_показывает_рабочую_папку_тему_и_окружение()
    {
        var settings = new FakeSettings(new PanelSettings
        {
            ServerWorkingDir = @"D:\Работа",
            Theme = PanelSettings.ThemeDark,
        })
        {
            Suggest = new[] { new WorkDirSuggestion(@"C:\Harness", PanelStrings.WorkDirSourcePreviousPanel) },
        };

        var window = new SettingsWindow();
        window.Attach(settings, autostart: null);

        var workDir = window.FindControl<TextBox>("WorkDirBox");
        var theme = window.FindControl<ComboBox>("ThemeBox");
        var environment = window.FindControl<StackPanel>("EnvironmentPanel");
        var suggest = window.FindControl<Button>("SuggestWorkDirButton");

        Assert.NotNull(workDir);
        Assert.NotNull(theme);
        Assert.NotNull(environment);
        Assert.NotNull(suggest);

        Assert.Equal(@"D:\Работа", workDir!.Text);
        Assert.Equal(PanelSettings.ThemeIndex(PanelSettings.ThemeDark), theme!.SelectedIndex);
        Assert.Equal(3, theme.ItemCount);

        // ОТЧЁТ ОКРУЖЕНИЯ — ГРУППАМИ, а не абзацем (просьба владельца 28.09.2026): у раздела
        // «Сервер» три группы, и в каждой свои строки. Прежней одной строки текста больше нет.
        Assert.NotEmpty(environment!.Children);
        Assert.Equal(3, environment.Children.Count);

        var groups = environment.Children.OfType<StackPanel>().Select(group =>
            group.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty).ToList();

        Assert.Contains(PanelStrings.EnvGroupSetup, groups);
        Assert.Contains(PanelStrings.EnvGroupRuntime, groups);
        Assert.Contains(PanelStrings.EnvGroupDsh, groups);

        Assert.True(suggest!.IsVisible, "найденная рабочая папка обязана быть видна как предложение");
    }

    /// <summary>Кнопка «Сохранить» кладёт в файл то, что стоит в полях, а не то, что было при открытии.</summary>
    [AvaloniaFact]
    public void Сохранение_в_окне_пишет_настройки_из_полей()
    {
        var settings = new FakeSettings();
        var window = new SettingsWindow();
        window.Attach(settings, autostart: null);

        window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
        window.FindControl<ComboBox>("ThemeBox")!.SelectedIndex = PanelSettings.ThemeIndex(PanelSettings.ThemeDark);

        // Выбор темы применяется СРАЗУ, ещё до кнопки: человек выбирает её глазами.
        Assert.Contains(PanelSettings.ThemeDark, settings.PreviewedThemes);

        window.FindControl<Button>("SaveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(settings.SavedSettings);
        Assert.Equal(@"D:\Новая папка", settings.SavedSettings!.ServerWorkingDir);
        Assert.Equal(PanelSettings.ThemeDark, settings.SavedSettings.Theme);
        Assert.True(window.Saved);
        Assert.Equal(PanelStrings.SettingsSaved, window.FindControl<TextBlock>("StatusText")!.Text);
    }

    /// <summary>
    /// В прогоне проверки окно обязано быть честным: показать, что настройки не читаются,
    /// и не притворяться, что сохраняет.
    /// </summary>
    [AvaloniaFact]
    public void Окно_настроек_в_прогоне_проверки_говорит_что_настройки_не_читаются()
    {
        var window = new SettingsWindow();
        window.Attach(new FakeSettings(writable: false) { LoadProblem = PanelStrings.SettingsReadOnlyNote }, null);

        window.FindControl<Button>("SaveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.False(window.Saved);
        Assert.Equal(PanelStrings.SettingsReadOnlyNote, window.FindControl<TextBlock>("StatusText")!.Text);
    }

    // --- галочка автозапуска: она переехала сюда из главного окна 24.09.2026 -----------------

    /// <summary>
    /// Подстановка состояния не должна выглядеть нажатием человека — иначе панель записала бы
    /// в реестр то, что только что прочитала.
    ///
    /// Запись нарочно СУЩЕСТВУЕТ к моменту привязки: окно ставит галочку из пустого состояния,
    /// и без предохранителя это подняло бы событие «человек поставил галочку». Проверка,
    /// начинавшаяся с пустой записи, этого пути не задевала вовсе — нашла мутация 24.09.2026.
    /// </summary>
    [AvaloniaFact]
    public void Галочка_автозапуска_в_настройках_и_подстановка_не_считается_нажатием()
    {
        var self = Environment.ProcessPath!;
        var store = new FakeAutostartStore();
        store.Seed(AutostartDecisions.ValueName, AutostartDecisions.BuildValue(self));

        var autostart = new AutostartController(store, self, _ => { });

        var window = new SettingsWindow();
        window.Attach(new FakeSettings(), autostart);

        var check = window.FindControl<CheckBox>("AutostartCheck");
        var note = window.FindControl<TextBlock>("AutostartNoteText");

        Assert.NotNull(check);
        Assert.NotNull(note);

        Assert.Equal(PanelStrings.AutostartCheck, check!.Content);
        Assert.True(check.IsChecked);
        Assert.True(check.IsEnabled);
        Assert.Contains("Автозапуск включён", note!.Text!, StringComparison.Ordinal);

        Assert.Empty(store.Written);
        Assert.Empty(store.Deleted);

        check.IsChecked = false;
        Assert.Equal(new[] { AutostartDecisions.ValueName }, store.Deleted);
        Assert.False(check.IsChecked);

        check.IsChecked = true;
        Assert.Equal(new[] { AutostartDecisions.ValueName }, store.Written);
        Assert.True(check.IsChecked);
        Assert.Contains("Автозапуск включён", note.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void В_прогоне_проверки_галочка_автозапуска_недоступна()
    {
        var inner = new FakeAutostartStore();
        inner.Seed(AutostartDecisions.ValueName, "\"" + Environment.ProcessPath + "\"");

        var window = new SettingsWindow();
        window.Attach(new FakeSettings(), new AutostartController(
            new ReadOnlyAutostartStore(inner), Environment.ProcessPath!, _ => { }));

        var check = window.FindControl<CheckBox>("AutostartCheck");
        var note = window.FindControl<TextBlock>("AutostartNoteText");

        Assert.True(check!.IsChecked);
        Assert.False(check.IsEnabled);
        Assert.Contains(PanelStrings.AutostartLockedSuffix, note!.Text!, StringComparison.Ordinal);
        Assert.Empty(inner.Written);
    }

    /// <summary>Окно настроек без автозапуска (так его снимает часть проверок) обязано быть честным.</summary>
    [AvaloniaFact]
    public void Настройки_без_автозапуска_говорят_что_не_подключён()
    {
        var window = new SettingsWindow();
        window.Attach(new FakeSettings(), autostart: null);

        var check = window.FindControl<CheckBox>("AutostartCheck");
        var note = window.FindControl<TextBlock>("AutostartNoteText");

        Assert.Equal(PanelStrings.AutostartUnbound, note!.Text);
        Assert.False(check!.IsChecked);
        Assert.False(check.IsEnabled);
    }

    // ---------------------------------------------------------------- порт своего сервера

    /// <summary>
    /// Умолчание порта — 3080, тот же адрес, на котором стояла панель 1.x: решение владельца
    /// 26.09.2026 («убираю 1.x, 2.0 встаёт на её место»). Проверка сторожит и обратное:
    /// <c>RunFallbackPort</c> — ДРУГОЕ число (3081), иначе приведение порта без права вернуло бы
    /// 3080 и прогон проверки занял бы порт владельца.
    /// </summary>
    [Fact]
    public void Умолчание_порта_3080_как_у_прежней_панели()
    {
        Assert.Equal(3080, ServerDecisions.OwnerPort);
        Assert.Equal(3080, ServerDecisions.DefaultServerPort);
        Assert.Equal(3080, PanelSettings.Default.ServerPort);
        Assert.Equal(3081, ServerDecisions.RunFallbackPort);
        Assert.NotEqual(ServerDecisions.DefaultServerPort, ServerDecisions.RunFallbackPort);

        // И в файл по умолчанию уходит именно он.
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.True(store.Save(PanelSettings.Default));
            Assert.Equal(3080, store.Load().Settings.ServerPort);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Порт_переживает_сохранение_и_чтение()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));

            Assert.True(store.Save(new PanelSettings { ServerPort = 3097, ServerPortChosen = true }));

            var load = store.Load();
            Assert.True(load.Ok);
            Assert.Equal(3097, load.Settings.ServerPort);
            Assert.True(load.Settings.ServerPortChosen);
            Assert.Equal(string.Empty, load.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// ПЕРЕНОС СТАРОГО УМОЛЧАНИЯ. До 26.09.2026 умолчанием 2.0 был 3081, и у всех, кто уже
    /// поставил 2.0, это значение лежит в файле — но человек его НЕ выбирал. Признак
    /// <c>serverPortChosen</c> не выставлен и в файле ровно 3081 — значит порт переводится
    /// на новое умолчание 3080, и человеку об этом СКАЗАНО: молчаливая подмена порта — это он,
    /// ищущий свой сервер не там.
    /// </summary>
    [Fact]
    public void Старое_умолчание_3081_переводится_на_3080_и_об_этом_сказано()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"serverPort\":3081}", new UTF8Encoding(false));

            var load = new SettingsStore(path).Load();

            Assert.True(load.Ok);
            Assert.Equal(ServerDecisions.DefaultServerPort, load.Settings.ServerPort);
            Assert.Contains("3081", load.Problem);
            Assert.Contains("3080", load.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// А вот ВЫБРАННЫЙ человеком порт панель не трогает — даже если он равен прежнему умолчанию.
    /// Отличить «не выбирал» от «выбрал 3081» ничем, кроме признака, нельзя; без этой проверки
    /// перенос старого умолчания переписывал бы выбор человека при КАЖДОМ чтении файла.
    /// </summary>
    [Fact]
    public void Порт_выбранный_человеком_панель_не_меняет()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "settings.json");

            File.WriteAllText(
                path, "{\"serverPort\":3081,\"serverPortChosen\":true}", new UTF8Encoding(false));

            var chosen = new SettingsStore(path).Load();

            Assert.True(chosen.Ok);
            Assert.Equal(3081, chosen.Settings.ServerPort);
            Assert.Equal(string.Empty, chosen.Problem);

            // Порт владельца в файле теперь ЗАКОНЕН: он и есть умолчание, и человек вправе
            // записать его руками. Запрет остался на ЗАНЯТИЕ — и живёт в контроллере сервера
            // (проверки «Порт_владельца_без_права_приводится_к_запасному» в ServerTests).
            File.WriteAllText(
                path, "{\"serverPort\":3080,\"serverPortChosen\":true}", new UTF8Encoding(false));

            var owner = new SettingsStore(path).Load();

            Assert.Equal(ServerDecisions.OwnerPort, owner.Settings.ServerPort);
            Assert.Equal(string.Empty, owner.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Мусор в порте приводится к умолчанию — и это по-прежнему 3080. Число вне 1…65535 панель
    /// занять не может, и «оставить как есть» значило бы не поднять сервер вовсе.
    /// </summary>
    [Fact]
    public void Мусор_в_порте_приводится_к_умолчанию_3080()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "settings.json");

            foreach (var junk in new[] { "0", "-1", "70000" })
            {
                File.WriteAllText(
                    path,
                    "{\"serverPort\":" + junk + ",\"serverPortChosen\":true}",
                    new UTF8Encoding(false));

                Assert.Equal(ServerDecisions.DefaultServerPort, new SettingsStore(path).Load().Settings.ServerPort);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Сохранение НЕ сбрасывает признак «порт выбран человеком»: сбрось его дверь настроек —
    /// и перенос старого умолчания срабатывал бы заново при каждом чтении файла.
    /// </summary>
    [Fact]
    public void Сохранение_не_сбрасывает_признак_выбранного_порта()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);

            Assert.True(store.Save(new PanelSettings
            {
                ServerPort = ServerDecisions.RunFallbackPort,
                ServerPortChosen = true,
            }));

            Assert.Contains("serverPortChosen", File.ReadAllText(path), StringComparison.Ordinal);

            var load = store.Load();

            // Перенос НЕ сработал: признак пережил и запись, и чтение.
            Assert.Equal(ServerDecisions.RunFallbackPort, load.Settings.ServerPort);
            Assert.Equal(string.Empty, load.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- запомненное согласие на сервер

    /// <summary>
    /// Порт владельца (3080) в согласии — ЗАКОНЕН, и это не мелочь: именно на 3080 стоит сервер
    /// владельца, который панель и берёт под управление. Примени здесь запрет «не занимать порт
    /// владельца» — и главный случай встраивания молча перестал бы работать: панель спрашивала бы
    /// снова и снова, а человек не понимал бы почему.
    /// </summary>
    [Fact]
    public void Согласие_на_порт_владельца_законно_а_мусор_становится_нулём()
    {
        Assert.Equal(3080, PanelSettings.NormalizeAdoptedPort(ServerDecisions.OwnerPort));
        Assert.Equal(3097, PanelSettings.NormalizeAdoptedPort(3097));

        // Порт СВОЕГО сервера — ДРУГОЕ решение и другой предикат: 3080 в файле законен (он и есть
        // умолчание), а вот ЗАНЯТЬ его без права нельзя, и приведение порта без права даёт 3081.
        Assert.Equal(ServerDecisions.DefaultServerPort, ServerDecisions.NormalizePort(
            ServerDecisions.OwnerPort, mayOccupyOwnerPort: true));
        Assert.Equal(ServerDecisions.RunFallbackPort, ServerDecisions.NormalizePort(
            ServerDecisions.OwnerPort, mayOccupyOwnerPort: false));

        // «Согласия нет» — это 0, и всё непонятное сводится к нему.
        foreach (var bad in new[] { 0, -1, -3080, 65536, 100000, int.MinValue, int.MaxValue })
            Assert.Equal(0, PanelSettings.NormalizeAdoptedPort(bad));
    }

    /// <summary>
    /// Согласие переживает запись и чтение файла — иначе оно не пережило бы перезапуск панели,
    /// а ради этого оно в файл и положено.
    /// </summary>
    [Fact]
    public void Согласие_на_сервер_переживает_сохранение_и_чтение()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));

            Assert.True(store.Save(new PanelSettings { AdoptedServerPort = ServerDecisions.OwnerPort }));

            var load = store.Load();
            Assert.True(load.Ok);
            Assert.Equal(ServerDecisions.OwnerPort, load.Settings.AdoptedServerPort);

            // Свой ключ панель знает: замечаний «ключ не применён» быть не должно.
            Assert.Equal(string.Empty, load.Problem);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>Мусор в поле согласия (файл правят руками) читается как «согласия нет», а не как порт.</summary>
    [Fact]
    public void Мусор_в_согласии_читается_как_отсутствие_согласия()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"adoptedServerPort\":70000}", new UTF8Encoding(false));

            var load = new SettingsStore(path).Load();

            Assert.True(load.Ok);
            Assert.Equal(0, load.Settings.AdoptedServerPort);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Согласие записывается тем же путём, что и остальные настройки, и ЗАБЫВАЕТСЯ значением 0
    /// («Отвязаться»). В прогоне без права писать настройки оно не запоминается вовсе — и это
    /// честно: панель тогда спрашивает подтверждение, как раньше, а в журнал НЕ попадает строка
    /// «запомнил», которой не было бы правдой.
    /// </summary>
    [Fact]
    public void Контроллер_запоминает_и_забывает_согласие_а_без_права_не_может()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            var log = new List<string>();

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: log.Add);

            Assert.True(controller.SetAdoptedServer(ServerDecisions.OwnerPort));
            Assert.Equal(ServerDecisions.OwnerPort, controller.Settings.AdoptedServerPort);
            Assert.Equal(ServerDecisions.OwnerPort, store.Load().Settings.AdoptedServerPort);
            Assert.Contains(log, line => line.Contains("запомнил согласие", StringComparison.Ordinal));

            Assert.True(controller.SetAdoptedServer(0));
            Assert.Equal(0, controller.Settings.AdoptedServerPort);
            Assert.Equal(0, store.Load().Settings.AdoptedServerPort);
            Assert.Contains(log, line => line.Contains("забыл согласие", StringComparison.Ordinal));

            // Тот же вызов в прогоне БЕЗ права писать настройки: отказ и ни слова о запоминании.
            var lockedLog = new List<string>();
            var locked = new SettingsController(
                store, paths, allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: lockedLog.Add);

            Assert.False(locked.SetAdoptedServer(ServerDecisions.OwnerPort));
            Assert.Equal(0, locked.Settings.AdoptedServerPort);
            Assert.Equal(0, store.Load().Settings.AdoptedServerPort);
            Assert.Contains(lockedLog, line => line.Contains("прогон проверки", StringComparison.Ordinal));
            Assert.DoesNotContain(lockedLog, line => line.Contains("запомнил согласие", StringComparison.Ordinal));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОХРАНЕНИЕ НАСТРОЕК ИЗ ОКНА НЕ ЗАБЫВАЕТ СОГЛАСИЕ. Окно пишет настройки ЦЕЛИКОМ, из своих
    /// полей, а согласие на найденный сервер в нём не показывается — значит поле, которого окно
    /// не знает, обнулялось бы каждым нажатием «Сохранить»: человек молча терял бы своё «да»,
    /// и панель начинала бы спрашивать заново (решение владельца 26.09.2026 — «спросить ОДИН раз»).
    /// Нашла приёмка 26.09.2026: проверялось хранилище и контроллер, а окно — нет.
    /// </summary>
    [AvaloniaFact]
    public void Сохранение_из_окна_не_забывает_согласие()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Assert.True(controller.SetAdoptedServer(ServerDecisions.OwnerPort));

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);

            // Человек поменял настройки и нажал «Сохранить»: согласие обязано уцелеть и в файле,
            // и в действующих настройках — панель читает согласие оттуда же, откуда и всё остальное.
            Assert.Equal(ServerDecisions.OwnerPort, window.PendingSettings().AdoptedServerPort);
            Assert.True(controller.Save(window.PendingSettings()));

            Assert.Equal(ServerDecisions.OwnerPort, store.Load().Settings.AdoptedServerPort);
            Assert.Equal(ServerDecisions.OwnerPort, controller.Settings.AdoptedServerPort);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Окно настроек показывает ТОТ порт, который панель займёт, и о неподходящем значении
    /// говорит сразу, как только человек его набрал, — а не после сохранения.
    ///
    /// ⚠️ Про 3080 здесь НЕТ запрета, и это решение владельца 26.09.2026: 2.0 встаёт на место 1.x,
    /// и порт владельца стал обычным умолчанием. Запрет остался на ЗАНЯТИЕ и живёт в контроллере
    /// сервера: прогон проверки займёт 3081, а не 3080.
    /// </summary>
    [AvaloniaFact]
    public void Окно_настроек_говорит_о_неподходящем_порте_сразу()
    {
        var window = new SettingsWindow();
        window.Attach(
            new FakeSettings(new PanelSettings { ServerPort = ServerDecisions.OwnerPort }),
            autostart: null);

        var box = window.FindControl<NumericUpDown>("PortBox");
        var hint = window.FindControl<TextBlock>("PortHintText");

        Assert.NotNull(box);
        Assert.NotNull(hint);

        // Порт владельца — обычное значение, и подсказка общая: он и есть умолчание.
        Assert.Equal(3080m, box!.Value);
        Assert.Equal(PanelStrings.SettingsPortHint, hint!.Text);

        // Человек набрал число, которого портом не бывает, — сказано сразу и назван тот порт,
        // который будет взят.
        box.Value = 70000;

        Assert.Contains("70000", hint.Text);
        Assert.Contains(ServerDecisions.DefaultServerPort.ToString(), hint.Text);

        // И сохраняется допустимый порт: обещание подсказки исполняется тем же нажатием.
        Assert.Equal(ServerDecisions.DefaultServerPort, window.PendingSettings().ServerPort);

        // Обычный порт подсказку не портит.
        box.Value = 3097;
        Assert.Equal(PanelStrings.SettingsPortHint, hint.Text);
        Assert.Equal(3097, window.PendingSettings().ServerPort);
    }

    /// <summary>
    /// «Сохранить» в окне настроек ПОМЕЧАЕТ порт выбранным. Без этого признака панель считала бы
    /// 3081 в файле остатком прежнего умолчания и переводила бы его на 3080 при каждом чтении,
    /// то есть переписывала бы выбор человека руками.
    /// </summary>
    [AvaloniaFact]
    public void Сохранение_из_окна_помечает_порт_выбранным()
    {
        var window = new SettingsWindow();
        window.Attach(new FakeSettings(PanelSettings.Default), autostart: null);

        var box = window.FindControl<NumericUpDown>("PortBox");
        Assert.NotNull(box);

        box!.Value = ServerDecisions.RunFallbackPort;

        Assert.True(window.PendingSettings().ServerPortChosen);
        Assert.Equal(ServerDecisions.RunFallbackPort, window.PendingSettings().ServerPort);
    }

    // ---------------------------------------------------------------- главное окно и дверь

    /// <summary>
    /// Главное окно — про состояние, и настроек в нём нет (решение владельца 24.09.2026).
    /// Дверь в них — кнопка, и она обязана сообщать о просьбе, а не открывать окно сама:
    /// окно панели не знает, где живут настройки.
    /// </summary>
    [AvaloniaFact]
    public void Главное_окно_не_содержит_настроек_а_просит_их_открыть()
    {
        var window = new MainWindow();

        Assert.Null(window.FindControl<CheckBox>("AutostartCheck"));

        var button = window.FindControl<Button>("SettingsButton");
        Assert.NotNull(button);
        Assert.Equal(PanelStrings.SettingsMenuText, PanelTestStand.Label(button!));

        var asked = 0;
        window.SettingsRequested += () => asked++;

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// Второго окна настроек не бывает: повторная просьба выводит на передний план уже открытое.
    /// Иначе человек, щёлкнувший дважды, получил бы два окна с одними и теми же полями.
    /// </summary>
    [AvaloniaFact]
    public void Второе_окно_настроек_не_заводится()
    {
        var slot = new SingleWindowSlot();
        var created = 0;

        Window Create()
        {
            created++;
            return new MainWindow();
        }

        Assert.True(slot.Open(Create));
        Assert.Equal(1, created);
        Assert.True(slot.IsOpen);

        Assert.False(slot.Open(Create));
        Assert.Equal(1, created);

        // Человек закрыл окно — следующая просьба обязана открыть новое, а не «вывести»
        // на передний план то, которого уже нет.
        slot.Close();
        Assert.False(slot.IsOpen);

        Assert.True(slot.Open(Create));
        Assert.Equal(2, created);
    }

    // ---------------------------------------------------------------- сбой темы (дефект Д4)

    /// <summary>
    /// СБОЙ ПРИМЕНЕНИЯ ТЕМЫ НЕ ОТМЕНЯЕТ СОХРАНЕНИЕ. Живой случай владельца 26.09.2026 в 23:10:12:
    /// встраивание в найденный сервер шло в ФОНОВОЙ нитке, применение темы упало
    /// («The calling thread cannot access this object because a different thread owns it»),
    /// и в журнал легло ложное «запомнить согласие не удалось» — притом что файл настроек был
    /// УЖЕ записан и согласие действовало.
    ///
    /// На старом коде эта проверка падала: исключение шло наружу из <c>Save</c>, и он не успевал
    /// вернуть <c>true</c>. Доказано подменой — в отчёте.
    /// </summary>
    [Fact]
    public void Сбой_темы_не_отменяет_сохранение_и_говорит_своей_строкой()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            var log = new List<string>();

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null,
                applyTheme: _ => throw new InvalidOperationException(
                    "The calling thread cannot access this object because a different thread owns it."),
                log: log.Add);

            // Наружу ничего не летит, и запись честно названа удачной: настройки УЖЕ на диске.
            Assert.True(controller.Save(new PanelSettings { Theme = PanelSettings.ThemeDark }));

            Assert.Contains(log, line => line.Contains("настройки сохранены", StringComparison.Ordinal));
            Assert.Contains(log, line => line.Contains("тему применить не удалось", StringComparison.Ordinal));
            Assert.Contains(log, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));

            // И НИ СЛОВА о провале: «сохранить не удалось» и «прогон проверки» тут были бы ложью.
            Assert.DoesNotContain(log, line => line.Contains("сохранить не удалось", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));

            Assert.Equal(PanelSettings.ThemeDark, store.Load().Settings.Theme);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- размер и положение окна

    /// <summary>
    /// Геометрия главного окна переживает запись и чтение — ради этого она в файл и положена:
    /// человек поставил и растянул окно, и на следующем запуске оно обязано открыться там же
    /// (решение владельца 26.09.2026, пункт 3 дефекта Д2).
    /// </summary>
    [Fact]
    public void Геометрия_окна_переживает_сохранение_и_чтение()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            var log = new List<string>();

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: log.Add);

            // Отрицательная координата — ЗАКОННАЯ: второй монитор слева.
            Assert.True(controller.SetWindowPlacement(-1600, 200, 900, 700));

            Assert.Equal(-1600, controller.Settings.WindowX);
            Assert.Equal(900, controller.Settings.WindowWidth);

            var load = store.Load();
            Assert.True(load.Ok);
            Assert.Equal(-1600, load.Settings.WindowX);
            Assert.Equal(200, load.Settings.WindowY);
            Assert.Equal(900, load.Settings.WindowWidth);
            Assert.Equal(700, load.Settings.WindowHeight);

            // Ключи панель знает: замечаний «ключ не применён» быть не должно.
            Assert.Equal(string.Empty, load.Problem);

            // Мусор в геометрии — «не задано»: ноль в поле, а не абсурдное число, из-за которого
            // окно открылось бы там, где его не найти.
            Assert.True(controller.SetWindowPlacement(0, 0, -5, WindowPlacement.SizeLimit + 1));
            var garbage = store.Load().Settings;
            Assert.Equal(0, garbage.WindowWidth);
            Assert.Equal(0, garbage.WindowHeight);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В прогоне БЕЗ права писать настройки геометрия не запоминается вовсе, и об этом сказано
    /// строкой журнала: панель не притворяется, что запомнила, и не падает.
    /// </summary>
    [Fact]
    public void Геометрия_окна_не_пишется_без_права_и_об_этом_сказано()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            store.Save(new PanelSettings { WindowX = 300, WindowY = 200, WindowWidth = 800, WindowHeight = 600 });

            var log = new List<string>();
            var locked = new SettingsController(
                store, paths, allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: log.Add);

            Assert.False(locked.SetWindowPlacement(10, 10, 640, 480));

            Assert.Equal(300, store.Load().Settings.WindowX);
            Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// РАЗМЕР И ПОЛОЖЕНИЕ ОКНА ПЕРЕЖИВАЮТ «СОХРАНИТЬ» ИЗ ОКНА НАСТРОЕК. Окно пишет настройки
    /// целиком, из своих полей, а геометрия в нём не показывается — значит поле, которого окно
    /// не знает, обнулялось бы каждым нажатием «Сохранить», и окно человека молча возвращалось бы
    /// к умолчанию. Та же грабля, что с запомненным согласием (приёмка 26.09.2026).
    /// </summary>
    [AvaloniaFact]
    public void Сохранение_из_окна_не_забывает_размер_и_положение_окна()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Assert.True(controller.SetWindowPlacement(-1600, 200, 900, 700));

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);

            var pending = window.PendingSettings();

            Assert.Equal(-1600, pending.WindowX);
            Assert.Equal(200, pending.WindowY);
            Assert.Equal(900, pending.WindowWidth);
            Assert.Equal(700, pending.WindowHeight);

            Assert.True(controller.Save(pending));

            var back = store.Load().Settings;
            Assert.Equal(-1600, back.WindowX);
            Assert.Equal(200, back.WindowY);
            Assert.Equal(900, back.WindowWidth);
            Assert.Equal(700, back.WindowHeight);
            Assert.Equal(-1600, controller.Settings.WindowX);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРОЧИТАННЫЕ ЦЕНЫ ПЕРЕЖИВАЮТ «СОХРАНИТЬ» ИЗ ОКНА НАСТРОЕК.
    ///
    /// Таблица цен в окне не показывается полем, значит окно о ней не знает — и «Сохранить» в любом
    /// разделе стирало бы её, а следующий запуск панели показывал бы пустое место там, где человек
    /// вчера видел тарифы. Владелец описал это словами: *«сами тарифы не сохраняются: обновлял вчера,
    /// сегодня снова пусто, только пики сохранены»* (жалоба 29.09.2026).
    ///
    /// ⚠️ **Жалоба была про ДВА дефекта сразу, и это второй из них.** Первый — пропажа ключа
    /// `pricingLast` при записи; и он же выдавал себя за другой: окно считало прочитанные цены
    /// НЕСОХРАНЁННОЙ ПРАВКОЙ и спрашивало «сохранить изменения?» у человека, который открыл
    /// настройки и не тронул ничего. Поэтому проверка называет и то, и другое: поле цело в файле
    /// И окно не считает его правкой.
    ///
    /// Проходит через окно, а не через <c>SettingsStore.Clean</c> (тот цел, и проверка на нём
    /// дефекта не видит): испорчен был именно путь «окно пишет настройки целиком».
    /// </summary>
    [AvaloniaFact]
    public void Сохранение_из_окна_не_забывает_прочитанные_цены()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            var read = new PricingResult(
                true, string.Empty, PricingDecisions.Url("ru"), "ru", "deepseek-flash",
                new[] { new PriceRow("1M OUTPUT TOKENS", "$0.6", "$1.2") },
                AgentCatalog.DeepSeek.PeakWindows,
                DateTimeOffset.Now);

            Assert.True(controller.SetPricingLast(PricingMemory.Encode(read)));
            Assert.NotEmpty(controller.Settings.PricingLast);

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);

            // Окно считает правкой ТОЛЬКО то, что человек изменил: прочитанные цены он не менял.
            Assert.False(window.HasUnsavedChanges());

            // И «Сохранить» их не стирает: после сохранения в файле та же таблица.
            Assert.True(controller.Save(window.PendingSettings()));

            var back = store.Load().Settings;
            Assert.Equal(controller.Settings.PricingLast, back.PricingLast);

            var decoded = PricingMemory.Decode(back.PricingLast);
            Assert.NotNull(decoded);
            Assert.Equal("1M OUTPUT TOKENS", Assert.Single(decoded.Rows).Item);

            // И окно, открытое заново на этом файле, снова не видит правки.
            var second = new SettingsWindow();
            second.Attach(controller, autostart: null);
            Assert.False(second.HasUnsavedChanges());
        }
        finally
        {
            RemoveTemp(dir);
        }
    }
}
