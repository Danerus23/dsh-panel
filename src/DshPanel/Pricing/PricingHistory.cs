using System.Text;
using System.Text.Json;
using DshPanel.Agents;
using DshPanel.Isolation;

namespace DshPanel.Pricing;

/// <summary>
/// СНИМОК ТАБЛИЦЫ ЦЕН И ОКОН ПИКА — то, что панель увидела на странице в один момент.
///
/// Зачем отдельно от <see cref="PricingResult"/>. У результата разбора есть и отказ, и «ещё
/// не читали»; снимок — только УДАВШИЙСЯ разбор, и это не мелочь: сравнивать «вчерашнюю таблицу»
/// с «сегодняшней пустотой» нельзя, иначе каждый отказ сети выглядел бы как изменение тарифа.
/// </summary>
public sealed record PricingSnapshot(
    DateTimeOffset At,
    string SourceUrl,
    string Language,
    string Model,
    IReadOnlyList<PriceRow> Rows,
    IReadOnlyList<PeakWindow> Windows)
{
    /// <summary>
    /// Снимок из удавшегося разбора. Неудача, «не читали» и пустая таблица дают <c>null</c>:
    /// истории у них нет, а место в файле они бы заняли.
    /// </summary>
    public static PricingSnapshot? From(PricingResult? result) =>
        result is { Ok: true } && result.Rows.Count > 0
            ? new PricingSnapshot(
                result.CheckedAt, result.SourceUrl, result.Language, result.Model, result.Rows, result.Windows)
            : null;
}

/// <summary>
/// ИЗМЕНЕНИЕ ОДНОЙ СТРОКИ ЦЕНЫ: было/стало по вне-пика и по пику.
///
/// Пустая строка означает «этой цены на странице не было» — так выглядит и появившаяся строка,
/// и пропавшая. Готового текста здесь нет: его собирает панель на своём языке при показе
/// (<see cref="PricingHistoryText"/>), иначе смена языка оставила бы историю на прежнем.
/// </summary>
public sealed record PriceChangeRow(
    string Item,
    string OffPeakBefore,
    string OffPeakAfter,
    string PeakBefore,
    string PeakAfter)
{
    /// <summary>Строка пропала со страницы: «стало» пусто с обеих сторон.</summary>
    public bool Removed => OffPeakAfter.Length == 0 && PeakAfter.Length == 0;
}

/// <summary>
/// ЗАПИСЬ ОБ ИЗМЕНЕНИИ — точка изменения, а не каждая проверка.
///
/// В записи лежит СТРУКТУРА изменения (модель, строки цены с «было/стало», окна с «было/стало»),
/// адрес источника и язык. Ни одного готового слова: запись переживает смену языка панели,
/// а текст собирается при показе.
///
/// ⚠️ Окна пика здесь — ТО, ЧТО НАПИСАНО НА СТРАНИЦЕ, и только. Расписание панели они не меняют:
/// оно меняется по подтверждению человека (<c>pricingPeakWindows</c>), и шарик об этом говорит
/// словами «на странице цены окна пика такие-то».
/// </summary>
public sealed record PricingChange(
    DateTimeOffset At,
    string SourceUrl,
    string Language,
    string Model,
    IReadOnlyList<PriceChangeRow> Prices,
    IReadOnlyList<PeakWindow> WindowsBefore,
    IReadOnlyList<PeakWindow> WindowsAfter)
{
    /// <summary>Есть ли что показывать: изменилась цена или окна. Пустая запись не заводится.</summary>
    public bool HasChanges => PricesChanged || WindowsChanged;

    public bool PricesChanged => Prices.Count > 0;

    public bool WindowsChanged =>
        !PricingDecisions.SameWindows(WindowsBefore, WindowsAfter);
}

