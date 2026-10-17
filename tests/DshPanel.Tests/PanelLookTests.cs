using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ШАГ 2 ВИДА ПАНЕЛИ: тени карточек, скругления, заметные заголовки, акцент главного действия
/// и цвета состояния.
///
/// Слова владельца 27.09.2026: *«очень не хватает цветов, объёмности, теней»*. Обещаний четыре,
/// и каждое проверяется там, где его видно:
///
/// 1. **цвета есть, и они ЧИТАЮТСЯ** — перебор тонов в СВЕТЛОЙ и ТЁМНОЙ теме с замером
///    контраста (WCAG) против подложки карточки: подобранный на глаз оттенок сломал бы одну
///    из тем, и заметить это на одной теме нельзя;
/// 2. **цвет берётся у темы, а не подобран в окне** — кисти тонов РАЗНЫЕ в двух темах (на светлой
///    тёмная зелень, на тёмной светлая), и это видно прогоном, а не обещанием;
/// 3. **карточки получили объём** — тень и скругление у настоящих карточек показанного окна,
///    а не «стиль объявлен»;
/// 4. **главное действие отличается от прочих кнопок** — и свойством, и КАДРОМ: акцент можно
///    выставить и не увидеть ничего.
///
/// ⚠️ Чего здесь нет намеренно (это «шаг 3», владелец его не выбирал): своего заголовка окна
/// вместо системного, анимаций и полной замены системного вида.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PanelLookTests
{
    /// <summary>Ресурсы тонов — по одному на каждое решение <see cref="TrayTone"/>.</summary>
    private static readonly TrayTone[] Tones =
    {
        TrayTone.Good, TrayTone.Warning, TrayTone.Bad, TrayTone.Neutral,
    };

    /// <summary>Ключ ресурса подложки карточки: у неё считается контраст цвета состояния.</summary>
    private const string CardBackgroundKey = "SystemControlBackgroundChromeMediumLowBrush";

    /// <summary>
    /// СКОЛЬКО ЗАГОЛОВКОВ РАЗДЕЛОВ ВИДНО В ОКНЕ НАСТРОЕК — ЧИСЛО ПОСЧИТАНО, А НЕ ВЗЯТО «НА ГЛАЗ».
    ///
    /// Находка П-1 промежуточной приёмки R1–R7 (17.10.2026): обе проверки заголовков требовали
    /// «не меньше четырёх», то есть **не падали**, если заголовков станет три, — тот же класс
    /// «проверка не может упасть», что и остальные находки серии.
    ///
    /// Замер: временная проба показала **7** `TextBlock` с классом `sectionTitle` в ПОКАЗАННОМ окне
    /// (`SettingsStand` + `Show()` + `Settle()`, 18.10.2026): «Общее» · «Резервное копирование» ·
    /// «Баланс и тариф» · «Сервер» · «Обновление» · «Телефон» · «В этом запуске телефоном не
    /// управляют…» — седьмой не название раздела, а строка с тем же классом; она входит в счёт, и
    /// это записано, чтобы число не выглядело ошибкой. Меняется окно — проверка падает и НАЗЫВАЕТ
    /// найденные тексты. Отчёт: `_team\c4\r22-kadry.md`.
    /// </summary>
    private const int SectionTitleCount = 7;

    private static Application App => Application.Current!;

    /// <summary>Кисть тона в этой теме или падение: «нет кисти» — это и есть дефект.</summary>
    private static IBrush Brush(string key, ThemeVariant variant)
    {
        Assert.True(
            App.TryFindResource(key, variant, out var value) && value is IBrush brush,
            $"тема {variant} не отдала кисть «{key}» — состояние осталось бы без цвета");

        return (IBrush)value!;
    }

    /// <summary>
    /// КИСТИ ТОНОВ ЕСТЬ В ОБЕИХ ТЕМАХ, и КАЖДАЯ ЧИТАЕТСЯ на подложке карточки своей темы.
    ///
    /// Числа берутся не «на глаз»: контраст считается по WCAG тем же способом, что у палитры
    /// значка, и порог — её же (<see cref="TrayPalette.TextContrastFloor"/>). Один и тот же
    /// зелёный на белом и на чёрном читается по-разному, поэтому у тона ДВА варианта —
    /// по одному на тему, и оба обязаны пройти порог.
    /// </summary>
    [AvaloniaFact]
    public void Кисти_тонов_есть_в_обеих_темах_и_читаются_на_карточке()
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var background = (ISolidColorBrush)Brush(CardBackgroundKey, variant);

            foreach (var tone in Tones)
            {
                var key = PanelLook.ToneKey(tone);
                var brush = (ISolidColorBrush)Brush(key, variant);

                var contrast = TrayPalette.Contrast(ToRgb(brush.Color), ToRgb(background.Color));

                Assert.True(
                    contrast >= TrayPalette.TextContrastFloor,
                    $"{variant}/{key}: контраст {contrast:0.00} ниже порога " +
                    $"{TrayPalette.TextContrastFloor:0.0} — цвет состояния не читается на карточке");
            }
        }

        // И тень карточки объявлена в обеих темах: у неё нет «цвета», но есть прозрачность,
        // и на тёмной теме она обязана быть сильнее — на чёрном фоне слабая не видна вовсе.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(
                App.TryFindResource(PanelLook.CardShadowKey, variant, out var value) && value is BoxShadows,
                $"тема {variant} не отдала тень карточки «{PanelLook.CardShadowKey}»");
        }

        var lightShadow = Shadow(ThemeVariant.Light);
        var darkShadow = Shadow(ThemeVariant.Dark);

        Assert.True(lightShadow.Count > 0, "у тени карточки нет ни одной тени");
        Assert.NotEqual(lightShadow[0].Color.A, darkShadow[0].Color.A);
    }

    /// <summary>Тень карточки в этой теме или падение.</summary>
    private static BoxShadows Shadow(ThemeVariant variant)
    {
        Assert.True(
            App.TryFindResource(PanelLook.CardShadowKey, variant, out var value) && value is BoxShadows,
            $"тема {variant} не отдала тень карточки «{PanelLook.CardShadowKey}»");

        return (BoxShadows)value!;
    }

    /// <summary>
    /// ПЛОТНАЯ МЕТКА СОСТОЯНИЯ ЧИТАЕТСЯ В ОБЕИХ ТЕМАХ — и слово на ней, и она сама на карточке.
    ///
    /// **От какого случая (решение владельца 14.10.2026).** На разделе «Сервер» состояния стояли
    /// обычным текстом и состоянием не читались; принят рендер с плотной меткой (GOV.UK Tag).
    /// У метки ДВА числа, и оба обязаны пройти порог: **слово на подложке метки** (WCAG 2.2
    /// SC 1.4.3, 4,5:1) и **подложка метки на карточке** (SC 1.4.11, 3:1) — иначе метка сливается
    /// с карточкой и перестаёт читаться как метка.
    ///
    /// ⚠️ Числа берутся у <see cref="PanelLook.Tag"/> — У ТОЙ ЖЕ двери, которой красит вид:
    /// проверка мерит ровно то, что видит человек. Замер владельца на принятом рендере: слово
    /// на метке 6,88 / 5,62 / 5,32 (светлая) и 7,21 / 7,42 / 8,78 (тёмная), метка на карточке
    /// 5,32…7,68.
    ///
    /// Мутация, которая обязана упасть здесь: **взять цвет метки из палитры значка, не пересчитав
    /// его** (у тонов значка чернила подобраны под ФОН ОКНА, а не под подложку метки).
    /// </summary>
    [AvaloniaFact]
    public void Метки_состояний_читаются_в_обеих_темах()
    {
        var tones = new[] { TrayTone.Good, TrayTone.Warning, TrayTone.Bad };

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var dark = variant == ThemeVariant.Dark;
            var background = (ISolidColorBrush)Brush(CardBackgroundKey, variant);

            foreach (var tone in tones)
            {
                var key = PanelLook.TagKey(tone);
                var plate = (ISolidColorBrush)Brush(key, variant);
                var ink = (ISolidColorBrush)Brush(PanelLook.TagTextKey(tone), variant);

                // 1) СЛОВО НА МЕТКЕ. Слово внутри метки — это ещё и WCAG 1.4.1 (цвет не один),
                //    но читаемость слова — отдельное обещание, и оно мерится числом.
                var onTag = TrayPalette.Contrast(ToRgb(ink.Color), ToRgb(plate.Color));

                Assert.True(
                    onTag >= TrayPalette.TextContrastFloor,
                    $"{variant}/{key}: слово на метке читается на {onTag:0.00} — ниже порога " +
                    $"{TrayPalette.TextContrastFloor:0.0}: состояние не прочитать");

                // 2) МЕТКА НА КАРТОЧКЕ: подложка метки обязана отличаться от карточки заметно —
                //    иначе метка не читается как метка (SC 1.4.11, порог 3:1).
                var onCard = TrayPalette.Contrast(ToRgb(plate.Color), ToRgb(background.Color));

                Assert.True(
                    onCard >= NonTextContrastFloor,
                    $"{variant}/{key}: метка на карточке читается на {onCard:0.00} — ниже порога " +
                    $"{NonTextContrastFloor:0.0}: метка сливается с карточкой");
            }

            // 3) И ЦВЕТА ЖИВУТ В ТАБЛИЦЕ, А НЕ В РАЗМЕТКЕ: та же дверь отдаёт их проверке и виду.
            var (lightPlate, lightInk) = PanelLook.Tag(TrayTone.Good, dark: false);
            var (darkPlate, darkInk) = PanelLook.Tag(TrayTone.Good, dark: true);

            Assert.NotEqual(lightPlate, darkPlate);
            Assert.NotEqual(lightInk, darkInk);

            // И серое состояние — СЕРЫМ ТЕКСТОМ, без плашки: у него нет подложки вовсе,
            // а читается оно на карточке не хуже прочего текста.
            var grey = PanelLook.GreyText(dark);
            var greyOnCard = TrayPalette.Contrast(ToRgb(grey), ToRgb(background.Color));

            Assert.True(
                greyOnCard >= TrayPalette.TextContrastFloor,
                $"{variant}: серое состояние читается на {greyOnCard:0.00} — ниже порога " +
                $"{TrayPalette.TextContrastFloor:0.0}");
        }
    }

    /// <summary>Порог WCAG 2.2 SC 1.4.11 для нетекстовых элементов: 3:1.</summary>
    private const double NonTextContrastFloor = 3.0;

    /// <summary>
    /// КИСТИ ТОНОВ РАЗНЫЕ В ДВУХ ТЕМАХ. Это и есть доказательство, что цвет приходит ОТ ТЕМЫ,
    /// а не подобран один раз в окне: у палитры значка на каждый тон две краски — тёмная для
    /// светлого фона и светлая для тёмного.
    /// </summary>
    [AvaloniaFact]
    public void Кисти_тонов_разные_в_светлой_и_тёмной_теме()
    {
        foreach (var tone in Tones)
        {
            var key = PanelLook.ToneKey(tone);

            var light = (ISolidColorBrush)Brush(key, ThemeVariant.Light);
            var dark = (ISolidColorBrush)Brush(key, ThemeVariant.Dark);

            Assert.NotEqual(light.Color, dark.Color);
        }
    }

    /// <summary>
    /// КАРТОЧКИ ГЛАВНОГО ОКНА ПОЛУЧИЛИ ОБЪЁМ: у настоящей карточки показанного окна есть тень,
    /// скругление, своя подложка и рамка — и всё это из темы.
    ///
    /// Мерятся НАСТОЯЩИЕ органы окна, а не объявление стиля: стиль можно написать и не применить.
    /// </summary>
    [AvaloniaFact]
    public void Карточки_главного_окна_тенисты_и_скруглены()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        var cards = window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("card"))
            .ToList();

        Assert.True(cards.Count >= 2, $"карточек с классом card найдено {cards.Count} — ожидались две");

        foreach (var card in cards)
        {
            Assert.True(card.BoxShadow.Count > 0, $"у карточки {card.Name} нет тени");
            Assert.True(card.CornerRadius.TopLeft > 0, $"у карточки {card.Name} нет скругления");
            Assert.NotNull(card.Background);
            Assert.NotNull(card.BorderBrush);
        }

        window.Close();
    }

    /// <summary>
    /// ГЛАВНОЕ ДЕЙСТВИЕ ОКНА ОКРАШЕНО АКЦЕНТОМ ТЕМЫ, и этот акцент — не тот же фон, что у соседней
    /// кнопки: «Запустить» обязана отличаться от «Остановить» и «Отвязаться».
    /// </summary>
    [AvaloniaFact]
    public void Главное_действие_окрашено_акцентом_темы()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        var start = window.FindControl<Button>("StartServerButton");
        var stop = window.FindControl<Button>("StopServerButton");

        Assert.NotNull(start);
        Assert.NotNull(stop);

        Assert.Contains("accent", start!.Classes);
        Assert.DoesNotContain("accent", stop!.Classes);

        var accent = (ISolidColorBrush)Brush("AccentButtonBackground", window.ActualThemeVariant);
        var startPresenter = Presenter(start);
        var stopPresenter = Presenter(stop);

        Assert.Equal(accent.Color, ((ISolidColorBrush)startPresenter.Background!).Color);
        Assert.NotEqual(accent.Color, ((ISolidColorBrush)stopPresenter.Background!).Color);

        window.Close();
    }

    /// <summary>
    /// АКЦЕНТ ВИДЕН КАДРОМ, а не только свойством: у окна с акцентной кнопкой кадр отличается
    /// от того же окна без класса. Свойство можно выставить и не увидеть ничего — это уже
    /// случалось в проекте.
    /// </summary>
    [AvaloniaFact]
    public void Акцентная_кнопка_отличается_кадром_от_обычной()
    {
        var window = new Window { Width = 240, Height = 100 };
        var button = new Button { Content = "Проба", Width = 160 };
        window.Content = button;

        window.Show();
        PanelTestStand.Settle();

        var plain = PanelTestStand.FrameHash(window);

        button.Classes.Add("accent");
        PanelTestStand.Settle();

        var accented = PanelTestStand.FrameHash(window);

        Assert.NotEqual(plain, accented);

        button.Classes.Remove("accent");
        PanelTestStand.Settle();

        // Контроль: снятый класс возвращает прежний вид — значит кадр менял именно акцент,
        // а не что-то ещё, что менялось само по себе.
        Assert.Equal(plain, PanelTestStand.FrameHash(window));

        window.Close();
    }

    /// <summary>
    /// ТОН СОСТОЯНИЯ СЕРВЕРА МЕНЯЕТСЯ ВМЕСТЕ С СОСТОЯНИЕМ — и у строки, и у кружка, и это те же
    /// тона, что у строки сервера в меню значка (<see cref="TrayStatus.ServerTone"/>).
    /// </summary>
    [AvaloniaFact]
    public void Тон_состояния_меняется_вместе_с_состоянием_сервера()
    {
        var cases = new (string Where, MainWindow Window, string Class)[]
        {
            (
                "сервер не запущен",
                PanelTestStand.MainStand(ServerState.Stopped(0)),
                PanelLook.BadClass),
            (
                "сервер работает",
                PanelTestStand.MainStand(
                    new ServerState(ServerPresence.Running, 3080, 4242, "node", "отвечает"), ServerOwner.Panel),
                PanelLook.GoodClass),
            (
                "панель сервера не знает",
                new MainWindow(),
                PanelLook.NeutralClass),
        };

        foreach (var (where, window, expected) in cases)
        {
            window.Show();
            PanelTestStand.Settle();

            var status = window.FindControl<TextBlock>("ServerStatusText");
            var dot = window.FindControl<Border>("ServerDot");

            Assert.NotNull(status);
            Assert.NotNull(dot);

            Assert.True(
                status!.Classes.Contains(expected),
                $"{where}: у строки состояния нет тона «{expected}» — она не покрашена или покрашена другим");

            Assert.True(dot!.Classes.Contains(expected), $"{where}: кружок состояния не покрашен тоном");

            Assert.NotNull(status.Foreground);
            Assert.NotNull(dot.Background);

            window.Close();
        }
    }

    /// <summary>
    /// ЗАНЯТОСТЬ — ЯНТАРНЫЙ ТОН, а не «зелёный»: пока сервер не ответил, красить состояние
    /// в «работает» значило бы соврать цветом. Тот же довод, по которому в этой ветке не дёргается
    /// огонёк значка.
    /// </summary>
    [AvaloniaFact]
    public void Пока_панель_занята_состояние_янтарное()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        window.BeginServerStart(PanelStrings.ServerStarting);
        PanelTestStand.Settle();

        var status = window.FindControl<TextBlock>("ServerStatusText");
        Assert.NotNull(status);

        Assert.Contains(PanelLook.WarningClass, status!.Classes);
        Assert.DoesNotContain(PanelLook.BadClass, status.Classes);
        Assert.DoesNotContain(PanelLook.GoodClass, status.Classes);

        window.EndServerStart();
        window.Close();
    }

    /// <summary>
    /// ТОН ПЕРЕКЛЮЧАЕТСЯ, А НЕ НАКАПЛИВАЕТСЯ: орган, сменивший состояние, обязан сменить цвет,
    /// а не остаться с прошлым. Два класса тонов на одном органе дали бы случайный из двух —
    /// и это ровно тот дефект, который глазами не поймать.
    /// </summary>
    [AvaloniaFact]
    public void Тон_переключается_а_не_накапливается()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        var status = window.FindControl<TextBlock>("ServerStatusText");
        Assert.NotNull(status);

        PanelLook.Tone(status!, TrayTone.Good);
        PanelLook.Tone(status!, TrayTone.Warning);
        PanelLook.Tone(status!, TrayTone.Neutral);

        var tones = status!.Classes.Where(name => name.StartsWith("tone-", StringComparison.Ordinal)).ToList();

        Assert.Single(tones);
        Assert.Equal(PanelLook.NeutralClass, tones[0]);

        window.Close();
    }

    /// <summary>
    /// ЗАГОЛОВКИ РАЗДЕЛОВ ЗАМЕТНЕЕ ПОДСКАЗОК: крупнее, полужирные и цветом из ПАЛИТРЫ ВИДА
    /// (не акцентом каркаса — см. <see cref="Заголовки_разделов_проходят_пол_контраста"/> и E-01).
    /// Мерятся настоящие заголовки показанного окна настроек.
    /// </summary>
    [AvaloniaFact]
    public void Заголовки_разделов_заметнее_подсказок()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            PanelTestStand.Settle();

            var ink = (ISolidColorBrush)Brush(PanelLook.SectionTitleKey, window.ActualThemeVariant);

            var titles = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.Classes.Contains("sectionTitle"))
                .ToList();

            Assert.True(
                titles.Count == SectionTitleCount,
                $"заголовков с классом sectionTitle найдено {titles.Count} — ожидалось ровно {SectionTitleCount}: "
                + string.Join(" | ", titles.Select(title => title.Text)));

            var hints = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.Classes.Contains("hint"))
                .ToList();

            Assert.NotEmpty(hints);

            var hintSize = hints[0].FontSize;

            foreach (var title in titles)
            {
                Assert.True(
                    title.FontSize > hintSize,
                    $"заголовок {title.Name} не крупнее подсказки ({title.FontSize:0.#} против {hintSize:0.#})");

                Assert.Equal(FontWeight.SemiBold, title.FontWeight);
                Assert.Equal(ink.Color, ((ISolidColorBrush)title.Foreground!).Color);
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ЗАГОЛОВКИ РАЗДЕЛОВ ПРОХОДЯТ ПОЛ КОНТРАСТА 4,5:1 — И ЭТО ПРОВЕРЯЕТСЯ, А НЕ ОБЕЩАЕТСЯ.
    ///
    /// **От какого случая (находка E-01 приёмки C1, 17.10.2026).** Заголовки набирались акцентной
    /// кистью каркаса КАК ЦВЕТОМ ТЕКСТА: `#0078D7` давал **4,02:1** на светлой карточке `#F2F2F2`
    /// и **3,15:1** на тёмной `#2B2B2B` при поле 4,5:1 (WCAG 1.4.3: 20 точек SemiBold — обычный
    /// текст, а не крупный, у него пол тоже 4,5). Прежняя проверка
    /// (<see cref="Заголовки_разделов_заметнее_подсказок"/>) закрепляла РАВЕНСТВО акценту и контраст
    /// не мерила — то есть зелёная проверка держала дефект.
    ///
    /// **Чем меряется.** Тем же счётом, что у палитры значка (<see cref="TrayPalette.Contrast"/>,
    /// порог <see cref="TrayPalette.TextContrastFloor"/>): своих формул проверка не заводит. Цвет
    /// берётся у ПАЛИТРЫ (<see cref="PanelLook.SectionTitleKey"/>), а не литералом, и он же
    /// сверяется с тем, чем НАРИСОВАНЫ заголовки показанного окна, — поэтому проверка видит и
    /// подмену цвета в палитре, и возврат стиля к акценту.
    ///
    /// Мутация, которая обязана упасть здесь: в `App.axaml` у стиля `TextBlock.sectionTitle` вернуть
    /// <c>Foreground</c> на `{DynamicResource AccentButtonBackground}` (так было до правки E-01).
    /// </summary>
    [AvaloniaFact]
    public void Заголовки_разделов_проходят_пол_контраста()
    {
        // (1) Палитра обеих тем: цвет заголовка против подложки карточки.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var background = (ISolidColorBrush)Brush(CardBackgroundKey, variant);
            var ink = (ISolidColorBrush)Brush(PanelLook.SectionTitleKey, variant);

            var contrast = TrayPalette.Contrast(ToRgb(ink.Color), ToRgb(background.Color));

            Assert.True(
                contrast >= TrayPalette.TextContrastFloor,
                $"{variant}: цвет заголовка раздела ({PanelLook.SectionTitleKey}) даёт контраст " +
                $"{contrast:0.00} против карточки — ниже порога {TrayPalette.TextContrastFloor:0.0}");
        }

        // (2) Настоящее окно: те же чернила и та же подложка, что видит человек.
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            PanelTestStand.Settle();

            var variant = window.ActualThemeVariant;
            var palette = (ISolidColorBrush)Brush(PanelLook.SectionTitleKey, variant);

            var titles = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.Classes.Contains("sectionTitle"))
                .ToList();

            Assert.True(
                titles.Count == SectionTitleCount,
                $"заголовков с классом sectionTitle найдено {titles.Count} — ожидалось ровно {SectionTitleCount}: "
                + string.Join(" | ", titles.Select(title => title.Text)));

            foreach (var title in titles)
            {
                var ink = ((ISolidColorBrush)title.Foreground!).Color;
                Assert.Equal(palette.Color, ink);

                var backdrop = NearestBackdrop(title, window);
                Assert.True(
                    backdrop is not null,
                    $"заголовок «{title.Text}»: подложка не найдена — контраст мерить не на чем");

                var contrast = TrayPalette.Contrast(ToRgb(ink), ToRgb(backdrop!.Value));

                Assert.True(
                    contrast >= TrayPalette.TextContrastFloor,
                    $"заголовок «{title.Text}»: контраст {contrast:0.00} к подложке #{ToRgb(backdrop.Value):X6} — " +
                    $"ниже порога {TrayPalette.TextContrastFloor:0.0}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Ближайшая НЕПРОЗРАЧНАЯ подложка предка — на ней заголовок и нарисован. Прозрачные
    /// пропускаются: контраст считается против того, что человек видит, а не против слоя с альфой.
    /// </summary>
    private static Color? NearestBackdrop(Visual visual, Visual stopAt)
    {
        var current = visual.GetVisualParent();
        while (current is not null)
        {
            if (current is Border border && border.Background is ISolidColorBrush borderBrush && borderBrush.Color.A == 255)
            {
                return borderBrush.Color;
            }

            if (current is Panel panel && panel.Background is ISolidColorBrush panelBrush && panelBrush.Color.A == 255)
            {
                return panelBrush.Color;
            }

            if (ReferenceEquals(current, stopAt)) break;
            current = current.GetVisualParent();
        }

        return null;
    }

    private static ContentPresenter Presenter(Button button)
    {
        var presenter = button.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(candidate => candidate.Name == "PART_ContentPresenter");

        Assert.NotNull(presenter);

        return presenter!;
    }

    private static uint ToRgb(Color color) =>
        ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
