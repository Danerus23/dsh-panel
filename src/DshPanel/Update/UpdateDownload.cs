using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ЧТО ПАНЕЛЬ ДЕЛАЕТ С ВЫПУСКОМ ПРЯМО СЕЙЧАС. Этап назван перечислением, а не строкой:
/// подпись для человека собирает дирижёр по ключу, а решение «показывать ли ход» остаётся
/// за интерфейсом. Строка здесь означала бы вторую таблицу подписей, которая однажды
/// разойдётся с первой.
/// </summary>
public enum UpdateStage
{
    /// <summary>Скачивается файл контрольных сумм.</summary>
    Sums,

    /// <summary>Скачивается архив панели.</summary>
    Archive,

    /// <summary>Скачивается установщик выпуска.</summary>
    Setup,

    /// <summary>Проверяется SHA-256 скачанного.</summary>
    Verify,

    /// <summary>Архив распаковывается, и сверяется версия внутри.</summary>
    Unpack,

    /// <summary>Снимается страховочная копия прежней панели.</summary>
    Backup,

    /// <summary>Пишется сценарий замены и шапка журнала.</summary>
    Script,
}

/// <summary>
/// ХОД СКАЧИВАНИЯ: этап, имя файла и сколько уже принято. <see cref="Total"/> меньше нуля
/// означает «размер неизвестен» (сервер не назвал длину) — тогда <see cref="Percent"/> равен -1,
/// и показывать «0 %» вместо честного «неизвестно» нельзя: человек решил бы, что загрузка встала.
/// </summary>
public sealed record UpdateProgress(UpdateStage Stage, string Name, long Done, long Total)
{
    /// <summary>Сколько процентов уже есть; -1 — размер неизвестен.</summary>
    public int Percent => Total > 0
        ? (int)Math.Clamp(Done * 100L / Total, 0L, 100L)
        : -1;
}

/// <summary>
/// ЧЕМ КОНЧИЛАСЬ ЗАГРУЗКА ФАЙЛА ВЫПУСКА. Причина названа ТЕХНИЧЕСКИ (типом), а человеческую фразу
/// берёт на себя строка по ключу отказа: второй набор подписей в движке разошёлся бы со словарём
/// на первой же правке перевода.
///
/// **Почему это перечисление, а не исключение.** Загрузка кусками и сверка общей суммы — это
/// несколько РАЗНЫХ бед, и раньше все они сходились в одно «DownloadFailed»: отличить «сеть
/// оборвалась и попытки кончились» от «скачался не тот файл» можно было только словами, а слова
/// причины однажды уже стояли русским текстом посреди английской фразы (дефект 29.09.2026).
/// Теперь различие видно типом, а не текстом.
/// </summary>
public enum UpdateDownloadOutcome
{
    /// <summary>Файл скачался целиком и (если сумма названа) прошёл сверку, лежит по рабочему пути.</summary>
    Ok,

    /// <summary>Сумма не сошлась: скачался не тот файл. Рабочий путь НЕ занят.</summary>
    SumMismatch,

    /// <summary>Соединение обрывалось, и попытки кончились. Рабочий путь НЕ занят.</summary>
    Interrupted,

    /// <summary>Сервер отказал по HTTP (ответ не 200 и не 206). Рабочий путь НЕ занят.</summary>
    HttpRefused,

    /// <summary>Сервер не назвал длину содержимого — резать нечего, класть в рабочий путь нечего.</summary>
    NoLength,
}

