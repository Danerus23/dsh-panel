using System.Globalization;
using System.Text;
using DshPanel.Files;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ПОЧЕМУ ОБНОВЛЕНИЕ НЕ ПОДГОТОВЛЕНО. Ответ называется перечислением, а не фразой: фразу
/// на языке панели собирает интерфейс по ключу (<see cref="UpdateRefusals.Key"/>), а движок
/// отдаёт данные. Иначе движок пришлось бы трогать при каждой правке перевода.
///
/// ⚠️ **ЗНАЧЕНИЕ НА КАЖДЫЙ РАЗЛИЧИМЫЙ СЛУЧАЙ, а не «на тему».** Здесь это правило оплачено
/// дефектом языка, найденным и названным 29.09.2026: пять разных причин распаковки и три разных
/// неудачи скачивания сходились в <c>ArchiveUnreadable</c> и <c>DownloadFailed</c>, и тогда
/// человеческие слова причины приходилось класть в <see cref="UpdatePreparation.Detail"/> —
/// а туда их клал движок, русскими литералами. На английском и китайском внутри английской
/// и китайской фразы стоял русский текст («Could not download the release file: файл сумм: …»).
///
/// Отсюда два правила, которые проверяются прогоном (<c>UpdateRefusalLanguageTests</c>):
///
/// 1. **у каждого значения — СВОЙ ключ** (иначе причина снова поедет в <c>Detail</c> словами);
/// 2. <see cref="UpdatePreparation.Detail"/> и <see cref="UpdatePreparation.DetailMore"/> несут
///    ЗНАЧЕНИЯ (имя файла, хеш, ожидаемое и полученное, счёт, причина от загрузчика или
///    исключения) — и ни одного слова, которое панель могла бы перевести.
/// </summary>
public enum UpdateRefusal
{
    /// <summary>Отказа нет: обновление подготовлено.</summary>
    None,

    /// <summary>У прогона нет права готовить замену файлов.</summary>
    NotAllowed,

    /// <summary>Выпуск не новее установленной панели — обновляться некуда.</summary>
    AlreadyLatest,

    /// <summary>Версию выпуска не удалось разобрать (пустой или непонятный тег).</summary>
    NoVersion,

    /// <summary>Не названа папка панели, которую нужно заменить.</summary>
    NoTarget,

    /// <summary>В выпуске нет архива панели.</summary>
    NoArchive,

    /// <summary>В выпуске нет файла контрольных сумм — без него обновление не готовится.</summary>
    NoSums,

    /// <summary>Файл контрольных сумм скачать не удалось. Значение — причина от загрузчика.</summary>
    SumsDownloadFailed,

    /// <summary>Файл сумм пуст или не разобрался ни на одну строку.</summary>
    SumsUnreadable,

    /// <summary>Архив панели скачать не удалось. Значение — причина от загрузчика.</summary>
    ArchiveDownloadFailed,

    /// <summary>Суммы для архива в файле нет. Значение — имя файла. Это ОТКАЗ, а не «пропустим».</summary>
    ArchiveSumMissing,

    /// <summary>Сумма архива не сошлась: скачался не тот файл. Значения — ожидаемое и полученное.</summary>
    ArchiveSumMismatch,

    /// <summary>Скачанного архива нет там, где его ждут (защита самой распаковки).</summary>
    ArchiveMissing,

    /// <summary>Не названа папка, в которую распаковывать архив (защита самой распаковки).</summary>
    UnpackFolderMissing,

    /// <summary>Архив не читается: открыть его или перечислить записи не удалось.</summary>
    ArchiveUnreadable,

    /// <summary>Архив читается, но файлов в нём нет вовсе.</summary>
    ArchiveEmpty,

    /// <summary>В архиве есть записи мимо каталога распаковки. Значение — сколько их.</summary>
    ArchiveUnsafe,

    /// <summary>Разобрать архив не удалось. Значение — причина от исключения.</summary>
    ArchiveBroken,

    /// <summary>В распакованном архиве нет собранной панели.</summary>
    NoExe,

    /// <summary>Версия внутри архива не совпала с версией выпуска. Значения — внутри и ожидалось.</summary>
    VersionMismatch,

    /// <summary>Установщик выпуска скачать не удалось. Значение — причина от загрузчика.</summary>
    SetupDownloadFailed,

    /// <summary>Суммы для установщика в файле нет. Значение — имя файла.</summary>
    SetupSumMissing,

    /// <summary>Сумма установщика не сошлась. Значения — ожидаемое и полученное.</summary>
    SetupSumMismatch,

    /// <summary>Страховочную копию прежней панели снять не удалось — откатываться будет некуда.</summary>
    NoBackup,

