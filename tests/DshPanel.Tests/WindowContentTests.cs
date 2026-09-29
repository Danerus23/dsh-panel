using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧТО ВИДНО В ОКНАХ НАСТРОЕК И КОПИЙ — и видно ли вообще.
///
/// Пачка правок 27.09.2026 по списку `docs\DESIGN.md` (жалобы владельца):
///
/// * **п. 4–5** — «настройки не влезают в 1080p», «владелец обнаружил прокрутку наощупь»:
///   полосы прокрутки не было видно ни в настройках, ни в окне копий (системная появляется
///   только в момент прокрутки, то есть когда человек уже догадался прокрутить), а обрезанный
///   снизу текст читался как обрезанное окно;
/// * **п. 8** — «план наката — простыня, и его появление незаметно»: план появлялся НИЖЕ списка
///   копий, без прокрутки к нему;
/// * **п. 10** — «заголовок „Копии“ показывает путь, а в поле настроек пусто»: два окна говорили
///   об одном и том же по-разному.
///
/// ⚠️ Проверка обязана проходить через то состояние, в котором дефект ВОЗМОЖЕН, поэтому окна
/// здесь показываются и мерятся на настоящих размерах, а не «на глаз»: на высокой высоте
/// содержимое влезает целиком, и «полоса не видна» прошло бы незамеченным.
/// </summary>
public class WindowContentTests
{
    /// <summary>Дать вёрстке и очереди диспетчера отработать до конца: без этого Bounds ещё нулевые.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// НАСТОЯЩАЯ полоса прокрутки окна — орган в дереве, а не свойство разметки.
    ///
    /// Разница не придирка: свойство можно выставить и не увидеть ничего, а человеку нужна
    /// полоса, которую ВИДНО. Поэтому проверка спрашивает у самого органа: показан ли он,
    /// есть ли у него ширина и есть ли что прокручивать.
    /// </summary>
    private static ScrollBar VerticalBar(ScrollViewer scroll)
    {
        // Ближайший ScrollViewer у полосы обязан быть НАШИМ: внутри окна есть и другие
        // (у каждого поля ввода и у списка свой), и первая попавшаяся полоса оказалась бы
        // полосой текстового поля — всегда скрытой. Проверка ловила бы не то.
        var bar = scroll.GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical
                                      && ReferenceEquals(candidate.FindAncestorOfType<ScrollViewer>(), scroll));

        Assert.NotNull(bar);

