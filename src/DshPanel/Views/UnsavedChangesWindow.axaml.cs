using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// Что человек ответил на вопрос о несохранённой правке. Три исхода, и средний — не «нет»,
/// а осознанное решение: закрыть окно и потерять введённое.
///
/// ⚠️ <see cref="Cancel"/> стоит ПЕРВЫМ намеренно: значение по умолчанию обязано значить
/// «ничего не делать». Окно можно закрыть крестиком, не ответив, и тогда ответом становится
/// умолчание — а умолчание «сохранить» молча записало бы в файл то, чего человек не подтверждал.
/// </summary>
public enum UnsavedChoice
{
    /// <summary>Ничего не делать: окно настроек остаётся открытым, правка на месте.</summary>
    Cancel,

    /// <summary>Сохранить правку и закрыть окно настроек.</summary>
    Save,

    /// <summary>Закрыть окно настроек и потерять правку — человек сказал это сам.</summary>
    Discard,
}

/// <summary>
/// Вопрос перед закрытием окна настроек: «параметры изменены — сохранить?»
///
/// Жалоба владельца (п. 16 `docs\DESIGN.md`): *«Вероятно требуется предупреждение при закрытии
/// окна что параметры изменены, сохранить ли перед закрытием»*. До этой работы окно закрывалось
/// молча, и введённое число пропадало без единого слова — человек был уверен, что настройку
/// поменял.
///
/// Почему отдельное окно, а не строка в самом окне настроек: вопрос задаётся в тот момент, когда
/// человек уже закрывает окно, и ответ обязан быть на виду. Строка в углу закрывающегося окна
/// читалась бы как «что-то мигнуло».
///
/// Окно НИЧЕГО не сохраняет и не решает само: оно отдаёт ответ в <see cref="Choice"/>, а сохраняет
/// и закрывает вызывающий (<see cref="SettingsWindow"/>). Так «случайно сохранилось» и «случайно
/// пропало» — разные утверждения, и каждое видно прогону.
///
/// ⚠️ Ответ читается СВОЙСТВОМ, а не результатом <c>ShowDialog&lt;T&gt;</c>, и это не вкус:
/// крестик окна закрывает диалог, не отвечая, а тогда ответом обязано стать «Отмена»
/// (<see cref="UnsavedChoice.Cancel"/>), а не первое значение перечня по умолчанию. Свойство
/// с явным умолчанием делает это видимым и проверяемым.
/// </summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи ставим сразу, а не только по просьбе: окно, построенное без наполнения,
        // не должно выглядеть пустым — так его видят проверки и так его мог бы увидеть человек.
        Title = PanelStrings.SettingsUnsavedTitle;
        QuestionText.Text = PanelStrings.SettingsUnsavedQuestion;
        CancelButton.Content = PanelStrings.SettingsUnsavedCancel;
        DiscardButton.Content = PanelStrings.SettingsUnsavedDiscard;

        // Кнопка «Сохранить» — та же строка, что и в самом окне настроек: действие одно и то же,
        // и второй такой же подписи в словаре быть не должно.
        SaveButton.Content = PanelStrings.SettingsSave;

        CancelButton.Click += (_, _) => Answer(UnsavedChoice.Cancel);
        DiscardButton.Click += (_, _) => Answer(UnsavedChoice.Discard);
        SaveButton.Click += (_, _) => Answer(UnsavedChoice.Save);

        // Подсказки трёх кнопок: у вопроса о несохранённой правке ответы стоят рядом, и разница
        // между «отмена» и «выйти без сохранения» — ровно то, что человеку нужно прочитать
        // ДО щелчка, а не после.
        PanelToolTip.Set(CancelButton, PanelStrings.TipUnsavedCancelButton);
        PanelToolTip.Set(DiscardButton, PanelStrings.TipUnsavedDiscardButton);

        // «Сохранить» — та же подсказка, что у кнопки в самом окне настроек: действие одно,
        // и два разных текста для него разошлись бы.
        PanelToolTip.Set(SaveButton, PanelStrings.TipSaveSettingsButton);
    }

    /// <summary>
    /// Ответ человека. До ответа — «Отмена»: закрытое крестиком окно ничего не подтверждает.
    /// </summary>
    public UnsavedChoice Choice { get; private set; } = UnsavedChoice.Cancel;

    /// <summary>Ответить и закрыться — одна дверь на все три кнопки: иначе подписи и ответы разойдутся.</summary>
    private void Answer(UnsavedChoice choice)
    {
        Choice = choice;
        Close();
    }
}
