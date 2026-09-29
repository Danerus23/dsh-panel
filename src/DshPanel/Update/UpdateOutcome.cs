using DshPanel.Isolation;

namespace DshPanel.Update;

/// <summary>
/// ЧЕМ КОНЧИЛАСЬ ЗАМЕНА ФАЙЛОВ — тот самый итог, который сценарий пишет последней строкой
/// журнала. Набор итогов перенесён у панели 1.x (<c>dsh-tray\UpdateService.cs</c>) ДОСЛОВНО:
/// это не вкус, а словарь, которым обмениваются сценарий и панель. Появится здесь своё слово —
/// и панель перестанет понимать собственное обновление.
/// </summary>
public enum UpdateResultKind
{
    /// <summary>Итога нет: сценарий до него не дошёл (его не запускали, машину выключили).</summary>
    None,

    /// <summary>Файлы заменены, панель поднялась из своей папки.</summary>
    Applied,

    /// <summary>Файлы заменены, но панель из папки панели не поднялась.</summary>
    AppliedNoPanel,

    /// <summary>Замена не удалась, прежняя версия возвращена из страховочной копии.</summary>
    RolledBack,

    /// <summary>Откат не удался: вернуть прежнюю версию не вышло.</summary>
    FailedRestore,

    /// <summary>Откатываться некуда: годной страховочной копии нет.</summary>
    FailedNoBackup,

    /// <summary>Панель не вышла вовремя — файлы не трогали.</summary>
    NotAppliedPanelRunning,

    /// <summary>Замок одной копии занят — файлы не трогали.</summary>
    NotAppliedMutex,

    /// <summary>Замок не удалось проверить — файлы не трогали.</summary>
    MutexCheckFailed,

    /// <summary>В журнале стоит итог, которого панель не знает: журнал правили руками.</summary>
    Unknown,
}

/// <summary>
/// ЧТО СТАЛО С ОБНОВЛЕНИЕМ — три ответа и отдельный четвёртый, и каждый назван своей причиной.
///
/// * <see cref="Applied"/> — замена состоялась: версия в папке обновления не новее установленной;
/// * <see cref="NotApplied"/> — НЕ состоялась: панель работает прежней версией, а ждавшая сборка
///   новее её. Об этом человеку говорят, и <see cref="UpdateOutcome.ReasonKey"/> называет причину;
/// * <see cref="RolledBack"/> — не состоялась, но прежняя версия возвращена из страховочной копии:
///   панель цела, и это тоже надо сказать словами, а не молчанием;
/// * <see cref="Unknown"/> — папка обновления есть, а прочитать её не удалось.
///
/// ⚠️ Четвёртый ответ — решение, принятое здесь осознанно, и оно названо дирижёру: замолчать
/// нечитаемое состояние значило бы оставить человека с «обновление готово» там, где панель
/// не знает правды. Три ответа без него не покрывают случая «прочитать не вышло».
/// </summary>
public enum UpdateVerdict
{
    /// <summary>Замена состоялась.</summary>
    Applied,

    /// <summary>Не применилось: панель работает прежней версией.</summary>
    NotApplied,

    /// <summary>Откатилось: прежняя версия возвращена из страховочной копии.</summary>
    RolledBack,

    /// <summary>Понять не удалось: состояние обновления прочитать не вышло, причина — в Detail.</summary>
    Unknown,
}

/// <summary>
/// ИТОГ ОБНОВЛЕНИЯ, ПРОЧИТАННЫЙ ПРИ ЗАПУСКЕ ПАНЕЛИ. Наружу отдаются ДАННЫЕ, а не готовая фраза:
/// ключ строки (<see cref="ReasonKey"/>) и версия — остальное соберёт интерфейс на языке панели.
///
/// <see cref="Detail"/> — техническая причина для журнала и отчёта (в ней бывает и текст
/// исключения). Показывать её человеку вместо строки по ключу не нужно.
/// </summary>
public sealed record UpdateOutcome(
    UpdateVerdict Verdict,
    string Version,
    UpdateResultKind Kind,
    string Marker,
    string ReasonKey,
    string FailedFolder,
    string Detail)
{
    /// <summary>Об этом надо сказать человеку: всё, кроме «применилось».</summary>
    public bool Failed => Verdict != UpdateVerdict.Applied;
}

