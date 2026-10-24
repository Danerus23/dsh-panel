using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DshPanel.Tray;

namespace DshPanel.Shell;

/// <summary>
/// ВИД ПАНЕЛИ: тени карточек, цвета состояния и место, где они объявлены.
///
/// Зачем отдельный файл, а не цвета прямо в разметке. Решение владельца 27.09.2026 («очень
/// не хватает цветов, объёмности, теней», шаг 2): у панели должны появиться мягкие тени,
/// заметные заголовки, акцентный цвет главного действия и цветовые акценты состояния. Но
/// «своей жёсткой палитры не заводить»: оттенок, подобранный на светлой теме, не читается
/// на тёмной, и панель уже один раз на этом спотыкалась (<c>ButtonStateTests</c>). Поэтому:
///
/// * **акцент главного действия — из темы каркаса** (<c>AccentButtonBackground</c> и соседи,
///   <c>App.axaml</c>): он у Fluent есть в обеих темах и меняется вместе с ними;
/// * **тона состояния — из уже принятой палитры** (<see cref="TrayPalette"/>): зелёный,
///   янтарный, красный, серый — те же числа, что красят строки меню значка и его огонёк,
///   и те же, что измерены по контрасту (WCAG) на светлом и тёмном фоне;
/// * **тень — своя ПРОЗРАЧНОСТЬ чёрного** (владелец это разрешил прямо): сплошного цвета
///   у тени быть не может, а на тёмной теме она и не видна — там карточку держат подложка
///   и рамка из темы.
///
/// **Почему цвета объявляются кодом, а не в <c>App.axaml</c>.** Кисти тонов ЗАВИСЯТ ОТ ТЕМЫ
/// (на светлой — тёмная зелень, на тёмной — светлая), а разметка не умеет считать цвет
/// из чужой палитры. Здесь они собираются в словарь с двумя наборами
/// (<see cref="ResourceDictionary.ThemeDictionaries"/>) и живут как обычные ресурсы:
/// разметка берёт их через <c>DynamicResource</c> и сама переключается вместе с темой.
///
/// ⚠️ **Вызов <see cref="Install"/> обязателен.** Без него стили тонов не найдут кистей,
/// и состояние осталось бы без цвета — поэтому у проверок есть отдельное утверждение
/// «каждый ключ вида находится в ОБЕИХ темах», а не «стиль объявлен».
/// </summary>
public static class PanelLook
{
    /// <summary>Ресурс мягкой тени карточки. Нужен и разметке, и проверке — берётся отсюда.</summary>
    public const string CardShadowKey = "PanelCardShadow";

    /// <summary>
    /// ПОДЛОЖКА ВЫБРАННОЙ СТРОКИ СПИСКА (раздел настроек и записи истории цен выбираются списком
    /// слева). Прежняя кисть — каркасная <c>SystemControlBackgroundChromeMediumLowBrush</c> — была
    /// ТОЙ ЖЕ, что фон списка: кадр p8 (22.10.2026) дал контраст **1,00:1**, то есть выбранная
    /// строка не отличалась от соседних ничем, кроме полужирной подписи.
    ///
    /// **От какого случая (DEF-047, 2.4.0).** Норма `50-lists-navigation.md` §2 и WCAG 1.4.11:
    /// состояние органа обязано читаться на 3:1 к соседу. Числа подобраны по формуле WCAG и
    /// замерены (`SelectionAndTodayTests`):
    ///
    /// * светлая тема — подложка `#006ABD` к фону списка `#F2F2F2` = **4,94:1**; чернила `#FFFFFF`
    ///   на подложке = **5,53:1** (пол текста 4,5 — обычный кегль 13);
    /// * тёмная тема — подложка `#4CA0E3` к фону списка `#2B2B2B` = **5,02:1** (к `#202020` —
    ///   5,92:1); чернила `#0B1620` на подложке = **6,48:1**.
    ///
    /// ⚠️ **Почему не акцентная кисть каркаса.** `AccentButtonBackground` — `#0078D7` в обеих
    /// темах; белые чернила на ней дают **4,499:1**, то есть пол 4,5 НЕ проходят (банк прямо
    /// называет «4,499:1 — не проходит»). Оттенок остался акцентным по тону, но набран из
    /// проверенных чисел — тем же путём, что заголовок раздела (<see cref="SectionTitle"/>).
    /// </summary>
    public static Color SelectionFill(bool dark) => dark ? Rgb(0x4CA0E3) : Rgb(0x006ABD);

