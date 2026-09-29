using System.ComponentModel;
using System.Runtime.InteropServices;
using DshPanel.Platform;

namespace DshPanel.Shell;

/// <summary>
/// Единый экземпляр панели и просьба «покажи окно».
///
/// Зачем это вообще нужно. Панель живёт в трее, окна при запуске не показывает. Человек,
/// забывший об этом, запускает ярлык второй раз — и что должно произойти? В v1 происходило
/// правильное: вторая панель не заводилась, а первая показывала своё окно. Здесь то же самое,
/// но устройство проще и без единого байта личных данных: замок на имя + своё скрытое окно-приёмник.
///
/// Почему окно, а не файл или канал. Значок в трее у панели уже живёт на скрытом окне, и насос
/// сообщений Avalonia раздаёт сообщения чужим окнам — это проверено самотестом трея. Значит
/// второй запуск может просто постучать в окно первой панели, и ничего изобретать не нужно.
/// Имя класса окна ФИКСИРОВАННОЕ (в отличие от окна трея): иначе второму запуску нечего искать.
/// </summary>
public sealed class InstanceSignal : IDisposable
{
    /// <summary>
    /// Замок «панель уже работает». Префикс <c>Local\</c> — на сеанс пользователя: два человека
    /// на одной машине могут держать каждый свою панель, и это не ошибка.
    /// </summary>
    public const string DefaultMutexName = @"Local\DshPanel2.Panel";

    /// <summary>
    /// Начало имени класса окна-приёмника. ПОЛНОЕ имя вычисляется из имени замка
    /// (<see cref="ClassNameFor"/>), а не берётся одно на всех, — и это не украшение:
    ///
    /// 1. **проверки.** Проба единого экземпляра заводит СВОЙ замок (уникальное имя), чтобы
    ///    проверить «никто не слушает». С общим именем класса окно-приёмник искалось у ВСЕХ
    ///    процессов сразу, и проверка краснела оттого, что рядом работает хоть какая-то панель:
    ///    на живом просмотре 26.09.2026 она падала ровно так. Чей приёмник откликнулся в тот раз,
    ///    не установлено; известно, что просьбы «покажи окно» журнал панели владельца НЕ видел
    ///    (а он пишет её всегда, когда получает). Подробности — `docs\HISTORY.md`;
    /// 2. **изолированный прогон** со своим именем замка (`DSH_PANEL_INSTANCE_MUTEX`) по той же
    ///    причине не должен стучаться в панель владельца.
    ///
    /// Для НАСТОЯЩИХ запусков имя замка одно и то же (<see cref="DefaultMutexName"/>), поэтому
    /// и класс один и тот же — второй запуск ярлыка по-прежнему находит первую панель.
    /// Общего с окном трея ничего не имеет — то имя случайно и живёт своей жизнью.
    /// </summary>
    public const string SignalClassPrefix = "DshPanel2InstanceSignal";

    /// <summary>
    /// Имя класса окна-приёмника для этого имени замка. ЧИСТАЯ и ДЕТЕРМИНИРОВАННАЯ: два процесса
    /// с одним именем замка обязаны получить одно имя класса (иначе второй запуск ярлыка не найдёт
    /// первую панель), а с разными именами — разные. Поэтому хеш имени, а не счётчик и не GUID.
    /// </summary>
    public static string ClassNameFor(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(mutexName));

        return SignalClassPrefix + "_" + Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Имя переменной окружения, которой проба единого экземпляра передаёт имя замка
    /// дочернему процессу. Переменной, а не ключом: так же поступает изоляция, и по той же
    /// причине — дочерний процесс обязан получить тот же контекст, а не догадаться о нём.
    /// </summary>
    public const string MutexNameVariable = "DSH_PANEL_INSTANCE_MUTEX";

    /// <summary>Наше сообщение: «покажи окно». Лежит выше WM_USER, чтобы не пересечься с системными.</summary>
    private const uint ShowPanelMessage = 0x0400 + 2048;

    private const uint WM_CLOSE = 0x0010;

    private readonly string _mutexName;
    private readonly string _className;
    private readonly NativeMethods.WndProcDelegate _wndProc;

    private Mutex? _mutex;
    private IntPtr _hwnd;
    private ushort _classAtom;
    private bool _classRegistered;
    private bool _disposed;

    public InstanceSignal(string mutexName = DefaultMutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        _mutexName = mutexName;
        _className = ClassNameFor(mutexName);
        _wndProc = WndProc;
    }

    /// <summary>Первый ли это запуск. У второго экземпляра — <c>false</c>.</summary>
    public bool IsFirst { get; private set; }

    /// <summary>Первая панель получила просьбу показать окно.</summary>
    public event Action? ShowPanelRequested;

    /// <summary>
    /// Попытаться стать первой панелью. <c>false</c> — панель уже работает: тогда окно-приёмник
    /// не создаётся, а вызывающий обязан позвать <see cref="RequestShowPanel"/> и выйти.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // createdNew == false означает, что замок с таким именем уже существует: значит, панель
        // работает. Мёртвая панель замка не оставляет — её дескриптор закрывается вместе с процессом.
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);
        IsFirst = createdNew;

        if (IsFirst) EnsureWindow();
        return IsFirst;
    }

    /// <summary>
    /// Попросить работающую панель показать окно. <c>false</c> — панель не отозвалась:
    /// окна-приёмника нет. Это не «мелочь»: молчаливый выход второго запуска выглядел бы
    /// как «ярлык не работает», поэтому вызывающий обязан сказать об этом вслух.
    /// </summary>
    public bool RequestShowPanel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = NativeMethods.FindWindowEx(
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, _className, null);

        if (target == IntPtr.Zero) return false;

        return NativeMethods.PostMessage(target, ShowPanelMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Разобрать очередь сообщений НАШЕГО потока, пока не придёт просьба или не выйдет время.
    /// Нужно пробам: у них нет цикла панели, который раздал бы сообщения сам.
    /// Возвращает <c>true</c>, если за это время просьба пришла.
    /// </summary>
    public bool PumpUntilShowRequested(TimeSpan timeout)
    {
        var before = ShowRequests;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
                if (ShowRequests > before) return true;
                continue;
            }

            Thread.Sleep(10);
        }

        return ShowRequests > before;
    }

    /// <summary>Сколько раз приходила просьба показать окно (для отчётов проверок).</summary>
    public int ShowRequests { get; private set; }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _className,
        };

        _classAtom = NativeMethods.RegisterClassEx(ref wc);
        _classRegistered = _classAtom != 0;

        if (!_classRegistered)
        {
            // Класс уже занят в этом процессе (повторный TryAcquire без Dispose) — берём окно
            // по имени, а не падаем: замок уже наш, и второго окна быть не должно.
            var existing = NativeMethods.FindWindowEx(
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, _className, null);

            if (existing != IntPtr.Zero)
            {
                _hwnd = existing;
                return;
            }

            throw new Win32Exception(
                Marshal.GetLastWin32Error(), "не удалось зарегистрировать класс окна-приёмника");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, _className, "DSH Panel signal", 0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось создать окно-приёмник");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == ShowPanelMessage)
        {
            ShowRequests++;
            ShowPanelRequested?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == WM_CLOSE)
        {
            NativeMethods.DestroyWindow(hWnd);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classRegistered)
        {
            NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
            _classRegistered = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
