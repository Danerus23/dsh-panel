using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using DshPanel.Platform;
using DshPanel.Shell;

namespace DshPanel.Tray;

/// <summary>
/// Откуда трей берёт НАШ значок — и почему именно так.
///
/// **Значок берётся из СБОРКИ, а не из файла рядом.** Панель раздаётся ОДНИМ `exe`: рядом с ним
/// ни `.ico`, ни папки `assets` нет, и требование владельца 26.09.2026 звучит ровно так — «значок
/// свой, не системный». Байты `.ico` вшиты в сборку (`EmbeddedResource`, имя ресурса задано
/// в проекте явно), поэтому значок едет вместе с панелью куда угодно.
///
/// **Почему не Win32-ресурс самого `exe`** (`LoadImage(GetModuleHandle(null), "#<номер>", …)`) —
/// способ рабочий, но у него две беды. Первая: номер ресурса — не контракт, а следствие того,
/// как сборщик положил значок; его пришлось бы узнавать перечислением и надеяться, что он не
/// изменится. Вторая, решающая: под проверками (`dotnet test`) `GetModuleHandle(null)` — это
/// `testhost.exe`, а не панель, и такой путь **невозможно проверить прогоном**: он «зеленел» бы
/// на пустом месте. Байты из сборки читаются одинаково и в панели, и в проверке.
///
/// **Почему из кадра BMP, а не PNG — и почему это НЕ запрет Windows.** Замер 26.09.2026
/// (мутация M3): `CreateIconFromResourceEx` принимает и сжатый PNG-кадр — вопреки тому, что
/// написано в справочниках и стояло в этом файле раньше. Значит PNG не отвергается: он берётся,
/// когда BMP-кадров в значке нет вовсе. Но ПЕРВЫМ он не берётся, и довод другой: единственный
/// PNG нашего значка — 256 точек, а сжатый в 16 точек лотка он даёт мыло там, где рядом лежит
/// кадр, нарисованный ровно в 16.
///
/// **При любой неудаче — системный запасной значок** и причина словами: трей обязан работать
/// всегда, даже если значок не собрался (`TrayIconHost.SetIconFromOwnBytes`).
/// </summary>
internal static class TrayIconSource
{
    /// <summary>Имя ресурса в сборке — задано в `DshPanel.csproj` через `LogicalName`.</summary>
    internal const string ResourceName = "DshPanel.Assets.dsh-panel.ico";

    /// <summary>Сигнатура PNG: по ней кадр признаётся тем, который Windows не примет.</summary>
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Один кадр внутри `.ico`: где он лежит и какого он размера.</summary>
    internal readonly record struct Frame(int Width, int Height, int Offset, int Length, bool IsPng);

