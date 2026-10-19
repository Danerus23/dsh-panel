using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshPanel.Server;

/// <summary>
/// НА КАКОЙ МАШИНЕ ЛЕЖИТ ПАКЕТ. У записи замка npm бывают поля `os`, `cpu`, `libc` — значения
/// вроде `win32`, `darwin`, `linux`, `x64`, `arm64`, `musl`, и отрицания (`!win32`). Правило здесь
/// то же, что у npm: положительные значения — «одно из них должно совпасть», отрицательные —
/// «совпасть не должно», пусто — «годится везде».
/// </summary>
public static class EnginePlatform
{
    /// <summary>Имя системы так, как его пишет npm: `win32` · `linux` · `darwin`.</summary>
    public static string Os =>
        OperatingSystem.IsWindows() ? "win32"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "darwin"
        : string.Empty;

    /// <summary>Разрядность так, как её пишет npm: `x64` · `ia32` · `arm64` · `arm`.</summary>
    public static string Cpu => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "ia32",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => string.Empty,
    };

    /// <summary>
    /// СВОЯ LIBC. У Windows её нет вовсе, и это не «неизвестно», а ответ: пакет, которому нужен
    /// `musl` или `glibc`, здесь не ставится — замер это подтвердил (в замке 20 таких записей,
    /// и ни одна на Windows не появилась).
    /// </summary>
    public static string Libc => OperatingSystem.IsLinux() ? "glibc" : string.Empty;

    /// <summary>Годится ли значение для этой машины: пусто — годится, отрицание запрещает, иначе — одно из списка.</summary>
    public static bool Matches(IReadOnlyList<string> wanted, string current)
    {
        if (wanted.Count == 0) return true;

        var positive = false;
        var matched = false;

        foreach (var item in wanted)
        {
            var value = (item ?? string.Empty).Trim();
            if (value.Length == 0) continue;

            if (value.StartsWith('!'))
            {
                if (string.Equals(value[1..], current, StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }

            positive = true;

            if (value is "any" or "current") return true;
            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) matched = true;
        }

        // Одни отрицания — «годится, кроме перечисленного».
        return !positive || matched;
    }
}