/// <summary>
/// ИСТОРИЯ ЦЕН: молчаливая базовая точка и записи об изменениях.
///
/// **Точки изменения, а не проверки.** Проверили — не изменилось: новой записи НЕТ, прежняя
/// остаётся текущей. Поэтому файл не растёт от каждого запуска панели, а человек видит ровно
/// те дни, когда тариф на странице поменялся (уточнение владельца 28.09.2026).
///
/// **Сравнение всегда с ПРЕДЫДУЩЕЙ ЗАПИСЬЮ**, а не с прошлой проверкой. Состояние предыдущей
/// записи получается свёрткой: якорь плюс все изменения по порядку (<see cref="PricingHistoryStore.Latest"/>).
/// Так в файле лежат только изменения, и при этом ни одна цена не теряется: цена, вернувшаяся
/// к прежнему значению, снова станет изменением.
///
/// **Предел — 30 изменений.** Самая старая запись при переполнении уходит, и счётчик
/// <see cref="Dropped"/> говорит об этом словами в окне, а не молча.
/// </summary>
public sealed record PricingHistory(
    PricingSnapshot? Anchor,
    IReadOnlyList<PricingChange> Changes,
    int Dropped)
{
    /// <summary>Сколько записей об изменении панель хранит. Больше — место на диске и длина списка.</summary>
    public const int MaxChanges = 30;

    /// <summary>Истории нет: панель ещё ни разу не читала страницу.</summary>
    public static PricingHistory Empty { get; } = new(null, Array.Empty<PricingChange>(), 0);

    public bool HasChanges => Changes.Count > 0;

    /// <summary>Число изменений, ушедших за предел: по нему окно говорит «самая старая ушла».</summary>
    public bool WasTrimmed => Dropped > 0;

    /// <summary>
    /// Подписана ли запись «текущие». Считается от ПОРЯДКА, а не хранится полем: запись, у которой
    /// подпись лежала бы в файле, пришлось бы переписывать при каждом новом изменении, а забытая
    /// переписка оставила бы две «текущие» записи сразу.
    /// </summary>
    public bool IsCurrent(int index) => index >= 0 && index == Changes.Count - 1;

    /// <summary>
    /// СКОЛЬКО СТРОК В ОКНЕ «ИСТОРИЯ ЦЕН»: первая цена (если панель её уже получала) плюс точки
    /// изменения.
    ///
    /// Решение владельца 29.09.2026 (п. 39 <c>docs\DESIGN.md</c>): список НИКОГДА не пуст, как только
    /// панель хоть раз прочитала цены. Иначе человек, у которого тариф не менялся, открывал пустое
    /// окно и решал, что панель цены потеряла, — его слова: *«Я думал она накапливать и хранит
    /// данные где-то отдельно и данные она не теряет»*.
    /// </summary>
    public int Entries => (Anchor is null ? 0 : 1) + Changes.Count;

    /// <summary>
    /// ПЕРВАЯ СТРОКА СПИСКА — ПЕРВАЯ ЦЕНА (когда панель её уже получала).
    ///
    /// «Первая цена», а не «якорь» и не «базовая точка»: те слова — внутренний язык разбора,
    /// и человеку их показывать нельзя (п. 39).
    /// </summary>
    public bool IsFirstPrice(int entry) => Anchor is not null && entry == 0;

    /// <summary>
    /// НОМЕР ТОЧКИ ИЗМЕНЕНИЯ по номеру строки списка: первая цена сдвигает все записи на одну.
    /// Одно место на это смещение — второй его расчёт однажды разошёлся бы с первым, и «текущие»
    /// уехали бы не на ту строку.
    /// </summary>
    public int ChangeIndex(int entry) => entry - (Anchor is null ? 0 : 1);

    /// <summary>Точка изменения по номеру строки списка (<c>null</c> — там первая цена или пусто).</summary>
    public PricingChange? ChangeAt(int entry)
    {
        var position = ChangeIndex(entry);

        return position >= 0 && position < Changes.Count ? Changes[position] : null;
    }

    /// <summary>
    /// Подписана ли СТРОКА СПИСКА «текущие»: ею всегда оказывается ПОСЛЕДНЯЯ точка изменения.
    ///
    /// ⚠️ Когда изменений нет вовсе, текущее состояние — это и есть первая цена, и она говорит
    /// об этом СЛОВАМИ в своём пояснении («с тех пор цены не менялись»), а не второй подписью
    /// в той же строке: две подписи на одной строке спорили бы между собой (решение владельца
    /// 29.09.2026).
    /// </summary>
    public bool IsCurrentEntry(int entry) => Changes.Count > 0 && ChangeIndex(entry) == Changes.Count - 1;
}

