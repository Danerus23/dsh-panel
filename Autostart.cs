using Microsoft.Win32;

namespace DshTray;

/// <summary>
/// Автозапуск приложения при входе в Windows (HKCU\...\Run). Запись называется DSHPanel;
/// прежнее имя (DeepSeekHarness) читается и заменяется при первом включении — чтобы
/// установка новой версии не показывала автозапуск выключенным.
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DSHPanel";
    private const string LegacyValueName = "DeepSeekHarness";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key == null) return false;

            foreach (var name in new[] { ValueName, LegacyValueName })
            {
                if (!string.IsNullOrWhiteSpace(key.GetValue(name) as string)) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }
}
