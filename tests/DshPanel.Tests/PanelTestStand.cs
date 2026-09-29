using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshPanel.Isolation;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Коллекция проверок ВИДА: они показывают окна, двигают указатель и СРАВНИВАЮТ КАДРЫ, поэтому
/// идут ПО ОДНОЙ, а не рядом с другими классами.
///
/// Зачем это нужно и от какого случая. Указатель в безоконном (headless) прогоне — состояние
/// ОДНО НА ПРОЦЕСС: проверка, наводящая мышь на кнопку, красит её в состояние наведения,
/// а в это же время соседний класс снимает кадр и получает разницу, которой в жизни не бывает
/// («кадр обычного состояния не равен сам себе» — на этом и попался первый прогон
/// <c>ButtonStateTests</c>, пока классы шли параллельно).
///
/// Второй довод — тема: она тоже общая на процесс, и проверка, меняющая её, ломала бы кадр соседу.
/// </summary>
[CollectionDefinition(PanelLookCollection.Name, DisableParallelization = true)]
public sealed class PanelLookCollection
{
    public const string Name = "panel-look";
}

/// <summary>
/// ОБЩАЯ ОСНАСТКА проверок ВИДА панели: показать окно, досчитать вёрстку, снять кадр, перечислить
/// кнопки, завести временный корень настроек.
///
/// Заведена пачкой 27.09.2026 (решения владельца: раскладка главного окна, настройки разделами,
/// подсказки и состояния кнопок). Четыре проверки вида видят одно и то же, и «как показать окно»
/// не должно быть записано в каждой по-своему: разошедшись, проверки начнут мерить разное.
/// </summary>
internal static class PanelTestStand
{
    /// <summary>
    /// Дать вёрстке и очереди диспетчера отработать до конца: без этого Bounds ещё нулевые,
    /// а кадр — прошлого состояния.
    /// </summary>
    public static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Отпечаток НАРИСОВАННОГО окна. Мерить по нему состояния кнопок — единственный способ
    /// проверить обещание «состояния различимы»: свойства стиля можно выставить и не увидеть
    /// ничего, а человеку нужно то, что видно.
    /// </summary>
    public static string FrameHash(Window window)
    {
        Settle();

        var width = (int)Math.Ceiling(window.ClientSize.Width);
        var height = (int)Math.Ceiling(window.ClientSize.Height);

        Assert.True(width > 0 && height > 0, $"пустой размер окна {width}x{height}");

        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);

        var stride = width * 4;
        var buffer = new byte[stride * height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return Convert.ToHexString(SHA256.HashData(buffer));
    }

