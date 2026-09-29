using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DshPanel.Backup;
using DshPanel.Restore;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «КОПИИ» — то, что видит человек: папка копий, список готовых копий, кнопка «Создать копию
/// сейчас» и план наката выбранной копии. Решение владельца 26.09.2026: **отдельное окно**, как
/// было в v1 (там копии и накат жили в отдельных формах), а дверь в него — кнопка в главном окне.
///
/// Три правила, по которым это окно устроено, и каждое выросло из случая:
///
/// 1. **Окно ничего не решает само.** Что можно, что нельзя и куда что ляжет — знает
///    <see cref="IBackupControl"/> и движки под ним; здесь только раскладка строк и нажатия.
///    Поэтому поведение проверяется без экрана (тесты модели окна и кадра), а не глазами.
/// 2. **Работа идёт в ФОНЕ.** Полная копия — это сотни мегабайт и минуты; синхронный вызов
///    заморозил бы окно, и человек решил бы, что панель повисла. Пока идёт копия или накат,
///    кнопки заблокированы, а строка состояния говорит, чего ждём (тот же приём, что в главном окне).
/// 3. **Ничего не меняется, пока человек не нажал.** Выбор копии в списке только ЧИТАЕТ её
///    (<see cref="RestoreEngine.Plan"/>): человек должен видеть, что именно он собирается
///    наложить, — правило v1, оплаченное накатом вслепую.
///
/// ⚠️ **Чего здесь нет:** галочки «гасить сервер перед копией». Это не настройка, а решение
/// каждый раз: панель спрашивает отдельным окном (<see cref="BackupStopWindow"/>), потому что
/// сессии пишет живой процесс, и «копия на ходу» — допущение, а не режим работы.
/// </summary>
public partial class BackupWindow : Window
{
    private IBackupControl? _backups;
    private IServerControl? _server;
    private RestorePlan? _plan;
    private bool _busy;

    /// <summary>
    /// Что сейчас показано в таблице копий. Нужно, чтобы не пересобирать её зря: пересборка теряет
    /// выбор, а он будит <see cref="ShowPlan"/>, который снова зовёт <see cref="Render"/>.
    /// </summary>
    private string[] _listedSignatures = Array.Empty<string>();

    /// <summary>
    /// Номер выбранной строки таблицы, <c>-1</c> — не выбрано. Живёт ЗДЕСЬ, а не в таблице: у окна
    /// и таблицы не должно быть двух правд о том, что выбрано (см. <see cref="BackupTableView.Fill"/>).
    /// </summary>
    private int _selectedIndex = -1;

    /// <summary>
    /// Галочку состава копии ставит и код (при показе состояния), и человек. Без этого
    /// предохранителя показ состояния сам поднимал бы событие и панель записывала бы в настройки
    /// то, что только что прочитала, — та же грабля, что была у автозапуска (см.
    /// <c>SettingsWindow.OnAutostartToggled</c>).
    /// </summary>
    private bool _updatingKeys;

    /// <summary>
    /// Тот же предохранитель для галочки «копия для передачи»: показ состояния окна не должен
    /// выглядеть как действие человека (иначе окно приняло бы свой же показ за щелчок).
    /// </summary>
    private bool _updatingShareable;

    /// <summary>
    /// «ЭТА КОПИЯ — ДЛЯ ПЕРЕДАЧИ»: РАЗОВОЕ состояние ОКНА, а не настройка. Решение владельца
    /// 27.09.2026 (п. 11 <c>docs\DESIGN.md</c>): *«функция по созданию копии и передачи кому-либо
    /// смешалась с настройками резервных копий, хотя это просто отдельная фича»*. Живёт здесь и
    /// только здесь: ночные копии её не наследуют вовсе (расписание передаёт <c>false</c>), а после
    /// снятия копии она снимается САМА — вторая копия не должна молча стать передаваемой.
    /// </summary>
    private bool _shareable;

    /// <summary>
    /// Тот же предохранитель для списка режимов объёма: <see cref="RenderScope"/> ставит выбор
    /// по значению из настроек, и без флага этот показ выглядел бы как выбор человека — то есть
    /// панель записывала бы в настройки то, что только что прочитала.
    /// </summary>
    private bool _updatingScope;

    /// <summary>
    /// Посчитанные размеры по режимам: обход дерева небыстрый, а режим человек щёлкает туда-сюда.
    /// Ключ — режим, значение — «сколько байт»; <c>null</c> значит «посчитать не удалось».
    /// </summary>
    private readonly Dictionary<BackupScope, long?> _estimates = new();

    /// <summary>Какой режим считается прямо сейчас (чтобы не запустить второй обход того же дерева).</summary>
    private BackupScope? _estimating;

    /// <summary>
    /// Что окно САМО положило в поле лишних имён (<c>null</c> — ещё ничего не клало). Нужно, чтобы
    /// отличить показ состояния от правки человека: замер размера заканчивается в фоне в любой
    /// момент, и до этой проверки его перерисовка СТИРАЛА набранные имена — человек печатал,
    /// а окно молча вычищало поле (нашла проверка окна 26.09.2026: это была потеря введённого).
    /// </summary>
    private string? _shownExtras;

    /// <summary>Окно показано человеку. До показа замер размера не запускается вовсе.</summary>
    private bool _opened;

    /// <summary>
    /// Окно закрыто. Ответ фонового замера после этого рисовать нечего: органы окна уже не наши.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c>. Та же грабля, что у главного окна и настроек (`docs\STACK.md` §6, грабля 13).
    /// </summary>
    public BackupWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        Title = PanelStrings.BackupWindowTitle;
        HeadingText.Text = PanelStrings.BackupHeading;
        FolderLabel.Text = PanelStrings.BackupFolderLabel;
        CreateButton.Content = PanelStrings.BackupCreateButton;
        RefreshButton.Content = PanelStrings.BackupRefreshButton;
        PlanReadOnlyText.Text = PanelStrings.RestoreReadOnlyNote;
        EngineCheck.Content = PanelStrings.RestoreEngineCheck;
        PanelCheck.Content = PanelStrings.RestorePanelCheck;
        KeysConsentCheck.Content = PanelStrings.RestoreKeysCheck;
        ConsentNoteText.Text = PanelStrings.RestoreConsentNote;

