using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// DEF-061 (2.4.2): СВЕРНУЛ ПАНЕЛЬ — СПУТНИКИ СПРЯТАЛИСЬ, РАЗВЕРНУЛ — ВЕРНУЛИСЬ ТЕ ЖЕ.
///
/// **От какого случая.** Владелец 24.10.2026 на 2.4.1: свернул окно панели — настройки и копии
/// остались висеть развёрнутыми; развернул — висят; крестик (уход в трей) — прячутся оба и
/// возвращаются оба. Причина: реакции на `WindowState.Minimized` не было ВООБЩЕ, спутники прятало
/// только `PanelWindow.Hidden` (уход в трей).
///
/// **Почему это наша работа, а не системы.** Спутники намеренно БЕЗ владельца (`О-8`: с владельцем
/// появилась бы вторая кнопка на панели задач), а норма `О-6` («owned-окна прячутся при минимизации
/// владельца и показываются при восстановлении») без владельца не работает вовсе. Банк назвал цену
/// заранее: «скрытие при минимизации панели надо написать самим» (`95-windows.md` §3.2).
///
/// **Что проверяется и чем.** Стенд — НАСТОЯЩИЕ `PanelWindow`, `PanelShell`, `SingleWindowSlot`,
/// `SatelliteSet` и настоящие окна: двери те же, что у человека, включая проводку в конструкторе
/// связки. Состояние меняется так, как его меняет человек: `WindowState` окна панели.
///
/// **Мутации, которые обязаны упасть:**
/// M1 «не прятать при минимизации» (убрать `_window.Minimized = …` в связке) → красная
/// <see cref="Сворачивание_прячет_спутники_а_не_закрывает"/>;
/// M2 «при восстановлении открывать заново» (закрыть спутник и построить новое окно) → красная
/// <see cref="Разворот_возвращает_те_же_окна"/> — построенных окон стало бы два.
///
/// ⚠️ **Чего эти проверки НЕ доказывают:** числа кнопок на панели задач и настоящий z-порядок —
/// это состояние ОС, а не свойства окна (`О-17`). Их показывает живая проба `--windows-selftest`
/// (шаги «свернуть/развернуть» + число кнопок ДО и ПОСЛЕ).
/// </summary>
public class SatelliteMinimizeTests
{
    /// <summary>
    /// СТЕНД: панель, связка и ДВА спутника — ровно те двери, которыми ходит человек.
    ///
    /// Спутники — подставные окна, а не настоящие настройки: проверяется прятание и возврат,
    /// а содержимое окон к делу не относится (у настоящих настроек своя цена — `PanelTestStand`).
    /// Каждое построенное окно запоминается: «открыли заново» видно числом построек.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        public readonly List<string> Log = new();
        public readonly List<Window> Built = new();

        public readonly Window PanelWindow;
        public readonly PanelWindow Panel;
        public readonly PanelShell Shell;

        public Stand()
        {
            PanelWindow = new Window { Width = 320, Height = 240 };
            Panel = new PanelWindow(PanelWindow, isolated: false, log: Log.Add);

            Shell = new PanelShell(
                new TrayIconHost("проверка"),
                Panel,
                new ClassicDesktopStyleApplicationLifetime(),
                Log.Add,
                isolated: false,
                mayOpenBrowser: false,
                entryLink: () => string.Empty,
                trayStatus: () => new TrayStatusLines(
                    new TrayStatusLine("сервер", TrayTone.Neutral),
                    new TrayStatusLine("агент", TrayTone.Neutral),
                    new TrayStatusLine("тариф", TrayTone.Neutral),
                    new TrayStatusLine("обновление", TrayTone.Neutral)),
                notify: (_, _, _) => { },
                createIssue: () => new IssueWindow(),
                checkUpdate: () => { },
                createSettings: () => Build(),
                createBackups: () => Build());
        }

        /// <summary>Настройки — первое построенное окно; копии — второе.</summary>
        public Window Settings => Built[0];
        public Window Backups => Built[1];

        private Window Build()
        {
            var window = new Window { Width = 300, Height = 200 };
            Built.Add(window);

            return window;
        }

        /// <summary>Сколько строк журнала содержат названную часть (проверка «подняли один раз»).</summary>
        public int Lines(string part) => Log.Count(line => line.Contains(part, StringComparison.Ordinal));

