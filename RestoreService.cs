using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;

namespace DshTray;

/// <summary>
/// Что лежит в архиве и что из него можно вернуть. Собирается до наката, чтобы человек
/// видел, что именно он собирается наложить, и мог отказаться.
/// </summary>
public sealed class RestorePlan
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);

    public DateTime CreatedAt { get; set; }
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string DshVersion { get; set; } = "";
    public bool WithEngine { get; set; }
    public bool WithSessions { get; set; }

    public int DshHomeFiles { get; set; }
    public int PanelFiles { get; set; }
    public int KeyFiles { get; set; }
    public int EngineFiles { get; set; }

    /// <summary>Установщик панели внутри полной копии — чтобы комплект был полным.</summary>
    public int InstallerFiles { get; set; }

    public long Bytes { get; set; }

    /// <summary>Каталоги ключей в архиве: префикс внутри архива и куда их брали на прежней машине.</summary>
    public List<(string Prefix, string Original)> Keys { get; set; } = new();

    public bool HasDshHome => DshHomeFiles > 0;
    public bool HasPanel => PanelFiles > 0;
    public bool HasKeys => KeyFiles > 0;
    public bool HasEngine => EngineFiles > 0;
    public bool HasInstaller => InstallerFiles > 0;

    /// <summary>Строка о происхождении копии: «19.09.2026 14:20 · машина DESKTOP · панель 1.20.0».</summary>
    public string Origin()
    {
        var parts = new List<string> { CreatedAt.ToString("dd.MM.yyyy HH:mm") };
        if (Machine.Length > 0) parts.Add(Loc.T("restore.machine", Machine));
        if (AppVersion.Length > 0) parts.Add(Loc.T("restore.panelVersion", AppVersion));
        if (DshVersion.Length > 0) parts.Add(Loc.T("restore.dshVersion", DshVersion));
        parts.Add(Loc.T(WithEngine ? "backup.kind.full" : "backup.kind.thin"));
        return string.Join(" · ", parts);
    }

    /// <summary>Что именно можно вернуть: по строке на группу с числом файлов.</summary>
    public List<string> Contents()
    {
        var lines = new List<string>();
        if (HasDshHome) lines.Add(Loc.T("restore.has.dshHome", Count(DshHomeFiles)));
        if (HasPanel) lines.Add(Loc.T("restore.has.panel", Count(PanelFiles)));
        if (HasKeys) lines.Add(Loc.T("restore.has.keys", Count(KeyFiles)));
        if (HasEngine) lines.Add(Loc.T("restore.has.engine", Count(EngineFiles)));
        if (HasInstaller) lines.Add(Loc.T("restore.has.installer", Count(InstallerFiles)));
        return lines;
    }

    private static string Count(int files) => files.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>Что человек выбрал вернуть. Ключи и движок — только по явному согласию.</summary>
public sealed class RestoreOptions
{
    public bool DshHome { get; set; } = true;
    public bool PanelSettings { get; set; } = true;
    public bool Keys { get; set; }

    /// <summary>Движок DSH и Node: без него на чистой машине копия не заработает.</summary>
    public bool Engine { get; set; } = true;

    /// <summary>Сделать копию текущего состояния перед накатом.</summary>
    public bool SafetyCopy { get; set; } = true;
}

/// <summary>Итог наката: что вернули, что пропустили и куда легла предохранительная копия.</summary>
public sealed class RestoreResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public string SafetyPath { get; set; } = "";
    public TimeSpan Took { get; set; }
    public List<string> Restored { get; set; } = new();
    public List<string> Skipped { get; set; } = new();

    public string Summary()
    {
        if (!Ok) return Loc.T("restore.failed", Error);

        var text = Loc.T("restore.done",
            Files.ToString("N0", CultureInfo.InvariantCulture),
            BackupFormat.Size(Bytes),
            BackupFormat.Duration(Took));
        if (SafetyPath.Length > 0) text += Loc.T("restore.doneSafety", System.IO.Path.GetFileName(SafetyPath));
        return text;
    }
}

