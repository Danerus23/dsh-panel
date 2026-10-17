using System.Text;
using System.Text.Json;
using DshPanel.Files;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>
/// Проба настроек — то, что нельзя проверить одними тестами без экрана: настоящий файл на диске,
/// выбор пути по признаку изоляции и то, что панель НЕ читает чужое окружение, когда не имеет права.
///
/// Проверяется восемь вещей, и каждая строка отчёта ВЛИЯЕТ на исход:
///
/// 1. файл настроек лежит под корнем прогона (у изолированного прогона нет пути к чужому);
/// 2. право читать и писать есть у изолированного прогона и у запуска человеком, и нет
///    у прогона проверки;
/// 3. запись → чтение возвращает то же самое, файл — **без BOM**;
/// 4. мусор в файле даёт умолчания и честный признак, а не падение;
/// 5. неизвестная тема приводится к «как в Windows»;
/// 6. рабочая папка: пустая настройка = папка панели, заданная = настройка, и смена
///    **применяется** (тема — сразу, папка — при следующем запуске сервера);
/// 7. предложение рабочей папки берётся у прежней панели, и **только** когда читать её можно;
/// 8. путь человека в показе маскируется — иначе он уехал бы в кадр и в журнал.
///
/// За собой проба прибирает: временный каталог удаляется вместе с пустым родительским.
/// </summary>
public static class SettingsSelfTest
{
    public static int Run()
    {
        var report = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        var parent = Path.Combine(Path.GetTempPath(), "dsh-panel-settings-selftest");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));

        try
        {
            var isolated = RunContext.Create(
                RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, _ => null));
            var paths = isolated.Paths;

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var store = new SettingsStore(paths.SettingsFile);
            report.Add($"файл настроек = {store.Describe}");

            Check(
                "файл настроек лежит под корнем прогона",
                store.Describe.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            // --- 2. право ---------------------------------------------------------

            var owner = RunContext.Create(RunRequest.Owner());

            Check("в изоляции настройки доступны", SettingsController.For(isolated, humanLaunch: false));
            Check("в прогоне проверки настройки недоступны", !SettingsController.For(owner, humanLaunch: false));
            Check("у обычного запуска человеком доступны", SettingsController.For(owner, humanLaunch: true));

            // --- 3. запись и чтение ----------------------------------------------

            Check("файла нет — это умолчания, а не ошибка", store.Load().Ok && store.Load().Settings.ServerWorkingDir.Length == 0);

            var folder = Path.Combine(root, "Моя рабочая папка");
            Check("сохранение прошло", store.Save(new PanelSettings
            {
                ServerWorkingDir = folder + Path.DirectorySeparatorChar,
                Theme = PanelSettings.ThemeDark,
            }));

            var back = store.Load();
            Check("прочиталась та же тема", back.Settings.Theme == PanelSettings.ThemeDark);
            Check("хвостовой разделитель срезан", back.Settings.ServerWorkingDir == folder);

            var bytes = File.ReadAllBytes(store.Describe);
            Check(
                "файл записан без BOM",
                bytes.Length >= 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
            Check("временного файла не осталось", !File.Exists(store.Describe + ".tmp"));

            // --- 4. мусор и неизвестная тема -------------------------------------

            File.WriteAllText(store.Describe, "{ это не json", new UTF8Encoding(false));
            var broken = store.Load();
            Check("мусор — умолчания и честный признак", !broken.Ok && broken.Problem.Length > 0);
            Check("в мусоре тема — умолчание", broken.Settings.Theme == PanelSettings.ThemeSystem);

            File.WriteAllText(
                store.Describe,
                "{\"theme\":\"неон\",\"serverWorkingDir\":\"   \"}",
                new UTF8Encoding(false));

            var weird = store.Load();
            Check("неизвестная тема приводится к «как в Windows»", weird.Settings.Theme == PanelSettings.ThemeSystem);

            // --- 5. контроллер и рабочая папка -----------------------------------

            var applied = new List<PanelTheme>();
            var controller = new SettingsController(
                store, paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: applied.Add,
                log: _ => { });

            Check("тема применилась сразу при чтении", applied.Count == 1 && applied[0] == PanelTheme.System);
            Check("пустая настройка = папка панели", controller.WorkDirInUse == paths.DataDir);

            Check("сохранение через контроллер прошло", controller.Save(new PanelSettings
            {
                ServerWorkingDir = paths.BackupsDir,
                Theme = PanelSettings.ThemeLight,
            }));

            Check("рабочая папка взялась из настроек", controller.WorkDirInUse == paths.BackupsDir);
            Check("тема применилась при сохранении", applied.Count == 2 && applied[1] == PanelTheme.Light);
            Check("сохранённое пережило новое чтение", store.Load().Settings.ServerWorkingDir == paths.BackupsDir);

            // --- 6. прогон проверки: только умолчания ----------------------------

            var locked = new SettingsController(
                store, paths, allowed: false, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            Check("прогон проверки видит умолчания, а не настройки", locked.Settings.ServerWorkingDir.Length == 0);
            Check("прогон проверки не сохраняет", !locked.Save(new PanelSettings { Theme = PanelSettings.ThemeDark }));

            // --- 7. предложение рабочей папки ------------------------------------

            Directory.CreateDirectory(paths.DataDir);
            var previous = Path.Combine(paths.DataDir, "v1-settings.json");
            File.WriteAllText(
                previous,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["serverWorkingDir"] = paths.BackupsDir }),
                new UTF8Encoding(false));

            var suggested = WorkDirSuggestions.Find(mayReadOwnerEnvironment: true, previous, _ => true);
            Check("предложение найдено, когда читать можно", suggested.Count == 1 && suggested[0].Path == paths.BackupsDir);
            Check("чужое окружение не читается без права", WorkDirSuggestions.Find(false, previous, _ => true).Count == 0);
            Check("несуществующий каталог не предлагается", WorkDirSuggestions.Find(true, previous, _ => false).Count == 0);

            // --- 8. маскировка пути в показе --------------------------------------

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var inside = Path.Combine(profile, "Заметки");
            Check(
                "каталог пользователя маскируется",
                DisplayMask.Path(inside).StartsWith('~') && !DisplayMask.Path(inside).Contains(profile, StringComparison.OrdinalIgnoreCase));
            Check("обычный путь не портится", DisplayMask.Path(@"D:\Работа") == @"D:\Работа");
            Check(
                "профиль другого человека тоже маскируется",
                !DisplayMask.Path(@"C:\Users\Другой\папка").Contains("Другой", StringComparison.Ordinal));

            // --- 9. отчёт об окружении не выносит лишнего -------------------------

            // Отдельный контроллер с НАСТОЯЩИМ поиском движка: отчёт обязан совпадать с фактом,
            // а не с тем, что ему подсунули.
            var realReport = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => DshEngine.Locate(), server: () => null, applyTheme: _ => { }, log: _ => { });

            var text = realReport.EnvironmentText();
            report.Add("отчёт об окружении:");
            foreach (var line in text.Split(Environment.NewLine)) report.Add("    " + line);

            var located = DshEngine.Locate();

            Check("отчёт об окружении не пуст", text.Length > 0);
            Check("в отчёте нет имени пользователя", !text.Contains(profile, StringComparison.OrdinalIgnoreCase));

            // ⚠️ СВЕРКА С ФАКТОМ — ПО СЛОВУ О НАЙДЕННОМ, А НЕ ПО ПУТИ (дефект «проверка не может
            // пройти», пачка D цикла C2). Здесь стояло `text.Contains(DisplayMask.Path(located.BinPath))`:
            // от отчёта требовался МАСКИРОВАННЫЙ ПУТЬ движка, которого в отчёте об окружении НЕТ
            // и не было — строка «DSH» несёт ВЕРСИЮ, а не путь. На машине с движком проверка не
            // могла пройти никогда; на машине без движка проходила, потому что сверяла другое
            // слово. Теперь меряется то, что в отчёте есть: СЛОВО строки «DSH» («найден» /
            // «не найден») против того, что нашёл настоящий поиск.
            var engineLine = realReport.EnvironmentInfo()
                .Groups
                .SelectMany(group => group.Lines)
                .FirstOrDefault(line => string.Equals(line.What, PanelStrings.EnvEngine, StringComparison.Ordinal));

            Check("в отчёте есть строка про DSH", engineLine is not null);

            if (engineLine is not null)
            {
                var wanted = located is null ? PanelStrings.EnvNotFound : PanelStrings.EnvFound;

                Check(
                    "отчёт про движок совпадает с фактом",
                    string.Equals(engineLine.Word, wanted, StringComparison.Ordinal));
            }

            // Версии в отчёте НАЗВАНЫ (просьба владельца 28.09.2026): либо номер, либо честное
            // «версия не прочитана» — но не пустое место.
            Check(
                "версия движка названа или честно не прочитана",
                text.Contains(PanelStrings.EnvEngine, StringComparison.Ordinal));
            Check(
                "версия Node названа или честно не прочитана",
                text.Contains(PanelStrings.EnvNode, StringComparison.Ordinal));

            // И отдельно — как выглядит случай «движка нет»: панель обязана сказать это словами.
            Check(
                "без движка отчёт говорит «не найден»",
                controller.EnvironmentText().Contains(PanelStrings.EnvNotFound, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            failed = true;
            report.Add($"ОШИБКА: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            // Уборка идёт ОДНИМ местом и сама проверяет, что дерева не осталось (см.
            // `Files\TreeDeletion.cs`): прежний `Directory.Delete(recursive: true)` оставлял дерево
            // на junction и на файле «только для чтения», а пустой `catch` отвечал на это успехом.
            var gone = TreeDeletion.Remove(root).Ok;

            try { if (Directory.Exists(parent)) Directory.Delete(parent); } catch { }

            report.Add($"уборка: временный каталог пробы убран = {gone}");
            if (!gone) failed = true;
        }

        foreach (var line in report) Console.WriteLine("НАСТРОЙКИ| " + line);
        Console.WriteLine(failed ? "НАСТРОЙКИ ПРОВАЛ" : "НАСТРОЙКИ УСПЕХ");
        return failed ? 1 : 0;
    }
}
