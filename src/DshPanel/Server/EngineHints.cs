using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// ОДНА ЗАПИСЬ ПОДСКАЗОК: версия движка, проверена ли панель на ней, короткая выжимка и известные
/// беды — каждое на трёх языках.
///
/// ⚠️ **Заметки живут на трёх языках В ФАЙЛЕ, а не в словарях панели.** Файл обновляется одним
/// коммитом без выпуска панели (решение владельца), поэтому перевод новой заметки не может ждать
/// нового выпуска: он приходит вместе с заметкой.
/// </summary>
public sealed record EngineHint(
    string Version,
    bool Verified,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Notes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Troubles);

/// <summary>
/// ПОДСКАЗКИ О ВЕРСИЯХ ДВИЖКА — то, что панель читает из публичного репозитория КАК ПОДСКАЗКУ.
///
/// **Зачем.** Умения панель проверяет сама (проба), но «на какой версии движка её уже проверяли»
/// и «какие беды про эту версию известно» знает не движок, а мы. Файл в публичном репозитории —
/// дешёвое отслеживание выпусков: он обновляется **одним коммитом, без нового выпуска панели**.
///
/// ⚠️ **Недоступен файл — «не проверялась», и панель работает.** Это правило владельца, и оно тут
/// главное: подсказка не имеет права ничего ронять. Разбор терпим ко всему: битый JSON, чужая
/// форма, пустой список — всё это «подсказок нет», а не поломка.
///
/// ⚠️ **Версия сверяется ПРАВИЛОМ ПАНЕЛИ** (<see cref="Update.UpdateDecisions.Numeric"/>), как
/// везде в панели: ведущая `v` и хвост сборки `+хеш` отбрасываются. **Предрелизный хвост при этом
/// значим:** запись про `0.1.5-rc.2` не выдаётся за финальный `0.1.5` — иначе подсказка про
/// предрелиз молча приклеилась бы к выпуску, который панель не проверяла.
/// </summary>
public sealed record EngineHints(IReadOnlyList<EngineHint> Versions)
{
    /// <summary>
    /// ВЕРСИЯ СХЕМЫ ФАЙЛА. Файл правят одним коммитом **без выпуска панели**, поэтому панель обязана
    /// понимать, ЧТО именно она читает: файл ЧУЖОЙ (будущей) схемы — это «подсказок нет», а не
    /// догадка по полям, смысла которых мы ещё не знаем. Поле необязательное: файл без него
    /// (первая редакция) панель читает как свою.
    /// </summary>
    public const int Format = 1;

    /// <summary>Подсказок нет: файла не было, он не разобрался или в нём пусто.</summary>
    public static EngineHints Empty { get; } = new(Array.Empty<EngineHint>());

    /// <summary>Есть ли вообще подсказки — от этого зависит строка «проверенные версии».</summary>
    public bool Any => Versions.Count > 0;

    /// <summary>
    /// РАЗБОР ФАЙЛА ПОДСКАЗОК. Терпимый намеренно: файл правят руками и он чужой, поэтому битый
    /// JSON — это «подсказок нет», а не исключение наружу. Но **чужая схема не угадывается**:
    /// объявленная версия схемы, отличная от нашей, даёт тот же честный ответ «подсказок нет».
    /// </summary>
    public static EngineHints Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            if (!Ours(document.RootElement)) return Empty;
            if (!document.RootElement.TryGetProperty("versions", out var versions)) return Empty;
            if (versions.ValueKind != JsonValueKind.Array) return Empty;

            var found = new List<EngineHint>();

            foreach (var item in versions.EnumerateArray())
            {
                var hint = Read(item);
                if (hint is not null) found.Add(hint);
            }

            return found.Count == 0 ? Empty : new EngineHints(found);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Запись про НАШУ версию движка — сверкой правилом панели (<c>Numeric</c>: без ведущей `v`
    /// и без хвоста сборки `+хеш`, а предрелизный хвост значим). Нет такой — <c>null</c>, и это
    /// честное «про эту версию мы ничего не знаем».
    /// </summary>
    public EngineHint? Find(string? version)
    {
        var wanted = Update.UpdateDecisions.Numeric(version);
        if (wanted.Length == 0) return null;

        return Versions.FirstOrDefault(hint =>
            string.Equals(Update.UpdateDecisions.Numeric(hint.Version), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Версии, на которых панель проверена, — списком словами (для строки отчёта).</summary>
    public string VerifiedList() => string.Join(
        ", ",
        Versions.Where(hint => hint.Verified).Select(hint => hint.Version));

    /// <summary>
    /// ЗАМЕТКИ НА ЯЗЫКЕ ПАНЕЛИ. Нет перевода на этот язык — берём русский (язык разработки),
    /// потом любой: пустая подсказка хуже неточной, но выдумывать перевод мы не станем.
    /// </summary>
    public static IReadOnlyList<string> NotesFor(EngineHint? hint, string? language) =>
        ForLanguage(hint?.Notes, language);

    /// <summary>ИЗВЕСТНЫЕ БЕДЫ на языке панели — тем же правилом, что заметки.</summary>
    public static IReadOnlyList<string> TroublesFor(EngineHint? hint, string? language) =>
        ForLanguage(hint?.Troubles, language);

    private static IReadOnlyList<string> ForLanguage(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? map, string? language)
    {
        if (map is null || map.Count == 0) return Array.Empty<string>();

        var wanted = (language ?? string.Empty).Trim().ToLowerInvariant();

        foreach (var candidate in new[] { wanted, "ru", "en" })
        {
            if (candidate.Length == 0) continue;
            if (map.TryGetValue(candidate, out var lines) && lines.Count > 0) return lines;
        }

        return map.Values.FirstOrDefault(lines => lines.Count > 0) ?? Array.Empty<string>();
    }

    /// <summary>
    /// НАША ЛИ ЭТО СХЕМА. Поля <c>format</c> может не быть — тогда это первая редакция файла,
    /// и она наша. Объявленная версия сверяется строго: не наша — не читаем.
    /// </summary>
    private static bool Ours(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format)) return true;
        if (format.ValueKind != JsonValueKind.Number) return false;

        return format.TryGetInt32(out var number) && number == Format;
    }

    private static EngineHint? Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("version", out var version)) return null;

        var text = version.GetString()?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        var verified = item.TryGetProperty("verified", out var flag) && flag.ValueKind == JsonValueKind.True;

        return new EngineHint(text, verified, Lines(item, "notes"), Lines(item, "troubles"));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Lines(JsonElement item, string name)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (!item.TryGetProperty(name, out var languages) || languages.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var language in languages.EnumerateObject())
        {
            if (language.Value.ValueKind != JsonValueKind.Array) continue;

            var lines = new List<string>();

            foreach (var line in language.Value.EnumerateArray())
            {
                var text = line.GetString()?.Trim() ?? string.Empty;
                if (text.Length > 0) lines.Add(text);
            }

            if (lines.Count > 0) map[language.Name] = lines;
        }

        return map;
    }
}
