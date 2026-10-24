using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// Что человек ответил, закрывая окно копий на середине копии (DEF-045, 2.4.0).
///
/// Два исхода, и оба — про ОДНО дело (копию), а не «да/нет»: окно закрыть, оставив копию
/// в фоне, либо вернуться и смотреть, как она идёт. <see cref="Stay"/> стоит ПЕРВЫМ по той же
/// причине, что «Отмена» в <see cref="UnsavedChoice"/>: крестик закрывает вопрос, не отвечая,
/// и умолчанием обязано стать безопасное действие — не закрывать окно.
/// </summary>
public enum BusyCloseChoice
{
    /// <summary>Вернуться в окно: не закрывать его, копия продолжает идти на виду.</summary>
    Stay,

    /// <summary>Оставить в фоне: закрыть окно, копия продолжается; итог — в журнале и в списке.</summary>
    LeaveInBackground,
}

/// <summary>
/// ВОПРОС «ЗАКРЫТЬ ОКНО, ПОКА ИДЁТ КОПИЯ?» — отдельная маленькая дверь (DEF-045).
///
/// **От какого случая.** Закрытие окна копий на середине копии ничего не спрашивало: копия
/// продолжалась в фоне, а человек этого не знал — ни что она идёт, ни где итог. Кнопка
/// «Остановить» в панели гасит сервер, но не копию; отмены у копии нет вовсе, поэтому честный
/// выбор — «оставить в фоне» или «вернуться», а не «прервать» (ложь, за которую 2.4.0 уже
/// заплатила в DEF-044).
///
/// **Окно ничего не решает.** Оно возвращает <see cref="Choice"/>; закрывает окно копий
/// вызывающий (<see cref="BackupWindow"/>), и это видно прогону: «спросили» и «закрыли» —
/// разные утверждения.
///
/// ⚠️ Ответ читается СВОЙСТВОМ, а не результатом <c>ShowDialog&lt;T&gt;</c>: крестик закрывает
/// вопрос, не отвечая, и ответом обязано стать безопасное <see cref="BusyCloseChoice.Stay"/>,
/// а не первое значение перечня «по счёту».
/// </summary>
public partial class BusyCloseWindow : Window
{
    public BusyCloseWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи — сразу, а не «когда-нибудь»: окно, построенное без наполнения, не должно
        // выглядеть пустым (так его видят проверки и так его мог бы увидеть человек).
        Title = PanelStrings.BackupBusyCloseTitle;
        QuestionText.Text = PanelStrings.BackupBusyCloseQuestion;
        LeaveButton.Content = PanelStrings.BackupBusyCloseYes;
        StayButton.Content = PanelStrings.BackupBusyCloseNo;

        LeaveButton.Click += (_, _) => Answer(BusyCloseChoice.LeaveInBackground);
        StayButton.Click += (_, _) => Answer(BusyCloseChoice.Stay);

        // ФОКУС ПРИ ОТКРЫТИИ — НА БЕЗОПАСНОМ ОТВЕТЕ (`80` §3а, Ш-54): человек, закрывающий окно
        // с клавиатуры, первым делом попадает на «Остаться в окне», а не на закрытие.
        Opened += (_, _) => StayButton.Focus();
    }

    /// <summary>Ответ человека. До ответа — безопасный: закрытое крестиком окно ничего не решило.</summary>
    public BusyCloseChoice Choice { get; private set; } = BusyCloseChoice.Stay;

    /// <summary>Ответить и закрыться — одна дверь на обе кнопки: подписи и ответы не разойдутся.</summary>
    private void Answer(BusyCloseChoice choice)
    {
        Choice = choice;
        Close();
    }
}
