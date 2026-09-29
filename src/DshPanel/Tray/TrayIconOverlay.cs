using System.Globalization;
using System.Runtime.InteropServices;
using DshPanel.Platform;
using DshPanel.Shell;

namespace DshPanel.Tray;

/// <summary>
/// Огонёк состояния в углу значка: маленький цветной кружок того же языка, что строки в меню
/// (решение владельца 27.09.2026 — «значок остаётся узнаваемым, добавляем огонёк в угол»).
///
/// **Сам рисунок значка не меняется.** Огонёк кладётся ПОВЕРХ готовых точек: панель берёт
/// значок, каким его собрала Windows (<see cref="TrayIconSource"/>), читает его точки и красит
/// угол. Поэтому рисунок остаётся тем же в любом размере и при любом масштабе экрана — угла
/// хватает, чтобы добавить кружок.
///
/// **Почему точки, а не «нарисовать значок средствами GDI».** Замер 27.09.2026: <c>DrawIconEx</c>
/// в 32-битный DIB не кладёт НИЧЕГО — буфер остаётся нулевым (так ведёт себя GDI, когда у приёмника
/// нулевой канал прозрачности). Зато <c>GetIconInfo</c> + <c>GetDIBits</c> отдают настоящие точки
/// с прозрачностью, а <c>CreateIconIndirect</c> принимает их обратно ровно в том же виде
/// (проверено кругом «прочитать → записать → нарисовать»: точка в точке).
///
/// **Пиксели — прямые (не premultiplied) BGRA, сверху вниз.** Это тоже измерено, а не вычитано:
/// у полупрозрачной точки значка канал ярче канала прозрачности (B47 G31 R15 при A16), значит
/// цвет не умножен на прозрачность. Отсюда и формула наложения ниже — обычная «источник поверх».
///
/// **Отказ — не беда, а причина словами.** Значок без канала прозрачности, нечитаемые точки,
/// отказ Windows — во всех случаях возвращается «не вышло» с причиной: вызывающий ставит ПРЕЖНИЙ
/// значок (не системный запасной!) и пишет причину строкой в журнал. Трей обязан работать всегда.
/// </summary>
internal static class TrayIconOverlay
{
    /// <summary>Цвет обводки огонька: отделяет кружок от светлых полос самого рисунка.</summary>
    private const uint RingColor = 0x101010;

    /// <summary>
    /// Собрать значок с огоньком. Размер берётся У САМОГО ЗНАЧКА (а не у системы): значок уже
    /// собран в размере лотка, и второй раз спрашивать <c>SM_CXSMICON</c> значило бы гадать,
    /// совпадут ли два числа.
    /// </summary>
    internal static bool TryBuild(IntPtr baseIcon, TrayTone tone, out IntPtr handle, out string detail)
    {
        handle = IntPtr.Zero;

        if (baseIcon == IntPtr.Zero)
        {
            detail = PanelStrings.TrayIconDotNoBase;
            return false;
        }

        if (!NativeMethods.GetIconInfo(baseIcon, out var info))
        {
            detail = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.TrayIconDotNoPixelsFormat, Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            var bitmap = default(NativeMethods.BITMAP);
            if (NativeMethods.GetObject(info.hbmColor, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bitmap) == 0
                || bitmap.bmWidth <= 0
                || bitmap.bmHeight <= 0)
            {
                detail = string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.TrayIconDotNoPixelsFormat, Marshal.GetLastWin32Error());
                return false;
            }

            var cx = bitmap.bmWidth;
            var cy = bitmap.bmHeight;

            if (!TryReadPixels(info.hbmColor, cx, cy, out var pixels))
            {
                detail = string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.TrayIconDotNoPixelsFormat, Marshal.GetLastWin32Error());
                return false;
            }

            // Без канала прозрачности огонёк ставить не на что: точки значка в этом случае
            // описывают прямоугольник, а форму значка задаёт отдельная маска, которой у нас
            // в точках нет. Слепить из этого значок значило бы получить чёрный квадрат.
            if (!HasAlphaChannel(pixels))
            {
                detail = PanelStrings.TrayIconDotNoAlpha;
                return false;
            }

            Compose(pixels, cx, cy, tone);

