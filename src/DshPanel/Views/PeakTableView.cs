using Avalonia;
using Avalonia.Controls;
using DshPanel.Peak;
using DshPanel.Shell;
using DshPanel.Tray;

namespace DshPanel.Views;

/// <summary>
/// ТАБЛИЦА 1 — «ГРАФИК ПИКОВ»: День · Пик · Вне пика. ОДИН построитель на ДВА окна: раздел
/// «Баланс и тариф» в настройках и окно «Пики и тарифы».
///
/// Зачем один, а не по построителю в каждом окне. Слова владельца 27.09.2026: *«текст немного
/// сливается в строку»* — таблица из двух колонок читалась как склейка. И ровно поэтому
/// построитель один: два экземпляра одной таблицы, написанные порознь, расходятся на первой же
/// правке — в одном окне колонка появится, в другом нет, и человек увидит два разных ответа
/// про один и тот же тариф.
///
/// ⚠️ **Таблица переделана 27.09.2026 по замечанию владельца** («табличка пиков собрана неверно —
/// сделать ДВЕ таблицы»). Прежде колонки были «день · окна · цена», и расписание с ценой жили
/// в одной таблице. Теперь здесь только РАСПИСАНИЕ: окна дорогого времени и окна дешёвого —
/// РАЗНЫМИ колонками. Цена — во второй таблице (<see cref="PriceTableView"/>).
///
/// **Ничего не считает и не выдумывает.** Строки приходят готовыми (<see cref="PeakTable.Rows"/>):
/// дни, окна по местному времени и признак «есть ли в этот день пик». Здесь — только раскладка
/// по колонкам, подсветка и тонкие разделители строк.
///
/// ⚠️ **Текста в разметке нет и здесь его быть не может.** Подписи колонок приходят из словаря
/// (<see cref="PanelStrings"/>), а таблицу собирает код: разметка панели не содержит ни одного
/// литерала (ворота <c>SourceStringsTests.В_разметке_нет_текста</c>).
/// </summary>
public static class PeakTableView
{
    /// <summary>
    /// Класс строки таблицы. По нему <c>App.axaml</c> ставит кисть разделителя — из ТЕМЫ
    /// (<c>DynamicResource</c>), а не подобранную в окне: цвет, выбранный на светлой теме,
    /// на тёмной читается иначе, и заметить это на одной теме нельзя.
    /// </summary>
    public const string SeparatorClass = "peakRow";

    /// <summary>Ширина разделителя снизу строки: тонкая линия, а не рамка.</summary>
    public const double SeparatorThickness = 1;

    /// <summary>Внутренний отступ строки снизу: без него разделитель прилипает к тексту (шкала 4/8/12/16/24).</summary>
    public const double RowBottomPadding = 4;

    /// <summary>
    /// Ширина КОЛОНКИ ДНЯ — ОДНА на все строки, и это не прихоть, а условие выравнивания.
    ///
    /// У каждой строки своя сетка: строка — это рамка с разделителем снизу, а внутри неё ячейки.
    /// Колонки <c>Auto</c> в разных сетках совпадают ТОЛЬКО тогда, когда совпадает их содержимое,
    /// а содержимое разное: в шапке стоит «День», в строках — «пн». Без общей ширины шапка
    /// «Пик» уезжала бы вправо от самих окон ровно на ширину слова «День», то есть заголовок
    /// стоял бы не над своей колонкой.
    ///
    /// 56 — с запасом на самые длинные названия дней в трёх словарях (китайское «星期一» вдвое
    /// с лишним шире русского «пн») и кратно шкале панели: 24 + 16 + 16.
    /// </summary>
    public const double DayColumnWidth = 56;

    /// <summary>
    /// Ширины пяти колонок: день, промежуток, окна пика, промежуток, окна дешёвого времени.
    ///
    /// ⚠️ Обе колонки с окнами — <c>Star</c>, и поровну: они равноправны, и любая другая дележка
    /// сказала бы человеку, что одно из двух окон важнее. Перенос строк им обязателен: в сутках
    /// дешёвого времени бывает три отрезка, и в узком окне они не влезают в одну строку.
    /// </summary>
    private static readonly GridLength[] Columns =
    {
        GridLength.Auto,
        new(TableGrid.Gutter),
        new(1, GridUnitType.Star),
        new(TableGrid.Gutter),
        new(1, GridUnitType.Star),
    };

    /// <summary>
    /// Наполнить сетку таблицей: строка заголовков колонок и по строке на каждый день.
    ///
    /// <paramref name="todayIndex"/> — номер строки сегодняшнего дня (см.
    /// <see cref="PeakTable.TodayIndex"/>); <c>-1</c> значит «сегодня не подсвечиваем» — так таблицу
    /// собирают проверки, у которых нет момента времени.
    ///
    /// <paramref name="inPeakNow"/> — идёт ли пик прямо сейчас: по нему выбирается, какая из двух
    /// колонок сегодняшней строки — ТЕКУЩЕЕ окно, и та получает подложку поплотнее.
    /// </summary>
    public static void Fill(Grid target, IReadOnlyList<PeakTableRow> rows, int todayIndex = -1, bool inPeakNow = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rows);

        TableGrid.Prepare(target, Columns);

        var line = 0;

        TableGrid.AddLine(
            target,
            line++,
            Columns.Length,
            new[]
            {
                (TableGrid.Header(PanelStrings.PeakTableColumnDay, minWidth: DayColumnWidth), 0),
                (TableGrid.Header(PanelStrings.PeakTableColumnPeak), 2),
                (TableGrid.Header(PanelStrings.PeakTableColumnOffPeak), 4),
            });

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];

            var day = TableGrid.Cell(row.Day, minWidth: DayColumnWidth);
            var peak = TableGrid.Cell(row.PeakWindows, wrap: true);
            var offPeak = TableGrid.Cell(row.OffPeakWindows, wrap: true);

            // День БЕЗ окон пика — его дешёвая половина тоном «хорошо», тем же, что у строки тарифа
            // в меню значка: «вне пика весь день» — это не «нет данных», а цена, которая держится
            // там круглые сутки. Тон тот же, что у подложек, и берётся у того же решения
            // (PanelLook → TrayPalette), а не подбирается в окне.
            if (!row.HasPeak) PanelLook.Tone(offPeak, TrayTone.Good);

            // ТЕКУЩЕЕ ОКНО — заметная подложка. Она на ячейке той колонки, в которой человек
            // находится сейчас, и только в сегодняшней строке: подложка «не сегодня» читалась бы
            // как ещё одно состояние, которого у таблицы нет.
            if (index == todayIndex)
            {
                var current = inPeakNow ? peak : offPeak;
                current.Classes.Add(PanelLook.PeakNowCellClass);
            }

            TableGrid.AddLine(
                target,
                line++,
                Columns.Length,
                new[] { (day, 0), (peak, 2), (offPeak, 4) },
                index == todayIndex ? PanelLook.PeakTodayRowClass : null);
        }
    }
}