    /// <summary>Сценарий замены или шапку журнала записать не удалось. Значение — причина от исключения.</summary>
    WriteFailed,

    /// <summary>
    /// ПУТЬ ПАПКИ ОБНОВЛЕНИЯ ЗАНЯТ ФАЙЛОМ. Случай отдельный от «архив не разобран»: архив цел,
    /// а папку занять нечем. Значений нет — человеку и так всё сказано.
    ///
    /// ⚠️ Заведён 10.10.2026 по замечанию владельца: до него случай показывался ключом про
    /// разобранный архив и **нашей же английской фразой внутри русского сообщения**
    /// («the unpack folder path is occupied by a file») — предмет не тот, язык смешанный.
    /// Сам отказ как таковой сделан правильно (файл не сносится, вместо ложного успеха) —
    /// это не менялось.
    /// </summary>
    UnpackFolderOccupied,
}

/// <summary>
/// КЛЮЧИ СТРОК ПО ОТКАЗУ. Здесь только имена: <c>PanelStrings</c> объявляет строку свойством
/// с этим именем, а словари дают три перевода. Второго списка фраз в движке не будет — иначе
/// он разошёлся бы со словарём на первой же правке.
/// </summary>
public static class UpdateRefusals
{
    /// <summary>
    /// КЛЮЧ СТРОКИ ПРО НЕУДАВШУЮСЯ УБОРКУ. Он не привязан к значению <see cref="UpdateRefusal"/>:
    /// уборка не удаётся ПОСЛЕ ответа движка (и после удачного тоже), поэтому отказ уборки —
    /// отдельная строка рядом с ответом, а не ещё одно значение перечисления. Строку берут
    /// и страница отказа (<see cref="UpdateRefusalLines.Line"/>), и итог замены
    /// (<c>UpdateOutcomeLines</c>).
    /// </summary>
    public const string CleanupFailedKey = "UpdateCleanupFailedFormat";

    /// <summary>
    /// Ключ строки для человека. Пусто — отказа нет. Строки, у которых в имени нет <c>Format</c>,
    /// показываются как есть; у остальных в текст подставляются значения отказа
    /// (<see cref="UpdatePreparation.Detail"/> и <see cref="UpdatePreparation.DetailMore"/>).
    ///
    /// ⚠️ Ключи РАЗЛИЧИМЫ: двух значений с одним ключом здесь быть не должно. Один ключ на пять
    /// причин — это и есть дорога назад, к русской причине, подставленной в чужую фразу
    /// (дефект 29.09.2026); сторожит <c>UpdateRefusalLanguageTests</c>.
    /// </summary>
    public static string Key(UpdateRefusal refusal) => refusal switch
    {
        UpdateRefusal.None => string.Empty,
        UpdateRefusal.NotAllowed => "UpdateRefusedLocked",
        UpdateRefusal.AlreadyLatest => "UpdateAlreadyLatest",
        UpdateRefusal.NoVersion => "UpdateNoReleaseVersion",
        UpdateRefusal.NoTarget => "UpdateRefusedNoTarget",
        UpdateRefusal.NoArchive => "UpdateNoArchive",
        UpdateRefusal.NoSums => "UpdateNoSums",
        UpdateRefusal.SumsDownloadFailed => "UpdateSumsDownloadFailedFormat",
        UpdateRefusal.SumsUnreadable => "UpdateSumsUnreadable",
        UpdateRefusal.ArchiveDownloadFailed => "UpdateArchiveDownloadFailedFormat",
        UpdateRefusal.ArchiveSumMissing => "UpdateArchiveSumMissingFormat",
        UpdateRefusal.ArchiveSumMismatch => "UpdateArchiveSumMismatchFormat",
        UpdateRefusal.ArchiveMissing => "UpdateArchiveMissing",
        UpdateRefusal.UnpackFolderMissing => "UpdateUnpackFolderMissing",
        UpdateRefusal.ArchiveUnreadable => "UpdateArchiveUnreadable",
        UpdateRefusal.ArchiveEmpty => "UpdateArchiveEmpty",
        UpdateRefusal.ArchiveUnsafe => "UpdateArchiveUnsafeFormat",
        UpdateRefusal.ArchiveBroken => "UpdateArchiveBrokenFormat",
        UpdateRefusal.UnpackFolderOccupied => "UpdateUnpackFolderOccupied",
        UpdateRefusal.NoExe => "UpdateNoExe",
        UpdateRefusal.VersionMismatch => "UpdateVersionMismatchFormat",
        UpdateRefusal.SetupDownloadFailed => "UpdateSetupDownloadFailedFormat",
        UpdateRefusal.SetupSumMissing => "UpdateSetupSumMissingFormat",
        UpdateRefusal.SetupSumMismatch => "UpdateSetupSumMismatchFormat",
        UpdateRefusal.NoBackup => "UpdateNoBackup",
        UpdateRefusal.WriteFailed => "UpdateWriteFailedFormat",
        _ => "UpdateRefusedUnknown",
    };
}

