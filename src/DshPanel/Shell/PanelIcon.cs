using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DshPanel.Shell;

/// <summary>
/// Значок панели в ОКНАХ — одна точка правды на все шесть окон.
///
/// Зачем отдельный тип, а не строка в каждом окне: значок читается из сборки по адресу, который
/// задаётся в `DshPanel.csproj` метаданными `Link` (файл лежит ВНЕ папки проекта). Ошибись адрес
/// в шести местах — и «значок пропал» придётся искать в шести файлах; здесь он один, и промах
/// виден прогоном (`PanelIconTests`).
///
/// ⚠️ **Формат — PNG, и это не вкусовщина.** ICO Avalonia не читает вовсе: у Skia нет его кодека.
/// Поэтому значок окон и значок трея — два файла из одного источника (`tools\make-icon.ps1`):
/// трей берёт ICO (`Tray\TrayIconSource.cs`), окна — PNG.
///
/// ⚠️ **Значок окна не роняет окно молча.** Если ресурса в сборке нет, чтение бросает исключение
/// прямо в построении окна, и это правильно: значок, которого нет, — дефект сборки, а не «мелкая
/// неудача», и проверки обязаны его поймать до человека. Прикрывать это пустым значком значило бы
/// прятать поломку.
/// </summary>
public static class PanelIcon
{
    /// <summary>
    /// Адрес значка внутри сборки. Собран из имени сборки (`AssemblyName` = `DshPanel`) и `Link`
    /// из проекта — то есть является частью сборки, а не догадкой: проверяется прогоном.
    /// </summary>
    public const string ResourceUri = "avares://DshPanel/Assets/dsh-panel.png";

    /// <summary>Исходный файл — для отчёта проверок и человека, читающего код.</summary>
    public const string SourceFile = @"assets\dsh-panel.png";

    private static Bitmap? _bitmap;
    private static WindowIcon? _windowIcon;

    /// <summary>
    /// Сама картинка 256×256. Читается один раз на процесс: значок нужен всем окнам один и тот же,
    /// а каждое чтение с диска — это ещё и второй декод PNG.
    /// </summary>
    public static Bitmap Bitmap => _bitmap ??= Load();

    /// <summary>Значок для <see cref="Window.Icon"/>. Тоже один на процесс.</summary>
    public static WindowIcon Window => _windowIcon ??= new WindowIcon(Bitmap);

    private static Bitmap Load()
    {
        using var stream = AssetLoader.Open(new Uri(ResourceUri));
        return new Bitmap(stream);
    }
}
