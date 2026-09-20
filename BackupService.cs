using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshTray;

/// <summary>Готовая копия в списке: то, что видно в окне «Резервные копии».</summary>
public sealed class BackupEntry
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public DateTime CreatedAt { get; set; }
    public long Bytes { get; set; }
    public int Files { get; set; }
    public bool WithEngine { get; set; }
    public bool WithSessions { get; set; }
    public bool WithCredentials { get; set; }
    public bool Readable { get; set; }
    public string Error { get; set; } = "";

    /// <summary>Строка для списка: «18.09.2026 12:00 · тонкая · 4,2 МБ · 2 231 файл».</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            CreatedAt.ToString("dd.MM.yyyy HH:mm"),
            Loc.T(WithEngine ? "backup.kind.full" : "backup.kind.thin"),
        };

        if (Bytes > 0) parts.Add(BackupFormat.Size(Bytes));
        if (Files > 0) parts.Add(Loc.T("backup.files", Files.ToString("N0", CultureInfo.InvariantCulture)));
        if (WithSessions) parts.Add(Loc.T("backup.withSessionsShort"));
        if (WithCredentials) parts.Add(Loc.T("backup.withKey"));
        if (!Readable) parts.Add(Loc.T("backup.manifestBroken", Error.Length > 0 ? Error : Loc.T("backup.unknown")));
        return string.Join(" · ", parts);
    }
}

/// <summary>Итог проверки копии: цела ли она и совпадает ли с описью.</summary>
public sealed class BackupCheck
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int Files { get; set; }
    public int Verified { get; set; }

    /// <summary>Сколько файлов вообще имеет контрольную сумму (у движка и Node их нет).</summary>
    public int Hashable { get; set; }

    public long Bytes { get; set; }
    public bool AllHashesChecked { get; set; } = true;

    public string Summary()
    {
        if (!Ok) return Loc.T("backup.notValid", Error);

        var text = Loc.T("backup.checkOk",
            Files.ToString("N0", CultureInfo.InvariantCulture),
            BackupFormat.Size(Bytes),
            Verified.ToString("N0", CultureInfo.InvariantCulture),
            Hashable.ToString("N0", CultureInfo.InvariantCulture));
        if (!AllHashesChecked) text += Loc.T("backup.checkPartial");
        return text;
    }
}

/// <summary>Итог создания копии.</summary>
public sealed class BackupResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public long SourceBytes { get; set; }
    public int Files { get; set; }
    public bool WithEngine { get; set; }
    public bool WithSessions { get; set; }
    public TimeSpan Took { get; set; }
    public List<string> Removed { get; set; } = new();

    /// <summary>Сколько крупных производных файлов (диски ВМ, ISO) в копию не попало.</summary>
    public int Skipped { get; set; }
    public long SkippedBytes { get; set; }

    public string Summary()
    {
        if (!Ok) return Loc.T("backup.notCreated", Error);

        var text = Loc.T("backup.createdOk",
            Loc.T(WithEngine ? "backup.kind.full" : "backup.kind.thin"),
            BackupFormat.Size(Bytes),
            Files.ToString("N0", CultureInfo.InvariantCulture),
            BackupFormat.Size(SourceBytes),
            BackupFormat.Duration(Took));
        if (Removed.Count > 0) text += Loc.T("backup.createdRemoved", Removed.Count);
        if (Skipped > 0)
        {
            text += Loc.T("backup.createdSkipped", Skipped, BackupFormat.Size(SkippedBytes));
        }
        return text;
    }
}

internal static class BackupFormat
{
    public static string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return Loc.T("backup.unit.gb", (bytes / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture));
        if (bytes >= 1024 * 1024) return Loc.T("backup.unit.mb", (bytes / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture));
        if (bytes >= 1024) return Loc.T("backup.unit.kb", (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture));
        return Loc.T("backup.unit.b", bytes.ToString(CultureInfo.InvariantCulture));
    }

    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 1
            ? Loc.T("backup.took.ms", span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            : span.TotalSeconds < 60
                ? Loc.T("backup.took.sec", span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                : Loc.T("backup.took.min", span.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture));
}

