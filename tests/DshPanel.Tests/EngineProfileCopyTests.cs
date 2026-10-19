using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРЕДОХРАНИТЕЛЬНАЯ КОПИЯ ПРОФИЛЯ ПЕРЕД ПОДМЕНОЙ ВЕРСИИ ДВИЖКА (пункт 4 плана E3.2).
///
/// Два обещания и оба — из замеров: **имя копии ротация не трогает по ПРАВИЛУ** (а не «потому что
/// не узнала»), и **копия либо целая, либо обновление не начинается**. Само действие (упаковка)
/// делает существующая дверь 7-Zip у того, кто зовёт; здесь проверяется решение о копии.
/// </summary>
public class EngineProfileCopyTests
{
    /// <summary>
    /// ⚠️ ГЛАВНОЕ ПРО ИМЯ: копия перед движком — **предохранительная**, и потому её не удаляет
    /// уборка. Это свойство вида, а не совпадение: проверяем и вид, и что уборка её не называет
    /// среди удаляемых.
    /// </summary>
    [Fact]
    public void Копия_перед_движком_не_убирается_ротацией()
    {
        var name = EngineProfileCopy.Name(new DateTimeOffset(2026, 10, 10, 22, 44, 29, TimeSpan.Zero));

        Assert.Equal("dsh2-before-engine-2026-10-10-224429.zip", name);
        Assert.StartsWith(EngineProfileCopy.Prefix, name, StringComparison.Ordinal);

        // Вид — предохранительная копия, а не «чужой файл».
        Assert.Equal(BackupFileKind.Safety, EngineProfileCopy.Kind(name));

        // И правило уборки её не берёт: среди удаляемых её нет, даже когда обычных копий сверх меры.
        var installed = new[] { name, "dsh2-backup-2026-10-01-000000-full.zip", "dsh2-backup-2026-10-02-000000-full.zip" };
        var victims = installed.Where(file => BackupNaming.Kind(file) == BackupFileKind.Copy).ToList();

        Assert.DoesNotContain(name, victims);
        Assert.Equal(2, victims.Count);
    }

    /// <summary>
    /// НЕЧЕГО ИЛИ НЕКУДА СНИМАТЬ — ОТКАЗ ДО ДЕЛА, и он назван: дома движка нет, папки копий нет.
    /// </summary>
    [Fact]
    public void Без_профиля_и_без_места_копию_не_планируем()
    {
        var noProfile = EngineProfileCopy.Decide(null, @"C:\копии", DateTimeOffset.Now);

        Assert.False(noProfile.Any);
        Assert.Equal(EngineProfileCopyRefusal.NoProfile, noProfile.Refusal);
        Assert.Equal(EngineProfileCopy.Words.NoProfile, noProfile.RefusalKey);

        var noPlace = EngineProfileCopy.Decide(@"C:\профиль", null, DateTimeOffset.Now);

        Assert.False(noPlace.Any);
        Assert.Equal(EngineProfileCopyRefusal.NoPlace, noPlace.Refusal);
        Assert.Equal(EngineProfileCopy.Words.NoPlace, noPlace.RefusalKey);

        // А когда есть и то и другое — копия планируется, и путь назван целиком.
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var planned = EngineProfileCopy.Decide(@"C:\профиль", @"C:\копии", now);

        Assert.True(planned.Any);
        Assert.Equal(@"C:\профиль", planned.Source);
        Assert.Equal(System.IO.Path.Combine(@"C:\копии", EngineProfileCopy.Name(now)), planned.Target);
        Assert.Equal(@"C:\копии", planned.Folder);
    }

