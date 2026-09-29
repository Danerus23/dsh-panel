using System.Diagnostics;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Server;

namespace DshPanel.Backup;

/// <summary>
/// Самотест копий и наката на собранном exe. Отвечает на главный вопрос этапа 4, который нельзя
/// задать модульной проверке: **копию сняли, разложили — и работа ПРОДОЛЖАЕТСЯ?**
///
/// Что здесь делается и почему именно так:
///
/// 1. **Своя площадка под корнем прогона.** Всё, что проба создаёт, лежит в одной папке
///    (<c>&lt;корень&gt;/backup-selftest</c>) и убирается за собой — вместе с временными ссылками.
///    Данные владельца проба не читает вообще: пути берутся у <see cref="AppPaths.Under"/>,
///    а не у настоящего прогона.
/// 2. **Движок для копии — подставной, внутри площадки.** Настоящий движок машины в копию
///    не берётся намеренно: он лежит в <c>Program Files</c> или в <c>%APPDATA%\npm</c>, и накат
///    разложил бы его ОБРАТНО ТУДА ЖЕ, то есть проба переписала бы установку Node на машине.
///    Подставной движок даёт то же, что нужно проверке: группу движка и Node в описи, их виды
///    и раскладку на место.
/// 3. **Копия снимается настоящим движком копии** (список корней, опись внутри архива, права,
///    проверка `7z t`), а не «похожей» сборкой: проверять надо то, что работает у человека.
/// 4. **Порча перед накатом — настоящая.** Файлы сессии и проекта удаляются с диска, настройки
///    панели тоже, ссылка снимается. После наката они обязаны вернуться, и вернуться с тем же
///    содержимым.
/// 5. **«Продолжил» доказывается НАСТОЯЩИМ сервером.** На восстановленном профиле поднимается
///    настоящий движок на своём свободном порту, отвечает отпечатком и гасится. Без этого
///    «накат сделан» означало бы только «файлы легли», а критерий этапа — «копия → развернул →
///    продолжил».
///
/// Идёт ТОЛЬКО в изоляции: накат и копия — это запись в каталоги данных (красная линия 5),
/// а у настоящего прогона это каталоги владельца.
///
/// ⚠️ Лежит рядом с движком копии (по имени режима — <c>--backup-selftest</c>), а проверяет ОБЕ
/// половины этапа 4: и снятие копии, и накат. Разделять их по файлам значило бы утверждать, что
/// копия и накат проверяются порознь, — а критерий этапа у них общий: «копия → развернул →
/// продолжил».
/// </summary>
public static class BackupSelfTest
{
    /// <summary>
    /// Сколько ждать сервера на восстановленном профиле. С запасом, как у самотеста сервера:
    /// первый запуск в свежем домашнем каталоге достраивает профиль около минуты.
    /// </summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(240);

