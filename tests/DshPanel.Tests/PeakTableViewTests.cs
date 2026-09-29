using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DshPanel.Agents;
using DshPanel.Peak;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТАБЛИЦА «ГРАФИК ПИКОВ» — НАСТОЯЩАЯ ТАБЛИЦА: день недели · пик · вне пика.
///
/// Слова владельца 27.09.2026: *«текст немного сливается в строку»*, и его же замечание, глядя
/// на панель живьём: *«табличка пиков собрана неверно — сделать ДВЕ таблицы»*. Прежде колонки были
/// «день · окна · цена», и расписание с ценой читались как один ответ. Теперь здесь только
/// расписание, а цена — в своей таблице (<c>PriceTableViewTests</c>).
///
/// Проверяется то, что видит человек, и через НАСТОЯЩИЙ построитель (<see cref="PeakTableView"/>),
/// а не через его пересказ: строки, ячейки, разделители, подложки и классы читаются у собранной
/// таблицы. Данные при этом берутся готовыми (<see cref="PeakTable.Rows"/>) — второй расчёт окон
/// завёл бы вторую правду о тарифе.
///
/// ⚠️ КИСТИ (разделитель, подложка дня и подложка окна) проверяются в ПОКАЗАННОМ окне
/// (см. <c>SettingsLookTests</c> и <c>PeakWindowTests</c>): стиль с <c>DynamicResource</c>
/// применяется к органу в дереве, и у таблицы, собранной «в воздухе», кисти ещё нет. Классы —
/// свойства самого построителя, они видны и здесь.
/// </summary>
public class PeakTableViewTests
{
    /// <summary>Таблица, собранная построителем: то же, что делают оба окна панели.</summary>
    private static Grid Table(IReadOnlyList<PeakTableRow> rows, int todayIndex = -1, bool inPeakNow = false)
    {
        var grid = new Grid();
        PeakTableView.Fill(grid, rows, todayIndex, inPeakNow);

        return grid;
    }

