using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
            string notes = "")
        {
            Current = current;
            Skipped = skipped;
            Allowed = allowed;

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

        public string SourceText => Result.Ok ? "проверено 28.09.2026, 15:00" : "Обновление ещё не проверялось.";

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
        public FakeInstall(bool allowed = true, UpdatePreparation? answer = null, string launch = "")
        {
            Allowed = allowed;
            Answer = answer;
            LaunchError = launch;
        }

        public bool Allowed { get; }

        /// <summary>Что движок ответит на «подготовить». <c>null</c> — отказ «обновляться некуда».</summary>
        public UpdatePreparation? Answer { get; set; }

        public string LaunchError { get; }

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
