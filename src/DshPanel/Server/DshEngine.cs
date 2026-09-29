namespace DshPanel.Server;

/// <summary>
/// Где на этой машине Node и движок DSH. Только ЧТЕНИЕ: панель ищет уже установленное
/// и ничего не ставит (установка движка — этап выпуска, `docs\ENGINE.md` §4).
///
/// Пути не выдуманы: это те же места, что проверял v1 (`NodeLocator`), и на машине владельца
/// движок лежит именно так (`%APPDATA%\npm\node_modules\@deepseek-ai\dsh\lib\bin.js`).
/// Подменить их для проверки можно переменными <c>DSH_PANEL_NODE</c> и <c>DSH_PANEL_ENGINE</c> —
/// без этого самотест нельзя было бы прогнать на подставном движке.
/// </summary>
public sealed record DshEngine(string NodePath, string BinPath)
{
    public const string NodeOverrideVariable = "DSH_PANEL_NODE";
    public const string EngineOverrideVariable = "DSH_PANEL_ENGINE";

    /// <summary>Искать движок и Node. Возвращает <c>null</c>, если чего-то нет.</summary>
    public static DshEngine? Locate(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        var node = Exist(environment(NodeOverrideVariable)) ?? LocateNode(environment);
        var bin = Exist(environment(EngineOverrideVariable)) ?? LocateBin(node);

        return node is null || bin is null ? null : new DshEngine(node, bin);
    }

    private static string? LocateNode(Func<string, string?> environment)
    {
        var candidates = new List<string?>();

        // Node рядом с собой в PATH — самый частый случай на машине, где DSH уже работает.
        var path = environment("Path") ?? environment("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Combine(dir.Trim(), "node.exe"));

        // Глобальные пакеты npm: сюда встаёт `npm install -g`, и сюда же кладёт node сам npm.
        candidates.Add(Combine(NpmPrefix(), "node.exe"));
        candidates.Add(Combine(ProgramFiles(), "nodejs", "node.exe"));

        return candidates.Select(Exist).FirstOrDefault(found => found is not null);
    }

    private static string? LocateBin(string? node)
    {
        var candidates = new List<string?>
        {
            // Глобальная установка npm: %APPDATA%\npm и она же как prefix.
            Combine(NpmPrefix(), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            Combine(ProgramFiles(), "nodejs", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
        };

        // Пакет, поставленный рядом с самим node (переносимый Node): npm кладёт глобальные
        // пакеты в node_modules рядом с node.exe.
        if (node is not null)
            candidates.Add(Combine(Path.GetDirectoryName(node) ?? string.Empty,
                "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));

        return candidates.Select(Exist).FirstOrDefault(found => found is not null);
    }

    private static string? NpmPrefix() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

    private static string? ProgramFiles() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static string? Combine(params string?[] parts)
    {
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;
        return Path.Combine(parts!);
    }

    private static string? Exist(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
}