/// <summary>
/// ЧТЕНИЕ ИТОГА ЗАМЕНЫ ПРИ СЛЕДУЮЩЕМ ЗАПУСКЕ ПАНЕЛИ — то, что панель 1.x звала
/// <c>StartupNotice</c>, и перенесено сюда вместе с её уроком: **итог замены обязан кто-то
/// читать**. В 1.x журнал сценария (<c>update.log</c>) однажды не читал ни один <c>.cs</c>:
/// человек видел «обновление готово» даже тогда, когда файлы не заменились, а журнал тихо лежал
/// в состоянии панели.
///
/// **Решение принимается по ВЕРСИИ в имени папки, а не по маркеру сценария.** Маркер объясняет
/// ПОЧЕМУ, версия отвечает ЧТО СТАЛОСЬ:
///
/// * версия в папке СТРОГО новее нашей — обновление не применилось (замена не состоялась, откат
///   вернул прежнюю версию, файлы не скопировались). Об этом человеку говорят ОДИН раз, после
///   чего папка переименовывается в <c>failed-&lt;версия&gt;</c>: она остаётся для разбора
///   и ручной починки, но повторно о ней уже не сообщается — ни при следующем запуске,
///   ни после перезагрузки;
/// * версия не новее — считаем, что замена состоялась, и убираем за собой. **Честная оговорка
///   остаётся в журнале:** при пересборке выпуска под тем же номером замены могло и не быть —
///   код robocopy 0..7 означает «нечего копировать» не реже, чем «скопировано».
///
/// Возвращает <c>null</c>, если папки обновления нет, подготовленной сборки в ней не осталось
/// или у прогона нет права трогать это состояние.
/// </summary>
public static class UpdateOutcomes
{
    /// <summary>Строка итога, которую сценарий замены пишет в журнал последней.</summary>
    public const string ResultMarkerPrefix = "update result: ";

    /// <summary>
    /// Словарь итогов: текст ↔ значение — ОДИН список на панель. Сценарий берёт из него строки
    /// (<see cref="UpdateScript"/>), а чтение журнала — значения; второй список однажды разошёлся
    /// бы с первым, и «откатилось» в журнале перестало бы значить «откатилось» в панели.
    /// </summary>
    private static readonly Dictionary<UpdateResultKind, string> Markers = new()
    {
        [UpdateResultKind.Applied] = "applied",
        [UpdateResultKind.AppliedNoPanel] = "applied-no-panel",
        [UpdateResultKind.RolledBack] = "rolled-back",
        [UpdateResultKind.FailedRestore] = "failed-restore",
        [UpdateResultKind.FailedNoBackup] = "failed-no-backup",
        [UpdateResultKind.NotAppliedPanelRunning] = "not-applied-panel-running",
        [UpdateResultKind.NotAppliedMutex] = "not-applied-mutex",
        [UpdateResultKind.MutexCheckFailed] = "mutex-check-failed",
    };

    /// <summary>Текст итога в журнале. У «итога нет» и «итог незнаком» текста нет.</summary>
    public static string Marker(UpdateResultKind kind) =>
        Markers.TryGetValue(kind, out var text) ? text : string.Empty;

