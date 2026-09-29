using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Tray;

namespace DshPanel.Shell;

/// <summary>
/// Самотест связки «значок ↔ окно» — того, что человек и называет панелью. Отвечает на три
/// вопроса, которые иначе проверяются только руками или не проверяются вовсе:
///   1) щелчок по значку открывает окно;
///   2) закрытие окна прячет его в трей, а не завершает панель;
///   3) «Выход» завершает панель.
///
/// Щелчок здесь **настоящий на всём пути, кроме самого щелчка**: в окно трея посылается ровно
/// то сообщение <c>WM_LBUTTONUP</c>, которое присылает оболочка Windows, и дальше всё идёт
/// своим чередом — WndProc, событие, показ окна через предикат изоляции. Не проверено этим
/// ровно одно: что оболочка посылает именно это сообщение, когда человек щёлкает по значку.
/// Это остаётся за человеком, и об этом честно сказано в `README.md`.
///
/// Работает с УЖЕ ПОСТРОЕННОЙ панелью и не строит её сам: окно нельзя создать в колбэке
/// <c>StartWithClassicDesktopLifetime</c> — тот вызывается до подъёма платформы. Поймано
/// этим же самотестом 24.09.2026: «Unable to locate 'Avalonia.Platform.IWindowingPlatform'».
///
/// Каждая строка отчёта влияет на исход: ноль — успех, ненулевой код — провал.
/// </summary>
public sealed class PanelShellSelfTest
{
    private readonly List<string> _log = new();

    private bool _failed;
    private bool _exitRequested;
    private int _finished;

    public PanelShellSelfTest(int seconds) => Seconds = seconds;

    /// <summary>Сколько ждать между посылкой щелчка и проверкой: насосу сообщений надо успеть.</summary>
    public int Seconds { get; }

    /// <summary>Куда панель пишет журнал в этом прогоне. Список в памяти, а не файл владельца.</summary>
    public void CollectLog(string line) => _log.Add(line);

    /// <summary>Подключиться к построенной панели, послать щелчок и проверить всё остальное.</summary>
    /// <param name="server">
    /// Контроллер сервера ЖИВОЙ панели. Нужен здесь ради одного утверждения, и оно не украшение:
    /// прогон проверки идёт ОБЫЧНЫМ путём панели, значит через него проходит и АВТОПОДЪЁМ сервера
    /// (решение владельца 26.09.2026). Проверка обязана подтвердить, что панель проверки
    /// не подняла НИЧЕГО: иначе «право только у человека» осталось бы словом, а прогон проверки
    /// на машине владельца занял бы порт и оставил за собой движок.
    /// </param>
    public void Attach(TrayIconHost tray, PanelWindow window, PanelShell shell, IServerControl server)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(server);

        StartWatchdog();

        Check("значок поставлен", tray.IsAdded);

