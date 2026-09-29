using System.Globalization;
using System.Text;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ПОЧЕМУ ОБНОВЛЕНИЕ НЕ ПОДГОТОВЛЕНО. Ответ называется перечислением, а не фразой: фразу
/// на языке панели собирает интерфейс по ключу (<see cref="UpdateRefusals.Key"/>), а движок
/// отдаёт данные. Иначе движок пришлось бы трогать при каждой правке перевода.
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

    /// <summary>Файл сумм пуст или не разобрался ни на одну строку.</summary>
    SumsUnreadable,

    /// <summary>Суммы для этого вложения в файле нет. Это ОТКАЗ, а не «пропустим».</summary>
    SumMissing,

    /// <summary>Сумма не сошлась: скачался не тот файл. Он не распаковывается.</summary>
    SumMismatch,

    /// <summary>Скачать вложение не удалось.</summary>
    DownloadFailed,

    /// <summary>Архив не читается, пуст или содержит записи мимо каталога распаковки.</summary>
    ArchiveUnreadable,

    /// <summary>В распакованном архиве нет собранной панели.</summary>
    NoExe,

    /// <summary>Версия внутри архива не совпала с версией выпуска.</summary>
    VersionMismatch,

    /// <summary>Страховочную копию прежней панели снять не удалось — откатываться будет некуда.</summary>
    NoBackup,

    /// <summary>Сценарий замены или шапку журнала записать не удалось.</summary>
    WriteFailed,
}

/// <summary>
/// КЛЮЧИ СТРОК ПО ОТКАЗУ. Здесь только имена: <c>PanelStrings</c> объявляет строку свойством
/// с этим именем, а словари дают три перевода. Второго списка фраз в движке не будет — иначе
/// он разошёлся бы со словарём на первой же правке.
/// </summary>
public static class UpdateRefusals
{
    /// <summary>
    /// Ключ строки для человека. Пусто — отказа нет. Строки, у которых в имени нет <c>Format</c>,
    /// показываются как есть; у остальных в текст подставляется <see cref="UpdatePreparation.Detail"/>.
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
        UpdateRefusal.SumsUnreadable => "UpdateSumsUnreadableFormat",
        UpdateRefusal.SumMissing => "UpdateSumMissingFormat",
        UpdateRefusal.SumMismatch => "UpdateSumMismatchFormat",
        UpdateRefusal.DownloadFailed => "UpdateDownloadFailedFormat",
        UpdateRefusal.ArchiveUnreadable => "UpdateArchiveUnreadableFormat",
        UpdateRefusal.NoExe => "UpdateNoExe",
        UpdateRefusal.VersionMismatch => "UpdateVersionMismatchFormat",
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
/// <see cref="Detail"/> — техническая причина (для журнала и отчёта): она называет, ЧТО именно
/// не сошлось — какой файл, какая версия, какая сумма. Человеческая фраза собирается из
/// <see cref="Refusal"/> и этой подробности.
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
    /// <summary>Ключ строки отказа ("" — отказа нет).</summary>
    public string RefusalKey => UpdateRefusals.Key(Refusal);