/// <summary>
/// ИСТОРИЯ ЦЕН НА ДИСКЕ: свёртка состояния, запись нового изменения и терпимое чтение.
///
/// Здесь одни решения — ни сети, ни окон, ни таймера. Файл читает и пишет
/// <see cref="PricingHistoryFiles"/> по путям из <see cref="AppPaths"/>; проверки гоняют решения
/// на значениях, а «ходит ли панель в сеть» решается отдельно и по праву.
/// </summary>
public static class PricingHistoryStore
{
    /// <summary>
    /// ПРОЧИТАЛИ СТРАНИЦУ — ЧТО ЗАПИСАТЬ.
    ///
    /// Три случая, и каждый назван своим поведением:
    ///
    /// * **истории нет вовсе** — пишется ТОЛЬКО базовая точка: без неё первое изменение не с чем
    ///   сравнить. Молча: ни записи об изменении, ни шарика;
    /// * **сравнение пустое** — возвращается ТА ЖЕ история (та же ссылка), и вызывающий по ней
    ///   видит, что трогать файл незачем: прежняя запись остаётся текущей;
    /// * **сравнение непустое** — добавляется запись, при переполнении уходит самая старая.
    /// </summary>
    public static PricingHistory Check(PricingHistory history, PricingSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(current);

        if (history.Anchor is null && history.Changes.Count == 0)
        {
            return history with { Anchor = current };
        }

        var change = PricingChangeDecisions.Between(Latest(history), current);
        if (change is null) return history;

        var changes = history.Changes.ToList();
        changes.Add(change);

        var dropped = history.Dropped;

        while (changes.Count > PricingHistory.MaxChanges)
        {
            changes.RemoveAt(0);
            dropped++;
        }

        return history with { Changes = changes, Dropped = dropped };
    }

    /// <summary>
    /// СОСТОЯНИЕ ПОСЛЕДНЕЙ ЗАПИСИ — якорь плюс все изменения по порядку. Это и есть то, с чем
    /// сравнивается новая проверка: «с предыдущей записью», как сказал владелец.
    ///
    /// Пустая история даёт <c>null</c>: сравнивать не с чем — и это честный ответ, а не пустая
    /// таблица (на ней первая же проверка объявила бы изменением всё подряд).
    /// </summary>
    public static PricingSnapshot? Latest(PricingHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var state = history.Anchor;

        foreach (var change in history.Changes) state = Apply(state, change);

        return state;
    }

