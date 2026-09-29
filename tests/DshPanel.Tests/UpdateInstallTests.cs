using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshPanel.Isolation;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ВТОРОЙ СРЕЗ ОБНОВЛЕНИЯ: скачивание, проверка, распаковка, подготовка замены и чтение итога.
///
/// **Чего в этих проверках нет и быть не может.**
///
/// 1. **Сети.** Живая загрузка отделена уговором <see cref="IUpdateDownload"/>: здесь
///    подставляется источник, который кладёт заранее известные байты. Сеть от имени владельца —
///    его право, а не наше.
/// 2. **Настоящей замены файлов.** Сценарий <c>update.cmd</c> здесь только СОЧИНЯЕТСЯ и читается
///    текстом: запуск замены — это правка файлов у человека, и прогон проверки её не делает
///    ни при каких условиях.
/// 3. **Каталогов владельца.** Всё живёт в своём временном корне, который проверка убирает
///    за собой; «чужим каталогом» здесь назван второй временный каталог, и проверяется, что
///    в него не попало ни байта.
///
/// Три беды, ради которых проверки и заведены (все три случались у панели 1.x):
/// сверка SHA-256 пропущена; версия внутри архива не сверена с версией выпуска; путь с пробелом
/// и хвостовым «\» ломает сценарий замены.
/// </summary>
public class UpdateInstallTests
{
    private const string SumsUrl = "https://example.invalid/SHA256SUMS.txt";
    private const string ArchiveUrl = "https://example.invalid/DshPanel.zip";
    private const string SetupUrl = "https://example.invalid/dsh-panel-setup.exe";

    /// <summary>Момент свой и вымышленный: от системных часов отчёт зависеть не должен.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ оснастка

    /// <summary>
    /// Временный стенд одного прогона: свой корень панели, своя папка панели («установленная»)
    /// и второй каталог, в который не должно попасть ничего.
    ///
    /// Папка панели названа ТАК ЖЕ, как у человека (<c>…\Programs\DSH Panel</c>), и в запрос она
    /// уходит с хвостовым «\»: ровно этот вид пути однажды съел закрывающую кавычку в сценарии.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        public Stand()
        {
            Root = Path.Combine(Path.GetTempPath(), "dsh-panel-update-install-tests", Guid.NewGuid().ToString("N"));

            Target = Path.Combine(Root, "Programs", "DSH Panel");
            Other = Path.Combine(Root, "Other");
            Paths = AppPaths.Under(Path.Combine(Root, "run"));

            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Other);

