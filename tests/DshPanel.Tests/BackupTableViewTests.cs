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
                readable: false, error: "описи нет"),
        };

        var (window, table, _) = Stand(broken);

        try
        {
            var cells = Cells(BackupTableView.Rows(table)[0]);

            Assert.Contains("описи нет", cells[4], StringComparison.Ordinal);
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
