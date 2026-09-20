using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Проверка вёрстки: не обрезается ли текст в окнах. Окна панели собраны на точных
/// координатах, а подписи приходят из словарей — на английском и китайском строка может
/// оказаться шире, чем отведённое ей место. Глазами это видно только на снимках, а этот
/// класс считает ширину текста и сравнивает с шириной элемента.
///
/// Проверки:
///  • метка с заданной шириной — хватает ли высоты на перенос и не шире ли метки слово;
///  • кнопка или флажок — хватает ли ширины, текст там не переносится;
///  • любой элемент — не выходит ли он за клиентскую область родителя (ловит Anchor,
///    посчитанный от размера контейнера до раскладки);
///  • соседи — не налезают ли элементы друг на друга.
///
/// Окно показывается далеко за пределами экрана и раскладывается: только так у страниц
/// вкладок и у панелей шагов мастера появляется настоящий размер, а у невыбранных вкладок
/// и непоказанных шагов он остаётся размером по умолчанию (200×100) — такие элементы
/// пропускаются как невидимые, иначе проверка сообщала бы о несуществующих наложениях.
/// </summary>
internal static class LayoutCheck
{
    public static List<string> Inspect(string window, Control root)
    {
        var issues = new List<string>();
        try
        {
            // Главное окно не показываем: оно создаётся хостом трея и показ запускает его
            // собственную логику. Остальные окна — диалоги, их показ безопасен.
            var shown = root is Form form && root is not MainForm && !form.Visible ? Show(form) : null;
            try
            {
                WalkPlaces(window, root, shown != null, issues);
            }
            finally
            {
                shown?.Invoke();
            }
        }
        catch (Exception error)
        {
            issues.Add(window + ": проверка не удалась — " + error.Message);
        }

        // Одна и та же беда видна с каждой вкладки, поэтому повторы убираем.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return issues.Where(issue => seen.Add(issue)).ToList();
    }

    /// <summary>
    /// Показывает окно незаметно; возвращает действие, возвращающее всё как было.
    /// Прозрачность вместо одних только отрицательных координат: на конфигурации с монитором
    /// слева виртуальный рабочий стол уходит в минус, и окно могло мелькнуть на экране.
    /// </summary>
    private static Action Show(Form form)
    {
        var start = form.StartPosition;
        var taskbar = form.ShowInTaskbar;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        Settle(form);

        return () =>
        {
            form.Hide();
            form.Opacity = 1;
            form.StartPosition = start;
            form.ShowInTaskbar = taskbar;
        };
    }

    private static void Settle(Control control)
    {
        Application.DoEvents();
        control.PerformLayout();
        Application.DoEvents();
    }

    /// <summary>
    /// Обходит все места окна, которые человек видит по очереди: каждую вкладку и каждый шаг
    /// мастера. Невыбранные вкладки и непоказанные шаги не разложены, поэтому за один проход
    /// их проверить нельзя.
    /// </summary>
    private static void WalkPlaces(string window, Control root, bool shown, List<string> issues)
    {
        if (root is OnboardingForm wizard)
        {
            for (var step = 0; step < OnboardingForm.StepCount; step++)
            {
                wizard.ShowStepForCheck(step);
                Settle(wizard);
                Walk(wizard, window, shown, issues);
            }

            return;
        }

        var tabs = FindTabs(root);
        if (tabs != null && shown)
        {
            for (var index = 0; index < tabs.TabPages.Count; index++)
            {
                tabs.SelectedIndex = index;
                Settle(tabs.TabPages[index]);
                Walk(root, window, shown, issues);
            }

            tabs.SelectedIndex = 0;
            return;
        }

        Walk(root, window, shown, issues);
    }

    /// <summary>Ищет вкладки рекурсивно: они могут лежать не прямо в окне, а внутри панели.</summary>
    private static TabControl FindTabs(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TabControl tabs) return tabs;

