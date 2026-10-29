using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО НАСТРОЕК НЕ ТЕРЯЕТ НЕСОХРАНЁННУЮ ПРАВКУ МОЛЧА (жалоба владельца, п. 16 `docs\DESIGN.md`).
///
/// Слова владельца: *«Вероятно требуется предупреждение при закрытии окна что параметры изменены,
/// сохранить ли перед закрытием»*. До этой работы ни <c>Closing</c>, ни <c>OnClosing</c> в окне
/// не было вовсе: крестик закрывал окно, и введённое пропадало без единого слова — сохраняла
/// только кнопка «Сохранить».
///
/// Проверки идут ДВУМЯ путями, и оба нужны:
///
/// * **чистое решение** (<see cref="SettingsCloseDecisions.HasUnsavedChanges"/>) — «есть ли что
///   сохранять»: его в окне нечем проверить, а тут оно проверяется прогоном, который умеет падать;
/// * **живой путь окна** — настоящий <c>Close()</c> по крестику, настоящий контроллер настроек
///   под своим временным корнем, а сохранение проверяется ПО ФАЙЛУ, а не по возвращённому значению.
///
/// Отдано проверкам ровно одно — ответ на вопрос: в проверке без экрана диалог всплыл бы сам
/// и ответил не то, что решил бы человек (тот же приём, что у окна копий с вопросом о сервере).
/// </summary>
public class SettingsUnsavedTests
{
    /// <summary>Дать вёрстке и очереди диспетчера отработать до конца.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-unsaved-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Уборка только СВОЕГО каталога: общий родитель семейства не снимается (t3, `TempRoots`).</summary>
    private static void RemoveTemp(string dir) => TempRoots.Remove(dir);

    /// <summary>Окно настроек с НАСТОЯЩИМ контроллером под своим временным корнем.</summary>
    private static (SettingsWindow Window, SettingsStore Store, string Dir) Stand(
        bool allowed = true, Action<PanelSettings>? seed = null)
    {
        var dir = TempDir();
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        if (seed is not null)
        {
            var seeded = PanelSettings.Default;
            seed(seeded);
            Assert.True(store.Save(seeded));
        }

        var controller = new SettingsController(
            store, paths, allowed: allowed, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null);

        return (window, store, dir);
    }

    // ------------------------------------------------------------------ чистое решение

    /// <summary>
    /// ПРИЗНАК «ПОРТ ВЫБРАЛ ЧЕЛОВЕК» — НЕ ПРАВКА. Его ставит сама кнопка «Сохранить»
    /// (<c>PendingSettings</c>), а не человек: он не выбирает этот признак ни в одном поле.
    /// Считай мы его изменением — окно спрашивало бы «сохранить?» у человека, который не менял
    /// ничего, и вопрос перестали бы читать (а в следующий раз правка пропала бы молча).
    /// </summary>
    [Fact]
    public void Признак_выбранного_порта_не_считается_правкой()
    {
        // Ровно то, что отдаёт окно: признак выставлен.
        var pending = new PanelSettings { ServerPortChosen = true };
        var current = PanelSettings.Default;

        Assert.False(SettingsCloseDecisions.HasUnsavedChanges(pending, current));
        Assert.False(SettingsCloseDecisions.HasUnsavedChanges(current, current));
    }

