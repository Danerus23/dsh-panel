using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки ГЛАВНОГО ОКНА: низ его содержимого достижим, а двери — на месте.
///
/// Дефект Д2 (живой просмотр владельца 26.09.2026): с найденным чужим сервером карточка
/// «Найден работающий DSH…» удлиняла содержимое, окно 860x720 обрезало его на середине кнопки
/// копий, а «О программе…» была недостижима вовсе — прокрутки не было.
///
/// ⚠️ Почему это не поймал кадр `--shot`: съёмка идёт ПРОГОНОМ ПРОВЕРКИ, где чужого сервера нет,
/// то есть проверка проходит через состояние, в котором дефект невозможен. Поэтому окно здесь
/// строится С найденным сервером, а достижимость мерится на НЕСКОЛЬКИХ высотах, включая
/// минимальную: на 720 контент может влезать целиком, и тогда «снять прокрутку» прошло бы
/// незамеченным.
/// </summary>
public class MainWindowLayoutTests
{
    /// <summary>Сервер, который панель не поднимала, — ровно тот случай, на котором окно и обрезало контент.</summary>
    private sealed class FakeServer : IServerControl
    {
        public ServerState State { get; set; } = ServerState.Stopped(0);

        public ServerOwner Owner => ServerOwner.None;

        public bool ConsentRemembered => false;

        /// <summary>Ссылка входа (в ней токен). Пусто — панель её ещё не знает.</summary>
        public string EntryLink { get; set; } = string.Empty;

        public FoundServer? Found { get; set; }

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan() => new(
            true,
            Found is null ? Array.Empty<FoundServer>() : new[] { Found.Value });

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }

    /// <summary>Дать вёрстке и очереди диспетчера отработать до конца: без этого Bounds ещё нулевые.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Окно панели С найденным чужим сервером — то состояние, в котором дефект и возможен.</summary>
    private static (MainWindow Window, ScrollViewer Scroll) Found(double? height = null)
    {
        var window = new MainWindow();
        if (height is not null) window.Height = height.Value;

        window.Attach(new FakeServer { Found = new FoundServer(ServerDecisions.OwnerPort, 19804, "node") });
        window.RefreshNow();
        window.Show();
        Settle();

        var scroll = window.FindControl<ScrollViewer>("MainContentScroll");
        Assert.NotNull(scroll);

        // Само состояние обязано быть тем самым: карточка найденного сервера видна.
        var panel = window.FindControl<Border>("FoundServerPanel");
        Assert.NotNull(panel);
        Assert.True(panel!.IsVisible, "карточка найденного сервера не показана — проверка прошла не через то состояние");

        return (window, scroll!);
    }

    [AvaloniaTheory]
    [InlineData(480)]
    [InlineData(600)]
    [InlineData(720)]
    public void Низ_содержимого_достижим_на_любой_высоте_окна(int height)
    {
        var (window, scroll) = Found(height);

        var extent = scroll.Extent.Height;
        var viewport = scroll.Viewport.Height;

        // ДВЕРИ ШАПКИ ВИДНЫ СРАЗУ — они в начале содержимого, и до них прокручивать нечего
        // (решение владельца 27.09.2026: «Копии…» и «Настройки…» стоят в шапке).
        foreach (var name in new[] { "BackupsButton", "SettingsButton" })
        {
            var door = window.FindControl<Button>(name);
            Assert.NotNull(door);

            var top = door!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(top);

            var bottom = top!.Value.Y + door.Bounds.Height;
            var where = $"высота окна {height}, «{name}»: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

            Assert.True(door.IsVisible, $"«{name}» не показана — {where}");
            Assert.True(top.Value.Y >= -0.5, $"«{name}» выше видимой области — {where}");
            Assert.True(bottom <= window.ClientSize.Height + 0.5, $"«{name}» не видна без прокрутки — {where}");
        }

        // Прокрутка в самый низ — ровно так это делает человек колесом.
        scroll.Offset = new Vector(0, Math.Max(0, extent - viewport));
        Settle();

        // А НИЗ СОДЕРЖИМОГО достижим прокруткой: на минимальной высоте окна это и есть его путь
        // к человеку. Нижний элемент — обновление баланса в последней карточке (прежде здесь
        // мерилась дверь «О программе», но она уехала в настройки, и мерить надо то, что
        // в главном окне осталось НИЖНИМ).
        foreach (var name in new[] { "RefreshBalanceButton" })
        {
            var button = window.FindControl<Button>(name);
            Assert.NotNull(button);

            var top = button!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(top);

            var bottom = top!.Value.Y + button.Bounds.Height;
            var where = $"высота окна {height}, окно {window.ClientSize.Height:0.#}, " +
                        $"Extent={extent:0.#}, Viewport={viewport:0.#}, " +
                        $"«{name}»: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

            Assert.True(button.IsVisible, $"«{name}» не показана — {where}");
            Assert.True(top.Value.Y >= -0.5, $"«{name}» выше видимой области — {where}");
            Assert.True(bottom <= window.ClientSize.Height + 0.5, $"«{name}» не достижима — {where}");
        }
    }

