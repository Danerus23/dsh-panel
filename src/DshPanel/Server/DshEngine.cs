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

    /// <summary>
    /// НАЙТИ ТОЛЬКО NODE, без движка. Нужно решению о своём Node (этап E3.1): на чистой машине
    /// движка ещё нет, а вопрос «какой у человека Node и годится ли он» уже стоит — и ответ на него
    /// решает, скачивать ли свой. Возвращает <c>null</c>, если Node не нашёлся.
    ///
    /// ⚠️ **Подмена <c>DSH_PANEL_NODE</c> уважается и здесь** — как в <see cref="Locate"/>: без этого
    /// проба не увидела бы подставленный старый Node и проверяла бы не то состояние (живой прогон
    /// 10.10.2026: проба сказала «системный Node не найден», хотя старый Node был подставлен).
    /// </summary>
    public static string? FindNode(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        return Exist(environment(NodeOverrideVariable)) ?? LocateNode(environment);
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

    /// <summary>
    /// ГДЕ ИСКАЛИ — ЯРЛЫКАМИ, ДЛЯ ЖУРНАЛЬНОЙ СТРОКИ (S-11, пачка B).
    ///
    /// **Зачем.** Строка «сервер DSH не найден» говорила человеку нашу кухню: пути поиска и имя
    /// пакета. Человеку это не нужно (база, п. 13), а журналу — нужно: без него отказ нечем
    /// разобрать. Поэтому места названы здесь ОДИН раз и уходят строкой в журнал.
    ///
    /// ⚠️ Ярлыки, а не пути этой машины: журнал уезжает в копию и в чужой отчёт, и настоящие пути
    /// в нём — личные данные (красная линия 7). Человек по ярлыку поймёт, где смотреть.
    /// </summary>
    public static IReadOnlyList<string> SearchPlaces { get; } = new[]
    {
        "PATH", @"%APPDATA%\npm", @"Program Files\nodejs",
    };
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