    /// <summary>
    /// ЧЕРНИЛА ВЫБРАННОЙ СТРОКИ — под ту же подложку: на тёмной подложке светлой темы белые,
    /// на светлой подложке тёмной — тёмные. Мерить их на глаз нельзя: это второй признак выбора,
    /// и он обязан читаться (числа — в <see cref="SelectionFill"/>).
    /// </summary>
    public static Color SelectionInk(bool dark) => dark ? Rgb(0x0B1620) : Rgb(0xFFFFFF);

    /// <summary>Кисть подложки выбранной строки — её берёт стиль <c>ListBoxItem:selected</c>.</summary>
    public const string SelectionFillKey = "PanelSelectionFill";

    /// <summary>Кисть чернил выбранной строки (подпись и значок).</summary>
    public const string SelectionInkKey = "PanelSelectionInk";

    /// <summary>
    /// Кисть ТОНКОГО РАЗДЕЛИТЕЛЯ строк таблицы окон пика (<c>Views\PeakTableView.cs</c>).
    ///
    /// Кисть темы, а не подобранный серый: разделитель обязан быть виден и на светлой подложке
    /// карточки, и на тёмной, а «#33808080», выбранный один раз, на одной из тем пропадает —
    /// на этом панель уже спотыкалась (рамка карточки была подобрана числом сразу в трёх файлах).
    /// </summary>
    public const string PeakRowSeparatorKey = "SystemControlForegroundBaseLowBrush";

    /// <summary>
    /// КИСТЬ СТРОКИ ПОД КУРСОРОМ — подсветка наведения у строки, по которой выбирают
    /// (таблица готовых копий, <c>Views\BackupTableView.cs</c>).
    ///
    /// Слова владельца 28.09.2026: *«Не совсем понятно даже, что это кликабельно»*. Наведение
    /// обязано быть видно ДО щелчка, а своего оттенка панель не заводит: кисть берётся у темы
    /// каркаса, как и всё остальное в ней. Ключ назван ЗДЕСЬ, а не написан в разметке строкой,
    /// ровно затем, чтобы проверка могла спросить его у ОБЕИХ тем: написанный в стиле ключ,
    /// которого тема не отдаёт, оставил бы строку без подсветки, и заметить это на одной теме
    /// нельзя.
    /// </summary>
    public const string RowHoverKey = "SystemControlHighlightListLowBrush";

    /// <summary>
    /// КИСТЬ ВЫБРАННОЙ СТРОКИ — плотнее наведения: выбранная строка читается и тогда, когда
    /// курсор уже ушёл (у выбора нет «наведения», по которому его видно).
    /// </summary>
    public const string RowSelectedKey = "SystemControlHighlightListMediumBrush";

    /// <summary>
    /// МЯГКАЯ ЗЕЛЁНАЯ ПОДЛОЖКА СТРОКИ СЕГОДНЯШНЕГО ДНЯ в таблице «График пиков» (замечание
    /// владельца 27.09.2026: «чтобы человек сразу видел, где он сейчас»).
    ///
    /// Цвет — тон «хорошо» из уже принятой палитры (<see cref="TrayPalette"/>), но с ПРОЗРАЧНОСТЬЮ:
    /// сплошной зелёный залил бы всю строку и спорил с текстом. Прозрачность у светлой и тёмной
    /// темы РАЗНАЯ (на тёмной слабая примесь не видна вовсе) — поэтому кисть объявлена в обеих
    /// темах, а не подобрана числом в разметке.
    /// </summary>
    public const string PeakTodayRowKey = "PanelPeakTodayRow";

