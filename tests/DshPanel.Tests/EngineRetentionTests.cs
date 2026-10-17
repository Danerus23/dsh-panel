using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТКАТ, УБОРКА ПРЕЖНЕЙ ВЕРСИИ И ОПИСЬ ПОСТАВЛЕННОГО (этап E3.1, шаг 2 плана владельца).
///
/// Решения владельца 10.10.2026: *«держим текущую и прежнюю версию (≈213 МБ каждая), освобождение
/// места — словами»*; приёмка уборки — *«папка версии исчезла И общий кэш npm цел — установка могла
/// положить в дерево ссылки, а TreeDeletion снимает ссылку, не заходя внутрь»*; опись пополняется
/// **движком, версией и Node**, а системный Node в опись не попадает никогда — он не наш.
/// </summary>
public class EngineRetentionTests
{
    /// <summary>Держим ТЕКУЩУЮ и ПРЕЖНЮЮ, остальное — на уборку. Повторы и пустые имена не мешают.</summary>
    [Fact]
    public void Держим_текущую_и_прежнюю()
    {
        var remove = EngineRetention.ToRemove("1.3.0", "1.2.0", new[] { "1.1.0", "1.2.0", "1.3.0", "1.1.0", "  ", "" });

        Assert.Equal(new[] { "1.1.0" }, remove);
        Assert.Empty(EngineRetention.ToRemove("1.3.0", "1.2.0", new[] { "1.3.0", "1.2.0" }));
        Assert.Empty(EngineRetention.ToRemove(null, null, Array.Empty<string>()));

        // Прежней нет — держим только текущую: убирать «всё, кроме текущей» тоже верно.
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, EngineRetention.ToRemove("1.3.0", null, new[] { "1.1.0", "1.2.0", "1.3.0" }));
    }

    /// <summary>
    /// УБИРАЕТСЯ РОВНО СВОЁ: папка версии исчезает, а <b>общий кэш npm цел</b>. Установка кладёт
    /// в дерево ссылки (junction), и удаление «как обычно» снесло бы либо ссылку, либо сам кэш —
    /// поэтому уборка идёт помощником <c>TreeDeletion</c>, который снимает ссылку, не заходя внутрь.
    /// </summary>
    [Fact]
    public void Версия_исчезает_а_кэш_npm_цел()
    {
        var root = NewFolder();
        var cache = Path.Combine(root, "npm-cache");
        var engineDir = Path.Combine(root, "engine");

        try
        {
            var old = Path.Combine(engineDir, "1.1.0");
            var current = Path.Combine(engineDir, "1.3.0");
            var unknown = Path.Combine(engineDir, "чужой-движок");

            Directory.CreateDirectory(old);
            Directory.CreateDirectory(current);
            Directory.CreateDirectory(unknown);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "вложение.tgz"), "кэш");

            // Ссылка внутрь кэша — ровно то, что оставляет npm в поставленном дереве.
            var link = Path.Combine(old, "node_modules-cache");
            Assert.True(MakeJunction(link, cache), "junction не завелась — проба проверяла бы не то");

            var removed = EngineRetention.Remove(
                engineDir, EngineRetention.ToRemove("1.3.0", "1.2.0", new[] { "1.1.0", "1.3.0" }));

            Assert.Equal(new[] { "1.1.0" }, removed);
            Assert.False(Directory.Exists(old), "папка прежней версии осталась");
            Assert.True(Directory.Exists(current), "текущая версия убрана — откат сломан");

            // ЦЕЛ ли кэш: файл на месте и содержимое не тронуто.
            Assert.True(File.Exists(Path.Combine(cache, "вложение.tgz")), "уборка снесла общий кэш npm");
            Assert.Equal("кэш", File.ReadAllText(Path.Combine(cache, "вложение.tgz")));

            // А папка, которую панель не ставила, не тронута вовсе.
            Assert.True(Directory.Exists(unknown), "уборка тронула папку, которой панель не заводила");
        }
        finally
        {
            Files.TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// ЗАПИСЬ «ЧТО СТОИТ СЕЙЧАС» ХОДИТ ПО КРУГУ: пишется, читается, переживает перезапись,
    /// а битый файл и файл чужой формы дают «панель ничего не ставила» — не догадку.
    /// </summary>
    [Fact]
    public void Запись_что_стоит_сейчас_читается_и_не_угадывается()
    {
        var root = NewFolder();
        var path = Path.Combine(root, "state", "engine.json");

        try
        {
            Assert.False(EngineInstallRecord.Read(path).Any);

            var node = new EngineNodeResult(EngineNodeOutcome.Ready, @"C:\панель\node\22.11.0\node.exe", "v22.11.0",
                string.Empty, string.Empty, Ours: true);

            var record = EngineInstallRecord.After("0.1.5-rc.2", @"C:\панель\engine\0.1.5-rc.2", node);

            Assert.True(record.Write(path, out var why), why);

            var read = EngineInstallRecord.Read(path);

            Assert.True(read.Any);
            Assert.Equal("0.1.5-rc.2", read.EngineVersion);
            Assert.Equal(@"C:\панель\engine\0.1.5-rc.2", read.EnginePath);
            Assert.Equal("v22.11.0", read.NodeVersion);
            Assert.True(read.NodeOurs, "свой Node перестал быть своим — в опись он не попадёт");

            // Чужой Node (системный) записью не становится нашим.
            var system = EngineInstallRecord.After("0.1.5-rc.2", @"C:\панель\engine\0.1.5-rc.2",
                new EngineNodeResult(EngineNodeOutcome.SystemSuits, @"C:\Program Files\nodejs\node.exe", "v26.9.0",
                    string.Empty, string.Empty, Ours: false));

            Assert.False(system.NodeOurs);

            // Битый файл и чужая форма — «панель ничего не ставила».
            File.WriteAllText(path, "{ это не json");
            Assert.False(EngineInstallRecord.Read(path).Any);

            File.WriteAllText(path, "[]");
            Assert.False(EngineInstallRecord.Read(path).Any);

            File.WriteAllText(path, "{\"engine\":{\"path\":\"C:\\\\где-то\"}}");
            Assert.False(EngineInstallRecord.Read(path).Any);
        }
        finally
        {
            Files.TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// ОПИСЬ НАЗЫВАЕТ ПОСТАВЛЕННОЕ ДВИЖКОМ: движок с версией и **свой** Node с версией; системный
    /// Node не появляется НИКОГДА (он не наш и удалять его панель не вправе).
    /// </summary>
    [Fact]
    public void Опись_называет_движок_и_свой_Node_а_системный_нет()
    {
        var paths = AppPaths.Under(Path.Combine(Path.GetTempPath(), "dsh-inv-" + Guid.NewGuid().ToString("N")[..8]));

        try
        {
            var ours = new EngineInstallRecord("0.1.5-rc.2", @"C:\панель\engine\0.1.5-rc.2",
                "v22.11.0", @"C:\панель\node\22.11.0\node.exe", NodeOurs: true);

            var entries = InstalledInventory.Collect(paths, panelExe: null, autostart: false, shortcuts: null, ours);

            var engine = Assert.Single(entries, entry => entry.Kind == InstalledInventory.KindEngine);
            Assert.Equal("0.1.5-rc.2", engine.Version);
            Assert.Equal(@"C:\панель\engine\0.1.5-rc.2", engine.Path);

            var node = Assert.Single(entries, entry => entry.Kind == InstalledInventory.KindNode);
            Assert.Equal("v22.11.0", node.Version);

            // Системный Node — не наш: в описи его быть не должно, и версия движка при этом остаётся.
            var system = new EngineInstallRecord("0.1.5-rc.2", @"C:\панель\engine\0.1.5-rc.2",
                "v26.9.0", @"C:\Program Files\nodejs\node.exe", NodeOurs: false);

            var without = InstalledInventory.Collect(paths, panelExe: null, autostart: false, shortcuts: null, system);

            Assert.DoesNotContain(without, entry => entry.Kind == InstalledInventory.KindNode);
            Assert.Single(without, entry => entry.Kind == InstalledInventory.KindEngine);

            // Записи нет — опись молчит про движок, а не выдумывает версию по именам папок.
            var none = InstalledInventory.Collect(paths, panelExe: null, autostart: false, shortcuts: null);

            Assert.DoesNotContain(none, entry => entry.Kind == InstalledInventory.KindEngine);

            // И текст описи несёт версию: файл читает человек и деинсталлятор.
            var json = InstalledInventory.ToJson(entries);

            Assert.Contains("0.1.5-rc.2", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);

            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        }
        finally
        {
            Files.TreeDeletion.Remove(paths.Root);
        }
    }

    /// <summary>
    /// </summary>
    /// <remarks>
    /// Ссылка заводится junction-ом через `mklink /J`: он не требует прав администратора
    /// (в отличие от символьной ссылки) и работает на обычной машине. Возвращает признак успеха —
    /// проба обязана УПАСТЬ, если ссылку завести не удалось, а не пройти мимо.
    /// </remarks>
    private static bool MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null) return false;

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();

        return process.WaitForExit(20000) && process.ExitCode == 0 && Directory.Exists(link);
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }
}