        return bar!;
    }

    /// <summary>Полоса видна и умеет работать: показана, с шириной, и предел прокрутки не ноль.</summary>
    private static void AssertBarVisible(ScrollViewer scroll, string where)
    {
        var bar = VerticalBar(scroll);

        Assert.True(bar.IsVisible, $"полоса прокрутки не показана — {where}");
        Assert.True(bar.Bounds.Width > 0, $"полоса прокрутки нулевой ширины ({bar.Bounds.Width:0.#}) — {where}");
        Assert.True(bar.Maximum > 0, $"полосе нечего прокручивать: Maximum={bar.Maximum:0.#} — {where}");
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-window-tests", Guid.NewGuid().ToString("N"));

    private static void RemoveTemp(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
        }
        catch
        {
            // Родитель занят другой проверкой — это не ошибка.
        }
    }

    /// <summary>Окно настроек с настоящим контроллером под СВОИМ временным корнем.</summary>
    private static SettingsWindow SettingsStand(string dir, bool allowed = true)
    {
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        var controller = new SettingsController(
            store, paths, allowed: allowed, canReadOwnerEnvironment: false,
            locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null);

        return window;
    }

    // ------------------------------------------------------------------ полоса прокрутки

    /// <summary>
    /// ПОЛОСА ПРОКРУТКИ В НАСТРОЙКАХ ВИДНА ВСЕГДА — и теперь у СОДЕРЖИМОГО РАЗДЕЛА, а не у всего
    /// окна (решение владельца 27.09.2026: «настройки разделами со списком слева, прокрутка —
    /// у содержимого раздела»).
    ///
    /// Самая длинная настройка живёт в разделе «Копии» — там и меряется: «сколько копий хранить»
    /// (единственная настройка, спасающая копии от ротации) на 1080p оказывалась ниже края
    /// (жалоба п. 4). А кнопка «Сохранить» теперь ВНЕ прокрутки: она видна при любом разделе.
    ///
    /// ⚠️ ВЫСОТА ЗДЕСЬ 800, а не 900 (та, с которой окно открывается), и это замер, а не подгонка:
    /// решением владельца 27.09.2026 (п. 11) из раздела «Копии» ушла «копия для передачи», и его
    /// содержимое стало 651 точку — при 900 в окно влезает 710, то есть прокручивать нечего и
    /// «полоса делает работу» ничего бы не значила. При 800 видно 610 против 651.
    /// </summary>
    [AvaloniaFact]
    public void В_настройках_полоса_прокрутки_видна_всегда_и_настройки_раздела_достижимы()
    {
        var dir = TempDir();

        try
        {
            var window = SettingsStand(dir);

            window.Height = 800;
            window.Show();
            Settle();

            // Раздел «Копии» — второй в списке разделов.
            window.ShowSection(1);
            Settle();

            var scroll = window.FindControl<ScrollViewer>("ContentScroll");
            Assert.NotNull(scroll);

            Assert.Equal(ScrollBarVisibility.Visible, scroll!.VerticalScrollBarVisibility);

            // Полоса делает настоящую работу: содержимое раздела длиннее окна. Иначе «полоса видна»
            // ничего не значит — она была бы украшением.
            Assert.True(
                scroll.Extent.Height > scroll.Viewport.Height,
                $"прокручивать нечего: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

            AssertBarVisible(
                scroll,
                $"настройки: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

            // Настройка ДОСТИЖИМА прокруткой — так это делает человек колесом.
            var control = window.FindControl<Control>("BackupKeepBox");
            Assert.NotNull(control);

            // Положение в СОДЕРЖИМОМ, а не в окне: начало координат прокрутки — верх содержимого.
            var inViewport = control!.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(inViewport);

            var inContent = inViewport!.Value.Y + scroll.Offset.Y;
            var limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

            scroll.Offset = new Vector(0, Math.Clamp(inContent - 20, 0, limit));
            Settle();

            var top = control.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(top);

            var bottom = top!.Value.Y + control.Bounds.Height;
            var where = $"окно {window.ClientSize.Height:0.#}, Extent={scroll.Extent.Height:0.#}, " +
                        $"Viewport={scroll.Viewport.Height:0.#}, Offset={scroll.Offset.Y:0.#}, " +
                        $"«сколько копий хранить»: в содержимом {inContent:0.#}, сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

            Assert.True(control.IsVisible, $"настройка не показана — {where}");
            Assert.True(top.Value.Y >= -0.5, $"настройка выше видимой области — {where}");
            Assert.True(bottom <= window.ClientSize.Height + 0.5, $"настройка не достижима — {where}");

            // А «Сохранить» при этом НЕ прокручивается вместе с содержимым: она стоит ниже
            // прокрутки раздела и видна человеку всегда (решение владельца).
            var save = window.FindControl<Button>("SaveButton");
            Assert.NotNull(save);

            var saveTop = save!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(saveTop);

            Assert.True(save.IsVisible, "«Сохранить» не показана при прокрученном разделе");

            Assert.True(
                saveTop!.Value.Y + save.Bounds.Height <= window.ClientSize.Height + 0.5,
                $"«Сохранить» уехала за нижний край — сверху {saveTop.Value.Y:0.#}, " +
                $"окно {window.ClientSize.Height:0.#}");

            window.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// И В ОКНЕ КОПИЙ полоса видна всегда — по той же причине: список копий, план наката и строка
    /// состояния уходят ниже края.
    /// </summary>
    [AvaloniaFact]
    public void В_окне_копий_полоса_прокрутки_видна_всегда()
    {
        var backups = new BackupWindowStopFlowTests.StubBackups();
        var window = new BackupWindow { Height = 520 };

        window.Attach(backups);
        window.Show();
        Settle();

        var scroll = window.FindControl<ScrollViewer>("ContentScroll");
        Assert.NotNull(scroll);

        Assert.Equal(ScrollBarVisibility.Visible, scroll!.VerticalScrollBarVisibility);
        Assert.True(
            scroll.Extent.Height > scroll.Viewport.Height,
            $"прокручивать нечего: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

        AssertBarVisible(
            scroll,
            $"копии: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

        window.Close();
    }

    /// <summary>
    /// ПОЛОСА ВИДНА И ТОГДА, КОГДА ПРОКРУЧИВАТЬ НЕЧЕГО, — вот что значит «видна ВСЕГДА»,
    /// и это в ОБОИХ окнах.
    ///
    /// Это единственное состояние, в котором «видна всегда» отличается от системного поведения:
    /// при <c>Auto</c> полоса в нём прячется, и человек, у которого на другой высоте окна или
    /// при другом масштабе экрана содержимое обрезано краем, снова видит «обрезанное окно» без
    /// единого признака прокрутки — ровно жалоба владельца (п. 5).
    /// </summary>
    [AvaloniaFact]
    public void Полоса_видна_и_тогда_когда_прокручивать_нечего()
    {
        // Окно копий: его содержимое влезает уже на 1400.
        var window = new BackupWindow { Height = 1400 };
        window.Attach(new BackupWindowStopFlowTests.StubBackups());
        window.Show();
        Settle();

        AssertBarWhenNothingToScroll(window, "копии");
        window.Close();

        // Окно настроек: содержимое длиннее (около 2100) и влезает только на очень высокой высоте.
        var dir = TempDir();

        try
        {
            var settings = SettingsStand(dir);
            settings.Height = 3000;
            settings.Show();
            Settle();

            AssertBarWhenNothingToScroll(settings, "настройки");
            settings.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «Прокручивать нечего, а полоса видна» — одним утверждением, чтобы оба окна проверялись
    /// одинаково: у каждого своя высота, на которой содержимое влезает целиком.
    /// </summary>
    private static void AssertBarWhenNothingToScroll(Window window, string what)
    {
        var scroll = window.FindControl<ScrollViewer>("ContentScroll");
        Assert.NotNull(scroll);

        Assert.Equal(ScrollBarVisibility.Visible, scroll!.VerticalScrollBarVisibility);

        Assert.True(
            scroll.Extent.Height <= scroll.Viewport.Height,
            $"{what}: содержимое НЕ влезло, проверка прошла не через то состояние — " +
            $"Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

        var bar = VerticalBar(scroll);

        Assert.True(
            bar.IsVisible,
            $"{what}: полоса спряталась, хотя обязана быть видна всегда — " +
            $"Maximum={bar.Maximum:0.#}, Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");
    }

    /// <summary>
    /// ПОЛОСА ПРОКРУТКИ НЕ НАЕЗЖАЕТ НА ПОЛЯ — ни на одном органе раздела.
    ///
    /// Жалоба владельца 27.09.2026 (его скриншот): у правого края карточки содержимого полоса
    /// идёт ПОВЕРХ полей и флажков. Причина у этого вида системная: у каркаса полоса прокрутки
    /// живёт в тех же 16 точках, что и содержимое (<c>ScrollContentPresenter</c> занимает всю
    /// ширину), поэтому «полоса видна» и «полоса ничего не закрывает» — РАЗНЫЕ обещания,
    /// и второе до этой проверки не сторожил никто.
    ///
    /// Меряется ГЕОМЕТРИЯ: правый край каждого органа раздела против левого края полосы,
    /// в координатах окна. Проверка идёт через то состояние, в котором дефект и был: раздел
    /// «Резервное копирование» (самое длинное содержимое) и НАСТОЯЩАЯ видимая полоса.
    ///
    /// ⚠️ Два размера окна, а не один: на широком окне поля короче и до полосы не достают даже
    /// без правки, а на минимальной ширине под полосу уходит как раз то, что шире всего.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(860, 900)]
    [InlineData(640, 420)]
    public void Полоса_прокрутки_настроек_не_перекрывает_ни_одного_поля(double width, double height)
    {
        var dir = TempDir();

        try
        {
            var window = SettingsStand(dir);
            window.Width = width;
            window.Height = height;
            window.Show();
            Settle();

            // Самый длинный раздел — «Резервное копирование»: сюда и уезжает полоса.
            window.ShowSection(SettingsWindow.BackupSection);
            Settle();

            var scroll = window.FindControl<ScrollViewer>("ContentScroll");
            Assert.NotNull(scroll);

            var bar = VerticalBar(scroll!);

            var barLeft = bar.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(barLeft);

            var measured = 0;
            var crossing = new List<string>();

            foreach (var control in scroll!.GetVisualDescendants().OfType<Control>())
            {
                // Внутренние части чужих органов (стрелки и ползунки полей, полосы внутри
                // списков) панель не объявляла: у них своё место, и к ним обещания нет.
                if (control.TemplatedParent is not null) continue;

                // Мерим только то, что человек видит: спрятанное поле ничего не закрывает.
                if (!control.IsVisible || control.Bounds.Width <= 0.5 || control.Bounds.Height <= 0.5) continue;

                // Сама полоса и её части — не «поле»: с собой она пересекается по определению.
                if (ReferenceEquals(control, bar) || control is ScrollBar) continue;

                var topLeft = control.TranslatePoint(new Point(0, 0), window);
                if (topLeft is null) continue;

                measured++;

                var right = topLeft.Value.X + control.Bounds.Width;

                if (right > barLeft.Value.X + 0.5)
                {
                    crossing.Add(
                        $"{control.GetType().Name}/{control.Name}: правый край {right:0.#} " +
                        $"за левым краем полосы {barLeft.Value.X:0.#}");
                }
            }

            Assert.True(
                measured >= 10,
                $"органов раздела осмотрено {measured} — похоже, проверка прошла не через то состояние " +
                "(раздел не показан? полоса не найдена?)");

            Assert.True(
                crossing.Count == 0,
                $"полоса прокрутки наезжает на органы раздела ({measured} осмотрено):" + Environment.NewLine +
                string.Join(Environment.NewLine, crossing));

            window.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ТО ЖЕ ОБЕЩАНИЕ В ОКНЕ КОПИЙ: правка одна на оба окна (правое поле содержимого — ровно
    /// ширина полосы), и «в настройках починили, а в копиях нет» — это тот же дефект, только
    /// в другом окне.
    /// </summary>
    [AvaloniaFact]
    public void Полоса_прокрутки_окна_копий_не_перекрывает_ни_одной_кнопки()
    {
        var window = new BackupWindow { Width = 620, Height = 520 };

        window.Attach(new BackupWindowStopFlowTests.StubBackups());
        window.Show();
        Settle();

        var scroll = window.FindControl<ScrollViewer>("ContentScroll");
        Assert.NotNull(scroll);

        var bar = VerticalBar(scroll!);
        var barLeft = bar.TranslatePoint(new Point(0, 0), window);

        Assert.NotNull(barLeft);

        var crossing = new List<string>();
        var measured = 0;

        foreach (var control in scroll!.GetVisualDescendants().OfType<Control>())
        {
            if (control.TemplatedParent is not null) continue;
            if (!control.IsVisible || control.Bounds.Width <= 0.5 || control.Bounds.Height <= 0.5) continue;
            if (ReferenceEquals(control, bar) || control is ScrollBar) continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), window);
            if (topLeft is null) continue;

            measured++;

            var right = topLeft.Value.X + control.Bounds.Width;

            if (right > barLeft.Value.X + 0.5)
            {
                crossing.Add($"{control.GetType().Name}/{control.Name}: правый край {right:0.#} > {barLeft.Value.X:0.#}");
            }
        }

        Assert.True(measured >= 5, $"органов окна копий осмотрено {measured} — похоже, окно не показано");

        Assert.True(
            crossing.Count == 0,
            "полоса прокрутки окна копий наезжает на органы:" + Environment.NewLine +
            string.Join(Environment.NewLine, crossing));

        window.Close();
    }

    /// <summary>
    /// ПОСЛЕ ВЫБОРА КОПИИ ОКНО САМО ПОДВОДИТ ПЛАН К ГЛАЗАМ (п. 8).
    ///
    /// Проверка идёт через то состояние, в котором дефект и был: окно ростом 520 (его собственный
    /// минимальный размер) — план при такой высоте за нижним краем, и без прокрутки человек
    /// нажимал «Восстановить», не прочитав, что именно вернётся.
    /// </summary>
    [AvaloniaFact]
    public void После_выбора_копии_план_наката_оказывается_на_экране()
    {
        var backups = new BackupWindowStopFlowTests.StubBackups();
        var window = new BackupWindow { Height = 520 };

        window.Attach(backups);
        window.Show();
        Settle();

        var scroll = window.FindControl<ScrollViewer>("ContentScroll");
        var plan = window.FindControl<Border>("PlanPanel");
        var table = window.FindControl<Grid>("EntriesTable");

        Assert.NotNull(scroll);
        Assert.NotNull(plan);
        Assert.NotNull(table);

        // До выбора копии плана нет вовсе — окно показывает таблицу копий и ничего не обещает.
        Assert.False(plan!.IsVisible, "план показан до выбора копии");
        Assert.True(window.ListedCount > 0, "в таблице нет ни одной копии — выбирать нечего");

        // Тот же путь, что у щелчка по строке таблицы (`Views\BackupTableView.cs` зовёт эту дверь).
        window.ChooseEntry(0);
        Settle();

        Assert.True(plan.IsVisible, "после выбора копии план обязан быть показан");

        var top = plan.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(top);

        var bottom = top!.Value.Y + plan.Bounds.Height;
        var where = $"окно {window.ClientSize.Height:0.#}, Extent={scroll!.Extent.Height:0.#}, " +
                    $"Viewport={scroll.Viewport.Height:0.#}, Offset={scroll.Offset.Y:0.#}, " +
                    $"план: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

        Assert.True(scroll.Offset.Y > 0, $"окно не прокрутилось к плану — {where}");
        Assert.True(top.Value.Y >= -0.5, $"план выше видимой области — {where}");
        Assert.True(bottom <= window.ClientSize.Height + 0.5, $"план не достижим — {where}");

        window.Close();
    }

    // ------------------------------------------------------------------ умолчание папки копий

    /// <summary>
    /// УМОЛЧАНИЕ ПАПКИ КОПИЙ ПОКАЗАНО В САМОМ ПОЛЕ НАСТРОЕК (п. 10): окно копий пишет
    /// развёрнутый путь, а здесь поле стояло пустым, и связь «пусто = умолчание» жила только
    /// словами под ним. Теперь и тут видно ТО ЖЕ САМОЕ — заполнителем, который виден ровно
    /// тогда, когда поле пусто, то есть ровно тогда, когда умолчание и действует.
    ///
    /// В файл при этом уходит по-прежнему пусто: подставленный путь перестал бы быть умолчанием,
    /// и вернуться к нему человек уже не смог бы.
    /// </summary>
    [AvaloniaFact]
    public void Умолчание_папки_копий_показано_в_поле_настроек()
    {
        var dir = TempDir();

        try
        {
            var window = SettingsStand(dir);
            var box = window.FindControl<TextBox>("BackupFolderBox");
            var hint = window.FindControl<TextBlock>("BackupFolderHintText");

            Assert.NotNull(box);
            Assert.NotNull(hint);

            var expected = DisplayMask.Path(BackupNaming.DefaultFolder());

            Assert.Equal(string.Empty, box!.Text);
            Assert.Equal(expected, box.PlaceholderText);

            // И подсказка под полем называет тот же самый путь: поле и слова не разошлись.
            Assert.Contains(expected, hint!.Text!, StringComparison.Ordinal);

            window.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Выбранная человеком папка показывается КАК ЕСТЬ и заполнителем не подменяется: иначе
    /// человек не понял бы, откуда взялся путь, которого он не выбирал.
    /// </summary>
    [AvaloniaFact]
    public void Выбранная_папка_копий_не_подменяется_умолчанием()
    {
        var dir = TempDir();

        try
        {
            var paths = AppPaths.Under(dir);
            var store = new SettingsStore(paths.SettingsFile);
            var chosen = Path.Combine(dir, "Мои копии");

            Assert.True(store.Save(new PanelSettings { BackupFolder = chosen }));

            var controller = new SettingsController(
                store, paths, allowed: true, canReadOwnerEnvironment: false,
                locateEngine: () => null, server: () => null, applyTheme: _ => { }, log: _ => { });

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);

            var box = window.FindControl<TextBox>("BackupFolderBox");
            Assert.NotNull(box);

            Assert.Equal(chosen, box!.Text);
            Assert.Equal(DisplayMask.Path(chosen), box.PlaceholderText);

            window.CloseQuietly();
        }
        finally
        {
            RemoveTemp(dir);
        }
    }
}
