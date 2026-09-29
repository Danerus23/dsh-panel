using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Окно «Копии» проверяется БЕЗ экрана и по ПИКСЕЛЯМ, как главное окно: смысл не в том,
/// чтобы «тест прошёл», а в том, чтобы поймать пустой или схлопнувшийся кадр. Критерий этапа —
/// «видно на экране», и он обязан доказываться машинно, а не словами.
///
/// Окно строится в состоянии прогона проверки (<c>allowed: false</c>): это единственное
/// состояние, в котором проверка имеет право его построить, и оно же попадает на кадр README
/// (<c>--shot файл backup</c>).
/// </summary>
public class BackupWindowRenderTests
{
    private static (int Width, int Height, long Opaque, int Colors) Render()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-backup-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Func<DshEngine?> noEngine = () => null;

            var control = new BackupController(
                AppPaths.Under(root),
                () => new PanelSettings { BackupFolder = Path.Combine(root, "Копии") },
                allowed: false,
                locateEngine: noEngine);

            var window = new BackupWindow();
            window.Attach(control);

            // До выбора копии панель наката скрыта: панель ещё ничего не читала и не должна
            // выглядеть так, будто она что-то собирается разложить.
            Assert.Null(window.Plan);
            Assert.Equal(0, window.ListedCount);
            Assert.False(window.Busy);

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
        finally
        {
            try { Directory.Delete(root, true); } catch { /* уборка не должна ронять проверку */ }
        }
    }

    [AvaloniaFact]
    public void Окно_копий_рисуется_непустым_кадром()
    {
        var (w, h, opaque, colors) = Render();

        Assert.True(opaque > 0, "в кадре нет ни одного непрозрачного пикселя — окно не нарисовалось");
        Assert.True(colors > 1, $"в кадре всего {colors} цвет(ов) — это заливка, а не интерфейс");

        var total = (long)w * h;
        Assert.True(opaque >= total / 2, $"непрозрачных всего {opaque} из {total} — похоже, вёрстка схлопнулась");
    }

    [AvaloniaFact]
    public void Кадр_окна_копий_воспроизводим_между_запусками()
    {
        var first = Render();
        var second = Render();

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Opaque, second.Opaque);
    }
}