    public static string Encode(PricingHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var payload = new FileRecord(
            history.Anchor is null ? null : Snapshot(history.Anchor),
            history.Changes.Select(Change).ToList(),
            history.Dropped);

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// ИСТОРИЯ ИЗ ФАЙЛА. Битый, чужой и пустой JSON — «истории нет», а не падение панели: файл
    /// состояния правят руками, и пережить это панель обязана. Тот же приём, что у памяти цен
    /// (<see cref="PricingMemory.Decode"/>) и у расписания окон (<see cref="PricingWindows.Decode"/>).
    /// </summary>
    public static PricingHistory Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return PricingHistory.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<FileRecord>(json);
            if (parsed is null) return PricingHistory.Empty;

            var anchor = Snapshot(parsed.Anchor);

            var changes = new List<PricingChange>();

            foreach (var record in parsed.Changes ?? new List<ChangeRecord>())
            {
                var change = Change(record);
                if (change is not null) changes.Add(change);
            }

            var dropped = Math.Max(0, parsed.Dropped);

            // Правленый руками файл может нести больше записей, чем предел: лишние уходят
            // тем же правилом, что при записи, и это тоже сказано счётчиком.
            while (changes.Count > PricingHistory.MaxChanges)
            {
                changes.RemoveAt(0);
                dropped++;
            }

            if (anchor is null && changes.Count == 0) return PricingHistory.Empty;

            return new PricingHistory(anchor, changes, dropped);
        }
        catch (JsonException)
        {
            return PricingHistory.Empty;
        }
        catch (NotSupportedException)
        {
            return PricingHistory.Empty;
        }
    }

    /// <summary>История в вид для файла: мусор и пустое становятся «истории нет» — дверью, а не как получится.</summary>
    public static string Normalize(string? json) => Encode(Decode(json));

    /// <summary>Наложить одно изменение на состояние: строки по имени, окна и подпись — из записи.</summary>
    private static PricingSnapshot? Apply(PricingSnapshot? state, PricingChange change)
    {
        var rows = new List<PriceRow>(state?.Rows ?? Array.Empty<PriceRow>());

        foreach (var row in change.Prices)
        {
            var index = rows.FindIndex(candidate => string.Equals(candidate.Item, row.Item, StringComparison.Ordinal));

            if (row.Removed)
            {
                if (index >= 0) rows.RemoveAt(index);
                continue;
            }

            var updated = new PriceRow(row.Item, row.OffPeakAfter, row.PeakAfter);

            if (index >= 0) rows[index] = updated;
            else rows.Add(updated);
        }

        if (rows.Count == 0 && state is null) return null;

        return new PricingSnapshot(
            change.At, change.SourceUrl, change.Language, change.Model, rows, change.WindowsAfter);
    }

    private static SnapshotRecord Snapshot(PricingSnapshot snapshot) => new(
        PricingDecisions.Stamp(snapshot.At),
        snapshot.SourceUrl,
        snapshot.Language,
        snapshot.Model,
        snapshot.Rows.Select(row => new RowRecord(row.Item, row.OffPeak, row.Peak)).ToList(),
        snapshot.Windows
            .Select(window => new WindowRecord(window.Days?.ToArray() ?? Array.Empty<int>(), window.FromMinutes, window.ToMinutes))
            .ToList());

    private static PricingSnapshot? Snapshot(SnapshotRecord? record)
    {
        if (record is null) return null;
        if (!PricingDecisions.TryParse(record.At, out var at)) return null;
        if (record.Rows is null || record.Rows.Count == 0) return null;

        var rows = record.Rows
            .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Item))
            .Select(row => new PriceRow(row!.Item!, row.OffPeak ?? string.Empty, row.Peak ?? string.Empty))
            .ToList();

        if (rows.Count == 0) return null;

        return new PricingSnapshot(
            at,
            record.SourceUrl ?? string.Empty,
            record.Language ?? string.Empty,
            record.Model ?? string.Empty,
            rows,
            Windows(record.Windows));
    }

    private static ChangeRecord Change(PricingChange change) => new(
        PricingDecisions.Stamp(change.At),
        change.SourceUrl,
        change.Language,
        change.Model,
        change.Prices
            .Select(row => new RowChangeRecord(
                row.Item, row.OffPeakBefore, row.OffPeakAfter, row.PeakBefore, row.PeakAfter))
            .ToList(),
        change.WindowsBefore
            .Select(window => new WindowRecord(window.Days?.ToArray() ?? Array.Empty<int>(), window.FromMinutes, window.ToMinutes))
            .ToList(),
        change.WindowsAfter
            .Select(window => new WindowRecord(window.Days?.ToArray() ?? Array.Empty<int>(), window.FromMinutes, window.ToMinutes))
            .ToList());

    private static PricingChange? Change(ChangeRecord? record)
    {
        if (record is null) return null;
        if (!PricingDecisions.TryParse(record.At, out var at)) return null;

        var prices = (record.Prices ?? new List<RowChangeRecord>())
            .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Item))
            .Select(row => new PriceChangeRow(
                row!.Item!,
                row.OffPeakBefore ?? string.Empty,
                row.OffPeakAfter ?? string.Empty,
                row.PeakBefore ?? string.Empty,
                row.PeakAfter ?? string.Empty))
            .ToList();

        var before = Windows(record.WindowsBefore);
        var after = Windows(record.WindowsAfter);

        // Запись без единого изменения — не запись: она бы сказала «изменилось ничего».
        if (prices.Count == 0 && PricingDecisions.SameWindows(before, after)) return null;

        return new PricingChange(
            at,
            record.SourceUrl ?? string.Empty,
            record.Language ?? string.Empty,
            record.Model ?? string.Empty,
            prices,
            before,
            after);
    }

    /// <summary>
    /// Окна из файла — с теми же границами, что у расписания и памяти цен: день 0–6, границы
    /// в пределах суток, пустое окно отбрасывается.
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
            if (record.From is < 0 or > 1440) continue;
            if (record.To is < 0 or > 1440) continue;
            if (record.From == record.To) continue;

            windows.Add(new PeakWindow(days, record.From, record.To));
        }

        return windows;
    }

    /// <summary>История КАК ОНА ЛЕЖИТ В ФАЙЛЕ. Отдельные записи от записей в памяти — тот же приём
    /// и та же причина, что у памяти цен: у одной стороны списки, у другой массивы.</summary>
    private sealed record FileRecord(SnapshotRecord? Anchor, List<ChangeRecord>? Changes, int Dropped);

    private sealed record SnapshotRecord(
        string? At,
        string? SourceUrl,
        string? Language,
        string? Model,
        List<RowRecord>? Rows,
        List<WindowRecord>? Windows);

    private sealed record ChangeRecord(
        string? At,
        string? SourceUrl,
        string? Language,
        string? Model,
        List<RowChangeRecord>? Prices,
        List<WindowRecord>? WindowsBefore,
        List<WindowRecord>? WindowsAfter);

    private sealed record RowRecord(string? Item, string? OffPeak, string? Peak);

    private sealed record RowChangeRecord(
        string? Item,
        string? OffPeakBefore,
        string? OffPeakAfter,
        string? PeakBefore,
        string? PeakAfter);

    private sealed record WindowRecord(int[]? Days, int From, int To);
}

