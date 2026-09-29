using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ГДЕ КАКАЯ ДВЕРЬ В ГЛАВНОМ ОКНЕ. Решения владельца 27.09.2026:
///
/// * *«кнопка настройки не должна быть в одном ряду с кнопками управления сервером»*;
/// * *«„о программе“ не туда воткнул. Предлагаю перенести вообще в настройки»*.
///
/// Отсюда три обещания, и каждое проверяется отдельно:
///
/// 1. **ряд управления сервером — только про сервер**: «Запустить», «Остановить», «Отвязаться».
///    Ни настроек, ни копий рядом с «Запустить» нет;
/// 2. **шапка**: слева имя панели, справа «Копии и восстановление…» и «Настройки…» — одним рядом;
/// 3. **двери «О программе» в этом окне БОЛЬШЕ НЕТ** — она переехала в настройки, и её место
///    сторожит <c>AboutDoorTests</c>: дверь, оставшаяся в двух окнах, вернула бы человеку
///    ту самую путаницу, из-за которой её и переносили.
///
/// ⚠️ Проверяется и ГЕОМЕТРИЯ, а не только родство в дереве: двери могут быть «не в ряду
/// сервера» и всё же стоять рядом с «Запустить» по вертикали — этого владелец и не хотел.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class WindowDoorsTests
{
    /// <summary>Окно панели с найденным чужим сервером: тогда видны ВСЕ кнопки, включая встраивание.</summary>
    private static MainWindow Stand()
    {
        var window = PanelTestStand.MainWith(new PanelTestStand.StubServer(ServerState.Stopped(0))
        {
            Found = new FoundServer(3081, 4242, "node"),
        });

        window.Show();
        PanelTestStand.Settle();

        return window;
    }

    private static Button Require(Window window, string name)
    {
        var button = window.FindControl<Button>(name);
        Assert.NotNull(button);

        return button!;
    }

    /// <summary>
    /// РЯД УПРАВЛЕНИЯ СЕРВЕРОМ — ТОЛЬКО ПРО СЕРВЕР. Это первое, что сказал владелец: настройки
    /// не должны стоять в одном ряду с кнопками управления сервером, и рядом с «Запустить»
    /// не должно быть ни настроек, ни копий.
    /// </summary>
    [AvaloniaFact]
    public void В_ряду_управления_сервером_нет_посторонних_кнопок()
    {
        var window = Stand();

        var start = Require(window, "StartServerButton");
        var row = start.Parent as WrapPanel;

        Assert.NotNull(row);

        var inRow = row!.Children
            .OfType<Button>()
            .Select(button => button.Name)
            .Where(name => name is not null)
            .ToList();

        Assert.Equal(
            new[] { "StartServerButton", "RestartServerButton", "StopServerButton", "DetachServerButton" },
            inRow);

        // И обратная сторона обещания: дверей в ряду сервера нет НИ ОДНОЙ — ни в ряду, ни где-то
        // ещё внутри карточки сервера (карточка — предок ряда).
        var empty = new List<string>();

        foreach (var name in new[] { "SettingsButton", "BackupsButton" })
        {
            var button = Require(window, name);

            if (row.Children.Contains(button) ||
                button.GetVisualAncestors().Contains(row.Parent))
            {
                empty.Add(name);
            }
        }

        Assert.True(
            empty.Count == 0,
            "двери стоят в карточке сервера: " + string.Join(", ", empty) +
            ". Ряд управления сервером и ряд дверей больше не смешиваются.");

        window.Close();
    }

    /// <summary>
    /// ШАПКА: слева имя панели, СПРАВА «Копии и восстановление…» и «Настройки…» — одним рядом.
    /// Справа значит «правее имени» и «в правой колонке», а не «где-то в окне».
    /// </summary>
    [AvaloniaFact]
    public void В_шапке_справа_копии_и_настройки_а_слева_имя_панели()
    {
        var window = Stand();

        var title = window.FindControl<TextBlock>("AppTitleText");
        var backups = Require(window, "BackupsButton");
        var settings = Require(window, "SettingsButton");

        Assert.NotNull(title);

        // Обе двери — соседи в ОДНОМ ряду: две разные двери в двух разных углах шапки
        // читались бы как разные по смыслу.
        var row = backups.Parent as StackPanel;
        Assert.NotNull(row);
        Assert.Same(row, settings.Parent);

        var grid = row!.Parent as Grid;
        Assert.NotNull(grid);

        // Имя панели — в первой колонке, двери — во второй.
        Assert.Equal(0, Grid.GetColumn(title!));
        Assert.Equal(1, Grid.GetColumn(row));

        // ⚠️ Имя стоит ВНУТРИ знака-логотипа (решение владельца 28.09.2026, вариант A): в первой
        // колонке теперь не голый TextBlock, а строка «знак + имя». Поэтому проверяется не
        // «имя лежит прямо в сетке» — это перестало быть правдой, — а «имя лежит в первой колонке»:
        // подъём по родителям до самой сетки. Так проверка переживёт и следующую правку вида.
        var column = (Control?)title;

        while (column is not null && !ReferenceEquals(column, grid))
        {
            column = column.Parent as Control;
        }

        Assert.True(ReferenceEquals(column, grid), "имя панели уехало из первой колонки шапки");

        // И знак рядом с именем — часть того же логотипа: монограмма в первой колонке.
        Assert.NotNull(window.FindControl<TextBlock>("LogoMonogramText"));

        // Геометрия: двери ПРАВЕЕ имени и на одной с ним высоте (шапка — один ряд, не два).
        var titleTop = title.TranslatePoint(new Point(0, 0), window);
        var backupsTop = backups.TranslatePoint(new Point(0, 0), window);
        var settingsTop = settings.TranslatePoint(new Point(0, 0), window);

        Assert.NotNull(titleTop);
        Assert.NotNull(backupsTop);
        Assert.NotNull(settingsTop);

        var where = $"имя X={titleTop!.Value.X:0.#}, копии X={backupsTop!.Value.X:0.#}, " +
                    $"настройки X={settingsTop!.Value.X:0.#}, ширина окна {window.ClientSize.Width:0.#}";

        Assert.True(backupsTop.Value.X > titleTop.Value.X, $"«Копии…» не правее имени панели — {where}");
        Assert.True(settingsTop.Value.X > backupsTop.Value.X, $"«Настройки…» не правее «Копий…» — {where}");

        // Двери и имя панели стоят В ОДНОЙ строке шапки: их полосы по вертикали перекрываются,
        // а не идут одна под другой. Сравнивать верхние края нельзя: у кнопки и строки текста
        // разная высота, и «не тот же Y» ничего не значило бы.
        var titleBottom = titleTop.Value.Y + title.Bounds.Height;
        var doorsBottom = backupsTop.Value.Y + backups.Bounds.Height;

        Assert.True(
            backupsTop.Value.Y < titleBottom && titleTop.Value.Y < doorsBottom,
            $"имя панели и двери не в одной строке шапки — {where}, " +
            $"имя: {titleTop.Value.Y:0.#}…{titleBottom:0.#}, двери: {backupsTop.Value.Y:0.#}…{doorsBottom:0.#}");

        // Обе внутри окна: дверь, уехавшая за край, — это недостижимая дверь (дефект Д2).
        Assert.True(
            settingsTop.Value.X + settings.Bounds.Width <= window.ClientSize.Width + 0.5,
            $"двери шапки не вмещаются в окно — {where}");

        window.Close();
    }

    /// <summary>
    /// ДВЕРИ «О ПРОГРАММЕ» В ГЛАВНОМ ОКНЕ БОЛЬШЕ НЕТ (слова владельца: «„о программе“ не туда
    /// воткнул. Предлагаю перенести вообще в настройки»). Проверка именно про ОТСУТСТВИЕ:
    /// дверь, оставшаяся тут «на всякий случай», вернула бы ту самую путаницу, из-за которой
    /// её переносили.
    ///
    /// ⚠️ Ищется по ИМЕНИ органа, а не по подписи: подпись переводят, и поиск по ней перестал бы
    /// что-либо значить на английском языке. Где дверь теперь — <c>AboutDoorTests</c>.
    /// </summary>
    [AvaloniaFact]
    public void В_главном_окне_двери_о_программе_больше_нет()
    {
        var window = Stand();

        Assert.Null(window.FindControl<Control>("AboutButton"));

        // Прежнее место двери (нижний левый угол содержимого) занято карточкой баланса: там,
        // где стояла кнопка, теперь ничего не осталось — «пустое место» тут не дефект, а след
        // переезда, и проверка называет его прямо.
        var main = window.FindControl<StackPanel>("MainContent");
        Assert.NotNull(main);

        var buttons = main!.Children.OfType<Button>().Select(button => button.Name).ToList();

        Assert.DoesNotContain("AboutButton", buttons);

        window.Close();
    }

    /// <summary>
    /// ЩЕЛЧОК ПО КАЖДОЙ ДВЕРИ ПОДНИМАЕТ СВОЮ ПРОСЬБУ РОВНО ОДИН РАЗ — и подписи у дверей
    /// из строк панели. Ворота «нет текста в разметке» имена кнопок не защищают: имя совпадает
    /// с <c>x:Name</c>, и подпись обязана подтверждать проверка.
    /// </summary>
    [AvaloniaFact]
    public void Каждая_дверь_подписана_и_поднимает_свою_просьбу()
    {
        var window = Stand();

        var settings = Require(window, "SettingsButton");
        var backups = Require(window, "BackupsButton");

        Assert.Equal(PanelStrings.SettingsMenuText, PanelTestStand.Label(settings));
        Assert.Equal(PanelStrings.BackupsButton, PanelTestStand.Label(backups));

        var asked = (Settings: 0, Backups: 0);

        window.SettingsRequested += () => asked.Settings++;
        window.BackupsRequested += () => asked.Backups++;

        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        backups.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal((1, 1), asked);

        window.Close();
    }
}
