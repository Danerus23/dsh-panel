using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>Что и куда снимать.</summary>
/// <param name="ArchivePath">Путь архива. Выбирает человек, и это ЕГО выбор: копия уезжает на флешке.</param>
/// <param name="ServerRunning">
/// Работает ли сервер в момент копии. Названо вызывающим явно, а не угадано: сессии пишет живой
/// процесс, поэтому «копия на ходу» — это допущение, и оно должно быть названо
/// (`ROADMAP.md`, находка В2). Останавливать чужой сервер панель сама не станет — это решение
/// человека, и принимает его тот, кто зовёт копию.
/// </param>
/// <param name="SevenZipPath">
/// Чем писать: пусто — искать самим, <c>""</c> — только своими силами, путь — им и писать.
/// </param>
/// <param name="Verify">
/// Проверять ли архив сторонним валидатором (<c>7z t</c>). Оставлено отдельным решением, потому
/// что это единственная проверка, которая ЛОВИТ поломку: встроенный распаковщик .NET контрольные
/// суммы не сверяет, и замер поймал ровно такой случай (<c>docs\ARCHIVE-MEASUREMENT.md</c>).
/// </param>
public sealed record BackupRequest(
    string ArchivePath,
    bool ServerRunning = false,
    string? SevenZipPath = null,
    bool Verify = true);

/// <summary>Чем кончилась копия. <see cref="Notes"/> — пропуски и допущения, названные словами.</summary>
public sealed record BackupRunResult(
    bool Ok,
    string Error,
    string ArchivePath,
    long Bytes,
    int Files,
    long SourceBytes,
    ZipWriterKind Writer,
    string Reason,
    TimeSpan Took,
    bool Restricted,
    bool? Verified,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Строка для отчёта и журнала. Путь показывается через маскировку, а «не проверено» никогда
    /// не выдаётся за «проверено»: <see cref="Verified"/> равный <c>null</c> — это «проверить нечем»,
    /// и он говорит об этом словами.
    /// </summary>
    public string Summary()
    {
        if (!Ok) return string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupNotTakenFormat, Error);

        var text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.BackupEngineDoneFormat,
            BackupFormat.Size(Bytes), BackupFormat.Size(SourceBytes), Files,
            BackupFormat.Duration(Took), Reason);

        text += Restricted
            ? PanelStrings.BackupRightsOwnerOnly
            : PanelStrings.BackupRightsOpenWarning;

        text += Verified switch
        {
            true => PanelStrings.BackupZipVerified,
            false => PanelStrings.BackupZipBroken,
            _ => PanelStrings.BackupZipUnverifiable,
        };

        return Notes.Count == 0 ? text : text + " · " + string.Join(" · ", Notes);
    }
}

/// <summary>
/// СНЯТИЕ КОПИИ: список корней берётся у <see cref="BackupPlan"/>, дерево пишет
/// <see cref="ZipWriter"/> (через 7-Zip или своими силами), а здесь живёт то, что относится
/// к копии целиком, — **опись внутри архива**, **права только владельцу** и **проверка архива**.
///
/// Три вещи, каждая из которых выросла из чужого провала:
///
/// 1. **Опись внутри архива.** Копию уносят на флешке и передают человеку; опись рядом с архивом
///    теряется первой, и тогда в архиве остаётся набор каталогов, про который нельзя сказать
///    ни что это, ни куда раскладывать. Опись описывает **то, что в архив действительно легло**:
///    записи перечисляются из самого архива, а не из наших надежд.
/// 2. **Права только владельцу.** В архиве лежит <c>.credentials.yaml</c> с ключами агентов
///    и секретом подписи (находка В4, `ROADMAP.md`). Архив создаётся сразу закрытым: владелец
///    и SYSTEM — та же пара, которой v1 закрывала каталог ключей
///    (<c>dsh-tray\RestoreService.cs:Tighten</c>). SYSTEM — это сама система (том-копии,
///    антивирус), а не ещё один человек; другим учётным записям доступа нет.
/// 3. **Проверка архива.** Свой распаковщик .NET контрольных сумм не сверяет, поэтому «архив
///    открывается» не значит «архив цел». Ловит это только <c>7z t</c> — и копия, не прошедшая
///    проверку, копией не считается.
///
/// ⚠️ **Ссылки на каталоги (junction) в архив не кладутся содержимым** — они уезжают списком
/// в описи (<c>BackupManifest.Links</c>), потому что внутри ссылки записан абсолютный путь
/// машины-источника, и на другой машине он мёртв. Создаёт их заново накат
/// (<c>docs\ARCHIVE-MEASUREMENT.md</c>, находка 3).
///
/// ⚠️ **Чего здесь нет и почему.** Контрольных сумм по каждому файлу (<c>BackupFileEntry.Sha256</c>)
/// нет: у движка и Node это десятки тысяч файлов, а свой CRC уже лежит в zip. Считать SHA-256
/// значило бы прочитать всё дерево второй раз ради проверки, которую делает и распаковщик;
/// поле остаётся необязательным и пустым (так же, как в v1 для крупных файлов).
/// </summary>
public static class BackupEngine
{
    /// <summary>Имя панели в описи. Одно место на всю панель: <see cref="AppPaths.ProductFolder"/>.</summary>
    public const string AppValue = AppPaths.ProductFolder;

