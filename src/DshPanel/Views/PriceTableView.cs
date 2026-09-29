using Avalonia;
using Avalonia.Controls;
using DshPanel.Pricing;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ТАБЛИЦА 2 — «СТОИМОСТЬ»: Единица · Вне пика · Пик. ОДИН построитель на ДВА окна — ровно так же,
/// как у таблицы графика (<see cref="PeakTableView"/>), и по той же причине: две таблицы одного
/// тарифа, написанные порознь, расходятся на первой же правке.
///
/// Замечание владельца 27.09.2026: «табличка пиков собрана неверно — сделать ДВЕ таблицы».
/// Эта отвечает на «сколько стоит»: цена за единицу, как её печатает СТРАНИЦА ЦЕН. Названия
/// единиц («1M INPUT TOKENS (CACHE HIT)») — СЛОВА САМОЙ СТРАНИЦЫ: переводить их значило бы
/// выдумывать названия, которых на странице нет.
///
/// ⚠️ **Валюта — по языку панели**, а числа — СО СТРАНИЦЫ ТОГО ЖЕ языка
/// (<see cref="PricingDecisions.Url"/>): английская страница отдаёт доллары, китайская — юани,
/// и пересчитывать одну в другую по курсу панель не вправе (курса она не знает). Знак ставит
/// разбор (<see cref="PricingPage.PriceRows"/>), здесь он только показывается.
///
/// ⚠️ **Текста в разметке нет и здесь его быть не может**: подписи колонок приходят из словаря,
/// строки — готовыми (<see cref="PricingResult.Rows"/>).
/// </summary>
public static class PriceTableView
{
    /// <summary>
    /// Ширины пяти колонок: единица, промежуток, цена вне пика, промежуток, цена пика.
    ///
    /// Колонка единицы — <c>Star</c> (её подпись самая длинная и переносится), а обе цены —
    /// <c>Auto</c> и прижаты вправо: числа читаются столбиком, и столбик обязан быть один.
    /// </summary>
    private static readonly GridLength[] Columns =
    {
        new(1, GridUnitType.Star),
        new(TableGrid.Gutter),
        GridLength.Auto,
        new(TableGrid.Gutter),
        GridLength.Auto,
    };

    /// <summary>
    /// НАИМЕНЬШАЯ ШИРИНА КОЛОНКИ ЦЕНЫ, ОДНА НА ВСЕ СТРОКИ И НА ШАПКУ.
    ///
    /// ⚠️ Без неё КОЛОНКИ ЦЕН В РАЗНЫХ СТРОКАХ РАЗНОЙ ШИРИНЫ — и это не мелочь: у каждой строки
    /// своя сетка (строка — рамка с разделителем снизу), а колонка <c>Auto</c> меряется
    /// по СВОЕМУ содержимому. В шапке стоит «Вне пика», в строке — «$0.003»; шапка шире, и её
    /// колонка шире — значит числа встают левее подписи, а не под ней. Ровно эта беда была у дня
    /// недели в таблице графика (<see cref="PeakTableView.DayColumnWidth"/>), и лечится она так же:
    /// общей наименьшей шириной.
    ///
    /// 72 — с запасом на самое длинное из трёх словарей («Вне пика» / «Off-peak» / «非高峰»)
    /// и на цену вроде «¥27.0», и кратно шкале панели: 24 + 24 + 24.
    /// </summary>
    public const double PriceColumnWidth = 72;

    /// <summary>
    /// Наполнить сетку таблицей цен: строка заголовков и по строке на единицу страницы.
    ///
    /// Пустой список даёт таблицу из ОДНОЙ шапки — и это честно: почему цен нет, словами говорит
    /// строка под таблицей (<see cref="IPricingControl.PriceSourceText"/>), а подставленные нули
    /// в ячейках читались бы как настоящая цена.
    /// </summary>
    public static void Fill(Grid target, IReadOnlyList<PriceRow> rows)
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
                (TableGrid.Header(PanelStrings.PriceTableColumnItem), 0),
                (TableGrid.Header(PanelStrings.PeakTableColumnOffPeak, right: true, minWidth: PriceColumnWidth), 2),
                (TableGrid.Header(PanelStrings.PeakTableColumnPeak, right: true, minWidth: PriceColumnWidth), 4),
            });

        foreach (var row in rows)
        {
            TableGrid.AddLine(
                target,
                line++,
                Columns.Length,
                new[]
                {
                    (TableGrid.Cell(row.Item, wrap: true), 0),
                    (TableGrid.Cell(row.OffPeak, right: true, minWidth: PriceColumnWidth), 2),
                    (TableGrid.Cell(row.Peak, right: true, minWidth: PriceColumnWidth), 4),
                });
        }
    }
}
