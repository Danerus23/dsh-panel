using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Update;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «ОБНОВЛЕНИЕ ПАНЕЛИ» — что стоит сейчас, что лежит на GitHub, что нового и что с этим
/// делать.
///
/// **Четыре обещания, и каждое проверяется прогоном, а не словами.**
///
/// 1. **Версии СРАВНИВАЮТСЯ.** Окно не повторяет номер с GitHub как новость: «доступна версия»
///    оно говорит только тогда, когда выпуск ДЕЙСТВИТЕЛЬНО новее (решение принимает ядро —
///    <see cref="UpdateDecisions.IsNewer"/>, единственная реализация сравнения в панели).
/// 2. **Заметки читаются.** Строки заметок к выпуску разбираются на заголовки, пункты списка
///    и абзацы (<see cref="UpdateNotes"/>) и рисуются по одной (<see cref="UpdateNotesView"/>) —
///    не простынёй: на «сплошной неструктурированный текст» владелец жаловался дважды
///    (`docs\DESIGN.md`, пункты 32–33).
/// 3. **Замена файлов не запускается сама.** «Скачать и подготовить» только готовит: скачивает,
///    сверяет сумму, распаковывает, сверяет версию и снимает страховочную копию. Кнопка
///    «Обновить и перезапустить панель» живёт в ОТДЕЛЬНОЙ карточке и появляется только после
///    удачной подготовки: это единственная дверь продукта, подменяющая саму панель.
/// 4. **Отказ называется словами.** Причина приходит ключом от ядра
///    (<see cref="UpdateRefusals.Key"/>) и превращается в фразу словарём
///    (<see cref="UpdateRefusalLines"/>); молчания в окне нет ни на одном пути.
///
/// ⚠️ **Право готовить замену — не решение окна.** Окно только ПОКАЗЫВАЕТ, можно ли нажимать
/// (<see cref="PrepareState"/>); сам движок отказывает без права, и отказать он обязан
/// независимо от того, что показало окно.
///
/// ⚠️ **Ни одного текста в разметке**: все подписи ставятся здесь из <see cref="PanelStrings"/>,
/// а тот берёт их из словаря трёх языков (`docs\LOCALIZATION.md` §1).
/// </summary>
public partial class UpdateWindow : Window
{
    private IUpdateControl? _update;
    private IUpdateInstall? _install;
    private Func<string, bool>? _skip;
    private Action? _exitRequested;
    private UpdatePreparation? _prepared;
    private bool _busy;
    private string _status = string.Empty;
    private bool _refusalShown;
    private IReadOnlyList<UpdateNoteLine> _notes = Array.Empty<UpdateNoteLine>();

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c> (та же грабля, что у главного окна, настроек, копий и пиков).
    /// </summary>
    public UpdateWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        Title = PanelStrings.UpdateWindowTitle;
        HeadingText.Text = PanelStrings.UpdateWindowHeading;
        SubtitleText.Text = PanelStrings.UpdateWindowSubtitle;

        CheckNowButton.Content = PanelStrings.UpdateCheckNowButton;
        PanelToolTip.Set(CheckNowButton, PanelStrings.TipUpdateCheckNowButton);
        CheckNowButton.Click += (_, _) => _update?.Check();

        // Подпись та же, что у окна «О программе» и «Пики и тарифы»: дверь закрытия одна на панель,
        // и второй набор слов для неё разошёлся бы с ней на первой правке.
        CloseButton.Content = PanelStrings.AboutCloseButton;
        PanelToolTip.Set(CloseButton, PanelStrings.TipCloseAboutButton);
        CloseButton.Click += (_, _) => Close();

        NotesTitleText.Text = PanelStrings.UpdateNotesTitle;
        NotesEmptyText.Text = PanelStrings.UpdateNotesEmpty;

