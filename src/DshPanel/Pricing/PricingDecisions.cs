using System.Globalization;
using System.Text.Json;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Settings;

namespace DshPanel.Pricing;

/// <summary>
/// РЕШЕНИЯ О ЦЕНАХ: какой адрес страницы, какой знак валюты, пора ли разбирать заново и как
/// применяются окна пика со страницы.
///
/// Здесь нет ни сети, ни таймера, ни словаря: чистые функции от значений. Поэтому их можно
/// перебрать проверками (в том числе по всем сочетаниям аргументов), а «ходит ли панель в сеть»
/// решается отдельно и по праву.
///
/// **Два адреса — две страницы одного сайта, и это не дублирование.** Языковая версия страницы
/// цен — отдельный документ с отдельными ЧИСЛАМИ (английская отдаёт доллары, китайская — юани).
/// Поэтому валюта не пересчитывается по курсу (курса панель не знает и выдумывать его нельзя),
/// а БЕРЁТСЯ СО СТРАНИЦЫ НУЖНОГО ЯЗЫКА. Адреса перенесены из панели 1.x
/// (<c>dsh-tray\PricingUpdateService.cs</c>) как знание: там они проверены живыми запросами.
/// </summary>
public static class PricingDecisions
{
    /// <summary>Страница цен по-английски — та же, что названа источником у агента.</summary>
    public const string EnglishUrl = "https://api-docs.deepseek.com/quick_start/pricing";

    /// <summary>Страница цен по-китайски. Числа в ней ДРУГИЕ (юани), и это не перевод английских.</summary>
    public const string ChineseUrl = "https://api-docs.deepseek.com/zh-cn/quick_start/pricing";

    /// <summary>Язык, которому принадлежит китайская страница.</summary>
    public const string ChineseLanguage = "zh";

    /// <summary>
    /// За какое время панель не разбирает страницу цен второй раз. Решение владельца
    /// (правило для обновлений, 27.09.2026): при запуске панели, но **не чаще раза в сутки**.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Как часто идут часы разбора. Час — не «раз в сутки»: часы лишь СПРАШИВАЮТ, пора ли,
    /// а решение «пора» принимает <see cref="Due"/> по отметке последней проверки. Так панель,
    /// проработавшая несколько суток, разберёт страницу сама, а не только при запуске.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Адрес страницы цен для языка панели: китайскому — китайская, остальным — английская.
    ///
    /// ⚠️ Английская страница годится и для русского: третьей языковой версии у сайта нет,
    /// а английская — та, которую владелец назвал источником в профиле агента.
    /// </summary>
    public static string Url(string? language) =>
        IsChinese(language) ? ChineseUrl : EnglishUrl;

    /// <summary>
    /// Знак валюты для языка панели: <c>$</c> для ru/en, <c>¥</c> для zh.
    ///
    /// Знак выбирается ЗДЕСЬ, а не выковыривается из текста ячейки: страница нужного языка уже
    /// даёт числа в своей валюте, и знак обязан ей соответствовать. Пересчёта по курсу нет.
    /// </summary>
    public static string Currency(string? language) => IsChinese(language) ? "¥" : "$";

