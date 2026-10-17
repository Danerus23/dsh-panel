namespace DshPanel.Server;

/// <summary>Чем кончился откат по кнопке.</summary>
public enum EngineRollbackOutcome
{
    /// <summary>Вернулись: прежняя версия проверена живьём, запись переписана, сервер поднят заново.</summary>
    RolledBack,

    /// <summary>Не начинали: возвращаться некуда (нет записи или прежней версии на диске).</summary>
    Refused,

    /// <summary>Прежняя версия не прошла живую пробу — остаёмся на текущей, ничего не меняя.</summary>
    ProbeFailed,

    /// <summary>Свой движок погасить не удалось — запись не трогаем.</summary>
    StopFailed,

    /// <summary>Запись «стоит сейчас» не переписалась — остаёмся на текущей версии.</summary>
    RecordFailed,
}

/// <summary>Итог отката: чем кончилось, какая версия теперь и какими словами.</summary>
public sealed record EngineRollbackResult(
    EngineRollbackOutcome Outcome,
    string Version,
    string Detail,
    IReadOnlyList<string> Lines)
{
    /// <summary>Откат состоялся и подтверждён пробой.</summary>
    public bool Ok => Outcome == EngineRollbackOutcome.RolledBack;
}

/// <summary>
/// ДВЕРИ ОТКАТА — то, что исполнитель делает с МИРОМ: гасит свой движок, проверяет прежнюю версию
/// живьём, переписывает запись «стоит сейчас». Доводами — по той же причине, что у обновления:
/// без них «вернулись и проверили» проверялось бы на живой машине владельца.
/// </summary>
public sealed record EngineRollbackDoors(
    Func<int, bool> StopOwnEngine,
    Func<string, bool> Probe,
    Func<string, bool> WriteRecord)
{
    /// <summary>Заглушка: ничего не делает и всё отвергает (прогон проверки, изоляция).</summary>
    public static EngineRollbackDoors None { get; } = new(
        _ => false,
        _ => false,
        _ => false);
}

/// <summary>
/// ИСПОЛНИТЕЛЬ ОТКАТА ПО КНОПКЕ (пункт 4 плана E3.3). Делает ровно то, что велит
/// <see cref="EngineRollbackDecisions.Decide"/>, и в том же порядке, а машину трогает **через двери**.
///
/// **Порядок здесь — не вкус, а защита:**
///
/// 1. **Прежняя версия проверяется ЖИВЬЁМ ДО всякой подмены.** Проба поднимает её на свободном порту
///    в своём временном доме; не поднялась — панель остаётся на текущей версии и говорит это
///    словами. Так «вернулись в нерабочее» становится невозможным: переключение записи идёт
///    только после удачной пробы.
/// 2. **Гашение своего движка — после пробы.** Погасить и не суметь поднять — худший исход; сначала
///    убеждаемся, что поднимать есть что.
/// 3. **Запись переписывается последней**, и только она меняет «что стоит сейчас». Не переписалась —
///    панель остаётся на прежней (текущей) версии, а двери поднимают сервер обратно.
///
/// **Слова — по правилу владельца:** предупреждение ДО каждого важного шага, одна строка ПОСЛЕ.
/// Предупреждение об откате говорит **про сессии прямым текстом**: сессии, созданные новой версией,
/// старый движок не откроет — их журнал новее (замер: `session.v4` старым движком не читается вовсе).
/// </summary>
public static class EngineRollbackRun
{
    public static EngineRollbackResult Run(
        EngineRollbackPlan plan,
        int ownPort,
        EngineRollbackDoors doors,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(doors);

        var lines = new List<string>();

        void Say(string text)
        {
            lines.Add(text);
            log?.Invoke(text);
        }

        if (!plan.Any)
        {
            var refusal = EngineInstallPhrases.UpdateRefusal(plan.RefusalKey);

            Say(refusal);
            return new EngineRollbackResult(EngineRollbackOutcome.Refused, plan.Current, plan.RefusalKey, lines);
        }

        var target = plan.Target;

        // (1) ПРЕЖНЯЯ ВЕРСИЯ ЖИВЬЁМ: слова ДО, дело, одна строка ПОСЛЕ.
        Say(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.Probe, target));

        if (!doors.Probe(target))
        {
            Say(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.ProbeFailed, target, plan.Current));
            return new EngineRollbackResult(EngineRollbackOutcome.ProbeFailed, plan.Current, EngineRollbackDecisions.Words.ProbeFailed, lines);
        }

        // (2) ГАСИМ СВОЙ ДВИЖОК: через него может идти работа агента — предупреждение сказано окном.
        if (!doors.StopOwnEngine(ownPort))
        {
            Say(EngineInstallPhrases.UpdateStopFailed());
            return new EngineRollbackResult(EngineRollbackOutcome.StopFailed, plan.Current, EngineUpdateFlow.Words.WarnAndStopOwnEngine, lines);
        }

        Say(EngineInstallPhrases.UpdateStopped());

        // (3) ЗАПИСЬ: она и есть «что стоит сейчас».
        Say(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.Record, target));

        if (!doors.WriteRecord(target))
        {
            Say(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.RecordFailed, plan.Current));
            return new EngineRollbackResult(EngineRollbackOutcome.RecordFailed, plan.Current, EngineRollbackDecisions.Words.RecordFailed, lines);
        }

        Say(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.Done, target, plan.Current));

        return new EngineRollbackResult(EngineRollbackOutcome.RolledBack, target, string.Empty, lines);
    }
}