/// <summary>
/// ДВЕРЬ К ФАЙЛУ ИСТОРИИ: <c>state\pricing-history.json</c> под корнем панели.
///
/// **Почему делегатами, а не путём.** Путь берётся у <see cref="AppPaths"/> — второго места, где
/// он вычисляется, в панели нет. Прогон без права на данные панели получает
/// <see cref="None"/>: он не читает историю владельца и не пишет своей — ровно как с файлами
/// состояния сервера (<c>Server\ServerStateFiles</c>, тот же приём и та же причина).
///
/// <see cref="Persists"/> — «есть ли куда писать»: без него панель говорила бы в журнал
/// «история сохранена», не сохранив ничего.
/// </summary>
public sealed record PricingHistoryFiles(Func<string> Read, Action<string> Write)
{
    /// <summary>Права писать нет — прогон проверки. Пусто и молча.</summary>
    public static PricingHistoryFiles None { get; } = new(
        Read: () => string.Empty,
        Write: _ => { })
    {
        Persists = false,
    };

    /// <summary>Есть ли куда писать: <c>false</c> — прогон без права, и «сохранил» говорить нельзя.</summary>
    public bool Persists { get; init; } = true;

    /// <summary>
    /// Файл ПОД ЭТИМ КОРНЕМ — то, что получает прогон с правом на данные панели: обычный запуск
    /// человеком и изолированный прогон. Чтение и запись — без BOM и с созданием каталога:
    /// тот же способ, каким пишутся остальные файлы состояния.
    /// </summary>
    public static PricingHistoryFiles Under(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new PricingHistoryFiles(
            Read: () => ReadFile(paths.PricingHistoryFile),
            Write: text => WriteFile(paths.PricingHistoryFile, text));
    }

    private static string ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            // Файл занят или недоступен — для вызывающего это «истории нет».
            return string.Empty;
        }
    }

    private static void WriteFile(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