/// <summary>
/// ОТВЕТ ЗАГРУЗКИ: причина (<see cref="Outcome"/>), её техническая подробность и то, что
/// ожидалось и что получилось.
///
/// <see cref="Detail"/> — ЗНАЧЕНИЕ, а не фраза: имя исключения, строка «HTTP 404», прочитанная
/// и ожидаемая сумма. Человеческих слов здесь нет намеренно: слова живут в трёх словарях
/// (см. <see cref="UpdateRefusalLines"/>), а это значение подставляется в фразу человека.
/// </summary>
public sealed record UpdateDownloadResult(
    UpdateDownloadOutcome Outcome,
    string Detail = "",
    string Expected = "",
    string Actual = "")
{
    /// <summary>Файл на рабочем пути — и он проверен, если сумма была названа.</summary>
    public bool Ok => Outcome == UpdateDownloadOutcome.Ok;

    /// <summary>Сошлось.</summary>
    public static UpdateDownloadResult Done { get; } = new(UpdateDownloadOutcome.Ok);

    /// <summary>
    /// ТЕХНИЧЕСКАЯ СТРОКА ДЛЯ ЖУРНАЛА И ОТКАЗА: случай и его подробность. Живёт здесь, а не у
    /// вызывающего, потому что собирать её в двух местах — значит однажды собрать по-разному.
    /// </summary>
    public string Report()
    {
        var name = Outcome switch
        {
            UpdateDownloadOutcome.Ok => "ok",
            UpdateDownloadOutcome.SumMismatch => "sum-mismatch",
            UpdateDownloadOutcome.Interrupted => "interrupted",
            UpdateDownloadOutcome.HttpRefused => "http-refused",
            UpdateDownloadOutcome.NoLength => "no-length",
            _ => Outcome.ToString(),
        };

        return Detail.Length > 0 ? name + ": " + Detail : name;
    }
}

/// <summary>
/// ЧТО ИМЕННО МОЖНО СКАЧАТЬ, И НАСКОЛЬКО ЧАСТО ЭТО ПОВТОРЯТЬ. Отдельная запись, а не «сколько
/// влезло»: <see cref="ChunkSize"/> закреплён ЗАМЕРОМ (см. <see cref="HttpUpdateDownload"/>),
/// а попытки и пауза — часть уговора, который проверки подставляют вместе с размером куска.
/// </summary>
public sealed record UpdateDownloadPolicy(long ChunkSize, int Attempts, TimeSpan Pause)
{
    /// <summary>Политика панели, закреплённая замером 10.10.2026.</summary>
    public static UpdateDownloadPolicy Default { get; } =
        new(HttpUpdateDownload.ChunkSize, HttpUpdateDownload.ChunkAttempts, HttpUpdateDownload.ChunkPause);

    /// <summary>Попыток меньше одной не бывает: «не пробовать» — это не загрузка.</summary>
    public int Tries => Attempts < 1 ? 1 : Attempts;

    /// <summary>Кусок меньше байта не бывает: иначе резать нечего.</summary>
    public long Size => ChunkSize < 1 ? 1 : ChunkSize;
}

/// <summary>
/// ОТКУДА ПАНЕЛЬ БЕРЁТ ФАЙЛЫ ВЫПУСКА. Отдельный уговор по той же причине, что
/// <see cref="IUpdateClient"/>: проверки подставляют свой источник и в сеть не ходят ВОВСЕ,
/// а живая загрузка делается только там, где у прогона есть право.
///
/// **Обещание реализации, и оно проверяется прогоном** (<c>UpdateDownloadTests</c>):
///
/// 1. скачанное лежит РОВНО по <c>path</c> — и только тогда, когда файл собран ЦЕЛИКОМ
///    (и сошлась общая сумма, если <c>expectedSum</c> названа). **Недокачанный файл никогда
///    не занимает место рабочего**: куски пишутся во временные <c>&lt;path&gt;.part-&lt;N&gt;</c>,
///    сборка — в <c>&lt;path&gt;.part</c>, а рабочий путь получается атомарной заменой;
/// 2. причина неудачи возвращается ТИПОМ (<see cref="UpdateDownloadOutcome"/>), а не исключением
///    и не фразой: проверки различают «не сошлась сумма», «обрыв», «HTTP отказал» и «длина
///    не названа» по значению, а не по тексту;
/// 3. <c>expectedSum</c> — ОБЩАЯ сумма файла. Пустая значит «сверять нечем»: так качается файл
///    контрольных сумм (сумму которого взять неоткуда — это законный случай, а не дыра).
///    Такой файл тоже кладётся на рабочий путь только целиком;
/// 4. временных хвостов (<c>*.part*</c>) рядом с рабочим путём после ответа не остаётся —
///    ни при удаче, ни при неудаче.
/// </summary>
public interface IUpdateDownload
{
    /// <param name="url">Адрес вложения выпуска.</param>
    /// <param name="path">Рабочий путь: файл занимает его только собранным целиком.</param>
    /// <param name="expectedSum">
    /// Ожидаемая SHA-256 (64 шестнадцатеричных знака, регистр не важен) или пусто/<c>null</c> —
    /// «сверять нечем».
    /// </param>
    /// <param name="progress">Ход загрузки: принято байт и длина файла (-1 — не названа).</param>
    /// <param name="policy">Размер куска, число попыток и пауза между ними (пусто — политика панели).</param>
    UpdateDownloadResult Save(
        string url,
        string path,
        string? expectedSum,
        Action<long, long>? progress,
        UpdateDownloadPolicy? policy = null);
}

