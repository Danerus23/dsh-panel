using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ПРИЧИНА ОТКАЗА СЛОВАМИ: ключ, который отдаёт ЯДРО (<see cref="UpdateRefusals.Key"/>), →
/// строка словаря.
///
/// **Зачем отдельная таблица, если ключ уже есть.** Ядро называет отказ КЛЮЧОМ
/// (<c>"UpdateNoArchive"</c>), а не фразой, и это правильно: фразы живут в трёх словарях,
/// и движок не должен трогаться при каждой правке перевода. Но словарь адресуется ИМЕНЕМ
/// ЧЛЕНА <c>PanelStrings</c> (ворота <c>LocalizationTests</c> требуют, чтобы ключ словаря был
/// членом), а по строке-ключу член не выбрать. Отсюда эта таблица — единственное место, где
/// ключ ядра превращается в строку.
///
/// ⚠️ **Расхождение таблицы с ядром ЛОВИТСЯ, а не подразумевается**:
/// <see cref="Knows"/> обязан отвечать «да» на каждый ключ, который ядро может вернуть,
/// и это проверяет <c>UpdatePhrasesTests</c>, перебирая ВСЕ значения <see cref="UpdateRefusal"/>.
/// Забудь строку для нового отказа — проверка падает, а не молчит.
///
/// ⚠️ Правило подстановки взято у ядра ДОСЛОВНО (<see cref="UpdateRefusals.Key"/>): у ключа
/// с <c>Format</c> в имени в текст подставляются ЗНАЧЕНИЯ отказа
/// (<see cref="UpdatePreparation.Detail"/> и <see cref="UpdatePreparation.DetailMore"/> — имя
/// файла, хеш, ожидаемое и полученное), остальные показываются как есть. Человеческих слов
/// среди подставляемого нет: слова живут в трёх словарях. Проверка трёх языков собирает строку
/// тем же правилом (<see cref="Fill"/>) — второго правила в панели быть не должно.
/// </summary>
public static class UpdateRefusalLines
{
    /// <summary>
    /// Есть ли у этого ключа своя строка. Ищется по ОТДЕЛЬНОЙ таблице известных ключей,
    /// а не по «получилось ли что-то непустое»: у неизвестного ключа строка тоже есть
    /// («причина отказа не названа»), и проверка, спрашивающая не то, зеленела бы всегда.
    /// </summary>
    public static bool Knows(string? key) => !string.IsNullOrEmpty(key) && Known(key) is not null;

    /// <summary>
    /// Строка отказа по ключу ядра. Пустой ключ — «отказа нет». Незнакомый ключ — «причина отказа
    /// не названа»: пустое место человек прочитал бы как сломанную панель, а выдумывать причину
    /// нельзя.
    /// </summary>
    public static string Text(string? key) => key is null or ""
        ? string.Empty
        : Known(key) ?? PanelStrings.UpdateRefusedUnknown;

    /// <summary>
    /// Строка для человека по готовому ответу движка: ключ плюс значения. Отказа нет —
    /// пустая строка (окну нечего показывать).
    /// </summary>
    public static string Line(UpdatePreparation? preparation)
    {
        if (preparation is null || preparation.Ok) return string.Empty;

        var key = preparation.RefusalKey;

        return Fill(key, Text(key), preparation.Detail, preparation.DetailMore);
    }

    /// <summary>
    /// ЗНАЧЕНИЯ В ГОТОВЫЙ ТЕКСТ: «<c>Format</c>» в имени ключа значит «сюда идут значения отказа».
    ///
    /// Правило ОДНО на панель и вынесено отдельно ровно затем, чтобы проверка трёх языков
    /// собирала строку ТЕМ ЖЕ правилом, а не своим: своё правило в проверке разошлось бы
    /// с панелью молча — и прогон зеленел бы на строке, которой человек не видит.
    ///
    /// Лишний довод <c>string.Format</c> пропускает: у отказа со значениями их бывает одно
    /// (<see cref="UpdatePreparation.Detail"/>) или два (плюс <see cref="UpdatePreparation.DetailMore"/>).
    /// </summary>
    public static string Fill(string? key, string text, string detail, string more) =>
        (key ?? string.Empty).Contains("Format", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, text, detail, more)
            : text;