/// <summary>
/// ЧТО ИМЕННО ПОДГОТОВИТЬ. Собрано записью, а не десятью аргументами, по той же причине, что
/// и <see cref="UpdateTraces"/>: у этих полей один смысл — «какое обновление готовим», — и
/// разойтись им нельзя. Состояние сервера и вид окна сюда не входят: замену файлов человек
/// запускает сам, и решает об этом он, а не движок.
/// </summary>
/// <param name="Release">Версия выпуска (тег GitHub как он пришёл) — имя папки и то, с чем сверяется архив.</param>
/// <param name="IgnoreNewer">
/// Готовить, даже если выпуск не новее установленной панели. Нужно ОДНОМУ случаю — самотесту
/// обновления, который гоняет тот же путь на выпуске той же версии. Ни одну проверку целостности
/// это не снимает: суммы, распаковка и версия внутри архива проверяются как обычно.
/// </param>
/// <param name="StartArgument">
/// С каким ключом поднять панель после замены (пусто — обычным запуском). Режим запуска решает
/// вызывающий, а не сценарий: панель, работавшая значком, не должна после обновления открыть окно
/// на весь экран.
/// </param>
/// <param name="At">Момент для шапки журнала. Пусто — системные часы (в проверках задаётся свой).</param>
public sealed record UpdateRequest(
    AppPaths Paths,
    string CurrentVersion,
    string TargetFolder,
    string Release,
    ReleaseAssets Assets,
    bool Allowed,
    bool IgnoreNewer = false,
    string StartArgument = "",
    string MutexName = "",
    DateTimeOffset? At = null);

/// <summary>
/// ЧТО ГОТОВО К ЗАМЕНЕ. Наружу отдаются данные и ПУТИ, а не готовый рассказ: командную строку
/// показывает интерфейс, а строки берёт по ключу отказа.
///
/// <see cref="Detail"/> и <see cref="DetailMore"/> — ЗНАЧЕНИЯ отказа (для подстановки в строку
/// человека и для журнала): имя файла, хеш, ожидаемое и полученное, счёт, причина от загрузчика
/// или исключения. Человеческих слов среди них нет: слова живут в трёх словарях, и фраза
/// собирается из ключа (<see cref="UpdateRefusal"/>) и этих значений. Так было не всегда —
/// до 29.09.2026 здесь лежал русский текст причины, и он же стоял посреди английской фразы.
/// </summary>
public sealed record UpdatePreparation(
    bool Ok,
    UpdateRefusal Refusal,
    string Detail,
    string Version,
    string StagedFolder,
    string BackupFolder,
    string TargetFolder,
    string ScriptPath,
    string LogPath,
    string CommandLine)
{
    /// <summary>
    /// ВТОРОЕ значение подстановки: строка вида «ожидалось {0}, получено {1}» берёт его отсюда.
    /// Пусто — и это нормальный ответ: у большинства отказов значение одно, а лишний довод
    /// <c>string.Format</c> пропускает.
    /// </summary>
    public string DetailMore { get; init; } = string.Empty;

    /// <summary>Ключ строки отказа ("" — отказа нет).</summary>
    public string RefusalKey => UpdateRefusals.Key(Refusal);

    /// <summary>
    /// ИТОГ УБОРКИ ЗА СОБОЙ: <c>Ok</c> — мусора не осталось. <see cref="TreeRemoval.Reason"/>
    /// называет причину словами среды.
    ///
    /// ⚠️ Это НЕ украшение и не «на всякий случай». Прежний вызов уборки игнорировал её итог,
    /// и папка, которую держал антивирус, оставалась на диске, а человек об этом не узнавал
    /// вовсе. Отказ уборки обязан быть виден словами: в журнал — всегда, а человеку — строкой
    /// <see cref="PanelStrings.UpdateCleanupFailedFormat"/> (см. <see cref="UpdateRefusalLines"/>
    /// и <c>UpdateOutcomeLines</c>).
    /// </summary>
    public TreeRemoval Cleanup { get; init; } = TreeRemoval.Nothing;

    /// <summary>Уборка не удалась: папка обновления осталась на диске и её придётся убрать руками.</summary>
    public bool CleanupFailed => !Cleanup.Ok;

    /// <summary>Ключ строки про неудавшуюся уборку ("" — уборка удалась).</summary>
    public string CleanupKey => Cleanup.Ok ? string.Empty : UpdateRefusals.CleanupFailedKey;

    /// <summary>Причина отказа уборки словами среды (для журнала; пусто — уборка удалась).</summary>
    public string CleanupReason => Cleanup.Ok ? string.Empty : Cleanup.Reason;

    /// <summary>Отказ без пути и без версии — так выглядят ответы до первого касания диска.</summary>
    public static UpdatePreparation Refuse(UpdateRefusal refusal, string detail, string more = "") =>
        new(false, refusal, detail, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)
        {
            DetailMore = more,
        };
}

