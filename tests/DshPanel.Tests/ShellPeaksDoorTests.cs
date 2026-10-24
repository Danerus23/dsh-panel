using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Update;
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
        Func<Window>? createSettings = null)
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
                createIssue: () => new IssueWindow(),
            checkUpdate: () => { },
            createPeaks: createPeaks,
            createSettings: createSettings);

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
    /// ПУНКТ МЕНЮ ЗНАЧКА «ОБНОВЛЕНИЕ ПАНЕЛИ…» ОТКРЫВАЕТ РАЗДЕЛ «ОБНОВЛЕНИЕ» НАСТРОЕК.
    ///
    /// Решение владельца 09.10.2026, дословно: *«Это не отдельное окно должно быть… Я не хочу
    /// отдельно вызываемое окно»*. Прежде этот пункт открывал своё окно (`PanelShell.OpenUpdate`),
    /// и у него был свой слот; теперь он ведёт в НАСТРОЙКИ — и проверяется это НАСТОЯЩИМ путём:
    /// меню собирает сама связка (<see cref="PanelShell.BuildMenu"/>), пункт берётся из него,
    /// щелчок по нему — вызов настоящего действия двери, а раздел читается у открытого окна.
    /// Список действий, собранный проверкой заново, ничего бы не доказал.
    ///
    /// ⚠️ И вторая половина обещания: настройки, УЖЕ открытые на другом разделе, обязаны
    /// переключиться на «Обновление» — иначе человек, у которого настройки открыты, нажал бы
    /// пункт и не увидел обновления. Второго окна настроек при этом не заводится.
    /// </summary>
    [AvaloniaFact]
    public void Пункт_меню_обновления_открывает_раздел_обновления_в_настройках()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var built = new List<Window>();
            SettingsWindow? settings = null;

            var (shell, _) = Stand(
                createPeaks: null,
                createSettings: () =>
                {
                    settings = PanelTestStand.SettingsStand(dir, update: new SilentUpdate());
                    built.Add(settings);

                    return settings;
                });

            using (shell)
            {
                var menu = shell.BuildMenu();
                var item = menu.Items[10];

                Assert.False(item.IsSeparator);
                Assert.Equal(PanelStrings.UpdateSectionMenuText, item.Text);
                Assert.NotNull(item.Invoke);

                Assert.False(shell.SettingsOpen, "настройки ещё не открывали, а слот считает их открытыми");

                // Щелчок человека по пункту меню — настоящее действие настоящей связки.
                item.Invoke!();

                Assert.True(shell.SettingsOpen, "пункт меню не открыл настройки");
                Assert.Single(built);
                Assert.Equal(SettingsWindow.UpdateSection, settings!.VisibleSection);

                // Второй щелчок при открытых настройках на ДРУГОМ разделе: окно то же, раздел —
                // тот, за которым человек шёл.
                settings.ShowSection(SettingsWindow.BackupSection);

                Assert.Equal(SettingsWindow.BackupSection, settings.VisibleSection);

                shell.BuildMenu().Items[10].Invoke!();

                Assert.Single(built);
                Assert.Equal(SettingsWindow.UpdateSection, settings.VisibleSection);
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Состояние выпусков для раздела: сети нет вовсе, и разделу этого довольно — пункту меню
    /// важно не «что показано», а «какой раздел открыт».
    /// </summary>
    private sealed class SilentUpdate : IUpdateControl
    {
        public UpdateRelease Result { get; } = UpdateRelease.Failed("проверка не делалась", DateTimeOffset.Now);

        public bool Busy => false;

        public bool Allowed => false;

        public string Current => "2.0.0";

        public bool Newer => false;

        public bool Skipped => false;

        public string SourceText => string.Empty;

        public string StatusText => string.Empty;

        /// <summary>Попытки не было — строки о ней нет (дверь молчит, и это её ответ).</summary>
        public string AttemptText => string.Empty;

        public TrayStatusLine TrayLine => new(string.Empty, TrayTone.Neutral);

        public event Action<string>? Announce { add { } remove { } }

        public event Action? Changed { add { } remove { } }

        public void Check() { }
    }
}
