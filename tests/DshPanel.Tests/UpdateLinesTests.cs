using System;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ ОБНОВЛЕНИЯ (<see cref="UpdateLines"/>) — что стоит, что на GitHub, что нового
/// и что с этим делать.
///
/// **Откуда этот файл.** Прежде эти решения жили в окне «Обновление панели», и проверки назывались
/// <c>UpdateWindowTests</c>. Решением владельца 09.10.2026 отдельного окна у обновления НЕТ
/// (*«Я не хочу отдельно вызываемое окно»*): весь ответ живёт разделом «Обновление» окна настроек.
/// Функции переехали в ядро обновления не изменившись, и проверки чистых решений переехали с ними;
/// проверки САМОГО ОКНА (показ, кнопки, швы, геометрия) удалены вместе с окном. Видимую часть
/// раздела сторожит <c>SettingsUpdateSectionTests</c>.
///
/// Четыре обещания, и каждое проверяется прогоном:
///
/// 1. **номер с GitHub не выдаётся за новость** — «доступна версия» говорится только тогда,
///    когда выпуск ДЕЙСТВИТЕЛЬНО новее (<see cref="UpdateDecisions.IsNewer"/>);
/// 2. **заметки читаются** — разбор и показ проверяются отдельно, в <c>UpdateNotesTests</c>;
/// 3. **право спрашивается** — прогон проверки замену файлов не готовит, и причина названа словами;
/// 4. **замена файлов не запускается сама** — запуск живёт отдельной кнопкой раздела, и её
///    сторожит <c>SettingsUpdateSectionTests</c> (здесь — только решение «можно ли готовить»).
///
/// ⚠️ Движок установки здесь ПОДСТАВНЫЙ: настоящий качает сотни мегабайт и пишет в каталог
/// состояния, а прогон проверки не имеет права ни на то, ни на другое.
/// </summary>
public class UpdateLinesTests
{
    /// <summary>Движок установки, каким его видит решение: ничего не качает и ничего не пишет.</summary>
    private sealed class FakeInstall : IUpdateInstall
    {
        public FakeInstall(bool allowed = true) => Allowed = allowed;

        public bool Allowed { get; }

        public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null) =>
            UpdatePreparation.Refuse(UpdateRefusal.AlreadyLatest, "выпуск не новее");

