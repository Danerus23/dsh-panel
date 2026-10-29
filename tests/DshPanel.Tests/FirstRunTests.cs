using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО ПЕРВОГО ЗАПУСКА (A5, вариант «а», находка C1 N-01) — решения, показ и «один раз».
///
/// **Что здесь доказывается и чем это падает.**
/// * **строк не больше двух и они в порядке зависимости** — чистая функция
///   <see cref="FirstRunDecisions.Rows"/>: на чистой машине «нет сервера DSH» + «нет ключа»,
///   а «сервера нет» НЕ дублирует причину, когда она уже названа строкой выше;
/// * **«не прочитан» ≠ «нет»** — третий факт идёт своим видом строки, а не сливается с «не найден»;
/// * **окно показывается ОДИН РАЗ и только человеку** — четыре условия перебираются по сочетаниям;
/// * **отметка живёт в настройках и старая запись читается** — настоящий файл настроек в своём
///   временном корне, а не подставное поле;
/// * **безопасная кнопка справа и Esc** — разметка: <c>IsCancel</c> есть, <c>IsDefault</c> нет ни у одной;
/// * **окно не говорит «не установлен»** — проверка слов: запрещённого диагноза в строках окна нет.
///
/// ⚠️ **Чего эта проверка не видит:** как окно выглядит на рабочем столе владельца (кадр снимает
/// <c>--shot … first-run</c>), слышит ли его Narrator и «понятно ли написано» — это человек и прогон
/// кадра, а не строки.
/// </summary>
public class FirstRunTests
{
    private static FirstRunFacts Clean() => new(
        EnginePresent: false, ServerProblem: null, KeyFound: false, KeyReadable: true);

    private static FirstRunFacts Ready() => new(
        EnginePresent: true, ServerProblem: null, KeyFound: true, KeyReadable: true);

    // --- решения --------------------------------------------------------------