/// <summary>Опись копии. По ней инсталлятор понимает, что и куда раскладывать.</summary>
internal sealed class BackupManifest
{
    public string Kind { get; set; } = "dsh-backup";
    public int Format { get; set; } = 1;
    public string CreatedAt { get; set; } = "";
    public string App { get; set; } = "DshTray";
    public string AppVersion { get; set; } = "";
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public string Windows { get; set; } = "";
    public bool WithEngine { get; set; }
    public bool WithSessions { get; set; }
    public bool WithCredentials { get; set; }
    public Dictionary<string, string> Paths { get; set; } = new();
    public Dictionary<string, string> Versions { get; set; } = new();
    public List<string> Bundles { get; set; } = new();
    public Dictionary<string, string> Plugins { get; set; } = new();

    /// <summary>
    /// Junction-ссылки внутри dsh-home (профиль ссылается на глобальный движок).
    /// Сами ссылки в архив не кладутся — в описи лежит их список, восстановление
    /// создаёт их заново с новыми путями.
    /// </summary>
    public List<BackupLink> Links { get; set; } = new();

    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public List<BackupFileEntry> Files { get; set; } = new();
}

internal sealed class BackupFileEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }

    /// <summary>Контрольная сумма. У движка и Node её нет — там тысячи файлов и свой CRC в zip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Sha256 { get; set; }
}

/// <summary>Ссылка на каталог: путь внутри dsh-home и то, куда она ведёт.</summary>
internal sealed class BackupLink
{
    public string Path { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>
/// Резервная копия «себя»: наши проекты, данные DSH, настройки панели и — по желанию —
/// движок DSH вместе с Node. Кладётся одним zip-файлом с описью manifest.json внутри,
/// чтобы инсталлятор на чистой машине развернул всё без догадок.
/// </summary>
public sealed class BackupService
{
    /// <summary>Мусор, который в копию не попадает: собирается заново или весит слишком много.</summary>
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        // app-staging — публикация панели, из которой собирается установщик: она
        // воспроизводится сборкой и в копии только дублирует dsh-tray\app.
        "node_modules", ".git", ".pnpm-store", ".dotnet-home", ".appdata", ".nuget", "bin", "obj", "logs",
        "app-staging",
        // Вывод сборки Gradle и Android: у проекта ray-3d-forge это 116 МБ из 128 МБ
        // рабочей папки (замерено 19.09.2026) — ровно то же, что bin/obj у .NET,
        // и так же восстанавливается пересборкой из исходников.
        "build", ".gradle", ".kotlin",
    };

    /// <summary>
    /// Диски виртуальных машин и ISO-образы внутри рабочей папки: они весят десятки
    /// гигабайт и восстанавливаются из своих источников, а не из этой копии. Именно
    /// из-за такого файла копия однажды разрослась до 4,8 ГБ вместо 130 МБ.
    /// </summary>
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".vhd", ".vhdx", ".avhdx", ".vhdset", ".vsv", ".vmrs", ".vmgs",
    };

    private const string Prefix = "dsh-backup-";

    /// <summary>Имя предохранительной копии: делается перед накатом, ротацией не трогается.</summary>
    internal const string SafetyPrefix = "dsh-before-restore-";

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;

    /// <summary>Разовое указание состава копии: --full не должен менять настройку пользователя.</summary>
    private readonly bool? _withEngine;

    public BackupService(AppPaths paths, AppSettings settings, bool? withEngine = null)
    {
        _paths = paths;
        _settings = settings;
        _withEngine = withEngine;
    }

    /// <summary>Куда складывать копии. По умолчанию — Документы, чтобы легко унести на флешке.</summary>
    public string Folder
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_TRAY_BACKUP");
            if (!string.IsNullOrWhiteSpace(custom)) return Environment.ExpandEnvironmentVariables(custom.Trim());

