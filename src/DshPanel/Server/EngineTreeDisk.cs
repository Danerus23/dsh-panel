namespace DshPanel.Server;

/// <summary>
/// ЧТО ЛЕЖИТ В ПОСТАВЛЕННОМ ДЕРЕВЕ — чтение с диска, отдельной дверью.
///
/// **Зачем отдельно от сверки.** Сверка (<see cref="EngineTreeCheck"/>) — ЧИСТОЕ решение: ей дают
/// пути и версии, и она не знает, откуда они. Диск читает эта дверь, и её ответ — «вот что там
/// лежит», без выводов: расхождение считает сверка, а не читатель.
///
/// **Считаем пакетом только каталог с `package.json`.** В `node_modules` лежат ещё `.bin` и папки
/// областей (`@scope`), пакетами не являющиеся: без этого правила честная установка выглядела бы
/// «лишними пакетами» (замер 10.10.2026: так и вышло, пока правило не завели).
///
/// **Путь — он же адрес.** У одного и того же пакета бывает несколько мест
/// (`node_modules/x` и `node_modules/a/node_modules/x`), поэтому и сверка, и это чтение говорят
/// ПУТЯМИ в виде `node_modules/…`, а не именами.
/// </summary>
public static class EngineTreeDisk
{
    /// <summary>Итог чтения: пути пакетов и версии по пути.</summary>
    public sealed record Read(IReadOnlyList<string> Paths, Func<string, string?> VersionOf);

    /// <summary>
    /// ПРОЧИТАТЬ ДЕРЕВО УСТАНОВКИ. Папки нет — это пустое дерево (а не исключение): сверка скажет
    /// словами, что именно не сошлось.
    /// </summary>
    public static Read From(string? installDir)
    {
        var paths = new List<string>();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(installDir)) return new Read(paths, _ => null);

        var modules = Path.Combine(installDir.Trim(), "node_modules");
        Scan(modules, "node_modules", paths, versions);

        return new Read(paths, path => versions.TryGetValue(path, out var version) ? version : null);
    }

    /// <summary>Версия пакета по пути — из его же <c>package.json</c>. Пусто — не прочиталась.</summary>
    public static string VersionAt(string? installDir, string? path)
    {
        if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(path)) return string.Empty;

        var file = Path.Combine(installDir.Trim(), path.Trim().Replace('/', Path.DirectorySeparatorChar), "package.json");

        return Platform.VersionProbe.FromPackage(Path.GetDirectoryName(file));
    }

    /// <summary>
    /// Обойти каталог пакетов: каталог с `package.json` — пакет, каталог области (`@имя`) — не пакет,
    /// а его содержимое — пакеты; служебные (`.bin` и прочее скрытое) не считаются вовсе.
    /// </summary>
    private static void Scan(string directory, string prefix, List<string> paths, Dictionary<string, string> versions)
    {
        string[] children;

        try
        {
            if (!Directory.Exists(directory)) return;
            children = Directory.GetDirectories(directory);
        }
        catch
        {
            // Каталог не прочитался (права, занят, исчез) — в дереве его для нас нет.
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.Length == 0 || name.StartsWith('.')) continue;

            if (name.StartsWith('@'))
            {
                string[] scoped;

                try
                {
                    scoped = Directory.GetDirectories(child);
                }
                catch
                {
                    continue;
                }

                foreach (var package in scoped)
                {
                    var scopedName = Path.GetFileName(package);
                    if (scopedName.Length == 0 || scopedName.StartsWith('.')) continue;
                    Take(package, prefix + "/" + name + "/" + scopedName, paths, versions);
                }

                continue;
            }

            Take(child, prefix + "/" + name, paths, versions);
        }
    }

    private static void Take(string directory, string path, List<string> paths, Dictionary<string, string> versions)
    {
        var version = Platform.VersionProbe.FromPackage(directory);
        if (version.Length == 0) return;

        paths.Add(path);
        versions[path] = version;

        Scan(Path.Combine(directory, "node_modules"), path + "/node_modules", paths, versions);
    }
}