    /// <summary>Настоящая правка видна — и в одном поле, и в списке.</summary>
    [Fact]
    public void Правка_в_поле_видна()
    {
        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { Theme = PanelSettings.ThemeDark }, PanelSettings.Default));

        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { BackupFolder = @"D:\Мои копии" }, PanelSettings.Default));

        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { BackupKeepCount = 5 }, PanelSettings.Default));

        // Разрешение на ключи — живая настройка: она и в файл едет, и «несохранённой правкой» быть
        // обязана. ⚠️ А мёртвое поле «копия для передачи» правкой НЕ считается: решением владельца
        // 27.09.2026 (п. 11) передача настройкой быть перестала, и дверь настроек приводит это поле
        // к false — то есть «правка», которой ничего не меняет, и не должна ничего обещать.
        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { BackupWithKeys = true }, PanelSettings.Default));

        Assert.False(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { BackupShareable = true }, PanelSettings.Default));

        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { ServerPort = ServerDecisions.DefaultServerPort - 1 }, PanelSettings.Default));
    }

    /// <summary>
    /// Списки сравниваются ПО СОДЕРЖИМОМУ, а не по ссылке: <c>List&lt;string&gt;</c> — ссылочный
    /// тип, и сравнение по ссылке объявило бы правкой любую перерисовку окна.
    /// </summary>
    [Fact]
    public void Списки_сравниваются_по_содержимому()
    {
        var left = new PanelSettings { BackupKeyDirs = new List<string> { @"C:\ключи" } };
        var right = new PanelSettings { BackupKeyDirs = new List<string> { @"C:\ключи" } };

        Assert.False(SettingsCloseDecisions.HasUnsavedChanges(left, right));

        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(
            new PanelSettings { BackupKeyDirs = new List<string> { @"C:\ключи", @"D:\ещё" } }, right));
    }

    /// <summary>
    /// ВОРОТА: в сравнении участвуют ВСЕ поля настроек, кроме названных явно. Список «кроме»
    /// не растёт молча — расширить его можно только вместе с этой проверкой, то есть осознанно.
    /// </summary>
    [Fact]
    public void Все_поля_настроек_участвуют_в_сравнении_кроме_названных()
    {
        var all = typeof(PanelSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(all.Length > 15, $"полей настроек: {all.Length} — отражение вернуло не тот тип");

        Assert.Equal(new[] { nameof(PanelSettings.ServerPortChosen) }, SettingsCloseDecisions.IgnoredFields);

        var expected = all
            .Where(name => !SettingsCloseDecisions.IgnoredFields.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.Equal(expected, SettingsCloseDecisions.ComparedFields());
    }

    // ------------------------------------------------------------------ живой путь окна

    /// <summary>
    /// КРЕСТИК С НЕСОХРАНЁННОЙ ПРАВКОЙ СПРАШИВАЕТ, а «Отмена» оставляет окно открытым и НИЧЕГО
    /// не записывает: человек ещё не решил.
    /// </summary>
    [AvaloniaFact]
    public void Крестик_с_несохранённой_правкой_спрашивает_и_отмена_держит_окно()
    {
        var (window, store, dir) = Stand();

        try
        {
            var asked = 0;
            window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };

            window.Show();
            Settle();

            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";

            // Состояние то самое, в котором дефект и возможен: правка есть.
            Assert.True(window.HasUnsavedChanges(), "правка не видна — проверка прошла бы не через то состояние");

            window.Close();
            Settle();

            Assert.Equal(1, asked);
            Assert.True(window.IsVisible, "«Отмена» обязана оставить окно открытым");
            Assert.False(store.Exists, "«Отмена» ничего не записывает");
            Assert.False(window.Saved);

            window.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>«Сохранить» пишет правку В ФАЙЛ и закрывает окно.</summary>
    [AvaloniaFact]
    public void Ответ_сохранить_пишет_правку_и_закрывает_окно()
    {
        var (window, store, dir) = Stand();

        try
        {
            window.UnsavedChoiceForTests = () => UnsavedChoice.Save;

            window.Show();
            Settle();

            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
            window.Close();
            Settle();

            Assert.False(window.IsVisible, "окно обязано закрыться после сохранения");
            Assert.True(window.Saved);
            Assert.True(store.Exists);
            Assert.Equal(@"D:\Новая папка", store.Load().Settings.ServerWorkingDir);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «Выйти без сохранения» закрывает окно и НЕ пишет ничего: правка пропадает, но человек
    /// сказал это сам — молчаливого исчезновения здесь больше нет.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_выйти_без_сохранения_закрывает_окно_и_ничего_не_пишет()
    {
        var (window, store, dir) = Stand();

        try
        {
            window.UnsavedChoiceForTests = () => UnsavedChoice.Discard;

            window.Show();
            Settle();

            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
            window.Close();
            Settle();

            Assert.False(window.IsVisible);
            Assert.False(window.Saved);
            Assert.False(store.Exists, "без сохранения файл настроек создаваться не должен");
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// БЕЗ ПРАВКИ ВОПРОСА НЕТ. Окно, которое спрашивает «сохранить?» на каждом закрытии, приучает
    /// нажимать «да» не читая — и однажды так теряется настоящая правка.
    /// </summary>
    [AvaloniaFact]
    public void Без_правки_окно_закрывается_сразу_и_ни_о_чём_не_спрашивает()
    {
        var (window, store, dir) = Stand();

        try
        {
            var asked = 0;
            window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };

            window.Show();
            Settle();

            Assert.False(window.HasUnsavedChanges());

            window.Close();
            Settle();

            Assert.Equal(0, asked);
            Assert.False(window.IsVisible);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕ СОХРАНИЛОСЬ — ОКНО ОСТАЁТСЯ ОТКРЫТЫМ. В прогоне без права писать настройки сохранить
    /// нельзя; закрыть окно «по ответу человека» значило бы потерять правку ровно так же молча,
    /// как до этой работы, только с лишним вопросом.
    /// </summary>
    [AvaloniaFact]
    public void Если_сохранить_не_удалось_окно_остаётся_открытым()
    {
        var (window, store, dir) = Stand(allowed: false);

        try
        {
            window.UnsavedChoiceForTests = () => UnsavedChoice.Save;

            window.Show();
            Settle();

            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
            window.Close();
            Settle();

            Assert.True(window.IsVisible, "несохранённая правка не имеет права пропасть молча");
            Assert.False(window.Saved);
            Assert.Equal(PanelStrings.SettingsReadOnlyNote, window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ВЫХОД ИЗ ПАНЕЛИ ЗАКРЫВАЕТ ОКНО МОЛЧА. Человек уже сказал «Выход»; вопрос про настройки
    /// был бы обращён к тому, кто ушёл, и держал бы панель незакрытой. Дверь — <see cref="IQuietClose"/>,
    /// и ею же закрывает окно слот панели (<see cref="SingleWindowSlot"/>), а не сам человек.
    /// </summary>
    [AvaloniaFact]
    public void Выход_из_панели_закрывает_окно_молча()
    {
        var (window, store, dir) = Stand();

        try
        {
            var asked = 0;
            window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };

            var slot = new SingleWindowSlot();
            Assert.True(slot.Open(() => window));
            Settle();

            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
            Assert.True(window.HasUnsavedChanges());

            slot.Close();
            Settle();

            Assert.False(window.IsVisible, "слот обязан закрыть окно, а не оставить его висеть");
            Assert.Equal(0, asked);
            Assert.False(store.Exists, "на выходе из панели ничего не записывается само");
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ DEF-065: отметка первого запуска

    /// <summary>Отметка показа окна первого запуска — тем же форматом, каким её пишет App.</summary>
    private static string WelcomeStamp() =>
        FirstRunDecisions.Stamp(new DateTimeOffset(2026, 10, 25, 21, 48, 0, TimeSpan.Zero));

    /// <summary>
    /// ОТКРЫТЬ НАСТРОЙКИ ПОСЛЕ ОКНА ПЕРВОГО ЗАПУСКА И ЗАКРЫТЬ — ВОПРОСА НЕТ (DEF-065).
    ///
    /// **От какого случая.** `PendingSettings` собирался `new PanelSettings { … }` и не переносил
    /// `WelcomeShownAt`; сравнение всех полей видело разницу и спрашивало «Настройки изменены.
    /// Сохранить их перед закрытием?» у человека, который ничего не трогал (снимок владельца
    /// 25.10.2026). Такой вопрос перестают читать, и однажды теряется настоящая правка.
    ///
    /// **Мутация, которая обязана упасть:** M4 «убрать `WelcomeShownAt`» — `pending.WelcomeShownAt =
    /// string.Empty` в `PendingSettings`.
    /// </summary>
    [AvaloniaFact]
    public void После_первого_запуска_закрытие_настроек_не_спрашивает()
    {
        var stamp = WelcomeStamp();
        var (window, store, dir) = Stand(seed: settings => settings.WelcomeShownAt = stamp);

        try
        {
            var asked = 0;
            window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };

            window.Show();
            Settle();

            // Состояние то самое, в котором дефект и был возможен: отметка лежит в настройках.
            Assert.Equal(stamp, store.Load().Settings.WelcomeShownAt);
            Assert.False(window.HasUnsavedChanges(), "окно считает правкой отметку, которую не показывало");

            window.Close();
            Settle();

            Assert.Equal(0, asked);
            Assert.False(window.IsVisible);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «СОХРАНИТЬ» В НАСТРОЙКАХ НЕ СТИРАЕТ ОТМЕТКУ ПЕРВОГО ЗАПУСКА (DEF-065): сохранение пишет
    /// файл ЦЕЛИКОМ, и поле, которого окно не знает, обязано пройти нетронутым — иначе окно первого
    /// запуска возвращается на следующем старте.
    ///
    /// **Мутация, которая обязана упасть:** M4 (та же) — отметить поле пустым в `PendingSettings`.
    /// </summary>
    [AvaloniaFact]
    public void Сохранить_не_стирает_отметку_первого_запуска()
    {
        var stamp = WelcomeStamp();
        var (window, store, dir) = Stand(seed: settings => settings.WelcomeShownAt = stamp);

        try
        {
            window.UnsavedChoiceForTests = () => UnsavedChoice.Save;

            window.Show();
            Settle();

            // Настоящая правка человека — чтобы сохранение вообще случилось.
            window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";
            window.Close();
            Settle();

            Assert.True(window.Saved);

            var reread = store.Load();

            Assert.True(reread.Ok, reread.Problem);
            Assert.Equal(stamp, reread.Settings.WelcomeShownAt);
            Assert.Equal(@"D:\Новая папка", reread.Settings.ServerWorkingDir);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ИЗМЕНЕНИЕ САМОЙ ОТМЕТКИ — ПРАВКА, и сравнение обязано её видеть: иначе «сравнивать только
    /// поля окна» прошло бы незамеченным, а отметка однажды снова выпала бы из-под стражи.
    ///
    /// **Мутация, которая обязана упасть:** «сравнивать по полям окна» — `HasUnsavedChanges`
    /// перебирает только <c>SettingsWindow.EditedFields</c>.
    /// </summary>
    [Fact]
    public void Изменение_отметки_первого_запуска_видно_как_правка()
    {
        var current = PanelSettings.Default;
        var pending = current.Copy();

        pending.WelcomeShownAt = WelcomeStamp();

        Assert.True(SettingsCloseDecisions.HasUnsavedChanges(pending, current));
        Assert.Contains(
            nameof(PanelSettings.WelcomeShownAt),
            SettingsCloseDecisions.ComparedFields(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// КОПИЯ НАСТРОЕК СОХРАНЯЕТ КАЖДОЕ ПОЛЕ, ВКЛЮЧАЯ БУДУЩИЕ (DEF-065) — иначе окно снова начнёт
    /// терять то, чего не показывает.
    ///
    /// Проверка перебирает поля ОТРАЖЕНИЕМ и заполняет каждое отличным от умолчания значением:
    /// значение сверяется у копии, а ссылочные (списки) обязаны быть РАЗНЫМИ объектами, иначе
    /// правка в окне меняла бы сами настройки. Новое поле неизвестного типа уронит проверку
    /// с явным сообщением, а не выпадет молча.
    /// </summary>
    [Fact]
    public void Копия_настроек_сохраняет_все_поля()
    {
        var all = typeof(PanelSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .ToArray();

        Assert.True(all.Length > 20, $"полей настроек: {all.Length} — отражение вернуло не тот тип");

        var original = new PanelSettings();

        foreach (var property in all) property.SetValue(original, Marker(property.PropertyType));

        var copy = original.Copy();

        foreach (var property in all)
        {
            var before = property.GetValue(original);
            var after = property.GetValue(copy);

            if (before is IEnumerable<string> left && after is IEnumerable<string> right)
            {
                Assert.Equal(left, right);
            }
            else
            {
                Assert.Equal(before, after);
            }

            // Списки и прочие ссылочные поля — отдельными объектами: копия не делит ссылку с текущими.
            if (property.PropertyType != typeof(string) && !property.PropertyType.IsValueType)
            {
                Assert.NotSame(before, after);
            }
        }
    }

    /// <summary>
    /// ОБЩИЙ СТОРОЖ ПОЛЕЙ (DEF-065): каждое поле `PanelSettings` либо приходит из ПОЛНОЙ КОПИИ,
    /// либо названо в <see cref="SettingsWindow.EditedFields"/> — списка «по одному полю» нет.
    ///
    /// **Как доказывается.** `PendingSettings` обязан начинаться с `_settings.Settings.Copy()`,
    /// а каждое присваивание `pending.<Поле>` — быть названо в списке. Поэтому новое поле окна
    /// нельзя добавить молча, а новое поле настроек не нужно вписывать руками: его копирует
    /// <see cref="PanelSettings.Copy"/> (это стережёт проверка выше).
    ///
    /// **Мутации, которые обязаны упасть:** M4 «убрать `WelcomeShownAt`» (присваивание поля вне
    /// списка) · «перечислять поля окна по одному, без копии» (нет `Copy()`).
    /// </summary>
    [Fact]
    public void Общий_сторож_полей_окна()
    {
        var all = typeof(PanelSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var edited = SettingsWindow.EditedFields;

        Assert.Equal(edited.Length, edited.Distinct(StringComparer.Ordinal).Count());
        Assert.All(edited, name => Assert.Contains(name, all, StringComparer.Ordinal));
        Assert.True(edited.Length < all.Length, "окно не может править все поля: остальные обязаны проходить копией");

        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "Views", "SettingsWindow.axaml.cs"));

        var start = source.IndexOf("public PanelSettings PendingSettings()", StringComparison.Ordinal);
        Assert.True(start > 0, "в SettingsWindow нет PendingSettings");

        var end = source.IndexOf("/// <summary>Строки поля", start, StringComparison.Ordinal);
        Assert.True(end > start, "не нашёл конец PendingSettings");

        var body = source[start..end];

        Assert.Contains("_settings?.Settings.Copy()", body, StringComparison.Ordinal);

        var assigned = Regex.Matches(body, @"pending\.([A-Za-z]+)\s*=")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(edited.OrderBy(name => name, StringComparer.Ordinal).ToArray(), assigned);
    }

    /// <summary>Отличное от умолчания значение каждого типа поля — и явный отказ на незнакомый тип.</summary>
    private static object? Marker(Type type) => type switch
    {
        _ when type == typeof(string) => "маркер-проверки",
        _ when type == typeof(int) => 424242,
        _ when type == typeof(bool) => true,
        _ when type == typeof(decimal) => 12.5m,
        _ when type == typeof(List<string>) => new List<string> { "маркер-проверки" },
        _ => throw new InvalidOperationException(
            $"новое поле настроек типа {type.Name}: проверка не знает, чем его заполнить, — " +
            "допиши значение, иначе поле однажды выпадет из копии молча"),
    };
}