    /// <summary>
    /// На минимальной высоте прокрутка делает настоящую работу: содержимое длиннее окна.
    /// Без этого утверждения «кнопки внутри видимой области» выполнялось бы само собой и молчало
    /// о том, что прокручивать нечего.
    /// </summary>
    [AvaloniaFact]
    public void На_минимальной_высоте_содержимое_длиннее_окна_и_прокрутка_вертикальная()
    {
        var (_, scroll) = Found(480);

        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.True(
            scroll.Extent.Height > scroll.Viewport.Height,
            $"прокручивать нечего: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");
    }

    /// <summary>
    /// НОВАЯ КНОПКА ВМЕЩАЕТСЯ в окно на минимальной ширине — и в ряду сервера, и вместе
    /// с нижним рядом дверей. Дефект Д2 был ровно про это: ряд рос с каждой новой дверью,
    /// и на 640 содержимое переставало вмещаться. Поэтому ряд кнопок сервера ПЕРЕНОСИМЫЙ,
    /// а проверка мерит не «нарисовалось ли», а «внутри ли окна по обеим осям».
    /// </summary>
    [AvaloniaTheory]
    [InlineData(960)]
    [InlineData(640)]
    public void Кнопка_открыть_панель_вмещается_на_минимальной_ширине(double width)
    {
        var window = new MainWindow { Width = width, Height = 480 };

        // Ссылка известна и сервер отвечает — то состояние, в котором кнопка доступна и человек
        // её видит. Раньше этого состояния в проверке не было вовсе.
        window.Attach(new FakeServer
        {
            State = new ServerState(ServerPresence.Running, 3080, 42, "node", "отвечает"),
            EntryLink = "http://127.0.0.1:3080/?token=SECRET-TOKEN-42",
        });

        window.RefreshNow();
        window.Show();
        Settle();

        var scroll = window.FindControl<ScrollViewer>("MainContentScroll");
        Assert.NotNull(scroll);

        scroll!.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        Settle();

        foreach (var name in new[] { "OpenAgentButton", "BackupsButton" })
        {
            var button = window.FindControl<Button>(name);
            Assert.NotNull(button);

            var topLeft = button!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(topLeft);

            var where = $"ширина окна {width}, Extent={scroll.Extent.Width:0.#}, " +
                        $"Viewport={scroll.Viewport.Width:0.#}, «{name}»: X {topLeft.Value.X:0.#}, ширина {button.Bounds.Width:0.#}";

            Assert.True(button.IsVisible, $"«{name}» не показана — {where}");
            Assert.True(topLeft.Value.X >= -0.5, $"«{name}» левее видимой области — {where}");
            Assert.True(button.Bounds.Width > 0, $"«{name}» без ширины — {where}");

            Assert.True(
                topLeft.Value.X + button.Bounds.Width <= window.ClientSize.Width + 0.5,
                $"«{name}» не вмещается в окно — {where}");
        }
    }

