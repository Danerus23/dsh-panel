using System;

namespace DshPanel.Server;

/// <summary>Чем грозит смена версии движка журналу сессий — три исхода, и все три надо уметь сказать.</summary>
public enum EngineJournalRisk
{
    /// <summary>Прятать нечего: ни одна сессия не станет невидимой.</summary>
    NothingHidden,

    /// <summary>Есть сессии, которые станут невидимыми — их число названо.</summary>
    Hidden,

    /// <summary>
    /// УЗНАТЬ НЕ УДАЛОСЬ — и это отдельный исход, а не ноль. Замер `l2`: молчащий откат и есть
    /// та беда, ради которой предохранитель заводится; «не знаю» обязано звучать, а не выглядеть
    /// как «сессий нет».
    /// </summary>
    Unknown,
}

/// <summary>
/// ПРЕДУПРЕЖДЕНИЕ О ЖУРНАЛЕ: чем грозит, сколько сессий попадёт и каким ключом словаря это сказать.
/// Слова живут в трёх словарях, здесь — только числа и имя ключа (как у <see cref="EngineUpdateFlow"/>).
/// </summary>
public sealed record EngineJournalWarning(
    EngineJournalRisk Risk,
    int AtRisk,
    int Total,
    string Key,
    string Version,
    string Detail)
{
    /// <summary>Говорить нечего (нечего сказать — так и надо: пустая строка, а не «0 сессий»).</summary>
    public static EngineJournalWarning Silent { get; } =
        new(EngineJournalRisk.NothingHidden, 0, 0, string.Empty, string.Empty, string.Empty);

    /// <summary>Есть что сказать человеку до дела или после него.</summary>
    public bool SaysSomething => Key.Length > 0;

    /// <summary>Кого-то спрячем — по этому признаку путь и обязан сказать словами.</summary>
    public bool Hides => Risk == EngineJournalRisk.Hidden && AtRisk > 0;
}

/// <summary>
/// ПРЕДОХРАНИТЕЛЬ ОТКАТА ЖУРНАЛА (блок C плана 2.2.0): «предупредить + не откатывать молча».
///
/// **От какого случая — замер `l2` на копии профиля владельца (197 сессий, 18.10.2026):**
/// * журнал пишется двумя форматами: `session.v3.jsonl.zstd` (0.1.x) и `session.v4.jsonl.zstd` (0.2.x);
/// * миграция **ленивая** — сессия переезжает в v4 в момент ОТКРЫТИЯ под 0.2.x, а старый файл
///   остаётся рядом нетронутым (SHA-256 не менялся);
/// * **вперёд движок говорит**: `session/list` у 0.2.1 отдаёт `formatStatus=migration-required`;
/// * **назад — МОЛЧИТ**: после одного открытия 0.1.5 показал 197 → **196** без поля, без слова,
///   stderr 0 байт. Сессия не «сломалась» — она ИСЧЕЗЛА из списка.
///
/// Отсюда правило: предупредить может **только панель**, и предупредить она обязана **с числом**,
/// снятым с диска (<see cref="EngineJournalFormat"/>), а не общими словами.
///
/// ⚠️ **Граница, которую панель НЕ переходит:** она не уносит `session.v4` сама и не «чинит»
/// раскладку — это данные человека, и лечатся они одним ручным шагом, названным словами. Панель
/// говорит, что именно станет невидимым, что история цела и каким шагом видимость возвращается.
///
/// ⚠️ **Почему здесь решается, а не в окне.** Окно показывает и просит; числа и ключи — решение.
/// Тогда вопрос перед откатом, вопрос перед обновлением и строка после отката говорят ОДНО И ТО ЖЕ
/// одним и тем же счётом, и проверка идёт без окна, диска и движка.
/// </summary>
public static class EngineJournalFuse
{
    /// <summary>
    /// С КАКОЙ ВЕРСИИ ДВИЖОК ЧИТАЕТ НОВЫЙ ЖУРНАЛ — порог, названный числом, а не догадкой.
    ///
    /// Замеры, на которых он стоит (`ENGINE.md` §4, 10.10.2026 и `l2`, 18.10.2026):
    /// `0.1.5-rc.2` журнала v4 **не видит вовсе**; `0.2.0-rc.2` и `0.2.1-rc.2` видят и v3
    /// (`migration-required`), и v4. Между ними версий не было — поэтому порог проведён по старшей
    /// части версии, а сама граница названа здесь, чтобы её можно было перепроверить одной строкой.
    /// </summary>
    public const string FirstReadingNewJournal = "0.2.0";

    /// <summary>КЛЮЧИ СЛОВ — здесь только имена; фразы лежат в трёх словарях.</summary>
    public static class Words
    {
        /// <summary>Перед откатом: прежний движок не увидит N из M сессий (и не скажет об этом).</summary>
        public const string RollbackHidden = "EngineJournalRollbackHiddenFormat";

        /// <summary>После отката: то же самое в прошедшем времени — «не видит N из M».</summary>
        public const string RollbackHiddenAfter = "EngineJournalRollbackHiddenAfterFormat";

