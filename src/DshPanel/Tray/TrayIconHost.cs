using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using DshPanel.Platform;
using DshPanel.Shell;

namespace DshPanel.Tray;

public enum TrayNotificationKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Свой значок в трее на Win32 — без Avalonia.
///
/// Зачем свой, а не <c>Avalonia.Controls.TrayIcon</c>: у её реализации нет ни одного
/// метода уведомлений (проверено по сборке 23.09.2026), а достать её внутреннее окно
/// и номер значка можно только отражением в приватные поля — это ломается при любом
/// обновлении библиотеки. Трей для этой панели — не украшение, а суть, поэтому он наш.
///
/// Устройство: скрытое окно «только для сообщений» (HWND_MESSAGE) принимает обратные
/// вызовы от оболочки. Насос сообщений общий с Avalonia — сообщения чужих окон она
/// раздаёт через DispatchMessage, поэтому отдельный цикл не нужен. Это проверяется
/// самотестом: <see cref="PostSelfTestMessage"/> и <see cref="SelfTestMessageReceived"/>.
///
/// <para><b>Версия интерфейса — legacy (0), и это осознанно.</b> Современная
/// NOTIFYICON_VERSION_4 меняет формат обратного вызова: событие приходит в младшем слове
/// lParam, а не целиком. Переход на неё включал бы <c>NIF_SHOWTIP</c> (подсказка в
/// «стандартном» виде), но при этом затрагивает обработку щелчков и меню — а их
/// автоматически проверить нельзя, только человеком. Менять непроверяемое вслепую ради
/// косметического флага — плохой размен. Если понадобится версия 4, это отдельная задача
/// с ручной проверкой: щелчок, двойной щелчок, правый щелчок и пункты меню.</para>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int IconId = 1;

    /// <summary>Номер нашего сообщения-вызова. Лежит выше WM_USER, чтобы не пересечься с системными.</summary>
    private const uint CallbackMessage = 0x0400 + 1024;
    private const uint SelfTestMessage = 0x0400 + 1025;
    private const uint WM_NULL = 0x0000;

    // Делегат обязан жить в поле: если его соберёт сборщик мусора, окно останется
    // с висячим указателем на WndProc и процесс упадёт при первом же сообщении.
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly uint _taskbarCreatedMessage;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private ushort _classAtom;
    private bool _iconAdded;
    private bool _disposed;

    /// <summary>Значок БЕЗ огонька: то, что собрала Windows из кадра (или системный запасной).</summary>
    private IntPtr _baseIcon;

    /// <summary>Наш ли <see cref="_baseIcon"/> — системный запасной освобождать нельзя.</summary>
    private bool _ownBaseIcon;

    /// <summary>Какой тон нарисован на значке сейчас.</summary>
    private TrayTone _iconTone = TrayTone.Neutral;

    /// <summary>Ставился ли тон вообще: до первого вызова значок показывается как есть.</summary>
    private bool _toneSet;

    /// <summary>Рисовальщик строк состояния открытого меню. Живёт ровно столько, сколько меню.</summary>
    private TrayMenuDraw? _menuDraw;

    private TrayMenu? _menu;
    private Func<TrayMenu>? _menuProvider;
    private string _toolTip;

    /// <summary>Номер таймера самотеста меню. Один на процесс: таймер ставится на наше окно.</summary>
    private const int SelfTestMenuTimerId = 1;

    /// <summary>Сколько держать меню открытым в самотесте. Ноль — обычный показ, без таймера.</summary>
    private int _selfTestMenuHoldMs;

    /// <summary>Дескриптор меню, которое сейчас открыто, — им самотест считает пункты.</summary>
    private IntPtr _showingMenu;

    /// <summary>
    /// Чем кончилась последняя сборка меню: пусто — всё как задумано; иначе причина, по которой
    /// строки состояния пришлось оставить системными серыми (не собрался рисовальщик).
    ///
    /// Нужна читателю — самотесту трея: отступление обязано быть видно словами, а не молчанием,
    /// иначе «меню открылось» ничего не говорит о том, КАК оно выглядело.
    /// </summary>
    internal string LastMenuNotice { get; private set; } = string.Empty;

    public TrayIconHost(string toolTipText = "DSH Panel")
    {
        _toolTip = toolTipText;
        _wndProc = WndProc;
        _className = "DshPanelTrayIconHost_" + Guid.NewGuid().ToString("N");
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Щелчок левой кнопкой по значку — обычно «показать панель».</summary>
    public event Action? Clicked;

    public event Action? DoubleClicked;

    /// <summary>Человек щёлкнул по всплывшему уведомлению.</summary>
    public event Action? NotificationClicked;

    /// <summary>Пришло ли наше собственное сообщение: доказывает, что насос сообщений работает.</summary>
    public bool SelfTestMessageReceived { get; private set; }

    public bool IsAdded => _iconAdded;

    /// <summary>Создаёт скрытое окно и ставит значок. Повторный вызов безопасен.</summary>
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow();
        AddIcon();
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        DeleteIcon();
    }

    public void SetToolTip(string text)
    {
        _toolTip = text;
        if (_iconAdded) ModifyIcon(NativeMethods.NIF_TIP);
    }

    /// <summary>Запасной системный значок — чтобы трей работал даже без файла значка.</summary>
    public void SetDefaultIcon()
    {
        var handle = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось получить системный значок");
        SetBaseIcon(handle, ownsHandle: false);
    }

    /// <summary>
    /// Поставить НАШ значок — тот, что вшит в сборку вместе с панелью (решение владельца
    /// 26.09.2026: «значок свой, не системный»). Панель раздаётся ОДНИМ `exe`, рядом с ней
    /// ни `.ico`, ни папки `assets` нет, поэтому значок берётся из байтов сборки, а не с диска
    /// (`<see cref="TrayIconSource"/>` — там же разбор, почему не Win32-ресурс exe).
    ///
    /// Возвращает строку для журнала: она называет и кадр, и причину, если пришлось отступить.
    /// <b>Отступление — не ошибка:</b> трей обязан работать всегда, поэтому любая неудача
    /// заканчивается системным запасным значком, а не исключением.
    /// </summary>
    public string SetOwnIcon() => SetIconFromOwnBytes(TrayIconSource.LoadEmbedded());

    /// <summary>
    /// То же, но байты значка приходят снаружи — шов для проверок: «значка нет» и «в значке мусор»
    /// обязаны проверяться прогоном, а не рассуждением. Размер берётся у системы: на мониторе
    /// с масштабом «16 на глаз» даёт мыло (та же причина, что у <see cref="SetIconFromFile"/>).
    /// </summary>
    internal string SetIconFromOwnBytes(byte[]? ico)
    {
        var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
        if (cx <= 0) cx = 16;
        if (cy <= 0) cy = 16;

        if (TrayIconSource.TryLoad(ico, cx, cy, out var handle, out var detail))
        {
            SetBaseIcon(handle, ownsHandle: true);
            return string.Format(CultureInfo.CurrentCulture, PanelStrings.TrayIconOwnFormat, detail);
        }

        SetDefaultIcon();
        return string.Format(CultureInfo.CurrentCulture, PanelStrings.TrayIconFallbackFormat, detail);
    }

    /// <summary>
    /// Поставить ОГОНЁК СОСТОЯНИЯ в угол значка — тот же язык, что у строк в меню: зелёный,
    /// красный, серый (решение владельца 27.09.2026).
    ///
    /// **Повторный вызов с тем же тоном оболочку не трогает вовсе** и возвращает пустую строку:
    /// состояние панель перечитывает раз в секунду, и без этой двери Windows получала бы
    /// <c>NIM_MODIFY</c> каждую секунду, а значок мигал бы на каждом обновлении.
    ///
    /// **Отступление вместо падения:** не собрался огонёк (нет канала прозрачности, не читаются
    /// точки, отказала Windows) — ставится ПРЕЖНИЙ значок, а причина возвращается строкой
    /// в журнал. Значок панели узнаваем и без огонька, терять его из-за точки в углу незачем.
    /// </summary>
    /// <returns>Строку для журнала или пусто, если говорить нечего.</returns>
    public string SetIconTone(TrayTone tone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_toneSet && tone == _iconTone) return string.Empty;

        _iconTone = tone;
        _toneSet = true;
        ToneAppliedCount++;

        return ApplyBaseIcon();
    }

    /// <summary>
    /// Сколько раз значок ПЕРЕСОБИРАЛСЯ из-за смены тона. Нужен проверкам: «оболочку не дёргаем
    /// зря» — это утверждение о числе подмен, а не о намерении.
    /// </summary>
    internal int ToneAppliedCount { get; private set; }

    /// <summary>
    /// Дескриптор значка, который сейчас у трея. Нужен проверкам: «наш значок» и «системный
    /// запасной» — разные утверждения, и различить их можно только по дескриптору.
    /// </summary>
    internal IntPtr IconHandle => _hIcon;

    private void SetIconHandle(IntPtr handle, bool ownsHandle)
    {
        var old = _hIcon;
        var oldOwned = _ownIcon;
        _hIcon = handle;
        _ownIcon = ownsHandle;

        if (_iconAdded) ModifyIcon(NativeMethods.NIF_ICON);

        // Прежний значок освобождаем ПОСЛЕ подмены, иначе Windows останется с мёртвым дескриптором.
        if (old != IntPtr.Zero && oldOwned && old != handle) NativeMethods.DestroyIcon(old);
    }

    /// <summary>
    /// Поставить ОСНОВУ значка (без огонька) и сразу показать её с тем тоном, который просили.
    ///
    /// Основа живёт отдельно от того, что видит оболочка: при смене тона значок собирается заново
    /// ИЗ НЕЁ, поэтому кадр значка не приходится доставать из сборки второй раз. Прежняя основа
    /// освобождается здесь же — но только ПОСЛЕ подмены и только если она наша.
    /// </summary>
    private void SetBaseIcon(IntPtr handle, bool ownsHandle)
    {
        var old = _baseIcon;
        var oldOwned = _ownBaseIcon;

        _baseIcon = handle;
        _ownBaseIcon = ownsHandle;

        ApplyBaseIcon();

        if (old != IntPtr.Zero && oldOwned && old != _baseIcon && old != _hIcon) NativeMethods.DestroyIcon(old);
    }

    /// <summary>
    /// Показать основу с огоньком. Возвращает строку для журнала или пусто.
    /// Отдельным методом, потому что зовётся из ДВУХ мест: когда пришла основа и когда сменился тон.
    /// </summary>
    private string ApplyBaseIcon()
    {
        if (_baseIcon == IntPtr.Zero) return string.Empty;

        // Тона ещё не просили — показываем значок КАК ЕСТЬ. Это не только экономия: панель ставит
        // значок и только потом говорит, какое у неё состояние, и придуманный за неё серый кружок
        // мелькал бы на каждом запуске.
        if (!_toneSet)
        {
            SetIconHandle(_baseIcon, ownsHandle: false);
            return string.Empty;
        }

        if (TrayIconOverlay.TryBuild(_baseIcon, _iconTone, out var handle, out var detail))
        {
            SetIconHandle(handle, ownsHandle: true);
            return string.Empty;
        }

        SetIconHandle(_baseIcon, ownsHandle: false);
        return string.Format(CultureInfo.CurrentCulture, PanelStrings.TrayIconDotFailedFormat, detail);
    }

    /// <summary>
    /// Меню, которое не меняется: снимок, собранный один раз. Так его ставят проверки
    /// (<c>--tray-selftest</c>) — им состояние показывать нечего.
    /// </summary>
    public void SetMenu(TrayMenu menu)
    {
        _menu = menu;
        _menuProvider = null;
    }

    /// <summary>
    /// Меню, которое СОБИРАЕТСЯ НА КАЖДЫЙ ПОКАЗ.
    ///
    /// Зачем: меню значка показывает состояние (сервер, агент, тариф), а состояние меняется.
    /// Снимок, собранный при старте панели, врал бы весь день — «сервер работает» и после того,
    /// как человек его погасил, и наоборот. Провайдер зовётся ровно в тот миг, когда человек
    /// щёлкнул правой кнопкой, поэтому строки всегда свежие.
    ///
    /// ⚠️ Зовётся на нитке, которая обрабатывает сообщение трея, — то есть на нитке интерфейса.
    /// </summary>
    public void SetMenuProvider(Func<TrayMenu> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _menuProvider = provider;
        _menu = null;
    }

    /// <summary>
    /// Открыть меню ТАК ЖЕ, как это делает правый щелчок человека (то же сообщение от оболочки),
    /// и закрыть его через <paramref name="holdMs"/> самому.
    ///
    /// Нужен ровно одному читателю — самотесту трея: меню значка до 27.09.2026 не было снято
    /// ни разу (Windows прячет значок в «^», а всплывающее окно закрывается от любого промаха),
    /// и «состав меню проверен по коду» было единственным, что о нём знали. Отсюда и закрытие
    /// САМИМ СОБОЙ: проверка, оставившая меню висеть на рабочем столе владельца, — это не
    /// проверка, а помеха.
    /// </summary>
    internal void PostMenuForSelfTest(int holdMs)
    {
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("окно не создано");
        if (holdMs <= 0) throw new ArgumentOutOfRangeException(nameof(holdMs), "держать меню меньше миллисекунды нельзя");

        _selfTestMenuHoldMs = holdMs;
        SelfTestMenuItems = -1;

        if (!NativeMethods.PostMessage(_hwnd, CallbackMessage, IntPtr.Zero, new IntPtr(NativeMethods.WM_RBUTTONUP)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось послать открытие меню самотеста");
    }

    /// <summary>
    /// Сколько пунктов было в НАСТОЯЩЕМ всплывающем меню в тот миг, когда самотест его закрыл.
    /// <c>-1</c> — самотест меню не открывал.
    ///
    /// Это и есть проверка, которая может упасть: число приходит от Windows
    /// (<c>GetMenuItemCount</c>), а не из нашей же модели, — то есть доказывает, что человеку
    /// показывали меню с НАШИМИ пунктами, а не пустую рамку.
    /// </summary>
    internal int SelfTestMenuItems { get; private set; } = -1;

    private void CloseSelfTestMenu()
    {
        NativeMethods.KillTimer(_hwnd, new IntPtr(SelfTestMenuTimerId));

        // Считаем пункты, ПОКА меню открыто: после EndMenu дескриптор уже ничего не расскажет.
        SelfTestMenuItems = _showingMenu == IntPtr.Zero ? -1 : NativeMethods.GetMenuItemCount(_showingMenu);

        _selfTestMenuHoldMs = 0;

        // Закрываем меню ИЗ ТОГО ЖЕ модального цикла, который его открыл: EndMenu закрывает
        // активное меню ЭТОЙ нитки, и другой нитке это недоступно.
        NativeMethods.EndMenu();
    }

    /// <summary>
    /// Какое меню показывать ПРЯМО СЕЙЧАС. Отдельным методом — ради проверки: саму
    /// <see cref="ShowMenu"/> в тестах не позвать (она открывает настоящее всплывающее меню
    /// на рабочем столе владельца), а обещание «провайдер зовётся на каждый показ» обязано
    /// проверяться прогоном, который может упасть.
    /// </summary>
    internal TrayMenu? MenuForShow() => _menuProvider is null ? _menu : _menuProvider();

    private bool _ownIcon;

    /// <summary>
    /// Показывает всплывающее уведомление (шарик) на нашем же значке.
    /// NIIF_RESPECT_QUIET_TIME — чтобы не лезть в «не беспокоить»; это часть уважения к человеку.
    /// </summary>
    public void ShowNotification(string title, string text, TrayNotificationKind kind = TrayNotificationKind.Info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_iconAdded)
            throw new InvalidOperationException("значок ещё не поставлен: сначала Show()");

        var flags = kind switch
        {
            TrayNotificationKind.Warning => NativeMethods.NIIF_WARNING,
            TrayNotificationKind.Error => NativeMethods.NIIF_ERROR,
            _ => NativeMethods.NIIF_INFO,
        } | NativeMethods.NIIF_RESPECT_QUIET_TIME;

        var data = NewData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = flags;
        data.uTimeoutOrVersion = 10000;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось показать уведомление");
    }

    /// <summary>Посылает себе сообщение. Ответ придёт, только если насос сообщений раздаёт наши окна.</summary>
    public void PostSelfTestMessage()
    {
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("окно не создано");
        if (!NativeMethods.PostMessage(_hwnd, SelfTestMessage, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось послать самотест");
    }

    /// <summary>
    /// Посылает себе ровно то, что приходит от оболочки при щелчке левой кнопкой по значку:
    /// <c>WM_LBUTTONUP</c> в параметре обратного вызова. Нужен самотесту связки — он проверяет
    /// путь «щелчок → событие → показ окна», не дожидаясь человека.
    ///
    /// Оболочка при этом не участвует: проверяется наш путь, а не поведение Windows
    /// (его проверяет человек, и это записано в README).
    /// </summary>
    public void PostClickForSelfTest()
    {
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("окно не создано");
        if (!NativeMethods.PostMessage(_hwnd, CallbackMessage, IntPtr.Zero, new IntPtr(NativeMethods.WM_LBUTTONUP)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось послать щелчок самотеста");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DeleteIcon();

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classAtom != 0)
        {
            // Класс окна снимаем, иначе при повторных запусках в одном процессе копится мусор.
            NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
            _classAtom = 0;
        }

        if (_hIcon != IntPtr.Zero && _ownIcon) NativeMethods.DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
        _ownIcon = false;

        // Основа — отдельно от показанного значка: если у оболочки стояла она же, дескриптор
        // уже освобождён выше, и второй раз его трогать нельзя (двойное освобождение — падение).
        if (_baseIcon != IntPtr.Zero && _ownBaseIcon) NativeMethods.DestroyIcon(_baseIcon);
        _baseIcon = IntPtr.Zero;
        _ownBaseIcon = false;
    }

    // --- внутреннее ----------------------------------------------------------

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
        if (_classAtom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось зарегистрировать класс окна трея");

        _hwnd = NativeMethods.CreateWindowEx(
            0, _className, "DSH Panel tray", 0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "не удалось создать окно трея");
    }

    private NativeMethods.NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uCallbackMessage = unchecked((int)CallbackMessage),
        hIcon = _hIcon,
        szTip = Truncate(_toolTip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void AddIcon()
    {
        if (_iconAdded) return;
        if (_baseIcon == IntPtr.Zero) SetDefaultIcon();

        var data = NewData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "оболочка отказалась добавить значок");

        _iconAdded = true;
    }

    private void ModifyIcon(uint flags)
    {
        var data = NewData();
        data.uFlags = flags;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private void DeleteIcon()
    {
        if (!_iconAdded) return;
        var data = NewData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Проводник перезапустился — все значки исчезли, надо поставить заново.
        if (msg == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            try { AddIcon(); } catch { _iconAdded = false; }
            return IntPtr.Zero;
        }

        if (msg == SelfTestMessage)
        {
            SelfTestMessageReceived = true;
            return IntPtr.Zero;
        }

        // Меню самотеста держится ровно столько, сколько просили, и закрывается ОТСЮДА:
        // пока меню открыто, это единственный код, который вообще исполняется (см. таймер
        // в ShowMenu).
        if (msg == NativeMethods.WM_TIMER && wParam == new IntPtr(SelfTestMenuTimerId))
        {
            CloseSelfTestMenu();
            return IntPtr.Zero;
        }

        if (msg == CallbackMessage)
        {
            HandleTrayCallback(lParam.ToInt64());
            return IntPtr.Zero;
        }

        // Строки состояния в меню рисуем САМИ (owner-draw): цвет несёт смысл, а системная серая
        // строка его теряла. Windows спрашивает размер пункта (WM_MEASUREITEM) и просит нарисовать
        // его (WM_DRAWITEM) — оба сообщения приходят СЮДА, потому что это наше окно владеет меню.
        if (msg == NativeMethods.WM_MEASUREITEM || msg == NativeMethods.WM_DRAWITEM)
        {
            return HandleOwnerDraw(msg, lParam);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Ответ на «сколько места займёт пункт» и «нарисуй его». Возврат <c>1</c> — «сделано нами»;
    /// ноль возвращается, если пункт не наш (чужого рисовать мы не беремся).
    /// </summary>
    private IntPtr HandleOwnerDraw(uint msg, IntPtr lParam)
    {
        var draw = _menuDraw;
        if (draw is null) return IntPtr.Zero;

        if (msg == NativeMethods.WM_MEASUREITEM)
        {
            var item = Marshal.PtrToStructure<NativeMethods.MEASUREITEMSTRUCT>(lParam);
            if (item.CtlType != NativeMethods.ODT_MENU) return IntPtr.Zero;

            if (!draw.Measure(ref item)) return IntPtr.Zero;

            Marshal.StructureToPtr(item, lParam, false);
            return new IntPtr(1);
        }

        var drawn = Marshal.PtrToStructure<NativeMethods.DRAWITEMSTRUCT>(lParam);
        if (drawn.CtlType != NativeMethods.ODT_MENU) return IntPtr.Zero;

        return draw.Draw(ref drawn) ? new IntPtr(1) : IntPtr.Zero;
    }

    private void HandleTrayCallback(long evt)
    {
        switch (evt)
        {
            case NativeMethods.WM_LBUTTONUP:
                Clicked?.Invoke();
                break;
            case NativeMethods.WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke();
                break;
            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowMenu();
                break;
            case NativeMethods.NIN_BALLOONUSERCLICK:
                NotificationClicked?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        // Меню берётся у провайдера НА КАЖДЫЙ показ: строки состояния обязаны быть свежими
        // (см. SetMenuProvider).
        var menu = MenuForShow();
        if (menu is null || menu.Items.Count == 0) return;

        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        TrayMenuDraw? draw = null;

        try
        {
            // Замечание о сборке меню обнуляется на КАЖДЫЙ показ: иначе прошлый отказ (или его
            // отсутствие) читался бы как ответ про это меню, а меню собирается заново каждый раз.
            LastMenuNotice = string.Empty;

            // Рисовальщик строк состояния нужен, только если они в меню есть. Создаётся ОДИН раз
            // на показ (шрифт меню и контекст — вещь не бесплатная) и живёт до закрытия меню.
            var hasStatus = menu.Items.Any(item => item.Tone is not null);
            var reason = string.Empty;

            if (hasStatus && !TrayMenuDraw.TryCreate(out draw, out reason))
            {
                // Отступление, а не падение: строки состояния останутся системными серыми — ровно
                // теми же, какими были до 27.09.2026, — а причина названа словами (её печатает
                // самотест трея). Меню обязано открыться в любом случае.
                draw = null;
                LastMenuNotice = reason;
            }

            _menuDraw = draw;

            // Номер пункта = его индекс + 1: ноль означает «человек закрыл меню».
            var commands = new List<Action?>();

            foreach (var item in menu.Items)
            {
                if (item.IsSeparator)
                {
                    NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                commands.Add(item.Invoke);
                var id = new IntPtr(commands.Count);

                // Строка состояния рисуется нами: Windows для неё не рисует НИЧЕГО, поэтому
                // текст уходит не ей, а нам — в четвёртом параметре лежит наше значение пункта.
                if (item.Tone is { } tone && draw is not null)
                {
                    // MF_GRAYED остаётся: строка по-прежнему не нажимается — это её смысл,
                    // и это сторожат тесты. Вид при этом наш: цветной, а не системный серый.
                    NativeMethods.AppendMenuData(
                        hMenu,
                        NativeMethods.MF_OWNERDRAW | NativeMethods.MF_GRAYED,
                        id,
                        draw.Add(item.Text, tone));
                    continue;
                }

                var flags = NativeMethods.MF_STRING;
                if (!item.IsEnabled) flags |= NativeMethods.MF_GRAYED;
                if (item.IsChecked) flags |= NativeMethods.MF_CHECKED;
                if (item.IsDefault) flags |= NativeMethods.MF_DEFAULT;

                NativeMethods.AppendMenu(hMenu, flags, id, item.Text);
            }

            NativeMethods.GetCursorPos(out var pt);

            // Без SetForegroundWindow меню не закроется по щелчку мимо него — известная
            // особенность TrackPopupMenu, а не украшательство.
            NativeMethods.SetForegroundWindow(_hwnd);

            // Самотест: закрыться самому через holdMs. Таймер ставится ЗДЕСЬ — когда меню
            // собрано, но ещё не открыто: поставь его раньше, и при короткой задержке он
            // сработал бы до открытия меню, а меню осталось бы висеть навсегда.
            if (_selfTestMenuHoldMs > 0)
            {
                _showingMenu = hMenu;
                NativeMethods.SetTimer(_hwnd, new IntPtr(SelfTestMenuTimerId), (uint)_selfTestMenuHoldMs, IntPtr.Zero);
            }

            var chosen = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            if (chosen > 0 && chosen <= commands.Count)
                commands[chosen - 1]?.Invoke();

            // По документации после TrackPopupMenu надо послать WM_NULL, иначе меню
            // может остаться «залипшим» до следующего щелчка.
            NativeMethods.PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            // Меню закрыто (человеком или самотестом) — дескриптор больше не наш, и держать
            // таймер незачем: иначе он сработал бы уже без меню и посчитал бы чужой дескриптор.
            _showingMenu = IntPtr.Zero;
            _selfTestMenuHoldMs = 0;
            NativeMethods.KillTimer(_hwnd, new IntPtr(SelfTestMenuTimerId));

            NativeMethods.DestroyMenu(hMenu);

            // Рисовальщик отпускаем ПОСЛЕ DestroyMenu: своё значение пункта он ещё нужен меню,
            // пока то живо (шрифт и контекст — его собственные, и они освобождаются здесь).
            _menuDraw = null;
            draw?.Dispose();
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }
}
