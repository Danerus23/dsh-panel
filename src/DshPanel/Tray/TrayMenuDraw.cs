using System.Runtime.InteropServices;
using DshPanel.Platform;
using DshPanel.Shell;

namespace DshPanel.Tray;

/// <summary>
/// Геометрия одной строки состояния: где кружок, где текст, какой высоты строка.
///
/// ЧИСТАЯ запись, без Windows и без окна: размеры считаются из ширины надписи и высоты шрифта,
/// и это ровно то, что проверяется прогоном (кружок внутри строки, текст правее кружка, высота
/// не меньше шрифта). Ошибись здесь — и меню поедет: строка вылезет за пункт или накроет соседа.
/// </summary>
internal readonly record struct TrayLineLayout(int Width, int Height, int CircleLeft, int CircleTop, int CircleSize, int TextLeft)
{
    /// <summary>Отступ кружка от левого края пункта.</summary>
    internal const int LeftPad = 3;

    /// <summary>Просвет между кружком и текстом.</summary>
    internal const int Gap = 6;

    /// <summary>Отступ справа — чтобы надпись не упиралась в край меню.</summary>
    internal const int RightPad = 8;

    /// <summary>Отступ сверху и снизу от самой высокой части строки.</summary>
    internal const int VerticalPad = 2;

    /// <summary>Кружок не меньше этого: меньше семи точек в лотке и в меню не разглядеть.</summary>
    internal const int MinCircle = 7;

    /// <summary>Разложить строку по ширине её текста и высоте системного шрифта меню.</summary>
    internal static TrayLineLayout For(int textWidth, int textHeight)
    {
        var circle = Math.Max(MinCircle, textHeight - 4);
        var height = Math.Max(textHeight, circle) + (2 * VerticalPad);
        var textLeft = LeftPad + circle + Gap;

        return new TrayLineLayout(
            Width: textLeft + Math.Max(0, textWidth) + RightPad,
            Height: height,
            CircleLeft: LeftPad,
            CircleTop: (height - circle) / 2,
            CircleSize: circle,
            TextLeft: textLeft);
    }
}

/// <summary>
/// Строки состояния, нарисованные СВОИМИ руками (owner-draw), — цветной кружок и цветной текст.
///
/// **Почему своими руками.** Windows рисует недоступную строку меню одинаково серой, а цвет тут
/// несёт смысл: зелёный — работает, янтарный — внимание, красный — не работает (решение владельца
/// 27.09.2026). Команды меню при этом остаются СИСТЕМНЫМИ: перерисовывать всё меню целиком ради
/// трёх строк значило бы взять на себя и подсветку, и мнемонику, и вид тёмной темы.
///
/// **Как это устроено у Windows.** Пункт добавляется с флагом <c>MF_OWNERDRAW</c> и своим
/// значением в четвёртом параметре; Windows возвращает это значение в <c>itemData</c> и просит
/// сначала измерить пункт (<c>WM_MEASUREITEM</c>), а потом нарисовать его (<c>WM_DRAWITEM</c>).
/// Здесь лежит и то и другое — вызывают их из <see cref="TrayIconHost"/> (окно у него своё).
/// Измерено пробой 27.09.2026: сообщения приходят и для НЕДОСТУПНОГО пункта (то есть серая
/// строка рисуется нами, а не системой), <c>itemData</c> приходит ровно тем, чем его послали,
/// а прямоугольник пункта — настоящий.
///
/// ⚠️ Живёт ровно столько, сколько открыто меню: держит DC и шрифт меню, а её пункты ищутся
/// по значению, которое мы же и выдали. Закрывается в <c>finally</c> того же показа.
/// </summary>
internal sealed class TrayMenuDraw : IDisposable
{
    private readonly List<Line> _lines = new();
    private readonly IntPtr _dc;
    private readonly IntPtr _font;
    private readonly IntPtr _oldFont;
    private readonly int _textHeight;
    private readonly bool _ownFont;

    private bool _disposed;

