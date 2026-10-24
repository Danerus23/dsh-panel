using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DshPanel.Shell;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «ОБНОВЛЕНИЕ» В ОКНЕ НАСТРОЕК — решение владельца 28.09.2026.
///
/// Слова владельца, ради которых это сделано: *«Я предполагал что это будет просто нормальный пункт
/// меню как и остальные во вкладке настройки, может быть просто „обновления“. Кнопки пропустить,
/// проверить и т.д. можно было в 1 ряд поместить»*. Прежде обновление было кнопкой-дверью внизу
/// окна, и это читалось как чужой элемент среди разделов, а кнопки действий стояли переносимым
/// рядом и на узком окне складывались в два.
///
/// Три обещания, и каждое проверяется прогоном:
///
/// 1. **раздел есть в списке слева** и называется так, как назвал владелец;
/// 2. **ряд действий стоит в ОДНУ строку** — меряется геометрией, а не взглядом на разметку:
///    четыре кнопки обязаны уложиться в ширину содержимого раздела;
/// 3. **состояние показывается словами** чистыми решениями ядра обновления
///    (<see cref="UpdateLines"/>): номер с GitHub не выдаётся за новость, а заметки разбираются
///    строками.
///
/// ⚠️ **Это единственное место, где панель показывает ответ об обновлении** (решение владельца
/// 09.10.2026: *«Я не хочу отдельно вызываемое окно»*). Прежнее отдельное окно убрано, его кадр
/// «свой же выпуск» (<c>update-latest-*</c>) снимается теперь отсюда.
/// </summary>
public class SettingsUpdateSectionTests
{
    private sealed class FakeUpdate : IUpdateControl
    {
        public FakeUpdate(
            bool ok = true,
            string latest = "v2.1.0",
            string current = "2.0.0",
            bool skipped = false,
            bool allowed = true,
            string notes = "",
            string attempt = "")
        {
            Current = current;
            Skipped = skipped;
            Allowed = allowed;
            AttemptText = attempt;

            Result = ok
                ? new UpdateRelease(
                    true,
                    string.Empty,
                    latest,
                    ProductLinks.ReleasesPageUrl,
                    "2026-09-27T10:00:00Z",
                    notes,
                    notes,
                    DateTimeOffset.Now)
                {
                    Assets = new ReleaseAssets(
                        "https://example.invalid/DshPanel.zip",
                        string.Empty,
                        "https://example.invalid/SHA256SUMS.txt"),
                }
                : UpdateRelease.Failed("HTTP 500", DateTimeOffset.Now);
        }

        public UpdateRelease Result { get; }

        public bool Busy => false;

        public bool Allowed { get; }

        public string Current { get; }

        public bool Newer => Result.Ok && UpdateDecisions.IsNewer(Result.Latest, Current);

        public bool Skipped { get; }

        /// <summary>Строка о последней попытке: пусто — попытки не было или она удалась.</summary>
        public string AttemptText { get; }

        /// <summary>
        /// «Когда проверяли». В непроверенном состоянии — ТА ЖЕ строка, что у решения
        /// (`PanelStrings.UpdateNeverChecked`): окно гасит её сравнением с решением
        /// (`UpdateLines.SourceLine`), и своя копия текста в подстановке ломала бы это сравнение.
        /// Так же поступает и настоящий контроллер (`UpdateController.SourceText`).
        /// </summary>
        public string SourceText => Result.Ok ? "проверено 28.09.2026, 15:00" : PanelStrings.UpdateNeverChecked;

        public string StatusText => Result.Ok ? "Доступна версия " + Result.Latest : SourceText;

        public TrayStatusLine TrayLine => new(StatusText, TrayTone.Neutral);

        public int Checks { get; private set; }

        public event Action<string>? Announce { add { } remove { } }

        public event Action? Changed { add { } remove { } }

        public void Check() => Checks++;
    }

    /// <summary>
    /// Окно настроек с подставным состоянием выпусков: живой путь тот же, сети нет.
    /// </summary>
    private static SettingsWindow Stand(
        string dir,
        FakeUpdate? update,
        FakeInstall? install = null,
        Func<string, bool>? skipUpdate = null,
        Action? exitForUpdate = null)
    {
        var window = PanelTestStand.SettingsStand(
            dir,
            update: update,
            install: install,
            skipUpdate: skipUpdate,
            exitForUpdate: exitForUpdate);

        window.Show();
        PanelTestStand.Settle();

        return window;
    }

    /// <summary>
    /// ДОЖДАТЬСЯ ФОНОВОЙ РАБОТЫ: нажатие «Скачать и подготовить» уходит в фоновую задачу, и её
    /// исход проверка обязана увидеть. Опрос ОГРАНИЧЕН по времени: не дождались — провал с
    /// названной причиной, а не повисший прогон.
    /// </summary>
    private static void WaitFor(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);

        while (!done() && DateTime.UtcNow < until)
        {
            PanelTestStand.Settle();
            System.Threading.Thread.Sleep(10);
        }

