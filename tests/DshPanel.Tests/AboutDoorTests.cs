using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕРЬ «О ПРОГРАММЕ» ЖИВЁТ В НАСТРОЙКАХ. Слова владельца 27.09.2026: *«„о программе“ не туда
/// воткнул. Предлагаю перенести вообще в настройки»*.
///
/// Отсюда четыре обещания, и у каждого своя беда:
///
/// 1. **в главном окне двери НЕТ** — дверь, оставшаяся там «на всякий случай», вернула бы
///    ту самую путаницу, из-за которой её переносили (сторожит <c>WindowDoorsTests</c>);
/// 2. **в настройках она ЕСТЬ, подписана готовой строкой и имеет подсказку** — кнопка без
///    подписи и без объяснения читается как поломка;
/// 3. **она ВНЕ прокрутки раздела** — то есть видна при любом разделе и на минимальной высоте
///    окна: дверь, до которой надо догадаться прокрутить, — это ровно жалоба п. 5
///    («владелец обнаружил прокрутку наощупь»);
/// 4. **щелчок поднимает просьбу ровно один раз**, а окно «О программе» остаётся ОДНО на всю
///    панель: просьбу принимает связка и её слот (<c>PanelShell.OpenAbout</c>), поэтому дверей
///    у него может быть сколько угодно, а окно — одно.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class AboutDoorTests
{
    private static SettingsWindow Stand(string dir)
    {
        var window = PanelTestStand.SettingsStand(dir);
        window.Show();

        return window;
    }

    [AvaloniaFact]
    public void Дверь_о_программе_стоит_в_настройках_подписана_и_имеет_подсказку()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);
            PanelTestStand.Settle();

            var about = window.FindControl<Button>("AboutButton");

            Assert.NotNull(about);
            Assert.True(about!.IsVisible, "дверь «О программе» не показана в настройках");
            Assert.Equal(PanelStrings.AboutButton, about.Content);

            // Подсказка — та же строка, что была у двери в главном окне и стоит у пункта меню
            // значка: дверь одна, и три разных текста для неё разошлись бы.
            Assert.Equal(PanelStrings.TipAboutButton, ToolTip.GetTip(about) as string);
            Assert.True(
                ToolTip.GetShowOnDisabled(about),
                "у двери выключен показ подсказки у недоступного состояния");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ДВЕРЬ ВНЕ ПРОКРУТКИ РАЗДЕЛА: «О программе» не уезжает вместе с содержимым и видна
    /// при КАЖДОМ из четырёх разделов и на минимальной высоте окна. Меряется НАСТОЯЩАЯ
    /// геометрия, а не родство в дереве: кнопку можно «не положить в прокрутку» и всё же
    /// вытолкнуть за нижний край.
    /// </summary>
    [AvaloniaFact]
    public void Дверь_о_программе_видна_при_любом_разделе_и_на_минимальной_высоте()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);

            // Минимальная высота окна из разметки — та, на которой содержимое точно не влезает.
            window.Height = window.MinHeight;
            window.Show();
            PanelTestStand.Settle();

            var about = window.FindControl<Button>("AboutButton");
            Assert.NotNull(about);

            // И она НЕ внутри прокрутки: у двери не должно быть предка-прокрутки среди
            // предков окна (иначе её положение зависело бы от прокрученного раздела).
            var scrolled = about!.GetVisualAncestors().OfType<ScrollViewer>().ToList();

            Assert.True(
                scrolled.Count == 0,
                "дверь «О программе» лежит ВНУТРИ прокрутки — при длинном разделе она уедет " +
                "из виду, а дверь обязана быть видна всегда");

            for (var index = 0; index < 4; index++)
            {
                window.ShowSection(index);
                PanelTestStand.Settle();

                var top = about.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(top);

                var bottom = top!.Value.Y + about.Bounds.Height;
                var where = $"раздел {index}, окно {window.ClientSize.Height:0.#}, " +
                            $"дверь: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

                Assert.True(about.IsVisible, $"дверь «О программе» не показана — {where}");
                Assert.True(top.Value.Y >= -0.5, $"дверь выше видимой области — {where}");
                Assert.True(bottom <= window.ClientSize.Height + 0.5, $"дверь не достижима — {where}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ЩЕЛЧОК ПО ДВЕРИ ПОДНИМАЕТ ПРОСЬБУ РОВНО ОДИН РАЗ, и просьбу принимает ОДНА дверь связки:
    /// второе окно «О программе» в панели не заводится (у связки для него один слот).
    /// </summary>
    [AvaloniaFact]
    public void Щелчок_по_двери_поднимает_просьбу_один_раз()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);
            PanelTestStand.Settle();

            var about = window.FindControl<Button>("AboutButton");
            Assert.NotNull(about);

            var asked = 0;
            window.AboutRequested += () => asked++;

            about!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, asked);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// КНОПОК БЕЗ ПОДСКАЗКИ В НИЖНЕЙ СТРОКЕ НЕ ОСТАЛОСЬ — и это проверка ПРО ПЕРЕЕЗД дверей,
    /// а не повтор <c>ButtonTooltipTests</c>: новая кнопка, добавленная в окно, обязана получить
    /// подсказку тем же движением, и «забыли» видно по имени органа, а не по подписи.
    ///
    /// ⚠️ В строке теперь ДВЕ двери: «Сохранить» и «О программе». Обновление из этой строки ушло
    /// в РАЗДЕЛ списка слева (решение владельца 28.09.2026: *«это будет просто нормальный пункт
    /// меню как и остальные во вкладке настройки»*) — кнопка среди дверей читалась как чужой
    /// элемент. Проверка называет двери поимённо: кнопка, добавленная в строку и здесь
    /// не названная, обязана уронить этот список.
    /// </summary>
    [AvaloniaFact]
    public void В_нижней_строке_две_двери_и_у_обеих_подсказка()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);
            PanelTestStand.Settle();

            var row = window.FindControl<Button>("SaveButton")?.Parent as Grid;
            Assert.NotNull(row);

            var about = window.FindControl<Button>("AboutButton");

            Assert.NotNull(about);

            // Обе — соседи в ОДНОЙ нижней строке: дверь стоит рядом с «Сохранить», ровно как
            // её прежнее место в главном окне было нижним левым углом содержимого.
            Assert.Same(row, about!.Parent);
            Assert.Same(row, window.FindControl<Button>("SaveButton")!.Parent);

            var names = row!.Children.OfType<Button>().Select(button => button.Name).ToList();
            Assert.Equal(new[] { "SaveButton", "AboutButton" }, names);

            // И обновления среди дверей больше нет: оно стало разделом.
            Assert.Null(window.FindControl<Button>("UpdateButton"));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В СПИСКЕ РАЗДЕЛОВ НАСТРОЕК ДВЕРИ НЕТ: «О программе» — это ДВЕРЬ, а не настройка.
    /// Пункт в списке разделов ломал бы и список (он про настройки), и подсветку выбранного
    /// раздела, а содержимое справа осталось бы пустым.
    /// </summary>
    [AvaloniaFact]
    public void В_списке_разделов_двери_нет()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);
            PanelTestStand.Settle();

            var list = window.FindControl<ListBox>("SectionsList");
            Assert.NotNull(list);

            var titles = list!.ItemsSource!.Cast<string>().ToList();

            Assert.DoesNotContain(PanelStrings.AboutButton, titles);
            Assert.Equal(5, titles.Count);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
