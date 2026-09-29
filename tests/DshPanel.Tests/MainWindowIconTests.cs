using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЗНАЧКИ НА КНОПКАХ ГЛАВНОГО ОКНА — запрос владельца 28.09.2026: *«Можно ли на кнопках основного
/// окна как то приукрасить? Может значки какие то предложишь»*.
///
/// Два обещания, и оба про то, что видно:
///
/// 1. **у кнопки есть и значок, и подпись** — значок помогает глазу найти кнопку, а не заменяет
///    слово: кнопка без текста была бы загадкой;
/// 2. **значки разные там, где действия разные.** Одинаковый значок у «Запустить» и «Остановить»
///    не украшение, а путаница: человек ищет кнопку по форме.
/// </summary>
public class MainWindowIconTests
{
    /// <summary>Сервер не запущен — состояние, в котором панель только что построена.</summary>
    private static readonly ServerState Stopped = ServerState.Stopped(3081);

    /// <summary>Сервер работает: тогда кнопки «Остановить» и «Перезапустить» меняют подписи.</summary>
    private static readonly ServerState Running =
        new(ServerPresence.Running, 3081, 4242, "node", "сервер отвечает");

    private static MainWindow Window(ServerState state) =>
        PanelTestStand.MainStand(state);

    /// <summary>Подписи кнопок, которые обязаны нести значок, — по ИМЕНИ органа.</summary>
    private static readonly string[] IconButtons =
    {
        "StartServerButton", "RestartServerButton", "StopServerButton",
        "OpenAgentButton", "RefreshBalanceButton", "PeakButton",
        "SettingsButton", "BackupsButton",
    };

    /// <summary>
    /// КАЖДАЯ КНОПКА НЕСЁТ ЗНАЧОК И ПОДПИСЬ. Одного значка мало (непонятно), одной подписи —
    /// тоже (владелец просил украсить). Проверка меряет НАСТОЯЩИЕ органы в показанном окне.
    /// </summary>
    [AvaloniaFact]
    public void У_кнопок_главного_окна_есть_значок_и_подпись()
    {
        var window = Window(Stopped);
        window.Show();
        PanelTestStand.Settle();

        foreach (var name in IconButtons)
        {
            var button = window.FindControl<Button>(name);

            Assert.True(button is not null, $"кнопки «{name}» нет в окне");

            var icon = PanelTestStand.Icon(button!);
            var label = PanelTestStand.Label(button!);

            Assert.True(icon is not null, $"у кнопки «{name}» нет значка");
            Assert.NotNull(icon!.Data);
            Assert.True(
                icon.Data!.Bounds.Width > 0 && icon.Data.Bounds.Height > 0,
                $"значок кнопки «{name}» пустой по размерам: {icon.Data.Bounds}");

            Assert.False(string.IsNullOrWhiteSpace(label), $"у кнопки «{name}» нет подписи");
        }

        window.Close();
    }

    /// <summary>
    /// ЗНАЧКИ РАЗНЫХ ДЕЙСТВИЙ РАЗЛИЧАЮТСЯ. «Запустить» и «Остановить» с одним значком были бы
    /// не украшением, а путаницей: форма — то, по чему кнопку находят, не читая.
    /// </summary>
    [AvaloniaFact]
    public void Значки_разных_действий_различаются()
    {
        var window = Window(Running);
        window.Show();
        PanelTestStand.Settle();

        // ⚠️ Формы сравниваются ПО ГРАНИЦАМ, а не `ToString()`: у графического примитива
        // `ToString()` отдаёт ИМЯ ТИПА («Avalonia.Media.StreamGeometry»), и сравнение выходило бы
        // «равны» для любых двух значков. На этом проверка попалась дважды — сначала на органе,
        // потом на самой форме.
        var play = PanelGlyph.Play.Bounds;
        var stop = PanelGlyph.Stop.Bounds;
        var restart = PanelGlyph.Restart.Bounds;
        var refresh = PanelGlyph.Circle.Bounds;

        Assert.NotEqual(play, stop);
        Assert.NotEqual(play, restart);
        Assert.NotEqual(stop, restart);

        // «Обновить баланс» — тоже со своей формой: круговая стрелка не должна совпасть
        // ни с одной из кнопок сервера.
        Assert.NotEqual(refresh, play);
        Assert.NotEqual(refresh, stop);
        Assert.NotEqual(refresh, restart);

        // И на экране у четырёх кнопок четыре РАЗНЫХ органа-значка: форма доехала до окна,
        // а не осталась в описании.
        var shown = new[] { "StartServerButton", "RestartServerButton", "StopServerButton", "RefreshBalanceButton" }
            .Select(name => PanelTestStand.Icon(window.FindControl<Button>(name)!)!.Data)
            .Distinct()
            .Count();

        Assert.Equal(4, shown);

        // И у дверей значки тоже разные: копии, настройки и пики — разные вещи.
        var doors = new[] { "BackupsButton", "SettingsButton", "PeakButton" }
            .Select(name => PanelTestStand.Icon(window.FindControl<Button>(name)!)!.Data)
            .Distinct()
            .Count();

        Assert.Equal(3, doors);

        window.Close();
    }

    /// <summary>
    /// ПОДПИСЬ КНОПКИ СЕРВЕРА МЕНЯЕТСЯ ВМЕСТЕ СО ЗНАЧКОМ. У найденного и взятого под управление
    /// сервера «Остановить» и «Перезапустить» спрашивают подтверждения и подписываются иначе —
    /// и после смены состояния ЗНАЧОК ОБЯЗАН ОСТАТЬСЯ: прямое присваивание `Content` стёрло бы
    /// его и оставило голый текст.
    ///
    /// ⚠️ Состояние берётся ТО, в котором подпись меняется: без подтверждения подписи одинаковы,
    /// и проверка зеленела бы на сломанном коде (первый прогон так и вышел).
    /// </summary>
    [AvaloniaFact]
    public void После_смены_состояния_значок_остаётся()
    {
        var stub = new PanelTestStand.StubServer(ServerState.Stopped(3081));
        var window = PanelTestStand.MainWith(stub);

        window.Show();
        PanelTestStand.Settle();

        var before = PanelTestStand.Label(window.FindControl<Button>("StopServerButton")!);

        Assert.NotNull(PanelTestStand.Icon(window.FindControl<Button>("StopServerButton")!));

        // Сервер работает и он ВСТРОЕННЫЙ (взят под управление, подтверждения ещё не запоминали):
        // кнопки обязаны предложить спросить — «Остановить…» вместо «Остановить».
        var adopted = new PanelTestStand.StubServer(
            new ServerState(ServerPresence.Running, 3081, 4242, "node", "сервер отвечает"),
            ServerOwner.Adopted,
            consent: false);

        var second = PanelTestStand.MainWith(adopted);
        second.Show();
        PanelTestStand.Settle();

        var after = PanelTestStand.Label(second.FindControl<Button>("StopServerButton")!);

        Assert.NotEqual(before, after);

        var stop = second.FindControl<Button>("StopServerButton")!;

        Assert.NotNull(PanelTestStand.Icon(stop));
        Assert.False(string.IsNullOrWhiteSpace(PanelTestStand.Label(stop)));

        second.Close();
        window.Close();
    }
}
