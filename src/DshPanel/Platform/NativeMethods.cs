using System;
using System.Runtime.InteropServices;

namespace DshPanel.Platform;

/// <summary>
/// Объявления Win32, нужные для СВОЕГО значка в трее.
///
/// Почему свой, а не Avalonia: у её TrayIconImpl нет ни одного метода уведомлений —
/// проверено по сборке 23.09.2026. Понадобился бы доступ к приватным внутренностям
/// чужой библиотеки, а это ломается при любом её обновлении.
/// </summary>
internal static class NativeMethods
{
    // --- Shell_NotifyIcon ----------------------------------------------------
    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_INFO = 0x10;

    // Здесь СОЗНАТЕЛЬНО нет NIF_STATE, NIF_SHOWTIP и NIM_SETVERSION: первые два
    // действуют только при NOTIFYICON_VERSION_4, а мы работаем на legacy-версии
    // (пояснение — в TrayIconHost). Объявление, которое ни на что не влияет, —
    // обман читателя: он ищет, где оно применяется, и не находит.
    // Переход на версию 4 меняет формат обратного вызова (событие в младшем слове
    // lParam), а щелчки и меню автоматически проверить нельзя — только человеком.

    // Вид уведомления — только те, что мы действительно передаём.
    public const uint NIIF_INFO = 0x01;
    public const uint NIIF_WARNING = 0x02;
    public const uint NIIF_ERROR = 0x03;
    /// <summary>Уважать «не беспокоить»: без этого флага шарик пролезает в тихий час.</summary>
    public const uint NIIF_RESPECT_QUIET_TIME = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    // --- сообщения -----------------------------------------------------------
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    // Номера «TaskbarCreated» здесь НЕТ и быть не может: это не константа, его выдаёт
    // система при каждом запуске — берём через RegisterWindowMessage.
    // Выдуманное число (WM_USER + 1000) тут стояло и вводило в заблуждение.

    public const int NIN_BALLOONUSERCLICK = 0x0400 + 5;

    // --- окно ----------------------------------------------------------------
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Регистрирует НАШЕ сообщение по имени. Нужно для «TaskbarCreated»: его номер
    /// не является константой, его выдаёт система при каждом запуске.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>Родитель «только для сообщений»: окно невидимо и не мешает рабочему столу.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // --- единый экземпляр ----------------------------------------------------

    /// <summary>
    /// Найти окно по классу. Нужно второму запуску панели: он ищет окно-приёмник ПЕРВОЙ панели,
    /// чтобы попросить её показать окно. Ищем среди окон «только для сообщений» (HWND_MESSAGE) —
    /// там живут и наши окна, и искать их среди окон рабочего стола бессмысленно.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    /// <summary>Забрать сообщение из очереди НАШЕГО потока. Нужно пробам: в них нет цикла панели.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    public const uint PM_REMOVE = 0x0001;

