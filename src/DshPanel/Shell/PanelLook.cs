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
    /// Кисть СКРУГЛЁННОЙ ПОДЛОЖКИ выбранной строки списка (раздел настроек выбирается списком
    /// слева, решение владельца 27.09.2026, `docs\DESIGN.md` п. 21).
    ///
    /// ⚠️ **Кисть именно ТЕМЫ, а не своя.** Подложка выбранного раздела — то же, чем карточка
    /// отличается от фона окна (`Border.card` берёт ровно эту кисть), и на светлой теме она почти
    /// белая, на тёмной — почти чёрная. Свой оттенок, подобранный на одной теме, на другой
    /// перестал бы отличаться от фона окна — то есть выбранная строка исчезла бы вовсе.
    ///
    /// Стиль объявлен в <c>App.axaml</c> ПОСЛЕ <c>&lt;FluentTheme /&gt;</c> — иначе подсветка
    /// каркаса перебивала бы его. Здесь — только ключ: одна точка правды для проверки,
    /// которая сверяет цвет подложки с кистью темы.
    /// </summary>
    public const string SectionSelectionKey = "SystemControlBackgroundChromeMediumLowBrush";

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
            Theme(0x14000000, 8, TrayPalette.MenuLight, todayRowAlpha: 0x22, nowCellAlpha: 0x55);
        dictionary.ThemeDictionaries[ThemeVariant.Dark] =
            Theme(0x66000000, 12, TrayPalette.MenuDark, todayRowAlpha: 0x33, nowCellAlpha: 0x77);

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

    /// <summary>Один набор ресурсов темы: тень и четыре кисти тонов.</summary>
    private static ResourceDictionary Theme(uint shadow, int blur, uint background, uint todayRowAlpha, uint nowCellAlpha)
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

        return dictionary;
    }
}