/// <summary>
/// ДВИЖОК УСТАНОВКИ ОБНОВЛЕНИЯ — ВТОРОЙ СРЕЗ: скачать выпуск, проверить его, распаковать,
/// снять страховочную копию и СОЧИНИТЬ сценарий замены.
///
/// **Чего этот движок НЕ делает, и это не недоделка.**
///
/// 1. **Не заменяет файлы.** Замена идёт сценарием <c>update.cmd</c> ПОСЛЕ выхода панели: файлы
///    работающего exe заперты, а половина заменённой папки не запускается вовсе. Запускает
///    сценарий человек (или интерфейс по его решению) — ни один прогон проверки его не запускает.
/// 2. **Не ходит в сеть в проверках.** Живой путь отделён уговором <see cref="IUpdateDownload"/>:
///    проверки подставляют готовые байты, а загрузка делается только там, где у прогона есть право.
/// 3. **Не решает, обновляться ли.** «Есть ли выпуск новее», «не пропустил ли его человек» —
///    решения контроллера (<see cref="UpdateController"/>); сюда приходит готовый ответ.
///
/// **Право — то же, что у настроек и копий** (<see cref="RunRights.LocalData"/>): прогон проверки
/// без права не готовит замену вовсе, потому что подготовка читает папку панели и пишет в каталог
/// состояния. Право приходит ОБЯЗАТЕЛЬНЫМ полем запроса и берётся у <see cref="For"/>.
///
/// ⚠️ **За отказом движок убирает за собой** (папку сборки, скачанный архив, файл сумм), но НЕ
/// трогает страховочную копию: она и есть путь назад, и уборка не имеет права его забрать.
/// </summary>
public static class UpdateInstall
{
    /// <summary>Имя каталога обновления внутри состояния панели.</summary>
    public const string UpdateFolderName = "update";

    /// <summary>Право готовить замену файлов в этом прогоне — как у копий: изоляция или человек.</summary>
    public static bool For(RunContext context, bool humanLaunch) => RunRights.LocalData(context, humanLaunch);

