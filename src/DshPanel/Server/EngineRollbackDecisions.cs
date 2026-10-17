namespace DshPanel.Server;

/// <summary>Почему вернуться к прежней версии нельзя — ДО всякого действия с машиной.</summary>
public enum EngineRollbackRefusal
{
    /// <summary>Вернуться можно.</summary>
    None,

    /// <summary>Панель не знает, что ставила (записи нет или она битая) — возвращаться некуда.</summary>
    NoRecord,

    /// <summary>Прежней версии на диске нет: держать её было нечем или её убрали.</summary>
    NoPrevious,
}

/// <summary>Куда возвращаемся: с какой версии на какую — и можно ли вообще.</summary>
public sealed record EngineRollbackPlan(
    bool Any,
    string Current,
    string Target,
    EngineRollbackRefusal Refusal,
    string RefusalKey)
{
    /// <summary>Почему нельзя — ключом словаря; пусто, когда можно.</summary>
    public string Key => Any ? string.Empty : RefusalKey;
}

/// <summary>
/// ОТКАТ ПО КНОПКЕ — ЧИСТОЕ РЕШЕНИЕ (пункт 4 плана E3.3): **куда возвращаемся** и можно ли вообще.
///
/// **Откат — это не установка.** Панель держит **текущую и прежнюю** версию (решение владельца
/// 10.10.2026), и папка прежней версии остаётся на диске: возврат — это **подъём сервера из соседней
/// папки** и запись «стоит сейчас» про неё, а не новая установка. Поэтому в замке и не нужно второй
/// версии: возвращаться есть куда ровно потому, что прежнюю папку не убрали.
///
/// **Три правила выбора, и все три — из красных линий проекта:**
///
/// 1. **Только то, что ставила ПАНЕЛЬ** (`record.Known`): папка, заведённая человеком, чужой
///    установкой не считается и в откат не попадает никогда — панель про неё не знает.
/// 2. **Только то, что ЕСТЬ на диске**: запись могла пережить папку (её унесли руками) — тогда
///    честный ответ «возвращаться некуда», а не подъём в пустоту.
/// 3. **Самая новая из прежних**: если панель ставила несколько версий, откат идёт на ближайшую
///    прежнюю (старшая часть версии), а не на самую старую.
///
/// ⚠️ Здесь **нет ни одного действия**: решение только про версии. Папки приходят доводом
/// (их читает тот, у кого есть пути), поэтому решение проверяется без диска.
/// </summary>
public static class EngineRollbackDecisions
{
    /// <summary>КЛЮЧИ СЛОВ — здесь только имена, фразы живут в трёх словарях.</summary>
    public static class Words
    {
        public const string NoRecord = "EngineRollbackNoRecord";
        public const string NoPrevious = "EngineRollbackNoPrevious";
        public const string Button = "EngineRollbackButtonFormat";
        public const string AskTitle = "EngineRollbackAskTitle";
        public const string Ask = "EngineRollbackAskFormat";
        public const string AskYes = "EngineRollbackAskYes";
        public const string AskNo = "EngineRollbackAskNo";
        public const string TipYes = "TipEngineRollbackYes";
        public const string TipNo = "TipEngineRollbackNo";
        public const string Probe = "EngineRollbackProbeFormat";
        public const string Record = "EngineRollbackRecordFormat";
        public const string Done = "EngineRollbackDoneFormat";
        public const string ProbeFailed = "EngineRollbackProbeFailedFormat";
        public const string RecordFailed = "EngineRollbackRecordFailedFormat";
        public const string StartedAgain = "EngineRollbackStartedAgainFormat";
    }

    /// <summary>
    /// КУДА ВОЗВРАЩАЕМСЯ. <paramref name="folders"/> — папки версий, которые есть в папке движка
    /// (их читает вызывающий: у решения нет ни одного касания диска).
    /// </summary>
    public static EngineRollbackPlan Decide(EngineInstallRecord? record, IReadOnlyList<string>? folders)
    {
        if (record is null || !record.Any)
            return Refuse(EngineRollbackRefusal.NoRecord, Words.NoRecord);

        var current = record.EngineVersion.Trim();
        var present = folders ?? Array.Empty<string>();

        var candidates = record.Known
            .Select(version => (version ?? string.Empty).Trim())
            .Where(version => version.Length > 0)
            .Where(version => !string.Equals(version, current, StringComparison.OrdinalIgnoreCase))
            .Where(version => present.Any(folder => string.Equals(
                (folder ?? string.Empty).Trim(), version, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            return new EngineRollbackPlan(false, current, string.Empty, EngineRollbackRefusal.NoPrevious, Words.NoPrevious);

        // Самая НОВАЯ из прежних: старшая часть версии решает (то же правило, что у блока движка).
        var target = candidates
            .OrderByDescending(version => version, Comparer<string>.Create(EngineNode.Compare))
            .First();

        return new EngineRollbackPlan(true, current, target, EngineRollbackRefusal.None, string.Empty);
    }

    private static EngineRollbackPlan Refuse(EngineRollbackRefusal refusal, string key) =>
        new(false, string.Empty, string.Empty, refusal, key);
}
