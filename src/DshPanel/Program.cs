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

        // КЛЮЧИ СЪЁМКИ СНИМАЮТСЯ ДО РАЗБОРА РЕЖИМОВ: `--theme dark` и `--state focus:SaveButton`.
        // Иначе разбор счёл бы их опечаткой и отказал бы кодом 2 (незнакомый ключ — громкий отказ).
        // ⚠️ Значение снимается только у не-ключа: «--theme --shot кадр.png» не должно съесть съёмку
        // и открыть окно владельцу — это то же правило, что у языка (см. `ShotDecisions`).
        var shotTheme = ShotDecisions.Value(rest, ShotDecisions.ThemeSwitch);
        var shotState = ShotDecisions.Value(rest, ShotDecisions.StateSwitch);

        rest = ShotDecisions.WithoutSwitches(rest);

        // Съёмка интерфейса без показа окна — для README и для глазной проверки вёрстки.
        // Третий аргумент выбирает окно: нет — главное, «settings» — настройки, «backup» — копии.
        // Неизвестное имя окна — громкий отказ, а не «снимем главное»: опечатка в скрипте съёмки
        // иначе положила бы в README не тот кадр, и заметить это было бы некому.
        if (rest.Length >= 2 && rest[0] == "--shot")
        {
            return Shot(
                rest[1],
                rest.Length >= 3 ? rest[2] : string.Empty,
                shotTheme ?? string.Empty,
                shotState ?? string.Empty);
        }

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

        // ЖИВАЯ ПРОБА ВЛАДЕНИЯ ОКНАМИ (u14, 2.4.0): настоящие окна, кнопка панели задач числом,
        // z-порядок, «свернуть/вернуть». Отказывается в изоляции и в сессии 0 по той же причине,
        // что самотест связки: без рабочего стола окон нет, и проверять нечего.
        if (rest.Length >= 1 && rest[0] == "--windows-selftest")
        {
            if (isolated)
            {
                Console.WriteLine(
                    "ИЗОЛЯЦИЯ ПРОВАЛ: живая проба владения показывает настоящие окна на рабочем столе, " +
                    "а изолированный прогон не показывает ничего. Запустите пробу без корня.");
                return 2;
            }

            if (SessionZero())
            {
                Console.WriteLine(
                    "WINDOWS НЕПРИМЕНИМО: сессия 0 — интерактивного рабочего стола нет, окон и панели " +
                    "задач не будет. Запустите пробу в сессии человека.");
                return 2;
            }

            return WindowsSelfTestRun();
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

        // САМОТЕСТ КАЛИТКИ ТЕЛЕФОНА (этап F1.e): поднимает НАСТОЯЩИЙ слушатель на своём свободном
        // порту, сопрягает устройство настоящим файлом пропуска, поднимает НАСТОЯЩИЙ движок под
        // корнем прогона и проходит ЖИВОЕ рукопожатие мультиплексора (кадр `ready` с `clientId`).
        // Без изоляции отказывается сам (PhoneSelfTest): это и данные владельца, и его движок.
        // Порт 43127 не занимается — порт берётся свободным.
        if (rest.Length >= 1 && rest[0] == "--phone-selftest")
        {
            var seconds = (int)PhoneSelfTest.DefaultTimeout.TotalSeconds;
            if (rest.Length >= 2 && !int.TryParse(rest[1], out seconds))
            {
                Console.WriteLine($"КАЛИТКА ПРОВАЛ: длительность «{rest[1]}» — не число");
                return 2;
            }

            var phone = PhoneSelfTest.Run(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 1800)));

            foreach (var line in phone.Report) Console.WriteLine("КАЛИТКА| " + line);

            Console.WriteLine(phone.Title);

            return phone.Code;
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

        // ПРОБА УСТАНОВКИ ДВИЖКА ПО ЗАМКУ (этап E3.1): ставит НАСТОЯЩИЙ движок своим каталогом под
        // корнем прогона — настоящим npm и по встроенному замку, — и прогоняет на нём полную пробу
        // сервера (84 имени, 8 умений). Это единственный путь, которым проверяется, что по нашему
        // замку собирается ТО дерево, которое мерили: замер 10.10.2026 показал, что дайджест первой
        // редакции давал 1 пакет вместо 588. Без изоляции отказывается сама.
        if (rest.Length >= 1 && rest[0] == "--engine-install-selftest")
        {
            // Второй довод «beda» подкладывает беду: установка зовётся заведомо отказывающей
            // дверью, и проверяется ОТКАТ с предупреждением про сессии (шаг 3 плана владельца).
            var planted = rest.Length >= 2
                && string.Equals(rest[1], "beda", StringComparison.OrdinalIgnoreCase);

            return EngineInstallSelfTest.Run(EngineInstallSelfTest.DefaultTimeout, planted);
        }

        // ПРОБА ОБНОВЛЕНИЯ ДВИЖКА НАСТОЯЩИМИ ДВЕРЯМИ (этап E3.2, приёмка): ставит движок по замку,
        // заводит профиль с работой, поднимает свой сервер, проходит ЦЕПОЧКУ обновления (гашение →
        // предохранительная копия дверью 7-Zip → установка по замку → живая проба 84/84 → запись →
        // подъём сервера заново) и проверяет, что осталось на диске. Второй довод «beda» подкладывает
        // беду в место копий: панель обязана отказать, НЕ начинать установку и поднять прежний движок.
        // Без изоляции отказывается сама.
        if (rest.Length >= 1 && rest[0] == "--engine-update-selftest")
        {
            var plantedUpdate = rest.Length >= 2
                && string.Equals(rest[1], "beda", StringComparison.OrdinalIgnoreCase);

            return EngineUpdateSelfTest.Run(EngineUpdateSelfTest.DefaultTimeout, plantedUpdate);
        }

        // ПРОБА ОТКАТА ПО КНОПКЕ НАСТОЯЩИМИ ДВЕРЯМИ (этап E3.3, приёмка): панель держит прежнюю
        // версию на диске, переводит запись на «новую», поднимает на ней сервер — и возвращается
        // НАСТОЯЩИМ путём: проба прежней версии живьём → гашение своего сервера → запись → подъём
        // заново уже на прежней. Второй довод «beda» ломает прежний движок: проба обязана отказать
        // ДО всякой подмены. Без изоляции отказывается сама.
        if (rest.Length >= 1 && rest[0] == "--engine-rollback-selftest")
        {
            var plantedRollback = rest.Length >= 2
                && string.Equals(rest[1], "beda", StringComparison.OrdinalIgnoreCase);

            return EngineRollbackSelfTest.Run(EngineRollbackSelfTest.DefaultTimeout, plantedRollback);
        }

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

    /// <summary>
    /// ПОДСТАВНАЯ ПРОБА ДЛЯ КАДРА С ДВУМЯ НАЙДЕННЫМИ СЕРВЕРАМИ: она отвечает ЗАРАНЕЕ ЗАПИСАННЫМ
    /// и не спрашивает ни одного настоящего процесса.
    ///
    /// Зачем: решение владельца 10.10.2026 — кадр с двумя серверами в приёмке. Настоящая съёмка
    /// живых серверов владельца не показывает вовсе (в кадр не попадает ни его сервер, ни его
    /// процессы), поэтому два сервера здесь **выдуманы**: порты 3080 и 3099, номера процессов
    /// 19804 и 22222. «Наш движок» при этом берётся из **настоящей** папки движка этого прогона
    /// и проверенной версии — так пометка на кадре та же, что человек увидит в жизни.
    /// </summary>
    private static ServerProbe TwoServersProbe(AppPaths paths)
    {
        const int OwnPort = 3080;
        const int ForeignPort = 3099;
        const int OwnPid = 19804;
        const int ForeignPid = 22222;

        var ownBin = OwnEngine.BinPath(paths.EngineDir, EngineRpcSurface.MeasuredOnVersion);

        return new ServerProbe(
            ListenerPid: port => port == OwnPort ? OwnPid : port == ForeignPort ? ForeignPid : 0,
            Listeners: () => new[]
            {
                new PortTable.Listener(OwnPort, OwnPid),
                new PortTable.Listener(ForeignPort, ForeignPid),
            },
            CommandLine: pid => pid == OwnPid
                ? $"\"node.exe\" {ownBin} web --no-open --port {OwnPort}"
                : "\"node.exe\" C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js web --no-open --port " + ForeignPort,
            ProcessName: _ => "node",
            StartTicks: _ => 63_000_000_000_000_000,
            AnswersFingerprint: _ => true);
    }

    /// <summary>
    /// АДРЕС С ЗАМАСКИРОВАННЫМ ПОСЛЕДНИМ ЧИСЛОМ — <c>192.168.0.7</c> → <c>192.168.0.х</c>.
    ///
    /// ⚠️ **Зачем маска, а не настоящий адрес.** Кадр `--shot` уезжает в README, а номер адреса
    /// своей машины — её примета; решение владельца 11.10.2026 дословно: «номера не пишем либо
    /// заменяем последнее число на `х`» (так же маскируются адреса в `docs\PHONE-F0.md`).
    /// Сторож личных данных адреса не видит вовсе, и надеяться на него здесь не на что.
    ///
    /// ⚠️ **И зачем маска, а не пустота.** Пустой список означал бы «подходящего адреса нет»,
    /// и кадр показывал бы раздел в состоянии, которого у владельца, скорее всего, не бывает:
    /// он прочитал бы «телефону показывать нечего» и решил бы, что раздел не работает.
    /// Маска оставляет форму адреса и признак «подсеть on-link» — то есть ровно то, что кадр
    /// и должен показать, — а номера машины в нём нет.
    ///
    /// Не разобрался (чужой вид адреса) — отдаётся маска целиком, а не сам адрес: показать
    /// незнакомое как есть значило бы оставить в кадре то, ради чего маска и заведена.
    /// </summary>
    private static string MaskAddress(string address)
    {
        var parts = (address ?? string.Empty).Trim().Split('.');

        return parts.Length == 4
            ? parts[0] + "." + parts[1] + "." + parts[2] + ".х"
            : "х.х.х.х";
    }

    /// <summary>
    /// ЦЕНЫ ДЛЯ КАДРОВ — та же дверь, что у баланса, и по той же причине: съёмка идёт
    /// ПРОГОНОМ ПРОВЕРКИ (`allowed: false`), поэтому в кадр не может попасть ничего, что
    /// панель прочитала бы из сети, а сама съёмка в сеть не ходит вовсе. Таблица «Стоимость»
    /// на кадре показывает, что панель говорит человеку, когда цен нет, — и говорит словами.
    ///
    /// ⚠️ Отдельным членом, а не местной функцией `Shot`: тем же контроллером пользуется
    /// окно настроек на кадре (<see cref="BuildShotSettingsWindow"/>), и вторая такая же
    /// сборка разошлась бы с первой.
    /// </summary>
    private static PricingController ShotPricing(AppPaths paths) => new(
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

    /// <summary>
    /// ЦЕНЫ ДЛЯ КАДРА С ОБРАЗЦОВОЙ ИСТОРИЕЙ — та же дверь, что <see cref="ShotPricing"/>, но
    /// история цен приходит ЛИТЕРАЛОМ (<see cref="ShotSamples.PricingHistoryText"/>), а не пустотой.
    ///
    /// **Зачем.** До 12.10.2026 кадр «История цен» показывал одни прочерки: файла истории прогону
    /// проверки не выдают вовсе, и сравнивать было не с чем. По такому кадру нельзя судить
    /// ни о списке, ни о таблице изменений — а он уезжает в публичную витрину.
    ///
    /// ⚠️ **Файл владельца не читается:** двери истории — чтение из переменной прогона и запись
    /// в неё же. Ни одного пути, ни одного файла: `Persists: true` здесь значит «запись удалась
    /// в память», и для кадра этого довольно — кадр живёт один прогон.
    ///
    /// ⚠️ **`allowed: true` здесь и НЕ значит «пойти в сеть».** Право у контроллера открывает
    /// ДВЕ вещи: читать память (`SeededHistory` — из-под неё и берётся образцовая история) и ходить
    /// в сеть ПО РАСПИСАНИЮ или по просьбе человека. Ни часов, ни нажатия у кадра нет: контроллер
    /// собирается и его `History` читается сразу — запроса не делает никто. Сеть кадра не касается.
    /// </summary>
    private static PricingController ShotPricingWithHistory()
    {
        var store = ShotSamples.PricingHistoryText();

        return new PricingController(
            new HttpPricingClient(),
            () => PanelSettings.Default,
            () => Loc.Language,
            allowed: true,
            clock: () => DateTimeOffset.Now,
            dispatch: action => action(),
            log: _ => { },
            rememberCheckedAt: _ => { },
            rememberWindows: _ => { },
            rememberLast: _ => { },
            historyFiles: new PricingHistoryFiles(Read: () => store, Write: text => store = text),
            notify: (_, _, _) => { });
    }

    /// <summary>
    /// ОКНО НАСТРОЕК ДЛЯ КАДРА — СБОРКА СЪЁМКИ, НАЗВАННАЯ ОТДЕЛЬНО, ЧТОБЫ ЕЁ МОЖНО БЫЛО ПРОВЕРИТЬ
    /// ЦЕЛИКОМ (этап F1, хвост 3).
    ///
    /// **Зачем вынесено.** Прежде эта сборка жила прямо в <see cref="Shot"/>, и проверка не могла
    /// до неё дотянуться: она собирала окно САМА, со своей подставной дверью, — и **дефект обвязки
    /// съёмки был ей не виден вовсе**. Так и вышло: раздел «Телефон» на кадре получал `phone: null`
    /// (двери у прогона проверки не было), окно честно молчало про код, а кадр уезжал в README
    /// с подписью «код здесь образец», которого в нём не было. Нашёл это владелец, а не проверка
    /// (разбор — `docs\HISTORY.md`, «третий хвост»). Теперь сборка кадра — ОДИН член, и проверка
    /// (<c>PhoneShotFrameTests</c>) зовёт ровно его: пропадёт дверь здесь — проверка упадёт.
    ///
    /// ⚠️ **Настройки на кадре — с ВЫКЛЮЧЕННЫМ чтением** (`allowed: false`): кадр уезжает в README,
    /// и рабочая папка владельца в нём была бы личными данными. Проверка собирает такой же
    /// контроллер — тем же правом, каким идёт съёмка.
    /// </summary>
    internal static SettingsWindow BuildShotSettingsWindow(
        string kind,
        AppPaths paths,
        ServerController server,
        IAutostartControl? autostart,
        ISettingsControl? samples = null,
        IPhoneSection? samplePhone = null,
        IServerControl? sampleServer = null)
    {
        // ОБРАЗЦОВЫЕ НАСТРОЙКИ (фаза 3): на кадре видно РАБОЧУЮ панель — рабочую папку, порт,
        // расписание копий, порог баланса, — а не умолчания с серыми органами. Дверь при этом
        // НИЧЕГО не пишет и ничего с машины не читает (`Shell\ShotDrawers.cs`).
        var settingsControl = samples ?? new SettingsController(
            new SettingsStore(paths.SettingsFile),
            paths,
            allowed: false,
            canReadOwnerEnvironment: false,
            locateEngine: () => DshEngine.Locate(),
            server: () => sampleServer ?? server,
            applyTheme: _ => { },
            log: _ => { });

        var settingsWindow = new SettingsWindow();

        // РАЗДЕЛ «ТЕЛЕФОН» (F1.d) НА КАДРЕ — ДВЕРЬ ТОЛЬКО НА РИСОВАНИЕ (этап F1, хвост 3).
        //
        // ⚠️ **Почему дверь, хотя прежде её здесь не было вовсе.** Требование владельца дословно:
        // **«кадр `settings-phone` обязан показать код»**. Рисует код ДВЕРЬ (`IPhoneSection.QrPng`),
        // а не окно: без двери окно не может нарисовать ничего и говорит словами, что телефоном
        // в этом прогоне не управляют, — то есть кадр молчал о коде, о котором сам же и заявлял.
        //
        // ⚠️ **И почему эта дверь НЕ ИМЕЕТ НИ ОДНОГО ПРАВА КАЛИТКИ.** Ей дана **пустая калитка**
        // (`gate: () => null`): сопряжения нет (код выпустить некому — и выдумывать его панель
        // не станет), устройств нет (список пуст, отвязывать некого), порт не занимается вовсе
        // (поднимать нечего). Настоящая калитка в прогоне проверки не поднимается и по праву
        // (`RunRights.MayOpenPhoneGate`), а `43127` прогон не занимает никогда.
        //
        // ⚠️ **Строку для кода окно берёт из ПОКАЗА, а не из решения**: маска показывается
        // (<see cref="MaskAddress"/>) — и в код уезжает ровно показанное `192.168.0.х`, обычный
        // текст без номера машины (решение владельца 11.10.2026). Настоящего номера адреса
        // в кадре быть не должно, и это стережёт `PhoneShotFrameTests`.
        var phone = samplePhone ?? (kind == StartModes.ShotSettingsPhone
            ? new PhoneSection(settingsControl, gate: () => null, log: _ => { })
            : null);

        // КАДР РАЗДЕЛА «ТЕЛЕФОН» ПОКАЗЫВАЕТ АДРЕС ПОД МАСКОЙ: кадр уезжает в README,
        // а номер адреса своей машины — её примета (решение владельца 11.10.2026 дословно:
        // «номера не пишем либо заменяем последнее число на `х`»). Дверь адресов при этом
        // НАСТОЯЩАЯ — та же, что у человека: подменять её значило бы снимать не тот раздел,
        // какой он видит. Маска ложится на ПОКАЗ, а не на решение: замаскированный адрес
        // перестал бы быть адресом, и правило выбора отбросило бы его как негодный.
        settingsWindow.Attach(
            settingsControl,
            autostart,
            balance: samples is null ? null : ShotDrawers.All().Balance,
            pricing: samples is null ? ShotPricing(paths) : ShotPricingWithHistory(),

            // ⚠️ ДВЕРЬ ОБНОВЛЕНИЯ — ОБРАЗЦОВАЯ (фаза 3): без неё кадр раздела «Обновление» говорил
            // «обновления ещё не проверялись», показывал выключенные кнопки и «на GitHub данных нет» —
            // то есть ровно то, на что владелец смотрел в публичной витрине и называл кривым фото.
            // Значения выдуманы и личными быть не могут: это вымышленный выпуск вымышленной версии.
            update: samples is null ? null : ShotUpdateControl.For(Loc.Language),

            // ⚠️ ДВЕРЬ ЗАМЕНЫ НА КАДРЕ ДВА РАЗНЫХ ОТВЕТА, и выбор — ОДНИМ решением
            // (`ShotPreparesInstall`): кадр «подготовлено» получает УСПЕХ подготовки с путями-
            // литералами, все прочие — записанный ОТКАЗ (съёмка ничего не готовит). Подменить одну
            // дверь другой значило бы либо соврать о работе, которой не было, либо оставить кадр
            // без состояния, ради которого имя и заведено.
            install: samples is null
                ? null
                : ShotPreparesInstall(kind) ? ShotDrawers.PreparedInstall() : ShotDrawers.Install(),

            phone: phone,
            phoneAddressMask: kind == StartModes.ShotSettingsPhone ? MaskAddress : null);

        // ПОСТРОЕНИЕ КАДРА ГОТОВО — и блок движка получает ОБРАЗЦОВУЮ дверь: без неё он говорил
        // «обновлять некуда: проверенной версии нет» и «заметок нет: подсказки не прочитаны» —
        // две серые строки о том, чего панель НЕ СМОГЛА, а не о том, что человек видит в жизни.
        if (samples is not null) settingsWindow.AttachEngineUpdate(ShotDrawers.Engine());

        // РАЗДЕЛ «О ПРОГРАММЕ» НА КАДРЕ (u14, 2.4.0) — и дверь, которую легко забыть: блок
        // «Сообщить о проблеме» показывается ТОЛЬКО когда у раздела есть дверь
        // (`AboutInfo.ShowIssueBlock`). Без неё кадр показал бы раздел БЕДНЕЕ того, какой видит
        // человек, — из него пропало бы то, ради чего блок заводился. Дверь пустая: нажатие
        // в кадре ничего не открывает, и открывать нечего — прогон проверки владельцу не
        // показывает ничего (красная линия 8).
        settingsWindow.AttachIssueDoor(() => { });

        // Кадр `settings` снимает САМЫЙ ДЛИННЫЙ раздел («Резервное копирование»), а не первый:
        // только на нём видно полосу прокрутки и то, что она не наезжает на поля
        // (жалоба владельца 27.09.2026), и что подпись раздела влезает в колонку.
        // `settings-balance` — раздел «Баланс и тариф», `settings-update` — раздел
        // «Обновление» (он появился 28.09.2026, и кадр нужен и проверяющему, и README),
        // `settings-phone` — раздел «Телефон» (F1.d), `settings-about` — седьмой раздел
        // «О программе» (u14, 2.4.0: окна больше нет, ответ живёт разделом).
        settingsWindow.ShowSection(kind switch
        {
            StartModes.ShotSettingsGeneral => SettingsWindow.GeneralSection,
            StartModes.ShotSettingsBalance => SettingsWindow.BalanceSection,
            StartModes.ShotSettingsUpdate or StartModes.ShotSettingsUpdatePrepared => SettingsWindow.UpdateSection,
            StartModes.ShotSettingsServer => SettingsWindow.ServerSection,
            StartModes.ShotSettingsPhone => SettingsWindow.PhoneSection,
            StartModes.ShotSettingsAbout => SettingsWindow.AboutSection,
            _ => SettingsWindow.BackupSection,
        });

        return settingsWindow;
    }

    /// <summary>
    /// ОТКУДА ДАННЫЕ НА КАДРЕ — ЧИСТОЕ РЕШЕНИЕ, КОТОРОЕ МОЖНО ПРОВЕРИТЬ.
    ///
    /// **Зачем отдельный член, когда есть условие в одну строку.** Условие было —
    /// `kind != StartModes.ShotMainTwoServers`, — и жило только внутри `Shot`, куда проверке
    /// не дотянуться: съёмка поднимает платформу. Мутация «подставную дверь заменили настоящей»
    /// **не роняла ничего** (`AJ4` в `docs\HISTORY.md`): кадр оставался непустым за счёт других
    /// дверей, а проверки смотрели каталог значений, а не то, доехали ли значения до окна.
    /// Теперь решение названо здесь, и его спрашивают и съёмка, и проверка.
    /// </summary>
    internal static StartModes.ShotDoors ShotDoorSource(string kind) =>
        kind == StartModes.ShotMainTwoServers ? StartModes.ShotDoors.Real : StartModes.ShotDoors.Sample;

    /// <summary>
    /// СНИМАЕТ ЛИ ЭТО ИМЯ СОСТОЯНИЕ «ПОДГОТОВЛЕНО» (DEF-009, 2.4.1) — ОДНО РЕШЕНИЕ НА ДВА ДЕЙСТВИЯ.
    ///
    /// **Почему отдельным членом, а не условием на месте.** Это имя отличается от `settings-update`
    /// ровно двумя вещами: дверь замены отвечает успехом (<see cref="ShotDrawers.PreparedInstall"/>)
    /// и состояние производит ТОТ ЖЕ шов, каким идёт человек (`SettingsWindow.PrepareNow`). Двумя
    /// условиями подряд они однажды разошлись бы — кадр получил бы успешную дверь, но остался
    /// неподготовленным (или наоборот), и заметить это было бы нечем: кадр непустой, имя на месте.
    ///
    /// ⚠️ Спрашивается по имени окна: решение о ДАННЫХ снимает `ShotDoorSource`, а это — о состоянии
    /// кадра, и второе не заменяет первое.
    /// </summary>
    internal static bool ShotPreparesInstall(string kind) =>
        string.Equals(kind, StartModes.ShotSettingsUpdatePrepared, StringComparison.Ordinal);


    /// <summary>
    /// КАДР ИНТЕРФЕЙСА — БЕЗ ПОКАЗА ОКНА ЧЕЛОВЕКУ.
    ///
    /// <paramref name="themeValue"/> — значение ключа <c>--theme dark|light</c> (пусто — тема
    /// панели, как было). <paramref name="stateValue"/> — значение ключа
    /// <c>--state hover:ИмяОргана|focus:ИмяОргана</c> (пусто — без состояния).
    ///
    /// ⚠️ **Незнакомое значение ЛЮБОГО из этих ключей — громкий отказ кодом 2**, а не «снимем как
    /// получится»: кадр уезжает в README, и кадр по опечатке заметить было бы некому (то же правило,
    /// что у незнакомого имени окна).
    /// </summary>
    private static int Shot(string path, string kind, string themeValue = "", string stateValue = "")
    {
        // НЕЗНАКОМОЕ ИМЯ ОКНА — ОТКАЗ КОДОМ 2, И ЭТО ПЕРВАЯ ПРОВЕРКА ЗДЕСЬ (находка приёмки
        // 13.10.2026). Прежде имя проверялось внутри сборки окна и там **бросало исключение**:
        // прогон возвращал необработанное `-532462766` вместо обещанного `AGENTS.md` кода 2,
        // а строка «случай проверен прогоном» была неправдой. Теперь отказ стоит ДО подъёма
        // платформы — и раньше темы, чтобы опечатка в имени не тратила время на сборку окна.
        if (kind.Length > 0 && !StartModes.KnowsShotWindow(kind))
        {
            Console.WriteLine(
                $"SHOT ПРОВАЛ: неизвестное окно «{kind}». Известные: {string.Join(", ", StartModes.ShotWindows)}.");
            return 2;
        }

        // ТЕМА — ДО ПОДЪЁМА ПЛАТФОРМЫ И ДО ПОСТРОЙКИ ОКНА: тема это свойство ПРИЛОЖЕНИЯ, и окно,
        // построенное раньше неё, осталось бы в прежней. Значение разбирает чистое решение
        // (`ShotDecisions`), а отказ — здесь, до всякой работы.
        if (!string.IsNullOrWhiteSpace(themeValue) && !ShotDecisions.KnowsTheme(themeValue))
        {
            Console.WriteLine(
                $"SHOT ПРОВАЛ: незнакомая тема «{themeValue}». Годятся: {string.Join(", ", ShotDecisions.Themes)}.");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(stateValue) && !ShotDecisions.KnowsState(stateValue))
        {
            Console.WriteLine(
                $"SHOT ПРОВАЛ: незнакомое состояние «{stateValue}». Годятся: "
                + $"{ShotDecisions.StateFocus}:ИмяОргана или {ShotDecisions.StateHover}:ИмяОргана.");
            return 2;
        }

        var theme = ShotDecisions.Theme(themeValue);
        var state = ShotDecisions.State(stateValue);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // ⚠️ ТЁМНАЯ ТЕМА КАДРА: `SetupWithoutStarting` поднял приложение, и свойство темы у него
        // уже есть. Ставим ДО постройки окна — иначе окно построилось бы в прежней теме.
        // `--theme light` тоже ставится явно: кадр обязан быть снят ИМЕННО в названной теме,
        // а не «в той, что оказалась» (иначе тёмный и светлый кадры могли случайно совпасть).
        //
        // ⚠️ **Перевод «значение ключа → тема панели» живёт ОДНИМ решением** (`ShotDecisions.Palette`),
        // а не здесь: живя здесь, он был бы непроверяем (съёмка поднимает платформу) — ровно так
        // и появилась непокрытая мутация AJ3 «тёмная тема не применяется».
        if (ShotDecisions.Palette(theme) is { } palette) App.ApplyTheme(palette);

        // Снимаем ТО ЖЕ окно, которое видит человек, вместе с настоящим контроллером сервера:
        // он ещё ничего не запускал, поэтому кадр показывает исходное состояние панели.
        // Сервер при съёмке не поднимается — контроллер трогает пути только при запуске.
        // Журнал подменён пустышкой — съёмка не пишет владельцу.
        var paths = RunContext.Current.Paths;

        // Прогон съёмки — это ПРОГОН ПРОВЕРКИ: права занять порт владельца у него нет
        // (`mayOccupyOwnerPort: false`), и это не формальность: с настоящим правом кадр снимался бы
        // на 3080, то есть на канале сессии агента. Файлы состояния сервера не передаются вовсе —
        // источники по умолчанию молчат, и в кадр не может попасть ни ссылка входа, ни чужой сервер.
        //
        // ⚠️ ОДНО ИСКЛЮЧЕНИЕ — кадр «два найденных сервера» (`StartModes.ShotMainTwoServers`):
        // там факты о процессах ПОДСТАВЛЕНЫ (порты 3080 и 3099 выдуманы), потому что живой сервер
        // владельца в кадр попадать не должен, а кадр с двумя находками нужен приёмке (решение
        // владельца 10.10.2026). Окно, модель и кнопки при этом настоящие.
        var server = new ServerController(
            paths.DshHome,
            () => paths.DataDir,
            _ => { },
            mayOccupyOwnerPort: false,
            probe: kind == StartModes.ShotMainTwoServers ? TwoServersProbe(paths) : ServerProbe.System(),
            ownEngineDir: () => paths.EngineDir);

        // АВТОЗАПУСК НА КАДРЕ — ОБРАЗЦОВЫЙ, А НЕ МАШИННЫЙ (находка C1, пачка A).
        //
        // ⚠️ **Прежде здесь читалась НАСТОЯЩАЯ запись `HKCU\...\Run`** (только на чтение), и кадр
        // зависел от машины: у владельца он показывал «автозапуск включён» с его путём, а на чистой
        // машине — «выключен». Один и тот же кадр выходил разным, то есть не воспроизводимым,
        // и в него попадал путь пользователя. Теперь дверь — литерал (файл образцовых дверей),
        // и разбор съёмки не спрашивает хранилище автозапуска ни разу.
        //
        // ⚠️ Это правка ТОЛЬКО ветви съёмки: обычный запуск человека получает настоящий контроллер
        // автозапуска в `App.StartPanel`, и там ничего не меняется.
        var autostart = ShotDrawers.Autostart();

        var (window, _) = ShotWindowWithDoors(kind, paths, server, autostart);
        window.Show();
        UiThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        UiThread.RunJobs();

        // СОСТОЯНИЕ ОРГАНА — ПОСЛЕ ПОКАЗА ОКНА И ДО СЪЁМКИ: псевдокласс ставится на ЖИВОЕ дерево,
        // а до `Show()` элементов ещё нет. Фокус и наведение — это то, что чек-лист вида требует
        // в кадрах, а снять их иначе нечем: у безголовой съёмки нет ни мыши, ни клавиатуры.
        if (state is not null)
        {
            var marked = ShotState(window, state.Value.State, state.Value.Control);

            if (!marked)
            {
                Console.WriteLine(
                    $"SHOT ПРОВАЛ: в окне «{kind}» нет органа «{state.Value.Control}» — состояние "
                    + $"{state.Value.State} показать нечем.");
                return 2;
            }

            UiThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            UiThread.RunJobs();
        }

        // Кадру с двумя находками нужен ЕЩЁ один осмотр: разведка приходит в модель отдельным
        // ходом (в жизни — раз в секунду), и без этого кадр показал бы состояние ДО неё. Только
        // для этого имени: остальные кадры снимают то, что видно сразу.
        if (kind == StartModes.ShotMainTwoServers && window is MainWindow shotMain)
        {
            shotMain.RefreshNow();
            UiThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            UiThread.RunJobs();
        }

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
    /// ПОСТРОИТЬ ОКНО КАДРА — ОДНИМ МЕСТОМ, И ЭТОТ ЖЕ ЧЛЕН ЗОВЁТ ПРОВЕРКА ОБВЯЗКИ.
    ///
    /// ⚠️ **Почему отдельный член, а не строки внутри `Shot`.** Мутации **AJ4** и **AJ9**
    /// (`docs\HISTORY.md`) подменяют ИСПОЛЬЗОВАНИЕ решения о данных: снимают образцовую дверь
    /// из одного места сборки либо переворачивают выбор двери. Пока сборка жила внутри `Shot`,
    /// проверке до неё было не дотянуться (съёмка поднимает платформу), и обе мутации выжили.
    /// Теперь окно собирается ЗДЕСЬ, и проверка строит ЕГО ЖЕ и ищет в нём образцовое значение.
    ///
    /// ⚠️ **Пути и владелец автозапуска приходят доводами**, а не берутся из `RunContext`:
    /// у проверки свой контекст, и поднимать его ради кадра она не должна.
    ///
    /// **Возвращается и СЛОВАРЬ ДВЕРЕЙ, которые доехали до окна** — по имени, для проверки
    /// обвязки. **От какого случая (мутация AJ4, `docs\HISTORY.md`).** Проверка «в кадре есть
    /// образцовое значение» пропускала пропажу ОДНОЙ двери: кадр оставался непустым за счёт
    /// соседних. Имена добавляются ровно там, где дверь ПЕРЕДАНА окну (`sample ? drawers.X : …`),
    /// поэтому «образцовый сервер снят, баланс остался» видно по имени, а не по «кадр всё ещё
    /// непустой»; снять дверь и оставить имя нельзя, не тронув эту же строку.
    /// </summary>
    internal static (Window Window, IReadOnlyList<string> Doors) ShotWindowWithDoors(
        string kind,
        AppPaths paths,
        ServerController server,
        IAutostartControl? autostart = null,
        DateTimeOffset? peaksAt = null,
        Func<DateTime, int>? peaksOffsetAtUtc = null)
    {
        // ОБРАЗЦОВЫЕ ДАННЫЕ ДЛЯ КАДРА — ПОДСТАВНЫЕ ДВЕРИ (фаза 3, `Shell\ShotDrawers.cs`).
        //
        // ⚠️ **Только для ветви съёмки.** Обычный запуск и прогон проверки получают настоящие
        // контроллеры — правило изоляции не ослаблено ни на шаг: двери ниже ничего не читают
        // с машины, ничего не пишут и в сеть не ходят, а значения в них — ЛИТЕРАЛЫ.
        //
        // ⚠️ **Зачем это вообще.** Прогон проверки не читает данные владельца по устройству,
        // поэтому до 12.10.2026 кадры выходили ПУСТЫМИ: ни списков, ни чисел, ни таблиц, а в
        // публичную витрину уезжали кадры со словами «прогон проверки» и «баланс не читается».
        // Такой кадр не показывает вид продукта — на это и жаловался владелец 12.10.2026.
        var drawers = ShotDrawers.All();

        Window window;

        // Кадру «два найденных сервера» нужна НАСТОЯЩАЯ модель окна с подставленной пробой
        // процессов: его кадр — про пометки «наш движок (версия)» и «чужой», и образцовые данные
        // там не заменяют дело, а мешают ему. Поэтому подставные двери его не касаются.
        var sample = ShotDoorSource(kind) == StartModes.ShotDoors.Sample;

        // КАКИЕ ОБРАЗЦОВЫЕ ДВЕРИ ДОЕХАЛИ ДО ЭТОГО ОКНА — по ИМЕНИ, и спрашивается это
        // у собранного окна, а не у исходника.
        //
        // ⚠️ **Зачем счёт, когда проверка и так читает собранное окно.** Мутация AJ4 сняла
        // образцовый СЕРВЕР из одного места сборки, и кадр остался непустым за счёт соседних
        // дверей: «в кадре есть образцовое» зеленело, а двери в нём не было. Поэтому здесь
        // называется КАЖДАЯ дверь, которую окно получило, и проверка обвязки сверяет список
        // с обещанным для этого имени — пропажа любой одной видна по имени, а не по «пусто/непусто».
        var doors = new List<string>();

        // БАЛАНС ДЛЯ КАДРА «ДВЕ НАХОДКИ» И ДЛЯ ПРОВЕРОК ДВЕРИ — как прогон проверки: БЕЗ права
        // читать ключ. Иначе снимок показывал бы баланс владельца (он уезжает в README), а съёмка
        // ещё и ходила бы в сеть с его ключом. Образцовые кадры берут ЧИСЛО из `ShotDrawers`,
        // а этот контроллер остаётся для того кадра, где показываются не деньги, а находки.
        //
        // ⚠️ Имя с `Real` — чтобы не спутать с дверью `ShotBalance` (класс в `Shell\ShotDrawers.cs`):
        // две разные вещи с похожими именами однажды разошлись бы молча.
        BalanceController RealShotBalance() => new(
            new HttpBalanceClient(),
            paths.CredentialsPath,
            () => PanelSettings.Default,
            allowed: false,
            clock: () => DateTimeOffset.Now,
            dispatch: action => action(),
            log: _ => { },
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
        // ⚠️ Отказ здесь — НЕ «вернуть 2»: имя окна уже проверено `Shot` до вызова, и сюда
        // незнакомое имя дойти не может. Если дойдёт, это дефект вызова, и молча снять главное
        // окно нельзя — кадр по опечатке уехал бы в README. Поэтому исключение, а не код возврата.
        if (kind.Length > 0 && !StartModes.KnowsShotWindow(kind))
        {
            throw new ArgumentException($"неизвестное имя окна съёмки: «{kind}»", nameof(kind));
        }

        if (kind is StartModes.ShotSettings or StartModes.ShotSettingsGeneral or StartModes.ShotSettingsBackup
            or StartModes.ShotSettingsBalance or StartModes.ShotSettingsUpdate
            or StartModes.ShotSettingsUpdatePrepared
            or StartModes.ShotSettingsServer or StartModes.ShotSettingsPhone
            or StartModes.ShotSettingsAbout)
        {
            // Сборка окна настроек для кадра — ОДНИМ членом (`BuildShotSettingsWindow`), а не здесь:
            // иначе проверке до неё не дотянуться, и дефект обвязки съёмки (раздел «Телефон» на кадре
            // получал `phone: null`, то есть молчал о коде, о котором сам же и заявлял) снова прошёл
            // бы мимо всех проверок. Разбор — `docs\HISTORY.md`, «третий хвост после F1».
            if (sample)
            {
                doors.Add("settings");
                doors.Add("phone");
                doors.Add("server");
            }

            window = BuildShotSettingsWindow(
                kind,
                paths,
                server,
                autostart,
                sample ? drawers.Settings : null,
                sample ? drawers.Phone : null,
                sample ? drawers.Server : null);

            // ⚠️ И ДВЕ ДВЕРИ, КОТОРЫЕ СТАВЯТСЯ ПОСЛЕ ВЯЗКИ (`BuildShotSettingsWindow`): баланс
            // и обновление. Они не доводы того члена, поэтому названы здесь — иначе проверка
            // обвязки не увидела бы их пропажу.
            if (sample)
            {
                doors.Add("balance");
                doors.Add("update");
            }

            // КАДР «ПОДГОТОВЛЕНО» (DEF-009): состояние производит ТОТ ЖЕ ШОВ, КАКИМ ИДЁТ ЧЕЛОВЕК, —
            // `SettingsWindow.PrepareNow`, а не подстановка готового ответа в поле окна. Подстановка
            // показала бы состояние, которого продукт не производит, и кадр перестал бы быть кадром
            // (то же правило, что у образцовых дверей фазы 3). Дверь замены этому имени выдана
            // успешная (`ShotPreparesInstall`), поэтому шов ничего не качает, не пишет и не заменяет.
            //
            // ⚠️ Зовётся ДО `Show()`: окно ещё не показано, и прокрутка к карточке (её ставит
            // `RevealReplaceCard`) не делается — кадр обязан показать карточку ЦЕЛИКОМ без прокрутки,
            // а не «подведённую». Фокус на кадре ставит ключ `--state focus:UpdateReplaceCard`.
            if (window is SettingsWindow preparedSection && ShotPreparesInstall(kind))
            {
                preparedSection.PrepareNow();
            }
        }
        else if (kind == StartModes.ShotBackup)
        {
            // Окно копий на кадре: список — ОБРАЗЦОВЫЙ (три копии с датами, размерами и числом
            // файлов), папка владельца НЕ читается вовсе. Прежде список был пуст «как в прогоне
            // проверки» — и кадр показывал окно без единой строки, то есть не показывал ничего.
            //
            // ⚠️ Ни копия, ни накат на кадре не делаются: у подставной двери нет ни путей
            // владельца, ни права, и она отвечает отказом СЛОВАМИ. Нажимать в кадре некому.
            var backupWindow = new BackupWindow();
            backupWindow.Attach(ShotDrawers.Backup());
            doors.Add("backup");
            window = backupWindow;
        }
        else if (kind == StartModes.ShotPeaks)
        {
            // Окно «Пики и тарифы» на кадре — то же окно, что открывает человек кнопкой
            // в карточке баланса. С образцовыми дверями в нём видны и таблица дней, и строки цен:
            // без них кадр показывал бы «баланс не читается» — то, на что владелец и жаловался.
            var peaks = new PeakWindow();

            // ⚠️ ЦЕНЫ НА КАДРЕ — ОБРАЗЦОВАЯ ДВЕРЬ, А НЕ НАСТОЯЩИЙ КОНТРОЛЛЕР (t13, 18.10.2026).
            // С настоящим блок «Стоимость» выходил ПУСТЫМ: прогон проверки не имеет права ходить
            // в сеть, и окно честно писало «Цены не читались: это прогон проверки». На витрине это
            // читается как «панель ничего не показывает». Окно здесь не меняется — меняется дверь,
            // которую оно получает (см. `ShotDrawers.Pricing` и `ShotSamples.PricingSnapshotSample`).
            //
            // t24: момент и смещение «на момент UTC» передаются ОКНУ явно — тогда кадр
            // и его ожидание считаются от одного мгновения, а не от часов машины.
            peaks.Attach(
                sample ? drawers.Balance : RealShotBalance(),
                sample ? ShotDrawers.Pricing() : ShotPricing(paths),
                peaksAt,
                peaksOffsetAtUtc);
            if (sample)
            {
                doors.Add("balance");
                doors.Add("pricing");
            }

            window = peaks;
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
            pricingHistory.Attach(ShotPricingWithHistory());
            window = pricingHistory;
        }
        else if (kind == StartModes.ShotConfirmStop)
        {
            // ПОДТВЕРЖДЕНИЕ ОСТАНОВКИ ВСТРОЕННОГО СЕРВЕРА. Порт, номер процесса и имя процесса —
            // ОБРАЗЦОВЫЕ: подтверждать «остановку сервера вообще» человек не должен, он видит,
            // что именно остановится, — и на кадре это видно теми же словами.
            var confirmStop = new ConfirmStopWindow();
            confirmStop.Attach(ShotSamples.Port, ShotSamples.Pid, ShotSamples.Process);
            window = confirmStop;
        }
        else if (kind == StartModes.ShotConfirmEngine)
        {
            // ПОДТВЕРЖДЕНИЕ ОБНОВЛЕНИЯ ДВИЖКА: те же СЛОВА, что увидит человек, — их собирает
            // отдельная запись (`PanelQuestion`), а не эта ветвь. Поэтому кадр не может разойтись
            // с жизнью: он показывает ровно тот вопрос, какой задаётся.
            var confirmEngine = new ConfirmEngineWindow();
            confirmEngine.Attach(new PanelQuestion(
                PanelStrings.EngineUpdateAskTitle,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.EngineUpdateAskFormat,
                    ShotSamples.EngineVersion),
                PanelStrings.EngineUpdateAskYes,
                PanelStrings.EngineUpdateAskNo,
                PanelStrings.TipEngineUpdateAskYes,
                PanelStrings.TipEngineUpdateAskNo));
            window = confirmEngine;
        }
        else if (kind == StartModes.ShotBackupStop)
        {
            // «ПОГАСИТЬ СЕРВЕР ПЕРЕД КОПИЕЙ?» — вопрос, который панель задаёт человеку перед копией
            // и накатом. Порт образцовый; слова — из словаря, те же, что в жизни.
            var backupStop = new BackupStopWindow();
            backupStop.Attach(ShotSamples.Port);
            window = backupStop;
        }
        else if (kind == StartModes.ShotUnsavedChanges)
        {
            // «СОХРАНИТЬ ИЗМЕНЕНИЯ?» — вопрос при закрытии настроек с несохранённой правкой.
            // Подписи и подсказки ставит сам конструктор: окно, построенное без наполнения,
            // не должно выглядеть пустым — и на кадре это видно.
            window = new UnsavedChangesWindow();
        }
        else if (kind == StartModes.ShotBackupClose)
        {
            // «ЗАКРЫТЬ ОКНО, ПОКА ИДЁТ КОПИЯ?» (DEF-045, 2.4.0) — вопрос, который панель задаёт,
            // когда человек закрывает окно копий на середине копии. Подписи и тело ставит
            // конструктор: кадр не может разойтись с жизнью.
            window = new BusyCloseWindow();
        }
        else if (kind == StartModes.ShotFirstRun)
        {
            // ОКНО ПЕРВОГО ЗАПУСКА на кадре — ХУДШИЙ случай: ни сервера DSH, ни ключа модели.
            // ⚠️ Факты — ОБРАЗЦОВЫЕ (`Shell\ShotDrawers.cs`): настоящие сделали бы кадр зависимым
            // от машины, а он уезжает и в README, и в приёмку. У окна нет ни путей, ни значения
            // ключа — только «есть / нет», поэтому личному здесь взяться неоткуда.
            var firstRun = new FirstRunWindow();
            firstRun.Attach(ShotDrawers.FirstRun());

            // Двери окна на кадре НЕ нажимаются (нажимать некому): кадр показывает вид и слова.
            doors.Add("first-run");
            window = firstRun;
        }
        else
        {
            var main = new MainWindow();

            // ОБРАЗЦОВЫЕ ДАННЫЕ вместо пустого прогона проверки. Для кадра «два найденных
            // сервера» остаётся НАСТОЯЩАЯ модель с подставленной пробой процессов — там дело
            // именно в пометках «наш движок (версия)» и «чужой», и образцовые двери его заменяют
            // не делом, а помехой.
            main.Attach(sample ? drawers.Server : server);
            if (sample) doors.Add("server");

            // Баланс на кадре: с образцовыми дверями — число и подробность; на кадре двух
            // находок — прежнее «без права читать ключ» (там показывается не баланс, а находки).
            main.AttachBalance(sample ? drawers.Balance : RealShotBalance());
            if (sample) doors.Add("balance");

            // Смена активного агента — тем же владельцем настроек, что и в жизни: без него список
            // ЧЕСТНО недоступен, и кадр показывал бы серый список с подписью «настройки не читаются».
            if (sample)
            {
                main.AttachAgentChoice(drawers.Settings);
                doors.Add("settings");
            }

            window = main;
        }

        return (window, doors);
    }

    /// <summary>
    /// ПОСТАВИТЬ СОСТОЯНИЕ НА ОРГАН ПО ИМЕНИ ИЗ РАЗМЕТКИ (<c>x:Name</c>).
    ///
    /// **Зачем.** Чек-лист вида требует в кадрах **наведение** и **фокус**, а у безголовой съёмки
    /// нет ни мыши, ни клавиатуры: состояние ставится программно, псевдоклассом. Приём взят
    /// из проверок вида — там он уже работает.
    ///
    /// **Фокус важнее наведения** (доступность: видимый фокус — норма WCAG 2.2 SC 2.4.7/2.4.13),
    /// и он снимется даже там, где наведение не рисуется вовсе.
    ///
    /// Возвращает <c>false</c>, если органа с таким именем в окне нет: вызывающий обязан отказать
    /// **громко** — кадр «с состоянием», в котором состояния нет, уехал бы в витрину молча.
    ///
    /// ⚠️ **Виден проверке** (`ShotStateTests`) нарочно: кадры состояний снимаются ЭТИМ членом,
    /// и проверка обязана звать его же, а не расставлять псевдоклассы по-своему — иначе она мерила
    /// бы свою копию, а кадр уезжал бы в витрину по другой.
    /// </summary>
    internal static bool ShotState(Window window, string state, string controlName)
    {
        try
        {
            var found = window.FindControl<Control>(controlName);

            if (found is null) return false;

            // КАКОЙ ПСЕВДОКЛАСС СТАВИТЬ — решает ЧИСТОЕ решение (`ShotDecisions.PseudoClasses`),
            // а не эта ветвь: живя здесь, перевод «фокус → :focus» был бы непроверяем, и мутация
            // «ветвь наведения ничего не делает» выжила бы.
            foreach (var name in ShotDecisions.PseudoClasses(state))
            {
                ((IPseudoClasses)found.Classes).Set(name, true);
            }

            return ShotDecisions.PseudoClasses(state).Count > 0;
        }
        catch (Exception ex)
        {
            // Имя органа есть в разметке, но состояние поставить не вышло — это отказ словами,
            // а не молчаливый кадр без состояния.
            Console.WriteLine($"SHOT ПРОВАЛ: состояние «{state}» на органе «{controlName}» не поставилось: {ex.Message}");
            return false;
        }
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
                            // на кадре раздела «Обновление»: живой проверки в прогоне проверки нет,
                            // а состав меню обязан быть тем, что человек видит, вместе с тоном.
                            update: ShotUpdateControl.For(Loc.Language).TrayLine),
                        show: () => { },
                        openAgent: () => { },
                        settings: () => { },
                        about: () => { },
                        checkUpdate: () => { },
                        updateSection: () => { },
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

                var timer = UiThread.NewTimer(TimeSpan.FromSeconds(seconds));

                // Часов нет — нитки интерфейса нет. Самотесту тогда проверять нечего, и сказать
                // об этом надо вслух: «зелено» из-за отсутствия часов было бы ложью.
                if (timer is null)
                {
                    failed = true;
                    report.Add("нет нитки интерфейса — часы самотеста не завелись");
                    foreach (var line in report) Console.WriteLine("SELFTEST| " + line);
                    lifetime.Shutdown();
                    return;
                }

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
    /// ЖИВАЯ ПРОБА ВЛАДЕНИЯ ОКНАМИ (u14, 2.4.0). Окна строит <see cref="App"/> после подъёма
    /// платформы (до неё окно падает «Unable to locate IWindowingPlatform»), а здесь — только
    /// запуск цикла и вердикт. `SkipDefaultUi` выключает обычный путь панели: значок и сервер
    /// пробе не нужны, и второй значок на столе владельца был бы шумом.
    /// </summary>
    private static int WindowsSelfTestRun()
    {
        var selfTest = new WindowsSelfTest();

        ProgramOptions.WindowsSelfTest = selfTest;
        ProgramOptions.SkipDefaultUi = true;

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
            // ⚠️ УСТАНОВЛЕННАЯ ВЕРСИЯ НА КАДРЕ — ОБРАЗЦОВАЯ И СТАРШЕ ВЫПУСКА (DEF-009, 2.4.1,
            // решение капитана 24.10.2026): **2.3.2** — реально выпущенная 22.10.2026.
            // Прежде здесь стояла версия самой панели (`AboutInfo.PanelVersion`), и после выпуска
            // 2.4.0 она СОВПАЛА с образцовым выпуском: над готовой сборкой вставали «У вас последняя
            // версия — обновляться не нужно» и «Обновляться некуда», то есть кадр противоречил
            // сам себе. Обе версии на кадре — настоящие; 2.4.1 не называется, её ещё нет
            // (`_team\241\d1-normy.md` §2).
            Current = ShotSamples.UpdateInstalled;

            Result = new UpdateRelease(
                true,
                string.Empty,

                // ⚠️ НОМЕР ВЫПУСКА — ИЗ ОДНОГО ИСТОЧНИКА (`ShotSamples.UpdateVersion`, DEF-009):
                // его же называет путь подготовленной сборки на кадре «подготовлено». Две копии
                // номера однажды разошлись бы, и кадр показал бы сборку версии, которой у выпуска нет.
                "v" + ShotSamples.UpdateVersion,
                ProductLinks.ReleasesPageUrl,

                // ⚠️ ДАТА ВЫПУСКА — ИЗ ОДНОГО ИСТОЧНИКА (`ShotSamples.ReleasePublished`), t13, 18.10.2026.
                // Здесь стоял литерал «2026-09-27», и кадр витрины говорил «Выпущен: 27.09.2026»,
                // хотя 2.1.0 вышел 17.10.2026: читатель сверил бы дату со страницей релиза
                // и решил, что панель не знает собственного выпуска. Сценарий кадра не менялся.
                ShotSamples.ReleasePublished,
                UpdateDecisions.PickNotes(Body, language),
                Body,
                at)
            {
                // ⚠️ АДРЕСА ВЛОЖЕНИЙ — ОБРАЗЦОВЫЕ, и без них кадр врал бы ДВАЖДЫ (DEF-009б, 2.4.1).
                // Раздел решает «можно ли готовить» по наличию архива и файла сумм
                // (`UpdateLines.PrepareState`); с пустыми адресами кадр «есть новее» показывал бы
                // «Адреса вложений панель узнает при проверке» и ВЫКЛЮЧЕННУЮ кнопку подготовки —
                // то есть состояние, в котором человек не может обновиться, хотя выпуск есть.
                // Адреса — недействительный домен (`example.invalid` из RFC 2606): в кадре они
                // не показываются вовсе, скачать по ним съёмка не пытается, и личных данных в них нет.
                Assets = new ReleaseAssets(
                    "https://example.invalid/DshPanel.zip",
                    string.Empty,
                    "https://example.invalid/SHA256SUMS.txt"),
            };
        }

        /// <summary>
        /// Образец на языке панели: заметки разбираются на том языке, что выбран ключом.
        /// Дата ПРОВЕРКИ — тоже из одного источника (`ShotSamples.ReleaseCheckedAt`, 18.10.2026):
        /// прежде здесь стояло 28.09.2026, и кадр называл проверку, которой у этого выпуска не было.
        /// </summary>
        public static ShotUpdateControl For(string language) =>
            new(language, ShotSamples.ReleaseCheckedAt);

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

        /// <summary>Попытки на кадре не показываем: кадр снимает УДАЧНУЮ проверку, а у удачной
        /// этой строки нет вовсе (про время говорит «проверено {дата}»).</summary>
        public string AttemptText => string.Empty;

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
