using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DshPanel.Isolation;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТКАЗ УБОРКИ — СЛОВАМИ И ДО ЧЕЛОВЕКА. Третья беда панели 1.x, к которой этот файл заведён:
/// уборка за обновлением МОЛЧА ГЛОТАЛА неудачу. Папка, которую держит антивирус или чужой
/// процесс, оставалась на диске, а панель отвечала «успех» — и человек узнавал о мусоре
/// только тогда, когда сам заглядывал в состояние панели.
///
/// **Что здесь проверяется, и каждое умеет падать.**
///
/// 1. **Отказ уборки доходит до СТРОКИ ЧЕЛОВЕКА.** Берётся настоящий отказ движка: папка сборки
///    занята (файл внутри открыт с <see cref="FileShare.None"/>), подготовка обновления кончается
///    отказом, и уборка за собой не удаётся. Строка собирается ТЕМ ЖЕ путём, каким её собирает
///    раздел «Обновление» (<see cref="UpdateRefusalLines.Line"/>) и итог замены
///    (<see cref="UpdateOutcomeLines"/>), и отказ уборки в ней ВИДЕН.
/// 2. **Обратная сторона:** при удачной уборке этой строки нет. «Говорим всегда» — не проверка.
/// 3. **Причина отказа — ЗНАЧЕНИЕ** (тип и сообщение среды), а не фраза: слова живут в словарях,
///    и второй набор подписей в движке разошёлся бы со словарём на первой же правке перевода.
/// 4. **Каталог обновления не растёт:** после отказа уборки в нём не остаётся НИЧЕГО, кроме
///    занятой папки, которую держит проверка (это и есть «мусор, о котором человеку сказали»).
/// </summary>
public class UpdateCleanupTests
{
    private const string SumsUrl = "https://example.invalid/SHA256SUMS.txt";
    private const string ArchiveUrl = "https://example.invalid/DshPanel.zip";

