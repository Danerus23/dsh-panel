using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Pricing;
using DshPanel.Shell;

// ⚠️ У имени `PeakWindow` в панели ДВА смысла: окна пика как РАСПИСАНИЕ (`Agents.PeakWindow`)
// и само окно панели (`Views.PeakWindow` — этот файл лежит в том же пространстве имён, и короткое
// имя разрешилось бы в него). Истории нужны РАСПИСАНИЯ со страницы цен, поэтому имя разведено.
using PeakSchedule = DshPanel.Agents.PeakWindow;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «ИСТОРИЯ ЦЕН»: слева записи об изменениях, справа таблица выбранной.
///
/// **Зачем отдельное окно** (решение владельца 28.09.2026): переключатель дат ВНУТРИ «Пиков
/// и тарифов» — не то, что он просил; история живёт своим окном, а дверь в него стоит рядом
/// с «Обновить информацию».
///
/// **Почему список, а не полоса кнопок** (его же слова): «можно использовать не кнопки, а другой
/// вариант переключения между страничками» — записей со временем станет много, и ряд кнопок
/// упёрся бы в ширину окна. Список слева — тот же приём, что у разделов настроек.
///
/// **Что показывает таблица.** Только СТРУКТУРУ записи: имя строки со страницы, цену и её
/// изменение к предыдущей записи. Готового текста в записи нет — слова собирает
/// <see cref="PricingHistoryText"/> в момент показа и на текущем языке панели, поэтому смена
/// языка переводит и старые записи.
///
/// ⚠️ **Тона — те же роли, что у меню значка** (<see cref="PanelLook.Tone"/> →
/// <see cref="Tray.TrayPalette"/>): подорожание красное, удешевление зелёное, «сравнивать не с чем»
/// серое. Цвета не подбираются в окне: они берутся у палитры, где измерены по контрасту.
///
/// ⚠️ **Окно живёт без связки.** Его строят проверки, и построение без <c>Attach</c> не падает:
/// списка нет, таблицы пусты, а строки молчат — выдумывать записи панель не вправе.
/// </summary>
public partial class PricingHistoryWindow : Window
{
    /// <summary>Наименьшая ширина колонки цены — та же, что у таблицы «Стоимость»: те же числа.</summary>
    private const double PriceColumnWidth = PriceTableView.PriceColumnWidth;

    /// <summary>
    /// Наименьшая ширина колонки изменения. 64 — с запасом на «↑ +$0.1» и на стрелку с прочерком,
    /// и кратно шкале панели (8 × 8).
    /// </summary>
    private const double ChangeColumnWidth = 96;

    /// <summary>Пять колонок: единица, цена вне пика, её изменение, цена пика, её изменение.</summary>
    private static readonly GridLength[] Columns =
    {
        new(1, GridUnitType.Star),
        new(16, GridUnitType.Pixel),
        GridLength.Auto,
        GridLength.Auto,
        new(16, GridUnitType.Pixel),
        GridLength.Auto,
        GridLength.Auto,
    };

    private IPricingControl? _pricing;
    private bool _rendering;

    public PricingHistoryWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        Title = PanelStrings.PricingHistoryWindowTitle;
        HeadingText.Text = PanelStrings.PricingHistoryHeading;
        SubtitleText.Text = PanelStrings.PricingHistorySubtitle;

        // Подпись та же, что у окон «О программе» и «Пики и тарифы»: дверь закрытия одна на панель.
        CloseButton.Content = PanelStrings.AboutCloseButton;
        PanelToolTip.Set(CloseButton, PanelStrings.TipCloseAboutButton);
        CloseButton.Click += (_, _) => Close();

        EntriesList.SelectionChanged += (_, _) => RenderSelected();