        OpenGithubButton.Content = PanelStrings.UpdateOpenGithubButton;
        PanelToolTip.Set(OpenGithubButton, PanelStrings.TipUpdateOpenGithubButton);
        OpenGithubButton.Click += (_, _) => OpenOnGithub();

        SkipButton.Content = PanelStrings.UpdateSkipButton;
        PanelToolTip.Set(SkipButton, PanelStrings.TipUpdateSkipButton);
        SkipButton.Click += (_, _) => SkipThisVersion();

        PrepareButton.Content = PanelStrings.UpdatePrepareButton;
        PanelToolTip.Set(PrepareButton, PanelStrings.TipUpdatePrepareButton);
        PrepareButton.Click += (_, _) => PreparePressed();

        ReplaceButton.Content = PanelStrings.UpdateReplaceButton;
        PanelToolTip.Set(ReplaceButton, PanelStrings.TipUpdateReplaceButton);
        ReplaceButton.Click += (_, _) => ReplaceNow();

        ReplaceHintText.Text = PanelStrings.UpdateReplaceHint;
        RefusalTitleText.Text = PanelStrings.UpdateRefusalTitle;

        // Окно, построенное без связки, обязано показывать себя целиком: так его видят проверки,
        // и так его мог бы увидеть человек, если связка ещё не успела подойти. Пустые строки
        // честнее выдуманных: «панель ещё не знает» — это ответ.
        Render();
    }

    /// <summary>
    /// Связать окно с проверкой выпусков и движком установки.
    ///
    /// <paramref name="update"/> — откуда окно берёт состояние (может не быть вовсе: окно
    /// показывается и без связки). <paramref name="install"/> — тот, кто готовит замену файлов;
    /// без него кнопка подготовки недоступна и говорит об этом словами.
    /// <paramref name="skip"/> — «запомнить пропущенную версию» (владелец настроек); возвращает
    /// <c>false</c>, если записать не удалось, и тогда окно говорит об этом, а не делает вид,
    /// что пропуск запомнен.
    /// <paramref name="exitRequested"/> — закрыть панель ПОСЛЕ запуска сценария замены. Это
    /// единственный случай, когда окно просит панель завершиться, и зовётся он ровно по щелчку
    /// человека.
    /// </summary>
    public void Attach(
        IUpdateControl? update,
        IUpdateInstall? install = null,
        Func<string, bool>? skip = null,
        Action? exitRequested = null)
    {
        if (_update is not null) _update.Changed -= OnChanged;

        _update = update;
        if (_update is not null) _update.Changed += OnChanged;

        _install = install;
        _skip = skip;
        _exitRequested = exitRequested;

        Render();
    }

    // ------------------------------------------------------------------ решения (чистые)

    /// <summary>Что стоит сейчас: «Установлено: 2.0.0». Номер берётся у сборки, а не у настроек.</summary>
    public static string CurrentLine(string? current) =>
        string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateCurrentFormat, (current ?? string.Empty).Trim());

    /// <summary>
    /// Что лежит на GitHub. Данных нет — панель говорит «данных нет», а не подставляет свою
    /// версию: подстановка выглядела бы как «на GitHub то же самое», то есть как проверка,
    /// которой не было.
    /// </summary>
    public static string GithubLine(bool ok, string? latest)
    {
        var version = (latest ?? string.Empty).Trim();

        return ok && version.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateOnGithubFormat, version)
            : PanelStrings.UpdateOnGithubUnknown;
    }

    /// <summary>
    /// Дата выпуска. Разобрать её не удалось — панель говорит «неизвестна» и НЕ выдумывает дату:
    /// выдуманная дата хуже отсутствующей, потому что выглядит проверенной. Выпуска нет вовсе —
    /// строки о дате нет совсем.
    /// </summary>
    public static string PublishedLine(bool ok, string? published)
    {
        if (!ok) return string.Empty;

        if (!PricingDecisions.TryParse(published, out var at)) return PanelStrings.UpdatePublishedUnknown;

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.UpdatePublishedFormat,
            at.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// ЧТО СКАЗАТЬ ПРО ВЕРСИИ. **Сравнение — здесь и только здесь** (через ядро
    /// <see cref="UpdateDecisions.IsNewer"/>): окно, показывающее любой номер с GitHub как
    /// «доступна версия», сказало бы человеку неправду на выпуске, который не новее.
    ///
    /// Порядок ответов: данных нет → выпуска нет; человек пропустил эту версию → говорим о его
    /// решении (оно важнее новизны); выпуск новее → «доступна версия»; иначе → «последняя версия».
    /// </summary>
    public static string VerdictLine(bool ok, string? latest, string? current, bool skipped)
    {
        var version = (latest ?? string.Empty).Trim();
        if (!ok || version.Length == 0) return PanelStrings.UpdateNeverChecked;

        if (skipped) return string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, version);

        return UpdateDecisions.IsNewer(version, current)
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, version)
            : PanelStrings.UpdateLatest;
    }

    /// <summary>
    /// Тон строки решения — тот же язык, что у строк состояния в меню значка: новая версия —
    /// «внимание» (не сломано, но есть о чём подумать), пропуск и «данных нет» — серый
    /// (решение человека и неизвестность, а не беда), «последняя версия» — зелёный.
    /// </summary>
    public static TrayTone VerdictTone(bool ok, string? latest, string? current, bool skipped)
    {
        var version = (latest ?? string.Empty).Trim();

        if (!ok || version.Length == 0) return TrayTone.Neutral;
        if (skipped) return TrayTone.Neutral;

        return UpdateDecisions.IsNewer(version, current) ? TrayTone.Warning : TrayTone.Good;
    }

    /// <summary>
    /// МОЖНО ЛИ ГОТОВИТЬ ЗАМЕНУ и что об этом сказать человеку. Три отказа, и каждый назван
    /// своей причиной: движка нет вовсе (прогон, в котором окно построено без связки), права нет
    /// (прогон проверки — <see cref="UpdateInstall.For"/>), или панель ещё не знает адресов
    /// вложений (их узнают при проверке).
    ///
    /// ⚠️ Отдельной чистой функцией, а не условием в разметке: это ровно та проверка, которую
    /// обязана ловить мутация «право спрашивать перестали».
    /// </summary>
    public static (bool Ready, string Hint) PrepareState(IUpdateInstall? install, bool ok, ReleaseAssets assets)
    {
        if (install is null) return (false, PanelStrings.UpdateInstallUnavailable);
        if (!install.Allowed) return (false, PanelStrings.UpdateRefusedLocked);

        return ok && assets.HasArchive && assets.HasSums
            ? (true, PanelStrings.UpdatePrepareHint)
            : (false, PanelStrings.UpdateNeedCheckHint);
    }

    /// <summary>
    /// Ход подготовки словами: этап и проценты. Размер файла сервер не назвал — процентов нет,
    /// и «0 %» вместо честного «размер неизвестен» показало бы, что загрузка встала.
    /// </summary>
    public static string ProgressLine(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stage = UpdateStageLines.Text(progress.Stage);
        var percent = progress.Percent;

        return percent < 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateProgressUnknownFormat, stage)
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateProgressFormat, stage, percent);
    }

    /// <summary>
    /// Адрес страницы выпуска для кнопки «Открыть на GitHub»: своя страница выпуска, если ядро
    /// её принесло, иначе — страница выпусков продукта (<see cref="ProductLinks.ReleasesPageUrl"/>).
    /// Пустого адреса наружу не выходит: кнопки, ведущей в никуда, в панели не бывает.
    /// </summary>
    public static string GithubAddress(string? pageUrl) =>
        ProductLinks.ShouldShow(pageUrl) ? pageUrl!.Trim() : ProductLinks.ReleasesPageUrl;

    // ------------------------------------------------------------------ показ

    /// <summary>
    /// Пересчитать и показать всё, что видно. Ничего не читает, кроме готового состояния: заметки
    /// разбираются здесь из УЖЕ выбранного блока на языке панели (перевыбор — дело контроллера).
    /// </summary>
    public void Render()
    {
        var update = _update;
        var result = update?.Result;
        var ok = result?.Ok == true;

        var current = update?.Current ?? AboutWindow.PanelVersion;
        var latest = result?.Latest ?? string.Empty;
        var skipped = update?.Skipped == true;

        CurrentText.Text = CurrentLine(current);
        GithubText.Text = GithubLine(ok, latest);
        PublishedText.Text = PublishedLine(ok, result?.Published);
        PublishedText.IsVisible = !string.IsNullOrEmpty(PublishedText.Text);

        VerdictText.Text = VerdictLine(ok, latest, current, skipped);
        PanelLook.Tone(VerdictText, VerdictTone(ok, latest, current, skipped));

        SourceText.Text = update?.SourceText ?? PanelStrings.UpdateNeverChecked;

        // ЗАМЕТКИ: разбор — из текста выпуска, показ — по одной строке. Пустые заметки говорят
        // о себе словами; пустое место человек прочитал бы как «окно не догрузилось».
        _notes = UpdateNotes.Parse(result?.Notes);
        UpdateNotesView.Fill(NotesPanel, _notes);
        NotesEmptyText.IsVisible = _notes.Count == 0;
        NotesPanel.IsVisible = _notes.Count > 0;

        var assets = result?.Assets ?? ReleaseAssets.None;
        var (ready, hint) = PrepareState(_install, ok, assets);

        ActionHintText.Text = hint;
        PrepareButton.Content = _busy ? PanelStrings.UpdatePreparingButton : PanelStrings.UpdatePrepareButton;

        // Подготовленная сборка уже лежит на диске: второй раз готовить нечего, и кнопка об этом
        // молчит делом — она недоступна. Путь дальше называется карточкой ниже.
        PrepareButton.IsEnabled = ready && !_busy && _prepared is null;

        CheckNowButton.IsEnabled = update?.Allowed == true && !_busy;
        PanelToolTip.Set(
            CheckNowButton,
            update?.Allowed == true ? PanelStrings.TipUpdateCheckNowButton : PanelStrings.UpdateRefusedLocked);

        SkipButton.IsEnabled = ok && latest.Trim().Length > 0 && !_busy;
        OpenGithubButton.IsEnabled = GithubAddress(result?.PageUrl).Length > 0;

        RefusalTitleText.IsVisible = _refusalShown;
        StatusText.Text = _status;

        ShowPreparation(_prepared);
    }

    /// <summary>Строки заметок, которые сейчас на экране, — для проверок без экрана.</summary>
    public IReadOnlyList<UpdateNoteLine> NoteLines => _notes;

    /// <summary>Строки заметок, НАРИСОВАННЫЕ в панели (без пустых: у них нет текста).</summary>
    public IReadOnlyList<string> NoteTexts =>
        NotesPanel.Children.OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToList();

    /// <summary>Строка «что стоит сейчас» на экране.</summary>
    public string CurrentLineText => CurrentText.Text ?? string.Empty;

    /// <summary>Строка «что на GitHub» на экране.</summary>
    public string GithubLineText => GithubText.Text ?? string.Empty;

    /// <summary>Дата выпуска на экране (пусто, когда выпуска нет вовсе).</summary>
    public string PublishedLineText => PublishedText.Text ?? string.Empty;

    /// <summary>Решение о версиях на экране.</summary>
    public string VerdictLineText => VerdictText.Text ?? string.Empty;

    /// <summary>Строка состояния на экране: причина отказа, ход подготовки или ответ на щелчок.</summary>
    public string StatusLine => StatusText.Text ?? string.Empty;

    /// <summary>Видна ли подпись «Обновление не подготовлено».</summary>
    public bool RefusalShown => RefusalTitleText.IsVisible;

    /// <summary>Видна ли карточка «что подготовлено» с отдельной кнопкой замены.</summary>
    public bool ReplaceShown => ReplacePanel.IsVisible;

    /// <summary>Что подготовлено (пусто, пока ничего не подготовлено).</summary>
    public UpdatePreparation? Prepared => _prepared;

    // ------------------------------------------------------------------ действия

    /// <summary>
    /// Открыть страницу выпуска в браузере ЧЕЛОВЕКА. Ссылку открывает <see cref="ProductLinks.Open"/>
    /// и возвращает текст ошибки; окно показывает её СТРОКОЙ у себя, а не модальным окном поверх
    /// себя — владелец такого не любит. Сама съёмка (`--shot`) ничего не нажимает.
    ///
    /// ⚠️ В задаче эта дверь названа `Shell\PanelBrowser`; в коде она называется
    /// <see cref="AgentBrowser"/> и <see cref="ProductLinks"/> — двух дверей в браузер в панели
    /// нет, и эта пользуется той же, что окно «О программе».
    /// </summary>
    public void OpenOnGithub()
    {
        var url = GithubAddress(_update?.Result.PageUrl);
        if (url.Length == 0) return;

        var error = OpenLinkForTests is not null ? OpenLinkForTests(url) : ProductLinks.Open(url);

        SetStatus(error, refusal: false);
    }

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем отвечать на «открой ссылку». В жизни ответ даёт
    /// <see cref="ProductLinks.Open"/> — он же и открывает браузер; проверка показать браузер
    /// не имеет права (это окно на рабочем столе владельца), а путь «открыть не вышло → причина
    /// строкой в окне» обязан быть проверен. У обычного запуска это свойство <c>null</c>.
    /// </summary>
    internal Func<string, string>? OpenLinkForTests { get; set; }

    /// <summary>
    /// ПРОПУСТИТЬ ЭТУ ВЕРСИЮ — ответ человека, и он запоминается настройками. Записать не удалось
    /// (прогон без права) — окно говорит об этом: сделать вид, что пропуск запомнен, значило бы
    /// обещать молчание, которого не будет.
    /// </summary>
    public void SkipThisVersion()
    {
        var version = (_update?.Result.Latest ?? string.Empty).Trim();
        if (version.Length == 0) return;

        var saved = _skip?.Invoke(version) ?? false;

        SetStatus(
            saved
                ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateSkipDoneFormat, version)
                : PanelStrings.UpdateSkipFailed,
            refusal: false);
    }

    private void PreparePressed()
    {
        if (_update is null || _install is null) return;

        _busy = true;
        _prepared = null;
        SetStatus(string.Empty, refusal: false);
        Render();

        Task.Run(() =>
        {
            try
            {
                PrepareNow();
            }
            catch (Exception error)
            {
                OnUi(() => SetStatus($"{error.GetType().Name}: {error.Message}", refusal: true));
            }
            finally
            {
                OnUi(() =>
                {
                    _busy = false;
                    Render();
                });
            }
        });
    }

    /// <summary>
    /// ПРОГНАТЬ ПОДГОТОВКУ СЕЙЧАС, в этой же нитке, — шов для проверок без экрана. Кнопка зовёт
    /// то же самое, только в фоне (скачивание в сотни мегабайт на нитке интерфейса заморозило бы
    /// окно), поэтому «что видно после подготовки» проверяется прогоном того же кода, а не
    /// другого.
    /// </summary>
    internal void PrepareNow()
    {
        var update = _update;
        var install = _install;
        if (update is null || install is null) return;

        var release = update.Result;

        // Ход подготовки начинается ни с чего: первый же этап сам скажет, что происходит.
        // Строку «подготовка началась» пишет в ЖУРНАЛ движок установки — окну она не нужна.
        OnUi(() => SetStatus(string.Empty, refusal: false));

        var preparation = install.Prepare(release.Latest, release.Assets, Report);

        OnUi(() =>
        {
            _prepared = preparation.Ok ? preparation : null;

            SetStatus(UpdateRefusalLines.Line(preparation), refusal: !preparation.Ok);

            // Показ пересобирается ЦЕЛИКОМ: после подготовки меняется не только строка состояния,
            // но и карточка «что подготовлено» с отдельной кнопкой замены.
            Render();
        });
    }

    /// <summary>
    /// ЗАПУСТИТЬ ЗАМЕНУ И ЗАКРЫТЬ ПАНЕЛЬ — только по щелчку человека. Возвращает <c>true</c>,
    /// если сценарий запущен (и панель после этого завершается).
    ///
    /// ⚠️ Замена файлов не запускается НИКОГДА сама: ни после подготовки, ни при закрытии окна.
    /// Пока человек не нажал эту кнопку, на диске лежит лишь подготовленная сборка.
    /// </summary>
    public bool ReplaceNow()
    {
        var install = _install;
        var preparation = _prepared;

        if (install is null || preparation is null) return false;

        var error = install.Launch(preparation);

        if (error.Length > 0)
        {
            // Запуск не удался — это видно СЛОВАМИ, и панель остаётся работать: человек обязан
            // знать, что обновление не началось, а не догадываться об этом по молчанию.
            SetStatus(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateLaunchFailedFormat, error),
                refusal: true);

            return false;
        }

        SetStatus(string.Empty, refusal: false);
        _exitRequested?.Invoke();

        return true;
    }

    /// <summary>Ход подготовки: этап и проценты. Приходит из фоновой задачи — показ идёт на нитке интерфейса.</summary>
    private void Report(UpdateProgress progress) => OnUi(() =>
    {
        ProgressBar.IsVisible = true;
        ProgressText.Text = ProgressLine(progress);

        if (progress.Percent < 0)
        {
            ProgressBar.IsIndeterminate = true;
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = progress.Percent;
        }
    });

    /// <summary>
    /// ЧТО ПОДГОТОВЛЕНО. Показывается по факту, а не по обещанию: папка сборки, страховочная
    /// копия, сценарий и журнал. Пути идут через <see cref="DisplayMask"/>: каталог панели лежит
    /// в профиле человека, и имя пользователя в окне — личные данные (красная линия 7).
    /// </summary>
    private void ShowPreparation(UpdatePreparation? preparation)
    {
        ReplacePanel.IsVisible = preparation is { Ok: true };

        if (preparation is not { Ok: true })
        {
            ReplaceButton.IsEnabled = false;
            return;
        }

        PreparedTitleText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdatePreparedTitleFormat, preparation.Version);

        PreparedStagedText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdatePreparedStagedFormat, DisplayMask.Path(preparation.StagedFolder));

        PreparedBackupText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdatePreparedBackupFormat, DisplayMask.Path(preparation.BackupFolder));

        PreparedScriptText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdatePreparedScriptFormat, DisplayMask.Path(preparation.ScriptPath));

        PreparedLogText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdatePreparedLogFormat, DisplayMask.Path(preparation.LogPath));

        ReplaceButton.IsEnabled = true;
    }

    /// <summary>Строка состояния и признак «это причина отказа»: подпись над ней другая.</summary>
    private void SetStatus(string text, bool refusal)
    {
        _status = text;
        _refusalShown = refusal;

        RefusalTitleText.IsVisible = refusal;
        StatusText.Text = text;
    }

    /// <summary>
    /// Показать что-то на нитке интерфейса. Фоновая подготовка зовёт это из чужой нитки, а сам
    /// показ — дело интерфейса; в проверках (в том числе безоконных) нитка уже та, и вызов идёт
    /// напрямую — тот же приём, что у применения темы (<c>Settings\ThemeApply</c>).
    /// </summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void OnChanged() => Render();
}