    /// <summary>
    /// Значение по тексту из журнала. Пусто — итога нет (сценарий до него не дошёл),
    /// незнакомый текст — <see cref="UpdateResultKind.Unknown"/>: журнал правят руками,
    /// и «не понял» — это честный ответ, а не «ничего не было».
    /// </summary>
    public static UpdateResultKind KindOf(string? marker)
    {
        var text = (marker ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return UpdateResultKind.None;

        foreach (var pair in Markers)
        {
            if (string.Equals(pair.Value, text, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }

        return UpdateResultKind.Unknown;
    }

    /// <summary>
    /// Последняя строка «update result: …» в журнале. Именно ПОСЛЕДНЯЯ: сценарий может пройти
    /// несколько ветвей, и признаком кончины является последнее слово.
    /// </summary>
    public static string ReadMarker(string? logText)
    {
        var marker = string.Empty;
        if (string.IsNullOrEmpty(logText)) return marker;

        foreach (var raw in logText.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(ResultMarkerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            marker = line[ResultMarkerPrefix.Length..].Trim();
        }

        return marker;
    }

    /// <summary>
    /// КЛЮЧ СТРОКИ ДЛЯ ЧЕЛОВЕКА по итогу сценария. Ключи взяты у панели 1.x и названы по правилу
    /// этого проекта (<c>PanelStrings</c> объявляет строку именем вида <c>UpdateReason…</c>).
    /// Фразы здесь НЕТ намеренно: их подключает дирижёр, а этот файл отдаёт данные.
    ///
    /// ⚠️ Итог «применилось» получает ключ «панель всё ещё прежней» — так же, как в 1.x, и это
    /// не ошибка: ключ объясняет НЕУДАЧУ, а «applied» попадает сюда только в ветке «версия в папке
    /// новее нашей», то есть файлы заменились не той папки (или выпуск пересобран под тем же
    /// номером, а номер папки оставлен новым).
    /// </summary>
    public static string ReasonKeyFor(UpdateResultKind kind) => kind switch
    {
        UpdateResultKind.Applied => "UpdateReasonStillOld",
        UpdateResultKind.AppliedNoPanel => "UpdateReasonNoPanel",
        UpdateResultKind.RolledBack => "UpdateReasonRolledBack",
        UpdateResultKind.FailedRestore => "UpdateReasonRestoreFailed",
        UpdateResultKind.FailedNoBackup => "UpdateReasonNoBackup",
        UpdateResultKind.NotAppliedPanelRunning => "UpdateReasonPanelRunning",
        UpdateResultKind.NotAppliedMutex => "UpdateReasonMutexBusy",
        UpdateResultKind.MutexCheckFailed => "UpdateReasonMutexUnknown",
        _ => "UpdateReasonUnknown",
    };

    /// <summary>
    /// Что стало с последним обновлением. Панель зовёт это при старте: сценарий замены оставляет
    /// в папке обновления скачанную сборку (<c>update\&lt;версия&gt;</c>), журнал <c>update.log</c>
    /// и самого себя, а сам ничего не рассказывает.
    ///
    /// ⚠️ <paramref name="allowed"/> — ПРАВО прогона (<see cref="RunRights.LocalData"/>): чтение
    /// и уборка этого каталога касаются состояния панели, поэтому прогон проверки без права
    /// не трогает здесь НИЧЕГО и возвращает <c>null</c>.
    /// </summary>
    public static UpdateOutcome? StartupNotice(AppPaths paths, string currentVersion, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!allowed) return null;

        try
        {
            var root = UpdateInstall.UpdateRoot(paths);
            if (!Directory.Exists(root)) return null;

            var (version, folder) = NewestStaged(root);
            if (version.Length == 0) return null;

            var logFile = Path.Combine(root, UpdateScript.LogFileName);
            var marker = ReadMarker(ReadText(logFile));
            var kind = KindOf(marker);

            if (UpdateDecisions.IsNewer(version, currentVersion))
            {
                var kept = KeepFailed(root, folder, version);

                // Скачанный архив и файл сумм для разбора не нужны, а весят много (архив бывает
                // в сотни мегабайт). Сама папка failed-* и журнал остаются на месте.
                CleanupArchives(root);

                return new UpdateOutcome(
                    kind == UpdateResultKind.RolledBack ? UpdateVerdict.RolledBack : UpdateVerdict.NotApplied,
                    version,
                    kind,
                    marker,
                    ReasonKeyFor(kind),
                    kept,
                    "обновление не применилось, итог сценария: " + (marker.Length > 0 ? marker : "нет"));
            }

            CleanupStaged(root, folder);

            return new UpdateOutcome(
                UpdateVerdict.Applied,
                version,
                kind,
                marker,
                string.Empty,
                string.Empty,
                "замена состоялась" + (marker.Length > 0 ? ", итог сценария: " + marker : string.Empty)
                + "; если выпуск был пересобран под тем же номером, замены файлов могло и не быть");
        }
        catch (Exception error)
        {
            return new UpdateOutcome(
                UpdateVerdict.Unknown,
                string.Empty,
                UpdateResultKind.None,
                string.Empty,
                ReasonKeyFor(UpdateResultKind.Unknown),
                string.Empty,
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Самая свежая ПОДГОТОВЛЕННАЯ сборка в каталоге обновления. <c>backup-&lt;версия&gt;</c> —
    /// копия прежней панели для отката, <c>failed-&lt;версия&gt;</c> — уже рассказанное неудавшееся
    /// обновление; ни то, ни другое не «подготовленная сборка». Сборка без файла панели внутри
    /// тоже не считается: её не из чего поднимать.
    /// </summary>
    private static (string Version, string Folder) NewestStaged(string root)
    {
        var version = string.Empty;
        var folder = string.Empty;

        foreach (var candidate in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(candidate);

            if (name.StartsWith("backup-", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("failed-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(candidate, UpdateStaging.PanelExeName))) continue;

            var found = UpdateDecisions.Numeric(name);
            if (found.Length == 0) continue;

            if (version.Length == 0 || UpdateDecisions.IsNewer(found, version))
            {
                version = found;
                folder = candidate;
            }
        }

        return (version, folder);
    }

    /// <summary>
    /// Папка неудавшегося обновления остаётся для разбора и ручной починки, но под именем
    /// <c>failed-&lt;версия&gt;</c>: так следующий запуск видит в <c>update\</c> только
    /// подготовленные сборки, и сообщение о провале не повторяется при каждом запуске
    /// и после каждой перезагрузки. Держим ровно одну такую папку — прежние убираем,
    /// чтобы каталог не рос.
    /// </summary>
    private static string KeepFailed(string root, string folder, string version)
    {
        var kept = Path.Combine(root, "failed-" + version);

        try
        {
            foreach (var other in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(other);
                if (name.StartsWith("failed-", StringComparison.OrdinalIgnoreCase)) TryDeleteFolder(other);
            }

            if (Directory.Exists(folder)) Directory.Move(folder, kept);

            return kept;
        }
        catch
        {
            // Не переименовалась (папку держит кто-то другой) — сообщение просто повторится
            // в следующий раз. Это видно по Detail, а не повод соврать об итоге.
            return string.Empty;
        }
    }

    /// <summary>
    /// Убрать за обновившейся панелью: папку сборки, скачанные архивы, файл сумм, сценарий
    /// и журнал. Страховочную копию (<c>backup-&lt;версия&gt;</c>) НЕ трогаем: ею человек
    /// откатывается руками, и уборка не имеет права забрать единственный путь назад.
    /// </summary>
    private static void CleanupStaged(string root, string folder)
    {
        TryDeleteFolder(folder);
        CleanupArchives(root);
        TryDeleteFile(Path.Combine(root, UpdateScript.FileName));
        TryDeleteFile(Path.Combine(root, UpdateScript.LogFileName));
    }

    /// <summary>Скачанные архивы выпусков и файл сумм: после замены они не нужны.</summary>
    private static void CleanupArchives(string root)
    {
        try
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(UpdateAssets.SumsName, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch
        {
            // Не убралось — не беда: место освободит следующее обновление.
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // См. выше: уборка не имеет права уронить чтение итога.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // См. выше.
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            // Журнал не прочитать — о причине скажем «неизвестно», а не выдумаем её.
            return string.Empty;
        }
    }
}