    /// <summary>
    /// ПОДЛОЖКА ЯЧЕЙКИ ТЕКУЩЕГО ОКНА — та же зелёная, но ЗАМЕТНО ПЛОТНЕЕ строки: по ней человек
    /// и понимает, где он сейчас. Две подложки одного цвета, но разной плотности — это и есть
    /// «строка дня мягко, ячейка окна заметно».
    /// </summary>
    public const string PeakNowCellKey = "PanelPeakNowCell";

    /// <summary>Класс строки таблицы: сегодняшний день. По нему разметка берёт подложку.</summary>
    public const string PeakTodayRowClass = "peakToday";

    /// <summary>Класс ячейки: текущее окно. По нему разметка берёт подложку поплотнее.</summary>
    public const string PeakNowCellClass = "peakNow";


    /// <summary>Классы тонов в разметке: им соответствуют стили <c>Foreground</c>/<c>Background</c>.</summary>
    public const string GoodClass = "tone-good";
    public const string WarningClass = "tone-warning";
    public const string BadClass = "tone-bad";
    public const string NeutralClass = "tone-neutral";

    /// <summary>Все классы тонов — по ним <see cref="Tone"/> снимает прежний тон перед новым.</summary>
    public static readonly string[] ToneClasses = { GoodClass, WarningClass, BadClass, NeutralClass };

    /// <summary>
    /// КИСТИ ПЛОТНОЙ МЕТКИ СОСТОЯНИЯ (раздел «Сервер», решение владельца 14.10.2026): подложка
    /// и чернила метки — по кисти на тон. Объявлены ОТДЕЛЬНО от тонов текста (<see cref="ToneKey"/>)
    /// затем, что это разные краски одного смысла: у тона текста чернила подобраны под ФОН ОКНА,
    /// а у метки — под её собственную подложку.
    /// </summary>
    public static string TagKey(TrayTone tone) => tone switch
    {
        TrayTone.Good => "PanelTagGood",
        TrayTone.Warning => "PanelTagWarning",
        _ => "PanelTagBad",
    };

    /// <summary>Чернила метки: тот же тон, вторая кисть.</summary>
    public static string TagTextKey(TrayTone tone) => TagKey(tone) + "Text";

    /// <summary>Кисть СЕРОГО СОСТОЯНИЯ (и подписи под списком): просто текст, без плашки.</summary>
    public const string GreyTextKey = "PanelGreyText";

    /// <summary>
    /// Кисть ЦВЕТА ЗАГОЛОВКА РАЗДЕЛА — по теме (<see cref="SectionTitle"/>). Заголовок берёт её
    /// ПО ИМЕНИ, а не литералом в разметке: значение у вида одно, и проверка контраста меряет его же.
    /// </summary>
    public const string SectionTitleKey = "PanelSectionTitle";

    /// <summary>
    /// КОЛЬЦО ФОКУСА СТРОКИ ТАБЛИЦЫ (таблица копий) — видимый фокус с клавиатуры.
    ///
    /// **От какого случая (пачка долгов 14.10.2026, §2).** Строку копии можно было выбрать только
    /// мышью (`PointerPressed` на <c>Border</c>): с клавиатуры — никак, и видимого фокуса не было
    /// вовсе. Норма банка (`50-lists-navigation.md` §2/§8): **WCAG 2.2 SC 2.1.1 (Keyboard)** и
    /// **SC 2.4.7 (Focus Visible)**; провал F59 — «сделать из `div` орган управления, не дав роли».
    ///
    /// ⚠️ **Кольцо — тенью ВНУТРЬ, а не толщиной рамки.** Толщина рамки участвует в раскладке,
    /// и фокус менял бы РАЗМЕР строки — а это запрещено правилом, выросшим из «дрожания кнопок»
    /// (`PanelRhythmTests.В_разметке_состояние_не_меняет_размер`). Тень раскладку не двигает.
    ///
    /// ⚠️ Цвет — **акцент темы** (`SystemAccentColor`, тот же, чем крашено главное действие):
    /// подбирать оттенок числом нельзя, на другой теме он перестал бы отличаться от подложки.
    /// </summary>
    public const string RowFocusRingKey = "PanelRowFocusRing";