    /// <summary>
    /// КОПИЯ НЕ СНЯЛАСЬ — ОБНОВЛЕНИЕ НЕ НАЧИНАЕТСЯ, и человеку говорят про ВОЗВРАТ, а не про код
    /// ошибки: «без копии вернуться к прежней работе не получится».
    /// </summary>
    [Fact]
    public void Неудавшаяся_копия_значит_отказ_обновления()
    {
        var failed = EngineProfileCopy.Failed();

        Assert.False(failed.Any);
        Assert.Equal(EngineUpdateFlow.Words.CopyNotWhole, failed.RefusalKey);
        Assert.Equal(EngineProfileCopyRefusal.NoPlace, failed.Refusal);

        // И у плана обновления этот отказ уже есть — те же слова, одно место.
        Assert.False(EngineUpdateFlow.CopyFailed().Any);
        Assert.Equal(EngineUpdateFlow.CopyFailed().RefusalKey, failed.RefusalKey);

        // Ключи копии объявлены и переведены на три языка.
        var members = typeof(DshPanel.Shell.PanelStrings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in new[] { EngineProfileCopy.Words.NoProfile, EngineProfileCopy.Words.NoPlace })
        {
            Assert.True(members.Contains(key), $"ключа «{key}» нет среди членов PanelStrings");

            foreach (var language in new[] { "ru", "en", "zh" })
            {
                Assert.NotEqual(key, DshPanel.Localization.Loc.TIn(language, key));
            }
        }
    }

    /// <summary>
    /// КОПИЯ ПРОФИЛЯ ПОЯВЛЯЕТСЯ ТОЛЬКО ГОТОВОЙ (Н-2, t14): во время упаковки файла с КОНЕЧНЫМ именем
    /// в папке копий нет, после — есть, и ни одного `.part` не остаётся.
    ///
    /// **От какого случая.** Архив обновления писался ПРЯМО в `plan.Target`: в папке копий человек
    /// видел недоделанный архив под именем готовой копии.
    ///
    /// **Почему наблюдатель СБЫТИЯМИ, а не опросом папки.** Опрос успевает заглянуть уже ПОСЛЕ
    /// публикации и видит конечное имя, которого во время работы не было, — первая редакция этой
    /// проверки на этом и покраснела на исправном коде. У событий ОС есть различающий признак:
    /// с правкой конечное имя приходит `Renamed` (переименованием), а без правки — `Created`.
    /// </summary>
    [Fact]
    public async Task Копия_профиля_появляется_только_готовой()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-profilecopy " + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.DshHome);
            Directory.CreateDirectory(paths.BackupsDir);
            File.WriteAllText(Path.Combine(paths.DshHome, "settings.yaml"), "agent: test" + Environment.NewLine);
            File.WriteAllText(Path.Combine(paths.DshHome, "sessions.jsonl"), "{}" + Environment.NewLine);

            var token = TestContext.Current.CancellationToken;

            var gate = new object();
            var created = new List<string>();
            var renamed = new List<string>();

            using var watcher = new FileSystemWatcher(paths.BackupsDir, "dsh2-before-engine-*")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
            };

            watcher.Created += (_, e) => { lock (gate) created.Add(e.Name ?? string.Empty); };
            watcher.Renamed += (_, e) => { lock (gate) renamed.Add(e.Name ?? string.Empty); };
            watcher.EnableRaisingEvents = true;

            var done = false;

            await Task.Run(() => done = EngineUpdateRealDoors.Copy(paths, "2.1.0", _ => { }), token);

            // Ждём СИГНАЛ (событие о конечном имени), а не «спим на глаз»: уведомления приходят пачкой.
            var until = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < until)
            {
                lock (gate)
                {
                    if (renamed.Any(name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) break;
                }

                await Task.Delay(10, token);
            }

            watcher.EnableRaisingEvents = false;

            Assert.True(done, "копия профиля не снята");

            string[] createdNames;
            string[] renamedNames;

            lock (gate)
            {
                createdNames = created.ToArray();
                renamedNames = renamed.ToArray();
            }

            // Конечное имя НЕ появляется созданием — только одним переименованием.
            Assert.DoesNotContain(
                createdNames,
                name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(renamedNames, name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            Assert.Single(Directory.GetFiles(paths.BackupsDir, "dsh2-before-engine-*.zip"));
            Assert.Empty(Directory.GetFiles(paths.BackupsDir, "*.part"));
        }
        finally
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
    }
}