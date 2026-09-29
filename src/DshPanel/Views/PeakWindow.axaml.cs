using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Balance;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Tray;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «ПИКИ И ТАРИФЫ» — когда дешевле, сколько стоит и откуда взяты окна.
///
/// Зачем отдельное окно (решение дирижёра, названное владельцу). Прежде всё это жило в разделе
/// «Баланс и тариф» окна настроек и в главном окне. Слова владельца 27.09.2026: *«не хватает формы
/// запроса окон пиков и тарифов, там было сделано удобно чтобы можно было прочитать»* — читаемая
/// форма это таблица по дням недели, состояние, цена и источник. В карточку главного окна она
/// не влезает, не превратив её в простыню; в настройках она спрятана за списком разделов.
///
/// **ДВЕ ТАБЛИЦЫ** (замечание владельца 27.09.2026: «табличка пиков собрана неверно — сделать
/// ДВЕ таблицы»): «График пиков» (день · пик · вне пика) и «Стоимость» (единица · вне пика · пик).
/// Обе собирает код из ГОТОВЫХ ответов — <see cref="PeakTableView"/> и <see cref="PriceTableView"/>,
/// те же построители, что у раздела настроек. Двух таблиц одного тарифа в панели быть не должно.
///
/// ⚠️ **Что показано и откуда.** Живое состояние (<c>сейчас</c> и <c>когда переключение</c>) —
/// у контроллера баланса, то есть про АКТИВНОГО агента; расписание — из профиля ТОГО ЖЕ агента
/// (<see cref="IBalanceControl.Agent"/>); цены — у контроллера цен (<see cref="IPricingControl"/>),
/// и он один знает, читали ли их вообще. Так таблицы и состояние не могут разойтись: у каждой
/// пары — один источник.
///
/// ⚠️ **Окно живёт без связки.** Его строят проверки, и построение без <see cref="Attach"/>
/// обязано не падать: строки про живое состояние и про цены остаются ПУСТЫМИ, а не выдуманными.
/// Пустая строка честна («панель ещё не знает»), подставленный чужой агент — нет.
/// </summary>
public partial class PeakWindow : Window
{
    private IBalanceControl? _balance;
    private IPricingControl? _pricing;
    private IReadOnlyList<PeakTableRow> _rows = Array.Empty<PeakTableRow>();

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c> (та же грабля, что у главного окна, настроек и копий, `docs\STACK.md` §6).
    /// </summary>
    public PeakWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        Title = PanelStrings.PeakWindowTitle;
        HeadingText.Text = PanelStrings.PeakWindowHeading;
        SubtitleText.Text = PanelStrings.PeakWindowSubtitle;

        // Подпись та же, что у окна «О программе»: дверь закрытия одна на панель, и второй
        // набор слов для неё («Готово», «Закрыть окно») разошёлся бы с ней на первой правке.
        CloseButton.Content = PanelStrings.AboutCloseButton;
        PanelToolTip.Set(CloseButton, PanelStrings.TipCloseAboutButton);
        CloseButton.Click += (_, _) => Close();

        // ДВЕРЬ РУЧНОГО РАЗБОРА СТРАНИЦЫ ЦЕН (замечание владельца 27.09.2026: кнопка «Обновить
        // информацию»). Автоматически панель разбирает страницу при запуске и не чаще раза
        // в сутки — этой кнопкой человек говорит «сейчас», и это же его подтверждение для окон
        // пика со страницы. Подсказка ставится в Render: у недоступной кнопки она называет
        // причину, а причина зависит от состояния контроллера.
        RefreshPriceButton.Content = PanelStrings.PeakRefreshButton;
        RefreshPriceButton.Click += (_, _) => _pricing?.Refresh();

        // ДВЕРЬ В ИСТОРИЮ ЦЕН (решение владельца 28.09.2026: «рядом с кнопкой „Обновить
        // информацию“ должна быть где-то кнопка с историей», и это ОТДЕЛЬНОЕ окно). Окно истории
        // строится и живёт в своём слоте связки — окно пиков о нём ничего не знает и знать
        // не должно: оно только поднимает просьбу, как главное окно поднимает просьбу о пиках.
        HistoryButton.Content = PanelStrings.PricingHistoryButton;
        PanelToolTip.Set(HistoryButton, PanelStrings.TipPricingHistoryButton);
        HistoryButton.Click += (_, _) => HistoryRequested?.Invoke();