/// <summary>
/// ФАЙЛЫ ВЫПУСКА ПО HTTP — одна дверь в сеть за скачиванием.
///
/// Устроено как <see cref="HttpUpdateClient"/>: живой путь отделён уговором, поэтому проверки
/// подставляют готовые байты и в сеть не ходят, а панель ходит ровно там, где имеет право.
///
/// **Четыре правила, и ни одно не украшение.**
///
/// 1. **<c>User-Agent</c> обязателен.** Без него GitHub отвечает 403 на любой запрос, и панель
///    показала бы отказ вместо загрузки.
/// 2. **Потоковое чтение** (<c>ResponseHeadersRead</c>): архив бывает в сотни мегабайт, и держать
///    его целиком в памяти значит уронить панель на слабой машине.
/// 3. **Таймаут в минутах, а не в секундах.** Файл выпуска — не ответ API: двадцать секунд
///    отсекли бы загрузку на медленном канале. И таймаут этот — НА КУСОК: с куском в 8 МиБ
///    двадцать минут значат «канал совсем встал», а не «файл большой».
/// 4. **Куски по 8 МиБ, поток ОДИН.** Числа ниже — ЗАМЕР владельца 10.10.2026 на нашем файле
///    выпуска (<c>https://github.com/Danerus23/dsh-panel/releases/download/v2.0.3/DshPanel.zip</c>,
///    43 855 793 Б), а не догадка:
///
///      * Range сервер поддерживает: ответ на запрос куска — <c>206</c>,
///        <c>Accept-Ranges: bytes</c>, <c>Content-Length: 43855793</c>;
///      * кусок 8 МиБ — 2,28 с; 16 МиБ — 2,15 с; 32 МиБ — 3,57 с (накладные расходы на запрос
///        отыгрываются уже к 8 МиБ);
///      * **параллель ЗАМЕРЕНА ХУЖЕ**: 16 МиБ одним потоком — 2,15 с, а четырьмя кусками
///        по 4 МиБ одновременно — 3,40 с (узкое место — канал, а не клиент).
///
///    Отсюда и правило: **число потоков не «настраивается», а закреплено замером** — поток один.
///
/// ⚠️ Отказ HTTP называется технически («HTTP 404»), а не фразой для человека: фразу берёт
/// на себя строка по ключу отказа (<see cref="UpdateRefusals.Key"/>), и второго набора подписей
/// здесь быть не должно.
/// </summary>
public sealed class HttpUpdateDownload : IUpdateDownload
{
    /// <summary>
    /// РАЗМЕР КУСКА — 8 МиБ, и он взят ИЗ ЗАМЕРА, а не выбран «на глаз»: на этом размере
    /// накладные расходы на запрос уже отыграны (8 МиБ — 2,28 с против 16 МиБ — 2,15 с
    /// и 32 МиБ — 3,57 с), а память панели не зависит от размера файла вовсе. Меньший кусок
    /// множит запросы, больший — ничего не ускоряет. Замер — в описании класса.
    /// </summary>
    public const long ChunkSize = 8L * 1024 * 1024;

