namespace DshPanel.Server;

/// <summary>Чем кончилось «а можно ли вообще» — до всякой работы с машиной.</summary>
public enum EngineUpdateRefusal
{
    /// <summary>Обновляться некуда: рекомендованной (проверенной) версии нет.</summary>
    NoRecommended,

    /// <summary>Свежую просили поставить, а она не новее рекомендованной.</summary>
    FreshNotNewer,

    /// <summary>Предохранительную копию профиля снять не удалось — обновление НЕ начинается.</summary>
    CopyNotWhole,
}

/// <summary>Шаг обновления: что делаем и какими словами об этом говорим.</summary>
public enum EngineUpdateStepKind
{
    /// <summary>Спросить человека — ВСЕГДА (признака «идёт работа» у панели нет).</summary>
    AskHuman,

    /// <summary>Предупредить и погасить СВОЙ движок перед подменой версии.</summary>
    WarnAndStopOwnEngine,

    /// <summary>Снять предохранительную копию профиля (целиком или никак).</summary>
    CopyProfile,

    /// <summary>Поставить выбранную версию по замку.</summary>
    InstallByLock,

    /// <summary>Прогнать пробу на поставленном движке — и только потом сказать «готово».</summary>
    Probe,

    /// <summary>Записать, что стоит сейчас (из этой записи живут откат и опись).</summary>
    WriteRecord,
}

/// <summary>Один шаг: вид и слова (ключ словаря и доводы).</summary>
public sealed record EngineUpdateStep(EngineUpdateStepKind Kind, string WordsKey, string Detail = "");

/// <summary>План обновления: можно ли, какой версией и по каким шагам.</summary>
public sealed record EngineUpdatePlan(
    bool Any,
    string Version,
    EngineUpdateRefusal Refusal,
    string RefusalKey,
    IReadOnlyList<EngineUpdateStep> Steps)
{
    /// <summary>Почему нельзя — ключом словаря; пусто, когда можно.</summary>
    public string Key => Any ? string.Empty : RefusalKey;
}

/// <summary>
/// ПУТЬ ОБНОВЛЕНИЯ ДВИЖКА — ЧИСТЫЙ ПЛАН ШАГОВ (пункты 4–5 плана E3.2).
///
/// **Здесь только порядок и слова, ни одного действия.** Машину трогает исполнитель (следующий
/// кусок): гасит движок, снимает копию, зовёт `npm ci`, прогоняет пробу. Разделение то же, что
/// у установки: план проверяется без окна, сети и живого движка, а исполнитель — живьём.
///
/// **Два вывода замеров закреплены здесь порядком шагов, а не обещанием:**
///
/// 1. **«Спрашивать ВСЕГДА перед обновлением движка»** — первый шаг плана это `AskHuman`, и он есть
///    **всегда** (<see cref="EngineUpdatePolicy.AskAlwaysBeforeUpdate"/>): признака «идёт работа»
///    у панели нет, а «сервер отвечает» признаком не является (замер: отвечающий 3080 при простое).
///    Никакого «умного определения» здесь не будет, пока оно не измерено на живом движке с ключом.
/// 2. **«Полуархива не бывает»** — копия профиля стоит в плане **до** установки, и её неудача
///    не «предупреждение», а **отказ**: обновление не начинается (<see cref="EngineUpdateRefusal.CopyNotWhole"/>).
///
/// ⚠️ **Непроверенная версия ставится только по отдельной просьбе** (`freshWanted`) и только когда
/// она **новее** рекомендованной; иначе — отказ словами. Главная кнопка (`freshWanted: false`)
/// непроверенную не ставит никогда (<see cref="EngineUpdateDecisions.MainButtonInstallsFresh"/>).
/// </summary>
public static class EngineUpdateFlow
{
    /// <summary>КЛЮЧИ СЛОВ ПУТИ ОБНОВЛЕНИЯ — здесь только имена, фразы живут в трёх словарях.</summary>
    public static class Words
    {
        public const string NoRecommended = "EngineUpdateNoRecommended";
        public const string FreshNotNewer = "EngineUpdateFreshNotNewer";
        public const string CopyNotWhole = "EngineUpdateCopyNotWhole";
        public const string AskHuman = "EngineUpdateAskHuman";
        public const string WarnAndStopOwnEngine = "EngineUpdateWarnStopOwnEngineFormat";
        public const string CopyProfile = "EngineUpdateCopyProfileFormat";
        public const string InstallByLock = "EngineUpdateInstallByLockFormat";
        public const string Probe = "EngineUpdateProbeFormat";
        public const string WriteRecord = "EngineUpdateWriteRecordFormat";
    }

