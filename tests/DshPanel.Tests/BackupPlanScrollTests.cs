using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРОКРУТКА ПЛАНА НАКАТА К ГЛАЗАМ — САМ ВЫЗОВ, А НЕ «РЕШЕНИЕ РЯДОМ» (блок F плана 2.2.0).
///
/// **От какого случая.** План наката живёт ниже списка копий, и до этой работы он появлялся за
/// нижним краем окна: человек нажимал «Восстановить», не прочитав, что вернётся (жалоба владельца,
/// п. 8 `docs\DESIGN.md`). Прокрутка откладывается до конца прохода вёрстки
/// (`UiThread.PostTo(…, DispatcherPriority.Loaded)` → `PlanPanel.BringIntoView()`,
/// `Views\BackupWindow.axaml.cs:864`): без отсрочки `BringIntoView` уходил в ScrollViewer
/// с прежними координатами панели, и замер показал **180 пикселей вместо 600** — то есть план
/// оставался за нижним краем.
///
/// **Что здесь доказывается.** Не «работа отправлена», а её СЛЕДСТВИЕ: смещение прокрутки
/// `ContentScroll` ВЫРОСЛО после того, как панель плана стала видимой и вёрстка прошла.
/// Проверка падает на коде без прокрутки (смещение не меняется) и на отложенной работе, которая
/// не дошла до нитки (тоже не меняется).
///
/// ⚠️ **Чего эта проверка не доказывает:** ровно ли 600 пикселей и что панель стала видна целиком —
/// безголовый прогон не измеряет глазами; «смещение выросло» этого не заменяет.
/// </summary>
public class BackupPlanScrollTests
{
    /// <summary>
    /// ПЛАН ВИДЕН ЧЕЛОВЕКУ ПОСЛЕ ПРОКРУТКИ: панель показалась, и прокрутка ушла вниз.
    ///
    /// Мутации, которые обязаны упасть здесь: **«убрать `BringIntoView`»** (смещение не меняется)
    /// и **«убрать отсрочку до `Loaded`»** (в безголовом прогоне прокрутка до вёрстки уходит в те же
    /// прежние координаты — смещение остаётся нулевым).
    /// </summary>
    [AvaloniaFact]
    public void План_прокручивается_к_глазам_после_вёрстки()
    {
        var backups = new BackupWindowStopFlowTests.StubBackups();
        var window = new BackupWindow { Height = 520 };

        window.Attach(backups);
        window.Show();
        PanelTestStand.Settle();

        try
        {
            var scroll = window.FindControl<ScrollViewer>("ContentScroll");

            Assert.NotNull(scroll);

            var before = scroll!.Offset.Y;

            // Публичная дверь: выбор копии собирает план и показывает его панель.
            window.ChooseEntry(0);

            // Отложенная прокрутка — на DispatcherPriority.Loaded: прокручиваем очередь диспетчера.
            Dispatcher.UIThread.RunJobs();
            PanelTestStand.Settle();

            var panel = window.FindControl<Border>("PlanPanel");

            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible, "панель плана не показалась — прокручивать нечего");

            Assert.True(
                scroll.Offset.Y > before,
                $"прокрутки плана не было: смещение {scroll.Offset.Y:0.#} при исходном {before:0.#}");
        }
        finally
        {
            window.Close();
        }
    }
}