    /// <summary>Толщина кольца фокуса — 2: столько же у фокуса страницы телефона и у остальных.</summary>
    public const double FocusRingThickness = 2;

    /// <summary>
    /// ЦВЕТА МЕТКИ СОСТОЯНИЯ — ОДНО МЕСТО НА ВИД И НА ПРОВЕРКУ КОНТРАСТА.
    ///
    /// **Откуда числа.** Из принятого владельцем рендера (`..\_mockups\server-v2c-*.png`) и его
    /// замера контраста: подложка тёмная, чернила светлые на светлой теме — и наоборот на тёмной,
    /// потому что светлая зелень на белом фоне не читается. Замер владельца 14.10.2026: текст на
    /// метке 6,88 / 5,62 / 5,32 (светлая) и 7,21 / 7,42 / 8,78 (тёмная), метка против карточки
    /// 5,32…7,68 — всё выше 4,5:1 (WCAG 1.4.3) и 3:1 (1.4.11).
    ///
    /// ⚠️ **Функция, а не два словаря ресурсов.** Проверка обязана мерить ТЕ ЖЕ числа, какими
    /// красит вид: подставь она свои — она мерила бы не то, что видит человек. Поэтому цвета
    /// считаются ЗДЕСЬ, а <see cref="Install"/> кладёт из них кисти.
    ///
    /// ⚠️ **Серой метки не бывает** (решение владельца: серое состояние — просто серый текст,
    /// образец GOV.UK «grey text with no background colour»), поэтому <see cref="TrayTone.Neutral"/>
    /// здесь — ошибка вызова, а не бледная плашка.
    /// </summary>
    public static (Color Background, Color Text) Tag(TrayTone tone, bool dark) => tone switch
    {
        TrayTone.Good => dark ? (Rgb(0x57C07E), Rgb(0x0E2416)) : (Rgb(0x14682F), Rgb(0xFFFFFF)),
        TrayTone.Warning => dark ? (Rgb(0xE8B84B), Rgb(0x2A1F05)) : (Rgb(0x8A6508), Rgb(0xFFFFFF)),
        TrayTone.Bad => dark ? (Rgb(0xF08A8A), Rgb(0x2A0F0F)) : (Rgb(0xC62828), Rgb(0xFFFFFF)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(tone), "серое состояние показывается серым текстом, а не меткой с плашкой"),
    };

    /// <summary>Цвет серого состояния и подписи под списком — по теме.</summary>
    public static Color GreyText(bool dark) => dark ? Rgb(0xB0B0B0) : Rgb(0x666666);

    /// <summary>
    /// ЦВЕТ ЗАГОЛОВКА РАЗДЕЛА — из палитры вида, по теме (находка E-01 приёмки C1, 17.10.2026).
    ///
    /// **От какого случая.** Заголовки набирались АКЦЕНТНОЙ кистью каркаса КАК ЦВЕТОМ ТЕКСТА:
    /// `#0078D7` на карточке `#F2F2F2` давал **4,02:1**, на тёмной карточке `#2B2B2B` — **3,15:1**
    /// при поле 4,5:1 (WCAG 1.4.3: 20 точек SemiBold — обычный текст, а не крупный).
    ///
    /// **Откуда числа.** Тот же акцент, сдвинутый по яркости и замеренный: на светлой теме
    /// затемнён до `#006ABD` (**4,94:1** на карточке `#F2F2F2`), на тёмной осветлён до `#4CA0E3`
    /// (**5,02:1** на `#2B2B2B`). Оттенок остался акцентным, а порог взят С ЗАПАСОМ, а не «ровно
    /// 4,5»: округление канала не должно переворачивать вердикт.
    ///
    /// ⚠️ **Функция, а не литерал в разметке.** Стиль `TextBlock.sectionTitle` берёт кисть по имени
    /// (<see cref="SectionTitleKey"/>), а проверка контраста берёт ТЕ ЖЕ числа отсюда — подставь
    /// она свои, она мерила бы не то, что видит человек.
    /// </summary>
    public static Color SectionTitle(bool dark) => dark ? Rgb(0x4CA0E3) : Rgb(0x006ABD);

