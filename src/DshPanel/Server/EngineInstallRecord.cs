using System.Text;
using System.Text.Json;
using DshPanel.Files;

namespace DshPanel.Server;

/// <summary>
/// ЧТО ПАНЕЛЬ ПОСТАВИЛА И ЧЕМ ЭТО ЗАПУСКАЕТСЯ: версия движка, её папка и Node, которым её ставили.
///
/// **Зачем запись, а не догадка.** Именам папок верить нельзя: `<папка движка>\<версия>` мог завести
/// человек, а `current`/`latest` мы не заводим вовсе. Откат (и уборка прежней версии) обязаны знать,
/// **что ставила панель** и **какая версия стоит сейчас**, — иначе однажды убрали бы чужое дерево
/// или «вернулись» в папку, которой нет.
///
/// **Один источник для двух нужд.** Эту же запись читает опись поставленного
/// (<see cref="Isolation.InstalledInventory"/>): движок и **свой** Node попадают в неё отсюда,
/// а системный Node — никогда: он не наш, и красная линия запрещает его удалять.
///
/// ⚠️ **Файл состояния, а не настройка:** он пишется по праву <c>RunRights.LocalData</c>, лежит под
/// корнем панели (`state\engine.json`) и у изолированного прогона — свой.
/// </summary>
public sealed record EngineInstallRecord(
    string EngineVersion,
    string EnginePath,
    string NodeVersion,
    string NodePath,
    bool NodeOurs,
    IReadOnlyList<string>? Installed = null)
{
    /// <summary>Записи нет: панель ещё ничего не ставила.</summary>
    public static EngineInstallRecord Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, false);

    /// <summary>Есть ли что читать: без версии движка запись бессмысленна.</summary>
    public bool Any => EngineVersion.Length > 0;

    /// <summary>
    /// ВЕРСИИ, КОТОРЫЕ СТАВИЛА ПАНЕЛЬ. Уборка смотрит ТОЛЬКО на них: папку, заведённую человеком
    /// (или поставленную другим способом), панель не трогает никогда — она про неё не знает.
    /// </summary>
    public IReadOnlyList<string> Known => Installed ?? Array.Empty<string>();

    /// <summary>Прежняя версия из записи — та, к которой возвращает откат (пусто — возвращаться некуда).</summary>
    public string Previous => EngineVersion;

    /// <summary>
    /// ЧТО ЗАПИСАТЬ ПОСЛЕ УДАЧНОЙ УСТАНОВКИ. Папка движка и путь к Node берутся у самой установки,
    /// а не складываются заново: второе место, где собирается путь, однажды разошлось бы с первым.
    /// Список поставленного панелью пополняется новой версией и переживает установки.
    /// </summary>
    public static EngineInstallRecord After(
        string? engineVersion,
        string? enginePath,
        EngineNodeResult? node,
        EngineInstallRecord? before = null)
    {
        var version = (engineVersion ?? string.Empty).Trim();

        var known = new List<string>();

        foreach (var item in before?.Known ?? Array.Empty<string>())
        {
            var name = (item ?? string.Empty).Trim();
            if (name.Length == 0 || known.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            known.Add(name);
        }

        if (version.Length > 0 && !known.Contains(version, StringComparer.OrdinalIgnoreCase)) known.Add(version);

        return new EngineInstallRecord(
            version,
            (enginePath ?? string.Empty).Trim(),
            (node?.Version ?? string.Empty).Trim(),
            (node?.NodePath ?? string.Empty).Trim(),
            node?.Ours ?? false,
            known);
    }

    /// <summary>
    /// ПРОЧИТАТЬ запись. Файла нет, он битый или чужой формы — <see cref="Empty"/>: «панель ничего
    /// не ставила» честнее догадки, а откат в таком случае просто некуда делать.
    /// </summary>
    public static EngineInstallRecord Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;

        try
        {
            if (!File.Exists(path)) return Empty;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var engine = root.TryGetProperty("engine", out var engineNode) && engineNode.ValueKind == JsonValueKind.Object
                ? engineNode
                : default;

            var node = root.TryGetProperty("node", out var nodeNode) && nodeNode.ValueKind == JsonValueKind.Object
                ? nodeNode
                : default;

            var record = new EngineInstallRecord(
                Text(engine, "version"),
                Text(engine, "path"),
                Text(node, "version"),
                Text(node, "path"),
                node.ValueKind == JsonValueKind.Object
                    && node.TryGetProperty("ours", out var ours)
                    && ours.ValueKind == JsonValueKind.True,
                Names(root));

            return record.Any ? record : Empty;
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// ЗАПИСАТЬ. Возвращает признак успеха; исключений не бросает — неудавшаяся запись не имеет
    /// права уронить установку, но и молчать о ней нельзя: скажет вызывающий.
    ///
    /// ⚠️ **Запись ложится ПОВЕРХ старой только целиком** (временный файл + замена): оборванная
    /// запись «наполовину» увела бы откат в никуда.
    /// </summary>
    public bool Write(string? path, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var text = JsonSerializer.Serialize(
                new
                {
                    engine = new { version = EngineVersion, path = EnginePath },
                    node = new { version = NodeVersion, path = NodePath, ours = NodeOurs },
                    installed = Known,
                },
                new JsonSerializerOptions { WriteIndented = true });

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".part";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);

            return true;
        }
        catch (Exception error)
        {
            reason = error.GetType().Name;
            return false;
        }
    }

    private static string Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    /// <summary>Список версий, которые панель ставила: битые и пустые записи пропускаются.</summary>
    private static IReadOnlyList<string> Names(JsonElement root)
    {
        if (!root.TryGetProperty("installed", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList();
    }
}

/// <summary>
/// СКОЛЬКО ВЕРСИЙ ДЕРЖИМ И ЧТО УБИРАЕМ (решение владельца 10.10.2026: *«держим текущую и прежнюю
/// версию (≈213 МБ каждая), освобождение места — словами»*).
///
/// **Правило простое и потому проверяемое:** в папке движка остаются **текущая** версия (та, что
/// стоит сейчас) и **прежняя** (к которой вернёт откат); всё остальное — убираем. Так у человека
/// не растут деревья по 213 МБ, но и откат остаётся возможным.
///
/// ⚠️ **Папки, о которых панель не знает, не трогаются вовсе:** в папке движка мог оказаться
/// движок, поставленный человеком руками. Убираем ровно те имена, которые панель называет сама.
///
/// ⚠️ **Уборка идёт ОДНОЙ дверью** — <see cref="TreeDeletion.Remove"/>: дерево может содержать
/// ссылки (npm кладёт их в установку), и удаление «как обычно» оставило бы либо ссылку, либо чужой
/// каталог. Замер: `Directory.Delete(recursive: true)` на junction **не удаляет вовсе**.
/// </summary>
public static class EngineRetention
{
    /// <summary>
    /// ЧТО УБРАТЬ: всё, что не текущая и не прежняя. Пустые имена игнорируются, повторы — тоже
    /// (одна и та же версия не должна попасть в список дважды).
    /// </summary>
    public static IReadOnlyList<string> ToRemove(
        string? current,
        string? previous,
        IEnumerable<string>? installed)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Keep(string? version)
        {
            if (!string.IsNullOrWhiteSpace(version)) keep.Add(version.Trim());
        }

        Keep(current);
        Keep(previous);

        var remove = new List<string>();

        foreach (var version in installed ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(version)) continue;

            var trimmed = version.Trim();
            if (keep.Contains(trimmed)) continue;
            if (remove.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;

            remove.Add(trimmed);
        }

        return remove;
    }

    /// <summary>Имена папок версий, которые лежат в папке движка (только каталоги).</summary>
    public static IReadOnlyList<string> Installed(string? engineDir)
    {
        if (string.IsNullOrWhiteSpace(engineDir)) return Array.Empty<string>();

        try
        {
            if (!Directory.Exists(engineDir)) return Array.Empty<string>();

            return Directory.GetDirectories(engineDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }
        catch
        {
            // Папку не прочитать — «не знаем, что там»: убирать наугад нельзя.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// УБРАТЬ НЕНУЖНЫЕ ВЕРСИИ. Ответ — имена убранных: «убрали» должно быть видно, а не казаться.
    /// Ошибка уборки не роняет установку: движок уже поставлен и проверен, а место — дело второе.
    /// </summary>
    public static IReadOnlyList<string> Remove(string? engineDir, IEnumerable<string>? versions)
    {
        if (string.IsNullOrWhiteSpace(engineDir)) return Array.Empty<string>();

        var removed = new List<string>();

        foreach (var version in versions ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(version)) continue;

            var folder = Path.Combine(engineDir.Trim(), version.Trim());

            try
            {
                TreeDeletion.Remove(folder);

                // Убранной считаем ту, которой БОЛЬШЕ НЕТ: «команду отдали» и «папка исчезла» —
                // разные вещи, и человеку нужно второе.
                if (!Directory.Exists(folder)) removed.Add(version.Trim());
            }
            catch
            {
                // Не убралось (занято, нет прав) — оставляем как есть и говорим числом.
            }
        }

        return removed;
    }
}

/// <summary>
/// СКОЛЬКО СВОИХ NODE ДЕРЖИМ И ЧТО УБИРАЕМ (пункт 2 плана E3.2, решение владельца 10.10.2026).
///
/// **Держим ровно ту версию, с которой панель может подняться.** Пусковой путь берёт **только**
/// `node\&lt;закреплённая&gt;` (<see cref="EngineNode.Release"/>) — значит всякая другая своя версия
/// лежит мёртвым грузом по ~80 МБ. Их и убираем.
///
/// ⚠️ **Системный Node не трогается никогда** — он не наш, и панель про него ничего не решает.
/// Убираются только ПАПКИ ВЕРСИЙ в НАШЕЙ папке: каталог с именем не-версией (человек положил своё)
/// остаётся на месте.
///
/// ⚠️ **Уборка идёт той же дверью** — <see cref="TreeDeletion.Remove"/>: установка кладёт в дерево
/// ссылки, и общий кэш npm после уборки обязан остаться целым (проверка
/// `Версия_исчезает_а_кэш_npm_цел` в `EngineRetentionTests`).
/// </summary>
public static class EngineNodeRetention
{
    /// <summary>ЧТО УБРАТЬ: всё, кроме держимой версии, — и только то, что похоже на версию.</summary>
    public static IReadOnlyList<string> ToRemove(string? keep, IEnumerable<string>? installed)
    {
        var held = (keep ?? string.Empty).Trim();
        var remove = new List<string>();

        foreach (var version in installed ?? Array.Empty<string>())
        {
            var name = (version ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (string.Equals(name, held, StringComparison.OrdinalIgnoreCase)) continue;

            // Не-версия (человеческая папка) не убирается: панель убирает только то, что ставила.
            if (EngineNode.Normalize(name).Length == 0) continue;

            if (remove.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            remove.Add(name);
        }

        return remove;
    }

    /// <summary>
    /// УБРАТЬ ЛИШНИЕ СВОИ NODE. Ответ — имена убранных и освобождённое место числом: «убрали»
    /// должно быть видно, а место панель называет словами. Ошибка уборки работу не роняет:
    /// движок уже стоит.
    /// </summary>
    public static IReadOnlyList<string> Remove(string? nodeDir, string? keep, out long freedBytes)
    {
        freedBytes = 0;

        if (string.IsNullOrWhiteSpace(nodeDir)) return Array.Empty<string>();

        var removed = new List<string>();

        foreach (var version in ToRemove(keep, EngineRetention.Installed(nodeDir)))
        {
            var folder = Path.Combine(nodeDir.Trim(), version);

            try
            {
                var size = Size(folder);
                TreeDeletion.Remove(folder);

                if (!Directory.Exists(folder))
                {
                    removed.Add(version);
                    freedBytes += size;
                }
            }
            catch
            {
                // Не убралось (занято, нет прав) — оставляем как есть.
            }
        }

        if (removed.Count == 0) freedBytes = 0;

        return removed;
    }

    /// <summary>Сколько занимает папка версии — чтобы назвать освобождённое ЧИСЛОМ.</summary>
    public static long Size(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try { return new FileInfo(path).Length; } catch { return 0L; }
                })
                .Sum();
        }
        catch
        {
            return 0;
        }
    }
}