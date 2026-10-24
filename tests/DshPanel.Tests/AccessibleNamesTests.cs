using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДОСТУПНЫЕ ИМЕНА КНОПОК ГЛАВНОГО ОКНА (DEF-038, 2.4.0).
///
/// **От какого случая.** Восемь кнопок главного окна рисуются помощником `WithIcon`: в `Content`
/// кладётся `StackPanel` (значок + `TextBlock`). Читалка экрана не находит у такой кнопки ни
/// свойства `Name`, ни текста в `Content` (там не строка) и объявляет её просто «кнопкой» —
/// человек, работающий с экранной читалкой, слышит восемь безымянных кнопок. Замер до правки:
/// `AutomationProperties.Name` — **0 применений** на всю панель.
///
/// **Что проверяется.** Для КАЖДОЙ из восьми кнопок: имя не пустое и РАВНО подписи, которую
/// видит глаз (текст внутри значка-панели). Перебор по именам, а не «хотя бы у одной»: правило
/// одно на все, и одна забытая кнопка — тот же дефект для того, кто ею пользуется.
///
/// **Мутации, которые обязаны упасть:** убрать `AutomationProperties.SetName` из `WithIcon`
/// (имена снова пустые) · поставить имя константой (разойдётся с подписью).
/// </summary>
public class AccessibleNamesTests
{
    /// <summary>Восемь кнопок, которые рисует `WithIcon` (значок + подпись).</summary>
    private static readonly string[] IconButtons =
    {
        "StartServerButton", "RestartServerButton", "StopServerButton",
        "SettingsButton", "BackupsButton", "OpenAgentButton",
        "RefreshBalanceButton", "PeakButton",
    };

    /// <summary>Подпись кнопки так, как её видит глаз: текст внутри значка-панели.</summary>
    private static string Caption(Button button) =>
        button.Content is StackPanel panel
            ? string.Concat(panel.Children.OfType<TextBlock>().Select(text => text.Text))
            : button.Content as string ?? string.Empty;

    [AvaloniaFact]
    public void Доступное_имя_кнопки_с_картинкой_равно_её_подписи()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));

        try
        {
            window.Show();
            PanelTestStand.Settle();

            Assert.Equal(8, IconButtons.Length);

            foreach (var name in IconButtons)
            {
                var button = window.FindControl<Button>(name);

                Assert.NotNull(button);

                var caption = Caption(button!);
                var accessible = AutomationProperties.GetName(button!);

                Assert.False(
                    string.IsNullOrWhiteSpace(caption),
                    $"«{name}»: подписи нет вовсе — сравнивать нечего");

                Assert.False(
                    string.IsNullOrWhiteSpace(accessible),
                    $"«{name}»: доступного имени нет — читалка объявит кнопку безымянной (DEF-038)");

                Assert.Equal(caption, accessible);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