    /// <summary>
    /// Снимает полную копию по готовому плану.
    /// </summary>
    /// <param name="paths">Единственный источник путей панели: из него берётся файл ключей движка.</param>
    /// <param name="engine">Найденный движок — из него в опись уезжают версии движка, npm и pnpm.</param>
    public static BackupRunResult Run(
        BackupPlan plan,
        BackupRequest request,
        AppPaths paths,
        DshEngine? engine = null,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);

        var started = Stopwatch.StartNew();

        // Причину отказа называем ЗДЕСЬ и всегда непустой строкой (дефект Д2). Оба случая пустого
        // плана различны и названы по-разному: «не собрать по настоящей причине» — отказ,
        // а «корней нет вовсе» — это чистая машина, где копировать нечего. Пустая подстановка
        // («копию так не собрать: ») в отчёт не попадает ни при каком плане.
        if (!plan.Ok)
        {
            var why = plan.Error.Length > 0
                ? plan.Error
                : plan.NothingToLose
                    ? BackupPlan.NothingToLoseText
                    : PanelStrings.BackupPlanEmptyNoReason;

            return Failed(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupCannotBuildFormat, why), request.ArchivePath, started.Elapsed, plan.Notes);
        }

        string full;
        try
        {
            full = Path.GetFullPath(request.ArchivePath);
        }
        catch (Exception exception)
        {
            return Failed(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupArchivePathBrokenFormat, exception.Message), request.ArchivePath, started.Elapsed, plan.Notes);
        }

        var prepared = BackupPlanner.WithoutArchive(plan, full);
        var notes = new List<string>(prepared.Notes);

        if (request.ServerRunning)
        {
            notes.Add(PanelStrings.BackupEngineLiveCopyNote);
        }

        var manifest = BuildManifest(prepared, paths, engine);
        var sevenZipPath = request.SevenZipPath ?? SevenZip.Find();

        var created = ZipWriter.Write(
            full,
            prepared.Sources,
            sevenZipPath,
            ZipWriter.DefaultSkipDirectoryNames,
            progress,
            entries =>
            {
                // Опись описывает то, что в архиве ЕСТЬ. Присваиваем, а не дописываем: при смене
                // способа записи (7-Zip подвёл — пишем своими силами) дверь зовётся второй раз,
                // и добавление повторов показало бы в описи дерево дважды.
                manifest.TotalFiles = entries.Count;
                manifest.TotalBytes = entries.Sum(entry => entry.Length);
                manifest.Files = entries
                    .Select(entry => new BackupFileEntry { Path = entry.Name, Size = entry.Length })
                    .ToList();

                // Ключи доступа — ПО ЗАПИСЯМ: у «копии для передачи» файла в архиве нет, и опись
                // не должна утверждать обратное.
                manifest.WithCredentials = CredentialsLanded(prepared, entries);

                return JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions);
            });

        if (!created.Ok)
            return Failed(created.Error, full, created.Took, notes);

        var restricted = RestrictToOwner(full);
        if (!restricted.Ok)
        {
            notes.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupRightsNotRestrictedFormat, restricted.Reason));
        }

        bool? verified = null;
        if (request.Verify)
        {
            if (string.IsNullOrWhiteSpace(sevenZipPath))
            {
                notes.Add(PanelStrings.BackupNoVerifier);
            }
            else
            {
                var check = SevenZip.Verify(sevenZipPath, full);
                verified = check.Ok;

                if (!check.Ok)
                {
                    return new BackupRunResult(
                        false,
                        string.Format(
                            CultureInfo.CurrentCulture, PanelStrings.BackupVerifyFailedFormat,
                            Short(check.Output), DisplayMask.Path(full)),
                        full, created.Bytes, created.Files, created.SourceBytes, created.Writer, created.Reason,
                        created.Took, restricted.Ok, false, notes);
                }
            }
        }

        return new BackupRunResult(
            true, string.Empty, full, created.Bytes, created.Files, created.SourceBytes, created.Writer,
            created.Reason, created.Took, restricted.Ok, verified, notes);
    }

    // --- опись ---------------------------------------------------------------

    private static BackupManifest BuildManifest(BackupPlan plan, AppPaths paths, DshEngine? engine)
    {
        var manifest = new BackupManifest
        {
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            App = AppValue,
            AppVersion = typeof(BackupEngine).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Windows = Environment.OSVersion.VersionString,
            WithEngine = plan.Roots.Any(root => BackupRootKinds.IsEngine(root.Kind)),
            WithSessions = plan.Roots.Any(root => root.Kind == BackupRootKind.DshHome),

            // Заполняется ПО ЗАПИСЯМ АРХИВА в двери описи (см. ниже): до записи честного ответа
            // нет. Так «копия для передачи», из которой файл ключей исключён, не станет утверждать
            // в описи, что ключи в ней есть, — иначе накат ждал бы ключ, которого нет.
            WithCredentials = false,
        };

        foreach (var root in plan.Roots)
        {
            manifest.Paths[root.Prefix] = root.Directory;

            // Вид группы: без него накат на другой машине не знает, куда раскладывать
            // (в описи лежат пути машины-источника).
            manifest.Kinds[root.Prefix] = BackupRootKinds.Token(root.Kind);

            foreach (var link in ZipWriter.LinkDirectories(root.Source, ZipWriter.DefaultSkipDirectoryNames))
            {
                manifest.Links.Add(new BackupLink
                {
                    Path = root.Prefix + "/" + Path.GetRelativePath(root.Directory, link).Replace('\\', '/'),
                    Target = LinkTarget(link),
                });
            }
        }

        FillVersions(manifest, engine);
        return manifest;
    }

    /// <summary>
    /// Есть ли в архиве файл ключей доступа. Считается ПО ЗАПИСЯМ, а не по «файл есть на диске»:
    /// у «копии для передачи» его в архиве нет, и опись не должна утверждать обратное — иначе
    /// накат ждал бы ключ, которого нет.
    ///
    /// Группа берётся та, что снята с домашнего каталога движка, а путь сверяется ЦЕЛИКОМ:
    /// одноимённый файл в рабочей папке — не наш ключ, и о ключах он ничего не говорит.
    /// </summary>
    private static bool CredentialsLanded(BackupPlan plan, IReadOnlyList<ZipEntry> entries)
    {
        var home = plan.Roots.FirstOrDefault(root => root.Kind == BackupRootKind.DshHome);
        if (home is null) return false;

        var wanted = ZipLayout.NormalizePrefix(home.Prefix) + "/" + AppPaths.CredentialsFileName;

        return entries.Any(entry => string.Equals(
            ZipReader.Normalize(entry.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Версии, которые читаются ИЗ ФАЙЛОВ: движок, npm и pnpm.
    ///
    /// ⚠️ Версий node и dotnet здесь нет намеренно: их негде прочитать файлом, а спрашивать
    /// у программ значило бы запускать чужие процессы во время копии. v1 так делала; в 2.0
    /// это названо, а не унаследовано молча.
    /// </summary>
    private static void FillVersions(BackupManifest manifest, DshEngine? engine)
    {
        var npm = engine is null ? null : BackupPlanner.NpmDirectory(engine);
        if (npm is null) return;

        // Пакеты глобальной установки лежат ПОД каталогом, который мы кладём в копию:
        // `%APPDATA%\npm\node_modules\@deepseek-ai\dsh`.
        Put(manifest, "dsh", Path.Combine(npm, "node_modules", "@deepseek-ai", "dsh"));
        Put(manifest, "npm", Path.Combine(npm, "node_modules", "npm"));
        Put(manifest, "pnpm", Path.Combine(npm, "node_modules", "pnpm"));
    }

    /// <summary>
    /// Версия движка, который стоит на машине СЕЙЧАС, — тем же способом, каким она попадает в опись
    /// (<see cref="FillVersions"/>): собирается пустая опись, и в неё кладётся ровно одна запись.
    ///
    /// Зачем снаружи: находка В5 (<c>docs\ROADMAP.md</c>) — копию, снятую одной версией движка,
    /// другая версия может не открыть. Человеку это надо сказать ДО наката, а для этого нужно
    /// сравнить версию из описи с ТЕКУЩЕЙ. Второго способа узнать версию в панели нет и быть
    /// не должно: путь к установке знает <see cref="BackupPlanner.NpmDirectory"/>, и он один.
    /// </summary>
    public static string InstalledVersion(DshEngine? engine)
    {
        var npm = engine is null ? null : BackupPlanner.NpmDirectory(engine);
        if (npm is null) return string.Empty;

        var probe = new BackupManifest();
        Put(probe, "dsh", Path.Combine(npm, "node_modules", "@deepseek-ai", "dsh"));

        return probe.Versions.TryGetValue("dsh", out var version) ? version : string.Empty;
    }

    private static void Put(BackupManifest manifest, string name, string packageDirectory)
    {
        try
        {
            var path = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("version", out var version))
                manifest.Versions[name] = version.GetString() ?? string.Empty;
        }
        catch
        {
            // Версия — вспомогательная часть описи: без неё копия всё равно годится.
        }
    }

    private static string LinkTarget(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).LinkTarget ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // --- права ---------------------------------------------------------------

    /// <summary>
    /// Чем кончилось закрытие прав. <see cref="Ok"/> — получилось ли; <see cref="Reason"/> — что
    /// именно не удалось, словами.
    ///
    /// Отдельная запись, а не <c>bool</c>, по тому же правилу, что у находки В6 и дефекта Д6:
    /// причина обязана доехать до человека. До 26.09.2026 отказ был одним <c>false</c>, и отчёт
    /// говорил «ключи не закрыты» без единого слова о том, что случилось.
    /// </summary>
    public sealed record RestrictResult(bool Ok, string Reason)
    {
        public static readonly RestrictResult Done = new(true, string.Empty);

        public static RestrictResult Failed(string reason) => new(false, reason);
    }

    /// <summary>
    /// Закрывает ФАЙЛ (архив) или КАТАЛОГ (восстановленный каталог ключей) на владельца и SYSTEM:
    /// снимает наследование и оставляет ровно эти две учётные записи.
    ///
    /// ⚠️ **Правила НАСЛЕДУЮТСЯ детьми, и это не украшение** (дефект Д8, найден прогоном на живом
    /// носителе 26.09.2026). Прежняя редакция ставила правила без флагов наследования
    /// (<c>InheritanceFlags.None</c>) и применяла их к каталогу с <c>preserveInheritance: false</c>.
    /// Windows в этом случае снимает у детей ВСЁ унаследованное, а новых наследуемых правил
    /// у каталога нет — итог: у восстановленных файлов ключей **пустой список доступа**,
    /// и владелец не может прочитать собственный ключ (следующий накат падал на предохранительной
    /// копии: «Access to the path … is denied»). Проверка этого не видела, потому что смотрела
    /// КАТАЛОГ, а страдал ФАЙЛ.
    ///
    /// Поэтому: правила объявляются наследуемыми (<c>ContainerInherit | ObjectInherit</c>),
    /// а КАТАЛОГ дополнительно закрывается вместе со своим содержимым — повторным применением
    /// тех же правил к детям. Без второго шага уже разложенные файлы остались бы без доступа:
    /// снятие наследования у родителей не даёт детям доступа задним числом.
    ///
    /// Возвращает <see cref="RestrictResult"/>: <c>false</c> значит «закрыть не удалось», и об этом
    /// вызывающий обязан сказать человеку, а не считать копию с ключами безопасной.
    ///
    /// Каталог закрывается тем же правилом, что и архив, и это не расширение «на всякий случай»:
    /// в v1 накат закрывал восстановленные каталоги ключей на владельца, а у нас ключи возвращаются
    /// **по согласию**, то есть каталог с приватными ключами появляется на диске — и он обязан быть
    /// закрыт так же, как закрыт сам архив (находка В4, `ROADMAP.md`).
    ///
    /// Права ставятся средствами платформы, а не через <c>icacls</c>, как в v1: чужой процесс
    /// ради двух строк — лишняя зависимость, а его вывод ещё и локализован (v1 поэтому и
    /// передавала ему SID числами).
    /// </summary>
    public static RestrictResult RestrictToOwner(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RestrictResult.Failed(PanelStrings.BackupPathNotSet);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var user = WindowsIdentity.GetCurrent().User;
                if (user is null) return RestrictResult.Failed(PanelStrings.BackupUserUnknown);

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

                // У каталога и файла правило одно, а объекты разные — отсюда две ветви.
                var isDirectory = Directory.Exists(path);
                if (!isDirectory && !File.Exists(path))
                    return RestrictResult.Failed(PanelStrings.BackupPathMissing);

                // Наследуемость — ТОЛЬКО у каталога: у файла детей не бывает, и объявлять её там
                // значило бы писать в права то, чего в них нет.
                var inherit = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;

                if (isDirectory)
                {
                    var security = new DirectorySecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(
                        system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

                    new DirectoryInfo(path).SetAccessControl(security);

                    // Дети: снятие наследования у родителя НЕ даёт им доступа задним числом, поэтому
                    // то же правило применяется к содержимому — иначе файлы остались бы с пустым DACL.
                    GiveChildren(path, user, system, inherit);
                    return RestrictResult.Done;
                }

                var file = new FileSecurity();
                file.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                file.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                file.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

                new FileInfo(path).SetAccessControl(file);
                return RestrictResult.Done;
            }

            // На других системах правило то же, выражено их способом.
            if (Directory.Exists(path))
            {
                File.SetUnixFileMode(
                    path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return RestrictResult.Done;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return RestrictResult.Done;
        }
        catch (Exception error)
        {
            // Причина называется словами (дефект Д8): «закрыть не удалось» без неё человеку
            // не говорит ничего — он не знает, что именно поправить.
            return RestrictResult.Failed($"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Отдаёт детям каталога то же правило, что стоит на нём самом, — и спускается глубже.
    ///
    /// Нужно потому, что снятие наследования у родителя действует ВПЕРЁД: файл, у которого доступ
    /// был унаследован, теряет его и остаётся с пустым списком доступа. У восстановленного каталога
    /// ключей файлы уже лежат, значит их надо закрыть тем же правилом явно.
    ///
    /// Идём ЗВЕНЬЯМИ и не следуем по ссылкам: рекурсивный обход по дереву со ссылкой внутри —
    /// известный способ уйти не туда (правило проекта). Ссылка здесь маловероятна, но цена ошибки
    /// несоразмерна: под ссылкой лежит чужой каталог.
    ///
    /// <see cref="SupportedOSPlatformAttribute"/> здесь не украшение: средства доступа к спискам
    /// доступа существуют только на Windows, и без пометки сборка даёт предупреждения о платформе
    /// (а правило проекта — ноль предупреждений). Вызывается только из ветви <c>IsWindows</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void GiveChildren(string directory, SecurityIdentifier user, SecurityIdentifier system, InheritanceFlags inherit)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var isLink = false;
            try
            {
                isLink = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                // Атрибуты не прочитать — считаем обычным: ниже это выяснится само.
            }

            if (isLink) continue;

            if (Directory.Exists(child))
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

                new DirectoryInfo(child).SetAccessControl(security);
                GiveChildren(child, user, system, inherit);
                continue;
            }

            var file = new FileSecurity();
            file.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            file.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            file.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

            new FileInfo(child).SetAccessControl(file);
        }
    }

    // --- мелочи --------------------------------------------------------------

    private static BackupRunResult Failed(string error, string archivePath, TimeSpan took, IReadOnlyList<string> notes) =>
        new(false, error, archivePath, 0, 0, 0, ZipWriterKind.BuiltIn, string.Empty, took, false, null, notes);

    /// <summary>
    /// Вывод чужой программы в строку отчёта: обрезаем и маскируем — в нём есть путь архива,
    /// а показать его в журнале без маскировки значит вынести имя пользователя (красная линия 7).
    /// </summary>
    private static string Short(string? output)
    {
        var text = (output ?? string.Empty).Trim();
        if (text.Length == 0) return PanelStrings.BackupReasonUnnamed;

        text = DisplayMask.Path(text.Replace('\r', ' ').Replace('\n', ' '));
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
