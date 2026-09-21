using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Оформление продукта в одном месте: палитра (светлая и тёмная), роли шрифтов, шкала отступов,
/// метрики контролов, помощники применения и своя отрисовка вкладок.
///
/// Правило, ради которого файл и появился: в окнах больше нет ни одного литерала
/// <c>Color.FromArgb</c> и ни одного «магического» размера шрифта — только отсюда. Иначе окна
/// снова разъедутся по виду, как разъехались до 1.22.0 (41 литерал цвета на шесть файлов).
///
/// Техническая спека с точными значениями — <c>_review/ui-1.22-spec.md</c> в рабочей области.
/// Меняешь цвет или размер — меняй и спеку, иначе следующая сессия не поймёт, что здесь истина.
/// </summary>
internal static class Theme
{
    /// <summary>Откуда берём тему: как в Windows, всегда светлая или всегда тёмная.</summary>
    internal enum Mode
    {
        Auto,
        Light,
        Dark
    }

    /// <summary>Семантическая палитра. Роли, а не «серый1/серый2»: код не знает, тёмная тема сейчас
    /// или светлая, он просит «поверхность» и «приглушённый».</summary>
    internal sealed record Palette(
        Color Window,
        Color Surface,
        Color SurfaceAlt,
        Color Border,
        Color Text,
        Color Muted,
        Color Faint,
        Color Accent,
        Color AccentHover,
        Color OnAccent,
        Color Success,
        Color Warning,
        Color Danger,
        Color Disabled);

    // Гамма взята из иконки трея (плитка #0F172A → #1E293B): значок уже был нарисован в этих
    // цветах, а окна оставались белыми — теперь это один продукт.
    private static readonly Palette LightPalette = new(
        Window: Hex("#F3F4F6"),
        Surface: Hex("#FFFFFF"),
        SurfaceAlt: Hex("#F9FAFB"),
        Border: Hex("#E5E7EB"),
        Text: Hex("#111827"),
        Muted: Hex("#6B7280"),
        Faint: Hex("#9CA3AF"),
        Accent: Hex("#2563EB"),
        AccentHover: Hex("#1D4ED8"),
        OnAccent: Hex("#FFFFFF"),
        Success: Hex("#15803D"),
        Warning: Hex("#B45309"),
        Danger: Hex("#B91C1C"),
        Disabled: Hex("#9CA3AF"));

    private static readonly Palette DarkPalette = new(
        Window: Hex("#0F172A"),
        Surface: Hex("#1E293B"),
        SurfaceAlt: Hex("#172033"),
        Border: Hex("#2B3A55"),
        Text: Hex("#E5E7EB"),
        Muted: Hex("#94A3B8"),
        Faint: Hex("#64748B"),
        Accent: Hex("#60A5FA"),
        AccentHover: Hex("#93C5FD"),
        OnAccent: Hex("#0F172A"),
        Success: Hex("#4ADE80"),
        Warning: Hex("#FBBF24"),
        Danger: Hex("#F87171"),
        Disabled: Hex("#475569"));

    private static bool _dark;

    /// <summary>Тёмная тема сейчас? Решает <see cref="Use"/>.</summary>
    public static bool IsDark => _dark;

    /// <summary>Текущая палитра. Все окна читают цвета только отсюда.</summary>
    public static Palette Colors => _dark ? DarkPalette : LightPalette;

    /// <summary>Оттенки для конкретной темы — нужны самопроверке, которая перебирает обе палитры.</summary>
    internal static Palette PaletteFor(bool dark) => dark ? DarkPalette : LightPalette;

    // --- шкала отступов и метрики -------------------------------------------

    public const int Gap4 = 4;
    public const int Gap8 = 8;
    public const int Gap12 = 12;
    public const int Gap16 = 16;
    public const int Gap24 = 24;

    /// <summary>Поле окна: 16 со всех сторон — как в описи, никаких 7 и 19.</summary>
    public const int WindowPadding = Gap16;