            File.WriteAllText(Path.Combine(Target, UpdateStaging.PanelExeName), "прежняя панель");
            File.WriteAllText(Path.Combine(Target, "DshPanel.dll"), "прежняя библиотека");
            File.WriteAllText(Path.Combine(Other, "чужой.txt"), "не трогать");
        }

        public string Root { get; }

        /// <summary>Папка «установленной» панели — та, которую заменит сценарий.</summary>
        public string Target { get; }

        /// <summary>Второй каталог: в него не должно попасть ничего.</summary>
        public string Other { get; }

        public AppPaths Paths { get; }

        public FakeDownload Download { get; } = new();

        /// <summary>Каталог обновления внутри состояния панели.</summary>
        public string UpdateRoot => UpdateInstall.UpdateRoot(Paths);

        /// <summary>Снимок папки панели: имя файла → содержимое (для сверки «ничего не тронуто»).</summary>
        public IReadOnlyDictionary<string, string> Snapshot(string folder) =>
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    file => Path.GetRelativePath(folder, file),
                    File.ReadAllText,
                    StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);

                var parent = Path.GetDirectoryName(Root);
                if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
            }
            catch
            {
                // Родитель занят другой проверкой — это не ошибка.
            }
        }
    }

    /// <summary>
    /// ПОДСТАНОВКА ВМЕСТО СЕТИ: отдаёт заранее известные байты и считает, что у него просили.
    /// Ни одного живого запроса: адреса здесь вымышленные и ведут в никуда.
    /// </summary>
    private sealed class FakeDownload : IUpdateDownload
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = new();

        public List<UpdateProgress> Ticks { get; } = new();

        /// <summary>Адрес, на котором подстановка обязана отказать (пусто — не отказывает).</summary>
        public string FailUrl { get; set; } = string.Empty;

        public FakeDownload Add(string url, byte[] bytes)
        {
            _files[url] = bytes;
            return this;
        }

        public void Save(string url, string path, Action<long, long>? progress)
        {
            Asked.Add(url);

            if (FailUrl.Length > 0 && string.Equals(url, FailUrl, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("HTTP 500");

            if (!_files.TryGetValue(url, out var bytes))
                throw new InvalidOperationException("подстановки для адреса нет: " + url);

            progress?.Invoke(0, bytes.Length);
            File.WriteAllBytes(path, bytes);
            progress?.Invoke(bytes.Length, bytes.Length);
        }
    }

    /// <summary>Архив панели, каким его кладёт сборка: сама панель и что-то рядом.</summary>
    private static byte[] PanelArchive(string version = "2.1.0") =>
        Zip(
            (UpdateStaging.PanelExeName, "новая панель " + version),
            ("DshPanel.dll", "новая библиотека"));

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return memory.ToArray();
    }

    /// <summary>
    /// Файл сумм в том виде, в каком его публикует выпуск: «хэш  имя файла». Хэш считается здесь
    /// своим счётом (а не <see cref="UpdateSums.Sha256"/>) — иначе проверка сверяла бы панель
    /// с нею же, и неверный хэш в файле сумм остался бы незамеченным.
    /// </summary>
    private static string SumsText(params (string Name, byte[] Bytes)[] files) =>
        string.Join(
            Environment.NewLine,
            files.Select(file =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Bytes)).ToLowerInvariant()
                + "  " + file.Name));

    /// <summary>Скачанные файлы подстановки: подготовка просит ровно три адреса из выпуска.</summary>
    private static ReleaseAssets Ready(Stand stand, byte[]? archive = null, bool withSetup = false)
    {
        var zip = archive ?? PanelArchive();
        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, zip))));

        if (withSetup)
        {
            stand.Download.Add(SetupUrl, Encoding.UTF8.GetBytes("установщик"));
        }

        stand.Download.Add(ArchiveUrl, zip);

        return new ReleaseAssets(ArchiveUrl, withSetup ? SetupUrl : string.Empty, SumsUrl);
    }

    /// <summary>
    /// Запрос на подготовку. Папка панели уходит с хвостовым «\» — на этом пути проверяется
    /// <see cref="UpdateScript.SafePath"/> (урок владельца: панель лежит в «DSH Panel»).
    /// </summary>
    private static UpdateRequest Request(
        Stand stand,
        ReleaseAssets assets,
        string release = "v2.1.0",
        string current = "2.0.0",
        bool allowed = true,
        bool ignoreNewer = false,
        string startArgument = "") =>
        new(
            stand.Paths,
            current,
            stand.Target + "\\",
            release,
            assets,
            allowed,
            ignoreNewer,
            startArgument,
            string.Empty,
            Now);

    /// <summary>Полный удачный прогон подготовки: так выглядит «выпуск новее, всё сошлось».</summary>
    private static UpdatePreparation Prepare(
        Stand stand,
        ReleaseAssets assets,
        out List<UpdateProgress> progress,
        string version = "2.1.0",
        UpdateRequest? request = null)
    {
        var ticks = new List<UpdateProgress>();
        progress = ticks;

        return UpdateInstall.Prepare(
            request ?? Request(stand, assets),
            stand.Download,
            _ => version + "+abc",
            ticks.Add);
    }

    /// <summary>Подготовленный стенд: в выпуске лежит нужный архив, суммы сходятся.</summary>
    private static void Prepared(Stand stand, out ReleaseAssets assets, out UpdatePreparation preparation)
    {
        assets = Ready(stand);
        preparation = Prepare(stand, assets, out _, "2.1.0");

        Assert.True(preparation.Ok, preparation.RefusalKey + ": " + preparation.Detail);
    }

    /// <summary>Разложить подготовленную сборку в каталоге обновления — так её видит следующий запуск.</summary>
    private static string Stage(AppPaths paths, string version, string marker)
    {
        var root = UpdateInstall.UpdateRoot(paths);
        var folder = Path.Combine(root, version);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, UpdateStaging.PanelExeName), "панель " + version);
        File.WriteAllText(
            Path.Combine(root, UpdateScript.LogFileName),
            marker.Length > 0
                ? UpdateOutcomes.ResultMarkerPrefix + marker + Environment.NewLine
                : "шапка журнала без итога" + Environment.NewLine);

        return folder;
    }

    // ------------------------------------------------------------------ выбор вложений

    /// <summary>
    /// ИЗ ВЫПУСКА БЕРУТСЯ РОВНО ТРИ СВОИХ ВЛОЖЕНИЯ, и ничего «похожего». Имена те же, что у 1.x:
    /// их кладёт в выпуск сборка, и своё имя здесь означало бы панель, которая ищет вложение,
    /// которого в выпуске нет. Вариант 1.x «DshPanel-selfcontained.zip» не подставляется молча:
    /// чужая сборка либо не запустится, либо потянет за собой установку среды.
    /// </summary>
    [Fact]
    public void Выбор_вложений_берёт_только_свои_имена()
    {
        var assets = UpdateAssets.Pick(new[]
        {
            new ReleaseAsset("DshPanel-selfcontained.zip", "https://example.invalid/self.zip"),
            new ReleaseAsset("readme.txt", "https://example.invalid/readme.txt"),
            new ReleaseAsset("DSHPANEL.ZIP", ArchiveUrl),
            new ReleaseAsset("dsh-panel-setup.exe", SetupUrl),
            new ReleaseAsset("sha256sums.txt", SumsUrl),
        });

        Assert.Equal(ArchiveUrl, assets.ArchiveUrl);
        Assert.Equal(SetupUrl, assets.SetupUrl);
        Assert.Equal(SumsUrl, assets.SumsUrl);
        Assert.True(assets.HasArchive);
        Assert.True(assets.HasSums);
    }

    /// <summary>
    /// РАЗБОР ОТВЕТА GITHUB: вложения читаются из <c>assets</c>, вложение без имени или без адреса
    /// в выбор не попадает. «Вложение без адреса» — это отсутствие вложения, а не «скачаем пустоту».
    /// </summary>
    [Fact]
    public void Вложения_разбираются_из_ответа_GitHub()
    {
        using var document = JsonDocument.Parse(
            "{\"tag_name\":\"v2.1.0\",\"assets\":[" +
            "{\"name\":\"DshPanel.zip\",\"browser_download_url\":\"" + ArchiveUrl + "\"}," +
            "{\"name\":\"SHA256SUMS.txt\"}," +
            "{\"name\":\"\",\"browser_download_url\":\"https://example.invalid/nothing\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"" + SumsUrl + "\"}]}");

        var assets = UpdateAssets.Pick(UpdateAssets.Parse(document.RootElement));

        Assert.Equal(ArchiveUrl, assets.ArchiveUrl);
        Assert.Equal(SumsUrl, assets.SumsUrl);
        Assert.False(assets.HasSetup);

        Assert.False(UpdateAssets.Pick(Array.Empty<ReleaseAsset>()).HasArchive);
        Assert.False(UpdateAssets.Pick(null).HasSums);
    }

    /// <summary>
    /// БЕЗ АРХИВА В ВЫПУСКЕ ОБНОВЛЯТЬСЯ НЕЧЕМ, и отказ приходит ДО первого касания диска:
    /// каталога обновления после него не появляется, а сеть не тревожится вовсе.
    /// </summary>
    [Fact]
    public void Без_архива_выпуск_не_годится()
    {
        using var stand = new Stand();

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(string.Empty, string.Empty, SumsUrl)),
            stand.Download);

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.NoArchive, preparation.Refusal);
        Assert.Equal("UpdateNoArchive", preparation.RefusalKey);
        Assert.False(Directory.Exists(stand.UpdateRoot));
        Assert.Empty(stand.Download.Asked);
    }

    /// <summary>
    /// ФАЙЛ СУММ ОБЯЗАТЕЛЕН: без него сверять нечего, а хэш без подписи — единственная доступная
    /// проверка целостности (сертификата у проекта нет). Отказ называет причину и ничего не качает.
    /// </summary>
    [Fact]
    public void Без_сумм_выпуск_не_годится()
    {
        using var stand = new Stand();

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(ArchiveUrl, string.Empty, string.Empty)),
            stand.Download);

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.NoSums, preparation.Refusal);
        Assert.False(Directory.Exists(stand.UpdateRoot));
        Assert.Empty(stand.Download.Asked);
    }

    // ------------------------------------------------------------------ контрольные суммы

    /// <summary>
    /// РАЗБОР ФАЙЛА СУММ ТЕРПИМ К РУЧНОЙ ПРАВКЕ: BOM, комментарии, звёздочка, обратный слэш
    /// и путь перед именем, лишние пробелы. Нетерпим он ровно к одному: хэш обязан быть
    /// 64 шестнадцатеричных знака, иначе строка не считается суммой вовсе.
    /// </summary>
    [Fact]
    public void Разбор_сумм_переживает_ручную_правку()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var sums = UpdateSums.Parse(new[]
        {
            "\uFEFF" + hash + "  DshPanel.zip",
            "# комментарий сборщика",
            "",
            hash + " *./release\\DshPanel.zip",
            "короткий  DshPanel.zip",
            hash.ToUpperInvariant() + "\t" + UpdateAssets.SetupName,
            "не сумма",
        });

        Assert.Equal(2, sums.Count);
        Assert.Equal(hash, sums[UpdateAssets.ArchiveName]);
        Assert.Equal(hash, sums[UpdateAssets.SetupName]);
    }

    /// <summary>
    /// СУММА НЕ СОШЛАСЬ — ОТКАЗ, И АРХИВ НЕ РАСПАКОВЫВАЕТСЯ. Проверяется это не только кодом
    /// отказа, но и диском: папки сборки после отказа не остаётся, а в папке панели не меняется
    /// ни один байт. Ради этого сверка и заведена.
    /// </summary>
    [Fact]
    public void Несовпавшая_сумма_отказ_и_архив_не_распаковывается()
    {
        using var stand = new Stand();

        var before = stand.Snapshot(stand.Target);

        // Сумма посчитана от ДРУГОГО архива: скачался не тот файл.
        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, PanelArchive("9.9.9")))));
        stand.Download.Add(ArchiveUrl, PanelArchive("2.1.0"));

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl)),
            stand.Download,
            _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.SumMismatch, preparation.Refusal);
        Assert.Equal("UpdateSumMismatchFormat", preparation.RefusalKey);
        Assert.Contains("ожидалось", preparation.Detail, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(stand.UpdateRoot, "2.1.0")));
        Assert.Empty(Directory.GetFiles(stand.UpdateRoot));
        Assert.Equal(before, stand.Snapshot(stand.Target));
    }

    /// <summary>
    /// ОТСУТСТВИЕ СУММЫ В СПИСКЕ — ТОЖЕ ОТКАЗ, а не «пропустим эту проверку». Пропустить его
    /// значило бы объявить проверку выполненной там, где она не выполнялась вовсе (урок v1).
    /// </summary>
    [Fact]
    public void Отсутствие_суммы_в_списке_тоже_отказ()
    {
        using var stand = new Stand();

        var archive = PanelArchive();
        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.SetupName, Encoding.UTF8.GetBytes("установщик")))));
        stand.Download.Add(ArchiveUrl, archive);

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl)),
            stand.Download,
            _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.SumMissing, preparation.Refusal);
        Assert.Equal("UpdateSumMissingFormat", preparation.RefusalKey);
        Assert.False(Directory.Exists(Path.Combine(stand.UpdateRoot, "2.1.0")));
    }

    /// <summary>Пустой или неразобранный файл сумм — отказ: сверять не с чем.</summary>
    [Fact]
    public void Пустой_файл_сумм_отказ()
    {
        using var stand = new Stand();

        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes("# одни комментарии" + Environment.NewLine));
        stand.Download.Add(ArchiveUrl, PanelArchive());

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl)),
            stand.Download,
            _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.SumsUnreadable, preparation.Refusal);
    }

    // ------------------------------------------------------------------ версия внутри архива

    /// <summary>
    /// ВЕРСИЯ ВНУТРИ АРХИВА СВЕРЯЕТСЯ С ВЕРСИЕЙ ВЫПУСКА: совпала — обновление готовится,
    /// не совпала — отказ. Сверка идёт по ЧИСЛОВОЙ части: .NET дописывает к версии хеш сборки,
    /// а тег GitHub несёт букву «v».
    /// </summary>
    [Fact]
    public void Версия_внутри_архива_сверяется_с_выпуском()
    {
        Assert.True(UpdateStaging.VersionMatches("2.1.0+abc", "v2.1.0"));
        Assert.True(UpdateStaging.VersionMatches(" 2.1.0 ", "2.1.0"));
        Assert.False(UpdateStaging.VersionMatches("2.0.9+abc", "v2.1.0"));
        Assert.False(UpdateStaging.VersionMatches("", "v2.1.0"));
        Assert.False(UpdateStaging.VersionMatches("v2.1.0", ""));
        Assert.False(UpdateStaging.VersionMatches(null, null));
        Assert.False(UpdateStaging.VersionMatches("   ", "  "));
    }

    /// <summary>
    /// ПОДМЕНЁННЫЙ АРХИВ НЕ ГОТОВИТСЯ К ЗАМЕНЕ: сумма могла сойтись, а версия внутри — нет
    /// (выпуск пересобрали под другим номером, вложение подменили до публикации сумм).
    /// Без этой сверки такая сборка тихо встала бы на место рабочей панели.
    /// </summary>
    [Fact]
    public void Чужая_версия_внутри_архива_отказ()
    {
        using var stand = new Stand();
        var before = stand.Snapshot(stand.Target);

        var assets = Ready(stand);

        var preparation = UpdateInstall.Prepare(
            Request(stand, assets),
            stand.Download,
            _ => "2.0.9+abc");   // версия внутри архива — не та, что обещает выпуск

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.VersionMismatch, preparation.Refusal);
        Assert.Equal("UpdateVersionMismatchFormat", preparation.RefusalKey);
        Assert.Contains("2.0.9", preparation.Detail, StringComparison.Ordinal);
        Assert.Contains("2.1.0", preparation.Detail, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(stand.UpdateRoot, "2.1.0")));
        Assert.Equal(before, stand.Snapshot(stand.Target));
    }

    /// <summary>
    /// ВЕРСИЯ ЧИТАЕТСЯ У СОБРАННОЙ ПАНЕЛИ, а не у выдуманного файла: читатель берёт свойства
    /// самого exe, как делала панель 1.x. Проверка идёт на НАСТОЯЩЕЙ сборке этого дерева —
    /// и потому не знает заранее её номера: сверяется сама с собой.
    /// </summary>
    [Fact]
    public void Версия_читается_у_собранной_панели()
    {
        // Каталог прогона, а не Location сборки: у сборки одним файлом путь пуст, и проверка
        // молча мерила бы не то. Рядом с проверками лежит настоящая собранная панель.
        var folder = AppContext.BaseDirectory;

        var version = UpdateStaging.ReadVersion(folder);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(UpdateStaging.VersionMatches(version, version), "собранная панель не совпала сама с собой: " + version);

        // Каталог без панели — «версию не прочитать», и это не «совпало» ни с чем.
        using var stand = new Stand();
        Assert.Equal(string.Empty, UpdateStaging.ReadVersion(stand.Other));
    }

    /// <summary>
    /// АРХИВ БЕЗ ПАНЕЛИ ВНУТРИ — ОТКАЗ: заменять нечего, а «успешная» подготовка пустого архива
    /// закончилась бы папкой панели без панели.
    /// </summary>
    [Fact]
    public void Архив_без_панели_внутри_отказ()
    {
        using var stand = new Stand();

        var zip = Zip(("readme.txt", "это не панель"));
        var assets = Ready(stand, zip);

        var preparation = UpdateInstall.Prepare(Request(stand, assets), stand.Download, _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.NoExe, preparation.Refusal);
    }

    // ------------------------------------------------------------------ удачная подготовка

    /// <summary>
    /// УДАЧНАЯ ПОДГОТОВКА: архив распакован в свою папку, страховочная копия снята, сценарий
    /// и шапка журнала написаны, командная строка собрана. И — ни одного байта в папке панели:
    /// замену делает сценарий после выхода панели, а не подготовка.
    /// </summary>
    [Fact]
    public void Подготовка_раскладывает_сборку_и_ничего_не_меняет_в_папке_панели()
    {
        using var stand = new Stand();
        var before = stand.Snapshot(stand.Target);

        Prepared(stand, out _, out var preparation);

        Assert.True(File.Exists(Path.Combine(preparation.StagedFolder, UpdateStaging.PanelExeName)));
        Assert.True(File.Exists(Path.Combine(preparation.BackupFolder, UpdateStaging.PanelExeName)));
        Assert.True(File.Exists(preparation.ScriptPath));
        Assert.True(File.Exists(preparation.LogPath));
        Assert.Contains("update.cmd", preparation.CommandLine, StringComparison.OrdinalIgnoreCase);

        // Ни одного изменения в папке панели: подготовка её только ЧИТАЕТ.
        Assert.Equal(before, stand.Snapshot(stand.Target));

        // И в чужом каталоге — ни байта.
        Assert.Equal(
            new[] { "чужой.txt" },
            Directory.GetFiles(stand.Other).Select(Path.GetFileName).ToArray());
    }

    /// <summary>
    /// ХОД СКАЧИВАНИЯ НАЗЫВАЕТ ЭТАПЫ И ПРОЦЕНТЫ: интерфейсу нужно показать человеку, что идёт
    /// работа и сколько осталось. Этапы — перечислением, а не фразой: подписи берёт интерфейс.
    /// </summary>
    [Fact]
    public void Ход_скачивания_называет_этап_и_проценты()
    {
        using var stand = new Stand();

        var assets = Ready(stand);
        var preparation = Prepare(stand, assets, out var progress);

        Assert.True(preparation.Ok, preparation.RefusalKey);

        var stages = progress.Select(tick => tick.Stage).Distinct().ToList();

        Assert.Contains(UpdateStage.Sums, stages);
        Assert.Contains(UpdateStage.Archive, stages);
        Assert.Contains(UpdateStage.Verify, stages);
        Assert.Contains(UpdateStage.Unpack, stages);
        Assert.Contains(UpdateStage.Backup, stages);
        Assert.Contains(UpdateStage.Script, stages);

        // Проценты честные: у этапа скачивания архива их видно (подстановка называет длину),
        // а когда размер неизвестен — не ноль, а именно «неизвестно»: ноль человек прочитал бы
        // как «загрузка встала».
        var archive = progress.Where(tick => tick.Stage == UpdateStage.Archive).ToList();

        Assert.Contains(archive, tick => tick.Percent == 100);
        Assert.All(archive, tick => Assert.True(
            tick.Percent == -1 || tick.Percent is >= 0 and <= 100,
            "процент вне 0..100 и не «неизвестно»: " + tick.Percent));

        Assert.Equal(-1, new UpdateProgress(UpdateStage.Archive, UpdateAssets.ArchiveName, 0, -1).Percent);
        Assert.Equal(50, new UpdateProgress(UpdateStage.Archive, UpdateAssets.ArchiveName, 5, 10).Percent);
    }

    /// <summary>
    /// УСТАНОВЩИК ЕДЕТ РЯДОМ: он лежит в папке панели и уходит в полную копию, поэтому его
    /// скачивание и сверка — часть подготовки. Его сумма проверяется так же строго.
    /// </summary>
    [Fact]
    public void Установщик_выпуска_скачивается_и_сверяется()
    {
        using var stand = new Stand();

        var setup = Encoding.UTF8.GetBytes("установщик");
        var zip = PanelArchive();
        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText(
            (UpdateAssets.ArchiveName, zip), (UpdateAssets.SetupName, setup))));
        stand.Download.Add(ArchiveUrl, zip);
        stand.Download.Add(SetupUrl, setup);

        var preparation = UpdateInstall.Prepare(
            Request(stand, new ReleaseAssets(ArchiveUrl, SetupUrl, SumsUrl)),
            stand.Download,
            _ => "2.1.0");

        Assert.True(preparation.Ok, preparation.RefusalKey + ": " + preparation.Detail);
        Assert.True(File.Exists(Path.Combine(preparation.StagedFolder, UpdateAssets.SetupName)));

        // Установщик с чужой суммой — отказ, и в папке панели по-прежнему ничего не тронуто.
        using var second = new Stand();
        var before = second.Snapshot(second.Target);
        var other = Encoding.UTF8.GetBytes("подменённый установщик");

        second.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText(
            (UpdateAssets.ArchiveName, zip), (UpdateAssets.SetupName, setup))));
        second.Download.Add(ArchiveUrl, zip);
        second.Download.Add(SetupUrl, other);

        var refused = UpdateInstall.Prepare(
            Request(second, new ReleaseAssets(ArchiveUrl, SetupUrl, SumsUrl)),
            second.Download,
            _ => "2.1.0");

        Assert.False(refused.Ok);
        Assert.Equal(UpdateRefusal.SumMismatch, refused.Refusal);
        Assert.Equal(before, second.Snapshot(second.Target));
    }

    /// <summary>Неудача загрузки называется отказом, а не исключением наружу.</summary>
    [Fact]
    public void Неудача_загрузки_названа_отказом()
    {
        using var stand = new Stand();

        var assets = Ready(stand);
        stand.Download.FailUrl = ArchiveUrl;

        var preparation = UpdateInstall.Prepare(Request(stand, assets), stand.Download, _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.DownloadFailed, preparation.Refusal);
        Assert.Contains("HTTP 500", preparation.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(stand.UpdateRoot));
    }

    // ------------------------------------------------------------------ текст сценария

    /// <summary>
    /// СЦЕНАРИЙ ЖДЁТ ВЫХОДА ПАНЕЛИ ПО ЕЁ НОМЕРУ ПРОЦЕССА. Файлы работающего exe заперты, и замена
    /// «на живую» ломает папку: половина файлов новых, половина старых. Ждать «пока файл
    /// освободится» нельзя — exe успевает замениться раньше dll (урок v1).
    /// </summary>
    [Fact]
    public void Сценарий_ждёт_выхода_панели_по_номеру_процесса()
    {
        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        var script = File.ReadAllText(preparation.ScriptPath);

        Assert.Contains("tasklist /FI \"PID eq %PID%\"", script, StringComparison.Ordinal);
        Assert.Contains("find \"%PID%\"", script, StringComparison.Ordinal);
        Assert.Contains("not-applied-panel-running", script, StringComparison.Ordinal);
        Assert.Contains("\"" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"", preparation.CommandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// СЦЕНАРИЙ ЗНАЕТ ВСЕ ИТОГИ И УМЕЕТ ОТКАТЫВАТЬСЯ. Список итогов берётся у читателя журнала —
    /// он ОДИН на панель, и сценарий, знающий свои итоги отдельно, разошёлся бы с ним.
    /// Откат — единственный путь назад: без него неудачная замена оставляет человека
    /// с полузаменённой папкой.
    /// </summary>
    [Fact]
    public void Сценарий_перечисляет_все_итоги_и_умеет_откатиться()
    {
        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        var script = File.ReadAllText(preparation.ScriptPath);

        var expected = Enum.GetValues<UpdateResultKind>()
            .Select(UpdateOutcomes.Marker)
            .Where(marker => marker.Length > 0)
            .OrderBy(marker => marker, StringComparer.Ordinal)
            .ToArray();

        // Строка итога в сценарии — это «echo update result: <итог> >> "%LOG%"»: сам итог стоит
        // внутри команды, а в журнал попадает уже началом строки (см. ReadMarker).
        var found = script
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line =>
            {
                var at = line.IndexOf(UpdateOutcomes.ResultMarkerPrefix, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return string.Empty;

                var tail = line[(at + UpdateOutcomes.ResultMarkerPrefix.Length)..].Trim();

                return tail.Split(' ')[0];
            })
            .Where(marker => marker.Length > 0)
            .Distinct()
            .OrderBy(marker => marker, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, found);

        // Откат: прежняя версия возвращается из страховочной копии, и только потом панель
        // поднимается снова. Нет копии или не удалось вернуть — не запускается НИЧЕГО.
        Assert.Contains("robocopy \"%BACKUP%\" \"%TARGET%\"", script, StringComparison.Ordinal);
        Assert.Contains("if not exist \"%BACKUP%\\" + UpdateStaging.PanelExeName + "\" goto no-backup", script, StringComparison.Ordinal);
        Assert.Contains(":restore-failed", script, StringComparison.Ordinal);
        Assert.Contains(":no-backup", script, StringComparison.Ordinal);

        // Замок одной копии ждётся отдельно: копия, поднятая слишком рано, видит замок занятым
        // и молча завершается — человек остаётся без панели и без объяснения.
        Assert.Contains("%MUTEX%", script, StringComparison.Ordinal);
        Assert.Contains(InstanceSignal.DefaultMutexName, script, StringComparison.Ordinal);
    }

    /// <summary>
    /// СКОПИРОВАННЫЕ ФАЙЛЫ ПРОВЕРЯЮТСЯ НА ТО, ЧТО ОНИ ВООБЩЕ ПАНЕЛЬ ОБЕЩАННОЙ ВЕРСИИ.
    /// Дефект найден лабораторным прогоном 28.09.2026: robocopy отвечает нулём и на «скопировал
    /// чужой файл», сценарий объявлял замену удачной, панель не поднималась — человек оставался
    /// без панели, а прежняя версия лежала в страховочной копии нетронутой. Здесь проверяется,
    /// что сценарий читает версию у СКОПИРОВАННОГО exe и сравнивает её с обещанной выпуском,
    /// а не верит коду возврата robocopy.
    ///
    /// Сравнение идёт по ЧИСЛОВОЙ части: к <c>ProductVersion</c> .NET дописывает хеш сборки
    /// («2.1.0+abc»), и сравнение целых строк валило бы исправное обновление.
    /// </summary>
    [Fact]
    public void Сценарий_сверяет_скопированную_панель_с_обещанной_версией()
    {
        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        var script = File.ReadAllText(preparation.ScriptPath);
        var lines = script.Replace("\r\n", "\n").Split('\n');

        // Обещание выпуска — ОДНО на сценарий: та же строка стоит в шапке журнала и здесь.
        Assert.Equal("2.1.0", preparation.Version);
        Assert.Contains("set \"VERSION=" + preparation.Version + "\"", script, StringComparison.Ordinal);
        Assert.Contains(preparation.Version, File.ReadAllText(preparation.LogPath), StringComparison.Ordinal);

        // Проверка стоит ПОСЛЕ копирования и ДО запуска: иначе она не проверяла бы ничего.
        var copied = Array.FindIndex(lines, line => line.Contains("files replaced", StringComparison.Ordinal));
        var check = Array.FindIndex(lines, line => line.Contains("%VERSION%", StringComparison.Ordinal) && line.Contains("ProductVersion", StringComparison.Ordinal));
        var refuse = Array.FindIndex(lines, line => line.Contains(":copied-not-a-panel", StringComparison.Ordinal));
        var start = Array.FindIndex(lines, line => line.Contains(":start", StringComparison.Ordinal));

        Assert.True(copied > 0 && check > copied, "проверка копии стоит не после замены файлов");
        Assert.True(refuse > check, "у непрошедшей проверки нет своей ветки");
        Assert.True(start > check, "проверка копии стоит после запуска панели");

        // Отказ ведёт в откат, а не в запуск: полузаменённую папку поднимать нельзя.
        Assert.Contains("if errorlevel 1 goto copied-not-a-panel", script, StringComparison.Ordinal);
        Assert.Contains(
            "goto rollback",
            lines.Skip(refuse).First(line => line.StartsWith("goto", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        // Сравнивается ЧИСЛОВАЯ часть: «2.1.0+abc» — это версия 2.1.0, и хеш сборки её не портит.
        Assert.Contains("-split '\\+'", script, StringComparison.Ordinal);
        Assert.Contains("TrimStart('v','V')", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// БЕЗ ОБЕЩАННОЙ ВЕРСИИ ПРОВЕРКА ПРОПУСКАЕТСЯ, а не сравнивает панель с пустой строкой.
    /// Пустая версия — это не «любая версия», а «сверять не с чем»: иначе исправное обновление
    /// объявлялось бы неудачным, и откат возвращал бы человеку ту же самую панель.
    /// </summary>
    [Fact]
    public void Без_обещанной_версии_проверка_копии_пропускается()
    {
        var script = UpdateScript.Text();

        Assert.Contains("set \"VERSION=\"", script, StringComparison.Ordinal);
        Assert.Contains("if \"%VERSION%\"==\"\" goto start", script, StringComparison.Ordinal);

        var withVersion = UpdateScript.Text("2.1.0");

        // Версия уходит в сценарий ТЕМ ЖЕ значением, каким её называет выпуск, и пропуск стоит
        // ровно у пустой версии: «0.0.1» — это обещание, а не отсутствие обещания.
        Assert.Contains("set \"VERSION=2.1.0\"", withVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("set \"VERSION=0.0.1\"", withVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПУТЬ С ПРОБЕЛОМ И ХВОСТОВЫМ «\» — тот самый случай, на котором обновление v1 уже
    /// спотыкалось: панель лежит в <c>%LOCALAPPDATA%\Programs\DSH Panel</c>, и путь, ушедший
    /// в кавычках с хвостовым разделителем, съедает закрывающую кавычку — robocopy получает
    /// ключи как часть пути и отказывается копировать.
    ///
    /// Проверяется и чистым правилом (<see cref="UpdateScript.SafePath"/>), и тем, что
    /// действительное попадает в командную строку.
    /// </summary>
    [Fact]
    public void Сценарий_переживает_путь_с_пробелом_и_хвостовым_разделителем()
    {
        Assert.Equal(@"C:\Users\кто-то\AppData\Local\Programs\DSH Panel", UpdateScript.SafePath(@"C:\Users\кто-то\AppData\Local\Programs\DSH Panel\"));
        Assert.Equal(@"C:\Users\кто-то\AppData\Local\Programs\DSH Panel", UpdateScript.SafePath(@"C:\Users\кто-то\AppData\Local\Programs\DSH Panel\\"));
        Assert.Equal(@"C:\Users\кто-то\Programs\DSH Panel", UpdateScript.SafePath(@"C:\Users\кто-то\Programs\DSH Panel/"));

        // Корень диска остаётся корнем: срезать у него разделитель — значит не оставить ничего.
        Assert.Equal(@"C:\", UpdateScript.SafePath(@"C:\"));
        Assert.Equal(string.Empty, UpdateScript.SafePath(null));

        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        // В командной строке путь панели стоит БЕЗ хвостового разделителя и в кавычках.
        Assert.Contains("\"" + stand.Target + "\"", preparation.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"" + stand.Target + "\\\"", preparation.CommandLine, StringComparison.Ordinal);
        Assert.Contains("\"" + preparation.StagedFolder + "\"", preparation.CommandLine, StringComparison.Ordinal);
        Assert.Contains("\"" + preparation.BackupFolder + "\"", preparation.CommandLine, StringComparison.Ordinal);

        // В теле сценария путей нет вовсе: они приходят аргументами, потому что cmd читает .cmd
        // в кодировке консоли, и не-ASCII путь в теле превращается в несуществующий.
        var script = File.ReadAllText(preparation.ScriptPath);

        Assert.DoesNotContain(stand.Root, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stand.Target, script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ТЕЛО СЦЕНАРИЯ — ТОЛЬКО ASCII, и пути в шапке журнала пишет сама панель: cmd.exe читает
    /// <c>.cmd</c> в кодировке консоли (на русской Windows — cp866), и не-ASCII знак в теле
    /// делает путь несуществующим — панель закрывалась, а обновление молча не применялось.
    /// </summary>
    [Fact]
    public void Тело_сценария_только_ASCII_а_пути_пишет_панель()
    {
        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        var script = File.ReadAllText(preparation.ScriptPath);

        Assert.All(
            script,
            symbol => Assert.True(symbol < 128, "в теле сценария не-ASCII знак: " + (int)symbol));

        var header = File.ReadAllText(preparation.LogPath);

        Assert.Contains(preparation.Version, header, StringComparison.Ordinal);
        Assert.Contains(stand.Target, header, StringComparison.Ordinal);
        Assert.Contains(preparation.StagedFolder, header, StringComparison.Ordinal);

        // Журнал начинается шапкой, а итог дописывает сценарий: до прогона итога в нём нет.
        Assert.Equal(UpdateResultKind.None, UpdateOutcomes.KindOf(UpdateOutcomes.ReadMarker(header)));
    }

    /// <summary>
    /// РЕЖИМ ЗАПУСКА НЕ ТЕРЯЕТСЯ и не приходит от человека: панель, работавшая значком, после
    /// обновления обязана подняться так же, а не окном на весь экран. Знаки, которыми cmd
    /// разделяет команды, из ключа вырезаются: сценарий остаётся на диске, и «&amp;» в нём
    /// разобрал бы строку запуска на несколько команд.
    /// </summary>
    [Fact]
    public void Режим_запуска_доходит_до_сценария_и_чистится()
    {
        using var stand = new Stand();

        var assets = Ready(stand);
        var preparation = UpdateInstall.Prepare(
            Request(stand, assets, startArgument: "--tray"),
            stand.Download,
            _ => "2.1.0");

        Assert.True(preparation.Ok, preparation.RefusalKey);

        var script = File.ReadAllText(preparation.ScriptPath);

        Assert.Contains("if \"%START%\"==\"\"", script, StringComparison.Ordinal);
        Assert.Contains("%START%", script, StringComparison.Ordinal);
        Assert.Contains("\"--tray\"", preparation.CommandLine, StringComparison.Ordinal);

        using var second = new Stand();
        var secondAssets = Ready(second);
        var harmful = Prepare(
            second,
            secondAssets,
            out _,
            request: Request(second, secondAssets, startArgument: "--tray & del /q \"%TARGET%\""));

        Assert.True(harmful.Ok, harmful.RefusalKey);
        Assert.DoesNotContain("&", harmful.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q", harmful.CommandLine, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ чтение итога

    /// <summary>
    /// ИТОГ ЧИТАЕТСЯ ПО ВСЕМ СВОИМ ЗНАЧЕНИЯМ, и каждое называет свою причину. Набор итогов
    /// перенесён у панели 1.x дословно: это словарь, которым обмениваются сценарий и панель.
    /// Версия в папке обновления СТРОГО новее установленной — значит замена не состоялась.
    /// </summary>
    [Theory]
    [InlineData("applied", UpdateResultKind.Applied, UpdateVerdict.NotApplied, "UpdateReasonStillOld")]
    [InlineData("applied-no-panel", UpdateResultKind.AppliedNoPanel, UpdateVerdict.NotApplied, "UpdateReasonNoPanel")]
    [InlineData("rolled-back", UpdateResultKind.RolledBack, UpdateVerdict.RolledBack, "UpdateReasonRolledBack")]
    [InlineData("failed-restore", UpdateResultKind.FailedRestore, UpdateVerdict.NotApplied, "UpdateReasonRestoreFailed")]
    [InlineData("failed-no-backup", UpdateResultKind.FailedNoBackup, UpdateVerdict.NotApplied, "UpdateReasonNoBackup")]
    [InlineData("not-applied-panel-running", UpdateResultKind.NotAppliedPanelRunning, UpdateVerdict.NotApplied, "UpdateReasonPanelRunning")]
    [InlineData("not-applied-mutex", UpdateResultKind.NotAppliedMutex, UpdateVerdict.NotApplied, "UpdateReasonMutexBusy")]
    [InlineData("mutex-check-failed", UpdateResultKind.MutexCheckFailed, UpdateVerdict.NotApplied, "UpdateReasonMutexUnknown")]
    public void Итог_читается_по_всем_значениям(
        string marker, UpdateResultKind kind, UpdateVerdict verdict, string reasonKey)
    {
        using var stand = new Stand();

        Stage(stand.Paths, "2.1.0", marker);

        var outcome = UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true);

        Assert.NotNull(outcome);
        Assert.Equal(verdict, outcome!.Verdict);
        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(marker, outcome.Marker);
        Assert.Equal(reasonKey, outcome.ReasonKey);
        Assert.Equal("2.1.0", outcome.Version);
        Assert.True(outcome.Failed);
    }

    /// <summary>
    /// МУСОРНЫЙ ЖУРНАЛ И ОТСУТСТВИЕ ЖУРНАЛА — РАЗНЫЕ ОТВЕТЫ: «сценарий до итога не дошёл»
    /// и «итог есть, но незнакомый» говорят человеку разное. Молчания нет ни в одном случае:
    /// папка новее установленной панели — это неприменившееся обновление, о котором надо сказать.
    /// </summary>
    [Fact]
    public void Мусорный_журнал_и_журнал_без_итога_различаются()
    {
        using (var stand = new Stand())
        {
            Stage(stand.Paths, "2.1.0", "какой-то-чужой-итог");

            var outcome = UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true);

            Assert.NotNull(outcome);
            Assert.Equal(UpdateResultKind.Unknown, outcome!.Kind);
            Assert.Equal("UpdateReasonUnknown", outcome.ReasonKey);
            Assert.Equal(UpdateVerdict.NotApplied, outcome.Verdict);
        }

        using (var stand = new Stand())
        {
            var folder = Stage(stand.Paths, "2.1.0", string.Empty);
            var kept = Path.Combine(stand.UpdateRoot, "failed-2.1.0");

            var outcome = UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true);

            Assert.NotNull(outcome);
            Assert.Equal(UpdateResultKind.None, outcome!.Kind);
            Assert.Equal(UpdateVerdict.NotApplied, outcome.Verdict);

            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(kept));
        }

        using (var stand = new Stand())
        {
            // Папки обновления нет вовсе — рассказывать нечего.
            Assert.Null(UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true));
        }
    }

    /// <summary>
    /// ПРИМЕНИВШЕЕСЯ ОБНОВЛЕНИЕ УБИРАЕТ ЗА СОБОЙ: папку сборки, скачанный архив, файл сумм,
    /// сценарий и журнал. Страховочную копию НЕ трогаем — ею человек откатывается руками,
    /// и уборка не имеет права забрать единственный путь назад.
    /// </summary>
    [Fact]
    public void Применившееся_обновление_убирает_за_собой()
    {
        using var stand = new Stand();

        Prepared(stand, out _, out var preparation);

        // Замена состоялась: панель поднялась НОВОЙ версии, то есть версия папки не новее нашей.
        var root = stand.UpdateRoot;
        File.AppendAllText(preparation.LogPath, UpdateOutcomes.ResultMarkerPrefix + "applied" + Environment.NewLine);

        var outcome = UpdateOutcomes.StartupNotice(stand.Paths, preparation.Version, allowed: true);

        Assert.NotNull(outcome);
        Assert.Equal(UpdateVerdict.Applied, outcome!.Verdict);
        Assert.False(outcome.Failed);

        Assert.False(Directory.Exists(preparation.StagedFolder));
        Assert.False(File.Exists(preparation.ScriptPath));
        Assert.False(File.Exists(preparation.LogPath));
        Assert.Empty(Directory.GetFiles(root));

        // Страховочная копия на месте: она и есть путь назад.
        Assert.True(Directory.Exists(preparation.BackupFolder));
        Assert.True(File.Exists(Path.Combine(preparation.BackupFolder, UpdateStaging.PanelExeName)));
    }

    /// <summary>
    /// О НЕУДАВШЕМСЯ ОБНОВЛЕНИИ ГОВОРЯТ ОДИН РАЗ. Папка переименовывается в
    /// <c>failed-&lt;версия&gt;</c>: она остаётся для разбора и ручной починки, но повторного
    /// сообщения не будет — ни при следующем запуске, ни после перезагрузки. Иначе человек
    /// читал бы одно и то же при каждом старте панели.
    /// </summary>
    [Fact]
    public void О_неудавшемся_обновлении_говорят_один_раз()
    {
        using var stand = new Stand();

        var folder = Stage(stand.Paths, "2.1.0", "not-applied-mutex");

        var first = UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true);

        Assert.NotNull(first);
        Assert.Equal(UpdateVerdict.NotApplied, first!.Verdict);
        Assert.Equal(UpdateResultKind.NotAppliedMutex, first.Kind);
        Assert.Equal("UpdateReasonMutexBusy", first.ReasonKey);
        Assert.Equal("2.1.0", first.Version);

        var kept = Path.Combine(stand.UpdateRoot, "failed-2.1.0");

        Assert.Equal(kept, first.FailedFolder);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(kept));
        Assert.True(File.Exists(Path.Combine(kept, UpdateStaging.PanelExeName)));

        // Второй запуск: папка уже failed-*, подготовленной сборки нет — и рассказывать нечего.
        Assert.Null(UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: true));
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА НЕ ЧИТАЕТ И НЕ УБИРАЕТ НИЧЕГО: состояние обновления — это состояние
    /// панели, и прогон проверки без права не трогает здесь ни один файл.
    /// </summary>
    [Fact]
    public void Без_права_чтение_итога_ничего_не_трогает()
    {
        using var stand = new Stand();

        var folder = Stage(stand.Paths, "2.1.0", "applied");

        Assert.Null(UpdateOutcomes.StartupNotice(stand.Paths, "2.0.0", allowed: false));
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(stand.UpdateRoot, UpdateScript.LogFileName)));
    }

    // ------------------------------------------------------------------ право и чистота

    /// <summary>
    /// ПРАВО ГОТОВИТЬ ЗАМЕНУ — ТО ЖЕ, ЧТО У НАСТРОЕК И КОПИЙ: изолированный прогон (у него свой
    /// корень) и обычный запуск человеком. Прогон проверки без корня права не имеет, потому что
    /// подготовка читает папку панели и пишет в состояние.
    /// </summary>
    [Fact]
    public void Право_готовить_замену_как_у_копий()
    {
        using var stand = new Stand();

        var isolated = RunContext.Create(RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, stand.Root }, _ => null));
        var owner = RunContext.Create(RunRequest.Owner());

        Assert.True(UpdateInstall.For(isolated, humanLaunch: false));
        Assert.True(UpdateInstall.For(isolated, humanLaunch: true));
        Assert.True(UpdateInstall.For(owner, humanLaunch: true));
        Assert.False(UpdateInstall.For(owner, humanLaunch: false));
    }

    /// <summary>
    /// БЕЗ ПРАВА ПОДГОТОВКА НЕ НАЧИНАЕТСЯ ВООБЩЕ: ни каталога обновления, ни единого запроса
    /// в сеть, ни единого байта в папке панели. Право — не предупреждение, а отказ.
    /// </summary>
    [Fact]
    public void Без_права_не_пишется_ничего()
    {
        using var stand = new Stand();
        var before = stand.Snapshot(stand.Target);

        var assets = Ready(stand);
        var preparation = UpdateInstall.Prepare(
            Request(stand, assets, allowed: false),
            stand.Download,
            _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.NotAllowed, preparation.Refusal);
        Assert.Equal("UpdateRefusedLocked", preparation.RefusalKey);
        Assert.False(Directory.Exists(stand.UpdateRoot));
        Assert.Empty(stand.Download.Asked);
        Assert.Equal(before, stand.Snapshot(stand.Target));
    }

    /// <summary>
    /// ВЫПУСК НЕ НОВЕЕ УСТАНОВЛЕННОЙ ПАНЕЛИ — обновляться некуда, и это отказ, а не «на всякий
    /// случай поставим». Отдельный ключ <c>IgnoreNewer</c> есть ровно для самотеста, который
    /// гоняет тот же путь на выпуске той же версии; ни одной проверки целостности он не снимает.
    /// </summary>
    [Fact]
    public void Выпуск_не_новее_обновлением_не_считается()
    {
        using var stand = new Stand();

        var assets = Ready(stand);

        var refused = UpdateInstall.Prepare(
            Request(stand, assets, release: "v2.0.0", current: "2.0.0"),
            stand.Download,
            _ => "2.0.0");

        Assert.False(refused.Ok);
        Assert.Equal(UpdateRefusal.AlreadyLatest, refused.Refusal);
        Assert.False(Directory.Exists(stand.UpdateRoot));
        Assert.Empty(stand.Download.Asked);

        // С явным разрешением тот же путь проходит целиком: суммы, распаковка и версия —
        // как обычно, ни одной проверки не снято.
        var forced = UpdateInstall.Prepare(
            Request(stand, assets, release: "v2.0.0", current: "2.0.0", ignoreNewer: true),
            stand.Download,
            _ => "2.0.0");

        Assert.True(forced.Ok, forced.RefusalKey + ": " + forced.Detail);
        Assert.Equal("2.0.0", forced.Version);
    }

    /// <summary>
    /// ПРИ ОТКАЗЕ НИЧЕГО НЕ ПИШЕТСЯ ПОВЕРХ И СЛЕДОВ НЕ ОСТАЁТСЯ В ЧУЖОМ КАТАЛОГЕ. Это главное
    /// обещание второго среза: подготовка читает папку панели и не меняет в ней ни байта,
    /// а за собой убирает всё, что успела положить (папку сборки, архив, файл сумм).
    /// </summary>
    [Fact]
    public void При_отказе_ничего_не_пишется_поверх_и_следов_не_остаётся()
    {
        using var stand = new Stand();

        var panel = stand.Snapshot(stand.Target);
        var foreign = stand.Snapshot(stand.Other);

        // Отказ на сверке версии — самый поздний отказ, после которого уже есть и распакованная
        // папка, и скачанный архив: убирать придётся больше всего.
        var assets = Ready(stand);
        var preparation = UpdateInstall.Prepare(Request(stand, assets), stand.Download, _ => "1.0.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.VersionMismatch, preparation.Refusal);

        Assert.Equal(panel, stand.Snapshot(stand.Target));
        Assert.Equal(foreign, stand.Snapshot(stand.Other));

        // В каталоге обновления — пусто: ни папки сборки, ни архива, ни файла сумм, ни сценария.
        Assert.Empty(Directory.GetFiles(stand.UpdateRoot));
        Assert.Empty(Directory.GetDirectories(stand.UpdateRoot));
    }

    /// <summary>
    /// НЕДОСТУПНАЯ ПАПКА ПАНЕЛИ — ОТКАЗ, А НЕ «ОБНОВЛЕНИЕ ГОТОВО»: без страховочной копии
    /// у сценария нет пути назад, и готовить замену нечем. Ключ отказа называет причину.
    /// </summary>
    [Fact]
    public void Без_страховочной_копии_обновление_не_готовится()
    {
        using var stand = new Stand();

        var assets = Ready(stand);
        var missing = Path.Combine(stand.Root, "Programs", "Нет такой папки") + "\\";

        var request = new UpdateRequest(
            stand.Paths, "2.0.0", missing, "v2.1.0", assets, true, false, string.Empty, string.Empty, Now);

        var preparation = UpdateInstall.Prepare(request, stand.Download, _ => "2.1.0");

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.NoBackup, preparation.Refusal);
        Assert.Equal("UpdateNoBackup", preparation.RefusalKey);
        Assert.Empty(Directory.GetDirectories(stand.UpdateRoot));
    }

    /// <summary>Ключи отказов названы и различимы: по ним интерфейс берёт строку из словаря.</summary>
    [Fact]
    public void У_каждого_отказа_есть_свой_ключ()
    {
        var keys = Enum.GetValues<UpdateRefusal>()
            .Select(UpdateRefusals.Key)
            .Where(key => key.Length > 0)
            .ToArray();

        Assert.Equal(Enum.GetValues<UpdateRefusal>().Length - 1, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(string.Empty, UpdateRefusals.Key(UpdateRefusal.None));

        // Каждый итог тоже назван своим ключом, и «итога нет» — единственный без ключа-исключения.
        var reasons = Enum.GetValues<UpdateResultKind>().Select(UpdateOutcomes.ReasonKeyFor).ToArray();

        Assert.All(reasons, key => Assert.StartsWith("UpdateReason", key, StringComparison.Ordinal));
    }
}
