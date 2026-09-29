using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DshPanel.Restore;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДЕФЕКТ Д3: строка про версию движка (находка В5) лгала на машине без движка, и
/// ДЕФЕКТ Д7: настройка в другом регистре молча игнорировалась.
///
/// Оба — один класс: причина есть, а человеку её не говорят. У Д3 «той же версии, что стоит
/// сейчас» произносилось там, где сравнивать было не с чем (движка нет вовсе): это не умолчание,
/// а прямая ложь, и подтверждена она живым прогоном наката на чистой машине. У Д7 человек,
/// написавший в <c>settings.json</c> ключ с большой буквы, получал «настройки прочитаны»
/// и потерянное значение (на этом уже споткнулся живой прогон в лаборатории).
/// </summary>
public class SettingsTruthTests
{
    // --- Д3 ----------------------------------------------------------------

    /// <summary>
    /// Три ответа, и третий — новый: версия копии названа, нынешняя НЕИЗВЕСТНА, потому что движка
    /// на машине нет. Проверка идёт через само состояние дефекта: пустая нынешняя версия и
    /// <c>null</c> — ровно то, что приходит с чистой машины.
    /// </summary>
    [Theory]
    [InlineData("0.1.5-rc.3", null)]
    [InlineData("0.1.5-rc.3", "")]
    [InlineData("0.1.5-rc.3", "   ")]
    public void Версия_копии_без_движка_не_выдаётся_за_ту_же(string copy, string? installed)
    {
        var note = RestoreEngine.VersionNote(copy, installed);

        Assert.Contains(copy, note, StringComparison.Ordinal);
        Assert.Contains("движка нет", note, StringComparison.Ordinal);
        Assert.Contains("неизвестна", note, StringComparison.Ordinal);

        // Ложь, из-за которой дефект и заведён: «той же версии, что стоит сейчас» — нельзя.
        Assert.DoesNotContain("той же версии", note, StringComparison.Ordinal);
    }

    /// <summary>Совпадение версий по-прежнему называется совпадением, а расхождение — расхождением.</summary>
    [Fact]
    public void Совпадение_и_расхождение_версий_называются_как_раньше()
    {
        var same = RestoreEngine.VersionNote("0.1.5-rc.3", "0.1.5-RC.3");
        Assert.Contains("той же версии", same, StringComparison.Ordinal);
        Assert.DoesNotContain("движка нет", same, StringComparison.Ordinal);

        var differs = RestoreEngine.VersionNote("0.1.5-rc.3", "0.1.7-rc.2");
        Assert.Contains("0.1.5-rc.3", differs, StringComparison.Ordinal);
        Assert.Contains("0.1.7-rc.2", differs, StringComparison.Ordinal);
        Assert.Contains("может не открыть", differs, StringComparison.Ordinal);
    }

    /// <summary>Версии копии нет вовсе — это по-прежнему отдельный ответ, и он не про движок машины.</summary>
    [Fact]
    public void Без_версии_копии_ответ_про_опись()
    {
        Assert.Equal(PanelStrings.RestoreVersionUnknown, RestoreEngine.VersionNote(null, "0.1.5-rc.3"));
        Assert.Equal(PanelStrings.RestoreVersionUnknown, RestoreEngine.VersionNote("  ", null));
    }

    // --- Д7 ----------------------------------------------------------------

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh settings " + Guid.NewGuid().ToString("N")[..8]);

    private static void RemoveTemp(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }

    /// <summary>
    /// Ключ в ЧУЖОМ РЕГИСТРЕ принимается, а не теряется молча. Проверка идёт через само состояние
    /// дефекта: файл правлен руками, и имя ключа написано как человек его помнит.
    /// </summary>
    [Theory]
    [InlineData("ServerWorkingDir")]
    [InlineData("SERVERWORKINGDIR")]
    [InlineData("serverworkingdir")]
    [InlineData("ServerWorkingdir")]
    public void Ключ_в_чужом_регистре_принимается(string key)
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            var folder = Path.Combine(dir, "Работа");

            File.WriteAllText(path, "{\"" + key + "\":\"" + folder.Replace("\\", "\\\\") + "\"}",
                new UTF8Encoding(false));

            var load = new SettingsStore(path).Load();

            Assert.True(load.Ok, load.Problem);
            Assert.Equal(folder, load.Settings.ServerWorkingDir);

            // И замечания про непринятый ключ нет: ключ ПРИНЯТ, а не назван непринятым.
            Assert.DoesNotContain("НЕ применены", load.Problem, StringComparison.Ordinal);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Ключ, которого панель не знает ВООБЩЕ (опечатка), обязан быть назван: «настройки прочитаны»
    /// при молча потерянном значении — это и есть дефект. Названо и имя ключа, и то, что значение
    /// не применено.
    /// </summary>
    [Fact]
    public void Неизвестный_ключ_называется_а_не_молчит()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");

            File.WriteAllText(
                path,
                "{\"serverWrokingDir\":\"C:\\\\Работа\",\"theme\":\"dark\"}",
                new UTF8Encoding(false));

            var load = new SettingsStore(path).Load();

            // Файл разобран — и это правда: остальные ключи применены.
            Assert.True(load.Ok);
            Assert.Equal(PanelSettings.ThemeDark, load.Settings.Theme);

            // Значение потеряно — и об этом СКАЗАНО, а не умолчано.
            Assert.Equal(string.Empty, load.Settings.ServerWorkingDir);
            Assert.Contains("serverWrokingDir", load.Problem, StringComparison.Ordinal);
            Assert.Contains("НЕ применены", load.Problem, StringComparison.Ordinal);

            // Названо и то, какое имя панель понимает: иначе человеку нечего исправлять.
            Assert.Contains("serverWorkingDir", load.Problem, StringComparison.Ordinal);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Файл, написанный САМОЙ панелью, не даёт ни одного замечания про ключи: иначе панель
    /// жаловалась бы на свой же файл, и настоящее замечание утонуло бы в шуме.
    /// </summary>
    [Fact]
    public void Свой_файл_не_даёт_замечаний_о_ключах()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.True(store.Save(new PanelSettings
            {
                ServerWorkingDir = @"C:\Работа",
                Theme = PanelSettings.ThemeDark,
                BackupWithKeys = true,
                BackupShareable = true,
            }));

            var load = store.Load();

            Assert.True(load.Ok);
            Assert.Equal(string.Empty, load.Problem);
            Assert.Equal(@"C:\Работа", load.Settings.ServerWorkingDir);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }
}
