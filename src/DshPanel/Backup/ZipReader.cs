using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshPanel.Shell;
using System.Globalization;

namespace DshPanel.Backup;

/// <summary>Запись архива: имя, приведённое к прямой косой черте, и её длина в байтах.</summary>
public sealed record ZipEntry(string Name, long Length);

/// <summary>
/// Что вышло с описью копии. ТРИ разных ответа, и путать их нельзя — «описи нет» и «опись
/// не разбирается» говорят человеку разное: первое значит «это не наша копия», второе —
/// «копия наша, но испорчена». Один ответ на оба случая стоил бы потерянных данных, потому
/// что «нет описи» звучит безобиднее, чем есть.
/// </summary>
public enum ManifestState
{
    /// <summary>Опись есть и разобралась.</summary>
    Read,

    /// <summary>Опись есть, но не разбирается: обрезана, не JSON, пуста.</summary>
    Unreadable,

    /// <summary>Описи в архиве нет вовсе. Это не «архив сломан».</summary>
    Missing,
}

/// <summary>
/// Записи одной группы (корня копии) в архиве. <see cref="Note"/> — словами, почему группа пуста:
/// «не посмотреть» и «ничего нет» — разные ответы, и на этом в проекте уже спотыкались.
/// </summary>
public sealed record ZipGroup(string Prefix, IReadOnlyList<ZipEntry> Files, string Note)
{
    public bool Any => Files.Count > 0;

    /// <summary>Строка для отчёта: сколько файлов в группе или почему их нет.</summary>
    public string Summary() =>
        Any
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipGroupFilesFormat, Prefix, Files.Count)
            : Note.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipGroupEmptyWithNoteFormat, Prefix, Note)
                : string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipGroupEmptyFormat, Prefix);
}

/// <summary>
/// Что дало чтение архива. <see cref="Ok"/> — про АРХИВ: открылся ли он и перечислились ли
/// записи. Опись при этом может быть и не прочитана (<see cref="State"/>), и это не одно и то же
/// с «архив не читается».
/// </summary>
public sealed record ZipReadResult(
    bool Ok,
    string Error,
    IReadOnlyList<ZipEntry> Entries,
    IReadOnlyList<ZipEntry> Unsafe,
    ManifestState State,
    BackupManifest? Manifest,
    string Note)
{
    public static ZipReadResult Fail(string error) =>
        new(false, error, Array.Empty<ZipEntry>(), Array.Empty<ZipEntry>(), ManifestState.Missing, null, string.Empty);

    public int Files => Entries.Count;

    /// <summary>Строка для отчёта и журнала: без путей владельца и без содержимого описи.</summary>
    public string Summary()
    {
        if (!Ok) return string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipNotReadableFormat, Error);

        var manifest = State switch
        {
            ManifestState.Read => PanelStrings.ZipManifestRead,
            ManifestState.Unreadable => PanelStrings.ZipManifestUnreadable,
            _ => PanelStrings.ZipManifestMissing,
        };

        var text = string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipReadSummaryFormat, Files, manifest);
        if (Unsafe.Count > 0) text += string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipUnsafeEntriesFormat, Unsafe.Count);
        if (Note.Length > 0) text += $" · {Note}";
        return text;
    }
}

/// <summary>
/// ЧТЕНИЕ АРХИВА — то, на чём стоит накат. Своими силами, средствами .NET, без 7-Zip:
/// решение владельца 25.09.2026 именно об этом — «создаёт 7-Zip, распаковывает сама панель».
/// Отсюда следует правило, которое здесь и живёт: **копию можно вернуть на машине, где
/// никакого 7-Zip нет**.
///
/// Три вещи, которые читатель делает не «на всякий случай», а потому что без них копия
/// теряется молча:
///
/// 1. **Разделитель нормализуется.** 7-Zip кладёт прямую косую черту, свой обход — тоже,
///    но полагаться на соглашение одного инструмента в разборе ЧУЖОГО файла нельзя: обратный
///    разделитель в записи сделал бы группу «не найденной» при живых файлах внутри.
/// 2. **Регистр имени группы сверяется ТОЧНО** (<see cref="Group"/>). Это ровно та потеря,
///    из-за которой в <see cref="ZipLayout"/> закрыт быстрый путь через 7-Zip: запись ляжет
///    под именем с диска, опись будет искать её под своим — и целый корень исчезнет тихо.
///    Здесь расхождение не сглаживается, а называется: «лежит под другим именем».
/// 3. **Записи с выходом за каталог отсеиваются** (<see cref="IsSafe"/>). Архив — чужой файл,
///    и запись <c>../../…</c> внутри него — это не «странность», а способ записать файл мимо
///    места распаковки. Такие записи не попадают в <see cref="ZipReadResult.Entries"/> вовсе,
///    но и не исчезают: они перечислены в <see cref="ZipReadResult.Unsafe"/> — пропуск обязан
///    быть виден, а не выглядеть тихой потерей (урок v1 про пропущенные ISO).
/// </summary>
public static class ZipReader
{
    /// <summary>
    /// Имя записи в том виде, в каком с ним работает весь остальной код: с прямой косой чертой.
    /// Ничего больше не трогаем — в частности, НЕ срезаем ведущую черту: запись <c>/a.txt</c>
    /// обязана остаться подозрительной (<see cref="IsSafe"/>), а не выглядеть обычной.
    /// </summary>
    public static string Normalize(string? name) => (name ?? string.Empty).Replace('\\', '/');