        // Состав копии: галочка, предупреждение и заголовок списка каталогов — тексты из строк,
        // а не из разметки (правило новых экранов).
        KeysCheck.Content = PanelStrings.BackupKeysCheck;
        KeysWarningText.Text = PanelStrings.BackupKeysWarning;
        KeysLabelText.Text = PanelStrings.BackupKeysLabel;

        // «Копия для передачи» — рядом с ключами и тоже из строк: подпись, предупреждение
        // и пояснение, которое видно, когда галочка снята.
        ShareableCheck.Content = PanelStrings.BackupShareableCheck;
        ShareableWarningText.Text = PanelStrings.BackupShareableWarning;
        ShareableNoteText.Text = PanelStrings.BackupShareableOffNote;
        RunRestoreButton.Content = PanelStrings.RestoreRunButton;
        CancelRestoreButton.Content = PanelStrings.RestoreCancelButton;

        // Дверь «открыть папку копий проводником» (п. 31 `docs\DESIGN.md`): кнопка стоит вплотную
        // к строке с путём, а открывает её ДОМЕН (`IBackupControl.OpenFolder`) — окно не решает ни
        // про право, ни про существование каталога. Подпись и подсказка — из строк.
        OpenFolderButton.Content = PanelStrings.BackupOpenFolderButton;
        PanelToolTip.Set(OpenFolderButton, PanelStrings.TipBackupOpenFolderButton);

        // РЕЖИМ ОБЪЁМА (v2.2). Подписи берутся у ОДНОГО места на всю панель
        // (<see cref="BackupController.ScopeText"/>): то же слово человек читает в журнале,
        // и двух списков подписей быть не должно.
        ScopeLabel.Text = PanelStrings.BackupScopeLabel;
        ScopeBox.ItemsSource = new[]
        {
            BackupController.ScopeText(BackupScope.Auto),
            BackupController.ScopeText(BackupScope.Full),
            BackupController.ScopeText(BackupScope.Custom),
        };

        ScopeExtraLabel.Text = PanelStrings.BackupScopeExtraLabel;
        ScopeExtraHint.Text = PanelStrings.BackupScopeExtraHint;
        SaveExtrasButton.Content = PanelStrings.BackupScopeExtraSave;

        // Правда о копии на ходу — там же, где кнопка: работающий сервер ради копии не гасится
        // (решение владельца 26.09.2026), и человек обязан знать, чем это грозит копии.
        LiveAdviceText.Text = PanelStrings.BackupLiveAdvice;

        // ПОДСКАЗКИ КНОПОК. Постоянные — здесь, изменчивые (у «копия», «обновить», «Восстановить»
        // и «Отмена») — в Render: их подсказка обязана называть ПРИЧИНУ недоступности, а причина
        // меняется вместе с состоянием окна.
        PanelToolTip.Set(AddKeyDirButton, PanelStrings.TipAddKeyDirButton);