        /// <summary>Прятать нечего: цель увидит все сессии.</summary>
        public const string NothingHidden = "EngineJournalNothingHiddenFormat";

        /// <summary>Сессий на диске нет вовсе — честный ноль, а не «не знаю».</summary>
        public const string NoSessions = "EngineJournalNoSessions";

        /// <summary>Узнать не удалось (причина в скобках) — «не знаю» звучит словами.</summary>
        public const string Unknown = "EngineJournalUnknownFormat";

        /// <summary>Перед обновлением: N сессий переедут в новый формат при открытии.</summary>
        public const string UpdateMigrates = "EngineJournalUpdateMigratesFormat";

        /// <summary>Перед обновлением: все сессии уже в новом формате — переезжать нечему.</summary>
        public const string UpdateNothing = "EngineJournalUpdateNothingFormat";

        /// <summary>Выбранная версия нового формата не читает: N сессий она не увидит.</summary>
        public const string TargetOldFormat = "EngineJournalTargetOldFormatFormat";
    }

    /// <summary>
    /// ЧИТАЕТ ЛИ ЭТА ВЕРСИЯ НОВЫЙ ЖУРНАЛ. Пустая или непонятная версия — **нет** (и вызывающий
    /// обязан спросить заново: «не знаю версию» — это не «старая версия», см. ниже).
    /// </summary>
    public static bool ReadsNewJournal(string? version)
    {
        var numeric = EngineNode.Normalize(version);

        if (numeric.Length == 0) return false;

        return EngineNode.Compare(numeric, FirstReadingNewJournal) >= 0;
    }

    /// <summary>
    /// ПЕРЕД ОБНОВЛЕНИЕМ на <paramref name="target"/>: сколько сессий переедет в новый формат
    /// (и станет невидимым прежнему движку после первого же открытия).
    ///
    /// Число берётся с диска до работы: `OldFormatOnly` — те, что ещё в старом формате, то есть
    /// ровно те, кого новая версия переведёт. Уже переехавшие (`NewFormat`) вперёд не считаются:
    /// их судьба решена раньше, и врать про «переезд» нечего.
    /// </summary>
    public static EngineJournalWarning ForUpdate(EngineJournalState state, string? target)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Measured) return Unknown(state.Detail);

        // Версия не названа — сказать нечего; «не знаю» честнее, чем «прятать нечего».
        if (EngineNode.Normalize(target).Length == 0) return Unknown("version-not-named");

        if (state.Sessions == 0)
            return state.Flat > 0 ? Unknown("flat-layout") : Only(Words.NoSessions);

        if (ReadsNewJournal(target))
        {
            if (state.OldFormatOnly == 0) return Plain(Words.UpdateNothing, state.Sessions);

            return new EngineJournalWarning(
                EngineJournalRisk.Hidden,
                state.OldFormatOnly,
                state.Sessions,
                Words.UpdateMigrates,
                target!.Trim(),
                string.Empty);
        }

        // Цель новый формат НЕ читает: после установки она не увидит уже переехавшие сессии.
        if (state.NewFormat == 0) return Plain(Words.NothingHidden, state.Sessions, target);

        return new EngineJournalWarning(
            EngineJournalRisk.Hidden,
            state.NewFormat,
            state.Sessions,
            Words.TargetOldFormat,
            target!.Trim(),
            string.Empty);
    }

    /// <summary>
    /// ПЕРЕД ОТКАТОМ (и ПОСЛЕ него — <paramref name="after"/>): сколько сессий прежний движок
    /// не увидит. Число — `NewFormat`: у таких сессий рядом со старым журналом уже лежит новый,
    /// и движок старого формата не показывает их вовсе (замер: 197 → 196).
    /// </summary>
    public static EngineJournalWarning ForRollback(EngineJournalState state, string? target, bool after = false)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Measured) return Unknown(state.Detail);

        if (EngineNode.Normalize(target).Length == 0) return Unknown("version-not-named");

        if (state.Sessions == 0)
            return state.Flat > 0 ? Unknown("flat-layout") : Only(Words.NoSessions);

        // Цель читает новый журнал (0.2.1 → 0.2.0): прятать нечего, она увидит и v3, и v4.
        if (ReadsNewJournal(target)) return Plain(Words.NothingHidden, state.Sessions, target);

        if (state.NewFormat == 0) return Plain(Words.NothingHidden, state.Sessions, target);

        return new EngineJournalWarning(
            EngineJournalRisk.Hidden,
            state.NewFormat,
            state.Sessions,
            after ? Words.RollbackHiddenAfter : Words.RollbackHidden,
            target!.Trim(),
            string.Empty);
    }

    private static EngineJournalWarning Plain(string key, int total, string? version = null) =>
        new(EngineJournalRisk.NothingHidden, 0, total, key, (version ?? string.Empty).Trim(), string.Empty);

    private static EngineJournalWarning Only(string key) =>
        new(EngineJournalRisk.NothingHidden, 0, 0, key, string.Empty, string.Empty);

    private static EngineJournalWarning Unknown(string detail) =>
        new(EngineJournalRisk.Unknown, 0, 0, Words.Unknown, string.Empty, detail ?? string.Empty);
}
