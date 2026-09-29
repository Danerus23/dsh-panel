using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПОДСКАЗКИ КНОПОК: у КАЖДОЙ кнопки КАЖДОГО окна панели есть подсказка, и она говорит, что
/// произойдёт по нажатию.
///
/// Решение владельца 27.09.2026 (шаг 1 «живости вида»). Обещание проверяется ТРЕМЯ разными
/// способами, и это не избыточность:
///
/// 1. **Все кнопки всех окон** — перебор дерева: подсказка непустая и у неё включён показ
///    у недоступной кнопки (<c>ShowOnDisabled</c>). Без второго недоступная кнопка молчит —
///    а именно там подсказка и нужнее всего;
/// 2. **Причина недоступности** — у недоступной кнопки главного окна подсказка НАЗЫВАЕТ причину,
///    а не повторяет действие: «нажать нельзя, пока идёт запуск» и «нажать нельзя: сервер не
///    работает» — разные ответы на разные положения дел;
/// 3. **Живой путь** — указатель наводится на кнопку, и подсказка ОТКРЫВАЕТСЯ. Первые две
///    проверки этого не доказывают: свойство можно выставить, а показать нечего — так и было бы,
///    забудь <see cref="PanelToolTip"/> включить показ у недоступной кнопки.
///
/// ⚠️ Чего здесь нет и быть не может: подсказок у пунктов меню значка. Это меню рисует Windows
/// (<c>TrackPopupMenu</c>), и нативных подсказок у его пунктов не бывает вовсе; подсказка есть
/// у САМОГО значка — она показывает агента, баланс и тариф.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ButtonTooltipTests
{
    /// <summary>Собрать кнопки без подсказки — по имени органа, а не по подписи: подпись переводят.</summary>
    private static List<string> WithoutTip(Window window)
    {
        var without = new List<string>();

        foreach (var button in ChangedByPanel(window))
        {
            var tip = ToolTip.GetTip(button) as string;

            if (string.IsNullOrWhiteSpace(tip)) without.Add(Name(button));
        }

        return without;
    }

    /// <summary>
    /// КНОПКИ ПАНЕЛИ: те, что объявлены кнопками в разметке её окон.
    ///
    /// Отсеиваются двое, и оба — не «чтобы проверка прошла»:
    ///
    /// * **внутренние части чужих органов** (стрелки полосы прокрутки) — у них <c>TemplatedParent</c>
    ///   не пуст: их создал шаблон чужого органа, панель их не объявляла и подписей им не даёт;
    /// * **галочки** (<c>CheckBox</c> — это <c>ToggleButton</c>): у галочки своя подпись рядом,
    ///   и владелец просил подсказки у КНОПОК.
    /// </summary>
    private static List<Button> ChangedByPanel(Window window) =>
        PanelTestStand.Buttons(window)
            .Where(button => button.TemplatedParent is null && button is not ToggleButton)
            .ToList();

    private static string Name(Button button) =>
        !string.IsNullOrEmpty(button.Name)
            ? button.Name!
            : button.Content?.ToString() ?? button.GetType().Name;

    /// <summary>Подсказка есть, она непустая и у недоступной кнопки тоже ПОКАЗЫВАЕТСЯ.</summary>
    private static void AssertTips(Window window, string where, int atLeast)
    {
        var buttons = ChangedByPanel(window);

        Assert.True(
            buttons.Count >= atLeast,
            $"{where}: кнопок в окне {buttons.Count}, а ожидалось не меньше {atLeast} — " +
            "похоже, проверка прошла не через то состояние (окно не показано?)");

        var without = WithoutTip(window);

        Assert.True(
            without.Count == 0,
            $"{where}: кнопки без подсказки — {string.Join(", ", without)}. " +
            "Подсказка обязана быть у каждой кнопки: «что произойдёт по нажатию».");

        var silent = buttons
            .Where(button => !ToolTip.GetShowOnDisabled(button))
            .Select(Name)
            .ToList();

        Assert.True(
            silent.Count == 0,
            $"{where}: у кнопок выключен показ подсказки у НЕДОСТУПНОГО состояния — " +
            $"{string.Join(", ", silent)}. Без него серая кнопка молчит, хотя объяснять нужно именно ей.");

        var wrongDelay = buttons
            .Where(button => ToolTip.GetShowDelay(button) != PanelToolTip.ShowDelay)
            .Select(Name)
            .ToList();

        Assert.True(
            wrongDelay.Count == 0,
            $"{where}: у кнопок своя задержка подсказки — {string.Join(", ", wrongDelay)}. " +
            "Задержка одна на всю панель, иначе подсказки выглядят как разные механизмы.");
    }

    // ------------------------------------------------------------------ все окна панели

    [AvaloniaFact]
    public void У_каждой_кнопки_каждого_окна_есть_подсказка()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // Главное окно: сервер не запущен — «Запустить» доступна, «Остановить» нет.
            var main = PanelTestStand.MainStand(ServerState.Stopped(0));
            main.Show();
            PanelTestStand.Settle();
            AssertTips(main, "главное окно", atLeast: 5);
            main.Close();

            // Главное окно с найденным чужим сервером: карточка встраивания показана, и у её
            // кнопки тоже обязана быть подсказка — она появляется только в этом состоянии.
            var found = PanelTestStand.MainWith(new PanelTestStand.StubServer(ServerState.Stopped(0))
            {
                Found = new FoundServer(3081, 4242, "node"),
            });

            found.Show();
            PanelTestStand.Settle();

            var adopt = found.FindControl<Button>("AdoptServerButton");
            Assert.NotNull(adopt);
            Assert.True(adopt!.IsVisible, "карточка найденного сервера не показана — проверка прошла не через то состояние");
            AssertTips(found, "главное окно с найденным сервером", atLeast: 6);
            found.Close();

            var settings = PanelTestStand.SettingsStand(dir);
            settings.Show();
            PanelTestStand.Settle();
            AssertTips(settings, "настройки", atLeast: 3);
            settings.CloseQuietly();

            var backups = new BackupWindow();
            backups.Attach(new BackupWindowStopFlowTests.StubBackups());
            backups.Show();
            PanelTestStand.Settle();
            AssertTips(backups, "копии", atLeast: 5);
            backups.Close();

            var about = new AboutWindow();
            about.Show();
            PanelTestStand.Settle();
            AssertTips(about, "о программе", atLeast: 3);
            about.Close();

            // Окно «Пики и тарифы»: у него одна кнопка — «Закрыть», и подсказка у неё та же,
            // что у кнопки окна «О программе». Без этой строки подсказку его кнопки не сторожит
            // никто (решение дирижёра: окно новое, а проверка общая на все окна панели).
            var peak = new PeakWindow();
            peak.Show();
            PanelTestStand.Settle();
            AssertTips(peak, "пики и тарифы", atLeast: 1);
            peak.Close();

            var confirm = new ConfirmStopWindow();
            confirm.Attach(ServerDecisions.OwnerPort, 4242, "node");
            confirm.Show();
            PanelTestStand.Settle();
            AssertTips(confirm, "подтверждение остановки", atLeast: 2);
            confirm.Close();

            var unsaved = new UnsavedChangesWindow();
            unsaved.Show();
            PanelTestStand.Settle();
            AssertTips(unsaved, "вопрос о несохранённой правке", atLeast: 3);
            unsaved.Close();

            var stop = new BackupStopWindow();
            stop.Attach(ServerDecisions.OwnerPort);
            stop.Show();
            PanelTestStand.Settle();
            AssertTips(stop, "вопрос о гашении перед копией", atLeast: 3);
            stop.Close();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ причина недоступности

    /// <summary>
    /// НЕДОСТУПНАЯ КНОПКА НАЗЫВАЕТ ПРИЧИНУ, и причины РАЗНЫЕ у разных положений дел.
    ///
    /// «Нажать нельзя, пока идёт запуск» и «нажать нельзя: сервер не работает» требуют от человека
    /// разного — ждать или не ждать вовсе. Подсказка, повторяющая действие («остановить сервер»)
    /// на серой кнопке, не объясняет ничего.
    /// </summary>
    [AvaloniaFact]
    public void Недоступная_кнопка_называет_причину_а_не_действие()
    {
        var stopped = PanelTestStand.MainStand(ServerState.Stopped(0));
        stopped.Show();
        PanelTestStand.Settle();

        var start = stopped.FindControl<Button>("StartServerButton");
        var stop = stopped.FindControl<Button>("StopServerButton");
        var open = stopped.FindControl<Button>("OpenAgentButton");

        Assert.NotNull(start);
        Assert.NotNull(stop);
        Assert.NotNull(open);

        Assert.True(start!.IsEnabled, "«Запустить» обязана быть доступна у остановленного сервера");
        Assert.Equal(PanelStrings.TipStartServerButton, ToolTip.GetTip(start) as string);

        Assert.False(stop!.IsEnabled);
        Assert.Equal(PanelStrings.TipStopServerNothing, ToolTip.GetTip(stop) as string);

        // «Открыть агента» недоступна без сервера — и говорит именно это, а не «открыть агента».
        Assert.False(open!.IsEnabled);
        Assert.Equal(PanelStrings.OpenAgentNoServerHint, ToolTip.GetTip(open) as string);

        stopped.Close();

        // Другое состояние — другая причина: сервер работает, «Запустить» недоступна.
        var running = PanelTestStand.MainStand(
            new ServerState(ServerPresence.Running, 3080, 4242, "node", "отвечает"), ServerOwner.Panel);

        running.Show();
        PanelTestStand.Settle();

        var startRunning = running.FindControl<Button>("StartServerButton");
        var stopRunning = running.FindControl<Button>("StopServerButton");

        Assert.NotNull(startRunning);
        Assert.NotNull(stopRunning);

        Assert.False(startRunning!.IsEnabled);
        Assert.Equal(PanelStrings.TipStartServerRunning, ToolTip.GetTip(startRunning) as string);

        Assert.True(stopRunning!.IsEnabled);
        Assert.Equal(PanelStrings.TipStopServerButton, ToolTip.GetTip(stopRunning) as string);

        running.Close();
    }

    /// <summary>
    /// ПОКА ПАНЕЛЬ ЗАНЯТА, ВСЕ ТРИ КНОПКИ ГОВОРЯТ ОДНУ ПРИЧИНУ — и она про занятость, а не про
    /// сервер: «подожди», а не «сервер не работает».
    /// </summary>
    [AvaloniaFact]
    public void Пока_панель_занята_причина_у_всех_кнопок_одна()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        window.BeginServerStart(PanelStrings.ServerStarting);
        PanelTestStand.Settle();

        foreach (var name in new[] { "StartServerButton", "StopServerButton", "OpenAgentButton" })
        {
            var button = window.FindControl<Button>(name);

            Assert.NotNull(button);
            Assert.False(button!.IsEnabled, $"«{name}» доступна, хотя панель занята");
            Assert.Equal(PanelStrings.TipServerBusy, ToolTip.GetTip(button) as string);
        }

        window.EndServerStart();
        window.Close();
    }

    // ------------------------------------------------------------------ живой путь

    /// <summary>
    /// ПОДСКАЗКА ДЕЙСТВИТЕЛЬНО ПОЯВЛЯЕТСЯ — и у доступной кнопки, и у НЕДОСТУПНОЙ.
    ///
    /// Это проверка живого пути: указатель вводится в окно, затем наводится на кнопку, и каркас
    /// сам открывает подсказку. Свойство <c>Tip</c> можно выставить и не увидеть ничего —
    /// у недоступного органа подсказка выключена по умолчанию, и без <c>ShowOnDisabled</c>
    /// служба подсказок НЕ РАССМАТРИВАЕТ серую кнопку вовсе.
    ///
    /// ⚠️ **Задержка здесь обнуляется, и вот почему это честно.** Задержку показа каркас отмеряет
    /// своим таймером, а в безоконном (headless) прогоне этот таймер не тикает: с задержкой
    /// в 400 мс подсказка не открывается НИКОГДА, хотя в живом окне открывается (замер: та же
    /// кнопка с задержкой 0 открывается сразу, с 400 мс — нет). Поэтому проверка идёт путём,
    /// которым каркас открывает подсказку БЕЗ ожидания, а сама задержка — её панель и объявляет —
    /// проверяется выше, у каждой кнопки каждого окна. Иначе проверка живого пути была бы
    /// проверкой таймера каркаса, а не обещания владельцу.
    ///
    /// Третьим шагом идёт КОНТРОЛЬ: у той же недоступной кнопки показ у недоступного состояния
    /// выключается, и подсказка обязана перестать открываться. Без этого «открылась» ничего
    /// не доказывало бы: подсказка могла бы открываться по другой причине.
    /// </summary>
    [AvaloniaFact]
    public void Подсказка_открывается_по_наведению_и_у_недоступной_кнопки()
    {
        var window = PanelTestStand.MainStand(ServerState.Stopped(0));
        window.Show();
        PanelTestStand.Settle();

        var start = window.FindControl<Button>("StartServerButton");
        var stop = window.FindControl<Button>("StopServerButton");

        Assert.NotNull(start);
        Assert.NotNull(stop);
        Assert.True(start!.IsEnabled);
        Assert.False(stop!.IsEnabled);

        ToolTip.SetShowDelay(start, 0);
        ToolTip.SetShowDelay(stop, 0);

        Assert.True(Hover(window, start), "подсказка доступной кнопки не открылась по наведению");
        Assert.Equal(PanelStrings.TipStartServerButton, ToolTip.GetTip(start) as string);

        Assert.True(
            Hover(window, stop),
            "подсказка НЕДОСТУПНОЙ кнопки не открылась: без ShowOnDisabled серая кнопка молчит");

        Assert.Equal(PanelStrings.TipStopServerNothing, ToolTip.GetTip(stop) as string);

        // Контроль: выключим показ у недоступной — и подсказка обязана исчезнуть из живого пути.
        window.MouseMove(new Point(2, 2));
        PanelTestStand.Settle();
        ToolTip.SetShowOnDisabled(stop, false);

        Assert.False(
            Hover(window, stop),
            "подсказка открылась даже с выключенным показом у недоступной кнопки — " +
            "значит она открывается не тем путём, и проверка выше ничего не доказывает");

        window.Close();
    }

    /// <summary>
    /// Навести указатель на кнопку и дождаться, пока каркас откроет подсказку.
    ///
    /// ⚠️ Указатель сперва вводится в окно, потом наводится на кнопку, и если подсказка не
    /// открылась — уводится в угол и наводится СНОВА. Это не «попытка на всякий случай»:
    /// на первой же пробе выяснилось, что ПЕРВОЕ движение указателя в безоконном прогоне
    /// не поднимает «вошёл в орган», а второе, после выхода, показывает. Проверка от этого
    /// не слабеет: она по-прежнему смотрит, что каркас ОТКРЫВАЕТ подсказку по наведению.
    /// </summary>
    private static bool Hover(Window window, Button button)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            window.MouseMove(new Point(2, 2));
            PanelTestStand.Settle();

            var center = button.TranslatePoint(
                new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);

            Assert.NotNull(center);

            window.MouseMove(center!.Value);

            for (var tick = 0; tick < 40; tick++)
            {
                PanelTestStand.Settle();

                if (ToolTip.GetIsOpen(button)) return true;

                Thread.Sleep(20);
            }
        }

        return false;
    }
}