    /// <summary>
    /// СКОЛЬКО РАЗ ПРОБОВАТЬ ОДИН КУСОК. Обрыв соединения — обычное дело на мобильной сети,
    /// и без повторов одна помеха отменяла бы всю загрузку. Три попытки на кусок: обрыв
    /// на КАЖДОЙ из трёх — это уже не помеха, а отсутствие канала, и об этом человеку говорят
    /// отказом (<see cref="UpdateDownloadOutcome.Interrupted"/>), а не бесконечным ожиданием.
    /// </summary>
    public const int ChunkAttempts = 3;

    /// <summary>
    /// ПАУЗА МЕЖДУ ПОПЫТКАМИ ОДНОГО КУСКА. Взята у восстановления доступа к ключу
    /// (<c>Server\ServerController.cs</c>, <c>ForgetPause</c>): две секунды — время, за которое
    /// короткий разрыв канала успевает закрыться, и человек ещё не считает панель повисшей.
    /// </summary>
    public static readonly TimeSpan ChunkPause = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;

    public HttpUpdateDownload(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

    public UpdateDownloadResult Save(
        string url,
        string path,
        string? expectedSum,
        Action<long, long>? progress,
        UpdateDownloadPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var plan = policy ?? UpdateDownloadPolicy.Default;
        var wanted = (expectedSum ?? string.Empty).Trim();

        // Следов прошлой попытки быть не должно: иначе «сборка» могла бы склеить кусок от
        // прошлого запуска панели с куском от нынешнего, и сумма сошлась бы на смеси двух файлов.
        CleanupParts(path);

        // --- ПЕРВЫЙ ЗАПРОС: он же и нулевой кусок ---------------------------------------------
        //
        // ⚠️ ОДИН ЗАПРОС, А НЕ ДВА, и это не мелочь: запрос «а есть ли куски» отдельным обращением
        // без `Range` отдаёт ЦЕЛЫЙ файл — на нашем выпуске (43 855 793 Б) это 43 МБ трафика,
        // выброшенных в мусор, то есть загрузка ровно вдвое дольше. Здесь `Range: bytes=0-`
        // спрашивается сразу, и ЭТОТ ЖЕ ответ уходит в нулевой кусок: ответ 206 и есть «куски есть».
        //
        // ⚠️ ОТВЕТ 200 И ОТВЕТ 206 СЧИТАЮТ ДЛИНУ ПО-РАЗНОМУ, и на этом панель уже спотыкалась
        // (замерено 10.10.2026 своей петлёй, разбор — в `docs\HISTORY.md`): у 206 в
        // `Content-Length` лежит длина КУСКА, а у 200 — длина ВСЕГО, ЧТО СЕРВЕР СОБИРАЕТСЯ ОТДАТЬ
        // (сервер вправе ответить на запрос куска целым файлом и назвать его длину). Принять 200
        // за 206 значит решить, что файл кончается там, где кончается ответ, — и записать в рабочий
        // путь обрезок, который сумма потом назовёт «не тем файлом».
        //
        // Сервер, ответивший 200, кусков не поддерживает: берём файл одним ответом, без `Range`.
        // Следующая тонкость ЗАМЕРЕНА: сервер ОБЯЗАН на `bytes=0-` вернуть либо хоть один байт,
        // либо 416, поэтому «файл пуст» и «сервер не умеет куски» неразличимы — оба кончаются
        // одним запросом, а сумма всё равно считается с файла на диске.

        long total;
        var ranged = false;
        var firstFrom = 0L;
        HttpResponseMessage? first;

        try
        {
            first = _http.Send(Request(url, 0, range: true), HttpCompletionOption.ResponseHeadersRead);

            if (!first.IsSuccessStatusCode)
            {
                var status = Status(first);
                first.Dispose();

                return new UpdateDownloadResult(UpdateDownloadOutcome.HttpRefused, status);
            }

            // Длина нужна ОБЯЗАТЕЛЬНО, и это не придирка: без неё неизвестно, сколько кусков
            // просить (и кончился ли файл вообще), а «прочитали до конца потока» и «файл дошёл
            // целиком» — не одно и то же. Класть в рабочий путь то, чью полноту нечем доказать,
            // панель не станет: недокачанный файл никогда не занимает место рабочего.
            var declared = first.Content.Headers.ContentLength ?? -1;

            if (declared < 0)
            {
                var status = Status(first);
                first.Dispose();

                return new UpdateDownloadResult(UpdateDownloadOutcome.NoLength, status);
            }

            if (first.StatusCode == HttpStatusCode.PartialContent)
            {
                // 206: длина ЭТОГО ответа — длина КУСКА, а сколько всего в файле, говорит
                // `Content-Range`. Нет его — сервер ответил «куском» неизвестно чего.
                var whole = TotalOf(first);

                if (whole < 0)
                {
                    var status = Status(first);
                    first.Dispose();

                    return new UpdateDownloadResult(UpdateDownloadOutcome.NoLength, status);
                }

                total = whole;
                ranged = true;
            }
            else
            {
                // 200: кусков нет, ответ — целый файл, и он уже открыт. Но спрашивать его надо
                // БЕЗ `Range`: неизвестно, как сервер понимает запрос куска, а «получи ровно
                // весь файл» — единственное, о чём с таким сервером можно договориться.
                first.Dispose();

                first = _http.Send(Request(url, 0, range: false), HttpCompletionOption.ResponseHeadersRead);

                if (!first.IsSuccessStatusCode)
                {
                    var status = Status(first);
                    first.Dispose();

                    return new UpdateDownloadResult(UpdateDownloadOutcome.HttpRefused, status);
                }

                total = first.Content.Headers.ContentLength ?? -1;

                if (total < 0)
                {
                    var status = Status(first);
                    first.Dispose();

                    return new UpdateDownloadResult(UpdateDownloadOutcome.NoLength, status);
                }
            }
        }
        catch (Exception error)
        {
            return new UpdateDownloadResult(UpdateDownloadOutcome.Interrupted, Describe(error));
        }

        progress?.Invoke(0, total);

        // --- ГРАНИЦЫ КУСКОВ: подряд, без пропусков и наложений --------------------------------

        var ranges = new List<(long From, long To)>();

        for (var from = firstFrom; from < total; from += plan.Size)
        {
            ranges.Add((from, Math.Min(from + plan.Size, total) - 1));
        }

        var received = 0L;

        // --- ЗАГРУЗКА КУСКОВ, У КАЖДОГО СВОЙ СЧЁТЧИК ПОПЫТОК И СВОЯ ПАУЗА ---------------------

        for (var index = 0; index < ranges.Count; index++)
        {
            var (from, to) = ranges[index];
            var want = to - from + 1;
            var part = PartPath(path, index);

            var refusal = new UpdateDownloadResult(UpdateDownloadOutcome.Interrupted);

            for (var attempt = 1; attempt <= plan.Tries; attempt++)
            {
                // Первый ответ уже открыт: он и есть первый кусок. Повтор после обрыва и все
                // следующие куски спрашиваются своими запросами.
                var ready = index == 0 && attempt == 1 ? first : null;

                refusal = Take(url, from, part, want, received, total, progress, ranged, ready);

                if (refusal.Ok)
                {
                    received += want;
                    break;
                }

                // Отказ HTTP повторять нечем: сервер уже ответил, и второй запрос к тому же
                // адресу даст тот же ответ. Повторы — для ОБРЫВА соединения.
                if (refusal.Outcome == UpdateDownloadOutcome.HttpRefused) break;

                TryDelete(part);

                if (attempt < plan.Tries) Thread.Sleep(plan.Pause);
            }

            // ⚠️ ХВОСТ ОБРЫВА УБИРАЕТСЯ ЗДЕСЬ, А НЕ В `finally`: в `finally` он сносил бы и те
            // куски, которые ещё нужны сборке, — то есть удалял бы работу, которую только что
            // сделали. Правило при этом то же: «недокачанный файл никогда не занимает место
            // рабочего», и рабочий путь получает файл ТОЛЬКО собранным целиком.
            if (!refusal.Ok)
            {
                CleanupParts(path);
                return refusal;
            }
        }

        // --- СБОРКА ЦЕЛОГО -------------------------------------------------------------------

        var assembled = AssembledPartPath(path);

        try
        {
            using (var target = File.Create(assembled))
            {
                for (var index = 0; index < ranges.Count; index++)
                {
                    using var source = File.OpenRead(PartPath(path, index));
                    source.CopyTo(target);
                }
            }
        }
        catch (Exception error)
        {
            CleanupParts(path);
            return new UpdateDownloadResult(UpdateDownloadOutcome.Interrupted, Describe(error));
        }

        // Куски своё дело сделали: сборка больше не нуждается в них, а рядом с рабочим путём
        // им делать нечего. ⚠️ СБОРКА НЕ ТРОГАЕТСЯ — она и есть тот файл, который сейчас
        // проверяется суммой и занимает рабочий путь; снести её здесь значило бы снести работу.
        ClearChunks(path);

        // --- СВЕРКА ОБЩЕЙ СУММЫ — ДО ЗАНЯТИЯ РАБОЧЕГО ПУТИ ------------------------------------

        var actual = UpdateSums.Sha256(assembled);

        if (wanted.Length > 0 && !string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase))
        {
            // Скачался не тот файл (или обрезок), и рабочего места он НЕ займёт.
            CleanupParts(path);
            return new UpdateDownloadResult(UpdateDownloadOutcome.SumMismatch, string.Empty, wanted, actual);
        }

        // --- РАБОЧИЙ ПУТЬ: АТОМАРНАЯ ЗАМЕНА ---------------------------------------------------

        try
        {
            File.Move(assembled, path, overwrite: true);
        }
        catch (Exception error)
        {
            CleanupParts(path);
            return new UpdateDownloadResult(UpdateDownloadOutcome.Interrupted, Describe(error));
        }

        progress?.Invoke(total, total);
        return UpdateDownloadResult.Done;
    }