    /// <summary>
    /// Каталог обновления: <c>&lt;состояние панели&gt;\update</c>. ОДНО место на панель — здесь
    /// его берут и подготовка, и чтение итога; два вычисления однажды разошлись бы, и панель
    /// читала бы итог не там, где оставила сборку.
    /// </summary>
    public static string UpdateRoot(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.StateDir, UpdateFolderName);
    }

    /// <summary>Имя скачанного архива внутри каталога обновления (нужно уборке и проверкам).</summary>
    public static string ArchiveFileName(string version) => "DshPanel-" + version + ".zip";

    /// <summary>
    /// ПОДГОТОВИТЬ ОБНОВЛЕНИЕ: скачать вложения, сверить суммы, распаковать, сверить версию,
    /// снять страховочную копию и написать сценарий замены с шапкой журнала.
    ///
    /// Порядок шагов — не украшение: **сумма сверяется ДО распаковки** (архив, не прошедший
    /// сверку, не распаковывается вовсе), **версия — ДО страховочной копии** (незачем копировать
    /// прежнюю панель ради архива, который всё равно не подойдёт), а **копия — ДО сценария**
    /// (сценарий без копии объявил бы обновление готовым, не имея пути назад).
    ///
    /// Отказы, не требующие диска (нет права, нет вложения, выпуск не новее), отвечают, НИЧЕГО
    /// не создав: каталог обновления после них не появляется.
    /// </summary>
    public static UpdatePreparation Prepare(
        UpdateRequest request,
        IUpdateDownload? download = null,
        Func<string, string>? versionOf = null,
        Action<UpdateProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var paths = request.Paths;
        var version = UpdateDecisions.Numeric(request.Release);
        var root = UpdateRoot(paths);

        var staged = Path.Combine(root, version);
        var archivePath = Path.Combine(root, ArchiveFileName(version));
        var sumsPath = Path.Combine(root, UpdateAssets.SumsName);
        var backup = Path.Combine(root, "backup-" + UpdateDecisions.Numeric(request.CurrentVersion));
        var logPath = Path.Combine(root, UpdateScript.LogFileName);
        var scriptPath = Path.Combine(root, UpdateScript.FileName);

        var target = UpdateScript.SafePath(request.TargetFolder);
        var stagedSafe = UpdateScript.SafePath(staged);
        var backupSafe = UpdateScript.SafePath(backup);
        var logSafe = UpdateScript.SafePath(logPath);

        // --- отказы, которые не касаются диска ------------------------------------------------

        if (!request.Allowed)
            return UpdatePreparation.Refuse(UpdateRefusal.NotAllowed, "у прогона нет права готовить замену файлов");

        if (version.Length == 0)
            return UpdatePreparation.Refuse(UpdateRefusal.NoVersion, "версия выпуска не разобрана: «" + (request.Release ?? string.Empty) + "»");

        if (!request.IgnoreNewer && !UpdateDecisions.IsNewer(request.Release, request.CurrentVersion))
            return UpdatePreparation.Refuse(UpdateRefusal.AlreadyLatest, "выпуск не новее установленной панели: " + UpdateDecisions.Numeric(request.CurrentVersion));

        if (!request.Assets.HasArchive)
            return UpdatePreparation.Refuse(UpdateRefusal.NoArchive, "в выпуске нет вложения " + UpdateAssets.ArchiveName);

        // Контрольные суммы обязательны: именно они доказывают, что скачался тот самый архив,
        // а не обрезанный загрузкой или подменённый файл. Хэш без подписи (сертификата у проекта
        // нет) — единственная доступная проверка, поэтому выпуск без SHA256SUMS.txt обновлять
        // нельзя, о чём и говорим человеку.
        if (!request.Assets.HasSums)
            return UpdatePreparation.Refuse(UpdateRefusal.NoSums, "в выпуске нет вложения " + UpdateAssets.SumsName);

        if (target.Length == 0)
            return UpdatePreparation.Refuse(UpdateRefusal.NoTarget, "не названа папка панели");

        // ⚠️ ЗАГРУЗЧИК ПОЛУЧАЕТ КАТАЛОГ ОБНОВЛЕНИЯ, и это не мелочь: в нём он держит запись
        // о незаконченной загрузке и журнал попыток (DEF-035). Панель, закрытую посреди
        // скачивания, следующий запуск продолжит с принятого байта, а не начнёт сорок
        // мегабайт заново. Подстановка проверок остаётся подстановкой — состояние ей не нужно.
        var client = download ?? new HttpUpdateDownload(stateDir: root);
        var now = request.At ?? DateTimeOffset.Now;

        // Итог уборки ПАПКИ СБОРКИ, снятой распаковкой. Объявлен до `Fail`, потому что отказ
        // обязан назвать и эту причину: папку, которую держит чужой процесс, распаковка снять
        // не смогла, и молчать об этом нельзя.
        var unpacked = TreeRemoval.Nothing;

        // За отказом убираем за собой: без этого в состоянии панели копились бы обрезанные
        // загрузки, скачанный архив и файл сумм от неудавшихся обновлений. Страховочную копию
        // не трогаем: её могло не быть вовсе, а если она есть — это готовый откат.
        //
        // ⚠️ ИТОГ УБОРКИ НЕ ГЛОТАЕТСЯ. Папка, которую держит антивирус или чужой процесс,
        // остаётся на диске, и раньше об этом не узнавал никто: вызов уборки стоял без разбора
        // ответа. Теперь отказ уборки едет наружу в `Cleanup` и называется человеку словами.
        UpdatePreparation Fail(UpdateRefusal refusal, string detail, string more = "")
        {
            var removal = Keep(unpacked, TreeDeletion.Remove(staged));

            TryDeleteFile(archivePath);
            TryDeleteFile(sumsPath);

            return UpdatePreparation.Refuse(refusal, detail, more) with { Cleanup = removal };
        }

        UpdateDownloadResult Fetch(string url, string path, string expectedSum, UpdateStage stage, string name)
        {
            progress?.Invoke(new UpdateProgress(stage, name, 0, -1));

            try
            {
                // ВИД ОТКАЗА ВОЗВРАЩАЕТСЯ ЦЕЛИКОМ, а не строкой: «скачался не тот файл» (сумма
                // не сошлась) и «скачать не удалось» — разные отказы с разными словами, и
                // превращать первый во второй значит сказать человеку неправду. Нашла приёмка
                // 10.10.2026: до этой правки несовпавшая сумма выглядела как отказ скачивания.
                return client.Save(
                    url,
                    path,
                    expectedSum,
                    (done, total) => progress?.Invoke(new UpdateProgress(stage, name, done, total)));
            }
            catch (Exception error)
            {
                // Подстановка вправе бросить — живой уговор этого не делает, и причина от неё
                // всё равно обязана стать ЗНАЧЕНИЕМ отказа, а не исключением наружу.
                return new UpdateDownloadResult(
                    UpdateDownloadOutcome.Interrupted, $"{error.GetType().Name}: {error.Message}");
            }
        }

        // Отказ загрузки → отказ подготовки: несовпавшая сумма называется СВОИМ ключом (у него
        // есть ожидаемое и полученное), остальное — ключом отказа скачивания.
        UpdatePreparation DownloadFailed(UpdateDownloadResult result, UpdateRefusal sumMismatch, UpdateRefusal download)
            => result.Outcome == UpdateDownloadOutcome.SumMismatch
                ? Fail(sumMismatch, result.Expected, result.Actual)
                : Fail(download, DownloadDetail(result));

        try
        {
            TreeDeletion.Remove(staged);
            Directory.CreateDirectory(staged);

            // --- суммы ------------------------------------------------------------------------

            // ⚠️ Файл контрольных сумм качается БЕЗ СУММЫ, и это ЗАКОННЫЙ случай, а не дыра
            // в проверке: взять сумму этого файла неоткуда — он сам источник сумм. Такой файл
            // тоже занимает рабочий путь только собранным ЦЕЛИКОМ (см. `IUpdateDownload`).
            var sumsResult = Fetch(
                request.Assets.SumsUrl, sumsPath, string.Empty, UpdateStage.Sums, UpdateAssets.SumsName);

            if (!sumsResult.Ok) return Fail(UpdateRefusal.SumsDownloadFailed, DownloadDetail(sumsResult));

            var sums = UpdateSums.Parse(File.ReadAllLines(sumsPath));
            if (sums.Count == 0) return Fail(UpdateRefusal.SumsUnreadable, string.Empty);

            // --- архив и его сумма ------------------------------------------------------------

            // Сумма ищется ДО загрузки: во-первых, без неё панель не станет занимать место
            // сорокамегабайтным файлом (в файле сумм нет строки — обновляться нечем), а
            // во-вторых, сумма уходит В САМУ ЗАГРУЗКУ, и та сверяет её до занятия рабочего пути.
            if (!sums.TryGetValue(UpdateAssets.ArchiveName, out var archiveSum))
                return Fail(UpdateRefusal.ArchiveSumMissing, UpdateAssets.ArchiveName);

            var archiveResult = Fetch(
                request.Assets.ArchiveUrl, archivePath, archiveSum, UpdateStage.Archive, UpdateAssets.ArchiveName);

            if (!archiveResult.Ok)
                return DownloadFailed(archiveResult, UpdateRefusal.ArchiveSumMismatch, UpdateRefusal.ArchiveDownloadFailed);

            progress?.Invoke(new UpdateProgress(UpdateStage.Verify, UpdateAssets.ArchiveName, 0, -1));

            var verdict = UpdateSums.Verify(sums, UpdateAssets.ArchiveName, UpdateSums.Sha256(archivePath));

            if (verdict.Check == SumCheck.Missing)
                return Fail(UpdateRefusal.ArchiveSumMissing, UpdateAssets.ArchiveName);

            if (verdict.Check == SumCheck.Mismatch)
                return Fail(UpdateRefusal.ArchiveSumMismatch, verdict.Expected, verdict.Actual);

            // --- распаковка и версия внутри ---------------------------------------------------

            progress?.Invoke(new UpdateProgress(UpdateStage.Unpack, archivePath, 0, -1));

            var unpack = UpdateStaging.Unpack(archivePath, staged);
            if (!unpack.Ok) return Fail(unpack.Refusal, unpack.Detail, unpack.More);

            // Распаковка снимает прежнее содержимое папки сборки сама: её итог — ИТОГ УБОРКИ,
            // и потерять его значит промолчать о папке, которую держит чужой процесс.
            unpacked = unpack.Cleanup;

            if (!File.Exists(Path.Combine(staged, UpdateStaging.PanelExeName)))
                return Fail(UpdateRefusal.NoExe, UpdateStaging.PanelExeName);

            var inside = (versionOf ?? UpdateStaging.ReadVersion)(staged);
            if (!UpdateStaging.VersionMatches(inside, version))
                return Fail(UpdateRefusal.VersionMismatch, inside, version);

            // --- установщик выпуска (если он есть) --------------------------------------------

            if (request.Assets.HasSetup)
            {
                if (!sums.TryGetValue(UpdateAssets.SetupName, out var setupSum))
                    return Fail(UpdateRefusal.SetupSumMissing, UpdateAssets.SetupName);

                var setupPath = Path.Combine(staged, UpdateAssets.SetupName);

                var setupResult = Fetch(
                    request.Assets.SetupUrl, setupPath, setupSum, UpdateStage.Setup, UpdateAssets.SetupName);

                if (!setupResult.Ok)
                    return DownloadFailed(setupResult, UpdateRefusal.SetupSumMismatch, UpdateRefusal.SetupDownloadFailed);

                var setup = UpdateSums.Verify(sums, UpdateAssets.SetupName, UpdateSums.Sha256(setupPath));

                if (!setup.Ok)
                {
                    return setup.Check == SumCheck.Missing
                        ? Fail(UpdateRefusal.SetupSumMissing, UpdateAssets.SetupName)
                        : Fail(UpdateRefusal.SetupSumMismatch, setup.Expected, setup.Actual);
                }
            }

            // --- страховочная копия -----------------------------------------------------------

            progress?.Invoke(new UpdateProgress(UpdateStage.Backup, backup, 0, -1));

            var copy = CopyCurrent(target, backup);

            // Отказ уборки СТАРОЙ копии — единственная причина, по которой человеку стоит
            // сказать о папке обновления. Если места нет, ответ остаётся отказом по существу,
            // и тогда о причине уборки говорит ветка отказа, а не эта.
            if (copy.Folder.Length == 0)
            {
                // Папка подготовки убирается И ЗДЕСЬ, через тот же `Fail(...)`, что и остальные
                // отказы: этот путь возвращает отказ НАПРЯМУЮ (он минует `Fail`), и без уборки
                // пустая папка обновления оставалась на диске — регрессия, найденная приёмкой
                // 10.10.2026 сравнением с прежним поведением. Причина уборки не теряется: она
                // едет в `Cleanup`, а причина от копии присоединяется к ней.
                var refusal = Fail(UpdateRefusal.NoBackup, target);

                return copy.Cleanup.Ok
                    ? refusal
                    : refusal with { Cleanup = Keep(refusal.Cleanup, copy.Cleanup) };
            }

            // --- сценарий и шапка журнала -----------------------------------------------------

            progress?.Invoke(new UpdateProgress(UpdateStage.Script, scriptPath, 0, -1));

            File.WriteAllText(scriptPath, UpdateScript.Text(version), new UTF8Encoding(false));
            File.WriteAllText(
                logPath,
                UpdateScript.LogHeader(target, stagedSafe, version, CleanSwitch(request.StartArgument), now),
                new UTF8Encoding(false));

            var commandLine =
                "\"\"" + UpdateScript.SafePath(scriptPath) + "\" \"" +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\" \"" +
                target + "\" \"" + stagedSafe + "\" \"" + backupSafe + "\" \"" + logSafe + "\" \"" +
                MutexName(request.MutexName) + "\" \"" + CleanSwitch(request.StartArgument) + "\"\"";

            return new UpdatePreparation(
                true,
                UpdateRefusal.None,
                string.Empty,
                version,
                stagedSafe,
                backupSafe,
                target,
                UpdateScript.SafePath(scriptPath),
                logSafe,
                commandLine)
            {
                Cleanup = Keep(unpacked, copy.Cleanup),
            };
        }
        catch (Exception error)
        {
            // ОБРАМЛЕНИЕ ПЕРЕВОДИТСЯ, ПРИЧИНА ОСТАЁТСЯ: слова «записать файлы замены не удалось»
            // живут в словаре (`UpdateWriteFailedFormat`), а сюда идёт причина от .NET — тип
            // исключения и его сообщение. Сообщение .NET на русской Windows русское, и это
            // единственное место, где русский текст может попасть в чужую фразу: заменить его
            // нечем — иначе причина исчезнет вовсе. Решение названо прямо, а не спрятано.
            return Fail(UpdateRefusal.WriteFailed, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// ЗНАЧЕНИЕ ПРИЧИНЫ, КОТОРОЕ ЧИТАЕТ ЧЕЛОВЕК, — из ответа ЗАГРУЗКИ. Здесь причина загрузки
    /// превращается в строку подстановки: <c>sum-mismatch: ожидалось …, получено …</c>,
    /// <c>interrupted: IOException: …</c>, <c>http-refused: HTTP 404</c>, <c>no-length: HTTP 200</c>.
    ///
    /// Случай остаётся РАЗЛИЧИМЫМ (это <see cref="UpdateDownloadResult.Outcome"/>, а не текст),
    /// а человеческая фраза берётся строкой по ключу отказа: второй набор подписей в движке
    /// разошёлся бы со словарём на первой же правке перевода.
    /// </summary>
    private static string DownloadDetail(UpdateDownloadResult result)
    {
        var report = result.Report();

        if (result.Outcome != UpdateDownloadOutcome.SumMismatch) return report;

        // У несовпавшей суммы значения — это ДВА ХЕША, и оба нужны человеку: по ним видно, что
        // скачался не тот файл, а не что «что-то пошло не так». Имя исключения сюда не попадает.
        return string.IsNullOrWhiteSpace(result.Expected) || string.IsNullOrWhiteSpace(result.Actual)
            ? report
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1} / {2}", report, result.Expected, result.Actual);
    }

    /// <summary>
    /// Страховочная копия прежней панели. **Копия без собранной панели внутри стирается**: это
    /// не «путь назад», а мусор, и оставить её значило бы предложить человеку откат в никуда.
    /// Путь назад нужен до записи сценария: без него сценарий не готовят вовсе.
    ///
    /// Итог уборки прежнего каталога копии возвращается наружу, а не глотается: папка, которую
    /// держит чужой процесс, остаётся на диске, и человеку об этом говорят словами.
    /// </summary>
    private static (string Folder, TreeRemoval Cleanup) CopyCurrent(string appDir, string backup)
    {
        // Прежняя копия сносится ДО создания новой: иначе файлы двух копий смешались бы,
        // и «путь назад» стал бы смесью двух версий панели.
        var removal = TreeDeletion.Remove(backup);

        try
        {
            if (!Directory.Exists(appDir)) return (string.Empty, removal);

            Directory.CreateDirectory(backup);

            foreach (var file in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(appDir, file);
                var destination = Path.Combine(backup, relative);
                var directory = Path.GetDirectoryName(destination);

                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(file, destination, overwrite: true);
            }

            if (File.Exists(Path.Combine(backup, UpdateStaging.PanelExeName))) return (backup, removal);

            return (string.Empty, Keep(removal, TreeDeletion.Remove(backup)));
        }
        catch
        {
            // Копию сделать не удалось — обновление не готовим: откатываться будет некуда,
            // и об этом человеку говорит ключ отказа.
            return (string.Empty, Keep(removal, TreeDeletion.Remove(backup)));
        }
    }

    /// <summary>
    /// Два итога уборки — ОДИН ответ: причину назвать важнее, чем «сколько чего снято».
    /// Пустое имя причины означает, что уборка удалась.
    /// </summary>
    private static TreeRemoval Keep(TreeRemoval first, TreeRemoval second) =>
        first.Ok && !second.Ok ? second : first;

    /// <summary>
    /// Имя замка одной копии для сценария. По умолчанию — имя панели
    /// (<see cref="InstanceSignal.DefaultMutexName"/>): сценарий обязан ждать ТОТ САМЫЙ замок,
    /// а не свой выдуманный. Кавычка из имени вырезается: она закрыла бы аргумент командной
    /// строки, и сценарий получил бы чужой замок.
    /// </summary>
    private static string MutexName(string? name)
    {
        var text = (name ?? string.Empty).Replace("\"", string.Empty).Trim();

        return text.Length > 0 ? text : InstanceSignal.DefaultMutexName;
    }

    /// <summary>
    /// Ключ запуска панели после замены. Пусто — обычный запуск окном. **Знаки, которыми cmd
    /// разделяет команды, вырезаются, и берётся ТОЛЬКО первый ключ:** значение уходит в тело
    /// сценария (<c>%START%</c>), и «&amp;» или «|» разобрали бы строку запуска на несколько
    /// команд. Ключ приходит из кода панели, но полагаться на это в сценарии, который остаётся
    /// на диске, нельзя.
    /// </summary>
    private static string CleanSwitch(string? value)
    {
        var text = value ?? string.Empty;

        foreach (var symbol in new[] { '"', '&', '|', '<', '>', '^', '%', '\r', '\n' })
        {
            text = text.Replace(symbol.ToString(), string.Empty);
        }

        var words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        return words.Length > 0 ? words[0] : string.Empty;
    }

    /// <summary>
    /// Уборка — ОДНИМ местом: <c>Files\TreeDeletion.cs</c>. Прежний вызов
    /// <c>Directory.Delete(path, recursive: true)</c> в пустом <c>catch</c> оставлял дерево
    /// на месте сразу в двух случаях — junction внутри дерева и файл «только для чтения» внутри
    /// него, — и отвечал на это успехом (замер 10.10.2026, разбор — в самом помощнике).
    /// </summary>
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
}
