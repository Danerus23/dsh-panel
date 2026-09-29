using System;
using Avalonia;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки ЯДРА размещения окон — подставными прямоугольниками, без экрана.
///
/// Ядро (<c>Shell\WindowPlacement.cs</c>) нарочно не знает ни одного <c>Window</c>: поэтому здесь
/// проверяются ровно те живые случаи, ради которых оно и написано, — отсоединённый монитор,
/// второй монитор СЛЕВА (законные отрицательные координаты), мусор в файле настроек, панель
/// у правого края, свёрнутая в трей панель и область меньше окна.
/// Ни один из них на настоящем рабочем столе не воспроизводится, а все они случаются.
///
/// ⚠️ **Чего эти проверки НЕ доказывают:** что окно у человека выглядит правильно. Живой вид
/// (нитка интерфейса, второй монитор, свёрнутая панель) проверяет человек — подставные
/// прямоугольники заменяют собой решение, а не глаз.
/// </summary>
public class WindowPlacementTests
{
    /// <summary>Главный экран — область 0..1920, 0..1080.</summary>
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);

    /// <summary>Второй монитор СЛЕВА: его координаты отрицательны, и это ЗАКОННО.</summary>
    private static readonly PixelRect Left = new(-1920, 0, 1920, 1080);

    private static PixelRect[] TwoScreens() => new[] { Primary, Left };

    private static bool Inside(PixelRect frame, PixelRect area) =>
        frame.X >= area.X && frame.Y >= area.Y && frame.Right <= area.Right && frame.Bottom <= area.Bottom;

    [Fact]
    public void Окно_внутри_области_остаётся_как_есть()
    {
        var frame = WindowPlacement.Restore(100, 120, 860, 720, new[] { Primary });

        Assert.Equal(new PixelRect(100, 120, 860, 720), frame);
    }

    /// <summary>
    /// Монитор отсоединили — координата из файла указывает в пустоту. Окно обязано переехать
    /// на видимую область: иначе человек его не найдёт и за край не возьмётся.
    /// </summary>
    [Fact]
    public void Окно_за_всеми_экранами_переезжает_на_видимую_область()
    {
        var frame = WindowPlacement.Restore(-4000, 100, 860, 720, new[] { Primary });

        Assert.Equal(860, frame.Width);
        Assert.Equal(720, frame.Height);
        Assert.True(Inside(frame, Primary), $"окно осталось за экраном: {frame}");
    }

    /// <summary>
    /// ПОЛОЖИТЕЛЬНЫЙ КОНТРОЛЬ. Второй монитор стоит слева, и координаты у него отрицательные.
    /// Правило «отрицательное — значит мусор» перетаскивало бы такое окно на главный экран
    /// при каждом запуске, а человек считал бы это поломкой панели.
    /// </summary>
    [Fact]
    public void Отрицательная_координата_второго_монитора_не_считается_пропажей()
    {
        var frame = WindowPlacement.Restore(-1600, 200, 860, 720, TwoScreens());

        Assert.Equal(new PixelRect(-1600, 200, 860, 720), frame);
    }

    [Fact]
    public void Размер_больше_области_зажимается()
    {
        var frame = WindowPlacement.Restore(100, 100, 5000, 5000, new[] { Primary });

        Assert.Equal(Primary.Width, frame.Width);
        Assert.Equal(Primary.Height, frame.Height);
        Assert.True(Inside(frame, Primary), $"окно вылезло за область: {frame}");
    }

    [Fact]
    public void Размер_меньше_минимального_поднимается_до_минимума()
    {
        var frame = WindowPlacement.Restore(100, 100, 300, 200, new[] { Primary });

        Assert.Equal(WindowPlacement.MinWidth, frame.Width);
        Assert.Equal(WindowPlacement.MinHeight, frame.Height);
    }

    /// <summary>Окно, видное на один пиксель, — это не «видно»: за такой край не взяться.</summary>
    [Fact]
    public void Окно_за_краем_экрана_переезжает_целиком()
    {
        var frame = WindowPlacement.Restore(1919, 500, 860, 720, new[] { Primary });

        Assert.True(Inside(frame, Primary), $"окно осталось за краем: {frame}");
    }

    /// <summary>Мусор в РАЗМЕРЕ (ноль, отрицательное, абсурдно большое) — умолчание 860x720.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, -50)]
    [InlineData(100000, 100000)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void Мусор_в_размере_даёт_умолчание(int width, int height)
    {
        var frame = WindowPlacement.Restore(100, 100, width, height, new[] { Primary });

        Assert.Equal(WindowPlacement.DefaultWidth, frame.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, frame.Height);

        // Место при испорченном размере не трогаем: сохранённая позиция уцелела.
        Assert.Equal(100, frame.X);
        Assert.Equal(100, frame.Y);
    }

    /// <summary>
    /// Позиция не задана (нули — это умолчание полей в настройках) или испорчена — окно встаёт
    /// ВНУТРИ области, а не «где получилось».
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50000, 50000)]
    [InlineData(-4000, 100)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Позиция_не_задана_или_испорчена_ставит_окно_внутри_области(int x, int y)
    {
        var frame = WindowPlacement.Restore(x, y, 860, 720, new[] { Primary });

        Assert.Equal(860, frame.Width);
        Assert.Equal(720, frame.Height);
        Assert.True(Inside(frame, Primary), $"окно не в области: {frame}");
    }

    /// <summary>
    /// Приведение значений из файла: абсурд — «не задано», а законная отрицательная координата
    /// остаётся собой. Здесь же видно, что размер и координата судятся РАЗНЫМИ правилами.
    /// </summary>
    [Fact]
    public void Мусор_отсекается_а_законная_отрицательная_координата_остаётся()
    {
        Assert.Equal(0, WindowPlacement.NormalizeCoordinate(WindowPlacement.CoordinateLimit));
        Assert.Equal(0, WindowPlacement.NormalizeCoordinate(-WindowPlacement.CoordinateLimit));
        Assert.Equal(0, WindowPlacement.NormalizeCoordinate(int.MinValue));
        Assert.Equal(-1600, WindowPlacement.NormalizeCoordinate(-1600));
        Assert.Equal(320, WindowPlacement.NormalizeCoordinate(320));

        Assert.Equal(0, WindowPlacement.NormalizeSize(0));
        Assert.Equal(0, WindowPlacement.NormalizeSize(-1));
        Assert.Equal(0, WindowPlacement.NormalizeSize(WindowPlacement.SizeLimit + 1));
        Assert.Equal(300, WindowPlacement.NormalizeSize(300));
        Assert.Equal(WindowPlacement.SizeLimit, WindowPlacement.NormalizeSize(WindowPlacement.SizeLimit));
    }

    /// <summary>
    /// Экранов панель не знает (их ещё не спросили) — гадать не о чем: окно остаётся того
    /// размера, что задан разметкой, и в нуле. Это честное «нечего учитывать», а не «поставили
    /// куда попало».
    /// </summary>
    [Fact]
    public void Без_знакомых_экранов_геометрия_остаётся_умолчанием()
    {
        var empty = WindowPlacement.Restore(100, 100, 500, 500, Array.Empty<PixelRect>());

        Assert.Equal(new PixelRect(0, 0, WindowPlacement.DefaultWidth, WindowPlacement.DefaultHeight), empty);
    }

    [Fact]
    public void Видимое_окно_не_двигается_а_невидимое_переезжает()
    {
        var inside = new PixelRect(200, 200, 860, 720);
        Assert.Equal(inside, WindowPlacement.Visible(inside, TwoScreens()));

        var gone = new PixelRect(4000, 200, 860, 720);
        Assert.True(Inside(WindowPlacement.Visible(gone, new[] { Primary }), Primary));
    }

    // ---- место вспомогательного окна: рядом с панелью, в трее — у курсора --------------------

    /// <summary>Размер окон настроек и копий (860×720 в разметке) — им и проверяем решение.</summary>
    private static PixelSize Size() => new(WindowPlacement.DefaultWidth, WindowPlacement.DefaultHeight);

    /// <summary>Панель в левой части главного экрана — обычное её место у человека.</summary>
    private static readonly PixelRect Panel = new(100, 120, 860, 720);

    /// <summary>
    /// ПАНЕЛЬ НА ЭКРАНЕ — ОКНО ВСТАЁТ **ПОВЕРХ НЕЁ**, ПО ЦЕНТРУ.
    ///
    /// ⚠️ **Решение изменено владельцем 29.09.2026** (*«хочу чтобы окна открывались поверх
    /// основной панели, сейчас они открываются или слева или справа далеко»*). Прежнее правило
    /// ставило окно РЯДОМ, и это он же и отверг: панель в 640 точек, окно настроек в 1080 —
    /// справа от панели места нет, и окно уезжало к краю рабочего стола, то есть «далеко».
    ///
    /// Проверяется именно ЦЕНТР ПО ПАНЕЛИ (а не по экрану): окно встаёт там, где взгляд человека.
    /// </summary>
    [Fact]
    public void Панель_на_экране_окно_встаёт_поверх_неё_по_центру()
    {
        var frame = WindowPlacement.NearPanel(Size(), Panel, cursor: null, new[] { Primary });

        // Центр окна совпал с центром панели — это и есть «поверх панели».
        Assert.Equal(Panel.X + (Panel.Width / 2), frame.X + (frame.Width / 2));
        Assert.Equal(Panel.Y + (Panel.Height / 2), frame.Y + (frame.Height / 2));
        Assert.Equal(Size().Width, frame.Width);
        Assert.Equal(Size().Height, frame.Height);

        // И окно целиком на экране: у невидимого окна теряются и кнопка закрытия, и край.
        Assert.True(Inside(frame, Primary), $"окно вылезло за экран: {frame}");
    }

    /// <summary>
    /// ПАНЕЛЬ У ПРАВОГО КРАЯ, А ОКНО ШИРЕ МЕСТА — ОКНО ПРИЖИМАЕТСЯ ВНУТРЬ ЭКРАНА.
    ///
    /// Панель в 860 точек у самого края — живой случай: человек подвинул её к краю сам.
    /// Окно в 1080 по центру такой панели вылезло бы за экран на 430 точек, и у него не стало бы
    /// видно ни кнопки закрытия, ни правого края. Поэтому центрирование по панели — только если
    /// окно от этого остаётся ЦЕЛИКОМ на экране.
    ///
    /// ⚠️ Размер окна назван здесь прямо (1080×900 — окно настроек), а НЕ взят у умолчания ядра:
    /// главное окно стало 640 (замечание владельца 29.09.2026), оно целиком помещается у такого
    /// края, и через умолчание проверка перестала бы проходить через состояние, в котором дефект
    /// возможен, — то есть зеленела бы всегда.
    /// </summary>
    [Fact]
    public void Панель_у_правого_края_окно_прижимается_внутрь_экрана()
    {
        var panel = new PixelRect(Primary.Right - 860, 100, 860, 720);
        var wide = new PixelSize(1080, 900);

        var frame = WindowPlacement.NearPanel(wide, panel, cursor: null, new[] { Primary });

        Assert.True(Inside(frame, Primary), $"окно вылезло за экран: {frame}");
        Assert.Equal(Primary.Right - frame.Width, frame.X);
    }

    /// <summary>
    /// ВТОРОЙ МОНИТОР СЛЕВА: координаты отрицательны, и это ЗАКОННО. Панель стоит на нём — значит
    /// окно обязано встать на ТОМ ЖЕ мониторе, рядом с панелью, а не «на главном, потому что
    /// отрицательное — это мусор». Ровно этот случай и ломался бы правилом «сводить всё к нулю».
    /// </summary>
    [Fact]
    public void Панель_на_втором_мониторе_с_отрицательным_X_ставит_окно_на_тот_же_монитор()
    {
        // Панель у ЛЕВОГО края левого монитора: справа от неё как раз помещается окно.
        var panel = new PixelRect(Left.X + 40, 200, 860, 720);

        var frame = WindowPlacement.NearPanel(Size(), panel, cursor: null, TwoScreens());

        Assert.True(frame.X < 0, $"окно уехало с левого монитора на главный: {frame}");
        Assert.True(Inside(frame, Left), $"окно не на мониторе панели: {frame} против {Left}");

        // По центру панели — на ТОМ ЖЕ мониторе, с законными отрицательными координатами.
        Assert.Equal(panel.X + (panel.Width / 2), frame.X + (frame.Width / 2));
    }

    /// <summary>
    /// ПАНЕЛЬ УБРАНА В ТРЕЙ — ОКНО ВСТАЁТ У КУРСОРА: там рука и взгляд человека. Решение владельца
    /// 28.09.2026, принято без изменений.
    /// </summary>
    [Fact]
    public void Панель_в_трее_окно_встаёт_у_курсора()
    {
        var cursor = new PixelPoint(500, 200);

        var frame = WindowPlacement.NearPanel(Size(), panel: null, cursor, new[] { Primary });

        Assert.Equal(cursor.X, frame.X);
        Assert.Equal(cursor.Y, frame.Y);
        Assert.True(Inside(frame, Primary), $"окно у курсора вылезло за экран: {frame}");
    }

    /// <summary>
    /// Курсор у самого края экрана — окно ПОДТЯГИВАЕТСЯ внутрь области: у окна, свисающего за край,
    /// теряются кнопка закрытия и край содержимого, и «видимым» его назвать нельзя.
    /// </summary>
    [Fact]
    public void Курсор_у_края_экрана_окно_подтягивается_внутрь()
    {
        var frame = WindowPlacement.NearPanel(Size(), panel: null, new PixelPoint(1900, 1070), new[] { Primary });

        Assert.True(Inside(frame, Primary), $"окно у края осталось за экраном: {frame}");
    }

    /// <summary>
    /// НИ ПАНЕЛИ, НИ КУРСОРА (панель в трее, а курсор спросить не вышло) — окно по центру главной
    /// рабочей области: это честное «нечего учитывать», а не «поставили в угол по нулю».
    /// </summary>
    [Fact]
    public void Без_панели_и_без_курсора_окно_по_центру_главной_области()
    {
        var frame = WindowPlacement.NearPanel(Size(), panel: null, cursor: null, new[] { Primary });

        Assert.Equal(Primary.X + (Primary.Width - Size().Width) / 2, frame.X);
        Assert.Equal(Primary.Y + (Primary.Height - Size().Height) / 2, frame.Y);
    }

    /// <summary>
    /// ПАНЕЛЬ ЗА ВСЕМИ ЭКРАНАМИ (монитор отсоединили, разрешение сменили) — «рядом с панелью»
    /// означало бы поставить окно в пустоту. Окно идёт к курсору, а без курсора — по центру.
    /// </summary>
    [Fact]
    public void Панель_за_экранами_рядом_с_ней_окно_не_ставится()
    {
        var gone = new PixelRect(-5000, 100, 860, 720);

        var atCursor = WindowPlacement.NearPanel(Size(), gone, new PixelPoint(300, 300), new[] { Primary });
        Assert.Equal(300, atCursor.X);
        Assert.Equal(300, atCursor.Y);

        var centered = WindowPlacement.NearPanel(Size(), gone, cursor: null, new[] { Primary });
        Assert.True(Inside(centered, Primary), $"окно не в области: {centered}");
    }

    /// <summary>
    /// РАБОЧАЯ ОБЛАСТЬ МЕНЬШЕ ОКНА. Окно ужимается до области и прижимается внутрь: обещать ему
    /// размер, которого на экране нет, нельзя, а свисающее окно человек не найдёт.
    /// </summary>
    [Fact]
    public void Область_меньше_окна_ужимает_окно_и_прижимает_его_внутрь()
    {
        var small = new PixelRect(0, 0, WindowPlacement.MinWidth, WindowPlacement.MinHeight);

        var frame = WindowPlacement.NearPanel(new PixelSize(2000, 2000), panel: null, cursor: null, new[] { small });

        Assert.Equal(small.Width, frame.Width);
        Assert.Equal(small.Height, frame.Height);
        Assert.True(Inside(frame, small), $"окно вылезло из маленькой области: {frame}");
    }

    /// <summary>
    /// И тот же случай, но ПАНЕЛЬ НА ЭКРАНЕ: места «рядом» на узком экране нет вовсе, и окно
    /// прижимается внутрь области. Единственный случай, когда оно может накрыть панель, — и он
    /// назван прямо в коде, а здесь закреплён: окно обязано быть видимым и за него должно быть
    /// за что взяться.
    /// </summary>
    [Fact]
    public void На_узком_экране_окно_остаётся_видимым_даже_рядом_с_широкой_панелью()
    {
        var small = new PixelRect(0, 0, WindowPlacement.MinWidth, WindowPlacement.MinHeight);
        var panel = new PixelRect(0, 0, small.Width, small.Height);

        var frame = WindowPlacement.NearPanel(new PixelSize(2000, 2000), panel, cursor: null, new[] { small });

        Assert.True(frame.Width <= small.Width, $"окно шире области: {frame}");
        Assert.True(frame.X >= small.X && frame.Right <= small.Right, $"окно вылезло за область: {frame}");
    }

    /// <summary>Мусор в размере окна (ноль, отрицательное, «ещё не мерилось») — умолчание разметки.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-100, -50)]
    [InlineData(100000, 100000)]
    public void Мусор_в_размере_вспомогательного_окна_даёт_умолчание(int width, int height)
    {
        var frame = WindowPlacement.NearPanel(new PixelSize(width, height), Panel, cursor: null, new[] { Primary });

        Assert.Equal(WindowPlacement.DefaultWidth, frame.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, frame.Height);
    }

    /// <summary>
    /// Экранов панель не знает — гадать не о чем: окно остаётся своего размера и в нуле.
    /// Это честное «нечего учитывать», а не «поставили куда попало».
    /// </summary>
    [Fact]
    public void Без_знакомых_экранов_окно_остаётся_в_нуле()
    {
        var frame = WindowPlacement.NearPanel(Size(), Panel, new PixelPoint(500, 200), Array.Empty<PixelRect>());

        Assert.Equal(new PixelRect(0, 0, Size().Width, Size().Height), frame);
    }

    /// <summary>
    /// DPI: РАЗМЕР ОКНА ПЕРЕВОДИТСЯ В ПИКСЕЛИ МАСШТАБОМ ЭКРАНА. Рабочие области приходят
    /// в пикселях, а ширина окна в разметке — в DIP, и «пиксели = точки» верно только при 100 %.
    /// На мониторе «150 %» окно 860 DIP занимает 1290 пикселей, и решение, принятое по 860,
    /// поставило бы его за край экрана.
    /// </summary>
    [Fact]
    public void Размер_окна_переводится_в_пиксели_масштабом_экрана()
    {
        Assert.Equal(new PixelSize(1290, 1080), WindowPlacement.Pixels(860, 720, 1.5));
        Assert.Equal(new PixelSize(860, 720), WindowPlacement.Pixels(860, 720, 1));

        // Масштаб не спрошен (ноль) или испорчен (NaN) — «как есть», а не «пропало вовсе».
        Assert.Equal(new PixelSize(860, 720), WindowPlacement.Pixels(860, 720, 0));
        Assert.Equal(new PixelSize(860, 720), WindowPlacement.Pixels(860, 720, double.NaN));

        // Размер без числа (окно `SizeToContent` ещё не мерилось) и отрицательный — умолчание.
        Assert.Equal(
            new PixelSize(WindowPlacement.DefaultWidth, WindowPlacement.DefaultHeight),
            WindowPlacement.Pixels(double.NaN, -5, 1));
    }

    /// <summary>
    /// DPI МЕНЯЕТ ИМЕННО РЕШЕНИЕ, а не только число: на экране «200 %» окно вдвое шире, и «по центру»
    /// означает другую координату. Проверка сторожит это числами: если бы масштаб не учитывался,
    /// окно считалось бы вдвое уже и встало бы заметно правее.
    /// </summary>
    [Fact]
    public void Масштаб_экрана_меняет_место_окна_а_не_только_его_размер()
    {
        var wide = new PixelRect(0, 0, 3840, 2160);

        var at100 = WindowPlacement.NearPanel(WindowPlacement.Pixels(860, 720, 1), null, null, new[] { wide });
        var at200 = WindowPlacement.NearPanel(WindowPlacement.Pixels(860, 720, 2), null, null, new[] { wide });

        Assert.Equal((3840 - 860) / 2, at100.X);
        Assert.Equal((3840 - 1720) / 2, at200.X);
        Assert.NotEqual(at100.X, at200.X);
    }

    /// <summary>Рамка окна панели в пикселях: ОДИН перевод DIP в пиксели на два места (см. <c>Frame</c>).</summary>
    [Fact]
    public void Рамка_окна_считается_в_пикселях_экрана()
    {
        Assert.Equal(
            new PixelRect(100, 50, 1075, 900),
            WindowPlacement.Frame(new PixelPoint(100, 50), new Size(860, 720), 1.25));

        // Испорченный масштаб — «как есть»; нулевая клиентская часть остаётся нулевой (окно
        // ещё не мерилось, и выдумывать ей размер нельзя).
        Assert.Equal(
            new PixelRect(10, 20, 860, 0),
            WindowPlacement.Frame(new PixelPoint(10, 20), new Size(860, 0), 0));
    }
}
