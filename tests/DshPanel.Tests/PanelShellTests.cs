using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Isolation;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки связки «значок ↔ окно» без экрана и без значка.
///
/// Значок здесь не поднимается намеренно: он живёт на рабочем столе владельца, и проверка
/// не имеет права его туда ставить. Проверяется то, что от значка не зависит: состав меню,
/// поведение окна (крестик прячет, «Выход» закрывает) и решение изоляции о показе.
/// Сам путь щелчка по значку проверяет `--shell-selftest` на собранном exe.
/// </summary>
public class PanelShellTests
{
    // ------------------------------------------------------------------ меню

    /// <summary>
    /// Состав меню: ЧЕТЫРЕ строки состояния, разделитель, семь дверей и выход последним.
    ///
    /// Строки состояния — не пункты: серая строка в меню Windows значит «показать можно,
    /// нажать нельзя». Сделай её нажимаемой, и человек однажды нажал бы «Сервер: работает»
    /// и не понял бы, что произошло, — поэтому здесь это и сторожится.
    ///
    /// ⚠️ Четвёртая строка (обновление) и две двери обновления встали в этот же ряд третьим шагом
    /// работы обновления. Порядок назван здесь целиком: перепутанные местами состояния выглядели бы
    /// ровно так же, как правильные, если проверять «где-то есть».
    /// </summary>
    [Fact]
    public void Меню_значка_это_четыре_строки_состояния_и_семь_дверей()
    {
        var shown = 0;
        var opened = 0;
        var settings = 0;
        var about = 0;
        var checkedNow = 0;
        var updateWindow = 0;
        var exited = 0;

        var status = new TrayStatusLines(
            new TrayStatusLine("СОСТОЯНИЕ-СЕРВЕР", TrayTone.Good),
            new TrayStatusLine("СОСТОЯНИЕ-АГЕНТ", TrayTone.Warning),
            new TrayStatusLine("СОСТОЯНИЕ-ТАРИФ", TrayTone.Bad),
            new TrayStatusLine("СОСТОЯНИЕ-ОБНОВЛЕНИЕ", TrayTone.Neutral));

        var menu = PanelMenu.Build(
            status: () => status,
            show: () => shown++,
            openAgent: () => opened++,
            settings: () => settings++,
            about: () => about++,
            checkUpdate: () => checkedNow++,
            updateWindow: () => updateWindow++,
            exit: () => exited++);

        Assert.Equal(13, menu.Items.Count);

        // Шапка: сервер, агент с балансом, тариф, обновление — в этом порядке и первыми.
        Assert.Equal(status.Server.Text, menu.Items[0].Text);
        Assert.Equal(status.Agent.Text, menu.Items[1].Text);
        Assert.Equal(status.Peak.Text, menu.Items[2].Text);
        Assert.Equal(status.Update.Text, menu.Items[3].Text);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(menu.Items[i].IsSeparator);
            Assert.False(menu.Items[i].IsEnabled, "строка состояния обязана быть серой");
            Assert.Null(menu.Items[i].Invoke);      // и ничего не делать
            Assert.False(menu.Items[i].IsDefault);  // и не быть жирной
            Assert.False(menu.Items[i].IsChecked);
        }

        // Тон строки доходит до меню ЦЕЛИКОМ и по порядку: перепутанные тона выглядели бы
        // ровно так же, как правильные, если проверять только «тон где-то есть».
        Assert.Equal(TrayTone.Good, menu.Items[0].Tone);
        Assert.Equal(TrayTone.Warning, menu.Items[1].Tone);
        Assert.Equal(TrayTone.Bad, menu.Items[2].Tone);
        Assert.Equal(TrayTone.Neutral, menu.Items[3].Tone);

        // Разделитель: состояние отделено от дверей.
        Assert.True(menu.Items[4].IsSeparator);
        Assert.Null(menu.Items[4].Invoke);

        var show = menu.Items[5];
        Assert.False(show.IsSeparator);
        Assert.Equal(PanelStrings.ShowPanelText, show.Text);
        // Жирный пункт по умолчанию: двойной щелчок по значку обязан делать то же самое.
        Assert.True(show.IsDefault);
        Assert.True(show.IsEnabled);

