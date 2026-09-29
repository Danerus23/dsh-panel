using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕРЬ В ОКНО «ПИКИ И ТАРИФЫ» — та же, что у настроек, копий и «О программе».
///
/// Решение дирижёра 27.09.2026 (предложение владельцу): таблица окон пика живёт в ОТДЕЛЬНОМ
/// окне, а не в выезжающей справа панели, — потому что у панели уже принят порядок «дверь
/// открывает своё окно» (<see cref="SingleWindowSlot"/>), главное окно и так прокручивается,
/// и окно можно однажды открыть ещё и из меню значка.
///
/// Отсюда три обещания, и все три проверяются ЗДЕСЬ, а не глазами:
///
/// 1. **второго такого окна не бывает**: повторная просьба выводит на передний план уже
///    открытое, а не строит второе (иначе человек получил бы две таблицы над одним агентом);
/// 2. **закрытое окно не остаётся «открытым»**: следующая просьба строит его заново, а не
///    выводит на передний план то, чего уже нет (иначе окно перестало бы открываться вовсе);
/// 3. **окно недоступно — панель ГОВОРИТ это строкой в журнал**, а не молчит: молчание
///    на нажатие читается как поломка панели, и человек ищет дефект там, где его нет.
///
/// ⚠️ Значок в проверке НЕ поднимается (<see cref="TrayIconHost.Show"/> не зовётся): он ставится
/// на рабочий стол владельца, а проверка не имеет права его туда ставить. Связка строится
/// без значка — этого хватает: слот и журнал от него не зависят.
/// </summary>
public class ShellPeaksDoorTests
{
    /// <summary>Связка без значка и без показанного окна: только слот двери и журнал.</summary>
    private static (PanelShell Shell, List<string> Log) Stand(
        Func<Window>? createPeaks,
        Func<Window>? createUpdate = null)
    {
        var log = new List<string>();
        var tray = new TrayIconHost("проверка");
        var window = new MainWindow();
        var panel = new PanelWindow(window, isolated: false, log.Add);

        var shell = new PanelShell(
            tray,
            panel,
            new ClassicDesktopStyleApplicationLifetime(),
            log.Add,
            isolated: false,
            mayOpenBrowser: false,
            entryLink: () => string.Empty,
            trayStatus: () => new TrayStatusLines(
                new TrayStatusLine("сервер", TrayTone.Neutral),
                new TrayStatusLine("агент", TrayTone.Neutral),
                new TrayStatusLine("тариф", TrayTone.Neutral),
                new TrayStatusLine("обновление", TrayTone.Neutral)),
            notify: (_, _, _) => { },
            createAbout: () => new AboutWindow(),
                createIssue: () => new IssueWindow(),
            checkUpdate: () => { },
            createPeaks: createPeaks,
            createUpdate: createUpdate);

        return (shell, log);
    }

    /// <summary>
    /// ДВЕРЬ ОТКРЫВАЕТСЯ РОВНО ОДНИМ ОКНОМ. Проверка идёт через НАСТОЯЩУЮ связку (без значка)
    /// и настоящий слот: «второе окно» на подставном слоте не поймать — там проверялся бы слот,
    /// а не дверь панели.
    /// </summary>
    [AvaloniaFact]
    public void Дверь_открывает_одно_окно_и_не_заводит_второго()
    {
        var built = new List<Window>();
        var (shell, log) = Stand(() => { var window = new Window(); built.Add(window); return window; });

        using (shell)
        {
            Assert.False(shell.PeaksOpen, "окно ещё не открывали, а слот считает его открытым");

            shell.OpenPeaks();

            Assert.True(shell.PeaksOpen, "окно открыто, а слот его не видит");
            Assert.Single(built);
            Assert.Contains(PanelStrings.PeakOpenedLog, log);

            // Вторая просьба — на передний план уже открытое, а не второе окно. Это и есть
            // решение «окно одно на панель»: две таблицы над одним агентом человеку не нужны.
            shell.OpenPeaks();

            Assert.Single(built);
            Assert.Contains(PanelStrings.PeakRaisedLog, log);

            // Человек закрыл окно — дверь снова строит его, а не выводит на передний план то,
            // чего уже нет: слот, забывший о закрытии, оставил бы окно неоткрывающимся навсегда.
            built[0].Close();

            Assert.False(shell.PeaksOpen, "окно закрыто, а слот всё ещё считает его открытым");

            shell.OpenPeaks();

            Assert.Equal(2, built.Count);
            Assert.True(shell.PeaksOpen);
        }
    }

    /// <summary>
    /// ДВЕРЬ, КОТОРОЙ НЕЧЕМ ОТКРЫТЬ, ГОВОРИТ ЭТО СЛОВАМИ. У панели без домена баланса (прогон)
    /// построителя окон нет вовсе — и тогда журнал обязан назвать это, а не промолчать.
    /// </summary>
    [AvaloniaFact]
    public void Дверь_без_построителя_называет_отказ_строкой()
    {
        var (shell, log) = Stand(createPeaks: null);

        using (shell)
        {
            shell.OpenPeaks();

            Assert.False(shell.PeaksOpen);
            Assert.Contains(PanelStrings.PanelLogPeaksUnavailable, log);
            Assert.DoesNotContain(PanelStrings.PeakOpenedLog, log);
        }
    }

    /// <summary>
    /// ДВЕРЬ В ОКНО ОБНОВЛЕНИЯ — ТО ЖЕ ПРАВИЛО, ЧТО У «ПИКОВ»: одно окно на панель, повторная
    /// просьба выводит на передний план уже открытое. Это не формальность — в окне обновления
    /// живёт кнопка, запускающая замену файлов, и два таких окна означали бы две кнопки над одним
    /// и тем же сценарием, какая из них «настоящая» — человеку не понять.
    ///
    /// ⚠️ И вторая половина обещания: у панели БЕЗ построителя дверь говорит об отказе словами,
    /// а не молчит (так выглядит прогон, где замену файлов готовить нечем).
    /// </summary>
    [AvaloniaFact]
    public void Дверь_обновления_открывает_одно_окно_и_говорит_об_отказе()
    {
        var built = new List<Window>();
        var (shell, log) = Stand(
            createPeaks: null,
            createUpdate: () => { var window = new UpdateWindow(); built.Add(window); return window; });

        using (shell)
        {
            Assert.False(shell.UpdateOpen, "окно ещё не открывали, а слот считает его открытым");

            shell.OpenUpdate();

            Assert.True(shell.UpdateOpen, "окно открыто, а слот его не видит");
            Assert.Single(built);
            Assert.Contains(PanelStrings.UpdateOpenedLog, log);

            shell.OpenUpdate();

            Assert.Single(built);
            Assert.Contains(PanelStrings.UpdateRaisedLog, log);

            // Человек закрыл окно — дверь снова строит его, а не выводит на передний план то,
            // чего уже нет.
            built[0].Close();

            Assert.False(shell.UpdateOpen, "окно закрыто, а слот всё ещё считает его открытым");

            shell.OpenUpdate();

            Assert.Equal(2, built.Count);
        }

        var (without, refused) = Stand(createPeaks: null);

        using (without)
        {
            without.OpenUpdate();

            Assert.False(without.UpdateOpen);
            Assert.Contains(PanelStrings.PanelLogUpdateWindowUnavailable, refused);
        }
    }
}
