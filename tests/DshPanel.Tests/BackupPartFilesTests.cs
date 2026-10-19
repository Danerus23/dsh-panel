using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПОДЛОЖЕННЫЙ ВРЕМЕННЫЙ ФАЙЛ (`*.part`) — НЕ КОПИЯ: не в списке, не в счёте ротации, не в жертвах,
/// не удаляется, в выборе для наката не участвует (Н-2, задача t14).
///
/// **Зачем отдельная проверка.** Копия теперь пишется под `<конечное имя>.part` и публикуется
/// одним переименованием (t12). Значит `.part` в папке копий — законное ПРОМЕЖУТОЧНОЕ состояние,
/// и всё, что читает папку, обязано видеть в нём не копию. Свойство это было верным и до правки
/// (`BackupNaming.Kind` даёт не-`.zip` вид `Other`), поэтому проверка **зелёная ДО всякой правки** —
/// это тест-пояс, а не доказательство починки.
///
/// ⚠️ **`BackupRotation.List` — промежуточный и НЕ фильтрует:** он отдаёт все файлы папки, а вид
/// решает `Plan` (`Kind == Copy`). Так и задумано; дублирующий фильтр в `List` не добавлялся
/// (двойной фильтр выдал бы несуществующую правку за дефект).
/// </summary>
public class BackupPartFilesTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-part " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Настоящая копия панели: zip с описью внутри — так её и пишет снятие.</summary>
    private static string WriteCopy(string folder, string name)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);

        var manifest = new BackupManifest
        {
            CreatedAt = "2026-09-26 12:00:00",
            WithEngine = true,
            WithSessions = true,
            WithCredentials = false,
            TotalFiles = 3,
            TotalBytes = 512,
        };

        using var zip = new ZipArchive(File.Open(path, FileMode.Create), ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));

        return path;
    }

    /// <summary>Подложенный промежуточный файл: имена, которые даёт наше же снятие.</summary>
    private static string WritePart(string folder, string name, string text = "половина архива")
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static BackupController Controller(AppPaths paths, SettingsController settings) =>
        new(
            paths,
            () => settings.Settings,
            allowed: true,
            mayTakeMachineEngineData: false,
            locateEngine: () => null,
            clock: () => new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3)),
            log: _ => { },
            settingsOwner: settings);

    private static SettingsController Settings(AppPaths paths) =>
        new(
            new SettingsStore(paths.SettingsFile),
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: () => null,
            applyTheme: _ => { },
            log: _ => { });

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Уборка подставного корня не имеет права уронить проверку.
        }
    }

    /// <summary>
    /// ПОДЛОЖЕННЫЙ `.part` НЕ ВИДЕН НИГДЕ, А САМ ОСТАЁТСЯ НА МЕСТЕ — даже когда ротация удаляет
    /// свою копию по пределу хранения.
    /// </summary>
    [Fact]
    public void Подложенный_part_не_копия_и_не_удаляется()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "backups");

        try
        {
            var older = WriteCopy(folder, "dsh2-backup-2026-09-26-100000-full.zip");
            var newest = WriteCopy(folder, "dsh2-backup-2026-09-26-110000-full.zip");

            // (1) ОТЧЁТ О ПАПКЕ БЕЗ ПОДЛОЖЕННЫХ: предел 1 при двух копиях — одна жертва,
            //     и причина называет ДВЕ копии.
            var clean = BackupRotation.Plan(BackupRotation.List(folder), keepCount: 1, newestName: Path.GetFileName(newest));

            Assert.Single(clean.Delete);
            Assert.Equal(older, clean.Delete[0].Path);

            // (2) ПОДКЛАДЫВАЕМ промежуточные файлы — оба вида имён, какие даёт наше снятие.
            var partZip = WritePart(folder, "dsh2-backup-2026-09-26-110000-full.zip.part");
            var partBare = WritePart(folder, "dsh2-backup-2026-09-26-110000-full.part");

            // Список готовых копий: только настоящие копии, промежуточных нет.
            var list = BackupFolder.List(folder);

            Assert.Equal(string.Empty, list.Problem);
            Assert.Equal(2, list.Entries.Count);
            Assert.DoesNotContain(list.Entries, entry => entry.Name.Contains(".part", StringComparison.OrdinalIgnoreCase));

            // `BackupRotation.List` — ПРОМЕЖУТОЧНЫЙ: отдаёт ВСЕ файлы папки (4), вид решает `Plan`.
            var all = BackupRotation.List(folder);
            Assert.Equal(4, all.Count);

            // План и отчёт: подложенные не в счёте копий и не в жертвах — причина ТА ЖЕ, что без них.
            var plan = BackupRotation.Plan(all, keepCount: 1, newestName: Path.GetFileName(newest));

            Assert.Equal(clean.Reason, plan.Reason);
            Assert.DoesNotContain(plan.Reason, ".part", StringComparison.OrdinalIgnoreCase);
            Assert.Single(plan.Delete);
            Assert.Equal(older, plan.Delete[0].Path);
            Assert.DoesNotContain(plan.Delete, victim => victim.Path.Contains(".part", StringComparison.OrdinalIgnoreCase));

            // (3) ВЫБОР ДЛЯ НАКАТА идёт по записям списка: в них промежуточных нет.
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.DshHome);
            Directory.CreateDirectory(paths.Root);

            var settings = Settings(paths);
            var seeded = SettingsStore.Clean(settings.Settings);
            seeded.BackupFolder = paths.BackupsDir;
            Assert.True(settings.Save(seeded));

            var backups = Controller(paths, settings);
            backups.Refresh();

            Assert.Equal(2, backups.Entries.Count);
            Assert.DoesNotContain(backups.Entries, entry => entry.Name.Contains(".part", StringComparison.OrdinalIgnoreCase));
            // Уборка: жертва удалена, подложенные — НА МЕСТЕ (чужие для неё имена не трогаются).
            var removal = BackupRotation.Apply(plan);

            Assert.Equal(1, removal.Deleted);
            Assert.False(File.Exists(older), "жертва предела хранения не удалена");
            Assert.True(File.Exists(partZip), "подложенный `.zip.part` удалён — его трогать нельзя");
            Assert.True(File.Exists(partBare), "подложенный `.part` удалён — его трогать нельзя");


        }
        finally
        {
            Cleanup(root);
        }
    }
}