        public void Dispose()
        {
            // Связка закрывает спутники молча и снимает значок; `PanelWindow` снимает подписки
            // на `Closing` и на смену состояния окна — второго пути уборки в проверке нет.
            Shell.Dispose();
        }
    }

    /// <summary>
    /// СВЕРНУЛ ПАНЕЛЬ — СПУТНИКИ СПРЯТАНЫ, А НЕ ЗАКРЫТЫ, и кнопка на панели задач осталась у панели.
    ///
    /// **Мутация M1** «не прятать при минимизации» (убрать `_window.Minimized = …` в конструкторе
    /// связки) роняет первое же утверждение: спутники остаются видимыми поверх свёрнутой панели —
    /// ровно то, что видел владелец на 2.4.1.
    /// </summary>
    [AvaloniaFact]
    public void Сворачивание_прячет_спутники_а_не_закрывает()
    {
        using var stand = new Stand();

        stand.Panel.Show();
        stand.Shell.OpenSettings();
        stand.Shell.OpenBackups();

        Assert.True(stand.Settings.IsVisible, "спутник настроек не показался — стенд не собран");
        Assert.True(stand.Backups.IsVisible, "спутник копий не показался — стенд не собран");

        // Человек нажал «свернуть» на заголовке панели. Это НЕ крестик: окно остаётся на панели
        // задач, но спутники по норме `О-6` обязаны спрятаться.
        stand.PanelWindow.WindowState = WindowState.Minimized;

        Assert.False(stand.Settings.IsVisible);
        Assert.False(stand.Backups.IsVisible);

        // Спрятать — не закрыть: слоты помнят окна, и при развороте вернётся то же самое.
        Assert.True(stand.Shell.SettingsOpen, "настройки закрылись, а должны были только спрятаться");
        Assert.True(stand.Shell.BackupsOpen, "копии закрылись, а должны были только спрятаться");

        // Кнопка на панели задач остаётся у ПАНЕЛИ (`О-7`): сворачивание её не отбирает,
        // и спрятанные спутники своей не дают вовсе (это проверяет `SatelliteWindowsTests`).
        Assert.True(stand.PanelWindow.ShowInTaskbar);

        // Повод назван в журнале отдельно от ухода в трей: по строке видно, что человек СВЕРНУЛ.
        Assert.Equal(1, stand.Lines("панель свернулась: спутников спрятано 2"));
    }

    /// <summary>
    /// РАЗВЕРНУЛ ПАНЕЛЬ — ВЕРНУЛИСЬ ТЕ ЖЕ ОКНА, а не «найти и открыть заново».
    ///
    /// **Мутация M2** «при восстановлении открывать заново» (закрыть спутник и построить новый)
    /// роняет `Assert.Equal(2, stand.Built.Count)`: построек стало бы три.
    /// </summary>
    [AvaloniaFact]
    public void Разворот_возвращает_те_же_окна()
    {
        using var stand = new Stand();

        stand.Panel.Show();
        stand.Shell.OpenSettings();
        stand.Shell.OpenBackups();

        var settings = stand.Settings;
        var backups = stand.Backups;

        stand.PanelWindow.WindowState = WindowState.Minimized;
        stand.PanelWindow.WindowState = WindowState.Normal;

        Assert.True(settings.IsVisible, "окно настроек не вернулось при развороте панели");
        Assert.True(backups.IsVisible, "окно копий не вернулось при развороте панели");

        // Ни одного нового окна: вернулись ТЕ ЖЕ объекты, второго построения не было.
        Assert.Equal(2, stand.Built.Count);
        Assert.Same(settings, stand.Settings);
        Assert.Same(backups, stand.Backups);

        Assert.Equal(1, stand.Lines("панель развернулась: спутников возвращено 2"));
    }

    /// <summary>
    /// ВОЗВРАТ ЧЕРЕЗ ПОКАЗ ПАНЕЛИ НЕ ПОДНИМАЕТ ОКНА ДВАЖДЫ (щелчок по значку при свёрнутой панели).
    ///
    /// `PanelWindow.Show()` сам снимает `Minimized` — значит переход состояния поднимает возврат
    /// спутников, и только ПОСЛЕ него приходит `Shown`. Второй возврат обязан достаться пустому
    /// списку: строка «возвращено» в журнале ровно одна, построек — ни одной новой.
    /// </summary>
    [AvaloniaFact]
    public void Возврат_через_показ_панели_не_поднимает_дважды()
    {
        using var stand = new Stand();

        stand.Panel.Show();
        stand.Shell.OpenSettings();

        stand.PanelWindow.WindowState = WindowState.Minimized;
        Assert.False(stand.Settings.IsVisible);

        stand.Shell.ShowPanel(); // щелчок по значку: Show() снимает сворачивание

        Assert.True(stand.Settings.IsVisible);
        Assert.Equal(1, stand.Lines("панель развернулась: спутников возвращено 1"));
        Assert.Single(stand.Built); // новых окон не строили
    }

    /// <summary>
    /// ЗАКРЫТОЕ ЧЕЛОВЕКОМ ВО ВРЕМЯ СВОРАЧИВАНИЯ НЕ ВСПЛЫВАЕТ: пока панель была свёрнута, человек
    /// мог закрыть окно — возвращать закрытое значило бы отменить его действие.
    /// </summary>
    [AvaloniaFact]
    public void Закрытое_во_время_сворачивания_не_всплывает()
    {
        using var stand = new Stand();

        stand.Panel.Show();
        stand.Shell.OpenSettings();
        stand.Shell.OpenBackups();

        stand.PanelWindow.WindowState = WindowState.Minimized;

        // Человек закрыл настройки, пока панель была свёрнута.
        stand.Settings.Close();
        Assert.False(stand.Shell.SettingsOpen);

        stand.PanelWindow.WindowState = WindowState.Normal;

        Assert.False(stand.Settings.IsVisible, "закрытое человеком окно всплыло при развороте панели");
        Assert.False(stand.Shell.SettingsOpen);
        Assert.True(stand.Backups.IsVisible, "второй спутник обязан вернуться");

        Assert.Equal(1, stand.Lines("панель развернулась: спутников возвращено 1"));
    }
}