/// <summary>
/// Накат резервной копии: разбор архива, предохранительная копия, распаковка по местам
/// и возврат ссылок профиля. Работает и из окна панели, и ключом --restore.
///
/// Правила, за которые этот класс отвечает:
///  • распаковывается только то, что лежит внутри своей группы — запись с «..» или
///    абсолютным путём не выйдет за пределы каталога-назначения;
///  • ключи и сертификаты возвращаются только по явной галочке и закрываются на владельца;
///  • чужой архив (без нашей описи) не трогаем вовсе.
/// </summary>
public static class RestoreService
{
    /// <summary>Разбирает архив: что внутри, куда это ляжет и чего в нём нет.</summary>
    public static RestorePlan Plan(string zipPath)
    {
        var plan = new RestorePlan { Path = zipPath };

        try
        {
            if (!File.Exists(zipPath))
            {
                plan.Error = Loc.T("err.noFile", zipPath);
                return plan;
            }

            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = BackupService.ReadManifest(zip);
            if (manifest == null)
            {
                plan.Error = Loc.T("err.notOurBackup");
                return plan;
            }

            if (!string.Equals(manifest.Kind, "dsh-backup", StringComparison.OrdinalIgnoreCase))
            {
                plan.Error = Loc.T("err.notOurBackup");
                return plan;
            }

            plan.CreatedAt = DateTime.TryParse(manifest.CreatedAt, out var created)
                ? created
                : File.GetLastWriteTime(zipPath);
            plan.Machine = manifest.Machine;
            plan.User = manifest.User;
            plan.AppVersion = manifest.AppVersion;
            plan.DshVersion = manifest.Versions.GetValueOrDefault("dsh", "");
            plan.WithEngine = manifest.WithEngine;
            plan.WithSessions = manifest.WithSessions;

            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prefix in zip.Entries.Select(entry => entry.FullName).Where(name => name.StartsWith("keys/", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = prefix.Split('/');
                if (parts.Length < 2 || parts[1].Length == 0) continue;
                var group = "keys/" + parts[1];
                keys[group] = manifest.Paths.GetValueOrDefault(group, "");
            }

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                if (name is "manifest.json" or "README.txt") continue;

                plan.Bytes += entry.Length;

                if (name.StartsWith("dsh-home/", StringComparison.OrdinalIgnoreCase)) plan.DshHomeFiles++;
                else if (name.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase)) plan.PanelFiles++;
                else if (name.StartsWith("keys/", StringComparison.OrdinalIgnoreCase)) plan.KeyFiles++;
                else if (name.StartsWith("engine/", StringComparison.OrdinalIgnoreCase)) plan.EngineFiles++;
                else if (name.StartsWith("installer/", StringComparison.OrdinalIgnoreCase)) plan.InstallerFiles++;
            }

            foreach (var (group, original) in keys) plan.Keys.Add((group, original));

            if (!plan.HasDshHome && !plan.HasPanel && !plan.HasKeys && !plan.HasEngine)
            {
                plan.Error = Loc.T("restore.empty");
                return plan;
            }

            plan.Ok = true;
        }
        catch (Exception error)
        {
            plan.Error = error.Message;
        }