    /// <summary>
    /// ОДИН КУСОК: запрос (или уже открытый ответ первого запроса), чтение потока и запись
    /// во временный файл этого куска. Возвращает «получилось» либо ПРИЧИНУ; исключение наружу
    /// не летит — обрыв соединения обязан быть замером (типом ответа), а не исключением,
    /// которое кто-то не поймал.
    /// </summary>
    private UpdateDownloadResult Take(
        string url,
        long from,
        string part,
        long want,
        long received,
        long total,
        Action<long, long>? progress,
        bool ranged,
        HttpResponseMessage? ready = null)
    {
        try
        {
            using var answer = ready ?? _http.Send(
                Request(url, from, ranged), HttpCompletionOption.ResponseHeadersRead);

            if (!answer.IsSuccessStatusCode)
                return new UpdateDownloadResult(UpdateDownloadOutcome.HttpRefused, Status(answer));

            using var source = answer.Content.ReadAsStream();
            using var target = File.Create(part);

            var buffer = new byte[81920];
            long got = 0;

            // ⚠️ ЧИТАЕМ ДО КОНЦА КУСКА, А НЕ «ОДНИМ ЧТЕНИЕМ». `Stream.Read` ВПРАВЕ вернуть
            // меньше запрошенного: так выглядит и оборванное соединение, и медленный канал,
            // и внутренняя буферизация. Проверка «вернулось не ноль — значит кусок цел» на
            // обрыве МОЛЧА ЗЕЛЕНЕЕТ: замерено 10.10.2026 на своей петле — оборванный ответ
            // («Content-Length» обещал 400 000, отдано 200 000) читался короткими чтениями,
            // и обрезок занимал место целого куска. Отсюда правило: кусок ЦЕЛ только тогда,
            // когда прочитано РОВНО столько байт, сколько он обещает.
            while (got < want)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, want - got));
                if (read <= 0) break;

