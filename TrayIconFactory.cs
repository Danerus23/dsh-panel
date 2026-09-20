using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DshTray;

public enum ServerVisual
{
    Running,
    Stopped,
    BusyOther,
}

/// <summary>
/// Иконка трея рисуется кодом: тёмная плитка со знаком «приглашение командной строки»,
/// состояние сервера — цветная точка в правом нижнем углу (зелёная — работает, серая —
/// остановлен, жёлтая — порт занят чужим процессом), пик — оранжевый жетон в правом
/// верхнем углу.
/// </summary>
public static class TrayIconFactory
{
    private static readonly Color TileTop = Color.FromArgb(255, 15, 23, 42);
    private static readonly Color TileBottom = Color.FromArgb(255, 30, 41, 59);
    private static readonly Color Text = Color.FromArgb(255, 226, 232, 240);
    private static readonly Color Running = Color.FromArgb(255, 34, 197, 94);
    private static readonly Color Stopped = Color.FromArgb(255, 148, 163, 184);
    private static readonly Color Busy = Color.FromArgb(255, 234, 179, 8);
    private static readonly Color Peak = Color.FromArgb(255, 249, 115, 22);

    public static Icon Create(ServerVisual server, bool peak, int size)
    {
        using var bitmap = Render(server, peak, size);
        var handle = bitmap.GetHicon();
        using var fromHandle = Icon.FromHandle(handle);

        // Копия владеет своими данными, поэтому исходный HICON можно освободить сразу.
        var icon = (Icon)fromHandle.Clone();
        DestroyIcon(handle);
        return icon;
    }

    public static Bitmap Render(ServerVisual server, bool peak, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var radius = Math.Max(2f, size * 0.22f);
        var border = Math.Max(1f, size * 0.06f);

        using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), radius))
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, size, size), TileTop, TileBottom, 90f))
        {
            graphics.FillPath(fill, path);
            using var pen = new Pen(peak ? Peak : Color.FromArgb(255, 51, 65, 85), border);
            graphics.DrawPath(pen, path);
        }

        // Знак «панель»: приглашение командной строки — шеврон и черта под ним.
        // Буквы на 16 px не читаются, а две штриховые фигуры — вполне; правый нижний угол
        // оставлен точке состояния, правый верхний — жетону пика.
        var stroke = Math.Max(1.3f, size * 0.11f);
        var left = size * 0.24f;
        var top = size * 0.28f;
        var step = size * 0.18f;
        using (var pen = new Pen(Text, stroke)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
                   LineJoin = LineJoin.Round,
               })
        {
            graphics.DrawLines(pen, new[]
            {
                new PointF(left, top),
                new PointF(left + step, top + step),
                new PointF(left, top + step * 2),
            });

            var lineY = top + step * 2 + stroke * 1.4f;
            graphics.DrawLine(pen,
                new PointF(left + step * 0.5f, lineY),
                new PointF(left + step * 1.9f, lineY));
        }

        // Точка состояния сервера — правый нижний угол.
        var dotSize = Math.Max(3f, size * 0.34f);
        var dotColor = server switch
        {
            ServerVisual.Running => Running,
            ServerVisual.BusyOther => Busy,
            _ => Stopped,
        };
        using (var dot = new SolidBrush(dotColor))
        using (var ring = new Pen(Color.FromArgb(255, 15, 23, 42), Math.Max(1f, size * 0.08f)))
        {
            var rect = new RectangleF(size - dotSize - size * 0.02f, size - dotSize - size * 0.02f, dotSize, dotSize);
            graphics.FillEllipse(dot, rect);
            graphics.DrawEllipse(ring, rect);
        }

        // Пик — жетон в правом верхнем углу.
        if (peak)
        {
            var badge = Math.Max(3f, size * 0.30f);
            var rect = new RectangleF(size - badge - size * 0.02f, size * 0.02f, badge, badge);
            using var badgeBrush = new SolidBrush(Peak);
            using var badgeRing = new Pen(Color.FromArgb(255, 15, 23, 42), Math.Max(1f, size * 0.07f));
            graphics.FillEllipse(badgeBrush, rect);
            graphics.DrawEllipse(badgeRing, rect);
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