/// <summary>
/// Один пакет проверенного дерева: путь, имя, ТОЧНАЯ версия, контрольная сумма вложения и адрес,
/// откуда вложение брали, — плюс то, что npm знает о пригодности пакета этой машине.
///
/// ⚠️ **Адрес — не украшение, а предохранитель** (требование владельца 10.10.2026): замок с пакетом
/// «с другого хоста» означал бы, что панель ставит у человека не то, что проверяла, — и это
/// отказ, а не замечание. Проверяется <see cref="FromOfficialRegistry"/>.
///
/// ⚠️ **`Optional` и `Extraneous` — из замера 10.10.2026.** В замке 588 пакетов, но на Windows
/// ставятся не все: 68 из 72 `optional` не появляются (им нужны `darwin`/`linux`, `musl`/`glibc`),
/// а один (`cordis-plugin-include`) помечен `extraneous` и `npm ci` его не ставит вовсе. Считать
/// их «пропажей» значило бы объявлять честную установку расхождением — а настоящая пропажа
/// утонула бы в этих 69 строках.
/// </summary>
public sealed record EngineTreeLockEntry(
    string Path,
    string Name,
    string Version,
    string Integrity,
    string Resolved,
    bool Optional,
    bool Extraneous,
    IReadOnlyList<string> Os,
    IReadOnlyList<string> Cpu,
    IReadOnlyList<string> Libc,
    string EnginesNode = "")
{
    /// <summary>Есть ли у записи сумма. Без неё «проверенным» пакет не считается.</summary>
    public bool HasSum => Integrity.Length > 0;

    /// <summary>
    /// Вложение берётся с **официального реестра** — и только оттуда. Свой (зеркальный, чужой)
    /// хост в замке означает подменённый набор, и ставить его панель не станет.
    /// </summary>
    public bool FromOfficialRegistry =>
        Resolved.StartsWith(EngineTreeLock.OfficialRegistry, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// НУЖЕН ЛИ ПАКЕТ НА ЭТОЙ МАШИНЕ: необязательный и «лишний по мнению npm» — не нужен,
    /// и чужая система/разрядность/libc — тоже. Всё остальное обязано лежать в дереве.
    /// </summary>
    public bool NeededHere(string os, string cpu, string libc) =>
        !Optional
        && !Extraneous
        && EnginePlatform.Matches(Os, os)
        && EnginePlatform.Matches(Cpu, cpu)
        && EnginePlatform.Matches(Libc, libc);
}

/// <summary>
/// ЗАМОК ПРОВЕРЕННОГО ДВИЖКА — точные версии и контрольные суммы ВСЕГО дерева (588 путей
/// у `0.1.5-rc.2`), снятые замером из своей изолированной установки.
///
/// **Зачем замок.** Решение владельца 10.10.2026, дословно: *«вариант 2 — это ровно то, что убило
/// v1 (голый latest собрал смешанное дерево), и вдобавок он делает слово „проверена“ неправдой:
/// у двух людей будет разное дерево из 236 пакетов»*. Замер это подтвердил: свежая установка **той
/// же** версии движка притянула зависимости `0.1.5-rc.3` — 232 расхождения с проверенным деревом.
/// Поэтому панель ставит движок **по замку**, а не «как решит npm».
///
/// ⚠️ **РЕДАКЦИЯ 2 — ПОСЛЕ ЗАМЕРА, И ЗАМЕР ИЗМЕНИЛ УСТРОЙСТВО ФАЙЛА.** Первая редакция была нашим
/// дайджестом (путь, имя, версия, сумма, адрес) — и для установки она **не годилась**: в дайджесте
/// нет поля `dependencies`, и `npm ci` по нему поставил **1 пакет вместо 588** (замер:
/// `upd-build\probe-install-by-lock.ps1`). Поэтому теперь внутри лежит **замок npm ЦЕЛИКОМ** —
/// ровно тот, что написал сам npm, со всеми полями (`dependencies` у 300 пакетов,
/// `peerDependencies` у 262, `os`/`cpu`/`optional` и прочее), — а наша оболочка добавляет только
/// то, чего npm не знает: редакцию, имя и версию движка, имя и версию изделия и объявленное число
/// пакетов. Второй замер (`probe-install-by-npm-lock.ps1` + `--server-selftest` по поставленному
/// дереву) подтвердил: по такому замку `npm ci` ставит дерево, проходящее **полную пробу панели** —
/// 84 из 84 имён, 8 из 8 умений.
///
/// **Что панель пишет npm.** Панель кладёт рядом два файла: `package-lock.json` — раздел
/// <c>lock</c> этого замка **как есть** (ни одного поля не выброшено и не переписано), и
/// `package.json` — собранный из КОРНЕВОЙ записи того же замка. Два места разойтись не могут:
/// у них один источник. Дальше `npm ci` проверяет суммы вложений сам.
///
/// **Откуда берётся.** Файл `engine-lock.json` в корне дерева, снятый
/// `upd-build\make-engine-lock.ps1` из СВОЕЙ изолированной установки (глобальную установку человека
/// он не читает и не меняет). В сборку он входит **встроенным ресурсом** — как три словаря панели:
/// тогда панель умеет поставить проверенное дерево **без сети** (в сеть идёт только npm за самими
/// вложениями), а обновляться файл может отдельно (тем же файлом данных, что подсказки о версиях —
/// решение владельца).
///
/// ⚠️ **Разбор терпимый, но схема — строгая.** Файл правят и обновляют отдельно от выпуска панели,
/// поэтому битый JSON, пустой список и объявленная ЧУЖАЯ версия схемы дают «замка нет» — а не
/// догадку по знакомым полям (<see cref="EngineHints"/> устроен так же, и по той же причине).
/// </summary>
public sealed record EngineTreeLock(
    string EngineName,
    string EngineVersion,
    string ProductName,
    string ProductVersion,
    IReadOnlyList<EngineTreeLockEntry> Entries,
    IReadOnlyList<KeyValuePair<string, string>> Dependencies,
    int Declared,
    string NpmLockJson)
{
    /// <summary>Версия схемы файла, которую понимает эта панель (первая редакция — дайджест — не подходит).</summary>
    public const int Format = 2;

    /// <summary>Имя встроенного ресурса: файл входит в сборку, а не лежит рядом с exe.</summary>
    internal const string ResourceSuffix = ".EngineTreeLock";

    /// <summary>
    /// ОФИЦИАЛЬНЫЙ РЕЕСТР — единственный источник, из которого панель ставит движок. Замер
    /// 10.10.2026: в снятом замке **все 588** адресов ведут сюда, других хостов нет вовсе.
    /// </summary>
    public const string OfficialRegistry = "https://registry.npmjs.org/";

    /// <summary>
    /// СУММА САМОГО ЗАМКА, ЗАКРЕПЛЁННАЯ В СБОРКЕ (требование владельца 10.10.2026): *«сумма самого
    /// замка закреплена в сборке, чтобы „обновление данных“ не подсунуло другой набор»*.
    ///
    /// ⚠️ Обновляется ТОЛЬКО вместе с замком — и это ловит проверка: `EngineTreeLockTests` считает
    /// сумму файла из дерева и требует, чтобы она совпадала с этой строкой. Забыть обновить нельзя:
    /// тогда `Load()` откажется читать встроенный замок, а проверка назовёт расхождение.
    /// </summary>
    public const string PinnedSum = "25399096f1aedcb58a2750f10e1a1f1af8acab593ce6864f8e23de0d7259d5f2";

    /// <summary>
    /// СУММА ВТОРОГО ЗАМКА — ЛИНИИ 0.2.x (`0.2.1-rc.2`, задача `t4` плана 2.2.0): **683 пакета**,
    /// снят своим `upd-build\make-engine-lock.ps1` из своей изолированной установки. Закреплена
    /// здесь по тому же правилу, что и первая: «обновление данных» не должно подсунуть другой набор.
    ///
    /// ⚠️ Сумма считается по СЫРОМУ тексту файла, как его пишет сценарий замера, — файлы замков
    /// лежат с CRLF (`.gitattributes`: `*.json text eol=crlf`), и это часть суммы, а не мелочь:
    /// после checkout с другими переводами строк сумма перестала бы сходиться.
    /// </summary>
    public const string PinnedSumOnKnown = "f40de5b2723be4167bc9937e910cafccdc3a41b84d5c5248f950802123c0ab83";

    /// <summary>
    /// ВСЕ ЗАКРЕПЛЁННЫЕ СУММЫ: версия движка → сумма файла замка. Два замка — две строки; третий
    /// (следующая линия) добавляется сюда же вместе со своим файлом и ресурсом.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PinnedSums { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EngineRpcSurface.MeasuredOnVersion] = PinnedSum,
            [EngineRpcSurface.KnownOnVersion] = PinnedSumOnKnown,
        };

    /// <summary>Сумма, закреплённая за этой версией движка; пусто — версия незакреплённая.</summary>
    public static string PinnedSumOf(string? version) =>
        version is not null && PinnedSums.TryGetValue(version.Trim(), out var sum) ? sum : string.Empty;

    /// <summary>Замка нет: файла не было, он не разобрался или чужой схемы.</summary>
    public static EngineTreeLock Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<EngineTreeLockEntry>(),
        Array.Empty<KeyValuePair<string, string>>(),
        0,
        string.Empty);

    /// <summary>Есть ли замок. Без него ставить движок «по замку» нечем — и панель это скажет.</summary>
    public bool Any => Entries.Count > 0 && NpmLockJson.Length > 0;

    /// <summary>Все ли записи с суммами: замок без сумм — это список версий, а не замок.</summary>
    public bool FullySummed => Any && Entries.All(entry => entry.HasSum);

    /// <summary>
    /// ВСЁ ЛИ ВЗЯТО С ОФИЦИАЛЬНОГО РЕЕСТРА. Замок — это то, что панель поставит у человека;
    /// пакет с чужого хоста в нём означал бы, что ставится не то, что проверялось.
    /// </summary>
    public bool AllFromRegistry => Any && Entries.All(entry => entry.FromOfficialRegistry);

    /// <summary>Записи НЕ с официального реестра — их панель называет словами при отказе.</summary>
    public IReadOnlyList<EngineTreeLockEntry> ForeignSources =>
        Entries.Where(entry => !entry.FromOfficialRegistry).ToList();

    /// <summary>
    /// ЦЕЛ ЛИ ЗАМОК: число записей совпадает с объявленным в файле. Обрезанный файл (правка руками,
    /// оборванная запись) выглядел бы «замком из двухсот пакетов» — и панель поставила бы половину
    /// дерева, называя его проверенным.
    /// </summary>
    public bool Complete => !Any || Entries.Count == Declared;

    /// <summary>
    /// `package.json`, КОТОРЫЙ ПАНЕЛЬ ПОЛОЖИТ РЯДОМ С ЗАМКОМ. Собирается из КОРНЕВОЙ записи замка:
    /// имя, версия и зависимости — оттуда же, откуда их возьмёт `npm ci`. Своей версии движка
    /// здесь не появляется: она уже стоит точной в зависимостях.
    /// </summary>
    public string PackageJson =>
        "{\n" +
        $"  \"name\": {Quote(ProductName)},\n" +
        $"  \"version\": {Quote(ProductVersion)},\n" +
        "  \"private\": true,\n" +
        "  \"dependencies\": {\n" +
        string.Join(",\n", Dependencies.Select(item => $"    {Quote(item.Key)}: {Quote(item.Value)}")) +
        "\n  }\n}\n";

    /// <summary>Запись про пакет по имени. Нет такой — <c>null</c>, и это честное «в замке его нет».</summary>
    public EngineTreeLockEntry? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Name, name.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// РАЗБОР ФАЙЛА ЗАМКА. Терпимый намеренно (битый JSON — это «замка нет», а не исключение), но
    /// **чужая схема не угадывается**: объявленная версия, отличная от нашей, даёт тот же честный
    /// ответ. Поля без версии или без пути пакета пропускаются: они не описывают пакет.
    ///
    /// ⚠️ Версия движка берётся из оболочки И сверяется с записью самого движка в замке npm:
    /// разошлись — это «две правды об одном дереве», и обе не стоят ничего (то же правило, что
    /// в <see cref="EngineInstallDecisions"/>).
    /// </summary>
    public static EngineTreeLock Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return Empty;
            if (!Ours(root)) return Empty;

            if (!root.TryGetProperty("engine", out var engine) || engine.ValueKind != JsonValueKind.Object)
                return Empty;

            var engineName = Text(engine, "name");
            var engineVersion = Text(engine, "version");
            if (engineName.Length == 0 || engineVersion.Length == 0) return Empty;

            if (!root.TryGetProperty("lock", out var npm) || npm.ValueKind != JsonValueKind.Object) return Empty;
            if (!npm.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object)
                return Empty;

            var declared = root.TryGetProperty("packages", out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var number)
                ? number
                : 0;

            var product = root.TryGetProperty("product", out var productNode) && productNode.ValueKind == JsonValueKind.Object
                ? productNode
                : default;

            var productName = product.ValueKind == JsonValueKind.Object ? Text(product, "name") : string.Empty;
            var productVersion = product.ValueKind == JsonValueKind.Object ? Text(product, "version") : string.Empty;

            var dependencies = new List<KeyValuePair<string, string>>();

            if (packages.TryGetProperty(string.Empty, out var rootEntry) && rootEntry.ValueKind == JsonValueKind.Object
                && rootEntry.TryGetProperty("dependencies", out var rootDependencies)
                && rootDependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in rootDependencies.EnumerateObject())
                {
                    var version = item.Value.ValueKind == JsonValueKind.String
                        ? item.Value.GetString()?.Trim() ?? string.Empty
                        : string.Empty;

                    if (version.Length > 0) dependencies.Add(new KeyValuePair<string, string>(item.Name, version));
                }
            }

            // Корень без зависимостей — ставить нечего: `npm ci` запускается ИМЕННО за ними.
            if (dependencies.Count == 0) return Empty;

            var found = new List<EngineTreeLockEntry>();

            foreach (var item in packages.EnumerateObject())
            {
                var entry = Read(item.Name, item.Value);
                if (entry is not null) found.Add(entry);
            }

            if (found.Count == 0) return Empty;

            // Движок обязан быть в замке — и той же версии, что объявлена в оболочке.
            var enginePath = "node_modules/" + engineName;
            var engineEntry = found.FirstOrDefault(entry =>
                string.Equals(entry.Path, enginePath, StringComparison.OrdinalIgnoreCase));

            if (engineEntry is null) return Empty;
            if (!string.Equals(engineEntry.Version, engineVersion, StringComparison.OrdinalIgnoreCase)) return Empty;

            return new EngineTreeLock(
                engineName, engineVersion, productName, productVersion, found, dependencies, declared, npm.GetRawText());
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// ЗАМОК, ВСТРОЕННЫЙ В СБОРКУ (ресурс ищется по СУФФИКСУ — имя сборки не хардкодится, как
    /// у словарей). Не прочитался — <see cref="Empty"/>, и это ответ, а не падение.
    /// </summary>
    public static EngineTreeLock Load() => EngineTreeLocks.Load().Primary;

    /// <summary>
    /// СУММА ЭТОГО ЗАМКА СОВПАДАЕТ С ЗАКРЕПЛЁННОЙ В СБОРКЕ ДЛЯ ЕГО ВЕРСИИ. Считается по СЫРОМУ
    /// тексту файла — так же, как её считает `Get-FileHash` у сценария замера, поэтому числа
    /// сходятся у сборки, у файла в дереве и у человека, который проверит файл руками.
    ///
    /// Версия берётся из САМОГО текста: замок, подсунутый под чужим именем, не пройдёт по сумме
    /// соседа — у каждого замка своя строка в <see cref="PinnedSums"/>.
    /// </summary>
    public static bool MatchesPinned(string? json, string? version)
    {
        if (string.IsNullOrEmpty(json)) return false;

        var pinned = PinnedSumOf(version);
        if (pinned.Length == 0) return false;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return string.Equals(hash, pinned, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// СУММА ЗАМКА СОВПАДАЕТ С ЗАКРЕПЛЁННОЙ В СБОРКЕ (одноаргументная форма — для НАШЕГО замка,
    /// на котором панель мерилась). Оставлена ради проверок и ради читателя, который держит в руках
    /// один файл; в сборке замков теперь два — их сверяет <see cref="EngineLocks"/>.
    /// </summary>
    public static bool MatchesPinned(string? json) => MatchesPinned(json, EngineRpcSurface.MeasuredOnVersion);

    /// <summary>НАША ЛИ ЭТО СХЕМА: редакция обязана быть объявлена и совпадать (первая не годится).</summary>
    private static bool Ours(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format)) return false;
        if (format.ValueKind != JsonValueKind.Number) return false;

        return format.TryGetInt32(out var number) && number == Format;
    }

    private static EngineTreeLockEntry? Read(string path, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (path.Length == 0 || !path.Contains("node_modules/", StringComparison.Ordinal)) return null;

        var version = Text(item, "version");
        if (version.Length == 0) return null;

        // Имя — хвост пути после ПОСЛЕДНЕГО `node_modules/`: у пакетов со областью оно состоит
        // из двух частей (`@deepseek-ai/dsh`), и разрезать его по слэшу нельзя.
        var slash = path.LastIndexOf("node_modules/", StringComparison.Ordinal);
        var name = path[(slash + "node_modules/".Length)..];

        return new EngineTreeLockEntry(
            path,
            name,
            version,
            Text(item, "integrity"),
            Text(item, "resolved"),
            Flag(item, "optional"),
            Flag(item, "extraneous"),
            List(item, "os"),
            List(item, "cpu"),
            List(item, "libc"),
            Engines(item));
    }

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    /// <summary>ngines.node записи — из чего видно, какой Node нужен ЭТОМУ пакету.</summary>
    private static string Engines(JsonElement item) =>
        item.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object
            ? Text(engines, "node")
            : string.Empty;

    private static bool Flag(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> List(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.String) return new[] { value.GetString()?.Trim() ?? string.Empty };

        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()?.Trim() ?? string.Empty)
            .ToList();
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text);
}
