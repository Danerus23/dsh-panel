using System.Diagnostics;
using DshPanel.Shell;
using System.Globalization;

namespace DshPanel.Backup;

/// <summary>Чем кончился запуск 7-Zip: код возврата и то, что он сказал (для журнала и отчёта).</summary>
public sealed record SevenZipRun(bool Ok, int Code, string Output)
{
    public static SevenZipRun Failed(string output) => new(false, -1, output);
}

/// <summary>
/// Поиск и запуск 7-Zip.
///
/// ПОЧЕМУ ПО КАНДИДАТАМ, А НЕ ПО ИМЕНИ. «Установлен и не в PATH» — не выдумка, а состояние
/// этой самой машины 25.09.2026: `Get-Command 7z` пусто, а `C:\Program Files\7-Zip\7z.exe`
/// на месте. Программа, которая зовёт чужой инструмент по имени, у такого человека скажет
/// «7-Zip не найден» и уйдёт на медленный путь — при том что он есть.
///
/// И обратное тоже верно: **отсутствие 7-Zip ничего не ломает** (см. <see cref="ZipLayout.ChooseWriter"/>).
/// Он ускоряет съёмку копии, но не является её условием: накат читает ZIP средствами .NET и
/// без 7-Zip, и с ним одинаково.
/// </summary>
public static class SevenZip
{
    /// <summary>
    /// Подмена пути к 7-Zip на один прогон — по образцу остальных подмен панели
    /// (<c>DSH_TRAY_NODE</c> в v1). Нужна и человеку с переносимым 7-Zip, и проверкам.
    /// </summary>
    public const string EnvOverride = "DSH_PANEL_7ZIP";

    /// <summary>Имя файла программы на Windows.</summary>
    public const string ExeName = "7z.exe";

    /// <summary>
    /// Где искать 7-Zip — по порядку, от самого вероятного к самому общему.
    ///
    /// Функция ЧИСТАЯ: она ничего не проверяет и никуда не ходит, поэтому её правила можно
    /// перебрать проверкой. Ищет — <see cref="Find"/>.
    /// </summary>
    public static IReadOnlyList<string> Candidates(
        string? overridePath = null,
        string? programFiles = null,
        string? programFilesX86 = null,
        string? localAppData = null,
        string? programData = null,
        string? userProfile = null)
    {
        var list = new List<string>();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            list.Add(Path.Combine(directory.Trim(), "7-Zip", ExeName));
        }

        // 1. Явная подмена — она главнее всего: если человек сказал, где лежит 7-Zip, спорить нечем.
        if (!string.IsNullOrWhiteSpace(overridePath)) list.Add(overridePath.Trim());

        // 2. Обычная установка (в том числе winget и установщик с сайта).
        Add(programFiles);
        Add(programFilesX86);

        // 3. Установка только для пользователя — так ставит winget без прав администратора.
        Add(localAppData is null ? null : Path.Combine(localAppData, "Programs"));
        if (!string.IsNullOrWhiteSpace(localAppData))
            list.Add(Path.Combine(localAppData.Trim(), "7-Zip", ExeName));

        // 4. Пакетные менеджеры: chocolatey и scoop ставят в свои каталоги.
        if (!string.IsNullOrWhiteSpace(programData))
            list.Add(Path.Combine(programData.Trim(), "chocolatey", "bin", ExeName));

        if (!string.IsNullOrWhiteSpace(userProfile))
            list.Add(Path.Combine(userProfile.Trim(), "scoop", "shims", ExeName));

