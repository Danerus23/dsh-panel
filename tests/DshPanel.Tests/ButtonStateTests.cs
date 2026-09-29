using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Styling;
using DshPanel.Server;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СОСТОЯНИЯ КНОПОК: наведение, нажатие, фокус и недоступность ВИДНЫ, цвета берутся у темы
/// каркаса, а РАСКЛАДКА от смены состояния не меняется.
///
/// Слова владельца 27.09.2026: *«кнопки выглядят простовато»*. Стили живут в ОДНОМ месте —
/// <c>App.axaml</c> (решение дирижёра: «одно место на всё приложение, как тема»), и обещаний
/// у них четыре:
///
/// 1. **состояния различимы** — проверяется КАДРОМ: свойства стиля можно выставить и не увидеть
///    ничего, а человеку нужно то, что видно. Мерится и ОБЫЧНАЯ кнопка, и АКЦЕНТНАЯ: у второй кисть
///    рамки объявлена своим правилом класса, и состояние, объявленное раньше, она перебивает собой
///    (на этом уже стоял невидимый фокус — замер 28.09.2026);
/// 2. **цвета из темы** — проверяется и объявлением (в <c>App.axaml</c> нет ни одного своего
///    цвета), и живой выпиской ресурсов в СВЕТЛОЙ и ТЁМНОЙ теме: подобранный оттенок сломал бы
///    одну из них, и заметить это на одной теме нельзя;
/// 3. **состояние фокуса объявлено** — у каркаса фокус рисуется отдельным слоем и на светлом фоне
///    читается слабо; панель обводит кнопку сама;
/// 4. **состояние НЕ МЕНЯЕТ РАСКЛАДКУ** — дефект живого просмотра (владелец, 28.09.2026):
///    *«когда на них нажимаешь, у них меняется слегка размер, от чего соседняя кнопка уменьшается
///    и дрожит весь текст ниже»*. Проверяется ЗАМЕРОМ границ: у кнопки, её соседа и подписи под
///    ними границы обязаны быть одни и те же во всех состояниях. Кадровая проверка этого не ловит:
///    раскладка может уехать на точке ровно там, где кадр меняется и без неё.
///
/// ⚠️ **Чего здесь нет намеренно:** значков у кнопок, теней у разделов и анимаций.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ButtonStateTests
{
    /// <summary>Кисти кнопки, которые панель берёт у темы. Все обязаны существовать в двух темах.</summary>
    private static readonly string[] ThemeBrushes =
    {
        "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
        "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled",
    };

    private static void SetState(Button button, string state, bool on)
    {
        var classes = (IPseudoClasses)button.Classes;
        classes.Set(state, on);
    }

    /// <summary>
    /// Дать успокоиться переходам. Смена состояния у кнопки каркаса идёт ПЕРЕХОДОМ (плавно),
    /// и кадр сразу после снятия состояния ещё не равен обычному: замер показал, что после
    /// «нажатия» кадр возвращается к обычному только через несколько тактов отрисовки. Без этого
    /// ожидания сторож «кадр обычного состояния равен сам себе» падал бы на живой анимации,
    /// а не на дефекте — и это был бы ложный провал.
    /// </summary>
    private static void Calm()
    {
        for (var tick = 0; tick < 12; tick++)
        {
            PanelTestStand.Settle();
            System.Threading.Thread.Sleep(20);
        }

        PanelTestStand.Settle();
    }

    /// <summary>
    /// Кнопка в ОТДЕЛЬНОМ окне — и это не «проверка в стороне от дела».
    ///
    /// Стили кнопок живут в <c>App.axaml</c>, то есть принадлежат ПРИЛОЖЕНИЮ, и в этом окне
    /// работают ровно те же. А мерятся состояния кадром только так: у главного окна есть полоса
    /// прокрутки и таймер опроса, и КАДРЫ ОДНОГО И ТОГО ЖЕ состояния в нём расходятся — полоса
    /// каркаса доигрывает своё появление. Сравнивать состояния на таком кадре нельзя: разница
    /// «наведение против обычного» утонула бы в разнице «кадр раньше против кадра позже».
    ///
    /// <paramref name="accent"/> — кнопка главного действия (класс <c>accent</c>): у неё свои
    /// правила кисти рамки, и состояние обязано быть видно и на ней тоже (см. проверку ниже).
    /// </summary>
    private static (Window Window, Button Button, Dictionary<string, string> Frames) States(bool accent = false)
    {
        var button = new Button { Content = "Проба кнопки", Width = 180 };
        if (accent) button.Classes.Add("accent");

        var window = new Window { Width = 260, Height = 120, Content = button };

        window.Show();
        PanelTestStand.Settle();

        var frames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["обычное"] = PanelTestStand.FrameHash(window),
        };

        foreach (var state in new[] { ":pointerover", ":pressed", ":focus" })
        {
            SetState(button, state, true);
            frames[state] = PanelTestStand.FrameHash(window);
            SetState(button, state, false);

            // Переход доигрывается — иначе следующий замер ловил бы ещё не погасшее состояние.
            Calm();
        }

        // Кадр «обычного» состояния после снятия состояний — сторож: если он разошёлся с первым,
        // значит менялось что-то ещё, и сравнивать состояния между собой нельзя.
        Assert.Equal(frames["обычное"], PanelTestStand.FrameHash(window));

        button.IsEnabled = false;
        frames["недоступна"] = PanelTestStand.FrameHash(window);
        button.IsEnabled = true;
        Calm();

        return (window, button, frames);
    }

    [AvaloniaFact]
    public void Каждое_состояние_кнопки_видно_по_кадру()
    {
        Видны_все_состояния("обычная кнопка", accent: false);
        Видны_все_состояния("акцентная кнопка", accent: true);
    }

    /// <summary>
    /// СОСТОЯНИЯ ОДНОЙ КНОПКИ РАЗЛИЧИМЫ. Зовётся дважды — для обычной кнопки и для акцентной, —
    /// и это не удвоение ради числа: у акцентной кнопки кисть рамки объявлена правилом КЛАССА,
    /// а правила одного свойства применяются по порядку объявления. Состояние, объявленное раньше
    /// акцента, акцентная кнопка перебивает собой, и фокус на ней не виден ВООБЩЕ — именно так
    /// и случилось 28.09.2026 (замер: 0 отличий в кадре против 434 у обычной кнопки).
    /// Кадр обычной кнопки такого дефекта не показывает: у неё правила класса нет.
    /// </summary>
    private static void Видны_все_состояния(string where, bool accent)
    {
        var (window, _, frames) = States(accent);

        try
        {
            var normal = frames["обычное"];
            var indistinguishable = frames
                .Where(pair => pair.Key != "обычное" && pair.Value == normal)
                .Select(pair => pair.Key)
                .ToList();

            Assert.True(
                indistinguishable.Count == 0,
                $"{where}: состояния не отличаются от обычного кадра: " + string.Join(", ", indistinguishable) +
                ". Обещание владельцу — «наведение, нажатие, фокус и недоступность различимы», " +
                "и проверять это можно только кадром: свойство можно выставить и не увидеть ничего.");

            // И состояния отличаются ДРУГ ОТ ДРУГА, а не только от обычного: наведение и нажатие,
            // нарисованные одинаково, человек читает как «кнопка не отзывается».
            Assert.NotEqual(frames[":pointerover"], frames[":pressed"]);
            Assert.NotEqual(frames[":pointerover"], frames[":focus"]);
            Assert.NotEqual(frames[":pressed"], frames[":focus"]);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ЗАМЕР: СОСТОЯНИЕ КНОПКИ НЕ ДВИГАЕТ СОСЕДЕЙ.
    ///
    /// Дефект живого просмотра (владелец, 28.09.2026): *«Есть какая-то проблема с кнопками. Они
    /// анимированные, и когда на них нажимаешь, у них меняется слегка размер, от чего соседняя
    /// кнопка уменьшается и дрожит весь текст ниже — получается эффект дрожания интерфейса
    /// от нажатия кнопок»*. Причина была в стиле фокуса: <c>BorderThickness 2</c> против базовой
    /// <c>1</c>. Нажатие отдаёт кнопке фокус, рамка толстела, размер кнопки пересчитывался —
    /// и раскладка вокруг неё ехала.
    ///
    /// Мерятся ГРАНИЦЫ, а не кадр: кадровая проверка выше такого дефекта не ловит — раскладка
    /// может уехать там, где кадр меняется и по другой причине (фон наведения), а может уехать
    /// на точке и не попасть в отпечаток вовсе. Границы же отвечают ровно на вопрос владельца:
    /// сдвинулось ли то, что рядом.
    ///
    /// Стенд — по словам владельца: две кнопки в столбик и текст под ними. Мутация, которую
    /// проверка обязана ловить: вернуть <c>BorderThickness 2</c> в стиль фокуса.
    /// </summary>
    [AvaloniaFact]
    public void Состояние_кнопки_не_двигает_соседей_и_подпись()
    {
        var first = new Button { Content = "Первая", Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var second = new Button { Content = "Вторая", Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var label = new TextBlock { Text = "Текст под кнопками" };

        var panel = new StackPanel { Spacing = 8, Children = { first, second, label } };
        var window = new Window { Width = 320, Height = 300, Content = panel };

        window.Show();
        PanelTestStand.Settle();

        try
        {
            var normal = Bounds(first, second, label);

            // Стенд обязан быть СНАЧАЛА построен: у несосчитанной вёрстки границы нулевые,
            // и «ничего не сдвинулось» было бы правдой про пустое место.
            Assert.True(
                first.Bounds.Height > 0 && second.Bounds.Height > 0 && label.Bounds.Height > 0,
                $"вёрстка не досчитана, мерить нечего: {normal}");

            var states = new (string Name, Action Apply, Action Undo)[]
            {
                (":pointerover",
                    () => SetState(first, ":pointerover", true),
                    () => SetState(first, ":pointerover", false)),
                (":pressed",
                    () => SetState(first, ":pressed", true),
                    () => SetState(first, ":pressed", false)),
                (":focus",
                    () => SetState(first, ":focus", true),
                    () => SetState(first, ":focus", false)),
                ("недоступность",
                    () => first.IsEnabled = false,
                    () => first.IsEnabled = true),
            };

            var moved = new List<string>();

            foreach (var (name, apply, undo) in states)
            {
                apply();
                PanelTestStand.Settle();

                var now = Bounds(first, second, label);
                if (now != normal) moved.Add($"{name}: было [{normal}], стало [{now}]");

                undo();
                Calm();
            }

            Assert.True(
                moved.Count == 0,
                "состояние первой кнопки сдвинуло её саму, соседа или подпись под ними — это и есть " +
                "«дрожание интерфейса от нажатия кнопок» (владелец, 28.09.2026). Ни одно состояние " +
                "не смеет менять размер: только цвет, фон и прозрачность. Сдвинулось:" +
                Environment.NewLine + string.Join(Environment.NewLine, moved));

            // И обратный ход: снятые состояния вернули ровно ту же раскладку.
            Assert.Equal(normal, Bounds(first, second, label));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Границы всех трёх органов одной строкой — так падение читается, а не «где-то уехало».</summary>
    private static string Bounds(Button first, Button second, TextBlock label) =>
        $"кнопка1={first.Bounds} кнопка2={second.Bounds} подпись={label.Bounds}";

    /// <summary>
    /// ЦВЕТА БЕРУТСЯ У ТЕМЫ — и она их отдаёт в ОБЕИХ темах. Своя палитра в панели не заводится:
    /// оттенок, подобранный на светлой теме, не читается на тёмной.
    /// </summary>
    [AvaloniaFact]
    public void Кисти_кнопок_есть_и_в_светлой_и_в_тёмной_теме()
    {
        var application = Application.Current;
        Assert.NotNull(application);

        var missing = new List<string>();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var key in ThemeBrushes)
            {
                if (!application!.TryFindResource(key, variant, out var value) || value is null)
                {
                    missing.Add($"{variant}/{key}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "тема не отдала кисти, на которые опираются стили кнопок: " + string.Join(", ", missing) +
            ". Взять их неоткуда — значит состояния кнопок остались бы без цвета в одной из тем.");
    }

    /// <summary>
    /// ОБЪЯВЛЕНИЕ СТИЛЕЙ: состояния кнопок живут в <c>App.axaml</c> (одно место на всё приложение),
    /// и ни одного СВОЕГО цвета там нет — только ресурсы темы.
    ///
    /// Проверка читает разметку, а не кадр, и это дополнение к кадровой: кадр доказывает, что
    /// состояние ВИДНО, а объявление — что оно видно ИМЕННО у панели (у каркаса свои состояния),
    /// и что цвета не подменены подобранными.
    /// </summary>
    [Fact]
    public void Состояния_кнопок_объявлены_в_App_axaml_и_без_своих_цветов()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var path = Path.Combine(root, "src", "DshPanel", "App.axaml");
        Assert.True(File.Exists(path), $"нет файла стилей приложения: {path}");

        var markup = File.ReadAllText(path);

        foreach (var state in new[] { ":pointerover", ":pressed", ":focus", ":disabled" })
        {
            Assert.Contains($"Button{state} ", markup, StringComparison.Ordinal);
        }

        // Свой цвет — это «#RRGGBB» в значении свойства. Ни одного: палитра приходит из темы,
        // и в тёмной она другая.
        Assert.DoesNotMatch(@"""#[0-9A-Fa-f]{3,8}""", markup);

        Assert.DoesNotContain("SolidColorBrush", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Color ", markup, StringComparison.Ordinal);
    }
}