    // --- значки --------------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Собрать HICON из КАДРА значка — ровно так Windows читает значки из своих ресурсов
    /// (<c>RT_ICON</c>). Кадр — это <c>BITMAPINFOHEADER</c> + точки + маска, то есть в точности
    /// содержимое кадра BMP внутри файла `.ico`; достаёт его <see cref="Tray.TrayIconSource"/>.
    ///
    /// ⚠️ **Про PNG: справочники говорят «не принимает», а замер говорит «принимает».**
    /// Проверено 26.09.2026 (мутация M3): кадр 256×256 из нашего значка — это PNG, и Windows
    /// собрала из него HICON наравне с BMP-кадрами. Поэтому PNG в коде НЕ отвергается; он лишь
    /// берётся вторым, когда BMP-кадров нет вовсе, и довод там не про Windows, а про качество
    /// (`Tray\TrayIconSource.Pick`). Мусор вместо кадра эта функция отвергает: возвращает ноль.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cx,
        int cy,
        uint flags);

    /// <summary>Версия формата значка: 0x00030000 — «как у ресурса RT_ICON».</summary>
    public const uint IconResourceVersion = 0x00030000;

    /// <summary>Значок цветной, размер берём заданный. Ноль — значение по умолчанию.</summary>
    public const uint LR_DEFAULTCOLOR = 0x00000000;

    /// <summary>Стандартный значок приложения — запасной, если своего файла нет.</summary>
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    /// <summary>
    /// Размер значка берём у системы, а не числом: на мониторе с масштабом
    /// «16 на глаз» даёт мыло. SM_CXSMICON — это ровно тот размер, что нужен лотку.
    /// </summary>
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // --- меню ----------------------------------------------------------------
    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_DEFAULT = 0x1000;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // --- самотест меню: таймер и закрытие ------------------------------------
    //
    // Таймер здесь не украшение, а единственный способ что-то сделать, ПОКА меню открыто:
    // `TrackPopupMenuEx` уходит в собственный модальный цикл, и очередь диспетчера Avalonia
    // до конца этого цикла не разбирается. Сообщение `WM_TIMER` модальный цикл раздаёт сам,
    // поэтому закрыть меню и снять его размеры можно ровно оттуда.

    public const uint WM_TIMER = 0x0113;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    /// <summary>Закрыть активное меню ЭТОЙ нитки. Зовётся из модального цикла самого меню.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndMenu();

    /// <summary>Сколько пунктов в меню. Нужен самотесту: он считает пункты НАСТОЯЩЕГО меню.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    // --- меню: СВОЯ отрисовка строк состояния (owner-draw) --------------------
    //
    // Строка состояния рисуется нами, потому что цвет несёт смысл (решение владельца 27.09.2026:
    // вне пика — зелёный, пик — янтарный, авария — красный), а системная серая строка состояния
    // этот смысл теряла: «сливается всё в одно».
    //
    // ⚠️ НОМЕРА СООБЩЕНИЙ СВЕРЕНЫ С WinUser.h 10.0.26100.0, а не взяты по памяти: WM_DRAWITEM
    // это 0x002B, а WM_MEASUREITEM — 0x002C (наоборот, чем кажется). Проба с перепутанными
    // числами «работала» и показывала мусор вместо структуры — так эту ошибку и поймали.
    public const uint MF_OWNERDRAW = 0x0100;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_MEASUREITEM = 0x002C;

    /// <summary>Вид элемента в CtlType обеих структур: пункт меню.</summary>
    public const uint ODT_MENU = 0x0001;

    /// <summary>Системный цвет фона меню — по нему выбирается краска для текста.</summary>
    public const int COLOR_MENU = 4;

    /// <summary>Прозрачный фон текста: иначе GDI закрасит прямоугольник под буквами.</summary>
    public const int TRANSPARENT = 1;

    /// <summary>Кисть «без пера» — кружок рисуется только заливкой, без обводки.</summary>
    public const int NULL_PEN = 8;

    public const uint DT_LEFT = 0x0000;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_SINGLELINE = 0x0020;

    /// <summary>
    /// Текст пункта — ДАННЫЕ, а не подпись с мнемоникой: без этого флага Windows съела бы «&amp;»
    /// и подчеркнула следующую букву (в строке состояния может стоять что угодно — например,
    /// имя агента).
    /// </summary>
    public const uint DT_NOPREFIX = 0x0800;

    /// <summary>Кисть системного цвета (общая, освобождать её нельзя).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetSysColorBrush(int nIndex);

    /// <summary>Системный цвет в виде COLORREF (0x00BBGGRR — красный и синий наоборот).</summary>
    [DllImport("user32.dll")]
    public static extern uint GetSysColor(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    /// <summary>
    /// «Сколько места займёт мой пункт». Windows спрашивает это ПЕРЕД показом меню; не ответить
    /// или ответить нулём — значит получить пункт нулевой высоты (меню поедет).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEASUREITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemWidth;
        public uint itemHeight;
        public IntPtr itemData;
    }

    /// <summary>
    /// «Нарисуй мой пункт»: прямоугольник, контекст и то самое значение, что мы отдали
    /// в <see cref="AppendMenuData"/> (проверено замером: <c>itemData</c> приходит ровно тем,
    /// чем его послали).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public IntPtr itemData;
    }

    /// <summary>
    /// Добавить пункт, у которого четвёртый параметр — НЕ строка, а наше значение.
    ///
    /// Для <c>MF_OWNERDRAW</c> Windows строку не хранит вовсе: она возвращает это значение
    /// в <c>itemData</c> обеих структур — так пункт и узнаёт, что ему рисовать. Объявление
    /// отдельное от <see cref="AppendMenu"/> намеренно: там строка, здесь число, и перепутать
    /// их значит получить пункт без текста.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuData(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, IntPtr lpNewItem);

    // --- рисование: кисти, перья, шрифт меню ---------------------------------

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>Общая кисть/перо Windows по номеру (<c>NULL_PEN</c> и прочее). Не освобождается.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr GetStockObject(int i);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    /// <summary>
    /// Нарисовать строку в контексте. ⚠️ Живёт в <c>user32</c>, а не в <c>gdi32</c>, как соседи
    /// по рисованию: проба на живом пути 27.09.2026 поймала это падением
    /// <c>EntryPointNotFoundException</c> — сборка и проверки были зелёными, потому что до
    /// отрисовки меню они не доходили.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    /// <summary>Шрифт по описанию: высота и имя берутся у системного шрифта МЕНЮ (см. ниже).</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFontIndirect(ref LOGFONT lplf);

    /// <summary>Сколько точек займёт строка этим шрифтом — ширина пункта считается по ней.</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTextExtentPoint32(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTextMetrics(IntPtr hdc, out TEXTMETRIC lptm);

    /// <summary>
    /// Размеры шрифта меню. Числом их взять негде: «9 pt» на мониторе с масштабом — не то же
    /// самое, что 9 pt на обычном, и придуманная высота сделала бы строку состояния ниже
    /// остальных пунктов.
    /// </summary>
    public const uint SPI_GETNONCLIENTMETRICS = 0x0029;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref NONCLIENTMETRICS pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    /// <summary>Полное описание (Vista и новее): без <c>iPaddedBorderWidth</c> Windows отвечает отказом.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NONCLIENTMETRICS
    {
        public uint cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;
        public LOGFONT lfCaptionFont;
        public int iSmCaptionWidth;
        public int iSmCaptionHeight;
        public LOGFONT lfSmCaptionFont;
        public int iMenuWidth;
        public int iMenuHeight;
        public LOGFONT lfMenuFont;
        public LOGFONT lfStatusFont;
        public LOGFONT lfMessageFont;
        public int iPaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TEXTMETRIC
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    // --- значок: огонёк состояния в углу --------------------------------------
    //
    // Готовый HICON с кружком собирается из пикселей: берём точки значка (GetIconInfo →
    // GetDIBits), ставим точку огонька и отдаём обратно (CreateIconIndirect). Так вышло не
    // от хорошей жизни: DrawIconEx в 32-битный DIB точки НЕ кладёт вовсе (замер 27.09.2026 —
    // в буфере остались нули), поэтому «нарисовать значок и поверх кружок» средствами GDI
    // нельзя, а пиксели значка читаются и пишутся честно.

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>Описание DIB с одной палитровой записью: нам хватает 32 бит и «без сжатия».</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public const uint DIB_RGB_COLORS = 0;

    /// <summary>Отрицательная высота — DIB «сверху вниз»: строка 0 в памяти это верх картинки.</summary>
    public const int TopDownDib = -1;

    /// <summary>Биты значка: цветная и маска. Освобождать ОБЯЗАН вызывающий (это копии).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    /// <summary>Собрать значок из готовых битов — так Windows принимает и канал прозрачности.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    /// <summary>Прочитать точки битовой карты. Возвращает число прочитанных строк (не точек!).</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);

    /// <summary>Одноцветная маска значка: бит 1 — «здесь прозрачно».</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpvBits);

    /// <summary>Что за битовая карта: размеры значка берутся отсюда, а не у системы.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetObject(IntPtr h, int c, ref BITMAP pv);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }
}
