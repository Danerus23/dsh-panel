namespace DshPanel.Server;

/// <summary>Чем кончилась попытка скачать файл: путь, куда легло, и причина, если не вышло.</summary>
public sealed record EngineNodeDownload(bool Ok, string Path, string Detail)
{
    public static EngineNodeDownload Failed(string detail) => new(false, string.Empty, detail);
}

/// <summary>Чем кончился свой Node.</summary>
public enum EngineNodeOutcome
{
    /// <summary>Node есть и годится — системный или свой, скачанный сейчас.</summary>
    Ready,

    /// <summary>Свой Node не понадобился: системный годится (это НЕ отказ).</summary>
    SystemSuits,

    /// <summary>Ни системного, ни своего: скачать не вышло.</summary>
    DownloadFailed,

    /// <summary>В официальном файле сумм нашего архива нет — сверять нечем.</summary>
    SumMissing,

    /// <summary>Официальный файл сумм и закреплённая сумма разошлись — набор подменён.</summary>
    SumMismatch,

    /// <summary>Архив скачался, но не распаковался.</summary>
    UnpackFailed,

    /// <summary>Распакованный Node не запускается или моложе порога.</summary>
    Broken,
}

/// <summary>Итог: путь к Node, которым можно ставить движок, версия и признак «он наш».</summary>
public sealed record EngineNodeResult(
    EngineNodeOutcome Outcome,
    string NodePath,
    string Version,
    string RefusalKey,
    string Detail,
    bool Ours)
{
    /// <summary>Node готов ставить движок.</summary>
    public bool Ok => Outcome is EngineNodeOutcome.Ready or EngineNodeOutcome.SystemSuits;
}

/// <summary>
/// ДВЕРИ NODE-УСТАНОВКИ: скачать, распаковать, спросить версию. Доводами — по той же причине, что
/// у установки движка: без них «скачали и проверили» проверялось бы настоящей сетью.
/// </summary>
public sealed record EngineNodeDoors(
    Func<string, string, string?, EngineNodeDownload> Download,
    Action<string, string> Unpack,
    Func<string, string> Version)
{
    /// <summary>
    /// Настоящие двери. Скачивание — ТЕМ ЖЕ кусочным докачиванием, что у обновления панели
    /// (<see cref="Update.IUpdateDownload"/>): куски по 8 МиБ, один поток, сумма проверяется
    /// на целом файле, недокачанный файл рабочего места не занимает.
    /// </summary>
    public static EngineNodeDoors Real(Update.IUpdateDownload download) => new(
        (url, path, sum) =>
        {
            var result = download.Save(url, path, sum, null, null);

            return result.Ok ? new EngineNodeDownload(true, path, string.Empty) : EngineNodeDownload.Failed(result.Detail);
        },
        (archive, folder) => UnpackReal(archive, folder),
        path => Platform.VersionProbe.Run(path, "--version"));

    /// <summary>
    /// РАСПАКОВАТЬ АРХИВ В ПАПКУ, СНЯВ ВЕРХНИЙ КАТАЛОГ: в выпуске Node файлы лежат внутри
    /// `node-v&lt;версия&gt;-win-x64\`, и без снятия верхнего каталога `node.exe` оказался бы на
    /// уровне глубже, чем его ищет панель.
    /// </summary>
    public static void UnpackReal(string archive, string folder)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);

        var top = zip.Entries
            .Select(entry => entry.FullName.Split('/')[0])
            .FirstOrDefault(name => name.Contains("node-v", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        Directory.CreateDirectory(folder);

        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName;

            if (top.Length > 0)
            {
                if (!relative.StartsWith(top + "/", StringComparison.Ordinal)) continue;
                relative = relative[(top.Length + 1)..];
            }

            if (relative.Length == 0) continue;

            var target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? folder);

            using var source = entry.Open();
            using var file = File.Create(target);
            source.CopyTo(file);
        }
    }
}

/// <summary>
/// СВОЙ NODE: СКАЧАТЬ, СВЕРИТЬ, РАСПАКОВАТЬ, ПРОВЕРИТЬ (решение владельца 10.10.2026:
/// *«Node — системный, проверяется ВЕРСИЯ (≥ 20), свой Node только когда его нет или он старше,
/// в свою папку, со сверкой SHA-256»*).
///
/// **Порядок и почему он такой.** Сначала — вопрос «а нужен ли свой вообще»: системный Node
/// подходящей версии берётся как есть, и **ничего не скачивается**. Свой нужен ровно в двух
/// случаях: системного нет или он старше порога (замер: порог 20 объявляют зависимости движка).
/// Дальше — **официальный файл сумм**, потом архив, потом распаковка, потом **проверка версии
/// у распакованного node.exe**: скачанный и распакованный, он обязан запуститься и ответить
/// версией не ниже порога — иначе это «сломано», а не «готово».
///
/// **Что здесь НЕ делается.** Панель не трогает системный Node: он не её, и в описи поставленного
/// его нет. Своя папка версии — единственное, что панель вправе убрать, и это записывается в опись.
/// </summary>
public static class EngineNodeInstall
{
    /// <summary>Срок на скачивание архива: 30 МБ на медленном канале — минуты, а не секунды.</summary>
    public static TimeSpan Timeout => TimeSpan.FromMinutes(15);