    /// <summary>Китайский ли язык панели (единственный, у которого своя страница цен).</summary>
    public static bool IsChinese(string? language) =>
        string.Equals((language ?? string.Empty).Trim(), ChineseLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Пора ли разбирать страницу цен. Решение владельца: при запуске панели, но НЕ чаще раза
    /// в сутки, и только там, где расписание вообще работает
    /// (<see cref="IsolationRules.ShouldRunScheduledWork"/>).
    ///
    /// Пустая или неразобранная отметка означает «никогда»: панель, поставленная впервые,
    /// обязана прочитать цены при первом же запуске, а не через сутки.
    /// </summary>
    public static bool Due(DateTimeOffset now, string? checkedAt, bool isolatedRun)
    {
        if (!IsolationRules.ShouldRunScheduledWork(isolatedRun)) return false;

        if (!TryParse(checkedAt, out var last)) return true;

        return now - last >= CheckInterval;
    }

    /// <summary>
    /// Отметка времени в том виде, в каком она живёт в файле настроек: круговая дата-время
    /// по инвариантной культуре. Пусто уходит пустым — «никогда» остаётся собой.
    /// </summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Читаемая отметка для окна: «28.09.2026, 14:32». Пусто — пусто.</summary>
    public static string CheckedText(string? stamp)
    {
        if (!TryParse(stamp, out var at)) return string.Empty;

        return at.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Разбор отметки; неразобранное (в том числе пустое) — «отметки нет».</summary>
    public static bool TryParse(string? stamp, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(
            stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);

    /// <summary>
    /// Отличаются ли окна со страницы от тех, по которым панель работает. Сравнение — по составу
    /// (дни, начало, конец), а не по порядку строк: страница вправе переставить окна местами.
    /// </summary>
    public static bool SameWindows(IReadOnlyList<PeakWindow> left, IReadOnlyList<PeakWindow> right)
    {
        if (left.Count != right.Count) return false;

        static string Key(PeakWindow window) =>
            string.Join(",", window.Days.OrderBy(day => day)) + "|" + window.FromMinutes + "|" + window.ToMinutes;

        var a = left.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToList();
        var b = right.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToList();

        return a.SequenceEqual(b, StringComparer.Ordinal);
    }
}

/// <summary>
/// ОКНА ПИКА, ПОЛУЧЕННЫЕ СО СТРАНИЦЫ ЦЕН, — отдельным уговором, потому что это ВТОРОЙ источник
/// расписания, и он обязан быть назван вслух.
///
/// **Почему окна со страницы вообще применяются.** Кнопка «Обновить информацию» — щелчок человека,
/// то есть то самое ПОДТВЕРЖДЕНИЕ, которого требует правило панели 1.x: *«Окна применяются только
/// по подтверждению, цены — показ»*. Автоматический ночной разбор окон НЕ применяет: он только
/// показывает цены. Так расписание не меняется молча — его меняет человек.
///
/// **Где они живут.** В настройках панели (<see cref="PanelSettings.PricingPeakWindows"/>), строкой
/// JSON: у настроек уже есть дверь, проверка границ и запись через одного владельца, а второго
/// хранилища рядом с ними панель не заводит.
///
/// ⚠️ Пустое значение означает «взять расписание профиля агента» — и это умолчание: панель,
/// которая ни разу не разбирала страницу, работает ровно как прежде.
/// </summary>
public static class PricingWindows
{
    /// <summary>
    /// Профиль агента с действующим расписанием: из настроек, если они его несут, иначе — свой.
    ///
    /// Единственная дверь к этому решению: и окно панели, и таблица настроек, и значок берут
    /// профиль ЗДЕСЬ, поэтому окна не могут разойтись между окнами.
    /// </summary>
    public static AgentProfile Apply(AgentProfile agent, PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(settings);

        var stored = Decode(settings.PricingPeakWindows);
        if (stored.Windows.Count == 0) return agent;

        var applied = agent with { PeakWindows = stored.Windows };

        // Дата проверки берётся у САМОГО НАБОРА окон, а не у отметки разбора цен: набор со страницы
        // проверен тогда, когда его оттуда взяли, и более поздний разбор цен (он идёт раз в сутки
        // и окон не берёт) не вправе удревнить или освятить эту дату. Отметки нет — остаётся дата
        // профиля: «проверено ничем» было бы хуже.
        return PricingDecisions.TryParse(stored.Checked, out var at)
            ? applied with { PricingChecked = at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : applied;
    }

    /// <summary>
    /// Окна в вид для файла настроек: сам набор и МОМЕНТ, когда он взят со страницы.
    /// Пустой список уходит пустой строкой — «как в профиле».
    /// </summary>
    public static string Encode(IReadOnlyList<PeakWindow> windows, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) return string.Empty;

        var payload = new StoredSetRecord(
            PricingDecisions.Stamp(at),
            windows.Select(window => new PeakWindowRecord(
                window.Days?.ToArray() ?? Array.Empty<int>(), window.FromMinutes, window.ToMinutes)).ToList());

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Окна из файла настроек. Мусор, пустое значение и битый JSON означают «как в профиле»,
    /// а не «расписание пропало»: файл правят руками, и панель обязана это пережить.
    /// </summary>
    public static StoredSet Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return StoredSet.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<StoredSetRecord>(json);
            if (parsed?.Windows is null || parsed.Windows.Count == 0) return StoredSet.Empty;

            var windows = new List<PeakWindow>();

            foreach (var record in parsed.Windows)
            {
                var days = (record.Days ?? Array.Empty<int>())
                    .Where(day => day is >= 0 and <= 6)
                    .Distinct()
                    .OrderBy(day => day)
                    .ToArray();

                if (days.Length == 0) continue;
                if (record.FromMinutes is < 0 or > 1440) continue;
                if (record.ToMinutes is < 0 or > 1440) continue;
                if (record.FromMinutes == record.ToMinutes) continue;

                windows.Add(new PeakWindow(days, record.FromMinutes, record.ToMinutes));
            }

            return windows.Count == 0 ? StoredSet.Empty : new StoredSet(parsed.Checked, windows);
        }
        catch (JsonException)
        {
            return StoredSet.Empty;
        }
    }

    /// <summary>
    /// Набор окон в сравнимом виде для файла настроек: мусор, битый JSON и набор без единого
    /// целого окна становятся ПУСТОЙ строкой, то есть «как в профиле агента».
    ///
    /// Дверь одна на всех: и запись из настроек, и окно, и контроллер проходят через неё —
    /// поэтому «в файле мусор, а в памяти окна» разойтись не может.
    /// </summary>
    public static string Normalize(string? json)
    {
        var stored = Decode(json);

        if (stored.Windows.Count == 0) return string.Empty;
        if (!PricingDecisions.TryParse(stored.Checked, out var at)) return string.Empty;

        return Encode(stored.Windows, at);
    }

    /// <summary>Набор окон со страницы: список и момент, когда он взят. Пустой набор — «как в профиле».</summary>
    public sealed record StoredSet(string? Checked, IReadOnlyList<PeakWindow> Windows)
    {
        public static StoredSet Empty { get; } = new(null, Array.Empty<PeakWindow>());
    }

    /// <summary>
    /// Расписание КАК ОНО ЛЕЖИТ В ФАЙЛЕ. Отдельная запись от <see cref="StoredSet"/>, а не та же
    /// самая: у записи в памяти дни лежат списком (<see cref="PeakWindow"/>), а разбирается из JSON
    /// массив — и попытка разобрать один тип другим молча дала бы пустой набор.
    /// </summary>
    private sealed record StoredSetRecord(string? Checked, List<PeakWindowRecord>? Windows);

    /// <summary>Расписание в файле: дни недели и границы окна в минутах от полуночи UTC.</summary>
    private sealed record PeakWindowRecord(int[]? Days, int FromMinutes, int ToMinutes);
}

/// <summary>
/// ПАМЯТЬ О ПРОЧИТАННОЙ ТАБЛИЦЕ ЦЕН — что панель видела в прошлый раз.
///
/// **Зачем это вообще, и почему это дефект, а не улучшение.** Разбор страницы закрыт суточным
/// гейтом (<see cref="PricingDecisions.Due"/>): разобрали сегодня — второй раз сегодня не пойдём,
/// так велит решение владельца. Но в настройках лежала только ОТМЕТКА ВРЕМЕНИ, а самих цен не было
/// нигде. Значит после перезапуска панели выходило так: гейт закрыт, значит разбора не будет;
/// память процесса пуста, значит и показывать нечего — и таблица «Стоимость» стояла пустой до тех
/// пор, пока человек не нажмёт «Обновить информацию». Окна пика при этом оставались на месте
/// (они лежат в настройках отдельной записью) — ровно как описал владелец 28.09.2026:
/// *«сами тарифы я заметил что не сохраняются. Я обновлял инфу вчера, но сегодня снова пусто,
/// только пики сохранены»*.
///
/// **Почему строкой JSON, а не полем на каждую цену.** У настроек уже есть дверь, проверка границ
/// и один владелец записи; заводить рядом с ними второе хранилище панель не станет. Тот же выбор
/// сделан для окон пика (<see cref="PricingWindows"/>) и по той же причине.
///
/// **Что здесь НЕ лежит.** Успешный разбор — только он: причина отказа и «не читали» остаются
/// в памяти процесса. Показать вчерашнюю причину отказа сегодня значило бы выдать старое за новое:
/// сеть могла починиться пять минут назад. Пустая строка означает «панель ещё не читала цены» —
/// и это честный ответ, а не «цена ноль».
/// </summary>
public static class PricingMemory
{
    /// <summary>
    /// Что запомнить: адрес, язык, модель, строки, окна и время разбора. Пустой набор строк
    /// запоминать нечего — показывать будет нечего, а место в файле он займёт.
    /// </summary>
    public static string Encode(PricingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is not { Ok: true } || result.Rows.Count == 0) return string.Empty;

        var payload = new MemoryRecord(
            PricingDecisions.Stamp(result.CheckedAt),
            result.SourceUrl,
            result.Language,
            result.Model,
            result.Rows.Select(row => new PriceRowRecord(row.Item, row.OffPeak, row.Peak)).ToList(),
            result.Windows
                .Select(window => new WindowRecord(
                    window.Days?.ToArray() ?? Array.Empty<int>(), window.FromMinutes, window.ToMinutes))
                .ToList());

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Что панель помнит. Мусор, битый JSON, неразобранная отметка времени и запись без единой
    /// строки — «не читала» (<c>null</c>), а не «цены пропали»: файл настроек правят руками,
    /// и панель обязана это пережить.
    ///
    /// Возвращается <c>null</c>, а не пустая запись: вызывающему нужно ОТЛИЧИТЬ «помним таблицу»
    /// от «не помним ничего» — на пустой записи окно нарисовало бы пустую таблицу с подписью
    /// «прочитано тогда-то».
    /// </summary>
    public static PricingResult? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<MemoryRecord>(json);

            if (parsed?.Rows is null || parsed.Rows.Count == 0) return null;
            if (!PricingDecisions.TryParse(parsed.Checked, out var at)) return null;

            var rows = parsed.Rows
                .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Item))
                .Select(row => new PriceRow(row!.Item!, row.OffPeak ?? string.Empty, row.Peak ?? string.Empty))
                .ToList();