    /// <summary>
    /// Ряд кнопок сервера ПЕРЕНОСИМЫЙ и содержит ТОЛЬКО действия с сервером.
    ///
    /// Прежняя причина осталась: ряд растёт с каждой новой дверью, а минимальная ширина окна —
    /// 640, и жёсткий ряд однажды перестал бы вмещаться (дефект Д2). А с 27.09.2026 у ряда есть
    /// и второе правило — решение владельца: «кнопка настройки не должна быть в одном ряду
    /// с кнопками управления сервером». Здесь проверяется и то, и другое; «рядом с „Запустить“
    /// нет ни настроек, ни копий» отдельно сторожит <c>WindowDoorsTests</c>.
    /// </summary>
    [AvaloniaFact]
    public void Ряд_кнопок_сервера_переносимый_и_только_про_сервер()
    {
        var window = new MainWindow();

        var start = window.FindControl<Button>("StartServerButton");
        Assert.NotNull(start);

        var row = start!.Parent as WrapPanel;
        Assert.NotNull(row);
        Assert.Equal(Orientation.Horizontal, row!.Orientation);

        // Все четыре действия с сервером — соседи в одном ряду, а не в разных углах окна.
        foreach (var name in new[] { "RestartServerButton", "StopServerButton", "DetachServerButton" })
        {
            Assert.Same(row, window.FindControl<Button>(name)!.Parent);
        }
    }

    /// <summary>
    /// ПОКА ИДЁТ АВТОПОДЪЁМ, ОКНО ГОВОРИТ, ЧТО ПАНЕЛЬ РАБОТАЕТ: то же состояние, что при нажатии
    /// «Запустить», и кнопки заблокированы.
    ///
    /// Зачем это человеку. Подъём идёт в фоне и держит замок контроллера, поэтому окно до его конца
    /// не обновляется; первый запуск в чистом профиле достраивается около минуты, и без этого
    /// человек минуту смотрел бы на «Сервер не запущен» — то есть решил бы, что панель сломалась.
    /// После перезагрузки он смотрит ровно на это.
    /// </summary>
    [AvaloniaFact]
    public void Пока_идёт_подъём_окно_показывает_что_работает()
    {
        var window = new MainWindow();
        window.Attach(new FakeServer { State = ServerState.Stopped(0) });
        window.Show();
        Settle();

        var status = window.FindControl<TextBlock>("ServerStatusText");
        var detail = window.FindControl<TextBlock>("ServerDetailText");
        var start = window.FindControl<Button>("StartServerButton");
        var stop = window.FindControl<Button>("StopServerButton");

        Assert.NotNull(status);
        Assert.NotNull(detail);
        Assert.NotNull(start);
        Assert.NotNull(stop);

        // Обычное состояние: сервер не запущен, «Запустить» доступна.
        Assert.Equal(PanelStrings.ServerStopped, status!.Text);
        Assert.True(start!.IsEnabled);

        // Начался автоподъём — окно показывает то же, что при нажатии «Запустить».
        window.BeginServerStart(PanelStrings.ServerStarting);
        Settle();

        Assert.Equal(PanelStrings.ServerStarting, status.Text);
        Assert.Equal(PanelStrings.ServerStartingDetail, detail!.Text);
        Assert.False(start.IsEnabled);
        Assert.False(stop!.IsEnabled);

        // Подъём кончился (чем бы ни кончился: здесь — отказом) — окно снова про настоящее,
        // и кнопки разблокированы. Занятость, пережившая неудачу, оставила бы окно мёртвым.
        window.EndServerStart();
        window.RefreshNow();
        Settle();

        Assert.Equal(PanelStrings.ServerStopped, status.Text);
        Assert.True(start.IsEnabled);
    }

    /// <summary>
    /// Карточки копий в главном окне нет (решение владельца 26.09.2026 — окно про оперативную
    /// информацию, карточка дублировала раздел настроек), а ДВЕРЬ осталась: копия — действие.
    /// </summary>
    [AvaloniaFact]
    public void Карточки_копий_в_главном_окне_нет_а_дверь_осталась()
    {
        var window = new MainWindow();

        Assert.Null(window.FindControl<Control>("BackupsTitleText"));
        Assert.Null(window.FindControl<Control>("BackupsNoteText"));
        Assert.NotNull(window.FindControl<Button>("BackupsButton"));
    }

