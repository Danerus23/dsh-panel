using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ОКНО ПЕРВОГО ЗАПУСКА — «чего нет и что делать», один раз на профиль (A5, вариант «а»).
///
/// **От какого случая (находка C1 N-01, блокер).** Незнакомец ставил панель на чистую машину,
/// видел значок в трее и больше ничего: ни сервера DSH, ни ключа модели, ни объяснения, почему
/// ничего не работает. Полный мастер первой настройки (10 ч) — следующий цикл; здесь маленькое окно,
/// которое говорит ровно то, чего не хватает, и даёт ОДИН путь к исправлению.
///
/// **Чего окно не делает:** ничего не ставит, не заводит ключ, не поднимает сервер. Двери у него —
/// «открыть раздел „Обновление“» (там кнопка «Установить») и «Показать панель» (там карточка сервера
/// с причиной и кнопками); обе уводит вызывающий, окно только сообщает о щелчке.
///
/// ⚠️ **Строк не больше двух, и каждая — ответ на свой вопрос.** Логика строк живёт в
/// <see cref="FirstRunDecisions"/> и проверяется без окна; здесь только показ.
///
/// ⚠️ **Клавиатура — по норме диалогов Windows:** безопасное действие справа («Закрыть»),
/// <c>Esc</c> закрывает (<c>IsCancel</c>), ни одна кнопка не помечена <c>IsDefault</c> —
/// у окна нет «действия по Enter», и Enter не уводит человека в настройки.
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>Опознание окна для проверок и читалки (имя окна — его заголовок).</summary>
    public const string AutomationIdValue = "first-run";

    private IFirstRunFacts? _facts;

    public FirstRunWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи ставим СРАЗУ, а не только по просьбе: окно, построенное без наполнения,
        // не должно выглядеть пустым — так его видят проверки и так его мог бы увидеть человек.
        Title = PanelStrings.FirstRunTitle;
        IntroText.Text = PanelStrings.FirstRunIntro;
        RecheckButton.Content = PanelStrings.FirstRunRecheckButton;
        CloseButton.Content = PanelStrings.FirstRunCloseButton;
        NeedsNote.Text = PanelStrings.FirstRunNeedsNote;

        PanelToolTip.Set(RecheckButton, PanelStrings.TipFirstRunRecheck);
        PanelToolTip.Set(CloseButton, PanelStrings.TipFirstRunClose);

        RecheckButton.Click += (_, _) => Recheck();
        CloseButton.Click += (_, _) => Close();
        DoorButton.Click += (_, _) => OpenDoor();

        // ОПОЗНАНИЕ ДЛЯ ПРОГОНА, а не для человека: по нему проверка находит окно и его органы.
        AutomationProperties.SetAutomationId(this, AutomationIdValue);
        AutomationProperties.SetAutomationId(RowOnePanel, "first-run-row-one");
        AutomationProperties.SetAutomationId(RowTwoPanel, "first-run-row-two");

        // До первой сверки окно не обещает строк, которых в нём нет.
        RowOnePanel.IsVisible = false;
        RowTwoPanel.IsVisible = false;
        DoorButton.IsVisible = false;
    }

    /// <summary>
    /// ЧЕЛОВЕК ПОПРОСИЛ ОТКРЫТЬ РАЗДЕЛ «ОБНОВЛЕНИЕ» — там кнопка «Установить».
    /// Окно само ничего не открывает и не решает: окна друг поверх друга не ставим, поэтому
    /// вызывающий сначала закрывает это окно, а потом открывает раздел.
    /// </summary>
    public event Action? UpdateSectionRequested;

    /// <summary>ЧЕЛОВЕК ПОПРОСИЛ ПОКАЗАТЬ ОКНО ПАНЕЛИ — там причина и кнопки запуска сервера.</summary>
    public event Action? PanelRequested;

    /// <summary>Строки, которые окно показывает сейчас (для журнала и проверок).</summary>
    public IReadOnlyList<FirstRunRowKind> Rows { get; private set; } = Array.Empty<FirstRunRowKind>();

    /// <summary>
    /// СТРОКА ЖУРНАЛА: окно показано и С ЧЕМ — названиями строк, словами словаря. Ни путей, ни
    /// значения ключа здесь нет и быть не может: в <c>{0}</c> уходят только заголовки строк.
    /// </summary>
    public string ShownLogLine => string.Format(
        CultureInfo.CurrentCulture,
        PanelStrings.FirstRunShownLogFormat,
        string.Join(", ", Rows.Select(TitleOf)));

    /// <summary>Заголовок строки по её виду — одно место и для окна, и для строки журнала.</summary>
    internal static string TitleOf(FirstRunRowKind kind) => kind switch
    {
        FirstRunRowKind.EngineMissing => PanelStrings.FirstRunServerMissingEngineTitle,
        FirstRunRowKind.ServerNotStarted => PanelStrings.FirstRunServerNotStartedTitle,
        FirstRunRowKind.KeyMissing => PanelStrings.FirstRunKeyMissingTitle,
        _ => PanelStrings.FirstRunKeyUnreadableTitle,
    };

    /// <summary>Что сказала последняя проверка: «ничего не изменилось» или пусто.</summary>
    public string RecheckText => RecheckLine.Text ?? string.Empty;

    /// <summary>
    /// ДАТЬ ОКНУ ДВЕРЬ ФАКТОВ и показать то, что она говорит СЕЙЧАС. Дверь, а не запись фактов:
    /// кнопка «Проверить снова» обязана спросить истину заново, а не перерисовать прежний ответ.
    /// </summary>
    public void Attach(IFirstRunFacts facts)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        Refresh();
    }

    /// <summary>Перечитать факты и перерисовать строки. Возвращает то, что показано.</summary>
    public IReadOnlyList<FirstRunRowKind> Refresh()
    {
        var facts = _facts?.Read() ?? FirstRunFacts.Unknown;

        Rows = FirstRunDecisions.Rows(facts);
        Render(Rows, facts);

        return Rows;
    }

    /// <summary>
    /// НАЖАТИЕ «ПРОВЕРИТЬ СНОВА»: перечитать и сказать СЛОВАМИ, что изменилось. Строка результата
    /// объявляется читалке (<c>LiveSetting="Polite"</c>), поэтому она говорит сама за себя, без
    /// «нажал — не нажал» в контексте.
    ///
    /// ⚠️ Слов «изменилось, но не всё» в таблице слов НЕТ, и выдумывать их инженеру нельзя: в этом
    /// случае строка остаётся пустой, а строки окна говорят сами. Это названо в отчёте, а не
    /// замаскировано чужой фразой.
    /// </summary>
    internal void Recheck()
    {
        var before = Rows;

        var after = Refresh();

        RecheckLine.Text = after.Count == 0
            ? PanelStrings.FirstRunRecheckOkLine
            : Same(before, after)
                ? PanelStrings.FirstRunRecheckSameLine
                : string.Empty;
    }

    private static bool Same(IReadOnlyList<FirstRunRowKind> left, IReadOnlyList<FirstRunRowKind> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    /// <summary>Показать строки и НАЗВАТЬ ИХ СМЫСЛ СЛОВАМИ СЛОВАРЯ — ни одного литерала здесь.</summary>
    private void Render(IReadOnlyList<FirstRunRowKind> rows, FirstRunFacts facts)
    {
        // ПОРЯДОК СТРОК — по зависимости (сначала сервер DSH, потом ключ). Порядок задаёт чистая
        // функция, а не этот член: он лишь раскладывает готовый список по двум местам.
        FirstRunRowKind? serverRow = null;
        FirstRunRowKind? keyRow = null;

        foreach (var row in rows)
        {
            if (row is FirstRunRowKind.EngineMissing or FirstRunRowKind.ServerNotStarted && serverRow is null)
                serverRow = row;
            else if (row is FirstRunRowKind.KeyMissing or FirstRunRowKind.KeyUnreadable && keyRow is null)
                keyRow = row;
        }

        RowOnePanel.IsVisible = serverRow is not null;
        RowTwoPanel.IsVisible = keyRow is not null;

        Fill(RowOneTitle, RowOneBody, RowOneHint, serverRow, facts);
        Fill(RowTwoTitle, RowTwoBody, RowTwoHint, keyRow, facts);

        // ДВЕРЬ: у состояния «сервер не поднялся» путь — окно панели; у остальных — раздел
        // «Обновление» (там кнопка «Установить»). Своего ключа для второй кнопки не заводим:
        // это тот же пункт меню значка (`ShowPanelText`), и одна вещь — одно имя.
        var panelDoor = serverRow == FirstRunRowKind.ServerNotStarted;

        DoorButton.Content = panelDoor ? PanelStrings.ShowPanelText : PanelStrings.FirstRunDoorUpdate;
        PanelToolTip.Set(
            DoorButton,
            panelDoor ? PanelStrings.TipFirstRunDoorPanel : PanelStrings.TipFirstRunDoorUpdate);

        DoorButton.Tag = panelDoor;
        DoorButton.IsVisible = rows.Count > 0;

        if (rows.Count > 0) RecheckLine.Text = string.Empty;
    }

    /// <summary>
    /// НАПОЛНИТЬ ОДНУ СТРОКУ. Слова — из словаря; причина сервера приходит ЗАКРЫТЫМ СПИСКОМ фраз
    /// (<see cref="FirstRunFacts.ServerProblem"/> — уже готовая фраза, а не код и не текст исключения).
    /// </summary>
    private static void Fill(
        TextBlock title,
        TextBlock body,
        TextBlock hint,
        FirstRunRowKind? kind,
        FirstRunFacts facts)
    {
        if (kind is null)
        {
            title.Text = string.Empty;
            body.Text = string.Empty;
            hint.Text = string.Empty;
            hint.IsVisible = false;

            return;
        }

        (title.Text, body.Text, hint.Text) = kind switch
        {
            FirstRunRowKind.EngineMissing => (
                PanelStrings.FirstRunServerMissingEngineTitle,
                PanelStrings.FirstRunServerMissingEngineBody,
                PanelStrings.FirstRunServerMissingEngineHint),

            FirstRunRowKind.ServerNotStarted => (
                PanelStrings.FirstRunServerNotStartedTitle,
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.FirstRunServerNotStartedReasonFormat,
                    FirstRunDecisions.ReasonText(facts.ServerProblem ?? FirstRunReason.Unknown)),
                string.Empty),

            FirstRunRowKind.KeyMissing => (
                PanelStrings.FirstRunKeyMissingTitle,
                PanelStrings.FirstRunKeyMissingBody,
                PanelStrings.FirstRunKeyMissingHint),

            _ => (
                PanelStrings.FirstRunKeyUnreadableTitle,
                PanelStrings.FirstRunKeyUnreadableBody,
                string.Empty),
        };

        hint.IsVisible = hint.Text.Length > 0;
    }

    /// <summary>
    /// ДВЕРЬ НАЖАТА — сообщить вызывающему, какая именно. Окно закрывается ДО открытия следующего:
    /// два окна друг поверх друга панель не ставит.
    /// </summary>
    private void OpenDoor()
    {
        var panel = DoorButton.Tag is true;

        Close();

        if (panel) PanelRequested?.Invoke();
        else UpdateSectionRequested?.Invoke();
    }
}