                target.Write(buffer, 0, read);
                got += read;

                progress?.Invoke(received + got, total);
            }

            // НЕДОБОР — ЭТО ОБРЫВ: соединение, оборванное на середине ответа, выглядит именно
            // так. Без этой сверки обрезок считался бы готовым куском, и панель собрала бы
            // из обрезков файл с чужой суммой.
            if (got != want)
            {
                return new UpdateDownloadResult(
                    UpdateDownloadOutcome.Interrupted,
                    string.Format(CultureInfo.InvariantCulture, "read {0} of {1}", got, want));
            }

            return UpdateDownloadResult.Done;
        }
        catch (Exception error)
        {
            return new UpdateDownloadResult(UpdateDownloadOutcome.Interrupted, Describe(error));
        }
    }

    /// <summary>
    /// Запрос файла или куска: <c>Range: bytes=&lt;с&gt;-</c>, когда <paramref name="range"/>.
    /// Верхняя граница НЕ называется намеренно — лишняя граница в запросе это лишний способ
    /// разойтись с сервером в понимании диапазона, а «не больше ли он отдал» панель всё равно
    /// проверяет на своей стороне (см. <see cref="Take"/>).
    /// </summary>
    private static HttpRequestMessage Request(string url, long from, bool range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Без User-Agent GitHub отвечает 403 на любой запрос — см. правило 1 у класса.
        request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);

        if (range)
        {
            request.Headers.TryAddWithoutValidation(
                "Range", "bytes=" + from.ToString(CultureInfo.InvariantCulture) + "-");
        }

        return request;
    }

    /// <summary>
    /// СКОЛЬКО ВСЕГО БАЙТ В ФАЙЛЕ — из <c>Content-Range</c> ответа 206 («bytes 0-8388607/43855793»).
    /// Меньше нуля — заголовка нет или он не разобран: «кусок неизвестно чего» класть некуда.
    /// </summary>
    private static long TotalOf(HttpResponseMessage answer)
    {
        if (!answer.Content.Headers.TryGetValues("Content-Range", out var values)) return -1;

        foreach (var value in values)
        {
            var slash = value.LastIndexOf('/');
            if (slash < 0) continue;

            if (long.TryParse(
                value[(slash + 1)..].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var total))
            {
                return total;
            }
        }

        return -1;
    }

    /// <summary>Технический ответ сервера: «HTTP 404» — значение, а не фраза для человека.</summary>
    private static string Status(HttpResponseMessage answer) =>
        "HTTP " + ((int)answer.StatusCode).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Причина отката словами СРЕДЫ (тип и сообщение исключения): заменить её нечем — иначе
    /// причина исчезнет вовсе. Так же поступает отказ записи файлов замены в <c>UpdateInstall</c>.
    /// </summary>
    private static string Describe(Exception error) =>
        error.GetType().Name + ": " + error.Message;

    /// <summary>Временный файл куска рядом с рабочим путём: <c>&lt;path&gt;.part-&lt;N&gt;</c>.</summary>
    public static string PartPath(string path, int index) =>
        path + ".part-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Временный файл сборки: <c>&lt;path&gt;.part</c>.</summary>
    public static string AssembledPartPath(string path) => path + ".part";

    /// <summary>
    /// Убрать ВСЕ временные файлы: сборку и все куски. Имена кусков перебираются, а не берутся
    /// «сколько мы насчитали»: панель могла быть закрыта на середине загрузки, и файлов от
    /// прошлого раза на диске больше, чем знает нынешний прогон.
    /// </summary>
    private static void CleanupParts(string path)
    {
        TryDelete(AssembledPartPath(path));
        ClearChunks(path);
    }

    /// <summary>
    /// Убрать ТОЛЬКО куски, не трогая сборку. Отдельно от <see cref="CleanupParts"/> потому,
    /// что у этих двух уборок разный смысл: куски — мусор ВСЕГДА, а сборка — мусор лишь до тех
    /// пор, пока она не прошла сверку и не заняла рабочий путь.
    /// </summary>
    private static void ClearChunks(string path)
    {
        for (var index = 0; index < 4096; index++)
        {
            TryDelete(PartPath(path, index));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Не убралось — не беда: место освободит следующая загрузка, а рабочий путь
            // от этого не занят (он занимается ТОЛЬКО целиком собранным файлом).
        }
    }
}