    public const int ButtonHeight = 32;
    public const int PrimaryButtonHeight = 34;
    public const int FieldHeight = 26;
    public const int ButtonRadius = 6;
    public const int CardRadius = 8;

    // --- шрифты --------------------------------------------------------------

    // Loc.UiFont создаёт НОВЫЙ Font на каждый вызов, а семейство зависит от языка. Кэшируем по
    // языку: красить кэшированным шрифтом в Paint безопасно, создавать там Font — утечка GDI.
    private static string _fontLanguage = "";
    private static readonly Dictionary<string, Font> Fonts = new();

    private static Font Role(float size, FontStyle style)
    {
        if (_fontLanguage != Loc.Language)
        {
            foreach (var font in Fonts.Values) font.Dispose();
            Fonts.Clear();
            _fontLanguage = Loc.Language;
        }

        var key = size.ToString("0.##") + "/" + style;
        if (!Fonts.TryGetValue(key, out var cached))
        {
            cached = Loc.UiFont(size, style);
            Fonts[key] = cached;
        }

        return cached;
    }

    /// <summary>Имя окна/продукта.</summary>
    public static Font Title => Role(15F, FontStyle.Bold);

    /// <summary>Заголовок карточки, подпись шага мастера.</summary>
    public static Font Heading => Role(11F, FontStyle.Bold);

    /// <summary>Рабочий текст: подписи, кнопки, поля.</summary>
    public static Font Body => Role(9F, FontStyle.Regular);

    /// <summary>Рабочий текст, на который надо обратить внимание.</summary>
    public static Font BodyBold => Role(9F, FontStyle.Bold);

    /// <summary>Пояснения и подсказки — на полпункта меньше рабочего текста.</summary>
    public static Font Hint => Role(8.5F, FontStyle.Regular);

    // Моноширинный — своё семейство, поэтому отдельный кэш.
    private static readonly Dictionary<string, Font> MonoFonts = new();

    private static Font MonoRole(float size)
    {
        if (_fontLanguage != Loc.Language)
        {
            foreach (var font in MonoFonts.Values) font.Dispose();
            MonoFonts.Clear();
        }

        var key = "mono/" + size.ToString("0.##");
        if (!MonoFonts.TryGetValue(key, out var cached))
        {
            cached = Mono(size);
            MonoFonts[key] = cached;
        }

        return cached;
    }

    private static Font Mono(float size)
    {
        foreach (var family in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            try
            {
                return new Font(family, size);
            }
            catch
            {
                // Семейства нет — пробуем следующее.
            }
        }

        return Loc.UiFont(size);
    }

    /// <summary>Заметки к выпуску, отчёт наката, поле прайса — всё, где важны колонки.</summary>
    public static Font Mono9 => MonoRole(9F);

    /// <summary>Точка состояния в панели.</summary>
    public static Font Dot => Role(14F, FontStyle.Regular);

    /// <summary>Крупное число (баланс) — заметнее рабочего текста, но не заголовок окна.</summary>
    public static Font Number => Role(15F, FontStyle.Bold);

    // --- тема ----------------------------------------------------------------

    /// <summary>Выбрать тему. <see cref="Mode.Auto"/> читает системную настройку Windows.</summary>
    public static void Use(Mode mode)
    {
        _dark = mode switch
        {
            Mode.Light => false,
            Mode.Dark => true,
            _ => SystemPrefersDark()
        };
    }

