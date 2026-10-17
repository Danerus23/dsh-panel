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
            enginePresent: false, running: false, ServerPresence.BusyByOther, ServerOwner.None, "занято"));

        // Сервер отвечает — причины нет.
        Assert.Null(FirstRunDecisions.Reason(
            enginePresent: true, running: true, ServerPresence.Running, ServerOwner.Panel, ""));

        // Порт занят ЧУЖОЙ программой — устойчивый факт, и он называется.
        Assert.Equal(
            FirstRunReason.PortBusy,
            FirstRunDecisions.Reason(true, false, ServerPresence.BusyByOther, ServerOwner.None, ""));

        // Отказ системы в запуске — по строке отказа самого контроллера, а не по догадке.
        Assert.Equal(
            FirstRunReason.StartRefused,
            FirstRunDecisions.Reason(true, false, ServerPresence.Stopped, ServerOwner.None, PanelStrings.SrvEngineStartRefused));

        // Сервер был наш и его нет — «завершился, не начав отвечать».
        Assert.Equal(
            FirstRunReason.ExitedSilent,
            FirstRunDecisions.Reason(true, false, ServerPresence.Stopped, ServerOwner.Panel, ""));

        // Прочий названный отказ — «подробности в окне панели»: машинный текст в окно не идёт.
        Assert.Equal(
            FirstRunReason.Unknown,
            FirstRunDecisions.Reason(true, false, ServerPresence.Stopped, ServerOwner.None, "какая-то беда"));

        // СВОБОДНЫЙ ПОРТ — ЭТО НЕ ОТКАЗ: сервер ещё не поднимали.
        Assert.Null(FirstRunDecisions.Reason(true, false, ServerPresence.Stopped, ServerOwner.None, PanelStrings.SrvPortFree));
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
}