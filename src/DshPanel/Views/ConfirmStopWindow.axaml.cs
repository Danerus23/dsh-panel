using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// Подтверждение остановки ВСТРОЕННОГО сервера — то самое «отдельное подтверждение каждый раз»,
/// которое выбрал владелец 24.09.2026.
///
/// Зачем отдельное окно, а не вторая кнопка в главном: подтверждение должно быть действием,
/// которое нельзя сделать случайно. Двойной щелчок по «Остановить» не должен обрывать текущую
/// работу человека — а через встроенный сервер идёт именно она.
///
/// Окно НИЧЕГО не решает само и ничего не гасит: оно возвращает «да» или «нет», а решение
/// принимает предикат <see cref="Server.ServerDecisions.CanStop"/>, и он же проверяется тестом.
/// Так «случайно подтвердилось» и «случайно погасилось» — разные утверждения, и каждое видно.
/// </summary>
public partial class ConfirmStopWindow : Window
{
    public ConfirmStopWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи ставим сразу, а не только в Attach: окно, построенное без наполнения,
        // не должно выглядеть пустым — так его видят тесты и так его мог бы увидеть человек.
        Title = PanelStrings.ConfirmStopTitle;
        CancelButton.Content = PanelStrings.ConfirmStopNo;
        ConfirmButton.Content = PanelStrings.ConfirmStopYes;

        CancelButton.Click += (_, _) => Close(false);
        ConfirmButton.Click += (_, _) => Close(true);

        // Подсказки обеих кнопок: что произойдёт по нажатию и чем ответ «остановить» отличается
        // от «отмены». Вопрос задаётся про ЖИВОЙ сервер, и человек обязан видеть это до щелчка.
        PanelToolTip.Set(CancelButton, PanelStrings.TipConfirmStopNoButton);
        PanelToolTip.Set(ConfirmButton, PanelStrings.TipConfirmStopYesButton);
    }

    /// <summary>
    /// Наполнить окно тем, о чём спрашиваем. Порт и процесс называются прямо: подтверждать
    /// «остановку сервера вообще» человек не должен — он должен видеть, что именно остановится.
    /// </summary>
    public void Attach(int port, int pid, string processName)
    {
        Title = PanelStrings.ConfirmStopTitle;
        QuestionText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.ConfirmStopFormat, port, pid, processName);

        CancelButton.Content = PanelStrings.ConfirmStopNo;
        ConfirmButton.Content = PanelStrings.ConfirmStopYes;
    }

    /// <summary>
    /// ТО ЖЕ ОКНО, НО С ЧУЖИМ ВОПРОСОМ. Заведено для действия «поднять свой сервер заново»
    /// (решение владельца 13.10.2026, ответ 2): гасится сервер, которым панель НЕ управляет,
    /// и вопрос у него СВОЙ — «остановит найденный и поднимет свой», а не «остановить сервер?».
    ///
    /// ⚠️ Отдельным членом, а не «допиши текст снаружи»: вопрос и подписи кнопок — одно решение
    /// о том, что человек подтверждает, и разойтись им нельзя. Окно при этом ОДНО на оба случая
    /// намеренно: человеку это один и тот же вопрос «точно?», и второе такое же окно завело бы
    /// вторую реализацию безопасного действия (безопасное — справа, решение владельца).
    /// </summary>
    public void AttachQuestion(string title, string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        Title = title;
        QuestionText.Text = question;

        CancelButton.Content = PanelStrings.ConfirmStopNo;
        ConfirmButton.Content = PanelStrings.SrvRecoverButtonYes;
    }
}