    /// <summary>Параметр оформления из строки настроек: пустое или неизвестное — «как в Windows».</summary>
    public static Mode ModeFrom(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "light" => Mode.Light,
            "dark" => Mode.Dark,
            _ => Mode.Auto
        };
    }

    /// <summary>Строка для настроек — обратное к <see cref="ModeFrom"/>.</summary>
    public static string ModeTo(Mode mode) => mode switch
    {
        Mode.Light => "light",
        Mode.Dark => "dark",
        _ => "auto"
    };

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // 0 — приложения в тёмной теме. Нет ключа (старая Windows, урезанный профиль) — светлая.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    // --- применение ----------------------------------------------------------

    /// <summary>Применить тему к окну и ко всему, что внутри, рекурсивно.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Colors.Window;
        form.ForeColor = Colors.Text;
        Walk(form);
    }

    /// <summary>Пройти по дереву контролов и оформить каждый.</summary>
    public static void Walk(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            Style(control);
            if (control.HasChildren) Walk(control);
        }
    }

    /// <summary>Оформить один контрол по его типу. Незнакомые контролы получают только цвета
    /// текста и фона — этого достаточно, чтобы они не выбивались из окна.</summary>
    public static void Style(Control control)
    {
        switch (control)
        {
            case CardPanel card:
                card.Invalidate();
                break;

            case LinkLabel link:
                Link(link);
                break;

            case Button button:
                Button(button, button is PrimaryButton ? ButtonKind.Primary : ButtonKind.Secondary);
                break;

            case TextBoxBase text:
                Field(text);
                break;

            case ComboBox combo:
                Field(combo);
                break;

            case NumericUpDown numeric:
                Field(numeric);
                break;

            case ListView view:
                Table(view);
                break;

            case ListControl list:
                List(list);
                break;

            case TabControl tabs:
                Tabs(tabs);
                break;

            case TabPage page:
                page.BackColor = Colors.Surface;
                page.ForeColor = Colors.Text;
                break;

            case GroupBox group:
                group.BackColor = Colors.Surface;
                group.ForeColor = Colors.Muted;
                group.Font = BodyBold;
                break;

            case Label label:
                Label(label);
                break;

            case CheckBox check:
                check.BackColor = Color.Transparent;
                check.ForeColor = Colors.Text;
                check.Font = Body;
                break;

            case RadioButton radio:
                radio.BackColor = Color.Transparent;
                radio.ForeColor = Colors.Text;
                radio.Font = Body;
                break;

            case ProgressBar:
                break;

            case Panel:
                control.BackColor = Colors.Window;
                control.ForeColor = Colors.Text;
                break;
        }
    }

    /// <summary>Метка: цвет и роль берём из уже заданных (окна ставят роль сами), но приводим
    /// фон к прозрачному, чтобы метка не светила белым на карточке или на тёмном фоне.</summary>
    public static void Label(Label label)
    {
        if (label.BackColor != Color.Transparent) label.BackColor = Color.Transparent;
        if (label.ForeColor == Color.Empty) label.ForeColor = Colors.Text;
    }

    /// <summary>Ссылка: акцентный цвет темы, подчёркивание оставляем — это признак ссылки.</summary>
    public static void Link(LinkLabel link)
    {
        link.BackColor = Color.Transparent;
        link.LinkColor = Colors.Accent;
        link.ActiveLinkColor = Colors.AccentHover;
        link.VisitedLinkColor = Colors.Accent;
        link.Font = Body;
    }

    internal enum ButtonKind
    {
        Secondary,
        Primary,
        Danger
    }

    /// <summary>Кнопка: плоская, одинаковая во всём продукте. Главная — залитая акцентом,
    /// вторичная — поверхность с рамкой, опасная — красный текст.</summary>
    public static void Button(Button button, ButtonKind kind)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = kind == ButtonKind.Primary ? 0 : 1;
        button.FlatAppearance.BorderColor = kind == ButtonKind.Danger ? Colors.Danger : Colors.Border;
        button.FlatAppearance.MouseOverBackColor = kind switch
        {
            ButtonKind.Primary => Colors.AccentHover,
            ButtonKind.Danger => Blend(Colors.Surface, Colors.Danger, 0.12),
            _ => Colors.SurfaceAlt
        };
        button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
        button.BackColor = kind switch
        {
            ButtonKind.Primary => Colors.Accent,
            _ => Colors.Surface
        };
        button.ForeColor = kind switch
        {
            ButtonKind.Primary => Colors.OnAccent,
            ButtonKind.Danger => Colors.Danger,
            _ => Colors.Text
        };
        button.Font = Body;
        if (button.Height != PrimaryButtonHeight) button.Height = ButtonHeight;
        if (button.Height > ButtonHeight && kind != ButtonKind.Primary) button.Height = ButtonHeight;
    }

    /// <summary>Поле ввода: поверхность, рамка темы, одинаковая высота. ReadOnly-поля не белые
    /// в тёмной теме — это была самая заметная «дырка» в прежнем виде.</summary>
    public static void Field(Control field)
    {
        field.BackColor = field.Enabled ? Colors.Surface : Colors.SurfaceAlt;
        field.ForeColor = field.Enabled ? Colors.Text : Colors.Disabled;
        field.Font = Body;
        if (field.Height != 0) field.Height = FieldHeight;
    }

    /// <summary>Список: поверхность темы и своя отрисовка строки, чтобы выделение не выпадало из
    /// палитры. Шапка и сетка <c>ListView</c> остаются системными — это осознанный предел уровня A:
    /// своя отрисовка колонок стоит дороже, чем даёт эффекта.</summary>
    public static void List(ListControl list)
    {
        list.BackColor = Colors.Surface;
        list.ForeColor = Colors.Text;
        list.Font = Body;

        if (list is ListBox box)
        {
            box.BorderStyle = BorderStyle.FixedSingle;
            box.DrawMode = DrawMode.OwnerDrawFixed;
            box.ItemHeight = 20;
            // Обработчик вешаем один раз: Retext пересобирает окно и зовёт нас снова.
            box.DrawItem -= DrawListItem;
            box.DrawItem += DrawListItem;
        }
    }

    /// <summary>Таблица (<c>ListView</c>): цвета и рамка темы. Шапка и сетка остаются системными —
    /// это осознанный предел уровня A: своя отрисовка колонок стоит дороже, чем даёт эффекта.</summary>
    public static void Table(ListView view)
    {
        view.BackColor = Colors.Surface;
        view.ForeColor = Colors.Text;
        view.Font = Body;
        view.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>Отрисовка строки списка: выбранная — акцент, остальные — поверхность.</summary>
    private static void DrawListItem(object sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox box || e.Index < 0) return;

        var palette = Colors;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? palette.Accent : palette.Surface))
            e.Graphics.FillRectangle(fill, e.Bounds);

        var text = box.Items[e.Index]?.ToString() ?? "";
        TextRenderer.DrawText(e.Graphics, text, Body, e.Bounds,
            selected ? palette.OnAccent : palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Разделитель: тонкая линия цвета рамки.</summary>
    public static void Separator(Panel panel)
    {
        panel.Height = 1;
        panel.BackColor = Colors.Border;
    }

    // --- карточка ------------------------------------------------------------

    /// <summary>Карточка вместо серого <c>GroupBox</c>: фон поверхности, рамка, скругление.
    /// Именно системные рамки <c>GroupBox</c> давали «склеенный из модулей» вид.</summary>
    internal sealed class CardPanel : Panel
    {
        /// <summary>Заголовок карточки: рисуется сам, внутри рамки, с отступом из шкалы. Пусто —
        /// карточка без заголовка (содержимое тогда начинается с её собственного отступа).</summary>
        public string Title { get; set; } = "";

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var palette = Colors;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? palette.Window);

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Rounded(bounds, CardRadius);
            using (var fill = new SolidBrush(palette.Surface)) e.Graphics.FillPath(fill, path);
            using (var pen = new Pen(palette.Border)) e.Graphics.DrawPath(pen, path);

            if (Title.Length > 0)
            {
                var area = new Rectangle(Gap12, Gap8, Width - (Gap12 * 2), 20);
                TextRenderer.DrawText(e.Graphics, Title, Heading, area, palette.Muted,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            }

            base.OnPaint(e);
        }
    }

    /// <summary>Прямоугольник со скруглёнными углами: используется карточками и кнопками тем,
    /// кто рисует сам.</summary>
    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        if (diameter <= 0 || bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    // --- вкладки -------------------------------------------------------------

    /// <summary>Настроить вкладки: рисуем сами. <c>TabControl</c> обязан остаться
    /// <c>TabControl</c> — от этого зависит обход страниц в проверке вёрстки
    /// (<c>LayoutCheck.FindTabs</c>); боковое меню заставило бы проверку молча пропустить
    /// три страницы.</summary>
    public static void Tabs(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(148, 34);
        tabs.Padding = new Point(14, 4);
        tabs.BackColor = Colors.Window;
        tabs.ForeColor = Colors.Text;
        tabs.Font = Body;

        // Обработчик вешаем один раз: Retext пересобирает окно и может позвать нас снова.
        tabs.DrawItem -= DrawTab;
        tabs.DrawItem += DrawTab;

        foreach (TabPage page in tabs.TabPages) Style(page);
    }

    /// <summary>Отрисовка одной вкладки: выбранная — поверхность и акцентная полоса снизу,
    /// остальные — приглушённые подписи на фоне окна.</summary>
    private static void DrawTab(object sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs) return;

        var palette = Colors;
        var selected = e.Index == tabs.SelectedIndex;
        var bounds = tabs.GetTabRect(e.Index);

        using (var fill = new SolidBrush(selected ? palette.Surface : palette.Window))
            e.Graphics.FillRectangle(fill, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            e.Graphics.FillRectangle(accent, bounds.X, bounds.Bottom - 2, bounds.Width, 2);
        }

        var text = tabs.TabPages[e.Index].Text;
        var font = selected ? BodyBold : Body;
        var color = selected ? palette.Text : palette.Muted;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var textBounds = new Rectangle(bounds.X + Gap4, bounds.Y, bounds.Width - Gap8, bounds.Height);
        e.Graphics.DrawString(text, font, brush, textBounds, format);
    }

    // --- служебное -----------------------------------------------------------

    /// <summary>Главная кнопка окна (залита акцентом). Отдельный тип нужен, чтобы
    /// <see cref="Style"/> отличал её от вторичных, не спрашивая каждое окно.</summary>
    internal sealed class PrimaryButton : Button
    {
    }

    private static Color Hex(string value)
    {
        var text = value.TrimStart('#');
        return Color.FromArgb(
            255,
            System.Convert.ToInt32(text.Substring(0, 2), 16),
            System.Convert.ToInt32(text.Substring(2, 2), 16),
            System.Convert.ToInt32(text.Substring(4, 2), 16));
    }

    /// <summary>Смешать два цвета: нужен для наведения на «опасную» кнопку и подобных мелочей,
    /// чтобы не заводить ещё одну роль в палитре.</summary>
    internal static Color Blend(Color first, Color second, double amount)
    {
        var part = System.Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (int)(first.R + ((second.R - first.R) * part)),
            (int)(first.G + ((second.G - first.G) * part)),
            (int)(first.B + ((second.B - first.B) * part)));
    }

    /// <summary>Относительная яркость по WCAG — на ней стоит проверка контраста в самопроверке.</summary>
    internal static double Luminance(Color color)
    {
        double Channel(int value)
        {
            var scaled = value / 255.0;
            return scaled <= 0.03928 ? scaled / 12.92 : System.Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>Коэффициент контраста двух цветов (1 — совпали, 21 — чёрное на белом).</summary>
    internal static double Contrast(Color first, Color second)
    {
        var left = Luminance(first);
        var right = Luminance(second);
        var lighter = System.Math.Max(left, right);
        var darker = System.Math.Min(left, right);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
