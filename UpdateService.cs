using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DshTray;

/// <summary>Что нового на GitHub и нужно ли обновляться.</summary>
public sealed class UpdateCheck
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Current { get; set; } = AppVersion.Short;
    public string Latest { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string PageUrl { get; set; } = "";
    public string ZipUrl { get; set; } = "";
    public string SetupUrl { get; set; } = "";

    /// <summary>
    /// Имя сборки выпуска, которая подходит ЭТОЙ панели: DshPanel.zip для обычной сборки
    /// (рядом лежит DshTray.dll, нужна установленная среда .NET) либо
    /// DshPanel-selfcontained.zip для сборки одним файлом со средой внутри.
    /// </summary>
    public string AssetName { get; set; } = "";

    /// <summary>Наша панель собрана самодостаточной (один .exe, среда .NET внутри).</summary>
    public bool SelfContained { get; set; }

    /// <summary>Ссылка на файл контрольных сумм выпуска (SHA256SUMS.txt), если он опубликован.</summary>
    public string SumsUrl { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;

    /// <summary>На GitHub лежит версия новее нашей.</summary>
    public bool Newer { get; set; }

    /// <summary>Строка для окна: «на GitHub 1.21.0 от 21.09.2026» или «у вас последняя версия».</summary>
    public string Summary()
    {
        if (!Ok) return Loc.T("update.failed", Error);
        if (Latest.Length == 0) return Loc.T("update.noReleases");
        if (!Newer) return Loc.T("update.latest", Latest);
        return Loc.T("update.newer", Latest, PublishedAt.ToString("dd.MM.yyyy"));
    }
}

    /// <summary>Итог подготовки обновления: что скачано и чем заменить файлы.</summary>
public sealed class UpdateDownload
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Version { get; set; } = "";
    public string StagedFolder { get; set; } = "";
    public string BackupFolder { get; set; } = "";
    public string ScriptPath { get; set; } = "";

    /// <summary>Аргументы для cmd.exe: сценарий, пути, имя мьютекса и режим запуска. Пути идут
    /// аргументами, а не в теле сценария, потому что cmd читает .cmd в кодировке консоли и
    /// не-ASCII путь в теле ломается (см. WriteScript).</summary>
    public string CommandLine { get; set; } = "";
}

/// <summary>
/// Что стало с последним обновлением — панель выясняет это при старте, сверяя свою версию с
/// версией в папке обновления (см. <see cref="UpdateService.StartupNotice"/>). Раньше итог
/// замены файлов не читал ни один .cs: журнал сценария (update.log) оставался лежать в
/// состоянии панели, а человек видел «обновление готово» даже тогда, когда файлы не заменились.
/// </summary>
public sealed class UpdateOutcome
{
    /// <summary>Обновление НЕ применилось — об этом надо сказать человеку (см. Message).</summary>
    public bool Failed { get; set; }

    /// <summary>Версия, которая ждала в папке обновления.</summary>
    public string Version { get; set; } = "";

    /// <summary>Готовая строка для человека (пусто, если показывать нечего). Только через Loc.T.</summary>
    public string Message { get; set; } = "";

    /// <summary>Причина из журнала сценария — уже переведённая строка ("" — итога в журнале нет).</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// Обновление панели с GitHub: проверка выпусков, скачивание сборки и подготовка замены
/// файлов. Личные данные при этом не трогаются: настройки лежат в %APPDATA%, состояние —
/// в %LOCALAPPDATA%, а меняются только файлы самой панели.
///
/// Какая сборка выпуска нужна — решается по своей папке (<see cref="SelfContainedBuild"/>):
/// обычной панели — DshPanel.zip, панели одним файлом со средой внутри —
/// DshPanel-selfcontained.zip. Чужой вариант не подставляется никогда: одна сборка без
/// установленной среды .NET не запустится, вторая тянет её за собой.
///
/// Адрес репозитория берётся из <see cref="Repository"/>; переменная окружения
/// DSH_PANEL_REPO подменяет его для проверок.
/// </summary>
public static class UpdateService
{
    /// <summary>Репозиторий выпусков. Уточняется владельцем перед публикацией.</summary>
    public const string DefaultRepository = "Danerus23/dsh-panel";

