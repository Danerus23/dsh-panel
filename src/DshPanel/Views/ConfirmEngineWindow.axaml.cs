using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ВОПРОС ПЕРЕД ОБНОВЛЕНИЕМ ДВИЖКА — то самое «спрашивать ВСЕГДА» (решение владельца 10.10.2026,
/// пункт 4 плана E3.2).
///
/// **Почему спрашивать всегда, а не «когда идёт работа».** Замер разведки (10.10.2026): признака
/// «идёт работа агента» у панели **нет** — в закреплённой поверхности 84 имён нет ни `session/status`,
/// ни `job/*`, а «сервер отвечает» признаком занятости не является (отвечает и простаивающий).
/// Поэтому безопасное поведение по умолчанию: спросить перед каждой подменой версии, назвав словами,
/// что именно прервётся.
///
/// Окно НИЧЕГО не делает само: оно возвращает «да» или «нет», а работу ведёт путь обновления
/// (<see cref="Server.EngineUpdateRun"/>). Спрашивать здесь, а не в домене, — по той же причине,
/// что у подтверждения остановки сервера: вопрос — это разговор с человеком, и он должен быть
/// действием, которое нельзя сделать случайно.
/// </summary>
public partial class ConfirmEngineWindow : Window
{
    public ConfirmEngineWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи ставим сразу, а не только в Attach: окно, построенное без наполнения,
        // не должно выглядеть пустым — так его видят тесты и так его мог бы увидеть человек.
        Title = PanelStrings.EngineUpdateAskTitle;
        CancelButton.Content = PanelStrings.EngineUpdateAskNo;
        ConfirmButton.Content = PanelStrings.EngineUpdateAskYes;

        CancelButton.Click += (_, _) => Close(false);
        ConfirmButton.Click += (_, _) => Close(true);

        // Подсказки обеих кнопок: что произойдёт по нажатию. У вопроса про движок «да» значит
        // «останови наш движок и поставь версию» — это и должно быть видно ДО щелчка.
        PanelToolTip.Set(CancelButton, PanelStrings.TipEngineUpdateAskNo);
        PanelToolTip.Set(ConfirmButton, PanelStrings.TipEngineUpdateAskYes);
    }

    /// <summary>
    /// Наполнить окно ВОПРОСОМ: заголовок, текст, подписи кнопок и их подсказки. Дверью — потому что
    /// вопросов у панели ДВА (обновление и откат), и оба обязаны спрашивать одинаково: слова у них
    /// разные, а окно одно.
    /// </summary>
    public void Attach(PanelQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        Title = question.Title;
        QuestionText.Text = question.Text;

        CancelButton.Content = question.No;
        ConfirmButton.Content = question.Yes;

        PanelToolTip.Set(CancelButton, question.TipNo);
        PanelToolTip.Set(ConfirmButton, question.TipYes);
    }
}
