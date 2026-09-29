using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DshPanel.Platform;

/// <summary>
/// Объявления Win32 для реестра — нужны автозапуску панели (<c>HKCU\...\Run</c>).
///
/// Почему свой P/Invoke, а не пакет <c>Microsoft.Win32.Registry</c>: в проекте намеренно
/// минимум зависимостей (взяты <c>Avalonia.Win32</c> + <c>Avalonia.Skia</c> вместо мета-пакета),
/// а каждый новый пакет — это два замка <c>packages.lock.json</c> и пересчёт чисел графа,
/// записанных в документах. Своего значка в трее это уже касалось: он тоже на Win32.
///
/// Здесь ТОЛЬКО обмен с системой. Разбор значений, решения и тексты — в <c>Autostart\</c>:
/// так их можно проверить тестами, не трогая реестр вовсе.
/// </summary>
internal static class RegistryNative
{
    /// <summary>Ветка текущего пользователя: панель пишет только в неё и прав администратора не требует.</summary>
    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorMoreData = 234;

    private const int KeyQueryValue = 0x0001;
    private const int KeySetValue = 0x0002;
    private const int KeyRead = 0x20019;

    private const int RegSz = 1;
    private const int RegExpandSz = 2;

    /// <summary>Разделитель ветки в записи проб и журнале: <c>HKCU\Software\...</c>.</summary>
    public const string HivePrefix = @"HKCU\";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyEx(
        IntPtr hKey, string lpSubKey, int reserved, string? lpClass, int dwOptions,
        int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueEx(
        IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType, byte[]? lpData, ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int reserved, int dwType, byte[] lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValue(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteKey(IntPtr hKey, string lpSubKey);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    /// <summary>Открыть ветку на чтение. <see cref="IntPtr.Zero"/> — не открылась (нет ветки или нет прав).</summary>
    public static IntPtr Open(string keyPath)
    {
        if (RegOpenKeyEx(HKEY_CURRENT_USER, keyPath, 0, KeyRead, out var key) != ErrorSuccess) return IntPtr.Zero;
        return key;
    }

    /// <summary>
    /// Открыть СУЩЕСТВУЮЩУЮ ветку для правки значений (не создавая её).
    ///
    /// Отдельно от <see cref="Open"/> — и это не педантизм. Удаление значения требует прав
    /// на запись: с дескриптором, открытым только на чтение, <c>RegDeleteValue</c> возвращает
    /// «отказано в доступе», и запись автозапуска остаётся на месте, а панель при этом считает,
    /// что сняла её. Ровно это и нашла проба на собранном exe 24.09.2026 — тесты на подставном
    /// хранилище такую ошибку поймать не могут, потому что в памяти прав доступа нет.
    /// </summary>
    public static IntPtr OpenForWrite(string keyPath)
    {
        var access = KeyQueryValue | KeySetValue;
        if (RegOpenKeyEx(HKEY_CURRENT_USER, keyPath, 0, access, out var key) != ErrorSuccess) return IntPtr.Zero;
        return key;
    }

    /// <summary>Открыть или создать ветку для записи. <see cref="IntPtr.Zero"/> — не вышло.</summary>
    public static IntPtr Create(string keyPath)
    {
        var access = KeyQueryValue | KeySetValue;
        var rc = RegCreateKeyEx(
            HKEY_CURRENT_USER, keyPath, 0, null, 0, access, IntPtr.Zero, out var key, out _);

        return rc == ErrorSuccess ? key : IntPtr.Zero;
    }

    public static void Close(IntPtr key)
    {
        if (key != IntPtr.Zero) RegCloseKey(key);
    }

    /// <summary>
    /// Прочитать строковое значение. Три исхода, и все три разные: прочитали; значения нет;
    /// прочитать не удалось. «Не удалось» — это НЕ «значения нет»: при неизвестном состоянии
    /// панель не имеет права ничего править.
    /// </summary>
    public static bool TryReadString(IntPtr key, string name, out string? value, out bool found)
    {
        value = null;
        found = false;

        var size = 0;
        var rc = RegQueryValueEx(key, name, IntPtr.Zero, out var type, null, ref size);

        // Значения нет вовсе — это нормальный ответ, а не отказ.
        if (rc == ErrorFileNotFound) return true;

        // Пустое значение на первом вызове даёт 0 байт; всё остальное — отказ.
        if (rc != ErrorSuccess) return false;

        if (type is not (RegSz or RegExpandSz))
        {
            // Значение есть, но не текст (REG_DWORD и подобное). Панель пишет только текст,
            // и чужой формат она не разбирает: считаем, что записи для неё нет.
            return true;
        }

        var buffer = new byte[Math.Max(size, 2)];
        var actual = size;
        rc = RegQueryValueEx(key, name, IntPtr.Zero, out _, buffer, ref actual);
        if (rc != ErrorSuccess) return false;

        var text = Encoding.Unicode.GetString(buffer, 0, Math.Min(actual, buffer.Length));
        value = text.TrimEnd('\0');
        found = value.Length > 0;
        return true;
    }

    public static bool WriteString(IntPtr key, string name, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        return RegSetValueEx(key, name, 0, RegSz, bytes, bytes.Length) == ErrorSuccess;
    }

    public static bool DeleteValue(IntPtr key, string name)
    {
        var rc = RegDeleteValue(key, name);
        return rc is ErrorSuccess or ErrorFileNotFound;
    }

    /// <summary>Убрать свою ветку целиком. Нужно пробе: она заводит собственную ветку и прибирает за собой.</summary>
    public static bool DeleteKey(string keyPath) =>
        RegDeleteKey(HKEY_CURRENT_USER, keyPath) is ErrorSuccess or ErrorFileNotFound or ErrorPathNotFound;

    /// <summary>Есть ли такая ветка. Нужно пробе: она проверяет, что за собой прибрала.</summary>
    public static bool Exists(string keyPath)
    {
        var key = Open(keyPath);
        if (key == IntPtr.Zero) return false;

        Close(key);
        return true;
    }
}