        public string Launch(UpdatePreparation preparation) => string.Empty;
    }

    private static ReleaseAssets Ready() => new(
        "https://example.invalid/DshPanel.zip",
        string.Empty,
        "https://example.invalid/SHA256SUMS.txt");

    // ------------------------------------------------------------------ решения (чистые)

    /// <summary>
    /// ГЛАВНОЕ ОБЕЩАНИЕ: номер с GitHub не выдаётся за новость. Выпуск не новее — панель
    /// говорит «у вас последняя версия», а не «доступна версия», и тон у строки зелёный.
    ///
    /// ⚠️ **Слова «обновляться не нужно» — замечание владельца 09.10.2026.** Прежняя строка
    /// «Последняя версия» не отвечала на его вопрос: прочитав экран целиком, он не понял, у него
    /// ли последняя версия и надо ли нажимать «Пропустить эту версию». Теперь ответ называет
    /// версию и говорит, что делать нечего.
    ///
    /// ⚠️ Мутация «сравнение убрали, показываем любой номер» падает ИМЕННО здесь.
    /// </summary>
    [Fact]
    public void Названа_доступной_только_та_версия_что_новее()
    {
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "v2.1.0"),
            UpdateLines.VerdictLine(true, "v2.1.0", "2.0.0", skipped: false));

        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateWindowLatestFormat, "2.0.0"),
            UpdateLines.VerdictLine(true, "v2.0.0", "2.0.0", skipped: false));

        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateWindowLatestFormat, "2.0.0"),
            UpdateLines.VerdictLine(true, "v1.9.0", "2.0.0", skipped: false));

        // Сравнение — по ЧИСЛАМ, а не по строкам: «2.10.0» новее «2.9.9».
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "2.10.0"),
            UpdateLines.VerdictLine(true, "2.10.0", "2.9.9", skipped: false));

        Assert.Equal(TrayTone.Warning, UpdateLines.VerdictTone(true, "v2.1.0", "2.0.0", skipped: false));
        Assert.Equal(TrayTone.Good, UpdateLines.VerdictTone(true, "v2.0.0", "2.0.0", skipped: false));

        // Данных нет — «ещё не проверяли», а не «доступна пустая версия».
        Assert.Equal(PanelStrings.UpdateNeverChecked, UpdateLines.VerdictLine(false, string.Empty, "2.0.0", skipped: false));
    }

    /// <summary>
    /// СТРОКА «КОГДА ПРОВЕРЯЛИ» МОЛЧИТ, КОГДА ПОВТОРЯЕТ РЕШЕНИЕ. Замечание владельца 09.10.2026
    /// по кадру раздела настроек: в непроверенном состоянии «Обновление ещё не проверялось» стояло
    /// ДВАЖДЫ подряд (решение и эта строка) — читается как сбой. Когда проверка была, строка
    /// называет дату и обязана остаться.
    /// </summary>
    [Fact]
    public void Строка_источника_не_повторяет_решение()
    {
        const string verdict = "Обновление ещё не проверялось.";

        Assert.Equal(string.Empty, UpdateLines.SourceLine(verdict, verdict));
        Assert.Equal(string.Empty, UpdateLines.SourceLine("  " + verdict + " ", verdict));
        Assert.Equal(string.Empty, UpdateLines.SourceLine(null, verdict));
        Assert.Equal(string.Empty, UpdateLines.SourceLine(string.Empty, verdict));

        Assert.Equal("проверено 28.09.2026, 12:00", UpdateLines.SourceLine("проверено 28.09.2026, 12:00", verdict));
    }

    /// <summary>
    /// ПОДСКАЗКА КНОПКИ «ПРОПУСТИТЬ» ГОВОРИТ ПРАВДУ В КАЖДОМ ИЗ ТРЁХ СОСТОЯНИЙ: выпуск новее;
    /// выпуск не новее (свой же); выпусков ещё не спрашивали. Одна фраза на все случаи соврала бы
    /// в двух из трёх — и человек либо ждал бы молчания, которого не будет, либо решил бы, что
    /// панель что-то проверила.
    /// </summary>
    [Fact]
    public void Подсказка_пропуска_различает_три_состояния()
    {
        Assert.Equal(PanelStrings.TipUpdateSkipButton, UpdateLines.SkipTip(true, "v2.1.0", newer: true));
        Assert.Equal(PanelStrings.TipUpdateSkipNothingNew, UpdateLines.SkipTip(true, "2.0.0", newer: false));
        Assert.Equal(PanelStrings.TipUpdateSkipNothingToSkip, UpdateLines.SkipTip(false, string.Empty, newer: false));
        Assert.Equal(PanelStrings.TipUpdateSkipNothingToSkip, UpdateLines.SkipTip(true, "   ", newer: false));
    }

    /// <summary>
    /// ПРОПУЩЕННАЯ ВЕРСИЯ: решение человека важнее новизны — панель говорит о пропуске, а не о том,
    /// что версия новее (человек уже ответил, и напоминать ему не о чем).
    /// </summary>
    [Fact]
    public void Пропущенная_версия_названа_решением_человека()
    {
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, "v2.1.0"),
            UpdateLines.VerdictLine(true, "v2.1.0", "2.0.0", skipped: true));

        Assert.Equal(TrayTone.Neutral, UpdateLines.VerdictTone(true, "v2.1.0", "2.0.0", skipped: true));
    }

    /// <summary>
    /// ДАТА ВЫПУСКА НЕ ВЫДУМЫВАЕТСЯ: неразобранная — «неизвестна», а выпуска нет вовсе — строки
    /// о дате нет совсем. Выдуманная дата хуже отсутствующей: она выглядит проверенной.
    /// </summary>
    [Fact]
    public void Дата_выпуска_не_выдумывается()
    {
        Assert.Contains("27.09.2026", UpdateLines.PublishedLine(true, "2026-09-27T10:00:00Z"), StringComparison.Ordinal);

        Assert.Equal(PanelStrings.UpdatePublishedUnknown, UpdateLines.PublishedLine(true, "когда-то в сентябре"));
        Assert.Equal(PanelStrings.UpdatePublishedUnknown, UpdateLines.PublishedLine(true, string.Empty));
        Assert.Equal(string.Empty, UpdateLines.PublishedLine(false, "2026-09-27T10:00:00Z"));
    }

    /// <summary>Строка «что на GitHub» называет версию, а без данных — «данных нет», не подставляя свою.</summary>
    [Fact]
    public void Строка_про_GitHub_не_подставляет_свою_версию()
    {
        Assert.Contains("v2.1.0", UpdateLines.GithubLine(true, "v2.1.0"), StringComparison.Ordinal);
        Assert.Equal(PanelStrings.UpdateOnGithubUnknown, UpdateLines.GithubLine(false, "v2.1.0"));
        Assert.Equal(PanelStrings.UpdateOnGithubUnknown, UpdateLines.GithubLine(true, string.Empty));
    }

    /// <summary>
    /// ХОД ПОДГОТОВКИ НАЗЫВАЕТ ЭТАП И ПРОЦЕНТЫ, а когда размер файла не назван — у СКАЧИВАНИЯ
    /// говорит «размер неизвестен»: «0 %» вместо этого показало бы, что загрузка встала.
    /// </summary>
    [Fact]
    public void Ход_подготовки_называет_этап_и_проценты()
    {
        var text = UpdateLines.ProgressLine(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 42, 100));

        Assert.Contains(UpdateStageLines.Text(UpdateStage.Archive), text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);

        var unknown = UpdateLines.ProgressLine(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 0, -1));

        Assert.Contains(UpdateStageLines.Text(UpdateStage.Archive), unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("0 %", unknown, StringComparison.Ordinal);

        // ФРАЗА ПРО РАЗМЕР ОСТАЁТСЯ У СКАЧИВАНИЯ: там дело именно в размере, который сервер
        // не назвал (слово владельца 20.10.2026: «остаётся только для скачивания»).
        Assert.Contains("размер файла", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    /// У ЭТАПОВ БЕЗ ПРОЦЕНТОВ СЛОВ ПРО РАЗМЕР ФАЙЛА НЕТ — только «этап: …». Размер у сверки
    /// контрольной суммы, распаковки, страховочной копии и записи сценария ни при чём, и прежняя
    /// фраза «размер файла неизвестен» на них была ложью по смыслу (слово владельца 20.10.2026,
    /// пункт 3 пакета 2.3.1).
    ///
    /// ⚠️ Группы этапов названы здесь ЦЕЛИКОМ, а их сумма сверена с числом значений
    /// <see cref="UpdateStage"/>: новый этап в перечислении обязан попасть в одну из групп,
    /// а не проскользнуть мимо правила.
    ///
    /// ⚠️ Мутации, которые обязаны упасть ИМЕННО здесь: **«вернуть старую формулировку на этапах
    /// без процентов»** (строка пошла бы через ключ «размер файла неизвестен») и **«перевернуть
    /// правило выбора»** (`HasPercent` отвечает не про те этапы).
    /// </summary>
    [Fact]
    public void Этапы_без_процентов_не_говорят_про_размер_файла()
    {
        var withoutPercent = new[]
        {
            UpdateStage.Verify, UpdateStage.Unpack, UpdateStage.Backup, UpdateStage.Script,
        };

        var downloads = new[] { UpdateStage.Sums, UpdateStage.Archive, UpdateStage.Setup };

        Assert.Equal(
            Enum.GetValues<UpdateStage>().Length,
            withoutPercent.Length + downloads.Length);

        // У этапов без процентов — «этап: …» и НИ СЛОВА про размер.
        foreach (var stage in withoutPercent)
        {
            var text = UpdateLines.ProgressLine(new UpdateProgress(stage, "update.cmd", 0, -1));

            Assert.False(
                UpdateStageLines.HasPercent(stage),
                $"этап {stage}: процентов у него нет, а правило говорит, что есть");
            Assert.Contains(UpdateStageLines.Text(stage), text, StringComparison.Ordinal);
            Assert.StartsWith(
                PanelStrings.UpdateProgressStageFormat.Split('{')[0].Trim(),
                text,
                StringComparison.Ordinal);
            Assert.DoesNotContain("размер файла", text, StringComparison.Ordinal);
        }

        // У скачивания при неизвестном размере — прежняя честная фраза, и никакого «этап:».
        foreach (var stage in downloads)
        {
            var text = UpdateLines.ProgressLine(new UpdateProgress(stage, "DshPanel.zip", 0, -1));

            Assert.True(
                UpdateStageLines.HasPercent(stage),
                $"этап {stage}: он скачивание, а правило говорит, что процентов нет");
            Assert.Contains("размер файла", text, StringComparison.Ordinal);
            Assert.DoesNotContain("этап:", text, StringComparison.Ordinal);
        }

        // И то самое имя из слова владельца: на «сценарии замены» слов про размер файла нет.
        var script = UpdateLines.ProgressLine(new UpdateProgress(UpdateStage.Script, "update.cmd", 0, -1));

        Assert.Contains(UpdateStageLines.Text(UpdateStage.Script), script, StringComparison.Ordinal);
        Assert.DoesNotContain("размер файла", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРАВО СПРАШИВАЕТСЯ: прогон проверки замену файлов не готовит, и причина названа словами
    /// ядра. Движка нет вовсе (раздел построен без связки) — тоже отказ, а не «нажми и посмотрим».
    ///
    /// ⚠️ Мутация «право спрашивать перестали» падает ИМЕННО здесь.
    /// </summary>
    [Fact]
    public void Без_права_человека_подготовка_отказывает_словами()
    {
        var locked = UpdateLines.PrepareState(new FakeInstall(allowed: false), ok: true, newer: true, Ready());

        Assert.False(locked.Ready);
        Assert.Equal(PanelStrings.UpdateRefusedLocked, locked.Hint);

        var none = UpdateLines.PrepareState(null, ok: true, newer: true, Ready());

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
        var state = UpdateLines.PrepareState(new FakeInstall(), ok: true, newer: true, ReleaseAssets.None);

        Assert.False(state.Ready);
        Assert.Equal(PanelStrings.UpdateNeedCheckNote, state.Hint);

        var ready = UpdateLines.PrepareState(new FakeInstall(), ok: true, newer: true, Ready());

        Assert.True(ready.Ready);
        Assert.Equal(PanelStrings.UpdatePrepareNote, ready.Hint);

        // Выпуска нет вовсе — готовить нечего, и это тот же честный ответ.
        Assert.False(UpdateLines.PrepareState(new FakeInstall(), ok: false, newer: false, Ready()).Ready);
    }

    /// <summary>
    /// ОБНОВЛЯТЬСЯ НЕКУДА — ГОТОВИТЬ НЕЧЕГО (замечание владельца 09.10.2026): до этой правки
    /// кнопка «Скачать и подготовить» была ДОСТУПНА на своём же выпуске, а движок потом отказывал
    /// словами «выпуск не новее». Доступная кнопка, которая ничего не сделает, читается как
    /// «обновиться всё-таки можно».
    ///
    /// ⚠️ Проверка стоит ДО вопроса о вложениях: у своего же выпуска вложения есть, и «проверьте
    /// ещё раз» было бы ответом не на тот вопрос.
    /// </summary>
    [Fact]
    public void На_своём_выпуске_подготовка_говорит_что_обновляться_некуда()
    {
        var state = UpdateLines.PrepareState(new FakeInstall(), ok: true, newer: false, Ready());

        Assert.False(state.Ready);
        Assert.Equal(PanelStrings.UpdateAlreadyLatest, state.Hint);
    }

    /// <summary>
    /// АДРЕС СТРАНИЦЫ ВЫПУСКА БЕРЁТСЯ У ПРОДУКТА: своя страница выпуска, если ядро её принесло,
    /// иначе — страница выпусков. Пустого адреса наружу не выходит вовсе.
    /// </summary>
    [Fact]
    public void Адрес_кнопки_ведёт_на_страницу_выпуска()
    {
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateLines.GithubAddress(string.Empty));
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateLines.GithubAddress(null));
        Assert.Equal(ProductLinks.ReleasesPageUrl, UpdateLines.GithubAddress("   "));
        Assert.Equal(
            "https://github.com/Danerus23/dsh-panel/releases/tag/v2.1.0",
            UpdateLines.GithubAddress("https://github.com/Danerus23/dsh-panel/releases/tag/v2.1.0"));

        Assert.NotEqual(ProductLinks.ReleasesApiUrl, ProductLinks.ReleasesPageUrl);
    }
}
