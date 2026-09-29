using System.Globalization;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;

namespace DshPanel.Headless;

/// <summary>
/// ОБНОВЛЕНИЕ БЕЗ ОКНА: <c>--update-check</c> и <c>--update-prepare</c>.
///
/// **Зачем эти два ключа вообще.** Панель обновляется из окна: «Проверить сейчас» → «Скачать
/// и подготовить» → «Обновить и перезапустить панель». Но **лабораторный прогон самообновления
/// без ключей невозможен**: окно двигает человек, а прогон идёт сам и должен быть повторяемым
/// (`docs\LAB-UPDATE.md` §1 и §3). В панели 1.x такие ключи были (<c>--update-check</c>,
/// <c>--update-prepare</c>) — переносим то же решение, а не выдумываем своё.
///
/// **Чего здесь НЕТ, и это нарочно:**
/// * **замены файлов.** Её запускает сценарий, и запускает его ЧЕЛОВЕК отдельным нажатием
///   (<c>IUpdateInstall.Launch</c>). Ключа «запусти замену» в панели не будет: это единственная
///   дверь продукта, которая подменяет саму панель;
/// * **своего пути обновления.** Скачивание, сверка суммы, распаковка, сверка версии, страховая
///   копия и сочинение сценария идут ТОЙ ЖЕ дверью, что у окна (<see cref="UpdateInstallController"/>):
///   скрипт и человек обязаны получать одно и то же, иначе прогон доказывал бы не то, что делает
///   продукт.
///
/// **Изоляция обязательна** (красная линия 5): режим пишет в каталог состояния (скачанное, журнал
/// замены, страховочную копию) и читает настройки. Без своего корня он отказывается кодом 2 —
/// ровно как копия и накат.
/// </summary>
public static class UpdateHeadless
{
    /// <summary>Спросить выпуски и рассказать, что нашли. Сети требует — потому и не в проверках.</summary>
    public const string CheckKey = "--update-check";

    /// <summary>Скачать, сверить сумму, распаковать и сочинить сценарий. Файлы панели НЕ трогает.</summary>
    public const string PrepareKey = "--update-prepare";

    /// <summary>Знает ли панель этот ключ (спрашивает разбор аргументов).</summary>
    public static bool IsMode(string? key) => key is CheckKey or PrepareKey;

