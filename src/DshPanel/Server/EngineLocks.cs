namespace DshPanel.Server;

/// <summary>
/// ЧТО ПАНЕЛЬ НАШЛА НА МЕСТЕ ЗАМКА ДВИЖКА.
///
/// Замок движка — не выдумка панели: это файл-сосед `&lt;файл&gt;.lock`, который движок создаёт
/// перед записью и снимает сам. **Замер 10.10.2026 (разведка этапа B), опыт с убитым держателем:**
/// писатель взял замок на `settings.yaml` — появился `settings.yaml.lock` на 6 байт со строкой
/// «13268» (номер процесса держателя); `taskkill /F` держателя — замок ОСТАЛСЯ на диске; следующий
/// писатель получил отказ `atomic-write: timed out waiting for the writer lock`; и только после
/// того, как замок убрал человек, запись пошла снова.
///
/// Отсюда три состояния, и каждое требует СВОЕГО обращения: мёртвый замок можно убрать,
/// живой — нельзя никогда, а неразобранный — нельзя тем более: движок говорит об этом прямо
/// («a contender never deletes a lock… an operator removes it only after verifying that no writer
/// still owns it»), и панель обязана вести себя как тот самый осторожный оператор.
/// </summary>
public enum EngineLockState
{
    /// <summary>Файла замка нет — обычное состояние: движок не пишет прямо сейчас.</summary>
    Missing,

    /// <summary>Владелец замка жив — не трогать никогда.</summary>
    Live,

    /// <summary>Владелец мёртв — замок можно убрать, назвав его в журнале.</summary>
    Stale,

    /// <summary>Жив ли владелец — выяснить не удалось: не трогать.</summary>
    Unverifiable,

    /// <summary>В файле не номер процесса (мусор, пусто) — владельца не проверить: не трогать.</summary>
    Unreadable,
}

/// <summary>Найденный замок движка: путь, состояние и номер процесса из файла (0 — номера нет).</summary>
public readonly record struct EngineLock(string Path, EngineLockState State, int Pid);

/// <summary>
/// ЗАМКИ ДВИЖКА: какие файлы он запирает, как понять состояние замка и какие из них панель вправе
/// убрать.
///
/// **Какие именно файлы (замер 10.10.2026 по коду движка):** `settings.yaml` (хранилище настроек),
/// `.credentials.yaml` (ключи доступа), `llm-deepseek/files-v3.json` (указатель загруженных файлов)
/// и КАТАЛОГ `profiles/node_modules` — движок берёт замок и у каталога. Список живёт ОДНИМ местом
/// (<see cref="LockedNames"/>): второй список однажды разошёлся бы с первым, а неверное имя значило
/// бы, что панель ищет замок там, где его не бывает.
///
/// **Правило уборки — одно и короткое:** убирается ТОЛЬКО замок с мёртвым владельцем, и только
/// с записью об этом в журнале (путь и номер). Живой владелец — неприкосновенен, «не смог узнать» —
/// тоже: сомнение решается в пользу чужой работы.
/// </summary>
public static class EngineLocks
{
    /// <summary>
    /// Что движок запирает внутри своего дома. Пути — ОТНОСИТЕЛЬНЫЕ: дом приходит доводом, и другого
    /// способа узнать его у панели нет (у неё свой корень, у движка свой).
    /// </summary>
    public static readonly string[] LockedNames =
    {
        "settings.yaml",
        ".credentials.yaml",
        Path.Combine("llm-deepseek", "files-v3.json"),
        Path.Combine("profiles", "node_modules"),
    };

    /// <summary>
    /// Путь замка для названного файла: `<файл>.lock` в том же каталоге. ОДНО правило на панель —
    /// ровно так замок и создаёт движок.
    /// </summary>
    public static string LockPath(string dshHome, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dshHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        return Path.Combine(dshHome, relative) + ".lock";
    }

    /// <summary>
    /// Состояние замка по содержимому его файла. ЧИСТОЕ решение: ни диска, ни процессов —
    /// поэтому оно и перебирается проверками целиком, включая мусор в файле.
    ///
    /// Пустой текст — «файла нет» (<see cref="EngineLockState.Missing"/>): отсутствие замка
    /// и замок с мусором внутри — РАЗНЫЕ вещи, и путать их нельзя (мусор надо назвать человеку,
    /// а отсутствие — молчание).
    /// </summary>
    /// <param name="text">Содержимое файла замка: номер процесса, как его пишет движок.</param>
    /// <param name="existed">Был ли файл вообще: пустая строка без файла — это «замка нет».</param>
    /// <param name="liveness">Жив ли владелец: три ответа, см. <see cref="ProcessFacts.Liveness"/>.</param>
    public static EngineLockState Decide(string? text, bool existed, Func<int, ProcessFacts.Liveness> liveness)
    {
        ArgumentNullException.ThrowIfNull(liveness);

        if (!existed) return EngineLockState.Missing;

        var value = (text ?? string.Empty).Trim();
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            // Не номер — владельца не проверить. Оставляем и говорим: убрать чужое наугад нельзя.
            return EngineLockState.Unreadable;
        }

        return liveness(pid) switch
        {
            ProcessFacts.Liveness.Dead => EngineLockState.Stale,
            ProcessFacts.Liveness.Alive => EngineLockState.Live,
            _ => EngineLockState.Unverifiable,
        };
    }

    /// <summary>
    /// Осмотреть все замки движка в его доме. Чтение — «только структура»: панель читает ФАЙЛЫ
    /// ЗАМКОВ (номер процесса), а не то, что заперто, и содержимого настроек и ключей не касается.
    /// </summary>
    public static IReadOnlyList<EngineLock> Inspect(string dshHome, Func<int, ProcessFacts.Liveness> liveness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dshHome);
        ArgumentNullException.ThrowIfNull(liveness);

        var found = new List<EngineLock>();

        foreach (var name in LockedNames)
        {
            var path = LockPath(dshHome, name);
            var exists = false;
            var text = string.Empty;

            try
            {
                if (File.Exists(path))
                {
                    exists = true;
                    text = File.ReadAllText(path);
                }
            }
            catch
            {
                // Файл есть, но не читается: это «не смог узнать», а не «замка нет».
                exists = true;
                text = string.Empty;
            }

            var state = Decide(text, exists, liveness);
            if (state == EngineLockState.Missing) continue;

            found.Add(new EngineLock(path, state, PidOf(text)));
        }

        return found;
    }

    /// <summary>Номер процесса из содержимого замка (0 — номера в файле нет). Читается, чтобы назвать его человеку.</summary>
    public static int PidOf(string? text) =>
        int.TryParse(
            (text ?? string.Empty).Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var pid) && pid > 0
            ? pid
            : 0;
}