    /// <summary>Цвет по числу <c>RRGGBB</c> — им записаны принятые владельцем цвета.</summary>
    private static Color Rgb(uint rgb) => Color.FromUInt32(0xFF000000u | rgb);

    /// <summary>
    /// АКЦЕНТ ТЕМЫ для кольца фокуса — <c>SystemAccentColor</c> каркаса: тот же акцент, которым
    /// крашено главное действие окна. Берётся у ТЕМЫ, а не подбирается числом (подобранный оттенок
    /// на другой теме перестал бы отличаться от подложки).
    ///
    /// ⚠️ Запасной цвет нужен на случай, когда каркас акцента не отдал (Fluent отдаёт в обеих
    /// темах): тогда кольцо всё равно обязано быть видно — это фокус, а не украшение.
    /// </summary>
    private static Color Accent(Application application, ThemeVariant variant) =>
        application.TryFindResource("SystemAccentColor", variant, out var value) && value is Color colour
            ? colour
            : Rgb(0x0F6CBD);

    /// <summary>
    /// Объявить ресурсы вида: тень карточки и кисти тонов. Зовётся ОДИН раз при инициализации
    /// приложения (<c>App.Initialize</c>) — то есть и в живом запуске, и в проверках без экрана:
    /// окно, собранное в проверке, обязано видеть тот же вид, что окно у человека.
    /// </summary>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        var dictionary = new ResourceDictionary();

        // Тень мягкая: смещение вниз на 2, размытие 8–12 и НИЧЕГО в разбросе — резкая граница
        // читалась бы как вторая рамка, а не как объём. На тёмной теме тень сильнее: на чёрном
        // фоне слабая прозрачность не видна вовсе.
        //
        // Подложки таблицы графика идут тем же доводом: у СВЕТЛОЙ темы они бледнее (там зелёный
        // и без примеси читается), у ТЁМНОЙ — плотнее. Числа — прозрачность тона «хорошо»,
        // а не свой оттенок: цвет остаётся одним на всю панель.
        dictionary.ThemeDictionaries[ThemeVariant.Light] =
            Theme(0x14000000, 8, TrayPalette.MenuLight, todayRowAlpha: 0x22, nowCellAlpha: 0x55,
                dark: false, accent: Accent(application, ThemeVariant.Light));
        dictionary.ThemeDictionaries[ThemeVariant.Dark] =
            Theme(0x66000000, 12, TrayPalette.MenuDark, todayRowAlpha: 0x33, nowCellAlpha: 0x77,
                dark: true, accent: Accent(application, ThemeVariant.Dark));

