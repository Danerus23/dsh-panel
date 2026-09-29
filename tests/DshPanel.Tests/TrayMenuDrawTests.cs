using System;
using DshPanel.Shell;
using DshPanel.Tray;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Строки состояния в меню значка рисуются СВОИМИ руками (owner-draw): цветной кружок и цветной
/// текст. Решение владельца 27.09.2026: серые строки «сливались в одно».
///
/// Проверки здесь — про РАЗМЕР пункта, и это не придирка: <c>WM_MEASUREITEM</c> обязан вернуть
/// правдоподобную высоту и ширину, иначе меню поедет — строка накроет соседний пункт или вылезет
/// за край. Проверяется сначала чистая раскладка (числами), потом настоящий шрифт меню от Windows.
///
/// Чего здесь нет: КАК это выглядит на экране. Цвет и вид меню живьём снимает
/// <c>--tray-selftest</c> вместе со скриптом кадра, а решение о цветах проверяется
/// <see cref="TrayPaletteTests"/>.
/// </summary>
public class TrayMenuDrawTests
{
    /// <summary>
    /// Раскладка строки: кружок внутри пункта, текст правее кружка, высота не меньше шрифта.
    /// Числа названы прямо — это договор с Windows о размере пункта, а не украшение.
    /// </summary>
    [Fact]
    public void Строка_вмещает_кружок_и_текст()
    {
        var layout = TrayLineLayout.For(textWidth: 120, textHeight: 16);

        Assert.Equal(12, layout.CircleSize);              // 16 − 4: кружок чуть ниже строчных букв
        Assert.Equal(3, layout.CircleLeft);               // отступ от левого края пункта
        Assert.Equal(21, layout.TextLeft);                // 3 + 12 + 6
        Assert.Equal(20, layout.Height);                  // 12 + 2·2 (высота шрифта 16 меньше)
        Assert.Equal(4, layout.CircleTop);                // кружок по центру строки

        // Текст и кружок не пересекаются, и всё это внутри ширины пункта.
        Assert.True(layout.TextLeft >= layout.CircleLeft + layout.CircleSize, "текст налез на кружок");
        Assert.True(layout.Width > layout.TextLeft + 120, "ширина пункта не оставляет места справа");
        Assert.True(layout.CircleTop + layout.CircleSize <= layout.Height, "кружок вылез за строку");
    }

    /// <summary>
    /// Ширина растёт вместе с надписью: «Сервер: не запущен» и «Сервер: работает · порт 3080 ·
    /// поднят не панелью» — это разные пункты, и один размер на оба обрезал бы длинный.
    /// </summary>
    [Fact]
    public void Ширина_растёт_вместе_с_надписью()
    {
        var shortLine = TrayLineLayout.For(textWidth: 40, textHeight: 16);
        var longLine = TrayLineLayout.For(textWidth: 400, textHeight: 16);

        Assert.True(longLine.Width > shortLine.Width);

        // Высота при этом одна и та же: строки состояния обязаны быть одной высоты, иначе
        // шапка меню выглядит рваной.
        Assert.Equal(shortLine.Height, longLine.Height);
    }

    /// <summary>
    /// Кружок не вырождается в точку и не растёт до размера значка: у высокого шрифта (монитор
    /// с масштабом) он крупнее, у мелкого — не меньше семи точек, иначе его в меню не разглядеть.
    /// </summary>
    [Fact]
    public void Кружок_не_меньше_семи_точек_и_растёт_со_шрифтом()
    {
        Assert.Equal(TrayLineLayout.MinCircle, TrayLineLayout.For(10, 4).CircleSize);
        Assert.True(TrayLineLayout.For(10, 32).CircleSize > TrayLineLayout.For(10, 16).CircleSize);
    }

