using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshPanel.Update;

/// <summary>
/// СКОЛЬКО БАЙТ ОДНОГО КУСКА УЖЕ ЛЕЖИТ НА ДИСКЕ. Запись живёт в файле состояния, чтобы
/// следующий запуск панели знал, откуда продолжать, а не начинал сорокамегабайтный файл заново.
/// </summary>
public sealed class UpdateDownloadChunk
{
    /// <summary>Номер куска: 0 — первый. Номера идут подряд, без пропусков.</summary>
    public int Index { get; set; }

    /// <summary>Принято байт этого куска: от нуля до его длины.</summary>
    public long Done { get; set; }
}

/// <summary>
/// ЗАПИСЬ О НЕЗАКОНЧЕННОЙ ЗАГРУЗКЕ — то, что переживает перезапуск панели (DEF-035).
///
/// **Зачем файл, если куски и так лежат на диске.** Имя куска — <c>&lt;path&gt;.part-&lt;N&gt;</c>,
/// и по нему НЕЛЬЗЯ понять, чей это кусок: панель могла закрыться на середине одного выпуска,
/// а запуститься уже с другим. Склеить куски двух разных выпусков — значит собрать файл, который
/// не сойдётся ни с одной суммой, или (хуже) сойдётся с чужой. Поэтому рядом с кусками лежит
/// запись, которая называет ФАЙЛ ЦЕЛИКОМ: адрес, ожидаемую сумму, длину, размер куска. Не
/// совпало хоть одно — куски прошлой попытки сносятся, а не «продолжаются».
///
/// **Чего в записи НЕТ и почему.** Здесь нет ни человеческих слов, ни пути рабочей папки
/// относительно профиля: запись читает только загрузчик, а слова живут в словарях. Здесь же
/// нет и «сколько байт принято всего» — это сумма по кускам, и хранить её отдельно значило бы
/// однажды разойтись с кусками.
/// </summary>
public sealed class UpdateDownloadState
{
    /// <summary>Адрес, с которого качается файл. Часть личности файла, а не украшение.</summary>
    public string Url { get; set; } = "";

    /// <summary>Рабочий путь: куда файл ляжет, когда соберётся целиком.</summary>
    public string Path { get; set; } = "";

    /// <summary>Длина файла целиком в байтах (её назвал сервер в <c>Content-Range</c>).</summary>
    public long Total { get; set; }

    /// <summary>Ожидаемая SHA-256 (пусто — «сверять нечем»: так качается файл контрольных сумм).</summary>
    public string Sum { get; set; } = "";

    /// <summary>Размер куска, которым резался файл: план продолжения обязан быть тем же.</summary>
    public long ChunkSize { get; set; }

    /// <summary>Сколько попыток уже было — числом, чтобы «лог попыток» был и в записи, и в журнале.</summary>
    public int Attempts { get; set; }

    /// <summary>Когда запись обновлялась (ISO-8601, UTC). По ней видно, что попытка была, а не «висит».</summary>
    public string Updated { get; set; } = "";

    /// <summary>Куски по номерам: сколько байт каждого уже принято.</summary>
    public List<UpdateDownloadChunk> Chunks { get; set; } = new();

    /// <summary>
    /// ТОТ ЖЕ ЛИ ЭТО ФАЙЛ. Сверяются адрес, рабочий путь, ожидаемая сумма и размер куска —
    /// ровно то, от чего зависит, можно ли приклеить новый кусок к старому. Длину файла здесь
    /// сверить нельзя: её называет сервер только в ответе, поэтому она сверяется отдельно.
    /// </summary>
    public bool SameFile(string url, string path, string sum, long chunkSize) =>
        ChunkSize == chunkSize
        && string.Equals(Url, url, StringComparison.Ordinal)
        && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sum, sum, StringComparison.OrdinalIgnoreCase);

    /// <summary>Сколько байт куска записано (0 — куска нет, номера нет или он не начинался).</summary>
    public long Done(int index)
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.Index == index) return chunk.Done < 0 ? 0 : chunk.Done;
        }

        return 0;
    }

    /// <summary>Момент в том виде, в каком он лежит в файле: UTC, ISO-8601, без зависимости от языка.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// ФАЙЛ СОСТОЯНИЯ И ЖУРНАЛ ПОПЫТОК ЗАГРУЗКИ — рядом, в каталоге состояния панели
