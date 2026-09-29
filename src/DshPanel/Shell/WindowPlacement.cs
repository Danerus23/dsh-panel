using Avalonia;

namespace DshPanel.Shell;

/// <summary>
/// Ядро размещения окон панели: «сохранённый прямоугольник + рабочие области экранов →
/// прямоугольник, гарантированно видимый» и «панель, курсор, рабочие области → место
/// вспомогательного окна рядом с панелью».
///
/// **Здесь нет ни одного <c>Window</c>**, и это не стилистика: вход и выход — только
/// <see cref="PixelRect"/>, <see cref="PixelPoint"/> и <see cref="PixelSize"/>. Поэтому оба правила
/// проверяются подставными прямоугольниками, без экрана и без настоящего второго монитора,
/// которого у проверки нет вовсе. Размеры и координаты — в ПИКСЕЛЯХ экрана; перевод DIP в пиксели
/// делает вызывающий (<see cref="Pixels"/>, <see cref="Frame"/>).
///
/// Первое правило (<see cref="Restore"/>) — про главное окно. Зачем оно нужно (решение владельца
/// 26.09.2026, пункт 3 дефекта Д2): панель запоминает размер и положение окна, а на следующем
/// запуске ставит его туда же. Наивная запись «поставить как было» ломается на живых случаях,
/// и все они здесь названы:
///
/// * монитор отсоединили, разрешение сменили — координата из файла может указывать в пустоту,
///   и окно оказалось бы там, где его не найти и не за что взяться мышью;
/// * второй монитор стоит СЛЕВА, и его координаты ОТРИЦАТЕЛЬНЫ: «отрицательное значит мусор» —
///   это окно, которое каждый запуск переезжает на главный экран;
/// * файл настроек человек правит руками, поэтому мусор в нём — обычное дело, а не исключение.
///
/// Наружу отсюда выходит прямоугольник, за который можно взяться мышью и у которого видно
/// содержимое: свисающее за край окно подтягивается внутрь области.
///
/// Второе правило (<see cref="NearPanel"/>) — про четыре вспомогательных окна панели, и
/// у него свои доводы и свои живые случаи; они названы там.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Умолчание размера — то же, что стоит в разметке окна (860x720).</summary>
    public const int DefaultWidth = 640;

    public const int DefaultHeight = 640;

    /// <summary>
    /// Наименьший разумный размер окна — тот же, что <c>MinWidth</c>/<c>MinHeight</c> разметки:
    /// окно меньше собственного предела всё равно не станет, и обещать человеку другое нельзя.
    /// Совпадение с разметкой проверяется тестом.
    /// </summary>
    public const int MinWidth = 560;

    public const int MinHeight = 420;

    /// <summary>
    /// Граница «это ещё координата, а не мусор». Число нарочно далеко от жизни: настоящий
    /// рабочий стол из нескольких мониторов укладывается в сотни пикселей от нуля, а всё, что
    /// дальше, — испорченный файл. Проверка «на глаз» тут не годится: окно, уехавшее на
    /// 100 000 пикселей влево, человек найдёт не раньше, чем снесёт настройки.
    /// </summary>
    public const int CoordinateLimit = 100_000;

    /// <summary>Граница размера: «абсурдно большое» — тоже мусор, а не повод растянуть окно.</summary>
    public const int SizeLimit = 10_000;

    /// <summary>
    /// Полоса окна, которая обязана поместиться на экране, чтобы окно считалось видимым.
    /// «Пересекается с экраном на один пиксель» — это не «видно»: за такой край не взяться
    /// мышью, и найти окно человек не сможет. Полоса взята с запасом на заголовок окна.
    /// </summary>
    private const int GrabbableWidth = 200;

    private const int GrabbableHeight = 60;

    /// <summary>
    /// Зазор между панелью и окном, которое встаёт рядом (шкала панели 4/8/12/16/24).
    ///
    /// Зачем зазор вообще: окно, прижатое к панели вплотную, читается как её продолжение,
    /// и человек не понимает, что это ДРУГОЕ окно. 24 — самый крупный шаг шкалы: на нём
    /// граница видна и не выглядит случайной щелью.
    /// </summary>
    public const int PanelGap = 24;

    /// <summary>
    /// Прямоугольник, по которому окно откроется на следующем запуске. Ноль и мусор в любом
    /// поле означают «не задано» — берётся умолчание; законная отрицательная координата
    /// остаётся собой.
    /// </summary>
    public static PixelRect Restore(int x, int y, int width, int height, IReadOnlyList<PixelRect>? screens)
    {
        var areas = Areas(screens);

        // Экранов не знаем — гадать не о чем: окно остаётся того размера и в том месте, что
        // заданы разметкой. Это не «поставили куда попало», а честное «нечего учитывать».
        if (areas.Count == 0) return new PixelRect(0, 0, DefaultWidth, DefaultHeight);

        var size = new PixelSize(Size(width, DefaultWidth), Size(height, DefaultHeight));
        var spot = Position(x, y);

        // Позиция не задана (или испорчена) — окно встаёт по центру главной области.
        if (spot is null) return Centered(areas[0], Fit(size, areas[0]));

        var wanted = new PixelRect(spot.Value, size);

        // Размер зажимается по той области, на которой окно действительно окажется: сохранили
        // окно на большом мониторе, а открывают на маленьком.
        return Visible(Fit(wanted, AreaFor(wanted, areas)), areas);
    }

    /// <summary>
    /// Тот же прямоугольник, но гарантированно видимый. Отдельно от <see cref="Restore"/>:
    /// этим же правилом окно проверяется, когда его двигает сам человек, а не файл настроек.
    /// </summary>
    public static PixelRect Visible(PixelRect wanted, IReadOnlyList<PixelRect>? screens)
    {
        var areas = Areas(screens);
        if (areas.Count == 0) return wanted;

        foreach (var area in areas)
        {
            if (Grabbable(wanted, area)) return Keep(wanted, area);
        }

        // Ни на одном экране окна не видно (монитор отсоединили, разрешение сменили).
        // Переезжаем на главную область целиком — так окно видно и за него можно взяться.
        return Centered(areas[0], Fit(wanted.Size, areas[0]));
    }

    /// <summary>
    /// РАЗМЕР ОКНА В ПИКСЕЛЯХ ЭКРАНА: размеры разметки (DIP) × масштаб экрана.
    ///
    /// ⚠️ Почему это отдельная дверь, а не умножение на месте вызова. Рабочие области экранов
    /// приходят в ПИКСЕЛЯХ, а ширина окна в разметке — в DIP, и «пиксели = точки» верно только при
    /// масштабе 100 %. На мониторе «150 %» окно 860 DIP занимает 1290 пикселей, и решение, принятое
    /// по 860, поставило бы его за край экрана. Масштаб, размер без числа (у окна
    /// <c>SizeToContent</c> высота ещё не известна) и мусор обрабатываются ЗДЕСЬ, в одном месте, —
    /// и здесь же живёт правило «отрицательный размер не законен» (<see cref="NormalizeSize"/>).
    /// </summary>
    public static PixelSize Pixels(double width, double height, double scale) =>
        new(ToPixels(width, scale, DefaultWidth), ToPixels(height, scale, DefaultHeight));

    /// <summary>
    /// Прямоугольник окна в ПИКСЕЛЯХ экрана по тому, что о нём известно: где стоит его левый
    /// верхний угол, каков размер его клиентской части и каков масштаб экрана.
    ///
    /// ОДНА дверь на два места: связка запоминает место окна панели и она же ставит рядом с ним
    /// вспомогательные окна. Разойдись эти два перевода — окно встало бы «рядом» с местом,
    /// которого панель никогда не занимала.
    /// </summary>
    public static PixelRect Frame(PixelPoint position, Size clientSize, double scale)
    {
        // Ноль и мусор в масштабе — «как есть»: умножать на ноль значило бы стереть размер окна
        // (окно нулевой ширины в расчёте места — это «поставили куда попало»).
        var factor = Good(scale) && scale > 0 ? scale : 1;

        return new PixelRect(
            position.X,
            position.Y,
            (int)Math.Round(Math.Max(0, clientSize.Width) * factor),
            (int)Math.Round(Math.Max(0, clientSize.Height) * factor));
    }

    /// <summary>
    /// Где поставить ВСПОМОГАТЕЛЬНОЕ окно панели (настройки, копии, «Пики и тарифы»,
    /// «О программе», история цен, отчёт о проблеме) — ОДНО решение на все двери.
    ///
    /// ⚠️ **Решение изменено владельцем 29.09.2026, и прежнее отклонено им же дважды.**
    /// Сначала он сказал: *«Немного бесящий факт, что когда открываешь окно, оно открывается
    /// далеко от основной панели и приходится тянуться мышью»* — и панель стала ставить окно
    /// **рядом** с собой. Это не помогло: 29.09.2026 он сказал прямо — *«хочу чтобы окна
    /// открывались поверх основной панели, сейчас они открываются или слева или справа далеко»*.
    /// «Рядом» на широком экране и есть «далеко»: панель в 640 точек, а окно настроек — 1080,
    /// и сбоку для него места нет вовсе, поэтому оно уезжало к краю рабочего стола.
    ///
    /// Отсюда порядок:
    /// 1. **панель на экране** — окно встаёт **ПОВЕРХ неё**, по центру: там, где взгляд человека,
    ///    и наверняка недалеко. Панель при этом закрывается — это его решение, названное прямо;
    /// 2. **панель убрана в трей** (или её не видно ни на одном экране) — окно встаёт **у курсора**;
    /// 3. **ни панели, ни курсора** — окно по центру главной рабочей области.
    ///
    /// И всегда — в границах рабочего стола ТОГО экрана, где оно появляется
    /// (<see cref="Visible"/>): окно, уехавшее за край, человек не найдёт.
    ///
    /// ⚠️ Вход и выход — только <see cref="PixelRect"/>, <see cref="PixelPoint"/> и
    /// <see cref="PixelSize"/>: ни одного <c>Window</c>, ни экрана, ни курсора эта функция
    /// не знает. Поэтому все живые случаи (второй монитор СЛЕВА с отрицательными координатами,
    /// панель у правого края, свёрнутая панель, область меньше окна) проверяются подставными
    /// прямоугольниками — но живого вида окна у человека эти проверки не заменяют и не обещают.
    ///
    /// <param name="panel">Границы окна панели в пикселях или <c>null</c>, если панели на экране
    /// нет; <paramref name="cursor"/> — курсор в пикселях или <c>null</c>, если спросить его
    /// не вышло. Размер окна приходит уже в пикселях (<see cref="Pixels"/>).</param>
    /// </summary>
    public static PixelRect NearPanel(
        PixelSize size,
        PixelRect? panel,
        PixelPoint? cursor,
        IReadOnlyList<PixelRect>? screens)
    {
        var areas = Areas(screens);

        var wanted = new PixelSize(
            NormalizeSize(size.Width) > 0 ? size.Width : DefaultWidth,
            NormalizeSize(size.Height) > 0 ? size.Height : DefaultHeight);

        // Экранов не знаем — гадать не о чем: окно остаётся своего размера и в нуле.
        if (areas.Count == 0) return new PixelRect(0, 0, wanted.Width, wanted.Height);

        // Панель на экране — окно ПОВЕРХ неё, по центру. Панель, которая НЕ пересекает ни одной
        // рабочей области (монитор отсоединили), за панель не считается: «поверх» невидимого окна
        // означало бы поставить окно в пустоту.
        if (panel is { } frame && OnScreen(frame, areas)) return OverPanel(wanted, frame, areas);

        // Панель в трее — окно у курсора: там рука и взгляд человека.
        if (cursor is { } point)
        {
            var area = AreaFor(new PixelRect(point, wanted), areas);
            return Visible(new PixelRect(point, Fit(wanted, area)), areas);
        }

        return Centered(areas[0], Fit(wanted, areas[0]));
    }

    /// <summary>
    /// Окно ПОВЕРХ панели, по её центру (решение владельца 29.09.2026).
    ///
    /// Почему именно так, а не «рядом»: владелец сказал *«хочу чтобы окна открывались поверх
    /// основной панели, сейчас они открываются или слева или справа далеко»*. Прежнее правило
    /// ставило окно сбоку от панели, и на обычном экране это означало «у края рабочего стола»:
    /// окно настроек — 1080 точек, панель — 640, и справа от панели места нет вовсе.
    ///
    /// Центр панели, а не центра экрана: окно встаёт там, где взгляд человека, и остаётся
    /// рядом с панелью. Если окно БОЛЬШЕ панели (обычный случай: настройки 1080 против 640),
    /// оно всё равно центрируется по панели — и тогда <see cref="Visible"/> прижимает его
    /// внутрь рабочего стола, чтобы ни край, ни кнопка закрытия не уехали за границу.
    /// </summary>
    private static PixelRect OverPanel(PixelSize size, PixelRect panel, IReadOnlyList<PixelRect> areas)
    {
        var area = AreaFor(panel, areas);
        var fitted = Fit(size, area);

        var x = panel.X + ((panel.Width - fitted.Width) / 2);
        var y = panel.Y + ((panel.Height - fitted.Height) / 2);

        // Сначала пробуем по центру панели (в границах экрана), и только если так не выходит —
        // прижимаем к рабочей области: у невидимого окна теряются и кнопка закрытия, и край.
        var centered = new PixelRect(x, y, fitted.Width, fitted.Height);
        if (centered.X >= area.X && centered.Right <= area.Right &&
            centered.Y >= area.Y && centered.Bottom <= area.Bottom)
            return centered;

        return Visible(centered, new[] { area });
    }

    /// <summary>
    /// Окно РЯДОМ с панелью: справа, а если справа не помещается — слева. Панель при этом
    /// не закрывается: окно целиком лежит по одну её сторону.
    /// </summary>
    /// <remarks>
    /// Если не помещается ни справа, ни слева (область узкая, панель широкая) — окно прижимается
    /// к тому краю, где его видно больше. Это единственный случай, когда окно может накрыть панель,
    /// и он назван прямо: места «рядом» на таком экране просто нет, а окно обязано быть видимым
    /// целиком (у невидимого окна теряются кнопка закрытия и край содержимого).
    /// </remarks>
    private static PixelRect Beside(PixelSize size, PixelRect panel, IReadOnlyList<PixelRect> areas)
    {
        var area = AreaFor(panel, areas);
        var fitted = Fit(size, area);

        var top = Clamp(panel.Y, area.Y, Math.Max(area.Y, area.Bottom - fitted.Height));

        var right = panel.Right + PanelGap;
        if (Fits(right, fitted.Width, area)) return new PixelRect(right, top, fitted.Width, fitted.Height);

        var left = panel.X - PanelGap - fitted.Width;
        if (left >= area.X) return new PixelRect(left, top, fitted.Width, fitted.Height);

        // Ни справа, ни слева места нет: берём сторону, где его больше, и прижимаем окно внутрь.
        var x = area.Right - panel.Right >= panel.X - area.X
            ? area.Right - fitted.Width
            : area.X;

        return Visible(new PixelRect(x, top, fitted.Width, fitted.Height), new[] { area });
    }

    /// <summary>Окно этой ширины помещается в область, если начать с этой координаты.</summary>
    private static bool Fits(int x, int width, PixelRect area) =>
        x >= area.X && x + width <= area.Right;

    /// <summary>Пересекает ли прямоугольник хотя бы одну рабочую область.</summary>
    private static bool OnScreen(PixelRect frame, IReadOnlyList<PixelRect> areas)
    {
        foreach (var area in areas)
        {
            var overlap = Overlap(frame, area);
            if (overlap.Width > 0 && overlap.Height > 0) return true;
        }

        return false;
    }

    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));

    /// <summary>Размер в пикселях: мусор и «ещё не мерилось» — умолчание разметки.</summary>
    private static int ToPixels(double dip, double scale, int fallback)
    {
        if (!Good(dip) || dip <= 0) return fallback;

        var value = NormalizeSize((int)Math.Round(dip * (Good(scale) && scale > 0 ? scale : 1)));
        return value > 0 ? value : fallback;
    }

    /// <summary>Число, пригодное для умножения: не NaN и не бесконечность.</summary>
    private static bool Good(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// Размер из файла в сравнимом виде: ноль, отрицательное и абсурдно большое — «не задано»
    /// (файл правят руками). Остальное — размер; к границам его приводит <see cref="Fit"/>.
    /// </summary>
    public static int NormalizeSize(int value) => value is > 0 and <= SizeLimit ? value : 0;

    /// <summary>
    /// Координата из файла в сравнимом виде: абсурд — «не задано». Отрицательная координата
    /// при этом ЗАКОННА (второй монитор слева), и сводить её к нулю нельзя: это переезд окна
    /// на главный экран при каждом запуске.
    /// </summary>
    public static int NormalizeCoordinate(int value) =>
        value > -CoordinateLimit && value < CoordinateLimit ? value : 0;

    /// <summary>Размер с умолчанием: ноль из файла означает «не задано», а не «нулевая ширина».</summary>
    private static int Size(int value, int fallback)
    {
        var size = NormalizeSize(value);
        return size > 0 ? size : fallback;
    }

    /// <summary>
    /// Место из файла. Ноль в обоих полях — «не задано» (умолчания настроек нулевые); мусор
    /// хотя бы в одном поле — тоже «не задано»: половина испорченной координаты не становится
    /// верной от того, что вторая уцелела. Всё остальное, включая отрицательные координаты, —
    /// место как есть.
    /// </summary>
    private static PixelPoint? Position(int x, int y)
    {
        if (x == 0 && y == 0) return null;
        if (NormalizeCoordinate(x) != x || NormalizeCoordinate(y) != y) return null;

        return new PixelPoint(x, y);
    }

    /// <summary>Рабочие области, у которых есть размер: пустая запись экрана — не область.</summary>
    private static List<PixelRect> Areas(IReadOnlyList<PixelRect>? screens)
    {
        var areas = new List<PixelRect>();

        foreach (var area in screens ?? Array.Empty<PixelRect>())
        {
            if (area.Width > 0 && area.Height > 0) areas.Add(area);
        }

        return areas;
    }

    /// <summary>Область, на которой окно окажется: с наибольшим пересечением, иначе главная.</summary>
    private static PixelRect AreaFor(PixelRect wanted, IReadOnlyList<PixelRect> areas)
    {
        var best = areas[0];
        var bestShare = 0L;

        foreach (var area in areas)
        {
            var overlap = Overlap(wanted, area);
            var share = (long)overlap.Width * overlap.Height;

            if (share > bestShare)
            {
                bestShare = share;
                best = area;
            }
        }

        return best;
    }

    /// <summary>
    /// Размер окна в границах области: меньше наименьшего — поднимаем до наименьшего (окно
    /// меньше своего предела всё равно не станет), больше области — зажимаем по области.
    /// Место не трогаем: им занимается <see cref="Visible"/>.
    /// </summary>
    private static PixelRect Fit(PixelRect wanted, PixelRect area) => new(
        wanted.X,
        wanted.Y,
        Math.Clamp(wanted.Width, MinWidth, Math.Max(MinWidth, area.Width)),
        Math.Clamp(wanted.Height, MinHeight, Math.Max(MinHeight, area.Height)));

    /// <summary>Размер в границах области — тем же правилом, для размера без места.</summary>
    private static PixelSize Fit(PixelSize size, PixelRect area) => new(
        Math.Clamp(size.Width, MinWidth, Math.Max(MinWidth, area.Width)),
        Math.Clamp(size.Height, MinHeight, Math.Max(MinHeight, area.Height)));

    /// <summary>Окно по центру области — так оно и видно, и не прижато к краю.</summary>
    private static PixelRect Centered(PixelRect area, PixelSize size)
    {
        var width = Math.Min(size.Width, area.Width);
        var height = Math.Min(size.Height, area.Height);

        return new PixelRect(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// Окно помещается на области — подтягиваем его внутрь, если оно свисает за край: у такого
    /// окна теряются кнопка закрытия и край содержимого, и «видимым» его назвать нельзя.
    /// Окно ШИРЕ области сдвинуть нельзя — тогда оно просто прижимается к её левому краю.
    /// </summary>
    private static PixelRect Keep(PixelRect wanted, PixelRect area) => new(
        Math.Max(area.X, Math.Min(wanted.X, area.Right - wanted.Width)),
        Math.Max(area.Y, Math.Min(wanted.Y, area.Bottom - wanted.Height)),
        wanted.Width,
        wanted.Height);

    /// <summary>Помещается ли на этой области полоса, за которую окно можно взять мышью.</summary>
    private static bool Grabbable(PixelRect wanted, PixelRect area)
    {
        var overlap = Overlap(wanted, area);

        return overlap.Width >= Math.Min(GrabbableWidth, wanted.Width)
            && overlap.Height >= Math.Min(GrabbableHeight, wanted.Height);
    }

    /// <summary>Пересечение прямоугольников. Пустое пересечение — нулевой размер, а не исключение.</summary>
    private static PixelRect Overlap(PixelRect left, PixelRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.Right, right.Right);
        var bottom = Math.Min(left.Bottom, right.Bottom);

        return rightEdge <= x || bottom <= y
            ? default
            : new PixelRect(x, y, rightEdge - x, bottom - y);
    }
}