    private static IReadOnlyList<PeakTableRow> Rows() => PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 0);

    /// <summary>
    /// ТРИ КОЛОНКИ С ТЕКСТОМ И ДВА ПРОМЕЖУТКА, И ИХ ЗАГОЛОВКИ — СТРОКИ СЛОВАРЯ. Не «какая-то
    /// шапка»: подписи обязаны приходить из <see cref="PanelStrings"/> (правило проекта «ни одного
    /// текста в разметке»), иначе на английском кадре они остались бы русскими.
    /// </summary>
    [AvaloniaFact]
    public void Таблица_это_три_колонки_и_их_заголовки_приходят_из_словаря()
    {
        var table = Table(Rows());

        // Пять колонок: три с текстом и два промежутка по 12 между ними (шкала панели).
        Assert.Equal(5, table.ColumnDefinitions.Count);

        Assert.True(table.ColumnDefinitions[0].Width.IsAuto, "колонка дня не Auto");
        Assert.Equal(12, table.ColumnDefinitions[1].Width.Value);
        Assert.True(table.ColumnDefinitions[2].Width.IsStar, "колонка пика не растягивается");
        Assert.Equal(12, table.ColumnDefinitions[3].Width.Value);
        Assert.True(table.ColumnDefinitions[4].Width.IsStar, "колонка дешёвого времени не растягивается");

        // Обе колонки с окнами — ОДНОЙ ширины: они равноправны, и другая дележка сказала бы
        // человеку, что одно из двух окон важнее.
        Assert.Equal(table.ColumnDefinitions[2].Width.Value, table.ColumnDefinitions[4].Width.Value);

        var lines = PeakTableProbe.Lines(table);
        Assert.NotEmpty(lines);

        var header = (Grid)lines[0].Child!;
        var headerColumns = header.Children.OfType<TextBlock>().Select(cell => Grid.GetColumn(cell)).ToArray();

        Assert.Equal(new[] { 0, 2, 4 }, headerColumns);

        var texts = header.Children.OfType<TextBlock>().OrderBy(cell => Grid.GetColumn(cell))
            .Select(cell => cell.Text)
            .ToArray();

        Assert.Equal(
            new[]
            {
                PanelStrings.PeakTableColumnDay,
                PanelStrings.PeakTableColumnPeak,
                PanelStrings.PeakTableColumnOffPeak,
            },
            texts);

        // Три РАЗНЫЕ подписи: склейка «пик и дешёвое время» в одну колонку дала бы две.
        Assert.Equal(3, texts.Distinct().Count());

        // Заголовок — подсказка (мелкая и серая), но полужирная: она и отделяет шапку от строк.
        foreach (var cell in header.Children.OfType<TextBlock>())
        {
            Assert.Contains("hint", cell.Classes);
            Assert.Equal(FontWeight.SemiBold, cell.FontWeight);
        }
    }

    /// <summary>
    /// СЕМЬ СТРОК — по одной на день недели, — и у КАЖДОЙ день, окна пика и окна дешёвого времени;
    /// содержимое обеих колонок — ровно то, что дало ядро, без второго расчёта.
    /// </summary>
    [AvaloniaFact]
    public void Семь_строк_и_у_каждой_есть_день_пик_и_вне_пика()
    {
        var rows = Rows();
        var table = Table(rows);

        // Восемь линий: строка заголовков и семь дней.
        Assert.Equal(8, PeakTableProbe.Lines(table).Count);

        var cells = PeakTableProbe.Cells(table);
        var days = cells.Skip(1).Select(line => line[0]).ToList();

        Assert.Equal(
            new[]
            {
                PanelStrings.PeakDayMonday,
                PanelStrings.PeakDayTuesday,
                PanelStrings.PeakDayWednesday,
                PanelStrings.PeakDayThursday,
                PanelStrings.PeakDayFriday,
                PanelStrings.PeakDaySaturday,
                PanelStrings.PeakDaySunday,
            },
            days);

        for (var index = 0; index < rows.Count; index++)
        {
            var line = cells[index + 1];

            Assert.Equal(3, line.Length);
            Assert.Equal(rows[index].Day, line[0]);
            Assert.Equal(rows[index].PeakWindows, line[1]);
            Assert.Equal(rows[index].OffPeakWindows, line[2]);

            Assert.False(string.IsNullOrWhiteSpace(line[1]), "у строки нет окон пика");
            Assert.False(string.IsNullOrWhiteSpace(line[2]), "у строки нет окон дешёвого времени");
        }

        // И оба ответа в таблице ЕСТЬ: будни дороже часть суток, выходные дешевле круглые сутки.
        Assert.Contains(cells, line => line[1] == PanelStrings.PeakTableNoPeak);
        Assert.Contains(cells, line => line[2] == PanelStrings.PeakTableOffPeakAllDay);
    }

    /// <summary>
    /// ДЕНЬ БЕЗ ОКОН ПИКА — его дешёвая половина тоном «хорошо», тем же, что у строки тарифа
    /// в меню значка. Это и значит «вне пика весь день»: не пустое место и не «нет данных»,
    /// а цена, которая держится там круглые сутки.
    ///
    /// ⚠️ День С окнами пика не красится вовсе — ни одним тоном: цвет здесь что-то значит,
    /// и зелёная колонка пика означала бы «дорого» зелёным. Проверяется именно ОТСУТСТВИЕ тона.
    /// </summary>
    [AvaloniaFact]
    public void День_без_окон_красит_только_дешёвую_колонку()
    {
        var rows = Rows();
        var table = Table(rows);
        var lines = PeakTableProbe.Blocks(table);

        // Тон — тот же, что у строки тарифа: не «похожий зелёный», а то же решение.
        var expected = PanelLook.ToneClass(TrayStatus.PeakTone(inPeak: false));

        Assert.Equal(PanelLook.GoodClass, expected);

        for (var index = 0; index < rows.Count; index++)
        {
            var line = lines[index + 1];

            var peak = line.Single(cell => Grid.GetColumn(cell) == 2);
            var offPeak = line.Single(cell => Grid.GetColumn(cell) == 4);

            if (rows[index].HasPeak)
            {
                Assert.DoesNotContain(peak.Classes, name => name.StartsWith("tone-", StringComparison.Ordinal));
                Assert.DoesNotContain(offPeak.Classes, name => name.StartsWith("tone-", StringComparison.Ordinal));
                continue;
            }

            Assert.Contains(PanelLook.GoodClass, offPeak.Classes);
            Assert.DoesNotContain(peak.Classes, name => name.StartsWith("tone-", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// СТРОКА СЕГОДНЯШНЕГО ДНЯ ПОМЕЧЕНА КЛАССОМ ПОДЛОЖКИ, И ТОЛЬКО ОНА ОДНА. Подложка «не сегодня»
    /// читалась бы как ещё одно состояние, которого у таблицы нет; а без пометки человек не видит,
    /// где он находится.
    /// </summary>
    [AvaloniaFact]
    public void Строка_сегодняшнего_дня_помечена_подложкой()
    {
        const int today = 2;

        var table = Table(Rows(), todayIndex: today);
        var lines = PeakTableProbe.Lines(table);

        for (var index = 0; index < lines.Count; index++)
        {
            var marked = lines[index].Classes.Contains(PanelLook.PeakTodayRowClass);

            // Первая линия — шапка: она не день и подложки не получает никогда.
            Assert.Equal(index - 1 == today, marked);
        }
    }

    /// <summary>
    /// ЯЧЕЙКА ТЕКУЩЕГО ОКНА ПОМЕЧЕНА ОТДЕЛЬНЫМ КЛАССОМ, и это ТА колонка, в которой человек
    /// находится сейчас: идёт пик — колонка пика, иначе — колонка дешёвого времени.
    ///
    /// Проверяются ОБА состояния: подложка, всегда стоящая на одной колонке, врала бы про вторую
    /// половину суток.
    /// </summary>
    [AvaloniaFact]
    public void Ячейка_текущего_окна_помечена_по_состоянию()
    {
        const int today = 4;

        foreach (var inPeak in new[] { true, false })
        {
            var lines = PeakTableProbe.Blocks(Table(Rows(), todayIndex: today, inPeakNow: inPeak));
            var marked = new List<int>();

            // Шапка (0) не день: её ячейки подложки не получают.
            for (var index = 1; index < lines.Count; index++)
            {
                foreach (var cell in lines[index])
                {
                    if (cell.Classes.Contains(PanelLook.PeakNowCellClass)) marked.Add(index - 1);
                }
            }

            // Ровно ОДНА ячейка во всей таблице — в сегодняшней строке.
            Assert.Equal(new[] { today }, marked);

            var line = lines[today + 1];
            var current = line.Single(cell => cell.Classes.Contains(PanelLook.PeakNowCellClass));

            Assert.Equal(inPeak ? 2 : 4, Grid.GetColumn(current));
        }
    }

    /// <summary>
    /// БЕЗ МОМЕНТА ВРЕМЕНИ ПОДСВЕТКИ НЕТ ВООБЩЕ: таблица, собранная проверкой или окном,
    /// которому «сегодня» неизвестно, обязана выглядеть как обычно, а не подсветить первый день.
    /// </summary>
    [AvaloniaFact]
    public void Без_сегодняшнего_дня_подсветки_нет()
    {
        var table = Table(Rows());
        var lines = PeakTableProbe.Lines(table);

        Assert.All(lines, line => Assert.DoesNotContain(PanelLook.PeakTodayRowClass, line.Classes));

        Assert.All(
            PeakTableProbe.Blocks(table),
            line => Assert.All(
                line,
                cell => Assert.DoesNotContain(PanelLook.PeakNowCellClass, cell.Classes)));
    }

    /// <summary>
    /// РАЗДЕЛИТЕЛЬ ЕСТЬ У КАЖДОЙ СТРОКИ, И ОН ТОНКИЙ: линия в одну точку СНИЗУ, сверху и по бокам
    /// ничего. По ней семь дней и читаются как семь строк, а не как слипшийся столбец.
    /// </summary>
    [AvaloniaFact]
    public void У_каждой_строки_тонкий_разделитель_снизу()
    {
        var table = Table(Rows());
        var lines = PeakTableProbe.Lines(table);

        Assert.Equal(8, lines.Count);

        foreach (var line in lines)
        {
            Assert.Contains(PeakTableView.SeparatorClass, line.Classes);

            // ⚠️ Числа здесь — ЛИТЕРАЛЫ, а не константы построителя. Проверка, сравнивающая
            // константу с самой собой, не может упасть: поменяй `SeparatorThickness` на 0 —
            // и она осталась бы зелёной, а разделители исчезли бы у человека. Тонкая линия — это
            // «1», отступ снизу — «4» (шкала панели), и оба числа названы здесь прямо.
            Assert.Equal(1, line.BorderThickness.Bottom);
            Assert.Equal(0, line.BorderThickness.Top);
            Assert.Equal(0, line.BorderThickness.Left);
            Assert.Equal(0, line.BorderThickness.Right);

            // Отступ снизу из шкалы панели: без него разделитель прилипает к тексту.
            Assert.Equal(4, line.Padding.Bottom);
            Assert.Equal(0, line.Padding.Top);

            // Разделитель — свойство строки, а не отдельный орган между строками: внутри неё
            // ровно три ячейки, и ни одной лишней.
            var inner = (Grid)line.Child!;
            Assert.Equal(3, inner.Children.OfType<TextBlock>().Count());
        }
    }

    /// <summary>
    /// ПОДПИСЬ КОЛОНКИ ВЫРОВНЕНА ТАК ЖЕ, КАК ЕЁ ЗНАЧЕНИЯ. Колонка, у которой шапка стоит слева,
    /// а значения справа, читается криво, и заметить это на одной строке нельзя — потому сравнение
    /// идёт по ВСЕМ строкам таблицы.
    /// </summary>
    [AvaloniaFact]
    public void Подпись_колонки_выровнена_так_же_как_её_значения()
    {
        var table = Table(Rows());
        var blocks = PeakTableProbe.Blocks(table);

        // Ячейки каждой строки в порядке колонок: 0 — день, 1 — пик, 2 — вне пика.
        TextBlock CellOf(TextBlock[] line, int column) =>
            line.Single(cell => Grid.GetColumn(cell) == column * 2);

        var header = blocks[0];

        for (var column = 0; column < 3; column++)
        {
            Assert.Equal(TextAlignment.Left, CellOf(header, column).TextAlignment);
        }

        for (var index = 1; index < blocks.Count; index++)
        {
            for (var column = 0; column < 3; column++)
            {
                Assert.Equal(
                    CellOf(header, column).TextAlignment,
                    CellOf(blocks[index], column).TextAlignment);
            }
        }
    }

    /// <summary>
    /// СЕТКА ОЧИЩАЕТСЯ, А НЕ ДОПОЛНЯЕТСЯ: её пересобирают при смене агента и при каждом разборе
    /// страницы, и накопление строк дало бы таблицу из двух агентов сразу — четырнадцать дней
    /// в неделе.
    /// </summary>
    [AvaloniaFact]
    public void Повторное_наполнение_заменяет_таблицу_а_не_дополняет_её()
    {
        var table = Table(Rows());

        PeakTableView.Fill(table, PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 180));

        Assert.Equal(8, PeakTableProbe.Lines(table).Count);
        Assert.Equal(5, table.ColumnDefinitions.Count);

        // И содержимое — именно ВТОРОЕ наполнение, а не первое.
        Assert.Equal(
            PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 180).Select(row => row.PeakWindows),
            PeakTableProbe.Cells(table).Skip(1).Select(line => line[1]));
    }
}

/// <summary>
/// Разбор собранной таблицы на строки и ячейки — для проверок, которым нужен не рисованный кадр,
/// а устройство таблицы. Живёт здесь, а не в общей оснастке (<c>PanelTestStand</c>): та чужая,
/// и её правят другие рабочие.
///
/// Устройство одно на ОБЕ таблицы панели: строка — <c>Border</c> с разделителем снизу, а внутри
/// неё сетка с ячейками. Поэтому разбор годится и для «Графика пиков», и для «Стоимости».
/// </summary>
internal static class PeakTableProbe
{
    /// <summary>Строки таблицы: каждая — <c>Border</c> с разделителем снизу.</summary>
    public static IReadOnlyList<Border> Lines(Grid table) =>
        table.Children.OfType<Border>().ToList();

    /// <summary>Ячейки построчно, слева направо.</summary>
    public static IReadOnlyList<string[]> Cells(Grid table) =>
        Blocks(table).Select(line => line.Select(cell => cell.Text ?? string.Empty).ToArray()).ToList();

    /// <summary>Сами органы ячеек — когда проверке нужен класс или тон, а не только текст.</summary>
    public static IReadOnlyList<TextBlock[]> Blocks(Grid table) =>
        Lines(table)
            .Select(line => ((Grid)line.Child!).Children
                .OfType<TextBlock>()
                .OrderBy(cell => Grid.GetColumn(cell))
                .ToArray())
            .ToList();
}
