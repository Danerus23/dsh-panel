using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using DshPanel.Isolation;
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
        Assert.Contains("сервера DSH нет", note, StringComparison.Ordinal);
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
        Assert.DoesNotContain("сервера DSH нет", same, StringComparison.Ordinal);

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
    /// Ключ, которого панель не знает ВООБЩЕ (опечатка ИЛИ значение от более новой версии панели),
    /// обязан быть назван: «настройки прочитаны» при молча потерянном значении — это и есть дефект.
    ///
    /// ⚠️ **И назван он ЖУРНАЛУ, а не экрану** (уточнение владельца 11.10.2026 по живому прогону):
    /// прежняя панель 2.0.3, прочитав файл новой сборки, показывала в окне простыню из тридцати
    /// шести имён, и человек по ней ничего не мог сделать. Теперь на экране ОДНА строка
    /// (<see cref="PanelStrings.SettingsNewerVersionNote"/>), а перечень имён читает тот, кто
    /// разбирается в записях (<see cref="SettingsStore.NewerKeys"/> → <c>SettingsNewerVersionLogFormat</c>).
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

            var store = new SettingsStore(path);
            var load = store.Load();

            // Файл разобран — и это правда: остальные ключи применены.
            Assert.True(load.Ok);
            Assert.Equal(PanelSettings.ThemeDark, load.Settings.Theme);

            // Значение потеряно — и об этом СКАЗАНО, а не умолчано.
            Assert.Equal(string.Empty, load.Settings.ServerWorkingDir);

            // НА ЭКРАНЕ — СЛОВА О СЛУЧИВШЕМСЯ И НИ ОДНОГО ИМЕНИ: «одна строка вместо простыни».
            Assert.Contains(PanelStrings.SettingsNewerVersionNote, load.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain("serverWrokingDir", load.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.NewLine, PanelStrings.SettingsNewerVersionNote, StringComparison.Ordinal);

            // А САМО ИМЯ — В ПЕРЕЧНЕ, КОТОРЫЙ ЧИТАЕТ ЖУРНАЛ, и в том написании, в каком оно в файле:
            // иначе человеку нечего исправлять.
            Assert.Equal(new[] { "serverWrokingDir" }, store.NewerKeys);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// **НЕЗНАКОМЫЕ КЛЮЧИ ПЕРЕЖИВАЮТ ЗАПИСЬ — ЦЕНОЮ ОТКАТА ЖИВОГО ПРОГОНА.**
    ///
    /// Дефект, ради которого проверка заведена (замер владельца 11.10.2026): прежняя панель 2.0.3
    /// прочитала файл новой сборки и **перезаписала его без незнакомых ключей** — вернувшись на
    /// прежнюю версию, человек молча терял настройки новой (пропали <c>phoneEnabled</c>,
    /// <c>phonePort</c>, <c>updateAttemptAt</c> и ещё семь). Поэтому запись обязана вернуть в файл
    /// всё, чего панель не понимает, — КАК ЕСТЬ: их значения не применены, но и не потеряны.
    ///
    /// Имена здесь нарочно выдуманные: то, что панель знает сегодня, незнакомым не считается,
    /// а «значение от более новой версии» — это ровно ключ, которого в ЭТОЙ версии ещё нет.
    /// </summary>
    [Fact]
    public void Незнакомые_ключи_переживают_запись()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");

            File.WriteAllText(
                path,
                "{\"theme\":\"dark\",\"serverWrokingDir\":\"C:\\\\Работа\",\"phoneSmsCode\":\"123456\","
                + "\"panel3TrayMode\":\"quiet\",\"panel3Limits\":{\"depth\":[1,2,3],\"on\":true}}",
                new UTF8Encoding(false));

            var store = new SettingsStore(path);
            var load = store.Load();

            Assert.True(load.Ok);
            Assert.Equal(PanelSettings.ThemeDark, load.Settings.Theme);

            // (1) НЕЗНАКОМОЕ НАЗВАНО — и ровно оно, а не «всё подряд».
            Assert.Equal(
                new[] { "serverWrokingDir", "phoneSmsCode", "panel3TrayMode", "panel3Limits" },
                store.NewerKeys);

            // (2) ЗАПИСЬ — ТЕМ ЖЕ путём, что и кнопка «Сохранить».
            Assert.True(store.Save(load.Settings));

            using var written = JsonDocument.Parse(File.ReadAllText(path));
            var root = written.RootElement;

            // (3) ЧУЖИЕ ЗНАЧЕНИЯ НА МЕСТЕ И ТЕ ЖЕ: строка, число-строка, вложенный объект с массивом.
            Assert.Equal(@"C:\Работа", root.GetProperty("serverWrokingDir").GetString());
            Assert.Equal("123456", root.GetProperty("phoneSmsCode").GetString());
            Assert.Equal("quiet", root.GetProperty("panel3TrayMode").GetString());
            Assert.Equal(3, root.GetProperty("panel3Limits").GetProperty("depth").GetArrayLength());
            Assert.True(root.GetProperty("panel3Limits").GetProperty("on").GetBoolean());

            // (4) И СВОИ НАСТРОЙКИ ТОЖЕ ЗАПИСАНЫ: файл — не «только чужое».
            Assert.Equal(PanelSettings.ThemeDark, root.GetProperty("theme").GetString());

            // (5) КРУГ ЗАМКНУЛСЯ: перечитанный файл снова читается панелью, и замечание — прежнее.
            var again = new SettingsStore(path);
            var reloaded = again.Load();

            Assert.True(reloaded.Ok);
            Assert.Equal(PanelSettings.ThemeDark, reloaded.Settings.Theme);
            Assert.Contains(PanelStrings.SettingsNewerVersionNote, reloaded.Problem, StringComparison.Ordinal);

            // (6) И ВТОРАЯ ЗАПИСЬ ДАЁТ ТО ЖЕ САМОЕ: файл не «плывёт» от чтения-записи (иначе панель
            //     переписывала бы его при каждом запуске, а разница в копии пугала бы человека).
            Assert.Equal(4, again.NewerKeys.Count);
            Assert.True(again.Save(reloaded.Settings));

            var once = File.ReadAllText(path);

            var third = new SettingsStore(path);
            var thirdLoad = third.Load();

            Assert.True(third.Save(thirdLoad.Settings));

            Assert.Equal(once, File.ReadAllText(path));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПЕРЕЧЕНЬ НЕПРИНЯТЫХ ИМЁН УХОДИТ В ЖУРНАЛ — живым путём контроллера, а не «где-то вызывается».
    ///
    /// Проверяются обе стороны одного решения владельца 11.10.2026: на экране (это
    /// <see cref="ISettingsControl.LoadProblem"/>, его и показывает окно) — одна строка без имён,
    /// а имена — в журнале, той же строкой словаря. Мутации, которые обязаны упасть здесь:
    /// «вернуть простыню в окно» · «не сказать о непринятых ключах вовсе» · «перечень в журнал
    /// не уходит».
    /// </summary>
    [Fact]
    public void Перечень_непринятых_ключей_уходит_в_журнал()
    {
        var dir = TempDir();
        try
        {
            var paths = AppPaths.Under(dir);
            Directory.CreateDirectory(paths.DataDir);

            File.WriteAllText(
                paths.SettingsFile,
                "{\"theme\":\"dark\",\"phoneSmsCode\":\"123456\",\"panel3TrayMode\":\"quiet\"}",
                new UTF8Encoding(false));

            var log = new List<string>();

            var control = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: log.Add);

            // (1) ЭКРАН: слова о случившемся есть, имён нет.
            Assert.Contains(PanelStrings.SettingsNewerVersionNote, control.LoadProblem, StringComparison.Ordinal);
            Assert.DoesNotContain("phoneSmsCode", control.LoadProblem, StringComparison.Ordinal);
            Assert.DoesNotContain("panel3TrayMode", control.LoadProblem, StringComparison.Ordinal);

            // (2) ЖУРНАЛ: оба имени, через запятую, той же строкой словаря.
            var head = PanelStrings.SettingsNewerVersionLogFormat.Split("{0}")[0];

            Assert.Contains(
                log,
                line => line.StartsWith(head, StringComparison.Ordinal)
                    && line.Contains("phoneSmsCode", StringComparison.Ordinal)
                    && line.Contains("panel3TrayMode", StringComparison.Ordinal)
                    && line.Contains(", ", StringComparison.Ordinal));
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