    /// <summary>
    /// ДОБЫТЬ ГОДНЫЙ NODE: системный, если он годится, иначе свой — скачанный и проверенный.
    /// </summary>
    /// <param name="nodeDir">Папка своих Node (<see cref="Isolation.AppPaths.NodeDir"/>).</param>
    /// <param name="systemNodePath">Системный Node, если найден (пусто — не найден).</param>
    /// <param name="systemVersion">Его версия (пусто — не узнать).</param>
    /// <param name="progress">Строка хода (журнал).</param>
    /// <param name="engineLock">
    /// Замок проверенного движка: из него берётся ПОРОГ версии Node (решение владельца `7265d9d`).
    /// «Замок молчит» выражается ПУСТЫМ замком (<see cref="EngineTreeLock.Empty"/>), а не пустой ссылкой:
    /// </param>
    public static EngineNodeResult Ensure(
        string? nodeDir,
        string? systemNodePath,
        string? systemVersion,
        EngineTreeLock engineLock,
        EngineNodeDoors doors,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(doors);

        // (1) системный годится — и брать больше нечего.
        if (!string.IsNullOrWhiteSpace(systemNodePath) && EngineNode.Fits(systemVersion, engineLock))
        {
            return new EngineNodeResult(
                EngineNodeOutcome.SystemSuits, systemNodePath.Trim(), (systemVersion ?? string.Empty).Trim(),
                string.Empty, string.Empty, Ours: false);
        }

        if (string.IsNullOrWhiteSpace(nodeDir))
        {
            return new EngineNodeResult(
                EngineNodeOutcome.DownloadFailed, string.Empty, string.Empty,
                EngineInstallRefusals.NoPlace, EngineInstallPhrases.NodeMissing(), Ours: false);
        }

        var folder = EngineNode.VersionDir(nodeDir);
        var exe = EngineNode.ExePath(nodeDir);

        // (2) свой уже лежит и он годный — второй раз не качаем (сеть и 30 МБ даром не нужны).
        if (File.Exists(exe))
        {
            var present = (doors.Version(exe) ?? string.Empty).Trim();

            if (EngineNode.Fits(present, engineLock))
            {
                return new EngineNodeResult(
                    EngineNodeOutcome.Ready, exe, present, string.Empty, string.Empty, Ours: true);
            }
        }

        progress?.Invoke(EngineInstallPhrases.NodeDownloading(EngineNode.Release));

        // (3) ОФИЦИАЛЬНЫЙ ФАЙЛ СУММ: наш архив обязан быть в нём, и сумма обязана совпасть
        // с закреплённой в сборке. Два источника говорят одно и то же — или отказ.
        var sumsPath = Path.Combine(Path.GetTempPath(), "dsh-node-sums-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var archivePath = Path.Combine(Path.GetTempPath(), "dsh-node-" + Guid.NewGuid().ToString("N")[..8] + ".zip");

        try
        {
            var sums = doors.Download(EngineNode.SumsUrl(), sumsPath, null);

            if (!sums.Ok)
            {
                return Refuse(EngineNodeOutcome.DownloadFailed, EngineInstallRefusals.NodeSourceFailed,
                    EngineInstallPhrases.NodeSourceFailed(sums.Detail));
            }

            var official = EngineNode.SumFrom(File.ReadAllText(sumsPath), EngineNode.ArchiveName());

            if (official.Length == 0)
            {
                return Refuse(EngineNodeOutcome.SumMissing, EngineInstallRefusals.NodeSumMissing,
                    EngineInstallPhrases.NodeSumMissing(EngineNode.ArchiveName()));
            }

            if (!string.Equals(official, EngineNode.ArchiveSum, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(EngineNodeOutcome.SumMismatch, EngineInstallRefusals.NodeSumMismatch,
                    EngineInstallPhrases.NodeSumMismatch());
            }

            progress?.Invoke(EngineInstallPhrases.NodeFetching(EngineNode.ArchiveName()));

            var archive = doors.Download(EngineNode.ArchiveUrl(), archivePath, EngineNode.ArchiveSum);

            if (!archive.Ok)
            {
                return Refuse(EngineNodeOutcome.DownloadFailed, EngineInstallRefusals.NodeDownloadFailed,
                    EngineInstallPhrases.NodeDownloadFailed(archive.Detail));
            }

            try
            {
                doors.Unpack(archive.Path.Length > 0 ? archive.Path : archivePath, folder);
            }
            catch (Exception error)
            {
                return Refuse(EngineNodeOutcome.UnpackFailed, EngineInstallRefusals.NodeUnpackFailed,
                    EngineInstallPhrases.NodeUnpackFailed(error.GetType().Name));
            }
        }
        finally
        {
            Remove(sumsPath);
            Remove(archivePath);
        }

        // (4) ПРОВЕРКА, А НЕ НАДЕЖДА: распакованный Node обязан запуститься и ответить версией.
        var version = (doors.Version(exe) ?? string.Empty).Trim();

        if (!EngineNode.Fits(version, engineLock))
        {
            return Refuse(EngineNodeOutcome.Broken, EngineInstallRefusals.NodeBroken,
                EngineInstallPhrases.NodeBroken(version));
        }

        return new EngineNodeResult(EngineNodeOutcome.Ready, exe, version, string.Empty, string.Empty, Ours: true);
    }

    private static EngineNodeResult Refuse(EngineNodeOutcome outcome, string key, string detail) =>
        new(outcome, string.Empty, string.Empty, key, detail, Ours: false);

    private static void Remove(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* хвост во временном каталоге — не беда */ }
    }
}
