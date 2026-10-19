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
using DshPanel.Shell;
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

        // ⚠️ КАДРЫ — В КОРНЕ СВОЕГО ПРОГОНА, а не в общей папке (t3, 18.10.2026). Прежде путь был
        // `%TEMP%\dsh-panel-look-frames\<имя>.png`, имена детерминированные (`settings-server-Light`),
        // и ВТОРОЙ одновременный прогон открывал занятый файл — воспроизведено падением
        // `ServerSectionLookTests` → IOException «being used by another process». Теперь папка кадров
        // своя у каждого прогона (`TempRoots.Frames`), а снятый путь по-прежнему возвращается и
        // печатается проверкой — по нему кадр и находят глазами.
        var folder = TempRoots.Frames;
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name + ".png");

        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);

        // Явный выбор формата: Save(string) объявлен устаревшим ровно затем, чтобы формат не
        // подразумевался, — а проверке нужен PNG и ничего больше.
        bitmap.Save(path, new PngBitmapEncoderOptions());

        return path;
    }

    /// <summary>
    /// СКОЛЬКО РАЗНЫХ ЦВЕТОВ ВО ВСЁМ НАРИСОВАННОМ ОКНЕ — тот же счётчик, что
    /// <see cref="ColorsIn(Window, Rect)"/>, только областью служит весь кадр.
    ///
    /// ⚠️ **Заведено 17.10.2026 (находка F-1 приёмки пачки D).** Правило кадра в `tests\AGENTS.md`
    /// объявляло `ColorsIn` **единственным** счётчиком и запрещало «свой пересчёт в проверке» —
    /// а в дереве таких пересчётов было **два**: `ServerTests.DistinctColors` и
    /// `PhoneSectionTests.DistinctColors`, те же тридцать строк, скопированные из оснастки.
    /// Причём примерами «так и считают» то же правило называло ровно эти два класса: читатель
    /// не мог отличить пример от нарушения. Теперь пересчёт ОДИН — обе копии удалены, оба класса
    /// зовут этот метод, и запрет стал правдой.
    ///
    /// **Отличие, исчезнувшее вместе с копиями:** они считали ключом все четыре байта
    /// (`BitConverter.ToInt32`: RGB **и альфу**), а <see cref="ColorsIn(Window, Rect)"/> — только
    /// RGB. Прозрачность — не цвет: два пикселя, различающиеся лишь альфой, человеку не видны,
    /// и считать их разными цветами значило мерить не то. Числа кадров от этого не поехали —
    /// замер в `_team\c2\d-kod.md` §12: оба ключа дают на обоих кадрах одно и то же число.
    /// </summary>
    public static int ColorsIn(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return ColorsIn(window, new Rect(window.ClientSize));
    }

    /// <summary>
    /// СКОЛЬКО РАЗНЫХ ЦВЕТОВ В ПРЯМОУГОЛЬНИКЕ НАРИСОВАННОГО ОКНА — «нарисовано ли ЧТО-ТО вот
    /// здесь» одним числом. «Картинка нарисована» — утверждение о ПИКСЕЛЯХ, а не о том, что
    /// кто-то положил непустые байты в свойство: спрятанный код и несозданный выглядят в коде
    /// одинаково, а человеку и README — по-разному.
    ///
    /// ⚠️ Прямоугольник задаётся в точках окна и переводится в пиксели тем же множителем, что
    /// у отрисовки (96 точек на дюйм → один к одному), поэтому дробные границы округляются
    /// НАРУЖУ: лишний пиксель фона не мешает, а вот срезанный край картинки помешал бы.
    ///
    /// Это ЕДИНСТВЕННЫЙ счётчик цветов в наборе проверок: свой пересчёт в проверке запрещён
    /// (иначе две проверки однажды посчитают по-разному). Нужен весь кадр — зовётся
    /// <see cref="ColorsIn(Window)"/>, тот же счётчик без области.
    /// </summary>
    public static int ColorsIn(Window window, Rect area)
    {
        ArgumentNullException.ThrowIfNull(window);

        Settle();

        var width = (int)Math.Ceiling(window.ClientSize.Width);
        var height = (int)Math.Ceiling(window.ClientSize.Height);

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

        var left = Math.Clamp((int)Math.Floor(area.X), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(area.Y), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling(area.Right), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom), top + 1, height);

        var colors = new HashSet<int>();

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var at = (y * stride) + (x * 4);

                colors.Add((buffer[at] << 16) | (buffer[at + 1] << 8) | buffer[at + 2]);
            }
        }

        return colors.Count;
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

    /// <summary>
    /// Временный корень прогона: свои настройки, ни одного каталога владельца.
    /// Свой у КАЖДОГО прогона (`TempRoots`): два одновременных `dotnet test` не делят ни одного
    /// каталога — см. разбор в `TempRoots`.
    /// </summary>
    public static string TempDir() => TempRoots.For("look-tests");

    /// <summary>
    /// Уборка корня: снимает ТОЛЬКО своё. Общий каталог семейства не снимается никогда — прежде
    /// уборка удаляла `%TEMP%\dsh-panel-look-tests` целиком и могла снести корень, из которого
    /// в это время работал второй прогон (t3, 18.10.2026).
    /// </summary>
    public static void RemoveTemp(string dir) => TempRoots.Remove(dir);

    /// <summary>
    /// Окно настроек с настоящим контроллером под СВОИМ временным корнем.
    ///
    /// <paramref name="pricing"/> — контроллер цен; без него таблица «Стоимость» остаётся одной
    /// шапкой, и строка-объяснение молчит. Так выглядит окно, построенное без связки, и это
    /// проверяемое состояние, а не недоделка.
    ///
    /// <paramref name="update"/> — состояние выпусков для РАЗДЕЛА «Обновление»; без него раздел
    /// честно говорит, что в этом прогоне панель замену файлов не готовит.
    ///
    /// <paramref name="install"/> — движок установки для того же раздела. Без него кнопка
    /// подготовки называет причиной «в этом прогоне не готовим», с ним — говорит про право и про
    /// то, обновляться ли вообще; обе причины раздел обязан уметь показать.
    ///
    /// <paramref name="skipUpdate"/> — «запомнить пропущенную версию» (владелец настроек).
    /// <paramref name="exitForUpdate"/> — «панель, закройся: сценарий замены запущен». Зовётся
    /// ровно по нажатию последней кнопки, и проверка обязана это видеть.
    ///
    /// <paramref name="phone"/> — дверь РАЗДЕЛА «ТЕЛЕФОН» (F1.d). Без неё раздел честно говорит,
    /// что телефоном в этом прогоне не управляют, — и это проверяемое состояние, а не недоделка.
    ///
    /// <paramref name="phoneAddressMask"/> — маска ПОКАЗА адреса для того же раздела: её ставит
    /// прогон съёмки (`Program.MaskAddress`). Ставится она ЗДЕСЬ, а не отдельным вызовом после
    /// связки, и это не удобство: связка УЖЕ рисует раздел, и маска, поставленная после неё,
    /// опоздала бы — окно один раз показало бы настоящий адрес. Кадр уезжает в README, поэтому
    /// «один раз успело» здесь означает «утекло».
    ///
    /// <paramref name="firewall"/> — дверь БРАНДМАУЭРА (этап F4), ОТДЕЛЬНАЯ от двери телефона:
    /// изменение на машине и состояние телефона — разные права. Без неё раздел честно говорит, что
    /// брандмауэром тут не управляют, и НИ ОДНОГО правила не читает (так его видит прогон съёмки).
    /// </summary>
    public static SettingsWindow SettingsStand(
        string dir,
        bool allowed = true,
        IPricingControl? pricing = null,
        IUpdateControl? update = null,
        IUpdateInstall? install = null,
        Func<string, bool>? skipUpdate = null,
        Action? exitForUpdate = null,
        IPhoneSection? phone = null,
        Func<string, string>? phoneAddressMask = null,
        IFirewallDoor? firewall = null)
    {
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        var controller = new SettingsController(
            store, paths, allowed: allowed, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(
            controller,
            autostart: null,
            balance: null,
            pricing: pricing,
            update: update,
            install: install,
            skipUpdate: skipUpdate,
            exitForUpdate: exitForUpdate,
            phone: phone,
            phoneAddressMask: phoneAddressMask,
            firewall: firewall);

        return window;
    }

    /// <summary>
    /// Окно настроек ВМЕСТЕ С ВЛАДЕЛЬЦЕМ настроек под своим временным корнем — для проверок,
    /// которым нужны обе половины: «что легло в файл» и «что показано». Читать файл отдельно
    /// и собирать второе окно значило бы проверять не тот путь, каким идёт человек.
    /// </summary>
    public static (SettingsWindow Window, SettingsController Settings, AppPaths Paths) SettingsWithOwner(
        string dir,
        bool allowed = true,
        IPhoneSection? phone = null)
    {
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        var controller = new SettingsController(
            store, paths, allowed: allowed, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null, phone: phone);

        return (window, controller, paths);
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

        /// <summary>
        /// СОСТОЯНИЕ ПРОЧИТАНО ХОТЬ РАЗ (задание владельца 14.10.2026, §1). Подстановка отвечает
        /// готовым состоянием, поэтому по умолчанию «прочитано»; проверка ставит <c>false</c>,
        /// когда ей нужно положение «панель ещё не смотрела на сервер» — в нём калитка обязана
        /// МОЛЧАТЬ про связь, а не писать «связи нет».
        /// </summary>
        public bool Known { get; set; } = true;

        /// <summary>
        /// СМЕНА СОСТОЯНИЯ — СООБЩЕНИЕ, КОТОРОЕ ПОДСТАНОВКА УМЕЕТ ПОДНЯТЬ (задание владельца
        /// 14.10.2026, §3). Настоящий контроллер поднимает его сам, когда состояние ДЕЙСТВИТЕЛЬНО
        /// сменилось; подстановке поднять его нечем — поэтому здесь <see cref="Signal"/>, и зовёт
        /// его проверка ровно в тот миг, когда «сервер подняли или погасили».
        /// </summary>
        public event Action? Changed;

        /// <summary>Сообщить подписчикам, что состояние сменилось. Зовёт ПРОВЕРКА.</summary>
        public void Signal() => Changed?.Invoke();

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

        /// <summary>«Поднять свой сервер заново» — подставной сервер запоминает вызов и ответ человека.</summary>
        public List<bool> Recovers { get; } = new();

        public ServerState RecoverOwnServer(TimeSpan timeout, bool confirmed)
        {
            Recovers.Add(confirmed);
            return State;
        }
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
