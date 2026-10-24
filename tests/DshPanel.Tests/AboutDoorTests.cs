using System;
using System.Collections.Generic;
using System.IO;
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
/// ДВЕРЬ «О ПРОГРАММЕ» — ПУНКТ МЕНЮ ЗНАЧКА, ВЕДУЩИЙ В РАЗДЕЛ НАСТРОЕК (u14, 2.4.0).
///
/// История решения: владелец 27.09.2026 (`docs\DECISIONS.md:32`) сказал «„о программе“ не туда
/// воткнул. Предлагаю перенести вообще в настройки». Сначала дверь переехала в настройки, но
/// открывала ОТДЕЛЬНОЕ окно; в 2.4.0 исполнено до конца — окна больше нет, ответ стал СЕДЬМЫМ
/// разделом. Здесь три обещания, и каждое проверяется прогоном, который умеет падать:
///
/// 1. **пункт меню «О программе…» открывает настройки на разделе 6** — настоящим путём:
///    меню собирает сама связка (<see cref="PanelShell.BuildMenu"/>), щелчок — вызов её двери;
/// 2. **в изолированном прогоне дверь не показывает ничего** (красная линия 8) и говорит это
///    строкой в журнал;
/// 3. **окна больше нет** — ни в дереве исходников, ни в нижнем ряду настроек, а имя кадра
///    сменилось названным образом (`settings-about`), старое `about` отвергается кодом 2.
/// </summary>
public class AboutDoorTests
{
    private static (PanelShell Shell, List<string> Log) Stand(
        Func<Window>? createSettings,
        bool isolated = false)
    {
        var log = new List<string>();
        var tray = new TrayIconHost("проверка");
        var window = new MainWindow();
        var panel = new PanelWindow(window, isolated, log.Add);

        var shell = new PanelShell(
            tray,
            panel,
            new ClassicDesktopStyleApplicationLifetime(),
            log.Add,
            isolated: isolated,
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
            createSettings: createSettings);

        return (shell, log);
    }

    /// <summary>
    /// ПУНКТ МЕНЮ ОТКРЫВАЕТ НАСТРОЙКИ НА РАЗДЕЛЕ «О ПРОГРАММЕ» — и второе окно не заводится.
    /// Если настройки уже открыты на другом разделе, дверь ПЕРЕКЛЮЧАЕТ его: иначе человек,
    /// у которого настройки открыты, нажал бы пункт и не увидел ответа.
    ///
    /// Мутация «вернуть открытие отдельного окна» роняет `Assert.Single(built)`; мутация
    /// «дверь не выбирает раздел» — `Assert.Equal(AboutSection, ...)`.
    /// </summary>
    [AvaloniaFact]
    public void Пункт_меню_открывает_раздел_о_программе_в_настройках()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var built = new List<Window>();
            SettingsWindow? settings = null;

            var (shell, _) = Stand(createSettings: () =>
            {
                settings = PanelTestStand.SettingsStand(dir);
                built.Add(settings);

                return settings;
            });

            using (shell)
            {
                var menu = shell.BuildMenu();
                var item = menu.Items[8];

                Assert.False(item.IsSeparator);
                Assert.Equal(PanelStrings.AboutButton, item.Text);
                Assert.NotNull(item.Invoke);

                Assert.False(shell.SettingsOpen, "настройки ещё не открывали, а слот считает их открытыми");

                item.Invoke!();

                Assert.True(shell.SettingsOpen, "пункт меню не открыл настройки");
                Assert.Single(built);
                Assert.Equal(SettingsWindow.AboutSection, settings!.VisibleSection);

                // Второй щелчок при открытых настройках на ДРУГОМ разделе: окно то же, раздел — тот.
                settings.ShowSection(SettingsWindow.GeneralSection);
                Assert.Equal(SettingsWindow.GeneralSection, settings.VisibleSection);

                shell.BuildMenu().Items[8].Invoke!();

                Assert.Single(built);
                Assert.Equal(SettingsWindow.AboutSection, settings.VisibleSection);
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ИЗОЛИРОВАННЫЙ ПРОГОН ВЛАДЕЛЬЦУ НЕ ПОКАЗЫВАЕТ НИЧЕГО (красная линия 8): дверь не открывает
    /// настройки и называет подавленное решение строкой в журнал — иначе «панель молчала» нечем
    /// объяснить. Мутация «снять проверку изоляции в двери» роняет оба утверждения.
    /// </summary>
    [AvaloniaFact]
    public void Изолированный_прогон_не_показывает_раздел()
    {
        var built = 0;

        var (shell, log) = Stand(
            createSettings: () =>
            {
                built++;
                return new Window();
            },
            isolated: true);

        using (shell)
        {
            shell.OpenAboutSection();

            Assert.False(shell.SettingsOpen, "изолированный прогон открыл окно владельцу");
            Assert.Equal(0, built);
            Assert.Contains(PanelStrings.WindowSuppressedLog, log);
        }
    }

    /// <summary>
    /// ДВЕРИ «О ПРОГРАММЕ» В НИЖНЕМ РЯДУ НАСТРОЕК БОЛЬШЕ НЕТ: её орган ушёл вместе с окном.
    /// В нижней строке осталась одна кнопка — «Сохранить». Проверка именно про ОТСУТСТВИЕ:
    /// кнопка, оставленная «на всякий случай», вернула бы вторую дверь в тот же ответ.
    /// </summary>
    [AvaloniaFact]
    public void В_нижнем_ряду_настроек_двери_о_программе_нет()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            PanelTestStand.Settle();

            Assert.Null(window.FindControl<Control>("AboutButton"));

            var row = window.FindControl<Button>("SaveButton")?.Parent as Grid;
            Assert.NotNull(row);

            var names = row!.Children.OfType<Button>().Select(button => button.Name).ToList();
            Assert.Equal(new[] { "SaveButton" }, names);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ОКНА «О ПРОГРАММЕ» В ДЕРЕВЕ БОЛЬШЕ НЕТ — и это проверяется ПОИСКОМ, а не словом:
    /// ни одного упоминания в исходниках и разметке. Оставленный класс окна означал бы, что
    /// решение исполнено наполовину: мёртвый орган, который однажды снова откроют.
    /// </summary>
    [Fact]
    public void Окно_о_программе_удалено_из_дерева()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            if (name.Contains("AboutWindow", StringComparison.Ordinal)) found.Add(file);
            else if (File.ReadAllText(file).Contains("AboutWindow", StringComparison.Ordinal)) found.Add(file);
        }

        Assert.Empty(found);
    }

    /// <summary>
    /// ИМЯ КАДРА СМЕНИЛОСЬ НАЗВАННЫМ ОБРАЗОМ: `settings-about` известно, старое `about` — нет
    /// (и разбор отвергает его кодом 2 со списком известных имён, а не открывает панель молча).
    /// Мутация «вернуть имя about в список» роняет обе половины.
    /// </summary>
    [Fact]
    public void Имя_кадра_settings_about_заменило_about()
    {
        Assert.True(StartModes.KnowsShotWindow(StartModes.ShotSettingsAbout));
        Assert.True(StartModes.KnowsShotWindow("settings-about"));
        Assert.False(StartModes.KnowsShotWindow("about"));

        Assert.Contains(StartModes.ShotSettingsAbout, StartModes.ShotWindows);
        Assert.DoesNotContain("about", StartModes.ShotWindows);
    }
}
