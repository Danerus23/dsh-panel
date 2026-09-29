using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// Что человек выбрал, когда панель предложила погасить сервер перед копией или накатом.
/// Отдельный перечень, а не «да/нет»: у выбора ТРИ исхода, и средний — не компромисс, а
/// осознанное решение владельца («копировать на ходу»), которое обязано быть названо.
/// </summary>
public enum StopChoice
{
    /// <summary>Ничего не делать: копию не снимать.</summary>
    Cancel,

    /// <summary>Копировать на ходу: сервер работает, допущение уезжает в отчёт словами.</summary>
    ContinueLive,

    /// <summary>Погасить сервер и продолжить — копия получается целой.</summary>
    StopAndContinue,
}

/// <summary>
/// Вопрос перед копией и накатом: «сервер работает — погасить?» — решение владельца 26.09.2026.
///
/// Почему это отдельное окно, а не галочка в экране копий: сессии пишет ЖИВОЙ процесс, и копия
/// «на ходу» может взять недописанный файл. Выбор между «подождать и получить целую копию» и
/// «не прерывать работу и получить копию с оговоркой» — решение о данных человека, и принимать
/// его молча за него панель не имеет права.
///
/// Окно НИЧЕГО не гасит и ничего не решает: оно возвращает выбор, гасит — вызывающий, и делает
/// это по правилу: свой сервер свободно, найденный и взятый под управление — только по отдельному
/// подтверждению каждый раз (<see cref="ConfirmStopWindow"/>).
/// </summary>
public partial class BackupStopWindow : Window
{
    public BackupStopWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        CancelButton.Click += (_, _) => Close(StopChoice.Cancel);
        LiveButton.Click += (_, _) => Close(StopChoice.ContinueLive);
        StopButton.Click += (_, _) => Close(StopChoice.StopAndContinue);

        // Подсказки трёх кнопок: у выбора «погасить или копировать на ходу» цена ошибки разная,
        // и слова о ней стоят на кнопках, а не в документе.
        PanelToolTip.Set(CancelButton, PanelStrings.TipBackupStopCancelButton);
        PanelToolTip.Set(LiveButton, PanelStrings.TipBackupStopLiveButton);
        PanelToolTip.Set(StopButton, PanelStrings.TipBackupStopStopButton);
    }

    /// <summary>
    /// Наполнить окно. Порт называется прямо: «сервер вообще» человеку подтверждать нечего —
    /// он должен видеть, о каком сервере речь.
    /// </summary>
    public void Attach(int port)
    {
        Title = PanelStrings.BackupStopTitle;
        QuestionText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.BackupStopQuestionFormat, port);

        CancelButton.Content = PanelStrings.BackupStopCancel;
        LiveButton.Content = PanelStrings.BackupStopNo;
        StopButton.Content = PanelStrings.BackupStopYes;
    }
}