            if (rows.Count == 0) return null;

            return new PricingResult(
                true,
                string.Empty,
                parsed.SourceUrl ?? string.Empty,
                parsed.Language ?? string.Empty,
                parsed.Model ?? string.Empty,
                rows,
                Windows(parsed.Windows),
                at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Память в вид для файла настроек: мусор и нелепые строки становятся ПУСТОЙ строкой, то есть
    /// «панель ещё не читала цены». Дверь одна на всех — и запись, и чтение, и проверка границ
    /// значения проходят здесь, поэтому «в файле мусор, а в памяти окна таблица» разойтись не может.
    /// </summary>
    public static string Normalize(string? json) => Encode(Decode(json) ?? PricingResult.NotRequested(string.Empty));

    /// <summary>
    /// Окна из памяти — с теми же границами, что у <see cref="PricingWindows.Decode"/>: день 0–6,
    /// границы в пределах суток, пустое окно отбрасывается. Это ЦЕНЫ, а не расписание: окна тут
    /// лежат лишь затем, чтобы со страницы помнилось всё прочитанное целиком.
    /// </summary>
    private static List<PeakWindow> Windows(List<WindowRecord>? records)
    {
        var windows = new List<PeakWindow>();

        foreach (var record in records ?? new List<WindowRecord>())
        {
            var days = (record.Days ?? Array.Empty<int>())
                .Where(day => day is >= 0 and <= 6)
                .Distinct()
                .OrderBy(day => day)
                .ToArray();

            if (days.Length == 0) continue;
            if (record.FromMinutes is < 0 or > 1440) continue;
            if (record.ToMinutes is < 0 or > 1440) continue;
            if (record.FromMinutes == record.ToMinutes) continue;

            windows.Add(new PeakWindow(days, record.FromMinutes, record.ToMinutes));
        }

        return windows;
    }

    /// <summary>Память КАК ОНА ЛЕЖИТ В ФАЙЛЕ. Отдельная запись от <see cref="PricingResult"/>:
    /// у результата в памяти строки — готовая запись, а из JSON разбирается массив.</summary>
    private sealed record MemoryRecord(
        string? Checked,
        string? SourceUrl,
        string? Language,
        string? Model,
        List<PriceRowRecord>? Rows,
        List<WindowRecord>? Windows);

    /// <summary>Строка цены в файле: единица и две цены — ровно то, что видел человек.</summary>
    private sealed record PriceRowRecord(string? Item, string? OffPeak, string? Peak);

    /// <summary>Окно пика в памяти цен — тот же вид, что в <see cref="PricingWindows"/>.</summary>
    private sealed record WindowRecord(int[]? Days, int FromMinutes, int ToMinutes);
}