            return TryCreateIcon(pixels, cx, cy, out handle, out detail);
        }
        finally
        {
            // Копии битов значка — наши: Windows их не освобождает.
            if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
        }
    }

    /// <summary>
    /// ЧИСТАЯ часть: положить огонёк в точки. Точки — прямые BGRA сверху вниз, по четыре байта
    /// на точку; результат — обычное наложение «источник поверх» с плавным краем кружка
    /// (край считается по расстоянию до центра, поэтому кружок не выглядит вырубленным топором).
    /// </summary>
    internal static void Compose(byte[] pixels, int cx, int cy, TrayTone tone)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        var radius = OuterRadius(cx);
        var (centerX, centerY) = Center(cx, cy);

        // Сначала обводка, потом сам кружок: обводка остаётся видимым кольцом вокруг огонька.
        Blend(pixels, cx, cy, centerX, centerY, radius, RingColor);
        Blend(pixels, cx, cy, centerX, centerY, radius - 1.0, TrayPalette.Circle(tone));
    }

    /// <summary>Есть ли у значка канал прозрачности: хоть одна точка не полностью прозрачна.</summary>
    internal static bool HasAlphaChannel(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Радиус обводки огонька: чуть меньше четверти ширины значка, но не меньше трёх точек.
    /// Числа подобраны по замеру: при 16 точках кружок получается 5 точек в поперечнике —
    /// видно, но значок не заслонён (замерено: огонёк занимает около 40 точек из 256).
    /// </summary>
    internal static double OuterRadius(int cx) => Math.Max(3.0, cx * 0.22);

    /// <summary>Центр огонька — в правом нижнем углу, с отступом от края.</summary>
    internal static (double X, double Y) Center(int cx, int cy)
    {
        var radius = OuterRadius(cx);
        var margin = radius + 1.0;

        return (cx - margin, cy - margin);
    }

    private static void Blend(byte[] pixels, int cx, int cy, double centerX, double centerY, double radius, uint rgb)
    {
        if (radius <= 0) return;

        var b = (byte)(rgb & 0xFF);
        var g = (byte)((rgb >> 8) & 0xFF);
        var r = (byte)((rgb >> 16) & 0xFF);

        for (var y = 0; y < cy; y++)
        {
            for (var x = 0; x < cx; x++)
            {
                var dx = (x + 0.5) - centerX;
                var dy = (y + 0.5) - centerY;

                // Покрытие точки кружком: 1 внутри, 0 снаружи, между — плавный край.
                var coverage = Math.Clamp((radius + 0.5) - Math.Sqrt((dx * dx) + (dy * dy)), 0.0, 1.0);
                if (coverage <= 0) continue;

                var at = ((y * cx) + x) * 4;

                var db = pixels[at];
                var dg = pixels[at + 1];
                var dr = pixels[at + 2];
                var da = pixels[at + 3];

                pixels[at] = Mix(b, db, coverage);
                pixels[at + 1] = Mix(g, dg, coverage);
                pixels[at + 2] = Mix(r, dr, coverage);
                pixels[at + 3] = (byte)Math.Clamp((int)Math.Round((255 * coverage) + (da * (1 - coverage))), 0, 255);
            }
        }
    }

    private static byte Mix(byte source, byte target, double coverage) =>
        (byte)Math.Clamp((int)Math.Round((source * coverage) + (target * (1 - coverage))), 0, 255);

    /// <summary>Прочитать точки битовой карты значка: 32 бита, сверху вниз, с прозрачностью.</summary>
    private static bool TryReadPixels(IntPtr hbm, int cx, int cy, out byte[] pixels)
    {
        pixels = new byte[cx * cy * 4];

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = cx,
                biHeight = -cy,          // сверху вниз: строка 0 — верх картинки
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,       // BI_RGB
            },
        };

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.GetDIBits(screen, hbm, 0, (uint)cy, pixels, ref info, NativeMethods.DIB_RGB_COLORS) == cy;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Отдать точки обратно Windows значком. Маска строится ПО ПРОЗРАЧНОСТИ (бит 1 — прозрачно):
    /// так значок выглядит правильно и там, где маску читают, а не канал прозрачности.
    /// </summary>
    private static bool TryCreateIcon(byte[] pixels, int cx, int cy, out IntPtr handle, out string detail)
    {
        handle = IntPtr.Zero;

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
        if (screen == IntPtr.Zero)
        {
            detail = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.TrayIconDotBuildFailedFormat, Marshal.GetLastWin32Error());
            return false;
        }

        var hbmColor = IntPtr.Zero;
        var hbmMask = IntPtr.Zero;

        try
        {
            hbmColor = NativeMethods.CreateDIBSection(screen, ref info, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (hbmColor == IntPtr.Zero || bits == IntPtr.Zero)
            {
                detail = string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.TrayIconDotBuildFailedFormat, Marshal.GetLastWin32Error());
                return false;
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);

            hbmMask = NativeMethods.CreateBitmap(cx, cy, 1, 1, BuildMask(pixels, cx, cy));
            if (hbmMask == IntPtr.Zero)
            {
                detail = string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.TrayIconDotBuildFailedFormat, Marshal.GetLastWin32Error());
                return false;
            }

            var icon = new NativeMethods.ICONINFO { fIcon = true, hbmMask = hbmMask, hbmColor = hbmColor };

            handle = NativeMethods.CreateIconIndirect(ref icon);
            if (handle == IntPtr.Zero)
            {
                detail = string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.TrayIconDotBuildFailedFormat, Marshal.GetLastWin32Error());
                return false;
            }

            detail = string.Empty;
            return true;
        }
        finally
        {
            if (hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(hbmMask);

            // Цветную карту удаляем ПОСЛЕ создания значка: Windows копирует её себе.
            if (hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(hbmColor);

            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Одноцветная маска значка: бит 1 — «здесь прозрачно».</summary>
    private static byte[] BuildMask(byte[] pixels, int cx, int cy)
    {
        var stride = ((cx + 31) / 32) * 4;
        var mask = new byte[stride * cy];

        for (var y = 0; y < cy; y++)
        {
            for (var x = 0; x < cx; x++)
            {
                if (pixels[(((y * cx) + x) * 4) + 3] >= 128) continue;

                mask[(y * stride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        return mask;
    }
}
