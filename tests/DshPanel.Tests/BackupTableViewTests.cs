using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Backup;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТАБЛИЦА ГОТОВЫХ КОПИЙ — то, ради чего окно копий и переделано.
///
/// Слова владельца 28.09.2026 (живой просмотр, «Готовые копии»): *«Мне кажется, правильно будет
/// сделать правкой строк с имеющимися копиями тоже в виде таблицы или как-то иначе визуально эти
/// строки отделить. Не совсем понятно даже, что это кликабельно»*.
///
/// Отсюда обещания, и каждое проверяется там, где его видно:
///
/// 1. **строки — таблица**: строк ровно столько, сколько копий, у каждой те же колонки, что у шапки,
///    а значения берутся из ОПИСИ архива и ниоткуда больше;
/// 2. **кликабельность видна ДО щелчка**: курсор-рука, подложка под курсором, подложка выбранной
///    строки, тонкая линия между строками — и всё это КАДРОМ, а не одним объявлением стиля;
/// 3. **щелчок выбирает ИМЕННО ту строку** — проверяется настоящей мышью по настоящему окну;
/// 4. **состояние не меняет размер строки**: подсветка — это цвет, а не мерка (правило, выросшее
///    из «дрожания кнопок» 28.09.2026).
///
/// ⚠️ Копий панели 1.x в таблице нет и быть не может — их не отдаёт список; это сторожит
/// <c>BackupFolderTests</c>, а не здесь.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class BackupTableViewTests
{
    private static BackupEntry Copy(
        string name,
        DateTime moment,
        long bytes,
        int files,
        bool engine = true,
        bool sessions = true,
        bool keys = true,
        string error = "",
        bool readable = true) => new()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name),
        CreatedAt = moment,
        Bytes = bytes,
        Files = files,
        WithEngine = engine,
        WithSessions = sessions,
        WithCredentials = keys,
        Readable = readable,
        Error = error,
    };

    /// <summary>Три разные копии — чтобы «первая» и «третья» различались содержимым, а не номером.</summary>
    private static List<BackupEntry> Three() => new()
    {
        Copy("dsh2-backup-2026-09-26-013500-full.zip", new DateTime(2026, 9, 26, 1, 35, 0), 4_194_304, 12),
        Copy("dsh2-backup-2026-09-25-210000-thin.zip", new DateTime(2026, 9, 25, 21, 0, 0), 1024, 3,
            engine: false, sessions: false, keys: false),
        Copy("dsh2-backup-2026-09-24-120000-full.zip", new DateTime(2026, 9, 24, 12, 0, 0), 2048, 7,
            sessions: false),
    };

    /// <summary>Таблица в настоящем показанном окне: только так у строк есть границы, кадр и мышь.</summary>
    private static (Window Window, Grid Table, List<int> Chosen) Stand(IReadOnlyList<BackupEntry> entries, int selected = -1)
    {
        var chosen = new List<int>();

        var table = new Grid();
        var window = new Window { Width = 900, Height = 300, Content = table };

        window.Show();
        PanelTestStand.Settle();

        BackupTableView.Fill(table, entries, selected, chosen.Add);
        PanelTestStand.Settle();

        return (window, table, chosen);
    }

    /// <summary>Тексты ячеек одной строки — в порядке колонок.</summary>
    private static string[] Cells(Border row)
    {
        var grid = Assert.IsType<Grid>(row.Child);

        return grid.Children.OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToArray();
    }

    // ---- 1. таблица -------------------------------------------------------------------------

    /// <summary>
    /// СТРОКА НА КАЖДУЮ КОПИЮ И ШАПКА КОЛОНОК. Число колонок — одно у шапки и у строк: иначе
    /// значения стояли бы не под своими подписями, и таблица читалась бы как та же простыня,
    /// только с чертой сверху.
    /// </summary>
    [AvaloniaFact]
    public void Строк_столько_же_сколько_копий_а_шапка_называет_колонки()
    {
        var entries = Three();
        var (window, table, _) = Stand(entries);

        try
        {
            var rows = BackupTableView.Rows(table);

            Assert.Equal(entries.Count, rows.Count);

            // Шапка — тоже строка таблицы, но строкой КОПИИ не считается: у неё нет класса строки.
            Assert.Equal(entries.Count + 1, table.Children.OfType<Border>().Count());

            var header = table.Children.OfType<Border>().First();
            var titles = header.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();

            Assert.Equal(
                new[]
                {
                    PanelStrings.BackupTableTime,
                    PanelStrings.BackupTableKind,
                    PanelStrings.BackupTableSize,
                    PanelStrings.BackupTableFiles,
                    PanelStrings.BackupTableFeatures,
                },
                titles);

            // И у каждой строки ровно столько ячеек, сколько колонок объявлено.
            Assert.All(rows, row => Assert.Equal(titles.Length, Cells(row).Length));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ЗНАЧЕНИЯ БЕРУТСЯ ИЗ ОПИСИ И НИЧЕГО НЕ ВЫДУМЫВАЮТСЯ: дата и время, тип, размер, число файлов,
    /// особенности. Проверяются НАСТОЯЩИЕ ячейки показанной таблицы, а не функции форматирования:
    /// функцию можно написать и не позвать.
    /// </summary>
    [AvaloniaFact]
    public void Ячейки_строки_берутся_из_описи_архива()
    {
        var entries = Three();
        var (window, table, _) = Stand(entries);

        try
        {
            var rows = BackupTableView.Rows(table);

            // Полная копия со всем составом.
            Assert.Equal(
                new[]
                {
                    "26.09.2026 01:35",
                    PanelStrings.BackupKindFull,
                    BackupFormat.Size(4_194_304),
                    "12 файл(ов)",
                    string.Join(", ", new[]
                    {
                        PanelStrings.BackupWithEngine,
                        PanelStrings.BackupWithSessions,
                        PanelStrings.BackupWithKey,
                    }),
                },
                Cells(rows[0]));

            // Тонкая копия без сессий и без ключа: особенности не выдумывают того, чего нет.
            Assert.Equal(PanelStrings.BackupKindThin, Cells(rows[1])[1]);
            Assert.Equal("3 файл(ов)", Cells(rows[1])[3]);
            Assert.DoesNotContain(PanelStrings.BackupWithSessions, Cells(rows[1])[4], StringComparison.Ordinal);
            Assert.DoesNotContain(PanelStrings.BackupWithKey, Cells(rows[1])[4], StringComparison.Ordinal);

            // Полная, но без сессий: «с движком» есть, «с сессиями» нет — состав не склеен в один флаг.
            Assert.Equal(PanelStrings.BackupKindFull, Cells(rows[2])[1]);
            Assert.Contains(PanelStrings.BackupWithEngine, Cells(rows[2])[4], StringComparison.Ordinal);
            Assert.DoesNotContain(PanelStrings.BackupWithSessions, Cells(rows[2])[4], StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// НЕЧИТАЕМАЯ ОПИСЬ НАЗЫВАЕТ ПРИЧИНУ в колонке особенностей, а не оставляет пустое место:
    /// молчащая строка читается как «всё в порядке». Размер и число файлов при этом не выдумываются —
    /// их взять неоткуда.
    /// </summary>
    [AvaloniaFact]
    public void Нечитаемая_опись_называет_причину_а_не_молчит()
    {
        var broken = new List<BackupEntry>
        {
            Copy("dsh2-backup-2026-09-26-013500-full.zip", new DateTime(2026, 9, 26, 1, 35, 0), 0, 0,
                readable: false, error: PanelStrings.ZipManifestMissing),
        };

        var (window, table, _) = Stand(broken);

        try
        {
            var cells = Cells(BackupTableView.Rows(table)[0]);

            Assert.Contains("списка содержимого копии", cells[4], StringComparison.Ordinal);
            Assert.Equal(string.Empty, cells[2]);
            Assert.Equal(string.Empty, cells[3]);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- 2. кликабельность видна до щелчка ---------------------------------------------------

    /// <summary>
    /// СТРОКА ЛОВИТ КУРСОР И ОБЕЩАЕТ ЩЕЛЧОК: у неё есть подложка (без неё орган вовсе не ловит
    /// мышь — тогда ни наведения, ни щелчка не было бы) и курсор-рука.
    ///
    /// ⚠️ Курсор сверяется с ТЕМ ЖЕ «рука», а не с любым непустым значением: стрелка вместо руки
    /// ничего не обещает, а «курсор есть» прошло бы и с ней.
    /// </summary>
    [AvaloniaFact]
    public void Строка_ловит_курсор_и_показывает_руку()
    {
        var (window, table, _) = Stand(Three());

        try
        {
            var rows = BackupTableView.Rows(table);

            Assert.NotEmpty(rows);

            foreach (var row in rows)
            {
                Assert.NotNull(row.Background);
                Assert.NotNull(row.Cursor);

                // `Cursor` сравнивается по ССЫЛКЕ (проверено: два «Hand» не равны друг другу),
                // поэтому вид курсора сверяется по его имени — тем же значением, что объявлено
                // в таблице. Стрелка вместо руки это поймает: имя будет другим.
                Assert.Equal(BackupTableView.PointerCursor.ToString(), row.Cursor!.ToString());
            }

            // И само объявленное значение — «рука»: иначе предыдущее сравнение сошлось бы на «стрелке».
            Assert.Equal(StandardCursorType.Hand, BackupTableView.PointerCursor);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// НАВЕДЕНИЕ И ВЫБОР ВИДНЫ КАДРОМ — а не обещаны объявлением стиля. Свойство можно выставить
    /// и не увидеть ничего: в этом проекте так уже ломался невидимый фокус кнопки.
    /// </summary>
    [AvaloniaFact]
    public void Наведение_и_выбор_видны_кадром()
    {
        var (window, table, _) = Stand(Three());

        try
        {
            var row = BackupTableView.Rows(table)[1];

            var plain = PanelTestStand.FrameHash(window);

            // Наведение ставится тем же способом, каким его ставит каркас при живой мыши.
            ((IPseudoClasses)row.Classes).Set(":pointerover", true);
            var hovered = PanelTestStand.FrameHash(window);

            ((IPseudoClasses)row.Classes).Set(":pointerover", false);
            PanelTestStand.Settle();

            BackupTableView.Select(table, 1);
            var selected = PanelTestStand.FrameHash(window);

            Assert.NotEqual(plain, hovered);
            Assert.NotEqual(plain, selected);
            Assert.NotEqual(hovered, selected);

            // И обратный ход: снятый выбор возвращает прежний кадр — значит менял кадр именно выбор,
            // а не что-то ещё, что менялось само по себе.
            BackupTableView.Select(table, -1);
            Assert.Equal(plain, PanelTestStand.FrameHash(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ЧИСЛОВЫЕ КОЛОНКИ КОПИЙ ВЫРОВНЕНЫ ВПРАВО — И У ШАПКИ, И У ЗНАЧЕНИЙ.
    ///
    /// **От какого случая (пачка долгов 14.10.2026, §3).** «Размер» и «Файлов» стояли влево
    /// (`BackupTableView`), тогда как у плана наката то же сделано правильно (`RestoreTableView`) —
    /// и колонки чисел не читались столбиком. Норма — **GOV.UK Table**: «When comparing columns of
    /// numbers, align the numbers to the right»; у `TableGrid.Header`/`Cell` для этого есть довод
    /// `right`.
    ///
    /// Мутации, которые обязаны упасть здесь: **«снять `right: true` у шапки»** ·
    /// **«снять `right: true` у значений»**.
    /// </summary>
    [AvaloniaFact]
    public void Числовые_колонки_копий_выровнены_вправо()
    {
        var entries = Three();
        var (window, table, _) = Stand(entries);

        try
        {
            var lines = table.Children.OfType<Border>().ToList();

            // Шапка — тоже строка таблицы, поэтому строк ровно на одну больше числа копий.
            Assert.Equal(entries.Count + 1, lines.Count);

            // КОЛОНКИ ЧИСЕЛ НАЗВАНЫ НОМЕРАМИ — теми же, что в `Columns` таблицы: 4 — размер,
            // 6 — файлов. Список один на шапку и на значения, иначе «шапка вправо, а числа влево»
            // прошло бы проверку.
            foreach (var numeric in new[] { 4, 6 })
            {
                Assert.Equal(TextAlignment.Right, Line(lines[0], numeric).TextAlignment);

                for (var index = 1; index < lines.Count; index++)
                {
                    Assert.Equal(TextAlignment.Right, Line(lines[index], numeric).TextAlignment);
                }
            }

            // И ЭТО ИМЕННО ЧИСЛА, А НЕ ПУСТЫЕ ЯЧЕЙКИ: в четвёртой колонке первой копии — размер,
            // в шестой — число файлов, ровно теми словами, какими их показывает таблица.
            Assert.Equal(BackupTableView.Size(entries[0]), Line(lines[1], 4).Text);
            Assert.Equal(BackupTableView.Files(entries[0]), Line(lines[1], 6).Text);

            // А СОСЕДИ ОСТАЛИСЬ СЛЕВА: выравнивание — свойство ЧИСЛОВОЙ колонки, а не всей таблицы.
            Assert.Equal(TextAlignment.Left, Line(lines[1], 0).TextAlignment);
            Assert.Equal(TextAlignment.Left, Line(lines[1], 2).TextAlignment);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Ячейка строки таблицы в названной колонке сетки — по НОМЕРУ колонки, а не по счёту.</summary>
    private static TextBlock Line(Border line, int column)
    {
        var grid = Assert.IsType<Grid>(line.Child);

        return grid.Children.OfType<TextBlock>().Single(block => Grid.GetColumn(block) == column);
    }

    // ---- 5. клавиатура (пачка долгов 14.10.2026, §2) -----------------------------------------

    /// <summary>
    /// СТРОКУ КОПИИ МОЖНО ВЫБРАТЬ С КЛАВИАТУРЫ, ФОКУС ВИДЕН, И ВЫБОР НЕ МЕНЯЕТ РАЗМЕР СТРОКИ.
    ///
    /// **От какого случая (пачка долгов 14.10.2026, §2).** Выбор строки жил ТОЛЬКО на
    /// `PointerPressed`: с клавиатуры выбрать было нельзя, и фокуса не было видно вовсе. Норма
    /// банка (`50-lists-navigation.md` §2/§8): **WCAG 2.2 SC 2.1.1 (Keyboard)** и
    /// **SC 2.4.7 (Focus Visible)**; провал F59 — орган управления, сделанный из `div` без роли.
    ///
    /// ⚠️ **Клавиша нажимается НАСТОЯЩИМ вводом** (`window.KeyPressQwerty`, как мышь —
    /// `MouseMove`): подними `KeyDown` событием «в воздух» — и проверка доказала бы, что обработчик
    /// лежит на строке, а не что клавиатура до неё доехала.
    ///
    /// ⚠️ И кольцо фокуса меряется ТЕМ ЖЕ способом, что наведение и выбор выше: свойство можно
    /// выставить и не увидеть ничего — в этом проекте так уже ломался невидимый фокус кнопки.
    ///
    /// Мутации, которые обязаны упасть здесь: **«снять обработку клавиш»** · **«снять правило
    /// фокуса»** · «фокус не едет за выбором».
    /// </summary>
    [AvaloniaFact]
    public void Строку_копии_можно_выбрать_с_клавиатуры_и_фокус_виден()
    {
        var (window, table, chosen) = Stand(Three());

        try
        {
            var rows = BackupTableView.Rows(table);

            Assert.Equal(3, rows.Count);

            var height = rows[0].Bounds.Height;
            var plain = PanelTestStand.FrameHash(window);

            // (1) СТРОКА — ОРГАН: её можно взять фокусом, и фокус приходит С КЛАВИАТУРЫ.
            Assert.True(rows[0].Focusable, "строка не берёт фокус — с клавиатуры её не выбрать");
            Assert.True(rows[0].Focus(NavigationMethod.Directional), "строка не взяла фокус");

            PanelTestStand.Settle();

            Assert.True(rows[0].IsFocused, "фокус не встал на строку");
            Assert.True(rows[0].BoxShadow.Count > 0, "фокуса НЕ ВИДНО: у строки нет кольца фокуса");
            Assert.NotEqual(plain, PanelTestStand.FrameHash(window));

            // (2) СТРЕЛКА ВНИЗ ВЫБИРАЕТ СЛЕДУЮЩУЮ — настоящим нажатием, по настоящему окну.
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            PanelTestStand.Settle();

            Assert.Equal(new[] { 1 }, chosen);

            var after = BackupTableView.Rows(table);

            Assert.True(after[1].IsFocused, "фокус не поехал за выбором — вторая стрелка не сработает");

            // (3) РАЗМЕР СТРОКИ НЕ ИЗМЕНИЛСЯ: кольцо фокуса — тень, а не толщина рамки
            // (правило «состояние не меняет размер» выросло из «дрожания кнопок»).
            Assert.Equal(height, after[0].Bounds.Height);

            // И ВВЕРХ — ОБРАТНО: выбор не «залипает» в конце списка.
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            PanelTestStand.Settle();

            Assert.Equal(new[] { 1, 0 }, chosen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// КИСТИ ПОДСВЕТКИ — У ТЕМЫ, и она отдаёт их в ОБЕИХ темах. Своего оттенка, подобранного
    /// на светлой теме, на тёмной не видно вовсе — на этом панель уже спотыкалась.
    /// </summary>
    [AvaloniaFact]
    public void Кисти_подсветки_строки_есть_в_обеих_темах()
    {
        var application = Application.Current;
        Assert.NotNull(application);

        var missing = new List<string>();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var key in new[] { PanelLook.RowHoverKey, PanelLook.RowSelectedKey })
            {
                if (!application!.TryFindResource(key, variant, out var value) || value is not IBrush)
                {
                    missing.Add($"{variant}/{key}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "тема не отдала кисти подсветки строки: " + string.Join(", ", missing) +
            ". Взять их неоткуда — значит кликабельность осталась бы без единого признака.");
    }

    // ---- 3. щелчок выбирает именно эту строку -----------------------------------------------

    /// <summary>
    /// ЩЁЛЧОК ПО СТРОКЕ ВЫБИРАЕТ ИМЕННО ЕЁ. Проверка идёт НАСТОЯЩЕЙ мышью по настоящему окну:
    /// «обработчик привязан» и «щелчок доходит» — разные утверждения, и второе свойственно ломать
    /// (орган без подложки мышь не ловит вовсе, а строка с чужой сеткой попадает не туда).
    /// </summary>
    [AvaloniaFact]
    public void Щелчок_по_строке_выбирает_именно_её()
    {
        var (window, table, chosen) = Stand(Three());

        try
        {
            var row = BackupTableView.Rows(table)[2];
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window);

            Assert.NotNull(point);

            window.MouseDown(point!.Value, MouseButton.Left);
            window.MouseUp(point.Value, MouseButton.Left);

            Assert.Equal(new[] { 2 }, chosen);

            // И по первой строке — тоже она: «щелчок всегда попадает в первую» не сойдёт за выбор.
            var first = BackupTableView.Rows(table)[0];
            var firstPoint = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), window);

            window.MouseDown(firstPoint!.Value, MouseButton.Left);
            window.MouseUp(firstPoint.Value, MouseButton.Left);

            Assert.Equal(new[] { 2, 0 }, chosen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ВЫБРАННАЯ СТРОКА ВИДНА КЛАССОМ, И ОНА РОВНО ОДНА: две подсвеченные строки читались бы как
    /// «выбрано два», а подсветка не на той строке — как «щелчок попал в соседа».
    /// </summary>
    [AvaloniaFact]
    public void Выбранная_строка_одна_и_это_та_которую_выбрали()
    {
        var (window, table, _) = Stand(Three(), selected: 1);

        try
        {
            var marked = BackupTableView.Rows(table)
                .Select((row, index) => (Index: index, Marked: row.Classes.Contains(BackupTableView.SelectedClass)))
                .Where(pair => pair.Marked)
                .Select(pair => pair.Index)
                .ToArray();

            Assert.Equal(new[] { 1 }, marked);

            BackupTableView.Select(table, 2);

            marked = BackupTableView.Rows(table)
                .Select((row, index) => (Index: index, Marked: row.Classes.Contains(BackupTableView.SelectedClass)))
                .Where(pair => pair.Marked)
                .Select(pair => pair.Index)
                .ToArray();

            Assert.Equal(new[] { 2 }, marked);

            // Снятый выбор не оставляет подсвеченной ни одной строки.
            BackupTableView.Select(table, -1);
            Assert.DoesNotContain(BackupTableView.Rows(table), row => row.Classes.Contains(BackupTableView.SelectedClass));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- 4. состояние не меняет размер ------------------------------------------------------

    /// <summary>
    /// НАВЕДЕНИЕ И ВЫБОР НЕ МЕНЯЮТ РАЗМЕР СТРОКИ. Правило, выросшее из слов владельца 28.09.2026
    /// про дрожание кнопок: состояние меняет только ЦВЕТ. Здесь оно проверено на строке таблицы —
    /// у подсветки соблазн добавить отступ или рамку больше, чем у кнопки.
    /// </summary>
    [AvaloniaFact]
    public void Состояние_строки_не_меняет_её_размер()
    {
        var (window, table, _) = Stand(Three());

        try
        {
            var rows = BackupTableView.Rows(table);

            Assert.True(rows[0].Bounds.Height > 0, "вёрстка не досчитана — мерить нечего");

            var before = rows.Select(row => row.Bounds).ToArray();

            ((IPseudoClasses)rows[0].Classes).Set(":pointerover", true);
            PanelTestStand.Settle();

            BackupTableView.Select(table, 0);
            PanelTestStand.Settle();

            Assert.Equal(before, rows.Select(row => row.Bounds).ToArray());

            ((IPseudoClasses)rows[0].Classes).Set(":pointerover", false);
            BackupTableView.Select(table, -1);
            PanelTestStand.Settle();

            Assert.Equal(before, rows.Select(row => row.Bounds).ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    // ---- 5. счётчик считает только показанные строки ----------------------------------------

    /// <summary>
    /// СЧЁТЧИК «Готовые копии (N)» СЧИТАЕТ ТОЛЬКО ПОКАЗАННЫЕ СТРОКИ. Иначе человек видит «4»,
    /// а строк три, — и это ложь ровно о его копиях.
    ///
    /// ⚠️ Проверяется НА ОКНЕ, а не на модели: подпись и строки — два разных места, и разойтись
    /// они могут только здесь. Число берётся у самих органов (сколько строк в таблице), а не из
    /// того же поля, что и подпись.
    /// </summary>
    [AvaloniaFact]
    public void Счётчик_считает_только_показанные_строки()
    {
        var backups = new BackupWindowStopFlowTests.StubBackups { Entries = Three() };
        var window = new BackupWindow();

        window.Attach(backups);
        window.Show();
        PanelTestStand.Settle();

        try
        {
            Assert.Equal(3, window.ListedCount);
            Assert.Equal(-1, window.SelectedEntry);

            var label = window.FindControl<TextBlock>("ListLabel");
            Assert.NotNull(label);

            Assert.Equal(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupListCountFormat, 3),
                label!.Text);

            // И выбор идёт той же дверью, что у щелчка: номер, подпись и подсветка сходятся.
            window.ChooseEntry(2);

            Assert.Equal(2, window.SelectedEntry);

            var table = window.FindControl<Grid>("EntriesTable");
            Assert.NotNull(table);

            Assert.Equal(
                new[] { 2 },
                BackupTableView.Rows(table!)
                    .Select((row, index) => (Index: index, Marked: row.Classes.Contains(BackupTableView.SelectedClass)))
                    .Where(pair => pair.Marked)
                    .Select(pair => pair.Index)
                    .ToArray());

            // Выбор снят — плана нет ни у одной строки.
            window.ChooseEntry(-1);
            Assert.Equal(-1, window.SelectedEntry);
            Assert.Null(window.Plan);
        }
        finally
        {
            window.Close();
        }
    }
}