        // Отсюда и дальше — ВСЁ асинхронно, и это принципиально. Проверка подключается
        // до того, как цикл приложения запустился, а каркас показывает своё главное окно
        // сам, уже внутри цикла. Синхронная проверка «до щелчка окно спрятано» в этот момент
        // зеленела бы всегда: окна ещё нет ни у кого. Поймано мутацией 24.09.2026 — при
        // выключенном показе окна по щелчку проверка «щелчок открыл окно» всё равно проходила.
        var phase = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Seconds) };
        timer.Tick += (_, _) =>
        {
            if (phase++ == 0)
            {
                Check("до щелчка окно спрятано", !window.IsVisible);

                try
                {
                    tray.PostClickForSelfTest();
                    Note("щелчок по значку послан");
                }
                catch (Exception ex)
                {
                    Fail($"ОШИБКА посылки щелчка: {ex.GetType().Name} — {ex.Message}");
                    timer.Stop();
                    _exitRequested = true;
                    shell.Exit();
                }

                return;
            }

            timer.Stop();

            Check("щелчок открыл окно", window.IsVisible);

            // Человек закрыл окно крестиком. Закрытие обрабатывается синхронно, поэтому
            // проверять можно сразу за ним.
            window.CloseAsUser();

            Check("закрытие окна спрятало его в трей", !window.IsVisible);
            Check("после закрытия окна значок на месте", tray.IsAdded);
            Check("журнал: показ окна записан", _log.Contains(PanelStrings.WindowShownLog));
            Check("журнал: уход в трей записан", _log.Contains(PanelStrings.WindowHiddenLog));

            // ЗУБЫ ОБВЯЗКИ АВТОПОДЪЁМА. Прогон проверки идёт обычным путём панели, и без этих
            // трёх утверждений «право поднимать сервер только у человека» держалось бы на слове.
            //
            // Почему проверяются ТРИ вещи, а не одна. Признак «сервер наш» (`Owner`) на машине
            // владельца остался бы `None` и у СЛОМАННОГО кода: рядом всегда работает его живой DSH,
            // замок от второго движка честно срабатывает, и до запуска дело не доходит. То есть
            // признак проходит через состояние, в котором дефект возможен, НЕ ВСЕГДА. Поэтому
            // рядом стоит утверждение про ЖУРНАЛ: решение автоподъёма обязано быть записано,
            // и записано как ОТКАЗ. Строка «автоподъём включён» в прогоне проверки — это и есть
            // сломанное право, видимое независимо от того, что творится на портах.
            Check("прогон проверки сервер не поднял: панель ничем не управляет",
                server.Owner == ServerOwner.None);
            Check("прогон проверки сервер не поднял: панель не заняла ни одного порта",
                server.State.Port == 0);
            Check("журнал: автоподъём отказал прогону проверки",
                _log.Any(line => line.Contains(PanelStrings.AutoStartRefusedCheckRun, StringComparison.Ordinal)));
            Check("журнал: автоподъём не включался вовсе",
                !_log.Any(line => line.Contains(PanelStrings.AutoStartEnabledLog, StringComparison.Ordinal)));

            _exitRequested = true;
            shell.Exit();
        };
        timer.Start();
    }

    /// <summary>
    /// Итог. Зовётся ПОСЛЕ того, как цикл приложения завершился: если он завершился, значит
    /// «Выход» действительно погасил панель, а не оставил её висеть в трее.
    /// </summary>
    public int Verdict()
    {
        Interlocked.Exchange(ref _finished, 1);

        // Строки уже напечатаны по мере появления (см. Report) — здесь только журнал и вердикт:
        // печатать их дважды значило бы удваивать отчёт.
        Check("«Выход» завершил панель", _exitRequested);

        foreach (var line in _log) Console.WriteLine("ЖУРНАЛ| " + line);
        Console.WriteLine(_failed ? "SHELL ПРОВАЛ" : "SHELL УСПЕХ");
        return _failed ? 1 : 0;
    }

    private void Check(string what, bool ok) => Report($"{what} = {ok}", ok);

    private void Fail(string line) => Report(line, ok: false);

    /// <summary>Строка без вердикта: просто «что делаем».</summary>
    private void Note(string line) => Console.WriteLine("SHELL| " + line);

    /// <summary>
    /// Записать строку отчёта и СРАЗУ её напечатать.
    ///
    /// Почему сразу, а не «в конце, как раньше»: проверка живёт в цикле сообщений, и если цикл
    /// не погаснет, отчёт не напечатается вовсе — а без него непонятно, где именно застряло.
    /// Поймано прогоном на чистой ВМ 24.09.2026: `--shell-selftest` в сессии 0 не завершился,
    /// и на выходе не было НИ ОДНОЙ строки.
    /// </summary>
    private void Report(string line, bool ok)
    {
        if (!ok) _failed = true;
        Console.WriteLine("SHELL| " + line);
    }

    /// <summary>
    /// Сторож: сколько проверка имеет права длиться. С запасом на две фазы и на медленную машину.
    /// </summary>
    public TimeSpan Watchdog => TimeSpan.FromSeconds(Math.Max(25, Seconds * 6 + 20));

    /// <summary>
    /// Сторож на ОТДЕЛЬНОМ потоке — намеренно, а не таймером: таймер живёт в том же цикле
    /// сообщений, который и может застрять. Сторож обязан сработать именно тогда, когда всё
    /// остальное не работает, поэтому он спит в своём потоке и выходит через Environment.Exit.
    /// Код 3 — «проверка не завершилась», и он отличается от 1 («провал») и 2 («неприменимо»).
    /// </summary>
    private void StartWatchdog()
    {
        var limit = Watchdog;
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(limit);
            if (Volatile.Read(ref _finished) != 0) return;

            Console.WriteLine(
                $"SHELL| ПРОВЕРКА НЕ ЗАВЕРШИЛАСЬ за {limit.TotalSeconds:F0} с — цикл панели не погас");
            Console.WriteLine("SHELL ЗАВИС");
            Environment.Exit(3);
        })
        {
            IsBackground = true,
            Name = "shell-selftest-watchdog",
        };

        watchdog.Start();
    }
}