    public static string Repository
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_REPO");
            return string.IsNullOrWhiteSpace(custom) ? DefaultRepository : custom.Trim();
        }
    }

    /// <summary>
    /// Адрес GitHub API. Переменная DSH_PANEL_API нужна проверкам: с ней можно поднять
    /// локальную заглушку и прогнать весь путь обновления (поиск выпуска, скачивание,
    /// сверку контрольных сумм, распаковку) без сети и без настоящего репозитория.
    /// </summary>
    public static string ApiBase
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_API");
            return string.IsNullOrWhiteSpace(custom) ? "https://api.github.com" : custom.Trim().TrimEnd('/');
        }
    }

    /// <summary>Обычная сборка выпуска: рядом с DshTray.exe лежит DshTray.dll, нужна среда .NET.</summary>
    public const string FrameworkAsset = "DshPanel.zip";

    /// <summary>Сборка «без .NET»: один DshTray.exe, среда Desktop Runtime внутри.</summary>
    public const string SelfContainedAsset = "DshPanel-selfcontained.zip";

    /// <summary>
    /// Вариант сборки этой панели. Решаем по своей папке: у обычной сборки рядом с DshTray.exe
    /// лежат DshTray.dll, DshTray.deps.json и DshTray.runtimeconfig.json, у сборки одним файлом
    /// со средой внутри — ничего из этого. Переменная DSH_PANEL_VARIANT (framework либо
    /// selfcontained) подменяет ответ: ею проверки прогоняют оба варианта на одной сборке,
    /// на сам путь обновления она больше ни на что не влияет.
    /// </summary>
    public static bool SelfContainedBuild
    {
        get
        {
            var forced = (Environment.GetEnvironmentVariable("DSH_PANEL_VARIANT") ?? "").Trim().ToLowerInvariant();
            if (forced is "framework" or "frameworkdependent") return false;
            if (forced is "selfcontained" or "self-contained" or "singlefile") return true;
            return IsSelfContainedBuild(AppContext.BaseDirectory);
        }
    }

    /// <summary>Имя сборки выпуска для варианта. Чужой вариант не подставляем никогда.</summary>
    public static string AssetNameFor(bool selfContained) => selfContained ? SelfContainedAsset : FrameworkAsset;

    /// <summary>
    /// Собрана ли папка одним exe со средой внутри. У обычной сборки .NET рядом с DshTray.exe
    /// есть DshTray.dll и служебные DshTray.deps.json с DshTray.runtimeconfig.json; сборка
    /// одним файлом их внутрь себя не кладёт — файлов нет, а панель всё равно запускается,
    /// значит среда внутри неё самой.
    /// </summary>
    internal static bool IsSelfContainedBuild(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

            foreach (var name in new[] { "DshTray.dll", "DshTray.deps.json", "DshTray.runtimeconfig.json" })
            {
                if (File.Exists(Path.Combine(directory, name))) return false;
            }

            return true;
        }
        catch
        {
            // Папку не посмотреть — считаем сборку обычной: её ставит установщик, и это
            // большинство. Проверка «чего в выпуске нет» ниже всё равно остановит подмену.
            return false;
        }
    }

    /// <summary>
    /// Имя мьютекса одной копии панели — то же, что создаёт Program.RunGui
    /// (Local\DshTray.SingleInstance; DSH_PANEL_INSTANCE меняет имя, этим пользуются проверки).
    /// Сценарий замены ждёт освобождения этого мьютекса: копия, поднятая слишком рано, видит
    /// мьютекс занятым и молча завершается — человек остаётся без панели и без объяснения.
    /// </summary>
    internal static string InstanceMutexName()
    {
        var custom = Environment.GetEnvironmentVariable("DSH_PANEL_INSTANCE");
        var instance = string.IsNullOrWhiteSpace(custom) ? @"Local\DshTray" : custom.Trim();
        return instance + ".SingleInstance";
    }

    /// <summary>
    /// Запущена ли панель скрыто (значок в трее без окна): так её поднимает автозапуск
    /// (Autostart пишет в Run «DshTray.exe --tray»). Этот режим передаётся сценарию замены,
    /// чтобы новая копия поднялась так же: раньше сценарий всегда запускал панель без
    /// аргументов, то есть окном, и человек, сидевший в трее, получал окно на весь экран.
    /// </summary>
    internal static bool StartedHidden()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Последний выпуск: тег, дата, заметки и ссылки на сборки.</summary>
    public static UpdateCheck Check()
    {
        var result = new UpdateCheck();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/" + AppVersion.Short);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var url = ApiBase + "/repos/" + Repository + "/releases/latest";
            using var response = client.GetAsync(url).GetAwaiter().GetResult();

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.Error = Loc.T("update.repoMissing");
                return result;
            }

            // 403 и 429 — это не «сломалось», а лимит GitHub на число запросов с адреса
            // (60 в час без токена): иначе человек видит просто «HTTP 403» и не понимает,
            // что делать. Подсказка про ручное скачивание и повтор позже.
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                || (int)response.StatusCode == 429)
            {
                result.Error = Loc.T("update.rateLimited");
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                result.Error = Loc.T("err.http", (int)response.StatusCode);
                return result;
            }

            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            var root = document.RootElement;

            result.Latest = Clean(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : "");
            result.Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            result.Notes = root.TryGetProperty("body", out var body) ? (body.GetString() ?? "") : "";
            result.PageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "";

            if (root.TryGetProperty("published_at", out var published)
                && DateTime.TryParse(published.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var when))
            {
                result.PublishedAt = when.ToLocalTime();
            }

            // Какой сборке выпуска мы соответствуем — решаем по своей папке, а не по имени
            // файла в выпуске. Самодостаточной панели обычная сборка не годится: она без среды
            // .NET внутри и на машине без установленной среды просто не запустится, а robocopy
            // отчитается об успехе. Наоборот — тоже: подменять вариант нельзя, о чём ниже.
            result.SelfContained = SelfContainedBuild;
            result.AssetName = AssetNameFor(result.SelfContained);

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var assetNameValue) ? assetNameValue.GetString() ?? "" : "";
                    var assetUrl = asset.TryGetProperty("browser_download_url", out var assetUrlValue) ? assetUrlValue.GetString() ?? "" : "";
                    // Берём ровно свою сборку: чужой вариант в ZipUrl не попадает.
                    if (assetName.Equals(result.AssetName, StringComparison.OrdinalIgnoreCase)) result.ZipUrl = assetUrl;
                    if (assetName.Equals("dsh-panel-setup.exe", StringComparison.OrdinalIgnoreCase)) result.SetupUrl = assetUrl;
                    if (assetName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) result.SumsUrl = assetUrl;
                }
            }

            result.Newer = IsNewer(result.Latest, result.Current);
            result.Ok = true;
        }
        catch (Exception error)
        {
            result.Error = error.Message;
        }

        return result;
    }

    /// <summary>
    /// Скачивает сборку выпуска, раскладывает её в папку обновления и готовит сценарий,
    /// который заменит файлы панели после её закрытия. Ничего не удаляем: прежняя папка
    /// копируется в backup-&lt;версия&gt;, чтобы обновление можно было откатить руками.
    /// </summary>
    public static UpdateDownload Prepare(UpdateCheck check, AppPaths paths, Action<string> progress = null,
        bool ignoreNewer = false, bool? startHidden = null)
    {
        var result = new UpdateDownload();
        var staged = "";
        var zipPath = "";
        var sumsPath = "";

        try
        {
            if (check == null || !check.Ok) throw new InvalidOperationException(check?.Error ?? Loc.T("update.notChecked"));

            // ignoreNewer — только для проверки самого пути обновления: она гоняет тот же код
            // на выпуске той же версии, что стоит. Ни одну проверку целостности это не снимает.
            if (!check.Newer && !ignoreNewer) throw new InvalidOperationException(Loc.T("update.alreadyLatest"));

            // Своя сборка выпуска. Вариант не подменяем: обычной сборке нужна установленная
            // среда .NET, а самодостаточной она не нужна вовсе — «похожая» сборка у человека
            // либо не запустится, либо потянет за собой установку среды. Если её в выпуске нет —
            // говорим прямо и останавливаемся.
            var assetName = check.AssetName.Length > 0 ? check.AssetName : AssetNameFor(SelfContainedBuild);
            if (check.ZipUrl.Length == 0) throw new InvalidOperationException(Loc.T("update.noVariantAsset", assetName));

            // Контрольные суммы обязательны: именно они доказывают, что скачался тот самый
            // архив, а не обрезанный загрузкой или подменённый файл. Хэш панели без своей
            // подписи (сертификата у проекта нет) — единственная доступная проверка, поэтому
            // выпуск без SHA256SUMS.txt обновлять нельзя, о чём и говорим человеку.
            if (check.SumsUrl.Length == 0) throw new InvalidOperationException(Loc.T("update.noSums"));

            var version = check.Latest;
            var updateRoot = Path.Combine(AppPaths.StateDir, "update");
            staged = Path.Combine(updateRoot, version);
            var backup = Path.Combine(updateRoot, "backup-" + AppVersion.Short);

            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);

            progress?.Invoke(Loc.T("update.downloadingSums"));
            sumsPath = Path.Combine(updateRoot, "SHA256SUMS.txt");
            Download(check.SumsUrl, sumsPath);
            var sums = ReadSums(sumsPath);

            progress?.Invoke(Loc.T("update.downloading", assetName));
            zipPath = Path.Combine(updateRoot, Path.GetFileNameWithoutExtension(assetName) + "-" + version + ".zip");
            var zipHash = Download(check.ZipUrl, zipPath);
            VerifyHash(assetName, zipHash, sums);

            progress?.Invoke(Loc.T("update.unpacking"));
            ZipFile.ExtractToDirectory(zipPath, staged, overwriteFiles: true);

            // Проверяем, что скачали именно ту версию: распаковывать чужой архив нельзя.
            var stagedExe = Path.Combine(staged, "DshTray.exe");
            if (!File.Exists(stagedExe)) throw new InvalidOperationException(Loc.T("update.noExe"));

            var stagedVersion = Clean(FileVersionInfo.GetVersionInfo(stagedExe).ProductVersion);
            if (!string.Equals(stagedVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Loc.T("update.versionMismatch", stagedVersion, version));
            }

            // Установщик тоже обновляем: он лежит рядом с панелью и уходит в полную копию.
            if (check.SetupUrl.Length > 0)
            {
                progress?.Invoke(Loc.T("update.downloading", "dsh-panel-setup.exe"));
                var setupHash = Download(check.SetupUrl, Path.Combine(staged, "dsh-panel-setup.exe"));
                VerifyHash("dsh-panel-setup.exe", setupHash, sums);
            }

            progress?.Invoke(Loc.T("update.staging"));

            // Копия прежней версии — не украшение: это единственный откат, который есть у
            // сценария. Раньше её неудача не мешала объявить обновление готовым, и человек
            // получал панель без пути назад. Теперь без копии обновление не готовим.
            result.BackupFolder = CopyCurrent(paths.BaseDir, backup);
            if (result.BackupFolder.Length == 0 || !File.Exists(Path.Combine(result.BackupFolder, "DshTray.exe")))
            {
                result.BackupFolder = "";
                throw new InvalidOperationException(Loc.T("update.noBackup", backup));
            }

            // Пути уходят в командный файл и в командную строку: хвостовой «\» там ломает
            // кавычки ("C:\...\DSH Panel\" — cmd съедает закрывающую), поэтому срезаем его.
            var target = SafePath(paths.BaseDir);
            var stagedSafe = SafePath(staged);
            var backupSafe = SafePath(backup);

            // Режим запуска: скрыто (значок в трее) или окном — чтобы новая копия поднялась
            // так же, как работала прежняя. Ключ --tray для скрытого запуска понимает Program.
            var hidden = startHidden ?? StartedHidden();

            // Путь к журналу выбирает сценарий, поэтому сначала пишем его, потом нормализуем.
            // Шапку журнала (версия, папка панели, папка сборки, режим) пишет сама панель:
            // путь с «&», «(», «)» или «%» в строке echo сценария рвёт строку на команды.
            result.ScriptPath = WriteScript(stagedSafe, backupSafe, updateRoot, InstanceMutexName(), hidden, out var logPath);
            var logSafe = SafePath(logPath);
            WriteLogHeader(logPath, target, stagedSafe, version, hidden);

            result.CommandLine = "\"\"" + result.ScriptPath + "\" \"" + Environment.ProcessId + "\" \"" +
                  target + "\" \"" + stagedSafe + "\" \"" + backupSafe + "\" \"" + logSafe + "\" \"" +
                  InstanceMutexName() + "\" \"" + (hidden ? "tray" : "window") + "\"\"";
            result.StagedFolder = staged;
            result.Version = version;
            result.Ok = true;
        }
        catch (Exception error)
        {
            result.Error = error.Message;

            // За собой убираем: без этого в %LOCALAPPDATA%\DshPanel\update копились бы
            // обрезанные загрузки, скачанный архив и файл сумм от неудавшихся обновлений.
            // Копию прежней версии (backup-*) не трогаем: её могло не быть вовсе, а если она
            // есть — это готовый откат, и удалять его на неудаче подготовки незачем.
            TryDeleteFolder(staged);
            TryDeleteFile(zipPath);
            TryDeleteFile(sumsPath);

            // Об этом стоит знать и в журнале панели: в окне человек видит строку ошибки,
            // а причина (нет места, файл занят) ищется уже по журналу.
            if (paths != null) AppLog.Write(paths, "обновление не подготовлено: " + error.Message);
        }

        return result;
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true);
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

    /// <summary>
    /// Путь без хвостового разделителя. Нужен там, где путь подставляется в командный файл
    /// в кавычках: «C:\…\DSH Panel\» ломает разбор — cmd считает «\"» экранированной
    /// кавычкой и передаёт robocopy остаток строки вместе с путём. Корень диска («C:\»)
    /// оставляем как есть.
    /// </summary>
    private static string SafePath(string path)
    {
        var trimmed = (path ?? "").TrimEnd('\\', '/');
        return trimmed.EndsWith(":") ? trimmed + "\\" : trimmed;
    }

    /// <summary>
    /// Сценарий замены файлов. Тело намеренно состоит только из ASCII, а все данные приходят
    /// аргументами (%~1…%~7): cmd.exe читает .cmd в кодировке консоли (на русской Windows —
    /// cp866), и не-ASCII путь, вписанный в тело, превращается в несуществующий — панель
    /// закрывалась, а обновление молча не применялось.
    ///
    /// Пути и имя мьютекса не попадают ни в текст команды PowerShell, ни в строки echo: у
    /// «powershell -Command "…" значение» хвост дописывается к самой команде (проверено: значение
    /// с «&» или «%» ломает разбор и даёт ложное «мьютекс занят»), а «&», «(», «)» и «%» внутри
    /// echo рвут строку на несколько команд. Поэтому значения уходят в PowerShell через
    /// окружение (set «DSH_UPD_…» перед вызовом), а пути пишет в журнал сама панель
    /// (<see cref="WriteLogHeader"/>), сценарий туда только дописывает.
    ///
    /// Порядок: дождаться выхода панели (по её PID, а не попыткой записи файла, иначе exe
    /// успевал замениться раньше dll), дождаться освобождения мьютекса одной копии,
    /// скопировать, проверить код robocopy (0..7 — успех, 8 и больше — ошибка), при ошибке
    /// вернуть файлы из копии, запустить панель в том же режиме, в каком она работала
    /// (%~7: tray — скрыто, как автозапуск; window — с окном), и проверить, что поднялась
    /// именно новая копия из папки панели.
    ///
    /// Панель поднимается только там, где файлы на месте: после удачной замены и после удачного
    /// отката. Если панель не вышла вовремя, мьютекс занят или его не удалось проверить, откат
    /// невозможен или не удался — файлы не трогаются (или не восстанавливаются) и ничего не
    /// запускается: полузаменённую папку поднимать нельзя, а лишняя копия — это второй значок
    /// в трее. В каждой ветке в журнал идёт строка «update result: …» (последняя) — по ней
    /// панель при следующем запуске рассказывает человеку, чем кончилось обновление
    /// (см. <see cref="StartupNotice"/>); код возврата сценария признаком успеха не является.
    /// </summary>
    private static string WriteScript(string staged, string backup, string updateRoot, string mutexName,
        bool startHidden, out string logPath)
    {
        var script = Path.Combine(updateRoot, "update.cmd");
        logPath = Path.Combine(updateRoot, "update.log");

        var lines = new[]
        {
            "@echo off",
            "rem DSH Panel self-update. ASCII only: cmd reads this file in the OEM code page.",
            "rem Args: %~1 pid, %~2 target, %~3 staged, %~4 backup, %~5 log, %~6 mutex, %~7 mode.",
            "rem The last two have defaults; the other five must come from the printed command.",
            "setlocal EnableExtensions",
            "set \"PID=%~1\"",
            "set \"TARGET=%~2\"",
            "set \"STAGED=%~3\"",
            "set \"BACKUP=%~4\"",
            "set \"LOG=%~5\"",
            "set \"MUTEX=%~6\"",
            "set \"MODE=%~7\"",
            "if \"%MUTEX%\"==\"\" set \"MUTEX=Local\\DshTray.SingleInstance\"",
            "if \"%MODE%\"==\"\" set \"MODE=window\"",
            "rem Without the paths there is nothing to do: say so instead of copying into nowhere.",
            "if \"%TARGET%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex mode & exit /b 1)",
            "if \"%STAGED%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex mode & exit /b 1)",
            "if \"%LOG%\"==\"\" (echo usage: update.cmd pid target staged backup log mutex mode & exit /b 1)",
            "rem The header (version, target, staged, mode) was written into the log by the panel:",
            "rem a path with &, ( ) or % inside a batch echo line breaks the line. Here we append.",
            "",
            "rem Wait for the panel to exit: about two minutes at most. Nothing is copied while",
            "rem it is alive: a half-replaced folder cannot be started. The pause is real one second",
            "rem (ping -n 2): ping -n 1 on loopback returns at once, and the wait would be seconds.",
            "for /L %%i in (1,1,120) do (",
            "  tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul || goto exited",
            "  ping -n 2 -w 1000 127.0.0.1 >nul",
            ")",
            "echo warning: the panel with pid %PID% did not exit in time >> \"%LOG%\"",
            "echo nothing was copied: replacing files of a running panel breaks it >> \"%LOG%\"",
            "echo update result: not-applied-panel-running >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":exited",
            "",
            "rem The single-instance mutex must be free before the new copy starts: a copy started",
            "rem too early sees the mutex taken and exits at once, leaving the person without a panel.",
            "rem The check itself may fail (no PowerShell, broken name) — that is not \"busy\", and the",
            "rem log must say what really happened. Exit codes: 0 free, 1 busy, 2 could not check.",
            "rem The wait is about a minute: 60 checks with a real one second pause between them.",
            "rem The name goes through the environment: inside the -Command text an apostrophe or an",
            "rem ampersand would break the command, and PowerShell would report nonsense about it.",
            "set \"DSH_UPD_MUTEX=%MUTEX%\"",
            "for /L %%i in (1,1,60) do (",
            "  powershell -NoProfile -ExecutionPolicy Bypass -Command \"$n = $env:DSH_UPD_MUTEX; $m = $null; try { $m = [System.Threading.Mutex]::OpenExisting($n); try { $got = $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $got = $true }; if ($got) { exit 0 } else { exit 1 } } catch [System.Threading.WaitHandleCannotBeOpenedException] { exit 0 } catch { [Console]::Error.WriteLine('mutex check failed: ' + $_.Exception.Message); exit 2 } finally { if ($m -ne $null) { $m.Dispose() } }\" 2>> \"%LOG%\"",
            "  if errorlevel 2 goto mutex-unknown",
            "  if not errorlevel 1 goto free",
            "  ping -n 2 -w 1000 127.0.0.1 >nul",
            ")",
            "echo warning: the single-instance mutex was still held after about a minute >> \"%LOG%\"",
            "echo nothing was copied: another panel instance is running >> \"%LOG%\"",
            "echo update result: not-applied-mutex >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":mutex-unknown",
            "rem The check did not work at all: do not copy (a live panel may be holding the mutex),",
            "rem but do not blame \"another instance\" either — the reason is written above.",
            "echo warning: the single-instance mutex could not be checked: see the line above >> \"%LOG%\"",
            "echo nothing was copied: replacing files of a possibly running panel breaks it >> \"%LOG%\"",
            "echo update result: mutex-check-failed >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":free",
            "robocopy \"%STAGED%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "set \"RC=%ERRORLEVEL%\"",
            "echo robocopy exit code: %RC% >> \"%LOG%\"",
            "if %RC% GEQ 8 goto rollback",
            "echo files replaced >> \"%LOG%\"",
            "goto start",
            "",
            ":rollback",
            "rem A rollback needs the whole previous version: a folder without DshTray.exe is not a",
            "rem safety copy, and restoring from it would mix versions in the panel folder.",
            "if not exist \"%BACKUP%\\DshTray.exe\" goto no-backup",
            "echo update failed, restoring the previous version >> \"%LOG%\"",
            "robocopy \"%BACKUP%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "if errorlevel 8 goto restore-failed",
            "echo update result: rolled-back >> \"%LOG%\"",
            "goto failed",
            "",
            ":restore-failed",
            "rem The copy is there, but putting it back did not work: start nothing (the panel folder",
            "rem may be half replaced) and keep both the copy and the staged build for manual repair.",
            "echo the rollback did not work: the previous version was NOT restored >> \"%LOG%\"",
            "echo nothing was started: the panel folder may be half replaced >> \"%LOG%\"",
            "echo the safety copy and the staged build were left in place >> \"%LOG%\"",
            "echo update result: failed-restore >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":no-backup",
            "rem Nothing to roll back to: say so instead of pretending the rollback worked, and start",
            "rem nothing. The staged build stays where it is for manual repair.",
            "echo no usable safety copy of the previous version: nothing to roll back to >> \"%LOG%\"",
            "echo nothing was started: the panel folder may be half replaced >> \"%LOG%\"",
            "echo the staged build was left in place >> \"%LOG%\"",
            "echo update result: failed-no-backup >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":failed",
            "rem Only reached after a rollback that worked: the previous version is in place again, so",
            "rem the panel may be started.",
            "echo the update was not applied, the previous version was restored >> \"%LOG%\"",
            "if not exist \"%TARGET%\\DshTray.exe\" goto failed-no-exe",
            "if /i \"%MODE%\"==\"tray\" (start \"\" \"%TARGET%\\DshTray.exe\" --tray) else (start \"\" \"%TARGET%\\DshTray.exe\")",
            "exit /b 1",
            "",
            ":failed-no-exe",
            "echo warning: there is no DshTray.exe in the target folder, nothing was started >> \"%LOG%\"",
            "exit /b 1",
            "",
            ":start",
            "if /i \"%MODE%\"==\"tray\" (start \"\" \"%TARGET%\\DshTray.exe\" --tray) else (start \"\" \"%TARGET%\\DshTray.exe\")",
            "",
            "rem Make sure the panel that came up is the one from the target folder. The path goes",
            "rem through the environment, and PowerShell compares the process path itself: &, ( ) and",
            "rem % in it are harmless here, unlike in find/findstr, which split such a path apart.",
            "set \"DSH_UPD_PROC=%TARGET%\\DshTray.exe\"",
            "powershell -NoProfile -Command \"$want = $env:DSH_UPD_PROC; for ($i = 0; $i -lt 20; $i++) { foreach ($p in @(Get-Process DshTray -ErrorAction SilentlyContinue)) { $path = ''; try { $path = [string]$p.Path } catch { $path = '' }; if ($path -ieq $want) { exit 0 } }; Start-Sleep -Seconds 1 }; exit 1\" 2>> \"%LOG%\"",
            "if not errorlevel 1 set \"SEEN=1\"",
            "if defined SEEN (",
            "  echo update result: applied >> \"%LOG%\"",
            "  echo the panel was restarted from the target folder >> \"%LOG%\"",
            ") else (",
            "  echo warning: files were replaced, but the panel from the target folder did not come up >> \"%LOG%\"",
            "  echo update result: applied-no-panel >> \"%LOG%\"",
            ")",
            "del \"%~f0\"",
        };

        File.WriteAllLines(script, lines, new System.Text.UTF8Encoding(false));
        return script;
    }

    /// <summary>
    /// Шапка журнала замены: версия, папка панели, папка сборки и режим запуска. Пишет её сама
    /// панель, а не сценарий: путь с «&», «(», «)» или «%» внутри строки echo разбирается cmd как
    /// несколько команд, и в журнал попадала бы обрезанная строка с ошибкой вместо пути. Здесь
    /// путь ложится в файл как есть, без участия cmd. Сценарий в этот журнал только дописывает.
    /// </summary>
    private static void WriteLogHeader(string logPath, string target, string staged, string version, bool hidden)
    {
        try
        {
            var header = new System.Text.StringBuilder();
            header.AppendLine("update started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            header.AppendLine("version: " + version);
            header.AppendLine("target: " + target);
            header.AppendLine("staged: " + staged);
            header.AppendLine("mode: " + (hidden ? "tray" : "window"));
            File.WriteAllText(logPath, header.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Журнал не записался — сценарий всё равно допишет в него свои строки; причину
            // отказа человек увидит по маркеру в журнале панели.
        }
    }

    /// <summary>Строка итога, которую сценарий замены пишет в update.log последней.</summary>
    internal const string ResultMarkerPrefix = "update result: ";

    /// <summary>
    /// Итог замены из журнала сценария: то, что стоит после «update result: » в последней
    /// такой строке. Пусто — сценарий до итога не дошёл (его не запускали, машину выключили).
    /// </summary>
    internal static string ResultMarker(string logText)
    {
        var marker = "";
        if (string.IsNullOrEmpty(logText)) return marker;

        foreach (var raw in logText.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(ResultMarkerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                marker = line[ResultMarkerPrefix.Length..].Trim();
            }
        }

        return marker;
    }

    /// <summary>
    /// Ключ Loc с причиной по итогу сценария — им объясняют человеку неудачу, поэтому вызывается
    /// только для ветки «версия в папке новее нашей». Маркер «applied» в этой ветке означает,
    /// что заменились файлы не той папки, из которой панель запущена (или выпуск пересобран под
    /// тем же номером, а номер папки оставлен новым).
    /// </summary>
    internal static string ReasonKeyFor(string marker)
    {
        return (marker ?? "").Trim().ToLowerInvariant() switch
        {
            "applied" => "update.reasonStillOld",
            "applied-no-panel" => "update.reasonNoPanel",
            "rolled-back" => "update.reasonRolledBack",
            "failed-restore" => "update.reasonRestoreFailed",
            "failed-no-backup" => "update.reasonNoBackup",
            "not-applied-panel-running" => "update.reasonPanelRunning",
            "not-applied-mutex" => "update.reasonMutexBusy",
            "mutex-check-failed" => "update.reasonMutexUnknown",
            _ => "update.reasonUnknown",
        };
    }

    /// <summary>
    /// Что стало с последним обновлением. Панель зовёт это при старте: сценарий замены оставляет
    /// в папке обновления скачанную сборку (update\&lt;версия&gt;) и журнал update.log, а сам
    /// ничего не рассказывает.
    ///
    /// Решение принимается по версии в имени папки, а не по маркеру сценария:
    ///
    /// * версия в папке СТРОГО новее нашей — обновление не применилось (замена не состоялась,
    ///   откат вернул прежнюю версию, файлы не скопировались). Об этом человеку говорят ОДИН раз,
    ///   после чего папка переименовывается в failed-&lt;версия&gt;: она остаётся для разбора и
    ///   ручной починки, но повторно о ней уже не сообщается — ни при следующем запуске, ни
    ///   после перезагрузки;
    /// * версия не новее — считаем, что замена состоялась, и убираем за собой (папку, скачанный
    ///   архив, файл сумм; backup-&lt;версия&gt; не трогаем). При этом в журнал панели честно
    ///   пишется, что при пересборке выпуска под тем же номером замены могло и не быть: код
    ///   robocopy 0..7 означает «нечего копировать» не реже, чем «скопировано». Человека этим
    ///   не тревожим — версия у него та же, что в выпуске.
    ///
    /// Возвращает null, если папки обновления нет или подготовленной сборки в ней не осталось.
    /// Всё, что нашлось, уходит в журнал панели (AppLog); строку для человека из Message
    /// показывать надо только при Failed = true.
    /// </summary>
    public static UpdateOutcome StartupNotice(AppPaths paths)
    {
        try
        {
            var root = Path.Combine(AppPaths.StateDir, "update");
            if (!Directory.Exists(root)) return null;

            var stagedVersion = "";
            var stagedFolder = "";
            foreach (var folder in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder);

                // backup-<версия> — копия прежней панели для отката, failed-<версия> — уже
                // рассказанное неудавшееся обновление. Ни то, ни другое не «подготовленная сборка».
                if (name.StartsWith("backup-", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("failed-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.Combine(folder, "DshTray.exe"))) continue;

                var version = Clean(name);
                if (version.Length == 0) continue;
                if (stagedVersion.Length == 0 || IsNewer(version, stagedVersion))
                {
                    stagedVersion = version;
                    stagedFolder = folder;
                }
            }

            if (stagedVersion.Length == 0) return null;

            var logPath = Path.Combine(root, "update.log");
            var logText = "";
            try
            {
                if (File.Exists(logPath)) logText = File.ReadAllText(logPath);
            }
            catch
            {
                // Журнал не прочитать — о причине скажем «неизвестно», а не выдумаем её.
            }

            var marker = ResultMarker(logText);
            var reason = Loc.T(ReasonKeyFor(marker));

            if (IsNewer(stagedVersion, AppVersion.Short))
            {
                if (paths != null)
                {
                    AppLog.Write(paths, "обновление " + stagedVersion + " не применилось: " + reason
                                       + "; итог сценария: " + (marker.Length > 0 ? marker : "нет"));
                }

                // Переименовываем папку: следы остаются, а повторного сообщения не будет.
                KeepFailedFolder(root, stagedFolder, stagedVersion, paths);

                return new UpdateOutcome
                {
                    Failed = true,
                    Version = stagedVersion,
                    Reason = reason,
                    Message = Loc.T("update.notApplied", stagedVersion, reason),
                };
            }

            // Версия в папке обновления не новее нашей: держать сборку незачем. Но «не новее» —
            // это ещё не доказательство замены: тот же номер мог быть пересобран и выпущен снова,
            // и тогда robocopy не скопировал ничего (код 0..7). Пишем это в журнал честно.
            if (paths != null)
            {
                AppLog.Write(paths, "обновление " + stagedVersion + " применилось"
                                   + (marker.Length > 0 ? " (итог сценария: " + marker + ")" : "")
                                   + "; если выпуск был пересобран под тем же номером, замены файлов могло и не быть");
            }

            CleanupUpdateFolder(root, stagedFolder);
            return new UpdateOutcome { Failed = false, Version = stagedVersion };
        }
        catch (Exception error)
        {
            if (paths != null) AppLog.Write(paths, "состояние обновления прочитать не удалось: " + error.Message);
            return null;
        }
    }

    /// <summary>
    /// Папка неудавшегося обновления остаётся для разбора и ручной починки, но под именем
    /// failed-&lt;версия&gt;: так следующая проверка видит в update\ только подготовленные сборки,
    /// и сообщение о провале не повторяется при каждом запуске и после каждой перезагрузки.
    /// Держим ровно одну такую папку — прежние убираем, чтобы каталог не рос.
    /// </summary>
    private static void KeepFailedFolder(string root, string stagedFolder, string version, AppPaths paths)
    {
        var kept = Path.Combine(root, "failed-" + version);

        try
        {
            foreach (var other in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(other);
                if (name.StartsWith("failed-", StringComparison.OrdinalIgnoreCase)) TryDeleteFolder(other);
            }

            if (Directory.Exists(stagedFolder)) Directory.Move(stagedFolder, kept);
        }
        catch (Exception error)
        {
            // Не переименовалась (папку держит кто-то другой) — сообщение просто повторится
            // в следующий раз; это видно в журнале.
            if (paths != null) AppLog.Write(paths, "папку обновления " + stagedFolder + " переименовать не удалось: " + error.Message);
        }

        // Скачанный архив и файл сумм для разбора и починки не нужны, а весят много (архив
        // «без .NET» — сотни мегабайт). Саму папку failed-* и журнал оставляем на месте.
        CleanupArchives(root);
    }

    /// <summary>
    /// Убирает за обновившейся панелью скачанную сборку (её папку), архивы и файл сумм.
    /// Копию прежней версии (backup-&lt;версия&gt;) не трогаем: ею человек откатывается руками.
    /// </summary>
    private static void CleanupUpdateFolder(string root, string stagedFolder)
    {
        TryDeleteFolder(stagedFolder);
        CleanupArchives(root);
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
                    || name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Копия текущей панели перед заменой: чтобы обновление можно было откатить.</summary>
    private static string CopyCurrent(string appDir, string backup)
    {
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(backup);

            foreach (var file in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(appDir, file);
                var target = Path.Combine(backup, relative);
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(file, target, overwrite: true);
            }

            return backup;
        }
        catch
        {
            // Копию сделать не удалось — обновление не готовим: откатываться будет некуда
            // (об этом Prepare говорит человеку через update.noBackup).
            return "";
        }
    }

    /// <summary>Скачивает файл и возвращает его SHA-256 (в нижнем регистре, без дефисов).</summary>
    private static string Download(string url, string path)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/" + AppVersion.Short);

        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var source = response.Content.ReadAsStream();
        using var hash = System.Security.Cryptography.SHA256.Create();
        using var target = File.Create(path);

        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            // Считаем хэш по дороге: второй раз читать файл с диска незачем.
            hash.TransformBlock(buffer, 0, read, null, 0);
            target.Write(buffer, 0, read);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(hash.Hash).ToLowerInvariant();
    }

    /// <summary>
    /// Разбирает строки файла контрольных сумм: «хэш  имя файла» (как у sha256sum), плюс
    /// терпимость к обратному слэшу, звёздочке перед именем, комментариям и лишним пробелам.
    /// Возвращает имя → хэш. Отдельно от файла — чтобы это проверялось в --selftest
    /// без сети и временных файлов.
    /// </summary>
    internal static Dictionary<string, string> ParseSums(IEnumerable<string> lines)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            // BOM в начале файла (его легко получить руками) не должен ломать первый хэш.
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var hash = parts[0].Trim().ToLowerInvariant();
            if (hash.Length != 64) continue;

            // Имя может быть помечено звёздочкой («*DshPanel.zip») или содержать путь.
            var name = parts[^1].TrimStart('*').Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            sums[name] = hash;
        }

        return sums;
    }

    private static Dictionary<string, string> ReadSums(string path)
    {
        var sums = ParseSums(File.ReadAllLines(path));
        if (sums.Count == 0) throw new InvalidOperationException(Loc.T("update.sumsUnreadable"));
        return sums;
    }

    private static void VerifyHash(string name, string actual, Dictionary<string, string> sums)
    {
        if (!sums.TryGetValue(name, out var expected))
        {
            throw new InvalidOperationException(Loc.T("update.hashMissing", name));
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Loc.T("update.hashMismatch", name, actual, expected));
        }
    }

    /// <summary>
    /// Текст заметок к выпуску на языке интерфейса. Тело релиза несёт три блока с машинными
    /// маркерами («&lt;!-- dsh-notes:en --&gt;» … «&lt;!-- /dsh-notes:en --&gt;»): английский,
    /// русский и китайский (см. tools\release-notes.ps1). Правила: блок по языку; нет его — английский;
    /// маркеров нет вовсе (выпуски до 1.21.0) — тело целиком, как показывала панель раньше.
    /// Кривой или незакрытый маркер — тоже тело целиком: пустое окно человеку хуже, чем лишние
    /// строки. Метки внутри ограждённых блоков (``` и ~~~) за маркеры не считаются.
    /// </summary>
    public static string PickNotes(string body, string language)
    {
        if (string.IsNullOrEmpty(body)) return body ?? "";

        var blocks = NotesBlocks(body);
        if (blocks.Count == 0) return body;

        var wanted = (language ?? "").Trim().ToLowerInvariant();
        if (wanted.Length > 0 && blocks.TryGetValue(wanted, out var own) && own.Length > 0) return own;
        if (blocks.TryGetValue("en", out var english) && english.Length > 0) return english;

        // Маркеры есть, а понятного текста в них нет: показываем тело как есть.
        return body;
    }

    /// <summary>
    /// Сырое тело релиза, каким его стоит запомнить до показа. В настройках лежит именно
    /// оно, а не выбранный блок: язык интерфейса меняется, и заметки обязаны пересобраться
    /// в окне (см. SettingsForm.FillUpdates), а не ждать следующей проверки обновлений
    /// (она бывает не чаще раза в сутки).
    ///
    /// Предел здесь — про размер settings.json, и он заметно больше предела показа: тело
    /// несёт три языка сразу. Тело, помещающееся в предел, хранится как пришло. Тело длиннее
    /// СНАЧАЛА разбирается на блоки, и в настройки едут только сами блоки: обрезка «как
    /// получилось» рвала бы маркер, у разбора осталось бы ноль блоков, и
    /// <see cref="PickNotes"/> отдал бы всё тело — на русском интерфейсе английское.
    /// Блок входит целиком, если влезает; иначе его содержимое режется по безопасной границе
    /// (<see cref="CutBlock"/>); если резать нечего — блок отбрасывается. Порядок блоков
    /// задаёт приоритет: русский как источник истины выживает первым. Размер считается по
    /// настоящей длине маркеров, а не по оценке, поэтому предел держится и на телах с
    /// длинными именами блоков. Текст вне блоков ничего не теряет: когда блоки найдены,
    /// PickNotes тело не отдаёт вообще.
    ///
    /// Предел показа — отдельное правило, оно живёт в <see cref="PickNotesForPanel"/>.
    /// </summary>
    public static string RawNotes(string body)
    {
        var text = body ?? "";
        if (text.Length <= NotesBodyLimit) return text;

        var blocks = NotesBlocks(text);
        if (blocks.Count == 0) return CutNotes(text, NotesBodyLimit);

        var order = NotesOrder(blocks);

        // «Обвязка» блока — маркеры с двух сторон и пустая строка между блоками — считается по
        // настоящей строке маркеров: длинное имя блока иначе съедало бы предел, и он переставал
        // быть пределом. Блок, на который не остаётся даже места под маркеры, отбрасывается.
        var busy = 0;
        var chosen = new List<string>();
        foreach (var language in order)
        {
            // Пустой блок для показа неотличим от отсутствующего: PickNotes такие пропускает.
            if (blocks[language].Length == 0) continue;

            var cost = NotesBlock(language, "").Length + (chosen.Count == 0 ? 0 : NotesGap.Length);
            if (busy + cost > NotesBodyLimit) continue;

            busy += cost;
            chosen.Add(language);
        }

        if (chosen.Count == 0) return "";

        // Доли содержимого: сперва целиком влезающие блоки (по приоритету), затем остаток
        // поровну между теми, кому целиком не хватило. Так короткий перевод ничего не теряет
        // и не отнимает место у длинного, а у каждого языка остаётся своя часть.
        var pool = NotesBodyLimit - busy;
        var limits = new int[chosen.Count];
        var whole = new bool[chosen.Count];
        var open = 0;
        for (var index = 0; index < chosen.Count; index++)
        {
            var content = blocks[chosen[index]];
            if (content.Length > pool)
            {
                open++;
                continue;
            }

            limits[index] = content.Length;
            whole[index] = true;
            pool -= content.Length;
        }

        if (open > 0)
        {
            var share = pool / open;
            for (var index = 0; index < chosen.Count; index++)
            {
                if (!whole[index]) limits[index] = share;
            }
        }

        // Сборка с проверкой: NotesBlocks узнаёт маркер только вне ограждения, поэтому мало
        // сложить блоки обратно — собранное обязано разбираться заново ровно так, как задумано.
        // Неподтвердившийся блок отбрасываем: пустое значение честнее тела не на своём языке.
        var stored = "";
        for (var index = 0; index < chosen.Count; index++)
        {
            var content = CutBlock(blocks[chosen[index]], limits[index]);
            if (content.Length == 0) continue;

            var block = NotesBlock(chosen[index], content);
            var trial = stored.Length == 0 ? block : stored + NotesGap + block;
            if (!NotesReads(trial, chosen[index], content)) continue;

            stored = trial;
        }

        return stored;
    }

    /// <summary>
    /// Порядок блоков при пересборке длинного тела: сперва русский (источник истины — он
    /// выживает первым), затем языки панели, затем остальные по алфавиту. На выбор языка при
    /// показе порядок не влияет: он нужен только для приоритета и повторяемости settings.json.
    /// </summary>
    private static List<string> NotesOrder(Dictionary<string, string> blocks)
    {
        var order = new List<string>();
        foreach (var known in new[] { "ru", "en", "zh" })
        {
            if (blocks.ContainsKey(known)) order.Add(known);
        }

        var rest = new List<string>();
        foreach (var key in blocks.Keys)
        {
            if (!order.Exists(taken => string.Equals(taken, key, StringComparison.OrdinalIgnoreCase))) rest.Add(key);
        }
        rest.Sort(StringComparer.OrdinalIgnoreCase);
        order.AddRange(rest);
        return order;
    }

    /// <summary>
    /// Содержимое блока, урезанное по пределу так, чтобы его снова разобрал
    /// <see cref="NotesBlocks"/>: ограждение ``` или ~~~ не разрезано пополам (разрезанное
    /// уводит закрывающий маркер «в код», у разбора получается ноль блоков, и на русском
    /// интерфейсе показывается английское тело), комментарий «&lt;!--» не оборван, суррогатная
    /// пара UTF-16 цела. Резать нечего — вернётся пустая строка, и блок отбрасывается.
    /// </summary>
    private static string CutBlock(string content, int limit)
    {
        if (limit <= 0) return "";

        // Безопасное место ищем и для содержимого, которое в предел влезает целиком: оно могло
        // остаться с незакрытым ограждением (NotesBlocks собирает текст из строк и потом
        // обрезает пробелы краёв — отступ первой строки от этого меняется).
        var cut = SafeCut(content, Math.Min(limit, content.Length), fences: true);
        return cut > 0 ? content[..cut].TrimEnd() : "";
    }

    /// <summary>
    /// Обрезка заметок, в которых меток нет (так выглядят выпуски до 1.21.0): они показываются
    /// целиком, но и такое тело нельзя рвать посередине суррогатной пары или открытого
    /// комментария «&lt;!--». Ограждения здесь не учитываются: разбирать на блоки нечего.
    /// </summary>
    private static string CutNotes(string text, int limit)
    {
        if (limit <= 0) return "";
        if (text.Length <= limit) return text;

        var cut = SafeCut(text, limit, fences: false);
        return cut > 0 ? text[..cut].TrimEnd() : "";
    }

    /// <summary>
    /// Ближайшее к пределу место, на котором текст можно оборвать: не разрезав суррогатную
    /// пару UTF-16 (разрезанная уезжает в settings.json как «\uFFFD»), не оставив открытый
    /// комментарий «&lt;!-- … --&gt;» и — когда <paramref name="fences"/> — не оставив
    /// незакрытое ограждение ``` или ~~~. Ноль значит «безопасного места нет».
    /// </summary>
    private static int SafeCut(string text, int limit, bool fences)
    {
        var cut = Math.Min(limit, text.Length);
        while (cut > 0)
        {
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;  // низ пары остался за пределом — режем до неё
                continue;
            }

            if (CommentOpen(text, cut))
            {
                var open = text.LastIndexOf("<!--", cut - 1, StringComparison.Ordinal);
                cut = open > 0 ? open : 0;
                continue;
            }

            if (fences && FenceOpen(text, cut, out var openedAt))
            {
                cut = openedAt > 0 ? openedAt : 0;   // последнее непарное открытие отбрасываем
                continue;
            }

            break;
        }

        return cut;
    }

    /// <summary>
    /// Открыт ли в префиксе комментарий «&lt;!--» без закрывающего «--&gt;». Считается по
    /// последнему открытию и последнему закрытию: комментарий может тянуться через строки,
    /// а префикс может обрываться внутри самой метки.
    /// </summary>
    private static bool CommentOpen(string text, int length)
    {
        if (length <= 0) return false;

        var open = text.LastIndexOf("<!--", length - 1, StringComparison.Ordinal);
        if (open < 0) return false;
        if (open + 4 > length) return true;

        var close = text.LastIndexOf("-->", length - 1, StringComparison.Ordinal);
        if (close < 0 || close + 3 > length) return true;

        return close < open;
    }

    /// <summary>
    /// Открыто ли ограждение (``` или ~~~) к позиции <paramref name="length"/> и где оно
    /// началось. Строка-ограждение читается только целиком: незавершённая последняя строка
    /// нового ограждения не открывает, но уже открытое не закрывает — так же читает её и
    /// Markdown, и <see cref="NotesBlocks"/>.
    /// </summary>
    private static bool FenceOpen(string text, int length, out int openedAt)
    {
        openedAt = -1;
        var fence = ' ';
        var fenceLength = 0;
        var index = 0;

        while (index < length)
        {
            var newline = text.IndexOf('\n', index);
            var end = newline < 0 || newline > length ? length : newline;
            var line = text[index..end];

            if (fence != ' ')
            {
                if (IsFenceEnd(line, fence, fenceLength))
                {
                    fence = ' ';
                    fenceLength = 0;
                }
            }
            else if (IsFenceStart(line, out var marker, out var run))
            {
                fence = marker;
                fenceLength = run;
                openedAt = index;
            }

            if (end >= length) break;
            index = end + 1;
        }

        return fence != ' ';
    }

    /// <summary>Один блок в том виде, в каком он ложится в настройки: маркеры и текст между ними.</summary>
    private static string NotesBlock(string language, string content) =>
        "<!-- dsh-notes:" + language + " -->\n" + content + "\n<!-- /dsh-notes:" + language + " -->";

    /// <summary>Пустая строка между блоками в настройках.</summary>
    private const string NotesGap = "\n\n";

    /// <summary>
    /// Читается ли собранный текст так, как задумано: нужный блок на месте и с тем же текстом.
    /// Проверка настоящая, а не «мы же его сами собрали»: NotesBlocks видит маркер только вне
    /// ограждения, и разъехавшееся ограждение увело бы маркер в код.
    /// </summary>
    private static bool NotesReads(string text, string language, string content)
    {
        var blocks = NotesBlocks(text);
        return blocks.TryGetValue(language, out var parsed) &&
               string.Equals(parsed, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Тот же выбор языка, но для окна панели: текст обрезается по прежнему пределу в 8000
    /// знаков, чтобы заметки не раздували settings.json. Обрезка идёт ПОСЛЕ выбора блока —
    /// иначе длинный английский блок вытеснил бы короткий блок другого языка.
    /// </summary>
    public static string PickNotesForPanel(string body, string language)
    {
        var notes = PickNotes(body, language);
        return notes.Length > NotesLimit ? notes[..NotesLimit] : notes;
    }

    /// <summary>Сколько знаков заметок согласна хранить панель в settings.json.</summary>
    private const int NotesLimit = 8000;

    /// <summary>Сколько знаков СЫРОГО тела релиза панель согласна хранить в settings.json.</summary>
    private const int NotesBodyLimit = 20000;

    /// <summary>
    /// Разбирает тело релиза на блоки по маркерам. Маркер обязан стоять один на всей строке
    /// (так его пишет release-notes.ps1): иначе слова о маркерах внутри самих заметок сбивали бы
    /// разбор. Незакрытая пара в словарь не попадает, повторный маркер ничего не переписывает.
    ///
    /// ОГРАЖДЁННЫЕ БЛОКИ (``` и ~~~) разбор не видит. Тело — недоверенный текст: он приходит
    /// с GitHub и его можно поправить руками на странице выпуска. История, которая рассказывает
    /// про формат заметок и показывает пример метки в ограждённом блоке, иначе объявила бы
    /// пример настоящим блоком — и настоящий перевод был бы отброшен.
    /// </summary>
    private static Dictionary<string, string> NotesBlocks(string body)
    {
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var open = "";
        var content = new List<string>();

        // Ограждение: ``` или ~~~ (не меньше трёх), в том числе с языком после них.
        var fence = ' ';
        var fenceLength = 0;

        foreach (var line in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();

            if (fence != ' ')
            {
                // Внутри ограждённого блока. Меткой не считается НИ ОДНА строка блока — ни
                // закрывающее ограждение, ни строки между ними. Само ограждение — часть текста
                // заметок, поэтому в открытый блок оно попадает наравне с содержимым.
                if (IsFenceEnd(line, fence, fenceLength)) fence = ' ';
                if (open.Length > 0) content.Add(line);
                if (fence == ' ') fenceLength = 0;
                continue;
            }

            if (IsFenceStart(line, out var marker, out var run))
            {
                // Ограждение внутри открытого блока — часть его текста (так его читает и
                // Markdown), поэтому открытый блок НЕ трогаем: содержимое кода в заметках
                // законно, а вот метка внутри него — нет.
                fence = marker;
                fenceLength = run;
                if (open.Length > 0) content.Add(line);
                continue;
            }

            var language = "";

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
                trimmed.EndsWith("-->", StringComparison.Ordinal) && trimmed.Length > 7)
            {
                var inner = trimmed[4..^3].Trim();
                if (inner.StartsWith("dsh-notes:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = inner["dsh-notes:".Length..].Trim().ToLowerInvariant();
                    if (name.Length > 0) language = name;
                }
                else if (inner.StartsWith("/dsh-notes:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = inner["/dsh-notes:".Length..].Trim().ToLowerInvariant();
                    // Закрывающий маркер без открывающего — просто строка текста, а не граница.
                    if (open.Length > 0 && name == open)
                    {
                        if (!blocks.ContainsKey(open)) blocks[open] = string.Join("\n", content).Trim();
                        open = "";
                        content.Clear();
                    }
                    continue;
                }
            }

            if (language.Length > 0)
            {
                // Открывающий маркер: предыдущий блок остался незакрытым — он не считается.
                open = language;
                content.Clear();
                continue;
            }

            if (open.Length > 0) content.Add(line);
        }

        return blocks;
    }

    /// <summary>
    /// Начало строки: сколько в ней пробелов (табуляция — за четыре) и что идёт следом.
    /// Нужно ограждениям: Markdown допускает у начала блока не больше трёх пробелов, а
    /// с большим отступом ограждение — это уже код внутри списка, а не граница блока.
    /// </summary>
    private static int FenceIndent(string line, out string rest)
    {
        var spaces = 0;
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == ' ') spaces++;
            else if (line[index] == '\t') spaces += 4;
            else break;
            index++;
        }

        rest = line[index..].TrimEnd();
        return spaces;
    }

    /// <summary>
    /// Открывает ли строка ограждённый блок кода. Разметка Markdown: не больше трёх пробелов
    /// отступа, три и больше символов «`» или «~», после них — что угодно (обычно язык).
    /// Смешанные символы (```~) началом не считаются: так же читает их и Markdown.
    /// </summary>
    private static bool IsFenceStart(string line, out char marker, out int run)
    {
        marker = ' ';
        run = 0;

        if (FenceIndent(line, out var rest) > 3) return false;
        if (rest.Length < 3) return false;

        var first = rest[0];
        if (first != '`' && first != '~') return false;

        var length = 0;
        while (length < rest.Length && rest[length] == first) length++;
        if (length < 3) return false;

        marker = first;
        run = length;
        return true;
    }

    /// <summary>
    /// Закрывает ли строка открытый ограждённый блок: тот же символ и не короче открывшего.
    /// Хвост после ограждения допускается — Markdown его игнорирует.
    /// </summary>
    private static bool IsFenceEnd(string line, char marker, int run)
    {
        var indent = FenceIndent(line, out var rest);
        if (indent > 3 || rest.Length < run || rest[0] != marker) return false;

        var length = 0;
        while (length < rest.Length && rest[length] == marker) length++;
        return length >= run;
    }

    /// <summary>«v1.21.0» → «1.21.0»: теги на GitHub обычно с буквой.</summary>
    private static string Clean(string version)
    {
        var text = (version ?? "").Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];
        return text.Trim();
    }

    /// <summary>
    /// Говорить ли человеку шариком о новой версии. Проверка обновлений идёт при КАЖДОМ запуске
    /// панели, поэтому без этой проверки шарик появлялся бы на каждом старте, пока человек не
    /// обновится, — а это уже приставание. Помним версию, о которой уже сказали: она и передаётся
    /// в <paramref name="announced"/>. Сказать не о чем, если на GitHub пусто или версия не новее
    /// текущей.
    /// </summary>
    public static bool ShouldAnnounce(string latest, string announced, string current)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (!IsNewer(latest, current)) return false;

        // Сравниваем версии как версии, а не как строки: тег с «v» спереди или с хешем сборки
        // после «+» — это та же версия, и второй раз о ней говорить нечего.
        var wanted = Clean(latest);
        var told = Clean(announced ?? "");
        if (told.Length == 0) return true;

        // О версии, которая не новее уже объявленной, молчим: выпуск на GitHub мог и откатиться
        // назад, а шарик про «1.21.1» после рассказа про «1.22.0» только путает.
        return IsNewer(wanted, told);
    }

    /// <summary>
    /// Новая ли версия на GitHub. Сравниваем как SemVer: сначала числа (1.21.0 новее 1.20.9),
    /// а при равных числах версия без предрелизного хвоста считается новее: 1.21.0 новее
    /// 1.21.0-rc.1. Иначе человек, поставивший предрелиз, никогда не получил бы финальный
    /// выпуск — апдейтер считал бы их одной версией.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        var left = Clean(candidate);
        var right = Clean(current);

        var leftNumbers = Numbers(left);
        var rightNumbers = Numbers(right);
        for (var index = 0; index < Math.Max(leftNumbers.Length, rightNumbers.Length); index++)
        {
            var a = index < leftNumbers.Length ? leftNumbers[index] : 0;
            var b = index < rightNumbers.Length ? rightNumbers[index] : 0;
            if (a != b) return a > b;
        }

        var leftPre = PreRelease(left);
        var rightPre = PreRelease(right);

        // Числа совпали: без хвоста — финальный выпуск, с хвостом — предварительный.
        if (leftPre.Length == 0 && rightPre.Length == 0) return false;
        if (leftPre.Length == 0) return true;
        if (rightPre.Length == 0) return false;

        return ComparePreRelease(leftPre, rightPre) > 0;
    }

    /// <summary>Предрелизный хвост версии: «1.21.0-rc.2» → «rc.2»; у финальной версии пусто.</summary>
    private static string PreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? "" : version[(dash + 1)..].Trim();
    }

    /// <summary>Сравнение хвостов по правилам SemVer: числа сравниваются как числа
    /// (rc.2 старше rc.10), слова — по алфавиту, короткий список младше длинного.</summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            if (index >= a.Length) return -1;
            if (index >= b.Length) return 1;

            var leftNumber = int.TryParse(a[index], out var leftValue);
            var rightNumber = int.TryParse(b[index], out var rightValue);

            if (leftNumber && rightNumber)
            {
                if (leftValue != rightValue) return leftValue.CompareTo(rightValue);
                continue;
            }

            // Числовой идентификатор младше буквенного (так требует SemVer).
            if (leftNumber != rightNumber) return leftNumber ? -1 : 1;

            var compare = string.Compare(a[index], b[index], StringComparison.OrdinalIgnoreCase);
            if (compare != 0) return compare;
        }

        return 0;
    }

    private static int[] Numbers(string version)
    {
        // Числа берём только из части до хвоста: «1.21.0-rc.2» — это 1, 21, 0.
        var head = version;
        var dash = head.IndexOf('-');
        if (dash >= 0) head = head[..dash];

        var parts = head.Split('.');
        var result = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var value)) break;
            result.Add(value);
        }

        return result.ToArray();
    }
}
