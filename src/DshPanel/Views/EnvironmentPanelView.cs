using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// РАЗДЕЛ «СЕРВЕР» КАРТОЧКАМИ: имя блока, одна строка пояснения и строки-находки
/// «что · состояние · подробность · действие».
///
/// **Зачем своя отрисовка и почему она переписана 14.10.2026.** Прежний вид был парами
/// «имя — значение» полотном: состояния («найден», «не проверялась») стояли обычным текстом
/// в общем столбце и состоянием не читались — на это владелец и указал, и решение принял
/// по рендеру (`..\_mockups\server-v2c-*.png`): **плотная метка со словом внутри**, серое
/// состояние — **просто серым текстом**, путь — **маской**, у папки — **действие строки**.
///
/// **Что здесь и чего нет.** Здесь ТОЛЬКО раскладка: ни одного решения о содержимом, ни одной
/// подписи, ни одного числа. Строки и их слова приходят готовыми (<see cref="EnvironmentReport"/>),
/// цвета — из темы (<see cref="PanelLook"/>). Поэтому «что показывает раздел» проверяется перебором
/// отчёта, а не разбором разметки.
///
/// ⚠️ **Шапки колонок здесь НЕТ и быть не должна.** Имя блока сказано заголовком карточки, форма
/// взята у GOV.UK summary list; шапка «Что · Значение» над девятью строками объясняла бы то, что
/// и так видно. Это проверяется отдельно — возврат шапки обязан уронить проверку.
///
/// ⚠️ **Путь показывается МАСКОЙ, а действие открывает НАСТОЯЩУЮ папку.** Оба пути приходят
/// в строке: показанный (<see cref="EnvironmentLine.Detail"/>) и рабочий
/// (<see cref="EnvironmentLine.Folder"/>), и рабочий наружу не выводится ничем.
/// </summary>
public static class EnvironmentPanelView
{
    /// <summary>Ширина колонки имени — как у прочих таблиц панели: число, а не проценты.</summary>
    private const double NameColumn = 250;

    /// <summary>
    /// Наполнить панель карточками. Повторный вызов заменяет прежние строки целиком.
    /// </summary>
    /// <param name="openFolder">
    /// Чем открыть папку строки. <c>null</c> — действия в списке нет вовсе; строки с папкой
    /// остаются без кнопки, и это честнее мёртвой кнопки.
    /// </param>
    public static void Fill(StackPanel panel, EnvironmentReport report, Action<string>? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(report);

        panel.Children.Clear();

        foreach (var group in report.Groups) panel.Children.Add(Group(group, openFolder));
    }

    /// <summary>Карточка блока: имя, пояснение под ним и строки.</summary>
    private static Control Group(EnvironmentGroup group, Action<string>? openFolder)
    {
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = group.Title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (group.Subtitle.Length > 0)
            stack.Children.Add(new TextBlock { Text = group.Subtitle, Classes = { "hint" } });

        for (var index = 0; index < group.Lines.Count; index++)
        {
            stack.Children.Add(Row(group.Lines[index], last: index == group.Lines.Count - 1, openFolder));
        }

        return new Border { Classes = { "card" }, Child = stack };
    }

    /// <summary>
    /// СТРОКА НАХОДКИ: «что» — своим столбцом, состояние с подробностью — своим, действие — третьим.
    /// Сетка, а не строка текста: у находок разная длина, и столбцы обязаны совпадать по вертикали
    /// (иначе отчёт читается как список, а не как таблица).
    /// </summary>
    private static Control Row(EnvironmentLine line, bool last, Action<string>? openFolder)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{NameColumn:0},16,*,16,Auto"),
            Margin = new Thickness(0, 9),
        };

        var what = new TextBlock
        {
            Text = line.What,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };

        var value = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var child in State(line)) value.Children.Add(child);

        if (line.Detail.Length > 0)
            value.Children.Add(new TextBlock { Text = line.Detail, Classes = { "rowHint" } });

        var action = Action(line, openFolder);

        Grid.SetColumn(what, 0);
        Grid.SetColumn(value, 2);

        row.Children.Add(what);
        row.Children.Add(value);

        if (action is not null)
        {
            Grid.SetColumn(action, 4);
            row.Children.Add(action);
        }

        var wrapper = new Border { Classes = { "envRow" }, Child = row };
        if (last) wrapper.Classes.Add("envRowLast");

        return wrapper;
    }

    /// <summary>
    /// СОСТОЯНИЕ СТРОКИ: слово ВСЕГДА, цвет — только как дополнение.
    ///
    /// ⚠️ **Цвет не единственный носитель смысла** (WCAG 2.2 SC 1.4.1): серое состояние — серым
    /// ТЕКСТОМ без плашки, цветное — меткой со словом внутри. Метка не интерактивна: состояние
    /// читают, а не нажимают (GOV.UK Tag).
    /// </summary>
    private static IEnumerable<Control> State(EnvironmentLine line)
    {
        if (line.Word.Length == 0) yield break;

        if (line.State == EnvLineState.Grey)
        {
            yield return new TextBlock { Text = line.Word, Classes = { "stateGrey" } };
            yield break;
        }

        var plate = TagClass(line.State);
        if (plate.Length == 0) yield break;

        var text = new TextBlock
        {
            Text = line.Word,
            Classes = { "stateTagText", plate + "Text" },
        };

        yield return new Border { Classes = { "stateTag", plate }, Child = text };
    }

    /// <summary>Класс метки по состоянию. Пусто — метки у этого состояния нет.</summary>
    private static string TagClass(EnvLineState state) => state switch
    {
        EnvLineState.Ok => "tagOk",
        EnvLineState.Warning => "tagWarning",
        EnvLineState.Error => "tagBad",
        _ => string.Empty,
    };

    /// <summary>
    /// ДЕЙСТВИЕ СТРОКИ: кнопка есть РОВНО у строки с папкой — и только когда есть чем открыть.
    /// У строки без папки действия НЕТ ВОВСЕ (не спрятанная кнопка, а её отсутствие): кнопка
    /// в никуда читается как поломка панели, а мёртвый орган в дереве окна ещё и путает проверки,
    /// которые перебирают кнопки окна (`ButtonTooltipTests`).
    /// </summary>
    private static Button? Action(EnvironmentLine line, Action<string>? openFolder)
    {
        if (line.Folder.Length == 0 || openFolder is null) return null;

        var button = new Button
        {
            Classes = { "rowAction" },
            Content = PanelStrings.EnvOpenFolderButton,
        };

        // ПОДСКАЗКА ОБЯЗАТЕЛЬНА у каждой кнопки панели (решение владельца 27.09.2026) и говорит,
        // что произойдёт по нажатию, а не повторяет подпись.
        PanelToolTip.Set(button, PanelStrings.TipEnvOpenFolder);

        button.Click += (_, _) => openFolder(line.Folder);

        return button;
    }
}
