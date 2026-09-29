using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

// ⚠️ Имя «PeakWindow» занято ДВАЖДЫ — окном (`Views.PeakWindow`) и записью расписания
// (`Agents.PeakWindow`), поэтому пространство имён целиком здесь не подключается: подключи его —
// и каждая ссылка на окно станет неоднозначной (CS0104). Нужен только расчёт состояния.
using PeakDecisions = DshPanel.Peak.PeakDecisions;

namespace DshPanel.Tests;

/// <summary>
/// СТИЛЬ СПИСКА РАЗДЕЛОВ И КАРТОЧЕК — «как в harness» (решение владельца 27.09.2026, шаг 2 вида).
///
/// Три обещания, и каждое проверяется там, где его видно:
///
/// 1. **у каждого раздела списка есть СВОЙ значок** — векторная геометрия без картинок и
///    шрифтовых значков (<see cref="PanelGlyph"/>). Мерятся настоящие <c>PathIcon</c> показанного
///    окна, а не объявление: значок можно объявить и не увидеть ничего;
/// 2. **выбранный раздел отмечен скруглённой подложкой ИЗ ТЕМЫ**, а не одной лишь полужирной
///    подписью, и у соседней строки такой подложки нет — иначе «выбранный» не отличить;
/// 3. **воздух карточек один на все окна** и он вырос: у настоящих <c>Border.card</c> всех
///    показанных окон отступ не меньше 24 (шкала 4/8/12/16/24), и «в настройках 24, а в копиях
///    16» — тот же разнобой, от которого панель уже избавлялась.
///
/// ⚠️ Проверки идут через ПОКАЗАННЫЕ окна: у органа, собранного «в воздухе», стиль ещё
/// не применён, и «подложка из темы» на нём ничего не доказывала бы.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class SettingsLookTests
{
    /// <summary>Воздух карточки — одно число на всю панель (шкала 4/8/12/16/24).</summary>
    private const double CardAir = 24;

    private static Application App => Application.Current!;

    private static void Settle() => PanelTestStand.Settle();

    /// <summary>Кисть темы в этой теме или падение: «нет кисти» — это и есть дефект.</summary>
    private static IBrush ThemeBrush(string key, ThemeVariant variant)
    {
        Assert.True(
            App.TryFindResource(key, variant, out var value) && value is IBrush brush,
            $"тема {variant} не отдала кисть «{key}» — подложка выбранной строки осталась бы без цвета");

        return (IBrush)value!;
    }

    /// <summary>Контейнер строки списка — тот, по которому видно выбор.</summary>
    private static ListBoxItem Item(ListBox list, int index)
    {
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();

        Assert.True(
            items.Count > index,
            $"в списке разделов контейнеров {items.Count}, а нужен {index} — список не показан?");

        return items[index];
    }

    /// <summary>Орган, которым Fluent рисует подложку строки.</summary>
    private static ContentPresenter Presenter(ListBoxItem item)
    {
        var presenter = item.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(candidate => candidate.Name == "PART_ContentPresenter");

        Assert.NotNull(presenter);

        return presenter!;
    }

    /// <summary>Кадр окна настроек с выбранным разделом — и его список слева.</summary>
    private static (SettingsWindow Window, ListBox List, string Dir) Settings(int section)
    {
        var dir = PanelTestStand.TempDir();
        var window = PanelTestStand.SettingsStand(dir);

        window.Show();
        Settle();

        window.ShowSection(section);
        Settle();

        var list = window.FindControl<ListBox>("SectionsList");
        Assert.NotNull(list);

        return (window, list!, dir);
    }

    // ------------------------------------------------------------------ значки разделов

    /// <summary>
    /// У КАЖДОГО РАЗДЕЛА СПИСКА — СВОЙ ЗНАЧОК: настоящий <c>PathIcon</c> с непустой геометрией
    /// и с краской (иначе значка не видно). Четыре геометрии РАЗНЫЕ — по одному значку на раздел,
    /// а не один на всех.
    /// </summary>
    [AvaloniaFact]
    public void У_каждого_раздела_списка_есть_свой_значок()
    {
        var (window, list, dir) = Settings(SettingsWindow.BackupSection);

        try
        {
            var icons = new List<Geometry>();

            for (var index = 0; index < 5; index++)
            {
                var icon = Item(list, index).GetVisualDescendants().OfType<PathIcon>().SingleOrDefault();

                Assert.True(icon is not null, $"у раздела {index} нет значка");
                Assert.NotNull(icon!.Data);
                Assert.NotNull(icon.Foreground);

                Assert.True(
                    icon.Data!.Bounds.Width > 0 && icon.Data.Bounds.Height > 0,
                    $"значок раздела {index} пустой по размерам: {icon.Data.Bounds}");

                icons.Add(icon.Data);
            }

            // Значки РАЗНЫЕ — и как органы, и как формы (данные пути, а не «четыре раза
            // один и тот же разобранный»).
            Assert.Equal(5, icons.Distinct().Count());
            Assert.Equal(5, PanelGlyph.SectionPaths.Distinct().Count());
            Assert.Equal(PanelGlyph.SectionPaths.Count, PanelGlyph.Sections.Count);

            // И подпись рядом со значком — на месте: значок дополняет слово, а не заменяет его.
            var titles = list.ItemsSource!.Cast<string>().ToList();

            Assert.Equal(5, titles.Count);

            for (var index = 0; index < titles.Count; index++)
            {
                var labels = Item(list, index).GetVisualDescendants().OfType<TextBlock>()
                    .Select(block => block.Text)
                    .ToList();

                Assert.Contains(titles[index], labels);
            }
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ЗНАЧОК РАЗДЕЛА ЗА ПРЕДЕЛАМИ СПИСКА НЕ РОНЯЕТ ОКНО: список разделов у панели растёт,
    /// и «пятый раздел уронил окно из-за картинки» — недопустимая цена. Незнакомый номер даёт
    /// значок первого раздела, а не пустое место и не исключение.
    ///
    /// ⚠️ Проверка идёт под работающим каркасом (<c>AvaloniaFact</c>), и это не формальность:
    /// разбор пути спрашивает у каркаса его графическую часть, а её у «голого» процесса нет.
    /// Значок, разобранный при загрузке типа, уронил бы тип целиком — именно этот случай здесь
    /// и сторожится: недоступная графика не имеет права ронять окно.
    /// </summary>
    [AvaloniaFact]
    public void Номер_раздела_вне_списка_даёт_значок_по_умолчанию()
    {
        Assert.Same(PanelGlyph.Section(0), PanelGlyph.Section(-1));
        Assert.Same(PanelGlyph.Section(0), PanelGlyph.Section(99));

        // И у значка есть размер: «пустая» геометрия на кнопке выглядела бы как пропавший значок.
        Assert.True(PanelGlyph.Section(0).Bounds.Width > 0);
        Assert.True(PanelGlyph.Section(0).Bounds.Height > 0);

        // Список значков — те же объекты, что отдаёт Section: второго разбора одних и тех же
        // путей нет, и «список» и «по номеру» не могут разойтись.
        var sections = PanelGlyph.Sections;

        Assert.Equal(PanelGlyph.SectionPaths.Count, sections.Count);

        for (var index = 0; index < sections.Count; index++)
        {
            Assert.Same(sections[index], PanelGlyph.Section(index));
        }
    }

    /// <summary>
    /// СПИСОК ЗНАЧКОВ — ТОЛЬКО ДАННЫЕ ПУТИ: он не зависит ни от каркаса, ни от графики и потому
    /// доступен всегда, даже там, где рисовать нечем. Это и есть проверка «все четыре геометрии
    /// разные» без участия графической части: две одинаковые формы здесь и поймались бы.
    /// </summary>
    [Fact]
    public void Данные_значков_не_зависят_от_графики()
    {
        Assert.Equal(5, PanelGlyph.SectionPaths.Count);
        Assert.Equal(5, PanelGlyph.SectionPaths.Distinct().Count());
        Assert.All(PanelGlyph.SectionPaths, path => Assert.False(string.IsNullOrWhiteSpace(path)));
    }

    // ------------------------------------------------------------------ подложка выбранного

    /// <summary>
    /// ВЫБРАННЫЙ РАЗДЕЛ ОТМЕЧЕН СКРУГЛЁННОЙ ПОДЛОЖКОЙ ИЗ ТЕМЫ, а у СОСЕДНЕЙ строки её нет.
    /// Цвет сверяется с кистью темы (<see cref="PanelLook.SectionSelectionKey"/>) — иначе
    /// «подобрали похожий оттенок в окне» осталось бы незамеченным, а на второй теме он бы
    /// слился с фоном.
    /// </summary>
    [AvaloniaFact]
    public void Выбранная_строка_отмечена_скруглённой_подложкой_из_темы()
    {
        var (window, list, dir) = Settings(SettingsWindow.BackupSection);

        try
        {
            var selected = Item(list, SettingsWindow.BackupSection);
            var neighbour = Item(list, 0);

            var selectedBackground = Presenter(selected).Background;
            var neighbourBackground = Presenter(neighbour).Background;

            var brush = (ISolidColorBrush)ThemeBrush(PanelLook.SectionSelectionKey, window.ActualThemeVariant);

            Assert.True(selectedBackground is ISolidColorBrush, "у выбранной строки нет подложки");
            Assert.Equal(brush.Color, ((ISolidColorBrush)selectedBackground!).Color);

            Assert.True(Presenter(selected).CornerRadius.TopLeft > 0, "подложка выбранной строки не скруглена");
            Assert.True(selected.CornerRadius.TopLeft > 0, "у строки списка нет скругления");
            Assert.True(selected.Padding.Left > 0, "у строки списка нет внутренних отступов");

            // У соседней строки подложки НЕТ: иначе «выбранный» ничем не отличается от прочих,
            // и подсветка ничего не значит.
            Assert.True(
                neighbourBackground is not ISolidColorBrush plain || plain.Color != brush.Color,
                "у невыбранной строки та же подложка, что у выбранной — выбор не виден");
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ разделитель строк таблицы

    /// <summary>
    /// РАЗДЕЛИТЕЛЬ СТРОК ТАБЛИЦЫ БЕРЁТ КИСТЬ ИЗ ТЕМЫ: у каждой из восьми строк показанного окна
    /// она та же, что у рамки карточки (<see cref="PanelLook.PeakRowSeparatorKey"/>).
    ///
    /// ⚠️ **Мерится в окне «Пики и тарифы», а не в настройках:** таблица «График пиков» живёт
    /// теперь ТОЛЬКО там (решение владельца 28.09.2026), и проверять её кисть в окне настроек
    /// значило бы проверять то, чего человек больше не видит. Проверка переехала вместе с таблицей.
    ///
    /// И она идёт в ПОКАЗАННОМ окне — это не придирка: кисть приходит стилем с
    /// <c>DynamicResource</c>, а стиль применяется к органу в дереве. У таблицы, собранной
    /// «в воздухе», кисти ещё нет — там видна только толщина (её сторожит
    /// <c>PeakTableViewTests</c>). Свой серый, подобранный числом, на одной из двух тем пропадает:
    /// панель на этом уже спотыкалась с рамкой карточки.
    /// </summary>
    [AvaloniaFact]
    public void Разделитель_строк_таблицы_берёт_кисть_из_темы()
    {
        var window = new Views.PeakWindow();

        try
        {
            window.Attach(new PeakWindowTests.StubBalance(
                AgentCatalog.DeepSeek,
                PeakDecisions.State(AgentCatalog.DeepSeek, DateTimeOffset.Now)));

            window.Show();
            PanelTestStand.Settle();

            var table = window.FindControl<Grid>("PeakTablePanel");
            Assert.NotNull(table);

            var expected = (ISolidColorBrush)ThemeBrush(PanelLook.PeakRowSeparatorKey, window.ActualThemeVariant);
            var lines = PeakTableProbe.Lines(table!);

            Assert.Equal(8, lines.Count);

            foreach (var line in lines)
            {
                Assert.Contains(PeakTableView.SeparatorClass, line.Classes);
                Assert.True(line.BorderBrush is ISolidColorBrush, "у разделителя строки нет кисти");
                Assert.Equal(expected.Color, ((ISolidColorBrush)line.BorderBrush!).Color);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ подзаголовки

    /// <summary>
    /// У КАЖДОГО РАЗДЕЛА — ЗАГОЛОВОК И СЕРЫЙ ПОДЗАГОЛОВОК «что здесь настраивается».
    /// Подзаголовок — класс <c>hint</c>, а НЕ <c>sectionTitle</c>: на <c>sectionTitle</c> стоит
    /// проверка «заголовок крупнее подсказки и цветом акцента», и подзаголовок сломал бы её смысл.
    /// </summary>
    [AvaloniaFact]
    public void У_каждого_раздела_есть_серый_подзаголовок()
    {
        var (window, _, dir) = Settings(0);

        try
        {
            var expected = new[]
            {
                (Title: "GeneralSectionTitle", Subtitle: "GeneralSubtitleText", Text: PanelStrings.SettingsGeneralSubtitle),
                (Title: "BackupSectionTitle", Subtitle: "BackupSectionSubtitleText", Text: PanelStrings.BackupSectionSubtitle),
                (Title: "BalanceSectionTitle", Subtitle: "BalanceSectionSubtitleText", Text: PanelStrings.BalanceSectionSubtitle),
                (Title: "ServerSectionTitle", Subtitle: "ServerSectionSubtitleText", Text: PanelStrings.ServerSectionSubtitle),
            };

            foreach (var (titleName, subtitleName, text) in expected)
            {
                var title = window.FindControl<TextBlock>(titleName);
                var subtitle = window.FindControl<TextBlock>(subtitleName);

                Assert.NotNull(title);
                Assert.NotNull(subtitle);

                Assert.True(title!.Classes.Contains("sectionTitle"), $"{titleName} потерял класс заголовка");
                Assert.Contains("hint", subtitle!.Classes);
                Assert.DoesNotContain("sectionTitle", subtitle.Classes);

                Assert.Equal(text, subtitle.Text);
                Assert.False(string.IsNullOrWhiteSpace(subtitle.Text), $"{subtitleName} пуст");

                Assert.True(subtitle.FontSize < title.FontSize, $"{subtitleName} не мельче заголовка");
            }
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ шапка окна

    /// <summary>
    /// ВТОРИЧНАЯ КНОПКА «Открыть файл настроек» И «✕» СТОЯТ В ШАПКЕ ОКНА, ВНЕ ПРОКРУТКИ:
    /// подписи и подсказки приходят из словаря, а прокрутка содержимого их не двигает.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_шапки_подписаны_словами_и_стоят_вне_прокрутки()
    {
        var (window, _, dir) = Settings(SettingsWindow.BackupSection);

        try
        {
            var scroll = window.FindControl<ScrollViewer>("ContentScroll");
            var open = window.FindControl<Button>("OpenFolderButton");
            var save = window.FindControl<Button>("SaveButton");

            Assert.NotNull(scroll);
            Assert.NotNull(open);
            Assert.NotNull(save);

            Assert.Equal(PanelStrings.SettingsOpenFolderButton, open!.Content?.ToString());
            Assert.Equal(PanelStrings.TipSettingsOpenFolderButton, ToolTip.GetTip(open));

            // ⚠️ СВОЕГО КРЕСТИКА У ОКНА НАСТРОЕК НЕТ (замечание владельца 29.09.2026:
            // *«в верхнем правом углу странный крестик… зачем он, если есть стандартная функция
            // окна по закрытию?»*). Проверка стережёт именно это: кнопки закрытия в шапке быть
            // не должно, закрывает окно системная кнопка заголовка — и она спрашивает про правку
            // тем же `OnClosing` (проверено `SettingsUnsavedTests`).
            Assert.Null(window.FindControl<Button>("CloseButton"));

            foreach (var (button, name) in new[] { (open, "открыть файл настроек"), (save, "сохранить") })
            {
                Assert.True(
                    !scroll!.GetVisualDescendants().Contains(button),
                    $"кнопка «{name}» уехала внутрь прокрутки раздела — на минимальной высоте её не найти");

                Assert.True(button.IsVisible, $"кнопка «{name}» не показана");
            }
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «✕» ЗАКРЫВАЕТ ОКНО ТЕМ ЖЕ ПУТЁМ, ЧТО КРЕСТИК ОКНА: без правок — закрывает; с несохранённой
    /// правкой и ответом «Отмена» — НЕ закрывает и правку не теряет; с «Выйти без сохранения» —
    /// закрывает и ничего не пишет.
    ///
    /// Ради этого окно и держит один-единственный путь закрытия (<c>Close()</c>): второй путь
    /// не спрашивал бы, и правка пропала бы ровно через ту дверь, которую забыли научить.
    /// </summary>
    [AvaloniaFact]
    public void Крестик_кнопки_закрывает_окно_тем_же_путём_что_крестик_окна()
    {
        // 1. Без правок окно закрывается сразу и ни о чём не спрашивает.
        using (var stand = Stand())
        {
            var asked = 0;

            stand.Window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };
            stand.Window.Show();
            Settle();

            Click(stand.Window);
            Settle();

            Assert.False(stand.Window.IsVisible, "«✕» не закрыла окно без правок");
            Assert.Equal(0, asked);
        }

        // 2. Несохранённая правка и «Отмена»: окно остаётся открытым, правка на месте.
        using (var stand = Stand())
        {
            var asked = 0;

            stand.Window.UnsavedChoiceForTests = () => { asked++; return UnsavedChoice.Cancel; };
            stand.Window.Show();
            Settle();

            var box = stand.Window.FindControl<TextBox>("WorkDirBox")!;
            box.Text = @"D:\Новая папка";

            Assert.True(stand.Window.HasUnsavedChanges(), "правка не видна — проверка прошла бы не через то состояние");

            Click(stand.Window);
            Settle();

            Assert.Equal(1, asked);
            Assert.True(stand.Window.IsVisible, "«Отмена» обязана оставить окно открытым");
            Assert.Equal(@"D:\Новая папка", box.Text);
            Assert.True(stand.Window.HasUnsavedChanges(), "окно перестало видеть несохранённую правку");
            Assert.False(stand.Store.Exists, "«Отмена» ничего не записывает");

            stand.Window.CloseQuietly();
        }

        // 3. «Выйти без сохранения»: окно закрывается и в файл не уходит ничего.
        using (var stand = Stand())
        {
            stand.Window.UnsavedChoiceForTests = () => UnsavedChoice.Discard;
            stand.Window.Show();
            Settle();

            stand.Window.FindControl<TextBox>("WorkDirBox")!.Text = @"D:\Новая папка";

            Click(stand.Window);
            Settle();

            Assert.False(stand.Window.IsVisible, "«Выйти без сохранения» обязано закрыть окно");
            Assert.False(stand.Window.Saved);
            Assert.False(stand.Store.Exists, "без сохранения файл настроек создаваться не должен");
        }
    }

    /// <summary>
    /// Закрыть окно ТЕМ ЖЕ путём, что кнопка заголовка Windows.
    /// Close() поднимает Closing, на который окно и подписано, — второго пути у него нет.
    /// </summary>
    private static void Click(SettingsWindow window) => window.Close();

    /// <summary>Окно настроек с настоящим владельцем настроек под СВОИМ временным корнем.</summary>
    private static StandScope Stand()
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        var controller = new SettingsController(
            store, paths, allowed: true, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null);

        return new StandScope(window, store, dir);
    }

    /// <summary>Временный корень, окно и файл настроек: за собой убирает сам.</summary>
    private sealed class StandScope : IDisposable
    {
        public StandScope(SettingsWindow window, SettingsStore store, string dir)
        {
            Window = window;
            Store = store;
            Dir = dir;
        }

        public SettingsWindow Window { get; }

        public SettingsStore Store { get; }

        public string Dir { get; }

        public void Dispose() => PanelTestStand.RemoveTemp(Dir);
    }

    // ------------------------------------------------------------------ воздух карточек

    /// <summary>
    /// ВОЗДУХ КАРТОЧЕК ОДИН НА ВСЕ ОКНА И НЕ МЕНЬШЕ 24: у настоящих <c>Border.card</c> показанных
    /// окон внутренний отступ вырос с 16 и стал одинаковым. Единственное исключение названо и
    /// проверено: у карточки раздела настроек ПРАВОЕ поле — 0, потому что в нём живёт полоса
    /// прокрутки (полоса забирает последние 16 точек области прокрутки).
    /// </summary>
    [AvaloniaFact]
    public void Воздух_карточек_один_на_все_окна_и_не_меньше_24()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var windows = new List<(Window Window, string Where)>
            {
                (PanelTestStand.MainStand(ServerState.Stopped(0)), "главное окно"),
                (PanelTestStand.SettingsStand(dir), "настройки"),
                (NewPeakWindow(), "пики и тарифы"),
            };

            var backups = new BackupWindow();
            backups.Attach(new BackupWindowStopFlowTests.StubBackups());
            windows.Add((backups, "копии"));

            var cards = new List<(Window Window, string Where, Border Card, Thickness Air)>();
            var measured = 0;

            foreach (var (window, where) in windows)
            {
                window.Show();
                Settle();

                foreach (var card in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")))
                {
                    measured++;
                    cards.Add((window, where, card, card.Padding));
                }

                if (window is SettingsWindow quiet) quiet.CloseQuietly();
                else window.Close();
            }

            Assert.True(measured >= 5, $"карточек с классом card найдено {measured} — ожидались карточки всех четырёх окон");

            var wrong = new List<string>();

            foreach (var (_, where, card, air) in cards)
            {
                // Верх, низ и лево — ОДНО число, и оно из шкалы панели.
                if (air.Top < CardAir - 0.01 || air.Bottom < CardAir - 0.01 || air.Left < CardAir - 0.01)
                {
                    wrong.Add($"{where}/{card.Name}: Padding={air} — воздуха меньше {CardAir}");
                }

                // Право: либо тот же воздух, либо 0 — но тогда в карточке обязана жить прокрутка.
                if (Math.Abs(air.Right - CardAir) > 0.01)
                {
                    var scroll = card.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

                    if (Math.Abs(air.Right) > 0.01 || scroll is null)
                    {
                        wrong.Add(
                            $"{where}/{card.Name}: Padding={air} — правое поле не {CardAir} и не 0, " +
                            $"либо полосы прокрутки в карточке нет");
                    }
                }
            }

            Assert.True(
                wrong.Count == 0,
                "воздух карточек разошёлся (ждём верх/низ/лево = 24, право = 24 или 0 у карточки с прокруткой):" +
                Environment.NewLine + string.Join(Environment.NewLine, wrong));

            // И воздух ОДИН: у карточек с полным полем он совпадает у всех окон.
            var full = cards.Where(entry => Math.Abs(entry.Air.Right - CardAir) < 0.01)
                .Select(entry => entry.Air)
                .Distinct()
                .ToList();

            Assert.True(full.Count >= 1, "ни одной карточки с полным воздухом — исключение стало правилом");
            Assert.Single(full);

            // И «прежних 16» не осталось ни у одной карточки: это и есть «воздуха стало больше».
            Assert.DoesNotContain(cards, entry => Math.Abs(entry.Air.Top - 16) < 0.01);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>Окно «Пики и тарифы» без связки: карточки у него те же, а живое состояние не нужно.</summary>
    private static Views.PeakWindow NewPeakWindow() => new();
}