    /// <summary>
    /// Знакомая строка ключа или <c>null</c>, если ключа панель не знает. Таблица ОДНА:
    /// по ней отвечают и <see cref="Knows"/>, и <see cref="Text"/>.
    /// </summary>
    private static string? Known(string key) => key switch
    {
        "UpdateRefusedLocked" => PanelStrings.UpdateRefusedLocked,
        "UpdateAlreadyLatest" => PanelStrings.UpdateAlreadyLatest,
        "UpdateNoReleaseVersion" => PanelStrings.UpdateNoReleaseVersion,
        "UpdateRefusedNoTarget" => PanelStrings.UpdateRefusedNoTarget,
        "UpdateNoArchive" => PanelStrings.UpdateNoArchive,
        "UpdateNoSums" => PanelStrings.UpdateNoSums,
        "UpdateSumsDownloadFailedFormat" => PanelStrings.UpdateSumsDownloadFailedFormat,
        "UpdateSumsUnreadable" => PanelStrings.UpdateSumsUnreadable,
        "UpdateArchiveDownloadFailedFormat" => PanelStrings.UpdateArchiveDownloadFailedFormat,
        "UpdateArchiveSumMissingFormat" => PanelStrings.UpdateArchiveSumMissingFormat,
        "UpdateArchiveSumMismatchFormat" => PanelStrings.UpdateArchiveSumMismatchFormat,
        "UpdateArchiveMissing" => PanelStrings.UpdateArchiveMissing,
        "UpdateUnpackFolderMissing" => PanelStrings.UpdateUnpackFolderMissing,
        "UpdateArchiveUnreadable" => PanelStrings.UpdateArchiveUnreadable,
        "UpdateArchiveEmpty" => PanelStrings.UpdateArchiveEmpty,
        "UpdateArchiveUnsafeFormat" => PanelStrings.UpdateArchiveUnsafeFormat,
        "UpdateArchiveBrokenFormat" => PanelStrings.UpdateArchiveBrokenFormat,
        "UpdateNoExe" => PanelStrings.UpdateNoExe,
        "UpdateVersionMismatchFormat" => PanelStrings.UpdateVersionMismatchFormat,
        "UpdateSetupDownloadFailedFormat" => PanelStrings.UpdateSetupDownloadFailedFormat,
        "UpdateSetupSumMissingFormat" => PanelStrings.UpdateSetupSumMissingFormat,
        "UpdateSetupSumMismatchFormat" => PanelStrings.UpdateSetupSumMismatchFormat,
        "UpdateNoBackup" => PanelStrings.UpdateNoBackup,
        "UpdateWriteFailedFormat" => PanelStrings.UpdateWriteFailedFormat,

        // Ключ «панель не знает отказа» тоже приходит ОТ ЯДРА — оно отдаёт его на значение, которого
        // само не знает (журнал или выпуск правили руками). Значит строка ему нужна, и она есть.
        "UpdateRefusedUnknown" => PanelStrings.UpdateRefusedUnknown,
        _ => null,
    };
}

/// <summary>
/// ЧЕМ КОНЧИЛАСЬ ЗАМЕНА ФАЙЛОВ — словами, по ключу итога (<see cref="UpdateOutcomes.ReasonKeyFor"/>).
///
/// Устроено как <see cref="UpdateRefusalLines"/> и по той же причине: итог приходит из журнала
/// замены ключом, а фраза живёт в словаре. Проверка «у каждого итога есть строка» перебирает
/// ВСЕ значения <see cref="UpdateResultKind"/> — новый итог без строки обязан уронить прогон.
/// </summary>
public static class UpdateReasonLines
{
    /// <summary>Есть ли у этого ключа своя строка (пустой ключ — «итога нет»).</summary>
    public static bool Knows(string? key) => !string.IsNullOrEmpty(key) && Known(key) is not null;

    /// <summary>Строка итога по ключу. Незнакомый ключ — «причина не названа».</summary>
    public static string Text(string? key) => key is null or ""
        ? string.Empty
        : Known(key) ?? PanelStrings.UpdateReasonUnknown;

