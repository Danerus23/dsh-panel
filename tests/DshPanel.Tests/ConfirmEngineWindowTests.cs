using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ВОПРОС ПЕРЕД ОБНОВЛЕНИЕМ ДВИЖКА: «спрашивать ВСЕГДА» (решение владельца 10.10.2026, пункт 4
/// плана E3.2).
///
/// Проверяется не вид, а СЛОВА: человек обязан видеть до щелчка, что панель остановит движок
/// и какую версию поставит. Пустое или чужое подтверждение здесь дороже, чем где-либо: через
/// движок может идти работа агента, и она прервётся.
/// </summary>
public class ConfirmEngineWindowTests
{
    /// <summary>Окно, построенное без наполнения, не выглядит пустым: подписи есть сразу.</summary>
    [AvaloniaFact]
    public void Окно_без_наполнения_не_пустое()
    {
        var window = new ConfirmEngineWindow();

        try
        {
            Assert.Equal(PanelStrings.EngineUpdateAskTitle, window.Title);
            Assert.Equal(PanelStrings.EngineUpdateAskNo, window.FindControl<Button>("CancelButton")!.Content as string);
            Assert.Equal(PanelStrings.EngineUpdateAskYes, window.FindControl<Button>("ConfirmButton")!.Content as string);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>ВОПРОС НАЗЫВАЕТ ВЕРСИЮ и говорит про прерывание работы — словами, а не намёком.</summary>
    [AvaloniaFact]
    public void Вопрос_называет_версию_и_предупреждает_о_работе()
    {
        var window = new ConfirmEngineWindow();
        window.Attach(new PanelQuestion(
            PanelStrings.EngineUpdateAskTitle,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture, PanelStrings.EngineUpdateAskFormat, "0.1.5-rc.2"),
            PanelStrings.EngineUpdateAskYes,
            PanelStrings.EngineUpdateAskNo,
            PanelStrings.TipEngineUpdateAskYes,
            PanelStrings.TipEngineUpdateAskNo));

        try
        {
            var question = window.FindControl<TextBlock>("QuestionText")!.Text!;

            Assert.Contains("0.1.5-rc.2", question, StringComparison.Ordinal);
            Assert.Contains("прервётся", question, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PanelStrings.EngineUpdateAskTitle, window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ОКНО ОДНО НА ДВА ВОПРОСА: перед откатом оно показывает СВОИ слова — и они говорят про сессии,
    /// созданные новой версией, которые старый движок не откроет.
    /// </summary>
    [AvaloniaFact]
    public void Вопрос_об_откате_говорит_про_сессии()
    {
        var window = new ConfirmEngineWindow();
        window.Attach(new PanelQuestion(
            PanelStrings.EngineRollbackAskTitle,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.EngineRollbackAskFormat, "0.2.0-rc.2", "0.1.5-rc.2"),
            PanelStrings.EngineRollbackAskYes,
            PanelStrings.EngineRollbackAskNo,
            PanelStrings.TipEngineRollbackYes,
            PanelStrings.TipEngineRollbackNo));

        try
        {
            var question = window.FindControl<TextBlock>("QuestionText")!.Text!;

            Assert.Contains("0.2.0-rc.2", question, StringComparison.Ordinal);
            Assert.Contains("0.1.5-rc.2", question, StringComparison.Ordinal);
            Assert.Contains("сессии", question, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PanelStrings.EngineRollbackAskTitle, window.Title);
            Assert.Equal(PanelStrings.EngineRollbackAskYes, window.FindControl<Button>("ConfirmButton")!.Content as string);
        }
        finally
        {
            window.Close();
        }
    }
}
