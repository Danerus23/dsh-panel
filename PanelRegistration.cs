using Microsoft.Win32;

namespace DshTray;

/// <summary>
/// Панель обновляет себя сама, а версию в «Программах и компонентах» пишет установщик — один раз,
/// при установке. Из-за этого список программ со временем начинает врать: там остаётся версия
/// установщика, а работает уже другая. Для человека это мелочь, но для winget — нет: он сравнивает
/// свою версию с записанной и может предложить «обновление» на выпуск, который СТАРЕЕ работающей
/// панели (и тогда панель тут же предложит обновиться обратно — тот самый цикл, который
/// предостерегает документация winget-pkgs).
///
/// Поэтому панель при старте приводит запись в порядок: пишет в СВОЮ запись одно значение —
/// <c>DisplayVersion</c>. Запись не создаётся, чужие записи не трогаются, ничего кроме этого
/// значения не меняется.
/// </summary>
public static class PanelRegistration
{
    /// <summary>
    /// Запись установщика: Inno заводит её в HKCU как <c>&lt;AppId&gt;_is1</c>, где AppId — тот же
    /// GUID, что в <c>installer\dsh-panel.iss</c> (и его же ищет <c>tools\check-installed.ps1</c>).
    /// </summary>
    private const string ArpKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F3C6A21-9D4E-4B8A-9C1F-2E5D8B7A4C11}_is1";

    /// <summary>Имя, которым запись обязана представиться, чтобы мы считали её своей.</summary>
    private const string ExpectedName = "DSH Panel";

    /// <summary>
    /// Приводит <c>DisplayVersion</c> к версии работающей панели. Возвращает true, если значение
    /// изменилось (это попадает в журнал). Записи нет (портативная сборка), запись чужая или
    /// значение уже верное — тогда ничего не делаем.
    /// </summary>
    public static bool RefreshVersion()
    {
        // Прогон проверки с подменёнными каталогами машинную запись не трогает: приёмочные проверки
        // и проверка мастера поднимают панель в трее, и без этого предохранителя они бы правили
        // «Программы и компоненты» владельца. Тот же признак использует Autostart.cs.
        if (IsCheckRun()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ArpKey, writable: true);
            if (key == null) return false;

            // Пишем только в свою запись: иначе мы бы поправили чужую строку в списке программ.
            if (key.GetValue("DisplayName") as string != ExpectedName) return false;

            var current = AppVersion.Short;
            if (key.GetValue("DisplayVersion") as string == current) return false;

            key.SetValue("DisplayVersion", current);
            return true;
        }
        catch
        {
            // Ключ заперт политикой или прав нет — не беда: в списке программ останется прежняя
            // версия, на работу панели это не влияет.
            return false;
        }
    }

    /// <summary>Это прогон проверки: у панели подменены свои каталоги или имя экземпляра.</summary>
    private static bool IsCheckRun()
    {
        foreach (var name in new[] { "DSH_PANEL_DATA", "DSH_PANEL_STATE", "DSH_PANEL_INSTANCE" })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return true;
        }

        return false;
    }
}
