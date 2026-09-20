using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Значки на кнопках. Панель — один исполняемый файл без картинок рядом, поэтому значки
/// рисуются глифами системного шрифта «Segoe MDL2 Assets» в маленькую картинку. Если шрифта
/// в системе нет (так бывает на урезанных сборках Windows), значки просто не ставятся:
/// кнопка остаётся с подписью и работает как работала.
/// </summary>
internal static class Glyphs
{
    /// <summary>Шестерёнка — «Настройки».</summary>
    public const string Settings = "\uE713";

    /// <summary>Стопка (Library) — «Резервные копии».</summary>
    public const string Backups = "\uE8F1";

    /// <summary>Папка — «Открыть папку».</summary>
    public const string Folder = "\uE8B7";

    private const string FontName = "Segoe MDL2 Assets";

    private static bool? _available;

    /// <summary>Есть ли в системе шрифт со значками — проверяем один раз за запуск.</summary>
    private static bool Available
    {
        get
        {
            if (_available.HasValue) return _available.Value;

            var found = false;
            try
            {
                using var fonts = new InstalledFontCollection();
                found = fonts.Families.Any(family => string.Equals(family.Name, FontName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                found = false;
            }

            _available = found;
            return found;
        }
    }

    /// <summary>Ставит значок перед подписью кнопки. Молча ничего не делает, если шрифта нет.</summary>
    public static void Attach(Button button, string glyph, Color color)
    {
        if (button == null || !Available) return;

        try
        {
            button.Image = Draw(glyph, 16, color);
            button.ImageAlign = ContentAlignment.MiddleLeft;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
            button.Padding = new Padding(6, 0, 6, 0);
        }
        catch
        {
            // Значок — украшение: не получилось, кнопка остаётся как была.
        }
    }

    private static Bitmap Draw(string glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var font = new Font(FontName, size * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}