/// <summary>
/// ЧТО СКАЗАЛ ФАЙЛ КОНТРОЛЬНЫХ СУММ ПРО ЭТО ВЛОЖЕНИЕ. Три ответа, и путать их нельзя:
/// «сумма сошлась», «суммы для вложения в списке НЕТ» и «сумма не сошлась» — разные причины,
/// и человеку они говорят разное. Отсутствие суммы — ТОЖЕ ОТКАЗ (урок панели 1.x): пропустить
/// его значило бы объявить проверку выполненной там, где она не выполнялась вовсе.
/// </summary>
public enum SumCheck
{
    /// <summary>Сумма есть и совпала.</summary>
    Matches,

    /// <summary>Суммы для этого вложения в списке нет.</summary>
    Missing,

    /// <summary>Сумма есть, но не совпала: скачался не тот файл.</summary>
    Mismatch,
}

/// <summary>Итог сверки одной суммы: ответ, а рядом — что ожидалось и что получилось (для журнала).</summary>
public sealed record SumVerdict(SumCheck Check, string Expected, string Actual)
{
    public bool Ok => Check == SumCheck.Matches;
}

/// <summary>
/// КОНТРОЛЬНЫЕ СУММЫ ВЫПУСКА: разбор файла <c>SHA256SUMS.txt</c> и сверка с ним.
///
/// Сертификата у проекта нет, подписи нет, поэтому **хэш — единственное доступное доказательство**,
/// что скачался именно тот архив, а не обрезанный загрузкой или подменённый посредником файл.
/// Отсюда правило, взятое у панели 1.x (<c>dsh-tray\UpdateService.cs</c>, <c>ParseSums</c>
/// и <c>VerifyHash</c>): без суммы вложение не распаковывается.
///
/// Разбор терпим к тому, что файл правят руками: BOM в начале, комментарии, звёздочка перед
/// именем (обычай <c>sha256sum</c>), обратный слэш и путь перед именем, лишние пробелы
/// и вкладки. Нетерпим он ровно к одному: хэш обязан быть 64 шестнадцатеричных знака.
/// </summary>
public static class UpdateSums
{
    /// <summary>
    /// Разбор строк файла сумм: «хэш  имя файла». Ответ — имя → хэш (сравнение имён без учёта
    /// регистра, хэш в нижнем регистре). Отдельно от файла — чтобы это проверялось без диска
    /// и без сети.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string>? lines)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines is null) return sums;