    /// <summary>
    /// ПОДПИСИ ОБЕИХ ДВЕРЕЙ — ИЗ СТРОК ПАНЕЛИ, и щелчок по каждой поднимает СВОЮ просьбу
    /// ровно один раз (ворота «нет текста в разметке» имена кнопок не защищают: имя совпадает
    /// с <c>x:Name</c>, и подпись обязана подтверждать проверка).
    ///
    /// Где какая дверь стоит — <c>WindowDoorsTests</c>; здесь — что они подписаны и работают.
    /// Прежнее имя проверки говорило про «нижний ряд»: с 27.09.2026 нижнего РЯДА нет, двери
    /// разведены по своим местам, а «О программе…» из этого окна уехала в настройки.
    /// </summary>
    [AvaloniaFact]
    public void Двери_подписаны_строками_и_каждый_щелчок_поднимает_просьбу_один_раз()
    {
        var window = new MainWindow();

        var settings = window.FindControl<Button>("SettingsButton");
        var backups = window.FindControl<Button>("BackupsButton");

        Assert.NotNull(settings);
        Assert.NotNull(backups);

        Assert.Equal(PanelStrings.SettingsMenuText, PanelTestStand.Label(settings!));
        Assert.Equal(PanelStrings.BackupsButton, PanelTestStand.Label(backups!));

        var requested = 0;

        window.SettingsRequested += () => requested++;
        window.BackupsRequested += () => requested++;

        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        backups.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(2, requested);
    }

    /// <summary>
    /// Умолчания ядра размещения и разметка окна — одно и то же число. Иначе панель без
    /// сохранённой геометрии открывалась бы «не того» размера, а окно меньше собственного предела
    /// всё равно не станет, и обещание проверки разошлось бы с делом.
    /// </summary>
    [AvaloniaFact]
    public void Умолчания_ядра_размещения_совпадают_с_разметкой_окна()
    {
        var window = new MainWindow();

        Assert.Equal((double)WindowPlacement.MinWidth, window.MinWidth);
        Assert.Equal((double)WindowPlacement.MinHeight, window.MinHeight);
        Assert.Equal((double)WindowPlacement.DefaultWidth, window.Width);
        Assert.Equal((double)WindowPlacement.DefaultHeight, window.Height);
    }

    /// <summary>
    /// Окно принимает восстановленный прямоугольник и отдаёт его обратно тем же (масштаб
    /// в проверке единичный). Это и есть связка «сохранили — поставили»: без неё ядро
    /// размещения было бы правильным, а окно стояло бы не там.
    /// </summary>
    [AvaloniaFact]
    public void Окно_принимает_восстановленную_геометрию_и_отдаёт_её_же()
    {
        var window = new MainWindow();

        // Экраны спрашиваются ДО показа — ровно так это делает связка (`App.RestorePlacement`).
        // Если бы окно отдавало их только показанным, панель открывалась бы с умолчанием размера
        // и в углу, а «запомнили — поставили» не работало бы.
        var areas = window.Screens.All.Select(screen => screen.WorkingArea).ToArray();
        Assert.NotEmpty(areas);

        var frame = WindowPlacement.Restore(120, 80, 900, 700, areas);

        window.AttachPlacement(frame);
        window.Show();
        Settle();

        Assert.Equal(frame, window.CurrentFrame());
    }

    /// <summary>
    /// Крестик и «Выход» — оба пути закрытия — проходят через запоминание геометрии: связка
    /// вешает его на <see cref="PanelWindow.BeforeClosing"/>, и это последнее мгновение, когда
    /// окно стоит там, где его оставил человек.
    /// </summary>
    [AvaloniaFact]
    public void Геометрия_запоминается_перед_тем_как_окно_спрятать_и_перед_выходом()
    {
        var window = new MainWindow { Height = 600, Width = 800 };
        window.Show();
        Settle();

        var saved = 0;
        var panel = new PanelWindow(window, isolated: false, _ => { }) { BeforeClosing = () => saved++ };

        panel.CloseAsUser();

        Assert.False(panel.IsVisible, "крестик обязан спрятать окно, а не закрыть панель");
        Assert.Equal(1, saved);

        panel.CloseForExit();

        Assert.Equal(2, saved);
    }
}