        return list;
    }

    /// <summary>
    /// Находит 7-Zip. Порядок: подмена, кандидаты, затем PATH.
    ///
    /// PATH — ПОСЛЕДНИМ, а не первым: «нашлось в PATH» и «нашлось по известному месту» —
    /// разные ответы, и первый из них может принадлежать чужой копии неизвестной версии.
    /// Все три двери наружу (существование файла, переменные среды, PATH) — подменяемые,
    /// чтобы проверка не зависела от того, что стоит на машине.
    /// </summary>
    public static string? Find(
        Func<string, bool>? fileExists = null,
        Func<string, string?>? environment = null,
        Func<IEnumerable<string>>? pathDirectories = null)
    {
        fileExists ??= File.Exists;
        environment ??= Environment.GetEnvironmentVariable;

        foreach (var candidate in Candidates(
                     overridePath: environment(EnvOverride),
                     programFiles: environment("ProgramFiles"),
                     programFilesX86: environment("ProgramFiles(x86)"),
                     localAppData: environment("LOCALAPPDATA"),
                     programData: environment("ProgramData"),
                     userProfile: environment("USERPROFILE")))
        {
            if (fileExists(candidate)) return candidate;
        }

        foreach (var directory in (pathDirectories ?? PathFromEnvironment)())
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), ExeName);
            }
            catch
            {
                continue;
            }

            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> PathFromEnvironment() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';');

    /// <summary>
    /// Запускает 7-Zip и ждёт его. Рабочий каталог задаёт вызывающий, и это не мелочь:
    /// именно от него зависит, под каким именем корень ляжет в архив (<see cref="ZipLayout"/>).
    /// </summary>
    public static SevenZipRun Run(
        string sevenZipPath,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 15 * 60 * 1000)
    {
        try
        {
            var start = new ProcessStartInfo(sevenZipPath, arguments)
            {
                WorkingDirectory = workingDirectory ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start);
            if (process is null) return SevenZipRun.Failed(PanelStrings.SevenZipNotStarted);

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Процесса уже нет — это и есть то, чего мы хотели.
                }

                return SevenZipRun.Failed(string.Format(CultureInfo.CurrentCulture, PanelStrings.SevenZipTimeoutFormat, timeoutMs / 1000));
            }

            var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            return new SevenZipRun(process.ExitCode == 0, process.ExitCode, text.Trim());
        }
        catch (Exception exception)
        {
            return SevenZipRun.Failed($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Проверяет целостность архива (`7z t`) — и это единственная проверка, которая ЛОВИТ
    /// поломку, невидимую для .NET: встроенный распаковщик не сверяет контрольные суммы.
    /// Замер (<c>docs\ARCHIVE-MEASUREMENT.md</c>) поймал ровно такой случай: 22 пустых файла,
    /// записанных не тем способом, дали 22 ошибки «Data Error» — и .NET распаковал их молча.
    /// </summary>
    public static SevenZipRun Verify(string sevenZipPath, string archivePath, int timeoutMs = 15 * 60 * 1000) =>
        Run(sevenZipPath, $"t \"{archivePath}\"", timeoutMs: timeoutMs);

    /// <summary>
    /// Добавляет один корень в архив. Ключ <c>-snl</c> обязателен и объяснён замером: в пакете
    /// движка есть САМОССЫЛАЮЩИЙСЯ junction, и 7-Zip без этого ключа уходит в него на десятки
    /// уровней — в пробе это дало 943 МБ мусора и ни одной годной копии.
    /// </summary>
    public static SevenZipRun Add(
        string sevenZipPath,
        string archivePath,
        BackupSource source,
        IReadOnlyList<string>? excludeDirectoryNames = null,
        IReadOnlyList<string>? excludeFileNames = null,
        int timeoutMs = 15 * 60 * 1000)
    {
        var leaf = ZipLayout.Leaf(source.Directory);
        if (leaf.Length == 0) return SevenZipRun.Failed(PanelStrings.SevenZipRootHasNoName);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Directory)));
        if (string.IsNullOrEmpty(parent)) return SevenZipRun.Failed(PanelStrings.SevenZipRootHasNoParent);

        return AddRelative(sevenZipPath, archivePath, parent, leaf, excludeDirectoryNames, excludeFileNames, timeoutMs);
    }

    /// <summary>
    /// Добавляет в архив путь, заданный ОТНОСИТЕЛЬНО рабочего каталога, — ровно так, как его
    /// положит 7-Zip: имя записи равно заданному пути (это и есть ограничение из
    /// <see cref="ZipLayout"/>). Отдельным входом, а не «ещё одним ключом», по двум причинам:
    ///
    /// * **каталог, который берётся целиком** (<c>.git</c>), кладётся ОТДЕЛЬНЫМ запуском и без
    ///   исключений: общее правило мусора выбросило бы внутри него <c>logs</c> (reflog), а
    ///   исключения 7-Zip действуют на весь обход сразу и «только внутри .git» сказать не умеют;
    /// * **опись** кладётся одним файлом без префикса.
    /// </summary>
    /// <param name="excludeFileNames">
    /// Имена ФАЙЛОВ, которые в архив не кладутся. Появились 26.09.2026 вместе с «копией для
    /// передачи»: файл ключей доступа не должен уехать в архив, которым делятся.
    ///
    /// ⚠️ 7-Zip исключает по ИМЕНИ на любом уровне дерева — отдельного ключа «исключить вот этот
    /// путь» у него нет (то же ограничение, из-за которого исключаются и каталоги). Поэтому список
    /// точечный и задаётся только тому корню, где имя означает ровно то, что нужно.
    /// </param>
    public static SevenZipRun AddRelative(
        string sevenZipPath,
        string archivePath,
        string workingDirectory,
        string relativePath,
        IReadOnlyList<string>? excludeDirectoryNames = null,
        IReadOnlyList<string>? excludeFileNames = null,
        int timeoutMs = 15 * 60 * 1000)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return SevenZipRun.Failed(PanelStrings.SevenZipNothingToAdd);

        var arguments = $"a -tzip -mmt=on -snl -y \"{archivePath}\" \"{relativePath}\"";

        // Исключаем по ИМЕНИ, а не по пути: правило «bin, obj, node_modules» относится к любому
        // уровню дерева, а не только к его корню. Тем же ключом исключаются и файлы — списки
        // разные по смыслу, поэтому и приходят двумя параметрами, а не одним.
        foreach (var excluded in excludeDirectoryNames ?? Array.Empty<string>())
            arguments += $" -xr!\"{excluded}\"";

        foreach (var excluded in excludeFileNames ?? Array.Empty<string>())
            arguments += $" -xr!\"{excluded}\"";

        return Run(sevenZipPath, arguments, workingDirectory, timeoutMs);
    }

    /// <summary>
    /// Правда ли, что 7-Zip вообще работоспособен. Нужна, чтобы «нашёл файл» не выдавалось
    /// за «нашёл программу»: файл на месте, а запуститься он может и не суметь.
    /// </summary>
    public static bool Works(string? sevenZipPath, int timeoutMs = 30_000)
    {
        if (string.IsNullOrWhiteSpace(sevenZipPath)) return false;

        var run = Run(sevenZipPath, "i", timeoutMs: timeoutMs);
        return run.Ok;
    }

    /// <summary>Строка для отчёта и журнала: что нашли — и где. Путь показывается через маскировку.</summary>
    public static string Describe(string? sevenZipPath) =>
        sevenZipPath is null
            ? PanelStrings.SevenZipMissing
            : $"7-Zip: {DisplayMask.Path(sevenZipPath)}";
}
