using System.Globalization;
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
/// ОТКУДА ПАНЕЛЬ БЕРЁТ ФАЙЛЫ ВЫПУСКА. Отдельный уговор по той же причине, что
/// <see cref="IUpdateClient"/>: проверки подставляют свой источник и в сеть не ходят ВОВСЕ,
/// а живая загрузка делается только там, где у прогона есть право.
///
/// Реализация обязана положить скачанное РОВНО в <c>path</c> и либо довести дело до конца,
/// либо бросить исключение (причину движок назовёт сам, техническим текстом).
/// Возвращать хэш отсюда не нужно намеренно: сумму движок считает С ФАЙЛА на диске —
/// сверяется то, что действительно ляжет на место панели, а не то, что о себе рассказал клиент.
/// </summary>
public interface IUpdateDownload
{
    void Save(string url, string path, Action<long, long>? progress);
}

/// <summary>
/// ФАЙЛЫ ВЫПУСКА ПО HTTP — одна дверь в сеть за скачиванием.
///
/// Устроено как <see cref="HttpUpdateClient"/>: живой путь отделён уговором, поэтому проверки
/// подставляют готовые байты и в сеть не ходят, а панель ходит ровно там, где имеет право.
///
/// **Три правила, и все три — не украшение.**
///
/// 1. **<c>User-Agent</c> обязателен.** Без него GitHub отвечает 403 на любой запрос, и панель
///    показала бы отказ вместо загрузки.
/// 2. **Потоковое чтение** (<c>ResponseHeadersRead</c>): архив бывает в сотни мегабайт, и держать
///    его целиком в памяти значит уронить панель на слабой машине.
/// 3. **Таймаут в минутах, а не в секундах.** Файл выпуска — не ответ API: двадцать секунд
///    отсекли бы загрузку на медленном канале.
///
/// ⚠️ Отказ HTTP называется технически («HTTP 404»), а не фразой для человека: фразу берёт
/// на себя строка по ключу отказа (<see cref="UpdateRefusals.Key"/>), и второго набора подписей
/// здесь быть не должно.
/// </summary>
public sealed class HttpUpdateDownload : IUpdateDownload
{
    private readonly HttpClient _http;

    public HttpUpdateDownload(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

    public void Save(string url, string path, Action<long, long>? progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);

        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        // Длина может быть не названа: тогда ход идёт без процентов, и это честнее нуля.
        var total = response.Content.Headers.ContentLength ?? -1;
        progress?.Invoke(0, total);

        using var source = response.Content.ReadAsStream();
        using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);

            done += read;
            progress?.Invoke(done, total);
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