    /// <summary>Запись-каталог: zip помечает такие хвостовой чертой.</summary>
    public static bool IsDirectory(string? name)
    {
        var text = name ?? string.Empty;
        return text.EndsWith('/') || text.EndsWith('\\');
    }

    /// <summary>
    /// Безопасно ли имя записи. Отказ — на выход за каталог распаковки, а не на «некрасиво»:
    /// абсолютный путь, буква диска, переход на уровень выше.
    /// </summary>
    public static bool IsSafe(string? name)
    {
        var text = Normalize(name);
        if (text.Length == 0) return false;
        if (text.Contains('\0')) return false;
        if (text.StartsWith('/')) return false;
        if (text.Contains(':')) return false;

        foreach (var segment in text.Split('/'))
            if (segment == "..") return false;

        return true;
    }

    /// <summary>Первая часть пути — имя группы. У записи без черты это она сама.</summary>
    public static string Head(string? name)
    {
        var text = Normalize(name);
        var cut = text.IndexOf('/');
        return cut < 0 ? text : text[..cut];
    }

    /// <summary>
    /// Запись принадлежит группе: её путь РАВЕН имени группы или лежит внутри неё.
    ///
    /// Почему не «первый отрезок пути»: группа бывает вложенной (<c>keys/1-ssh</c> — так зовутся
    /// каталоги ключей), и сверка по первому отрезку нашла бы только «keys», то есть ни одного
    /// файла. Нашлось проверкой 26.09.2026, когда ключи вернулись в состав копии.
    /// </summary>
    private static bool InGroup(string name, string group, StringComparison comparison) =>
        name.Equals(group, comparison) || name.StartsWith(group + "/", comparison);

    /// <summary>
    /// Имя группы, которой принадлежит запись, — САМОЕ ДЛИННОЕ из совпавших. Пусто — запись не
    /// принадлежит ни одной группе описи.
    ///
    /// Почему «самое длинное», а не «первое попавшееся»: у вложенных имён (<c>keys/1-ssh</c>)
    /// по началу пути совпадает и <c>keys</c>, и выбор наугад увёл бы файл ключа не в ту группу.
    /// Нужно это накату: он раскладывает записи по группам описи.
    /// </summary>
    public static string GroupOf(string? name, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var text = Normalize(name);
        var best = string.Empty;

        foreach (var candidate in groups)
        {
            var group = ZipLayout.NormalizePrefix(candidate);
            if (group.Length == 0 || group.Length <= best.Length) continue;
            if (!InGroup(text, group, StringComparison.Ordinal)) continue;

            best = group;
        }

        return best;
    }

    /// <summary>
    /// Файлы одной группы: <c>prefix/…</c>. Сверка имени — ТОЧНАЯ, и причина пустоты называется
    /// словами. Отдельно разбирается случай «файлы есть, но группа названа в другом регистре»:
    /// это единственный ответ, по которому человек поймёт, что именно произошло.
    /// </summary>
    public static ZipGroup Group(IReadOnlyList<ZipEntry> entries, string prefix)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var wanted = ZipLayout.NormalizePrefix(prefix);
        if (wanted.Length == 0)
            return new ZipGroup(wanted, Array.Empty<ZipEntry>(), PanelStrings.ZipGroupNameNotSet);

        var files = new List<ZipEntry>();
        var foreign = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            // Группа может быть ВЛОЖЕННОЙ (<c>keys/1-ssh</c> — так зовутся каталоги ключей),
            // поэтому сверяется весь путь до файла, а не первый его отрезок: сверка «по первому
            // отрезку» нашла бы группу «keys» и не нашла бы в ней ни одного файла. Нашлось
            // проверкой 26.09.2026, когда в копию вернулись ключи.
            if (InGroup(entry.Name, wanted, StringComparison.Ordinal))
            {
                files.Add(entry);
            }
            else if (InGroup(entry.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                var depth = wanted.Count(symbol => symbol == '/') + 1;
                foreign.Add(string.Join("/", entry.Name.Split('/').Take(depth)));
            }
        }