/// (<c>state\update\</c>: его отдаёт <see cref="UpdateInstall.UpdateRoot"/>).
///
/// **Почему два файла, а не один.** Состояние — это МАШИННАЯ запись: её читает загрузчик, и
/// человеку она нужна только тогда, когда что-то пошло не так. Журнал попыток — это ЛЕНТА:
/// её дописывают, не переписывают, и по ней видно, что было (обрыв, повтор с какого байта,
/// сколько секунд занял кусок), а не только «что осталось». Сложить их в один файл значило бы
/// переписывать историю при каждом обновлении состояния.
///
/// **Ошибки записи не валят загрузку.** Состояние — удобство и след, а не условие скачивания:
/// каталог может быть занят, диск полон, права урезаны. Тогда панель качает как раньше (куски
/// сносятся при отказе, продолжения не будет), и молчит об этом здесь — сказать человеку
/// нечего: файл всё равно скачается или откажет по своей причине.
/// </summary>
public static class UpdateDownloadStateFile
{
    /// <summary>Имя записи о незаконченной загрузке внутри каталога обновления.</summary>
    public const string Name = "download.state.json";

    /// <summary>Имя журнала попыток: дописывается, не переписывается.</summary>
    public const string LogName = "download-attempts.log";

    /// <summary>Полный путь записи о загрузке в названном каталоге состояния.</summary>
    public static string Of(string stateDir) =>
        System.IO.Path.Combine(stateDir, Name);

    /// <summary>Полный путь журнала попыток в названном каталоге состояния.</summary>
    public static string LogOf(string stateDir) =>
        System.IO.Path.Combine(stateDir, LogName);

