using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DshPanel.Settings;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ТРИ ДЕФЕКТА 2.4.0, КОТОРЫЕ ПРОВЕРЯЮТСЯ ПРОГОНОМ, А НЕ КАДРОМ (t4, u4).
///
/// 1. **DEF-037 — Esc закрывает диалог, и закрывает БЕЗОПАСНОЙ кнопкой.** До 2.4.0 `IsCancel`
///    стоял только у окна первого запуска, и Esc не делал ничего: клавиша «выхода» работала
///    в одном окне из пяти. Проверка нажимает Esc НАСТОЯЩИМ вводом и смотрит, что диалог закрылся,
///    а ответ — «Отмена» (не «погасить сервер» и не «не сохранять»).
/// 2. **DEF-036 — одна проверка обновления не делает вид, что человек правил настройки.**
///    Проверка пишет след попытки в настройки; окно обязано провести его через себя нетронутым,
///    иначе сравнение «есть ли несохранённая правка» видит разницу и спрашивает о правке,
///    которой не было.
/// 3. **DEF-006 — кнопка подготовки гаснет в полёте, второе нажатие не стартует второй
///    подготовки.** Правило вынесено чистой функцией и проверяется перебором сочетаний:
///    мутация «не смотреть `busy`» роняет и таблицу, и дверь.
///
/// ⚠️ Кадром это не доказывается: клавиша, ответ диалога и «кнопка погасла» — поведение,
/// а не вид (норма `95-windows.md` `О-17`; `60-states.md` §10).
/// </summary>
public class CloseAndBusyTests
{
    // ------------------------------------------------------------------ DEF-037

    /// <summary>
    /// ESC ЗАКРЫВАЕТ КАЖДЫЙ ИЗ ЧЕТЫРЁХ ДИАЛОГОВ. Перебираются все четыре, а не один:
    /// `IsCancel` — свойство разметки, и забыть его можно ровно в одном окне из четырёх.
    ///
    /// Мутация «убрать `IsCancel`» (одна строка разметки) роняет проверку этого окна.
    /// </summary>
    [AvaloniaFact]
    public void Esc_закрывает_четыре_диалога()
    {
        AssertClosesOnEscape(new ConfirmStopWindow());
        AssertClosesOnEscape(new ConfirmEngineWindow());
        AssertClosesOnEscape(new BackupStopWindow());
        AssertClosesOnEscape(new UnsavedChangesWindow());
    }

    private static void AssertClosesOnEscape(Avalonia.Controls.Window dialog)
    {
        var closed = 0;

        dialog.Closed += (_, _) => closed++;
        dialog.Show();

        // Настоящий ввод, как у человека: клавиша проходит через окно, а не «ставим ответ сами».
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(1, closed);
        Assert.False(dialog.IsVisible);
    }

    /// <summary>
    /// ESC В ВОПРОСЕ О НЕСОХРАНЁННОЙ ПРАВКЕ — ЭТО «ОТМЕНА», А НЕ «НЕ СОХРАНЯТЬ». Разница
    /// решающая: отмена возвращает человека в окно с целой правкой, а «не сохранять» потеряла бы
    /// её. Мутация «повесить IsCancel на DiscardButton» роняет именно это утверждение.
    /// </summary>
    [AvaloniaFact]
    public void Esc_в_вопросе_о_правке_значит_отмена()
    {
        var dialog = new UnsavedChangesWindow();

        dialog.Show();
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(UnsavedChoice.Cancel, dialog.Choice);
    }

    // ------------------------------------------------------------------ DEF-036

    /// <summary>
    /// ПОСЛЕ ОДНОЙ ПРОВЕРКИ ОБНОВЛЕНИЯ ЗАКРЫТИЕ НЕ СПРАШИВАЕТ. След попытки (когда, чем кончилась,
    /// какая причина) пишется в настройки самой проверкой; окно обязано провести его через
    /// `PendingSettings` нетронутым — иначе сравнение видит разницу и спрашивает о правке,
    /// которой человек не делал.
    ///
    /// Мутация «убрать копирование поля» (любое из трёх) роняет и прямое сравнение полей,
    /// и «несохранённых изменений нет».
    /// </summary>
    [AvaloniaFact]
    public void Проверили_обновление_закрытие_не_спрашивает()
    {
        var root = PanelTestStand.TempDir();

        try
        {
            var (window, controller, _) = PanelTestStand.SettingsWithOwner(root);

            // Проверка обновления оставила след — ровно так, как его пишет контроллер.
            controller.Settings.UpdateAttemptAt = "23.10.2026 10:00";
            controller.Settings.UpdateAttemptKind = "failed";
            controller.Settings.UpdateAttemptError = "нет связи с GitHub — проверьте подключение к сети и повторите.";

            var pending = window.PendingSettings();

            Assert.Equal("23.10.2026 10:00", pending.UpdateAttemptAt);
            Assert.Equal("failed", pending.UpdateAttemptKind);
            Assert.Equal(controller.Settings.UpdateAttemptError, pending.UpdateAttemptError);

            Assert.False(
                SettingsCloseDecisions.HasUnsavedChanges(pending, controller.Settings),
                "окно считает несохранённой правкой след проверки обновления — оно спросит о правке, которой не было");
        }
        finally
        {
            PanelTestStand.RemoveTemp(root);
        }
    }

    // ------------------------------------------------------------------ DEF-006

    /// <summary>
    /// КНОПКА ПОДГОТОВКИ ГАСНЕТ В ПОЛЁТЕ, И ВТОРОЕ НАЖАТИЕ НЕ СТАРТУЕТ ВТОРОЙ ПОДГОТОВКИ.
    ///
    /// Перебираются ВСЕ восемь сочетаний «есть что готовить × идёт × готово»: «где-то есть false»
    /// ничего не доказывает, а ошибка в одном сочетании стоит второй подготовки в сотни мегабайт.
    /// Мутации «не смотреть `busy`» (в кнопке) и «пускать, даже если идёт» (в двери) — красные.
    /// </summary>
    [Fact]
    public void Кнопка_подготовки_гаснет_и_второй_старт_невозможен()
    {
        foreach (var ready in new[] { false, true })
        {
            foreach (var busy in new[] { false, true })
            {
                foreach (var prepared in new[] { false, true })
                {
                    var expected = ready && !busy && !prepared;

                    Assert.Equal(expected, UpdatePrepareDecisions.ButtonEnabled(ready, busy, prepared));

                    // Пока подготовка идёт, кнопка погашена — каким бы ни было остальное.
                    if (busy) Assert.False(UpdatePrepareDecisions.ButtonEnabled(ready, busy, prepared));
                }
            }
        }

        // Дверь: пока идёт — не пускает; когда свободно — пускает.
        Assert.False(UpdatePrepareDecisions.MayStart(busy: true));
        Assert.True(UpdatePrepareDecisions.MayStart(busy: false));
    }
}
