using System.Diagnostics;

namespace DshTray;

/// <summary>
/// Поиск node.exe и точки входа dsh. Сервер запускается напрямую через node,
/// поэтому ни окна PowerShell, ни окна cmd в системе не появляется.
/// </summary>
public static class NodeLocator
{
    private static string _cachedNode;
    private static string _cachedBin;

    /// <summary>
    /// Пути, заданные человеком в мастере настройки (settings.json). Переменные окружения
    /// важнее: ими пользуются проверки и перенос. Пустая строка — искать самим.
    /// </summary>
    public static string OverrideNodePath { get; set; } = "";

    public static string OverrideBinPath { get; set; } = "";

    /// <summary>Запомнить пути из настроек (вызывается при старте приложения).</summary>
    public static void ApplyOverrides(string nodePath, string binPath)
    {
        var node = string.IsNullOrWhiteSpace(nodePath) ? "" : nodePath.Trim();
        var bin = string.IsNullOrWhiteSpace(binPath) ? "" : binPath.Trim();
        if (node == OverrideNodePath && bin == OverrideBinPath) return;

        OverrideNodePath = node;
        OverrideBinPath = bin;
        ResetCache();
    }

    /// <summary>
    /// Добавляет папку своего Node в PATH дочернего процесса.
    ///
    /// Зачем: переносимый Node лежит внутри панели (%LOCALAPPDATA%\DshPanel\node) и в PATH
    /// не прописан. npm при этом запускается (его зовут по полному пути), но скрипты пакетов
    /// вызывают «node ./что-то» через cmd — и на чистой машине установка движка DSH обрывалась
    /// на сборке koffi с «node не является внутренней или внешней командой» (это и увидел
    /// владелец на приёмке). Тот же PATH нужен и серверу: движок запускает подпроцессы.
    /// </summary>
    public static void AddToPath(ProcessStartInfo start)
    {
        try
        {
            var node = TryResolveNode();
            if (node.Length == 0) return;

            var directory = Path.GetDirectoryName(node);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            var path = start.Environment.TryGetValue("PATH", out var current) && !string.IsNullOrEmpty(current)
                ? current
                : Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(part.Trim().TrimEnd('\\'), directory.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            start.Environment["PATH"] = directory + ";" + path;
        }
        catch
        {
            // Не вышло — процесс запустится как раньше, без нашей папки в PATH.
        }
    }

    public static string ResolveNode()
    {
        if (_cachedNode != null) return _cachedNode;

        var overridePath = Environment.GetEnvironmentVariable("DSH_TRAY_NODE");
        if (string.IsNullOrWhiteSpace(overridePath)) overridePath = OverrideNodePath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return _cachedNode = overridePath;
        }

        var candidates = new List<string>();
        var pathVariable = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        foreach (var dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "node.exe"));
            }
            catch
            {
                // Кривой элемент PATH — пропускаем.
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", "node.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));

        // Свой переносимый Node: панель ставит его сюда, когда прав администратора нет
        // (или когда Node на машине вообще не было). Раньше здесь его не было — и после
        // установки на чистой машине панель не находила собственный Node: «node не найден»,
        // шарик про отсутствующий dsh, отказ поднять сервер (это и увидел владелец на приёмке).
        candidates.Add(Path.Combine(AppPaths.DefaultStateDir, "node", "node.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return _cachedNode = candidate;
        }

        throw new InvalidOperationException(Loc.T("error.nodeMissing"));
    }

    /// <summary>Файл lib\bin.js пакета dsh — то, что запускает шим dsh.cmd.</summary>
    public static string ResolveDshBin()
    {
        if (_cachedBin != null) return _cachedBin;

        var overridePath = Environment.GetEnvironmentVariable("DSH_TRAY_BIN");
        if (string.IsNullOrWhiteSpace(overridePath)) overridePath = OverrideBinPath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return _cachedBin = overridePath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            // Движок, поставленный в наш же переносимый Node: npm кладёт глобальные пакеты
            // в node_modules рядом с node.exe. Без этого пути панель не видела движок,
            // который сама только что поставила (владелец на приёмке получил из-за этого
            // шарик «пакет dsh не найден» сразу после успешной установки).
            Path.Combine(AppPaths.DefaultStateDir, "node", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
        };

        // npm может быть перенастроен на другой prefix — спрашиваем его напрямую.
        var prefix = TryGetNpmPrefix();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            candidates.Add(Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return _cachedBin = candidate;
        }

        throw new InvalidOperationException(Loc.T("error.dshMissing"));
    }

    public static void ResetCache()
    {
        _cachedNode = null;
        _cachedBin = null;
    }

    /// <summary>
    /// Путь к node.exe или пустая строка, если Node на машине нет. Не бросает исключения:
    /// на чистой машине панель обязана уметь развернуть движок из копии именно потому,
    /// что Node ещё не поставлен, — а ResolveNode() в этом случае как раз и падает.
    /// </summary>
    public static string TryResolveNode()
    {
        try
        {
            return ResolveNode();
        }
        catch
        {
            return "";
        }
    }

    private static string TryGetNpmPrefix()
    {
        try
        {
            var psi = new ProcessStartInfo("npm.cmd", "prefix -g")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            output = output.Trim();
            return Directory.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
