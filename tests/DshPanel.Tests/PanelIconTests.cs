using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DshPanel.Platform;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Значок панели: тот, что в окнах, и тот, что в трее.
///
/// Проверки здесь — ПРО ПРОИСХОЖДЕНИЕ значка, а не про «нарисовалось что-то». Значок берётся
/// из сборки по адресу, который задан в `DshPanel.csproj` (`Link` и `LogicalName`), и опечатка
/// в этом адресе — самая вероятная поломка: файлы лежат ВНЕ папки проекта, и «глазами» такую
/// ошибку не видно. Поэтому каждая проверка читает ЗНАЧОК, а не факт отсутствия исключения:
/// не тот ресурс — падение.
///
/// Чего здесь нет и почему: путь трея через ОБОЛОЧКУ (значок на рабочем столе владельца) —
/// его проверяет `--tray-selftest` на собранном exe, а не тест: тест не имеет права ставить
/// значок человеку на рабочий стол (красная линия 8). Вид значка в лотке и в проводнике —
/// это видит только человек.
/// </summary>
public class PanelIconTests
{
    // ------------------------------------------------------------- значок окон

    /// <summary>
    /// Адрес ресурса в сборке — не догадка: читаем по нему и смотрим, что ПРИШЛО.
    /// Ошибись адрес (или `Link` в проекте) — здесь будет исключение, а не пустая картинка.
    /// </summary>
    [AvaloniaFact]
    public void Значок_окон_читается_из_сборки_и_несёт_наш_рисунок()
    {
        var bitmap = PanelIcon.Bitmap;

        Assert.Equal(new PixelSize(256, 256), bitmap.PixelSize);

        var (opaque, colors, green) = ReadPixels(bitmap);

        Assert.True(opaque > 0, "в значке нет ни одного непрозрачного пикселя — прочитан не тот ресурс");
        Assert.True(colors > 1, $"в значке всего {colors} цвет(ов) — это заливка, а не рисунок");
        Assert.True(green > 0,
            "в значке нет зелёной точки: прочитан не наш рисунок «пульт» (три полосы и зелёная точка)");
    }

    /// <summary>
    /// Значок достаётся ВСЕМ окнам панели и всем — ОДИН И ТОТ ЖЕ. Второй смысл проверки: окно,
    /// которое забыли подключить, здесь и ловится (`ConfirmStopWindow`, `BackupStopWindow`
    /// и `AboutWindow` — окна редкие, их легко потерять при добавлении нового).
    /// </summary>
    [AvaloniaFact]
    public void Значок_достаётся_всем_окнам_панели()
    {
        var expected = PanelIcon.Window;

        Assert.Same(expected, new MainWindow().Icon);
        Assert.Same(expected, new SettingsWindow().Icon);
        Assert.Same(expected, new BackupWindow().Icon);
        Assert.Same(expected, new BackupStopWindow().Icon);
        Assert.Same(expected, new ConfirmStopWindow().Icon);
        Assert.Same(expected, new AboutWindow().Icon);
    }

    // -------------------------------------------------------------- значок трея