        // Окно, построенное без связки, обязано показывать себя целиком: так его видят проверки,
        // и так его мог бы увидеть человек, если связка ещё не успела подойти.
        Render();
    }

    /// <summary>
    /// Просьба открыть историю цен — её поднимает кнопка «История», а принимает связка
    /// (<see cref="Shell.PanelShell.OpenPricingHistory"/>). Отдельным событием, а не вызовом окна
    /// из окна: у панели на показ вспомогательного окна ОДНА дверь (<see cref="Shell.SingleWindowSlot"/>),
    /// и второе окно истории из-за двойного щелчка появиться не должно.
    /// </summary>
    public event Action? HistoryRequested;

    /// <summary>
    /// Связать окно с балансом и окнами пика АКТИВНОГО агента: взять живое состояние, нарисовать
    /// таблицу и перерисовываться по каждому изменению (контроллер сам говорит, когда пересчитал
    /// пик или получил баланс, — окну нечего спрашивать по часам).
    ///
    /// Повторный вызов (связка переехала на другой контроллер) отписывает прежний: иначе окно
    /// держало бы его живым до конца процесса и рисовалось бы дважды на каждое изменение.
    /// </summary>
    public void Attach(IBalanceControl balance, IPricingControl? pricing = null)
    {
        ArgumentNullException.ThrowIfNull(balance);

        if (_balance is not null) _balance.Changed -= OnBalanceChanged;
        if (_pricing is not null) _pricing.Changed -= OnBalanceChanged;

        _balance = balance;
        _balance.Changed += OnBalanceChanged;

        _pricing = pricing;
        if (_pricing is not null) _pricing.Changed += OnBalanceChanged;

        Render();
    }

    /// <summary>Пересчитать и показать всё, что видно. Ничего не читает, кроме готового состояния.</summary>
    public void Render()
    {
        // Момент спрашивается ОДИН раз и на всё: и смещение часового пояса, и сегодняшний день
        // считаются от него. Двух «сейчас» в одном построении быть не должно — на границе суток
        // подсветка уехала бы на соседний день, а часы остались бы прежними.
        var now = DateTimeOffset.Now;
        var offset = PeakDecisions.OffsetMinutes(now);

        // Заголовки и пояснения есть ВСЕГДА: часовой пояс, «где дороже» и название таблицы
        // не зависят ни от агента, ни от связки — это свойства самих часов и самого тарифа.
        TableTitleText.Text = PanelStrings.PeakTableTitle;
        TableHeadingText.Text = PeakTable.Heading(offset);
        LegendText.Text = PeakTable.Legend();

        RenderPrices();

        if (_balance is null)
        {
            // Связки нет — про живое состояние панель не знает НИЧЕГО. Пустые строки честнее
            // подставленных: «пик идёт» про агента, которого в этом окне нет, — это ложь,
            // а пустую строку человек прочитает как «ещё не спрашивали».
            NowText.Text = string.Empty;
            NextText.Text = string.Empty;
            NowLineText.Text = string.Empty;
            PeakSourceText.Text = string.Empty;

            PanelLook.Tone(NowText, TrayTone.Neutral);

            _rows = Array.Empty<PeakTableRow>();
            PeakTableView.Fill(PeakTablePanel, _rows);

            return;
        }

        var agent = _balance.Agent;
        var state = _balance.Peak;

        NowText.Text = PeakTable.NowText(state);
        NextText.Text = PeakTable.NextText(state);
        NowLineText.Text = PeakTable.NowLine(state);
        PeakSourceText.Text = PeakDecisions.SourceText(agent, offset);

        // Тариф — цветом: пик дороже, вне пика вдвое дешевле. Тон — у того же решения, что красит
        // строку тарифа в меню значка и строку состояния в настройках.
        PanelLook.Tone(NowText, TrayStatus.PeakTone(state.InPeak));

        _rows = PeakTable.Rows(agent, offset);
        PeakTableView.Fill(PeakTablePanel, _rows, PeakTable.TodayIndex(now), state.InPeak);
    }

    /// <summary>
    /// Таблица «Стоимость» и подпись под ней: откуда взяты цены, а когда их нет — почему.
    ///
    /// Связки с контроллером цен может не быть (окно строят проверки) — тогда таблица остаётся
    /// одной шапкой, а строка под ней молчит: выдумать ей причину панель не вправе.
    /// </summary>
    private void RenderPrices()
    {
        var rows = _pricing is null ? Array.Empty<PriceRow>() : _pricing.Result.Rows;

        PriceTitleText.Text = _pricing is null ? PanelStrings.PriceTableTitle : _pricing.PriceHeadingText;
        PriceSourceText.Text = _pricing is null ? string.Empty : _pricing.PriceSourceText;

        PriceTableView.Fill(PriceTablePanel, rows);

        // Подсказка кнопки называет СОСТОЯНИЕ, а не только действие: у недоступной — причину,
        // у занятой — что разбор уже идёт (PanelToolTip показывает подсказку и у недоступной).
        var busy = _pricing?.Busy == true;
        var allowed = _pricing?.Allowed ?? false;

        RefreshPriceButton.IsEnabled = allowed && !busy;
        PanelToolTip.Set(
            RefreshPriceButton,
            !allowed
                ? PanelStrings.TipPeakRefreshLocked
                : busy
                    ? PanelStrings.TipPeakRefreshBusy
                    : PanelStrings.TipPeakRefreshButton);
    }

    /// <summary>Строки таблицы графика, которые сейчас на экране, — для проверок без экрана.</summary>
    public IReadOnlyList<PeakTableRow> Rows => _rows;

    /// <summary>Крупная строка состояния на экране (пусто, пока окно не связано с балансом).</summary>
    public string StateText => NowText.Text ?? string.Empty;

    /// <summary>Строка «откуда взяты окна» на экране (пусто, пока окно не связано с балансом).</summary>
    public string SourceText => PeakSourceText.Text ?? string.Empty;

    private void OnBalanceChanged() => Render();
}