    /// <summary>Пункт, который рисуем сами: текст и его тон.</summary>
    internal readonly record struct Line(string Text, TrayTone Tone);

    private TrayMenuDraw(IntPtr dc, IntPtr font, IntPtr oldFont, int textHeight, bool ownFont)
    {
        _dc = dc;
        _font = font;
        _oldFont = oldFont;
        _textHeight = textHeight;
        _ownFont = ownFont;
    }

    /// <summary>Сколько строк состояния в этом меню.</summary>
    internal int Count => _lines.Count;

    /// <summary>Высота строки по системному шрифту меню — её же возвращает <c>WM_MEASUREITEM</c>.</summary>
    internal int TextHeight => _textHeight;

    /// <summary>
    /// Собрать рисовальщик: шрифт берётся у СИСТЕМЫ (<c>SPI_GETNONCLIENTMETRICS</c>, поле
    /// <c>lfMenuFont</c>), а не назначается числом, — иначе строка состояния окажется выше или
    /// ниже соседних пунктов на мониторе с масштабом.
    ///
    /// Отказ возможен (не отдался системный шрифт, не создался DC) и возвращается причиной
    /// словами: вызывающий обязан отступить на прежний вид, а не оставить меню пустым.
    /// </summary>
    internal static bool TryCreate(out TrayMenuDraw? draw, out string detail)
    {
        draw = null;

        var font = CreateMenuFont(out var fontDetail);
        if (font == IntPtr.Zero)
        {
            detail = fontDetail;
            return false;
        }

        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(font);
            detail = "не удалось создать контекст для измерения строки состояния";
            return false;
        }

        var oldFont = NativeMethods.SelectObject(dc, font);

        if (!NativeMethods.GetTextMetrics(dc, out var metrics) || metrics.tmHeight <= 0)
        {
            NativeMethods.SelectObject(dc, oldFont);
            NativeMethods.DeleteObject(font);
            NativeMethods.DeleteDC(dc);
            detail = "система не назвала высоту шрифта меню";
            return false;
        }

        draw = new TrayMenuDraw(dc, font, oldFont, metrics.tmHeight, ownFont: true);
        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// Добавить строку состояния. Возвращает её значение для <c>itemData</c> — по нему Windows
    /// вернёт нам тот же пункт, и по нему же строка находится на отрисовке.
    ///
    /// Значение — «номер + 1»: ноль остаётся за «это не наш пункт», и путать его с первым
    /// пунктом нельзя.
    /// </summary>
    internal IntPtr Add(string text, TrayTone tone)
    {
        _lines.Add(new Line(text, tone));
        return new IntPtr(_lines.Count);
    }

    /// <summary>Ответ на «сколько места займёт пункт». Ноль вместо размера — пункт нулевой высоты.</summary>
    internal bool Measure(ref NativeMethods.MEASUREITEMSTRUCT item)
    {
        if (!TryFind(item.itemData, out var line)) return false;

        var layout = LayoutOf(line.Text);
        item.itemWidth = (uint)layout.Width;
        item.itemHeight = (uint)layout.Height;
        return true;
    }

    /// <summary>
    /// Нарисовать пункт: фон меню, кружок тона и текст тона.
    ///
    /// Фон закрашивается ЦВЕТОМ МЕНЮ, а не оставляется как есть: у пункта, нарисованного своими
    /// руками, Windows фон не рисует вовсе, и без заливки под цветной надписью остался бы мусор
    /// от предыдущего кадра.
    /// </summary>
    internal bool Draw(ref NativeMethods.DRAWITEMSTRUCT item)
    {
        if (!TryFind(item.itemData, out var line)) return false;

        var background = TrayPalette.ToRgb(NativeMethods.GetSysColor(NativeMethods.COLOR_MENU));
        var layout = LayoutOf(line.Text);

        var rc = item.rcItem;
        NativeMethods.FillRect(item.hDC, ref rc, NativeMethods.GetSysColorBrush(NativeMethods.COLOR_MENU));

        DrawCircle(item.hDC, item.rcItem, layout, TrayPalette.Circle(line.Tone));
        DrawText(item.hDC, item.rcItem, layout, line.Text, TrayPalette.Text(line.Tone, background));
        return true;
    }