    public static int Run(TimeSpan timeout)
    {
        var context = RunContext.Current;
        var report = new List<string>();
        var log = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        if (!context.IsIsolated)
        {
            Console.WriteLine(
                "КОПИИ ПРОВАЛ: самотест снимает копию и раскладывает её — то есть пишет в каталоги " +
                "данных, а у настоящего прогона это каталоги владельца (~/.dsh и папка панели). " +
                "Запустите с «--run-root <свой путь>».");
            return 2;
        }

        var engine = DshEngine.Locate();
        Check("движок найден (node и @deepseek-ai/dsh)", engine is not null);

        if (engine is null)
        {
            // Движка нет — это НЕ поломка панели: «продолжить» нечем. Код 2 — «неприменимо»,
            // как у самотеста сервера. Путать с «панель сломана» нельзя.
            report.Add("на этой машине нет Node и пакета @deepseek-ai/dsh — продолжать нечем");
            Print(report, log);
            Console.WriteLine("КОПИИ НЕПРИМЕНИМО");
            return 2;
        }

        // Площадка пробы: лежит под корнем прогона и убирается за собой целиком.
        var area = Path.Combine(context.Paths.Root, "backup-selftest");
        var subject = AppPaths.Under(Path.Combine(area, "subject"));
        var work = Path.Combine(subject.Root, "projects");
        var probeEngine = ProbeEngine(area);
        var archive = Path.Combine(area, "copy.zip");

        ServerController? controller = null;

        try
        {
            report.Add($"площадка пробы = {area}");

            // --- (1) пустой файл ключа возвращать нельзя -----------------------------
            //
            // Прогон в ВМ 23.09.2026: копия с файлом из 11 байт (`token: ""`) вернула его
            // на место — и сервер перестал подниматься вовсе. Проверяем это на диске, а не
            // только разбором строки.
            var empty = BuildSource(subject, work, "token: \"\"\n");
            var firstCopy = Copy(subject, work, probeEngine, archive);

            report.Add(firstCopy.Summary());
            Check("копия снята (файл ключа без ключей)", firstCopy.Ok);
            Check("в описи есть движок", ManifestOf(archive)?.WithEngine == true);

            File.Delete(subject.CredentialsPath);
            File.Delete(empty.Session);

            var firstRestore = Restore(subject, work, probeEngine, archive);

            report.Add(firstRestore.Summary());
            Check("накат сделан (файл ключа без ключей)", firstRestore.Ok);
            Check("файл ключа без ключей НЕ возвращён", !File.Exists(subject.CredentialsPath));
            Check("пропуск назван в отчёте",
                firstRestore.Skipped.Any(line => line.Contains("не хранит ни одного ключа")));
            Check("сессия вернулась с тем же содержимым", File.ReadAllText(empty.Session) == empty.SessionText);

            // --- (2) копия → порча → накат → ПРОДОЛЖИЛ -------------------------------

            var source = BuildSource(subject, work, "token: sk-probe-not-a-real-key\n");
            var secondCopy = Copy(subject, work, probeEngine, archive);

            report.Add(secondCopy.Summary());
            Check("копия снята (рабочее состояние)", secondCopy.Ok);
            Check("в копии названы все пять групп", Groups(secondCopy) == 5);
            Check("копия проверена сторонним валидатором или 7-Zip отсутствует", secondCopy.Verified != false);

            // Портим всё, что человек заметит: сессию, проект, историю git, ссылку, настройки.
            File.Delete(source.Session);
            File.Delete(source.Project);
            File.Delete(source.GitLog);
            File.Delete(Path.Combine(subject.Root, "settings.json"));
            RemoveLink(source.Link);

            Check("порча сделана: сессии нет", !File.Exists(source.Session));
            Check("порча сделана: ссылки нет", !IsLink(source.Link));

            var restore = Restore(subject, work, probeEngine, archive);

            report.Add(restore.Summary());
            Check("накат сделан", restore.Ok);
            Check("сессия вернулась", File.Exists(source.Session) && File.ReadAllText(source.Session) == source.SessionText);
            Check("файл проекта вернулся", File.Exists(source.Project));
            Check("`.git` вернулся целиком (вместе с logs)", File.Exists(source.GitLog));
            Check("настройки панели вернулись", File.Exists(Path.Combine(subject.Root, "settings.json")));
            Check("ссылка создана заново", IsLink(source.Link) && restore.Links == 1);
            Check("предохранительная копия снята перед накатом",
                restore.SafetyPath is not null && File.Exists(restore.SafetyPath));

            // --- (3) «продолжил»: настоящий сервер на восстановленном профиле ---------
            //
            // ⚠️ Проба НЕ МОЖЕТ изготовить настоящую сессию, и это названо, а не спрятано:
            // движок читает БАЙТЫ (проверено дважды: «unsupported flat-file layout» на плоском
            // файле и «invalid frame magic» на подложенном), а zstd средствами .NET недоступен.
            // Поэтому подложенные байты убираются перед пуском — иначе проверялось бы не «накат
            // вернул данные», а «движок переживает чужие байты». Что файл под `sessions` ВЕРНУЛСЯ,
            // уже проверено выше; у человека в копии лежат настоящие сессии, и они читаются.
            var sessions = Path.Combine(subject.DshHome, "sessions");
            var aside = Path.Combine(area, "sessions-set-aside");

            if (Directory.Exists(sessions))
            {
                Directory.Move(sessions, aside);
                report.Add("сессии: файл вернулся (проверено выше), перед пуском движка убран — "
                           + "настоящую сессию проба создать не может (движок читает байты, zstd в .NET нет)");
            }

            controller = new ServerController(
                subject.DshHome,
                () => work,
                log.Add,
                // Права занять порт владельца у прогона проверки нет — порт берётся свободный.
                mayOccupyOwnerPort: false,
                locateEngine: () => engine,
                requestedPort: () =>
                {
                    // Свободный порт: проба не отбирает порт ни у человека, ни у соседнего прогона.
                    var free = FreePort.Find();
                    return free == 0 ? ServerDecisions.DefaultServerPort : free;
                },

                // Право «поднимать рядом» просится ОТКРЫТЫМ ТЕКСТОМ: прогон изолированный (свой
                // корень, свои данные), а на машине при этом работает сервер владельца — иначе
                // замок от второго движка не дал бы проверить, что движок поднимается на
                // ВОССТАНОВЛЕННОМ профиле.
                allowParallelStart: true);

            var started = controller.Start(timeout);
            report.Add($"сервер на восстановленном профиле: порт {started.Port}, {started.Detail}");

            Check("сервер поднялся на ВОССТАНОВЛЕННОМ профиле", started.IsRunning);
            Check($"порт не порт владельца ({ServerDecisions.OwnerPort})",
                ServerDecisions.IsAllowedPort(started.Port, mayOccupyOwnerPort: false));

            if (started.IsRunning)
            {
                foreach (var line in controller.EngineTail().TakeLast(10)) report.Add("движок| " + line);

                var stopped = controller.Stop(confirmed: false);
                Check("сервер погашен, порт свободен",
                    stopped.Presence == ServerPresence.Stopped);
            }
            else
            {
                // Не поднялся — это главный провал пробы: значит накат вернул не всё, что нужно
                // для работы. Показываем хвост движка, иначе разбираться будет нечем.
                foreach (var line in controller.EngineTail().TakeLast(20)) report.Add("движок| " + line);
            }
        }
        catch (Exception error)
        {
            failed = true;
            report.Add($"исключение пробы: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            // Самоуборка: гасим свой сервер и убираем площадку целиком. Ссылки снимаются
            // звеньями — рекурсивное удаление сквозь них снесло бы не то, что собирались.
            try { controller?.Stop(confirmed: false); } catch { }
            controller?.Dispose();
            RemoveTree(area);
        }

        report.Add($"площадка убрана за собой = {!Directory.Exists(area)}");
        if (Directory.Exists(area)) failed = true;

        Print(report, log);
        Console.WriteLine(failed ? "КОПИИ ПРОВАЛ" : "КОПИИ УСПЕХ");
        return failed ? 1 : 0;
    }

    // --- что проверяем --------------------------------------------------------

    /// <summary>Состояние «до копии»: что именно человек потеряет, если копии не будет.</summary>
    private sealed record Source(string Session, string SessionText, string Project, string GitLog, string Link);

    /// <summary>
    /// Собирает рабочее состояние пробы: сессия движка, файл ключа, проект с историей git и
    /// ссылкой, настройки панели и «глобальные пакеты», на которые эта ссылка смотрит.
    ///
    /// ⚠️ Профиль движка (<c>profiles\…</c>) здесь НЕ создаётся: его достраивает сам движок при
    /// первом запуске. Подсунуть ему поддельный профиль значило бы проверять не то, что бывает
    /// у человека, а свою выдумку.
    /// </summary>
    private static Source BuildSource(AppPaths subject, string work, string credentials)
    {
        // Сессия движка — В ТОЙ ФОРМЕ, в какой он её ждёт: `sessions\<проект>\<сессия>\session.vN.jsonl.zstd`.
        //
        // Форма добыта двумя прогонами самотеста, а не догадкой, и оба отказа движка записаны:
        //   * плоский файл прямо в `sessions\<проект>` — «uses the unsupported flat-file layout»;
        //   * файл с суффиксом `.jsonl` — «uses .jsonl, but this backend is configured for
        //     compression "zstd"».
        // Отсюда раскладка: каталог проекта (`_no-cwd` — штатный, когда рабочая папка неизвестна),
        // каталог сессии и файл поколения с zstd-суффиксом. Имя берём ровно такое, какое пишет
        // сам движок (`dsh-session-format`: `session.vN.jsonl` + суффикс сжатия), — на старте он
        // смотрит на ИМЯ, байты не читает: это и есть причина, по которой здесь заглушка,
        // а не настоящий zstd-поток.
        var session = Path.Combine(subject.DshHome, "sessions", "_no-cwd", "probe-0001", "session.v3.jsonl.zstd");
        const string sessionText = "{\"turn\":1}\n";

        var project = Path.Combine(work, "proj", "src", "a.txt");
        var gitLog = Path.Combine(work, "proj", ".git", "logs", "HEAD");
        var link = Path.Combine(work, "proj", "link");
        var packages = Path.Combine(subject.Root, "global-packages");

        Write(session, sessionText);
        Write(subject.CredentialsPath, credentials);
        Write(project, "A\n");
        Write(gitLog, "reflog\n");
        Write(Path.Combine(subject.Root, "settings.json"), "{\"serverPort\":3081}\n");
        Write(Path.Combine(packages, "some-pkg", "index.js"), "pkg\n");

        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateLink(link, packages);

        return new Source(session, sessionText, project, gitLog, link);
    }

    private static BackupRunResult Copy(AppPaths subject, string work, DshEngine engine, string archive) =>
        BackupEngine.Run(
            BackupPlanner.Full(subject, work, engine),
            new BackupRequest(archive, SevenZipPath: null, Verify: true),
            subject,
            engine);

    private static RestoreRunResult Restore(AppPaths subject, string work, DshEngine engine, string archive)
    {
        var options = new RestoreOptions(WithEngine: true, WithPanel: true, SafetyCopy: true);
        var plan = RestoreEngine.Plan(archive, subject, work, options, engine);

        // План несобираемый — накат откажет сам и назовёт причину; здесь ничего не подменяем.
        return RestoreEngine.Run(plan, options, subject, engine);
    }

    private static BackupManifest? ManifestOf(string archive) => ZipReader.Read(archive).Manifest;

    private static int Groups(BackupRunResult copy) =>
        ZipReader.Read(copy.ArchivePath).Manifest?.Paths.Count ?? 0;

    // --- подставной движок ----------------------------------------------------

    /// <summary>
    /// Движок и Node ВНУТРИ площадки — чтобы группы движка были в описи, а накат раскладывал их
    /// на своё место, не касаясь настоящей установки Node на машине.
    ///
    /// Раскладка повторяет настоящую: пакеты лежат под <c>node_modules</c>, а имя папки равно
    /// имени группы — иначе быстрый путь через 7-Zip закрылся бы (это проверяется в другом месте).
    /// </summary>
    private static DshEngine ProbeEngine(string area)
    {
        var npm = Path.Combine(area, "engine", "npm");
        var node = Path.Combine(area, "engine", "nodejs");

        var bin = Path.Combine(npm, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        Write(bin, "// probe engine\n");
        Write(Path.Combine(npm, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.0.0-probe\"}\n");
        Write(Path.Combine(npm, "dsh.cmd"), "@echo off\n");
        Write(Path.Combine(node, "node.exe"), "MZ\n");

        return new DshEngine(Path.Combine(node, "node.exe"), bin);
    }

    // --- мелочи площадки -------------------------------------------------------

    private static void Write(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

    private static bool IsLink(string path)
    {
        try
        {
            return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ссылка-джункция: Windows умеет это только через <c>mklink /J</c>.</summary>
    private static bool CreateLink(string path, string target)
    {
        var run = Run("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"", 15_000);
        return run && Directory.Exists(path);
    }

    private static void RemoveLink(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path);
        }
        catch
        {
            // Ссылки может уже не быть.
        }
    }

    private static bool Run(string executable, string arguments, int timeoutMs)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(timeoutMs) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Уборка площадки. Ссылки снимаются ЗВЕНЬЯМИ, а не обходом: рекурсивное удаление по дереву
    /// со ссылкой внутри — известный способ снести не то, что собирался (правило проекта).
    /// </summary>
    private static void RemoveTree(string root)
    {
        static void Walk(string directory)
        {
            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(directory);
            }
            catch
            {
                return;
            }

            foreach (var child in children)
            {
                try
                {
                    if (!Directory.Exists(child))
                    {
                        File.Delete(child);
                        continue;
                    }

                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(child);
                        continue;
                    }

                    Walk(child);
                    Directory.Delete(child);
                }
                catch
                {
                    // Занятый файл (движок мог не отпустить) — не повод падать в уборке.
                }
            }
        }

        try
        {
            if (!Directory.Exists(root)) return;

            Walk(root);
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Не убралось — проба скажет об этом отдельной строкой отчёта.
        }
    }

    private static void Print(List<string> report, List<string> log)
    {
        foreach (var line in report) Console.WriteLine("КОПИИ| " + line);
        foreach (var line in log) Console.WriteLine("ЖУРНАЛ| " + line);
    }
}