    /// <summary>
    /// Запись читается ровно так, как писалась, но имена полей — без учёта регистра: файл мог
    /// править человек, разбираясь, почему загрузка встала. Битая или чужая запись — это
    /// «состояния нет», а не исключение: загрузка обязана идти и без него.
    /// </summary>
    public static UpdateDownloadState? Read(string stateDir)
    {
        try
        {
            var path = Of(stateDir);
            if (!File.Exists(path)) return null;

            var state = JsonSerializer.Deserialize<UpdateDownloadState>(
                File.ReadAllText(path), ReadOptions);

            if (state is null) return null;

            // Запись без личности файла бесполезна и опасна: по ней нельзя решить, можно ли
            // приклеить кусок. Такую не принимаем вовсе.
            if (state.Url.Length == 0 || state.Path.Length == 0) return null;
            if (state.Total <= 0 || state.ChunkSize <= 0) return null;

            // ⚠️ НЕПРАВДОПОДОБНАЯ ДЛИНА — ЭТО МУСОР, А НЕ ПРОДОЛЖЕНИЕ. Запись пишет загрузчик,
            // но файл лежит в каталоге состояния, куда может заглянуть и человек. По длине
            // строится план кусков, поэтому «двадцать гигабайт» из правленого файла означали бы
            // массив на миллионы кусков. Предел взят с запасом: файл выпуска — десятки мегабайт.
            if (state.Total > 1024L * 1024 * 1024) return null;

            state.Chunks ??= new List<UpdateDownloadChunk>();

            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Записать состояние. Пишется во временный файл и подменяет запись одним перемещением:
    /// панель, убитую посреди записи, иначе оставила бы обрезанный JSON, и следующая попытка
    /// начала бы файл заново — то есть потеряла бы уже скачанное.
    /// </summary>
    public static void Write(string stateDir, UpdateDownloadState state)
    {
        try
        {
            Directory.CreateDirectory(stateDir);

            var path = Of(stateDir);
            var temporary = path + ".part";

            File.WriteAllText(temporary, JsonSerializer.Serialize(state, WriteOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // См. правило класса: без состояния загрузка идёт, просто без продолжения.
        }
    }

    /// <summary>Забыть состояние: файл закончен, отвергнут или не принадлежит этому файлу.</summary>
    public static void Clear(string stateDir)
    {
        try
        {
            if (File.Exists(Of(stateDir))) File.Delete(Of(stateDir));
        }
        catch
        {
            // Не убралось — не беда: личность файла в записи не даст приклеить чужое.
        }
    }

    /// <summary>Дописать строку журнала. Лента, а не состояние: её никто не переписывает.</summary>
    public static void Append(string stateDir, string line)
    {
        try
        {
            Directory.CreateDirectory(stateDir);
            File.AppendAllText(LogOf(stateDir), line + Environment.NewLine);
        }
        catch
        {
            // Журнал — след, а не условие загрузки.
        }
    }

    /// <summary>Пишется с отступами и без экранирования: файл читают глазами, когда разбирают дефект.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Чтение терпимо к регистру имён: запись мог править человек.</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// СТРОКА ЖУРНАЛА ОБ ОДНОЙ ПОПЫТКЕ КУСКА. Значения, а не фразы: журнал технический, его читают,
/// когда разбирают «почему 8 МиБ качались три минуты» (DEF-035). Из него видно номер куска,
/// номер попытки, с какого байта спросили, сколько приняли, чем кончилось и сколько это заняло.
///
/// ⚠️ Секунды пишутся с ТОЧКОЙ как разделителем и всегда в инвариантной культуре: журнал
/// сравнивают числом, а не словом, и запятая вместо точки сделала бы разбор зависимым от языка.
/// </summary>
public static class UpdateDownloadAttemptLine
{
    /// <summary>Строка вида <c>…Z chunk=1 attempt=2 from=308192 want=291808 got=8192 outcome=interrupted seconds=0.012 detail="…"</c>.</summary>
    public static string Of(
        DateTimeOffset at,
        int chunk,
        int attempt,
        long from,
        long want,
        long got,
        UpdateDownloadResult result,
        TimeSpan elapsed)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0} chunk={1} attempt={2} from={3} want={4} got={5} outcome={6} seconds={7:0.000}",
            UpdateDownloadState.Stamp(at),
            chunk,
            attempt,
            from,
            want,
            got,
            NameOf(result.Outcome),
            elapsed.TotalSeconds);

        var detail = OneLine(result.Detail);

        return detail.Length > 0 ? line + " detail=\"" + detail + "\"" : line;
    }

    /// <summary>Короткое техническое имя исхода — то же, что в отказе человека, но без слов фразы.</summary>
    public static string NameOf(UpdateDownloadOutcome outcome) => outcome switch
    {
        UpdateDownloadOutcome.Ok => "ok",
        UpdateDownloadOutcome.SumMismatch => "sum-mismatch",
        UpdateDownloadOutcome.Interrupted => "interrupted",
        UpdateDownloadOutcome.HttpRefused => "http-refused",
        UpdateDownloadOutcome.NoLength => "no-length",
        _ => outcome.ToString(),
    };

    /// <summary>
    /// Подробность — в одну строку: причина обрыва приходит от среды и может нести перевод строки,
    /// а журнал читают построчно. Кавычки внутри не экранируются: строка журнала не JSON.
    /// </summary>
    private static string OneLine(string detail) =>
        (detail ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>
/// МЕТРИКА СКАЧИВАНИЯ: «8 МиБ за 30 секунд» (DEF-035). Не настройка и не таймаут — ПОВОД
/// посмотреть в журнал: кусок такого размера, пришедший медленнее, означает не «медленный
/// канал», а повторную загрузку того же куска (именно так выглядел дефект: 8 МиБ за 3–5 минут
/// при том же запросе за 4,9 с).
///
/// Порог назван числом и лежит рядом с размером куска: мерить «8 МиБ за 30 с» на куске другого
/// размера — значит мерить не то. Замер, на котором стоит политика (8 МиБ — 2,28 с), снят
/// 10.10.2026 и записан у <see cref="HttpUpdateDownload"/>.
/// </summary>
public static class UpdateDownloadMeasure
{
    /// <summary>Сколько байт считается «куском замера»: ровно размер куска панели (8 МиБ).</summary>
    public const long Bytes = 8L * 1024 * 1024;

    /// <summary>Предел, дольше которого кусок замера считается поводом для разбора.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Кусок замера пришёл медленнее предела.</summary>
    public static bool Overdue(long bytes, TimeSpan elapsed) => bytes >= Bytes && elapsed > Limit;

    /// <summary>Строка журнала: сколько байт, сколько секунд, каков предел и просрочено ли.</summary>
    public static string Line(long bytes, TimeSpan elapsed) => string.Format(
        CultureInfo.InvariantCulture,
        "metric=8MiB bytes={0} seconds={1:0.000} limit={2:0} slow={3}",
        bytes,
        elapsed.TotalSeconds,
        Limit.TotalSeconds,
        Overdue(bytes, elapsed) ? "yes" : "no");
}