        // У КОМАНД тона нет — и это не мелочь: тон означает «строку рисуем своими руками»,
        // и попавший в команды он превратил бы нажимаемый пункт в нарисованную картинку,
        // которая ничего не делает (щелчок перестал бы доходить до человека).
        foreach (var command in new[]
                 {
                     menu.Items[5], menu.Items[6], menu.Items[7], menu.Items[8],
                     menu.Items[9], menu.Items[10], menu.Items[12],
                 })
        {
            Assert.Null(command.Tone);
        }

        // «Открыть панель в браузере» — сразу за показом окна: это второе, что человек
        // делает с панелью. Подпись взята у кнопки главного окна: дверь одна, и тексты разошлись бы.
        Assert.Equal(PanelStrings.OpenAgentButton, menu.Items[6].Text);
        Assert.Equal(PanelStrings.SettingsMenuText, menu.Items[7].Text);

        // Дверь в «О программе» — после настроек. Порядок назван владельцем,
        // и выход обязан остаться последним и отделённым.
        Assert.Equal(PanelStrings.AboutButton, menu.Items[8].Text);

        // ДВЕ ДВЕРИ ОБНОВЛЕНИЯ, и они разные по смыслу: «Проверить обновления» делает работу
        // (идёт в сеть), «Обновление панели…» открывает окно с тем, что нашлось.
        Assert.Equal(PanelStrings.CheckUpdateMenuText, menu.Items[9].Text);
        Assert.Equal(PanelStrings.UpdateWindowMenuText, menu.Items[10].Text);

        Assert.True(menu.Items[11].IsSeparator);
        // У разделителя нечего вызывать: если он что-то делает, это ошибка сборки меню.
        Assert.Null(menu.Items[11].Invoke);

        Assert.Equal(PanelStrings.ExitText, menu.Items[12].Text);

        // Каждый пункт вызывается и попадает в СВОЮ цель. Проверять только состав мало:
        // перепутанные местами действия выглядели бы точно так же.
        show.Invoke!();
        Assert.Equal(1, shown);
        Assert.Equal(0, opened);
        Assert.Equal(0, settings);
        Assert.Equal(0, about);
        Assert.Equal(0, checkedNow);
        Assert.Equal(0, updateWindow);
        Assert.Equal(0, exited);

        menu.Items[6].Invoke!();
        Assert.Equal(1, opened);
        Assert.Equal(0, settings);
        Assert.Equal(0, about);
        Assert.Equal(0, checkedNow);
        Assert.Equal(0, updateWindow);
        Assert.Equal(0, exited);

        menu.Items[7].Invoke!();
        Assert.Equal(1, settings);
        Assert.Equal(0, about);
        Assert.Equal(0, checkedNow);
        Assert.Equal(0, exited);

        menu.Items[8].Invoke!();
        Assert.Equal(1, about);
        Assert.Equal(0, checkedNow);
        Assert.Equal(0, updateWindow);
        Assert.Equal(0, exited);

        // «Проверить обновления» идёт в СВОЮ цель, а не открывает окно: это два разных пункта,
        // и перепутать их значило бы заставлять человека ждать окна ради ответа «новее ничего нет».
        menu.Items[9].Invoke!();
        Assert.Equal(1, checkedNow);
        Assert.Equal(0, updateWindow);
        Assert.Equal(0, exited);

        menu.Items[10].Invoke!();
        Assert.Equal(1, checkedNow);
        Assert.Equal(1, updateWindow);
        Assert.Equal(0, exited);