        Assert.True(done(), what);
    }

    /// <summary>
    /// Движок установки, каким его видит раздел: ничего не качает и ничего не пишет.
    ///
    /// ⚠️ Право он проверяет САМ, как настоящий (<c>UpdateInstall.Prepare</c> отвечает отказом
    /// <see cref="UpdateRefusal.NotAllowed"/>): раздел по праву только ПОКАЗЫВАЕТ кнопку, а решает
    /// движок, и подставной обязан повторять это, иначе проверка «без права отказывает» проверяла
    /// бы собственную выдумку.
    /// </summary>
    private sealed class FakeInstall : IUpdateInstall
    {
        public FakeInstall(
            bool allowed = true,
            UpdatePreparation? answer = null,
            string launch = "",
            Action? afterProgress = null,
            Exception? failure = null)
        {
            Allowed = allowed;
            Answer = answer;
            LaunchError = launch;
            AfterProgress = afterProgress;
            Failure = failure;
        }

        public bool Allowed { get; }

        /// <summary>Что движок ответит на «подготовить». <c>null</c> — отказ «обновляться некуда».</summary>
        public UpdatePreparation? Answer { get; set; }

        public string LaunchError { get; }

        /// <summary>
        /// Зовётся СРАЗУ ПОСЛЕ сообщения о ходе и ДО ответа: проверке нужно увидеть состояние
        /// «работа идёт» тем же путём, каким его видит человек. Иначе «в процессе — как было»
        /// нечем опровергнуть, и очистка, позванная на каждой перерисовке, прошла бы незамеченной.
        /// </summary>
        public Action? AfterProgress { get; }

        /// <summary>
        /// Исключение ВМЕСТО ответа — беда, от которой заведена ветка <c>catch</c> в
        /// <c>SettingsWindow.PrepareUpdate</c>. Настоящий движок бросает при неожиданности
        /// (диск, права, разбор), и ход подготовки обязан исчезнуть и здесь.
        /// </summary>
        public Exception? Failure { get; }

        public int Prepared { get; private set; }

        public int Launched { get; private set; }

        public string LastRelease { get; private set; } = string.Empty;

        public ReleaseAssets LastAssets { get; private set; } = ReleaseAssets.None;

        public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null)
        {
            Prepared++;
            LastRelease = release;
            LastAssets = assets;

            progress?.Invoke(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 50, 100));
            AfterProgress?.Invoke();

            if (Failure is not null) throw Failure;

            if (!Allowed)
            {
                return UpdatePreparation.Refuse(UpdateRefusal.NotAllowed, "у прогона нет права готовить замену файлов");
            }

            return Answer ?? UpdatePreparation.Refuse(UpdateRefusal.AlreadyLatest, "выпуск не новее");
        }

        public string Launch(UpdatePreparation preparation)
        {
            Launched++;

            return LaunchError;
        }
    }

    /// <summary>
    /// Готовая подготовка — тот же ответ, что отдаёт настоящее ядро, и с путями, в которых есть имя
    /// человека: так проверяется, что раздел показывает путь МАСКОЙ (красная линия 7).
    /// </summary>
    private static UpdatePreparation Prepared() => new(
        true,
        UpdateRefusal.None,
        string.Empty,
        "2.1.0",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\2.1.0",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\backup-2.0.0",
        @"C:\Users\person\AppData\Local\Programs\DSH Panel",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\update.cmd",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\update.log",
        "\"\"update.cmd\" \"42\" \"C:\\Panel\" \"C:\\Staged\" \"C:\\Backup\" \"C:\\Log\" \"Mutex\" \"\"\"");

    /// <summary>
    /// ОТКАЗ, ПОСЛЕ КОТОРОГО ПОВТОРЯТЬ ЕСТЬ ЧЕМ: право готовить замену на месте, выпуск новее,
    /// адреса вложений известны — а работа не вышла (архив не скачался). Ровно так выглядит
    /// настоящий отказ после нажатия: кнопка подготовки к этому мгновению снова доступна,
    /// и именно на неё возвращается фокус (DEF-008).
    /// </summary>
    private static UpdatePreparation Refused() =>
        UpdatePreparation.Refuse(UpdateRefusal.ArchiveDownloadFailed, "timeout");

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ» ЕСТЬ В СПИСКЕ — последним, как и остальные разделы настроек.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_обновления_есть_в_списке_разделов()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());
            var list = window.FindControl<ListBox>("SectionsList")!;

            var titles = list.ItemsSource!.Cast<string>().ToList();

            Assert.Contains(PanelStrings.UpdateSectionTitle, titles);
            Assert.Equal(SettingsWindow.UpdateSection, titles.IndexOf(PanelStrings.UpdateSectionTitle));

            // И показывается именно он, когда его выбрали.
            Assert.Equal(SettingsWindow.UpdateSection, window.ShowSection(SettingsWindow.UpdateSection));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ⚠️ ТРЕБОВАНИЕ ВЛАДЕЛЬЦА 11.10.2026 (проверка обязана падать, если это сломать): **РАЗДЕЛ
    /// НЕ ТЕРЯЕТ РЕЗУЛЬТАТ ПРОВЕРКИ.** Прежний ответ (тот, что сохранён с датой) стоит в окне,
    /// а неудачная попытка названа ОТДЕЛЬНОЙ строкой — и вместе они дают полную правду:
    /// «что мы знаем» и «что новая попытка не вышла».
    ///
    /// Пустое окно после настоящей работы — тот самый дефект, ради которого хвост и заведён.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_не_теряет_результат_проверки_из_за_отказа()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(attempt: "последняя попытка: 11.10.2026, 15:04 — не вышло: HTTP 503"));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            // Прежний ответ на месте: версия, решение и дата проверки.
            Assert.Contains("v2.1.0", window.FindControl<TextBlock>("UpdateVerdictText")!.Text!, StringComparison.Ordinal);

            var source = window.FindControl<TextBlock>("UpdateSourceText")!;

            Assert.True(source.IsVisible, "строка «когда проверяли» пропала — окно выглядит как «не проверялось»");
            Assert.NotEqual(PanelStrings.UpdateNeverChecked, source.Text);

            // И строка о попытке тоже видна: причина отказа не спрятана.
            var attempt = window.FindControl<TextBlock>("UpdateAttemptText")!;

            Assert.True(attempt.IsVisible, "строка о неудачной попытке не показана");
            Assert.Contains("HTTP 503", attempt.Text!, StringComparison.Ordinal);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СТРОКА О ПОПЫТКЕ МОЛЧИТ, КОГДА МОЛЧАТЬ ПРАВИЛЬНО: у удачной проверки про время говорит
    /// строка «проверено {дата}», и повторять то же самое второй строкой панель не должна.
    /// </summary>
    [AvaloniaFact]
    public void Строка_о_попытке_молчит_у_удачной_проверки()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            Assert.False(window.FindControl<TextBlock>("UpdateAttemptText")!.IsVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// КНОПКИ ДЕЙСТВИЙ — В ОДНУ СТРОКУ. Это главное обещание раздела: владелец назвал переносимый
    /// ряд «сделано очень небрежно», когда тот складывался в две строки.
    ///
    /// ⚠️ Проверка МЕРЯЕТ ГЕОМЕТРИЮ на минимальной высоте и при выбранном разделе: четыре кнопки
    /// обязаны уложиться в ширину содержимого, а самая правая — не выйти за правый край.
    /// На разметку смотреть бесполезно: переносимый ряд и жёсткий ряд выглядят в тексте одинаково,
    /// а на экране ведут себя по-разному.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_действий_стоят_в_один_ряд()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var buttons = new[]
            {
                window.FindControl<Button>("UpdateCheckButton"),
                window.FindControl<Button>("UpdateGithubButton"),
                window.FindControl<Button>("UpdateSkipButton"),
                window.FindControl<Button>("UpdatePrepareButton"),
            };

            Assert.All(buttons, button => Assert.NotNull(button));

            // Все четыре — в ОДНОЙ строке: у них одна вертикаль и один родитель-панель.
            var tops = buttons.Select(button => button!.TranslatePoint(new Point(0, 0), window)!.Value.Y).ToList();
            var parents = buttons.Select(button => button!.Parent).Distinct().ToList();

            Assert.Single(parents);
            Assert.True(
                tops.Max() - tops.Min() <= 0.5,
                $"кнопки стоят на разных строках: Y = {string.Join(", ", tops.Select(y => y.ToString("0.#")))}; " +
                $"ширины: {string.Join(", ", buttons.Select(b => $"«{b!.Content}»={b.Bounds.Width:0.#}"))}; " +
                $"окно {window.ClientSize.Width:0.#}");

            // И ряд не выходит за правый край окна: кнопка, уехавшая за край, для человека
            // не существует — это и было замечанием про прокрутку.
            foreach (var button in buttons)
            {
                var widget = button!;
                var left = widget.TranslatePoint(new Point(0, 0), window)!.Value.X;
                var right = left + widget.Bounds.Width;

                Assert.True(widget.Bounds.Width > 0, $"у кнопки «{widget.Content}» нулевая ширина");
                Assert.True(right <= window.ClientSize.Width + 0.5,
                    $"кнопка «{widget.Content}» кончается на {right:0.#}, а окно — на {window.ClientSize.Width:0.#}; " +
                    $"ширины: {string.Join(", ", buttons.Select(b => $"{b!.Bounds.Width:0.#}"))}; " +
                    $"ряд = {((Control)parents[0]!).Bounds.Width:0.#}; содержимое = {window.FindControl<ScrollViewer>("ContentScroll")!.Bounds.Width:0.#}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОСТОЯНИЕ В РАЗДЕЛЕ — ТЕМИ ЖЕ РЕШЕНИЯМИ, ЧТО У ОКНА ОБНОВЛЕНИЯ: номер с GitHub не выдаётся
    /// за новость, а заметки идут строками, не простынёй. Второго сравнения версий в панели быть
    /// не должно — оно однажды разошлось бы с первым.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_показывает_состояние_и_заметки_строками()
    {
        var dir = PanelTestStand.TempDir();

        const string notes = "### Что нового\n\n- Первое\n- Второе\n\nАбзац заметок.";

        try
        {
            var window = Stand(dir, new FakeUpdate(notes: notes));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            // Версии и решение — словами словаря.
            Assert.Contains("2.0.0", window.FindControl<TextBlock>("UpdateCurrentText")!.Text!, StringComparison.Ordinal);
            Assert.Contains("v2.1.0", window.FindControl<TextBlock>("UpdateGithubText")!.Text!, StringComparison.Ordinal);
            Assert.Contains("v2.1.0", window.FindControl<TextBlock>("UpdateVerdictText")!.Text!, StringComparison.Ordinal);
            Assert.Equal(PanelStrings.UpdateSectionTitle, window.FindControl<TextBlock>("UpdateSectionTitle")!.Text);

            // Заметки — строками, и они прокручиваются в своём поле.
            var panel = window.FindControl<StackPanel>("UpdateNotesPanel")!;

            Assert.True(panel.Children.Count > 3, $"строк заметок: {panel.Children.Count} — похоже, простыня");
            Assert.Contains(
                panel.Children.OfType<TextBlock>(),
                block => block.Text == "Что нового");

            // Замены файлов в разделе по умолчанию НЕТ: кнопка замены появляется только после
            // подготовки — это единственная дверь продукта, подменяющая саму панель.
            Assert.False(window.FindControl<Button>("UpdateReplaceButton")!.IsVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «ПРОВЕРИТЬ СЕЙЧАС» ПРОСИТ ПРОВЕРКУ — кнопка действует, а не только выглядит. Суточный гейт
    /// при этом не спрашивается: человек попросил.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_проверить_сейчас_просит_проверку()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var update = new FakeUpdate();
            var window = Stand(dir, update);

            window.FindControl<Button>("UpdateCheckButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, update.Checks);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА ГОТОВИТЬ ЗАМЕНУ: кнопка недоступна и говорит причину словами — тем же
    /// решением, что у всякого, кто показывает состояние обновления (<see cref="UpdateLines.PrepareState"/>).
    /// </summary>
    [AvaloniaFact]
    public void Без_права_подготовка_называет_причину()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            // Окно построено с настоящим движком установки ЭТОГО прогона (проверка), поэтому права
            // готовить замену у него нет — и это сказано словами, а не молчанием кнопки.
            var prepare = window.FindControl<Button>("UpdatePrepareButton")!;
            var hint = window.FindControl<TextBlock>("UpdateHintText")!.Text ?? string.Empty;

            Assert.False(prepare.IsEnabled);
            Assert.True(hint.Length > 0, "кнопка недоступна, а причины не названо");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НА СВОЁМ ЖЕ ВЫПУСКЕ РАЗДЕЛ ГОВОРИТ ТО ЖЕ, ЧТО ОКНО: обновляться не нужно и пропускать
    /// нечего. Правило одно на два места (замечание владельца 09.10.2026), и проверяется здесь
    /// НАСТОЯЩЕЙ кнопкой раздела — иначе окно и настройки разошлись бы, а человек читает оба.
    /// </summary>
    [AvaloniaFact]
    public void На_своём_выпуске_раздел_не_предлагает_пропускать()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(latest: "2.0.0", current: "2.0.0"));
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var verdict = window.FindControl<TextBlock>("UpdateVerdictText")!.Text ?? string.Empty;
            var skip = window.FindControl<Button>("UpdateSkipButton")!;

            Assert.Contains("2.0.0", verdict, StringComparison.Ordinal);
            Assert.False(skip.IsEnabled, "пропускать нечего: версия уже установлена");
            Assert.Equal(
                Avalonia.Controls.ToolTip.GetTip(skip) as string,
                PanelStrings.TipUpdateSkipNothingNew);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НА СВОЁМ ЖЕ ВЫПУСКЕ РАЗДЕЛ ГОВОРИТ, ЧТО ДЕЛАТЬ НЕЧЕГО, — и обе кнопки это подтверждают
    /// делом, а кадр этого состояния снимается С РАЗДЕЛА.
    ///
    /// ⚠️ Здесь живёт кадр <c>update-latest-*</c>, который прежде снимался с окна обновления
    /// (`PanelTestStand.SaveFrame`). Окна больше нет (решение владельца 09.10.2026), а кадр
    /// остался: он был доказательством для владельца — он смотрел ГЛАЗАМИ, что на своём выпуске
    /// панель не предлагает обновиться и не предлагает пропускать. Прогон без права в сеть
    /// (`--shot settings-update`) показывает «проверка не делалась», а не «своя же версия»,
    /// поэтому состояние «свой выпуск» снимается только отсюда.
    /// </summary>
    [AvaloniaFact]
    public void На_своём_выпуске_раздел_говорит_обновляться_не_нужно_и_кадр_этого_виден()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(
                dir,
                update: new FakeUpdate(latest: "2.0.0", current: "2.0.0"),
                install: new FakeInstall());

            window.ShowSection(SettingsWindow.UpdateSection);
            window.Show();
            PanelTestStand.Settle();

            var verdict = window.FindControl<TextBlock>("UpdateVerdictText")!.Text ?? string.Empty;
            var skip = window.FindControl<Button>("UpdateSkipButton")!;
            var prepare = window.FindControl<Button>("UpdatePrepareButton")!;

            Assert.Contains("2.0.0", verdict, StringComparison.Ordinal);
            Assert.Contains("не нужно", verdict, StringComparison.OrdinalIgnoreCase);

            Assert.False(skip.IsEnabled, "пропускать нечего: версия уже установлена");
            Assert.Equal(PanelStrings.TipUpdateSkipNothingNew, Avalonia.Controls.ToolTip.GetTip(skip) as string);

            Assert.False(prepare.IsEnabled, "готовить нечего: выпуск не новее установленной панели");
            Assert.Equal(
                PanelStrings.UpdateAlreadyLatest,
                window.FindControl<TextBlock>("UpdateHintText")!.Text);

            var frame = PanelTestStand.SaveFrame(window, "update-latest-" + Guid.NewGuid().ToString("N")[..8]);

            Assert.True(File.Exists(frame), "кадр не сохранён: " + frame);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// РЕШЕНИЕ СТОИТ ПЕРЕД ВЕРСИЯМИ — тот же порядок, что был у прежнего окна обновления,
    /// и та же причина.
    /// ⚠️ Замечание владельца 09.10.2026: в окне порядок поправили, а в разделе настроек он остался
    /// СТАРЫМ — и владелец увидел это сразу («дизайн пункта обновления старый»). Проверяется
    /// ГЕОМЕТРИЕЙ показанного раздела, а не порядком строк в разметке: строку можно переставить
    /// в файле и не увидеть на экране.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_ставит_решение_перед_версиями()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var verdict = window.FindControl<TextBlock>("UpdateVerdictText")!;
            var current = window.FindControl<TextBlock>("UpdateCurrentText")!;
            var github = window.FindControl<TextBlock>("UpdateGithubText")!;

            Assert.True(verdict.Bounds.Height > 0, "строка решения не показана — мерить нечего");
            Assert.True(
                verdict.Bounds.Y < current.Bounds.Y,
                $"решение ниже версии: решение Y={verdict.Bounds.Y}, версия Y={current.Bounds.Y}");
            Assert.True(
                current.Bounds.Y < github.Bounds.Y,
                $"порядок «установлено / на GitHub» сломан: {current.Bounds.Y} / {github.Bounds.Y}");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕПРОВЕРЕННОЕ СОСТОЯНИЕ НЕ ПОВТОРЯЕТ ОДНУ ФРАЗУ ДВАЖДЫ: «Обновление ещё не проверялось» —
    /// это решение, и строка «когда проверяли» обязана молчать (замечание владельца 09.10.2026
    /// по кадру раздела: две одинаковые строки подряд читаются как сбой).
    /// </summary>
    [AvaloniaFact]
    public void Раздел_не_повторяет_одну_фразу_дважды()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(ok: false));
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var verdict = window.FindControl<TextBlock>("UpdateVerdictText")!;
            var source = window.FindControl<TextBlock>("UpdateSourceText")!;

            Assert.Equal(PanelStrings.UpdateNeverChecked, verdict.Text);
            Assert.False(source.IsVisible, "строка «когда проверяли» повторяет решение: «" + source.Text + "»");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------- ТРИ КНОПКИ РАЗДЕЛА: ЧТО ПРОИСХОДИТ ПО НАЖАТИЮ
    //
    // ⚠️ Здесь закрыт долг, названный в `NEXT.md` и `UNRELEASED.md` §5: после удаления окна
    // обновления (09.10.2026) три действия раздела — «Пропустить эту версию», «Скачать
    // и подготовить» и «Обновить и перезапустить панель» — не нажимала НИ ОДНА проверка: их
    // стерёг удалённый файл окна. Правила были проверены на чистом уровне, а сами нажатия — нет.
    // Путь замены файлов — самый опасный в продукте, и он обязан быть покрыт нажатием.

    /// <summary>
    /// «СКАЧАТЬ И ПОДГОТОВИТЬ» ПО НАЖАТИЮ КНОПКИ: работа доходит до движка, карточка замены
    /// появляется, а сценарий замены НЕ запускается и панель закрыться не просит.
    ///
    /// ⚠️ Проверка идёт НАЖАТИЕМ настоящей кнопки в показанном разделе, и исход дожидается
    /// опросом: нажатие уходит в фоновую задачу (скачивание в сотни мегабайт на нитке интерфейса
    /// заморозило бы окно). Шов <c>PrepareNow</c> — тот же путь, а не второй: обработчик кнопки
    /// зовёт именно его, поэтому «мимо шва» нажатие пройти не может.
    /// </summary>
    [AvaloniaFact]
    public void Подготовка_по_нажатию_кнопки_идёт_через_движок_и_замену_не_запускает()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(answer: Prepared());
            var exits = 0;
            var window = Stand(dir, new FakeUpdate(), install, exitForUpdate: () => exits++);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var prepare = window.FindControl<Button>("UpdatePrepareButton")!;

            Assert.True(prepare.IsEnabled, "готовить есть что, а кнопка недоступна");

            prepare.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            WaitFor(
                () => window.UpdatePrepared is not null,
                "нажатие «Скачать и подготовить» не дошло до готового ответа");

            Assert.Equal(1, install.Prepared);
            Assert.Equal("v2.1.0", install.LastRelease);
            Assert.Equal(0, install.Launched);
            Assert.Equal(0, exits);
            Assert.True(window.UpdatePrepared!.Ok);

            // Карточка замены появляется ТОЛЬКО после подготовки, путь показан МАСКОЙ (имени
            // человека в окне быть не должно — красная линия 7), и кнопка доступна, но не нажата.
            var replace = window.FindControl<Button>("UpdateReplaceButton")!;
            var staged = window.FindControl<TextBlock>("UpdatePreparedText")!.Text ?? string.Empty;

            Assert.True(replace.IsVisible, "карточка замены не показана после подготовки");
            Assert.True(replace.IsEnabled, "замена доступна, а кнопка молчит");
            Assert.Contains("~", staged, StringComparison.Ordinal);
            Assert.DoesNotContain("person", staged, StringComparison.Ordinal);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------- ХОД ПОДГОТОВКИ КОНЧАЕТСЯ ЧИСТО
    //
    // ⚠️ Требование владельца 20.10.2026 (пункт 2 пакета 2.3.1): после успеха И отказа строка
    // хода пуста, полоса прогресса не видна. Прежде последняя строка хода («скачивание: 50 %»)
    // оставалась на экране и после ответа движка — человек читал застывшие проценты как
    // продолжающуюся работу. Мутации названы у самых проверок.

    /// <summary>Строка хода пуста, полоса прогресса скрыта — одно утверждение на оба исхода.</summary>
    private static void AssertProgressCleared(SettingsWindow window, string outcome)
    {
        var line = window.FindControl<TextBlock>("UpdateProgressText")!;
        var bar = window.FindControl<ProgressBar>("UpdateProgressBar")!;

        Assert.Equal(string.Empty, line.Text ?? string.Empty);
        Assert.False(bar.IsVisible, $"после {outcome} полоса прогресса осталась на экране");
    }

    /// <summary>
    /// ПОСЛЕ УСПЕХА СТРОКА ХОДА ПУСТА, ПОЛОСА ПРОГРЕССА СКРЫТА. Идёт ЖИВЫМ путём: нажатие настоящей
    /// кнопки, работа в фоне, готовый ответ — тот же шов, каким идёт человек.
    ///
    /// ⚠️ Мутация «не очищать после успеха» (финализация зовёт очистку только на отказе) падает
    /// ИМЕННО здесь: до ответа движок уже назвал ход, значит строка была на экране.
    /// </summary>
    [AvaloniaFact]
    public void После_успеха_строка_хода_пуста_и_полоса_прогресса_скрыта()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(answer: Prepared());
            var window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.FindControl<Button>("UpdatePrepareButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            WaitFor(
                () => window.UpdatePrepared is not null,
                "нажатие «Скачать и подготовить» не дошло до готового ответа");

            AssertProgressCleared(window, "успешной подготовки");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОСЛЕ ОТКАЗА — ТО ЖЕ САМОЕ: строка хода пуста, полоса прогресса скрыта. Отказ здесь не
    /// выдуман проверкой: движок сам отвечает отказом (у прогона нет права готовить замену), и
    /// ход он называет ДО отказа — на экране успевает побывать и строка, и полоса.
    ///
    /// ⚠️ Мутация «не очищать после отказа» (финализация зовёт очистку только на успехе) падает
    /// ИМЕННО здесь.
    /// </summary>
    [AvaloniaFact]
    public void После_отказа_строка_хода_пуста_и_полоса_прогресса_скрыта()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(allowed: false, answer: Prepared());
            var window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();

            Assert.Null(window.UpdatePrepared);
            Assert.Equal(PanelStrings.UpdateRefusedLocked, window.UpdateStatusLine);
            AssertProgressCleared(window, "отказа подготовки");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ИСКЛЮЧЕНИЕ ФОНОВОЙ ЗАДАЧИ — ТОТ ЖЕ КОНЕЦ ХОДА: строка пуста, полоса скрыта. Прежде здесь
    /// пряталась ТОЛЬКО полоса, и последняя строка хода висела рядом со словами беды — человек
    /// читал застывший этап как «работа ещё идёт». Ветка <c>catch</c> в нажатии — единственный
    /// путь, которым эта беда приходит.
    ///
    /// ⚠️ Мутация «не чистить ход на исключении» (убрать очистку из <c>catch</c>) падает здесь.
    /// </summary>
    [AvaloniaFact]
    public void После_исключения_фоновой_задачи_строка_хода_пуста_и_полоса_прогресса_скрыта()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(
                answer: Prepared(),
                failure: new InvalidOperationException("сеть оборвалась"));
            var window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.FindControl<Button>("UpdatePrepareButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            WaitFor(
                () => window.UpdateStatusLine.Contains("InvalidOperationException", StringComparison.Ordinal),
                "исключение фоновой подготовки не дошло до раздела словами");

            AssertProgressCleared(window, "исключения фоновой задачи");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В ПРОЦЕССЕ — КАК БЫЛО: пока движок называет ход, строка хода и полоса прогресса видны.
    /// Утверждение стережёт другую крайность, чем две проверки выше: очистка, позванная на каждой
    /// перерисовке раздела (а не в конце попытки), стёрла бы ход сразу же — и «видно, что делается»
    /// превратилось бы в молчание.
    /// </summary>
    [AvaloniaFact]
    public void В_процессе_подготовки_строка_хода_и_полоса_прогресса_видны()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            SettingsWindow? window = null;
            string? midLine = null;
            var midBar = false;

            var install = new FakeInstall(answer: Prepared(), afterProgress: () =>
            {
                midLine = window?.FindControl<TextBlock>("UpdateProgressText")?.Text;
                midBar = window?.FindControl<ProgressBar>("UpdateProgressBar")?.IsVisible == true;
            });

            window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();

            Assert.False(
                string.IsNullOrEmpty(midLine),
                "в процессе подготовки строка хода пуста — человек не видит, что делается");
            Assert.True(midBar, "в процессе подготовки полоса прогресса скрыта");

            // И после ответа — чисто: это тот же конец попытки, что и у успеха.
            AssertProgressCleared(window, "подготовки");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------- КАРТОЧКА ЗАМЕНЫ ВИДНА БЕЗ РУЧНОЙ ПРОКРУТКИ
    //
    // ⚠️ Требование владельца 20.10.2026 (пункт 1 пакета 2.3.1; класс — живое обновление
    // 2.2.1 → 2.3.0): после успешной подготовки карточка замены видна БЕЗ РУЧНОЙ ПРОКРУТКИ.
    // Прежде она стояла после «Что нового»: верх 702 при видимой области 706 — человек видел
    // 4 точки рамки и решал, что обновление сломалось. Теперь карточка стоит под рядом кнопок,
    // выше заметок, а панель ещё и прокручивает к ней и ставит на неё фокус.
    //
    // Настоящую видимость подтверждает кадр дизайн-лидера (`_team\231\u0-design.md` §4.4):
    // он меряет то же число — верх карточки против `ScrollViewer.Viewport` — и после правки
    // обязан сказать «ВИДНА БЕЗ ПРОКРУТКИ = ДА». Здесь тот же расчёт повторён проверками.

    /// <summary>Карточка замены — тот самый орган, который появляется после подготовки.</summary>
    private static Border ReplaceCard(SettingsWindow window) =>
        window.FindControl<Border>("UpdateReplaceCard")!;

    /// <summary>Панель раздела «Обновление» — карточки лежат в ней прямыми детьми.</summary>
    private static StackPanel UpdatePanel(SettingsWindow window) =>
        window.FindControl<StackPanel>("UpdateSectionPanel")!;

    /// <summary>Карточка раздела, внутри которой лежит названный орган (Border — ребёнок панели).</summary>
    private static Control CardContaining(SettingsWindow window, Control inside)
    {
        var panel = UpdatePanel(window);
        Control? node = inside;

        while (node is not null && !ReferenceEquals(node.Parent, panel))
        {
            node = node.Parent as Control;
        }

        Assert.NotNull(node);

        return node!;
    }

    /// <summary>Геометрия карточки замены против видимой области прокрутки — число из кадра u0.</summary>
    private static (double Top, double Bottom, double Viewport) ReplaceGeometry(SettingsWindow window)
    {
        var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;
        var card = ReplaceCard(window);
        var top = card.TranslatePoint(new Point(0, 0), scroll) ?? default;

        return (top.Y, top.Y + card.Bounds.Height, scroll.Viewport.Height);
    }

    /// <summary>
    /// Заметки, из-за которых карточка «Что нового» занимает свою постоянную высоту (180): ровно
    /// в этом состоянии дефект владельца и случился — у выпуска были заметки, и карточка замены
    /// уезжала за нижний край. С пустыми заметками карточка короче и дефект «не помещается»
    /// не воспроизводится вовсе.
    /// </summary>
    private static string Notes() =>
        "## Что нового\n\n- Карточка замены видна без прокрутки\n- Строка хода очищается после шага";

    /// <summary>
    /// КАРТОЧКА ЗАМЕНЫ СТОИТ ДО «ЧТО НОВОГО» — перенос, ради которого заведён пункт 1.
    /// Порядок меряется в ДЕРЕВЕ ЭЛЕМЕНТОВ, а не по координатам: координаты зависят от высоты окна
    /// и темы, а порядок — это и есть решение («сразу под ряд кнопок, выше заметок»).
    ///
    /// ⚠️ Мутация «вернуть карточку под заметки» падает ИМЕННО здесь (и следом — проверка
    /// геометрии: карточка снова уезжает за нижний край).
    /// </summary>
    [AvaloniaFact]
    public void Карточка_замены_стоит_до_заметок_о_выпуске()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(), new FakeInstall(answer: Prepared()));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();
            PanelTestStand.Settle();

            Assert.NotNull(window.UpdatePrepared);

            var panel = UpdatePanel(window);
            var card = ReplaceCard(window);
            var notes = CardContaining(window, window.FindControl<ScrollViewer>("UpdateNotesScroll")!);

            Assert.Equal(panel, card.Parent);
            Assert.Equal(panel, notes.Parent);
            Assert.True(
                panel.Children.IndexOf(card) < panel.Children.IndexOf(notes),
                "карточка замены стоит ПОСЛЕ «Что нового» — её снова придётся искать прокруткой");
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОСЛЕ ПОДГОТОВКИ КАРТОЧКА ВИДНА БЕЗ ПРОКРУТКИ — в обычном окне, человек не трогал колесо.
    /// Меряется число: верх карточки и её низ против `ScrollViewer.Viewport`, в исходном положении
    /// прокрутки (сдвиг 0).
    ///
    /// ⚠️ Мутация «вернуть карточку под заметки» краснеет: низ карточки оказывается ниже области.
    /// </summary>
    [AvaloniaFact]
    public void Карточка_замены_видна_без_прокрутки_после_подготовки()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // Заметки — не пустые: именно в этом состоянии дефект владельца и случился.
            var window = Stand(dir, new FakeUpdate(notes: Notes()), new FakeInstall(answer: Prepared()));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();
            PanelTestStand.Settle();

            Assert.NotNull(window.UpdatePrepared);

            // Исходное положение человека: раздел открыт сверху, колесо не трогали.
            var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;
            scroll.Offset = new Vector(0, 0);
            PanelTestStand.Settle();

            Assert.Equal(0, scroll.Offset.Y);

            var (top, bottom, viewport) = ReplaceGeometry(window);

            Assert.True(
                top >= 0 && bottom <= viewport,
                $"карточка замены: верх {top:0.#}, низ {bottom:0.#} при видимой области {viewport:0.#} — без прокрутки не видна");
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В ОКНЕ, ГДЕ ПЕРЕНОСА НЕ ХВАТАЕТ, ПАНЕЛЬ ПРОКРУЧИВАЕТ К КАРТОЧКЕ САМА. Окно настроек тянется
    /// до `MinHeight 420`; проверка ставит его в эту высоту и убеждается, что после подготовки
    /// сдвиг прокрутки ненулевой, а карточка попала в видимую область целиком.
    ///
    /// ⚠️ Мутация «не прокручивать» (убрать `BringIntoView`) краснеет ИМЕННО здесь: в обычном окне
    /// карточка видна и без прокрутки, и эта проверка — единственная, где прокрутка обязательна.
    /// </summary>
    [AvaloniaFact]
    public void В_низком_окне_панель_сама_прокручивает_к_карточке_замены()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(), new FakeInstall(answer: Prepared()));

            // Минимальная высота окна настроек: столько человек может отвести панели.
            window.Height = 420;
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;

            Assert.True(
                scroll.Extent.Height > scroll.Viewport.Height,
                $"прокручивать нечего: Extent={scroll.Extent.Height:0.#}, Viewport={scroll.Viewport.Height:0.#}");

            scroll.Offset = new Vector(0, 0);
            PanelTestStand.Settle();

            window.PrepareNow();
            PanelTestStand.Settle();

            Assert.NotNull(window.UpdatePrepared);

            var (top, bottom, viewport) = ReplaceGeometry(window);

            Assert.True(scroll.Offset.Y > 0, "панель не прокрутила к карточке замены: сдвиг прокрутки 0");
            Assert.True(
                top >= 0 && bottom <= viewport,
                $"после автопрокрутки карточка замены: верх {top:0.#}, низ {bottom:0.#} при видимой области {viewport:0.#}");
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОСЛЕ ПОДГОТОВКИ ФОКУС — НА КАРТОЧКЕ, И КОЛЬЦО ФОКУСА ВИДНО. Это норма банка
    /// (`60-states.md` §7: действие после шага «панель прокручивает к нему и ставит фокус»)
    /// и замер дизайн-лидера: `Focusable` + `Focus()` без видимого кольца кадр НЕ менял,
    /// а это провал WCAG 2.4.7 / F78.
    ///
    /// ⚠️ Кольцо меряется КАДРОМ, а не только свойством: у карточки и без фокуса есть тень, поэтому
    /// «тень есть» ничего не доказывает. Кадр с фокусом обязан отличаться от кадра без фокуса —
    /// иначе человек фокуса не видит.
    ///
    /// ⚠️ Мутации, которые обязаны упасть здесь: «не ставить фокус» · «убрать правило кольца».
    /// </summary>
    [AvaloniaFact]
    public void После_подготовки_фокус_на_карточке_замены_и_кольцо_видно()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(), new FakeInstall(answer: Prepared()));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();
            PanelTestStand.Settle();

            var card = ReplaceCard(window);

            Assert.Same(card, window.FocusManager!.GetFocusedElement());
            Assert.Contains(":focus-visible", card.Classes);

            // КОЛЬЦО НАРИСОВАНО, а не только свойство: полоска у ЛЕВОГО края карточки. Без фокуса
            // там подложка карточки, с фокусом — акцентная тень кольца, поэтому разных цветов
            // в полоске становится больше.
            var at = card.TranslatePoint(new Point(0, 0), window) ?? default;
            var strip = new Rect(at.X + 1, at.Y + 12, 3, 20);
            var ringedColors = PanelTestStand.ColorsIn(window, strip);
            var ringedShadow = card.BoxShadow;
            var height = card.Bounds.Height;

            // Снять фокус — карточка обязана перестать рисовать кольцо: иначе «фокус виден»
            // не доказано, а кадр одинаков с фокусом и без него.
            window.FocusManager!.Focus(null);
            PanelTestStand.Settle();

            Assert.False(card.IsFocused);
            Assert.DoesNotContain(":focus-visible", card.Classes);
            Assert.NotEqual(ringedShadow, card.BoxShadow);

            var plainColors = PanelTestStand.ColorsIn(window, strip);

            Assert.True(
                ringedColors > plainColors,
                $"кольца фокуса на карточке не видно: цветов в полоске {plainColors} без фокуса и {ringedColors} с фокусом");

            // И РАЗМЕР НЕ ПОЕХАЛ: кольцо — тень, раскладку оно не двигает.
            Assert.Equal(height, card.Bounds.Height);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОСЛЕ ОТКАЗА ПОДГОТОВКИ ФОКУС НЕ ТЕРЯЕТСЯ: он возвращается на ПОВТОРЯЕМОЕ действие —
    /// кнопку «Скачать и подготовить», — и виден (`:focus-visible`), а причина сказана словами рядом.
    ///
    /// **От какого случая.** Пока идёт подготовка, кнопка выключена, и фокус с неё снимается;
    /// на успехе его забирает карточка замены, а на отказе карточки нет — фокус уходил В НИКУДА
    /// (тот же класс, что замер 20.10.2026 «фокус = ничего»). Нормы «куда после отказа» в банке
    /// НЕТ: принят вариант А дизайн-лидера (`_team\241\d1-normy.md` §1) — фокус на повторяемое
    /// действие, причина уже показана словами.
    ///
    /// ⚠️ Проверка проходит через состояние, в котором дефект возможен: фокус СНАЧАЛА ставится
    /// на кнопку — как у человека, который её нажал, — а ЗАТЕМ уводится на соседнюю кнопку, пока
    /// работа идёт (кнопка подготовки в это время выключена, и держать на ней фокус нечем). Иначе
    /// «фокус вернулся» нечем опровергнуть: он бы и не уходил.
    ///
    /// ⚠️ Мутации, которые обязаны упасть здесь: «не возвращать фокус на отказе» · «поставить
    /// фокус на кнопку необратимой замены» · «не помечать фокус как видимый» (Focus без способа).
    /// </summary>
    [AvaloniaFact]
    public void После_отказа_подготовки_фокус_возвращается_на_кнопку_подготовки()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            SettingsWindow? window = null;
            Button? prepare = null;
            object? midFocus = null;

            // Человек увёл фокус, пока шла работа: кнопка подготовки выключена, и фокус с неё
            // снимается — ровно то состояние, в котором дефект и случается.
            var install = new FakeInstall(answer: Refused(), afterProgress: () =>
            {
                var check = window?.FindControl<Button>("UpdateCheckButton");

                check?.Focus(NavigationMethod.Pointer);

                midFocus = window?.FocusManager?.GetFocusedElement();
            });

            window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            prepare = window.FindControl<Button>("UpdatePrepareButton")!;

            var check = window.FindControl<Button>("UpdateCheckButton")!;

            Assert.True(prepare.IsEnabled, "повторять нечем: кнопка подготовки выключена ещё до нажатия");

            // Человек нажал кнопку — фокус на ней (щёлкнуть мышью можно и не по центру, поэтому
            // способ получения здесь тот же, каким его даёт указатель).
            prepare.Focus(NavigationMethod.Pointer);
            PanelTestStand.Settle();

            Assert.True(prepare.IsFocused, "фокус не встал на кнопку — «потерять» его было бы нечем");

            window.PrepareNow();
            PanelTestStand.Settle();

            // Состояние «фокус уведён» действительно было — иначе проверка ниже ничего не стоит.
            Assert.Same(check, midFocus);

            // Отказ состоялся и назван словами, а не молчанием.
            Assert.Null(window.UpdatePrepared);
            Assert.Equal(1, install.Prepared);

            var reason = window.FindControl<TextBlock>("UpdateStatusText")!;

            Assert.True(reason.IsVisible, "строка причины отказа не показана");
            Assert.False(
                string.IsNullOrWhiteSpace(reason.Text),
                "причина отказа пуста — человек не узнает, что случилось и что делать");

            // ФОКУС — на повторяемом действии, и он ВИДЕН: способ получения клавиатурный, значит
            // каркас зажигает `:focus-visible` (без него кольца нет — замер 20.10.2026).
            Assert.Same(prepare, window.FocusManager!.GetFocusedElement());
            Assert.Contains(":focus-visible", prepare.Classes);

            // И кнопка замены в фокусе не оказалась: карточки на отказе нет вовсе.
            Assert.NotSame(window.FindControl<Button>("UpdateReplaceButton"), window.FocusManager!.GetFocusedElement());

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ИСКЛЮЧЕНИЕ ФОНОВОЙ ЗАДАЧИ — ТО ЖЕ САМОЕ: причина сказана словами, фокус возвращается
    /// на кнопку подготовки, а не пропадает. Идёт ЖИВЫМ путём: нажатие настоящей кнопки, работа
    /// в фоне, беда в ветке `catch`.
    ///
    /// ⚠️ Мутации, которые обязаны упасть здесь: «не возвращать фокус на отказе» (дверь фокуса
    /// одна на отказ и на исключение) · «поставить фокус на кнопку замены».
    ///
    /// ⚠️ Фокус уводится на соседнюю кнопку, пока идёт фоновая работа: кнопка подготовки в это
    /// время выключена. Кадр фокуса снимается уже на нитке интерфейса (работа идёт в фоне).
    /// </summary>
    [AvaloniaFact]
    public void После_исключения_фоновой_задачи_фокус_возвращается_на_кнопку_подготовки()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            SettingsWindow? window = null;
            object? midFocus = null;

            var install = new FakeInstall(
                answer: Prepared(),
                failure: new InvalidOperationException("диск отвалился"),
                afterProgress: () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    window?.FindControl<Button>("UpdateCheckButton")?.Focus(NavigationMethod.Pointer);

                    midFocus = window?.FocusManager?.GetFocusedElement();
                }));
            window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var prepare = window.FindControl<Button>("UpdatePrepareButton")!;
            var check = window.FindControl<Button>("UpdateCheckButton")!;

            Assert.True(prepare.IsEnabled, "готовить есть что, а кнопка недоступна");

            prepare.Focus(NavigationMethod.Pointer);
            PanelTestStand.Settle();

            Assert.True(prepare.IsFocused, "фокус не встал на кнопку — «потерять» его было бы нечем");

            window.FindControl<Button>("UpdatePrepareButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            WaitFor(
                () => window.UpdateStatusLine.Contains("диск отвалился", StringComparison.Ordinal),
                "исключение фоновой подготовки не дошло до раздела словами");

            PanelTestStand.Settle();

            // Состояние «фокус уведён» действительно было — иначе проверка ниже ничего не стоит.
            Assert.Same(check, midFocus);

            Assert.Null(window.UpdatePrepared);

            var reason = window.FindControl<TextBlock>("UpdateStatusText")!;

            Assert.True(reason.IsVisible, "строка причины исключения не показана");
            Assert.Contains("диск отвалился", reason.Text ?? string.Empty, StringComparison.Ordinal);

            Assert.Same(prepare, window.FocusManager!.GetFocusedElement());
            Assert.Contains(":focus-visible", prepare.Classes);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// КНОПКА НЕОБРАТИМОЙ ЗАМЕНЫ НЕ ОКАЗЫВАЕТСЯ В ФОКУСЕ НИКОГДА — ни после успеха, ни после
    /// отказа. Это отдельное утверждение, потому что соблазн ровно в ней: после шага «видимое
    /// действие» — это в том числе «Обновить и перезапустить панель», а фокус на ней означал бы,
    /// что одно нажатие Enter подменяет панель (`60-states.md` §12(5), вариант А).
    ///
    /// ⚠️ Пустоту проверка не стережёт: на успехе кнопка замены ВИДИМА и ДОСТУПНА — то есть
    /// настоящий кандидат в фокус, — и всё равно фокус стоит на карточке.
    ///
    /// ⚠️ Мутация «поставить фокус на кнопку замены» падает здесь и в проверке отказа.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_необратимой_замены_не_оказывается_в_фокусе_никогда()
    {
        var successDir = PanelTestStand.TempDir();
        var refusedDir = PanelTestStand.TempDir();

        try
        {
            // УСПЕХ: карточка замены появилась, её кнопка видима и доступна — и всё равно не в фокусе.
            var done = Stand(successDir, new FakeUpdate(), new FakeInstall(answer: Prepared()));

            done.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            done.PrepareNow();
            PanelTestStand.Settle();

            Assert.NotNull(done.UpdatePrepared);

            var replace = done.FindControl<Button>("UpdateReplaceButton")!;

            Assert.True(replace.IsVisible, "кнопка замены не показана — утверждение проверяло бы пустоту");
            Assert.True(replace.IsEnabled, "кнопка замены недоступна — в фокус её и не тянуло бы");

            Assert.Same(ReplaceCard(done), done.FocusManager!.GetFocusedElement());
            Assert.NotSame(replace, done.FocusManager!.GetFocusedElement());

            done.CloseQuietly();

            // ОТКАЗ: карточки нет, фокус на повторяемом действии — и снова не на кнопке замены.
            var failed = Stand(refusedDir, new FakeUpdate(), new FakeInstall(answer: Refused()));

            failed.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            failed.PrepareNow();
            PanelTestStand.Settle();

            Assert.Null(failed.UpdatePrepared);
            Assert.Same(
                failed.FindControl<Button>("UpdatePrepareButton"),
                failed.FocusManager!.GetFocusedElement());
            Assert.NotSame(
                failed.FindControl<Button>("UpdateReplaceButton"),
                failed.FocusManager!.GetFocusedElement());

            failed.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(successDir);
            PanelTestStand.RemoveTemp(refusedDir);
        }
    }

    /// <summary>
    /// ЗАМЕНА ЗАПУСКАЕТСЯ ОТДЕЛЬНЫМ НАЖАТИЕМ и только тогда просит панель закрыться: сценарий
    /// обязан дождаться выхода панели, иначе он не сможет заменить занятые файлы.
    ///
    /// ⚠️ Мутация «после подготовки сразу запускаем замену» падает ИМЕННО здесь: до нажатия
    /// запусков ноль, а панель закрыться не просили ни разу.
    /// </summary>
    [AvaloniaFact]
    public void Замена_запускается_отдельным_нажатием_и_просит_панель_закрыться()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(answer: Prepared());
            var exits = 0;
            var window = Stand(dir, new FakeUpdate(), install, exitForUpdate: () => exits++);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();

            Assert.Equal(1, install.Prepared);
            Assert.Equal(0, install.Launched);
            Assert.Equal(0, exits);

            window.FindControl<Button>("UpdateReplaceButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, install.Launched);
            Assert.Equal(1, exits);
            Assert.Equal(string.Empty, window.UpdateStatusLine);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕУДАЧНЫЙ ЗАПУСК ЗАМЕНЫ ВИДЕН СЛОВАМИ, и панель при этом НЕ закрывается: человек обязан
    /// узнать, что обновление не началось, а не остаться без панели и без объяснения.
    /// </summary>
    [AvaloniaFact]
    public void Неудачный_запуск_замены_виден_словами_и_панель_не_закрывается()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(answer: Prepared(), launch: "система не создала процесс");
            var exits = 0;
            var window = Stand(dir, new FakeUpdate(), install, exitForUpdate: () => exits++);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.PrepareNow();

            window.FindControl<Button>("UpdateReplaceButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, install.Launched);
            Assert.Equal(0, exits);

            Assert.Contains("система не создала процесс", window.UpdateStatusLine, StringComparison.Ordinal);
            Assert.Contains(
                "система не создала процесс",
                window.FindControl<TextBlock>("UpdateStatusText")!.Text ?? string.Empty,
                StringComparison.Ordinal);

            // Панель не закрыта, и путь назад остался на месте: вторая попытка возможна.
            Assert.True(window.FindControl<Button>("UpdateReplaceButton")!.IsVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ЗАМЕНУ НЕЛЬЗЯ ЗАПУСТИТЬ БЕЗ ПОДГОТОВКИ: сценария ещё нет, и нажимать нечего. Это не
    /// формальность — «обновить» без подготовленной сборки значило бы заменить папку неизвестно чем.
    /// Проверяется ДВУМЯ дверями: кнопки в этом состоянии не существует для человека (не видна
    /// и недоступна), а щелчок по ней, если бы он как-то случился, ничего не запускает.
    /// </summary>
    [AvaloniaFact]
    public void Без_подготовки_замена_не_запускается()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall();
            var exits = 0;
            var window = Stand(dir, new FakeUpdate(), install, exitForUpdate: () => exits++);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var replace = window.FindControl<Button>("UpdateReplaceButton")!;

            Assert.False(replace.IsVisible, "кнопка замены показана до подготовки");
            Assert.False(replace.IsEnabled, "кнопка замены доступна до подготовки");

            replace.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(0, install.Launched);
            Assert.Equal(0, exits);

            // И тем же ответом отвечает шов: до подготовки запускать нечего.
            Assert.False(window.ReplaceNow());
            Assert.Equal(0, install.Launched);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОДГОТОВКА БЕЗ ПРАВА ОТКАЗЫВАЕТ — и отказывает не только серая кнопка: прогон проверки
    /// права готовить замену не имеет, и движок обязан ответить ОТКАЗОМ, а не сделать работу.
    /// Проверяется через тот же шов, каким идёт нажатие: кнопка недоступна, а позванная работа
    /// называет причину словами словаря и ничего не готовит.
    ///
    /// ⚠️ Мутация «право спрашивать перестали» (движок готовит, когда права нет) падает здесь.
    /// </summary>
    [AvaloniaFact]
    public void Подготовка_без_права_отказывает_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var install = new FakeInstall(allowed: false, answer: Prepared());
            var window = Stand(dir, new FakeUpdate(), install);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            Assert.False(window.FindControl<Button>("UpdatePrepareButton")!.IsEnabled, "кнопка подготовки доступна без права");
            Assert.Equal(
                PanelStrings.UpdateRefusedLocked,
                window.FindControl<TextBlock>("UpdateHintText")!.Text);

            window.PrepareNow();

            Assert.Null(window.UpdatePrepared);
            Assert.Equal(PanelStrings.UpdateRefusedLocked, window.UpdateStatusLine);
            Assert.Equal(1, install.Prepared);
            Assert.Equal(0, install.Launched);

            // Отказ подготовки не открывает карточку замены: заменять нечего.
            Assert.False(window.FindControl<Button>("UpdateReplaceButton")!.IsVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «ПРОПУСТИТЬ ЭТУ ВЕРСИЮ» ПО НАЖАТИЮ КНОПКИ ЗАПОМИНАЕТСЯ И ГОВОРИТ ОБ ЭТОМ. Записать
    /// настройку — единственный способ пообещать молчание о выпуске; не записалось — раздел
    /// говорит словами, а не делает вид.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_пропустить_запоминает_версию_и_говорит_об_этом()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var asked = new List<string>();
            var window = Stand(dir, new FakeUpdate(), skipUpdate: version => { asked.Add(version); return true; });

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var skip = window.FindControl<Button>("UpdateSkipButton")!;

            Assert.True(skip.IsEnabled, "выпуск новее, а пропустить нельзя");

            skip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(new[] { "v2.1.0" }, asked);
            Assert.Equal(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.UpdateSkipDoneFormat,
                    "v2.1.0"),
                window.UpdateStatusLine);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРОПУСК НЕ ЗАПИСАЛСЯ (прогон без права) — РАЗДЕЛ ГОВОРИТ ОБ ЭТОМ: сделать вид, что пропуск
    /// запомнен, значило бы обещать молчание, которого не будет.
    /// </summary>
    [AvaloniaFact]
    public void Пропуск_который_не_записался_сказан_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate(), skipUpdate: _ => false);

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            window.FindControl<Button>("UpdateSkipButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(PanelStrings.UpdateSkipFailed, window.UpdateStatusLine);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
