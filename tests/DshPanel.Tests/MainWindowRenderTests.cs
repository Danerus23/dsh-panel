using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки интерфейса БЕЗ экрана. Смысл не в том, чтобы «тест прошёл», а в том,
/// чтобы поймать пустой или сломанный кадр: проверяем ПИКСЕЛИ, а не факт отсутствия
/// исключения. Кадр одного цвета или без непрозрачных пикселей — провал.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class MainWindowRenderTests
{
    private static (int Width, int Height, long Opaque, int Colors) Render()
    {
        var window = new MainWindow();
        window.Show();

        // Дать вёрстке и очереди диспетчера отработать до конца.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var w = (int)Math.Ceiling(window.ClientSize.Width);
        var h = (int)Math.Ceiling(window.ClientSize.Height);
        Assert.True(w > 0 && h > 0, $"пустой размер окна {w}x{h}");

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);

        var stride = w * 4;
        var buf = new byte[stride * h];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
        finally { handle.Free(); }

        long opaque = 0;
        var colors = new HashSet<int>();
        for (var i = 0; i + 3 < buf.Length; i += 4)
        {
            if (buf[i + 3] != 0) opaque++;
            colors.Add((buf[i] << 16) | (buf[i + 1] << 8) | buf[i + 2]);
        }

        return (w, h, opaque, colors.Count);
    }

    [AvaloniaFact]
    public void Главное_окно_рисуется_непустым_кадром()
    {
        var (w, h, opaque, colors) = Render();

        Assert.True(opaque > 0, "в кадре нет ни одного непрозрачного пикселя — окно не нарисовалось");
        Assert.True(colors > 1, $"в кадре всего {colors} цвет(ов) — это заливка, а не интерфейс");

        // Интерфейс занимает разумную долю кадра: если вёрстка «схлопнулась»,
        // непрозрачных пикселей будет подозрительно мало.
        var total = (long)w * h;
        Assert.True(opaque >= total / 2, $"непрозрачных всего {opaque} из {total} — похоже, вёрстка схлопнулась");
    }

    [AvaloniaFact]
    public void Кадр_воспроизводим_между_запусками()
    {
        var first = Render();
        var second = Render();

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Opaque, second.Opaque);
    }
}