    /// <summary>
    /// Рисовальщик берёт настоящий шрифт меню у Windows и меряет им: высота строки не выдумана
    /// числом, она приходит от системы. Здесь же проверяется, что измерение вообще работает —
    /// нулевая ширина у непустой надписи означала бы пустой пункт.
    /// </summary>
    [Fact]
    public void Рисовальщик_меряет_надпись_системным_шрифтом_меню()
    {
        Assert.True(TrayMenuDraw.TryCreate(out var draw, out var detail), detail);

        using (draw)
        {
            Assert.NotNull(draw);
            Assert.True(draw!.TextHeight > 0, "система не назвала высоту шрифта меню");

            var shortWidth = draw.TextWidth("Сервер");
            var longWidth = draw.TextWidth("Сервер: работает · порт 3080 · поднят не панелью");

            Assert.True(shortWidth > 0, "надпись измерилась нулевой шириной");
            Assert.True(longWidth > shortWidth, "длинная надпись измерилась не длиннее короткой");

            // Раскладка берёт размеры у того же шрифта, что и отрисовка.
            var layout = draw.LayoutOf("Сервер");
            Assert.Equal(layout.Height, draw.LayoutOf("Сервер: не запущен").Height);
            Assert.Equal(layout.Width, draw.TextWidth("Сервер") + layout.TextLeft + TrayLineLayout.RightPad);
        }
    }

    /// <summary>
    /// Строка состояния РИСУЕТСЯ: в контексте в памяти появляется кружок цвета тона и текст.
    ///
    /// Эта проверка заведена по живому случаю: <c>DrawText</c> был объявлен в <c>gdi32</c>,
    /// а живёт в <c>user32</c>, и панель падала при первом показе меню. Проверки этого не видели,
    /// потому что до отрисовки не доходили — а прогон, который рисует строку так же, как её просит
    /// Windows, видит это сразу.
    /// </summary>
    [Fact]
    public void Строка_рисуется_в_контекст_и_красит_кружок_цветом_тона()
    {
        Assert.True(TrayMenuDraw.TryCreate(out var draw, out var detail), detail);

        using (draw)
        {
            var data = draw!.Add("Сервер: не запущен", TrayTone.Bad);
            var layout = draw.LayoutOf("Сервер: не запущен");

            var width = layout.Width + 40;
            var height = layout.Height;

            var surface = new Surface(width, height);

            try
            {
                var item = default(DshPanel.Platform.NativeMethods.DRAWITEMSTRUCT);
                item.CtlType = DshPanel.Platform.NativeMethods.ODT_MENU;
                item.hDC = surface.Dc;
                item.rcItem = new DshPanel.Platform.NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
                item.itemData = data;

                Assert.True(draw.Draw(ref item), "строка состояния не нарисовалась");

                // Кружок — ровно цвет тона: он залит кистью, без сглаживания.
                var circle = TrayPalette.Circle(TrayTone.Bad);
                var circleX = layout.CircleLeft + (layout.CircleSize / 2);
                var circleY = layout.CircleTop + (layout.CircleSize / 2);

                Assert.Equal(circle, surface.Pixel(circleX, circleY));

                // И текст на месте: правее кружка есть точки, которых не было до отрисовки.
                var textPixels = 0;

                for (var y = 0; y < height; y++)
                {
                    for (var x = layout.TextLeft; x < width; x++)
                    {
                        if (surface.Pixel(x, y) != surface.Background) textPixels++;
                    }
                }

                Assert.True(textPixels > 0, "текст строки состояния не нарисован вовсе");
            }
            finally
            {
                surface.Dispose();
            }
        }
    }

    /// <summary>
    /// Пункт, которого нет в этом меню, не измеряется и не рисуется: ответ «не наш» — это
    /// не пустой размер и не падение. Без такой двери чужое значение <c>itemData</c> (или старое
    /// меню) увело бы отрисовку в никуда.
    /// </summary>
    [Fact]
    public void Чужой_пункт_не_измеряется_и_не_рисуется()
    {
        Assert.True(TrayMenuDraw.TryCreate(out var draw, out var detail), detail);

        using (draw)
        {
            var item = default(DshPanel.Platform.NativeMethods.MEASUREITEMSTRUCT);
            item.CtlType = DshPanel.Platform.NativeMethods.ODT_MENU;
            item.itemData = new IntPtr(42);           // такого пункта в меню нет

            Assert.False(draw!.Measure(ref item));
            Assert.Equal(0u, item.itemWidth);
            Assert.Equal(0u, item.itemHeight);
        }
    }

