using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Список готовых копий: что видно в окне «Копии». Проверяются не «исключений не было»,
/// а РЕШЕНИЯ: пустая папка — не ошибка, недоступная папка — не «копий нет», чужой zip — не копия,
/// а прогон проверки не читает папку владельца вовсе (красные линии 4 и 7).
/// </summary>
public class BackupFolderTests
{
    /// <summary>Опись нашей копии — как её пишет снятие: поля те же, имена в JSON те же.</summary>
    private static BackupManifest Manifest(string created = "2026-09-26 01:35:00") => new()
    {
        CreatedAt = created,
        WithEngine = true,
        WithSessions = true,
        WithCredentials = true,
        TotalFiles = 12,
        TotalBytes = 4096,
        Versions = { ["dsh"] = "0.1.5-rc.2" },
    };

    /// <summary>Настоящий zip с описью внутри — иначе проверялось бы чтение несуществующего файла.</summary>
    private static string WriteArchive(string folder, string name, BackupManifest? manifest)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);

        using var zip = new ZipArchive(File.Open(path, FileMode.Create), ZipArchiveMode.Create);

        if (manifest is not null)
        {
            var entry = zip.CreateEntry("manifest.json");
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
        }
        else
        {
            var entry = zip.CreateEntry("мусор.txt");
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write("это не копия панели");
        }

        return path;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Папки_нет_список_пуст_и_это_не_ошибка()
    {
        var root = NewRoot();
        try
        {
            var list = BackupFolder.List(Path.Combine(root, "нет такой папки"));

            Assert.True(list.Ok);
            Assert.Empty(list.Entries);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Копия читается по описи. Строку для человека здесь больше НЕ собирают: абзац заменён
    /// таблицей (замечание владельца 28.09.2026), и её ячейки проверяет <c>BackupTableViewTests</c>.
    /// </summary>
    [Fact]
    public void Копия_читается_по_описи()
    {
        var root = NewRoot();
        try
        {
            var path = WriteArchive(root, "dsh2-backup-2026-09-26-013500-full.zip", Manifest());

            var entry = BackupFolder.Inspect(path);

            Assert.True(entry.Readable);
            Assert.Equal(12, entry.Files);
            Assert.True(entry.WithEngine);
            Assert.True(entry.WithSessions);
            Assert.True(entry.WithCredentials);
            Assert.Equal("0.1.5-rc.2", entry.EngineVersion);
            Assert.Equal(new DateTime(2026, 9, 26, 1, 35, 0), entry.CreatedAt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Свежие сверху. Порядок берётся из ОТМЕТКИ ВРЕМЕНИ В ИМЕНИ, а не из имени целиком: имя
    /// начинается с префикса панели, и посимвольное сравнение имени поставило бы архивы в порядке,
    /// который к дате отношения не имеет.
    ///
    /// ⚠️ Среди копий нарочно есть САМАЯ СВЕЖАЯ и САМАЯ СТАРАЯ — без них проверка соглашалась бы
    /// и с «как повезёт сортировке».
    /// </summary>
    [Fact]
    public void Свежие_копии_сверху()
    {
        var root = NewRoot();
        try
        {
            WriteArchive(root, "dsh2-backup-2026-09-26-010000-full.zip", Manifest("2026-09-26 01:00:00"));
            WriteArchive(root, "dsh2-backup-2026-09-26-050000-full.zip", Manifest("2026-09-26 05:00:00"));

            // Копия панели 1.x, и она свежее всех наших. В списке её НЕТ (решение владельца
            // 28.09.2026), и она не должна ни сбить порядок, ни появиться строкой.
            WriteArchive(root, "dsh-backup-2026-09-26-120000-full.zip", Manifest("2026-09-26 12:00:00"));

            var list = BackupFolder.List(root);

            Assert.True(list.Ok);
            Assert.Equal(2, list.Entries.Count);
            Assert.Equal(
                new[]
                {
                    "dsh2-backup-2026-09-26-050000-full.zip",
                    "dsh2-backup-2026-09-26-010000-full.zip",
                },
                list.Entries.Select(entry => entry.Name).ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// СПИСОК ПОКАЗЫВАЕТ ТОЛЬКО НАШИ КОПИИ, А ЧУЖИЕ НЕ ПОКАЗЫВАЕТ ВОВСЕ — решение владельца
    /// 28.09.2026: *«Удали у меня просто эти строки от старой версии панели, они уже не нужны…
    /// Пользователей у v1 нет, чтобы изобретать»*.
    ///
    /// ⚠️ **И рядом — вторая половина обещания, без которой первая была бы опасной: файлы
    /// на диске НЕ ТРОГАЮТСЯ.** Панель перестаёт их ПОКАЗЫВАТЬ, а не удаляет. Проверяется это
    /// не словами, а тем, что чужой архив после чтения списка:
    ///
    /// * по-прежнему лежит на диске ПОД ТЕМ ЖЕ ИМЕНЕМ (не удалён и не переименован);
    /// * читается панелью как архив (<see cref="BackupFolder.Inspect"/>) — то есть остался целым;
    /// * не попал в удаляемые ротацией (<see cref="BackupRotation.Plan"/>) — ни в счёт хранения,
    ///   ни в victims.
    ///
    /// Сторож ротации здесь не «на всякий случай»: скрытие стоит в ЧТЕНИИ СПИСКА, и соблазн
    /// перенести его в общий обход папки однажды появится. Тогда именно эта проверка и упадёт.
    /// </summary>
    [Fact]
    public void Список_показывает_только_наши_копии_а_чужие_не_трогает()
    {
        var root = NewRoot();
        try
        {
            WriteArchive(root, "dsh2-backup-2026-09-26-010000-full.zip", Manifest("2026-09-26 01:00:00"));

            // Наша предохранительная — в папке есть, в списке её нет (правило старше этой правки).
            WriteArchive(root, "dsh2-before-restore-2026-09-26-013000.zip", Manifest("2026-09-26 01:30:00"));

            // Копии прежней панели: обе её имени — и обычное, и предохранительное.
            var legacy = WriteArchive(root, "dsh-backup-2026-09-25-210000-full.zip", Manifest("2026-09-25 21:00:00"));
            var legacySafety = WriteArchive(root, "dsh-before-restore-2026-09-26-230000.zip", Manifest("2026-09-26 23:00:00"));

            // Не копии: чужой архив и вообще не zip. Оба обязаны остаться за списком.
            WriteArchive(root, "чужой-архив.zip", Manifest("2026-09-26 23:30:00"));
            File.WriteAllText(Path.Combine(root, "заметка.txt"), "не копия");

            var list = BackupFolder.List(root);

            Assert.True(list.Ok);
            Assert.Equal(new[] { "dsh2-backup-2026-09-26-010000-full.zip" }, list.Entries.Select(entry => entry.Name).ToArray());

            // 1. Файлы на диске на месте и под теми же именами — скрытие их не коснулось.
            Assert.True(File.Exists(legacy), "чужой архив исчез из папки: список не имеет права удалять");
            Assert.True(File.Exists(legacySafety), "предохранительная копия 1.x исчезла из папки");

            // 2. И читаются они по-прежнему: архив остался целым и разбираемым.
            var read = BackupFolder.Inspect(legacy);
            Assert.True(read.Readable, read.Error);

            // 3. И ротация их не удаляет и не считает: в плане их нет, а причина говорит о них словами.
            var plan = BackupRotation.Plan(BackupRotation.List(root), keepCount: 1);

            Assert.DoesNotContain(plan.Delete, file => file.Name.Contains("dsh-backup-", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.Delete, file => file.Name.Contains("dsh-before-restore-", StringComparison.Ordinal));
            Assert.Contains("1.x", plan.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Чужой zip с именем нашей копии копией НЕ считается: описи в нём нет, и это названо
    /// причиной. Иначе накат попытался бы разложить чужой архив.
    /// </summary>
    [Fact]
    public void Чужой_zip_не_выдаётся_за_копию()
    {
        var root = NewRoot();
        try
        {
            var path = WriteArchive(root, "dsh2-backup-2026-09-26-013500-full.zip", manifest: null);

            var entry = BackupFolder.Inspect(path);

            Assert.False(entry.Readable);
            Assert.NotEqual(string.Empty, entry.Error);
            Assert.Contains("описи", entry.Error);

            // И в списке он есть: пропустить его молча значило бы показать «копий меньше, чем есть».
            var list = BackupFolder.List(root);
            Assert.Single(list.Entries);
            Assert.False(list.Entries[0].Readable);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Архив С ОПИСЬЮ ЧУЖОГО ВИДА копией не считается: опись есть, но она не наша.
    ///
    /// ⚠️ Это НЕ тот же случай, что «описи нет вовсе», и проверяется отдельно. Так нашла себя
    /// выжившая мутация: подмена «наша ли опись» на «читать всегда» не роняла ничего, потому что
    /// архив без описи до этой проверки просто не доходит. Правило проекта: проверка обязана
    /// проходить через то состояние, в котором дефект возможен.
    /// </summary>
    [Fact]
    public void Опись_чужого_вида_не_считается_нашей_копией()
    {
        var root = NewRoot();
        try
        {
            var foreign = Manifest();
            foreign.Kind = "чужая-копия";

            var path = WriteArchive(root, "dsh2-backup-2026-09-26-013500-full.zip", foreign);

            var entry = BackupFolder.Inspect(path);

            Assert.False(entry.Readable);
            Assert.Contains("чужого вида", entry.Error);

            // И накат такой архив не примет — причина называется, а не «попробуем».
            Assert.False(BackupManifest.IsOurKind(foreign.Kind));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Прогон проверки НЕ читает папку копий и не снимает копию: его архив лёг бы в каталог
    /// владельца, а список его копий был бы личными данными (красные линии 4 и 7). Проверка
    /// идёт на настоящем архиве в папке: если бы гейт убрали, список стал бы непустым.
    /// </summary>
    [Fact]
    public void Прогон_проверки_не_читает_папку_копий_и_не_снимает_копию()
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "Копии");
            WriteArchive(folder, "dsh2-backup-2026-09-26-013500-full.zip", Manifest());

            var settings = new PanelSettings { BackupFolder = folder };
            Func<DshEngine?> noEngine = () => null;

            var controller = new BackupController(
                AppPaths.Under(root),
                () => settings,
                allowed: false,
                locateEngine: noEngine,
                clock: () => new DateTimeOffset(2026, 9, 26, 1, 35, 0, TimeSpan.Zero));

            controller.Refresh();

            Assert.False(controller.Writable);
            Assert.Empty(controller.Entries);
            Assert.Equal(PanelStrings.BackupNotAllowed, controller.ListProblem);

            // И сама копия не снимается: отказ, а не «попробуем».
            var copy = controller.CreateCopy(Path.Combine(folder, "dsh2-backup-test.zip"), serverRunning: false, shareable: false);

            Assert.False(copy.Ok);
            Assert.Null(copy.Result);
            Assert.Equal(PanelStrings.BackupNotAllowed, copy.Error);
            Assert.False(File.Exists(Path.Combine(folder, "dsh2-backup-test.zip")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Изолированный прогон видит свою папку и называет путь новой копии по правилу v1.</summary>
    [Fact]
    public void Разрешённый_прогон_видит_копии_и_называет_путь_новой()
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "Копии");
            WriteArchive(folder, "dsh2-backup-2026-09-26-013500-full.zip", Manifest());

            var settings = new PanelSettings { BackupFolder = folder };
            Func<DshEngine?> noEngine = () => null;

            var controller = new BackupController(
                AppPaths.Under(root),
                () => settings,
                allowed: true,
                locateEngine: noEngine,
                clock: () => new DateTimeOffset(2026, 9, 26, 1, 35, 0, TimeSpan.Zero));

            controller.Refresh();

            Assert.True(controller.Writable);
            Assert.Single(controller.Entries);
            Assert.Equal(string.Empty, controller.ListProblem);
            Assert.False(controller.FolderFromDefault);
            Assert.Equal(folder, controller.Folder);

            var next = controller.NextArchivePath();
            Assert.Equal(folder, Path.GetDirectoryName(next));
            Assert.Equal("dsh2-backup-2026-09-26-013500-full.zip", Path.GetFileName(next));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
