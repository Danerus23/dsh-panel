using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DshPanel.Shell;
using System.Globalization;

namespace DshPanel.Backup;

/// <summary>Чем кончилась запись архива: годится ли он, сколько в нём файлов и чем он снят.</summary>
public sealed record ZipCreateResult(
    bool Ok,
    string Error,
    string Path,
    long Bytes,
    int Files,
    long SourceBytes,
    ZipWriterKind Writer,
    string Reason,
    TimeSpan Took)
{
    public static ZipCreateResult Fail(string error, string path, TimeSpan took) =>
        new(false, error, path, 0, 0, 0, ZipWriterKind.BuiltIn, string.Empty, took);

    /// <summary>Строка для отчёта: «копия снята: 2,1 ГБ, 31 402 файла, 5,3 с, 7-Zip: …».</summary>
    public string Summary() =>
        Ok
            ? string.Format(
                CultureInfo.CurrentCulture, PanelStrings.ZipSummaryDoneFormat,
                BackupFormat.Size(Bytes), BackupFormat.Size(SourceBytes), Files,
                BackupFormat.Duration(Took), Reason)
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipNotCreatedFormat, Error);
}

/// <summary>
/// Запись архива — ОБОИМИ способами, как решил владелец 25.09.2026: контейнер ZIP, создаёт 7-Zip,
/// распаковывает сама панель.
///
/// Почему оба способа живут в одном месте и почему быстрый путь может отказать:
///
/// * 7-Zip **хранит путь ровно так, как тот задан относительно его рабочего каталога**
///   (проба 25.09.2026, разбор — <see cref="ZipLayout"/>). Значит имя группы обязано совпасть
///   с именем папки на диске; вложенное имя и корень диска закрывают быстрый путь;
/// * 7-Zip может просто **не найтись** — на этой машине его нет в PATH, и это не редкость;
/// * 7-Zip может **найтись, но подвести** (не запуститься, отказать на занятом файле).
///
/// Во всех трёх случаях панель обязана снять копию, а не отказаться: **чужой программы в условии
/// копии быть не должно.** Поэтому отказ быстрого пути — это не ошибка, а смена способа, и причина
/// уезжает в отчёт словами.
///
/// Оба способа кладут **одно и то же дерево** — это не обещание, а проверка
/// (<c>BackupTests.Оба_способа_кладут_одно_и_то_же_дерево</c>): архивы распаковываются и деревья
/// сравниваются файл за файлом.
///
/// **Опись — отдельная дверь (<c>manifest</c>), и она работает в обоих способах.** Вызывающему
/// отдаётся список записей, которые УЖЕ легли в архив, а он возвращает текст описи: так опись
/// описывает то, что в архиве действительно есть, а не то, что мы надеялись туда положить.
/// ⚠️ Через <c>ZipArchiveMode.Update</c> это не делается: он тянет весь архив в память, а копия
/// бывает в сотни мегабайт. Поэтому свой способ пишет опись в том же проходе, а быстрый кладёт
/// её отдельным запуском 7-Zip уже после дерева.
/// </summary>
public static class ZipWriter
{
    /// <summary>
    /// Мусор, который в копию не попадает: собирается заново или весит слишком много.
    /// Список перенесён из v1 без изменений — каждый пункт там оплачен случаем (диски ВМ,
    /// разросшаяся копия, вывод Gradle).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSkipDirectoryNames = new[]
    {
        "node_modules", ".git", ".pnpm-store", ".dotnet-home", ".appdata", ".nuget", "bin", "obj", "logs",
        "app-staging", "build", ".gradle", ".kotlin",
    };

    /// <summary>
    /// С какого размера файл жмут быстрым уровнем. Порог из v1 (<c>BackupService.cs:439</c>)
    /// и он оплачен случаем: крупные файлы (движок и Node) на «оптимальном» уровне собирались
    /// почти семь минут, и копия выглядела зависшей. Мелкие настройки и скрипты жмутся как
    /// раньше — там это дёшево и даёт выигрыш.
    /// </summary>
    public const long LargeFileBytes = 1_000_000;