        // У «Сохранить список» подсказка ставится и здесь, а не только в RenderExtras: тот зовётся
        // лишь для СВОЕГО фильтра, а кнопка существует и в других режимах (она просто не видна).
        // Кнопка без подсказки в любом состоянии окна — дефект, даже если её сейчас не видно.
        PanelToolTip.Set(SaveExtrasButton, PanelStrings.TipSaveExtrasButton);
    }

    /// <summary>Идёт ли копия или накат — для проверок без экрана.</summary>
    public bool Busy => _busy;

    /// <summary>План наката, показанный человеку, — для проверок без экрана.</summary>
    public RestorePlan? Plan => _plan;

    /// <summary>Строка состояния — для проверок без экрана.</summary>
    public string Status => StatusText.Text ?? string.Empty;

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем отвечать на вопрос «сервер работает — погасить?».
    /// В жизни ответ даёт человек в <see cref="BackupStopWindow"/>, и в проверке без экрана этот
    /// диалог показать нельзя (он всплывает сам и отвечает не то, что решил бы человек). Больше
    /// НИЧЕГО проверкам не отдано: кнопки они нажимают настоящие, а списки выбирают настоящие.
    /// У обычного запуска это свойство всегда <c>null</c> — ответ приходит от человека.
    /// </summary>
    internal Func<StopChoice>? StopChoiceForTests { get; set; }

    /// <summary>Сколько копий сейчас в таблице — для проверок без экрана и для подписи счётчика.</summary>
    public int ListedCount => BackupTableView.Rows(EntriesTable).Count;

    /// <summary>Номер выбранной строки таблицы (<c>-1</c> — не выбрано), для проверок без экрана.</summary>
    public int SelectedEntry => _selectedIndex;

    /// <summary>Связать окно с доменом копий и (если панель связана) с сервером.</summary>
    public void Attach(IBackupControl backups, IServerControl? server = null)
    {
        ArgumentNullException.ThrowIfNull(backups);

        _backups = backups;
        _server = server;

        CreateButton.Click += (_, _) => StartCopy();
        RefreshButton.Click += (_, _) => { backups.Refresh(); Render(); };
        OpenFolderButton.Click += (_, _) => OpenBackupFolder();
        EngineCheck.IsCheckedChanged += (_, _) => ShowPlan();
        PanelCheck.IsCheckedChanged += (_, _) => ShowPlan();
        KeysConsentCheck.IsCheckedChanged += (_, _) => ShowPlan();
        CancelRestoreButton.Click += (_, _) => { _plan = null; ChooseEntry(-1); };
        RunRestoreButton.Click += (_, _) => StartRestore();

        // Состав копии меняется ЗДЕСЬ, а значение живёт в настройках: галочка и кнопка
        // «Добавить» пишут туда же, куда пишет окно настроек, — второго места правды нет.
        KeysCheck.IsCheckedChanged += (_, _) => OnKeysToggled();
        ShareableCheck.IsCheckedChanged += (_, _) => OnShareableToggled();
        AddKeyDirButton.Click += (_, _) => OnAddKeyDirectory();

        // Режим объёма и список лишних имён — как у ключей: значение уходит в настройки, а окно
        // перечитывает то, что записалось, а не то, что нажали.
        ScopeBox.SelectionChanged += (_, _) => OnScopeChanged();
        SaveExtrasButton.Click += (_, _) => OnSaveExtras();

        // Копию может снять РАСПИСАНИЕ, пока окно открыто. Без этой подписки человек видел бы
        // список без только что снятой копии и нажал бы «создать» второй раз. Снимаем подписку
        // при закрытии: окно живёт меньше контроллера, и подписка держала бы его вечно.
        backups.Changed += Render;

        // Замер размера считается ТОЛЬКО у показанного окна и только пока оно не закрыто.
        // Это не оптимизация, а граница: ответ приходит из фоновой нити и трогает органы окна,
        // а окно, которого человек не видит, имеет право уже никому не принадлежать (так и вышло
        // в проверках: невидимое окно отвечало на замер из прошлой проверки, и прогон падал
        // «The calling thread cannot access this object» — 26.09.2026).
        Opened += (_, _) =>
        {
            _opened = true;
            Render();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            backups.Changed -= Render;
        };

        backups.Refresh();
        Render();
    }

    /// <summary>
    /// ПОКАЗАТЬ ПАПКУ КОПИЙ В ПРОВОДНИКЕ — дверь окна, и она НИЧЕГО не решает сама.
    ///
    /// Право (только тот прогон, которому эти настройки принадлежат) и существование каталога
    /// проверяет домен (`IBackupControl.OpenFolder`), а окно показывает его ответ словами:
    /// отказ встаёт в строку состояния, потому что молчащая кнопка не говорит ни «нельзя»,
    /// ни «папки ещё нет».
    ///
    /// ⚠️ УДАЧА В ОКНЕ НЕ ПОКАЗЫВАЕТСЯ НИЧЕМ — то же решение, что у двери каталога настроек
    /// (замечание владельца 27.09.2026: *«Я думаю это лишнее»*): проводник человек и так видит
    /// открытым, а строка «папка копий открыта по щелчку человека» ему не отвечает ни на один
    /// вопрос. В журнале она остаётся — она полезна для разбора.
    /// </summary>
    public void OpenBackupFolder()
    {
        if (_backups is null) return;

        StatusText.Text = _backups.OpenFolder(out var error) ? string.Empty : error;
    }

    /// <summary>Перечитать папку и разложить состояние по строкам (зовут и проверки).</summary>
    public void Render()
    {
        if (_backups is null) return;

        FolderText.Text = DisplayMask.Path(_backups.Folder);

        FolderNoteText.Text = _backups.FolderFromDefault
            ? string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.BackupFolderHintFormat,
                DisplayMask.Path(_backups.Folder))
            : PanelStrings.BackupFolderCustomHint;

        // Две РАЗНЫЕ мысли — двумя строками, а не склейкой через пробел. В прогоне проверки к подписи
        // папки добавляется оговорка «копии здесь не снимаются», и однажды они слиплись в кашу:
        // «Пусто — папка по умолчанию: … копии в прогоне проверки не снимаются: …». Перевод строки
        // (а не точка) выбран потому, что правила пунктуации у трёх языков разные, а перенос — нет.
        if (!_backups.Writable) FolderNoteText.Text += "\n" + PanelStrings.BackupNotAllowed;

        ListLabel.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.BackupListCountFormat, _backups.Entries.Count);

        ListNoteText.Text = ListNote();

        RenderList();

        CreateButton.IsEnabled = _backups.Writable && !_busy;
        RefreshButton.IsEnabled = !_busy;

        RenderKeyTips();

        RenderKeys();
        RenderShareable();
        RenderScope();

        PlanPanel.IsVisible = _plan is not null;
        RunRestoreButton.IsEnabled = !_busy && _backups.Writable;
        CancelRestoreButton.IsEnabled = !_busy;
        EngineCheck.IsEnabled = !_busy;
        PanelCheck.IsEnabled = !_busy;
        KeysConsentCheck.IsEnabled = !_busy;
        EntriesTable.IsEnabled = !_busy;
    }

    /// <summary>
    /// РАЗЛОЖИТЬ КОПИИ ПО ТАБЛИЦЕ — одна дверь на отрисовку и на смену выбора.
    ///
    /// ⚠️ Таблица пересобирается ТОЛЬКО тогда, когда список и правда изменился. Присваивание
    /// списка на каждой отрисовке — тихий источник беды, и он в этом окне уже срабатывал: список
    /// при этом терял выбор, потеря выбора поднимала событие, событие звало отрисовку — и окно
    /// крутилось, пока не кончится стек (нашла проверка связки окна копий 26.09.2026; в настоящем
    /// окне это выглядело бы как «выбрал копию — выбор слетел»). Сравнение — по СОДЕРЖИМОМУ
    /// строк, а не по ссылке: движок отдаёт новый список на каждое чтение.
    ///
    /// Содержимое изменилось — выбор снимается вместе со списком: номера строк теперь значат
    /// другое, и «выбрана третья» указывала бы на другую копию. План восстановления поэтому
    /// снимается тоже: раскладывать человек будет ту копию, которую выберет заново.
    /// </summary>
    private void RenderList()
    {
        if (_backups is null) return;

        var entries = _backups.Entries;
        var signatures = entries.Select(BackupTableView.Signature).ToArray();

        if (signatures.SequenceEqual(_listedSignatures))
        {
            // Список тот же — пересобирать нечего, а выбор показать надо: он мог смениться.
            BackupTableView.Select(EntriesTable, _selectedIndex);
            return;
        }

        _listedSignatures = signatures;
        _selectedIndex = -1;
        _plan = null;

        BackupTableView.Fill(EntriesTable, entries, _selectedIndex, ChooseEntry);
    }

    /// <summary>
    /// ВЫБРАТЬ КОПИЮ — та же дверь, что и щелчок по строке таблицы: строка зовёт её же
    /// (<see cref="BackupTableView.Fill"/>), поэтому проверка идёт ровно тем путём, каким идёт
    /// человек. Номер вне списка значит «выбор снят».
    ///
    /// Панель при этом только ЧИТАЕТ архив (<see cref="ShowPlan"/>): до нажатия «Восстановить»
    /// на диске ничего не меняется — правило v1, оплаченное накатом вслепую.
    /// </summary>
    public void ChooseEntry(int index)
    {
        if (_backups is null || _busy) return;

        _selectedIndex = index >= 0 && index < _backups.Entries.Count ? index : -1;

        BackupTableView.Select(EntriesTable, _selectedIndex);
        ShowPlan();
    }

    /// <summary>
    /// Подсказки кнопок этого окна. Кнопка здесь бывает недоступна по ДВУМ разным причинам —
    /// «идёт копия или накат» и «этот прогон копии не снимает», — и человеку они говорят разное:
    /// первая значит «подожди», вторая — «здесь этого не будет никогда». Молчащая серая кнопка
    /// не говорит ни того, ни другого.
    /// </summary>
    private void RenderKeyTips()
    {
        if (_backups is null) return;

        var busy = _busy;
        var allowed = _backups.Writable;

        PanelToolTip.Set(CreateButton, busy
            ? PanelStrings.TipBackupBusy
            : allowed ? PanelStrings.TipCreateBackupButton : PanelStrings.TipBackupNotAllowed);

        PanelToolTip.Set(RefreshButton, busy ? PanelStrings.TipBackupBusy : PanelStrings.TipRefreshBackupsButton);

        PanelToolTip.Set(RunRestoreButton, busy
            ? PanelStrings.TipBackupBusy
            : allowed ? PanelStrings.TipRunRestoreButton : PanelStrings.TipBackupNotAllowed);

        PanelToolTip.Set(CancelRestoreButton, busy
            ? PanelStrings.TipBackupBusy
            : PanelStrings.TipCancelRestoreButton);
    }

    /// <summary>
    /// Раздел «ключи»: галочка состава, предупреждение, список каталогов и кнопка «Добавить».
    ///
    /// Три вещи здесь важны, и все три — про честность:
    ///
    /// 1. **галочка показывает ДЕЙСТВУЮЩЕЕ значение из настроек** и меняет именно его: своего
    ///    значения у окна нет, иначе два окна однажды разошлись бы;
    /// 2. **предупреждение видно, когда разрешение включено** — архив с приватными ключами сам
    ///    становится ключом доступа, и об этом говорят до, а не после;
    /// 3. **найденный каталог предлагается кнопкой**, а не подставляется в настройки молча —
    ///    тем же правилом, что и рабочая папка, найденная у панели v1.
    /// </summary>
    private void RenderKeys()
    {
        if (_backups is null) return;

        _updatingKeys = true;
        KeysCheck.IsChecked = _backups.WithKeys;
        KeysCheck.IsEnabled = _backups.CanChangeComposition && !_busy;
        _updatingKeys = false;

        var dirs = _backups.KeyDirectories;

        KeysWarningText.IsVisible = _backups.WithKeys;
        KeysList.ItemsSource = dirs;
        KeysList.IsVisible = dirs.Count > 0;

        KeysNoteText.Text = !_backups.WithKeys
            ? PanelStrings.BackupKeysOff
            : dirs.Count == 0
                ? PanelStrings.BackupKeysEmpty
                : string.Empty;

        var suggestion = _backups.KeySuggestions.FirstOrDefault();

        AddKeyDirButton.IsVisible = suggestion is not null && _backups.CanChangeComposition && !_busy;
        if (suggestion is not null)
        {
            AddKeyDirButton.Content = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupKeysAddFormat, suggestion.Describe());
        }
    }

    /// <summary>
    /// Человек переключил разрешение на ключи. Значение сохраняется в настройках, а окно
    /// перечитывает состояние: показывается то, что записалось, а не то, что нажали.
    /// </summary>
    private void OnKeysToggled()
    {
        if (_updatingKeys || _backups is null) return;

        if (!_backups.SetWithKeys(KeysCheck.IsChecked == true))
            StatusText.Text = PanelStrings.BackupKeysLockedLog;

        Render();
    }

    /// <summary>
    /// «ЭТА КОПИЯ — ДЛЯ ПЕРЕДАЧИ»: галочка показывает состояние ОКНА (<see cref="_shareable"/>),
    /// предупреждение видно, когда она стоит, а при снятой пояснение говорит, что это СВОЯ копия
    /// и передавать её нельзя.
    ///
    /// ⚠️ **НАСТРОЙКИ ЗДЕСЬ НЕ ЧИТАЮТСЯ ВОВСЕ** (решение владельца 27.09.2026, п. 11): передача —
    /// разовое действие на одну копию, и брать её из файла значило бы снова сделать её настройкой,
    /// которую унаследуют ночные копии.
    ///
    /// ⚠️ **Сочетание с приватными ключами ЗАПРЕЩЕНО** (решение владельца 29.09.2026, п. 12): такой
    /// архив унёс бы приватные ключи человека тому, кому его отдают, — а назван «для передачи».
    /// Запрет живёт в ОДНОМ месте домена (<see cref="BackupPlanner.ShareableWithKeysRefusal"/>):
    /// у окна, расписания и «--backup» ответ один. Здесь он (1) не оставляет запрещённое сочетание
    /// выбранным в окне и (2) называет причину словами ТАМ ЖЕ, где человек ставит галочку, — чтобы
    /// он прочитал её до нажатия «Создать копию», а не после.
    /// </summary>
    private void RenderShareable()
    {
        if (_backups is null) return;

        // Возможно ли сочетание ВООБЩЕ: при включённых приватных ключах передаваемой эту копию
        // объявить нельзя ни при каком щелчке. Галочка тогда недоступна, а причина — словами.
        var blocked = RefusalFor(shareable: true);

        // Запрещённое сочетание в окне не живёт: галочка снимается.
        if (blocked.Length > 0) _shareable = false;

        _updatingShareable = true;
        ShareableCheck.IsChecked = _shareable;
        ShareableCheck.IsEnabled = _backups.CanChangeComposition && !_busy && blocked.Length == 0;
        _updatingShareable = false;

        ShareableWarningText.IsVisible = _shareable;
        ShareableNoteText.Text = blocked.Length > 0
            ? blocked
            : _shareable
                ? string.Empty
                : PanelStrings.BackupShareableOffNote;
    }

    /// <summary>
    /// Почему ЭТУ копию нельзя объявить передаваемой (пусто — можно). Спрашивается у домена:
    /// второй формулировки того же запрета в панели быть не должно — иначе окно, расписание
    /// и «--backup» однажды разошлись бы в том, что запрещено.
    /// </summary>
    private string RefusalFor(bool shareable) =>
        BackupPlanner.ShareableWithKeysRefusal(_backups?.WithKeys == true, shareable);

    /// <summary>Почему нельзя объявить передаваемой ту копию, которую окно готовит СЕЙЧАС.</summary>
    private string ShareableRefusal() => RefusalFor(_shareable);

    /// <summary>
    /// Человек переключил «эту копию — для передачи». Состояние живёт В ОКНЕ, а не в настройках;
    /// запрещённое сочетание (приватные ключи) не принимается вовсе, и причина называется словами
    /// сразу — молча отскочившая галочка читалась бы как «панель не поняла».
    /// </summary>
    private void OnShareableToggled()
    {
        if (_updatingShareable || _backups is null) return;

        var wanted = ShareableCheck.IsChecked == true;
        var refusal = RefusalFor(wanted);

        if (refusal.Length > 0)
        {
            _shareable = false;
            StatusText.Text = refusal;
        }
        else
        {
            _shareable = wanted;
        }

        Render();
    }

    /// <summary>
    /// РАЗДЕЛ «РЕЖИМ ОБЪЁМА» (v2.2): три значения, пояснение к выбранному и — для полного
    /// режима — честное предупреждение о размере, посчитанное ДО начала копии.
    ///
    /// Размер считается В ФОНЕ и запоминается по режиму: обход дерева на сотни мегабайт занимает
    /// секунды, и делать его в нитке интерфейса значило бы заморозить окно на ровном месте. Пока
    /// счёт идёт, на экране «считаю размер…», а не пустое место: молчание выглядело бы как
    /// «размер не важен». Не удалось посчитать — строки нет вовсе, и это честнее выдуманного числа.
    /// </summary>
    private void RenderScope()
    {
        if (_backups is null) return;

        var scope = _backups.Scope;

        _updatingScope = true;
        ScopeBox.SelectedIndex = BackupScopeDecisions.IndexOf(scope);
        ScopeBox.IsEnabled = _backups.CanChangeComposition && !_busy;
        _updatingScope = false;

        // Лишние имена видит только «свой фильтр»: в остальных режимах список не действует,
        // а поле, которое ничего не меняет, вреднее пустого места (правило окна настроек).
        var custom = scope == BackupScope.Custom;
        ScopeExtraLabel.IsVisible = custom;
        ScopeExtraBox.IsVisible = custom;
        ScopeExtraHint.IsVisible = custom;
        SaveExtrasButton.IsVisible = custom;

        if (custom) RenderExtras();

        ScopeHintText.Text = Hint(scope) + SizeLine(scope);
    }

    /// <summary>Пояснение к выбранному режиму. Про каждый режим — своё, и оно не смешивается с другими.</summary>
    private string Hint(BackupScope scope) => scope switch
    {
        BackupScope.Full => PanelStrings.BackupScopeFullWarning,
        BackupScope.Custom => _backups is not null && _backups.ExtraExclusions.Count == 0
            ? PanelStrings.BackupScopeCustomEmpty
            : PanelStrings.BackupScopeCustomHint,
        _ => PanelStrings.BackupScopeAutoHint,
    };

    /// <summary>
    /// Строка о размере: «Примерно 1,2 ГБ…», «считаю размер…» или пусто.
    ///
    /// Отдельной строкой, а не склейкой через пробел: пояснение режима и размер — две разные
    /// мысли, и однажды они слиплись бы в кашу (тот же случай, что у подписи папки копий).
    /// </summary>
    private string SizeLine(BackupScope scope)
    {
        // Прогон проверки не читает данные владельца вовсе — считать нечего и НЕЛЬЗЯ (красная
        // линия 4). Поэтому здесь не «не знаю», а «не спрашиваю».
        if (_backups is null || !_backups.Writable) return string.Empty;

        // И считаем только у ПОКАЗАННОГО, не закрытого окна: ответ придёт из фоновой нити и станет
        // трогать органы окна, а окно, которого человек не видит, имеет право уже никому не
        // принадлежать (на этом падал прогон проверок 26.09.2026).
        if (!_opened || _closed) return string.Empty;

        if (_estimates.TryGetValue(scope, out var bytes))
        {
            return bytes is null
                ? string.Empty
                : Environment.NewLine + string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BackupScopeEstimateFormat,
                    BackupFormat.Size(bytes.Value));
        }

        StartEstimate(scope);

        return Environment.NewLine + PanelStrings.BackupScopeEstimateRunning;
    }

    /// <summary>Посчитать размер в фоне и перерисовать окно, когда ответ придёт.</summary>
    private void StartEstimate(BackupScope scope)
    {
        if (_estimating is not null || _backups is null) return;

        _estimating = scope;
        var backups = _backups;

        Task.Run(() => backups.Estimate(scope)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            // Окно закрыли, пока считалось, — рисовать нечего и НЕЛЬЗЯ: органы окна уже не наши.
            if (_closed) return;

            _estimating = null;

            _estimates[scope] = task.IsCompletedSuccessfully && task.Result.Known
                ? task.Result.Bytes
                : null;

            Render();
        }));
    }

    /// <summary>
    /// Показать действующий список лишних имён — то, что лежит в настройках.
    ///
    /// ⚠️ **Набранное человеком не затирается.** Показ состояния обязан быть показом: в окне идёт
    /// ФОНОВЫЙ замер размера, его ответ приходит когда угодно и зовёт перерисовку — а человек
    /// в этот момент печатает имена. До 26.09.2026 перерисовка вычищала поле целиком, и «сохранить»
    /// записывало пустой список (поймала проверка окна; это была потеря введённого, а не мигание).
    /// Поэтому поле обновляется, только если оно всё ещё показывает то, что положило окно.
    /// </summary>
    private void RenderExtras()
    {
        if (_backups is null) return;

        ShowExtras(string.Join(Environment.NewLine, _backups.ExtraExclusions));

        ScopeExtraBox.IsEnabled = _backups.CanChangeComposition && !_busy;
        SaveExtrasButton.IsEnabled = _backups.CanChangeComposition && !_busy;

        // Кнопка сохранения списка бывает недоступна по ДВУМ причинам, и они разные: «идёт копия
        // или накат» (подожди) и «состав копии в этом прогоне не меняется» (не изменится вовсе).
        PanelToolTip.Set(SaveExtrasButton, _busy
            ? PanelStrings.TipBackupBusy
            : _backups.CanChangeComposition
                ? PanelStrings.TipSaveExtrasButton
                : PanelStrings.TipBackupCompositionLocked);
    }

    /// <summary>
    /// Положить в поле текст — если человек его не правил. Ровно одна дверь на всю запись в поле:
    /// так «показать состояние» и «показать только что сохранённое» не могут разойтись.
    /// <paramref name="force"/> — запись после сохранения: там показывается ЗАПИСАННОЕ,
    /// и она обязана победить набранное (человек должен увидеть, что именно ушло в настройки).
    /// </summary>
    private void ShowExtras(string text, bool force = false)
    {
        if (!force && ScopeExtraBox.Text != _shownExtras) return;

        _shownExtras = text;
        ScopeExtraBox.Text = text;
    }

    /// <summary>Человек выбрал другой режим объёма — значение уходит в настройки.</summary>
    private void OnScopeChanged()
    {
        if (_updatingScope || _backups is null) return;

        if (!_backups.SetScope(BackupScopeDecisions.FromIndex(ScopeBox.SelectedIndex)))
            StatusText.Text = PanelStrings.BackupKeysLockedLog;

        Render();
    }

    /// <summary>
    /// Сохранить названные лишние имена. Замер своего фильтра после этого не годится: список —
    /// часть режима, и старый размер обещал бы не то, что будет скопировано.
    /// </summary>
    private void OnSaveExtras()
    {
        if (_backups is null) return;

        var names = (ScopeExtraBox.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (!_backups.SetExtraExclusions(names))
        {
            StatusText.Text = PanelStrings.BackupKeysLockedLog;
        }
        else
        {
            _estimates.Remove(BackupScope.Custom);

            // Показать ЗАПИСАННОЕ (чистый список), а не то, что набрали: пустые строки и повторы
            // отсеялись, и человек обязан видеть, что именно ушло в настройки.
            ShowExtras(string.Join(Environment.NewLine, _backups.ExtraExclusions), force: true);

            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupScopeExtraSavedLogFormat,
                _backups.ExtraExclusions.Count, string.Join(", ", _backups.ExtraExclusions));
        }

        Render();
    }

    /// <summary>Добавить найденный каталог ключей в настройки — и сразу увидеть его в списке.</summary>
    private void OnAddKeyDirectory()
    {
        if (_backups is null) return;

        var suggestion = _backups.KeySuggestions.FirstOrDefault();
        if (suggestion is null) return;

        if (!_backups.AddKeyDirectory(suggestion.Path))
            StatusText.Text = PanelStrings.BackupKeysLockedLog;

        Render();
    }

    /// <summary>
    /// Строка под списком. Порядок важен: причина недоступности папки и «папки ещё нет» —
    /// это разные вещи, и ни одна из них не должна выглядеть как «копий нет» (урок v1).
    /// </summary>
    private string ListNote()
    {
        if (_backups is null) return string.Empty;

        if (_backups.ListProblem.Length > 0) return _backups.ListProblem;

        if (_backups.Entries.Count > 0) return PanelStrings.BackupListSelectHint;

        return Directory.Exists(_backups.Folder)
            ? PanelStrings.BackupListEmpty
            : string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.BackupListMissingFolderFormat,
                DisplayMask.Path(_backups.Folder));
    }

    /// <summary>
    /// Показать план наката по выбранной копии. Панель ЧИТАЕТ архив и ничего не меняет.
    /// План пересобирается при смене галочек: снятая галочка «движок» (или «ключи») меняет состав
    /// наката, и показанный план обязан совпадать с тем, что произойдёт.
    /// </summary>
    private void ShowPlan()
    {
        if (_backups is null || _busy) return;

        var index = _selectedIndex;
        if (index < 0 || index >= _backups.Entries.Count)
        {
            _plan = null;
            Render();
            return;
        }

        var entry = _backups.Entries[index];
        _plan = _backups.PlanRestore(
            entry.Path,
            EngineCheck.IsChecked == true,
            PanelCheck.IsChecked == true,
            KeysConsentCheck.IsChecked == true);

        PlanHeadingText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.RestorePlanHeadingFormat, entry.Name);
        PlanVersionText.Text = VersionLine(entry.EngineVersion);
        ShowPlanBody(_plan);

        Render();
        ShowPlanOnScreen();
    }

    /// <summary>
    /// ПОКАЗАТЬ САМ ПЛАН: годный — ТАБЛИЦЕЙ, негодный — причиной словами.
    ///
    /// Слова владельца 29.09.2026 (п. 32 `docs\DESIGN.md`): *«Что вернём, копия? Текст очень
    /// неструктурированный, просто сплошной, и читать невозможно»*. Поэтому строки плана строит
    /// `Views\RestoreTableView.cs` — по СТРУКТУРНЫМ данным плана (`RestorePlan.Groups`), на общей
    /// сетке панели, как у таблицы копий (п. 29). Прежняя склейка выбранных групп в один
    /// `TextBlock` убрана из разметки, а не оставлена рядом: «как выглядит строка» не может
    /// решаться в двух местах.
    ///
    /// ⚠️ ПУТЬ ОТКАЗА ПЛАНА СОХРАНЁН ЦЕЛИКОМ: `RestorePlan.Ok == false` — причина и примечания
    /// словами, ровно как было. Таблицы при отказе нет: раскладывать нечего, и строки без решения
    /// читались бы как готовый план.
    ///
    /// ⚠️ Примечания плана стоят ОТДЕЛЬНЫМИ строками, а счётчик под таблицей — КОРОТКИЙ: они
    /// отвечают на разные вопросы, и склейка их в один абзац вернула бы ту самую «простыню».
    /// В отказе примечания не дублируются: причину называет <see cref="PlanProblemText"/>, а сами
    /// примечания человек читает здесь же, одной строкой на каждое.
    /// </summary>
    private void ShowPlanBody(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        PlanProblemText.IsVisible = !plan.Ok;
        PlanProblemText.Text = plan.Ok
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.RestorePlanImpossibleFormat, plan.Error);

        var rows = RestoreTableView.Rows(plan);

        // Шапка без единой строки ничего не сообщает: таблицы нет вовсе, а числа называет счётчик.
        PlanTable.IsVisible = plan.Ok && rows.Count > 0;
        if (plan.Ok) RestoreTableView.Fill(PlanTable, plan);

        PlanSummaryText.IsVisible = plan.Ok;
        PlanSummaryText.Text = plan.Ok ? RestoreTableView.Summary(plan) : string.Empty;

        PlanNotesText.Text = string.Join(Environment.NewLine, plan.Notes);
        PlanNotesText.IsVisible = plan.Notes.Count > 0;
    }

    /// <summary>
    /// ПОДВЕСТИ ПЛАН К ГЛАЗАМ. План — главное, что человек обязан увидеть ПЕРЕД накатом,
    /// а живёт он НИЖЕ списка копий: до этой работы он появлялся за нижним краем окна, и человек
    /// нажимал «Восстановить», не прочитав, что именно вернётся (жалоба владельца, п. 8
    /// `docs\DESIGN.md` — «план наката — простыня, и его появление незаметно»).
    ///
    /// Отдельным методом, а не строкой внутри <see cref="ShowPlan"/>: «план собран» и «план
    /// показан» — два разных утверждения, и проверяются они порознь.
    /// </summary>
    private void ShowPlanOnScreen()
    {
        if (_plan is null) return;

        // Окно, которого человек не видит (прогон проверки; окно ещё не показано), прокручивать
        // некуда, а трогать органы закрытого окна нельзя вовсе.
        if (!_opened || _closed) return;

        // ⚠️ Просьба ОТКЛАДЫВАЕТСЯ до конца прохода вёрстки, и это не придирка: панель плана
        // только что стала видимой (Render выше), а вёрстка её ещё не измерила — прямой
        // `BringIntoView()` уходит в ScrollViewer с прежними координатами панели. Проверено
        // прогоном: окно прокручивалось на 180 пикселей вместо нужных 600, то есть план
        // оставался за нижним краем — ровно тот дефект, от которого эта работа и заведена.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_closed || !PlanPanel.IsVisible) return;
                PlanPanel.BringIntoView();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Строка про версию движка (находка В5, `docs\ROADMAP.md`): копию, снятую одной версией
    /// движка, другая версия может не открыть — и панель обязана сказать это ДО наката.
    /// После человек уже не поймёт, почему сессии не открылись.
    ///
    /// Само правило живёт в движке наката (<see cref="RestoreEngine.VersionNote"/>): у него ДВА
    /// пользователя — это окно и режим наката без окна (`--restore`), и второе место разошлось бы
    /// с первым.
    /// </summary>
    private string VersionLine(string copyVersion) =>
        RestoreEngine.VersionNote(copyVersion, _backups?.InstalledEngineVersion);

    /// <summary>
    /// Снять копию. Сначала — вопрос про сервер (решение владельца 26.09.2026), потом сама копия
    /// в фоне. Отказ от копии на любом из шагов не трогает ни сервер, ни папку.
    ///
    /// ⚠️ Запрещённое сочетание («приватные ключи» + «для передачи») отвергается ЗДЕСЬ, до вопроса
    /// про сервер и до всякой работы: человеку нужно прочитать причину раньше, чем он будет ждать
    /// копию, которой не будет. Тот же запрет есть в плане (<see cref="BackupPlanner.Full"/>) —
    /// и это не второй запрет, а тот же самый: слова берутся из одного места
    /// (<see cref="BackupPlanner.ShareableWithKeysRefusal"/>), а проверка тут стоит затем, чтобы
    /// отказ был ВИДЕН, а не только исполнен.
    /// </summary>
    private async void StartCopy()
    {
        if (_backups is null || _busy || !_backups.Writable) return;

        var refusal = ShareableRefusal();

        if (refusal.Length > 0)
        {
            _shareable = false;
            StatusText.Text = refusal;
            Render();
            return;
        }

        var running = _server is not null && _server.State.Presence == ServerPresence.Running;
        var stopSucceeded = false;
        var choice = StopChoice.ContinueLive;

        if (running)
        {
            choice = await AskAboutServer();

            // ГАСИМ ЗДЕСЬ, а не «помечаем, что собирались»: без этого вызова выбор «Погасить
            // и продолжить» ничего не гасил, а движку сообщалось, что сервер не работает, —
            // то есть из отчёта пропадала оговорка «копия снята на ходу». Найдено 26.09.2026.
            if (choice == StopChoice.StopAndContinue)
                stopSucceeded = await StopServer();
        }

        var decision = BackupStopDecisions.Decide(running, choice, stopSucceeded);
        if (!decision.Proceed) return;

        // Оговорка «на ходу» — это правда о СЕРВЕРЕ, а не намерение человека: если погасить
        // не удалось, сервер работает, и движок обязан сказать это в отчёте.
        var liveCopy = decision.LiveCopy;
        var archive = _backups.NextArchivePath();
        var backups = _backups;

        // Решение о передаче СНИМАЕТСЯ здесь же (до фона): оно про ОДНУ копию, и снимать его после
        // ответа движка значило бы оставить окно в состоянии «передаваемая» на время работы —
        // а человек за это время успел бы нажать «Создать» второй раз.
        var shareable = _shareable;
        _shareable = false;

        RunInBackground(
            () => backups.CreateCopy(archive, liveCopy, shareable),
            PanelStrings.BackupRunning,
            outcome =>
            {
                StatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    outcome.Ok ? PanelStrings.BackupDoneFormat : PanelStrings.BackupFailedFormat,
                    outcome.Summary())
                    + Environment.NewLine
                    + string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupFileFormat, DisplayMask.Path(archive));

                // Ротация (v2.2) видна ЗДЕСЬ, а не только в журнале: человек, только что снявший
                // копию, обязан узнать, что панель заодно убрала старую, — иначе он найдёт пропажу
                // сам и решит, что копии теряются.
                if (outcome.Rotation.Length > 0)
                    StatusText.Text += Environment.NewLine + outcome.Rotation;

                // Список перечитывается: новая копия обязана появиться в нём сразу, а не после
                // перезапуска окна — иначе человек нажмёт «Создать» второй раз.
                backups.Refresh();
                _plan = null;
            });
    }

    /// <summary>
    /// Разложить копию. Вопрос про сервер задаётся тем же окном и по той же причине: накат поверх
    /// работающего сервера не заменит занятые файлы.
    /// </summary>
    private async void StartRestore()
    {
        if (_backups is null || _plan is null || _busy || !_backups.Writable) return;

        var plan = _plan;
        var withEngine = EngineCheck.IsChecked == true;
        var withPanel = PanelCheck.IsChecked == true;
        var withKeys = KeysConsentCheck.IsChecked == true;

        var running = _server is not null && _server.State.Presence == ServerPresence.Running;
        var stopSucceeded = false;
        var choice = StopChoice.ContinueLive;

        if (running)
        {
            choice = await AskAboutServer();

            // То же, что при копии: накат поверх работающего сервера не заменит занятые файлы,
            // поэтому «погасить» обязано действительно гасить — см. разбор в StartCopy.
            if (choice == StopChoice.StopAndContinue)
                stopSucceeded = await StopServer();
        }

        var decision = BackupStopDecisions.Decide(running, choice, stopSucceeded);
        if (!decision.Proceed) return;

        var liveRestore = decision.LiveCopy;
        var backups = _backups;

        RunInBackground(
            () => backups.RunRestore(plan, withEngine, withPanel, withKeys, liveRestore),
            PanelStrings.RestoreRunning,
            outcome =>
            {
                // Три вида итога, а не два: «не сделано» и «сделано не всё» — разные вещи (находка В6).
                // На exFAT ссылки не ложатся, и файлы при этом восстанавливаются: сказать «накат
                // не сделан» значило бы послать человека накатывать заново поверх уже разложенного.
                var landed = outcome.Result is { Files: > 0 };
                var format = outcome.Ok
                    ? PanelStrings.RestoreDoneFormat
                    : landed ? PanelStrings.RestoreIncompleteFormat : PanelStrings.RestoreFailedFormat;

                StatusText.Text = string.Format(CultureInfo.CurrentCulture, format, outcome.Summary());

                backups.Refresh();
                _plan = null;
            });
    }

    /// <summary>Спросить про работающий сервер. Возвращает выбор человека, а не решение окна.</summary>
    private async Task<StopChoice> AskAboutServer()
    {
        // Проверка без экрана: диалог показать некому, ответ задан проверкой.
        if (StopChoiceForTests is not null) return StopChoiceForTests();

        var dialog = new BackupStopWindow();
        dialog.Attach(_server?.State.Port ?? 0);

        return await dialog.ShowDialog<StopChoice>(this);
    }

    /// <summary>
    /// Погасить сервер перед копией или накатом. СВОЙ сервер панель гасит свободно; найденный
    /// и взятый под управление — только по отдельному подтверждению КАЖДЫЙ РАЗ (решение владельца
    /// 24.09.2026): через такой сервер может идти его работа. Отказ в подтверждении — это
    /// «гасить не будем», и он не превращается в «гасим без спроса».
    ///
    /// ⚠️ Исключение одно и то же во всей панели: если согласие на этот сервер ЗАПОМНЕНО
    /// (`ConsentRemembered`), человек уже ответил «беру под управление» (решение владельца
    /// 26.09.2026) — тогда подтверждения не спрашиваем и здесь.
    /// </summary>
    private async Task<bool> StopServer()
    {
        if (_server is null) return false;

        // Спрашивать ли подтверждение — это ОТДЕЛЬНЫЙ вопрос от «есть ли уже согласие».
        // Путаница этих двух имён и была дефектом: `confirmed = !NeedsStopConfirmation(...)` делало
        // confirmed ИСТИННЫМ ровно там, где подтверждения не требуют, — и свой сервер панели уходил
        // в ветку вопроса ни за чем, а настоящий вопрос не задавался вовсе. Нашла проверка связки.
        var confirmed = false;

        if (ServerDecisions.NeedsStopConfirmation(
                _server.Owner, _server.State.Presence, _server.ConsentRemembered))
        {
            var state = _server.State;
            var dialog = new ConfirmStopWindow();
            dialog.Attach(state.Port, state.Pid, state.ProcessName);

            confirmed = await dialog.ShowDialog<bool>(this);
            if (!confirmed) return false;
        }
        else
        {
            // Свой сервер — процесс панели, и подтверждения ему не нужно. Сервер с запомненным
            // согласием — тот, о котором человек уже ответил: спрашивать второй раз нечего.
            confirmed = true;
        }

        var server = _server;

        _busy = true;
        StatusText.Text = PanelStrings.ServerStopping;
        Render();

        var stopped = await Task.Run(() => server.Stop(confirmed));

        _busy = false;

        if (stopped.Presence != ServerPresence.Stopped)
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupStopFailedFormat, stopped.Detail);

        Render();
        return stopped.Presence == ServerPresence.Stopped;
    }

    /// <summary>
    /// Выполнить работу в фоне и вернуть окно в рабочее состояние. Тот же приём, что в главном окне:
    /// синхронная копия на сотни мегабайт заморозила бы интерфейс.
    /// </summary>
    private void RunInBackground<T>(Func<T> work, string busyText, Action<T> done)
    {
        _busy = true;
        StatusText.Text = busyText;
        Render();

        Task.Run(work).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;

            if (task.IsCompletedSuccessfully)
            {
                done(task.Result);
            }
            else
            {
                StatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.BackupFailedFormat,
                    task.Exception?.GetBaseException().Message ?? PanelStrings.BackupUnknown);
            }

            Render();
        }));
    }
}