    /// <summary>
    /// СОХРАНИТЬ КАДР ОКНА В PNG и вернуть путь — то же изображение, что меряет
    /// <see cref="FrameHash"/>, только его можно посмотреть глазами.
    ///
    /// Зачем: «зелёная проверка» не отвечает на вопрос «а как это выглядит человеку». Для таблицы
    /// «что вернём» (п. 32 `docs\DESIGN.md`) это существенно: её переделывали ровно ради чтения,
    /// и вид обязан быть доступен глазам, а не только отпечатку из SHA-256.
    ///
    /// ⚠️ Пишется в <c>%TEMP%</c> и НИКОГДА в репозиторий, <c>dist</c> или <c>upd-build</c>: кадр
    /// окна копий — это в том числе пути и состав копий человека (красные линии 4 и 7).
    /// </summary>
    public static string SaveFrame(Window window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Settle();

        var width = (int)Math.Ceiling(window.ClientSize.Width);
        var height = (int)Math.Ceiling(window.ClientSize.Height);

        Assert.True(width > 0 && height > 0, $"пустой размер окна {width}x{height} — сохранять нечего");

        var folder = Path.Combine(Path.GetTempPath(), "dsh-panel-look-frames");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name + ".png");

        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);

        // Явный выбор формата: Save(string) объявлен устаревшим ровно затем, чтобы формат не
        // подразумевался, — а проверке нужен PNG и ничего больше.
        bitmap.Save(path, new PngBitmapEncoderOptions());

        return path;
    }

    /// <summary>Кнопки окна — в порядке дерева. По ИМЕНИ, а не по подписи: подпись переводят.</summary>
    public static IReadOnlyList<Button> Buttons(Window window) =>
        window.GetVisualDescendants().OfType<Button>().ToList();

    /// <summary>
    /// ПОДПИСЬ КНОПКИ — текстом, даже когда у кнопки есть ЗНАЧОК.
    ///
    /// Зачем: с 28.09.2026 кнопки главного окна несут значок и подпись вместе (`Content` — панель
    /// со значком и текстом), поэтому чтение `Content` как строки вернуло бы объект панели, а не
    /// слово. Второй способ достать подпись в каждой проверке разошёлся бы с первым — поэтому
    /// разбор ОДИН и живёт здесь, рядом с прочей оснасткой вида.
    /// </summary>
    public static string Label(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);

        if (button.Content is string text) return text;

        if (button.Content is Control content)
        {
            var block = content.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()
                        ?? content as TextBlock;

            return block?.Text ?? string.Empty;
        }

        return button.Content?.ToString() ?? string.Empty;
    }

    /// <summary>Значок кнопки, если он у неё есть (у кнопок без значка — <c>null</c>).</summary>
    public static PathIcon? Icon(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);

        return button.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault();
    }

    /// <summary>Временный корень прогона: свои настройки, ни одного каталога владельца.</summary>
    public static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-look-tests", Guid.NewGuid().ToString("N"));

    public static void RemoveTemp(string dir)
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

    /// <summary>
    /// Окно настроек с настоящим контроллером под СВОИМ временным корнем.
    ///
    /// <paramref name="pricing"/> — контроллер цен; без него таблица «Стоимость» остаётся одной
    /// шапкой, и строка-объяснение молчит. Так выглядит окно, построенное без связки, и это
    /// проверяемое состояние, а не недоделка.
    ///
    /// <paramref name="update"/> — состояние выпусков для РАЗДЕЛА «Обновление»; без него раздел
    /// честно говорит, что в этом прогоне панель замену файлов не готовит.
    /// </summary>
    public static SettingsWindow SettingsStand(
        string dir,
        bool allowed = true,
        IPricingControl? pricing = null,
        IUpdateControl? update = null)
    {
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        var controller = new SettingsController(
            store, paths, allowed: allowed, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null, balance: null, pricing: pricing, update: update);

        return window;
    }

    /// <summary>
    /// Подставной сервер окна панели: состояние задаётся ПРОВЕРКОЙ, настоящий движок не нужен.
    /// Повторяет подмену из <c>MainWindowLayoutTests</c> — та живёт в своём классе и закрыта,
    /// а виду нужна своя.
    /// </summary>
    public sealed class StubServer : IServerControl
    {
        public StubServer(ServerState state, ServerOwner owner = ServerOwner.None, bool consent = false)
        {
            State = state;
            Owner = owner;
            ConsentRemembered = consent;
        }

        public ServerState State { get; set; }

        public ServerOwner Owner { get; }

        public bool ConsentRemembered { get; }

        public string EntryLink { get; set; } = string.Empty;

        public FoundServer? Found { get; set; }

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan() => new(
            true, Found is null ? Array.Empty<FoundServer>() : new[] { Found.Value });

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }

    /// <summary>Окно панели с подставным сервером: состояние задано проверкой, движка нет.</summary>
    public static MainWindow MainStand(ServerState state, ServerOwner owner = ServerOwner.None, bool consent = false) =>
        MainWith(new StubServer(state, owner, consent));

    /// <summary>Окно панели с ГОТОВЫМ подставным сервером — когда проверке нужна и находка разведки.</summary>
    public static MainWindow MainWith(IServerControl server)
    {
        var window = new MainWindow();
        window.Attach(server);
        window.RefreshNow();

        return window;
    }
}