        // Окно, построенное без связки, обязано показывать себя целиком: так его видят проверки.
        Render();
    }

    /// <summary>
    /// Связать окно с историей цен. История спрашивается у контроллера цен — у цен и у истории
    /// один владелец, поэтому разойтись они не могут. Повторный вызов отписывает прежний
    /// контроллер: иначе окно держало бы его живым и рисовалось бы дважды на каждое изменение.
    /// </summary>
    public void Attach(IPricingControl pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);

        if (_pricing is not null) _pricing.Changed -= OnChanged;

        _pricing = pricing;
        _pricing.Changed += OnChanged;

        Render();
    }

    /// <summary>
    /// Показать всё, что видно. Ничего не читает, кроме готовой истории: ни файла, ни сети.
    ///
    /// **СПИСОК — ПЕРВАЯ ЦЕНА ПЛЮС ТОЧКИ ИЗМЕНЕНИЯ** (решение владельца 29.09.2026, п. 39
    /// <c>docs\DESIGN.md</c>): он больше НЕ пуст, когда панель уже читала цены. Его слова:
    /// *«Я думал она накапливать и хранит данные где-то отдельно и данные она не теряет»* —
    /// пустое окно и означало для человека потерю цен, хотя терять было нечего.
    ///
    /// Выбранная запись НЕ сбрасывается при перерисовке: если человек смотрит «30.09.2026»,
    /// а панель в это время записала новую запись, окно обязано остаться на том, что он выбрал,
    /// а не прыгнуть на самую свежую.
    /// </summary>
    public void Render()
    {
        var history = _pricing?.History ?? PricingHistory.Empty;
        var chosen = EntriesList.SelectedIndex;

        var titles = new List<string>();

        for (var entry = 0; entry < history.Entries; entry++)
        {
            titles.Add(PricingHistoryText.EntryTitle(history, entry));
        }

        _rendering = true;

        EntriesList.ItemsSource = titles;
        EntriesList.SelectedIndex = titles.Count == 0
            ? -1
            : chosen < 0 ? titles.Count - 1 : Math.Min(chosen, titles.Count - 1);

        _rendering = false;

        PricesHeadingText.Text = PanelStrings.PricingHistoryPricesHeading;
        WindowsHeadingText.Text = PanelStrings.PricingHistoryWindowsHeading;

        DroppedText.Text = PricingHistoryText.DroppedText(history);

        RenderSelected();
    }

    /// <summary>Записи в списке — для проверок без экрана (ровно то, что видит человек).</summary>
    public IReadOnlyList<string> EntryTitles =>
        EntriesList.ItemsSource?.Cast<string>().ToList() ?? new List<string>();

    /// <summary>С чем сравнивается выбранная запись — строка на экране.</summary>
    public string Compared => ComparedText.Text ?? string.Empty;

    /// <summary>Пояснение выбранной записи на экране: у первой цены — когда она получена.</summary>
    public string Empty => EmptyText.Text ?? string.Empty;

    /// <summary>
    /// ЧТО ПОКАЗЫВАЕТ ПРАВАЯ ЧАСТЬ у выбранной строки. Строк в списке два вида, и это ровно два
    /// разных рассказа:
    ///
    /// * **первая цена** — свои строки цен и окон, столбцы изменения ПУСТЫ (в них честный «—»:
    ///   сравнивать не с чем), и словами — когда панель её получила;
    /// * **точка изменения** — как было: с чем сравнивается, что изменилось и в какую сторону.
    /// </summary>
    private void RenderSelected()
    {
        if (_rendering) return;

        var history = _pricing?.History ?? PricingHistory.Empty;
        var entry = EntriesList.SelectedIndex;

        if (entry < 0 || entry >= history.Entries)
        {
            // Показывать нечего: либо панель ещё ни разу не читала цены, либо выбора нет.
            // Молчать нельзя — «ещё не читали» это ОТВЕТ, а не пустое окно.
            EntryTitleText.Text = string.Empty;
            ComparedText.Text = string.Empty;
            EmptyText.Text = PricingHistoryText.NeverCheckedText(history);
            PricesNoneText.Text = string.Empty;
            WindowsNoneText.Text = string.Empty;

            FillPrices(Array.Empty<PriceChangeRow>());
            FillWindows(null);

            return;
        }

        EntryTitleText.Text = PricingHistoryText.EntryTitle(history, entry);

        if (history.IsFirstPrice(entry) && history.Anchor is { } first)
        {
            ComparedText.Text = string.Empty;
            EmptyText.Text = PricingHistoryText.FirstPriceNote(history);

            // Обе таблицы — СВОИ данные снимка: и цены, и окна как они были на странице.
            PricesNoneText.Text = string.Empty;
            WindowsNoneText.Text = string.Empty;

            FillPrices(SnapshotRows(first.Rows));
            FillFirstPriceWindows(first.Windows);

            return;
        }

        var change = history.ChangeAt(entry);

        if (change is null)
        {
            EntryTitleText.Text = string.Empty;
            ComparedText.Text = string.Empty;
            EmptyText.Text = PricingHistoryText.NeverCheckedText(history);
            PricesNoneText.Text = string.Empty;
            WindowsNoneText.Text = string.Empty;

            FillPrices(Array.Empty<PriceChangeRow>());
            FillWindows(null);

            return;
        }

        ComparedText.Text = PricingHistoryText.ComparedText(history, history.ChangeIndex(entry));
        EmptyText.Text = string.Empty;

        // «Здесь этого не было» — тоже ответ: пустая таблица без слов читалась бы как поломка.
        PricesNoneText.Text = change.PricesChanged ? string.Empty : PanelStrings.PricingHistoryPricesUnchanged;
        WindowsNoneText.Text = change.WindowsChanged ? string.Empty : PanelStrings.PricingHistoryWindowsUnchanged;

        FillPrices(change.Prices);
        FillWindows(change.WindowsChanged ? change : null);
    }

    /// <summary>
    /// СТРОКИ СНИМКА в виде строк таблицы: «было» пустое, «стало» — цена со страницы. Пустое «было»
    /// и даёт в столбце изменения честный прочерк («сравнивать не с чем»), а не выдуманный ноль.
    /// </summary>
    private static IReadOnlyList<PriceChangeRow> SnapshotRows(IReadOnlyList<PriceRow> rows)
    {
        var converted = new List<PriceChangeRow>();

        foreach (var row in rows)
        {
            converted.Add(new PriceChangeRow(row.Item, string.Empty, row.OffPeak, string.Empty, row.Peak));
        }

        return converted;
    }

    /// <summary>
    /// ТАБЛИЦА ЦЕН ЗАПИСИ: единица страницы, цена и рядом её изменение. Строка заголовков есть
    /// всегда — даже когда менялись только окна: пустая таблица без шапки выглядит сломанной.
    /// </summary>
    private void FillPrices(IReadOnlyList<PriceChangeRow> rows)
    {
        TableGrid.Prepare(PriceTablePanel, Columns);

        TableGrid.AddLine(
            PriceTablePanel,
            0,
            Columns.Length,
            new[]
            {
                (TableGrid.Header(PanelStrings.PriceTableColumnItem), 0),
                (TableGrid.Header(PanelStrings.PeakTableColumnOffPeak, right: true, minWidth: PriceColumnWidth), 2),
                (TableGrid.Header(PanelStrings.PricingHistoryColumnChange, right: true, minWidth: ChangeColumnWidth), 3),
                (TableGrid.Header(PanelStrings.PeakTableColumnPeak, right: true, minWidth: PriceColumnWidth), 5),
                (TableGrid.Header(PanelStrings.PricingHistoryColumnChange, right: true, minWidth: ChangeColumnWidth), 6),
            });

        var line = 1;

        foreach (var row in rows)
        {
            var cheap = TableGrid.Cell(PricingHistoryText.Price(row.OffPeakAfter), right: true, minWidth: PriceColumnWidth);
            var dear = TableGrid.Cell(PricingHistoryText.Price(row.PeakAfter), right: true, minWidth: PriceColumnWidth);

            var cheapChange = Change(row.OffPeakBefore, row.OffPeakAfter);
            var dearChange = Change(row.PeakBefore, row.PeakAfter);

            TableGrid.AddLine(
                PriceTablePanel,
                line++,
                Columns.Length,
                new[]
                {
                    (TableGrid.Cell(row.Item, wrap: true), 0),
                    (cheap, 2),
                    (cheapChange, 3),
                    (dear, 5),
                    (dearChange, 6),
                });
        }
    }

    /// <summary>
    /// Таблица окон пика ЗАПИСИ: какими они были и какими стали — две строки, «Было» и «Стало».
    ///
    /// Окна показываются словами расписания (по местному времени с поясом) — тем же
    /// <see cref="Peak.PeakDecisions.WindowsText"/>, что и в окне пиков: второй набор слов для того
    /// же расписания разошёлся бы с первым.
    ///
    /// Окон в записи нет (менялись только цены) — таблица пуста, и это сказано словами
    /// (<c>WindowsNoneText</c>): «было/стало» без значений читалось бы как «окна пропали».
    /// </summary>
    private void FillWindows(PricingChange? change)
    {
        TableGrid.Prepare(
            WindowsTablePanel,
            new[] { GridLength.Auto, new(TableGrid.Gutter), new(1, GridUnitType.Star) });

        if (change is null) return;

        TableGrid.AddLine(
            WindowsTablePanel,
            0,
            3,
            new[]
            {
                (TableGrid.Header(PanelStrings.PricingHistoryColumnBefore), 0),
                (TableGrid.Cell(PricingHistoryText.WindowsText(change.WindowsBefore), wrap: true), 2),
            });

        TableGrid.AddLine(
            WindowsTablePanel,
            1,
            3,
            new[]
            {
                (TableGrid.Header(PanelStrings.PricingHistoryColumnAfter), 0),
                (TableGrid.Cell(PricingHistoryText.WindowsText(change.WindowsAfter), wrap: true), 2),
            });
    }

    /// <summary>
    /// ОКНА ПЕРВОЙ ЦЕНЫ: одна строка БЕЗ «Было» и «Стало» — сравнивать не с чем, и две такие
    /// подписи обещали бы сравнение, которого нет. Окна названы словами расписания, как и везде.
    /// </summary>
    private void FillFirstPriceWindows(IReadOnlyList<PeakSchedule> windows)
    {
        TableGrid.Prepare(
            WindowsTablePanel,
            new[] { GridLength.Auto, new(TableGrid.Gutter), new(1, GridUnitType.Star) });

        TableGrid.AddLine(
            WindowsTablePanel,
            0,
            3,
            new[] { (TableGrid.Cell(PricingHistoryText.WindowsText(windows), wrap: true), 2) });
    }

    /// <summary>Ячейка изменения: стрелка и разница, покрашенные тоном состояния.</summary>
    private static TextBlock Change(string before, string after)
    {
        var cell = TableGrid.Cell(
            PricingHistoryText.Delta(before, after), right: true, minWidth: ChangeColumnWidth);

        PanelLook.Tone(cell, PricingChangeDecisions.Tone(before, after));

        return cell;
    }

    private void OnChanged() => Render();
}