            if (control.HasChildren)
            {
                var nested = FindTabs(control);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static void Walk(Control parent, string window, bool skipInvisible, List<string> issues)
    {
        foreach (Control control in parent.Controls)
        {
            // Невидимое сейчас (чужая вкладка, непоказанный шаг) не разложено: его размеры
            // и координаты ещё не настоящие, проверять их нельзя.
            if (skipInvisible && !control.Visible) continue;

            var text = control.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (control is Label label && label.AutoSize)
                {
                    // Растягивается по тексту — ширина не проверяется, но за контейнер
                    // выходить не должна: это ловит CheckBounds.
                }
                else if (control is Label)
                {
                    // Метка с заданной шириной переносит строку: важно, хватает ли высоты.
                    var measured = TextRenderer.MeasureText(text, control.Font,
                        new Size(Math.Max(1, control.ClientSize.Width), int.MaxValue), TextFormatFlags.WordBreak);
                    if (measured.Height > control.ClientSize.Height)
                    {
                        issues.Add($"{window}: {Describe(control)} — «{Cut(text)}»: нужно {measured.Height}px высоты, есть {control.ClientSize.Height}px");
                    }

                    // Слово, которое шире метки, не переносится и обрезается на её краю.
                    var longest = LongestWord(text, control.Font);
                    if (longest > control.ClientSize.Width)
                    {
                        issues.Add($"{window}: {Describe(control)} — «{Cut(text)}»: слово шире метки на {longest - control.ClientSize.Width}px");
                    }
                }
                else if (text.IndexOf('\n') < 0)
                {
                    // Кнопки и флажки текст не переносят: важна ширина.
                    var padding = PaddingFor(control);
                    if (padding > 0)
                    {
                        var needed = TextRenderer.MeasureText(text, control.Font).Width + padding;
                        if (needed > control.ClientSize.Width)
                        {
                            issues.Add($"{window}: {Describe(control)} — «{Cut(text)}»: нужно {needed}px ширины, есть {control.ClientSize.Width}px");
                        }
                    }
                }
            }

            CheckBounds(control, parent, window, issues);

            if (control.HasChildren) Walk(control, window, skipInvisible, issues);
        }

        CheckOverlaps(parent, window, skipInvisible, issues);
    }

    /// <summary>
    /// Элемент не должен выходить за клиентскую область родителя. Так ловится забытый
    /// Anchor: отступы считаются от размера контейнера, а у страницы вкладки до раскладки
    /// он бывает размером по умолчанию — тогда метка растягивается за край окна и текст
    /// уезжает вправо, хотя по координатам всё выглядит прилично.
    /// </summary>
    private static void CheckBounds(Control control, Control parent, string window, List<string> issues)
    {
        const int tolerance = 1;

        var right = control.Right - parent.ClientSize.Width;
        if (right > tolerance)
        {
            issues.Add($"{window}: {Where(control)} — выходит на {right}px вправо за {Describe(parent)} ({parent.ClientSize.Width}px)");
        }

        var bottom = control.Bottom - parent.ClientSize.Height;
        if (bottom > tolerance)
        {
            issues.Add($"{window}: {Where(control)} — выходит на {bottom}px вниз за {Describe(parent)} ({parent.ClientSize.Height}px)");
        }

        if (control.Left < 0 || control.Top < 0)
        {
            issues.Add($"{window}: {Where(control)} — начинается за границей {Describe(parent)} ({control.Left};{control.Top})");
        }
    }

    /// <summary>
    /// Соседние элементы не должны налезать друг на друга: окна собраны на координатах,
    /// и при правке одной подписи легко задеть соседнюю кнопку или флажок.
    /// </summary>
    private static void CheckOverlaps(Control parent, string window, bool skipInvisible, List<string> issues)
    {
        var siblings = new List<Control>();
        foreach (Control control in parent.Controls)
        {
            if (control is TabControl || control is TabPage) continue;
            if (skipInvisible && !control.Visible) continue;
            siblings.Add(control);
        }

        for (var i = 0; i < siblings.Count; i++)
        {
            for (var j = i + 1; j < siblings.Count; j++)
            {
                var a = siblings[i];
                var b = siblings[j];

                var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (overlapX > 1 && overlapY > 1)
                {
                    issues.Add($"{window}: {Where(a)} и {Where(b)} налезают друг на друга на {overlapX}×{overlapY}px в {Describe(parent)}");
                }
            }
        }
    }

    /// <summary>Сколько места элемент тратит не на текст: рамка кнопки, квадратик флажка, значок.</summary>
    private static int PaddingFor(Control control)
    {
        if (control is GroupBox) return 18;

        if (control is Button button)
        {
            // Значок перед подписью тоже занимает ширину: без него проверка пропустила бы
            // кнопку, у которой подпись со значком уже не влезает.
            return 14 + (button.Image == null ? 0 : button.Image.Width + 8);
        }

        if (control is CheckBox || control is RadioButton) return 24;
        return 0;
    }

    private static string Describe(Control control) =>
        control.GetType().Name + (string.IsNullOrWhiteSpace(control.Name) ? "" : " " + control.Name);

    /// <summary>Имя элемента для сообщений о границах: тип, Name и начало текста.</summary>
    private static string Where(Control control)
    {
        var title = Describe(control);
        var text = control.Text;
        if (!string.IsNullOrWhiteSpace(text)) title += " «" + Cut(text.Replace('\n', ' ')) + "»";
        return title;
    }

    /// <summary>Самое широкое слово строки: оно не переносится и обрезается на краю.</summary>
    internal static int LongestWord(string text, Font font)
    {
        var width = 0;
        foreach (var word in text.Split(new[] { ' ', '\t', '\n', '\r', '\u3000' }, StringSplitOptions.RemoveEmptyEntries))
        {
            width = Math.Max(width, TextRenderer.MeasureText(word, font).Width);
        }

        return width;
    }

    private static string Cut(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