    /// <summary>
    /// Строка состояния, добавленная в рисовальщик, получает СВОЁ значение для <c>itemData</c>,
    /// и по нему же находится. Значения не повторяются: два одинаковых значения — это два пункта,
    /// которые нарисуются одинаково (то есть один из них — неправдой).
    /// </summary>
    [Fact]
    public void Строки_находятся_по_своему_значению()
    {
        Assert.True(TrayMenuDraw.TryCreate(out var draw, out var detail), detail);

        using (draw)
        {
            var first = draw!.Add("первая", TrayTone.Good);
            var second = draw.Add("вторая", TrayTone.Bad);

            Assert.Equal(2, draw.Count);
            Assert.NotEqual(first, second);
            Assert.True(first.ToInt64() > 0 && second.ToInt64() > 0, "ноль оставлен за «это не наш пункт»");

            // Обе строки измеряются — то есть обе находятся.
            foreach (var data in new[] { first, second })
            {
                var item = default(DshPanel.Platform.NativeMethods.MEASUREITEMSTRUCT);
                item.CtlType = DshPanel.Platform.NativeMethods.ODT_MENU;
                item.itemData = data;

                Assert.True(draw.Measure(ref item));
                Assert.True(item.itemHeight > 0);
                Assert.True(item.itemWidth > 0);
            }
        }
    }

    /// <summary>
    /// Контекст в памяти с точками: на нём проверка ВИДИТ, что нарисовала строка состояния.
    /// Фон залит цветом пункта — как его залила бы Windows перед вызовом owner-draw.
    /// </summary>
    private sealed class Surface : IDisposable
    {
        private readonly IntPtr _screen;
        private readonly IntPtr _dib;

        internal Surface(int cx, int cy)
        {
            var info = new DshPanel.Platform.NativeMethods.BITMAPINFO
            {
                bmiHeader = new DshPanel.Platform.NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DshPanel.Platform.NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = cx,
                    biHeight = -cy,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            Width = cx;
            Height = cy;

            _screen = DshPanel.Platform.NativeMethods.GetDC(IntPtr.Zero);
            _dib = DshPanel.Platform.NativeMethods.CreateDIBSection(_screen, ref info, DshPanel.Platform.NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            Bits = bits;

            Dc = DshPanel.Platform.NativeMethods.CreateCompatibleDC(_screen);
            DshPanel.Platform.NativeMethods.SelectObject(Dc, _dib);

            // Фон — цвет меню, тот же, каким его заливает сама строка состояния.
            var menu = TrayPalette.ToRgb(DshPanel.Platform.NativeMethods.GetSysColor(DshPanel.Platform.NativeMethods.COLOR_MENU));
            Background = menu;

            var pixels = new byte[cx * cy * 4];
            for (var i = 0; i < cx * cy; i++)
            {
                pixels[(i * 4) + 0] = (byte)(menu & 0xFF);
                pixels[(i * 4) + 1] = (byte)((menu >> 8) & 0xFF);
                pixels[(i * 4) + 2] = (byte)((menu >> 16) & 0xFF);
                pixels[(i * 4) + 3] = 255;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, Bits, pixels.Length);
        }

        internal IntPtr Dc { get; }

        internal IntPtr Bits { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal uint Background { get; }

        internal uint Pixel(int x, int y)
        {
            var at = (((y * Width) + x) * 4);
            var pixels = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(Bits + at, pixels, 0, 4);

            return ((uint)pixels[2] << 16) | ((uint)pixels[1] << 8) | pixels[0];
        }

        public void Dispose()
        {
            DshPanel.Platform.NativeMethods.DeleteDC(Dc);
            DshPanel.Platform.NativeMethods.DeleteObject(_dib);
            DshPanel.Platform.NativeMethods.ReleaseDC(IntPtr.Zero, _screen);
        }
    }
}
