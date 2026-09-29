using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DshPanel.Settings;

namespace DshPanel.Views;

/// <summary>
/// ОТЧЁТ ОКРУЖЕНИЯ В ВИДЕ ГРУПП — «что искали · что нашли · где именно».
///
/// **Зачем своя отрисовка.** Просьба владельца 28.09.2026: *«нормальная группировка с указанием
/// пути; версия сейчас DSH, версия node и так далее»*. Одним абзацем это не выражается: у каждой
/// находки три части, и читаются они по столбцам, а не подряд.
///
/// **Что здесь и чего нет.** Здесь ТОЛЬКО раскладка: ни одного решения о содержимом, ни одной
/// подписи, ни одного числа. Строки приходят готовыми (<see cref="EnvironmentReport"/>), подписи —
/// из словаря (<c>PanelStrings</c>). Поэтому «что показывает раздел» проверяется перебором
/// отчёта, а не разбором разметки.
///
/// ⚠️ **Путь переносится, а не обрезается.** Каталог установки бывает длинным, и обрезанный путь
/// бесполезен: его нельзя ни прочитать, ни скопировать. Ширину даёт колонка, высота строки —
/// автоматическая.
/// </summary>
public static class EnvironmentPanelView
{
    /// <summary>Наполнить панель группами. Повторный вызов заменяет прежние строки целиком.</summary>
    public static void Fill(StackPanel panel, EnvironmentReport report)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(report);

        panel.Children.Clear();

        foreach (var group in report.Groups)
        {
            panel.Children.Add(Group(group));
        }
    }

    /// <summary>Число строк во всех группах — для проверок без экрана.</summary>
    public static int Count(StackPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var total = 0;

        foreach (var group in panel.Children.OfType<StackPanel>())
        {
            foreach (var row in group.Children.OfType<Grid>())
            {
                total += row.Children.OfType<TextBlock>().Count();
            }
        }

        return total;
    }

    private static Control Group(EnvironmentGroup group)
    {
        var stack = new StackPanel { Spacing = 8 };

        stack.Children.Add(new TextBlock
        {
            Text = group.Title,
            FontWeight = FontWeight.SemiBold,
        });

        foreach (var line in group.Lines) stack.Children.Add(Row(line));

        return stack;
    }

    /// <summary>
    /// Строка находки: «что» — своим столбцом, «результат» — своим, «путь» — остатком ширины.
    /// Сетка, а не строка текста: у находок разная длина, и столбцы обязаны совпадать по вертикали
    /// (иначе отчёт читается как список, а не как таблица).
    /// </summary>
    private static Control Row(EnvironmentLine line)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,12,*") };

        var what = new TextBlock
        {
            Text = line.What,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.75,
        };

        var value = new StackPanel { Spacing = 2 };
        value.Children.Add(new TextBlock { Text = line.Result, TextWrapping = TextWrapping.Wrap });

        if (line.Where.Length > 0)
        {
            value.Children.Add(new TextBlock
            {
                Text = line.Where,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            });
        }

        Grid.SetColumn(what, 0);
        Grid.SetColumn(value, 2);

        row.Children.Add(what);
        row.Children.Add(value);

        return row;
    }
}