        application.Resources.MergedDictionaries.Add(dictionary);
    }

    /// <summary>
    /// Ресурс тона для стиля. Имя по тону, а не по цвету: цвет меняется вместе с темой,
    /// а «зелёный» остаётся зелёным.
    /// </summary>
    public static string ToneKey(TrayTone tone) => tone switch
    {
        TrayTone.Good => "PanelToneGood",
        TrayTone.Warning => "PanelToneWarning",
        TrayTone.Bad => "PanelToneBad",
        _ => "PanelToneNeutral",
    };

    /// <summary>Класс тона для органа: по нему стиль и покрасит текст или кружок.</summary>
    public static string ToneClass(TrayTone tone) => tone switch
    {
        TrayTone.Good => GoodClass,
        TrayTone.Warning => WarningClass,
        TrayTone.Bad => BadClass,
        _ => NeutralClass,
    };

    /// <summary>
    /// Покрасить орган тоном состояния. ЦВЕТ здесь не ставится — ставится класс, а цвет берёт
    /// стиль из ресурса темы. Так одно и то же состояние выглядит правильно и в светлой теме,
    /// и в тёмной, а «подобрать оттенок на глаз» в окне больше негде.
    ///
    /// Прежний тон снимается: орган, сменивший состояние, обязан сменить цвет, а не остаться
    /// с прошлым (два класса тонов на одном органе дали бы случайный из двух).
    /// </summary>
    public static void Tone(Control control, TrayTone tone)
    {
        ArgumentNullException.ThrowIfNull(control);

        foreach (var name in ToneClasses) control.Classes.Remove(name);

        control.Classes.Add(ToneClass(tone));
    }

    /// <summary>Один набор ресурсов темы: тень, кисти тонов, метки состояний, серый текст и кольцо фокуса.</summary>
    private static ResourceDictionary Theme(
        uint shadow, int blur, uint background, uint todayRowAlpha, uint nowCellAlpha, bool dark, Color accent)
    {
        var dictionary = new ResourceDictionary
        {
            [CardShadowKey] = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 2,
                Blur = blur,
                Spread = 0,
                Color = Color.FromUInt32(shadow),
            }),
        };

        foreach (var tone in new[] { TrayTone.Good, TrayTone.Warning, TrayTone.Bad, TrayTone.Neutral })
        {
            // Чернила тона берутся у палитры значка на ФОНЕ своей темы: у неё уже выбраны
            // и проверены по контрасту обе краски — тёмная для светлого фона, светлая для тёмного.
            dictionary[ToneKey(tone)] = new SolidColorBrush(
                Color.FromUInt32(0xFF000000u | TrayPalette.Text(tone, background)));
        }

        // Подложки таблицы графика — ТОТ ЖЕ зелёный тон, но с прозрачностью: цвет берётся
        // у палитры (там он измерен по контрасту), а прозрачность задаёт тему.
        var good = TrayPalette.Circle(TrayTone.Good);

        dictionary[PeakTodayRowKey] = new SolidColorBrush(Color.FromUInt32((todayRowAlpha << 24) | good));
        dictionary[PeakNowCellKey] = new SolidColorBrush(Color.FromUInt32((nowCellAlpha << 24) | good));

        // МЕТКИ СОСТОЯНИЙ И СЕРЫЙ ТЕКСТ — из той же таблицы цветов, какой мерит проверка контраста
        // (<see cref="Tag"/> и <see cref="GreyText"/>): вид и замер разойтись не могут.
        foreach (var tone in new[] { TrayTone.Good, TrayTone.Warning, TrayTone.Bad })
        {
            var (plate, ink) = Tag(tone, dark);

            dictionary[TagKey(tone)] = new SolidColorBrush(plate);
            dictionary[TagTextKey(tone)] = new SolidColorBrush(ink);
        }

        dictionary[GreyTextKey] = new SolidColorBrush(GreyText(dark));

        // ЦВЕТ ЗАГОЛОВКА РАЗДЕЛА — из той же таблицы, какой мерит проверка контраста (E-01):
        // акцентная кисть каркаса как цвет текста не проходила пол 4,5:1 (замер 4,02 / 3,15).
        dictionary[SectionTitleKey] = new SolidColorBrush(SectionTitle(dark));

        // ПОДЛОЖКА И ЧЕРНИЛА ВЫБРАННОЙ СТРОКИ — из той же таблицы, что мерит проверка (DEF-047):
        // вид и замер разойтись не могут. Числа и от какого случая — у SelectionFill/SelectionInk.
        dictionary[SelectionFillKey] = new SolidColorBrush(SelectionFill(dark));
        dictionary[SelectionInkKey] = new SolidColorBrush(SelectionInk(dark));

        // КОЛЬЦО ФОКУСА СТРОКИ — ВНУТРЕННЯЯ тень толщиной 2: она видна поверх подложки строки
        // и НЕ двигает раскладку (толщина рамки сдвинула бы размер строки — а это запрещено).
        dictionary[RowFocusRingKey] = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 0,
            Blur = 0,
            Spread = FocusRingThickness,
            IsInset = true,
            Color = accent,
        });

        return dictionary;
    }
}
