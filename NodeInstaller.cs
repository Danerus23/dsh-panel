using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DshTray;

/// <summary>Что получилось при установке Node.</summary>
public sealed class NodeInstallResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>Куда встал Node: машинный путь или свой, внутри панели.</summary>
    public string Path { get; set; } = "";

    /// <summary>true — поставили в систему (обычный установщик Node), false — распаковали к себе.</summary>
    public bool MachineWide { get; set; }

    public string Summary()
    {
        if (!Ok) return Loc.T("nodeInstall.failed", Error);
        return Loc.T(MachineWide ? "nodeInstall.doneSystem" : "nodeInstall.donePrivate", Version, AppPaths.Display(Path));
    }
}

/// <summary>
/// Установка Node.js: панель делает это и для мастера, и по просьбе установщика
/// (`DshTray.exe --install-node`), чтобы первому запуску не мешала ручная возня.
///
/// Порядок такой: сначала обычный установщик Node (машинная установка, спросит права
/// администратора — тогда node виден и в командной строке). Если прав нет или установка
/// не прошла, распаковываем переносимую сборку Node внутрь панели и прописываем путь
/// в настройках: движок DSH работает и так, а человек получает рабочий инструмент
/// без администратора.
/// </summary>
public static class NodeInstaller
{
    private const string IndexUrl = "https://nodejs.org/dist/index.json";

    /// <summary>
    /// Свежий LTS-выпуск: версия и адреса установщика и переносимого архива.
    ///
    /// Имена файлов у Node разные (node-v24.21.0-x64.msi, но node-v24.21.0-win-x64.zip), и
    /// угадывать их нельзя: раньше здесь имя архива собиралось по образцу MSI, и переносимая
    /// установка падала с 404 — Node не ставился ни из мастера, ни из установщика. Поэтому
    /// смотрим, какие сборки для Windows x64 выпуск действительно содержит.
    /// </summary>
    public static (string Version, string MsiUrl, string ZipUrl) FindLatestLts()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/" + AppVersion.Short);
        var json = client.GetStringAsync(IndexUrl).GetAwaiter().GetResult();

        using var document = JsonDocument.Parse(json);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            // У обычных выпусков lts = false, у LTS — имя линейки («Krypton» и подобное).
            if (!release.TryGetProperty("lts", out var lts) || lts.ValueKind != JsonValueKind.String) continue;

            var version = release.GetProperty("version").GetString() ?? "";
            if (version.Length == 0) continue;

