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

    /// <summary>Аргументы для cmd.exe: сценарий и пути. Пути идут аргументами, а не в теле
    /// сценария, потому что cmd читает .cmd в кодировке консоли и не-ASCII путь в теле ломается.</summary>
    public string CommandLine { get; set; } = "";
}

/// <summary>
/// Обновление панели с GitHub: проверка выпусков, скачивание сборки и подготовка замены
/// файлов. Личные данные при этом не трогаются: настройки лежат в %APPDATA%, состояние —
/// в %LOCALAPPDATA%, а меняются только файлы самой панели.
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

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var assetNameValue) ? assetNameValue.GetString() ?? "" : "";
                    var assetUrl = asset.TryGetProperty("browser_download_url", out var assetUrlValue) ? assetUrlValue.GetString() ?? "" : "";
                    if (assetName.Equals("DshPanel.zip", StringComparison.OrdinalIgnoreCase)) result.ZipUrl = assetUrl;
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
        bool ignoreNewer = false)
    {
        var result = new UpdateDownload();
        var staged = "";
        var zipPath = "";

        try
        {
            if (check == null || !check.Ok) throw new InvalidOperationException(check?.Error ?? Loc.T("update.notChecked"));

            // ignoreNewer — только для проверки самого пути обновления: она гоняет тот же код
            // на выпуске той же версии, что стоит. Ни одну проверку целостности это не снимает.
            if (!check.Newer && !ignoreNewer) throw new InvalidOperationException(Loc.T("update.alreadyLatest"));
            if (check.ZipUrl.Length == 0) throw new InvalidOperationException(Loc.T("update.noZip"));

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
            var sumsPath = Path.Combine(updateRoot, "SHA256SUMS.txt");
            Download(check.SumsUrl, sumsPath);
            var sums = ReadSums(sumsPath);

            progress?.Invoke(Loc.T("update.downloading", "DshPanel.zip"));
            zipPath = Path.Combine(updateRoot, "DshPanel-" + version + ".zip");
            var zipHash = Download(check.ZipUrl, zipPath);
            VerifyHash("DshPanel.zip", zipHash, sums);

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
            result.BackupFolder = CopyCurrent(paths.BaseDir, backup);
            result.ScriptPath = WriteScript(paths.BaseDir, staged, backup, updateRoot, out var log);
            result.CommandLine = "\"\"" + result.ScriptPath + "\" \"" + Environment.ProcessId + "\" \"" +
                                  paths.BaseDir + "\" \"" + staged + "\" \"" + backup + "\" \"" + log + "\"\"";
            result.StagedFolder = staged;
            result.Version = version;
            result.Ok = true;
        }
        catch (Exception error)
        {
            result.Error = error.Message;

            // За собой убираем: без этого в %LOCALAPPDATA%\DshPanel\update копились бы
            // обрезанные загрузки и распакованные папки неудавшихся обновлений.
            TryDeleteFolder(staged);
            TryDeleteFile(zipPath);
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
    /// Сценарий замены файлов. Тело намеренно состоит только из ASCII, а все пути приходят
    /// аргументами (%~1…%~5): cmd.exe читает .cmd в кодировке консоли (на русской Windows —
    /// cp866), и не-ASCII путь, вписанный в тело, превращается в несуществующий — панель
    /// закрывалась, а обновление молча не применялось.
    ///
    /// Порядок: дождаться выхода панели (по её PID, а не попыткой записи файла, иначе exe
    /// успевал замениться раньше dll), скопировать, проверить код robocopy (0..7 — успех,
    /// 8 и больше — ошибка), при ошибке вернуть файлы из копии и в любом случае запустить
    /// панель заново: человек не должен остаться без окна и значка.
    /// </summary>
    private static string WriteScript(string appDir, string staged, string backup, string updateRoot, out string logPath)
    {
        var script = Path.Combine(updateRoot, "update.cmd");
        logPath = Path.Combine(updateRoot, "update.log");

        var lines = new[]
        {
            "@echo off",
            "rem DSH Panel self-update. ASCII only: cmd reads this file in the OEM code page.",
            "rem Paths come as arguments: %~1 pid, %~2 target, %~3 staged, %~4 backup, %~5 log.",
            "setlocal EnableExtensions",
            "set \"PID=%~1\"",
            "set \"TARGET=%~2\"",
            "set \"STAGED=%~3\"",
            "set \"BACKUP=%~4\"",
            "set \"LOG=%~5\"",
            "echo update started > \"%LOG%\"",
            "echo target: %TARGET% >> \"%LOG%\"",
            "",
            "rem Wait for the panel to exit: about two minutes at most.",
            "for /L %%i in (1,1,120) do (",
            "  tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul || goto ready",
            "  ping -n 1 -w 1000 127.0.0.1 >nul",
            ")",
            "echo warning: the panel did not exit in time >> \"%LOG%\"",
            "",
            ":ready",
            "robocopy \"%STAGED%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "set \"RC=%ERRORLEVEL%\"",
            "echo robocopy exit code: %RC% >> \"%LOG%\"",
            "if %RC% GEQ 8 goto rollback",
            "echo files replaced >> \"%LOG%\"",
            "goto start",
            "",
            ":rollback",
            "echo update failed, restoring the previous version >> \"%LOG%\"",
            "if exist \"%BACKUP%\\DshTray.exe\" robocopy \"%BACKUP%\" \"%TARGET%\" /E /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
            "",
            ":start",
            "if exist \"%TARGET%\\DshTray.exe\" start \"\" \"%TARGET%\\DshTray.exe\"",
            "del \"%~f0\"",
        };

        File.WriteAllLines(script, lines, new System.Text.UTF8Encoding(false));
        return script;
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
            // Копию сделать не удалось — обновление не отменяем, но скажем об этом.
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
