using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Shell;

namespace DshPanel.Tray;

/// <summary>
/// Пункт меню трея. Модель своя, а не <c>Avalonia.Controls.NativeMenu</c>:
/// тот инструмент привязан к экспортёру Avalonia, а мы рисуем меню средствами Windows.
/// Заодно такую модель тривиально проверять тестами — она не тянет за собой UI.
/// </summary>
public sealed class TrayMenuItem
{
    public string Text { get; init; } = string.Empty;
    public bool IsSeparator { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsChecked { get; init; }
    /// <summary>Жирный пункт по умолчанию (двойной щелчок по значку).</summary>
    public bool IsDefault { get; init; }
    /// <summary>Что сделать при выборе. У разделителя и недоступного пункта не вызывается.</summary>
    public Action? Invoke { get; init; }

    /// <summary>
    /// Тон строки СОСТОЯНИЯ — то, что человек видит цветом (решение владельца 27.09.2026).
    /// <c>null</c> у всего остального: у команды состояния нет, и рисовать её своим цветом
    /// (owner-draw) тоже незачем — команды остаются системными, как были.
    ///
    /// Именно поэтому признак «это строка состояния» — не отдельное поле, а САМ тон: забыть
    /// выдать тон строке и забыть пометить её состоянием — одна и та же ошибка, и двух мест,
    /// где её можно сделать, быть не должно.
    /// </summary>
    public TrayTone? Tone { get; init; }

    public static TrayMenuItem Separator() => new() { IsSeparator = true };

    public static TrayMenuItem Command(string text, Action invoke, bool isEnabled = true, bool isChecked = false, bool isDefault = false)
        => new() { Text = text, Invoke = invoke, IsEnabled = isEnabled, IsChecked = isChecked, IsDefault = isDefault };

    /// <summary>
    /// Строка СОСТОЯНИЯ, а не команда: состояние сервера, агент с балансом, тариф.
    ///
    /// <see cref="IsEnabled"/> = <c>false</c> и <see cref="Invoke"/> = <c>null</c> — намеренно,
    /// и это не «пункт, который забыли доделать»: серая строка в меню Windows значит «показать
    /// можно, нажать нельзя» — ровно то, чем состояние и является. Сделай мы её нажимаемой,
    /// человек однажды нажал бы «Сервер: работает» и не понял, что произошло.
    ///
    /// ⚠️ С 27.09.2026 такая строка рисуется СВОИМИ руками (owner-draw) и потому не серая:
    /// цвет несёт сам <paramref name="tone"/>. Недоступность при этом остаётся — её сторожат
    /// и модель (<see cref="IsEnabled"/>), и флаг <c>MF_GRAYED</c> у Windows.
    ///
    /// ⚠️ Проверки обязаны это сторожить: строка состояния с непустым <see cref="Invoke"/>
    /// — дефект, а не мелочь.
    /// </summary>
    public static TrayMenuItem Info(string text, TrayTone tone) =>
        new() { Text = text, IsEnabled = false, Tone = tone };
}

/// <summary>Меню трея целиком.</summary>
public sealed class TrayMenu
{
    public TrayMenu(IEnumerable<TrayMenuItem> items) => Items = items.ToList();

    public IReadOnlyList<TrayMenuItem> Items { get; }
}