            var files = new List<string>();
            if (release.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in list.EnumerateArray()) files.Add(file.GetString() ?? "");
            }

            // Переносимая сборка нужна всегда: она единственная работает без прав администратора.
            if (!files.Contains("win-x64-zip")) continue;

            var hasMsi = files.Contains("win-x64-msi");
            return (version.TrimStart('v', 'V'),
                    hasMsi ? "https://nodejs.org/dist/" + version + "/node-" + version + "-x64.msi" : "",
                    "https://nodejs.org/dist/" + version + "/node-" + version + "-win-x64.zip");
        }

        throw new InvalidOperationException(Loc.T("onboard.nodeNoLts"));
    }

    /// <summary>
    /// Ставит Node. Возвращает результат с версией и путём; в <paramref name="progress"/>
    /// уходят короткие сообщения для окна мастера или отчёта установщика.
    /// </summary>
    public static NodeInstallResult Install(AppPaths paths, AppSettings settings, Action<string> progress = null)
    {
        var result = new NodeInstallResult();

        try
        {
            // Уже есть — ничего не делаем: повторная установка только запутает.
            var existing = NodeLocator.TryResolveNode();
            if (existing.Length > 0)
            {
                result.Ok = true;
                result.Path = existing;
                result.MachineWide = true;
                result.Version = NodeVersion(existing);
                progress?.Invoke(Loc.T("nodeInstall.already", existing));
                return result;
            }

            var release = FindLatestLts();
            result.Version = release.Version;

            string msi = "";
            string zip = "";
            try
            {
                // Машинную установку пробуем только с правами администратора и только если
                // выпуск вообще содержит MSI: Node MSI ставится в Program Files и без повышения
                // просто откажет (UAC сам не появится, потому что процесс запущен без запроса
                // прав). Иначе сразу идём переносимым путём — иначе человек ждал бы лишнюю
                // загрузку на 30 МБ.
                if (IsAdministrator() && release.MsiUrl.Length > 0)
                {
                    progress?.Invoke(Loc.T("nodeInstall.downloading", "node-" + release.Version + "-x64.msi"));
                    msi = Path.Combine(Path.GetTempPath(), "node-v" + release.Version + "-x64.msi");
                    Download(release.MsiUrl, msi, progress);

                    progress?.Invoke(Loc.T("nodeInstall.installing", release.Version));
                    if (RunInstaller(msi))
                    {
                        NodeLocator.ResetCache();
                        var installed = FindInstalledNode();
                        if (installed.Length > 0)
                        {
                            result.Ok = true;
                            result.Path = installed;
                            result.MachineWide = true;
                            result.Version = release.Version;
                            return result;
                        }
                    }
                }

                // Машинная установка не подошла — кладём Node к себе и прописываем путь
                // в настройках: движку этого достаточно, и прав администратора не нужно.
                progress?.Invoke(Loc.T("nodeInstall.portable", release.Version));
                zip = Path.Combine(Path.GetTempPath(), "node-v" + release.Version + "-win-x64.zip");
                Download(release.ZipUrl, zip, progress);

                var root = Path.Combine(AppPaths.StateDir, "node");
                var fresh = Path.Combine(AppPaths.StateDir, "node-new");

                // Распаковываем рядом и меняем местами: прежний рабочий Node не теряется,
                // если загрузка или распаковка не удалась.
                if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
                ZipFile.ExtractToDirectory(zip, fresh, overwriteFiles: true);

                var unpacked = Directory.GetDirectories(fresh).FirstOrDefault() ?? fresh;
                var nodeExe = Path.Combine(unpacked, "node.exe");
                if (!File.Exists(nodeExe))
                {
                    nodeExe = Directory.GetFiles(fresh, "node.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                }

                if (nodeExe.Length == 0) throw new InvalidOperationException(Loc.T("nodeInstall.noExe"));

                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.Move(unpacked, root);
                if (Directory.Exists(fresh)) Directory.Delete(fresh, true);

                var finalExe = Path.Combine(root, "node.exe");
                settings.NodePath = finalExe;
                settings.Save(paths.SettingsPath);
                NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);

                result.Ok = true;
                result.Path = finalExe;
                result.Version = release.Version;
                result.MachineWide = false;
                return result;
            }
            finally
            {
                // Скачанные файлы (30–80 МБ) в %TEMP% не оставляем — ни при успехе, ни при ошибке.
                Delete(msi);
                Delete(zip);
            }
        }
        catch (Exception error)
        {
            result.Ok = false;
            result.Error = error.Message;
            return result;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Не удалилось — не беда: файл лежит в %TEMP% и уйдёт при уборке системы.
        }
    }

    /// <summary>
    /// node.exe, который видит система: сперва обычные места, потом PATH. Нужен отдельно
    /// от NodeLocator, потому что тот кэширует ответ и не знает про свежую установку.
    /// </summary>
    private static string FindInstalledNode()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node.exe"),
        };

        foreach (var directory in (Environment.GetEnvironmentVariable("Path") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(directory.Trim(), "node.exe")); } catch { }
        }

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary>
    /// Тихая установка MSI с обычным окном прогресса: /qb показывает ход, /norestart не
    /// перезагружает машину. Права администратора спросит сам установщик — через UAC.
    /// </summary>
    private static bool RunInstaller(string msi)
    {
        try
        {
            var info = new ProcessStartInfo("msiexec.exe", "/i \"" + msi + "\" /qb /norestart")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            if (process == null) return false;
            process.WaitForExit(900000);
            // 0 — успех, 3010 — успех с необходимостью перезагрузки.
            return process.ExitCode == 0 || process.ExitCode == 3010;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Скачивает файл и проверяет, что пришло столько байт, сколько обещал сервер: обрыв
    /// загрузки иначе выглядел бы как «msiexec вернул ошибку» без объяснения причины.
    /// </summary>
    private static void Download(string url, string path, Action<string> progress = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/" + AppVersion.Short);

        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength ?? -1;
        var written = 0L;

        using (var source = response.Content.ReadAsStream())
        using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                written += read;
            }
        }

        progress?.Invoke(Loc.T("nodeInstall.downloaded", BackupFormat.Size(written)));

        if (expected >= 0 && written != expected)
        {
            throw new InvalidOperationException(Loc.T("nodeInstall.shortRead", written, expected));
        }
    }

    private static string NodeVersion(string nodePath)
    {
        try
        {
            var info = new ProcessStartInfo(nodePath, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Trim().TrimStart('v', 'V');
        }
        catch
        {
            return "";
        }
    }
}