    /// <summary>
    /// Пишет ОДИН архив из всех источников. Существующий файл по этому пути заменяется:
    /// копия с тем же именем — это либо прежняя копия, либо обрывок прошлого прогона,
    /// и дописывать в него нельзя.
    /// </summary>
    /// <param name="manifest">
    /// Дверь для описи: получает записи, которые уже легли в архив, и возвращает текст описи
    /// (пусто — описи не будет). Вызывающий заполняет по ним число файлов и объём.
    /// </param>
    public static ZipCreateResult Write(
        string archivePath,
        IReadOnlyList<BackupSource> sources,
        string? sevenZipPath = null,
        IReadOnlyList<string>? skipDirectoryNames = null,
        Action<string>? progress = null,
        Func<IReadOnlyList<ZipEntry>, string>? manifest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(sources);

        skipDirectoryNames ??= DefaultSkipDirectoryNames;
        var started = Stopwatch.StartNew();
        var full = Path.GetFullPath(archivePath);

        try
        {
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception exception)
        {
            return ZipCreateResult.Fail(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipCreateFailedFormat, DisplayMask.Path(full), exception.Message), full, started.Elapsed);
        }

        var choice = ZipLayout.ChooseWriter(sources, sevenZipPath);
        var writer = choice.Writer;
        var reason = choice.Reason;