        if (files.Count > 0) return new ZipGroup(wanted, files, string.Empty);

        if (foreign.Count > 0)
            return new ZipGroup(wanted, Array.Empty<ZipEntry>(),
                string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipGroupForeignNamesFormat, string.Join("», «", foreign)));

        return new ZipGroup(wanted, Array.Empty<ZipEntry>(), PanelStrings.ZipGroupEntriesMissing);
    }

    /// <summary>
    /// Читает архив: перечисляет файлы и разбирает опись. Опись как ТЕКСТ наружу не отдаётся
    /// намеренно: в ней имя машины и имя пользователя (красная линия 7), а пользы от сырого
    /// текста нет ни у наката, ни у экрана — им нужен разобранный <see cref="BackupManifest"/>.
    /// </summary>
    public static ZipReadResult Read(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return ZipReadResult.Fail(PanelStrings.ZipArchivePathNotSet);

        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full))
            return ZipReadResult.Fail(string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipArchiveMissingFormat, DisplayMask.Path(full)));

        try
        {
            using var zip = ZipFile.OpenRead(full);

            var entries = new List<ZipEntry>();
            var unsafeEntries = new List<ZipEntry>();

            foreach (var entry in zip.Entries)
            {
                var name = Normalize(entry.FullName);

                // Каталоги не восстанавливаем и в список файлов не берём: пустая папка
                // в копии смысла не несёт, а в описи её нет — там только файлы.
                if (IsDirectory(name)) continue;

                var item = new ZipEntry(name, entry.Length);
                if (IsSafe(name)) entries.Add(item);
                else unsafeEntries.Add(item);
            }

            var (state, manifest, note) = ReadManifest(zip);
            return new ZipReadResult(true, string.Empty, entries, unsafeEntries, state, manifest, note);
        }
        catch (Exception exception)
        {
            return ZipReadResult.Fail($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Прочитать ТОЛЬКО опись, не перечисляя записи архива. Нужно списку готовых копий
    /// (<see cref="BackupFolder"/>): полная копия движка — это десятки тысяч записей, и перебирать
    /// их ради одной строки в списке значит заставлять человека ждать на ровном месте.
    /// Ответ тот же, что у полного чтения, — включая причину, по которой описи нет.
    /// </summary>
    public static (ManifestState State, BackupManifest? Manifest, string Note) ReadManifestOnly(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return (ManifestState.Missing, null, PanelStrings.ZipManifestPathNotSet);

        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full))
            return (ManifestState.Missing, null, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestArchiveMissingFormat, DisplayMask.Path(full)));

        try
        {
            using var zip = ZipFile.OpenRead(full);
            return ReadManifest(zip);
        }
        catch (Exception exception)
        {
            return (ManifestState.Unreadable, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static (ManifestState State, BackupManifest? Manifest, string Note) ReadManifest(ZipArchive zip)
    {
        // Сначала точное имя, потом — с точностью до регистра. Второй путь НЕ равнозначен
        // первому, поэтому он и называется в примечании: опись под именем «Manifest.json»
        // найдётся, но человеку об этом стоит знать — инструмент, который её туда положил,
        // соглашения не держит.
        var exact = zip.GetEntry(ZipLayout.ManifestEntry);
        var entry = exact ?? zip.Entries.FirstOrDefault(candidate =>
            string.Equals(Normalize(candidate.FullName), ZipLayout.ManifestEntry, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return (ManifestState.Missing, null,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestEntryMissingFormat, ZipLayout.ManifestEntry));

        var foundUnder = exact is null ? string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestFoundUnderFormat, Normalize(entry.FullName)) : string.Empty;

        string text;
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception exception)
        {
            return (ManifestState.Unreadable, null,
                Join(foundUnder, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestNotReadableFormat, exception.GetType().Name)));
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(text, BackupManifest.ReadOptions);
        }
        catch (JsonException exception)
        {
            // Текст описи в сообщение НЕ попадает: в ней имя машины и пользователя.
            return (ManifestState.Unreadable, null,
                Join(foundUnder, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestJsonErrorFormat, exception.LineNumber)));
        }

        if (manifest is null)
            return (ManifestState.Unreadable, null, Join(foundUnder, PanelStrings.ZipManifestEmpty));

        var notes = new List<string>();
        if (!BackupManifest.IsOurKind(manifest.Kind))
            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestForeignKindFormat, manifest.Kind));

        if (manifest.Format > BackupManifest.SupportedFormat)
            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestVersionFormat, manifest.Format, BackupManifest.SupportedFormat));

        return (ManifestState.Read, manifest, Join(foundUnder, string.Join("; ", notes)));
    }

    private static string Join(string left, string right)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        return left + "; " + right;
    }
}
