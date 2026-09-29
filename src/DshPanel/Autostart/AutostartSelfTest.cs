using DshPanel.Isolation;
using DshPanel.Platform;

namespace DshPanel.Autostart;

/// <summary>
/// Проба автозапуска — то, что нельзя проверить тестами без экрана: настоящий реестр,
/// настоящий файл и выбор хранилища по признаку изоляции.
///
/// Три части, и все три нужны:
///
/// 1. **Изоляция.** В изолированном прогоне запись автозапуска идёт в ФАЙЛ под корнем прогона.
///    Проверяется не «мы старались», а факт: хранилище — файловое и лежит под корнем.
/// 2. **Реестр — на СВОЕЙ ветке.** Настоящий <c>Run</c> проба не читает и не пишет: она заводит
///    ветку <see cref="AutostartDecisions.SelfTestKeyPath"/>, упражняет на ней тот же код, что
///    работает у человека, и убирает её за собой. В v1 ветку автозапуска уводили переменной
///    окружения, и понадобился отдельный предохранитель против подмены на настоящую ветку —
///    здесь предохранитель не нужен по построению, но проверка «наша ветка не настоящая»
///    остаётся: она ловит того, кто однажды перепутает константу.
/// 3. **Решения и сверка.** «Запись ведёт на исчезнувший файл» переводится на себя, а живая
///    чужая копия — нет. Это то, ради чего автозапуск вообще сверяют при старте.
///
/// Каждая строка отчёта ВЛИЯЕТ на исход. Иначе «успех» означал бы лишь «исключений не было».
/// </summary>
public static class AutostartSelfTest
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

        var self = AutostartStores.SelfExe();
        Check("путь к себе известен", self.Length > 0);

        // Родитель у временного каталога СВОЙ: после пробы за ней не должно оставаться даже
        // пустой папки в %TEMP%, и это тоже проверяется, а не подразумевается.
        var parent = Path.Combine(Path.GetTempPath(), "dsh-panel-autostart-selftest");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var registry = RegistryAutostartStore.SelfTest();

        try
        {
            // --- 1. изоляция ---------------------------------------------------------

            var isolated = RunContext.Create(
                RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, _ => null));

            var isolatedStore = AutostartStores.For(isolated, humanLaunch: true);
            report.Add($"хранилище изолированного прогона = {isolatedStore.Describe}");

            Check("в изоляции запись идёт в файл", isolatedStore is FileAutostartStore);

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            Check(
                "файл записи лежит под корнем прогона",
                isolatedStore.Describe.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Check("ветка изоляции не настоящий Run", !AutostartDecisions.IsOwnerRunKey(isolatedStore.Describe));

            // Даже «обычный запуск человеком» в изоляции не даёт реестра: признак изоляции сильнее.
            Check(
                "обычный запуск в изоляции всё равно пишет в файл",
                AutostartStores.For(isolated, humanLaunch: false) is FileAutostartStore);

            // --- 2. файловое хранилище ----------------------------------------------

            var file = new FileAutostartStore(isolated.Paths.AutostartFile);
            Check("файла нет — записи нет", !file.Read(AutostartDecisions.ValueName).Exists);

            var value = AutostartDecisions.BuildValue(self);
            Check("запись легла", file.Write(AutostartDecisions.ValueName, value));

            var back = file.Read(AutostartDecisions.ValueName);
            Check("прочиталось то же значение", back.Exists && back.Value == value);

            Check("чужая запись не появилась", file.Read("DSHPanel").Readable && !file.Read("DSHPanel").Exists);

            Check("снятие записи", file.Delete(AutostartDecisions.ValueName));
            Check("после снятия записи нет", !file.Read(AutostartDecisions.ValueName).Exists);

            // --- 3. решения ----------------------------------------------------------

            var panel = @"C:\Program Files\DSH Panel\DshPanel.exe";
            var quoted = AutostartDecisions.BuildValue(panel);
            report.Add($"значение записи для пути с пробелом = {quoted}");

            Check("путь с пробелом записан в кавычках", quoted.StartsWith('"') && quoted.EndsWith('"'));
            Check("путь с пробелом разобран целиком", AutostartDecisions.EntryPath(quoted) == panel);
            Check("путь без кавычек тоже разобран", AutostartDecisions.EntryPath(panel) == panel);
            Check("ключи после пути не мешают", AutostartDecisions.EntryPath(quoted + " --что-то") == panel);

            // Значение из реестра сначала РАЗБИРАЕТСЯ, и уже разобранный путь сравнивается с собой:
            // сравнить сырое значение с путём («"C:\..."» против «C:\...») — самая частая ошибка
            // в этом месте, и проба на exe её уже ловила (24.09.2026).
            Check(
                "запись-на-себя распознана",
                AutostartDecisions.IsSelf(AutostartDecisions.EntryPath(quoted), panel)
                && AutostartDecisions.IsSelf(panel, panel));

            var missing = AutostartDecisions.Classify(
                new AutostartRecord(true, quoted), self, _ => false);
            Check("мёртвая запись — Missing", missing.Where == AutostartWhere.Missing);
            Check("мёртвая запись переводится на себя", AutostartDecisions.ShouldRewrite(missing, self));

            var neighbour = AutostartDecisions.Classify(
                new AutostartRecord(true, quoted), self, _ => true);
            Check("живая чужая копия — Other", neighbour.Where == AutostartWhere.Other);
            Check("живая чужая копия не трогается", !AutostartDecisions.ShouldRewrite(neighbour, self));

            var mine = AutostartDecisions.Classify(
                new AutostartRecord(true, AutostartDecisions.BuildValue(self)), self, _ => true);
            Check("своя запись — Self", mine.Where == AutostartWhere.Self);
            Check("своя запись не переписывается", !AutostartDecisions.ShouldRewrite(mine, self));

            var sloppy = AutostartDecisions.Classify(
                new AutostartRecord(true, self + "  "), self, _ => true);
            Check("неаккуратная своя запись приводится к виду", AutostartDecisions.ShouldRewrite(sloppy, self));

            Check(
                "нечитаемое хранилище — Unknown, а не «выключено»",
                AutostartDecisions.Classify(AutostartRecord.Unreadable, self, _ => true).Where == AutostartWhere.Unknown);
            Check(
                "неизвестное состояние не правится",
                !AutostartDecisions.ShouldRewrite(AutostartState.UnknownState, self));

            // --- 4. сверка при старте на файловом хранилище ---------------------------

            var checkRoot = Path.Combine(root, "сверка");
            Directory.CreateDirectory(checkRoot);
            var repairStore = new FileAutostartStore(Path.Combine(checkRoot, "autostart.txt"));
            var repair = new AutostartController(repairStore, self, _ => { });

            repairStore.Write(AutostartDecisions.ValueName, quoted);
            repair.Refresh();
            Check("перед сверкой запись ведёт в никуда", repair.State.Where == AutostartWhere.Missing);
            Check("сверка перевела мёртвую запись на себя", repair.Repair() && repair.State.Where == AutostartWhere.Self);

            Check("выключение убирает запись", repair.Set(false) && !repair.State.Enabled);
            Check("включение ставит её на себя", repair.Set(true) && repair.State.Where == AutostartWhere.Self);

            // --- 5. реестр: своя ветка ------------------------------------------------

            report.Add($"ветка пробы = {registry.Describe}");

            Check("ветка пробы — не настоящий Run", !AutostartDecisions.IsOwnerRunKey(registry.KeyPath));
            Check("настоящий Run узнаётся", AutostartDecisions.IsOwnerRunKey(AutostartDecisions.OwnerRunKeyPath));
            Check("настоящий Run узнаётся и с HKCU", AutostartDecisions.IsOwnerRunKey(@"HKCU\" + AutostartDecisions.OwnerRunKeyPath));

            // Ветка, которой нет: «ветки нет» — это «записи нет», а не «прочитать не удалось».
            var absent = new RegistryAutostartStore(
                $@"Software\DshPanel2\НетТакойВетки{Guid.NewGuid():N}");
            var absentRecord = absent.Read(AutostartDecisions.ValueName);
            Check("несуществующая ветка — это «записи нет»", absentRecord.Readable && !absentRecord.Exists);

            Check("реестр: запись", registry.Write("Selftest", quoted));
            Check("ветка пробы заведена", RegistryNative.Exists(registry.KeyPath));

            var registryBack = registry.Read("Selftest");
            Check("реестр: прочиталось то же значение", registryBack.Exists && registryBack.Value == quoted);
            Check("реестр: снятие", registry.Delete("Selftest") && !registry.Read("Selftest").Exists);

            // Убираем и лист, и свою родительскую ветку: лист оставлял бы за нами пустую ветку
            // в реестре владельца.
            Check("реестр: своя ветка убрана", registry.DeleteKey());
            Check("реестр: родительская ветка убрана", RegistryNative.DeleteKey(AutostartDecisions.OwnKeyRoot));
            Check(
                "в реестре не осталось наших веток",
                !RegistryNative.Exists(registry.KeyPath) && !RegistryNative.Exists(AutostartDecisions.OwnKeyRoot));
        }
        catch (Exception ex)
        {
            failed = true;
            report.Add($"ОШИБКА: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            // За собой прибираем ВСЕГДА, в том числе после ошибки: своя ветка реестра и временный
            // каталог не должны переживать проверку. Молчаливой уборки мало — она печатается.
            var keyGone = registry.DeleteKey() & RegistryNative.DeleteKey(AutostartDecisions.OwnKeyRoot);
            var dirGone = true;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

                // Пустой родительский каталог тоже убираем. Не вышло (значит, им пользуется
                // другая проба) — это не провал, но и об этом честно пишем.
                try { if (Directory.Exists(parent)) Directory.Delete(parent); } catch { }
            }
            catch { dirGone = false; }

            report.Add(
                $"уборка: ветки реестра нет = {!RegistryNative.Exists(AutostartDecisions.OwnKeyRoot)}, " +
                $"временный каталог убран = {dirGone}, пустого каталога проб нет = {!Directory.Exists(root)}");

            if (!keyGone || !dirGone || RegistryNative.Exists(AutostartDecisions.OwnKeyRoot)) failed = true;
        }

        foreach (var line in report) Console.WriteLine("АВТОЗАПУСК| " + line);
        Console.WriteLine(failed ? "АВТОЗАПУСК ПРОВАЛ" : "АВТОЗАПУСК УСПЕХ");
        return failed ? 1 : 0;
    }
}