    /// <summary>Отказ без пути и без версии — так выглядят ответы до первого касания диска.</summary>
    public static UpdatePreparation Refuse(UpdateRefusal refusal, string detail) =>
        new(false, refusal, detail, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
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

        var client = download ?? new HttpUpdateDownload();
        var now = request.At ?? DateTimeOffset.Now;

        // За отказом убираем за собой: без этого в состоянии панели копились бы обрезанные
        // загрузки, скачанный архив и файл сумм от неудавшихся обновлений. Страховочную копию
        // не трогаем: её могло не быть вовсе, а если она есть — это готовый откат.
        UpdatePreparation Fail(UpdateRefusal refusal, string detail)
        {
            TryDeleteFolder(staged);
            TryDeleteFile(archivePath);
            TryDeleteFile(sumsPath);

            return UpdatePreparation.Refuse(refusal, detail);
        }

        string Fetch(string url, string path, UpdateStage stage, string name)
        {
            progress?.Invoke(new UpdateProgress(stage, name, 0, -1));

            try
            {
                client.Save(url, path, (done, total) => progress?.Invoke(new UpdateProgress(stage, name, done, total)));
                return string.Empty;
            }
            catch (Exception error)
            {
                return $"{error.GetType().Name}: {error.Message}";
            }
        }

        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            Directory.CreateDirectory(staged);

            // --- суммы ------------------------------------------------------------------------

            var failure = Fetch(request.Assets.SumsUrl, sumsPath, UpdateStage.Sums, UpdateAssets.SumsName);
            if (failure.Length > 0) return Fail(UpdateRefusal.DownloadFailed, "файл сумм: " + failure);

            var sums = UpdateSums.Parse(File.ReadAllLines(sumsPath));
            if (sums.Count == 0) return Fail(UpdateRefusal.SumsUnreadable, "файл сумм пуст или не разобран");

            // --- архив и его сумма ------------------------------------------------------------

            failure = Fetch(request.Assets.ArchiveUrl, archivePath, UpdateStage.Archive, UpdateAssets.ArchiveName);
            if (failure.Length > 0) return Fail(UpdateRefusal.DownloadFailed, "архив: " + failure);

            progress?.Invoke(new UpdateProgress(UpdateStage.Verify, UpdateAssets.ArchiveName, 0, -1));

            var verdict = UpdateSums.Verify(sums, UpdateAssets.ArchiveName, UpdateSums.Sha256(archivePath));

            if (verdict.Check == SumCheck.Missing)
                return Fail(UpdateRefusal.SumMissing, "в файле сумм нет строки для " + UpdateAssets.ArchiveName);

            if (verdict.Check == SumCheck.Mismatch)
                return Fail(UpdateRefusal.SumMismatch, $"ожидалось {verdict.Expected}, получено {verdict.Actual}");

            // --- распаковка и версия внутри ---------------------------------------------------

            progress?.Invoke(new UpdateProgress(UpdateStage.Unpack, archivePath, 0, -1));

            var unpack = UpdateStaging.Unpack(archivePath, staged);
            if (!unpack.Ok) return Fail(UpdateRefusal.ArchiveUnreadable, unpack.Detail);

            if (!File.Exists(Path.Combine(staged, UpdateStaging.PanelExeName)))
                return Fail(UpdateRefusal.NoExe, "в распакованном архиве нет " + UpdateStaging.PanelExeName);

            var inside = (versionOf ?? UpdateStaging.ReadVersion)(staged);
            if (!UpdateStaging.VersionMatches(inside, version))
                return Fail(UpdateRefusal.VersionMismatch, $"внутри «{inside}», ожидалось «{version}»");

            // --- установщик выпуска (если он есть) --------------------------------------------

            if (request.Assets.HasSetup)
            {
                var setupPath = Path.Combine(staged, UpdateAssets.SetupName);
                failure = Fetch(request.Assets.SetupUrl, setupPath, UpdateStage.Setup, UpdateAssets.SetupName);
                if (failure.Length > 0) return Fail(UpdateRefusal.DownloadFailed, "установщик: " + failure);

                var setup = UpdateSums.Verify(sums, UpdateAssets.SetupName, UpdateSums.Sha256(setupPath));

                if (!setup.Ok)
                {
                    return Fail(
                        setup.Check == SumCheck.Missing ? UpdateRefusal.SumMissing : UpdateRefusal.SumMismatch,
                        "установщик: " + (setup.Check == SumCheck.Missing
                            ? "в файле сумм нет строки для " + UpdateAssets.SetupName
                            : $"ожидалось {setup.Expected}, получено {setup.Actual}"));
                }
            }

            // --- страховочная копия -----------------------------------------------------------

            progress?.Invoke(new UpdateProgress(UpdateStage.Backup, backup, 0, -1));

            var copy = CopyCurrent(target, backup);
            if (copy.Length == 0)
                return Fail(UpdateRefusal.NoBackup, "страховочную копию прежней панели снять не удалось: " + target);

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
                commandLine);
        }
        catch (Exception error)
        {
            return Fail(UpdateRefusal.WriteFailed, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Страховочная копия прежней панели. **Копия без собранной панели внутри стирается**: это
    /// не «путь назад», а мусор, и оставить её значило бы предложить человеку откат в никуда.
    /// Путь назад нужен до записи сценария: без него сценарий не готовят вовсе.
    /// </summary>
    private static string CopyCurrent(string appDir, string backup)
    {
        try
        {
            if (!Directory.Exists(appDir)) return string.Empty;

            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Directory.CreateDirectory(backup);

            foreach (var file in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(appDir, file);
                var destination = Path.Combine(backup, relative);
                var directory = Path.GetDirectoryName(destination);

                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(file, destination, overwrite: true);
            }

            if (File.Exists(Path.Combine(backup, UpdateStaging.PanelExeName))) return backup;

            TryDeleteFolder(backup);
            return string.Empty;
        }
        catch
        {
            // Копию сделать не удалось — обновление не готовим: откатываться будет некуда,
            // и об этом человеку говорит ключ отказа.
            TryDeleteFolder(backup);
            return string.Empty;
        }
    }

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

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Не убралось — не повод падать: место освободит следующее обновление.
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
}