    private static string? Known(string key) => key switch
    {
        "UpdateReasonStillOld" => PanelStrings.UpdateReasonStillOld,
        "UpdateReasonNoPanel" => PanelStrings.UpdateReasonNoPanel,
        "UpdateReasonRolledBack" => PanelStrings.UpdateReasonRolledBack,
        "UpdateReasonRestoreFailed" => PanelStrings.UpdateReasonRestoreFailed,
        "UpdateReasonNoBackup" => PanelStrings.UpdateReasonNoBackup,
        "UpdateReasonPanelRunning" => PanelStrings.UpdateReasonPanelRunning,
        "UpdateReasonMutexBusy" => PanelStrings.UpdateReasonMutexBusy,
        "UpdateReasonMutexUnknown" => PanelStrings.UpdateReasonMutexUnknown,

        // Ключ «причина не названа» ядро отдаёт САМО (итог, которого оно не знает), значит строка
        // ему нужна — и это тот же ответ, что панель говорит на незнакомый чужой ключ.
        "UpdateReasonUnknown" => PanelStrings.UpdateReasonUnknown,
        _ => null,
    };
}

/// <summary>
/// ИТОГ ЗАМЕНЫ ФАЙЛОВ — ЧТО СКАЗАТЬ ПРИ СЛЕДУЮЩЕМ ЗАПУСКЕ ПАНЕЛИ.
///
/// **Зачем отдельным решением, а не условием в <c>App</c>.** «Молчать или говорить» — это ровно
/// то место, где панель 1.x однажды промолчала: журнал замены не читал ни один файл, и человек
/// видел «обновление готово» там, где файлы не заменились. Условие, живущее внутри обработчика
/// запуска, проверить прогоном нельзя, а цена ошибки — обновление, о котором человеку не сказали.
/// Поэтому решение — ЧИСТАЯ функция, проверяемая перебором, а <c>App</c> только зовёт её.
/// </summary>
public static class UpdateOutcomeLines
{
    /// <summary>
    /// СТРОКА В ЖУРНАЛ о том, чем кончилось обновление: у применившегося — короткая (человеку
    /// говорить нечего, но факт смены версии должен быть объясним), у остальных — техническая
    /// причина (<see cref="UpdateOutcome.Detail"/>).
    /// </summary>
    public static string JournalLine(UpdateOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Failed
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateLogFormat, outcome.Detail)
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAppliedLogFormat, outcome.Version);
    }

    /// <summary>
    /// ТЕКСТ СООБЩЕНИЯ ЧЕЛОВЕКУ. Пусто — сообщать нечего (обновление применилось: панель и так
    /// обновилась, и шарик об этом был бы шумом). Непусто — и тогда сказано и ЧТО не применилось,
    /// и ПОЧЕМУ, словами словаря по ключу ядра.
    /// </summary>
    public static string Notice(UpdateOutcome? outcome)
    {
        if (outcome is null || !outcome.Failed) return string.Empty;

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.UpdateNoticeFailedFormat,
            outcome.Version,
            UpdateReasonLines.Text(outcome.ReasonKey));
    }
}

/// <summary>
/// ЭТАП ПОДГОТОВКИ СЛОВАМИ: значение <see cref="UpdateStage"/> → подпись в словаре.
///
/// Здесь ключа от ядра нет вовсе (ядро отдаёт перечисление), поэтому и таблица простая —
/// зато она ОДНА: подпись этапа берут и ход подготовки в окне, и отчёт самотеста, и второго
/// списка подписей в панели не заводится.
/// </summary>
public static class UpdateStageLines
{
    /// <summary>Есть ли у этого этапа своя подпись.</summary>
    public static bool Knows(UpdateStage stage) => Text(stage).Length > 0;

    /// <summary>Подпись этапа. Незнакомое значение — пустая строка: выдумывать этап нельзя.</summary>
    public static string Text(UpdateStage stage) => stage switch
    {
        UpdateStage.Sums => PanelStrings.UpdateStageSums,
        UpdateStage.Archive => PanelStrings.UpdateStageArchive,
        UpdateStage.Setup => PanelStrings.UpdateStageSetup,
        UpdateStage.Verify => PanelStrings.UpdateStageVerify,
        UpdateStage.Unpack => PanelStrings.UpdateStageUnpack,
        UpdateStage.Backup => PanelStrings.UpdateStageBackup,
        UpdateStage.Script => PanelStrings.UpdateStageScript,
        _ => string.Empty,
    };
}