    /// <summary>
    /// Исполнить режим. Возвращает код: <c>0</c> успех, <c>1</c> провал, <c>2</c> неприменимо.
    ///
    /// <paramref name="client"/> и <paramref name="clock"/> — швы для проверок: у настоящего прогона
    /// они пусты и берутся системные источники, а проверка подставляет записанный ответ и потому
    /// в сеть не ходит вовсе.
    /// </summary>
    public static int Run(
        string key,
        IUpdateClient? client = null,
        Func<DateTimeOffset>? clock = null)
    {
        var context = RunContext.Current;

        // Право — ДО любой работы, и до создания папок тоже. Прогон без своего корня не имеет права
        // ни читать настройки человека, ни писать в его каталог состояния.
        if (!context.IsIsolated)
        {
            Console.WriteLine(
                $"ОБНОВЛЕНИЕ ПРОВАЛ: {key} работает только со своим корнем ({RunRequest.RootSwitchArgument}). " +
                "Он читает настройки и пишет скачанное — на данных владельца этого делать нельзя.");
            return 2;
        }

        var paths = context.Paths;
        var now = (clock ?? (() => DateTimeOffset.Now))();
        var update = client ?? new HttpUpdateClient();
        var language = Language(paths);
        var current = Views.AboutWindow.PanelVersion;

        Console.WriteLine($"ОБНОВЛЕНИЕ: адрес выпусков = {ProductLinks.UpdateApi}");
        Console.WriteLine($"ОБНОВЛЕНИЕ: установлено = {current}");

        var release = update.Latest(language, now);

        if (!release.Ok)
        {
            Console.WriteLine($"ОБНОВЛЕНИЕ ПРОВАЛ: выпуск не прочитан — {release.Error}");
            return 1;
        }

        Console.WriteLine($"ОБНОВЛЕНИЕ: на выпуске = {release.Latest} (опубликован {release.Published})");

        var newer = UpdateDecisions.IsNewer(release.Latest, current);
        Console.WriteLine(newer
            ? "ОБНОВЛЕНИЕ: выпуск НОВЕЕ установленного"
            : "ОБНОВЛЕНИЕ: выпуск не новее установленного — обновляться некуда");

        // Заметки печатаются на языке панели — тем же разбором, что у окна: прогон обязан видеть
        // ровно тот текст, который увидел бы человек.
        var notes = UpdateDecisions.PickNotes(release.NotesRaw, language);

        if (notes.Length > 0)
        {
            Console.WriteLine("ОБНОВЛЕНИЕ: заметки —");
            foreach (var line in notes.Replace("\r\n", "\n").Split('\n')) Console.WriteLine("    " + line);
        }

        if (key == CheckKey) return 0;

        if (!newer)
        {
            Console.WriteLine("ОБНОВЛЕНИЕ ПРОВАЛ: готовить нечего — выпуск не новее установленного");
            return 1;
        }

        // Движок установки — ТОТ ЖЕ, что у окна: папка панели берётся у ЭТОГО exe, а не ищется.
        var install = new UpdateInstallController(
            paths,
            () => current,
            target: Path.GetDirectoryName(Autostart.AutostartStores.SelfExe()) ?? string.Empty,
            allowed: true,
            startArgument: string.Empty,
            mutexName: InstanceSignal.DefaultMutexName,
            log: line => Console.WriteLine("ОБНОВЛЕНИЕ (журнал): " + line));

        // ХОД ПЕЧАТАЕТСЯ ТОЛЬКО ПРИ ПЕРЕМЕНЕ — и это не косметика. Движок сообщает о себе часто
        // (одно и то же «Архив панели» на каждую прочитанную порцию), и печать каждой вести
        // превращала отчёт прогона в сотни одинаковых строк: по нему нельзя было понять, где
        // прогон находится и не завис ли он. ⚠️ Нашлось на первом же живом прогоне 28.09.2026.
        var lastStage = (UpdateStage?)null;
        var lastPercent = -2;

        var preparation = install.Prepare(
            release.Latest,
            release.Assets,
            progress =>
            {
                if (lastStage == progress.Stage && lastPercent == progress.Percent) return;

                lastStage = progress.Stage;
                lastPercent = progress.Percent;

                Console.WriteLine(
                    "    " + UpdateStageLines.Text(progress.Stage) +
                    (progress.Percent < 0 ? string.Empty : $" — {progress.Percent} %"));
            });

        if (!preparation.Ok)
        {
            Console.WriteLine($"ОБНОВЛЕНИЕ ПРОВАЛ: {UpdateRefusalLines.Line(preparation)}");
            return 1;
        }

        Console.WriteLine($"ОБНОВЛЕНИЕ: подготовлено {preparation.Version}");
        Console.WriteLine($"ОБНОВЛЕНИЕ: сборка = {preparation.StagedFolder}");
        Console.WriteLine($"ОБНОВЛЕНИЕ: страховочная копия = {preparation.BackupFolder}");
        Console.WriteLine($"ОБНОВЛЕНИЕ: сценарий = {preparation.ScriptPath}");
        Console.WriteLine($"ОБНОВЛЕНИЕ: журнал замены = {preparation.LogPath}");
        Console.WriteLine("ОБНОВЛЕНИЕ: ФАЙЛЫ ПАНЕЛИ НЕ ЗАМЕНЕНЫ — замену запускает человек отдельным нажатием");

        return 0;
    }

    /// <summary>
    /// Язык заметок — из настроек ЭТОГО корня (как у окна: настройки человека, прочитанные панелью),
    /// а если их нет — язык панели по умолчанию. Пустое значение — «как в системе», и тогда язык
    /// выбирает та же дверь, что у обычного запуска.
    /// </summary>
    private static string Language(AppPaths paths)
    {
        try
        {
            var settings = new SettingsStore(paths.SettingsFile).Load().Settings;
            var chosen = (settings.Language ?? string.Empty).Trim();

            if (chosen.Length > 0)
            {
                var resolved = Localization.LanguageDecisions.Resolve(chosen, Environment.GetEnvironmentVariable(Localization.LanguageDecisions.Switch), CultureInfo.CurrentUICulture.Name);
                if (resolved.Length > 0) return resolved;
            }
        }
        catch
        {
            // Настроек нет или они битые — берём язык системы: прогон из-за этого падать не должен.
        }

        return Localization.LanguageDecisions.Resolve(string.Empty, Environment.GetEnvironmentVariable(Localization.LanguageDecisions.Switch), CultureInfo.CurrentUICulture.Name);
    }
}
