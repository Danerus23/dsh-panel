using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// СЛОВА ПРЕДОХРАНИТЕЛЯ ЖУРНАЛА — одна дверь (как <c>EngineInstallPhrases</c> у установки).
///
/// **Зачем одна.** Об одном и том же (сколько сессий исчезнет из списка прежнего движка) панель
/// говорит в ТРЁХ местах: в вопросе перед обновлением, в вопросе перед откатом и в строке после
/// отката. Если бы каждое место собирало фразу само, три места однажды сказали бы разное —
/// а человек читает именно их.
///
/// ⚠️ **Строк в коде нет:** только сборка. Числа (сколько из скольких) подставляются доводами,
/// а сами фразы лежат в трёх словарях (`ru`, `en`, `zh`) и сверяются сторожем словарей.
/// </summary>
public static class EngineJournalPhrases
{
    /// <summary>
    /// СЛОВА ПО ПРЕДУПРЕЖДЕНИЮ: пусто, когда говорить нечего (нет ни одной беды — и это не «0 сессий»),
    /// иначе — фраза на языке панели с числами из предупреждения.
    /// </summary>
    public static string Sentence(EngineJournalWarning? warning)
    {
        if (warning is null || !warning.SaysSomething) return string.Empty;

        return warning.Key switch
        {
            EngineJournalFuse.Words.RollbackHidden => Loc.T(warning.Key, warning.Version, warning.AtRisk, warning.Total),
            EngineJournalFuse.Words.RollbackHiddenAfter => Loc.T(warning.Key, warning.Version, warning.AtRisk, warning.Total),
            EngineJournalFuse.Words.TargetOldFormat => Loc.T(warning.Key, warning.Version, warning.AtRisk, warning.Total),
            EngineJournalFuse.Words.NothingHidden => Loc.T(warning.Key, warning.Version, warning.Total),
            EngineJournalFuse.Words.UpdateNothing => Loc.T(warning.Key, warning.Total),
            EngineJournalFuse.Words.UpdateMigrates => Loc.T(warning.Key, warning.AtRisk, warning.Total),
            EngineJournalFuse.Words.NoSessions => Loc.T(warning.Key),
            EngineJournalFuse.Words.Unknown => Loc.T(warning.Key, warning.Detail),

            // Ключ, которого здесь нет, — ошибка сборки, а не повод показать человеку имя ключа:
            // пусть это будет пусто, а словом займётся сторож словарей.
            _ => string.Empty,
        };
    }
}