        if (writer == ZipWriterKind.SevenZip)
        {
            var run = AddWithSevenZip(full, sources, sevenZipPath!, skipDirectoryNames, manifest, progress);
            if (!run.Ok)
            {
                reason = string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipSevenZipFailedFormat, run.Code, run.Output);
                writer = ZipWriterKind.BuiltIn;
                TryDelete(full);
            }
        }

        long sourceBytes;
        var builtInFiles = 0;

        if (writer == ZipWriterKind.BuiltIn)
        {
            var built = WriteBuiltIn(full, sources, skipDirectoryNames, manifest, progress);
            if (!built.Ok) return ZipCreateResult.Fail(built.Error, full, started.Elapsed);

            sourceBytes = built.SourceBytes;
            builtInFiles = built.Files;
        }
        else
        {
            sourceBytes = Measure(sources, skipDirectoryNames, full);
        }

        // Число файлов берём ИЗ САМОГО АРХИВА, а не из своего обхода: тогда отчёт говорит о том,
        // что действительно легло, и оба способа отвечают одинаково. В v1 на этом уже спотыкались —
        // строка отчёта расходилась с числом записанных файлов. Опись, если она писалась, в это
        // число входит: она такая же запись архива.
        var files = CountFileEntries(full);
        if (files < 0)
            return ZipCreateResult.Fail(PanelStrings.ZipCreatedUnreadable, full, started.Elapsed);

        if (builtInFiles > 0 && files == 0)
            return ZipCreateResult.Fail(PanelStrings.ZipCreatedEmpty, full, started.Elapsed);

        long bytes;
        try
        {
            bytes = new FileInfo(full).Length;
        }
        catch
        {
            bytes = 0;
        }

        return new ZipCreateResult(true, string.Empty, full, bytes, files, sourceBytes, writer, reason, started.Elapsed);
    }

    // --- быстрый путь: 7-Zip -------------------------------------------------

    private static SevenZipRun AddWithSevenZip(
        string archivePath,
        IReadOnlyList<BackupSource> sources,
        string sevenZipPath,
        IReadOnlyList<string>? skipDirectoryNames,
        Func<IReadOnlyList<ZipEntry>, string>? manifest,
        Action<string>? progress)
    {
        var last = new SevenZipRun(true, 0, string.Empty);

        foreach (var source in sources)
        {
            progress?.Invoke(source.Prefix);

            // Корни движка и Node обходятся целиком: там node_modules и есть содержимое,
            // и выбрасывать его нельзя (урок v1).
            var skip = source.SkipFor(skipDirectoryNames);

            // Каталоги, которые берутся целиком, из основного прохода ИСКЛЮЧАЮТСЯ и кладутся
            // отдельным запуском без исключений: у 7-Zip исключения действуют на весь обход
            // сразу, и «не пропускать только внутри .git» он сказать не умеет.
            var whole = source.Everything ? null : source.WholeDirectoryNames;
            var excluded = Union(skip, whole);

            // Архив не кладёт сам себя: если он лежит внутри корня, 7-Zip читал бы файл,
            // который сам же и пишет.
            excluded = Union(excluded, new[] { Path.GetFileName(archivePath) });

            // Файловый пропуск — ОТДЕЛЬНЫМ списком, хотя 7-Zip исключает и каталоги, и файлы одним
            // ключом -xr!: списки разные по смыслу, и свалить их в один значило бы однажды
            // «пропустить каталог» там, где просили пропустить файл. Пустые списки 7-Zip не увидит.
            last = SevenZip.Add(sevenZipPath, archivePath, source, excluded, source.SkipFileNames);
            if (!last.Ok) return last;

            // «Целые» каталоги кладутся ОТДЕЛЬНЫМ запуском 7-Zip, без исключений: исключения
            // действуют на весь обход сразу, и «не пропускать только внутри .git» он не умеет.
            // Путь собирается вместе с именем группы: рабочий каталог 7-Zip — родитель корня,
            // поэтому запись `projects\proj\.git` задаётся как «projects/proj/.git».
            var prefix = ZipLayout.Leaf(source.Directory);

            foreach (var relative in WholeDirectories(source, skipDirectoryNames))
            {
                progress?.Invoke(source.Prefix + "/" + relative);

                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Directory)));
                if (string.IsNullOrEmpty(parent)) continue;

                last = SevenZip.AddRelative(sevenZipPath, archivePath, parent, prefix + "/" + relative);
                if (!last.Ok) return last;
            }
        }

        if (manifest is null) return last;

        return AddManifestWithSevenZip(sevenZipPath, archivePath, manifest);
    }

    /// <summary>
    /// Кладёт опись отдельным запуском 7-Zip: дерево уже записано, читаем его записи и просим
    /// у вызывающего текст. Временный файл живёт в <c>%TEMP%</c> и убирается за собой.
    /// </summary>
    private static SevenZipRun AddManifestWithSevenZip(
        string sevenZipPath,
        string archivePath,
        Func<IReadOnlyList<ZipEntry>, string> manifest)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dsh-manifest-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var read = ZipReader.Read(archivePath);
            if (!read.Ok)
                return SevenZipRun.Failed(string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipEntriesUnreadableFormat, read.Error));

            var text = manifest(read.Entries);
            if (string.IsNullOrWhiteSpace(text)) return new SevenZipRun(true, 0, string.Empty);

            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ZipLayout.ManifestEntry), text, new UTF8Encoding(false));

            return SevenZip.AddRelative(sevenZipPath, archivePath, folder, ZipLayout.ManifestEntry);
        }
        catch (Exception exception)
        {
            return SevenZipRun.Failed(string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipManifestNotWrittenFormat, exception.GetType().Name, exception.Message));
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Временный каталог в %TEMP% — если не убрался, это не провал копии.
            }
        }
    }

    // --- свой путь: обход плюс System.IO.Compression --------------------------

    private sealed record BuiltInOutcome(bool Ok, string Error, int Files, long SourceBytes);

    private static BuiltInOutcome WriteBuiltIn(
        string archivePath,
        IReadOnlyList<BackupSource> sources,
        IReadOnlyList<string>? skipDirectoryNames,
        Func<IReadOnlyList<ZipEntry>, string>? manifest,
        Action<string>? progress)
    {
        var files = 0;
        long sourceBytes = 0;
        var entries = manifest is null ? null : new List<ZipEntry>();

        try
        {
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);

            foreach (var source in sources)
            {
                progress?.Invoke(source.Prefix);

                var root = Path.GetFullPath(source.Directory);
                if (!Directory.Exists(root)) continue;

                var prefix = ZipLayout.NormalizePrefix(source.Prefix);
                var skip = Set(source.SkipFor(skipDirectoryNames));
                var whole = source.Everything ? null : Set(source.WholeDirectoryNames);

                foreach (var file in WalkFiles(root, skip, whole, Set(source.SkipFileNames)))
                {
                    // Архив не кладёт сам себя: без этого правила он читал бы файл, который
                    // в этот самый момент пишет.
                    if (string.Equals(file, archivePath, StringComparison.OrdinalIgnoreCase)) continue;

                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var name = prefix + "/" + relative;
                    long length;
                    try
                    {
                        length = new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Файл исчез между обходом и записью — копия не сломана.
                        continue;
                    }

                    // Д6: отказ обязан НАЗВАТЬ ФАЙЛ и причину. До 26.09.2026 исключение летело
                    // в общий catch ниже, отчёт говорил «копия не снята» без виновника, а один
                    // запертый файл ронял копию целиком (проверено прогоном: вывод прогона,
                    // направленный в копируемый каталог, дал ровно это). Пропустить файл молча
                    // нельзя: копия стала бы неполной, но выглядела бы целой. Поэтому отказ
                    // остаётся отказом — но теперь с именем файла и типом ошибки.
                    try
                    {
                        var entry = zip.CreateEntry(name, LevelFor(length));

                        using (var target = entry.Open())
                        using (var origin = File.OpenRead(file))
                        {
                            origin.CopyTo(target);
                        }
                    }
                    catch (Exception error)
                    {
                        return new BuiltInOutcome(false, FileRefusal(name, file, error), files, sourceBytes);
                    }

                    files++;
                    sourceBytes += length;
                    entries?.Add(new ZipEntry(name, length));
                }
            }

            if (manifest is not null)
            {
                progress?.Invoke(ZipLayout.ManifestEntry);

                var text = manifest(entries!);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Опись — не украшение: без неё архив не наша копия, и накат не знает,
                    // куда что раскладывать. Поэтому её отсутствие — провал, а не «и так сойдёт».
                    WriteText(zip, ZipLayout.ManifestEntry, text);
                }
            }
        }
        catch (Exception exception)
        {
            return new BuiltInOutcome(false, $"{exception.GetType().Name}: {exception.Message}", files, sourceBytes);
        }

        return new BuiltInOutcome(true, string.Empty, files, sourceBytes);
    }

    /// <summary>
    /// Отказ копии, называющий ВИНОВНИКА (дефект Д6): путь внутри копии, путь на диске (через
    /// маскировку — красная линия 7) и тип ошибки с её словами. Причина отказа без имени файла
    /// не помогает человеку: он не знает, что закрыть, чтобы копия снялась.
    /// </summary>
    public static string FileRefusal(string name, string file, Exception error) =>
        string.Format(
            CultureInfo.CurrentCulture, PanelStrings.ZipFileUnreadableFormat,
            name, DisplayMask.Path(file), error.GetType().Name, Short(error.Message));

    /// <summary>
    /// Уровень сжатия по размеру файла: крупные — быстрым, остальные — обычным.
    /// Порог и причина — <see cref="LargeFileBytes"/>.
    /// </summary>
    private static CompressionLevel LevelFor(long length) =>
        length > LargeFileBytes ? CompressionLevel.Fastest : CompressionLevel.Optimal;

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    // --- обход дерева --------------------------------------------------------

    /// <summary>
    /// Обход дерева с пропуском служебных каталогов и ТОЧЕК ПОВТОРНОЙ ОБРАБОТКИ.
    ///
    /// Второе — не осторожность, а необходимость: в пакете движка есть САМОССЫЛАЮЩИЙСЯ junction
    /// (<c>node_modules\@deepseek-ai\dsh</c> → корень пакета), и наивный обход уходит в него на
    /// десятки уровней. В замере это дало 943 МБ мусора и ни одной годной копии
    /// (<c>docs\ARCHIVE-MEASUREMENT.md</c>, находка 1). Ссылки при этом не теряются: они едут
    /// в описи (<c>BackupManifest.Links</c>), и накат создаёт их заново.
    ///
    /// Каталог, попавший в <paramref name="whole"/>, обходится ЦЕЛИКОМ: список пропуска внутри
    /// него не действует. Так сохраняется <c>.git</c> вместе со своим <c>logs</c>.
    ///
    /// <paramref name="skipFiles"/> — имена ФАЙЛОВ, которые не отдаются вовсе. Нужно «копии для
    /// передачи»: файл ключей доступа не должен уехать в архив, которым делятся. Сверка по ИМЕНИ,
    /// а не по пути: так умеет и 7-Zip (<c>-xr!</c>), и разойтись эти два способа не должны.
    /// </summary>
    private static IEnumerable<string> WalkFiles(
        string root, ISet<string>? skip, ISet<string>? whole, ISet<string>? skipFiles = null)
    {
        foreach (var (directory, _) in WalkDirectories(root, skip, whole))
        {
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
                if (skipFiles is not null && skipFiles.Contains(Path.GetFileName(file))) continue;

                yield return file;
            }
        }
    }

    /// <summary>
    /// Каталоги, которые будут скопированы, — в том же порядке обхода и по тем же правилам,
    /// что и файлы. Отдельным входом это нужно описи: ссылки (<c>FileAttributes.ReparsePoint</c>)
    /// записываются только те, что лежат ВНУТРИ копируемого дерева, — иначе опись предлагала бы
    /// накату создать ссылку на то, чего в копии нет.
    /// </summary>
    public static IEnumerable<string> Directories(BackupSource source, IReadOnlyList<string>? runSkip = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var (path, link) in WalkTree(source, runSkip))
            if (!link) yield return path;
    }

    /// <summary>
    /// Точки повторной обработки, которые лежат внутри копируемого дерева, — их список уезжает
    /// в опись (<c>BackupManifest.Links</c>), и накат создаёт их заново.
    ///
    /// Без этого восстановленный профиль движка не работает: почти весь
    /// <c>profiles\node_modules</c> — это junction-ссылки на глобальную установку, а архив
    /// (и распаковщик 7-Zip тем более) воспроизводит их пустым каталогом либо файлом в ноль байт
    /// (<c>docs\ARCHIVE-MEASUREMENT.md</c>, находка 3).
    /// </summary>
    public static IEnumerable<string> LinkDirectories(BackupSource source, IReadOnlyList<string>? runSkip = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var (path, link) in WalkTree(source, runSkip))
            if (link) yield return path;
    }

    private static IEnumerable<(string Path, bool Link)> WalkTree(BackupSource source, IReadOnlyList<string>? runSkip)
    {
        var root = Path.GetFullPath(source.Directory);
        if (!Directory.Exists(root)) yield break;

        var skip = Set(source.SkipFor(runSkip));
        var whole = source.Everything ? null : Set(source.WholeDirectoryNames);

        foreach (var item in WalkDirectories(root, skip, whole, links: true)) yield return item;
    }

    /// <summary>
    /// Обход каталогов дерева. <paramref name="links"/> — сообщать ли о точках повторной обработки:
    /// их не обходят никогда, но описи о них знать нужно, поэтому они отдаются отдельными
    /// элементами и только по запросу.
    /// </summary>
    private static IEnumerable<(string Path, bool Link)> WalkDirectories(
        string root,
        ISet<string>? skip,
        ISet<string>? whole,
        bool links = false)
    {
        var stack = new Stack<(string Directory, bool Whole)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (directory, insideWhole) = stack.Pop();
            yield return (directory, false);

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
                var name = Path.GetFileName(sub);
                var wholeHere = false;

                if (!insideWhole)
                {
                    // «Целый» каталог главнее списка пропуска — это его смысл: `.git` стоит
                    // и в пропуске (общее правило мусора), и в «берём целиком».
                    if (whole is not null && whole.Contains(name)) wholeHere = true;
                    else if (skip is not null && skip.Contains(name)) continue;
                }

                // Точка повторной обработки не обходится никогда — даже внутри «целого» каталога:
                // именно она и уводит обход на десятки уровней.
                bool isLink;
                try
                {
                    isLink = (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0;
                }
                catch
                {
                    continue;
                }

                if (isLink)
                {
                    if (links) yield return (sub, true);
                    continue;
                }

                stack.Push((sub, insideWhole || wholeHere));
            }
        }
    }

    /// <summary>
    /// Каталоги, которые берутся целиком и лежат на диске, — путями ОТНОСИТЕЛЬНО корня,
    /// по возрастанию глубины. Пустой ответ — таких нет (в том числе если корень обходится
    /// целиком: там исключений нет вовсе).
    ///
    /// ⚠️ Обход здесь идёт по списку пропуска БЕЗ имён «целых» каталогов: иначе <c>.git</c>
    /// отсекло бы тем же правилом, ради обхода которого он и ищется. Спускаться внутрь
    /// найденного не нужно — <c>.git</c> внутри <c>.git</c> не бывает.
    /// </summary>
    private static IReadOnlyList<string> WholeDirectories(BackupSource source, IReadOnlyList<string>? runSkip)
    {
        var wanted = Set(source.WholeDirectoryNames);
        if (wanted is null || wanted.Count == 0) return Array.Empty<string>();

        var root = Path.GetFullPath(source.Directory);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        var skip = Set(source.SkipFor(runSkip));
        var navigation = skip?.Where(name => !wanted.Contains(name)).ToArray();

        var found = new List<string>();

        foreach (var (directory, _) in WalkDirectories(root, Set(navigation), null))
        {
            if (directory.Length <= root.Length) continue;
            if (!wanted.Contains(Path.GetFileName(directory))) continue;

            found.Add(Path.GetRelativePath(root, directory).Replace('\\', '/'));
        }

        // Короткие пути первыми: 7-Zip кладёт их в порядке вызова, и так дерево читается.
        found.Sort((left, right) => left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right));

        return found;
    }

    /// <summary>
    /// Сколько БАЙТ данных попадёт в копию по этому набору источников: сумма размеров файлов,
    /// которые пройдут обход, — с учётом списков пропуска и «целых» каталогов
    /// (<see cref="BackupSource.Everything"/> и <see cref="BackupSource.SkipFor"/>).
    ///
    /// Это НЕ размер архива: сжатие заранее не посчитать. Это размер ДАННЫХ — и он-то и нужен
    /// человеку, который выбирает режим объёма: полный режим бывает в разы больше автоматического.
    ///
    /// Метод открыт ДВУМЯ читателями, и оба про одно — «сколько это будет»:
    ///
    /// * **окно копий** — предупреждение о размере ДО начала копии (решение владельца: «полный —
    ///   с честным предупреждением о размере»);
    /// * **сам писатель** — строка отчёта «снято X из Y» на быстром пути (<see cref="Write"/>).
    ///
    /// Поэтому второго обхода дерева в панели нет и быть не должно: разошлись бы правила пропуска,
    /// и обещанный размер перестал бы совпадать с настоящим.
    /// </summary>
    public static long EstimateBytes(IReadOnlyList<BackupSource> sources, string? archivePath = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return Measure(sources, DefaultSkipDirectoryNames, archivePath);
    }

    private static long Measure(
        IReadOnlyList<BackupSource> sources,
        IReadOnlyList<string>? skipDirectoryNames,
        string? archivePath)
    {
        long bytes = 0;

        foreach (var source in sources)
        {
            var root = Path.GetFullPath(source.Directory);
            if (!Directory.Exists(root)) continue;

            var skip = Set(source.SkipFor(skipDirectoryNames));
            var whole = source.Everything ? null : Set(source.WholeDirectoryNames);

            foreach (var file in WalkFiles(root, skip, whole, Set(source.SkipFileNames)))
            {
                if (archivePath is not null && string.Equals(file, archivePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // Файл исчез между обходом и замером — размер не тот, копия не сломана.
                }
            }
        }

        return bytes;
    }

    /// <summary>Список пропуска как множество: сверка идёт без учёта регистра, как в v1.</summary>
    private static ISet<string>? Set(IReadOnlyList<string>? names) =>
        names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string>? Union(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || left.Count == 0) return right;
        if (right is null || right.Count == 0) return left;

        var merged = new List<string>(left);
        foreach (var name in right)
            if (!merged.Contains(name, StringComparer.OrdinalIgnoreCase)) merged.Add(name);

        return merged;
    }

    /// <summary>
    /// Сколько в архиве ФАЙЛОВ (записи-каталоги не считаем). Ответ <c>-1</c> — архив не читается:
    /// это не «ноль файлов», и путать их нельзя.
    /// </summary>
    public static int CountFileEntries(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var count = 0;

            foreach (var entry in zip.Entries)
            {
                // Разделитель нормализуем: 7-Zip кладёт прямую косую черту, но полагаться
                // на соглашение одного инструмента в разборе чужого файла нельзя.
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;
                if (entry.Length == 0 && name.Length == 0) continue;
                count++;
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Не смогли убрать обрывок — следующая запись всё равно начинает с удаления.
        }
    }

    /// <summary>
    /// Чужие слова в строку отчёта: обрезаем и убираем переводы строк. Маскировку пути делает
    /// <see cref="DisplayMask"/> у вызывающего — здесь только текст ошибки.
    /// </summary>
    private static string Short(string? text)
    {
        var trimmed = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (trimmed.Length == 0) return PanelStrings.ZipReasonUnnamed;

        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