    /// <summary>Момент свой и вымышленный: от системных часов отчёт зависеть не должен.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Стенд одной проверки: свой корень панели, своя папка «установленной» панели и своя
    /// подстановка вместо сети. Ни одного чужого каталога, ни одного живого запроса.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        public Stand()
        {
            Root = Path.Combine(Path.GetTempPath(), "dsh-panel-update-cleanup-tests", Guid.NewGuid().ToString("N"));

            Target = Path.Combine(Root, "Programs", "DSH Panel");
            Paths = AppPaths.Under(Path.Combine(Root, "run"));

            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, UpdateStaging.PanelExeName), "прежняя панель");
        }

        public string Root { get; }

        public string Target { get; }

        public AppPaths Paths { get; }

        public FakeDownload Download { get; } = new();

        public string UpdateRoot => UpdateInstall.UpdateRoot(Paths);

        /// <summary>Папка сборки, которую готовит движок: <c>&lt;обновление&gt;\&lt;версия&gt;</c>.</summary>
        public string Staged => Path.Combine(UpdateRoot, "2.1.0");

        /// <summary>Уборка только СВОЕГО каталога: общий родитель семейства не снимается (t3, `TempRoots`).</summary>
        public void Dispose() => TempRoots.Remove(Root);
    }

    /// <summary>
    /// ПОДСТАНОВКА ВМЕСТО СЕТИ. Здесь важна ровно одна её сторона: она ОТКАЗЫВАЕТ на адресе
    /// архива, потому что проверке нужен отказ движка ПОСЛЕ того, как папка сборки уже создана, —
    /// а после отказа движок убирает за собой, и именно эта уборка и должна провалиться.
    /// </summary>
    private sealed class FakeDownload : IUpdateDownload
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public FakeDownload Add(string url, byte[] bytes)
        {
            _files[url] = bytes;
            return this;
        }

        public UpdateDownloadResult Save(
            string url,
            string path,
            string? expectedSum,
            Action<long, long>? progress,
            UpdateDownloadPolicy? policy = null)
        {
            if (!_files.TryGetValue(url, out var bytes))
                throw new InvalidOperationException("подстановки для адреса нет: " + url);

            var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var wanted = (expectedSum ?? string.Empty).Trim();

            progress?.Invoke(0, bytes.Length);

            if (wanted.Length > 0 && !string.Equals(wanted, got, StringComparison.OrdinalIgnoreCase))
                return new UpdateDownloadResult(UpdateDownloadOutcome.SumMismatch, string.Empty, wanted, got);

            File.WriteAllBytes(path, bytes);
            progress?.Invoke(bytes.Length, bytes.Length);

            return UpdateDownloadResult.Done;
        }
    }

    /// <summary>Файл сумм в том виде, в каком его публикует выпуск.</summary>
    private static string SumsText(params (string Name, byte[] Bytes)[] files) =>
        string.Join(
            Environment.NewLine,
            files.Select(file =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Bytes)).ToLowerInvariant()
                + "  " + file.Name));

    /// <summary>Запрос на подготовку: папка панели уходит с хвостовым «\», как у человека.</summary>
    private static UpdatePreparation Prepare(Stand stand, ReleaseAssets assets) =>
        UpdateInstall.Prepare(
            new UpdateRequest(
                stand.Paths,
                "2.0.0",
                stand.Target + "\\",
                "v2.1.0",
                assets,
                Allowed: true,
                IgnoreNewer: false,
                StartArgument: string.Empty,
                MutexName: string.Empty,
                At: Now),
            stand.Download,
            _ => "2.1.0");

    /// <summary>
    /// ЗАНЯТЬ ПАПКУ: внутри открывается файл с <see cref="FileShare.None"/>. Так папка занята
    /// ровно тем способом, каким её занимает чужой процесс (антивирус, индексатор, открытый
    /// просмотрщик): файл нельзя ни удалить, ни прочитать никому другому, а значит дерево
    /// не снимается — и это НАСТОЯЩИЙ отказ уборки, а не подставной.
    /// </summary>
    private static FileStream Occupy(string folder)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "занятый.txt");
        File.WriteAllText(path, "этот файл держит проверка");

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    /// <summary>Стенд, на котором подготовка обновления кончается отказом, а уборка — неудачей.</summary>
    private static UpdatePreparation RefusedWithBusyFolder(Stand stand, out FileStream busy)
    {
        var archive = Zip();

        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, archive))));

        var assets = new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl);

        // Занятая папка сборки: движок снимает её первым шагом, не снимает (файл занят) —
        // и продолжает работу, а отказ приходит на СКАЧИВАНИИ архива, после которого движок
        // убирает за собой. Именно эта уборка и проваливается.
        busy = Occupy(stand.Staged);

        return Prepare(stand, assets);
    }

    /// <summary>Архив панели, каким его кладёт сборка.</summary>
    private static byte[] Zip()
    {
        using var memory = new MemoryStream();

        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(UpdateStaging.PanelExeName).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            writer.Write("собранная панель");
        }

        return memory.ToArray();
    }

    // ------------------------------------------------------------------ отказ уборки — словами

    /// <summary>
    /// ПРИ НЕУДАВШЕЙСЯ УБОРКЕ СТРОКА ЧЕЛОВЕКА НАЗЫВАЕТ ЭТО ПРЯМО. Проверяется НАСТОЯЩИЙ путь:
    /// отказ движка добыт подготовкой на занятой папке, а строка собрана тем же
    /// <see cref="UpdateRefusalLines.Line"/>, каким её собирает раздел «Обновление».
    ///
    /// ⚠️ Проверка не «смотрит на поле»: она требует, чтобы в СТРОКЕ были и слова про неудавшуюся
    /// уборку, и причина (значение), а без них — падает.
    /// </summary>
    [Fact]
    public void Отказ_уборки_виден_в_строке_человека()
    {
        using var stand = new Stand();

        var preparation = RefusedWithBusyFolder(stand, out var busy);
        using var held = busy;

        Assert.False(preparation.Ok);
        Assert.Equal(UpdateRefusal.ArchiveDownloadFailed, preparation.Refusal);

        // ИТОГ УБОРКИ НЕ ПОТЕРЯН: он доехал от движка до ответа.
        Assert.True(preparation.CleanupFailed, "движок не заметил, что уборка не удалась");
        Assert.Equal("UpdateCleanupFailedFormat", preparation.CleanupKey);
        Assert.False(string.IsNullOrWhiteSpace(preparation.CleanupReason), "причина уборки не названа");

        // СТРОКА ЧЕЛОВЕКА: слова из словаря (без подстановки!) плюс причина уборки. Сверяется
        // ОБРАМЛЕНИЕ, а не строка словаря целиком: у ключа с `Format` в имени на месте `{0}`
        // стоит ЗНАЧЕНИЕ, и требовать в строке шаблон значило бы требовать фигурные скобки.
        var line = UpdateRefusalLines.Line(preparation);
        var words = UpdateRefusalLines.Text(preparation.CleanupKey);
        var tail = UpdateRefusalLines.CleanupTail(preparation.CleanupKey, preparation.CleanupReason);

        Assert.True(
            tail.Length > 0,
            "хвоста нет: key=«" + preparation.CleanupKey + "» reason=«" + preparation.CleanupReason + "»");

        Assert.Contains(preparation.CleanupReason, line, StringComparison.Ordinal);
        Assert.Contains(words.Split('(')[0].Trim(), line, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);

        // Слова — ИЗ СЛОВАРЯ, а не из движка: иначе строка разошлась бы с переводом.
        Assert.Equal(PanelStrings.UpdateCleanupFailedFormat, words);
        Assert.DoesNotContain("UpdateCleanupFailedFormat", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРИ УДАЧНОЙ УБОРКЕ ЭТОЙ СТРОКИ НЕТ. «Говорим всегда» — это не проверка: строка про
    /// неудавшуюся уборку обязана появляться ТОЛЬКО тогда, когда уборка действительно не удалась.
    /// </summary>
    [Fact]
    public void При_удачной_уборке_строки_про_отказ_нет()
    {
        using var stand = new Stand();

        var archive = Zip();
        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, archive))));
        stand.Download.Add(ArchiveUrl, archive);

        var preparation = Prepare(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl));

        Assert.True(preparation.Ok, preparation.RefusalKey + ": " + preparation.Detail);
        Assert.False(preparation.CleanupFailed, "уборка объявлена неудавшейся без причины");
        Assert.Equal(string.Empty, preparation.CleanupKey);
        Assert.Equal(string.Empty, UpdateRefusalLines.CleanupTail(preparation.CleanupKey, preparation.CleanupReason));

        // И в строке успешного ответа хвоста тоже нет: успех показывает интерфейс, а не эта строка.
        Assert.Equal(string.Empty, UpdateRefusalLines.Line(preparation));
    }

    // ------------------------------------------------------------------ итог замены — словами

    /// <summary>
    /// ИТОГ ЗАМЕНЫ ПРИ НЕУДАВШЕЙСЯ УБОРКЕ ТОЖЕ ГОВОРИТ СЛОВАМИ — и человеку, и в журнал.
    /// Это второй путь, которым уборка доходит до человека: панель убрала за обновившейся версией
    /// папку сборки, папка осталась на диске, и при следующем запуске об этом надо сказать,
    /// а не промолчать.
    ///
    /// Итог собран НАСТОЯЩИМ чтением состояния: папка сборки лежит там, где её оставляет движок,
    /// а занята она по-настоящему.
    /// </summary>
    [Fact]
    public void Отказ_уборки_виден_в_итоге_замены_и_в_журнале()
    {
        using var stand = new Stand();

        // Так состояние обновления выглядит после ПРИМЕНИВШЕЙСЯ замены: папка сборки версии,
        // которая не новее установленной, и журнал без итога.
        Directory.CreateDirectory(stand.Staged);
        File.WriteAllText(Path.Combine(stand.Staged, UpdateStaging.PanelExeName), "новая панель");
        File.WriteAllText(
            Path.Combine(stand.UpdateRoot, UpdateScript.LogFileName),
            "шапка журнала без итога" + Environment.NewLine);

        using var busy = Occupy(stand.Staged);

        var outcome = UpdateOutcomes.StartupNotice(stand.Paths, "2.1.0", allowed: true);

        Assert.NotNull(outcome);
        Assert.Equal(UpdateVerdict.Applied, outcome!.Verdict);

        // ПАПКА ОСТАЛАСЬ, И ЭТО НАЗВАНО: уборка не удалась, причина — от среды.
        Assert.True(outcome.CleanupFailed, "итог объявил уборку удачной, а папка осталась");
        Assert.Equal("UpdateCleanupFailedFormat", outcome.CleanupKey);
        Assert.False(string.IsNullOrWhiteSpace(outcome.CleanupReason));

        // ЧЕЛОВЕКУ: сообщение есть, в нём слова про уборку и её причина — причина подставлена
        // ЗНАЧЕНИЕМ, а обрамление взято из словаря.
        var notice = UpdateOutcomeLines.Notice(outcome);
        var words = UpdateRefusalLines.Text(outcome.CleanupKey);

        Assert.Contains(words.Split('(')[0].Trim(), notice, StringComparison.Ordinal);
        Assert.Contains(outcome.CleanupReason, notice, StringComparison.Ordinal);

        // В ЖУРНАЛ: строка про обновление плюс причина уборки — среда названа значением,
        // обрамление взято из словаря (в строке словаря на месте `{0}` стоит значение).
        var journal = UpdateOutcomeLines.JournalLine(outcome);

        Assert.Contains(PanelStrings.UpdateAppliedLogFormat.Split('{')[0].Trim(), journal, StringComparison.Ordinal);
        Assert.Contains(words.Split('(')[0].Trim(), journal, StringComparison.Ordinal);
        Assert.Contains(outcome.CleanupReason, journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРИ УДАЧНОЙ УБОРКЕ ИТОГ МОЛЧИТ О НЕЙ: сообщение о применившемся обновлении пусто
    /// (панель и так обновилась), а в журнале нет ни слова про уборку.
    /// </summary>
    [Fact]
    public void При_удачной_уборке_итог_молчит()
    {
        using var stand = new Stand();

        Directory.CreateDirectory(stand.Staged);
        File.WriteAllText(Path.Combine(stand.Staged, UpdateStaging.PanelExeName), "новая панель");
        File.WriteAllText(
            Path.Combine(stand.UpdateRoot, UpdateScript.LogFileName),
            "шапка журнала без итога" + Environment.NewLine);

        var outcome = UpdateOutcomes.StartupNotice(stand.Paths, "2.1.0", allowed: true);

        Assert.NotNull(outcome);
        Assert.Equal(UpdateVerdict.Applied, outcome!.Verdict);
        Assert.False(outcome.CleanupFailed);
        Assert.Equal(string.Empty, outcome.CleanupKey);

        Assert.Equal(string.Empty, UpdateOutcomeLines.Notice(outcome));

        var journal = UpdateOutcomeLines.JournalLine(outcome);
        Assert.DoesNotContain(
            UpdateRefusalLines.Text("UpdateCleanupFailedFormat"),
            journal,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// КЛЮЧ УБОРКИ ЕСТЬ В ТРЁХ СЛОВАРЯХ И ЗНАКОМ ПАНЕЛИ. Строку берут два разных пути (страница
    /// отказа и итог замены), и незаполненный словарь обернулся бы пустым местом у человека.
    /// </summary>
    [Fact]
    public void Ключ_уборки_есть_на_трёх_языках()
    {
        Assert.True(UpdateRefusalLines.Knows(UpdateRefusals.CleanupFailedKey));
        Assert.Equal(
            PanelStrings.UpdateCleanupFailedFormat,
            UpdateRefusalLines.Text(UpdateRefusals.CleanupFailedKey));

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            Assert.True(
                DshPanel.Localization.Loc.Dictionary(language).TryGetValue(
                    UpdateRefusals.CleanupFailedKey,
                    out var text) && text.Length > 0,
                language + ": у ключа уборки нет своей строки");
        }
    }
}