    /// <summary>
    /// НА ЧИСТОЙ МАШИНЕ СТРОК ДВЕ, И ПЕРВАЯ — ПРО СЕРВЕР DSH: без него не работает ничего,
    /// ключ модели — второй вопрос. Порядок — наше решение (нормы на порядок нет), и оно названо.
    /// </summary>
    [Fact]
    public void На_чистой_машине_две_строки_и_сначала_сервер()
    {
        var rows = FirstRunDecisions.Rows(Clean());

        Assert.Equal(new[] { FirstRunRowKind.EngineMissing, FirstRunRowKind.KeyMissing }, rows);
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// «СЕРВЕРА НЕТ» НЕ ДУБЛИРУЕТ ПРИЧИНУ, КОГДА ОНА УЖЕ НАЗВАНА: если сервера DSH нет вовсе,
    /// строка «сервер не поднялся» не добавляется — две строки с одним исправлением это шум.
    ///
    /// **Мутация, которая обязана упасть:** «всегда добавлять обе строки» — тогда здесь будет три.
    /// </summary>
    [Fact]
    public void Без_сервера_строка_о_неподнятом_сервере_не_дублируется()
    {
        // Сервера DSH нет, ключ есть, а причина «не поднялся» при этом задана.
        var rows = FirstRunDecisions.Rows(
            new FirstRunFacts(EnginePresent: false, FirstRunReason.PortBusy, KeyFound: true, KeyReadable: true));

        Assert.Equal(new[] { FirstRunRowKind.EngineMissing }, rows);
    }

    /// <summary>
    /// СЕРВЕР DSH ЕСТЬ, А ПОДНЯТЬСЯ НЕ СМОГ — единственная строка, и причина у неё СЛОВАМИ
    /// (фразой из закрытого списка, а не машинным текстом).
    /// </summary>
    [Fact]
    public void Неподнятый_сервер_называется_строкой_с_причиной()
    {
        var rows = FirstRunDecisions.Rows(
            new FirstRunFacts(true, FirstRunReason.StartRefused, KeyFound: true, KeyReadable: true));

        Assert.Equal(new[] { FirstRunRowKind.ServerNotStarted }, rows);

        // И причина — из словаря, а не литерал: одна фраза на всю панель.
        Assert.Equal(PanelStrings.FirstRunReasonStartRefused, FirstRunDecisions.ReasonText(FirstRunReason.StartRefused));
        Assert.Equal(PanelStrings.FirstRunReasonPortBusy, FirstRunDecisions.ReasonText(FirstRunReason.PortBusy));
        Assert.Equal(PanelStrings.FirstRunReasonExitedSilent, FirstRunDecisions.ReasonText(FirstRunReason.ExitedSilent));
        Assert.Equal(PanelStrings.FirstRunReasonUnknown, FirstRunDecisions.ReasonText(FirstRunReason.Unknown));
    }

    /// <summary>
    /// КЛЮЧ «НЕ ПРОЧИТАН» — ОТДЕЛЬНАЯ СТРОКА, А НЕ «КЛЮЧА НЕТ» (правило «прочиталось ≠ нет»).
    /// Мутация «считать нечитаемый ключ отсутствующим» обязана упасть здесь.
    /// </summary>
    [Fact]
    public void Нечитаемый_ключ_не_выдаётся_за_отсутствующий()
    {
        var unreadable = FirstRunDecisions.Rows(
            new FirstRunFacts(EnginePresent: true, ServerProblem: null, KeyFound: true, KeyReadable: false));

        Assert.Equal(new[] { FirstRunRowKind.KeyUnreadable }, unreadable);

        var missing = FirstRunDecisions.Rows(
            new FirstRunFacts(EnginePresent: true, ServerProblem: null, KeyFound: false, KeyReadable: false));

        Assert.Equal(new[] { FirstRunRowKind.KeyMissing }, missing);
    }

    /// <summary>
    /// ВСЁ НА МЕСТЕ — СТРОК НЕТ ВОВСЕ, и окна тоже: подтверждать ожидаемый успех панель не должна.
    /// </summary>
    [Fact]
    public void Когда_всё_есть_строк_нет()
    {
        Assert.Empty(FirstRunDecisions.Rows(Ready()));
    }

    /// <summary>
    /// ПОКАЗЫВАТЬ ЛИ ОКНО — ПЕРЕБИРАЮТСЯ ВСЕ СОЧЕТАНИЯ. Снятое любое из четырёх условий — дефект:
    /// прогон проверки не показывает окон человеку, изолированный прогон тоже, отмеченный профиль
    /// окна больше не видит, а пустой список строк значит «говорить нечего».
    /// </summary>
    [Fact]
    public void Окно_показывается_человеку_один_раз_и_только_когда_есть_что_сказать()
    {
        var rows = FirstRunDecisions.Rows(Clean());
        var none = Array.Empty<FirstRunRowKind>();

        var cases = new (bool Human, bool Isolated, string ShownAt, IReadOnlyList<FirstRunRowKind> Rows, bool Expected)[]
        {
            (true,  false, "",                       rows, true),   // первый запуск человека — окно есть
            (true,  false, "2026-10-17T10:00:00Z",   rows, false),  // профиль уже видел — молчим
            (true,  false, "",                       none, false),  // всё на месте — молчим
            (true,  true,  "",                       rows, false),  // изолированный прогон — молчим
            (false, false, "",                       rows, false),  // прогон проверки — молчим
            (false, true,  "",                       rows, false),  // прогон проверки в изоляции — молчим
        };

        Assert.Equal(6, cases.Length);

        foreach (var (human, isolated, shownAt, list, expected) in cases)
        {
            Assert.Equal(expected, FirstRunDecisions.ShouldShow(human, isolated, shownAt, list));
        }
    }

    /// <summary>
    /// ПРИЧИНА «СЕРВЕР НЕ ПОДНЯЛСЯ» — ИЗ УСТОЙЧИВОГО ФАКТА, А НЕ «НЕ ОТВЕЧАЕТ ПРЯМО СЕЙЧАС».
    ///
    /// Перебираются все виды фактов: занятый чужой программой порт · отказ системы в запуске ·
    /// завершившийся процесс · прочий отказ контроллера · свободный порт (это НЕ отказ — сервер
    /// просто ещё не поднимали).
    ///
    /// **Мутация, которая обязана упасть:** «любое неработающее состояние — причина» (тогда
    /// свободный порт дал бы строку) и «причина только по присутствию» (тогда эти пять случаев
    /// дали бы одно и то же).
    /// </summary>
    [Fact]
    public void Причина_берётся_из_устойчивого_факта()
    {
        // Сервера DSH нет — строку не спрашиваем вовсе, что бы ни лежало в фактах.
        Assert.Null(FirstRunDecisions.Reason(
            enginePresent: false, running: false, starting: false, ServerPresence.BusyByOther, ServerOwner.None, "занято"));

        // Сервер отвечает — причины нет.
        Assert.Null(FirstRunDecisions.Reason(
            enginePresent: true, running: true, starting: false, ServerPresence.Running, ServerOwner.Panel, ""));

        // Порт занят ЧУЖОЙ программой — устойчивый факт, и он называется.
        Assert.Equal(
            FirstRunReason.PortBusy,
            FirstRunDecisions.Reason(true, false, false, ServerPresence.BusyByOther, ServerOwner.None, ""));

        // Отказ системы в запуске — по строке отказа самого контроллера, а не по догадке.
        Assert.Equal(
            FirstRunReason.StartRefused,
            FirstRunDecisions.Reason(true, false, false, ServerPresence.Stopped, ServerOwner.None, PanelStrings.SrvEngineStartRefused));

        // Сервер был наш и его нет — «завершился, не начав отвечать».
        Assert.Equal(
            FirstRunReason.ExitedSilent,
            FirstRunDecisions.Reason(true, false, false, ServerPresence.Stopped, ServerOwner.Panel, ""));

        // Прочий названный отказ — «подробности в окне панели»: машинный текст в окно не идёт.
        Assert.Equal(
            FirstRunReason.Unknown,
            FirstRunDecisions.Reason(true, false, false, ServerPresence.Stopped, ServerOwner.None, "какая-то беда"));

        // СВОБОДНЫЙ ПОРТ — ЭТО НЕ ОТКАЗ: сервер ещё не поднимали.
        Assert.Null(FirstRunDecisions.Reason(true, false, false, ServerPresence.Stopped, ServerOwner.None, PanelStrings.SrvPortFree));
    }

    /// <summary>
    /// ПОКА ИДЁТ СТАРТ, «НЕ ПОДНЯЛСЯ» НЕ ГОВОРИТСЯ — а после настоящей неудачи говорится
    /// (DEF-054, 2.4.0).
    ///
    /// **От какого случая.** При старте ПК панель поднимает сервер в фоне, и окно первого запуска
    /// успевало сказать «Сервер DSH не поднялся» в ту же секунду, что журнал писал «Сервер DSH
    /// запущен» (журнал 23.10.2026 10:07:42). Контроллер в такие секунды отдаёт СВОЙ устойчивый
    /// ответ «ещё стартует» и процесс не гасит — то есть проверка называла неудачей незаконченное
    /// дело.
    ///
    /// **Что перебирается.** Четыре положения, и каждое — свой ответ:
    /// * старт идёт (`starting`) при состоянии «не работает» — причины НЕТ, строки нет, окна нет;
    /// * старт идёт, а контроллер уже назвал отказ строкой («ещё стартует») — тоже НЕТ: попытка
    ///   не кончилась, и её слово «не поднялся» не значит;
    /// * старт кончился настоящей неудачей (наш сервер был и его нет) — причина есть, ОКНО ЕСТЬ;
    /// * старт кончился, а сервер отвечает — причины нет.
    ///
    /// **Мутация, которая обязана упасть:** «показывать до старта» — снять проверку `starting`
    /// в `FirstRunDecisions.Reason`; тогда первые два положения дадут строку и окно.
    /// </summary>
    [Fact]
    public void Медленный_старт_не_зовёт_окна_а_настоящая_неудача_зовёт()
    {
        // «Ещё стартует» — слова самого контроллера (`WaitUntilRunning`), а не выдумка проверки.
        var stillStarting = EngineFirstRun.StillStartingDetail(firstRunLooks: true, port: 3080);

        var slowStart = FirstRunDecisions.Reason(
            enginePresent: true, running: false, starting: true,
            ServerPresence.Stopped, ServerOwner.Panel, stillStarting);

        Assert.Null(slowStart);

        // И окна в этом положении нет: других строк тоже нет (сервер DSH есть, ключ есть).
        var slowRows = FirstRunDecisions.Rows(new FirstRunFacts(
            EnginePresent: true, ServerProblem: slowStart, KeyFound: true, KeyReadable: true));

        Assert.Empty(slowRows);
        Assert.False(FirstRunDecisions.ShouldShow(
            humanLaunch: true, isolated: false, shownAt: "", slowRows));

        // НАСТОЯЩАЯ НЕУДАЧА — попытка кончилась, сервер был наш и его нет.
        var failed = FirstRunDecisions.Reason(
            enginePresent: true, running: false, starting: false,
            ServerPresence.Stopped, ServerOwner.Panel, "");

        Assert.Equal(FirstRunReason.ExitedSilent, failed);

        var failedRows = FirstRunDecisions.Rows(new FirstRunFacts(
            EnginePresent: true, ServerProblem: failed, KeyFound: true, KeyReadable: true));

        Assert.Equal(new[] { FirstRunRowKind.ServerNotStarted }, failedRows);
        Assert.True(FirstRunDecisions.ShouldShow(
            humanLaunch: true, isolated: false, shownAt: "", failedRows));

        // Старт кончился удачей — говорить нечего.
        Assert.Null(FirstRunDecisions.Reason(
            enginePresent: true, running: true, starting: false,
            ServerPresence.Running, ServerOwner.Panel, ""));
    }

    // --- «один раз»: настоящий файл настроек ---------------------------------

    /// <summary>
    /// ОТМЕТКА ПЕРЕЖИВАЕТ ПЕРЕЧИТЫВАНИЕ, А СТАРАЯ ЗАПИСЬ НАСТРОЕК ЧИТАЕТСЯ (A5 §3).
    ///
    /// **Мутации, которые обязаны упасть:** «отметка не пишется» · «отметка не читается обратно» ·
    /// «файл без поля считается ошибкой» (тогда старая запись настроек перестала бы читаться вовсе).
    /// </summary>
    [Fact]
    public void Отметка_переживает_перечитывание_а_старая_запись_читается()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-first-run-" + Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.Root);

            // 1. СТАРАЯ ЗАПИСЬ: файл настроек, заведённый до A5 — в нём этого поля нет вовсе.
            File.WriteAllText(paths.SettingsFile, "{\"serverPort\":3081}");

            var old = new SettingsStore(paths.SettingsFile).Load();

            Assert.True(old.Ok, old.Problem);
            Assert.Equal(string.Empty, old.Settings.WelcomeShownAt);

            // 2. ОТМЕТКА: пишется тем же путём, каким её пишет App (`Save` с новым значением).
            var stamp = FirstRunDecisions.Stamp(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

            var settings = old.Settings;
            settings.WelcomeShownAt = stamp;

            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            Assert.True(controller.Save(settings));

            // 3. ПЕРЕЧИТЫВАНИЕ НОВЫМ ХРАНИЛИЩЕМ — как при следующем запуске панели.
            var reread = new SettingsStore(paths.SettingsFile).Load();

            Assert.True(reread.Ok, reread.Problem);
            Assert.Equal(stamp, reread.Settings.WelcomeShownAt);

            // И решение по этой отметке — «окно больше не показывать».
            Assert.False(FirstRunDecisions.ShouldShow(
                humanLaunch: true, isolated: false, reread.Settings.WelcomeShownAt, FirstRunDecisions.Rows(Clean())));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Временный каталог в %TEMP%: не убрался — не провал проверки.
            }
        }
    }

    // --- окно ----------------------------------------------------------------

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// ОКНО ПОКАЗЫВАЕТ ДВЕ СТРОКИ СЛОВАМИ СЛОВАРЯ, а внизу — безопасное действие СПРАВА.
    ///
    /// **Мутации, которые обязаны упасть:** «окно не заполняется» · «кнопка «Закрыть» перестала быть
    /// безопасной (Esc)» · «у кнопки появилось действие по Enter» · «подписи взяты литералами».
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_строки_и_безопасную_кнопку_справа()
    {
        var window = new FirstRunWindow();

        try
        {
            window.Attach(new FirstRunFactsDoor(Clean));
            window.Show();
            Settle();

            var texts = Texts(window);

            Assert.Contains(PanelStrings.FirstRunTitle, window.Title ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunIntro, texts, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunServerMissingEngineTitle, texts, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunServerMissingEngineBody, texts, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunKeyMissingTitle, texts, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunKeyMissingBody, texts, StringComparison.Ordinal);

            // Ни одного диагноза машине: «не установлен» окно не говорит (поиск пуст и когда пакет
            // есть, а Node нет).
            Assert.DoesNotContain("не установлен", texts, StringComparison.OrdinalIgnoreCase);

            // ДВЕРЬ — в раздел «Обновление»: там кнопка «Установить», и это первый ответ незнакомцу.
            Assert.Equal(PanelStrings.FirstRunDoorUpdate, Button(window, "DoorButton").Content);

            // ЗАКРЫТЬ — СПРАВА И БЕЗОПАСНО: Esc закрывает, действия по Enter нет ни у одной кнопки.
            Assert.True(Button(window, "CloseButton").IsCancel, "«Закрыть» перестало закрываться по Esc");

            foreach (var name in new[] { "DoorButton", "RecheckButton", "CloseButton" })
            {
                Assert.False(Button(window, name).IsDefault, $"у кнопки «{name}» появилось действие по Enter");
            }

            // Ничего ещё не нажимали — строки результата нет.
            Assert.Equal(string.Empty, window.RecheckText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// «ПРОВЕРИТЬ СНОВА» ГОВОРИТ СЛОВАМИ, И ЭТО ТЕ ЖЕ СЛОВА, ЧТО В СЛОВАРЕ: «ничего не изменилось»
    /// либо «всё на месте». Третьего ответа в таблице слов нет, и окно его не выдумывает.
    ///
    /// **Мутации, которые обязаны упасть:** «кнопка ничего не делает» · «строка результата всегда
    /// пустая» · «ответ не отличает «всё на месте» от «ничего не изменилось».
    /// </summary>
    [AvaloniaFact]
    public void Проверить_снова_отвечает_словами_словаря()
    {
        var facts = Clean();
        var window = new FirstRunWindow();

        try
        {
            window.Attach(new FirstRunFactsDoor(() => facts));
            window.Show();
            Settle();

            // Первое нажатие: факты не изменились.
            window.Recheck();
            Assert.Equal(PanelStrings.FirstRunRecheckSameLine, window.RecheckText);

            // Второе: всё на месте — и это ДРУГИЕ слова, а не те же.
            facts = Ready();
            window.Recheck();

            Assert.Equal(PanelStrings.FirstRunRecheckOkLine, window.RecheckText);
            Assert.Empty(window.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// КАДРУ НУЖНЫ ОБРАЗЦОВЫЕ ФАКТЫ, И ЭТО ХУДШИЙ СЛУЧАЙ: ни сервера DSH, ни ключа — две строки.
    /// Проверка сторожит, что образцовая дверь съёмки не «настоящая»: она ничего не читает с машины
    /// и не может отдать значение ключа (его в фактах нет вовсе).
    /// </summary>
    [AvaloniaFact]
    public void Образцовые_факты_съёмки_дают_худший_случай()
    {
        var sample = ShotDrawers.FirstRun().Read();

        Assert.False(sample.EnginePresent);
        Assert.False(sample.KeyFound);
        Assert.Null(sample.ServerProblem);

        Assert.Equal(
            new[] { FirstRunRowKind.EngineMissing, FirstRunRowKind.KeyMissing },
            FirstRunDecisions.Rows(sample));

        // И окно, собранное этой дверью, показывает ровно две строки — то же, что человек на чистой машине.
        var window = new FirstRunWindow();

        try
        {
            window.Attach(ShotDrawers.FirstRun());
            window.Show();
            Settle();

            Assert.Equal(2, window.Rows.Count);
        }
        finally
        {
            window.Close();
        }
    }

    // --- помощники ------------------------------------------------------------

    /// <summary>Весь текст окна: и подписи, и поля — человек видит и то, и другое.</summary>
    private static string Texts(Window window)
    {
        var parts = new List<string>();

        foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!string.IsNullOrEmpty(block.Text)) parts.Add(block.Text);
        }

        return string.Join("\n", parts);
    }

    private static Button Button(Window window, string name) =>
        window.FindControl<Button>(name) ?? throw new InvalidOperationException($"в окне нет кнопки «{name}»");
    /// <summary>
    /// ОКНО ПОКАЗЫВАЕТСЯ ИЗ ОБЫЧНОГО ЗАПУСКА, И ДО ШАРИКА (A5 §8 п. 3) — ВОРОТА ПО ИСХОДНИКАМ.
    ///
    /// **Зачем отдельная проверка на вызов.** Решение «показывать ли» проверено чистой функцией,
    /// но её можно НЕ ПОЗВАТЬ — и тогда все проверки решений останутся зелёными, а человек на чистой
    /// машине снова увидит только значок в трее. Это ровно тот класс, который в этом проекте ловили
    /// воротами по исходникам (`TreeDeletionTests`, `PhoneGateTests`).
    ///
    /// **Мутации, которые обязаны упасть:** «вызов показа окна убран» (CD5) · «шарик о запуске больше
    /// не знает, что человек увидел окно».
    /// </summary>
    [Fact]
    public void Окно_первого_запуска_зовётся_из_запуска_панели_и_до_шарика()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var app = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "App.axaml.cs"));

        // 1. Показ окна — отдельной дверью, и она вызвана ровно один раз.
        Assert.Contains("ShowFirstRunOnce(paths, log, human, isolated)", app, StringComparison.Ordinal);

        // 2. И ДО ШАРИКА: человек, увидевший окно, не получает шарик о том же самом запуске.
        Assert.Contains("NotifyPanelStarted(windowShown || firstRunShown)", app, StringComparison.Ordinal);

        // 3. Отметка пишется ТОЛЬКО по факту показа — тем же членом, а не «где-то при старте».
        Assert.Contains("WelcomeShownAt = FirstRunDecisions.Stamp(", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРОВЕРКА ГОТОВНОСТИ СПРАШИВАЕТ КОНТРОЛЛЕР ОБ ИДУЩЕМ СТАРТЕ (DEF-054, 2.4.0; DEF-064) —
    /// ВОРОТА ПО ИСХОДНИКАМ, потому что App в проверке не построить.
    ///
    /// **Почему одних решений мало.** `FirstRunDecisions.Reason` может быть идеально правильным,
    /// а факт «старт идёт» — не дойти до неё: тогда на старте ПК окно снова скажет «не поднялся».
    /// Это тот же класс, что и ворота выше («решение можно не позвать»).
    ///
    /// **Что проверяется:**
    /// 1. у двери сервера есть сам факт (`Starting`) и чтение без ожидания замка (`TryRefresh`);
    /// 2. «процесс жив, но не ответил» ставится там, где контроллер сам говорит «ещё стартует»;
    /// 3. проба первого запуска берёт ОДИН снимок (`FirstRunDecisions.ServerSnapshot`), а не
    ///    читает два поля по отдельности;
    /// 4. в самом снимке `Starting` стоит ПОСЛЕ `TryRefresh` (DEF-064: наоборот — и решение
    ///    получает устаревшее «не поднялся», пока сервер поднимается);
    /// 5. факт доходит до решения;
    /// 6. после попытки проверка спрашивается снова.
    ///
    /// **Мутации, которые обязаны упасть:** «не спрашивать Starting» · «спросить и не передать» ·
    /// «прочитать Starting до TryRefresh» (M1) · «после попытки не переспрашивать».
    /// </summary>
    [Fact]
    public void Проверка_готовности_спрашивает_контроллер_об_идущем_старте()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var app = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "App.axaml.cs"));
        var controller = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "Server", "ServerController.cs"));
        var decisions = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "Shell", "FirstRunDecisions.cs"));

        // 1. Факт существует в ДВЕРИ (проверке первого запуска он нужен тем же правом, что и окну)
        // и назван словами «идёт попытка старта» у контроллера.
        Assert.Contains("bool Starting => false;", controller, StringComparison.Ordinal);
        Assert.Contains("public bool Starting => _startAttempt || _engineStillStarting;", controller, StringComparison.Ordinal);
        Assert.Contains("bool TryRefresh(out ServerState state)", controller, StringComparison.Ordinal);

        // 1а. И «процесс жив, но не ответил» ставится ТАМ, где контроллер сам говорит «ещё стартует»
        // (`WaitUntilRunning`): без этой строки `Starting` после конца ожидания станет ложью.
        Assert.Contains("_engineStillStarting = true;", controller, StringComparison.Ordinal);

        // 2. Проба первого запуска берёт ОДИН снимок фактов о сервере.
        Assert.Contains("var snapshot = FirstRunDecisions.ServerSnapshot(_server);", app, StringComparison.Ordinal);

        // 3. В снимке состояние читается ПЕРВЫМ, а «идёт попытка» — ПОСЛЕ обновления (DEF-064).
        var refresh = decisions.IndexOf("server.TryRefresh(out var fresh)", StringComparison.Ordinal);
        var starting = decisions.IndexOf("server?.Starting == true", StringComparison.Ordinal);

        Assert.True(refresh > 0, "в FirstRunDecisions нет чтения состояния без замка (`TryRefresh`)");
        Assert.True(
            starting > refresh,
            "`Starting` читается ДО `TryRefresh`: решение получит устаревшее «не поднялся» (DEF-064)");

        // 4. И факт доходит до чистого решения.
        Assert.Contains("enginePresent, snapshot.State.IsRunning, snapshot.Starting,", app, StringComparison.Ordinal);

        // 5. ПОСЛЕ ПОПЫТКИ проверка спрашивается СНОВА: иначе настоящую неудачу этого запуска
        // человек не увидит — окно показывается один раз, и второго случая не будет.
        Assert.Contains("ShowFirstRunAfterStart(autoStart, paths, log, human, isolated)", app, StringComparison.Ordinal);
    }

    // --- DEF-064: один снимок, правильный порядок и сторож состояния -------------

    /// <summary>
    /// `STARTING` ЧИТАЕТСЯ ПОСЛЕ ОБНОВЛЕНИЯ СОСТОЯНИЯ (DEF-064) — иначе решение видит устаревшее
    /// «наш процесс умер» и зовёт окно о сервере, который ещё поднимается.
    ///
    /// **Как устроено доказательство.** Подставной сервер выставляет «идёт попытка» САМИМ
    /// обновлением (`TryRefresh`) — ровно как настоящий контроллер выставляет `_engineStillStarting`.
    /// Если снимок прочитал `Starting` раньше, попытки он не видит, и причина становится
    /// `ExitedSilent` — то есть тот самый ложный «не поднялся» из журнала 25.10.2026.
    ///
    /// **Мутация, которая обязана упасть:** M1 «прочитать `Starting` до `TryRefresh`».
    /// </summary>
    [Fact]
    public void Медленный_старт_читается_после_обновления_состояния()
    {
        var server = new SlowStartServer();

        var snapshot = FirstRunDecisions.ServerSnapshot(server);

        Assert.True(
            snapshot.Starting,
            "`Starting` прочитан ДО `TryRefresh` — попытка старта не видна (DEF-064)");

        var problem = FirstRunDecisions.Reason(
            enginePresent: true,
            running: snapshot.State.IsRunning,
            starting: snapshot.Starting,
            snapshot.State.Presence,
            server.Owner,
            snapshot.State.Detail);

        Assert.Null(problem);
        Assert.Empty(FirstRunDecisions.Rows(
            new FirstRunFacts(true, problem, KeyFound: true, KeyReadable: true)));
    }

    /// <summary>
    /// ФАКТЫ ЧИТАЮТСЯ РОВНО ОДИН РАЗ, И ЭТОТ ЖЕ СНИМОК УХОДИТ ОКНУ (DEF-064).
    ///
    /// **От какого случая.** Решение брало одно чтение, окно — второе; между ними `TryRefresh`
    /// переставлял факты, окно открывалось по непустому списку и рисовалось пустым, а в журнал
    /// уходила строка «окно первого запуска показано: » без слов.
    ///
    /// **Мутация, которая обязана упасть:** M2 «открывать окно по второму чтению» — вызов окна
    /// без готового снимка (`window.Attach(door, null, _server)`) роняет ворота по исходникам,
    /// а второе чтение в `Decide` — счётчик обращений.
    /// </summary>
    [Fact]
    public void Окно_первого_запуска_читает_факты_один_раз()
    {
        var reads = 0;
        var facts = new FirstRunFactsDoor(() =>
        {
            reads++;
            return Clean();
        });

        var decision = FirstRunDecisions.Decide(humanLaunch: true, isolated: false, shownAt: "", facts);

        Assert.Equal(1, reads);
        Assert.True(decision.Show);
        Assert.Equal(new[] { FirstRunRowKind.EngineMissing, FirstRunRowKind.KeyMissing }, decision.Rows);

        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var app = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "App.axaml.cs"));

        // Решение — по одному снимку, окно получает ЕГО ЖЕ, а не читает дверь заново.
        Assert.Contains("FirstRunDecisions.Decide(human, isolated, shownAt, door)", app, StringComparison.Ordinal);
        Assert.Contains("window.Attach(door, decision.Facts, _server)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Rows(door.Read())", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОКНО БЕЗ СТРОК НЕ ОТКРЫВАЕТСЯ: `Attach` отвечает «говорить нечего», и вызывающий не
    /// показывает окно и не пишет пустую строку «окно первого запуска показано: » (DEF-064).
    ///
    /// **Мутация, которая обязана упасть:** M3 «показывать окно с нулём строк» — `Attach`,
    /// всегда отвечающий `true`.
    /// </summary>
    [AvaloniaFact]
    public void Окно_без_строк_не_открывается()
    {
        var window = new FirstRunWindow();

        try
        {
            Assert.False(window.Attach(new FirstRunFactsDoor(Ready), Ready()));
            Assert.Empty(window.Rows);
        }
        finally
        {
            window.Close();
        }

        // А с непустым снимком — открывается и пишет в журнал именно то, что показывает.
        var shown = new FirstRunWindow();

        try
        {
            var facts = Clean();

            Assert.True(shown.Attach(new FirstRunFactsDoor(() => facts), facts));
            Assert.Equal(2, shown.Rows.Count);
            Assert.Contains(PanelStrings.FirstRunServerMissingEngineTitle, shown.ShownLogLine, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.FirstRunKeyMissingTitle, shown.ShownLogLine, StringComparison.Ordinal);
        }
        finally
        {
            shown.Close();
        }

        // И вызывающий обязан спросить у окна «есть ли что сказать» до показа.
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var app = File.ReadAllText(Path.Combine(root!, "src", "DshPanel", "App.axaml.cs"));

        Assert.Contains("if (!window.Attach(door, decision.Facts, _server)) return false;", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОКНО САМО СЛЕДИТ ЗА СЕРВЕРОМ (DEF-064): подписка на <see cref="IServerControl.Changed"/>
    /// (сторож состояния, а не таймер), закрытие после ответа сервера и перерисовка на настоящей
    /// неудаче. Подписка снимается вместе с окном.
    ///
    /// **Мутации, которые обязаны упасть:** «не подписываться» (счётчик 0) · «не закрываться после
    /// ответа» · «не снимать подписку при закрытии».
    /// </summary>
    [AvaloniaFact]
    public void Окно_следит_за_сервером_и_закрывается_само()
    {
        var facts = Clean();
        var server = new WatchingServer();
        var window = new FirstRunWindow();

        try
        {
            Assert.True(window.Attach(new FirstRunFactsDoor(() => facts), facts, server));
            window.Show();
            Settle();

            Assert.True(window.WatchesServer, "окно не запомнило сервер при наполнении");
            Assert.True(server.Subscribers == 1, $"окно не подписалось на состояние сервера: {server.Subscribers}");

            // СЕРВЕР ОТВЕТИЛ — строк больше нет, и окно закрывается САМО: «окна нет или оно
            // закрылось» — второе из двух, и это не таймер, а сообщение о смене состояния.
            facts = Ready();
            server.Raise();
            Settle();

            Assert.False(window.IsVisible, "окно осталось открытым, хотя говорить больше нечего");
            Assert.True(server.Subscribers == 0, $"подписка не снята при закрытии: {server.Subscribers}");
        }
        finally
        {
            window.Close();
        }

        // НАСТОЯЩАЯ НЕУДАЧА — окно остаётся и перерисовывается С ПРИЧИНОЙ: это то же окно,
        // второго не появляется.
        facts = new FirstRunFacts(
            EnginePresent: true, ServerProblem: FirstRunReason.ExitedSilent,
            KeyFound: true, KeyReadable: true);

        var failed = new FirstRunWindow();

        try
        {
            Assert.True(failed.Attach(new FirstRunFactsDoor(() => facts), facts, server));
            failed.Show();
            Settle();

            Assert.True(server.Subscribers == 1, $"второе окно не подписалось: {server.Subscribers}");

            // Факты обновились настоящей неудачей — перерисовка на месте, без «Проверить снова».
            server.Raise();
            Settle();

            Assert.True(failed.IsVisible, "настоящая неудача не повод закрывать окно");
            Assert.Equal(new[] { FirstRunRowKind.ServerNotStarted }, failed.Rows);
            Assert.Contains(
                PanelStrings.FirstRunReasonExitedSilent,
                Texts(failed),
                StringComparison.Ordinal);
        }
        finally
        {
            failed.Close();
        }

        Assert.True(server.Subscribers == 0, $"подписка пережила закрытие окна: {server.Subscribers}");
    }

    // --- помощники DEF-064 ----------------------------------------------------

    /// <summary>Подставной сервер: всё, кроме нужного проверке, молчит и ничего не делает.</summary>
    private abstract class ServerStub : IServerControl
    {
        public ServerState State => ServerState.Stopped(0);

        public ServerOwner Owner => ServerOwner.Panel;

        public bool ConsentRemembered => false;

        public virtual bool Starting => false;

        public virtual bool TryRefresh(out ServerState state)
        {
            state = Refresh();
            return true;
        }

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public ServerState RecoverOwnServer(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan() => DiscoveryResult.Nothing;

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;

        public string EntryLink => string.Empty;

        public virtual event Action? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// СЕРВЕР, У КОТОРОГО «ИДЁТ ПОПЫТКА» ПОЯВЛЯЕТСЯ ОТ САМОГО ОБНОВЛЕНИЯ — как у настоящего
    /// контроллера (`TryRefresh` выставляет `_engineStillStarting`). Только так проверяется порядок.
    /// </summary>
    private sealed class SlowStartServer : ServerStub
    {
        private bool _attemptLive;

        public override bool Starting => _attemptLive;

        public override bool TryRefresh(out ServerState state)
        {
            _attemptLive = true;
            state = State;
            return true;
        }
    }

    /// <summary>СЕРВЕР, КОТОРЫЙ УМЕЕТ СООБЩАТЬ О СМЕНЕ СОСТОЯНИЯ и считает подписчиков.</summary>
    private sealed class WatchingServer : ServerStub
    {
        private Action? _changed;

        /// <summary>Сколько обработчиков подписано СЕЙЧАС — по списку вызовов, а не счётчиком ±1:
        /// панель снимает подписку перед тем, как поставить, и «минус один на пустом» соврал бы.</summary>
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public override event Action? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void Raise() => _changed?.Invoke();
    }
}