            return string.IsNullOrWhiteSpace(_settings.BackupFolder)
                ? DefaultFolder()
                : Environment.ExpandEnvironmentVariables(_settings.BackupFolder.Trim());
        }
    }

    public static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DeepSeekHarness-Backups");

    /// <summary>
    /// Каталоги ключей, которые человек разрешил кладывать в копию. По умолчанию список пуст,
    /// и в архив не попадает ни один приватный ключ: раньше ключи копировались всегда, но это
    /// личные файлы, и решать про них должен владелец машины, а не приложение.
    /// </summary>
    public IEnumerable<(string Directory, string Prefix)> KeyRoots()
    {
        if (!_settings.BackupWithKeys) yield break;

        // Имена групп строим по порядку и с гарантией уникальности. Раньше имя бралось из
        // массива из четырёх элементов, и пятый каталог ключей получал уже занятый префикс:
        // одноимённые файлы (id_rsa, known_hosts) смешивались, и накат терял часть ключей.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var candidate in KeyDirectories())
        {
            index++;
            var slug = Slug(Path.GetFileName(candidate.TrimEnd('\\', '/')));
            var name = slug.Length > 0 ? index + "-" + slug : index.ToString();

            while (!used.Add(name)) name += "x";

            yield return (candidate, "keys/" + name);
        }
    }

    /// <summary>Короткое имя каталога для группы в архиве: только латиница, цифры и дефис.</summary>
    private static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var symbol in name)
        {
            if (char.IsLetterOrDigit(symbol) && symbol < 128) builder.Append(char.ToLowerInvariant(symbol));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var text = builder.ToString().Trim('-');
        return text.Length > 20 ? text[..20] : text;
    }

    /// <summary>Что именно копировать при включённой упаковке ключей: свой каталог SSH и добавленные вручную.</summary>
    public IEnumerable<string> KeyDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[] { AppPaths.SshDir }.Concat(_settings.BackupKeyDirs ?? new List<string>()))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(expanded)) continue;
            if (!seen.Add(expanded)) continue;
            yield return expanded;
        }
    }

    /// <summary>Корень глобальных пакетов npm: там лежит движок DSH.</summary>
    public static string NpmRoot()
    {
        try
        {
            var bin = NodeLocator.ResolveDshBin();
            if (!string.IsNullOrWhiteSpace(bin))
            {
                // …\npm\node_modules\@deepseek-ai\dsh\lib\bin.js → …\npm\node_modules
                // Уровней нужно не меньше четырёх (lib → dsh → @scope → node_modules),
                // иначе поиск не доходит и возвращается запасной путь — а он верен только
                // для стандартного npm. Так полная копия уходила не в тот каталог.
                var directory = new DirectoryInfo(Path.GetDirectoryName(bin) ?? "");
                for (var level = 0; level < 8 && directory != null; level++)
                {
                    if (!string.Equals(directory.Name, "node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        directory = directory.Parent;
                        continue;
                    }

                    return directory.FullName;
                }
            }
        }
        catch
        {
            // Ниже — обычное место установки.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
    }

    // --- создание ----------------------------------------------------------

    public BackupResult Create(Action<string> progress = null, bool safety = false)
    {
        var result = new BackupResult();
        var started = DateTime.Now;

        // Предохранительная копия перед накатом — тонкая и без обновления настроек:
        // её задача — вернуть всё как было, если накат окажется неудачным.
        var withEngine = !safety && (_withEngine ?? _settings.BackupWithEngine);
        var withSessions = _settings.BackupWithSessions;

        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            CleanupTemp(folder);

            var name = $"{(safety ? SafetyPrefix : Prefix)}{started:yyyy-MM-dd-HHmmss}-{(withEngine ? "full" : "thin")}.zip";
            var finalPath = Path.Combine(folder, name);
            var tempPath = Path.Combine(folder, ".tmp-" + name);

            var manifest = new BackupManifest
            {
                CreatedAt = started.ToString("yyyy-MM-dd HH:mm:ss"),
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "",
                Machine = Environment.MachineName,
                User = Environment.UserName,
                Windows = Environment.OSVersion.VersionString,
                WithEngine = withEngine,
                WithSessions = withSessions,
            };

            manifest.Paths["dshHome"] = _paths.DshHomePath;
            manifest.Paths["appData"] = AppPaths.DataDir;
            manifest.Paths["app"] = _paths.BaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            manifest.Paths["npmRoot"] = NpmRoot();
            manifest.Paths["nodeDir"] = Path.GetDirectoryName(NodeLocator.TryResolveNode()) ?? "";

            // Откуда взяты каталоги ключей: накат вернёт их на те же места, если человек
            // не скажет иначе. Без этого ключи из архива некуда было бы раскладывать.
            foreach (var (keyDir, keyPrefix) in KeyRoots()) manifest.Paths[keyPrefix] = keyDir;

            ReadProfile(manifest);
            ReadVersions(manifest);
            ReadLinks(manifest);

            var files = 0;
            var sourceBytes = 0L;
            var skippedFiles = 0;
            var skippedBytes = 0L;

            using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var root in Roots(withEngine, folder))
                {
                    var skipDirs = root.Everything ? null : SkippedDirs;

                    // У корня может быть свой отбор файлов: так в полную копию попадает
                    // ровно один файл из папки панели — её же установщик.
                    var skipRelative = root.Skip ?? (withSessions ? null : SkipInDshHome(root.Prefix));
                    foreach (var file in EnumerateFiles(root.Directory, skipRelative, skipDirs))
                    {
                        // Папка копий может оказаться внутри рабочей папки — свои же архивы не тащим.
                        if (IsInside(file, folder)) continue;

                        // И вообще никакие копии: иначе в новый архив попадёт предыдущий.
                        var fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            && (fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                                || fileName.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        // Диски виртуальных машин и ISO-образы не тащим: они весят десятки
                        // гигабайт, а восстанавливаются из своих источников. Считаем их,
                        // чтобы пропуск был виден в отчёте, а не выглядел тихой потерей.
                        if (SkippedExtensions.Contains(Path.GetExtension(file)))
                        {
                            skippedFiles++;
                            try { skippedBytes += new FileInfo(file).Length; } catch { }
                            continue;
                        }

                        var length = new FileInfo(file).Length;
                        var relative = Path.GetRelativePath(root.Directory, file).Replace('\\', '/');
                        var entryName = root.Prefix + "/" + relative;

                        var hash = length <= MaxHashBytes
                            ? Hash(file)
                            : null;

                        try
                        {
                            // Уровень сжатия — по размеру файла. Крупные файлы (движок и Node,
                            // сотни мегабайт) на «оптимальном» уровне собирались почти семь минут, и
                            // полная копия выглядела зависшей; быстрый уровень сокращает это в разы,
                            // а размер архива растёт незначительно. Мелкие настройки и скрипты
                            // сжимаем как раньше — там это дёшево и даёт выигрыш.
                            var level = length > 1_000_000 ? CompressionLevel.Fastest : CompressionLevel.Optimal;
                            var entry = zip.CreateEntry(entryName, level);

                            // Формат ZIP знает только 1980..2107: файл с датой 1970 (обычное дело
                            // для файлов из tar/WSL) бросал ArgumentOutOfRangeException и отменял
                            // ВСЮ копию — включая предохранительную перед накатом, без которой
                            // накат вообще невозможен. Такую дату просто приводим к текущей.
                            var written = File.GetLastWriteTime(file);
                            if (written.Year < 1980 || written.Year > 2107) written = DateTime.Now;
                            entry.LastWriteTime = written;

                            using (var target = entry.Open())
                            using (var source = File.OpenRead(file))
                            {
                                source.CopyTo(target);
                            }
                        }
                        catch (IOException)
                        {
                            // Файл занят (например, журнал сервера) — копия важнее одного файла.
                            continue;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Дата всё же не понравилась формату — файл важнее его отметки времени.
                            continue;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Нет доступа к отдельному файлу — остальную копию всё равно делаем.
                            continue;
                        }

                        manifest.Files.Add(new BackupFileEntry { Path = entryName, Size = length, Sha256 = hash });
                        files++;
                        sourceBytes += length;

                        // Показываем, сколько уже упаковано: без числа файлов полная копия
                        // выглядит зависшей, хотя она просто идёт несколько минут.
                        if (files % 25 == 0) progress?.Invoke(files + " · " + entryName);
                    }
                }

                manifest.TotalFiles = files;
                manifest.TotalBytes = sourceBytes;
                if (File.Exists(AppPaths.CredentialsPath)) manifest.WithCredentials = true;

                progress?.Invoke("manifest.json");
                WriteText(zip, "manifest.json", JsonSerializer.Serialize(manifest, ManifestOptions));
                WriteText(zip, "README.txt", RestoreReadme(manifest));
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath);

            result.Ok = true;
            result.Path = finalPath;
            result.Bytes = new FileInfo(finalPath).Length;
            result.SourceBytes = sourceBytes;
            result.Files = files;
            result.WithEngine = withEngine;
            result.Skipped = skippedFiles;
            result.SkippedBytes = skippedBytes;
            result.WithSessions = withSessions;
            result.Took = DateTime.Now - started;

            if (safety)
            {
                // Предохранительную копию не считаем обычной: её не затирает ротация,
                // но и копить их бесконечно незачем — держим три последние.
                PruneSafety(folder);
            }
            else
            {
                result.Removed = Prune();

                _settings.BackupLastAt = started;
                _settings.BackupLastPath = finalPath;
                _settings.Save(_paths.SettingsPath);
            }
        }
        catch (Exception error)
        {
            result.Ok = false;
            result.Error = error.Message;
        }

        return result;
    }

    private const long MaxHashBytes = 32L * 1024 * 1024;

    private IReadOnlyList<(string Directory, string Prefix, bool Everything, Func<string, bool> Skip)> Roots(bool withEngine, string backupFolder)
    {
        var roots = new List<(string, string, bool, Func<string, bool>)>();

        var dshHome = _paths.DshHomePath;
        if (Directory.Exists(dshHome)) roots.Add((dshHome, "dsh-home", false, null));

        // node_modules профилей обход dsh-home пропускает (там junction-ссылки на движок),
        // но у профиля web он свой и крошечный — с ним плагин восстанавливается без интернета.
        foreach (var profileDir in ProfileNodeModules(dshHome))
        {
            roots.Add((profileDir, Path.Combine("dsh-home", Path.GetRelativePath(dshHome, profileDir)).Replace('\\', '/'), false, null));
        }

        if (Directory.Exists(AppPaths.DataDir)) roots.Add((AppPaths.DataDir, "appdata/DshPanel", false, null));

        // Ключи и сертификаты — только если человек разрешил это в настройках: это приватные
        // файлы, и в публичной версии по умолчанию они в архив не попадают.
        foreach (var (keyDir, keyPrefix) in KeyRoots())
        {
            if (Directory.Exists(keyDir)) roots.Add((keyDir, keyPrefix, false, null));
        }

        if (withEngine)
        {
            // Копируем всю папку глобальных пакетов вместе с шимами dsh/npm/pnpm:
            // без шимов восстановленная система не знает команду dsh.
            var npmRoot = NpmRoot();
            var npmDir = Directory.GetParent(npmRoot)?.FullName;
            if (npmDir != null && Directory.Exists(npmDir)) roots.Add((npmDir, "engine/npm", true, null));

            var nodeDir = Path.GetDirectoryName(NodeLocator.TryResolveNode()) ?? "";
            if (nodeDir.Length > 0 && Directory.Exists(nodeDir)) roots.Add((nodeDir, "engine/nodejs", true, null));

            // Установщик панели лежит рядом с ней самой (его кладёт туда установщик). В полной
            // копии он нужен: тогда архив — это полный комплект переезда, «архив + установщик»,
            // и на чистой машине есть чем всё поставить. Из папки панели берём только его.
            var installer = Path.Combine(_paths.BaseDir, "dsh-panel-setup.exe");
            if (File.Exists(installer))
            {
                roots.Add((_paths.BaseDir, "installer", false,
                    relative => !string.Equals(relative, "dsh-panel-setup.exe", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return roots;
    }

    /// <summary>Папки node_modules внутри профилей: свои зависимости профиля, без junction-ссылок.</summary>
    private static IEnumerable<string> ProfileNodeModules(string dshHome)
    {
        var profiles = Path.Combine(dshHome, "profiles");
        if (!Directory.Exists(profiles)) yield break;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(profiles);
        }
        catch
        {
            yield break;
        }

        foreach (var profile in directories)
        {
            var modules = Path.Combine(profile, "node_modules");
            try
            {
                if (!Directory.Exists(modules)) continue;
                if ((File.GetAttributes(modules) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch
            {
                continue;
            }

            yield return modules;
        }
    }

    /// <summary>Внутри dsh-home история сессий и вложения — только по отдельной галочке.</summary>
    private static Func<string, bool> SkipInDshHome(string prefix) =>
        prefix == "dsh-home"
            ? path =>
            {
                var relative = path.Length > 0 ? path : "";
                return relative.StartsWith("sessions", StringComparison.OrdinalIgnoreCase)
                       || relative.StartsWith("attachments", StringComparison.OrdinalIgnoreCase);
            }
            : null;

    /// <summary>
    /// Обход файлов с пропуском служебных папок и символических ссылок: в профиле DSH
    /// почти весь node_modules — junction-ссылки на глобальный npm, разворачивать их нельзя.
    /// Корни движка обходятся целиком (skipDirs = null): там node_modules и есть содержимое.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, Func<string, bool> skipRelative, ISet<string> skipDirs)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (skipRelative != null)
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (skipRelative(relative)) continue;
                }

                yield return file;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirectories)
            {
                if (skipDirs != null && skipDirs.Contains(Path.GetFileName(sub))) continue;

                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                stack.Push(sub);
            }
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

    private static string Hash(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Читает опись из открытого архива. Опись — единственное, что общего у копии и наката.</summary>
    internal static BackupManifest ReadManifest(ZipArchive zip)
    {
        var manifestEntry = zip.GetEntry("manifest.json");
        if (manifestEntry == null) return null;

        using var stream = manifestEntry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), ManifestOptions);
    }

    // --- что за «я»: профиль, плагины, версии ---------------------------------

    private void ReadProfile(BackupManifest manifest)
    {
        try
        {
            var profileDir = Path.Combine(_paths.ProfilesDir, "web");
            var packagePath = Path.Combine(profileDir, "package.json");
            if (!File.Exists(packagePath)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            var root = document.RootElement;

            if (root.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var bundle in bundles.EnumerateArray())
                {
                    var value = bundle.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) manifest.Bundles.Add(value);
                }
            }

            if (root.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    var installed = InstalledVersion(Path.Combine(profileDir, "node_modules", dependency.Name));
                    manifest.Plugins[dependency.Name] = installed ?? dependency.Value.GetString() ?? "";
                }
            }
        }
        catch
        {
            // Опись — вспомогательная часть: без неё копия всё равно годится.
        }
    }

    private static string InstalledVersion(string packageDir)
    {
        try
        {
            var path = Path.Combine(packageDir, "package.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает список junction-ссылок внутри dsh-home: в профиле почти весь
    /// node_modules ссылается на глобальный движок. Восстанавливать их надо заново,
    /// потому что абсолютные пути на новой машине другие.
    /// </summary>
    private void ReadLinks(BackupManifest manifest)
    {
        var dshHome = _paths.DshHomePath;
        if (!Directory.Exists(dshHome)) return;

        var stack = new Stack<string>();
        stack.Push(dshHome);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirectories)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        manifest.Links.Add(new BackupLink
                        {
                            Path = Path.GetRelativePath(dshHome, sub).Replace('\\', '/'),
                            Target = info.LinkTarget ?? "",
                        });
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static void ReadVersions(BackupManifest manifest)    {
        manifest.Versions["dsh"] = PackageVersion(Path.Combine(NpmRoot(), "@deepseek-ai", "dsh"));
        manifest.Versions["npm"] = PackageVersion(Path.Combine(NpmRoot(), "npm"));
        manifest.Versions["pnpm"] = PackageVersion(Path.Combine(NpmRoot(), "pnpm"));
        manifest.Versions["dotnet"] = FirstLine("dotnet", "--version");
        manifest.Versions["node"] = FirstLine(NodeLocator.TryResolveNode(), "--version");
    }

    private static string PackageVersion(string packageDir)
    {
        var version = InstalledVersion(packageDir);
        return version ?? "";
    }

    private static string FirstLine(string executable, string arguments)
    {
        if (string.IsNullOrWhiteSpace(executable)) return "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process == null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var line = output.Replace("\r", "").Split('\n')[0].Trim();
            return line;
        }
        catch
        {
            return "";
        }
    }

    private static string RestoreReadme(BackupManifest manifest)
    {
        // Записка читается человеком, поэтому идёт на языке интерфейса панели (Loc), а не
        // по-русски всегда: архив, собранный англоязычной панелью, должен и объяснять по-английски.
        var text = new StringBuilder();
        text.AppendLine(Loc.T("readme.title"));
        text.AppendLine(Loc.T("readme.created", manifest.CreatedAt, manifest.Machine));
        text.AppendLine(Loc.T("readme.kind", Loc.T(manifest.WithEngine ? "readme.kindFull" : "readme.kindThin")));
        text.AppendLine(Loc.T("readme.files", manifest.TotalFiles, BackupFormat.Size(manifest.TotalBytes)));
        text.AppendLine();
        text.AppendLine(Loc.T("readme.contents"));
        text.AppendLine(Loc.T("readme.item.manifest"));
        text.AppendLine(Loc.T("readme.item.dshHome"));
        text.AppendLine(Loc.T("readme.item.appdata"));
        if (manifest.WithEngine)
        {
            text.AppendLine(Loc.T("readme.item.engine"));
        }

        if (manifest.Files.Any(file => file.Path.StartsWith("installer/", StringComparison.OrdinalIgnoreCase)))
        {
            text.AppendLine(Loc.T("readme.item.installer"));
        }

        // Группы ключей пишутся как keys/<номер>-<имя> и попадают в опись даже пустыми:
        // смотрим на самом деле упакованные файлы, иначе записка обещала бы каталог, которого нет.
        if (manifest.Files.Any(file => file.Path.StartsWith("keys/", StringComparison.OrdinalIgnoreCase)))
        {
            text.AppendLine(Loc.T("readme.item.keys"));
        }

        text.AppendLine();
        text.AppendLine(Loc.T("readme.howTitle"));
        text.AppendLine(Loc.T("readme.step1"));
        text.AppendLine(Loc.T("readme.step2"));
        text.AppendLine(Loc.T("readme.step3"));
        text.AppendLine(Loc.T("readme.step3b"));
        text.AppendLine(Loc.T("readme.step4"));
        text.AppendLine(Loc.T("readme.manual"));
        text.AppendLine(Loc.T("readme.manualPaths"));
        text.AppendLine(Loc.T("readme.manualRest"));
        text.AppendLine();
        text.AppendLine(Loc.T("readme.warn1"));
        text.AppendLine(Loc.T("readme.warn2"));
        return text.ToString();
    }

    // --- список, проверка, ротация --------------------------------------------

    /// <summary>Копии в папке, свежие сверху.</summary>
    public List<BackupEntry> List()
    {
        var entries = new List<BackupEntry>();
        try
        {
            if (!Directory.Exists(Folder)) return entries;

            foreach (var file in Directory.GetFiles(Folder, Prefix + "*.zip"))
            {
                entries.Add(Inspect(file));
            }
        }
        catch
        {
            // Папка недоступна — покажем пустой список.
        }

        entries.Sort((left, right) => string.CompareOrdinal(right.Name, left.Name));
        return entries;
    }

    /// <summary>Читает опись, не распаковывая архив.</summary>
    public static BackupEntry Inspect(string zipPath)
    {
        var entry = new BackupEntry
        {
            Path = zipPath,
            CreatedAt = SafeTime(zipPath),
            Bytes = new FileInfo(zipPath).Length,
        };

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry == null)
            {
                entry.Error = Loc.T("err.noManifest");
                return entry;
            }

            using var stream = manifestEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), ManifestOptions);
            if (manifest == null)
            {
                entry.Error = Loc.T("err.manifestEmpty");
                return entry;
            }

            entry.Readable = true;
            entry.Files = manifest.TotalFiles;
            entry.WithEngine = manifest.WithEngine;
            entry.WithSessions = manifest.WithSessions;
            entry.WithCredentials = manifest.WithCredentials;

            if (DateTime.TryParse(manifest.CreatedAt, out var created)) entry.CreatedAt = created;
        }
        catch (Exception error)
        {
            entry.Error = error.Message;
        }

        return entry;
    }

    /// <summary>
    /// Проверка копии: опись читается, число файлов совпадает, контрольные суммы
    /// сходятся. Суммы считаются не для всего архива: у движка и Node их нет,
    /// а распаковывать сотни мегабайт ради проверки незачем.
    /// </summary>
    public static BackupCheck Verify(string zipPath, Action<string> progress = null)
    {
        var check = new BackupCheck();

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry == null)
            {
                check.Error = Loc.T("err.notOurBackup");
                return check;
            }

            BackupManifest manifest;
            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), ManifestOptions);
            }

            if (manifest == null)
            {
                check.Error = Loc.T("err.manifestUnreadable");
                return check;
            }

            var expected = new Dictionary<string, BackupFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files) expected[file.Path] = file;

            check.Files = zip.Entries.Count(entry => entry.FullName != "manifest.json" && entry.FullName != "README.txt");
            check.Hashable = manifest.Files.Count(file => file.Sha256 != null);

            var missing = manifest.Files.Count(file => zip.GetEntry(file.Path) == null);
            if (missing > 0)
            {
                check.Error = Loc.T("err.filesMissing", missing);
                return check;
            }

            var budget = 64L * 1024 * 1024;
            foreach (var entry in zip.Entries)
            {
                if (!expected.TryGetValue(entry.FullName, out var record)) continue;

                check.Bytes += entry.Length;

                // У движка и Node суммы не считались: там тысячи файлов и свой CRC в zip.
                if (record.Sha256 == null) continue;
                if (budget <= 0)
                {
                    check.AllHashesChecked = false;
                    continue;
                }

                string hash;
                using (var stream = entry.Open())
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }

                budget -= entry.Length;

                if (!string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    check.Error = Loc.T("err.hashMismatch", entry.FullName);
                    return check;
                }

                check.Verified++;
                if (check.Verified % 25 == 0) progress?.Invoke(entry.FullName);
            }

            check.Ok = true;
        }
        catch (Exception error)
        {
            check.Error = error.Message;
        }

        return check;
    }

    private static DateTime SafeTime(string path)    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Оставляет последние N копий, остальные удаляет. Возвращает имена удалённых.</summary>
    public List<string> Prune()
    {
        var removed = new List<string>();
        var keep = Math.Clamp(_settings.BackupKeepCount, 1, 100);

        try
        {
            var entries = List();
            for (var index = keep; index < entries.Count; index++)
            {
                try
                {
                    File.Delete(entries[index].Path);
                    removed.Add(entries[index].Name);
                }
                catch
                {
                    // Не удалось — не беда, при следующем запуске попробуем снова.
                }
            }
        }
        catch
        {
            // Ротация — вспомогательный шаг.
        }

        return removed;
    }

    /// <summary>Оставляет три последние предохранительные копии: больше — уже не подстраховка, а мусор.</summary>
    private static void PruneSafety(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, SafetyPrefix + "*.zip");
            Array.Sort(files, (left, right) => string.CompareOrdinal(right, left));
            for (var index = 3; index < files.Length; index++)
            {
                try
                {
                    File.Delete(files[index]);
                }
                catch
                {
                    // Не удалилось — оставим до следующего раза.
                }
            }
        }
        catch
        {
            // Уборка — вспомогательный шаг.
        }
    }

    private static void CleanupTemp(string folder)
    {
        try
        {
            // Ловим и каталоги-обрубки: при отмене или сбое остаётся файл без расширения.
              foreach (var file in Directory.GetFiles(folder, ".tmp-*"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromHours(6)) continue;
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Незавершённая копия — мусор, но не повод падать.
                }
            }
        }
        catch
        {
            // Ничего.
        }
    }
}