        return plan;
    }

    /// <summary>Распаковывает копию по местам. Сервер к этому моменту должен быть остановлен.</summary>
    public static RestoreResult Restore(RestorePlan plan, RestoreOptions options, AppPaths paths, AppSettings settings, Action<string> progress = null)
    {
        var result = new RestoreResult();
        var started = DateTime.Now;

        try
        {
            if (plan == null || !plan.Ok) throw new InvalidOperationException(plan?.Error ?? Loc.T("restore.noPlan"));

            if (options.SafetyCopy)
            {
                progress?.Invoke(Loc.T("restore.safetyMaking"));
                var safety = new BackupService(paths, settings).Create(null, safety: true);
                if (!safety.Ok) throw new InvalidOperationException(Loc.T("restore.safetyFailed", safety.Error));
                result.SafetyPath = safety.Path;
            }

            var npmDir = Path.GetDirectoryName(BackupService.NpmRoot()) ?? "";
            var nodePathBefore = settings.NodePath;
            var unsafeEntries = 0;
            var nodeSkipped = 0;
            var locked = 0;
            var lockedFiles = new List<string>();

            using (var zip = ZipFile.OpenRead(plan.Path))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                    var target = Target(entry.FullName, options, plan, paths, npmDir, settings, out var nodeEntry);
                    if (target == null)
                    {
                        if (nodeEntry) nodeSkipped++;
                        continue;
                    }

                    var full = Path.GetFullPath(target);
                    var root = Root(entry.FullName, plan, paths, npmDir, settings);
                    if (root.Length == 0 || !IsInside(full, root))
                    {
                        // Запись пытается выйти за пределы своего каталога — такую не распаковываем.
                        unsafeEntries++;
                        continue;
                    }

                    var directory = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    try
                    {
                        entry.ExtractToFile(full, overwrite: true);
                        result.Files++;
                        result.Bytes += entry.Length;
                        if (result.Files % 25 == 0) progress?.Invoke(entry.FullName);
                    }
                    catch (Exception error)
                    {
                        // Файл может быть занят: движок держит node, антивирус проверяет, человек
                        // открыл его в проводнике. Один такой файл не повод бросать весь накат —
                        // считаем, что не перезаписалось, и идём дальше; человеку скажем в отчёте.
                        locked++;
                        if (lockedFiles.Count < 5) lockedFiles.Add(Path.GetFileName(full) + " (" + error.Message + ")");
                    }
                }
            }

            // Ключи вернулись — закрываем их каталоги на владельца, как это делает сам DSH.
            if (options.Keys && plan.HasKeys)
            {
                foreach (var (group, _) in plan.Keys)
                {
                    var directory = KeyTarget(group, plan, paths);
                    if (directory.Length == 0 || !Directory.Exists(directory)) continue;

                    if (!Tighten(directory)) result.Skipped.Add(Loc.T("restore.keysAclFailed", AppPaths.Display(directory)));
                }
            }

            foreach (var (prefix, files) in Describe(plan, options, result))
            {
                result.Restored.Add(Loc.T("restore.group", Loc.T(prefix), files.ToString("N0", CultureInfo.InvariantCulture)));
            }

            RestoreLinks(plan, paths, result);

            if (unsafeEntries > 0) result.Skipped.Add(Loc.T("restore.skippedUnsafe", unsafeEntries));
            if (nodeSkipped > 0) result.Skipped.Add(Loc.T("restore.nodeSkipped", nodeSkipped));
            if (locked > 0)
            {
                result.Skipped.Add(Loc.T("restore.lockedFiles", locked));
                foreach (var line in lockedFiles) result.Skipped.Add("  " + line);
            }

            // Свой Node из копии прописываем в настройки ТОЛЬКО если файл действительно лёг
            // на диск: иначе панель запомнила бы путь к несуществующему node.exe.
            var ownNode = Path.Combine(OwnNodeDir(paths), "node.exe");
            if (File.Exists(ownNode))
            {
                settings.NodePath = ownNode;
                settings.Save(paths.SettingsPath);
                NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);
                result.Restored.Add(Loc.T("restore.nodeOwn", AppPaths.Display(settings.NodePath)));
            }
            else if (!string.Equals(nodePathBefore, settings.NodePath, StringComparison.OrdinalIgnoreCase))
            {
                // Ветка на случай, если путь пришёл из настроек копии: сохраняем как есть.
                settings.Save(paths.SettingsPath);
                NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);
            }

            // Накат считается удачным, только если записалось хоть что-то И ничего не осталось
            // незаписанным: иначе человек думал бы, что копия легла целиком.
            result.Ok = result.Files > 0 && locked == 0;
            if (result.Files == 0) result.Error = Loc.T("restore.nothing");
            else if (locked > 0) result.Error = Loc.T("restore.lockedFiles", locked);
        }
        catch (Exception error)
        {
            result.Ok = false;
            result.Error = error.Message;
        }

        result.Took = DateTime.Now - started;
        return result;
    }

    /// <summary>Что вернули — по строке на группу, для отчёта.</summary>
    private static IEnumerable<(string Key, int Files)> Describe(RestorePlan plan, RestoreOptions options, RestoreResult result)
    {
        if (options.DshHome && plan.HasDshHome) yield return ("restore.group.dshHome", plan.DshHomeFiles);
        if (options.PanelSettings && plan.HasPanel) yield return ("restore.group.panel", plan.PanelFiles);
        if (options.Keys && plan.HasKeys) yield return ("restore.group.keys", plan.KeyFiles);
        if (options.Engine && plan.HasEngine) yield return ("restore.group.engine", plan.EngineFiles);
        if (options.Engine && plan.HasInstaller) yield return ("restore.group.installer", plan.InstallerFiles);
    }

    /// <summary>
    /// Каталог назначения для записи архива или null, если группа не выбрана. Второй признак
    /// в <paramref name="nodeEntry"/> отмечает записи своего Node: их пропуск не ошибка,
    /// а решение — на машине уже есть свой Node.
    /// </summary>
    private static string Target(string entryName, RestoreOptions options, RestorePlan plan, AppPaths paths, string npmDir, AppSettings settings, out bool nodeEntry)
    {
        nodeEntry = false;

        if (entryName.StartsWith("dsh-home/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.DshHome) return null;
            return Path.Combine(paths.DshHomePath, entryName["dsh-home/".Length..].Replace('/', '\\'));
        }

        if (entryName.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.PanelSettings) return null;
            var rest = entryName["appdata/".Length..];
            // В архиве папка панели называется DshPanel — она и есть каталог настроек.
            if (rest.StartsWith("DshPanel/", StringComparison.OrdinalIgnoreCase)) rest = rest["DshPanel/".Length..];
            return Path.Combine(AppPaths.DataDir, rest.Replace('/', '\\'));
        }

        if (entryName.StartsWith("keys/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Keys) return null;
            var group = string.Join("/", entryName.Split('/').Take(2));
            var root = KeyTarget(group, plan, paths);
            if (root.Length == 0) return null;
            return Path.Combine(root, string.Join("/", entryName.Split('/').Skip(2)).Replace('/', '\\'));
        }

        if (entryName.StartsWith("engine/npm/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Engine || npmDir.Length == 0) return null;
            return Path.Combine(npmDir, entryName["engine/npm/".Length..].Replace('/', '\\'));
        }

        if (entryName.StartsWith("engine/nodejs/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Engine) return null;

            // Свой Node распаковываем только туда, где его нет: на машине с Node он не нужен,
            // а подменять чужую установку в Program Files панель не имеет права.
            // TryResolveNode не бросает: именно на машине без Node этот накат и нужен.
            var rest = entryName["engine/nodejs/".Length..].Replace('/', '\\');
            if (NodeLocator.TryResolveNode().Length > 0)
            {
                nodeEntry = true;
                return null;
            }

            // Путь в настройки здесь НЕ пишем: файл может не лечь (занят, нет места),
            // и панель запомнила бы несуществующий node.exe. Прописываем после распаковки.
            return Path.Combine(OwnNodeDir(paths), rest);
        }

        // Установщик панели из полной копии кладём рядом с самой панелью: оттуда его берёт
        // следующая полная копия, и оттуда же человек может поставить панель заново.
        if (entryName.StartsWith("installer/", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Engine) return null;
            return Path.Combine(paths.BaseDir, entryName["installer/".Length..].Replace('/', '\\'));
        }

        return null;
    }

    /// <summary>Каталог-назначение группы: за его пределы распаковка не выйдет.</summary>
    private static string Root(string entryName, RestorePlan plan, AppPaths paths, string npmDir, AppSettings settings)
    {
        if (entryName.StartsWith("dsh-home/", StringComparison.OrdinalIgnoreCase)) return paths.DshHomePath;
        if (entryName.StartsWith("appdata/", StringComparison.OrdinalIgnoreCase)) return AppPaths.DataDir;
        if (entryName.StartsWith("engine/npm/", StringComparison.OrdinalIgnoreCase)) return npmDir;
        if (entryName.StartsWith("engine/nodejs/", StringComparison.OrdinalIgnoreCase)) return OwnNodeDir(paths);
        if (entryName.StartsWith("installer/", StringComparison.OrdinalIgnoreCase)) return paths.BaseDir;
        if (entryName.StartsWith("keys/", StringComparison.OrdinalIgnoreCase))
        {
            var group = string.Join("/", entryName.Split('/').Take(2));
            return KeyTarget(group, plan, paths);
        }

        return "";
    }

    /// <summary>
    /// Куда возвращать каталог ключей. Записанный в описи путь — это путь машины-источника,
    /// поэтому сам по себе он годится не всегда: на новой машине ключи ушли бы в чужой профиль
    /// или в недоступный каталог.
    ///
    /// Исключение — копия, снятая на этом же компьютере этим же пользователем (в описи есть
    /// и машина, и пользователь): тогда путь родной, и ключи возвращаются туда, откуда взяты,
    /// даже если каталог лежал вне профиля (например, на другом диске). Для чужой копии
    /// разрешаем только путь внутри текущего профиля; свой каталог SSH знаем чем заменить,
    /// а незнакомый чужой не восстанавливаем и говорим об этом в отчёте.
    /// </summary>
    private static string KeyTarget(string group, RestorePlan plan, AppPaths paths)
    {
        var original = plan.Keys
            .FirstOrDefault(pair => string.Equals(pair.Prefix, group, StringComparison.OrdinalIgnoreCase))
            .Original ?? "";

        if (!string.IsNullOrWhiteSpace(original))
        {
            var expanded = Environment.ExpandEnvironmentVariables(original.Trim()).TrimEnd('\\');
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Корень диска ключами не засеваем даже по своей описи.
            if (expanded.Length > 2 && Directory.Exists(expanded))
            {
                if (IsSameMachine(plan)) return expanded;
                if (IsInside(expanded, profile)) return expanded;
            }
        }

        // Незнакомый чужой каталог не восстанавливаем — о нём скажем в отчёте. Исключение —
        // каталог SSH: у текущего пользователя он свой, и ключи должны лечь туда.
        return IsSshGroup(group, original) ? AppPaths.SshDir : "";
    }

    /// <summary>
    /// Группа ключей, которая в копии была каталогом .ssh. Имя группы — keys/&lt;номер&gt;-&lt;имя
    /// каталога&gt; (например keys/1-ssh), поэтому смотрим и на имя группы, и на записанный
    /// путь: знать, что это именно SSH, нужно, чтобы вернуть ключи в свой .ssh на новой машине.
    /// </summary>
    private static bool IsSshGroup(string group, string original)
    {
        if (group.EndsWith("/ssh", StringComparison.OrdinalIgnoreCase)) return true;
        if (group.EndsWith("-ssh", StringComparison.OrdinalIgnoreCase)) return true;

        var path = (original ?? "").TrimEnd('\\', '/');
        return path.EndsWith("\\.ssh", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/.ssh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Копия снята на этом же компьютере этим же пользователем — путям из описи можно верить.</summary>
    private static bool IsSameMachine(RestorePlan plan)
    {
        return !string.IsNullOrWhiteSpace(plan.Machine)
               && string.Equals(plan.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(plan.User, Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Свой Node для панели: кладём в её состояние, туда не нужен администратор.</summary>
    private static string OwnNodeDir(AppPaths paths) => Path.Combine(AppPaths.StateDir, "node");

    /// <summary>
    /// Возвращает ссылки профиля. Путь ссылки приходит из описи архива, а архив может быть
    /// чужим или подменённым, поэтому путь проверяем так же строго, как записи файлов:
    /// внутри dsh-home и без выхода наружу. Цель ссылки тоже ограничиваем понятными местами —
    /// движок, Node, профиль пользователя, — чтобы ссылка не увели файлы в чужой каталог.
    /// </summary>
    private static void RestoreLinks(RestorePlan plan, AppPaths paths, RestoreResult result)
    {
        try
        {
            using var zip = ZipFile.OpenRead(plan.Path);
            var manifest = BackupService.ReadManifest(zip);
            if (manifest == null || manifest.Links.Count == 0) return;

            var created = 0;
            foreach (var link in manifest.Links)
            {
                var path = Path.Combine(paths.DshHomePath, (link.Path ?? "").Replace('/', '\\'));

                if (!IsInside(Path.GetFullPath(path), paths.DshHomePath))
                {
                    // Ссылка пытается встать вне данных DSH — не создаём её вовсе.
                    result.Skipped.Add(Loc.T("restore.linkOutside", link.Path));
                    continue;
                }

                var target = ResolveTarget(link.Target, paths);
                if (target.Length == 0)
                {
                    result.Skipped.Add(Loc.T("restore.linkMissing", link.Path));
                    continue;
                }

                if (Directory.Exists(path) || File.Exists(path))
                {
                    // Уже есть: либо ссылка на месте, либо каталог с файлами — не трогаем.
                    if (!IsEmptyDirectory(path)) continue;
                    try { Directory.Delete(path); } catch { }
                }

                if (CreateJunction(path, target)) created++;
                else result.Skipped.Add(Loc.T("restore.linkFailed", link.Path));
            }

            if (created > 0) result.Restored.Add(Loc.T("restore.links", created));
        }
        catch (Exception error)
        {
            result.Skipped.Add(Loc.T("restore.linkFailed", error.Message));
        }
    }

    /// <summary>Прежняя цель ссылки; если её нет, ищем такой же пакет в текущем npm.</summary>
    private static string ResolveTarget(string target, AppPaths paths)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";

        var expanded = Environment.ExpandEnvironmentVariables(target.Replace('/', '\\'));

        if (Directory.Exists(expanded) && IsAllowedLinkTarget(expanded, paths)) return expanded;

        // Была ссылка на глобальный движок: путь заканчивается на node_modules\@scope\pkg.
        var marker = "node_modules\\";
        var index = expanded.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";

        var tail = expanded[(index + marker.Length)..];

        // Хвост тоже из архива: убираем попытки выйти наружу.
        if (tail.Contains("..")) return "";

        var candidate = Path.Combine(BackupService.NpmRoot(), tail);
        if (Directory.Exists(candidate) && IsInside(Path.GetFullPath(candidate), BackupService.NpmRoot())) return candidate;

        return "";
    }

    /// <summary>
    /// Куда ссылке разрешено указывать: движок в npm, папка Node, профиль пользователя,
    /// данные и состояние панели. Всё остальное — чужой каталог, ссылку на него не создаём.
    /// </summary>
    private static bool IsAllowedLinkTarget(string target, AppPaths paths)
    {
        var roots = new[]
        {
            BackupService.NpmRoot(),
            Path.GetDirectoryName(NodeLocator.TryResolveNode()) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppPaths.DataDir,
            AppPaths.StateDir,
            paths.BaseDir,
        };

        return roots.Any(root => !string.IsNullOrWhiteSpace(root) && IsInside(target, root));
    }

    private static bool CreateJunction(string path, string target)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // ProcessRunner: читает оба потока сразу, иначе mklink с длинным выводом подвешивал бы.
            var outcome = ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"", 10000);
            return outcome.Ok && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEmptyDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Закрывает каталог ключей на владельца и SYSTEM: копия могла прийти с другой машины,
    /// а приватные ключи не должны быть доступны никому, кроме владельца. Права ставим через
    /// icacls по номерам (SID): имена на разных языках Windows называются по-разному.
    ///
    /// Проходов два, и это важно. Первый снимает у каталога унаследованные права и выдаёт
    /// наследуемые права владельцу и SYSTEM. Второй даёт такие же права каждому файлу и
    /// подкаталогу: одним проходом не выходит, потому что у файла флаги (OI)(CI) значат
    /// «только для вложения» и доступа к самому файлу не дают — перенесённый из чужого
    /// архива ключ оставался вообще без прав, его нельзя было ни прочитать, ни удалить.
    /// </summary>
    private static bool Tighten(string directory)
    {
        var owner = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(owner)) return false;

        var inherit = $"\"{directory}\" /inheritance:r /grant:r *{owner}:(OI)(CI)F *S-1-5-18:(OI)(CI)F";
        var direct = $"\"{directory}\" /T /C /Q /grant:r *{owner}:F *S-1-5-18:F";

        return Run("icacls.exe", inherit, 60000) && Run("icacls.exe", direct, 120000);
    }

    /// <summary>Запускает внешнюю программу и говорит, прошло ли без ошибок.</summary>
    private static bool Run(string executable, string arguments, int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(info);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(timeoutMs);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInside(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return false;
        try
        {
            var full = Path.GetFullPath(candidate).TrimEnd('\\') + "\\";
            var outer = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            return full.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
