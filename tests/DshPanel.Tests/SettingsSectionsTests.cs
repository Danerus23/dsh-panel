using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// НАСТРОЙКИ РАЗДЕЛАМИ СО СПИСКОМ СЛЕВА (решение владельца 27.09.2026, `docs\DESIGN.md` п. 21):
/// слева вертикальный список разделов — «Общее», «Копии», «Баланс и тариф», «Сервер», — справа
/// содержимое выбранного.
///
/// Четыре обещания, и у каждого своя беда, ради которой оно и названо:
///
/// 1. **несохранённая правка переживает переключение раздела.** Пересборка разделов на
///    переключении (или вызов перерисовки из настроек) стёрла бы введённое — а над окном стоит
///    защита «несохранённая правка» (п. 16), и она опирается на те же поля;
/// 2. **содержимое раздела прокручивается, и полоса ВИДНА** — у содержимого, а не у всего окна
///    (жалоба п. 4–5: «владелец обнаружил прокрутку наощупь»);
/// 3. **«Сохранить» видна всегда** — при любом разделе и на минимальной высоте окна: она ВНЕ
///    прокрутки раздела;
/// 4. **окно не «прыгает» по высоте** при переключении разделов, а выбранный раздел виден
///    подсветкой.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class SettingsSectionsTests
{
    private const int General = 0;
    private const int Backup = 1;
    private const int Balance = 2;
    private const int Server = 3;
    private const int Update = 4;

    private static void Settle() => PanelTestStand.Settle();

    /// <summary>Контейнер строки списка — тот, по которому видно выбор (подсветка и подпись).</summary>
    private static ListBoxItem Item(ListBox list, int index)
    {
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();

        Assert.True(
            items.Count > index,
            $"в списке разделов контейнеров {items.Count}, а нужен {index} — список не показан?");

        return items[index];
    }

    /// <summary>
    /// СПИСОК РАЗДЕЛОВ — ЧЕТЫРЕ ШТУКИ, И ЭТО ИМЕННО ТЕ РАЗДЕЛЫ: подписи те же, что у заголовков
    /// разделов, и второго набора имён для тех же четырёх разделов быть не должно.
    /// </summary>
    [AvaloniaFact]
    public void Слева_список_разделов_и_справа_ровно_один_из_них()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            Settle();

            var list = window.FindControl<ListBox>("SectionsList");
            Assert.NotNull(list);

            var titles = list!.ItemsSource!.Cast<string>().ToList();

            Assert.Equal(5, titles.Count);
            Assert.Equal(
                new[]
                {
                    PanelStrings.SettingsGeneral,
                    PanelStrings.BackupSectionTitle,
                    PanelStrings.SettingsBalanceSection,
                    PanelStrings.SettingsServerSection,
                    PanelStrings.UpdateSectionTitle,
                },
                titles);

            // Выбран первый раздел, и показан РОВНО ОДИН раздел из пяти.
            Assert.Equal(General, list.SelectedIndex);
            Assert.Equal(General, window.VisibleSection);

            foreach (var index in new[] { General, Backup, Balance, Server, Update })
            {
                window.ShowSection(index);
                Settle();

                Assert.Equal(index, window.VisibleSection);
                Assert.Equal(index, list.SelectedIndex);
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ВЫБРАННЫЙ РАЗДЕЛ ВИДЕН: у него другая подпись начертания и другая подложка, чем у соседей.
    /// «Список слева» без признака выбора — это четыре строки, по которым не понять, чей
    /// содержимое справа.
    /// </summary>
    [AvaloniaFact]
    public void Выбранный_раздел_виден_подсветкой()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            Settle();

            var list = window.FindControl<ListBox>("SectionsList");
            Assert.NotNull(list);

            window.ShowSection(Backup);
            Settle();

            var selected = Item(list!, Backup);
            var neighbour = Item(list!, General);

            var selectedFrame = PanelTestStand.FrameHash(window);

            // Подпись выбранной строки жирнее: подсветка каркаса плюс собственный признак панели.
            Assert.NotEqual(neighbour.FontWeight, selected.FontWeight);
            Assert.Equal(Avalonia.Media.FontWeight.SemiBold, selected.FontWeight);

            // И кадр окна с выбранным разделом не тот, что с другим: содержимое сменилось.
            window.ShowSection(General);
            Settle();

            Assert.NotEqual(selectedFrame, PanelTestStand.FrameHash(window));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПЕРЕКЛЮЧЕНИЕ РАЗДЕЛА НЕ ТЕРЯЕТ НЕСОХРАНЁННУЮ ПРАВКУ — и окно по-прежнему ЗНАЕТ, что она
    /// есть (п. 16 не сломан). Ради этого содержимое всех четырёх разделов построено заранее
    /// и на переключении только показывается.
    /// </summary>
    [AvaloniaFact]
    public void Переключение_раздела_не_теряет_несохранённую_правку()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            Settle();

            var box = window.FindControl<TextBox>("WorkDirBox");
            Assert.NotNull(box);

            var typed = "C:\\Temp\\правка-владельца";
            box!.Text = typed;
            Settle();

            Assert.True(window.HasUnsavedChanges(), "правка не видна окну сразу после ввода");

            for (var round = 0; round < 2; round++)
            {
                foreach (var index in new[] { Backup, Balance, Server, Update, General })
                {
                    window.ShowSection(index);
                    Settle();

                    Assert.Equal(typed, box.Text);
                    Assert.True(
                        window.HasUnsavedChanges(),
                        $"правка потеряна при переходе в раздел {index} — окно закроется молча");
                }
            }

            // И в файл она не ушла: «показать раздел» — не «сохранить».
            Assert.False(window.Saved, "переключение раздела записало настройки — этого делать нельзя");
            Assert.True(window.HasUnsavedChanges(), "окно перестало видеть несохранённую правку");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОДЕРЖИМОЕ РАЗДЕЛА ПРОКРУЧИВАЕТСЯ, ПОЛОСА ВИДНА, и последняя настройка длинного раздела
    /// («Копии») достижима прокруткой. Меряется НАСТОЯЩАЯ полоса — орган в дереве, а не свойство
    /// разметки: свойство можно выставить и не увидеть ничего.
    ///
    /// ⚠️ ВЫСОТА ОКНА ЗДЕСЬ 800, а не 900, и это замер, а не подгонка: решением владельца 27.09.2026
    /// (п. 11) из раздела «Копии» ушла «копия для передачи», и его содержимое стало 651 точку —
    /// при высоте 900 в окно влезает 710, то есть прокручивать В НЁМ нечего, и «полоса делает
    /// работу» ничего бы не значила. При 800 видно окно 610 против содержимого 651: раздел «Копии»
    /// и остаётся самым длинным (у остальных 610).
    /// </summary>
    [AvaloniaFact]
    public void Содержимое_раздела_прокручивается_и_полоса_видна()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Height = 800;
            window.Show();
            Settle();

            var scroll = window.FindControl<ScrollViewer>("ContentScroll");
            Assert.NotNull(scroll);

            Assert.Equal(ScrollBarVisibility.Visible, scroll!.VerticalScrollBarVisibility);

            // Самый длинный раздел — «Копии»: единственная настройка, спасающая копии
            // от ротации («сколько копий хранить»), стоит в нём.
            window.ShowSection(Backup);
            Settle();

            Assert.True(
                scroll.Extent.Height > scroll.Viewport.Height,
                $"прокручивать нечего: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

            var bar = scroll.GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical
                                          && ReferenceEquals(candidate.FindAncestorOfType<ScrollViewer>(), scroll));

            Assert.NotNull(bar);
            Assert.True(bar!.IsVisible, "полоса прокрутки не показана");
            Assert.True(bar.Bounds.Width > 0, $"полоса нулевой ширины ({bar.Bounds.Width:0.#})");
            Assert.True(bar.Maximum > 0, $"полосе нечего прокручивать: Maximum={bar.Maximum:0.#}");

            // Последняя настройка раздела достижима прокруткой — так это делает человек колесом.
            var keep = window.FindControl<NumericUpDown>("BackupKeepBox");
            Assert.NotNull(keep);

            var inViewport = keep!.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(inViewport);

            var inContent = inViewport!.Value.Y + scroll.Offset.Y;
            var limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

            scroll.Offset = new Vector(0, Math.Clamp(inContent - 20, 0, limit));
            Settle();

            var top = keep.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(top);

            var bottom = top!.Value.Y + keep.Bounds.Height;
            var where = $"окно {window.ClientSize.Height:0.#}, Extent={scroll.Extent.Height:0.#}, " +
                        $"Viewport={scroll.Viewport.Height:0.#}, Offset={scroll.Offset.Y:0.#}, " +
                        $"«сколько копий хранить»: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

            Assert.True(keep.IsVisible, $"настройка не показана — {where}");
            Assert.True(top.Value.Y >= -0.5, $"настройка выше видимой области — {where}");
            Assert.True(bottom <= window.ClientSize.Height + 0.5, $"настройка не достижима — {where}");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «СОХРАНИТЬ» ВИДНА ВСЕГДА: при КАЖДОМ из четырёх разделов и на МИНИМАЛЬНОЙ высоте окна —
    /// она стоит вне прокрутки раздела. Прежде кнопка жила в конце общей простыни и на 1080p
    /// уезжала за нижний край.
    /// </summary>
    [AvaloniaFact]
    public void Сохранить_видна_при_любом_разделе_и_на_минимальной_высоте()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);

            // Минимальная высота окна из разметки — та, на которой содержимое точно не влезает.
            window.Height = window.MinHeight;
            window.Show();
            Settle();

            var save = window.FindControl<Button>("SaveButton");
            Assert.NotNull(save);

            for (var index = 0; index < 5; index++)
            {
                window.ShowSection(index);
                Settle();

                var top = save!.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(top);

                var bottom = top!.Value.Y + save.Bounds.Height;
                var where = $"раздел {index}, окно {window.ClientSize.Height:0.#}, " +
                            $"кнопка: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

                Assert.True(save.IsVisible, $"«Сохранить» не показана — {where}");
                Assert.True(top.Value.Y >= -0.5, $"«Сохранить» выше видимой области — {where}");
                Assert.True(bottom <= window.ClientSize.Height + 0.5, $"«Сохранить» не достижима — {where}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ОКНО НЕ «ПРЫГАЕТ» ПО ВЫСОТЕ при переключении разделов: размер содержимого меняется,
    /// а размер окна — нет.
    /// </summary>
    [AvaloniaFact]
    public void Окно_не_меняет_высоту_при_переключении_разделов()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            Settle();

            var scroll = window.FindControl<ScrollViewer>("ContentScroll");
            Assert.NotNull(scroll);

            var size = window.ClientSize;
            var viewport = scroll!.Viewport.Height;

            for (var index = 0; index < 5; index++)
            {
                window.ShowSection(index);
                Settle();

                Assert.Equal(size, window.ClientSize);

                Assert.True(
                    Math.Abs(viewport - scroll.Viewport.Height) <= 0.5,
                    $"область содержимого изменилась при переходе в раздел {index}: " +
                    $"{viewport:0.#} → {scroll.Viewport.Height:0.#}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОДПИСЬ РАЗДЕЛА ВЛЕЗАЕТ В КОЛОНКУ ЦЕЛИКОМ — замечание владельца 27.09.2026, который смотрел
    /// панель живьём и прислал скриншот: пункт «Резервное копирование» не вмещался в колонку
    /// разделов.
    ///
    /// ⚠️ Меряется ГЕОМЕТРИЯ, а не свойства: подпись с <c>TextWrapping</c>, которому не хватило
    /// ширины, обрезается ровно так же, как без него. Поэтому у КАЖДОЙ подписи берётся её
    /// ЕСТЕСТВЕННАЯ ширина (та же подпись, измеренная без ограничения), и проверяется, что она
    /// с запасом колонки, значком и отступами строки списка помещается в колонку ЦЕЛИКОМ.
    ///
    /// Второе утверждение — про перенос: подпись, которой места всё-таки не хватило (другой язык,
    /// крупный системный шрифт), обязана переноситься, а не обрезаться.
    /// </summary>
    [AvaloniaFact]
    public void Подпись_раздела_влезает_в_колонку_целиком()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            Settle();

            var list = window.FindControl<ListBox>("SectionsList");
            Assert.NotNull(list);

            var items = list!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(5, items.Count);

            var listRight = list.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(listRight);

            var border = listRight!.Value.X + list.Bounds.Width;
            var widest = 0.0;

            foreach (var item in items)
            {
                var label = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();

                Assert.NotNull(label);
                Assert.Equal(TextWrapping.Wrap, label!.TextWrapping);

                // Естественная ширина: та же подпись, измеренная БЕЗ ограничения колонки.
                var probe = new TextBlock
                {
                    Text = label.Text,
                    FontSize = label.FontSize,
                    FontWeight = label.FontWeight,
                    FontFamily = label.FontFamily,
                };

                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                widest = Math.Max(widest, probe.DesiredSize.Width);

                // И сама подпись не выходит за правый край списка: обрезанной подписи человек
                // не прочитает, а наехавшая на содержимое — ещё и закроет его.
                var point = label.TranslatePoint(new Point(0, 0), window);

                Assert.NotNull(point);
                Assert.True(
                    point!.Value.X + label.Bounds.Width <= border + 0.5,
                    $"подпись «{label.Text}» кончается на {point.Value.X + label.Bounds.Width:0.#}, " +
                    $"а список — на {border:0.#}");
            }

            // Колонка шире самой длинной подписи с запасом на значок (16), промежуток (8)
            // и внутренние отступы строки списка (12 + 12).
            Assert.True(
                list.Bounds.Width >= widest + 48,
                $"колонка разделов {list.Bounds.Width:0.#} уже самой длинной подписи {widest:0.#} " +
                "с запасом на значок и отступы — «Резервное копирование» снова не влезет");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
