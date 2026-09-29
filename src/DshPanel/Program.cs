using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Autostart;
using DshPanel.Backup;
using DshPanel.Balance;
using DshPanel.Headless;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Update;
using DshPanel.Views;

namespace DshPanel;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Кириллица в перенаправленном выводе: иначе отчёты проверок приходят крякозябрами.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        // Контекст прогона — ДО всего остального. Без него не вычисляется ни один путь,
        // а RunContext.Current падает с внятной ошибкой вместо того, чтобы молча взять
        // каталоги владельца.
        try
        {
            RunContext.Install(RunContext.Create(
                RunRequest.Parse(args, Environment.GetEnvironmentVariable)));
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"ИЗОЛЯЦИЯ ПРОВАЛ: {ex.Message}");
            return 2;
        }

        var isolated = RunContext.Current.IsIsolated;

        // Режимы разбираются по первому СВОЕМУ аргументу, а ключ корня может стоять где угодно.
        // Без этой зачистки «--run-root <путь> --isolation-selftest» не опознавался бы как проба
        // изоляции, а падал бы отказом «не сказано, что делать». Проверено на собранном exe.
        var rest = RunRequest.WithoutRootSwitch(args);

        // ЯЗЫК — ДО ВСЕГО, что строит интерфейс: и на обычном запуске, и в режиме съёмки.
        // Ключ `--lang` снимается до разбора режимов, как и `--run-root`, иначе разбор счёл бы
        // его опечаткой. Переменная DSH_PANEL_LANG сильнее ключа и настройки — порядок взят у v1.
        var languageSwitch = LanguageDecisions.SwitchValue(rest);
        if (LanguageDecisions.HasSwitch(rest) && languageSwitch is null)
        {
            Console.WriteLine(
                "ЯЗЫК ПРОВАЛ: ключ «--lang» указан без языка. Нужен один из: ru, en, zh.");
            return 2;
        }

        // Незнакомый язык — ТОЖЕ громкий отказ, а не «возьмём язык системы»: ключ `--lang` —
        // поверхность скриптов и съёмки, и опечатка («--lang e» вместо «--lang en») молча
        // положила бы в отчёт кадр не на том языке. Текст отказа и список языков живут
        // в чистых решениях: там же, где выбор языка.
        var languageRefusal = LanguageDecisions.RefuseSwitch(languageSwitch);
        if (languageRefusal is not null)
        {
            Console.WriteLine(languageRefusal);
            return 2;
        }

        // Настройку языка читает только тот прогон, которому она принадлежит: изолированный
        // (свой файл под своим корнем) — сразу, обычный запуск человеком — перед постройкой окон
        // (ниже, в обычном запуске). Проверки и съёмка читают «auto»: настроек владельца они
        // не касаются (красная линия 4), а язык берут из ключа, переменной или системы.
        Loc.Init(languageSwitch ?? (isolated ? SettingsLanguage() : null));

        rest = LanguageDecisions.WithoutSwitch(rest);

        // Съёмка интерфейса без показа окна — для README и для глазной проверки вёрстки.
        // Третий аргумент выбирает окно: нет — главное, «settings» — настройки, «backup» — копии.
        // Неизвестное имя окна — громкий отказ, а не «снимем главное»: опечатка в скрипте съёмки
        // иначе положила бы в README не тот кадр, и заметить это было бы некому.
        if (rest.Length >= 2 && rest[0] == "--shot") return Shot(rest[1], rest.Length >= 3 ? rest[2] : string.Empty);

        // Проверка трея без участия человека: значок, насос сообщений, уведомление.
        if (rest.Length >= 1 && rest[0] == "--tray-selftest")
        {
            // Самотест трея по своей природе ПОКАЗЫВАЕТ значок. Изолированному прогону
            // показывать владельцу нечего, поэтому отказываем громко: молчаливое «ничего
            // не показали» выглядело бы успешной проверкой, а ею не было бы.
            if (isolated)
            {
                Console.WriteLine(
                    "ИЗОЛЯЦИЯ ПРОВАЛ: самотест трея показывает значок на рабочем столе, " +
                    "а изолированный прогон не показывает ничего. Запустите самотест без корня.");
                return 2;
            }

            // Число разбираем с проверкой: нечисловой аргумент должен дать внятный
            // отказ, а не необработанное исключение в выводе.
            var seconds = 6;
            if (rest.Length >= 2 && !int.TryParse(rest[1], out seconds))
            {
                Console.WriteLine($"SELFTEST ПРОВАЛ: длительность «{rest[1]}» — не число");
                return 2;
            }

            // В сессии 0 нет интерактивного рабочего стола, и оболочка ОТКАЗЫВАЕТСЯ ставить
            // значок. Это не провал панели, а «проверять нечего»: измерено на чистой ВМ
            // 24.09.2026 (`Win32Exception: оболочка отказалась добавить значок`).
            if (SessionZero())
            {
                Console.WriteLine(
                    "SELFTEST НЕПРИМЕНИМО: сессия 0 — интерактивного рабочего стола нет, оболочка " +
                    "не принимает значки. Проверка трея возможна только в сессии человека.");
                return 2;
            }

            return TraySelfTest(Math.Clamp(seconds, 1, 120));
        }

        // Проверка связки «значок ↔ окно»: щелчок по значку открывает окно, крестик прячет
        // его в трей, «Выход» завершает панель. Единственный способ проверить это без человека.
        if (rest.Length >= 1 && rest[0] == "--shell-selftest")
        {
            // Причина отказа та же, что у самотеста трея: связка показывает значок И окно,
            // а изолированный прогон на рабочем столе владельца не показывает ничего.
            if (isolated)
            {
                Console.WriteLine(
                    "ИЗОЛЯЦИЯ ПРОВАЛ: самотест связки показывает значок и окно на рабочем столе, " +
                    "а изолированный прогон не показывает ничего. Запустите самотест без корня.");
                return 2;
            }

            var seconds = 1;
            if (rest.Length >= 2 && !int.TryParse(rest[1], out seconds))
            {
                Console.WriteLine($"SHELL ПРОВАЛ: длительность «{rest[1]}» — не число");
                return 2;
            }

            // То же, что у трея: в сессии 0 значка не будет вовсе, а связка без значка —
            // это уже не связка. Измерено на чистой ВМ 24.09.2026.
            if (SessionZero())
            {
                Console.WriteLine(
                    "SHELL НЕПРИМЕНИМО: сессия 0 — интерактивного рабочего стола нет, значка в трее " +
                    "не будет, и связку проверять нечем. Запустите самотест в сессии человека.");
                return 2;
            }

            return ShellSelfTest(Math.Clamp(seconds, 1, 120));
        }

        // Самотест сервера: поднимает НАСТОЯЩИЙ движок на своём свободном порту и гасит его.
        // Без изоляции отказывается сам (ServerSelfTest): движок пишет в домашний каталог,
        // а у настоящего прогона это ~/.dsh владельца.
        if (rest.Length >= 1 && rest[0] == "--server-selftest")
        {
            var seconds = (int)ServerSelfTest.DefaultTimeout.TotalSeconds;
            if (rest.Length >= 2 && !int.TryParse(rest[1], out seconds))
            {
                Console.WriteLine($"СЕРВЕР ПРОВАЛ: длительность «{rest[1]}» — не число");
                return 2;
            }

            return ServerSelfTest.Run(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 1800)));
        }

        // Самотест копий и наката: снимает НАСТОЯЩУЮ копию своей площадкой под корнем прогона,
        // портит данные, раскладывает копию обратно и поднимает настоящий движок на
        // восстановленном профиле — критерий этапа «копия → развернул → продолжил».
        // Без изоляции отказывается сам (BackupSelfTest): копия и накат пишут в каталоги данных.
        if (rest.Length >= 1 && rest[0] == "--backup-selftest")
        {
            var seconds = (int)BackupSelfTest.DefaultTimeout.TotalSeconds;
            if (rest.Length >= 2 && !int.TryParse(rest[1], out seconds))
            {
                Console.WriteLine($"КОПИИ ПРОВАЛ: длительность «{rest[1]}» — не число");
                return 2;
            }

            return BackupSelfTest.Run(TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 1800)));
        }

        // Копия и накат БЕЗ ОКНА: `--backup <папка>` и `--restore <архив>`. Возвращаются отсюда,
        // до обычного запуска, — иначе режим поднял бы GUI и повис на рабочем столе владельца.
        // Идут ТОЛЬКО в изоляции (отказ кодом 2 внутри, красная линия 5) и ничего не гасят.
        if (rest.Length >= 1 && HeadlessDecisions.IsMode(rest[0])) return HeadlessRun.Run(rest);

        // ОБНОВЛЕНИЕ БЕЗ ОКНА: --update-check и --update-prepare. Нужны лабораторному прогону
        // самообновления (`docs\LAB-UPDATE.md` §1): окно двигает человек, а прогон идёт сам и должен быть
        // повторяемым. Замену файлов эти ключи НЕ запускают — её запускает человек отдельным нажатием.
        if (rest.Length >= 1 && UpdateHeadless.IsMode(rest[0])) return UpdateHeadless.Run(rest[0]);

        // Проба самой изоляции: единственный режим, который проверяет её на собранном exe.
        if (rest.Length >= 1 && rest[0] == "--isolation-selftest") return IsolationSelfTest();

        // Сверка трёх словарей НА СОБРАННОЙ СБОРКЕ: сколько всего ключей, чего нет или что пусто
        // в каждом языке, где разошлись подстановки и какие ключи не принадлежат панели.
        // Ничего не читает и не пишет наружу — изоляции не требует.
        if (rest.Length >= 1 && rest[0] == "--lang-selftest") return LanguageSelfTest.Run();

        // Проба обновления (первый срез: проверка выпусков и извещение). Сети в ней нет вовсе —
        // уговор `IUpdateClient` подставлен, — и файлов владельца тоже: свои настройки проба
        // заводит под своим временным корнем и убирает за собой. Поэтому изоляции не требует.
        if (rest.Length >= 1 && rest[0] == "--update-selftest") return UpdateSelfTest.Run();

        // Проба автозапуска: решения, файловое хранилище изолированного прогона и НАСТОЯЩИЙ
        // реестр — но на своей ветке, которую проба заводит и убирает за собой. Настоящий
        // HKCU\...\Run не читается и не пишется ни здесь, ни в изолированном прогоне.
        if (rest.Length >= 1 && rest[0] == "--autostart-selftest") return AutostartSelfTest.Run();

        // Проба настроек: файл на диске, право читать и писать, мусор в файле, предложение
        // рабочей папки и маскировка пути человека в показе.
        if (rest.Length >= 1 && rest[0] == "--settings-selftest") return SettingsSelfTest.Run();

        // Проба баланса и окон пика. Без ключа владельца она не читает ни его файла ключей,
        // ни сети; живой запрос делается ТОЛЬКО по явной просьбе `--with-my-key`
        // (разрешение владельца 24.09.2026).
        if (rest.Length >= 1 && rest[0] == "--balance-selftest")
        {
            var withOwnerKey = rest.Any(arg => string.Equals(arg, "--with-my-key", StringComparison.OrdinalIgnoreCase));
            return BalanceSelfTest.Run(withOwnerKey);
        }

        // Проба единого экземпляра: запускает ВТОРУЮ копию этой же панели и проверяет, что она
        // не завела вторую панель, а достучалась до первой.
        if (rest.Length >= 1 && rest[0] == "--instance-selftest") return InstanceSelfTest();

        // Роль второй копии в этой пробе: стать первым нельзя, достучаться до первого — обязательно.
        if (rest.Length >= 1 && rest[0] == "--instance-probe") return InstanceProbe();

        // Изолированный прогон обязан сказать, ЧТО он проверяет. У него по построению нет
        // ни окна, ни значка, поэтому «просто запуск» завис бы навсегда без способа выйти.
        if (isolated)
        {
            Console.WriteLine(
                "ИЗОЛЯЦИЯ ПРОВАЛ: изолированный прогон обязан указать, что он делает " +
                "(например «--isolation-selftest» или «--shot файл»). Без этого он ничего " +
                "не покажет и не завершится сам.");
            return 2;
        }

        // Неизвестный ключ или ключ проверки без обязательного аргумента — ГРОМКИЙ отказ, а не
        // обычный запуск: иначе опечатка в имени проверки открывала бы окно на рабочем столе
        // владельца, а запускал её скрипт, и заметить это было бы некому. Решение — чистая
        // функция `StartModes.Refuse` со своей проверкой. Нашёл холодный агент 24.09.2026.
        var refusal = StartModes.Refuse(rest);
        if (refusal is not null)
        {
            Console.WriteLine(refusal);
            return 2;
        }

        // Обычный запуск — то, что делает человек, щёлкнув по ярлыку. Если панель УЖЕ работает,
        // второй панели быть не должно: просим первую показать окно и выходим. Именно этого
        // человек и ждёт от второго запуска — «где моя панель», а не «почему их две».
        using (var signal = new InstanceSignal())
        {
            if (!signal.TryAcquire())
            {
                var asked = signal.RequestShowPanel();
                Console.WriteLine(asked
                    ? "INSTANCE: панель уже работает — попросил её показать окно"
                    : "INSTANCE: панель уже работает, но окна-приёмника у неё нет — показать не могу");
                return asked ? 0 : 1;
            }

            ProgramOptions.InstanceSignal = signal;

            // Язык из настроек — ЗДЕСЬ, потому что только этот путь прошёл человек: у проверок
            // и съёмки прав на его файл настроек нет (красная линия 4), и они уже выбрали язык
            // из ключа, переменной или системы. Окна строятся ниже, поэтому окно, собранное
            // раньше языка, остаться не может.
            Loc.Init(languageSwitch ?? SettingsLanguage());

            // Право трогать машинное окружение. Выдаётся РОВНО здесь — на пути, который
            // проходит человек, открывший панель. Ни одна проверка ниже сюда не попадает,
            // а значит ни одна из них не тронет ни настоящий HKCU\...\Run, ни настройки
            // прежней панели (урок v1: проверка замены файлов перевела боевую запись
            // владельца на свою временную копию).
            ProgramOptions.HumanLaunch = true;

            BuildApp().StartWithClassicDesktopLifetime(args);
        }

        return 0;
    }

    /// <summary>
    /// Язык из настроек панели. Зовётся только оттуда, где файл настроек принадлежит этому
    /// прогону: обычный запуск человеком и изолированный прогон (свой файл под своим корнем).
    /// Прогоны проверок и съёмки сюда не попадают — они видят «как в системе».
    ///
    /// Мусор в файле и отсутствие файла дают «auto»: <see cref="SettingsStore.Load"/> не бросает
    /// исключений, а язык всё равно приводится к известному виду ещё раз внутри <see cref="Loc"/>.
    /// </summary>
    private static string SettingsLanguage()
    {
        try
        {
            return new SettingsStore(RunContext.Current.Paths.SettingsFile).Load().Settings.Language;
        }
        catch
        {
            return LanguageDecisions.Auto;
        }
    }

    /// <summary>
    /// Проба единого экземпляра. Ставит замок и слушающее окно (как первая панель), запускает
    /// ВТОРУЮ копию этого же exe в роли <c>--instance-probe</c> и проверяет три вещи: вторая копия
    /// не стала первой, вторая копия достучалась до первой, первая получила просьбу.
    ///
    /// Отдельно и честно: эта проба проверяет СВЯЗЬ, а не показ окна. Показ окна по просьбе —
    /// то же действие, что и по щелчку по значку, и оно проверено самотестом связки.
    /// </summary>
    private static int InstanceSelfTest()
    {
        var report = new List<string>();
        var failed = false;
        var mutexName = $@"Local\DshPanel2.Probe.{Environment.ProcessId}.{DateTime.UtcNow.Ticks}";

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        using var first = new InstanceSignal(mutexName);
        Check("первая панель стала первой", first.TryAcquire());

        var self = Environment.ProcessPath ?? string.Empty;
        if (self.Length == 0)
        {
            report.Add("не удалось определить путь к себе — пробу не провести");
            Print(report, Array.Empty<string>());
            return 2;
        }

        var psi = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Кодировка ОБЯЗАТЕЛЬНА: без неё .NET читает UTF-8 вывод дочернего процесса как ANSI
            // текущей локали, и отчёт приходит кашей (`РІС‚РѕСЂР°СЏ` вместо `вторая`).
            // Ровно та же грабля, что в правилах лаборатории (`..\_lab\README.md`, правило 7):
            // наступил на неё в собственном коде 24.09.2026.
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        psi.ArgumentList.Add("--instance-probe");
        psi.Environment[InstanceSignal.MutexNameVariable] = mutexName;

        var childExited = false;
        var childCode = -1;
        var childOut = string.Empty;

        try
        {
            using var child = Process.Start(psi);
            if (child is null)
            {
                report.Add("не удалось запустить вторую копию");
                failed = true;
            }
            else
            {
                childExited = child.WaitForExit(30_000);
                if (!childExited) child.Kill(entireProcessTree: true);
                else childCode = child.ExitCode;

                childOut = child.StandardOutput.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            report.Add($"ОШИБКА запуска второй копии: {ex.GetType().Name} — {ex.Message}");
            failed = true;
        }

        Check("вторая копия завершилась", childExited);
        Check("вторая копия отчиталась успехом", childCode == 0);

        // Просьба отправлена дочерним процессом — забираем её из своей очереди.
        var received = first.PumpUntilShowRequested(TimeSpan.FromSeconds(3));
        Check("первая панель получила просьбу показать окно", received);

        foreach (var line in childOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            report.Add("вторая копия| " + line.Trim());

        Print(report, Array.Empty<string>());
        Console.WriteLine(failed ? "INSTANCE ПРОВАЛ" : "INSTANCE УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>Роль второй копии в пробе единого экземпляра. Печатает то, что удалось выяснить.</summary>
    private static int InstanceProbe()
    {
        var name = Environment.GetEnvironmentVariable(InstanceSignal.MutexNameVariable);
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("INSTANCE PROBE ПРОВАЛ: не передано имя замка");
            return 2;
        }

        using var signal = new InstanceSignal(name);

        if (signal.TryAcquire())
        {
            Console.WriteLine("INSTANCE PROBE ПРОВАЛ: вторая копия стала первой — замок не работает");
            return 1;
        }

        Console.WriteLine("вторая копия не стала первой = True");

        if (!signal.RequestShowPanel())
        {
            Console.WriteLine("INSTANCE PROBE ПРОВАЛ: первая панель не найдена, просьба не доставлена");
            return 1;
        }

        Console.WriteLine("просьба доставлена первой панели = True");
        return 0;
    }

    private static void Print(List<string> report, IReadOnlyList<string> log)
    {
        foreach (var line in report) Console.WriteLine("INSTANCE| " + line);
        foreach (var line in log) Console.WriteLine("ЖУРНАЛ| " + line);
    }

    /// <summary>
    /// Проба изоляции на собранном exe. Отвечает на один вопрос: все ли пути прогона лежат
    /// под его корнем.
    ///
    /// Проверка идёт по СПИСКУ путей (<see cref="AppPaths.AllPaths"/>), а не по запомненным
    /// свойствам: новое свойство, забытое здесь, — это ровно тот способ, которым изоляция
    /// в v1 и обходилась (тогда проверка подменила данные и состояние, но не домашний
    /// каталог движка, и показала в журнале настоящий баланс владельца).
    ///
    /// Каждая строка не просто печатается, а влияет на исход. Без этого «успех» означал бы
    /// лишь «исключений не было».
    /// </summary>
    private static int IsolationSelfTest()
    {
        var context = RunContext.Current;
        if (!context.IsIsolated)
        {
            Console.WriteLine("ИЗОЛЯЦИЯ ПРОВАЛ: проба запущена без корня — проверять нечего.");
            return 2;
        }

        var report = new List<string>();
        var failed = false;

        var root = Path.TrimEndingDirectorySeparator(context.Paths.Root);
        var prefix = root + Path.DirectorySeparatorChar;
        report.Add($"корень = {root}");
        report.Add($"признак изоляции по фактам: {string.Join(", ", context.IsolationFacts)}");

        foreach (var path in context.Paths.AllPaths)
        {
            var inside = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(path), root, StringComparison.OrdinalIgnoreCase);

            if (!inside) failed = true;
            report.Add($"{(inside ? "внутри" : "ВОН!  ")} {path}");
        }

        // Пишем ровно туда, куда обязаны, и это тоже часть проверки: каталог создаётся,
        // строка в журнал ложится. Увёл бы путь наружу — упало бы здесь, а не на словах.
        try
        {
            Directory.CreateDirectory(context.Paths.StateDir);
            File.AppendAllText(
                context.Paths.LogFile,
                $"проба изоляции {DateTimeOffset.Now:O}{Environment.NewLine}");
            report.Add("каталог состояния и журнал созданы");
        }
        catch (Exception ex)
        {
            failed = true;
            report.Add($"ОШИБКА записи: {ex.GetType().Name} — {ex.Message}");
        }

        foreach (var line in report) Console.WriteLine("ИЗОЛЯЦИЯ| " + line);
        Console.WriteLine(failed ? "ИЗОЛЯЦИЯ ПРОВАЛ" : "ИЗОЛЯЦИЯ УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Платформа поднимается ЯВНО (Win32 + Skia), потому что мы намеренно взяли
    /// Avalonia.Win32 и Avalonia.Skia вместо мета-пакета Avalonia.Desktop.
    /// Автодетект UsePlatformDetect живёт в Avalonia.Desktop и тянет за собой
    /// X11, macOS-бэкенд и натив Linux/macOS/WebAssembly — нам это не нужно.
    ///
    /// ВАЖНО: без Avalonia.Desktop формирование текста надо включать самому —
    /// UseHarfBuzz(). Без него сборка проходит, headless рисует, а на настоящем
    /// рабочем столе приложение падает с «No text shaping system configured».
    /// Нашёл самотест трея 23.09.2026.
    /// </summary>
    public static AppBuilder BuildApp() =>
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont();

    private static int Shot(string path, string kind)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // Снимаем ТО ЖЕ окно, которое видит человек, вместе с настоящим контроллером сервера:
        // он ещё ничего не запускал, поэтому кадр показывает исходное состояние панели.
        // Сервер при съёмке не поднимается — контроллер трогает пути только при запуске.
        // Журнал подменён пустышкой — съёмка не пишет владельцу.
        var paths = RunContext.Current.Paths;

        // Прогон съёмки — это ПРОГОН ПРОВЕРКИ: права занять порт владельца у него нет
        // (`mayOccupyOwnerPort: false`), и это не формальность: с настоящим правом кадр снимался бы
        // на 3080, то есть на канале сессии агента. Файлы состояния сервера не передаются вовсе —
        // источники по умолчанию молчат, и в кадр не может попасть ни ссылка входа, ни чужой сервер.
        var server = new ServerController(
            paths.DshHome, () => paths.DataDir, _ => { }, mayOccupyOwnerPort: false);

        // Автозапуск на кадре — настоящий, но хранилище «только на чтение»: снимок показывает
        // ту запись, которую видит человек, и не меняет её ни при каких обстоятельствах.
        // В изолированном прогоне это файл под его корнем, до реестра пути нет вовсе.
        var autostart = new AutostartController(
            AutostartStores.For(RunContext.Current, humanLaunch: false),
            AutostartStores.SelfExe(),
            _ => { });

        Window window;

        // БАЛАНС ДЛЯ КАДРОВ — одна дверь на два окна (главное и «Пики и тарифы»): оба показывают
        // окна пика и состояние тарифа, и вторая такая же сборка контроллера разошлась бы с первой.
        //
        // Съёмка идёт ПРОГОНОМ ПРОВЕРКИ: права читать ключ у неё нет (`allowed: false`), поэтому
        // в кадр не может попасть баланс владельца, а сама съёмка не ходит в сеть с его ключом;
        // окна пика при этом видны целиком — они считаются из профиля агента и ключа не требуют.
        // Ни одного файла состояния сервера сюда не передаётся: ссылке входа в кадре места нет.
        BalanceController ShotBalance() => new(
            new HttpBalanceClient(),
            paths.CredentialsPath,
            () => PanelSettings.Default,
            allowed: false,
            clock: () => DateTimeOffset.Now,
            dispatch: action => action(),
            log: _ => { },
            notify: (_, _, _) => { });

        // ЦЕНЫ ДЛЯ КАДРОВ — та же дверь, что у баланса, и по той же причине: съёмка идёт
        // ПРОГОНОМ ПРОВЕРКИ (`allowed: false`), поэтому в кадр не может попасть ничего, что
        // панель прочитала бы из сети, а сама съёмка в сеть не ходит вовсе. Таблица «Стоимость»
        // на кадре показывает, что панель говорит человеку, когда цен нет, — и говорит словами.
        PricingController ShotPricing() => new(
            new HttpPricingClient(),
            () => PanelSettings.Default,
            () => Loc.Language,
            allowed: false,
            clock: () => DateTimeOffset.Now,
            dispatch: action => action(),
            log: _ => { },
            rememberCheckedAt: _ => { },
            rememberWindows: _ => { },
            rememberLast: _ => { },

            // Истории цен в кадре нет и быть не может: это прогон проверки, и файл состояния
            // ему не выдан вовсе (`PricingHistoryFiles.None`) — окно истории покажет пустой список.
            historyFiles: PricingHistoryFiles.None,
            notify: (_, _, _) => { });

        // Неизвестное имя окна — отказ, а не «снимем главное»: в скриптах съёмки опечатка иначе
        // положила бы в README не тот кадр, и заметить это было бы некому.
        //
        // ⚠️ `settings-balance` — ВТОРОЙ кадр окна настроек, и он появился 27.09.2026 вместе
        // с двумя таблицами пиков: раздел «Баланс и тариф» показывает их ОБЕ, а `settings` снимает
        // «Резервное копирование» (там видна полоса прокрутки и ширина колонки разделов).
        // Одним кадром два разных обещания не показать, а имена окон — не ключи запуска:
        // поведение панели они не меняют.
        //
        // ⚠️ Имена берутся из ОДНОГО места — `StartModes.ShotWindows`, — и оттуда же их берёт
        // строка «известные ключи». Прежде здесь стоял свой список из пяти имён, а в описании
        // ключа — свой из трёх: они разошлись, и описание молчало про два рабочих окна
        // (нашёл разведчик 28.09.2026). Сам отказ остаётся громким, кодом 2.
        if (kind.Length > 0 && !StartModes.KnowsShotWindow(kind))
        {
            Console.WriteLine(
                $"SHOT ПРОВАЛ: неизвестное окно «{kind}». Известные: {string.Join(", ", StartModes.ShotWindows)}.");
            return 2;
        }

        if (kind is StartModes.ShotSettings or StartModes.ShotSettingsBalance or StartModes.ShotSettingsUpdate or StartModes.ShotSettingsServer)
        {
            // Настройки на кадре — с ВЫКЛЮЧЕННЫМ чтением: `allowed: false` значит «показаны
            // умолчания». Снимок уезжает в README, и рабочая папка владельца в нём была бы
            // личными данными. Тема при этом НЕ применяется: съёмка не меняет вид приложения.
            var settingsControl = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: false,
                canReadOwnerEnvironment: false,
                locateEngine: () => DshEngine.Locate(),
                server: () => server,
                applyTheme: _ => { },
                log: _ => { });

            var settingsWindow = new SettingsWindow();
            settingsWindow.Attach(settingsControl, autostart, balance: null, pricing: ShotPricing());

            // Кадр `settings` снимает САМЫЙ ДЛИННЫЙ раздел («Резервное копирование»), а не первый:
            // только на нём видно полосу прокрутки и то, что она не наезжает на поля
            // (жалоба владельца 27.09.2026), и что подпись раздела влезает в колонку.
            // `settings-balance` — раздел «Баланс и тариф», `settings-update` — раздел
            // «Обновление» (он появился 28.09.2026, и кадр нужен и проверяющему, и README).
            settingsWindow.ShowSection(kind switch
            {
                StartModes.ShotSettingsBalance => SettingsWindow.BalanceSection,
                StartModes.ShotSettingsUpdate => SettingsWindow.UpdateSection,
                StartModes.ShotSettingsServer => SettingsWindow.ServerSection,
                _ => SettingsWindow.BackupSection,
            });

            window = settingsWindow;
        }
        else if (kind == StartModes.ShotBackup)
        {
            // Окно копий на кадре — как прогон проверки: БЕЗ права снимать копии. Поэтому список
            // пуст и папка владельца не читается вовсе (`BackupController.Refresh` про это знает):
            // кадр уезжает в README, и настоящие копии человека были бы в нём личными данными.
            // Панель наката на кадре не видна намеренно — она появляется только после выбора копии,
            // а в прогоне проверки выбирать нечего; её проверяет тест окна, а не кадр.
            var backupControl = new BackupController(
                paths,
                () => PanelSettings.Default,
                allowed: false,
                locateEngine: () => DshEngine.Locate(),
                clock: () => DateTimeOffset.Now,
                log: _ => { });

            var backupWindow = new BackupWindow();
            backupWindow.Attach(backupControl);
            window = backupWindow;
        }
        else if (kind == StartModes.ShotAbout)
        {
            // Окно «О программе» на кадре — как его видит человек: строки текущего языка
            // (язык задан ключом `--lang` или переменной), ссылки продукта и версия панели.
            // Ни ключа владельца, ни его путей этому окну не нужно вовсе, поэтому в кадр
            // личное попасть не может — и это проверяется не словами, а чтением кадра.
            //
            // Ссылки на кадре НЕ нажимаются: съёмка ничего не открывает (браузер владельца —
            // это окно на его рабочем столе).
            //
            // ⚠️ А дверь в отчёт о проблеме кадру ДАЁТСЯ, и это не противоречие: без двери блок
            // в окне скрыт (ShowIssueBlock), и кадр показывал бы окно БЕДНЕЕ того, какое видит
            // человек, — из него пропало бы то, ради чего окно менялось 29.09.2026. Дверь пустая:
            // нажатие в кадре ничего не открывает, и открывать нечего — прогон проверки владельцу
            // не показывает ничего (красная линия 8).
            var about = new AboutWindow();
            about.AttachIssueDoor(() => { });
            window = about;
        }
        else if (kind == StartModes.ShotPeaks)
        {
            // Окно «Пики и тарифы» на кадре — то же окно, что открывает человек кнопкой
            // в карточке баланса, и связано оно с балансом ТАК ЖЕ, как в прогоне проверки:
            // без права читать ключ. Окна пика считаются из профиля агента и ключа не требуют,
            // поэтому в кадре видна вся таблица — а баланса владельца в нём нет.
            var peaks = new PeakWindow();
            peaks.Attach(ShotBalance(), ShotPricing());
            window = peaks;
        }
        else if (kind == StartModes.ShotUpdate)
        {
            // ОКНО ОБНОВЛЕНИЯ на кадре. Живой проверки здесь нет и быть не может (съёмка — прогон
            // проверки, в сеть она не ходит), а показать кадр обязан СТРУКТУРУ заметок — заголовки,
            // списки и отступы, ради которых разбор и делался. Поэтому состояние — ОБРАЗЕЦ
            // (`ShotUpdateControl`), и он же честно называет себя примером. Движка установки
            // у кадра НЕТ: кнопка подготовки в прогоне проверки недоступна и говорит об этом словами.
            var updateWindow = new UpdateWindow();
            updateWindow.Attach(ShotUpdateControl.For(Loc.Language));
            window = updateWindow;
        }
        else if (kind == StartModes.ShotIssue)
        {
            // ОКНО «СООБЩИТЬ О ПРОБЛЕМЕ» на кадре. Отчёт собирается из ПУСТЫХ фактов: съёмка —
            // прогон проверки, и ни журнала владельца, ни состояния его сервера она не читает.
            // Кадр показывает то, ради чего окно и делалось: человек видит РОВНО тот текст,
            // который уйдёт, и рядом — строку о том, чего в нём нет.
            window = new IssueWindow();
        }
        else if (kind == StartModes.ShotPricingHistory)
        {
            // ОКНО «ИСТОРИЯ ЦЕН» на кадре. Список записей будет ПУСТ, и это честно: съёмка —
            // прогон проверки, файл истории ему не выдан вовсе (`PricingHistoryFiles.None`
            // в `ShotPricing`), а читать файл состояния владельца съёмке запрещено — кадр уезжает
            // в README.
            //
            // ⚠️ Список записей на кадре собрать НЕЛЬЗЯ, и это названо, а не спрятано: пустая
            // история — не «окно не работает», а «показывать нечего». Зато кадр показывает всё
            // остальное: заголовок, колонки таблицы, их ширину и слова о том, с чем идёт сравнение.
            // Вид СПИСКА записей в кадр не попадает — его проверяет безэкранная проверка окна,
            // а глазами увидит владелец, когда история у него появится.
            var pricingHistory = new PricingHistoryWindow();
            pricingHistory.Attach(ShotPricing());
            window = pricingHistory;
        }
        else
        {
            var main = new MainWindow();
            main.Attach(server);

            // Баланс на кадре — как в прогоне проверки: БЕЗ права читать ключ. Иначе снимок
            // показывал бы баланс владельца (он уезжает в README), а съёмка ещё и ходила бы
            // в сеть с его ключом. Так кадр честно говорит «прогон проверки», зато окна пика
            // видны целиком: они считаются из профиля агента и ключа не требуют.
            main.AttachBalance(ShotBalance());

            window = main;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var w = (int)Math.Ceiling(window.ClientSize.Width);
        var h = (int)Math.Ceiling(window.ClientSize.Height);
        if (w <= 0 || h <= 0)
        {
            Console.WriteLine($"SHOT провал: пустой размер окна {w}x{h}");
            return 2;
        }

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);

        var stride = w * 4;
        var buf = new byte[stride * h];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
        finally { handle.Free(); }

        rtb.Save(path, new PngBitmapEncoderOptions());

        // «Кадр не пустой» проверяем по пикселям, а не на глаз: непрозрачные пиксели
        // и число различных цветов. Пустой кадр — это один цвет и/или ноль непрозрачных.
        long opaque = 0;
        var colors = new HashSet<int>();
        for (var i = 0; i + 3 < buf.Length; i += 4)
        {
            if (buf[i + 3] != 0) opaque++;
            colors.Add((buf[i] << 16) | (buf[i + 1] << 8) | buf[i + 2]);
        }

        Console.WriteLine($"SHOT ок {w}x{h} непрозрачных={opaque} цветов={colors.Count} файл={path}");

        // Пустой кадр — это ПРОВАЛ (1), а не «не завершилось»: код 3 отдан сторожу проверок
        // и означает ровно одно — проверка не уложилась в отведённое время. Прежде здесь стояло 3,
        // и один код значил две разные вещи. Нашёл независимый аудит документов 24.09.2026.
        return opaque > 0 && colors.Count > 1 ? 0 : 1;
    }

    /// <summary>
    /// Самотест трея. Отвечает на четыре вопроса, которые иначе проверяются только руками:
    ///   1) оболочка приняла значок;
    ///   2) насос сообщений Avalonia раздаёт сообщения нашего ЧУЖОГО ей окна
    ///      (без этого значок был бы мёртвым: щелчки и меню не работали бы);
    ///   3) уведомление показывается без исключения;
    ///   4) **меню значка открывается и в нём НАШИ пункты** — Windows отдаёт их число,
    ///      поэтому «показали меню» здесь не обещание, а измерение.
    /// Сам снимает значок и закрывает меню — на рабочем столе владельца ничего не остаётся.
    ///
    /// ⚠️ Почему меню появилось в самотесте только 27.09.2026. Значок Windows прячет
    /// в переполнение «^», а всплывающее меню закрывается от любого промаха, поэтому снять его
    /// глазами ни разу не удалось: о составе меню знали ровно то, что он «проверен по коду».
    /// Теперь меню открывает сам самотест — тем же сообщением, каким его открывает правый
    /// щелчок человека, — держит его <see cref="MenuSelfTestHoldMs"/> и закрывает САМ.
    /// </summary>
    private static int TraySelfTest(int seconds)
    {
        ProgramOptions.SkipDefaultUi = true;   // ни окна, ни связки: только значок
        var report = new List<string>();
        TrayIconHost? tray = null;
        var failed = false;
        var expectedItems = 0;

        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(Array.Empty<string>(), lifetime =>
            {
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                try
                {
                    var paths = RunContext.Current.Paths;
                    Action<string> quiet = _ => { };

                    tray = new TrayIconHost("DSH Panel — самотест трея");

                    // Значок — НАШ, из сборки (решение владельца 26.09.2026). Строка ниже
                    // печатает то, что решил сам код, поэтому «наш или запасной» проверяется
                    // ещё и по ДЕСКРИПТОРУ: подмена одного другим обязана ронять самотест,
                    // иначе «зелено» здесь означало бы только «исключений не было».
                    report.Add(tray.SetOwnIcon());

                    var ownIcon = !TrayIconSource.IsSystemIcon(tray.IconHandle);
                    report.Add($"значок трея наш, а не системный запасной = {ownIcon}");
                    if (!ownIcon) failed = true;

                    // Состояние берётся у НАСТОЯЩЕГО контроллера, как у панели: ключ владельца
                    // не читается, порт владельца не занимается.
                    var server = new ServerController(
                        paths.DshHome, () => paths.DataDir, quiet, mayOccupyOwnerPort: false);

                    // ОГОНЁК СОСТОЯНИЯ (решение владельца 27.09.2026). Здесь он ставится ЖИВЬЁМ:
                    // человек видит значок на своём рабочем столе, и «сервер не работает» обязано
                    // читаться на нём цветом, а не обещаться в отчёте. Тон берётся ТОЙ ЖЕ чистой
                    // функцией, что у панели, — от состояния настоящего контроллера.
                    //
                    // Проверяются две вещи, и обе могут упасть: значок СТАЛ ДРУГИМ (иначе
                    // «поставили» значило бы только «исключения не было») и повтор того же тона
                    // значок НЕ тронул (состояние перечитывается раз в секунду, и «каждую секунду
                    // новая иконка» выглядело бы миганием значка).
                    var tone = TrayStatus.IconTone(connected: true, presence: server.State.Presence);

                    var beforeDot = tray.IconHandle;
                    var dotLine = tray.SetIconTone(tone);
                    var afterDot = tray.IconHandle;
                    var dotOn = afterDot != beforeDot;
                    report.Add($"огонёк состояния ({tone}) поставлен = {dotOn}{(dotLine.Length > 0 ? $" ({dotLine})" : string.Empty)}");
                    if (!dotOn) failed = true;

                    var again = tray.SetIconTone(tone);
                    var sameToneQuiet = tray.IconHandle == afterDot && again.Length == 0;
                    report.Add($"повтор того же тона значок не тронул = {sameToneQuiet}");
                    if (!sameToneQuiet) failed = true;

                    // МЕНЮ — НАСТОЯЩЕЕ: тот же сборщик, что у панели, и то же состояние,
                    // какое панель показывает в прогоне проверки (ключ владельца не читается,
                    // порт владельца не занимается). Ничего не выдумываем: если строка состояния
                    // сломается, это будет видно и в отчёте, и на кадре.
                    var balance = new BalanceController(
                        new HttpBalanceClient(),
                        paths.CredentialsPath,
                        () => PanelSettings.Default,
                        allowed: false,
                        clock: () => DateTimeOffset.Now,
                        dispatch: action => action(),
                        log: quiet,
                        notify: (_, _, _) => { });

                    var menu = PanelMenu.Build(
                        status: () => TrayStatus.Build(
                            connected: true,
                            presence: server.State.Presence,
                            owner: server.Owner,
                            port: server.State.Port,
                            agentTitle: balance.Agent.Title,
                            balanceSummary: string.Empty,
                            inPeak: balance.Peak.InPeak,

                            // Строка обновления в самотесте берётся у ОБРАЗЦА — того же, что
                            // на кадре окна обновления: живой проверки в прогоне проверки нет,
                            // а состав меню обязан быть тем, что человек видит, вместе с тоном.
                            update: ShotUpdateControl.For(Loc.Language).TrayLine),
                        show: () => { },
                        openAgent: () => { },
                        settings: () => { },
                        about: () => { },
                        checkUpdate: () => { },
                        updateWindow: () => { },
                        exit: () => { });

                    expectedItems = menu.Items.Count;

                    // Состав печатается ДО открытия меню: отчёт обязан называть то, что человек
                    // увидел бы в меню, а не только «сколько-то пунктов». Тон печатается рядом
                    // с текстом: цвет — такое же обещание, как слова, и прогон обязан называть
                    // и его (иначе «строка на месте» ничего не говорит о том, КАК она выглядит).
                    foreach (var item in menu.Items)
                    {
                        report.Add(item.IsSeparator
                            ? "MENU| ---"
                            : $"MENU| {item.Text}{(item.Tone is { } itemTone ? $" [{itemTone}]" : string.Empty)}");
                    }

                    // Меню отдаётся ПРОВАЙДЕРОМ — тем же путём, что у панели: так самотест
                    // проверяет и сборку меню, и механизм «собирается на каждый показ».
                    tray.SetMenuProvider(() => menu);

                    tray.Show();
                    var added = tray.IsAdded;
                    report.Add($"значок поставлен = {added}");
                    if (!added) failed = true;

                    tray.PostSelfTestMessage();
                    report.Add("самотест послан");

                    // Шарик берёт СВОИ строки из словаря, как и всё остальное, что видит человек:
                    // до 27.09.2026 здесь стоял литерал, и шарик проверки был русским на любом
                    // языке панели (нашёл владелец: меню снимали на трёх языках, а шарик — нет).
                    // Текст печатается в отчёт: тогда язык шарика проверяется ПРОГОНОМ, а не
                    // глазами — сам шарик Windows рисует по-своему, и поймать его кадром нельзя.
                    tray.ShowNotification(PanelStrings.TrayToolTip, PanelStrings.TraySelfTestBalloonText);
                    report.Add($"уведомление показано без исключения: {PanelStrings.TraySelfTestBalloonText}");

                    tray.PostMenuForSelfTest(MenuSelfTestHoldMs);
                    report.Add("меню открыто самотестом");
                }
                catch (Exception ex)
                {
                    failed = true;
                    report.Add($"ОШИБКА: {ex.GetType().Name} — {ex.Message}");
                }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();

                    // Каждая строка ниже не просто печатается, а ВЛИЯЕТ на исход.
                    // Иначе «SELFTEST УСПЕХ» означал бы лишь «исключений не было»,
                    // а это ровно тот случай, когда зелёный тест ничего не стоит.
                    var pumpOk = tray?.SelfTestMessageReceived == true;
                    report.Add($"насос сообщений раздаёт наше окно = {pumpOk}");
                    if (!pumpOk) failed = true;

                    // Число пунктов приходит ОТ WINDOWS, а не из нашей модели: -1 значит
                    // «меню не закрылось само» (то есть оно либо не открылось, либо висит).
                    var items = tray?.SelfTestMenuItems ?? -1;
                    var menuOk = items == expectedItems && expectedItems > 0;
                    report.Add($"пунктов в настоящем меню = {items}, ожидалось {expectedItems}");
                    if (!menuOk) failed = true;

                    // Отступление рисования называется словами и РУШИТ прогон: строки состояния
                    // обязаны быть цветными, и «меню открылось» этого не доказывает.
                    var notice = tray?.LastMenuNotice ?? string.Empty;
                    var drawn = notice.Length == 0;
                    report.Add($"строки состояния нарисованы своими руками = {drawn}{(drawn ? string.Empty : $" ({notice})")}");
                    if (!drawn) failed = true;

                    tray?.Dispose();
                    var removed = tray?.IsAdded == false;
                    report.Add($"значок снят = {removed}");
                    if (!removed) failed = true;

                    foreach (var line in report) Console.WriteLine("SELFTEST| " + line);
                    lifetime.Shutdown();
                };
                timer.Start();
            });

        Console.WriteLine(failed ? "SELFTEST ПРОВАЛ" : "SELFTEST УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Сколько самотест трея держит меню открытым. Две секунды — с запасом: за это время
    /// кадр меню успевает снять и человек (глазами), и скрипт съёмки, а на рабочем столе
    /// владельца меню не задерживается.
    /// </summary>
    private const int MenuSelfTestHoldMs = 2000;

    /// <summary>
    /// Идёт ли прогон в сессии 0 — неинтерактивной службе. Там нет рабочего стола человека:
    /// оболочка не ставит значки, окна некому показать. Проверки, которым нужен рабочий стол,
    /// обязаны сказать «неприменимо», а не «провал»: иначе отчёт врёт о вине панели.
    /// Измерено на чистой ВМ 24.09.2026.
    /// </summary>
    private static bool SessionZero()
    {
        try { return Process.GetCurrentProcess().SessionId == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Самотест связки «значок ↔ окно». Всю проверку ведёт <see cref="PanelShellSelfTest"/>,
    /// а здесь только выбор: панель строится ОБЫЧНЫМ путём (<see cref="App.StartPanel"/>),
    /// и проверка подключается к ней. Строить окно тут нельзя — этот колбэк вызывается
    /// до подъёма платформы Avalonia, и первое же окно падает. Поймано самотестом 24.09.2026.
    /// </summary>
    private static int ShellSelfTest(int seconds)
    {
        var selfTest = new PanelShellSelfTest(seconds);
        ProgramOptions.ShellSelfTest = selfTest;

        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(Array.Empty<string>(), _ => { });

        return selfTest.Verdict();
    }

    /// <summary>
    /// ОБРАЗЕЦ СОСТОЯНИЯ ДЛЯ КАДРА ОКНА ОБНОВЛЕНИЯ — и только для него.
    ///
    /// **Зачем образец, а не живой контроллер.** Съёмка — прогон проверки: в сеть она не ходит
    /// (`allowed: false`), поэтому живой контроллер показал бы «проверка недоступна в этом прогоне»
    /// и пустые заметки — то есть кадр не показал бы НИЧЕГО из того, ради чего окно сделано.
    /// А показать он обязан ровно одно: **структуру заметок** — заголовки, списки, абзацы
    /// и отступы, потому что на «сплошной неструктурированный текст» владелец жаловался дважды
    /// (`docs\DESIGN.md`, пункты 32–33).
    ///
    /// **Почему это не обман.** В кадре стоит вымышленный выпуск вымышленной версии, и он нарочно
    /// отличается от установленной панели: кадр показывает, как окно выглядит, когда новое есть.
    /// Ни одного личного данного здесь нет — ни ключа, ни пути владельца, ни баланса.
    ///
    /// ⚠️ `Check()` ничего не делает: нажимать в кадре некому, а живой запрос из съёмки был бы
    /// сетью от имени владельца — ровно тем, что красная линия 4 запрещает прогону проверки.
    /// </summary>
    private sealed class ShotUpdateControl : IUpdateControl
    {
        /// <summary>
        /// Тело выпуска, каким его публикует сборка: три языковых блока, и в каждом — заголовок,
        /// список и короткий абзац. Именно так проверяется, что разбор и показ заметок работают
        /// на всех трёх языках панели.
        /// </summary>
        private const string Body =
            "## RU\n\n### Что изменилось\n\n- Панель показывает ход обновления: этап и проценты\n" +
            "- Замена файлов запускается отдельной кнопкой и только по щелчку человека\n\n" +
            "Если запуск не удался, панель скажет об этом словами, а не промолчит.\n\n" +
            "## EN\n\n### What changed\n\n- The panel shows the update progress: stage and percent\n" +
            "- File replacement is started by a separate button and only by a human click\n\n" +
            "If the launch fails, the panel says so in words instead of staying silent.\n\n" +
            "## ZH\n\n### 变更内容\n\n- 面板显示更新的进度：阶段与百分比\n" +
            "- 替换文件由单独的按钮启动，且只在用户点击时执行\n\n" +
            "如果启动失败，面板会用文字说明，而不是保持沉默。\n";

        private ShotUpdateControl(string language, DateTimeOffset at)
        {
            Current = "2.0.0";

            Result = new UpdateRelease(
                true,
                string.Empty,
                "v2.1.0",
                ProductLinks.ReleasesPageUrl,
                "2026-09-27T10:00:00Z",
                UpdateDecisions.PickNotes(Body, language),
                Body,
                at);
        }

        /// <summary>Образец на языке панели: заметки разбираются на том языке, что выбран ключом.</summary>
        public static ShotUpdateControl For(string language) =>
            new(language, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

        public UpdateRelease Result { get; }

        public bool Busy => false;

        /// <summary>
        /// «Права нет, и это прогон проверки» — но окно на кадре показывает состояние проверки,
        /// а не запрет: право здесь ровно то, что видно кнопке «Проверить сейчас», и в кадре она
        /// показана как доступная (человек в жизни её видит именно такой).
        /// </summary>
        public bool Allowed => true;

        public string Current { get; }

        public bool Newer => UpdateDecisions.IsNewer(Result.Latest, Current);

        public bool Skipped => false;

        public string SourceText => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.UpdateCheckedFormat,
            PricingDecisions.CheckedText(PricingDecisions.Stamp(Result.CheckedAt)));

        public string StatusText => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.UpdateAvailableFormat,
            Result.Latest);

        public TrayStatusLine TrayLine => new(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateAvailableFormat,
                Result.Latest),
            TrayTone.Warning);

        /// <summary>
        /// События есть по уговору <see cref="IUpdateControl"/>, но поднимать их в кадре нечем:
        /// подписка принимается и молчит (явные <c>add</c>/<c>remove</c> — поле события, которое
        /// никто не поднимает, компилятор справедливо назвал бы предупреждением).
        /// </summary>
        public event Action<string>? Announce { add { } remove { } }

        public event Action? Changed { add { } remove { } }

        /// <summary>Проверки на кадре не бывает: сети в прогоне проверки нет вовсе.</summary>
        public void Check()
        {
        }
    }
}
