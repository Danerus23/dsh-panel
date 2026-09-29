using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// СПИСОК ГОТОВЫХ КОПИЙ В ПАПКЕ — то, что показывает окно «Копии». Это ВЗГЛЯД на папку, а не часть
/// снятия: движок копии (<see cref="BackupEngine"/>) о папке не знает ничего — путь архива ему
/// называет вызывающий.
///
/// Отличие от v1 ровно одно, и оно про честность: **недоступная папка — это не пустой список.**
/// v1 ловила исключение и показывала «копий нет» (<c>BackupService.cs:948–951</c>), то есть человек
/// с недоступной папкой видел ровно то же, что человек без копий, — и делал вывод «копий нет».
/// Здесь причина едет рядом со списком (<see cref="BackupListResult.Problem"/>) и показывается словами.
///
/// ⚠️ **Копии панели 1.x в списке БОЛЬШЕ НЕ ПОКАЗЫВАЮТСЯ ВОВСЕ** (решение владельца 28.09.2026:
/// *«Удали у меня просто эти строки от старой версии панели, они уже не нужны… Пользователей у v1
/// нет, чтобы изобретать»*). Прежде 2.0 показывала их и помечала словами — так был закрыт вопрос
/// «чем возвращать архивы 1.x»; теперь возвращать нечего, потому что предлагать нечего.
///
/// ⚠️ **Файлы на диске при этом НЕ ТРОГАЮТСЯ.** Панель перестаёт их ПОКАЗЫВАТЬ, а не удаляет:
/// «убрать строку» и «стереть архив» — разные вещи, и второго панель без прямой просьбы человека
/// не делает. Скрытие стоит ЗДЕСЬ, в чтении списка, и потому не может задеть ни ротацию, ни
/// расписание: у них свой обход папки (<see cref="BackupRotation.List"/>), и в него этот фильтр
/// не входит вовсе. Читать чужой архив панель по-прежнему умеет
/// (<see cref="Inspect"/>) — он остаётся на диске целым и разбираемым.
/// </summary>
public static class BackupFolder
{
    /// <summary>
    /// Копии в папке, свежие сверху.
    ///
    /// Порядок берётся из ОТМЕТКИ ВРЕМЕНИ В ИМЕНИ, а не из имени целиком: имя начинается
    /// с префикса панели (<c>dsh2-…</c> у нас, <c>dsh-…</c> у 1.x), и посимвольное сравнение
    /// поставило бы все наши архивы впереди всех чужих независимо от даты (а с 28.09.2026 чужих
    /// в списке и нет — но правило порядка осталось правилом про ДАТУ, а не про имя: имя решает
    /// ровно один случай, когда в одну секунду сняты две копии).
    /// </summary>
    public static BackupListResult List(string folder)
    {
        var entries = new List<BackupEntry>();

        try
        {
            if (!Directory.Exists(folder)) return BackupListResult.Empty();

            foreach (var file in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(file);

                // Только НАШИ обычные копии: предохранительная копия 2.0 — не «готовая копия»,
                // а путь назад для панели, и в списке её не было никогда; копии панели 1.x скрыты
                // решением владельца 28.09.2026 (см. комментарий класса). Всё остальное (чужой
                // архив, документ) в список не попадало и не попадает.
                if (!BackupNaming.IsCopy(name)) continue;

                entries.Add(Inspect(file));
            }
        }
        catch (Exception exception)
        {
            // Причина называется, а не превращается в «копий нет»: см. комментарий класса.
            return new BackupListResult(
                Array.Empty<BackupEntry>(),
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.BackupFolderUnreadableFormat,
                    DisplayMask.Path(folder),
                    exception.GetType().Name));
        }

        entries.Sort(CompareFreshness);
        return new BackupListResult(entries, string.Empty);
    }

    /// <summary>
    /// «Свежие первыми»: сначала момент из имени (у копий без разборчивой отметки его нет — такие
    /// уходят в конец), при равном моменте — имя. Имя решает ровно один случай, и он настоящий:
    /// полная и тонкая копия, снятые в одну секунду, обязаны стоять в устойчивом порядке, а не
    /// как повезёт сортировке.
    /// </summary>
    private static int CompareFreshness(BackupEntry left, BackupEntry right)
    {
        var byMoment = Nullable.Compare(right.ListMoment, left.ListMoment);
        if (byMoment != 0) return byMoment;

        return string.CompareOrdinal(right.Name, left.Name);
    }

    /// <summary>
    /// Одна копия: опись ЧИТАЕТСЯ, но архив не распаковывается и записи не перечисляются
    /// (<see cref="ZipReader.ReadManifestOnly"/>) — иначе список полной копии движка заставлял бы
    /// человека ждать, пока переберутся десятки тысяч записей.
    /// </summary>
    public static BackupEntry Inspect(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var full = Path.GetFullPath(archivePath);

        // Размер и время файла — украшение строки: без них копия всё равно показывается,
        // поэтому сбой на этом шаге не отменяет осмотр.
        var bytes = 0L;
        var fileTime = default(DateTime);

        try
        {
            var info = new FileInfo(full);
            bytes = info.Length;
            fileTime = info.LastWriteTime;
        }
        catch
        {
            // см. выше
        }

        var read = ZipReader.ReadManifestOnly(full);
        var manifest = read.Manifest;

        if (manifest is null)
            return new BackupEntry
            {
                Path = full,
                CreatedAt = fileTime,
                Bytes = bytes,
                Readable = false,
                Error = read.Note.Length > 0 ? read.Note : PanelStrings.BackupUnknown,
            };

        var ourKind = BackupManifest.IsOurKind(manifest.Kind);

        return new BackupEntry
        {
            Path = full,

            // Время съёмки берём из ОПИСИ: это время копии, а не время, когда файл в последний раз
            // кто-то трогал. Опись врёт или её нет — остаётся время файла, и это названо строкой.
            CreatedAt = ParseCreated(manifest.CreatedAt) ?? fileTime,
            Bytes = bytes,
            Files = manifest.TotalFiles,
            WithEngine = manifest.WithEngine,
            WithSessions = manifest.WithSessions,
            WithCredentials = manifest.WithCredentials,
            Readable = read.State == ManifestState.Read && ourKind,
            Error = read.Note,
            EngineVersion = manifest.Versions.TryGetValue("dsh", out var version) ? version : string.Empty,
        };
    }

    /// <summary>Время из описи — строкой «гггг-ММ-дд ЧЧ:мм:сс», как писал и v1. Не разобралось — null.</summary>
    private static DateTime? ParseCreated(string? text) =>
        DateTime.TryParseExact(
            (text ?? string.Empty).Trim(),
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// Что нашлось в папке и что об этом честно сказать. <see cref="Problem"/> пуст — всё в порядке;
/// непустой означает, что список НЕПОЛНЫЙ или его не удалось получить, и это видно человеку.
/// </summary>
public sealed record BackupListResult(IReadOnlyList<BackupEntry> Entries, string Problem)
{
    public static BackupListResult Empty() => new(Array.Empty<BackupEntry>(), string.Empty);

    public bool Ok => Problem.Length == 0;
}
