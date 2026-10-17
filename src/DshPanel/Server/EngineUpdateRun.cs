namespace DshPanel.Server;

/// <summary>Чем кончилось обновление движка.</summary>
public enum EngineUpdateOutcome
{
    /// <summary>Поставлено, проверено пробой, записано.</summary>
    Updated,

    /// <summary>Не начинали: план отказывает (нет проверенной версии, свежая не новее).</summary>
    Refused,

    /// <summary>Предохранительную копию профиля снять не удалось — установка НЕ начиналась.</summary>
    CopyFailed,

    /// <summary>Свой движок погасить не удалось — установка НЕ начиналась.</summary>
    StopFailed,

    /// <summary>Установка по замку не прошла (откат уже сделан установщиком).</summary>
    InstallFailed,

    /// <summary>Поставлено, но проба не прошла: «готово» панель не говорит.</summary>
    ProbeFailed,
}

/// <summary>Итог: чем кончилось, какими словами и сколько шагов пройдено.</summary>
public sealed record EngineUpdateResult(
    EngineUpdateOutcome Outcome,
    string Version,
    string Detail,
    IReadOnlyList<string> Lines)
{
    /// <summary>Обновление состоялось и подтверждено пробой.</summary>
    public bool Ok => Outcome == EngineUpdateOutcome.Updated;
}

/// <summary>
/// ДВЕРИ ОБНОВЛЕНИЯ — то, что исполнитель делает с МИРОМ: гасит свой движок, снимает копию профиля,
/// ставит по замку, прогоняет пробу. Доводами — по той же причине, что у установки: без них
/// «обновили и проверили» проверялось бы на живой машине владельца.
/// </summary>
public sealed record EngineUpdateDoors(
    Func<int, EngineUpdateStep, bool> StopOwnEngine,
    Func<string, EngineUpdateStep, bool> CopyProfile,
    Func<string, EngineUpdateStep, EngineInstallResult> Install,
    Func<string, EngineUpdateStep, bool> Probe)
{
    /// <summary>Настоящие двери собирает окно (E3.2): контроллер сервера, копия профиля, установщик, проба.</summary>
    public static EngineUpdateDoors None { get; } = new(
        (_, _) => false,
        (_, _) => false,
        (version, _) => new EngineInstallResult(EngineInstallOutcome.Refused, version, string.Empty,
            EngineInstallRefusals.NoLock, string.Empty, null),
        (_, _) => false);
}

/// <summary>
/// ИСПОЛНИТЕЛЬ ПУТИ ОБНОВЛЕНИЯ (пункт 4 плана E3.2). Делает ровно то, что велит
/// <see cref="EngineUpdateFlow.Plan"/>, и в том же порядке, а машину трогает **через двери**.
///
/// **Два правила, и оба — из замеров:**
///
/// 1. **Перед каждым важным шагом — слова, после — одна строка.** Гашение своего движка задевает
///    работу агента, снятие копии тратит время и место, установка тратит сеть: человек должен
///    видеть, что происходит, а не узнавать после.
/// 2. **Ничего не начинается, если предыдущий важный шаг не удался.** Копия не снялась — установки
///    НЕ БЫЛО («полуархива не бывает»); движок не погасился — тоже. Проверяется по журналу: строки
///    установки в нём просто нет.
/// </summary>
public static class EngineUpdateRun
{
    /// <summary>Обновить движок по плану и названным дверям. Исключений не бросает: беда — это ответ.</summary>
    public static EngineUpdateResult Run(EngineUpdatePlan plan, EngineUpdateDoors doors, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(doors);

        var lines = new System.Collections.Generic.List<string>();
        void Say(string text)
        {
            lines.Add(text);
            log?.Invoke(text);
        }

        if (!plan.Any)
        {
            Say(EngineInstallPhrases.UpdateRefusal(plan.RefusalKey));
            return new EngineUpdateResult(EngineUpdateOutcome.Refused, string.Empty, plan.RefusalKey, lines);
        }

        var version = plan.Version;

        // (1) Спросить человека — этот шаг делает окно ДО вызова; здесь он только отмечается строкой.
        // (2) Предупредить и погасить СВОЙ движок: слова ДО, дело, одна строка ПОСЛЕ.
        foreach (var step in plan.Steps)
        {
            switch (step.Kind)
            {
                case EngineUpdateStepKind.AskHuman:
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey));
                    break;

                case EngineUpdateStepKind.WarnAndStopOwnEngine:
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey, step.Detail));

                    if (!doors.StopOwnEngine(Port(step), step))
                    {
                        Say(EngineInstallPhrases.UpdateStopFailed());
                        return new EngineUpdateResult(EngineUpdateOutcome.StopFailed, version, EngineUpdateFlow.Words.WarnAndStopOwnEngine, lines);
                    }

                    Say(EngineInstallPhrases.UpdateStopped());
                    break;

                case EngineUpdateStepKind.CopyProfile:
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey, version));

                    if (!doors.CopyProfile(version, step))
                    {
                        // ⚠️ «Полуархива не бывает»: без копии обновление НЕ начинается.
                        Say(EngineInstallPhrases.UpdateStep(EngineUpdateFlow.Words.CopyNotWhole));
                        return new EngineUpdateResult(EngineUpdateOutcome.CopyFailed, version, EngineUpdateFlow.Words.CopyNotWhole, lines);
                    }

                    Say(EngineInstallPhrases.UpdateCopied());
                    break;

                case EngineUpdateStepKind.InstallByLock:
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey, version));

                    var installed = doors.Install(version, step);

                    if (!installed.Ok)
                    {
                        Say(installed.Detail);
                        return new EngineUpdateResult(EngineUpdateOutcome.InstallFailed, version, installed.RefusalKey, lines);
                    }

                    Say(installed.Detail);
                    break;

                case EngineUpdateStepKind.Probe:
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey, version));

                    if (!doors.Probe(version, step))
                    {
                        // Поставлено — но «готово» панель не говорит: проба не прошла.
                        Say(EngineInstallPhrases.UpdateProbeFailed());
                        return new EngineUpdateResult(EngineUpdateOutcome.ProbeFailed, version, string.Empty, lines);
                    }

                    Say(EngineInstallPhrases.UpdateProbed());
                    break;

                case EngineUpdateStepKind.WriteRecord:
                    // Запись делает установщик (он же её и ведёт): здесь только строка.
                    Say(EngineInstallPhrases.UpdateStep(step.WordsKey, version));
                    break;
            }
        }

        Say(EngineInstallPhrases.UpdateDone(version));
        return new EngineUpdateResult(EngineUpdateOutcome.Updated, version, string.Empty, lines);
    }

    private static int Port(EngineUpdateStep step) =>
        int.TryParse(step.Detail, out var port) ? port : 0;
}