    /// <summary>Размер надписи этим шрифтом — по нему считается и ширина пункта.</summary>
    internal int TextWidth(string text) =>
        NativeMethods.GetTextExtentPoint32(_dc, text, text.Length, out var size) ? size.cx : 0;

    /// <summary>Раскладка строки с этой надписью — то же, что уходит в <c>WM_MEASUREITEM</c>.</summary>
    internal TrayLineLayout LayoutOf(string text) => TrayLineLayout.For(TextWidth(text), _textHeight);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_oldFont != IntPtr.Zero) NativeMethods.SelectObject(_dc, _oldFont);
        if (_ownFont && _font != IntPtr.Zero) NativeMethods.DeleteObject(_font);
        if (_dc != IntPtr.Zero) NativeMethods.DeleteDC(_dc);
    }

    private bool TryFind(IntPtr itemData, out Line line)
    {
        var index = itemData.ToInt64() - 1;

        if (index < 0 || index >= _lines.Count)
        {
            line = default;
            return false;
        }

        line = _lines[(int)index];
        return true;
    }

    private static void DrawCircle(IntPtr hdc, NativeMethods.RECT item, TrayLineLayout layout, uint rgb)
    {
        var brush = NativeMethods.CreateSolidBrush(TrayPalette.ToColorRef(rgb));
        if (brush == IntPtr.Zero) return;

        var oldBrush = NativeMethods.SelectObject(hdc, brush);
        var oldPen = NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(NativeMethods.NULL_PEN));

        var left = item.Left + layout.CircleLeft;
        var top = item.Top + layout.CircleTop;

        NativeMethods.Ellipse(hdc, left, top, left + layout.CircleSize, top + layout.CircleSize);

        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.DeleteObject(brush);
    }

    private void DrawText(IntPtr hdc, NativeMethods.RECT item, TrayLineLayout layout, string text, uint rgb)
    {
        var oldFont = NativeMethods.SelectObject(hdc, _font);
        var oldMode = NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
        var oldColor = NativeMethods.SetTextColor(hdc, TrayPalette.ToColorRef(rgb));

        var rc = new NativeMethods.RECT
        {
            Left = item.Left + layout.TextLeft,
            Top = item.Top,
            Right = item.Right,
            Bottom = item.Bottom,
        };

        NativeMethods.DrawText(
            hdc,
            text,
            text.Length,
            ref rc,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_NOPREFIX);

        NativeMethods.SetTextColor(hdc, oldColor);
        NativeMethods.SetBkMode(hdc, oldMode);
        NativeMethods.SelectObject(hdc, oldFont);
    }

    /// <summary>
    /// Шрифт меню. Система отдаёт его описанием (<c>lfMenuFont</c>) — и это единственный способ
    /// узнать и высоту, и имя настоящего шрифта меню: у монитора с масштабом он не «9 pt».
    /// Отказ возвращается причиной словами: вызывающий отступает на прежний серый вид, а не
    /// оставляет меню пустым.
    /// </summary>
    private static IntPtr CreateMenuFont(out string detail)
    {
        var metrics = new NativeMethods.NONCLIENTMETRICS { cbSize = (uint)Marshal.SizeOf<NativeMethods.NONCLIENTMETRICS>() };

        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETNONCLIENTMETRICS, metrics.cbSize, ref metrics, 0))
        {
            var font = NativeMethods.CreateFontIndirect(ref metrics.lfMenuFont);
            if (font != IntPtr.Zero)
            {
                detail = string.Empty;
                return font;
            }

            detail = $"система отказала в шрифте меню (код {Marshal.GetLastWin32Error()})";
            return IntPtr.Zero;
        }

        detail = $"размеры системных шрифтов не читаются (код {Marshal.GetLastWin32Error()})";
        return IntPtr.Zero;
    }
}
