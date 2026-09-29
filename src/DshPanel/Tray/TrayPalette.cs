using DshPanel.Shell;

namespace DshPanel.Tray;

/// <summary>
/// Цвета меню значка — ОДНО место на кружок и текст трёх строк состояния.
///
/// Решение владельца 27.09.2026: строки состояния в меню должны быть ЦВЕТНЫМИ (вне пика —
/// зелёный, пик — янтарный, авария — красный), а слева от каждой — маленький кружок того же
/// тона. Цвета собираются здесь и только здесь: рисование тонов не знает, оно спрашивает
/// готовый цвет (<see cref="Circle"/> и <see cref="Text"/>), а тон ему даёт
/// <see cref="TrayStatus"/>.
///
/// **Цвета выбраны ЧИСЛАМИ, а не на глаз.** Фон меню бывает светлым (Windows сообщил
/// <c>COLOR_MENU = 0x00F0F0F0</c> — измерено) и тёмным (`#2B2B2B` в тёмной теме), и один и тот
/// же зелёный на одном из них пропадает. Поэтому:
///
/// * у КРУЖКА один цвет на оба фона — он подобран так, чтобы контраст (отношение яркостей
///   по WCAG) был не ниже <see cref="CircleContrastFloor"/> против белого, <c>#F0F0F0</c>,
///   <c>#2B2B2B</c>, <c>#1F1F1F</c> и чёрного;
/// * у ТЕКСТА два варианта — тёмный для светлого фона и светлый для тёмного: тонкая цветная
///   надпись на светлом фоне не читается, а на тёмном нужен обратный ход. Контраст варианта
///   обязан быть не ниже <see cref="TextContrastFloor"/> против фона СВОЕГО вида.
///
/// **Какая краска нужна — решает не «на глаз», а сравнение контрастов** (<see cref="Ink"/>):
/// берётся та из двух, что читается лучше. Порог, на котором они меняются местами, выходит
/// из самих цветов (≈0,19 по относительной яркости), а не выдуман числом.
///
/// ⚠️ Чего эти числа НЕ обещают: фона ровно посередине между светлым и тёмным (Windows таких
/// меню не рисует, но проверка это видит — контраст там естественно ниже). И того, что тёмная
/// краска прочитается на ЧУЖОМ фоне, если человек поставит нестандартную тему.
/// </summary>
internal static class TrayPalette
{
    /// <summary>Светлый фон меню: ровно то, что вернула Windows на машине владельца (`COLOR_MENU`).</summary>
    internal const uint MenuLight = 0xF0F0F0;

    /// <summary>Тёмный фон меню — тёмная тема Windows.</summary>
    internal const uint MenuDark = 0x2B2B2B;

    /// <summary>Порог читаемости кружка (нетекстовый элемент: 3:1 по WCAG).</summary>
    internal const double CircleContrastFloor = 3.0;

    /// <summary>Порог читаемости текста (4,5:1 по WCAG).</summary>
    internal const double TextContrastFloor = 4.5;

    /// <summary>Чернила для светлого фона — почти чёрные.</summary>
    internal const uint InkOnLight = 0x1A1A1A;

    /// <summary>Чернила для тёмного фона — почти белые.</summary>
    internal const uint InkOnDark = 0xF0F0F0;

    // Цвета тонов. Каждое число — результат замера контраста (см. `TrayPaletteTests`):
    // проверка перебирает все фоны и падает, если число перестанет читаться.
    private const uint GoodCircle = 0x1E8E3E;      // зелёный: 3,37 худший контраст
    private const uint WarningCircle = 0xA87A08;   // янтарный: 3,38
    private const uint BadCircle = 0xE04B4B;       // красный: 3,49
    private const uint NeutralCircle = 0x828282;   // серый: 3,37

    private const uint GoodTextOnLight = 0x14682F;     // 6,04 на #F0F0F0
    private const uint WarningTextOnLight = 0x8A6508;  // 4,67
    private const uint BadTextOnLight = 0xC62828;      // 4,93
    private const uint NeutralTextOnLight = 0x666666;  // 5,04

    private const uint GoodTextOnDark = 0x6FCF8B;      // 7,40 на #2B2B2B
    private const uint WarningTextOnDark = 0xE8B84B;   // 7,68
    private const uint BadTextOnDark = 0xF08A8A;       // 5,87
    private const uint NeutralTextOnDark = 0xB0B0B0;   // 6,53

    /// <summary>Цвет кружка перед строкой состояния.</summary>
    internal static uint Circle(TrayTone tone) => tone switch
    {
        TrayTone.Good => GoodCircle,
        TrayTone.Warning => WarningCircle,
        TrayTone.Bad => BadCircle,
        _ => NeutralCircle,
    };

    /// <summary>Цвет текста строки состояния на фоне такой-то яркости.</summary>
    internal static uint Text(TrayTone tone, uint background) => IsLightBackground(background)
        ? TextOnLight(tone)
        : TextOnDark(tone);

    /// <summary>
    /// Чернила для этого фона: тёмные, если фон светлый, светлые — если тёмный.
    ///
    /// Не «если яркость больше половины»: сравнение контрастов даёт тот же ответ на настоящих
    /// фонах Windows и не врёт там, где порог «половина» ошибается (на среднесером фоне лучше
    /// читаются именно тёмные чернила — измерено).
    /// </summary>
    internal static uint Ink(uint background) =>
        IsLightBackground(background) ? InkOnLight : InkOnDark;

    /// <summary>Светлый ли фон — то есть нужны ли на нём тёмные чернила.</summary>
    internal static bool IsLightBackground(uint background) =>
        Contrast(InkOnLight, background) >= Contrast(InkOnDark, background);

    /// <summary>Отношение контраста двух цветов по WCAG: от 1 (не видно) до 21 (чёрное на белом).</summary>
    internal static double Contrast(uint first, uint second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Относительная яркость цвета по WCAG (0 — чёрный, 1 — белый).</summary>
    internal static double Luminance(uint rgb)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel((int)((rgb >> 16) & 0xFF)))
             + (0.7152 * Channel((int)((rgb >> 8) & 0xFF)))
             + (0.0722 * Channel((int)(rgb & 0xFF)));
    }

    /// <summary>
    /// Цвет Win32 (<c>COLORREF</c>, <c>0x00BBGGRR</c>) в обычный <c>0xRRGGBB</c>: у системных
    /// цветов красный и синий стоят наоборот, и перепутать их — значит нарисовать красное зелёным.
    /// </summary>
    internal static uint ToRgb(uint colorRef) =>
        ((colorRef & 0x0000FF) << 16) | (colorRef & 0x00FF00) | ((colorRef >> 16) & 0xFF);

    /// <summary>Обратный перевод — та же перестановка: она обратна самой себе.</summary>
    internal static uint ToColorRef(uint rgb) => ToRgb(rgb);

    private static uint TextOnLight(TrayTone tone) => tone switch
    {
        TrayTone.Good => GoodTextOnLight,
        TrayTone.Warning => WarningTextOnLight,
        TrayTone.Bad => BadTextOnLight,
        _ => NeutralTextOnLight,
    };

    private static uint TextOnDark(TrayTone tone) => tone switch
    {
        TrayTone.Good => GoodTextOnDark,
        TrayTone.Warning => WarningTextOnDark,
        TrayTone.Bad => BadTextOnDark,
        _ => NeutralTextOnDark,
    };
}
