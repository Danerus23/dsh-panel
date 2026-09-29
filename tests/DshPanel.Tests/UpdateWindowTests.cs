using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DshPanel.Shell;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО «ОБНОВЛЕНИЕ ПАНЕЛИ» — ВИДИМАЯ ЧАСТЬ ОБНОВЛЕНИЯ: что стоит, что на GitHub, что нового
/// и что с этим делать.
///
/// Четыре обещания, и каждое проверяется прогоном:
///
/// 1. **номер с GitHub не выдаётся за новость** — «доступна версия» окно говорит только тогда,
///    когда выпуск ДЕЙСТВИТЕЛЬНО новее (<see cref="UpdateDecisions.IsNewer"/>);
/// 2. **заметки читаются** — строки, а не простыня (разбор и показ проверяются отдельно,
///    в <c>UpdateNotesTests</c>, а здесь — что окно их действительно показывает);
/// 3. **право спрашивается** — прогон проверки замену файлов не готовит, и кнопка говорит почему;
/// 4. **замена файлов не запускается сама** — подготовка только готовит, а «Обновить
///    и перезапустить панель» это ОТДЕЛЬНАЯ кнопка в отдельной карточке, и панель закрывается
///    только по ней.
///
/// ⚠️ Движок установки и проверка выпусков здесь ПОДСТАВНЫЕ: настоящий движок качает сотни
/// мегабайт и пишет в каталог состояния, а настоящая проверка ходит в сеть. Прогон проверки
/// не имеет права ни на то, ни на другое.
/// </summary>
public class UpdateWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Notes =
        "### Что нового\n\n- Первое\n- Второе\n\nАбзац заметок.\n\n## RU\n\n### Что нового\n\n- Первое\n- Второе\n\nАбзац заметок.";

    // ------------------------------------------------------------------ подстановки

    /// <summary>Состояние проверки выпусков, каким его видит окно — без сети и без настроек вовсе.</summary>
    private sealed class FakeUpdate : IUpdateControl
    {
        public FakeUpdate(
            bool ok = true,
            string latest = "v2.1.0",
            string current = "2.0.0",
            bool skipped = false,
            bool allowed = true,
            string notes = Notes,
            ReleaseAssets? assets = null,
            string published = "2026-09-27T10:00:00Z")
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
                    published,
                    notes,
                    notes,
                    Now)
                {
                    Assets = assets ?? new ReleaseAssets(
                        "https://example.invalid/DshPanel.zip",
                        string.Empty,
                        "https://example.invalid/SHA256SUMS.txt"),
                }
                : UpdateRelease.Failed("HTTP 500", Now);
        }

        public UpdateRelease Result { get; }

        public bool Busy => false;

        public bool Allowed { get; }

        public string Current { get; }

        public bool Newer => Result.Ok && UpdateDecisions.IsNewer(Result.Latest, Current);

        public bool Skipped { get; }

        public string SourceText => Result.Ok ? "проверено 28.09.2026, 12:00" : "Обновление ещё не проверялось.";

        public string StatusText => Result.Ok ? "Доступна версия " + Result.Latest : SourceText;

        public TrayStatusLine TrayLine => new(StatusText, TrayTone.Neutral);

        public int Checks { get; private set; }

        public event Action<string>? Announce { add { } remove { } }

        public event Action? Changed { add { } remove { } }

        public void Check() => Checks++;
    }

    /// <summary>Движок установки, каким его видит окно: ничего не качает и ничего не пишет.</summary>
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

        public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null)
        {
            Prepared++;
            LastRelease = release;

            progress?.Invoke(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 50, 100));

            return Answer ?? UpdatePreparation.Refuse(UpdateRefusal.AlreadyLatest, "выпуск не новее");
        }

        public string Launch(UpdatePreparation preparation)
        {
            Launched++;

            return LaunchError;
        }
    }

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

    /// <summary>Окно с подстановками: живой путь тот же, что у человека, но без сети и без диска.</summary>
    private static UpdateWindow Window(
        IUpdateControl? update,
        IUpdateInstall? install = null,
        Func<string, bool>? skip = null,
        Action? exit = null)
    {
        var window = new UpdateWindow();
        window.Attach(update, install, skip, exit);
        window.Show();

        return window;
    }

    // ------------------------------------------------------------------ решения (чистые)

    /// <summary>
    /// ГЛАВНОЕ ОБЕЩАНИЕ ОКНА: номер с GitHub не выдаётся за новость. Выпуск не новее — окно
    /// говорит «последняя версия», а не «доступна версия», и тон у строки зелёный.
    ///
    /// ⚠️ Мутация «сравнение убрали, показываем любой номер» падает ИМЕННО здесь.
    /// </summary>
    [Fact]
    public void Окно_называет_доступной_только_ту_версию_что_новее()
    {
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "v2.1.0"),
            UpdateWindow.VerdictLine(true, "v2.1.0", "2.0.0", skipped: false));

        Assert.Equal(PanelStrings.UpdateLatest, UpdateWindow.VerdictLine(true, "v2.0.0", "2.0.0", skipped: false));
        Assert.Equal(PanelStrings.UpdateLatest, UpdateWindow.VerdictLine(true, "v1.9.0", "2.0.0", skipped: false));

        // Сравнение — по ЧИСЛАМ, а не по строкам: «2.10.0» новее «2.9.9».
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "2.10.0"),
            UpdateWindow.VerdictLine(true, "2.10.0", "2.9.9", skipped: false));

        Assert.Equal(TrayTone.Warning, UpdateWindow.VerdictTone(true, "v2.1.0", "2.0.0", skipped: false));
        Assert.Equal(TrayTone.Good, UpdateWindow.VerdictTone(true, "v2.0.0", "2.0.0", skipped: false));

        // Данных нет — «ещё не проверяли», а не «доступна пустая версия».
        Assert.Equal(PanelStrings.UpdateNeverChecked, UpdateWindow.VerdictLine(false, string.Empty, "2.0.0", skipped: false));
    }

    /// <summary>
    /// ПРОПУЩЕННАЯ ВЕРСИЯ: решение человека важнее новизны — окно говорит о пропуске, а не о том,
    /// что версия новее (человек уже ответил, и напоминать ему не о чем).
    /// </summary>
    [Fact]
    public void Пропущенная_версия_в_окне_названа_решением_человека()
    {
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, "v2.1.0"),
            UpdateWindow.VerdictLine(true, "v2.1.0", "2.0.0", skipped: true));

        Assert.Equal(TrayTone.Neutral, UpdateWindow.VerdictTone(true, "v2.1.0", "2.0.0", skipped: true));
    }

    /// <summary>
    /// ДАТА ВЫПУСКА НЕ ВЫДУМЫВАЕТСЯ: неразобранная — «неизвестна», а выпуска нет вовсе — строки
    /// о дате нет совсем. Выдуманная дата хуже отсутствующей: она выглядит проверенной.
    /// </summary>
    [Fact]
    public void Дата_выпуска_не_выдумывается()
    {
        Assert.Contains("27.09.2026", UpdateWindow.PublishedLine(true, "2026-09-27T10:00:00Z"), StringComparison.Ordinal);

        Assert.Equal(PanelStrings.UpdatePublishedUnknown, UpdateWindow.PublishedLine(true, "когда-то в сентябре"));
        Assert.Equal(PanelStrings.UpdatePublishedUnknown, UpdateWindow.PublishedLine(true, string.Empty));
        Assert.Equal(string.Empty, UpdateWindow.PublishedLine(false, "2026-09-27T10:00:00Z"));
    }

    /// <summary>Строка «что на GitHub» называет версию, а без данных — «данных нет», не подставляя свою.</summary>
    [Fact]
    public void Строка_про_GitHub_не_подставляет_свою_версию()
    {
        Assert.Contains("v2.1.0", UpdateWindow.GithubLine(true, "v2.1.0"), StringComparison.Ordinal);
        Assert.Equal(PanelStrings.UpdateOnGithubUnknown, UpdateWindow.GithubLine(false, "v2.1.0"));
        Assert.Equal(PanelStrings.UpdateOnGithubUnknown, UpdateWindow.GithubLine(true, string.Empty));
    }

    /// <summary>
    /// ХОД ПОДГОТОВКИ НАЗЫВАЕТ ЭТАП И ПРОЦЕНТЫ, а когда размер файла не назван — говорит
    /// «размер неизвестен»: «0 %» вместо этого показало бы, что загрузка встала.
    /// </summary>
    [Fact]
    public void Ход_подготовки_называет_этап_и_проценты()
    {
        var text = UpdateWindow.ProgressLine(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 42, 100));

        Assert.Contains(UpdateStageLines.Text(UpdateStage.Archive), text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);

        var unknown = UpdateWindow.ProgressLine(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 0, -1));

        Assert.Contains(UpdateStageLines.Text(UpdateStage.Archive), unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("0 %", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРАВО СПРАШИВАЕТСЯ: прогон проверки замену файлов не готовит, и причина названа словами
    /// ядра. Движка нет вовсе (окно без связки) — тоже отказ, а не «нажми и посмотрим».
    ///
    /// ⚠️ Мутация «право спрашивать перестали» падает ИМЕННО здесь.
    /// </summary>
    [Fact]
    public void Без_права_человека_подготовка_отказывает_словами()
    {
        var locked = UpdateWindow.PrepareState(new FakeInstall(allowed: false), ok: true, Ready());

        Assert.False(locked.Ready);
        Assert.Equal(PanelStrings.UpdateRefusedLocked, locked.Hint);

        var none = UpdateWindow.PrepareState(null, ok: true, Ready());

        Assert.False(none.Ready);
        Assert.Equal(PanelStrings.UpdateInstallUnavailable, none.Hint);
    }

    /// <summary>
    /// АДРЕСА ВЛОЖЕНИЙ ПАНЕЛЬ УЗНАЁТ ПРИ ПРОВЕРКЕ: без них кнопка подготовки недоступна
    /// и говорит «проверьте ещё раз» — готовить замену вслепую нельзя.
    /// </summary>
    [Fact]
    public void Без_вложений_кнопка_подготовки_просит_проверить_ещё_раз()
    {
        var state = UpdateWindow.PrepareState(new FakeInstall(), ok: true, ReleaseAssets.None);

        Assert.False(state.Ready);
        Assert.Equal(PanelStrings.UpdateNeedCheckHint, state.Hint);

        var ready = UpdateWindow.PrepareState(new FakeInstall(), ok: true, Ready());

        Assert.True(ready.Ready);
        Assert.Equal(PanelStrings.UpdatePrepareHint, ready.Hint);

        // Выпуска нет вовсе — готовить нечего, и это тот же честный ответ.
        Assert.False(UpdateWindow.PrepareState(new FakeInstall(), ok: false, Ready()).Ready);
    }

    private static ReleaseAssets Ready() => new(
        "https://example.invalid/DshPanel.zip",
        string.Empty,
        "https://example.invalid/SHA256SUMS.txt");

    // ------------------------------------------------------------------ окно

    /// <summary>
    /// КНОПКИ ОБНОВЛЕНИЯ НЕ УЕЗЖАЮТ ЗА КРАЙ ВМЕСТЕ С ЗАМЕТКАМИ — замечание владельца 28.09.2026:
    /// *«функциональные клавиши „Пропустить эту версию“ и другие находятся внизу, и чтобы на них
    /// нажать, придётся промотать в самый низ. Текст с изменениями должен быть в своём внутреннем
    /// окне с прокруткой, а не на всё окно»*.
    ///
    /// Проверяется ГЕОМЕТРИЕЙ, а не свойствами: заметки длинные (в теле выпуска их бывает
    /// на несколько экранов), и раньше они растягивали окно, уводя кнопки вниз. Теперь у заметок
    /// СВОЯ прокрутка, и кнопка «Пропустить эту версию» обязана быть видна СРАЗУ.
    ///
    /// ⚠️ Заметки в проверке нарочно ДЛИННЫЕ: на коротких дефект не воспроизводится вовсе,
    /// и такая проверка зеленела бы всегда.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_видны_при_длинных_заметках_а_заметки_прокручиваются()
    {
        var long_notes = string.Join(
            "\n\n",
            Enumerable.Range(1, 60).Select(index => $"### Раздел {index}\n\n- пункт {index}\n- ещё пункт {index}"));

        var window = Window(new FakeUpdate(notes: long_notes));
        var skip = window.FindControl<Button>("SkipButton")!;

        // Кнопка действий — в видимой части окна, без единого оборота колеса.
        var top = skip.TranslatePoint(new Avalonia.Point(0, 0), window);

        Assert.NotNull(top);
        Assert.True(skip.IsVisible, "кнопка «Пропустить эту версию» не показана");

        var bottom = top!.Value.Y + skip.Bounds.Height;
        var where = $"окно {window.ClientSize.Height:0.#}, кнопка: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

        Assert.True(top.Value.Y >= -0.5, $"кнопка выше видимой области — {where}");
        Assert.True(bottom <= window.ClientSize.Height + 0.5, $"кнопка уехала за нижний край — {where}");

        // А заметки прокручиваются В СВОЁМ поле: орган прокрутки у них есть, и прокручивать есть что.
        var notes = window.FindControl<ScrollViewer>("NotesScroll");

        Assert.NotNull(notes);
        Assert.True(
            notes!.Extent.Height > notes.Viewport.Height,
            $"заметкам нечего прокручивать: Extent={notes.Extent.Height:0.#}, Viewport={notes.Viewport.Height:0.#}");

        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, notes.VerticalScrollBarVisibility);

        // И САМО ПОЛЕ ЗАМЕТОК ОГРАНИЧЕНО ПО ВЫСОТЕ. Это и есть вторая половина обещания: без
        // ограничения карточка растёт вместе с заметками и выдавливает кнопки — то есть дефект
        // возвращается через другую дверь (так и было: `Auto` без ограничения не спасал).
        Assert.True(
            notes.Bounds.Height > 0 && notes.Bounds.Height <= 320,
            $"поле заметок {notes.Bounds.Height:0.#} — заметки снова растягивают окно");

        // И строки заметок действительно нарисованы: иначе «прокрутка» была бы прокруткой пустоты.
        Assert.True(window.NoteTexts.Count > 100, $"строк заметок: {window.NoteTexts.Count}");

        window.Close();
    }

    /// <summary>
    /// А КОГДА ЗАМЕТОК НЕТ, ВИДЕН И ПУТЬ ЗАМЕНЫ: карточка «что подготовлено» с последней кнопкой
    /// стоит ВНЕ прокрутки. Проверяется после настоящей подготовки — раньше карточки нет.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_замены_видна_и_не_прячется_за_прокруткой()
    {
        var window = Window(new FakeUpdate(), new FakeInstall(answer: Prepared()));

        window.PrepareNow();
        PanelTestStand.Settle();

        Assert.True(window.ReplaceShown, "карточка замены не показана");

        var replace = window.FindControl<Button>("ReplaceButton")!;
        var top = replace.TranslatePoint(new Avalonia.Point(0, 0), window);

        Assert.NotNull(top);
        Assert.True(replace.IsEnabled);

        var bottom = top!.Value.Y + replace.Bounds.Height;
        var where = $"окно {window.ClientSize.Height:0.#}, кнопка: сверху {top.Value.Y:0.#}, снизу {bottom:0.#}";

        Assert.True(replace.IsVisible, $"кнопка замены не показана — {where}");
        Assert.True(top.Value.Y >= -0.5, $"кнопка замены выше видимой области — {where}");
        Assert.True(bottom <= window.ClientSize.Height + 0.5, $"кнопка замены уехала за нижний край — {where}");

        window.Close();
    }

    /// <summary>
    /// РЯД ДЕЙСТВИЙ В САМОМ ОКНЕ ТОЖЕ В ОДНУ СТРОКУ — то же требование владельца, что и в разделе
    /// настроек («Кнопки пропустить, проверить и т.д. можно было в 1 ряд поместить»). Меряется
    /// геометрией: на скриншоте владельца три кнопки сложились в две строки, и это выглядело
    /// небрежно.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_действий_в_окне_стоят_в_один_ряд()
    {
        var window = Window(new FakeUpdate(), new FakeInstall());

        var buttons = new[]
        {
            window.FindControl<Button>("CheckNowButton")!,
            window.FindControl<Button>("OpenGithubButton")!,
            window.FindControl<Button>("SkipButton")!,
            window.FindControl<Button>("PrepareButton")!,
        };

        var tops = buttons.Select(button => button.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.Y).ToList();

        Assert.True(
            tops.Max() - tops.Min() <= 0.5,
            $"кнопки разъехались по строкам: Y = {string.Join(", ", tops.Select(y => y.ToString("0.#")))}; " +
            $"ширины: {string.Join(", ", buttons.Select(b => $"{b.Bounds.Width:0.#}"))}; " +
            $"окно {window.ClientSize.Width:0.#}");

        foreach (var button in buttons)
        {
            var right = button.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X + button.Bounds.Width;

            Assert.True(right <= window.ClientSize.Width + 0.5,
                $"кнопка «{button.Content}» кончается на {right:0.#}, а окно — на {window.ClientSize.Width:0.#}");
        }

        window.Close();
    }

    /// <summary>
    /// РЯД ВЛЕЗАЕТ И НА МИНИМАЛЬНОЙ ШИРИНЕ ОКНА. Это не придирка: `MinWidth` — обещание человеку,
    /// что окно можно ужать до этой ширины и всё останется видно. Если ряд шире, то на минимуме
    /// последняя кнопка уедет за край — ровно тот дефект, ради которого ряд и переделывался.
    /// </summary>
    [AvaloniaFact]
    public void Ряд_влезает_на_минимальной_ширине()
    {
        var window = Window(new FakeUpdate(), new FakeInstall());

        window.Width = window.MinWidth;
        PanelTestStand.Settle();

        var prepare = window.FindControl<Button>("PrepareButton")!;
        var right = prepare.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X + prepare.Bounds.Width;

        Assert.True(
            right <= window.ClientSize.Width + 0.5,
            $"на минимальной ширине {window.ClientSize.Width:0.#} кнопка «{prepare.Content}» " +
            $"кончается на {right:0.#} — ряд шире окна");

        window.Close();
    }

    /// <summary>
    /// ОКНО ПОКАЗЫВАЕТ ОБЕ ВЕРСИИ, ДАТУ И РЕШЕНИЕ: «установлено», «на GitHub», «выпущен» и что
    /// с этим делать. Кадры этого окна уезжают в README, и пустых строк в них быть не должно.
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_версии_дату_и_решение()
    {
        var window = Window(new FakeUpdate());

        Assert.Contains("2.0.0", window.CurrentLineText, StringComparison.Ordinal);
        Assert.Contains("v2.1.0", window.GithubLineText, StringComparison.Ordinal);
        Assert.Contains("27.09.2026", window.PublishedLineText, StringComparison.Ordinal);
        Assert.Contains("v2.1.0", window.VerdictLineText, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// ЗАМЕТКИ В ОКНЕ — СТРОКАМИ, А НЕ ПРОСТЫНЁЙ: заголовок, пункты списка и абзац видны
    /// по отдельности, и строка «Что нового» не слита с текстом в один кусок.
    /// </summary>
    [AvaloniaFact]
    public void Заметки_в_окне_идут_строками()
    {
        var window = Window(new FakeUpdate());

        var texts = window.NoteTexts;

        Assert.True(texts.Count > 3, $"строк заметок в окне: {texts.Count} — похоже, простыня");
        Assert.Contains("Что нового", texts);
        Assert.Contains("• Первое", texts);
        Assert.Contains("Абзац заметок.", texts);

        window.Close();
    }

    /// <summary>Заметок нет — окно говорит об этом словами, а не пустым местом.</summary>
    [AvaloniaFact]
    public void Без_заметок_окно_говорит_словами()
    {
        var window = Window(new FakeUpdate(notes: string.Empty));

        Assert.Empty(window.NoteLines);
        Assert.False(window.ReplaceShown);
        Assert.Equal(PanelStrings.UpdateNotesEmpty, window.FindControl<TextBlock>("NotesEmptyText")!.Text);

        window.Close();
    }

    /// <summary>
    /// ПРОГОН ПРОВЕРКИ: кнопка «Скачать и подготовить» недоступна, и подсказка называет причину.
    /// Проверяется НАСТОЯЩЕЙ кнопкой в показанном окне, а не пересказом решения.
    /// </summary>
    [AvaloniaFact]
    public void В_прогоне_проверки_кнопка_подготовки_недоступна()
    {
        var window = Window(new FakeUpdate(), new FakeInstall(allowed: false));
        var prepare = window.FindControl<Button>("PrepareButton")!;

        Assert.False(prepare.IsEnabled);
        Assert.Equal(PanelStrings.UpdateRefusedLocked, window.FindControl<TextBlock>("ActionHintText")!.Text);

        window.Close();
    }

    /// <summary>С правом и с вложениями кнопка доступна: иначе обновиться было бы нечем.</summary>
    [AvaloniaFact]
    public void С_правом_кнопка_подготовки_доступна()
    {
        var window = Window(new FakeUpdate(), new FakeInstall());
        var prepare = window.FindControl<Button>("PrepareButton")!;

        Assert.True(prepare.IsEnabled);
        Assert.Equal(PanelStrings.UpdatePrepareHint, window.FindControl<TextBlock>("ActionHintText")!.Text);

        window.Close();
    }

    /// <summary>
    /// ПОДГОТОВКА НЕ ЗАПУСКАЕТ ЗАМЕНУ. После неё окно показывает, ЧТО подготовлено (папки, сценарий
    /// и журнал), отдельная карточка с кнопкой замены появляется — но сам сценарий НЕ запущен,
    /// и панель не закрыта.
    ///
    /// ⚠️ Мутация «после подготовки сразу запускаем замену» падает ИМЕННО здесь: запусков ноль.
    /// </summary>
    [AvaloniaFact]
    public void Подготовка_показывает_что_готово_и_не_запускает_замену()
    {
        var install = new FakeInstall(answer: Prepared());
        var exits = 0;
        var window = Window(new FakeUpdate(), install, exit: () => exits++);

        window.PrepareNow();

        Assert.Equal(1, install.Prepared);
        Assert.Equal("v2.1.0", install.LastRelease);
        Assert.Equal(0, install.Launched);
        Assert.Equal(0, exits);

        Assert.True(window.ReplaceShown, "карточка «что подготовлено» не показана");
        Assert.True(window.Prepared!.Ok);

        // Показывается ИМЕННО то, что подготовлено, и путь показан МАСКОЙ: имя пользователя
        // в кадр и в окно не выходит (красная линия 7).
        var staged = window.FindControl<TextBlock>("PreparedStagedText")!.Text ?? string.Empty;

        Assert.Contains("2.1.0", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("person", staged, StringComparison.Ordinal);
        Assert.Contains("~", staged, StringComparison.Ordinal);

        // Кнопка замены доступна, но НЕ нажата: её нажимает человек.
        Assert.True(window.FindControl<Button>("ReplaceButton")!.IsEnabled);

        window.Close();
    }

    /// <summary>
    /// ЗАМЕНА ЗАПУСКАЕТСЯ ОТДЕЛЬНЫМ НАЖАТИЕМ и только тогда просит панель закрыться: сценарий
    /// обязан дождаться выхода панели, иначе он не сможет заменить занятые файлы.
    /// </summary>
    [AvaloniaFact]
    public void Замена_запускается_отдельным_нажатием_и_закрывает_панель()
    {
        var install = new FakeInstall(answer: Prepared());
        var exits = 0;
        var window = Window(new FakeUpdate(), install, exit: () => exits++);

        window.PrepareNow();

        Assert.Equal(0, install.Launched);
        Assert.Equal(0, exits);

        Assert.True(window.ReplaceNow());

        Assert.Equal(1, install.Launched);
        Assert.True(exits == 1, "панель не попросили закрыться после запуска сценария");

        window.Close();
    }

    /// <summary>
    /// ЗАМЕНУ НЕЛЬЗЯ ЗАПУСТИТЬ БЕЗ ПОДГОТОВКИ: сценария ещё нет, и нажимать нечего. Это не
    /// формальность — «обновить» без подготовленной сборки значило бы заменить папку неизвестно чем.
    /// </summary>
    [AvaloniaFact]
    public void Без_подготовки_замена_не_запускается()
    {
        var install = new FakeInstall();
        var exits = 0;
        var window = Window(new FakeUpdate(), install, exit: () => exits++);

        Assert.False(window.ReplaceNow());
        Assert.Equal(0, install.Launched);
        Assert.Equal(0, exits);

        window.Close();
    }

    /// <summary>
    /// НЕУДАЧНЫЙ ЗАПУСК ВИДЕН СЛОВАМИ, и панель при этом НЕ закрывается: человек обязан узнать,
    /// что обновление не началось, а не остаться без панели и без объяснения.
    /// </summary>
    [AvaloniaFact]
    public void Неудачный_запуск_замены_виден_словами_и_панель_не_закрывается()
    {
        var install = new FakeInstall(answer: Prepared(), launch: "система не создала процесс");
        var exits = 0;
        var window = Window(new FakeUpdate(), install, exit: () => exits++);

        window.PrepareNow();

        Assert.False(window.ReplaceNow());
        Assert.Equal(1, install.Launched);
        Assert.Equal(0, exits);
        Assert.Contains("система не создала процесс", window.StatusLine, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// ОТКАЗ ПОДГОТОВКИ НАЗЫВАЕТСЯ СЛОВАМИ И ПО-РУССКИ (причину даёт словарь по ключу ядра),
    /// а подпись «Обновление не подготовлено» это подтверждает.
    /// </summary>
    [AvaloniaFact]
    public void Отказ_подготовки_назван_словами()
    {
        var install = new FakeInstall(answer: UpdatePreparation.Refuse(UpdateRefusal.NoSums, "нет файла сумм"));
        var window = Window(new FakeUpdate(), install);

        window.PrepareNow();

        Assert.True(window.RefusalShown);
        Assert.Equal(PanelStrings.UpdateNoSums, window.StatusLine);
        Assert.False(window.ReplaceShown);

        window.Close();
    }

    /// <summary>
    /// «ПРОПУСТИТЬ ЭТУ ВЕРСИЮ» ЗАПОМИНАЕТСЯ И ГОВОРИТ ОБ ЭТОМ. Не записалось (прогон без права) —
    /// окно тоже говорит словами: сделать вид, что пропуск запомнен, значило бы обещать молчание,
    /// которого не будет.
    /// </summary>
    [AvaloniaFact]
    public void Пропуск_версии_запоминается_и_сказан_словами()
    {
        var asked = new List<string>();
        var window = Window(new FakeUpdate(), null, version => { asked.Add(version); return true; });

        window.SkipThisVersion();

        Assert.Equal(new[] { "v2.1.0" }, asked);
        Assert.Contains("v2.1.0", window.StatusLine, StringComparison.Ordinal);

        window.Close();

        var refused = Window(new FakeUpdate(), null, _ => false);
        refused.SkipThisVersion();

        Assert.Equal(PanelStrings.UpdateSkipFailed, refused.StatusLine);

        refused.Close();
    }

    /// <summary>
    /// КНОПКИ ДЕЙСТВУЮТ, А НЕ ТОЛЬКО ВЫГЛЯДЯТ: щелчок по «Проверить сейчас» просит проверку,
    /// щелчок по «Открыть на GitHub» открывает СТРАНИЦУ ВЫПУСКА, и её адрес — не адрес API.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_проверить_и_открыть_делают_своё()
    {
        var update = new FakeUpdate();
        var window = Window(update);

        var opened = new List<string>();
        window.OpenLinkForTests = url => { opened.Add(url); return string.Empty; };

        window.FindControl<Button>("CheckNowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FindControl<Button>("OpenGithubButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, update.Checks);
        Assert.Equal(new[] { ProductLinks.ReleasesPageUrl }, opened);

        // Не открылось — причина уходит СТРОКОЙ в окно, а не модальным окном поверх него.
        window.OpenLinkForTests = _ => "браузера нет";
        window.OpenOnGithub();

        Assert.Equal("браузера нет", window.StatusLine);

        window.Close();
    }

    /// <summary>
    /// АДРЕС СТРАНИЦЫ ВЫПУСКА БЕРЁТСЯ У ПРОДУКТА: своя страница выпуска, если ядро её принесло,
    /// иначе — страница выпусков. Пустого адреса наружу не выходит вовсе.
    /// </summary>
    [Fact]
    public void Адрес_кнопки_ведёт_на_страницу_выпуска()
    {
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateWindow.GithubAddress(string.Empty));
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateWindow.GithubAddress(null));
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateWindow.GithubAddress("   "));
        Assert.Equal(
            "https://github.com/Danerus23/dsh-panel/releases/tag/v2.1.0",
            UpdateWindow.GithubAddress("https://github.com/Danerus23/dsh-panel/releases/tag/v2.1.0"));

        Assert.NotEqual(ProductLinks.ReleasesApiUrl, ProductLinks.ReleasesPageUrl);
    }
}
