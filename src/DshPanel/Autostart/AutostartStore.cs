using DshPanel.Isolation;
using DshPanel.Platform;

namespace DshPanel.Autostart;

/// <summary>
/// Что лежит под именем записи. Три исхода, и «записи нет» — не то же самое, что
/// «прочитать не удалось»: во втором случае панель не имеет права ничего менять.
/// </summary>
public readonly record struct AutostartRecord(bool Readable, string? Value)
{
    /// <summary>Записи нет — автозапуск выключен.</summary>
    public static AutostartRecord Missing => new(true, null);

    /// <summary>Хранилище не прочиталось: состояние неизвестно.</summary>
    public static AutostartRecord Unreadable => new(false, null);

    public bool Exists => Readable && !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// Где лежит запись автозапуска. Две реализации — и обе нужны:
///
/// * <see cref="RegistryAutostartStore"/> — то, что видит человек: <c>HKCU\...\Run</c>;
/// * <see cref="FileAutostartStore"/> — то, что видит ИЗОЛИРОВАННЫЙ прогон: файл под его корнем.
///
/// Второе — главное отличие от v1. Там ветку реестра «уводили» переменной окружения
/// (<c>DSH_PANEL_RUN_KEY</c>), и понадобился отдельный предохранитель против того, чтобы
/// эту переменную указали на настоящий <c>Run</c>: с такой подменой прогон проверки правил бы
/// боевую запись владельца, а окна и шарики при этом молчали бы — то есть человек ничего бы
/// не заметил. Здесь у изолированного прогона **пути к реестру не существует вовсе**:
/// хранилище выбирается по признаку изоляции, и файл лежит под корнем. Это то же правило,
/// что и у путей (<c>AppPaths</c>): не «мы старались не трогать», а «дотянуться нечем».
/// </summary>
public interface IAutostartStore
{
    /// <summary>Где именно лежит запись — для журнала и отчёта проверки.</summary>
    string Describe { get; }

    /// <summary>
    /// Можно ли в это хранилище ПИСАТЬ. У прогонов проверок — нет: машинную запись меняет
    /// только обычный запуск панели человеком. В v1 проверка замены файлов однажды перевела
    /// боевую запись владельца на свою временную копию — после перезагрузки поднялась бы она,
    /// папку удалили бы, и автозапуск молча перестал бы работать.
    /// </summary>
    bool Writable { get; }

    AutostartRecord Read(string name);
    bool Write(string name, string value);
    bool Delete(string name);
}

/// <summary>
/// Запись автозапуска в <c>HKCU</c>. Ветка приходит аргументом, и это существенно: настоящий
/// <c>Run</c> берётся здесь ровно в одном месте (<see cref="AutostartStores"/>), а проба заводит
/// свою ветку (<see cref="AutostartDecisions.SelfTestKeyPath"/>) и прибирает за собой.
/// </summary>
public sealed class RegistryAutostartStore : IAutostartStore
{
    private readonly string _keyPath;

    public RegistryAutostartStore(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _keyPath = keyPath.Trim();
    }

    /// <summary>Запись человека — та, ради которой всё это написано.</summary>
    public static RegistryAutostartStore OwnerRun() => new(AutostartDecisions.OwnerRunKeyPath);

    /// <summary>Своя ветка пробы: настоящий код и настоящий реестр, но не запись владельца.</summary>
    public static RegistryAutostartStore SelfTest() => new(AutostartDecisions.SelfTestKeyPath);

    public string KeyPath => _keyPath;

    public string Describe => RegistryNative.HivePrefix + _keyPath;

    public bool Writable => true;

    public AutostartRecord Read(string name)
    {
        var key = RegistryNative.Open(_keyPath);
        if (key == IntPtr.Zero)
        {
            // Ветки нет — это «записи нет», а не отказ: Run у свежего пользователя может
            // отсутствовать вовсе.
            return AutostartRecord.Missing;
        }

        try
        {
            return RegistryNative.TryReadString(key, name, out var value, out var found)
                ? found ? new AutostartRecord(true, value) : AutostartRecord.Missing
                : AutostartRecord.Unreadable;
        }
        finally { RegistryNative.Close(key); }
    }

    public bool Write(string name, string value)
    {
        var key = RegistryNative.Create(_keyPath);
        if (key == IntPtr.Zero) return false;

        try { return RegistryNative.WriteString(key, name, value); }
        finally { RegistryNative.Close(key); }
    }

    public bool Delete(string name)
    {
        // Именно OpenForWrite: с дескриптором «только чтение» RegDeleteValue отвечает отказом,
        // и запись осталась бы на месте (поймала проба на exe 24.09.2026).
        var key = RegistryNative.OpenForWrite(_keyPath);
        if (key == IntPtr.Zero) return true; // ветки нет — убирать нечего

        try { return RegistryNative.DeleteValue(key, name); }
        finally { RegistryNative.Close(key); }
    }

    /// <summary>Убрать свою ветку целиком — проба делает это за собой.</summary>
    public bool DeleteKey() => RegistryNative.DeleteKey(_keyPath);
}

/// <summary>
/// Запись автозапуска в файле под корнем прогона. Формат намеренно простой: по строке
/// на запись, <c>имя=значение</c>, первый <c>=</c> — разделитель. Читает его только панель,
/// и человеку этот файл не показывают.
/// </summary>
public sealed class FileAutostartStore : IAutostartStore
{
    private readonly string _path;

    public FileAutostartStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Describe => _path;

    public bool Writable => true;

    public AutostartRecord Read(string name)
    {
        try
        {
            if (!File.Exists(_path)) return AutostartRecord.Missing;

            foreach (var line in File.ReadAllLines(_path))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;
                if (!string.Equals(line[..split], name, StringComparison.Ordinal)) continue;

                var value = line[(split + 1)..];
                return string.IsNullOrWhiteSpace(value)
                    ? AutostartRecord.Missing
                    : new AutostartRecord(true, value);
            }

            return AutostartRecord.Missing;
        }
        catch
        {
            return AutostartRecord.Unreadable;
        }
    }

    public bool Write(string name, string value)
    {
        try
        {
            var entries = Load();
            entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            entries.Add((name, value));
            Save(entries);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Delete(string name)
    {
        try
        {
            var entries = Load();
            var removed = entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (removed == 0) return true;

            Save(entries);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<(string Name, string Value)> Load()
    {
        var entries = new List<(string, string)>();
        if (!File.Exists(_path)) return entries;

        foreach (var line in File.ReadAllLines(_path))
        {
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            entries.Add((line[..split], line[(split + 1)..]));
        }

        return entries;
    }

    private void Save(List<(string Name, string Value)> entries)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = entries.Select(e => e.Name + "=" + e.Value);
        File.WriteAllLines(_path, lines);
    }
}

/// <summary>
/// Обёртка «читать можно, писать нельзя». Ею пользуются прогоны проверок: они смотрят на
/// настоящую запись владельца, чтобы показать её в окне, но не меняют её ни при старте,
/// ни по нажатию.
/// </summary>
public sealed class ReadOnlyAutostartStore : IAutostartStore
{
    private readonly IAutostartStore _inner;

    public ReadOnlyAutostartStore(IAutostartStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public string Describe => _inner.Describe;

    public bool Writable => false;

    public AutostartRecord Read(string name) => _inner.Read(name);

    public bool Write(string name, string value) => false;

    public bool Delete(string name) => false;
}

/// <summary>
/// Выбор хранилища — ОДНО место на всю панель.
///
/// Три случая, и третьего не дано (как и у путей в <c>AppPaths</c>):
///
/// | Прогон | Хранилище |
/// |---|---|
/// | изолированный (`--run-root`) | файл под корнем прогона: до реестра пути нет вовсе |
/// | обычный запуск человеком | настоящий <c>HKCU\...\Run</c> |
/// | прогон проверки (не изолированный) | тот же <c>Run</c>, но ТОЛЬКО на чтение |
/// </summary>
public static class AutostartStores
{
    /// <summary>Путь к текущему исполняемому файлу — то, что записывается в автозапуск.</summary>
    public static string SelfExe() => Environment.ProcessPath ?? string.Empty;

    /// <param name="humanLaunch">
    /// Это обычный запуск панели человеком (единственный, кто имеет право менять машинную запись).
    /// Значение ставит <c>Program</c> перед обычным запуском и не ставит ни одна проверка.
    /// </param>
    public static IAutostartStore For(RunContext context, bool humanLaunch)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsIsolated) return new FileAutostartStore(context.Paths.AutostartFile);

        var run = RegistryAutostartStore.OwnerRun();
        return humanLaunch ? run : new ReadOnlyAutostartStore(run);
    }
}
