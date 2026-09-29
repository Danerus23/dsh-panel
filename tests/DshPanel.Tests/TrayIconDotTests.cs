using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using DshPanel.Platform;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Огонёк состояния в углу значка в трее (решение владельца 27.09.2026: «зелёный — сервер
/// отвечает, красный — не работает, серый — панель не знает», при этом сам рисунок не меняется).
///
/// Проверки — ПРО ТОЧКИ, а не про «не было исключения»: значок собирается в памяти и рисуется
/// на подставном фоне, после чего проверка читает пиксели. Иначе «огонёк поставлен» означало бы
/// только то, что функция вернула «да».
///
/// Значок при этом НЕ ставится на рабочий стол владельца: <c>Show()</c> здесь не зовётся, и это
/// проверяется отдельной строкой (красная линия 8). Живой вид значка и меню смотрит человек —
/// <c>--tray-selftest</c>.
/// </summary>
public class TrayIconDotTests
{
    // ------------------------------------------------------------------ чистая часть

    /// <summary>
    /// Огонёк кладётся в правый нижний угол и НЕ ЗАЛИВАЕТ значок: точки дальше радиуса обязаны
    /// остаться ровно такими, какими были. Проверка называет и геометрию числами — иначе «огонёк
    /// есть» зеленело бы и тогда, когда он накрыл весь значок.
    /// </summary>
    [Fact]
    public void Огонёк_кладётся_в_угол_и_не_заливает_значок()
    {
        const int Cx = 16;
        const int Cy = 16;

        var original = Solid(Cx, Cy, 0x808080, 255);
        var pixels = (byte[])original.Clone();

        TrayIconOverlay.Compose(pixels, Cx, Cy, TrayTone.Good);

        var (centerX, centerY) = TrayIconOverlay.Center(Cx, Cy);
        var radius = TrayIconOverlay.OuterRadius(Cx);

        // Геометрия прибита числами: значок лотка 16×16, кружок радиуса 3,52 с центром (11,48;11,48).
        Assert.Equal(11.48, centerX, 2);
        Assert.Equal(11.48, centerY, 2);
        Assert.Equal(3.52, radius, 2);

        var dot = TrayPalette.Circle(TrayTone.Good);
        var atDot = (((int)centerY * Cx) + (int)centerX) * 4;

        // Центр огонька — РОВНО цвет тона и полностью непрозрачен.
        Assert.Equal((byte)(dot & 0xFF), pixels[atDot]);
        Assert.Equal((byte)((dot >> 8) & 0xFF), pixels[atDot + 1]);
        Assert.Equal((byte)((dot >> 16) & 0xFF), pixels[atDot + 2]);
        Assert.Equal(255, pixels[atDot + 3]);

        // Точки дальше кружка не тронуты вовсе.
        var untouched = 0;
        var touched = 0;

        for (var y = 0; y < Cy; y++)
        {
            for (var x = 0; x < Cx; x++)
            {
                var dx = (x + 0.5) - centerX;
                var dy = (y + 0.5) - centerY;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var at = (((y * Cx) + x) * 4);

                if (distance > radius + 0.5)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        Assert.Equal(original[at + i], pixels[at + i]);
                    }

                    untouched++;
                }
                else
                {
                    touched++;
                }
            }
        }

        Assert.True(touched > 0, "огонёк не нарисовался вовсе");

        // Огонёк — именно ТОЧКА В УГЛУ, а не заливка: он занимает малую часть значка.
        // Границы названы числами: меньше двадцати точек в лотке не разглядеть, а больше
        // шестидесяти — это уже не угол, а половина значка.
        Assert.InRange(touched, 20, 60);
        Assert.True(untouched > 4 * touched, $"огонёк занял {touched} точек из {Cx * Cy} — это не точка в углу");
    }

    /// <summary>
    /// Вокруг огонька остаётся ТЁМНАЯ обводка: она отделяет цветной кружок от светлых полос
    /// самого рисунка. Без неё зелёный на светлой полосе значка читался бы хуже.
    /// </summary>
    [Fact]
    public void Вокруг_огонька_есть_тёмная_обводка()
    {
        const int Cx = 16;
        const int Cy = 16;

        var pixels = Solid(Cx, Cy, 0xFFFFFF, 255);
        TrayIconOverlay.Compose(pixels, Cx, Cy, TrayTone.Warning);

        var dot = TrayPalette.Circle(TrayTone.Warning);
        var darkest = double.MaxValue;

        for (var y = 0; y < Cy; y++)
        {
            for (var x = 0; x < Cx; x++)
            {
                var at = (((y * Cx) + x) * 4);
                var color = ((uint)pixels[at + 2] << 16) | ((uint)pixels[at + 1] << 8) | pixels[at];

                darkest = Math.Min(darkest, TrayPalette.Luminance(color));
            }
        }

        Assert.True(
            darkest < TrayPalette.Luminance(dot),
            "самая тёмная точка огонька не темнее самого кружка — обводки нет");
    }

    /// <summary>
    /// Огонёк ставится и на ПРОЗРАЧНЫЙ угол: там, где рисунка нет, кружок обязан стать
    /// непрозрачным — иначе он «просвечивал» бы сквозь рабочий стол и выглядел выцветшим.
    /// </summary>
    [Fact]
    public void Огонёк_на_прозрачном_углу_становится_непрозрачным()
    {
        const int Cx = 16;
        const int Cy = 16;

        var pixels = new byte[Cx * Cy * 4];   // всё прозрачное и чёрное

        // До огонька канала прозрачности нет вовсе — все точки пустые.
        Assert.False(TrayIconOverlay.HasAlphaChannel(pixels), "пустые точки не должны считаться каналом прозрачности");

        TrayIconOverlay.Compose(pixels, Cx, Cy, TrayTone.Bad);

        var (centerX, centerY) = TrayIconOverlay.Center(Cx, Cy);
        var atDot = (((int)centerY * Cx) + (int)centerX) * 4;

        Assert.Equal(255, pixels[atDot + 3]);
        Assert.Equal((byte)(TrayPalette.Circle(TrayTone.Bad) & 0xFF), pixels[atDot]);

        // А вдали от огонька прозрачность не тронута: значок не превратился в квадрат.
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[3]);
    }

    /// <summary>
    /// Точки без канала прозрачности — это НЕ значок с прозрачным фоном: у такого значка форму
    /// задаёт отдельная маска, и «слепить» из его точек значок значило бы получить чёрный квадрат.
    /// Поэтому канал проверяется отдельно и до всякой отрисовки.
    /// </summary>
    [Fact]
    public void Канал_прозрачности_отличается_от_пустых_точек()
    {
        var empty = new byte[16 * 16 * 4];
        Assert.False(TrayIconOverlay.HasAlphaChannel(empty));

        empty[3] = 1;
        Assert.True(TrayIconOverlay.HasAlphaChannel(empty));
    }

    // ------------------------------------------------------------- значок целиком (Windows)

    /// <summary>
    /// Главное утверждение про огонёк: значок с ним СОБИРАЕТСЯ Windows, угол красится РОВНО
    /// в цвет тона, а рисунок значка остаётся на месте. Проверка читает пиксели собранного значка,
    /// нарисованного на подставном фоне, — и потому может упасть по-настоящему.
    /// </summary>
    [Fact]
    public void Собранный_значок_красит_угол_в_цвет_тона()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);
        Assert.True(TrayIconSource.TryLoad(bytes, 16, 16, out var baseIcon, out var detail), detail);

        try
        {
            Assert.True(TrayIconOverlay.TryBuild(baseIcon, TrayTone.Bad, out var withDot, out var why), why);
            Assert.NotEqual(IntPtr.Zero, withDot);

            try
            {
                const uint Background = 0xFF00FF;   // цвет, которого нет ни в значке, ни в палитре

                var plain = Render(baseIcon, 16, 16, Background);
                var dotted = Render(withDot, 16, 16, Background);

                var (centerX, centerY) = TrayIconOverlay.Center(16, 16);
                var radius = TrayIconOverlay.OuterRadius(16);

                var colored = Pixel(dotted, 16, (int)centerX, (int)centerY);
                Assert.Equal(TrayPalette.Circle(TrayTone.Bad), colored);

                // Огонёк виден: там, где он лёг, картинка ИЗМЕНИЛАСЬ.
                Assert.NotEqual(Pixel(plain, 16, (int)centerX, (int)centerY), colored);

                // Прозрачный угол значка остался прозрачным: фон через него виден.
                Assert.Equal(Background, Pixel(dotted, 16, 0, 0));

                // И рисунок значка не сдвинулся: вне кружка с обводкой точки совпадают с исходными.
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var dx = (x + 0.5) - centerX;
                        var dy = (y + 0.5) - centerY;
                        if (Math.Sqrt((dx * dx) + (dy * dy)) <= radius + 0.5) continue;

                        Assert.Equal(Pixel(plain, 16, x, y), Pixel(dotted, 16, x, y));
                    }
                }
            }
            finally
            {
                Assert.True(NativeMethods.DestroyIcon(withDot), "дескриптор значка с огоньком обязан освобождаться");
            }
        }
        finally
        {
            NativeMethods.DestroyIcon(baseIcon);
        }
    }

    /// <summary>
    /// Огонёк ставится на все четыре тона, и каждый раз угол получается СВОЕГО цвета. Иначе
    /// проверка выше зеленела бы и при «огонёк всегда красный».
    /// </summary>
    [Fact]
    public void Каждый_тон_красит_угол_своим_цветом()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);
        Assert.True(TrayIconSource.TryLoad(bytes, 16, 16, out var baseIcon, out _));

        try
        {
            var seen = new System.Collections.Generic.List<uint>();
            var (centerX, centerY) = TrayIconOverlay.Center(16, 16);

            foreach (var tone in Enum.GetValues<TrayTone>())
            {
                Assert.True(TrayIconOverlay.TryBuild(baseIcon, tone, out var handle, out var why), why);

                try
                {
                    var color = Pixel(Render(handle, 16, 16, 0xFF00FF), 16, (int)centerX, (int)centerY);

                    Assert.Equal(TrayPalette.Circle(tone), color);
                    seen.Add(color);
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }

            Assert.Equal(Enum.GetValues<TrayTone>().Length, seen.Distinct().Count());
        }
        finally
        {
            NativeMethods.DestroyIcon(baseIcon);
        }
    }

    /// <summary>
    /// Основы нет — огонёк не собирается, и причина названа СЛОВАМИ ИЗ СЛОВАРЯ. Трей обязан
    /// работать всегда, поэтому отказ — это ответ, а не исключение.
    /// </summary>
    [Fact]
    public void Без_основы_огонёк_не_собирается_и_называет_причину()
    {
        Assert.False(TrayIconOverlay.TryBuild(IntPtr.Zero, TrayTone.Good, out var handle, out var detail));

        Assert.Equal(IntPtr.Zero, handle);
        Assert.Equal(PanelStrings.TrayIconDotNoBase, detail);
    }

    // ------------------------------------------------------------------ сам трей

    /// <summary>
    /// Значок пересобирается ТОЛЬКО когда тон сменился: состояние панель перечитывает раз
    /// в секунду, и без этой двери Windows получала бы новую иконку каждую секунду — значок
    /// мигал бы, а человек видел бы это как поломку.
    ///
    /// Проверка меряет ДЕСКРИПТОР и число пересборок, а не намерение: повтор с тем же тоном
    /// обязан оставить и то и другое как было.
    /// </summary>
    [Fact]
    public void Значок_пересобирается_только_при_смене_тона()
    {
        using var tray = new TrayIconHost("проверка огонька");

        tray.SetOwnIcon();

        // До первого тона значок показывается КАК ЕСТЬ — без огонька: панель ещё не сказала,
        // какое у неё состояние, и придумывать за неё серый кружок незачем.
        Assert.Equal(0, tray.ToneAppliedCount);

        var plain = tray.IconHandle;

        Assert.Equal(string.Empty, tray.SetIconTone(TrayTone.Good));
        var good = tray.IconHandle;

        Assert.Equal(1, tray.ToneAppliedCount);
        Assert.NotEqual(plain, good);

        // Тот же тон — оболочку не трогаем.
        Assert.Equal(string.Empty, tray.SetIconTone(TrayTone.Good));
        Assert.Equal(1, tray.ToneAppliedCount);
        Assert.Equal(good, tray.IconHandle);

        // Другой тон — пересобираем.
        Assert.Equal(string.Empty, tray.SetIconTone(TrayTone.Bad));
        Assert.Equal(2, tray.ToneAppliedCount);
        Assert.NotEqual(good, tray.IconHandle);

        Assert.False(tray.IsAdded, "проверка не имеет права ставить значок на рабочий стол владельца");
    }

    /// <summary>
    /// Огонёк на СИСТЕМНОМ запасном значке тоже ставится: панель обязана показывать состояние
    /// даже тогда, когда свой значок не собрался. У системного значка канала прозрачности может
    /// не быть вовсе — тогда путь отказа честно говорит «не вышло», и значок остаётся прежним.
    /// </summary>
    [Fact]
    public void На_запасном_значке_огонёк_либо_встаёт_либо_называет_отказ()
    {
        using var tray = new TrayIconHost("проверка огонька");
        tray.SetDefaultIcon();

        var before = tray.IconHandle;
        var line = tray.SetIconTone(TrayTone.Good);

        if (line.Length == 0)
        {
            Assert.NotEqual(before, tray.IconHandle);
        }
        else
        {
            // Отказ обязан быть строкой из словаря и не подменять значок системным запасным.
            var prefix = PanelStrings.TrayIconDotFailedFormat.Split('{')[0];
            Assert.StartsWith(prefix, line, StringComparison.Ordinal);
            Assert.Equal(before, tray.IconHandle);
        }
    }

    // ------------------------------------------------------------------ шов окна и значка

    /// <summary>
    /// Значок узнаёт о смене состояния ОТ ОКНА: оно перечитывает состояние (раз в секунду и после
    /// каждого действия человека) и сообщает об этом наружу — на этом шве и держится огонёк.
    ///
    /// Проверка нужна потому, что шов не проверяется больше нигде: <c>App</c> в проверках не
    /// поднимается, а самотест трея строит свой значок и до окна не доходит. Без неё «огонёк
    /// меняет цвет вместе с состоянием» осталось бы обещанием из отчёта.
    /// </summary>
    [AvaloniaFact]
    public void Окно_сообщает_значку_о_новом_состоянии_сервера()
    {
        var window = new MainWindow();
        var rounds = 0;

        window.StateRendered += () => rounds++;

        window.Attach(new SilentServer { State = ServerState.Stopped(3080) });

        Assert.True(rounds > 0, "окно связалось с сервером, но о состоянии не сообщило");

        var before = rounds;
        window.RefreshNow();

        Assert.True(rounds > before, "окно перечитало состояние, но значку об этом не сказало");
    }

    /// <summary>Сервер, который ничего не делает: проверке нужно только состояние.</summary>
    private sealed class SilentServer : IServerControl
    {
        public ServerState State { get; set; } = ServerState.Stopped(0);

        public ServerOwner Owner => ServerOwner.None;

        public bool ConsentRemembered => false;

        public string EntryLink => string.Empty;

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan() => new(true, Array.Empty<FoundServer>());

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }

    // ------------------------------------------------------------------ служебное

    private static byte[] Solid(int cx, int cy, uint rgb, byte alpha)
    {
        var pixels = new byte[cx * cy * 4];

        for (var i = 0; i < cx * cy; i++)
        {
            pixels[(i * 4) + 0] = (byte)(rgb & 0xFF);
            pixels[(i * 4) + 1] = (byte)((rgb >> 8) & 0xFF);
            pixels[(i * 4) + 2] = (byte)((rgb >> 16) & 0xFF);
            pixels[(i * 4) + 3] = alpha;
        }

        return pixels;
    }

    private static uint Pixel(byte[] pixels, int cx, int x, int y)
    {
        var at = (((y * cx) + x) * 4);

        return ((uint)pixels[at + 2] << 16) | ((uint)pixels[at + 1] << 8) | pixels[at];
    }

    /// <summary>
    /// Нарисовать значок на НЕПРОЗРАЧНОМ подставном фоне и прочитать точки. Так проверка видит
    /// то же, что увидел бы глаз: прозрачные точки значка оставляют фон, непрозрачные — закрывают.
    /// </summary>
    private static byte[] Render(IntPtr icon, int cx, int cy, uint background)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = cx,
                biHeight = -cy,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, screen);

        var dc = IntPtr.Zero;
        var dib = IntPtr.Zero;

        try
        {
            dib = NativeMethods.CreateDIBSection(screen, ref info, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            Assert.NotEqual(IntPtr.Zero, dib);

            dc = NativeMethods.CreateCompatibleDC(screen);
            Assert.NotEqual(IntPtr.Zero, dc);

            var old = NativeMethods.SelectObject(dc, dib);

            Marshal.Copy(Solid(cx, cy, background, 255), 0, bits, cx * cy * 4);

            Assert.True(DrawIconEx(dc, 0, 0, icon, cx, cy, 0, IntPtr.Zero, DI_NORMAL));

            var pixels = new byte[cx * cy * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            NativeMethods.SelectObject(dc, old);
            return pixels;
        }
        finally
        {
            if (dc != IntPtr.Zero) NativeMethods.DeleteDC(dc);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint DI_NORMAL = 0x0003;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint istep, IntPtr hbrFlicker, uint diFlags);
}