        foreach (var raw in lines)
        {
            // BOM в начале файла (его легко получить руками) не должен ломать первый хэш.
            var line = (raw ?? string.Empty).TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var hash = parts[0].Trim().ToLowerInvariant();
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;

            // Имя может быть помечено звёздочкой («*DshPanel.zip») или содержать путь.
            var name = parts[^1].TrimStart('*').Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];

            if (name.Length > 0) sums[name] = hash;
        }

        return sums;
    }

    /// <summary>
    /// Сверка суммы одного вложения. Отсутствие суммы — <see cref="SumCheck.Missing"/>,
    /// а не «пропустим»: см. комментарий класса.
    /// </summary>
    public static SumVerdict Verify(IReadOnlyDictionary<string, string>? sums, string name, string actual)
    {
        ArgumentNullException.ThrowIfNull(sums);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var got = (actual ?? string.Empty).Trim().ToLowerInvariant();

        if (!sums.TryGetValue(name, out var expected))
            return new SumVerdict(SumCheck.Missing, string.Empty, got);

        return new SumVerdict(
            string.Equals(expected, got, StringComparison.OrdinalIgnoreCase) ? SumCheck.Matches : SumCheck.Mismatch,
            expected,
            got);
    }

    /// <summary>
    /// SHA-256 файла на диске, в нижнем регистре и без дефисов. Считается С ФАЙЛА, а не по дороге
    /// в сеть: сверять надо то, что действительно ляжет на место панели.
    /// </summary>
    public static string Sha256(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();

        return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
    }
}