    /// <summary>
    /// Оглавление значка из сборки: девять кадров, и 256-й — PNG. Это не придирка к формату,
    /// а условие работы трея: `CreateIconFromResourceEx` PNG-кадр не принимает, поэтому выбор
    /// кадра для трея обязан опираться на признак PNG — и вот на чём этот признак проверен.
    /// </summary>
    [Fact]
    public void Кадры_значка_сборки_читаются_и_их_девять()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);

        var frames = TrayIconSource.Frames(bytes);

        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, frames.Select(f => f.Width).ToArray());
        Assert.True(frames.Single(f => f.Width == 256).IsPng, "кадр 256 обязан быть PNG — так его и рисует генератор");
        Assert.All(
            frames.Where(f => f.Width < 256),
            f => Assert.False(f.IsPng, $"кадр {f.Width} обязан быть BMP: PNG-кадр трей принять не может"));
    }

    /// <summary>
    /// Кадр 16×16 — это НАШ рисунок: тёмный фон, светлые полосы, зелёная точка. Проверка
    /// разбирает точки BMP вручную (без Windows и без Avalonia) и потому ловит подмену файла:
    /// системный значок приложения ни тёмного фона, ни зелёной точки не несёт.
    /// </summary>
    [Fact]
    public void Кадр_трея_из_сборки_несёт_наш_рисунок()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);

        var frame = TrayIconSource.Frames(bytes).Single(f => f.Width == 16);

        // BMP внутри .ico: BITMAPINFOHEADER, у которого высота ВДВОЕ больше — точки плюс маска.
        Assert.Equal(40u, BitConverter.ToUInt32(bytes, frame.Offset));
        Assert.Equal(16, BitConverter.ToInt32(bytes, frame.Offset + 4));
        Assert.Equal(32, BitConverter.ToInt32(bytes, frame.Offset + 8));

        int green = 0, dark = 0, light = 0;
        var pixels = frame.Offset + 40;

        for (var i = 0; i < 16 * 16; i++)
        {
            var at = pixels + (i * 4);
            int b = bytes[at], g = bytes[at + 1], r = bytes[at + 2];

            if (g > 150 && g > r + 40 && g > b + 40) green++;
            else if (r < 60 && g < 60 && b < 70) dark++;
            else if (r > 180 && g > 180 && b > 180) light++;
        }

        Assert.True(green > 0, "в кадре 16×16 нет зелёной точки — это не наш значок");
        Assert.True(dark > 100, $"в кадре 16×16 всего {dark} тёмных точек — это не наш значок");
        Assert.True(light > 0, "в кадре 16×16 нет светлых полос — это не наш значок");
    }

    /// <summary>
    /// Главное утверждение про трей: значок СОБИРАЕТСЯ Windows из нашего кадра и это НЕ
    /// системный запасной. Сравнение по дескриптору — единственный способ различить два
    /// значка, у которых оба «непустые».
    /// </summary>
    [Fact]
    public void Значок_трея_собирается_из_сборки_и_он_не_системный()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);

        Assert.True(
            TrayIconSource.TryLoad(bytes, 16, 16, out var handle, out var detail),
            $"значок не собрался: {detail}");

        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(TrayIconSource.IsSystemIcon(handle), "трей получил системный запасной значок вместо своего");

            // Кадр-ИСТОЧНИК назван первым в объяснении: лотку нужен кадр, нарисованный в 16 точках,
            // а не 256-й PNG, сжатый в 16. Проверка пришпиливает именно источник — мутация M3
            // (взять последний кадр) не роняла здесь НИЧЕГО, пока этой строки не было.
            Assert.StartsWith("кадр 16×16 →", detail);
        }
        finally
        {
            Assert.True(NativeMethods.DestroyIcon(handle), "дескриптор своего значка обязан освобождаться");
        }
    }

    /// <summary>
    /// Тот же путь, но через сам трей — и БЕЗ показа значка человеку: `Show()` здесь не зовётся,
    /// поэтому на рабочем столе владельца ничего не появляется (красная линия 8).
    /// </summary>
    [Fact]
    public void Трей_ставит_свой_значок_из_сборки()
    {
        using var tray = new TrayIconHost("проверка значка");

        var line = tray.SetOwnIcon();

        Assert.Contains("свой", line);
        Assert.False(TrayIconSource.IsSystemIcon(tray.IconHandle), "трей остался на системном значке");
        Assert.False(tray.IsAdded, "проверка не имеет права ставить значок на рабочий стол владельца");
    }

    // ------------------------------------------------------------------- отказы

    /// <summary>
    /// Значка нет (ресурс не вшит, сборка без него) — трей НЕ падает и берёт системный запасной.
    /// Именно этот отказ делает «свой значок» безопасным требованием: лоток обязан работать
    /// всегда, а не только когда всё сошлось.
    /// </summary>
    [Fact]
    public void Без_значка_трей_не_падает_и_остаётся_со_запасным()
    {
        using var tray = new TrayIconHost("проверка значка");

        var line = tray.SetIconFromOwnBytes(null);

        Assert.Contains("запасной", line);
        Assert.True(TrayIconSource.IsSystemIcon(tray.IconHandle), "без своего значка трей обязан взять системный");
        Assert.False(tray.IsAdded, "проверка не имеет права ставить значок на рабочий стол владельца");
    }

    /// <summary>
    /// Вместо значка — мусор. Ответ обязан быть «не вышло» с причиной, а не исключением
    /// и не молчанием: значок приходит из сборки, и испорченный ресурс — это отказ, а не повод
    /// уронить панель.
    /// </summary>
    [Fact]
    public void Мусор_вместо_значка_называет_причину()
    {
        Assert.False(TrayIconSource.TryLoad(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 16, 16, out var handle, out var detail));

        Assert.Equal(IntPtr.Zero, handle);
        Assert.NotEqual(string.Empty, detail);
    }

    /// <summary>
    /// Кадр выбирается ПОД РАЗМЕР лотка, а не «какой есть». Это не придирка: 256-й кадр в 16
    /// точках — мыло, и подмена выбора первым попавшимся кадром ломает вид значка, ничего
    /// не ломая в работе. Проверка называет и границы: ближайший БОЛЬШИЙ, а крупнее 128 — нет.
    /// </summary>
    [Fact]
    public void Кадр_для_трея_берётся_в_размере_лотка_а_не_первым_попавшимся()
    {
        var frames = TrayIconSource.Frames(TrayIconSource.LoadEmbedded());
        Assert.Equal(9, frames.Count);

        Assert.Equal(16, TrayIconSource.Pick(frames, 16)!.Value.Width);
        Assert.Equal(24, TrayIconSource.Pick(frames, 24)!.Value.Width);
        Assert.Equal(24, TrayIconSource.Pick(frames, 21)!.Value.Width);    // такого кадра нет — берём ближайший больший
        Assert.Equal(128, TrayIconSource.Pick(frames, 300)!.Value.Width);  // крупнее нет — самый большой BMP
        Assert.False(TrayIconSource.Pick(frames, 256)!.Value.IsPng);       // PNG первым не берётся
    }

    /// <summary>
    /// Значок, в котором ЕСТЬ ТОЛЬКО PNG-кадр, всё равно даёт наш значок. Раньше здесь стояло
    /// утверждение «такой кадр Windows не примет» — и оно оказалось ЛОЖЬЮ: замер 26.09.2026
    /// (мутация M3) показал, что `CreateIconFromResourceEx` собирает HICON и из PNG. Поэтому
    /// проверка переписана на ИЗМЕРЕННОЕ поведение, а не на вычитанное: PNG — последний выход,
    /// и он обязан работать, иначе значок от чужого генератора кончился бы системным запасным.
    /// </summary>
    [Fact]
    public void Значок_из_одного_PNG_кадра_всё_равно_наш()
    {
        var bytes = TrayIconSource.LoadEmbedded();
        Assert.NotNull(bytes);

        var png = TrayIconSource.Frames(bytes).Single(f => f.IsPng);
        var pngBytes = new byte[png.Length];
        Array.Copy(bytes, png.Offset, pngBytes, 0, png.Length);

        var ico = BuildIco(256, pngBytes);
        Assert.True(TrayIconSource.Frames(ico).Single().IsPng);

        Assert.True(TrayIconSource.TryLoad(ico, 16, 16, out var handle, out var detail), detail);

        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(TrayIconSource.IsSystemIcon(handle), "из PNG-кадра вышел системный значок");
            Assert.StartsWith("кадр 256×256 →", detail);
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Оглавление настоящее, а точки — мусор: Windows такой кадр не собирает (замер 26.09.2026:
    /// нули, последовательность 1..64 и «заголовок без точек» дают ровно ноль). Требование —
    /// назвать причину словами и НЕ уронить трей: этот путь отказа иначе не проверен ничем.
    /// </summary>
    [Fact]
    public void Кадр_которого_Windows_не_собрала_называет_причину()
    {
        var ico = BuildIco(16, new byte[64]);

        Assert.False(TrayIconSource.TryLoad(ico, 16, 16, out var handle, out var detail));

        Assert.Equal(IntPtr.Zero, handle);
        Assert.StartsWith("Windows не собрала значок", detail);
    }

    /// <summary>Собрать значок из одного кадра — так проверяются крайние случаи формата.</summary>
    private static byte[] BuildIco(int size, byte[] frame)
    {
        var ico = new byte[6 + 16 + frame.Length];
        ico[2] = 1;                                           // тип: значок
        ico[4] = 1;                                           // кадров: один
        ico[6] = (byte)(size >= 256 ? 0 : size);              // 0 в поле размера означает 256
        ico[7] = (byte)(size >= 256 ? 0 : size);
        ico[10] = 1;                                          // плоскостей
        ico[12] = 32;                                         // бит на точку
        BitConverter.GetBytes(frame.Length).CopyTo(ico, 14);   // bytesInRes
        BitConverter.GetBytes(22).CopyTo(ico, 18);             // imageOffset: сразу за оглавлением
        frame.CopyTo(ico, 22);
        return ico;
    }

    // ------------------------------------------------------------------ служебное

    /// <summary>
    /// Прочитать точки картинки: непрозрачные, различные цвета и «зелёные» (точка значка).
    /// Читаем через промежуточный буфер, а не «на глаз»: проверка обязана видеть ПИКСЕЛИ.
    /// </summary>
    private static (long Opaque, int Colors, long Green) ReadPixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        long opaque = 0;
        long green = 0;
        var colors = new System.Collections.Generic.HashSet<int>();

        using (var buffer = target.Lock())
        {
            bitmap.CopyPixels(buffer);

            var bytes = new byte[buffer.RowBytes * size.Height];
            Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    var at = (y * buffer.RowBytes) + (x * 4);
                    int b = bytes[at], g = bytes[at + 1], r = bytes[at + 2], a = bytes[at + 3];

                    colors.Add((r << 16) | (g << 8) | b);
                    if (a == 0) continue;

                    opaque++;
                    if (g > 150 && g > r + 40 && g > b + 40) green++;
                }
            }
        }

        return (opaque, colors.Count, green);
    }
}
