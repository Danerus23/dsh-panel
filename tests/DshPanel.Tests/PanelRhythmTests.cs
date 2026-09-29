using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DshPanel.Server;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЕДИНЫЙ РИТМ: одна шкала отступов, одна высота и одни внутренние отступы у кнопок, один размер
/// у подсказок под органами.
///
/// Решение владельца 27.09.2026 (шаг 1 «живости вида»): *«единый ритм отступов и размеров: одна
/// шкала (например 4/8/12/16/24), одинаковая высота и внутренние отступы у кнопок, одинаковые
/// отступы между карточками и внутри них, одинаковые размеры у подсказок»*. До этой работы
/// в разметке жили 6, 10, 14 и 18 — ряд чисел без правила, и именно он читается как «кнопки
/// выглядят простовато».
///
/// Ядро — ЧИСТАЯ функция <see cref="OffScaleNumbers"/>: ей отдают подставной текст разметки,
/// поэтому и проверку можно проверить, не уродуя дерево (проверки «Ядро…» ниже).
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PanelRhythmTests
{
    /// <summary>Шкала отступов панели. Других чисел в отступах быть не должно.</summary>
    public static readonly int[] Scale = { 4, 8, 12, 16, 24 };

    /// <summary>Высота кнопки — одна на все окна (объявлена стилем в <c>App.axaml</c>).</summary>
    public const double ButtonHeight = 28;

    /// <summary>Внутренние отступы кнопки — одни на все окна.</summary>
    public static readonly Thickness ButtonPadding = new(12, 6);

    /// <summary>Размер подсказки под органом — один на все окна (класс <c>hint</c>).</summary>
    public const double HintFontSize = 12;

    // ---- ядро: числа отступов в разметке ----------------------------------------------------

    // Значение атрибута отступа: Margin / Padding / Spacing. Слева запрещены буква, цифра, «_»,
    // «.» и «-» — иначе `TextBlock.Margin=` или `x:Name="NoMargin"` считались бы отступом.
    private static readonly Regex SpacingAttribute = new(
        "(?<![A-Za-z0-9_.:-])(?:Margin|Padding|Spacing)\\s*=\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Числа отступов, которых нет в шкале. Возвращает «файл:строка: атрибут» — по строке видно,
    /// куда идти, а не «где-то в разметке есть 6».
    /// </summary>
    public static IReadOnlyList<string> OffScaleNumbers(IEnumerable<SourceStringsTests.SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var found = new List<string>();

        foreach (var file in files)
        {
            var text = file.Text ?? string.Empty;

            foreach (Match match in SpacingAttribute.Matches(text))
            {
                foreach (var token in match.Groups["value"].Value.Split(','))
                {
                    var value = token.Trim();

                    // Пустое значение («Margin=""») отступом не является; «0» — законное «нет отступа».
                    if (value.Length == 0 || value == "0") continue;
                    if (!int.TryParse(value, out var number)) continue;

                    if (!Scale.Contains(number))
                    {
                        found.Add($"{file.Path}:{Line(text, match.Index)}: {match.Value.Trim()} (число {number})");
                    }
                }
            }
        }

        return found;
    }

    private static int Line(string text, int index)
    {
        var line = 1;

        for (var position = 0; position < index && position < text.Length; position++)
        {
            if (text[position] == '\n') line++;
        }

        return line;
    }

    /// <summary>
    /// Вся разметка панели: <c>src\DshPanel\**\*.axaml</c> без сборки и промежуточных файлов.
    /// Правило чтения ОДНО на два разбора (отступы и состояния): разошедшись, они мерили бы разные
    /// наборы файлов, и «в разметке чисто» означало бы «в той разметке, которую прочитал я».
    /// </summary>
    private static List<SourceStringsTests.SourceFile> ReadMarkup(string root)
    {
        var directory = Path.Combine(root, "src", "DshPanel");
        Assert.True(Directory.Exists(directory), $"нет каталога исходников панели: {directory}");

        return Directory
            .EnumerateFiles(directory, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                        && !path.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new SourceStringsTests.SourceFile(
                Path.GetRelativePath(root, path), File.ReadAllText(path)))
            .ToList();
    }

    // ---- ядро: состояние не меняет размер ----------------------------------------------------

    // Тело правила разметки: <Style ...> … </Style>. Вложенных правил в разметке панели нет;
    // появись они — разбор дошёл бы до ПЕРВОГО </Style> и вложенное правило осталось бы
    // непрочитанным. Сказано здесь прямо, а не спрятано: молчаливый пропуск — это дыра.
    private static readonly Regex StyleBlock = new(
        "<Style\\b(?<attributes>[^>]*)>(?<body>.*?)</Style>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SelectorAttribute = new(
        "Selector=\"(?<selector>[^\"]*)\"",
        RegexOptions.CultureInvariant);

    // ПСЕВДОСОСТОЯНИЕ — двоеточие и имя: `:pointerover`, `:pressed`, `:focus`, `:selected`,
    // `:disabled` и любое другое. Имя идёт СРАЗУ за типом или классом (`Button:pressed`,
    // `ListBoxItem:selected`) — поэтому перед двоеточием ничего не запрещается: запрет на букву
    // не пустил бы сюда ни одного настоящего состояния. `/template/` и точка класса
    // (`Border.card`) двоеточия не содержат вовсе и состояниями не являются.
    private static readonly Regex PseudoState = new(
        ":[A-Za-z][A-Za-z0-9-]*",
        RegexOptions.CultureInvariant);

    private static readonly Regex SetterProperty = new(
        "<Setter\\s+Property=\"(?<property>[^\"]+)\"",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// СВОЙСТВА, МЕНЯЮЩИЕ РАЗМЕР ИЛИ МЕСТО ОРГАНА, — их состоянию трогать нельзя.
    ///
    /// Дефект живого просмотра (владелец, 28.09.2026): фокус объявлял <c>BorderThickness 2</c>
    /// против базовой <c>1</c>, нажатие отдавало кнопке фокус, размер пересчитывался — и дрожал
    /// весь текст ниже. Список — это тот же дефект, названный шире одного свойства: и толщина,
    /// и поля, и кегль, и начертание меняют меру органа, а мера — это и есть раскладка соседей.
    /// </summary>
    public static readonly string[] SizeProperties =
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
        "Margin", "Padding", "BorderThickness", "FontSize", "FontFamily", "FontWeight", "Spacing",
    };

    /// <summary>
    /// ЕДИНСТВЕННОЕ ИСКЛЮЧЕНИЕ ИЗ ПРАВИЛА, и оно названо вместе с причиной — не молчаливое «ладно».
    ///
    /// <c>ListBoxItem:selected</c> → <c>FontWeight</c>: полужирная подпись ВЫБРАННОГО раздела
    /// настроек (решение владельца 27.09.2026: выбранную строку видно не одной подсветкой каркаса).
    /// Почему от него ничего не едет: строка списка живёт в колонке ФИКСИРОВАННОЙ ширины
    /// (<c>SettingsWindow.axaml</c>, <c>ColumnDefinitions="240,16,*"</c>), а кегль не меняется.
    /// Замер (здесь же, 28.09.2026): границы строки до и после выбора — одни и те же, <c>240x33</c>,
    /// меняется только начертание (<c>Normal → DemiBold</c>). То есть ширина строки задана колонкой,
    /// высота — кеглем, и ни сосед, ни подпись под строкой не двигаются.
    ///
    /// ⚠️ Исключение объявлено ПАРОЙ «селектор + свойство», а не «файл» и не «свойство вообще»:
    /// разрешение, выданное целому файлу, однажды накрыло бы и настоящий сдвиг раскладки.
    /// Список сторожится с двух сторон (см. <c>В_разметке_состояние_не_меняет_размер</c>):
    /// лишнее исключение и недостающее — оба валят проверку.
    /// </summary>
    public static readonly (string Selector, string Property, string Why)[] AllowedSizeChanges =
    {
        ("ListBoxItem:selected", "FontWeight",
            "полужирная подпись выбранного раздела настроек; строка в колонке фиксированной ширины 240, " +
            "кегль не меняется — замер: границы строки до и после выбора одни и те же (240x33)"),
    };

    /// <summary>
    /// МЕСТА, ГДЕ СОСТОЯНИЕ МЕНЯЕТ РАЗМЕР. Возвращает «файл:строка: селектор → свойство» — по строке
    /// видно, куда идти, а не «где-то в разметке есть Padding».
    ///
    /// Разбирается РАЗМЕТКА (текст), а не живые органы, и это дополнение к замеру границ: замер
    /// проходит по показанному окну, а разбор — по ВСЕЙ разметке панели, включая окна, которых
    /// проверка не строит вовсе. <paramref name="withExceptions"/> выключается только затем, чтобы
    /// доказать: список исключений не пустой и всё ещё нужен.
    /// </summary>
    public static IReadOnlyList<string> SizeChangesInStates(
        IEnumerable<SourceStringsTests.SourceFile> files,
        bool withExceptions = true)
    {
        ArgumentNullException.ThrowIfNull(files);

        var found = new List<string>();

        foreach (var file in files)
        {
            var text = file.Text ?? string.Empty;

            foreach (Match style in StyleBlock.Matches(text))
            {
                var selectorMatch = SelectorAttribute.Match(style.Groups["attributes"].Value);
                if (!selectorMatch.Success) continue;

                var selector = selectorMatch.Groups["selector"].Value;
                if (!PseudoState.IsMatch(selector)) continue;

                var body = style.Groups["body"];
                var bodyIndex = body.Index;
                var where = $"{file.Path}:{Line(text, style.Index)}: {selector}";

                foreach (Match setter in SetterProperty.Matches(body.Value))
                {
                    var property = setter.Groups["property"].Value;
                    if (!SizeProperties.Contains(property, StringComparer.Ordinal)) continue;

                    // Исключение спрашивается ПАРОЙ: «этот селектор и это свойство».
                    if (withExceptions && AllowedSizeChanges.Any(
                            allowed => allowed.Selector == selector && allowed.Property == property))
                    {
                        continue;
                    }

                    found.Add($"{where} → {property} (строка {Line(text, bodyIndex + setter.Index)})");
                }
            }
        }

        return found;
    }

    // ---- проверки --------------------------------------------------------------------------

    /// <summary>
    /// В РАЗМЕТКЕ ПАНЕЛИ НЕТ ОТСТУПОВ ВНЕ ШКАЛЫ. Проверяются все окна сразу: шкала — правило
    /// приложения, а не одного окна, и «в настройках 14, а в копиях 16» и есть тот разнобой,
    /// на который жаловался владелец.
    /// </summary>
    [Fact]
    public void В_разметке_нет_отступов_вне_шкалы()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var directory = Path.Combine(root, "src", "DshPanel");
        Assert.True(Directory.Exists(directory), $"нет каталога исходников панели: {directory}");

        var markup = ReadMarkup(root);

        Assert.True(markup.Count >= 5, $"разметки прочитано {markup.Count} — похоже, каталог не тот");

        var off = OffScaleNumbers(markup);

        Assert.True(
            off.Count == 0,
            $"отступы вне шкалы {string.Join("/", Scale)} — разнобой читается как «кнопки выглядят " +
            "простовато»:" + Environment.NewLine + string.Join(Environment.NewLine, off));
    }

    /// <summary>
    /// НИ ОДНО СОСТОЯНИЕ НЕ МЕНЯЕТ РАЗМЕР — правило на ВСЮ разметку панели, а не на одну кнопку.
    ///
    /// От какого случая. Владелец, глядя на панель живьём 28.09.2026: *«когда на них нажимаешь,
    /// у них меняется слегка размер, от чего соседняя кнопка уменьшается и дрожит весь текст
    /// ниже»*. Виноват был стиль фокуса с <c>BorderThickness 2</c>. Починка одного свойства
    /// закрыла бы один случай; правило закрывает КЛАСС: у правила с псевдосостоянием в селекторе
    /// не может быть ни одного свойства, меняющего меру органа, — а мера и есть раскладка соседей.
    ///
    /// Почему это отдельно от замера границ (<c>ButtonStateTests</c>): замер идёт по ПОКАЗАННОМУ
    /// окну и ловит сдвиг только там, где проверка смотрит, — а разметка панели шире одного стенда
    /// (шесть окон, кнопки в разных раскладках). Здесь читается вся разметка целиком.
    /// </summary>
    [Fact]
    public void В_разметке_состояние_не_меняет_размер()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var markup = ReadMarkup(root);

        Assert.True(markup.Count >= 5, $"разметки прочитано {markup.Count} — похоже, каталог не тот");

        var found = SizeChangesInStates(markup);

        Assert.True(
            found.Count == 0,
            "состояние в разметке меняет размер или место органа — это и есть «дрожание интерфейса " +
            "от нажатия кнопок»: состояние смеет менять только цвет, фон и прозрачность. Нашлось:" +
            Environment.NewLine + string.Join(Environment.NewLine, found));

        // И ОБРАТНАЯ СТОРОНА: без исключений разбор обязан найти РОВНО объявленные исключения.
        // Так список не может ни отрасти молча (лишнее исключение «разрешает» будущий дефект),
        // ни протухнуть (исключение, которого в разметке уже нет, сторожит пустое место).
        var withoutExceptions = SizeChangesInStates(markup, withExceptions: false);

        Assert.True(
            withoutExceptions.Count == AllowedSizeChanges.Length,
            $"исключений объявлено {AllowedSizeChanges.Length}, а мест, меняющих размер, в разметке " +
            $"{withoutExceptions.Count}: список исключений разошёлся с разметкой. Разберитесь, что " +
            "именно изменилось, и поправьте ОБЕ стороны (разметку или список с причиной). Нашлось:" +
            Environment.NewLine + string.Join(Environment.NewLine, withoutExceptions));
    }

    /// <summary>
    /// ВСЕ КНОПКИ ВСЕХ ОКОН ОДНОЙ ВЫСОТЫ И С ОДНИМИ ВНУТРЕННИМИ ОТСТУПАМИ. Мерятся НАСТОЯЩИЕ
    /// кнопки показанных окон, а не разметка: стиль можно объявить и не применить.
    /// </summary>
    [AvaloniaFact]
    public void Все_кнопки_окон_одной_высоты_и_с_одними_отступами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var windows = new List<(Window Window, string Where)>();

            var main = PanelTestStand.MainStand(ServerState.Stopped(0));
            windows.Add((main, "главное окно"));

            var settings = PanelTestStand.SettingsStand(dir);
            windows.Add((settings, "настройки"));

            var backups = new BackupWindow();
            backups.Attach(new BackupWindowStopFlowTests.StubBackups());
            windows.Add((backups, "копии"));

            windows.Add((new AboutWindow(), "о программе"));

            var wrong = new List<string>();
            var total = 0;

            foreach (var (window, where) in windows)
            {
                window.Show();
                PanelTestStand.Settle();

                // Только КНОПКИ ПАНЕЛИ — те, что объявлены как Button в разметке окон. Галочки
                // (CheckBox), ссылки (HyperlinkButton) и внутренние части чужих органов
                // (PART_IncreaseButton у числового поля) кнопками панели не являются: у них своя
                // тема каркаса, и общая высота с кнопкой «Сохранить» им не обещана.
                foreach (var button in PanelTestStand.Buttons(window).Where(b => b.GetType() == typeof(Button)))
                {
                    total++;

                    if (Math.Abs(button.MinHeight - ButtonHeight) > 0.01 ||
                        button.Padding != ButtonPadding)
                    {
                        wrong.Add(
                            $"{where}/{button.Name}: MinHeight={button.MinHeight:0.#}, " +
                            $"Padding={button.Padding}");
                    }
                }

                if (window is SettingsWindow quiet) quiet.CloseQuietly();
                else window.Close();
            }

            Assert.True(
                total >= 15,
                $"кнопок панели осмотрено {total} — похоже, окна не показаны или кнопки перестали быть Button");

            Assert.True(
                wrong.Count == 0,
                "кнопки разной высоты или с разными внутренними отступами — " +
                $"ждём MinHeight={ButtonHeight}, Padding={ButtonPadding}: " + Environment.NewLine +
                string.Join(Environment.NewLine, wrong));
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОДСКАЗКИ ПОД ОРГАНАМИ — ОДНОГО РАЗМЕРА, и это класс <c>hint</c>, а не число в каждой
    /// разметке: у подсказок в панели три разных размера, и разнобой начинается именно так.
    /// </summary>
    [AvaloniaFact]
    public void Подсказки_под_органами_одного_размера()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var windows = new List<Window>();

            var main = PanelTestStand.MainStand(ServerState.Stopped(0));
            windows.Add(main);

            var settings = PanelTestStand.SettingsStand(dir);
            windows.Add(settings);

            var backups = new BackupWindow();
            backups.Attach(new BackupWindowStopFlowTests.StubBackups());
            windows.Add(backups);

            var hints = 0;
            var wrong = new List<string>();

            foreach (var window in windows)
            {
                window.Show();
                PanelTestStand.Settle();

                foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
                {
                    if (!block.Classes.Contains("hint")) continue;

                    hints++;

                    if (Math.Abs(block.FontSize - HintFontSize) > 0.01)
                    {
                        wrong.Add($"{window.GetType().Name}/{block.Name}: FontSize={block.FontSize:0.#}");
                    }
                }

                if (window is SettingsWindow quiet) quiet.CloseQuietly();
                else window.Close();
            }

            Assert.True(hints >= 10, $"подсказок с классом hint найдено {hints} — похоже, класс не проставлен");

            Assert.True(
                wrong.Count == 0,
                $"размер подсказки разошёлся (ждём {HintFontSize}): " + Environment.NewLine +
                string.Join(Environment.NewLine, wrong));
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОДСКАЗКА В КОДЕ, А НЕ В РАЗМЕТКЕ: класс <c>hint</c> ставится в разметке, а текст — кодом
    /// из словаря (правило проекта «ни одного текста в разметке»). Проверка сторожит ровно это:
    /// у каждой программной подсказки под органом класс есть.
    /// </summary>
    [Fact]
    public void Подсказки_под_органами_помечены_классом()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var directory = Path.Combine(root, "src", "DshPanel");

        var namedHint = new Regex(
            "<TextBlock[^>]*x:Name=\"(?<name>[A-Za-z0-9_]*(Hint|HintText))\"[^>]*>",
            RegexOptions.CultureInvariant);

        var without = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.axaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(path);

            foreach (Match match in namedHint.Matches(markup))
            {
                if (!match.Value.Contains("Classes=\"hint\"", StringComparison.Ordinal))
                {
                    without.Add($"{Path.GetFileName(path)}: {match.Groups["name"].Value}");
                }
            }
        }

        Assert.True(
            without.Count == 0,
            "подсказки под органами без класса hint — размер у них свой, а не общий: " +
            string.Join(", ", without));
    }

    // ---- проверяемость самой проверки ------------------------------------------------------

    [Fact]
    public void Ядро_видит_число_вне_шкалы_и_не_считает_чужие_числа()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(@"Views\Bad.axaml", "<StackPanel Margin=\"0,6,0,0\" Spacing=\"14\" />"),
            new SourceStringsTests.SourceFile(
                @"Views\Fine.axaml",
                "<StackPanel Margin=\"24\" Spacing=\"12\" Padding=\"16\" Height=\"70\" Width=\"260\" />\n" +
                "<TextBlock x:Name=\"NoMargin\" TextWrapping=\"Wrap\" />"),
        };

        var off = OffScaleNumbers(files);

        Assert.Equal(2, off.Count);
        Assert.Contains(off, line => line.Contains("Bad.axaml:1", StringComparison.Ordinal) && line.Contains("6"));
        Assert.Contains(off, line => line.Contains("Bad.axaml:1", StringComparison.Ordinal) && line.Contains("14"));
    }

    [Fact]
    public void Ядро_называет_строку_файла()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(@"Views\Bad.axaml", "<Window>\n  <Border Padding=\"10\" />\n</Window>"),
        };

        var single = Assert.Single(OffScaleNumbers(files));

        Assert.Contains("Bad.axaml:2", single, StringComparison.Ordinal);
    }

    // ---- проверяемость разбора состояний ---------------------------------------------------

    [Fact]
    public void Ядро_видит_размер_в_состоянии_и_не_трогает_обычное_правило()
    {
        var files = new[]
        {
            new SourceStringsTests.SourceFile(
                @"Views\Bad.axaml",
                "<Style Selector=\"Button:pressed /template/ ContentPresenter#PART_ContentPresenter\">\n" +
                "  <Setter Property=\"Padding\" Value=\"16,8\" />\n" +
                "</Style>"),
            new SourceStringsTests.SourceFile(
                @"Views\Fine.axaml",
                "<Style Selector=\"Border.card\">\n  <Setter Property=\"Padding\" Value=\"24\" />\n</Style>\n" +
                "<Style Selector=\"ListBoxItem:selected\">\n" +
                "  <Setter Property=\"Background\" Value=\"{DynamicResource Кисть}\" />\n</Style>"),
        };

        var single = Assert.Single(SizeChangesInStates(files, withExceptions: false));

        Assert.Contains("Bad.axaml:1", single, StringComparison.Ordinal);
        Assert.Contains(":pressed", single, StringComparison.Ordinal);
        Assert.Contains("Padding", single, StringComparison.Ordinal);
        Assert.Contains("строка 2", single, StringComparison.Ordinal);
    }

    [Fact]
    public void Ядро_исключение_снимает_ровно_объявленную_пару()
    {
        var declared = AllowedSizeChanges[0];

        var markup = new[]
        {
            new SourceStringsTests.SourceFile(
                @"src\DshPanel\App.axaml",
                $"<Style Selector=\"{declared.Selector}\">\n" +
                $"  <Setter Property=\"{declared.Property}\" Value=\"SemiBold\" />\n</Style>"),
        };

        // С исключением — чисто; без него — ровно одно место. Обе половины обязаны держаться:
        // исключение, которое ничего не снимает, и исключение, которое снимает лишнее, — разные
        // дефекты, и оба видны только так.
        Assert.Empty(SizeChangesInStates(markup));
        Assert.Single(SizeChangesInStates(markup, withExceptions: false));
    }

    [Fact]
    public void Исключения_названы_и_выданы_парой()
    {
        Assert.NotEmpty(AllowedSizeChanges);

        foreach (var (selector, property, why) in AllowedSizeChanges)
        {
            Assert.False(string.IsNullOrWhiteSpace(selector), "исключение без селектора");
            Assert.Contains(property, SizeProperties, StringComparer.Ordinal);
            Assert.False(
                string.IsNullOrWhiteSpace(why),
                $"исключение {selector}/{property} без причины — «ладно» вместо довода");
        }

        // Разрешение выдано ПАРОЙ: тот же селектор с ДРУГИМ свойством размера остаётся нарушением.
        var declared = AllowedSizeChanges[0];
        var other = SizeProperties.First(name => name != declared.Property);

        var markup = new[]
        {
            new SourceStringsTests.SourceFile(
                @"src\DshPanel\App.axaml",
                $"<Style Selector=\"{declared.Selector}\">\n  <Setter Property=\"{other}\" Value=\"1\" />\n</Style>"),
        };

        Assert.Single(SizeChangesInStates(markup));
    }
}
