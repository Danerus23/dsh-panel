using System;
using System.Linq;
using DshPanel.Backup;
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
}