        menu.Items[12].Invoke!();
        Assert.Equal(1, shown);
        Assert.Equal(1, opened);
        Assert.Equal(1, settings);
        Assert.Equal(1, about);
        Assert.Equal(1, exited);
    }
    /// <summary>
    /// Строки состояния собираются НА КАЖДЫЙ показ меню, а не один раз при старте панели.
    ///
    /// Это главное обещание этой правки: состояние меняется (сервер погасили, баланс обновился),
    /// а снимок, собранный при запуске, врал бы про сервер весь день. Проверяется через тот же
    /// путь, которым меню берёт значок (<see cref="TrayIconHost.MenuForShow"/>) — саму
    /// <c>ShowMenu</c> в тестах не позвать: она открывает настоящее всплывающее меню на рабочем
    /// столе владельца.
    ///
    /// Значок при этом НЕ ставится: <see cref="TrayIconHost"/> создаёт окно и значок только
    /// в <c>Show()</c>, а здесь его не зовут.
    /// </summary>
    [Fact]
    public void Строки_состояния_собираются_на_каждый_показ_меню()
    {
        using var tray = new TrayIconHost("проверка");

        var built = 0;
        tray.SetMenuProvider(() => new TrayMenu(new[]
        {
            TrayMenuItem.Info($"строка {++built}", TrayTone.Good),
        }));

        Assert.Equal("строка 1", tray.MenuForShow()!.Items[0].Text);
        Assert.Equal("строка 2", tray.MenuForShow()!.Items[0].Text);
        Assert.Equal(2, built);

        // Снимок, поставленный SetMenu, наоборот, НЕ пересобирается — и провайдер после него
        // молчит: у меню не бывает двух источников сразу.
        var snapshot = new TrayMenu(new[] { TrayMenuItem.Info("снимок", TrayTone.Neutral) });
        tray.SetMenu(snapshot);

        Assert.Same(snapshot, tray.MenuForShow());
        Assert.Same(snapshot, tray.MenuForShow());
        Assert.Equal(2, built);
    }

    /// <summary>
    /// Дверь в «О программе» в изолированном прогоне МОЛЧИТ: прогон проверки не показывает
    /// владельцу ничего (красная линия 8). Проверяется чистым предикатом, потому что связку
    /// со значком в тестах не поднять — значок встаёт на рабочий стол владельца.
    /// </summary>
    [Fact]
    public void В_изолированном_прогоне_окно_о_программе_не_показывается()
    {
        Assert.False(PanelShell.ShouldShowAbout(isolatedRun: true));
        Assert.True(PanelShell.ShouldShowAbout(isolatedRun: false));
    }

    // ------------------------------------------------------- «Открыть панель»: отказ виден человеку

    /// <summary>
    /// ОТКАЗ БРАУЗЕРА ВИДИТ ЧЕЛОВЕК, а не только журнал: он нажал кнопку (или пункт меню) и ждёт
    /// ответа. Проверка идёт через тот же путь, что и щелчок, — <see cref="PanelShell.OpenAndTell"/>,
    /// с подставным «браузером», который не смог.
    ///
    /// ⚠️ И тут же главное про секрет: в сообщении НЕТ ссылки. В ней токен входа, и показать её
    /// человеку нельзя ни текстом, ни подсказкой (красная линия 7).
    /// </summary>
    [Fact]
    public void Отказ_браузера_уходит_человеку_и_без_ссылки()
    {
        const string Url = "http://127.0.0.1:3080/?token=SECRET-TOKEN-42";

        var notices = new List<(NoticeKind Kind, string Title, string Text)>();
        var log = new List<string>();

        var opened = PanelShell.OpenAndTell(
            Url,
            mayOpenBrowser: true,
            opener: _ => "Win32Exception — нет приложения по умолчанию",
            log.Add,
            (kind, title, text) => notices.Add((kind, title, text)));

        Assert.False(opened);

        var notice = Assert.Single(notices);
        Assert.Equal(NoticeKind.Error, notice.Kind);
        Assert.Equal(PanelStrings.OpenAgentFailedTitle, notice.Title);
        Assert.Contains("нет приложения", notice.Text, StringComparison.Ordinal);

        // Ссылки и токена в сообщении нет ни в каком виде.
        Assert.DoesNotContain("SECRET-TOKEN-42", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", notice.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", notice.Text, StringComparison.OrdinalIgnoreCase);

        // И в журнале её тоже нет — там только причина отказа.
        Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN-42", StringComparison.Ordinal));
    }

    /// <summary>
    /// Открылось — сообщать нечего: окно браузера человек и так видит. Проверка сторожит обратное
    /// первой: без неё «сообщение ушло» доказывало бы лишь то, что оно уходит ВСЕГДА.
    /// </summary>
    [Fact]
    public void Удачное_открытие_человека_не_тревожит()
    {
        const string Url = "http://127.0.0.1:3080/?token=SECRET-TOKEN-42";

        var notices = 0;
        var log = new List<string>();
        var opened = new List<string>();

        var ok = PanelShell.OpenAndTell(
            Url,
            mayOpenBrowser: true,
            opener: url => { opened.Add(url); return string.Empty; },
            log.Add,
            (_, _, _) => notices++);

        Assert.True(ok);
        Assert.Equal(0, notices);
        Assert.Equal(new[] { Url }, opened);
    }

    /// <summary>
    /// ПРОГОН ПРОВЕРКИ не открывает браузер — шов не зовут вовсе, и ссылка тут НЕПУСТАЯ:
    /// пустой ссылкой предохранитель проверялся бы случайностью. А сообщение о неудаче уходит
    /// в ОБЩУЮ ДВЕРЬ сообщений, и гасит его она же: второго места, где решается «показывать ли
    /// владельцу», в панели нет (красная линия 8). Подавленное решение уходит строкой в журнал.
    /// </summary>
    [Fact]
    public void Прогон_проверки_не_зовёт_браузер_а_сообщение_гасит_дверь()
    {
        var notices = 0;
        var opened = 0;
        var log = new List<string>();

        var ok = PanelShell.OpenAndTell(
            "http://127.0.0.1:3080/?token=SECRET-TOKEN-42",
            mayOpenBrowser: false,
            opener: _ => { opened++; return string.Empty; },
            log.Add,
            (_, _, _) => notices++);

        Assert.False(ok);
        Assert.Equal(0, opened);
        Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));

        // Дверь сообщений решение САМА: в изоляции молчит каждый вид — значит и это сообщение
        // до рабочего стола владельца не дойдёт, хотя в дверь его отдали.
        Assert.Equal(1, notices);
        Assert.False(IsolationRules.ShouldNotify(NoticeKind.Error, isolatedRun: true));
        Assert.True(IsolationRules.ShouldNotify(NoticeKind.Error, isolatedRun: false));
    }

    // ------------------------------------------------------- место вспомогательных окон

    /// <summary>
    /// МЕСТО ОКНА СЧИТАЕТСЯ В ОДНОМ МЕСТЕ, А НЕ РАЗМЕТКОЙ КАЖДОГО ОКНА.
    ///
    /// Замечание владельца 28.09.2026: *«когда открываешь окно, оно открывается далеко от основной
    /// панели и приходится тянуться мышью»*. Владельцем принято: панель на экране — окно рядом
    /// с ней, панель в трее — у курсора, и всегда в границах рабочего стола того экрана, где оно
    /// появляется.
    ///
    /// ⚠️ Проверка читает РАЗМЕТКУ ШЕСТИ окон: пока `WindowStartupLocation` стоял там, ответов
    /// на один вопрос было шесть, и «рядом с панелью» не получалось ни у одного. Число названо
    /// списком, а не условием «во всех файлах»: у окон-ДИАЛОГОВ (`ConfirmStopWindow`,
    /// `BackupStopWindow`, `UnsavedChangesWindow`) `CenterOwner` — законный ответ, они модальны
    /// и открываются НАД своим родителем, а не рядом с панелью.
    /// </summary>
    [Fact]
    public void Окна_панели_не_задают_место_в_разметке()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        foreach (var name in new[]
                 {
                     "SettingsWindow.axaml", "BackupWindow.axaml", "PeakWindow.axaml", "AboutWindow.axaml",
                     "UpdateWindow.axaml", "PricingHistoryWindow.axaml",
                 })
        {
            var path = Path.Combine(root, "src", "DshPanel", "Views", name);
            Assert.True(File.Exists(path), $"нет разметки окна панели: {path}");

            var markup = File.ReadAllText(path);

            // Комментарии разметки не считаются — тем же разбором, что у ворот «нет текста
            // в разметке»: в них свойство названо по имени (и это объяснение, а не значение).
            Assert.DoesNotContain(
                "WindowStartupLocation", SourceStringsTests.WithoutComments(markup), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// И все ШЕСТЬ дверей зовут ОДНО И ТО ЖЕ решение о месте: слот окон сам места не знает,
    /// ему его дают (<see cref="PanelShell.Place"/>). Проверка читает связку: дверь, забывшая
    /// передать решение, была бы ровно тем, от чего эта работа и заведена, — окном, появляющимся
    /// там, где решит Windows.
    ///
    /// ⚠️ Список назван ЯВНО, а не «все вызовы `Open` в файле»: так проверка падает и тогда,
    /// когда новую дверь забыли и в список, и в общее решение.
    /// </summary>
    [Fact]
    public void Все_двери_зовут_общее_решение_о_месте()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var path = Path.Combine(root, "src", "DshPanel", "Shell", "PanelShell.cs");
        Assert.True(File.Exists(path), $"нет связки окон панели: {path}");

        var text = File.ReadAllText(path);

        foreach (var door in new[]
                 {
                     "_settings.Open(_createSettings, Place)",
                     "_backups.Open(_createBackups, Place)",
                     "_peaks.Open(_createPeaks, Place)",
                     "_about.Open(_createAbout, Place)",
                     "_update.Open(_createUpdate, Place)",
                     "_pricingHistory.Open(_createPricingHistory, Place)",
                 })
        {
            Assert.Contains(door, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ОКНО СТАВИТСЯ ТУДА, КУДА РЕШИЛО ЯДРО, И РЕШЕНИЕ ПРИНИМАЕТСЯ ДО ПОКАЗА.
    ///
    /// Живых фактов у проверки нет — ни панели, ни курсора, ни второго монитора, поэтому они
    /// приходят подставными через <see cref="PanelShell.Place"/>. Но проверяется не «функция
    /// посчитала», а «окно оказалось именно там»: между решением и окном есть один шаг — присвоение
    /// места, — и он обязан быть проверен прогоном.
    /// </summary>
    [AvaloniaFact]
    public void Окно_ставится_туда_куда_решило_ядро()
    {
        var screens = new[] { new PixelRect(0, 0, 1920, 1080) };
        var panel = new PixelRect(100, 120, 860, 720);

        var atPanel = new Window { Width = 860, Height = 720 };
        PanelShell.Place(atPanel, panel, cursor: null, screens, scale: 1);

        // Число названо прямо, а не только «равно ядру»: так проверка падает и тогда, когда ядро
        // вернёт координаты по умолчанию (0, 0).
        // ⚠️ С 29.09.2026 окно встаёт ПОВЕРХ панели, по её центру (решение владельца: «хочу чтобы
        // окна открывались поверх основной панели»), а не рядом с ней — прежнее правило владелец
        // отверг: сбоку от панели окно уезжало к краю рабочего стола.
        Assert.Equal(panel.X + (panel.Width / 2), atPanel.Position.X + (atPanel.Width / 2));
        Assert.Equal(panel.Y + (panel.Height / 2), atPanel.Position.Y + (atPanel.Height / 2));

        // Панель в трее — окно у курсора.
        var atCursor = new Window { Width = 860, Height = 720 };
        PanelShell.Place(atCursor, panel: null, new PixelPoint(300, 200), screens, scale: 1);

        Assert.Equal(300, atCursor.Position.X);
        Assert.Equal(200, atCursor.Position.Y);

        // Курсор у нижнего края — окно ПОДТЯГИВАЕТСЯ внутрь: 400 + 720 вышло бы за экран 1080.
        var pulled = new Window { Width = 860, Height = 720 };
        PanelShell.Place(pulled, panel: null, new PixelPoint(300, 400), screens, scale: 1);

        Assert.Equal(1080 - 720, pulled.Position.Y);

        // DPI учтён и в МЕСТЕ, а не только в размере: на экране «200 %» окно вдвое шире, и «по центру»
        // означает другую координату. Без учёта масштаба вышло бы (1920-860)/2 = 530.
        var dpi = new Window { Width = 860, Height = 720 };
        PanelShell.Place(dpi, panel: null, cursor: null, screens, scale: 2);

        Assert.Equal((1920 - 1720) / 2, dpi.Position.X);
        Assert.NotEqual((1920 - 860) / 2, dpi.Position.X);
    }

    /// <summary>
    /// ОДНО ОКНО НА ПАНЕЛЬ, И ПОВТОРНАЯ ПРОСЬБА ЕГО НЕ ДВИГАЕТ. Место считается ровно один раз —
    /// когда окно построено; у уже открытого окна вызов <c>Activate</c>, а не пересчёт места: иначе
    /// окно выдёргивалось бы из-под руки на каждый щелчок по двери.
    /// </summary>
    [AvaloniaFact]
    public void Дверь_ставит_окно_один_раз_а_повторная_просьба_его_не_двигает()
    {
        var slot = new SingleWindowSlot();
        var placed = 0;

        Window? built = null;
        Window Create() => built = new Window { Width = 120, Height = 80 };

        Assert.True(slot.Open(Create, _ => placed++), "первая просьба обязана построить окно");
        Assert.True(slot.IsOpen);
        Assert.Equal(1, placed);

        Assert.False(slot.Open(Create, _ => placed++), "повторная просьба выводит на передний план уже открытое");
        Assert.Equal(1, placed);

        // Закрыли — следующий щелчок строит НОВОЕ окно и снова ставит его: место считается
        // для каждого построенного окна, иначе второе открытие уехало бы куда попало.
        built!.Close();
        Assert.False(slot.IsOpen);

        Assert.True(slot.Open(Create, _ => placed++));
        Assert.Equal(2, placed);

        slot.Close();
    }

    // ------------------------------------------------------------------ окно

    [AvaloniaFact]
    public void Крестик_прячет_окно_в_трей_а_не_завершает_панель()
    {
        var log = new List<string>();
        var window = new MainWindow();
        using var panel = new PanelWindow(window, isolated: false, log.Add);

        Assert.True(panel.Show(), "окно обязано показаться");
        Assert.True(panel.IsVisible, "окно показано, а себя видимым не считает");

        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();

        Assert.False(panel.IsVisible, "крестик обязан спрятать окно");
        Assert.False(closed, "окно закрылось по-настоящему: панель в трее осталась бы без окна и без выхода");
        Assert.Contains(log, line => line == PanelStrings.WindowHiddenLog);
    }

    [AvaloniaFact]
    public void Выход_закрывает_окно_по_настоящему()
    {
        var log = new List<string>();
        var window = new MainWindow();
        using var panel = new PanelWindow(window, isolated: false, log.Add);
        panel.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        panel.CloseForExit();

        Assert.True(closed, "при выходе окно обязано закрыться, а не спрятаться");
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void Изолированный_прогон_окна_не_показывает()
    {
        var log = new List<string>();
        var window = new MainWindow();
        using var panel = new PanelWindow(window, isolated: true, log.Add);

        Assert.False(panel.Show(), "в изолированном прогоне показывать владельцу нечего");
        Assert.False(panel.IsVisible, "окно показалось, хотя прогон изолированный");
        Assert.Contains(log, line => line == PanelStrings.WindowSuppressedLog);
    }

    [AvaloniaFact]
    public void Без_значка_закрытие_окна_завершает_панель()
    {
        // Случай, ради которого у окна есть HideOnClose: значок не встал, и «окно без значка»
        // обязано закрываться крестиком — иначе панель нечем завершить.
        var window = new MainWindow();
        using var panel = new PanelWindow(window, isolated: false, _ => { });
        panel.HideOnClose = false;
        panel.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();

        Assert.True(closed, "без значка крестик обязан закрывать окно, а не прятать его");
    }
}