    /// <summary>Байты значка из сборки. <c>null</c> — ресурса нет (сборка собрана без него).</summary>
    internal static byte[]? LoadEmbedded()
    {
        using var stream = typeof(TrayIconSource).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Разобрать оглавление `.ico`. Пустой список означает «это не значок» — и это ответ,
    /// а не исключение: значок приходит из сборки, и мусор в нём обязан кончаться запасным
    /// системным, а не падением трея.
    /// </summary>
    internal static IReadOnlyList<Frame> Frames(byte[]? ico)
    {
        // Заголовок: reserved(2) + тип(2) + число кадров(2). Тип 1 — значок, 2 — курсор.
        if (ico is null || ico.Length < 6) return Array.Empty<Frame>();
        if (BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1) return Array.Empty<Frame>();

        int count = BitConverter.ToUInt16(ico, 4);
        if (count <= 0 || ico.Length < 6 + (16 * count)) return Array.Empty<Frame>();

        var frames = new List<Frame>(count);
        for (var i = 0; i < count; i++)
        {
            var at = 6 + (16 * i);

            // 0 в поле размера означает 256: в байт больше не влезает (так устроен формат).
            var width = ico[at] == 0 ? 256 : ico[at];
            var height = ico[at + 1] == 0 ? 256 : ico[at + 1];
            var length = (int)BitConverter.ToUInt32(ico, at + 8);
            var offset = (int)BitConverter.ToUInt32(ico, at + 12);

            // Кадр, вылезающий за файл, — признак битого значка: молча пропускаем, но оглавление
            // в этом случае не считаем годным вовсе (иначе легко «выбрать» кадр из мусора).
            if (length <= 0 || offset < 0 || offset + length > ico.Length) return Array.Empty<Frame>();

            frames.Add(new Frame(width, height, offset, length, IsPng(ico, offset)));
        }

        return frames;
    }

    /// <summary>
    /// Кадр под нужный размер: самый маленький из тех, что НЕ МЕНЬШЕ запрошенного (уменьшать
    /// резче, чем нарисовано, — это мыло), а если таких нет — самый большой.
    ///
    /// **BMP впереди PNG, и причина НЕ в Windows.** Замер 26.09.2026 (мутация M3) показал:
    /// <c>CreateIconFromResourceEx</c> принимает и сжатый PNG-кадр — вопреки справочникам
    /// и тому, что было написано здесь раньше. Поэтому PNG НЕ отвергается: если BMP-кадров нет
    /// (значок собран чужим инструментом), берётся он. Но первым — не он: наш единственный PNG
    /// это 256 точек, и сжатый в 16 он даёт мыло там, где рядом лежит кадр ровно в 16.
    /// </summary>
    internal static Frame? Pick(IReadOnlyList<Frame> frames, int size) =>
        Choose(frames.Where(frame => !frame.IsPng).ToArray(), size) ?? Choose(frames, size);

    /// <summary>Выбор размера из готового набора: ближайший БОЛЬШИЙ, а если таких нет — самый большой.</summary>
    private static Frame? Choose(IReadOnlyList<Frame> frames, int size)
    {
        Frame? best = null;

        foreach (var frame in frames)
        {
            if (frame.Width < size) continue;
            if (best is null || frame.Width < best.Value.Width) best = frame;
        }

        if (best is not null) return best;

        foreach (var frame in frames)
        {
            if (best is null || frame.Width > best.Value.Width) best = frame;
        }

        return best;
    }

    /// <summary>
    /// Собрать HICON из кадра. Возвращает «получилось ли» и строку-объяснение — она уезжает
    /// в журнал панели, поэтому называет и кадр, и причину отказа.
    /// </summary>
    internal static bool TryLoad(byte[]? ico, int cx, int cy, out IntPtr handle, out string detail)
    {
        handle = IntPtr.Zero;

        if (ico is null)
        {
            detail = PanelStrings.TrayIconNoResource;
            return false;
        }

        var frames = Frames(ico);
        if (frames.Count == 0)
        {
            detail = PanelStrings.TrayIconBytesBroken;
            return false;
        }

        var picked = Pick(frames, cx > 0 ? cx : 16);
        if (picked is null)
        {
            detail = PanelStrings.TrayIconNoFrames;
            return false;
        }

        var frame = picked.Value;
        var data = new byte[frame.Length];
        Array.Copy(ico, frame.Offset, data, 0, frame.Length);

        handle = NativeMethods.CreateIconFromResourceEx(
            data, (uint)data.Length, fIcon: true, NativeMethods.IconResourceVersion, cx, cy,
            NativeMethods.LR_DEFAULTCOLOR);

        if (handle == IntPtr.Zero)
        {
            // Код ошибки называем, только если он есть: у этой функции ноль — это не «всё хорошо»,
            // а «ошибку не выставили», и строка «код 0» читалась бы как успех.
            var code = Marshal.GetLastWin32Error();
            detail = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.TrayIconBuildFailedFormat,
                frame.Width, frame.Height, frame.Length)
                + (code != 0
                    ? string.Format(CultureInfo.CurrentCulture, PanelStrings.TrayIconBuildFailedCodeFormat, code)
                    : string.Empty);
            return false;
        }

        // Кадр-ИСТОЧНИК называется первым и стоит до стрелки: по нему проверка видит, что для лотка
        // взят кадр нужного размера, а не первый попавшийся (мутация M3 нашла ровно эту дыру).
        detail = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.TrayIconFrameFormat,
            frame.Width, frame.Height, cx, cy, frames.Count);
        return true;
    }

    /// <summary>
    /// Тот ли это дескриптор, что у системного запасного значка. Нужен проверкам и самотесту:
    /// «наш значок» и «запасной» — разные утверждения, и подмена одного другим обязана быть видна.
    /// </summary>
    internal static bool IsSystemIcon(IntPtr handle) =>
        handle != IntPtr.Zero
        && handle == NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);

    private static bool IsPng(byte[] data, int offset)
    {
        if (offset < 0 || offset + PngSignature.Length > data.Length) return false;

        for (var i = 0; i < PngSignature.Length; i++)
        {
            if (data[offset + i] != PngSignature[i]) return false;
        }

        return true;
    }
}