    /// <summary>
    /// СОСТАВИТЬ ПЛАН. Порт своего сервера нужен для слов предупреждения: человек обязан видеть,
    /// **какой** сервер панель сейчас погасит, а не «какой-то».
    /// </summary>
    public static EngineUpdatePlan Plan(
        EngineHints? hints,
        string? freshVersion,
        bool freshWanted,
        int ownPort)
    {
        var recommended = EngineUpdateDecisions.Recommended(hints);

        if (recommended.Length == 0)
        {
            return Refuse(EngineUpdateRefusal.NoRecommended, Words.NoRecommended);
        }

        var version = recommended;

        if (freshWanted)
        {
            // Непроверенную ставим ТОЛЬКО когда она новее; «то же самое, только страшнее» не бывает.
            if (!EngineUpdateDecisions.MayInstallFresh(freshVersion, recommended))
            {
                return Refuse(EngineUpdateRefusal.FreshNotNewer, Words.FreshNotNewer);
            }

            version = freshVersion!.Trim();
        }

        var steps = new List<EngineUpdateStep>
        {
            // (1) ВСЕГДА: спросить — потому что признака работы у панели нет.
            new(EngineUpdateStepKind.AskHuman, Words.AskHuman),
        };

        if (EngineUpdatePolicy.AskAlwaysBeforeUpdate)
        {
            // Шаг уже стоит первым; здесь только страховка от «умного определения» в будущем.
            steps[0] = new EngineUpdateStep(EngineUpdateStepKind.AskHuman, Words.AskHuman, "always");
        }

        // (2) Предупредить и погасить свой движок: через него может идти работа агента.
        steps.Add(new EngineUpdateStep(EngineUpdateStepKind.WarnAndStopOwnEngine, Words.WarnAndStopOwnEngine,
            ownPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // (3) Копия профиля — ДО установки: без неё подмена версии может стать необратимой.
        steps.Add(new EngineUpdateStep(EngineUpdateStepKind.CopyProfile, Words.CopyProfile, version));

        // (4) Поставить по замку, (5) прогнать пробу, (6) записать — только тогда «готово».
        steps.Add(new EngineUpdateStep(EngineUpdateStepKind.InstallByLock, Words.InstallByLock, version));
        steps.Add(new EngineUpdateStep(EngineUpdateStepKind.Probe, Words.Probe, version));
        steps.Add(new EngineUpdateStep(EngineUpdateStepKind.WriteRecord, Words.WriteRecord, version));

        return new EngineUpdatePlan(true, version, EngineUpdateRefusal.NoRecommended, string.Empty, steps);
    }

    /// <summary>
    /// ЧТО ДЕЛАТЬ, ЕСЛИ КОПИЮ СНЯТЬ НЕ УДАЛОСЬ: **ничего** — обновление не начинается.
    /// «Полуархива не бывает»: копия, выглядящая как право откатиться, опаснее её отсутствия.
    /// </summary>
    public static EngineUpdatePlan CopyFailed() =>
        Refuse(EngineUpdateRefusal.CopyNotWhole, Words.CopyNotWhole);

    /// <summary>Ключ отказа словами — один на все случаи, чтобы окно не искало фразу по коду.</summary>
    public static string RefusalKey(EngineUpdateRefusal refusal) => refusal switch
    {
        EngineUpdateRefusal.NoRecommended => Words.NoRecommended,
        EngineUpdateRefusal.FreshNotNewer => Words.FreshNotNewer,
        EngineUpdateRefusal.CopyNotWhole => Words.CopyNotWhole,
        _ => string.Empty,
    };

    private static EngineUpdatePlan Refuse(EngineUpdateRefusal refusal, string key) =>
        new(false, string.Empty, refusal, key, Array.Empty<EngineUpdateStep>());
}
