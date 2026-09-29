namespace DshPanel.Autostart;

/// <summary>Куда ведёт запись автозапуска.</summary>
public enum AutostartWhere
{
    /// <summary>Записи нет — автозапуск выключен.</summary>
    None,

    /// <summary>Запись ведёт на эту копию панели.</summary>
    Self,

    /// <summary>Запись есть, но файла, на который она ведёт, на машине нет.</summary>
    Missing,

    /// <summary>Запись ведёт на другую живую копию панели 2.0.</summary>
    Other,

    /// <summary>Реестр не прочитался — состояние неизвестно, и трогать ничего нельзя.</summary>
    Unknown,
}

/// <summary>Что записано и куда это ведёт. Путь пуст, если записи нет.</summary>
public readonly record struct AutostartState(AutostartWhere Where, string Path, string Raw)
{
    /// <summary>Галочка в окне: отвечает на вопрос «запись есть?», а не «ведёт ли она на нас».</summary>
    public bool Enabled => Where is AutostartWhere.Self or AutostartWhere.Missing or AutostartWhere.Other;

    /// <summary>Состояние известно. При <see cref="AutostartWhere.Unknown"/> панель не решает ничего.</summary>
    public bool Known => Where != AutostartWhere.Unknown;

    public static AutostartState Off => new(AutostartWhere.None, "", "");

    public static AutostartState UnknownState => new(AutostartWhere.Unknown, "", "");
}

/// <summary>
/// Решения автозапуска — ЧИСТЫЕ функции.
///
/// Здесь нет ни одного обращения к реестру, диску и окружению: всё, что требует ввода-вывода,
/// приходит аргументом (запись из хранилища, путь к себе, ответ «файл есть?»). Поэтому решения
/// перебираются тестами по всем сочетаниям, а не проверяются глазами один раз.
///
/// Что перенесено из v1 (<c>dsh-tray\Autostart.cs</c>) как урок, а не как код:
///
/// * **запись хранит абсолютный путь, поэтому её обязательно сверять с собой при старте** —
///   папку переносят, копий на машине бывает несколько, и панель, которая этого не делает,
///   после входа в Windows поднимает ЧУЖУЮ копию, показывая «автозапуск включён»;
/// * **живую соседнюю копию молча не отбираем** — это законная ситуация, и решение о ней
///   принимает человек (галочкой в окне), а не панель при старте;
/// * **«реестр не читается» — это не «автозапуск выключен»**: при неизвестном состоянии
///   не правится ничего.
///
/// Чего здесь НЕТ и почему: сравнения версий («наша копия старее чужой»). В v1 оно опиралось
/// на сервис обновлений, которого в 2.0 ещё нет; пока запись, ведущая на живой чужой файл,
/// остаётся <see cref="AutostartWhere.Other"/> — панель её не трогает и говорит об этом в окне.
/// </summary>
public static class AutostartDecisions
{
    /// <summary>
    /// Имя записи 2.0 в <c>HKCU\...\Run</c>.
    ///
    /// Взято ОТЛИЧНЫМ от имени v1 (<c>DSHPanel</c>): панель 2.0 встаёт рядом, а не поверх,
    /// и запись v1 — это живая установка владельца. Запись под чужим именем панель 2.0
    /// не читает, не переводит на себя и не удаляет НИКОГДА (в v1 перенос прежнего имени
    /// на себя был правильным — там это была своя прежняя генерация; здесь это была бы
    /// кража автозапуска у работающей панели).
    /// </summary>
    public const string ValueName = "DSHPanel2";

    /// <summary>Настоящая ветка автозапуска Windows.</summary>
    public const string OwnerRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>
    /// Своя ветка для пробы. Проба обязана упражнять настоящий код работы с реестром, но не имеет
    /// права касаться настоящего <c>Run</c>: она пишет сюда и убирает за собой.
    /// </summary>
    public const string SelfTestKeyPath = OwnKeyRoot + @"\AutostartSelfTest";

    /// <summary>
    /// Ветка панели 2.0 в <c>HKCU</c>. Заводится ТОЛЬКО ради пробы и убирается вместе с ней:
    /// после <c>--autostart-selftest</c> в реестре владельца не должно остаться ни одной
    /// нашей ветки. Удаление листа оставляло бы пустую родительскую ветку — а это мусор
    /// в чужом реестре, пусть и невидимый (нашла проба на exe 24.09.2026).
    /// </summary>
    public const string OwnKeyRoot = @"Software\DshPanel2";

