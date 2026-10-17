using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «СЕРВЕР» — ВИД, КОТОРЫЙ ВЛАДЕЛЕЦ ПРИНЯЛ ПО РЕНДЕРУ 14.10.2026.
///
/// **От какого случая.** Владелец нашёл три места в разделе: полотно технического текста без
/// состояний (умения), «найден»/«не найден» обычными словами и полные пути в личных каталогах
/// (что панель видит на машине), и карточку обновления, предлагавшую «Обновить» на ту же самую
/// версию. Решение принято им **по рендеру** (`..\_mockups\server-v2c-*.png`): **плотная метка
/// со словом внутри**, **серое состояние — просто серым текстом**, **путь — маской**, у папки —
/// **действие строки**, и **никакой шапки колонок** над строками (форма GOV.UK summary list).
///
/// Здесь проверяется ТО, ЧЕГО НЕ ВИДНО ИЗ ЧИСТЫХ РЕШЕНИЙ, — сам вид, и каждым утверждением
/// с мутацией (см. описания):
///
/// 1. **слово внутри каждой метки**, а серое состояние — текстом без плашки (WCAG 2.2 SC 1.4.1:
///    цвет не имеет права быть единственным носителем смысла);
/// 2. **шапки колонок над списками нет** — имя блока сказано заголовком карточки;
/// 3. **в разделе нет имени пользователя, нет `C:\Users` и нет полного домашнего каталога** —
///    ни в разборе, ни в нарисованном виде, **в двух темах**;
/// 4. **«Открыть папку» есть ровно у строк с папкой** и зовёт ОБЩУЮ дверь панели;
/// 5. **кадры** раздела в двух темах сняты и не пусты.
///
/// ⚠️ Чего проверки НЕ доказывают и что читает человек: **понятность** («фраза стала короче» и
/// «фраза стала понятной» — разные утверждения) и **смысл перевода**. Кадры для этого и сняты.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ServerSectionLookTests
{
    // ------------------------------------------------------------------ владелец настроек

    /// <summary>
    /// ПОДСТАВНОЙ ВЛАДЕЛЕЦ НАСТРОЕК: отчёт задаёт проверка, а дверь папки СЧИТАЕТ вызовы.
    /// Настоящая дверь открыла бы проводник на рабочем столе владельца — этого в прогоне быть
    /// не должно ни при каком состоянии раздела.
    /// </summary>
    private sealed class Stand : ISettingsControl
    {
        public List<string> Opened { get; } = new();

        public EnvironmentReport Report { get; set; } = new(Array.Empty<EnvironmentGroup>());

        public PanelSettings Settings => PanelSettings.Default;

        public bool Writable => true;

        public string LoadProblem => string.Empty;

        public string WorkDirInUse => string.Empty;

        public IReadOnlyList<WorkDirSuggestion> Suggestions() => Array.Empty<WorkDirSuggestion>();

        public bool Save(PanelSettings settings) => true;

        public bool SetBackupWithKeys(bool value) => true;

        public bool SetActiveAgent(string agentId) => true;

        public bool SetBackupKeyDirs(IReadOnlyList<string> directories) => true;

        public bool SetAdoptedServer(int port) => true;

        public bool SetWindowPlacement(int x, int y, int width, int height) => true;

        public void PreviewTheme(string theme)
        {
        }

        public EnvironmentReport EnvironmentInfo() => Report;

        public string EnvironmentText() => Report.Text();

        public bool OpenSettingsFolder(out string error)
        {
            error = string.Empty;
            return true;
        }

        /// <summary>«Открыл» — и запомнил, ЧТО именно просили открыть: этим проверяется дверь строки.</summary>
        public bool OpenFolder(string folder, out string error)
        {
            Opened.Add(folder);
            error = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// ОТЧЁТ ДЛЯ ПРОВЕРОК — с ВСЕМИ ЧЕТЫРЬМЯ состояниями строки и тремя строками-папками.
    ///
    /// ⚠️ Умения берутся той же дверью, что у отчёта (`EngineSkills.Measure(EngineSkillProbe.None)`):
    /// выдуманное состояние умений показало бы вид, которого у человека не бывает.
    /// </summary>
    private static EnvironmentReport Report()
    {
        var paths = AppPaths.Under(@"C:\Проба\панель");
        var facts = EngineSkillProbe.None(paths);

        return EnvironmentReport.Build(
            engine: null,
            dshVersion: string.Empty,
            nodeVersion: string.Empty,
            npmVersion: string.Empty,
            pnpmVersion: string.Empty,
            paths: paths,
            workDir: @"C:\Проба\работа",
            workDirConfigured: false,
            serverPort: 3081,
            portState: PanelStrings.EnvPortFree,
            skills: EngineSkills.Measure(facts),
            skillFacts: facts);
    }

    /// <summary>Окно настроек РАЗДЕЛА «СЕРВЕР» с подставным владельцем: отчёт задан, дверь считает.</summary>
    private static (SettingsWindow Window, Stand Owner) Serve()
    {
        var owner = new Stand { Report = Report() };
        var window = new SettingsWindow();

        window.Attach(owner, autostart: null);
        window.ShowSection(SettingsWindow.ServerSection);
        window.Show();
        PanelTestStand.Settle();

        return (window, owner);
    }

    /// <summary>Панель отчёта — в ней и только в ней живёт вид раздела.</summary>
    private static StackPanel EnvironmentPanel(SettingsWindow window)
    {
        var panel = window.FindControl<StackPanel>("EnvironmentPanel");

        Assert.NotNull(panel);
        return panel!;
    }

    /// <summary>Строки вида: то, что нарисовано, — а не то, что лежит в разборе.</summary>
    private static List<TextBlock> Drawn(SettingsWindow window) =>
        EnvironmentPanel(window).GetVisualDescendants().OfType<TextBlock>().ToList();

    // ------------------------------------------------------------------ 1. слово внутри метки

    /// <summary>
    /// ЦВЕТ НЕ ЕДИНСТВЕННЫЙ НОСИТЕЛЬ СМЫСЛА: в каждой метке стоит СЛОВО, а серое состояние
    /// показано СЕРЫМ ТЕКСТОМ без плашки (решение владельца 14.10.2026; WCAG 2.2 SC 1.4.1
    /// и GOV.UK: «Do not use colour alone»; «grey text with no background colour»).
    ///
    /// **Что именно меряется.** Считаются НАРИСОВАННЫЕ органы: метка-плашка (`Border.stateTag`)
    /// и серое слово (`TextBlock.stateGrey`). Их обязано быть столько же, сколько строк со словом,
    /// у каждой плашки внутри — непустое слово, а у каждой строки с серым состоянием — НИ ОДНОЙ
    /// плашки.
    ///
    /// Мутации, которые обязаны упасть здесь: **«убрать слово из метки»** (плашка осталась,
    /// а внутри пусто) · **«покрасить серое состояние плашкой»** (серых слов не осталось) ·
    /// **«показать состояние только цветом»**.
    /// </summary>
    [AvaloniaFact]
    public void Слово_стоит_внутри_каждой_метки_а_серое_состояние_идёт_текстом()
    {
        var (window, owner) = Serve();

        try
        {
            var lines = owner.Report.Groups.SelectMany(group => group.Lines).ToList();

            var worded = lines.Where(line => line.Word.Length > 0).ToList();
            var grey = worded.Where(line => line.State == EnvLineState.Grey).ToList();
            var tagged = worded.Where(line => line.State != EnvLineState.Grey).ToList();

            Assert.NotEmpty(tagged);
            Assert.NotEmpty(grey);

            var plates = EnvironmentPanel(window)
                .GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("stateTag"))
                .ToList();

            // 1) ПЛАШКА — НА КАЖДОЕ ЦВЕТНОЕ СОСТОЯНИЕ, и внутри неё СЛОВО.
            Assert.Equal(tagged.Count, plates.Count);

            foreach (var plate in plates)
            {
                var inside = plate.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();

                Assert.True(inside is not null, "метка состояния нарисована БЕЗ слова — цвет остался один");
                Assert.False(
                    string.IsNullOrWhiteSpace(inside!.Text),
                    "в метке состояния пусто: состояние показано одним цветом (WCAG 1.4.1)");

                Assert.Contains(tagged, line => line.Word == inside.Text);
            }

            // 2) СЕРОЕ СОСТОЯНИЕ — ТЕКСТОМ, БЕЗ ПЛАШКИ: у него своей метки нет вовсе.
            var greyWords = Drawn(window)
                .Where(block => block.Classes.Contains("stateGrey"))
                .ToList();

            Assert.Equal(grey.Count, greyWords.Count);

            foreach (var line in grey)
            {
                Assert.Contains(greyWords, block => block.Text == line.Word);
                Assert.DoesNotContain(
                    plates,
                    plate => plate.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == line.Word));
            }
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    // ------------------------------------------------------------------ 2. шапки колонок нет

    /// <summary>
    /// НАД СПИСКАМИ РАЗДЕЛА НЕТ ШАПКИ КОЛОНОК (решение владельца 14.10.2026, форма GOV.UK
    /// summary list): имя блока сказано ЗАГОЛОВКОМ КАРТОЧКИ, под ним — одна строка пояснения
    /// и строки-находки. Шапка «Что · Значение» над восемью строками объясняла бы то, что и так
    /// видно.
    ///
    /// **Как это ловится без разбора слов.** Карточка обязана состоять РОВНО из заголовка,
    /// пояснения и строк: любая лишняя деталь перед строками (в том числе шапка с новыми словами)
    /// ломает и число детей, и их вид. Проверять шапку по её словам нельзя — слова шапки можно
    /// написать любые, и проверка стала бы проверкой одного набора букв.
    ///
    /// Мутация, которая обязана упасть здесь: **вернуть строку-шапку колонок** (над первым рядом).
    /// </summary>
    [AvaloniaFact]
    public void Над_списками_раздела_нет_шапки_колонок()
    {
        var (window, owner) = Serve();

        try
        {
            var cards = EnvironmentPanel(window).Children.OfType<Border>().ToList();

            Assert.Equal(owner.Report.Groups.Count, cards.Count);
            Assert.NotEmpty(cards);

            for (var index = 0; index < cards.Count; index++)
            {
                var group = owner.Report.Groups[index];
                var children = ((StackPanel)cards[index].Child!).Children;

                // Заголовок, пояснение и ровно столько строк, сколько строк в разборе.
                Assert.Equal(group.Lines.Count + 2, children.Count);

                Assert.Equal(group.Title, Assert.IsType<TextBlock>(children[0]).Text);
                Assert.Equal(group.Subtitle, Assert.IsType<TextBlock>(children[1]).Text);

                foreach (var child in children.Skip(2))
                {
                    var row = Assert.IsType<Border>(child);

                    Assert.Contains("envRow", row.Classes);
                    Assert.IsType<Grid>(row.Child);
                }
            }
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    // ------------------------------------------------------------------ 3. путь под маской

    /// <summary>
    /// В РАЗДЕЛЕ «СЕРВЕР» НЕТ ЛИЧНЫХ ПУТЕЙ — НИ В РАЗБОРЕ, НИ НАРИСОВАННЫМИ, И В ДВУХ ТЕМАХ.
    ///
    /// **От какого случая.** Это закрытие долга, записанного в банке (`90-our-deviations.md`):
    /// *«Маска личных путей в разделе „Сервер“: пути показаны полностью, хотя в „Копиях“ тот же
    /// домашний каталог замаскирован (`~`). Кадр раздела „Сервер“ унёс бы имя пользователя»*.
    /// Владелец увидел это живьём — `C:\Users\&lt;имя&gt;\AppData\Roaming\npm\…`.
    ///
    /// ⚠️ **Путь берётся НАСТОЯЩИЙ и с ЭТОЙ машины** (временный корень прогона лежит внутри
    /// каталога пользователя), потому что выдуманный путь ничего бы не доказал: маска обязана
    /// работать на том самом виде пути, которым утекало имя.
    ///
    /// ⚠️ **Кадр проверяется кодом, а не глазами:** мерятся ТЕКСТЫ нарисованного раздела — то,
    /// что кадр и рисует. Содержимое PNG (пиксели) человек смотрит сам: машина в буквах картинки
    /// имени пользователя не найдёт.
    ///
    /// Мутации, которые обязаны упасть здесь: **показать полный путь** · **собрать путь строки
    /// в разборе, а не взять у `AppPaths`** · **снять маску с подробности строки**.
    /// </summary>
    [AvaloniaFact]
    public void В_разделе_нет_имени_пользователя_и_полного_пути()
    {
        var dir = PanelTestStand.TempDir();
        var saved = Application.Current!.RequestedThemeVariant;

        try
        {
            // ВЛАДЕЛЕЦ НАСТРОЕК — НАСТОЯЩИЙ, и права на запись у него НЕТ: так раздел в прогоне
            // проверки и выглядит, а настоящая дверь папки не зовётся вовсе (щелчков здесь нет).
            // Пути приходят от `AppPaths` ВНУТРИ каталога пользователя — то есть ровно тем видом
            // пути, которым имя и утекало.
            var paths = AppPaths.Under(dir);

            var controller = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: false,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            var window = new SettingsWindow();
            window.Attach(controller, autostart: null);
            window.ShowSection(SettingsWindow.ServerSection);
            window.Show();
            PanelTestStand.Settle();

            var report = controller.EnvironmentInfo();
            var personal = new[]
            {
                @"C:\Users",
                Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();

            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = variant;
                PanelTestStand.Settle();

                // 1) РАЗБОР: ни одно поле строки не несёт личного пути — включая текст для журнала.
                foreach (var group in report.Groups)
                {
                    foreach (var line in group.Lines)
                    {
                        foreach (var value in new[] { line.What, line.Word, line.Detail, line.Result, line.Text() })
                        {
                            foreach (var secret in personal)
                            {
                                Assert.DoesNotContain(secret, value, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                }

                // 2) И НАРИСОВАННОЕ: то, что уезжает в кадр.
                var drawn = Drawn(window).Select(block => block.Text ?? string.Empty).ToList();

                Assert.NotEmpty(drawn);

                foreach (var text in drawn)
                {
                    foreach (var secret in personal)
                    {
                        Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
                    }
                }

                // 3) И ПУТЬ ВИДЕН — ПОД МАСКОЙ: иначе «нет личного» прошло бы и на пустом разделе.
                Assert.Contains(drawn, text => text.StartsWith('~'));

                // 4) КАДР: снят, не пуст и на нём что-то нарисовано, а не белый лист.
                var frame = PanelTestStand.SaveFrame(window, "settings-server-" + variant);

                Assert.True(new FileInfo(frame).Length > 0, $"кадр {variant} пуст");

                var panel = EnvironmentPanel(window);
                var origin = panel.TranslatePoint(new Point(0, 0), window)!.Value;
                var area = new Rect(origin, panel.Bounds.Size);

                Assert.True(
                    PanelTestStand.ColorsIn(window, area) > 8,
                    $"кадр {variant}: в разделе меньше девяти разных цветов — похоже, нарисован белый лист");
            }

            window.CloseQuietly();
        }
        finally
        {
            Application.Current.RequestedThemeVariant = saved;
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ 4. действие строки

    /// <summary>
    /// «ОТКРЫТЬ ПАПКУ» ЕСТЬ РОВНО У СТРОК С ПАПКОЙ, И ОНА ЗОВЁТ ОБЩУЮ ДВЕРЬ ПАНЕЛИ.
    ///
    /// **От чего.** Прежде путь в разделе «Сервер» показывался полотном и его нельзя было открыть;
    /// решение владельца 14.10.2026 — **действие строки** (как в GOV.UK summary list), тем же путём,
    /// что у «Копий» (`Shell\AgentBrowser.TryOpenFolder`).
    ///
    /// ⚠️ **Кнопка без папки не «спрятана», а ЕЁ НЕТ ВОВСЕ:** спрятанная кнопка — мёртвый орган
    /// в дереве окна, и она же путает проверки, которые перебирают кнопки окна.
    ///
    /// Мутации, которые обязаны упасть здесь: **показать кнопку без пути** (кнопка в никуда) ·
    /// **открыть не ту папку** · **позвать дверь не из строки, а из окна**.
    /// </summary>
    [AvaloniaFact]
    public void Открыть_папку_есть_ровно_у_строк_с_папкой()
    {
        var (window, owner) = Serve();

        try
        {
            var lines = owner.Report.Groups.SelectMany(group => group.Lines).ToList();
            var folders = lines.Where(line => line.Folder.Length > 0).ToList();

            Assert.Equal(3, folders.Count);

            var buttons = EnvironmentPanel(window)
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.TemplatedParent is null)
                .ToList();

            // Кнопок — РОВНО столько, сколько строк с папкой: у строки без папки действия нет.
            Assert.Equal(folders.Count, buttons.Count);

            foreach (var button in buttons)
            {
                Assert.Equal(PanelStrings.EnvOpenFolderButton, button.Content);
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string), "у кнопки строки нет подсказки");

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            PanelTestStand.Settle();

            // И ЗОВЁТ ОНА ИМЕННО ТУ ПАПКУ, которую показывает строка, — по одной на каждую.
            Assert.Equal(folders.Select(line => line.Folder).ToList(), owner.Opened);
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// У СТРОКИ БЕЗ ПАПКИ ДЕЙСТВИЯ НЕТ ВОВСЕ, А У СТРОКИ С ПАПКОЙ ПОКАЗАН ПУТЬ ПОД МАСКОЙ.
    ///
    /// За этим утверждением стоят две разные вещи, и обе — решения владельца 14.10.2026: действие
    /// строки появляется только там, где есть что открывать, а **показанный путь и рабочий путь —
    /// РАЗНЫЕ**: наружу выходит только маскированный (`Shell\DisplayMask`), а настоящий лежит
    /// в <see cref="EnvironmentLine.Folder"/> и открывается кнопкой.
    ///
    /// Мутации, которые обязаны упасть здесь: **«показать в подробности настоящий путь»** ·
    /// **«дать порту папку»** (тогда у строки без папки появилось бы действие).
    /// </summary>
    [Fact]
    public void У_строки_без_папки_нет_действия_а_путь_показан_под_маской()
    {
        var lines = Report().Groups.SelectMany(group => group.Lines).ToList();

        // У ПОРТА папки нет — значит и действия у него нет вовсе (кнопка в никуда не появляется).
        var port = Assert.Single(lines, line => line.What == PanelStrings.EnvServerPort);

        Assert.Empty(port.Folder);

        var folders = lines.Where(line => line.Folder.Length > 0).ToList();

        Assert.Equal(3, folders.Count);

        foreach (var line in folders)
        {
            Assert.StartsWith(DisplayMask.Path(line.Folder), line.Detail, StringComparison.Ordinal);
        }

        // И ЭТО ИМЕННО ТРИ СТРОКИ-ПАПКИ, а не три случайных: их порядок в карточке — от данных
        // сервера к настройкам панели.
        Assert.Equal(
            new[] { PanelStrings.EnvDshHome, PanelStrings.EnvWorkDir, PanelStrings.EnvSettingsFile },
            folders.Select(line => line.What).ToArray());
    }
}