    /// <summary>
    /// Это ветка настоящего автозапуска владельца? Сравнение без учёта регистра и ведущего
    /// <c>HKCU\</c>: ветку пишут и полным путём, и коротким. Нужно пробе (её ветка настоящей
    /// быть не должна) и читателю кода.
    /// </summary>
    public static bool IsOwnerRunKey(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return false;

        var normalized = keyPath.Trim().TrimStart('\\')
            .Replace("HKEY_CURRENT_USER\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace("HKCU\\", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\');

        return string.Equals(normalized, OwnerRunKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Значение записи: путь к себе В КАВЫЧКАХ.
    ///
    /// Кавычки не украшение: панель ставят и в путь с пробелом (в v1 это был
    /// <c>%LOCALAPPDATA%\Programs\DSH Panel</c>), а без кавычек Windows разберёт командную строку
    /// по пробелу и попытается запустить несуществующий файл. Хвостовой разделитель срезаем —
    /// в v1 он ломал уже разбор путей при обновлении.
    ///
    /// Ключей запуска нет намеренно: обычный запуск 2.0 и так ставит только значок в трей
    /// и окна не показывает, поэтому «скрытого» режима, как <c>--tray</c> в v1, здесь не нужно.
    /// </summary>
    public static string BuildValue(string selfPath) =>
        "\"" + Path.TrimEndingDirectorySeparator((selfPath ?? string.Empty).Trim()) + "\"";

    /// <summary>
    /// Путь из значения записи: <c>"C:\...\DshPanel.exe"</c> или без кавычек.
    /// Пустая строка — разобрать нечего.
    /// </summary>
    public static string EntryPath(string? rawValue)
    {
        var text = (rawValue ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : string.Empty;
        }

        // Без кавычек путь кончается на .exe, дальше могут идти ключи.
        var marker = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? text[..(marker + 4)] : text;
    }

    /// <summary>
    /// Приводит путь к сравнимому виду: полный путь без хвостового разделителя.
    /// Без этого один и тот же файл, записанный по-разному, выглядел бы «другой копией».
    /// </summary>
    public static string Normalize(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
        catch { return text.TrimEnd('\\', '/'); }
    }

    public static bool IsSelf(string entryPath, string selfPath) =>
        Normalize(entryPath).Length > 0
        && string.Equals(Normalize(entryPath), Normalize(selfPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Сетевой путь из реестра не щупаем: проверка файла на нём может залипнуть на секунды.</summary>
    public static bool IsNetworkPath(string? path) =>
        path is not null
        && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>
    /// Прочитать запись и сказать, куда она ведёт. <paramref name="fileExists"/> приходит
    /// аргументом, чтобы решение оставалось чистым и проверяемым.
    /// </summary>
    public static AutostartState Classify(
        AutostartRecord record, string selfPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        // Не прочиталось — это НЕ «записи нет». Разница решает всё: при неизвестном состоянии
        // мы не правим ничего.
        if (!record.Readable) return AutostartState.UnknownState;
        if (!record.Exists) return AutostartState.Off;

        var raw = record.Value!;
        var path = EntryPath(raw);
        if (path.Length == 0) return new AutostartState(AutostartWhere.Missing, string.Empty, raw);

        if (IsNetworkPath(path)) return new AutostartState(AutostartWhere.Other, path, raw);
        if (!fileExists(path)) return new AutostartState(AutostartWhere.Missing, path, raw);
        if (IsSelf(path, selfPath)) return new AutostartState(AutostartWhere.Self, path, raw);

        return new AutostartState(AutostartWhere.Other, path, raw);
    }

    /// <summary>
    /// Переписывать ли запись при старте. Случаев ровно два, и оба — про свою же запись:
    /// она ведёт на исчезнувший файл; либо ведёт на нас, но записана не нашим значением
    /// (без кавычек, с хвостовым разделителем, с чужими ключами). Живая чужая копия
    /// (<see cref="AutostartWhere.Other"/>) не трогается никогда.
    /// </summary>
    public static bool ShouldRewrite(AutostartState state, string selfPath) => state.Where switch
    {
        AutostartWhere.Missing => true,
        AutostartWhere.Self => !SameValue(state.Raw, BuildValue(selfPath)),
        _ => false,
    };

    /// <summary>Значения записей совпадают дословно (регистр и лишние пробелы не в счёт).</summary>
    public static bool SameValue(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
