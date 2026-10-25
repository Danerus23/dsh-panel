using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Agents;
using DshPanel.Autostart;
using DshPanel.Backup;
using DshPanel.Balance;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;

namespace DshPanel.Views;

/// <summary>
/// Окно настроек. Показывает ровно то, что панель умеет СЕЙЧАС, и ничего «на будущее»:
/// рабочую папку сервера, папку копий, тему оформления, автозапуск, баланс и тариф активного
/// агента и честный отчёт о том, что панель видит вокруг себя.
///
/// **Дверь в «О программе» живёт ЗДЕСЬ** (решение владельца 27.09.2026: «„о программе“ не туда
/// воткнул. Предлагаю перенести вообще в настройки»). Это ДВЕРЬ, а не настройка: она стоит
/// в нижней строке рядом с «Сохранить», ВНЕ прокрутки раздела, — и потому видна при любом
/// разделе. В списке разделов ей места нет: список — это настройки, и пункт, который ничего
/// не настраивает, ломал бы и список, и подсветку выбранного раздела. Само окно осталось одно
/// на всю панель: его держит слот связки, и дверь лишь сообщает о просьбе.
///
/// Чего здесь СОЗНАТЕЛЬНО нет и почему:
///
/// * **порт сервера** — здесь ЕСТЬ (решение владельца 24.09.2026, значение пересмотрено
///   26.09.2026): постоянный порт своего сервера, умолчание — 3080, тот же адрес, на котором
///   стояла панель 1.x. Запрет на ЗАНЯТИЕ этого порта остался у прогонов проверки и живёт
///   в контроллере сервера, а не в этом окне: файл настроек принадлежит человеку;
/// * **сводка по всем агентам** — решение владельца: отдельное окно со балансами всех
///   установленных агентов планируется на следующие версии. Пока в окне активный агент;
/// * **язык** — появится вместе со своим кодом (этап 5).
///
/// **Папка копий появилась 26.09.2026** вместе с окном копий: сам экран копий — отдельное окно
/// (решение владельца), а его НАСТРОЙКА живёт здесь, в настройках. Это не противоречие:
/// «главное окно функциональное, настройки — в настройках», а копия — действие, а не настройка.
///
/// Поле, которое ничего не меняет, вреднее пустого места: человек ставит галочку, ничего
/// не происходит, и он перестаёт верить остальным.
/// </summary>
public partial class SettingsWindow : Window, IQuietClose, IUpdateSectionWindow, IAboutSectionWindow
{
    private ISettingsControl? _settings;
    private IAutostartControl? _autostart;
    private IBalanceControl? _balance;
    private IPricingControl? _pricing;
    private AutostartPanelModel _autostartModel = new();

    /// <summary>
    /// РАЗДЕЛ «ТЕЛЕФОН» (этап F1.d) — дверь к решениям о телефоне: рубильник, порт, код
    /// сопряжения, список подключённых и адрес своей сети.
    ///
    /// ⚠️ **Ни одного своего решения здесь нет, и это тот же приём, что у раздела «Обновление».**
    /// Решение о том, годится ли адрес телефону, живёт в <see cref="PhoneDecisions.Suitable"/>,
    /// раскладка данных системы в адрес — в <see cref="NetworkAddresses"/>, слова о состоянии —
    /// в <see cref="PhoneDecisions"/> и <see cref="PhoneDecisions.PairingCodeText"/>. Окно только
    /// показывает готовое и просит у двери сделать то, что человек нажал.
    ///
    /// <see cref="IPhoneSection"/> нет (окно построено без связки — так его видят некоторые
    /// проверки) — раздел честно говорит, что телефоном в этом прогоне не управляют, а не
    /// показывает пустые поля без объяснения.
    /// </summary>
    private IPhoneSection? _phone;

    /// <summary>
    /// ДВЕРЬ БРАНДМАУЭРА (этап F4, кусок 1) — ОТДЕЛЬНЫМ ЧЛЕНОМ, а не внутри двери телефона.
    ///
    /// ⚠️ Почему отдельно. Правило брандмауэра — ИЗМЕНЕНИЕ НА МАШИНЕ, и права у него свои
    /// (<see cref="RunRights.MayChangeFirewall"/>): окно, которое умеет ставить правило, и окно,
    /// которое показывает состояние телефона, — разные двери, и слей их в одну, «нет права»
    /// у прогона проверки стало бы «нет телефона».
    ///
    /// ⚠️ Двери нет — раздел честно говорит, что брандмауэром в этом прогоне не управляют
    /// (<see cref="PanelStrings.FirewallNoDoor"/>), и НИ ОДНОГО чтения правил не делает:
    /// у съёмки (`--shot settings-phone`) двери нет вовсе, поэтому кадр не ходит в `netsh`.
    /// </summary>
    private IFirewallDoor? _firewall;

    /// <summary>
    /// СВОЯ нитка интерфейса, взятая при постройке окна (t13 — тот же класс, что Н-1).
    ///
    /// ⚠️ Через неё возвращаются ответы фоновой работы: пожарная дверь (OnFirewallClick →
    /// FinishFirewall) и подготовка обновления (PrepareNow). Память общей двери одна на процесс,
    /// и её забывает проверка «нитки нет вовсе», идущая параллельно; тогда ответ не доходил,
    /// а кнопка брандмауэра / раздел обновления оставались занятыми навсегда.
    /// </summary>
    private readonly Dispatcher _ui;

    /// <summary>
    /// НИТКА ИНТЕРФЕЙСА ОКНА НАСТРОЕК ИЛИ ГРОМКИЙ ОТКАЗ (t13). Текст называет СВОЁ окно
    /// (см. `UiThread.RequireWindow`); проверяется это решение, а не постройка окна: окно на нитке
    /// без платформы построить нельзя (базовый `Window` падает первым).
    /// </summary>
    internal static Dispatcher RequireUiThread(Dispatcher? captured) =>
        UiThread.RequireWindow(captured, "ОКНО НАСТРОЕК");
    /// <summary>Последнее прочитанное состояние правила — по нему решается, что сделает кнопка.</summary>
    private FirewallState? _firewallState;

    /// <summary>Идёт изменение: кнопка выключена, второй раз не жмётся, сказано «ждём подтверждения».</summary>
    private bool _firewallBusy;

    /// <summary>Проверка выпусков для РАЗДЕЛА «Обновление» — единственное место, где он показан.</summary>
    private IUpdateControl? _update;

    /// <summary>Дверь блока движка (даёт `App`; может молчать).</summary>
    private IEngineUpdateSection? _engineUpdate;

    /// <summary>Человек нажал «Да, поставить непроверенную» — согласие РАЗОВОЕ и снимается.</summary>
    private bool _engineFreshConfirmed;

    /// <summary>Идёт обновление движка: кнопки блока на это время недоступны, окно остаётся живым.</summary>
    private bool _engineBusy;

    /// <summary>Подставная дверь вопроса (проверки); у человека её нет — вопрос задаёт своё окно.</summary>
    private Func<PanelQuestion, Task<bool>>? _engineAsk;

    /// <summary>Подставная дверь браузера для ссылки на выпуски движка (проверки); у человека — своя.</summary>
    private Func<string, string>? _engineBrowse;

    /// <summary>Движок установки для того же раздела: готовит замену файлов и запускает сценарий.</summary>
    private IUpdateInstall? _install;

    /// <summary>«Запомнить пропущенную версию» — владелец настроек, как и у прочих одиночных решений.</summary>
    private Func<string, bool>? _skipUpdate;

    /// <summary>
    /// Закрыть панель ПОСЛЕ запуска сценария замены — общая дверь панели
    /// (<c>PanelShell.Exit</c>). Единственный случай, когда панель просят завершиться, и зовётся
    /// он ровно по щелчку человека.
    /// </summary>
    private Action? _exitForUpdate;

    /// <summary>
    /// СОСТОЯНИЕ ПОДГОТОВКИ ОБНОВЛЕНИЯ — ОБЪЕКТ УРОВНЯ ПАНЕЛИ, а не поле окна (DEF-058, 2.4.2).
    /// У человека его создаёт <c>App</c> и передаёт доводом <see cref="Attach"/>: одно состояние
    /// на процесс, поэтому закрытое окно больше не «забывает», что подготовка идёт.
    ///
    /// ⚠️ Здесь заводится СВОЁ только для окна, построенного без панели (проверки вида, съёмка
    /// кадра): это названный случай, а не второй дом состояния — у человека оно всегда приходит
    /// доводом. Показывать и менять его окно может только через двери объекта.
    /// </summary>
    private PanelUpdateState _updateState = new();

    /// <summary>
    /// ЧТО РАЗДЕЛ ПОКАЗАЛ В ПРОШЛЫЙ РАЗ. Нужно не для красоты: ход подготовки поднимает
    /// <see cref="PanelUpdateState.Changed"/> на каждый прочитанный буфер куска (сотни раз за
    /// загрузку), и перерисовывать весь раздел — с разбором заметок к выпуску — на каждое такое
    /// сообщение значит делать лишнюю работу. Полоса хода обновляется всегда, а весь раздел —
    /// только когда сменилось занятость, готовность или строка состояния.
    /// </summary>
    private (bool Busy, UpdatePreparation? Prepared, string Status) _shownUpdate;

    private IReadOnlyList<WorkDirSuggestion> _suggestions = Array.Empty<WorkDirSuggestion>();

    /// <summary>
    /// ОТКУДА БЕРУТСЯ АДРЕСА МАШИНЫ. По умолчанию — НАСТОЯЩАЯ дверь
    /// (<see cref="RealNetworkAddresses.Read"/>): раздел показывает то, что есть у человека.
    /// Проверка подставляет своё ЗНАЧЕНИЕ той же двери (<c>RealNetworkAddresses.Test</c>), а не
    /// свой путь: тогда проверенное правило держится на продукте, а не на стенде.
    /// </summary>
    private Func<IReadOnlyList<RawAddress>> _phoneAddresses = RealNetworkAddresses.Read;

    /// <summary>
    /// Что делать с адресом ПЕРЕД ПОКАЗОМ. **У человека маски НЕТ** (<c>null</c>) — он видит свой
    /// адрес как есть, иначе не смог бы набрать его на телефоне руками. У кадра маска есть
    /// (`Program.MaskAddress`): кадр уезжает в README, и номер адреса машины в нём лишний
    /// (решение владельца 11.10.2026).
    ///
    /// ⚠️ Маска стоит ЗДЕСЬ, а не в двери адресов, и это не перестановка: замаскированный адрес
    /// перестал бы быть адресом (в нём буква), и правило выбора (<c>PhoneDecisions.Suitable</c>)
    /// отбросило бы его как негодный. **Маскируется ПОКАЗ, а не решение.**
    ///
    /// ⚠️ <c>null</c> — не «то же самое, что тождество», а отдельный ответ: по нему видно, что
    /// это НЕ прогон съёмки, и QR рисуется для настоящего адреса (`RenderPhoneQr`).
    /// </summary>
    private Func<string, string>? _phoneAddressMask;

    /// <summary>Идёт раскладка списка кандидатов: отличить выбор ЧЕЛОВЕКА от нашей же подстановки.</summary>
    private bool _phoneAddressRendering;

    /// <summary>Последний выпущенный код — для проверок без экрана (человеку он виден крупно).</summary>
    public string PhoneCode { get; private set; } = string.Empty;

    /// <summary>Человек тронул поле порта телефона — только тогда порт считается выбранным им.</summary>
    private bool _phonePortTouched;

    /// <summary>
    /// Идёт подстановка значений раздела «Телефон» при отрисовке. Один флаг на рубильник и порт:
    /// оба поднимают событие изменения, и без предохранителя окно записало бы в настройки то,
    /// что только что прочитало.
    /// </summary>
    private bool _phoneUpdating;

    /// <summary>Строка под кнопкой кода: «взяли новый» или «этот уже показан».</summary>
    private string _phoneCodeNote = string.Empty;

    private bool _updatingAutostart;
    private bool _updatingTheme;
    private bool _updatingPort;
    private bool _saved;

    /// <summary>
    /// Окно закрывает САМА панель (выход из панели, снятие значка), а не человек. Тогда вопроса
    /// о несохранённой правке быть не должно: человек уже сказал «Выход», и окно, отказавшееся
    /// закрыться, держало бы панель незакрытой. Дверь — <see cref="CloseQuietly"/>.
    /// </summary>
    private bool _closingQuietly;

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c>. Та же грабля, что у главного окна (`docs\STACK.md` §6, грабля 13).
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();

        // Окно живёт на нитке интерфейса — она и есть нитка панели для общей двери
        // (`Shell\UiThread`): подготовка обновления в разделе «Обновление» возвращается сюда
        // из фона, и дверь обязана знать, куда.
        UiThread.Remember();

        // ...и ЗДЕСЬ же она берётся на память самому окну (t13): ответы фоновой работы идут
        // через `UiThread.PostTo(_ui, …)` и не зависят от того, помнит ли дверь нитку.
        _ui = RequireUiThread(UiThread.Capture());

        Icon = PanelIcon.Window;

        // В разметке НЕТ ни одного текста: заголовок, подписи и подсказки ставятся здесь
        // из строк, а те — из словаря (`Localization\ru.json`). Правило одно на все экраны.
        Title = PanelStrings.SettingsWindowTitle;
        SettingsHeadingText.Text = PanelStrings.SettingsTitle;
        GeneralSectionTitle.Text = PanelStrings.SettingsGeneral;
        GeneralSubtitleText.Text = PanelStrings.SettingsGeneralSubtitle;
        WorkDirLabel.Text = PanelStrings.SettingsWorkDirLabel;
        WorkDirBox.PlaceholderText = PanelStrings.SettingsWorkDirPlaceholder;
        SuggestWorkDirButton.Content = PanelStrings.SettingsSuggestButton;
        PortLabel.Text = PanelStrings.SettingsPortLabel;

        // ⚠️ ПОЯСНЕНИЕ — НАД ПОЛЕМ, ПОДСКАЗКА — ПОД НИМ (норма GOV.UK: подсказка под полем это одна
        // короткая фраза без точки; всё длинное выносится над полем обычным текстом —
        // `40-forms.md` §3 и §11(б) п. 3). До 12.10.2026 всё лежало в подсказке, и читалка читала
        // её целиком при каждом входе в поле.
        PortExplainText.Text = PanelStrings.SettingsPortExplain;
        ThemeLabel.Text = PanelStrings.SettingsThemeLabel;
        LanguageLabel.Text = PanelStrings.SettingsLanguageLabel;
        LanguageHintText.Text = PanelStrings.SettingsLanguageAppliesNextStart;

        // Длинное — НАД полем, короткое — под ним (`40-forms.md` §3, §11(б) п. 3).
        LanguageExplainText.Text = PanelStrings.SettingsLanguageExplain;
        AutostartCheck.Content = PanelStrings.AutostartCheck;

        BalanceSectionTitle.Text = PanelStrings.SettingsBalanceSection;
        BalanceSectionSubtitleText.Text = PanelStrings.BalanceSectionSubtitle;
        ActiveAgentLabel.Text = PanelStrings.SettingsActiveAgentLabel;
        BalanceAutoCheck.Content = PanelStrings.SettingsBalanceAuto;
        BalancePeriodLabel.Text = PanelStrings.SettingsBalancePeriodLabel;
        BalanceWarnCheck.Content = PanelStrings.SettingsBalanceWarn;
        BalanceThresholdLabel.Text = PanelStrings.SettingsBalanceThresholdLabel;
        PeakNotifyLabel.Text = PanelStrings.SettingsPeakNotifyLabel;

        // Почему расписания пиков и таблицы цен здесь больше нет — сказано человеку словами,
        // а не пропажей: раздел, у которого что-то исчезло молча, читается как сломанный.
        // Дверь в окно «Пики и тарифы» стоит в ГЛАВНОЙ панели (решение владельца 28.09.2026).
        PeakWhereNoteText.Text = PanelStrings.SettingsPeakWhereNote;

        ServerSectionTitle.Text = PanelStrings.SettingsServerSection;
        CheckEnvironmentButton.Content = PanelStrings.SettingsCheckEnvironment;
        SaveButton.Content = PanelStrings.SettingsSave;

        // РАЗДЕЛ «ОБНОВЛЕНИЕ»: подписи ставятся здесь, подписи и подсказки — из словаря. Один
        // набор слов на одно действие — второго места с тем же ответом в панели нет
        // (отдельного окна обновления не стало 09.10.2026), и разойтись этим словам не с чем.
        UpdateSectionTitle.Text = PanelStrings.UpdateSectionTitle;
        UpdateSectionSubtitleText.Text = PanelStrings.UpdateSectionSubtitle;
        UpdateCheckButton.Content = PanelStrings.UpdateCheckNowButton;
        PanelToolTip.Set(UpdateCheckButton, PanelStrings.TipUpdateCheckNowButton);
        UpdateCheckButton.Click += (_, _) => CheckUpdateNow();

        UpdateGithubButton.Content = PanelStrings.UpdateOpenGithubButton;
        PanelToolTip.Set(UpdateGithubButton, PanelStrings.TipUpdateOpenGithubButton);
        UpdateGithubButton.Click += (_, _) => OpenUpdateOnGithub();

        UpdateSkipButton.Content = PanelStrings.UpdateSkipButton;
        PanelToolTip.Set(UpdateSkipButton, PanelStrings.TipUpdateSkipButton);
        UpdateSkipButton.Click += (_, _) => SkipUpdateVersion();

        UpdatePrepareButton.Content = PanelStrings.UpdatePrepareButton;
        PanelToolTip.Set(UpdatePrepareButton, PanelStrings.TipUpdatePrepareButton);
        UpdatePrepareButton.Click += (_, _) => PrepareUpdate();

        // БЛОК ДВИЖКА (этапы E3.2 и E3.3): две строки, РЯД ИЗ ДВУХ кнопок и откат отдельной тихой
        // строкой под ним (решение владельца 11.10.2026), плюс выжимка про версии и ссылка на выпуск.
        // Слова и решения живут в `Server\EngineUpdateFlow`, `Server\EngineUpdateDecisions`,
        // `Server\EngineRollbackDecisions` и `Server\EngineLines`, а окно только показывает и просит —
        // работа целиком за дверью `IEngineUpdateSection` (её даёт `App`).
        EngineBlockTitle.Text = PanelStrings.EngineBlockTitle;

        // ПОДСКАЗКА — ПО СОСТОЯНИЮ, а не «обе половины сразу» (находка F-1): до первой сверки
        // вторая половина (про непроверенную версию) не показывается, потому что её кнопки ещё нет.
        EngineHintText.Text = EngineHintParts(EngineVersionStanding.Unknown, freshButtonVisible: false);
        EngineUpdateButton.Content = PanelStrings.EngineUpdateButton;
        EngineInstallFreshButton.Content = PanelStrings.EngineUpdateFreshButton;
        EngineRollbackButton.Content = PanelStrings.EngineRollbackButtonFormat;
        PanelToolTip.Set(EngineUpdateButton, PanelStrings.TipEngineUpdateButton);
        PanelToolTip.Set(EngineInstallFreshButton, PanelStrings.TipEngineInstallFreshButton);
        PanelToolTip.Set(EngineRollbackButton, PanelStrings.TipEngineRollbackYes);

        EngineUpdateButton.Click += (_, _) => RunEngineUpdate(freshWanted: false);
        EngineInstallFreshButton.Click += (_, _) => RunEngineUpdate(freshWanted: true);
        EngineRollbackButton.Click += (_, _) => RunEngineRollback();

        // ССЫЛКА «ПОКАЗАТЬ ВСЕ ВЕРСИИ» (решение владельца 13.10.2026 №6): выжимка про версии
        // скрыта по умолчанию, чтобы карточка показывала РЕШЕНИЕ и две кнопки, а не хвост текста.
        EngineNotesShowAllButton.Content = PanelStrings.EngineNotesShowAll;
        PanelToolTip.Set(EngineNotesShowAllButton, PanelStrings.EngineNotesShowAll);
        EngineNotesShowAllButton.Click += (_, _) => ToggleEngineNotes();

        EngineNotesTitleText.Text = PanelStrings.EngineNotesTitle;
        EngineNotesEmptyText.Text = PanelStrings.EngineNotesEmpty;
        EngineReleasesButton.Content = PanelStrings.EngineReleasesLink;
        PanelToolTip.Set(EngineReleasesButton, PanelStrings.TipEngineReleasesLink);
        EngineReleasesButton.Click += (_, _) => OpenEngineReleases();

        RefreshEngineBlock();

        UpdateNotesTitleText.Text = PanelStrings.UpdateNotesTitle;
        UpdateNotesEmptyText.Text = PanelStrings.UpdateNotesEmpty;

        // РАЗДЕЛ «ТЕЛЕФОН»: слова ставятся здесь, как у всех разделов. Код сопряжения и адрес
        // сюда не пишутся — их показывает разметка крупно, а значения приходят при отрисовке.
        PhoneSectionTitle.Text = PanelStrings.PhoneSectionTitle;
        PhoneSectionSubtitleText.Text = PanelStrings.PhoneSectionSubtitle;
        PhoneLimitsText.Text = PanelStrings.PhoneLimits;
        PhoneAllowedCheck.Content = PanelStrings.PhoneAllowedCheck;
        PhonePortLabel.Text = PanelStrings.PhonePortLabel;
        PhoneForbiddenPortsText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.PhonePortForbiddenFormat,
            ServerDecisions.OwnerPort,
            ServerDecisions.RunFallbackPort);
        PhoneCodeButton.Content = PanelStrings.PhoneShowCodeButton;
        PanelToolTip.Set(PhoneCodeButton, PanelStrings.TipPhoneShowCodeButton);
        PhoneCodeButton.Click += (_, _) => ShowPhoneCode();
        PhoneDevicesTitle.Text = PanelStrings.PhoneDevicesTitle;
        PhoneDevicesCountText.Text = string.Empty;
        PhoneDevicesEmptyText.Text = PanelStrings.PhoneDevicesEmpty;
        PhoneAddressTitle.Text = PanelStrings.PhoneAddressTitle;
        PhoneAddressNoteText.Text = PanelStrings.PhoneAddressNote;
        PhoneSeenText.Text = string.Empty;

        // QR: подпись и слова ставятся здесь, а САМА КАРТИНКА — при отрисовке (`RenderPhoneQr`):
        // её рисует библиотека по строке адреса, и до отрисовки этой строки ещё нет.
        PhoneQrTitle.Text = PanelStrings.PhoneQrTitle;
        PhoneQrNoteText.Text = PanelStrings.PhoneQrNote;

        PhonePortBox.ValueChanged += (_, _) => OnPhonePortTyped();
        PhoneAddressCombo.SelectionChanged += (_, _) => OnPhoneAddressChosen();

        UpdateReplaceButton.Content = PanelStrings.UpdateReplaceButton;
        PanelToolTip.Set(UpdateReplaceButton, PanelStrings.TipUpdateReplaceButton);
        UpdateReplaceButton.Click += (_, _) => ReplaceUpdate();

        // РАЗДЕЛ «О ПРОГРАММЕ» (u14, 2.4.0) — СЕДЬМОЙ И ПОСЛЕДНИЙ. Прежде это было ОТДЕЛЬНОЕ ОКНО
        // (отдельное окно «О программе»), а в нижней строке стояла дверь в него. Решение владельца 27.09.2026
        // (`docs\DECISIONS.md:32` — «предлагаю перенести вообще в настройки») исполнено до конца:
        // окно удалено, ответ живёт разделом, и дверь меню значка открывает ЭТОТ раздел.
        //
        // Содержание — обязательный минимум по строке дизайн-лидера (`_team\240\u14-dizayn.md`):
        // версия (числовая, без хеша), что делает панель, ЖИВЫЕ ссылки, «Сообщить о проблеме…»,
        // MIT, ©. «Закрыть» из содержания убрано вместе с окном: закрытие делает само окно
        // настроек. `AboutTranslationNote`, `AboutDonateNote` и строка отказа оставлены —
        // это ответы, а не украшение.
        AboutSectionTitle.Text = PanelStrings.AboutSectionTitle;
        AboutVersionText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.AboutVersionFormat,
            AboutInfo.PanelVersion);
        AboutPurposeHeadingText.Text = PanelStrings.AboutPurposeHeading;
        AboutPurposeText.Text = PanelStrings.AboutPurpose;
        AboutLinksHeadingText.Text = PanelStrings.AboutLinksHeading;
        AboutRepoLinkButton.Content = PanelStrings.AboutRepoLink;
        AboutDonateLinkButton.Content = PanelStrings.AboutDonateLink;
        AboutDonateNoteText.Text = PanelStrings.AboutDonateNote;
        AboutTranslationNoteText.Text = PanelStrings.AboutTranslationNote;
        AboutIssueHeadingText.Text = PanelStrings.AboutIssueHeading;
        AboutIssueNoteText.Text = PanelStrings.AboutIssueNote;
        AboutIssueButton.Content = PanelStrings.AboutIssueButton;
        AboutLicenseText.Text = PanelStrings.AboutLicense;
        AboutCopyrightText.Text = PanelStrings.AboutCopyright;

        AboutRepoLinkButton.Click += (_, _) => OpenAboutLink(ProductLinks.RepositoryUrl);
        AboutDonateLinkButton.Click += (_, _) => OpenAboutLink(ProductLinks.Donate);

        // Дверь в окно «Сообщить о проблеме» даёт ВЛАДЕЛЕЦ окна (App) — разделу не нужны
        // ни журнал панели, ни состояние сервера. Без двери блок скрыт: кнопка в никуда хуже
        // отсутствующей кнопки. Перерисовка — здесь же, иначе блок остался бы скрытым навсегда.
        AboutIssueButton.Click += (_, _) => _openIssue?.Invoke();

        PanelToolTip.Set(AboutRepoLinkButton, PanelStrings.TipRepoLinkButton);
        PanelToolTip.Set(AboutDonateLinkButton, PanelStrings.TipDonateLinkButton);
        PanelToolTip.Set(AboutIssueButton, PanelStrings.TipAboutIssueButton);

        RenderAbout();

        // ДВЕРЬ В ОБНОВЛЕНИЕ ПАНЕЛИ здесь БОЛЬШЕ НЕТ — обновление стало РАЗДЕЛОМ списка слева
        // (решение владельца 28.09.2026: «это будет просто нормальный пункт меню как и остальные
        // во вкладке настройки, может быть просто „обновления“»). Подписи раздела и его кнопок —
        // ниже, вместе с остальными разделами: второго набора слов для того же раздела быть
        // не должно.

        ThemeBox.ItemsSource = new[]
        {
            PanelStrings.SettingsThemeSystem,
            PanelStrings.SettingsThemeLight,
            PanelStrings.SettingsThemeDark,
        };

        // Порядок языков — тот же, что у значений в файле настроек (`PanelSettings.LanguageValues`),
        // и включает «как в системе»: список из трёх языков без него заставлял бы человека
        // выбирать между «русским» и «как в системе» в одном и том же окне, ничего не выбирая.
        LanguageBox.ItemsSource = new[]
        {
            PanelStrings.SettingsLanguageAuto,
            PanelStrings.SettingsLanguageRussian,
            PanelStrings.SettingsLanguageEnglish,
            PanelStrings.SettingsLanguageChinese,
        };

        AgentBox.ItemsSource = AgentCatalog.All.Select(agent => agent.Title).ToArray();

        // Раздел «Копии» — новый, и его тексты берутся из строк, а не из разметки: правило для
        // новых экранов (AGENTS.md, 25.09.2026) — подписи живут в одном месте и переживут
        // этап локализации без поиска текста по разметке.
        BackupSectionTitle.Text = PanelStrings.BackupSectionTitle;
        BackupSectionSubtitleText.Text = PanelStrings.BackupSectionSubtitle;
        BackupFolderLabel.Text = PanelStrings.BackupFolderLabel;

        // Расписание автокопий и предел хранения — НАСТРОЙКИ, и потому они здесь, а не в окне
        // копий: решение владельца «настройка — настройкой, действие — действием». Рядом стоит
        // строка о копии на ходу: человек обязан узнать её там, где ВКЛЮЧАЕТ расписание.
        BackupScheduleCheck.Content = PanelStrings.SettingsBackupScheduleCheck;
        BackupEveryLabel.Text = PanelStrings.SettingsBackupEveryLabel;
        BackupKeepLabel.Text = PanelStrings.SettingsBackupKeepLabel;
        BackupScheduleNoteText.Text = PanelStrings.SettingsBackupScheduleNote;
        BackupKeepNoteText.Text = PanelStrings.SettingsBackupKeepNote;
        BackupLiveNoteText.Text = PanelStrings.SettingsBackupLiveNote;

        // Состав копии: ключи. Раздел появился 26.09.2026 вместе с решением владельца «ключи снова
        // входят в копию, как в v1», и живёт он здесь потому, что это НАСТРОЙКА: окно копий
        // показывает то же значение, но меняет его через владельца настроек.
        KeysSectionTitle.Text = PanelStrings.SettingsKeysSection;
        BackupKeysCheck.Content = PanelStrings.BackupKeysCheck;
        BackupKeysWarningText.Text = PanelStrings.BackupKeysWarning;
        BackupKeysDirsLabel.Text = PanelStrings.SettingsKeysDirsLabel;
        BackupKeysDirsHint.Text = PanelStrings.SettingsKeysDirsNote;

        // ⚠️ Строк «копии для передачи» здесь больше НЕТ: решением владельца 27.09.2026 (п. 11)
        // передача — разовое действие в окне копий, а не настройка. Своего текста у этого окна
        // о ней не осталось вовсе (см. комментарий в `SettingsWindow.axaml`).

        // Закрытие окна с несохранённой правкой обязано СПРОСИТЬ (жалоба владельца, п. 16
        // `docs\DESIGN.md`). Подписка стоит здесь, а не в Attach: закрыть окно можно и до того,
        // как его связали с настройками, и тогда спрашивать не о чем — это решает сам обработчик.
        Closing += OnClosing;

        // РАЗДЕЛЫ — СПИСКОМ СЛЕВА (решение владельца 27.09.2026, п. 21). Подписи берутся из тех же
        // строк, что и заголовки разделов: второго набора имён для тех же четырёх разделов быть
        // не должно.
        //
        // ⚠️ **ItemsSource — по-прежнему список СТРОК**, и это не «пока так»: на нём стоят проверки
        // списка разделов (`SettingsSectionsTests`, `WindowContentTests`), и подмена его записями
        // «строка со значком» сломала бы им не разметку, а смысл. Значок — дело ПРЕДСТАВЛЕНИЯ
        // строки (`ItemTemplate`), а не данных.
        var titles = SectionTitles();

        SectionsList.ItemsSource = titles;
        SectionsList.ItemTemplate = new FuncDataTemplate<string>((title, _) => SectionRow(title, titles));
        SectionsList.SelectedIndex = GeneralSection;

        // Переключение раздела ТОЛЬКО показывает и прячет — и ничего больше. Здесь НЕ зовётся
        // Render(): он перечитывает поля из настроек, то есть стёр бы несохранённую правку
        // ровно в тот момент, когда человек уходит посмотреть другой раздел.
        SectionsList.SelectionChanged += (_, _) => ShowSection(SectionsList.SelectedIndex);
        ShowSection(GeneralSection);

        // ПОДСКАЗКИ КНОПОК — у каждой кнопки окна: что произойдёт по нажатию. У недоступной
        // кнопки подсказка называет причину (это включает PanelToolTip).
        PanelToolTip.Set(SuggestWorkDirButton, PanelStrings.TipSuggestWorkDirButton);
        PanelToolTip.Set(CheckEnvironmentButton, PanelStrings.TipCheckEnvironmentButton);
        PanelToolTip.Set(SaveButton, PanelStrings.TipSaveSettingsButton);

        // ВТОРИЧНАЯ КНОПКА ШАПКИ — «Открыть файл настроек»: показать человеку сам файл, который
        // панель правит (он его правит и руками). Открывается тем же путём, что ссылка агента:
        // через общую дверь `AgentBrowser` и с проверкой права (см. <see cref="OpenSettingsFolder"/>).
        OpenFolderButton.Content = PanelStrings.SettingsOpenFolderButton;
        PanelToolTip.Set(OpenFolderButton, PanelStrings.TipSettingsOpenFolderButton);
        OpenFolderButton.Click += (_, _) => OpenSettingsFolder();

        // ⚠️ СВОЕГО КРЕСТИКА У ОКНА НЕТ (замечание владельца 29.09.2026). Закрывает окно
        // СИСТЕМНАЯ кнопка заголовка, и путь у неё тот же — `OnClosing`, который спрашивает
        // про несохранённую правку. Проверка этого пути есть: `SettingsUnsavedTests`.
        // Второй, «свой» крестик стоял рядом с «Открыть файл настроек» и читался третьей
        // кнопкой панели инструментов, делая то же самое.

        // ⚠️ ДВЕРИ «О ПРОГРАММЕ» В НИЖНЕМ РЯДУ БОЛЬШЕ НЕТ (u14, 2.4.0): она стала разделом.
        // Просьбу о двери в отчёт подаёт сам раздел (см. `AttachIssueDoor`), а не кнопка окна.
    }

    /// <summary>
    /// Строка списка разделов: значок и подпись.
    ///
    /// Зачем значок (решение владельца 27.09.2026, шаг 2 вида — «стиль как в harness»): четыре
    /// раздела одним столбиком слов читаются медленно, глаз ищет строку по форме. Значок —
    /// векторная геометрия (<see cref="PanelGlyph"/>), без картинок и шрифтовых значков: файла
    /// рядом с панелью не лежит, а шрифтовой значок на чужой машине окажется пустым квадратом.
    ///
    /// Номер раздела берётся у СПИСКА ПОДПИСЕЙ (того же, что ушёл в <c>ItemsSource</c>), а не
    /// подбирается по тексту строки: порядок значков обязан совпадать с порядком разделов,
    /// и второй способ его определить однажды разошёлся бы с первым.
    /// </summary>
    private static Control SectionRow(string title, IReadOnlyList<string> titles)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var index = -1;

        for (var i = 0; i < titles.Count; i++)
        {
            if (!string.Equals(titles[i], title, StringComparison.Ordinal)) continue;

            index = i;
            break;
        }

        row.Children.Add(new PathIcon
        {
            Data = PanelGlyph.Section(index),
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // ⚠️ ПОДПИСЬ ПЕРЕНОСИТСЯ, И ЭТО ЧАСТЬ ЛЕЧЕНИЯ ЗАМЕЧАНИЯ ВЛАДЕЛЬЦА (27.09.2026: пункт
        // «Резервное копирование» не вмещался в колонку разделов). Ширина колонки увеличена
        // до 240 (разметка окна), а это — вторая половина средства: подпись, которой места
        // всё-таки не хватило (другой язык, крупный системный шрифт), обязана перенестись
        // на вторую строку, а не обрезаться. Высота строки списка автоматическая, поэтому
        // перенос её увеличивает сам.
        //
        // ⚠️ Перенос БЕЗ ограничения ширины бесполезен: `MaxWidth` здесь не ставится намеренно —
        // ширину даёт сама колонка списка, и второй её источник разошёлся бы с первым.
        row.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return row;
    }

    /// <summary>
    /// Открыть каталог файла настроек — через ОБЩУЮ дверь (<see cref="AgentBrowser.TryOpenFolder"/>)
    /// и по тому же праву, что у панели на запись настроек: чужие каталоги прогон проверки
    /// не открывает, а отказ называет причину словами.
    ///
    /// Решение о праве живёт НЕ здесь, а у владельца настроек (<c>SettingsController.Writable</c>):
    /// окно не знает, чей это прогон, и второй такой ответ разошёлся бы с первым.
    ///
    /// ⚠️ **УДАЧА В ОКНЕ НЕ ПИШЕТСЯ НИЧЕМ** (решение дирижёра 27.09.2026 по замечанию владельца,
    /// который смотрел панель живьём: *«Я думаю это лишнее, видимо субагент добавил»*). Прежде
    /// в строке состояния вставала та же фраза, что уходит в журнал, — «каталог настроек открыт
    /// по щелчку человека». Человеку она не отвечает ни на один его вопрос: проводник он и так
    /// видит открытым, а слова «по щелчку человека» описывают способ вызова, а не результат.
    /// Поэтому строка <see cref="PanelStrings.SettingsFolderOpenedLog"/> осталась ровно там, где
    /// она полезна, — в ЖУРНАЛЕ (<see cref="AgentBrowser.TryOpenFolder"/> её туда и пишет),
    /// а окно при удаче очищает строку состояния.
    ///
    /// **Отказ по-прежнему виден словами**, и это не мелочь: молчащая кнопка читается как
    /// «панель не поняла», и человек жмёт её снова.
    /// </summary>
    public void OpenSettingsFolder()
    {
        if (_settings is null)
        {
            // Окно построено без владельца настроек (проверки вида): открывать нечего,
            // и сказать об этом надо словами, а не молчанием кнопки.
            StatusText.Text = PanelStrings.SettingsFolderMissing;
            return;
        }

        // Удача — пустая строка состояния; причина отказа — его собственные слова.
        StatusText.Text = _settings.OpenSettingsFolder(out var error) ? string.Empty : error;
    }

    /// <summary>
    /// ДВЕРЬ В ОТЧЁТ О ПРОБЛЕМЕ, данная владельцем окна (<see cref="AttachIssueDoor"/>).
    /// Пусто — кнопки нет: раздел без двери не должен показывать кнопку в никуда.
    ///
    /// ⚠️ Ссылку даёт `App`: отчёт собирается из журнала панели и состояния сервера, а раздел
    /// «О программе» их не знает и знать не должен. Порядок тот же, что был у удалённого окна.
    /// </summary>
    private Action? _openIssue;

    /// <summary>
    /// Привязать дверь «Сообщить о проблеме» и перерисовать раздел.
    /// ⚠️ Перерисовка ЗДЕСЬ обязательна: конструктор уже вызвал <see cref="RenderAbout"/>, когда
    /// двери ещё не было, — без этого вызова блок оставался бы скрытым НАВСЕГДА.
    /// </summary>
    public void AttachIssueDoor(Action? openIssue)
    {
        _openIssue = openIssue;
        RenderAbout();
    }

    /// <summary>
    /// Разложить по строкам то, что РЕШЕНО предикатами: какие ссылки показывать и показывать ли
    /// строку о машинном переводе. Зовётся при построении окна, при выдаче двери и проверками —
    /// второго места, где эти решения принимаются, в окне нет.
    /// </summary>
    public void RenderAbout()
    {
        AboutRepoLinkButton.IsVisible = ProductLinks.ShouldShow(ProductLinks.RepositoryUrl);

        AboutDonateLinkButton.IsVisible = ProductLinks.ShouldShow(ProductLinks.Donate);
        // Пояснение про донаты живёт ПОД ссылкой и без неё выглядело бы брошенным.
        AboutDonateNoteText.IsVisible = AboutDonateLinkButton.IsVisible;

        AboutTranslationNoteText.IsVisible = AboutInfo.ShowsTranslationNote(
            DshPanel.Localization.Loc.Language);

        // Блок «Сообщить о проблеме» показывается, только если у раздела ЕСТЬ дверь.
        AboutIssuePanel.IsVisible = AboutInfo.ShowIssueBlock(_openIssue is not null);
    }

    /// <summary>
    /// Щёлкнуть по ссылке раздела — открыть её общей дверью `<see cref="ProductLinks.Open"/>`;
    /// ошибка идёт СТРОКОЙ здесь же, а не отдельным окном ради одной строки.
    /// </summary>
    private void OpenAboutLink(string url)
    {
        var error = AboutOpenLinkForTests is not null
            ? AboutOpenLinkForTests(url)
            : ProductLinks.Open(url);

        AboutLinkStatusText.Text = error.Length == 0
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.AboutLinkFailedFormat,
                error);

        AboutLinkStatusText.IsVisible = AboutLinkStatusText.Text!.Length > 0;
    }

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем отвечать на «открой ссылку». В жизни ответ даёт
    /// <see cref="ProductLinks.Open"/> и он же открывает браузер человека; проверка показать
    /// браузер не имеет права. У обычного запуска это свойство <c>null</c>.
    /// </summary>
    internal Func<string, string>? AboutOpenLinkForTests { get; set; }

    /// <summary>Щёлкнуть по ссылке донатов — так же, как это делает человек (для проверок).</summary>
    public void ClickAboutDonate() =>
        AboutDonateLinkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Номера разделов. Числа, а не строки: по строкам решение о разделе зависело бы от языка,
    /// и «Общее» на китайском перестало бы быть первым.
    ///
    /// «Резервное копирование» открыто наружу: это САМЫЙ ДЛИННЫЙ раздел, и съёмка кадра
    /// фотографирует именно его — только на нём видно и полосу прокрутки, и то, что она
    /// не наезжает на поля (жалоба владельца 27.09.2026).
    /// </summary>
    public const int GeneralSection = 0;

    public const int BackupSection = 1;

    /// <summary>Раздел «Баланс и тариф» — открыт проверкам: в нём таблица окон пика.</summary>
    public const int BalanceSection = 2;

    /// <summary>Раздел «Сервер» — открыт наружу: в нём отчёт окружения, и его снимает кадр.</summary>
    public const int ServerSection = 3;

    /// <summary>
    /// Раздел «Обновление» — открыт наружу: проверки обязаны уметь показать РОВНО его (в нём
    /// и ряд кнопок, который обязан влезать в одну строку).
    /// </summary>
    public const int UpdateSection = 4;

    /// <summary>
    /// Раздел «Телефон» — открыт наружу: в нём рубильник, код сопряжения, список подключённых
    /// и адрес своей сети. Кадр раздела (`--shot settings-phone`) снимается именно с него.
    /// </summary>
    public const int PhoneSection = 5;

    /// <summary>
    /// Раздел «О программе» — СЕДЬМОЙ и ПОСЛЕДНИЙ (u14, 2.4.0). Открыт наружу: проверка «дверь
    /// меню значка ведёт ИМЕННО сюда» идёт прогоном, а кадр `--shot settings-about` снимает
    /// ровно этот раздел.
    /// </summary>
    public const int AboutSection = 6;

    /// <summary>Подписи СЕМИ разделов — в порядке списка слева.</summary>
    private static string[] SectionTitles() => new[]
    {
        PanelStrings.SettingsGeneral,
        PanelStrings.BackupSectionTitle,
        PanelStrings.SettingsBalanceSection,
        PanelStrings.SettingsServerSection,
        PanelStrings.UpdateSectionTitle,
        PanelStrings.PhoneSectionTitle,

        // «О программе» — последняя, потому что это справка, а не настройка. Нормы порядка
        // в банке нет: решение наше и названо в строке дизайн-лидера u14.
        PanelStrings.AboutSectionTitle,
    };

    /// <summary>Панели разделов — в том же порядке, что и подписи.</summary>
    private StackPanel[] SectionPanels() => new[]
    {
        GeneralSectionPanel,
        BackupSectionPanel,
        BalanceSectionPanel,
        ServerSectionPanel,
        UpdateSectionPanel,
        PhoneSectionPanel,
        AboutSectionPanel,
    };

    /// <summary>
    /// Показать ОДИН раздел и спрятать остальные. Содержимое всех четырёх построено заранее
    /// и живёт в окне целиком: пересборка на переключении потеряла бы введённое, а над окном
    /// стоит защита «несохранённая правка» (п. 16), которая опирается на те же поля.
    ///
    /// Возвращает номер показанного раздела — вызывающему (и проверкам) видно, что именно
    /// произошло, а не «какая-то из четырёх панелей стала видимой».
    /// </summary>
    public int ShowSection(int index)
    {
        var panels = SectionPanels();
        var chosen = index >= 0 && index < panels.Length ? index : GeneralSection;

        for (var i = 0; i < panels.Length; i++) panels[i].IsVisible = i == chosen;

        // Выбранный раздел обязан быть виден и в списке: подсветка — единственный признак,
        // по которому человек понимает, чей это содержимое справа.
        if (SectionsList.SelectedIndex != chosen) SectionsList.SelectedIndex = chosen;

        // ⚠️ РАЗДЕЛ «ТЕЛЕФОН» ОБНОВЛЯЕТСЯ ПРИ ВХОДЕ И ПОКА ОН ВИДЕН (решение владельца 12.10.2026).
        //
        // **От какого случая.** `ShowSection` только переключал видимость, часов в окне не было
        // вовсе, а `RenderPhoneSection` звался при загрузке и по действиям ВНУТРИ панели. Телефон
        // сопрягается СНАРУЖИ — и человек, открыв раздел, видел «устройств нет», пока не тронет
        // что-нибудь руками. Это и есть дефект шва: правда о работе меняется не от наших нажатий.
        ShowPhoneSection(chosen == PhoneSection);

        return chosen;
    }

    /// <summary>
    /// ЧАСЫ РАЗДЕЛА «ТЕЛЕФОН»: тихо перечитывают раздел, ПОКА ОН ВИДЕН, и молчат, когда ушли.
    ///
    /// ⚠️ **Часы заводит дверь <see cref="UiThread.NewTimer"/>, а не `new DispatcherTimer`:**
    /// второй спрашивает `Dispatcher.UIThread` и тем самым захватывает нитку интерфейса у прогона
    /// проверок (замер 09.10.2026, стерегут ворота `UiThreadTests`). Нет нитки — часов нет,
    /// и раздел просто не обновляется сам (это и есть поведение без окна).
    ///
    /// ⚠️ **Обновляется ТОЛЬКО раздел «Телефон»** (`RenderPhoneSection`), а не всё окно: полное
    /// `Render` переписало бы поля, которые человек в это время правит.
    /// </summary>
    private void ShowPhoneSection(bool visible)
    {
        if (!visible)
        {
            _phoneTimer?.Stop();
            return;
        }

        RenderPhoneSection();

        if (_phoneTimer is null)
        {
            _phoneTimer = UiThread.NewTimer(TimeSpan.FromSeconds(2));

            if (_phoneTimer is not null) _phoneTimer.Tick += (_, _) => RenderPhoneSection();
        }

        _phoneTimer?.Start();
    }

    /// <summary>Часы раздела «Телефон» — заводятся один раз и живут вместе с окном.</summary>
    private DispatcherTimer? _phoneTimer;

    /// <summary>
    /// ПОКАЗАТЬ РАЗДЕЛ «ОБНОВЛЕНИЕ» — уговор <see cref="IUpdateSectionWindow"/>, которым
    /// пользуется дверь связки `PanelShell.OpenUpdateSection` (пункт меню значка «Обновление панели…»).
    ///
    /// ⚠️ Номер раздела живёт ЗДЕСЬ и только здесь (<see cref="UpdateSection"/>): связка не знает,
    /// какое окно ей вернут, и второй копии этого числа в панели быть не должно — разойдясь, дверь
    /// открывала бы соседний раздел.
    /// </summary>
    public int ShowUpdateSection() => ShowSection(UpdateSection);

    /// <summary>
    /// ПОКАЗАТЬ РАЗДЕЛ «О ПРОГРАММЕ» — уговор <see cref="IAboutSectionWindow"/>, которым
    /// пользуется дверь связки `PanelShell.OpenAboutSection` (пункт меню значка «О программе…»).
    ///
    /// ⚠️ Номер раздела живёт ЗДЕСЬ и только здесь (<see cref="AboutSection"/>): связка не знает,
    /// какое окно ей вернут, и второй копии этого числа в панели быть не должно.
    /// </summary>
    public int ShowAboutSection() => ShowSection(AboutSection);

    /// <summary>Что сейчас показывает окно — для проверок без экрана: номер видимого раздела.</summary>
    public int VisibleSection
    {
        get
        {
            var panels = SectionPanels();

            for (var i = 0; i < panels.Length; i++)
            {
                if (panels[i].IsVisible) return i;
            }

            return -1;
        }
    }

    /// <summary>Было ли сохранение, которое действительно прошло. Нужно вызывающему — и проверкам.</summary>
    public bool Saved => _saved;

    // ------------------------------------------------------------------ «О программе»: то, что видно

    /// <summary>Строка версии в разделе «О программе».</summary>
    public string AboutVersionLine => AboutVersionText.Text ?? string.Empty;

    /// <summary>«Что делает панель» — ответ на вопрос, а не визитка.</summary>
    public string AboutPurposeLine => AboutPurposeText.Text ?? string.Empty;

    /// <summary>Строка лицензии.</summary>
    public string AboutLicenseLine => AboutLicenseText.Text ?? string.Empty;

    /// <summary>Строка авторского права.</summary>
    public string AboutCopyrightLine => AboutCopyrightText.Text ?? string.Empty;

    /// <summary>Строка отказа при открытии ссылки: пусто или причина.</summary>
    public string AboutStatus => AboutLinkStatusText.Text ?? string.Empty;

    /// <summary>Видна ли строка ссылки на репозиторий.</summary>
    public bool AboutRepoVisible => AboutRepoLinkButton.IsVisible;

    /// <summary>Видна ли ссылка на донаты (и пояснение под ней).</summary>
    public bool AboutDonateVisible => AboutDonateLinkButton.IsVisible;

    /// <summary>Видна ли строка о машинном переводе.</summary>
    public bool AboutTranslationNoteVisible => AboutTranslationNoteText.IsVisible;

    /// <summary>Виден ли блок «Сообщить о проблеме» (он же — есть ли дверь).</summary>
    public bool AboutIssueVisible => AboutIssuePanel.IsVisible;

    /// <summary>
    /// Есть ли в окне несохранённая правка — то, о чём окно обязано спросить при закрытии.
    /// Решение принимает ЧИСТАЯ функция (<see cref="SettingsCloseDecisions.HasUnsavedChanges"/>):
    /// в окне его нечем проверить, а там оно проверяется прогоном, который умеет падать.
    /// </summary>
    public bool HasUnsavedChanges() =>
        _settings is not null && SettingsCloseDecisions.HasUnsavedChanges(PendingSettings(), _settings.Settings);

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем отвечать на вопрос «сохранить изменения?».
    /// В жизни ответ даёт человек в <see cref="UnsavedChangesWindow"/>, и в проверке без экрана
    /// этот диалог всплыл бы сам и ответил не то, что решил бы человек. У обычного запуска
    /// это свойство <c>null</c>.
    /// </summary>
    internal Func<UnsavedChoice>? UnsavedChoiceForTests { get; set; }

    /// <summary>
    /// Закрыть окно, не спрашивая о несохранённой правке (<see cref="IQuietClose"/>).
    /// Так его закрывает панель при выходе: человек сказал «Выход», и вопрос был бы обращён
    /// к тому, кто уже ушёл.
    /// </summary>
    public void CloseQuietly()
    {
        _closingQuietly = true;
        Close();
    }

    /// <summary>
    /// КРЕСТИК С НЕСОХРАНЁННОЙ ПРАВКОЙ СПРАШИВАЕТ, А НЕ ТЕРЯЕТ ЕЁ.
    ///
    /// До этой работы окно закрывалось молча, и введённое пропадало без единого слова: сохраняла
    /// только кнопка «Сохранить». Жалоба владельца (п. 16 `docs\DESIGN.md`) — дословно про это.
    ///
    /// ⚠️ **Первое закрытие ОТМЕНЯЕТСЯ, и окно закрывается вторым.** Спросить человека можно
    /// только модальным окном, а модальное окно в Avalonia не блокирует нитку — оно возвращает
    /// задачу. Закрытие окна — событие СИНХРОННОЕ, и ждать в нём ответа значило бы заморозить
    /// интерфейс навсегда. Поэтому: отменяем, спрашиваем, и по ответу закрываем окно сами.
    ///
    /// Отказ в сохранении (настройки записать не удалось) закрытие НЕ продолжает: окно остаётся
    /// открытым с правкой на месте и с честной строкой о причине — потерять правку молча нельзя,
    /// ради этого всё и заведено.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingQuietly || _settings is null) return;

        if (!HasUnsavedChanges()) return;

        // Первое закрытие — только вопрос. Окно пока НЕ закрываем: человек ещё не ответил.
        e.Cancel = true;

        var choice = await AskAboutUnsavedChanges();

        if (choice == UnsavedChoice.Cancel) return;

        if (choice == UnsavedChoice.Save && !SavePending()) return;

        // Ответ получен — закрываем по-настоящему, и второй раз не спрашиваем.
        CloseQuietly();
    }

    /// <summary>Спросить про несохранённую правку. Возвращает ответ человека, а не решение окна.</summary>
    private async Task<UnsavedChoice> AskAboutUnsavedChanges()
    {
        // Проверка без экрана: диалог показать некому, ответ задан проверкой.
        if (UnsavedChoiceForTests is not null) return UnsavedChoiceForTests();

        var dialog = new UnsavedChangesWindow();

        await dialog.ShowDialog(this);

        return dialog.Choice;
    }

    /// <summary>Записать то, что стоит в полях, и сказать об исходе. Возвращает, записалось ли.</summary>
    private bool SavePending()
    {
        if (_settings is null) return false;

        _saved = _settings.Save(PendingSettings());
        Render();

        StatusText.Text = _settings.Writable
            ? (_saved ? PanelStrings.SettingsSaved : PanelStrings.SettingsSaveFailed)
            : PanelStrings.SettingsReadOnlyNote;

        return _saved;
    }

    /// <summary>Что сейчас в полях — для проверок без экрана.</summary>
    public PanelSettings PendingSettings() => new()
    {
        ServerWorkingDir = WorkDirBox.Text ?? string.Empty,

        // Порт отдаётся уже приведённым: подсказка под полем обещает человеку, что будет взят
        // допустимый порт, и обещание обязано исполниться в том же нажатии, а не когда-нибудь
        // потом в хранилище.
        ServerPort = PanelSettings.NormalizeServerPort(
            (int)(PortBox.Value ?? ServerDecisions.DefaultServerPort)),

        // ⚠️ ПРИЗНАК «порт выбран человеком» ставится ЗДЕСЬ — нажатием «Сохранить» и нигде больше.
        // Без него панель считала бы 3081 в файле остатком прежнего умолчания и переводила бы его
        // на 3080 при КАЖДОМ чтении, то есть переписывала бы выбор человека руками (решение
        // владельца 26.09.2026). Проверка — «Сохранение_из_окна_помечает_порт_выбранным».
        ServerPortChosen = true,
        Theme = PanelSettings.ThemeAt(ThemeBox.SelectedIndex),

        // Язык: значение уходит в файл настроек и применяется при СЛЕДУЮЩЕМ запуске панели —
        // так решено дирижёром, и об этом человеку сказано подписью под списком. Пересобирать
        // окна и меню на ходу — отдельная работа, и обещать её молчанием нельзя.
        Language = PanelSettings.LanguageAt(LanguageBox.SelectedIndex),
        ActiveAgent = AgentCatalog.IdAt(AgentBox.SelectedIndex),
        BalanceAutoRefresh = BalanceAutoCheck.IsChecked == true,
        BalanceRefreshMinutes = (int)(RefreshMinutesBox.Value ?? PanelSettings.RefreshMinutesMin),
        BalanceWarnEnabled = BalanceWarnCheck.IsChecked == true,
        BalanceWarnThreshold = ThresholdBox.Value ?? 0,
        PeakNotifyMinutes = (int)(PeakNotifyBox.Value ?? 0),

        // Пустое поле означает «умолчание v1», и в файл уходит именно пустое: подставленное
        // значение перестало бы быть умолчанием, и вернуться к нему человек уже не смог бы.
        BackupFolder = PanelSettings.NormalizeFolder(BackupFolderBox.Text),

        // Расписание и хранение — поля ЭТОГО окна, поэтому берутся из полей и приводятся к границам
        // сразу: подсказка обещает человеку, что «100000 часов» станет неделей, и обещание обязано
        // исполниться в том же нажатии, а не когда-нибудь потом в хранилище (так же у порта).
        BackupScheduleEnabled = BackupScheduleCheck.IsChecked == true,
        BackupEveryHours = PanelSettings.ClampBackupHours((int)(BackupEveryBox.Value ?? 24)),
        BackupKeepCount = PanelSettings.ClampBackupKeep((int)(BackupKeepBox.Value ?? 2)),

        // ⚠️ Режим объёма и лишние имена — поля ОКНА КОПИЙ, и здесь они проходят НЕТРОНУТЫМИ.
        // Окно настроек пишет файл целиком, и поле, которого оно не знает, обнулялось бы каждым
        // нажатием «Сохранить»: человек выбрал «полный» в окне копий, зашёл в настройки, нажал
        // «Сохранить» — и режим молча вернулся к автоматическому. Та же грабля, что с запомненным
        // согласием на сервер (приёмка 26.09.2026); проверка — «Окно_настроек_не_забывает_режим_и_лишние_имена».
        BackupScope = _settings?.Settings.BackupScope ?? BackupScopeDecisions.Auto,
        BackupExtraExclusions = _settings?.Settings.BackupExtraExclusions ?? new List<string>(),

        // Ключи: разрешение и свои каталоги. Сюда же пишет и окно копий — через владельца
        // настроек, — поэтому файл остаётся единственным местом правды для обоих окон.
        BackupWithKeys = BackupKeysCheck.IsChecked == true,
        BackupKeyDirs = PanelSettings.NormalizeKeyDirs(Lines(BackupKeysDirsBox.Text)),

        // ⚠️ Мёртвое поле «копия для передачи» здесь НЕ читается и не пишется: передача — разовое
        // действие окна копий (п. 11 `docs\DESIGN.md`), и настройки о ней не знают. К `false` его
        // приводит общая дверь настроек (`SettingsStore.Clean`) — и при чтении, и при записи.

        // Запомненное согласие на найденный сервер (решение владельца 26.09.2026) в окне НЕ
        // ПОКАЗЫВАЕТСЯ — но обязано пройти через сохранение НЕТРОНУТЫМ: окно пишет настройки
        // целиком, и поле, которого оно не знает, обнулялось бы каждым нажатием «Сохранить»,
        // то есть человек молча терял бы своё «да» и панель начинала бы спрашивать заново.
        // Нашла приёмка 26.09.2026; проверка — «Сохранение_из_окна_не_забывает_согласие».
        AdoptedServerPort = _settings?.Settings.AdoptedServerPort ?? 0,

        // След работы панели по странице цен (когда разбирали и какие окна оттуда взяли) проходит
        // через сохранение НЕТРОНУТЫМ — по той же причине, что согласие на сервер: окно пишет
        // настройки ЦЕЛИКОМ, и поле, которого оно не знает, обнулялось бы каждым нажатием
        // «Сохранить». Тогда суточное правило разбора сбрасывалось бы, а расписание со страницы
        // молча заменялось бы расписанием профиля. Проверка —
        // «Сохранение_из_окна_не_забывает_след_разбора_цен».
        PricingCheckedAt = _settings?.Settings.PricingCheckedAt ?? string.Empty,
        PricingPeakWindows = _settings?.Settings.PricingPeakWindows ?? string.Empty,

        // ⚠️ САМА ТАБЛИЦА ЦЕН — и это НЕ «на всякий случай», а закрытый дефект 29.09.2026.
        // Её здесь не было, и одного пропуска хватило на ДВА разных отказа сразу:
        //   * «Сохранить» в любом разделе настроек стирало прочитанные цены — панель снова
        //     показывала пустое место («обновлял вчера, сегодня снова пусто»);
        //   * окно считало цены НЕСОХРАНЁННОЙ ПРАВКОЙ (сравнение идёт по всем полям), поэтому
        //     спрашивало «сохранить изменения?» у человека, который открыл настройки и не тронул
        //     ничего, — а такой вопрос перестают читать, и однажды теряется настоящая правка.
        // Проверка — «Сохранение_из_окна_не_забывает_прочитанные_цены» (падает на обоих отказах).
        PricingLast = _settings?.Settings.PricingLast ?? string.Empty,

        // Следы проверки обновления (когда проверяли, что нашлось, какие заметки) — по той же
        // причине и тем же путём, что след разбора цен: окно пишет настройки ЦЕЛИКОМ, и поле,
        // которого оно не знает, обнулялось бы каждым нажатием «Сохранить». Тогда суточное
        // правило проверки сбросилось бы, шарик о той же версии показался бы снова, а заметки
        // к выпуску пропали бы. ⚠️ Единственное из этих полей, которое человек меняет сам, —
        // `updateSkippedVersion`; оно тоже проходит через окно нетронутым.
        UpdateCheckedAt = _settings?.Settings.UpdateCheckedAt ?? string.Empty,
        UpdateLatest = _settings?.Settings.UpdateLatest ?? string.Empty,
        UpdatePublished = _settings?.Settings.UpdatePublished ?? string.Empty,
        UpdatePageUrl = _settings?.Settings.UpdatePageUrl ?? string.Empty,
        UpdateNotes = _settings?.Settings.UpdateNotes ?? string.Empty,
        UpdateNotesRaw = _settings?.Settings.UpdateNotesRaw ?? string.Empty,
        UpdateSkippedVersion = _settings?.Settings.UpdateSkippedVersion ?? string.Empty,

        // ⚠️ СЛЕД ПОПЫТКИ ПРОВЕРКИ — тоже через окно и НЕТРОНУТЫМ (DEF-036, 2.4.0).
        // Без этих трёх полей сравнение «есть ли несохранённая правка» видело разницу
        // ПОСЛЕ ОДНОЙ ПРОВЕРКИ ОБНОВЛЕНИЯ: проверка пишет след в настройки, окно о нём
        // не знает и подставляет пусто — и человек получал вопрос «сохранить изменения?»,
        // не тронув ни одного поля. Такой вопрос перестают читать, и однажды теряется
        // настоящая правка. Проверка — «Проверили_обновление_закрытие_не_спрашивает».
        UpdateAttemptAt = _settings?.Settings.UpdateAttemptAt ?? string.Empty,
        UpdateAttemptKind = _settings?.Settings.UpdateAttemptKind ?? string.Empty,
        UpdateAttemptError = _settings?.Settings.UpdateAttemptError ?? string.Empty,

        // Размер и положение ГЛАВНОГО окна в этом окне не показываются — но обязаны пройти
        // через сохранение НЕТРОНУТЫМИ, по той же причине: окно пишет настройки целиком,
        // и обнулять геометрию каждым «Сохранить» значило бы молча терять её.
        // Проверка — «Сохранение_из_окна_не_забывает_размер_и_положение_окна».
        WindowX = _settings?.Settings.WindowX ?? 0,
        WindowY = _settings?.Settings.WindowY ?? 0,
        WindowWidth = _settings?.Settings.WindowWidth ?? 0,
        WindowHeight = _settings?.Settings.WindowHeight ?? 0,

        // ТЕЛЕФОН (этап F) в этом окне ещё НЕ ПОКАЗЫВАЕТСЯ — раздел «Телефон» отдельный шаг, —
        // но поля обязаны пройти через сохранение НЕТРОНУТЫМИ: окно пишет настройки ЦЕЛИКОМ,
        // и поле, которого оно не знает, обнулялось бы каждым нажатием «Сохранить». Тогда человек,
        // тронувший любую настройку, молча терял бы и разрешение на телефон, и запомненный адрес,
        // и время согласия — то есть калитка закрывалась бы случайным щелчком в другом разделе.
        // ТЕЛЕФОН (этап F1.d). Здесь поля показываются, и через «Сохранить» проходят НЕТРОНУТЫМИ —
        // те, которых окно не меняет вовсе: след адреса, «когда видели» и «когда разрешили».
        // ⚠️ Рубильник и порт — поля ЭТОГО окна, и они читаются из формы: рубильник пишется сразу
        // по щелчку (он исполняет согласие человека), а порт — по кнопке «Сохранить», как у сервера.
        // Обнулять же здесь следы значило бы, что человек, тронувший любую настройку, молча теряет
        // и запомненный адрес, и время согласия — то есть калитка закрывалась бы щелчком в другом
        // разделе. Проверка — «Сохранение_из_окна_не_теряет_телефонные_поля».
        PhoneEnabled = PhoneAllowedCheck.IsChecked == true,
        PhonePort = PanelSettings.NormalizePhonePort((int)(PhonePortBox.Value ?? Server.PhonePass.DefaultPort)),

        // ⚠️ ПРИЗНАК «порт выбирал человек» ставится ТОЛЬКО когда человек его действительно
        // тронул, — как у порта сервера, где признак ставит кнопка «Сохранить». Разница в том,
        // что порт сервера человек видит всегда, а порт телефона — тоже всегда; но окно пишет
        // файл ЦЕЛИКОМ, и безусловное «выбрал» стирало бы разницу между «в файле умолчание»
        // и «человек выбрал это число», ради которой признак и заведён.
        PhonePortChosen = _phonePortTouched || (_settings?.Settings.PhonePortChosen ?? false),
        PhoneLastAddress = _settings?.Settings.PhoneLastAddress ?? string.Empty,
        PhoneChosenAddress = _settings?.Settings.PhoneChosenAddress ?? string.Empty,
        PhoneLastSeen = _settings?.Settings.PhoneLastSeen ?? string.Empty,
        PhoneConsentAt = _settings?.Settings.PhoneConsentAt ?? string.Empty,
    };

    /// <summary>Строки поля — по одному пути в строке. Пустые и повторы отсеивает NormalizeKeyDirs.</summary>
    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Attach(
        ISettingsControl settings,
        IAutostartControl? autostart,
        IBalanceControl? balance = null,
        IPricingControl? pricing = null,
        IUpdateControl? update = null,
        IUpdateInstall? install = null,
        PanelUpdateState? updateState = null,
        Func<string, bool>? skipUpdate = null,
        Action? exitForUpdate = null,
        IPhoneSection? phone = null,
        Func<IReadOnlyList<RawAddress>>? phoneAddresses = null,
        Func<string, string>? phoneAddressMask = null,
        IFirewallDoor? firewall = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _autostart = autostart;
        _balance = balance;
        _pricing = pricing;

        // РАЗДЕЛ «СЕРВЕР» ПОКАЗЫВАЕТ ПРАВДУ САМ (задание владельца 14.10.2026, §3).
        //
        // **От какого случая.** Владелец открыл раздел, увидел «кучу красных» меток и решил, что
        // панель сломана; на самом деле правду показывала только кнопка «Проверить окружение».
        // Его слова: *«Я думал панель автоматически это делает»*. Проба умений идёт в фоне и о своём
        // результате никому не сообщала, а состояние сервера раздел читал только в миг отрисовки.
        //
        // ⚠️ **ЗОВЁТСЯ ИМЕННО `RenderEnvironment()`, А НЕ `Render()`** — и это не мелочь: `Render()`
        // перечитывает поля из настроек и СТЁР БЫ несохранённую правку ровно в тот миг, когда
        // пришли факты пробы (то же объяснение, что у раздела «Обновление» строкой ниже).
        // Стережёт это `ServerSectionLiveTests.Перерисовка_раздела_не_стирает_несохранённую_правку`.
        _settings.EnvironmentChanged -= RenderEnvironment;
        _settings.EnvironmentChanged += RenderEnvironment;

        // РАЗДЕЛ «ТЕЛЕФОН» (F1.d): дверь к решениям о телефоне и дверь к адресам машины.
        // Порядок и раздельность дверей не случайны: адреса приходят от СИСТЕМЫ, а решения
        // о телефоне — от калитки и настроек; слей их в одну, и подставить в проверке адреса
        // можно было бы только вместе с подменой самого телефона.
        _phone = phone;
        if (phoneAddresses is not null) _phoneAddresses = phoneAddresses;
        if (phoneAddressMask is not null) _phoneAddressMask = phoneAddressMask;

        // ПРАВИЛО БРАНДМАУЭРА (F4) — своя дверь и своё право: её может не быть вовсе (прогон
        // проверки), и тогда раздел об этом говорит, а не показывает пустую кнопку.
        _firewall = firewall;

        // РАЗДЕЛ «ОБНОВЛЕНИЕ» — решение владельца 28.09.2026. Ему нужны ДВА домена: состояние
        // выпусков и движок установки. Ни сервера, ни копий ему не нужно, и второго места с этим
        // ответом в панели нет вовсе: отдельное окно обновления убрано 09.10.2026.
        if (_update is not null) _update.Changed -= RenderUpdateSection;

        _update = update;
        _install = install;
        _skipUpdate = skipUpdate;
        _exitForUpdate = exitForUpdate;

        // СОСТОЯНИЕ ПОДГОТОВКИ ПРИХОДИТ ДОВОДОМ (DEF-058, 2.4.2): у человека — один объект на
        // процесс из `App`, и окно только показывает его. Без довода (проверки вида, съёмка кадра)
        // окно заводит своё — названный случай, а не второй дом состояния.
        _updateState.Changed -= OnUpdateStateChanged;
        _updateState = updateState ?? new PanelUpdateState();
        _updateState.Changed += OnUpdateStateChanged;

        // ПОДПИСКА СНИМАЕТСЯ ВМЕСТЕ С ОКНОМ, а состояние панели продолжает жить: мёртвый
        // подписчик держал бы закрытое окно живым и будил бы его на каждый буфер загрузки.
        // Само закрытие окна состояние НЕ трогает (в этом и был DEF-058).
        Closed -= OnClosedDetachUpdateState;
        Closed += OnClosedDetachUpdateState;

        // Состояние изменилось (проверка началась, пришёл ответ) — перерисовать ТОЛЬКО раздел,
        // и ни в коем случае не весь Render: он перечитывает поля из настроек, то есть стёр бы
        // несохранённую правку ровно в тот момент, когда человек ждёт ответа о версии.
        if (_update is not null) _update.Changed += RenderUpdateSection;

        _autostartModel = new AutostartPanelModel(autostart);

        SaveButton.Click += OnSave;
        CheckEnvironmentButton.Click += (_, _) => RenderEnvironment();
        SuggestWorkDirButton.Click += OnUseSuggestion;
        WorkDirBox.TextChanged += (_, _) => RenderSuggestions();
        ThemeBox.SelectionChanged += OnThemeChanged;
        AutostartCheck.IsCheckedChanged += OnAutostartToggled;
        PortBox.ValueChanged += (_, _) => RenderPortHint();
        BackupFolderBox.TextChanged += (_, _) => RenderBackupFolderHint();
        BackupKeysCheck.IsCheckedChanged += (_, _) => RenderKeysWarning();
        PhoneAllowedCheck.IsCheckedChanged += (_, _) => OnPhoneAllowedToggled();
        FirewallButton.Click += (_, _) => OnFirewallClick();

        // ⚠️ ДВУХ ПОДПИСОК ЗДЕСЬ БОЛЬШЕ НЕТ, и это след решения владельца 28.09.2026: таблицы
        // пиков и цен из раздела убраны в своё окно, поэтому окну настроек нечего перерисовывать
        // ни на новый ответ по ценам, ни на смену агента. Смена агента по-прежнему ничего
        // не записывает — в файл он уйдёт по кнопке «Сохранить», как и тема.
        Render();
    }

    /// <summary>Перечитать всё из настроек и состояния (зовут и проверки).</summary>
    public void Render()
    {
        if (_settings is null) return;

        WorkDirBox.Text = _settings.Settings.ServerWorkingDir;

        _updatingTheme = true;
        ThemeBox.SelectedIndex = PanelSettings.ThemeIndex(_settings.Settings.Theme);
        _updatingTheme = false;

        // Язык — то, что СЕЙЧАС в файле, а не то, на котором говорит это окно: окно построено
        // на прежнем языке и переведётся только при следующем запуске. Показать здесь выбранный
        // язык — единственный честный ответ на «что у меня стоит».
        LanguageBox.SelectedIndex = PanelSettings.LanguageIndex(_settings.Settings.Language);

        RenderBalanceSection();
        RenderUpdateSection();
        RenderAutostart();
        RenderSuggestions();
        RenderPort();
        RenderBackupFolder();
        RenderBackupSchedule();
        RenderBackupKeys();
        RenderEnvironment();
        RenderPhoneSection();

        StatusText.Text = _settings.LoadProblem;
    }

    /// <summary>
    /// Порт в поле — ВСЕГДА тот, который панель действительно займёт. Файл правят руками, и порт
    /// 3080 в нём — умолчание v1, которое человек принесёт с собой; показать его как есть значило
    /// бы обещать сервер на порту, который панель занять не может.
    /// </summary>
    private void RenderPort()
    {
        _updatingPort = true;
        PortBox.Value = PanelSettings.NormalizeServerPort(_settings!.Settings.ServerPort);
        _updatingPort = false;

        RenderPortHint();
    }

    /// <summary>
    /// Подсказка под полем порта. Неподходящее значение называется вслух СРАЗУ, как только человек
    /// его набрал, — а не после сохранения, когда он уже ищет сервер не там.
    ///
    /// ⚠️ Здесь НЕТ запрета на 3080, и это не забывчивость: решение владельца 26.09.2026 — 2.0
    /// встаёт на место 1.x, и 3080 стал умолчанием. Запрет остался, но живёт там, где порт
    /// действительно занимают (<c>ServerController</c>): прогон проверки займёт 3081.
    /// </summary>
    private void RenderPortHint()
    {
        if (_settings is null) return;

        var typed = (int)(PortBox.Value ?? ServerDecisions.DefaultServerPort);

        if (_updatingPort || ServerDecisions.IsAllowedPort(typed, mayOccupyOwnerPort: true))
        {
            PortHintText.Text = PanelStrings.SettingsPortHint;
            return;
        }

        PortHintText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.SettingsPortForbiddenFormat,
            typed,
            PanelSettings.NormalizeServerPort(typed));
    }

    /// <summary>
    /// Показать в поле ту папку копий, которая действует сейчас.
    ///
    /// ⚠️ **Умолчание показывается В САМОМ ПОЛЕ, подсказкой-заполнителем** (жалоба владельца,
    /// п. 10 `docs\DESIGN.md`): окно копий пишет развёрнутый путь крупной строкой, а здесь поле
    /// стояло ПУСТЫМ, и связь «пусто = умолчание» жила только словами под ним. Два окна говорили
    /// об одном и том же по-разному — человек и путался. Заполнитель виден ровно тогда, когда
    /// поле пустое, то есть ровно тогда, когда действует умолчание.
    ///
    /// В файл при этом уходит по-прежнему ПУСТО (см. <see cref="PendingSettings"/>): подставленное
    /// значение перестало бы быть умолчанием, и вернуться к нему человек уже не смог бы.
    /// Путь показывается через маску — кадр окна уезжает в README.
    /// </summary>
    private void RenderBackupFolder()
    {
        if (_settings is null) return;

        var configured = _settings.Settings.BackupFolder ?? string.Empty;

        BackupFolderBox.Text = configured;
        BackupFolderBox.PlaceholderText = DisplayMask.Path(BackupNaming.Folder(configured));

        RenderBackupFolderHint();
    }

    /// <summary>
    /// Подсказка под папкой копий. Умолчание v1 называется ЦЕЛИКОМ и словами: пустое поле
    /// не должно выглядеть как «папка не выбрана». Путь показывается через маску — кадр окна
    /// уезжает в README, и имя пользователя в нём лишнее.
    /// </summary>
    private void RenderBackupFolderHint()
    {
        if (_settings is null) return;

        var configured = BackupFolderBox.Text ?? string.Empty;

        BackupFolderHintText.Text = BackupNaming.IsDefault(configured)
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BackupFolderHintFormat,
                DisplayMask.Path(BackupNaming.Folder(configured)))
            : PanelStrings.BackupFolderCustomNote;
    }

    /// <summary>
    /// Раздел «расписание и хранение»: галочка, часы и предел копий — из настроек, в файл они
    /// уйдут по кнопке «Сохранить», как и всё остальное в этом окне.
    ///
    /// Числа показываются УЖЕ приведёнными к границам: файл правят руками, и «100000 часов» в поле
    /// выглядело бы как принятое значение, хотя панель возьмёт неделю. Показать то, что будет
    /// на самом деле, — единственный честный ответ на «что у меня стоит».
    /// </summary>
    private void RenderBackupSchedule()
    {
        if (_settings is null) return;

        var settings = _settings.Settings;

        BackupScheduleCheck.IsChecked = settings.BackupScheduleEnabled;
        BackupScheduleCheck.IsEnabled = _settings.Writable;

        BackupEveryBox.Value = PanelSettings.ClampBackupHours(settings.BackupEveryHours);
        BackupEveryBox.IsEnabled = _settings.Writable;

        BackupKeepBox.Value = PanelSettings.ClampBackupKeep(settings.BackupKeepCount);
        BackupKeepBox.IsEnabled = _settings.Writable;
    }

    /// <summary>
    /// Раздел «ключи»: разрешение и свои каталоги. Галочка и поле показывают НАСТОЯЩИЕ настройки,
    /// а в файл они попадут по кнопке «Сохранить» — как и всё остальное в этом окне.
    /// </summary>
    private void RenderBackupKeys()
    {
        if (_settings is null) return;

        BackupKeysCheck.IsChecked = _settings.Settings.BackupWithKeys;
        BackupKeysCheck.IsEnabled = _settings.Writable;
        BackupKeysDirsBox.IsEnabled = _settings.Writable;
        BackupKeysDirsBox.Text = string.Join(
            Environment.NewLine, _settings.Settings.BackupKeyDirs);

        RenderKeysWarning();
    }

    /// <summary>
    /// Предупреждение видно ТОЛЬКО когда разрешение включено: оно про то, чем СТАНОВИТСЯ архив,
    /// и держать его на экране при выключенном разрешении значит приучать не читать его вовсе.
    /// </summary>
    private void RenderKeysWarning()
    {
        BackupKeysWarningText.IsVisible = BackupKeysCheck.IsChecked == true;
    }

    /// <summary>
    /// Раздел «Баланс и тариф»: поля ставятся из настроек, а расписание пиков берётся
    /// у КОНТРОЛЛЕРА — оно считается по профилю активного агента, и второго расчёта в панели быть
    /// не должно (иначе окно и значок однажды покажут разное).
    /// </summary>
    private void RenderBalanceSection()
    {
        var settings = _settings!.Settings;

        AgentBox.SelectedIndex = AgentCatalog.IndexOf(settings.ActiveAgent);

        // ЧЕСТНО О ВЫБОРЕ: пока агент один, список недоступен — живой список из одного пункта
        // обещал бы выбор, которого нет. Готовая строка под ним говорит об этом словами.
        AgentBox.IsEnabled = _settings.Writable && AgentCatalog.HasChoice;
        AgentNoteText.Text = PanelStrings.SettingsActiveAgentNote;

        BalanceAutoCheck.IsChecked = settings.BalanceAutoRefresh;
        RefreshMinutesBox.Value = PanelSettings.ClampRefreshMinutes(settings.BalanceRefreshMinutes);

        BalanceWarnCheck.IsChecked = settings.BalanceWarnEnabled;
        ThresholdBox.Value = settings.BalanceWarnThreshold;

        PeakNotifyBox.Value = PanelSettings.ClampPeakNotify(settings.PeakNotifyMinutes);
        PeakNotifyNoteText.Text = PanelStrings.SettingsPeakNotifyNote;
    }

    /// <summary>
    /// РАЗДЕЛ «ТЕЛЕФОН» (F1.d) — состояние, действия и подробности ЧЕТЫРЬМЯ карточками, как
    /// у раздела «Обновление». Что здесь можно узнать и что сделать: разрешён ли телефон и когда
    /// человек разрешил · на каком порту стоит калитка · код сопряжения на пять минут · кто уже
    /// подключён и когда его видели · по какому адресу телефон эту панель найдёт · пропустит ли
    /// входящее подключение брандмауэр Windows (F4 — и правило ставится ТОЛЬКО щелчком человека).
    ///
    /// ⚠️ **Ни одного решения о телефоне в окне нет.** Что годится телефону — решает
    /// <see cref="PhoneDecisions.Suitable"/>; раскладку данных системы в адрес делает
    /// <see cref="NetworkAddresses"/>; слова о коде собирает <see cref="PhoneDecisions.PairingCodeText"/>;
    /// а сам рубильник исполняет дверь (<see cref="IPhoneSection"/>). Окно показывает и просит —
    /// иначе решение о двери наружу жило бы в разметке.
    /// </summary>

    /// <summary>
    /// РАСКРЫТЬ ИЛИ СВЕРНУТЬ ВЫЖИМКУ ПРО ВЕРСИИ. Видимость ставится ВСЕМ трём частям сразу:
    /// заголовку, пустому состоянию и полю с прокруткой — иначе свёрнутая карточка оставила бы
    /// за собой пустое поле высотой 140 точек.
    /// </summary>
    /// <summary>
    /// РАСКРЫТЬ ИЛИ СВЕРНУТЬ ВЫЖИМКУ ПРО ВЕРСИИ.
    ///
    /// ⚠️ **Норма — СВЁРНУТО, и это состояние САМОЙ КАРТОЧКИ, а не поле окна.** Visibilité
    /// читается у заголовка выжимки: так «свёрнуто» — это отсутствие состояния, а не второе
    /// место, которое может разойтись с первым (проверено: отдельное поле однажды оказалось
    /// выставленным до первого щелчка, и разобраться, кто его тронул, было нельзя).
    /// </summary>
    private void ToggleEngineNotes()
    {
        ShowEngineNotes(!EngineNotesTitleText.IsVisible);
    }

    /// <summary>Показать или спрятать выжимку. Видимость ставится ВСЕМ трём частям сразу — иначе
    /// свёрнутая карточка оставила бы за собой пустое поле высотой 140 точек.</summary>
    private void ShowEngineNotes(bool show)
    {
        EngineNotesTitleText.IsVisible = show;
        EngineNotesEmptyText.IsVisible = show && EngineNotesPanel.Children.Count == 0;
        EngineNotesScroll.IsVisible = show && EngineNotesPanel.Children.Count > 0;
    }

    private void RenderPhoneSection()
    {
        if (_settings is null) return;

        PhoneStatusText.Classes.Add("sectionTitle");
        PhoneStatusText.FontSize = 16;
        PhoneStatusText.FontWeight = FontWeight.SemiBold;

        var allowed = _settings.Settings.PhoneEnabled;

        // ПРАВИЛО БРАНДМАУЭРА — ДО всех веток: оно не часть двери телефона, и карточка обязана
        // быть на месте и тогда, когда телефоном в этом прогоне не управляют вовсе.
        RenderPhoneFirewall();

        // РУБИЛЬНИК. Подстановка состояния сама поднимает событие — без этого окно записало бы
        // в настройки то, что только что прочитало (та же грабля, что у автозапуска).
        _phoneUpdating = true;
        PhoneAllowedCheck.IsChecked = allowed;
        PhoneAllowedCheck.IsEnabled = _settings.Writable;
        _phoneUpdating = false;

        // ПОРТ: показывается УЖЕ приведённым к границам — файл правят руками, и «порт 70000»
        // в поле выглядел бы принятым, хотя панель займёт умолчание (как у порта сервера).
        _phoneUpdating = true;
        PhonePortBox.Value = PanelSettings.NormalizePhonePort(_settings.Settings.PhonePort);
        PhonePortBox.IsEnabled = _settings.Writable;
        _phoneUpdating = false;

        RenderPhonePortHint();

        // ЧЕСТНО О БЕЗДВЕРЬЕ: окно без связки (так его видят проверки) телефоном
        // не управляет, и молчать об этом нельзя — пустая галочка читалась бы как «телефон выключен».
        var door = _phone;

        if (door is null)
        {
            PhoneStatusText.Text = PanelStrings.PhoneNoDoor;
            PhoneCodeButton.IsEnabled = false;
            PhoneConsentText.IsVisible = false;
            PhoneRightNoteText.IsVisible = false;
            PhoneDevicesEmptyText.Text = PanelStrings.PhoneNoDoor;
            PhoneAddressNoText.Text = PanelStrings.PhoneNoDoor;

            // ⚠️ АДРЕС ПОКАЗЫВАЕТСЯ И БЕЗ ДВЕРИ, и это не мелочь: «по какому адресу телефон найдёт
            // панель» — свойство САМОЙ машины, а не калитки. Первая редакция возвращалась здесь
            // целиком, и карточка адреса оставалась ПУСТОЙ — на кадре раздела не было ни адреса,
            // ни слов о том, что его нет. Беду нашёл сам кадр, а не проверка: она смотрела на текст,
            // которого не было вовсе.
            RenderPhoneAddress(new PhoneSectionInfo(
                Allowed: allowed,
                Listening: false,
                DeviceCount: 0,
                Devices: Array.Empty<Server.PhoneDeviceView>(),
                LiveCode: false,
                ConsentText: string.Empty,
                LastAddress: _settings.Settings.PhoneLastAddress,
                ChosenAddress: _settings.Settings.PhoneChosenAddress,
                LastSeenText: string.Empty));

            return;
        }

        var info = door.PhoneInfo();

        // СОСТОЯНИЕ — первой строкой, как решение в разделе «Обновление»: человеку нужен ответ,
        // а не перечень полей. Тон — тот же язык, что у строк состояния в меню значка.
        //
        // ⚠️ **АДРЕС, А НЕ ВНУТРЕННЕЕ СЛОВО О ПОДКЛЮЧЕНИИ** (решение владельца 13.10.2026, живой
        // прогон на телефоне): прежде сюда уходило «слушает порт» или «закрыта» — слова про НАШУ
        // дверь, которых человек не ищет. Теперь строка отвечает на его вопрос: подключён ли
        // телефон и по какому адресу панель его ждёт. Адреса нет — так и сказано словами.
        var address = info.LastAddress.Length > 0 ? info.LastAddress : PanelStrings.PhoneAddressNone;

        if (allowed && !info.EngineLinked)
        {
            // ТЕЛЕФОН СОПРЯЖЁН, А ВОПРОС ДОСТАТЬ НЕЧЕМ — и об этом говорят СЛОВАМИ (находка
            // владельца 13.10.2026). Строка «телефон подключён · адрес: …» в этом положении
            // обещала работающий телефон: дверь-то открыта, а связи с сервером DSH нет —
            // сервер поднят не панелью, и ссылки входа у неё нет. Вопрос агента не придёт никогда.
            PhoneStatusText.Text = PanelStrings.PhoneSectionNoEngine;
            PanelLook.Tone(PhoneStatusText, TrayTone.Neutral);
        }
        else
        {
            PhoneStatusText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                allowed ? PanelStrings.PhoneStateAllowedFormat : PanelStrings.PhoneStateOffFormat,
                address,
                info.DeviceCount);
            PanelLook.Tone(PhoneStatusText, allowed ? TrayTone.Good : TrayTone.Neutral);
        }

        // КОГДА РАЗРЕШИЛ: сам факт «включено» на этот вопрос не отвечает. Пусто — человек
        // не давал согласия, и строка молчит, а не показывает выдуманное «только что».
        PhoneConsentText.Text = info.ConsentText.Length > 0
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PhoneConsentFormat,
                info.ConsentText)
            : string.Empty;
        PhoneConsentText.IsVisible = PhoneConsentText.Text?.Length > 0;

        // ПОДСКАЗКА О ПРАВЕ: у прогона проверки калитка не поднимается вовсе, и «телефон разрешён,
        // а порт закрыт» без объяснения выглядело бы как поломка.
        PhoneRightNoteText.Text = PanelStrings.PhoneRightNote;
        PhoneRightNoteText.IsVisible = allowed && !info.Listening;

        // КНОПКА КОДА: живой код уже есть — кнопка называет другое действие, а не «покажи то же».
        // Пустоты она не показывает НИКОГДА: нет живого кода — она берёт новый.
        PhoneCodeButton.IsEnabled = _settings.Writable;
        PhoneCodeButton.Content = info.LiveCode
            ? PanelStrings.PhoneShowCodeAgainButton
            : PanelStrings.PhoneShowCodeButton;

        // ЗАМЕТКА О КОДЕ: «взяли новый» / «уже показан» — чтобы человек не искал, куда делся прежний.
        PhoneCodeNoteText.Text = _phoneCodeNote;
        PhoneCodeNoteText.IsVisible = _phoneCodeNote.Length > 0;

        RenderPhoneDevices(info);
        RenderPhoneAddress(info);
    }

    /// <summary>
    /// ПОДСКАЗКА ПОД ПОЛЕМ ПОРТА. Неподходящее значение называется вслух СРАЗУ, как его набрали,
    /// а не после сохранения. ⚠️ Здесь ЕСТЬ запрет на <c>3080</c> и <c>3081</c>, и это не забывчивость:
    /// у порта СЕРВЕРА их запрета нет (он владельца), а калитка телефона не занимает их никогда
    /// (<see cref="PhoneGate.PortForbidden"/>), и порт, на котором дверь не встанет, обязан быть
    /// назван запрещённым до нажатия, а не отказом после.
    /// </summary>
    private void RenderPhonePortHint()
    {
        if (_settings is null) return;

        var typed = (int)(PhonePortBox.Value ?? Server.PhonePass.DefaultPort);

        if (_phoneUpdating || PhoneGate.PortAllowed(typed))
        {
            PhonePortNoteText.Text = PanelStrings.PhonePortNote;
            return;
        }

        PhonePortNoteText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PhoneGate.PortForbidden(typed) ? PanelStrings.PhonePortForbiddenFormat : PanelStrings.PhonePortWrongFormat,
            typed,
            PanelSettings.NormalizePhonePort(typed));
    }

    /// <summary>
    /// ЧЕЛОВЕК ЩЁЛКНУЛ РУБИЛЬНИК. Это ЕДИНСТВЕННОЕ действие раздела, которое пишет настройки
    /// сразу, не дожидаясь «Сохранить», — и потому, что оно не настройка, а согласие: включение
    /// запоминает ВРЕМЯ согласия, выключение стирает его и отвязывает ВСЕ устройства (иначе
    /// выключение ничего бы не значило: старые cookie продолжали бы пускать), а открытая калитка
    /// гасится. Показывать это галочкой, которую человек ещё не подтвердил, значило бы держать
    /// дверь наружу открытой до нажатия другой кнопки.
    ///
    /// ⚠️ Отказ записи (прогон без права) назван словами: сделать вид, что телефон разрешён,
    /// значило бы обещать дверь, которой нет.
    /// </summary>
    private void OnPhoneAllowedToggled()
    {
        if (_phoneUpdating || _phone is null || _settings is null) return;

        var allowed = PhoneAllowedCheck.IsChecked == true;

        if (!_phone.SetPhoneAllowed(allowed))
        {
            StatusText.Text = PanelStrings.PhoneToggleFailed;
            RenderPhoneSection();
            return;
        }

        StatusText.Text = string.Empty;

        // ⚠️ ПОРЯДОК: СНАЧАЛА УЗКОЕ ПРАВИЛО, ПОТОМ ДВЕРЬ (уточнение владельца 11.10.2026 —
        // на живом прогоне вышло наоборот, и Windows задала свой вопрос и открыла ШИРОКИЕ правила).
        // Калитка при этом уже существует (владеет ею обычный запуск человеком), поэтому «включить»
        // и «поднять» — два разных действия, и поднимаем мы ТУ ЖЕ калитку.
        if (allowed) StartPhoneGateAfterFirewall();

        RenderPhoneSection();
    }

    /// <summary>
    /// ПОДНЯТЬ КАЛИТКУ, ЕСЛИ ПРАВИЛО БРАНДМАУЭРА ГОТОВО, — иначе сказать словами, чего не хватает.
    ///
    /// Три ответа, и все три названы:
    ///
    /// * **двери брандмауэра нет** (прогон проверки, съёмка) — требовать правило не с кого, и калитка
    ///   встаёт: прогон проверки правил машины не меняет вовсе;
    /// * **правило прочитано как наше** (<see cref="FirewallPresence.Ready"/>) — калитка встаёт;
    /// * **во всех прочих случаях** — НЕ встаёт, и человеку сказано, что сначала нужно правило:
    ///   «не прочиталось» никогда не значит «правила нет», а открыть дверь в надежде значило бы
    ///   получить тот самый вопрос Windows и широкие правила.
    /// </summary>
    private void StartPhoneGateAfterFirewall()
    {
        if (_phone is null || _settings is null) return;

        if (_firewall is null)
        {
            _phone.StartPhoneGate();
            return;
        }

        var port = PanelSettings.NormalizePhonePort(_settings.Settings.PhonePort);

        if (_firewall.Read(port).Presence == FirewallPresence.Ready)
        {
            _phone.StartPhoneGate();
            return;
        }

        FirewallResultText.Text = PanelStrings.PhoneGateBlockedByFirewall;
        FirewallResultText.IsVisible = true;
    }

    /// <summary>
    /// ЧЕЛОВЕК ТРОНУЛ ПОЛЕ ПОРТА: с этого мгновения порт считается выбранным им, и панель вправе
    /// больше не переводить его на новое умолчание. Подстановка при отрисовке событие не поднимает
    /// (<c>_phoneUpdating</c>), иначе «человек выбрал» стало бы правдой при простом открытии окна.
    /// </summary>
    private void OnPhonePortTyped()
    {
        if (_phoneUpdating) return;

        _phonePortTouched = true;
        RenderPhonePortHint();
    }

    /// <summary>
    /// ПОКАЗАТЬ КОД СОПРЯЖЕНИЯ — крупно и словами, со сроком жизни. Живой код уже есть — он
    /// и показывается: выдавать второй значило бы обесценить тот, который человек, может быть,
    /// уже диктует на телефон. Кода нет — берётся новый.
    ///
    /// ⚠️ **Пустоты кнопка не показывает никогда:** нет живого кода — значит будет новый, и это
    /// единственное, что она может сделать. Проверка «без живого кода пустоты нет» сторожит
    /// именно это: в первой редакции она показала бы пустую строку и человек решил бы, что панель
    /// сломалась.
    /// </summary>
    public void ShowPhoneCode()
    {
        if (_phone is null) return;

        var live = _phone.PhoneInfo().LiveCode;

        // Живой код берём у двери, а не у себя: он живёт в калитке, и второго его места быть
        // не должно. Пустой ответ (калитка закрыта, права нет) честно превращается в слова.
        PhoneCode = live ? _phone.PhoneCode() ?? string.Empty : _phone.NewPhoneCode() ?? string.Empty;

        if (PhoneCode.Length == 0)
        {
            _phoneCodeNote = PanelStrings.PhoneCodeFailed;
            RenderPhoneSection();
            return;
        }

        PhoneCodeText.Text = PhoneDecisions.PairingCodeText(PhoneCode);
        PhoneCodeText.IsVisible = true;
        _phoneCodeNote = live ? PanelStrings.PhoneCodeShownAgain : PanelStrings.PhoneCodeFresh;

        RenderPhoneSection();

        // Текст кода ставится ПОСЛЕ перерисовки: перерисовка чистит поле кода — она про состояние,
        // а не про то, что человек только что попросил показать.
        PhoneCodeText.Text = PhoneDecisions.PairingCodeText(PhoneCode);
        PhoneCodeText.IsVisible = true;
    }

    /// <summary>
    /// СПИСОК ПОДКЛЮЧЁННЫХ: у каждого — «когда подключился» и «когда видели», и своя кнопка
    /// «Отвязать». Пустой список говорит словами, что телефон ещё не подключался: пустое место
    /// человек прочитал бы как «список не догрузился».
    ///
    /// ⚠️ Строка устройства — ТОЛЬКО имя и две отметки времени. Ни адреса телефона, ни чего-либо
    /// ещё: список приходит из <c>PhonePass.SafeDevices</c>, где поля адреса нет вовсе, — вернуть
    /// его в окно нечем (и это проверяется отдельно).
    /// </summary>
    private void RenderPhoneDevices(PhoneSectionInfo info)
    {
        PhoneDevicesPanel.Children.Clear();

        PhoneDevicesCountText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.PhoneDevicesCountFormat,
            info.DeviceCount);

        PhoneDevicesEmptyText.IsVisible = info.Devices.Count == 0;

        foreach (var device in info.Devices)
        {
            var row = new DockPanel { LastChildFill = true };

            var forget = new Button { Content = PanelStrings.PhoneForgetDeviceButton };
            PanelToolTip.Set(forget, PanelStrings.TipPhoneForgetDeviceButton);

            var id = device.Id;
            forget.Click += (_, _) => ForgetPhoneDevice(id);
            DockPanel.SetDock(forget, Dock.Right);

            row.Children.Add(forget);
            row.Children.Add(new TextBlock
            {
                // ⚠️ ЧЕЛОВЕКУ — КОРОТКОЕ ИМЯ ТЕЛЕФОНА, а не внутренний `phone-N`
                // (решение владельца 12.10.2026). Имени нет — честное «телефон», а не номер,
                // которого человек не выбирал: номер остался в cookie и в записи панели.
                //
                // ⚠️ И ОТМЕТКИ — ОДНОЙ ФОРМОЙ ПАНЕЛИ И В МЕСТНОМ ВРЕМЕНИ (находка владельца
                // 14.10.2026, ночь). В записи пропуска они лежат машинной строкой
                // (`2026-10-13T21:18:17.6132318+00:00`), и прежде она уходила на экран КАК ЕСТЬ:
                // человек читал UTC вместо своих часов — дата врала на три часа, — а в той же
                // карточке двумя строками выше стояло правильное «Разрешение выдано: 14.10.2026,
                // 00:12». Две формы одной даты на одном экране читаются как ДВЕ РАЗНЫЕ величины
                // (`_design-library\50-lists-navigation.md`, строка 46: «Дата и время — одной
                // формой на всё приложение»). Приводит их ТА ЖЕ дверь, что у «Разрешение выдано»
                // и у истории цен (`PricingDecisions.CheckedText`), — второго формата не заводим.
                Text = DeviceLine(device),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            PhoneDevicesPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// СТРОКА ОДНОГО ПОДКЛЮЧЁННОГО УСТРОЙСТВА: имя, «подключился» и «видели».
    ///
    /// **От какого случая (находка владельца 14.10.2026, ночь).** Строка уходила на экран сырыми
    /// отметками записи пропуска — `2026-10-13T21:18:17.6132318+00:00` вместо «14.10.2026, 00:18»:
    /// машинная форма вместо формы панели, время в UTC вместо местного, и один и тот же миг
    /// напечатанный ДВАЖДЫ («подключился» и «видели») — то есть две формы одной даты на одном
    /// экране, ровно то, что запрещает банк (`_design-library\50-lists-navigation.md`, строка 46).
    ///
    /// ⚠️ Приводит отметки ТА ЖЕ дверь, что у «Разрешение выдано» и у истории цен
    /// (<see cref="Pricing.PricingDecisions.CheckedText"/>): своего формата у списка устройств быть
    /// не должно, иначе он однажды разойдётся с соседней строкой той же карточки.
    ///
    /// ⚠️ **Один миг печатается ОДИН раз.** У только что сопряжённого телефона «подключился»
    /// и «видели» — одно событие; две одинаковые отметки рядом читались бы как две разные величины.
    /// Совпали — так и сказано: «подключился и уже виден».
    /// </summary>
    internal static string DeviceLine(Server.PhoneDeviceView device)
    {
        var name = PhoneDecisions.DeviceDisplay(device.Name, PanelStrings.PhonePageDeviceFallback);
        var added = Pricing.PricingDecisions.CheckedText(device.AddedAt);
        var seen = Pricing.PricingDecisions.CheckedText(device.LastSeen);

        // Отметки совпали (в том числе обе пусты) — говорится одна строка, и в ней сказано, что
        // это один и тот же миг. Не совпали — прежний вид: два разных события и две отметки.
        return string.Equals(added, seen, StringComparison.Ordinal)
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PhoneDeviceOneMomentFormat,
                name,
                added)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PhoneDeviceFormat,
                name,
                added,
                seen);
    }

    /// <summary>
    /// «ОТВЯЗАТЬ» ОДНО УСТРОЙСТВО. Убирается РОВНО одно: остальные продолжают ходить, и это
    /// не мелочь — отвязать все можно рубильником, а здесь человек называет одно.
    /// Отказ записи говорится словами: устройство, оставшееся в списке, выглядело бы как «кнопка
    /// не работает».
    /// </summary>
    public void ForgetPhoneDevice(string deviceId)
    {
        if (_phone is null || string.IsNullOrWhiteSpace(deviceId)) return;

        StatusText.Text = _phone.ForgetPhoneDevice(deviceId)
            ? string.Empty
            : PanelStrings.PhoneForgetFailed;

        RenderPhoneSection();
    }

    /// <summary>
    /// ПРАВИЛО БРАНДМАУЭРА: СОСТОЯНИЕ, СЛОВА ДО И КНОПКА (этап F4, кусок 1).
    ///
    /// **Порядок ответов.** Человек видит: что с правилом сейчас (чтением, без прав) · что именно
    /// изменится, если нажать, и что Windows спросит подтверждение · кнопку. Ровно это и просил
    /// владелец 11.10.2026 — «слова ДО и одна строка ПОСЛЕ»; строка после приходит из
    /// <see cref="OnFirewallClick"/>.
    ///
    /// ⚠️ **Двери нет — ни одного чтения и ни одной кнопки**: прогон проверки брандмауэром не
    /// управляет, и это сказано словами. Пустая кнопка читалась бы как «сейчас нажму и что-то
    /// будет», а на деле не будет ничего.
    ///
    /// ⚠️ Состояние <see cref="FirewallPresence.Unknown"/> — НЕ «правила нет»: кнопка выключается,
    /// и человеку сказано, что прочитать не удалось. Ставить правило поверх неизвестного значило бы
    /// однажды получить два правила с одним именем.
    /// </summary>
    private void RenderPhoneFirewall()
    {
        if (_settings is null) return;

        var port = PanelSettings.NormalizePhonePort(_settings.Settings.PhonePort);

        FirewallTitleText.Text = PanelStrings.FirewallTitle;
        FirewallNoteText.Text = PanelStrings.FirewallNote;
        PanelToolTip.Set(FirewallButton, PanelStrings.FirewallTipButton);

        if (_firewall is null)
        {
            _firewallState = null;
            FirewallStateText.Text = PanelStrings.FirewallNoDoor;
            FirewallBeforeText.IsVisible = false;
            FirewallButton.IsVisible = false;

            return;
        }

        var state = _firewall.Read(port);

        _firewallState = state;
        FirewallStateText.Text = state.Words;

        FirewallBeforeText.Text = state.CanAdd
            ? FirewallDecisions.BeforeAdd(port)
            : state.CanRemove
                ? FirewallDecisions.BeforeRemove()
                : string.Empty;
        FirewallBeforeText.IsVisible = state.CanAdd || state.CanRemove;

        FirewallButton.IsVisible = true;
        FirewallButton.Content = state.CanRemove
            ? PanelStrings.FirewallRemoveButton
            : PanelStrings.FirewallAllowButton;
        FirewallButton.IsEnabled = !_firewallBusy && (state.CanAdd || state.CanRemove);
    }

    /// <summary>
    /// НАЖАТИЕ: СНАЧАЛА СЛОВА, ПОТОМ ДЕЛО, ПОТОМ ОДНА СТРОКА.
    ///
    /// ⚠️ **Изменение идёт В ФОНЕ, и это не украшение.** С повышением панель ждёт запрос Windows
    /// (человек может думать над ним сколько угодно), и на нитке интерфейса окно замёрзло бы —
    /// то есть строка «ждём подтверждения» не успела бы появиться ДО запроса, а обещание владельца
    /// говорит именно «сначала слова». Ответ возвращается на нитку интерфейса общей дверью
    /// (<see cref="OnUi"/>): вторых ворот к диспетчеру в панели нет.
    ///
    /// ⚠️ **Отказ человека ничего не меняет и говорит об этом словами** — это решает
    /// <see cref="FirewallWork"/> вместе с дверью, а окно только показывает её ответ.
    /// </summary>
    private void OnFirewallClick()
    {
        if (_firewall is null || _settings is null || _firewallBusy) return;

        var state = _firewallState;

        if (state is null || (!state.CanAdd && !state.CanRemove)) return;

        var action = state.CanRemove ? FirewallAction.Remove : FirewallAction.Add;
        var port = PanelSettings.NormalizePhonePort(_settings.Settings.PhonePort);
        var door = _firewall;

        _firewallBusy = true;
        FirewallButton.IsEnabled = false;
        FirewallResultText.Text = FirewallDecisions.Working();
        FirewallResultText.IsVisible = true;

        Task.Run(() => FirewallWork.Run(door, action, port))
            .ContinueWith(task => OnUi(() => FinishFirewall(task, action)));
    }

    /// <summary>
    /// НАЖАТИЕ — ДВЕРЬЮ, И ЭТА ДВЕРЬ ОТКРЫТА ПРОВЕРКАМ.
    ///
    /// ⚠️ Зачем отдельным членом. Кнопка на время изменения **выключается**, и второе нажатие
    /// до обработчика просто не доходит — выключенная кнопка события не даёт. Это защита, но она
    /// сделала бы НЕПРОВЕРЯЕМЫМ второй предохранитель («одно нажатие — одно изменение»,
    /// <see cref="_firewallBusy"/>), а он нужен на случай, когда два запроса приходят не от кнопки.
    /// Через эту дверь предохранитель можно уронить мутацией, а без неё он был бы строкой,
    /// в которую все верят.
    /// </summary>
    internal void PressFirewall() => OnFirewallClick();

    /// <summary>
    /// ОДНА СТРОКА ПОСЛЕ. Падение в фоне не показывается человеку текстом исключения (в нём бывают
    /// пути машины) — говорится, что изменение не сделано, и это правда: <see cref="FirewallWork"/>
    /// не доехал до конца.
    ///
    /// ⚠️ **ПРАВИЛО ВСТАЛО — ДВЕРЬ ПОДНИМАЕТСЯ ЗДЕСЬ ЖЕ** (уточнение владельца 11.10.2026:
    /// узкое правило ДО калитки, и человек не должен нажимать рубильник второй раз). Только после
    /// УДАЧНОГО добавления и только если телефон разрешён: «телефон разрешён» и «дверь открыта» —
    /// по-прежнему разные вещи, и вторая без первой была бы дверью без согласия.
    /// </summary>
    private void FinishFirewall(Task<FirewallChange> task, FirewallAction action)
    {
        _firewallBusy = false;

        var done = task.Status == TaskStatus.RanToCompletion && task.Result.Done;

        FirewallResultText.Text = task.Status == TaskStatus.RanToCompletion
            ? task.Result.Text
            : FirewallDecisions.Failed(PanelStrings.FirewallNotRemoved);
        FirewallResultText.IsVisible = true;

        if (done && action == FirewallAction.Add && _settings?.Settings.PhoneEnabled == true)
            _phone?.StartPhoneGate();

        RenderPhoneFirewall();
    }

    /// <summary>
    /// АДРЕС ДЛЯ ТЕЛЕФОНА. Один подходящий — показывается крупно словами «этот адрес видит
    /// телефон»; несколько — показываются ВСЕ и человек выбирает (решение владельца: гадать
    /// нельзя); ни одного — сказано словами, что показывать нечего.
    ///
    /// Рядом — «когда телефон выходил на связь» и замечание о смене адреса: он выдан по DHCP
    /// с арендой два часа (<c>docs\PHONE-F0.md</c> §7.4), и «адрес изменился» — не поломка,
    /// а одно сканирование.
    /// </summary>
    private void RenderPhoneAddress(PhoneSectionInfo info)
    {
        // АДРЕСА ПРИХОДЯТ ОТ СИСТЕМЫ, А РЕШЕНИЕ — ОТ ПРАВИЛА: раскладка сырых данных в адрес
        // делается чистой функцией (`NetworkAddresses.Collect`), отбор годных и выбор человека —
        // `PhoneDecisions.PickAddressChosen`. Окно не решает ни того, ни другого.
        var candidates = NetworkAddresses.Collect(_phoneAddresses());
        var picked = PhoneDecisions.PickAddressChosen(candidates, info.ChosenAddress, _phoneAddressMask);

        // РАСКЛАДКА СПИСКА ИДЁТ ПОД ФЛАГОМ: подстановка выделения поднимает событие выбора,
        // и без флага окно записало бы решение, которого человек не принимал (та же грабля,
        // что у автозапуска и темы).
        _phoneAddressRendering = true;

        try
        {
            // СЛОВА — ПРО МАСКУ, А СПИСОК ВЫБОРА — ПРО НАСТОЯЩИЙ АДРЕС: по маске выбрать нечего,
            // она не адрес. На кадре список и не виден вовсе — маска там ради README.
            PhoneAddressText.Text = picked.Text();
            PhoneAddressText.IsVisible = picked.Choice != AddressChoice.Several;

            PhoneAddressCombo.ItemsSource = candidates.Select(address => address.Ip).ToList();
            PhoneAddressCombo.IsVisible = picked.Choice == AddressChoice.Several;

            if (PhoneAddressCombo.IsVisible)
            {
                var chosen = info.ChosenAddress.Length > 0 ? info.ChosenAddress : info.LastAddress;

                // Выбор человека — ПЕРВЫМ: он уже назвал адрес, и список не должен выглядеть так,
                // будто человек ничего не решал. Не выбран никто — список начинается с первого.
                var index = candidates
                    .Select((address, at) => (address, at))
                    .Where(item => string.Equals(item.address.Ip, chosen, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.at)
                    .DefaultIfEmpty(0)
                    .First();

                PhoneAddressCombo.SelectedIndex = index;
            }
            else
            {
                PhoneAddressCombo.SelectedIndex = -1;
            }
        }
        finally
        {
            _phoneAddressRendering = false;
        }

        // «КОГДА ВЫХОДИЛ НА СВЯЗЬ»: у телефонной строки в меню значка этого нет, а здесь человек
        // вправе знать, когда телефон видели в последний раз. Пусто — строка молчит словами.
        PhoneSeenText.Text = info.LastSeenText.Length > 0
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.PhoneSeenFormat,
                info.LastSeenText)
            : PanelStrings.PhoneSeenNever;

        PhoneAddressNoText.Text = PanelStrings.PhoneAddressNoAddresses;
        PhoneAddressNoText.IsVisible = picked.Choice == AddressChoice.None;

        // QR — ТОЙ ЖЕ СТРОКОЙ, ЧТО ПОКАЗАНА ВЫШЕ. Строка берётся из `picked.Address`, а не
        // собирается заново: два независимых выбора адреса однажды разошлись бы, и человек
        // навёл бы камеру на ОДИН адрес, а панель ждала бы телефон на другом.
        RenderPhoneQr(picked.Address, picked.Masked);
    }

    /// <summary>
    /// НАРИСОВАТЬ QR ИЛИ ЧЕСТНО СКАЗАТЬ, ЧТО ЕГО НЕТ (этап F1, хвост 1).
    ///
    /// **Правило одно и оно проверяемо:** QR кодирует ровно ту строку, которую раздел показывает
    /// как адрес (<see cref="Server.PhoneQr.PanelsQrRule"/>). Отсюда:
    ///
    /// * в обычном запуске это настоящий адрес машины — человек наводит камеру, а не набирает
    ///   цифры глазами;
    /// * в прогоне съёмки (`--shot settings-phone`) — **та же строка, но замаскированная**
    ///   (<c>Program.MaskAddress</c>): кадр уезжает в README, и настоящего номера машины в нём
    ///   быть не должно (решение владельца 11.10.2026). ⚠️ **Код в кадре РИСУЕТСЯ и из этой
    ///   строки** — требование владельца «кадр обязан показать код»: замаскированная строка
    ///   (`192.168.0.х`) — обычный текст без номера машины, и утечь из неё нечему. Это **образец**,
    ///   а не рабочий код, и так и сказано словами (<see cref="PanelStrings.PhoneQrMasked"/>);
    /// * адреса нет (или он ещё не выбран из нескольких) — **QR НЕ РИСУЕТСЯ**, и раздел говорит
    ///   словами, почему. Выдуманный адрес в коде хуже его отсутствия: человек пошёл бы
    ///   с телефоном туда, где панели нет.
    ///
    /// ⚠️ **Строка и признак маски приходят ОДНИМ решением** (<c>picked.Address</c>,
    /// <c>picked.Masked</c> из <c>PhoneDecisions.PickAddressChosen</c>): собери окно их само —
    /// и строка у QR однажды разошлась бы со строкой на экране, а признак маски стал бы догадкой
    /// по буквам в тексте. Маска при этом ложится на ПОКАЗ, а не на отбор: вне прогона съёмки
    /// её нет вовсе.
    ///
    /// ⚠️ **И ЧТО ИМЕННО УЕЗЖАЕТ В КОД, РЕШАЕТСЯ ЗДЕСЬ ЖЕ ОДНИМ МЕСТОМ** (решение владельца
    /// 11.10.2026): <see cref="PhoneDecisions.QrText"/> добавляет к ПОКАЗАННОМУ адресу схему и порт,
    /// который раздел показывает рядом. Прежде в коде был голый адрес, и камера показывала текст
    /// вместо страницы.
    /// </summary>
    private void RenderPhoneQr(string shownAddress, bool masked)
    {
        var shown = (shownAddress ?? string.Empty).Trim();
        var port = PanelSettings.NormalizePhonePort(_settings?.Settings.PhonePort ?? Server.PhonePass.DefaultPort);
        var text = PhoneDecisions.QrText(shown, port);

        // QR РИСУЕТ ДВЕРЬ, а не окно: она объявлена в `IPhoneSection` ровно затем, чтобы «нарисован
        // ли QR и что в нём закодировано» проверялось без библиотеки и без картинок.
        // ⚠️ Маска НЕ мешает рисованию: она меняет СТРОКУ (её и кодируем), а не право её показать.
        var png = text.Length > 0 && _phone is not null
            ? _phone.QrPng(text)
            : Array.Empty<byte>();

        PhoneQrImage.Source = png.Length > 0 ? Decode(png) : null;

        // Слова — по причине, а не одни и те же: «телефону нечего показать» и «кода не вышло» —
        // разные беды, и человеку нужно знать, какая именно. Под маской слова стоят И рядом
        // с нарисованным кодом: код там образец, и об этом надо сказать, а не молчать.
        PhoneQrNoText.Text = masked
            ? PanelStrings.PhoneQrMasked
            : shown.Length == 0
                ? PanelStrings.PhoneQrNoAddress
                : PanelStrings.PhoneQrFailed;

        PhoneQrNoText.IsVisible = png.Length == 0 || masked;
    }

    /// <summary>
    /// БАЙТЫ PNG → КАРТИНКА. Не разобрались — пусто, и раздел скажет словами, что кода нет:
    /// картинка, из-за которой падает окно, недопустима.
    /// </summary>
    private static Bitmap? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);

            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// ПОКАЗАТЬ АДРЕСА ПОД МАСКОЙ — шов ТОЛЬКО для проверок: в жизни маску ставит `Program`
    /// (кадр уезжает в README), а человеку адрес показывается как есть. Отдельной дверью, потому
    /// что «адрес в кадре замаскирован» — утверждение о ПУТИ съёмки, и его надо уметь проверить,
    /// не запуская саму съёмку. Заодно по ней видно, что это прогон съёмки, — и QR рисуется
    /// ИЗ замаскированной строки, а словами сказано, что он образец (<see cref="PanelStrings.PhoneQrMasked"/>).
    /// </summary>
    internal void AttachAddressMaskForTests(Func<string, string>? mask)
    {
        _phoneAddressMask = mask;

        RenderPhoneSection();
    }

    /// <summary>
    /// ЧЕЛОВЕК ВЫБРАЛ АДРЕС ИЗ СПИСКА. Выбор запоминается настройками — он обязан пережить
    /// перезапуск панели, иначе человек выбирал бы его каждый раз заново. Отказ записи говорится
    /// словами: сделать вид, что выбор запомнен, значило бы обещать адрес, которого панель не знает.
    /// </summary>
    private void OnPhoneAddressChosen()
    {
        if (_phoneAddressRendering || _phone is null) return;
        if (PhoneAddressCombo.SelectedItem is not string address) return;
        if (address.Length == 0) return;

        StatusText.Text = _phone.ChoosePhoneAddress(address)
            ? string.Empty
            : PanelStrings.PhoneAddressFailed;

        RenderPhoneSection();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => SavePending();

    // ------------------------------------------------------------------ раздел «Обновление»

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ» — ответ на вопрос «что у меня стоит и что лежит на GitHub», внутри
    /// настроек, как и остальные разделы (решение владельца 28.09.2026).
    ///
    /// **Ни одного своего решения здесь нет.** Сравнение версий, выбор заметок по языку, «пропущена
    /// ли эта версия», «можно ли готовить замену» — всё это живёт в ядре обновления, и раздел
    /// показывает ГОТОВЫЕ ответы чистыми функциями <see cref="UpdateLines"/> (`VerdictLine` и соседи).
    /// Второго сравнения версий в панели быть не должно: оно однажды разошлось бы с первым,
    /// и человек увидел бы два разных ответа об одном выпуске в двух местах.
    ///
    /// ⚠️ **Связки может не быть вовсе** (окно строят проверки вида, съёмка кадра): тогда раздел
    /// честно говорит «в этом прогоне панель замену файлов не готовит», а не молчит и не обещает.
    /// </summary>
    private void RenderUpdateSection()
    {
        // Пришло новое состояние (окно раздела не открыто — контроллер мог сказать о смене) —
        // перерисовать; сам вызов уже на нитке интерфейса, как и все прочие Render.
        var update = _update;
        var result = update?.Result;
        var ok = result?.Ok == true;

        var current = update?.Current ?? AboutInfo.PanelVersion;
        var latest = result?.Latest ?? string.Empty;
        var skipped = update?.Skipped == true;

        UpdateCurrentText.Text = UpdateLines.CurrentLine(current);
        UpdateGithubText.Text = UpdateLines.GithubLine(ok, latest);

        UpdatePublishedText.Text = UpdateLines.PublishedLine(ok, result?.Published);
        UpdatePublishedText.IsVisible = UpdatePublishedText.Text?.Length > 0;

        UpdateVerdictText.Text = UpdateLines.VerdictLine(ok, latest, current, skipped);
        PanelLook.Tone(UpdateVerdictText, UpdateLines.VerdictTone(ok, latest, current, skipped));

        // Строка «когда проверяли» молчит, когда повторяет решение: в непроверенном состоянии
        // они говорили одно и то же ДВАЖДЫ подряд — владелец увидел это на кадре раздела.
        UpdateSourceText.Text = UpdateLines.SourceLine(update?.SourceText, UpdateVerdictText.Text);
        UpdateSourceText.IsVisible = UpdateSourceText.Text.Length > 0;

        // СТРОКА О ПОПЫТКЕ: отдельная правда, а не замена источника состояния. Прежний ответ
        // остаётся выше с датой, а здесь человек видит, что новая попытка не вышла и (после
        // отказа по числу запросов) когда панель попробует снова.
        UpdateAttemptText.Text = update?.AttemptText ?? string.Empty;
        UpdateAttemptText.IsVisible = UpdateAttemptText.Text.Length > 0;

        // ЗАМЕТКИ: своё поле с прокруткой и постоянной высотой. Пустые заметки говорят о себе
        // словами: пустое место читалось бы как «не догрузилось».
        var notes = UpdateNotes.Parse(result?.Notes);
        UpdateNotesView.Fill(UpdateNotesPanel, notes);
        UpdateNotesEmptyText.IsVisible = notes.Count == 0;
        UpdateNotesScroll.IsVisible = notes.Count > 0;

        var assets = result?.Assets ?? ReleaseAssets.None;
        var (ready, hint) = UpdateLines.PrepareState(_install, ok, update?.Newer == true, assets);

        // СОСТОЯНИЕ ПОДГОТОВКИ ЧИТАЕТСЯ ОДИН РАЗ: оно живёт в объекте уровня панели (DEF-058),
        // и его может сменить фоновая нитка между двумя чтениями. Запомненное показанное —
        // чтобы окно не перерисовывало раздел на каждое сообщение хода.
        var busy = _updateState.Busy;
        var preparedAnswer = _updateState.Prepared;
        var status = _updateState.Status;

        _shownUpdate = (busy, preparedAnswer, status);

        UpdateHintText.Text = hint;
        UpdatePrepareButton.Content = busy
            ? PanelStrings.UpdatePreparingButton
            : PanelStrings.UpdatePrepareButton;

        // ПОДСКАЗКА СМЕННОЙ КНОПКИ: пока идёт подготовка, «нажать нельзя» говорит причину —
        // это КОРОТКАЯ дверь отказа второго старта (W0, 2.4.2). Полная причина
        // (`UpdatePreparationBusy`) встаёт только по факту отказа, и двух фраз об одном на
        // экране не бывает.
        PanelToolTip.Set(
            UpdatePrepareButton,
            busy ? PanelStrings.TipUpdatePrepareBusy : PanelStrings.TipUpdatePrepareButton);

        // Подготовленная сборка уже лежит на диске: второй раз готовить нечего, и кнопка молчит
        // делом — она недоступна.
        UpdatePrepareButton.IsEnabled = UpdatePrepareDecisions.ButtonEnabled(ready, busy, preparedAnswer is not null);
        UpdateCheckButton.IsEnabled = update?.Allowed == true;
        PanelToolTip.Set(
            UpdateCheckButton,
            update?.Allowed == true ? PanelStrings.TipUpdateCheckNowButton : PanelStrings.UpdateRefusedLocked);

        // Пропускать нечего, когда обновляться некуда — то же правило и та же причина, что были
        // у прежнего окна обновления (замечание владельца 09.10.2026): кнопка на своём же выпуске
        // обещала бы молчание о версии, которая уже стоит.
        var newer = update?.Newer == true;

        UpdateSkipButton.IsEnabled = newer && !busy;
        PanelToolTip.Set(UpdateSkipButton, UpdateLines.SkipTip(ok, latest, newer));

        UpdateGithubButton.IsEnabled = UpdateLines.GithubAddress(result?.PageUrl).Length > 0;

        UpdateStatusText.Text = status;

        var prepared = preparedAnswer is { Ok: true };

        // Карточка замены — вокруг «последнего нажатия»: появляется ТОЛЬКО после подготовки
        // (до неё показывать нечего), и панель заменяет файлы ровно по этой кнопке.
        UpdateReplaceCard.IsVisible = prepared;
        UpdatePreparedText.IsVisible = prepared;
        UpdateReplaceButton.IsVisible = prepared;

        if (prepared)
        {
            UpdatePreparedText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdatePreparedStagedFormat,
                DisplayMask.Path(preparedAnswer!.StagedFolder));
        }

        UpdateReplaceButton.IsEnabled = prepared;

        RenderProgress();
    }

    /// <summary>
    /// ПОДВЕСТИ КАРТОЧКУ ЗАМЕНЫ К ГЛАЗАМ И ПОСТАВИТЬ НА НЕЁ ФОКУС — после УСПЕШНОЙ подготовки.
    ///
    /// **От какого случая** (пункт 1 владельца 20.10.2026; класс — живое обновление
    /// 2.2.1 → 2.3.0): после «Скачать и подготовить» кнопка гаснет, а кнопка «Обновить
    /// и перезапустить панель» оказывается за краем окна — владелец решил, что обновление
    /// сломалось. Одного переноса карточки под ряд кнопок мало: окно настроек тянется до
    /// `MinHeight 420`, и в нём места может не хватить. Норма банка (`60-states.md` §7):
    /// **действие, появившееся после шага, видно без прокрутки; не помещается — панель
    /// прокручивает к нему и ставит фокус.**
    ///
    /// **Почему фокус — на карточку, а не на её кнопку** (`60-states.md` §12(5), вариант А):
    /// кнопка замены необратима — панель закрывается и подменяется, — и держать её под Enter
    /// сразу после шага значило бы дать одному нажатию запустить замену. Карточка `Focusable`
    /// и в обходе стоит перед кнопкой замены: читалка скажет, что появилось, а Enter/Space
    /// на карточке ничего не делают.
    ///
    /// ⚠️ **Фокус ставится «с клавиатуры»** (<see cref="NavigationMethod.Directional"/>): только
    /// тогда каркас зажигает `:focus-visible` и кольцо фокуса карточки видно. Замер дизайн-лидера
    /// 20.10.2026: `Focusable` + `Focus()` без этого кадр НЕ менял — фокус был невидим.
    ///
    /// ⚠️ **Прокрутка откладывается до конца прохода вёрстки** — как у плана наката в окне копий
    /// (`BackupWindow.ShowPlanOnScreen`): карточка только что стала видимой, вёрстка её ещё
    /// не измерила, и прямой `BringIntoView()` ушёл бы в `ScrollViewer` со старыми координатами.
    /// </summary>
    private void RevealReplaceCard()
    {
        // Человек ушёл в другой раздел (или окно закрыли) — трогать органы нечего: карточка
        // скрыта, фокуса у неё не будет, а прокручивать чужой раздел нельзя.
        if (!IsVisible || !UpdateSectionPanel.IsVisible) return;

        UpdateReplaceCard.Focus(NavigationMethod.Directional);

        if (!UiThread.PostTo(
                _ui,
                () =>
                {
                    if (!UpdateReplaceCard.IsVisible) return;

                    UpdateReplaceCard.BringIntoView();
                },
                DispatcherPriority.Loaded))
        {
            // Ветка НЕДОСТИЖИМА по конструкции (_ui non-nullable), но названа вслух: здесь
            // теряется не занятость окна, а прокрутка к появившемуся действию.
            PanelLog.Write("ОКНО НАСТРОЕК: отложенная прокрутка к карточке замены не отправлена — нитка интерфейса окна недоступна");
        }
    }

    /// <summary>
    /// ВЕРНУТЬ ФОКУС НА ПОВТОРЯЕМОЕ ДЕЙСТВИЕ — после ОТКАЗА подготовки и после исключения фоновой
    /// задачи.
    ///
    /// **От какого случая.** Пока идёт подготовка, кнопка «Скачать и подготовить» выключена
    /// (<see cref="UpdatePrepareDecisions.ButtonEnabled"/>), и фокус с неё снимается — человек нажал
    /// и больше ничего не выбрал. На успехе фокус забирает появившаяся карточка замены
    /// (<see cref="RevealReplaceCard"/>), а на отказе карточки нет — и фокус уходил В НИКУДА, тот же
    /// класс, что замер дизайн-лидера 20.10.2026 «фокус = ничего» (`_team\231\u0-design.md` §2.3).
    /// Замер прогоном 24.10.2026 уточняет это словами: каркас Avalonia фокус на погасшей кнопке
    /// **удерживает**, и «в никуда» он уходит **видимостью** — кольца нет ни на одном органе,
    /// и с клавиатуры человек не видит, где он (WCAG 2.4.7 / F78).
    ///
    /// **Нормы «куда фокус после отказа в настольном окне» в банке НЕТ** — это решение, а не норма:
    /// принят вариант А (`_team\241\d1-normy.md` §1) — фокус на ПОВТОРЯЕМОЕ действие, а причина уже
    /// сказана словами рядом (<see cref="UpdateStatusText"/>). Вариант Б (фокус на строке причины)
    /// требовал бы `Focusable` и кольца у неинтерактивного текста, а урок 231 — «без кольца фокус
    /// невидим». Кнопка же настоящий орган обхода, и в этом состоянии она снова доступна: отказ
    /// движка не отнимает ни права, ни новизны выпуска.
    ///
    /// ⚠️ **Фокус ставится «с клавиатуры»** (<see cref="NavigationMethod.Directional"/>) — по тому же
    /// замеру, что у карточки: без способа получения каркас не зажжёт `:focus-visible`, и кольца
    /// человек не увидит.
    /// </summary>
    private void ReturnFocusToPrepareButton()
    {
        // Человек ушёл в другой раздел (или окно закрыли) — трогать органы нечего: фокуса в чужом
        // разделе не видно, а прокручивать чужой раздел нельзя (та же дверь-сторож, что у карточки).
        if (!IsVisible || !UpdateSectionPanel.IsVisible) return;

        // Повторять нечего: кнопка скрыта или выключена. Так выглядят отказы «нет права»
        // и «обновляться некуда» — они гасят кнопку ДО нажатия, и человек её нажать не мог.
        // Тащить фокус на недоступный орган значило бы потерять его молча: каркас такую
        // попытку отбрасывает.
        if (!UpdatePrepareButton.IsVisible || !UpdatePrepareButton.IsEnabled) return;

        // ⚠️ **ФОКУС МОЖЕТ УЖЕ СТОЯТЬ НА ЭТОЙ КНОПКЕ, НО БЫТЬ НЕВИДИМЫМ.** Каркас не снимает фокус,
        // когда кнопка гаснет на время работы, а повторный `Focus` на уже сфокусированном органе —
        // no-op: способ получения не меняется, и `:focus-visible` не зажигается. Замер прогоном до
        // этой строки: у кнопки остались `:focus` и `:focus-within`, а кольца нет — то есть человек
        // фокуса не видит (WCAG 2.4.7, провал F78), хотя формально «фокус не потерян». Поэтому фокус
        // ставится ЗАНОВО: снять и поставить с клавиатуры. Между двумя вызовами нет ни одного
        // прохода цикла, так что мигания человек не увидит.
        FocusManager?.Focus(null);
        UpdatePrepareButton.Focus(NavigationMethod.Directional);
    }

    /// <summary>«Проверить сейчас»: суточный гейт не спрашивается — человек попросил.</summary>
    public void CheckUpdateNow() => _update?.Check();

    /// <summary>Открыть страницу выпуска в браузере — ТОЛЬКО по щелчку человека.</summary>
    public void OpenUpdateOnGithub()
    {
        var address = UpdateLines.GithubAddress(_update?.Result.PageUrl);
        var error = ProductLinks.Open(address);

        if (error.Length > 0) _updateState.ReportStatus(error);

        RenderUpdateSection();
    }

    /// <summary>
    /// «Пропустить эту версию»: ответ человека, и он запоминается настройками. Записать не удалось
    /// (прогон без права) — раздел говорит об этом: сделать вид, что пропуск запомнен, значило бы
    /// обещать молчание, которого не будет.
    /// </summary>
    public void SkipUpdateVersion()
    {
        var version = (_update?.Result.Latest ?? string.Empty).Trim();
        if (version.Length == 0) return;

        var saved = _skipUpdate?.Invoke(version) ?? false;

        _updateState.ReportStatus(saved
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateSkipDoneFormat,
                version)
            : PanelStrings.UpdateSkipFailed);

        RenderUpdateSection();
    }

    /// <summary>
    /// «Скачать и подготовить» — в фоне: скачивание в сотни мегабайт на нитке интерфейса заморозило
    /// бы окно. Файлы панели при этом НЕ заменяются: замену запускает отдельная кнопка.
    ///
    /// ⚠️ **Работа вынесена в СИНХРОННЫЙ шов <see cref="PrepareNow"/>** — тем же приёмом, каким
    /// это было у прежнего окна обновления (`UpdateWindow.PrepareNow`, убрано 09.10.2026). Шов
    /// заведён потому, что нажатие уходило в фоновую задачу, а дождаться её проверке нечем:
    /// три кнопки раздела остались без прямых проверок. Нажатие и проверка идут ОДНИМ путём —
    /// нажатие дожидается того же шва, а не ходит мимо него.
    ///
    /// ⚠️ **ДВЕРЬ СТАРТА ОДНА — <see cref="PanelUpdateState.TryBegin"/>, и она в ОБЪЕКТЕ ПАНЕЛИ**
    /// (DEF-058/059, 2.4.2), а не в этом окне. Прежний флаг окна разрешал второй старт из нового
    /// окна, хотя первая загрузка шла: две подготовки писали в одни файлы, и вторая удаляла куски
    /// первой. Теперь второй старт возвращает <c>false</c> и говорит причину словами — сколько бы
    /// окон ни было открыто.
    /// </summary>
    public void PrepareUpdate()
    {
        if (_update is null || _install is null) return;

        // ⚠️ ОТКАЗ ВТОРОГО СТАРТА — СЛОВАМИ, А НЕ ГОНКОЙ. Текст причины уже стоит в состоянии
        // (его видит ЛЮБОЕ окно на этом состоянии), и здесь остаётся только показать раздел.
        if (!_updateState.TryBegin())
        {
            RenderUpdateSection();
            return;
        }

        RenderUpdateSection();

        Task.Run(() =>
        {
            try
            {
                PrepareNowCore();
            }
            catch (Exception error)
            {
                // СЫРОЕ ИСКЛЮЧЕНИЕ ЧЕЛОВЕКУ НЕ ПОКАЗЫВАЕТСЯ (DEF-059, 2.4.2). Человеку — слова
                // «причина не названа» и что делать; тип и сообщение — техслед, и его место
                // В ЖУРНАЛЕ, под маской путей (`DisplayMask.PathsIn`), а не в строке окна.
                // Прежде сюда попадало «InvalidOperationException: …», а в отказе загрузки —
                // тип исключения вместе с путём владельца.
                PanelLog.Write(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.UpdateLogFormat,
                    DisplayMask.PathsIn(error.GetType().Name + ": " + error.Message)));

                OnUi(() =>
                {
                    _updateState.Finish(null, PanelStrings.UpdateRefusedUnknown);
                    RenderUpdateSection();

                    // ИСКЛЮЧЕНИЕ — ТОТ ЖЕ ИСХОД, ЧТО ОТКАЗ, и по тому же правилу «одно на все
                    // исходы»: причина сказана словами, фокус возвращается на повторяемое действие.
                    ReturnFocusToPrepareButton();
                });
            }
        });
    }

    /// <summary>
    /// САМА подготовка — без фона: скачать, сверить, распаковать, снять страховочную копию
    /// и написать сценарий замены. Файлы панели не заменяются и здесь: замену запускает человек
    /// отдельной кнопкой.
    ///
    /// ⚠️ Шов проверок: он САМ проходит дверь старта, поэтому «два старта подряд» проверяются
    /// тем же путём, каким идёт нажатие, а не своей выдумкой проверки.
    ///
    /// Ход подготовки приходит от движка ЗВОНКАМИ — и уходит в состояние панели
    /// (<see cref="PanelUpdateState.Report"/>), а не в органы окна: рисовать из фоновой нитки
    /// нельзя, и переводит сообщение в свою нитку уже подписчик состояния (`OnUi`).
    /// </summary>
    internal void PrepareNow()
    {
        if (_update is null || _install is null) return;

        if (!_updateState.TryBegin()) return;

        PrepareNowCore();
    }

    /// <summary>
    /// РАБОТА ПОДГОТОВКИ БЕЗ ДВЕРИ СТАРТА: её зовут после <see cref="PanelUpdateState.TryBegin"/>
    /// и нажатие, и шов проверок. Отдельной дверью — чтобы дверь старта была ОДНА: две копии
    /// «начать» разошлись бы ровно так же, как разошлись флаг окна и настоящая занятость.
    /// </summary>
    private void PrepareNowCore()
    {
        var update = _update;
        var install = _install;

        if (update is null || install is null) return;

        var result = update.Result;
        var preparation = install.Prepare(
            result.Latest,
            result.Assets,
            progress => _updateState.Report(progress));

        OnUi(() =>
        {
            // ⚠️ Отказ НЕ становится «тем, что подготовлено»: это решает сама дверь состояния
            // (`Finish` берёт только удачный ответ) — держать отказ готовым значило бы оставить
            // открытой дверь замены файлов. Отказ человек читает словами
            // (<see cref="UpdateRefusalLines"/>).
            //
            // Ход кончился ЛЮБЫМ исходом — успехом и отказом одинаково: `Finish` забывает снимок
            // хода, и строка с полосой уходят с экрана. Прежде строка оставалась («висит» после
            // подготовки — замечание владельца 20.10.2026). Вызов ОДИН на оба исхода.
            _updateState.Finish(
                preparation,
                preparation.Ok ? string.Empty : UpdateRefusalLines.Line(preparation));

            RenderUpdateSection();

            // Появилось новое действие (карточка замены) — подвести его к глазам и поставить на него
            // фокус. Только на успехе: у отказа карточки нет, и подводить нечего.
            if (preparation.Ok) RevealReplaceCard();

            // ОТКАЗ ФОКУС НЕ ТЕРЯЕТ: кнопка подготовки к этому мгновению погасла и снова доступна,
            // и фокус возвращается на неё — на повторяемое действие (вариант А, d1-normy §1).
            // Причина к этому мгновению уже сказана словами строкой выше.
            else ReturnFocusToPrepareButton();
        });
    }

    /// <summary>
    /// СМЕНА СОСТОЯНИЯ ПОДГОТОВКИ: сообщение может прийти с ФОНОВОЙ нитки (ход загрузки), поэтому
    /// переводим его в свою дверью <see cref="OnUi"/> и только потом трогаем органы.
    /// </summary>
    private void OnUpdateStateChanged() => OnUi(RenderUpdateState);

    /// <summary>Окно закрылось — снять подписку. Состояние панели закрытие окна НЕ трогает (DEF-058).</summary>
    private void OnClosedDetachUpdateState(object? sender, EventArgs e) =>
        _updateState.Changed -= OnUpdateStateChanged;

    /// <summary>
    /// ПОКАЗАТЬ СОСТОЯНИЕ ПОДГОТОВКИ: ход — всегда, раздел целиком — только когда сменилось
    /// НЕ-ХОДОВОЕ (занятость, готовность, строка состояния).
    ///
    /// **Зачем развилка.** Ход приходит на каждый прочитанный буфер куска — сотни сообщений за
    /// одну загрузку. Перерисовывать на каждое весь раздел (с разбором заметок к выпуску) значит
    /// делать работу, которой человек не видит; при этом полоса и строка хода обязаны обновляться
    /// на каждое сообщение — иначе «видно, что делается» превратится в застывшее число.
    /// </summary>
    private void RenderUpdateState()
    {
        RenderProgress();

        var shown = (Busy: _updateState.Busy, Prepared: _updateState.Prepared, Status: _updateState.Status);

        if (shown == _shownUpdate) return;

        RenderUpdateSection();
    }

    /// <summary>
    /// ХОД ПОДГОТОВКИ НА ЭКРАНЕ — ИЗ СНИМКА СОСТОЯНИЯ, а не из органов окна: закрыл окно и открыл
    /// снова — числа на месте, а не с нуля (DEF-058). Пустой снимок — строки нет, полосы нет:
    /// это и есть «ход кончился», одно правило на успех, отказ и исключение.
    /// </summary>
    private void RenderProgress()
    {
        var progress = _updateState.Progress;

        UpdateProgressBar.IsVisible = progress is not null;
        UpdateProgressText.Text = progress is null ? string.Empty : UpdateLines.ProgressLine(progress);

        if (progress is null) return;

        UpdateProgressBar.IsIndeterminate = progress.Percent < 0;

        if (progress.Percent >= 0) UpdateProgressBar.Value = progress.Percent;
    }

    /// <summary>
    /// «Обновить и перезапустить панель» — отдельным нажатием и только после подготовки. При
    /// неудаче раздел говорит причину словами и панель НЕ закрывается.
    /// </summary>
    public void ReplaceUpdate() => ReplaceNow();

    /// <summary>
    /// САМА замена — синхронный шов нажатия; ответ: запущен ли сценарий. Отдельной дверью, а не
    /// прямо в обработчике кнопки, по той же причине, что и у подготовки: проверка проходит ровно
    /// тот же путь и видит и запуск, и просьбу панели закрыться, и слова отказа.
    ///
    /// Ничего не готовили — запускать нечего: «обновить» без подготовленной сборки значило бы
    /// заменить папку неизвестно чем.
    /// </summary>
    internal bool ReplaceNow()
    {
        var install = _install;
        var preparation = _updateState.Prepared;

        if (install is null || preparation is null) return false;

        var error = install.Launch(preparation);

        if (error.Length > 0)
        {
            _updateState.ReportStatus(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateLaunchFailedFormat,
                error));

            RenderUpdateSection();
            return false;
        }

        _updateState.ReportStatus(string.Empty);
        _exitForUpdate?.Invoke();

        return true;
    }

    /// <summary>
    /// Работа на нитке интерфейса: мы на ней — делаем сразу, нет — отправляем дверью
    /// (<see cref="UiThread"/>). Второго обращения к диспетчеру в панели быть не должно, а
    /// «отправить и не знать, приняли ли» здесь не годится: ответ о подготовке — не мелочь.
    /// </summary>
    private void OnUi(Action action)
    {
        if (UiThread.CheckAccess())
        {
            action();
            return;
        }

        if (UiThread.PostTo(_ui, action)) return;

        // Ветка НЕДОСТИЖИМА по конструкции: _ui non-nullable. Но потеря здесь — занятость
        // брандмауэра или раздела обновления навсегда, поэтому она названа строкой журнала
        // (и не исключением: это ответ фоновой задачи, бросок уронил бы процесс).
        PanelLog.Write("ОКНО НАСТРОЕК: ответ фоновой работы не дошёл — нитка интерфейса окна недоступна");
    }

    /// <summary>Строка состояния раздела — для проверок без экрана.</summary>
    public string UpdateStatusLine => _updateState.Status;

    /// <summary>Что подготовлено — для проверок без экрана (пусто — не готовили).</summary>
    public UpdatePreparation? UpdatePrepared => _updateState.Prepared;

    /// <summary>
    /// Смена темы в списке применяется СРАЗУ, не дожидаясь «Сохранить»: человек выбирает тему
    /// глазами, и увидеть её он должен в тот же момент. В файл она попадёт по кнопке.
    /// </summary>
    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingTheme || _settings is null) return;

        _settings.PreviewTheme(PanelSettings.ThemeAt(ThemeBox.SelectedIndex));
    }

    private void OnUseSuggestion(object? sender, RoutedEventArgs e)
    {
        if (_suggestions.Count == 0) return;

        WorkDirBox.Text = _suggestions[0].Path;
        StatusText.Text = PanelStrings.SettingsWorkDirAppliesNextStart;
    }

    private void RenderSuggestions()
    {
        if (_settings is null) return;

        _suggestions = _settings.Suggestions();
        var has = _suggestions.Count > 0;

        SuggestWorkDirButton.IsVisible = has;
        if (has)
        {
            SuggestWorkDirButton.Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.SettingsSuggestFormat,
                _suggestions[0].Source,
                DisplayMask.Path(_suggestions[0].Path));
        }

        // Подписи под полем рабочей папки БОЛЬШЕ НЕТ (замечание владельца 28.09.2026: «Это
        // абсолютно лишнее»). Прежде здесь стояла подсказка про «из этого каталога поднимается
        // сервер», а к ней при наличии предложения дописывалась оговорка «применится при следующем
        // запуске». Строка убрана целиком — и из разметки, и из словарей: подпись объясняла то,
        // что человек и так видит, и занимала место над полем «Порт своего сервера». Оговорка
        // о следующем запуске осталась там, где она что-то говорит: строкой состояния на нажатие
        // «Найдена рабочая папка…». ⚠️ Имени строки в пояснении нет намеренно: ворота «каждая
        // строка где-то используется» читают и комментарии, поэтому названное здесь имя сделало бы
        // мёртвую строку «живой» — ровно там, где проверка и нужна.
    }

    /// <summary>
    /// РАЗДЕЛ «СЕРВЕР»: отчёт окружения КАРТОЧКАМИ. Строки собирает одна дверь отчёта
    /// (<see cref="SettingsController.EnvironmentInfo"/>), раскладывает их
    /// <see cref="EnvironmentPanelView"/> — окно не решает ни что показать, ни как это называется.
    ///
    /// ⚠️ **Дверь открытия папки отдаётся СПИСКУ, а не ставится кнопкой в разметке:** строк
    /// в отчёте девять, у трёх из них есть папка, а решение о ПРАВЕ живёт у владельца настроек
    /// (<c>SettingsController.OpenFolder</c>) — окно только показывает отказ словами.
    /// </summary>
    private void RenderEnvironment()
    {
        if (_settings is null) return;

        EnvironmentPanelView.Fill(EnvironmentPanel, _settings.EnvironmentInfo(), OpenReportFolder);
    }

    /// <summary>Щелчок «Открыть папку» в строке отчёта: удача — пустая строка состояния, отказ — словами.</summary>
    private void OpenReportFolder(string folder)
    {
        if (_settings is null) return;

        StatusText.Text = _settings.OpenFolder(folder, out var error) ? string.Empty : error;
    }

    private void RenderAutostart()
    {
        _updatingAutostart = true;

        AutostartCheck.Content = _autostartModel.CheckText;
        AutostartCheck.IsChecked = _autostartModel.Enabled;
        AutostartCheck.IsEnabled = _autostartModel.CanToggle;
        AutostartNoteText.Text = _autostartModel.NoteText;

        _updatingAutostart = false;
    }

    private void OnAutostartToggled(object? sender, RoutedEventArgs e)
    {
        // Подстановка состояния сама поднимает это событие: без предохранителя панель записала бы
        // в реестр то, что только что прочитала (та же грабля, что была в главном окне).
        if (_updatingAutostart || _autostart is null) return;

        // Меняет запись МОДЕЛЬ, а не окно: она же помнит отказ и показывает его подписью под
        // галочкой. Окно только рисует NoteText — иначе отказ видел бы один журнал,
        // а человек у галочки не увидел бы ничего.
        _autostartModel.Set(AutostartCheck.IsChecked == true);
        RenderAutostart();
    }

    /// <summary>
    /// ДАТЬ ОКНУ ДВЕРЬ БЛОКА ДВИЖКА (её даёт `App`); без неё окно честно говорит, что обновлять нечем.
    /// </summary>
    public void AttachEngineUpdate(IEngineUpdateSection engineUpdate)
    {
        _engineUpdate = engineUpdate;
        RefreshEngineBlock();
    }

    /// <summary>
    /// ДАТЬ ОКНУ ДВЕРЬ ВОПРОСА (проверки подставляют свой ответ; у человека вопрос задаёт
    /// <see cref="ConfirmEngineWindow"/>). Отдельной дверью — потому что «спрашивать ВСЕГДА» это
    /// утверждение о ПУТИ, и его надо уметь проверить, не открывая окно.
    /// </summary>
    public void AttachEngineAsk(Func<PanelQuestion, Task<bool>> ask) => _engineAsk = ask;

    /// <summary>СПРОСИТЬ ЧЕЛОВЕКА: своё окно подтверждения или подставной ответ проверки.</summary>
    private async Task<bool> AskEngine(PanelQuestion question)
    {
        if (_engineAsk is not null) return await _engineAsk(question);

        var window = new ConfirmEngineWindow();
        window.Attach(question);

        return await window.ShowDialog<bool>(this);
    }

    /// <summary>
    /// ПОКАЗАТЬ ДВЕ СТРОКИ: рекомендуемую (проверенную) и свежую — и только если новее.
    ///
    /// ⚠️ Когда обновлять НЕЛЬЗЯ, окно говорит это СЛОВАМИ двери (`RefusalKey`), а не пустыми
    /// строками: у прогона проверки движок не ставится вовсе, и «обновлять некуда: проверенной
    /// версии в подсказках нет» было бы неправдой.
    /// </summary>
    private void RefreshEngineBlock()
    {
        var door = _engineUpdate;

        // ⚠️ СЕРВЕР DSH ПОДНЯТ НЕ ПАНЕЛЬЮ — И ЭТО СКАЗАНО ЗДЕСЬ СЛОВАМИ (решение владельца
        // 13.10.2026, ответ 3). Признак берётся у КАЛИТКИ ТЕЛЕФОНА (`EngineLinked`): дверь событий
        // собирается по ссылке входа, и её отсутствие значит ровно «своей ссылки входа у панели
        // нет» — то есть панель в этот сервер не входит и обновить его не может. Прежде блок
        // в этом положении молчал: человек видел «обновление недоступно» и не знал почему.
        var noOwnServer = _phone is not null && !_phone.PhoneInfo().EngineLinked;

        UpdateEngineNotOwnText.Text = PanelStrings.EngineUpdateNoOwnServer;
        UpdateEngineNotOwnText.IsVisible = noOwnServer;

        // ОТКАТ ЖИВЁТ ОТДЕЛЬНО ОТ ОБНОВЛЕНИЯ: вернуться к прежней версии можно и тогда, когда
        // обновлять нечем (подсказок нет), — это разные вопросы, и путать их нельзя.
        EngineRollbackButton.IsVisible = door is not null && door.RollbackAvailable;
        EngineRollbackButton.IsEnabled = !_engineBusy;
        EngineRollbackButton.Content = door is not null && door.RollbackTarget.Length > 0
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture, PanelStrings.EngineRollbackButtonFormat, door.RollbackTarget)
            : PanelStrings.EngineRollbackButtonFormat;

        // ВЫЖИМКА И ССЫЛКА — от той же двери и независимо от того, есть ли чем обновлять: заметки
        // про версию и место, где человек посмотрит подробности, нужны и когда кнопка молчит.
        RenderEngineNotes(door?.Notes);
        RenderEngineReleases(door?.ReleasesAddress);

        // СВОЯ ВЕРСИЯ — ПЕРВОЙ СТРОКОЙ, всегда, когда дверь отвечает (решение владельца 14.10.2026).
        // Прежде её на карточке не было вовсе: человек видел «рекомендуемая 0.1.5-rc.2» и кнопку
        // «Обновить» — в том числе тогда, когда у него стояла ровно эта же версия.
        //
        // ⚠️ СОСТОЯНИЕ СЧИТАЕТСЯ ОДИН РАЗ И ЗДЕСЬ, а не в трёх местах по отдельности: сравнение
        // с рекомендуемой, факт присутствия и подпись кнопки обязаны говорить ОДНО (A5 §7).
        // Факт присутствия приходит от двери отдельно (`EnginePresent`); «сервера нет» и «версию
        // не прочитали» — РАЗНЫЕ состояния, и слитые в строку версии они врут в обе стороны.
        var installed = door?.Installed ?? string.Empty;
        var standing = door is null
            ? EngineVersionStanding.Unknown
            : EngineUpdateDecisions.Standing(installed, door.Recommended, door.EnginePresent);

        EngineRecommendedText.Text = noOwnServer
            ? string.Empty

            // СЕРВЕРА DSH НЕТ ВОВСЕ — свой слот: «не найден», а не «версию прочитать не удалось».
            // Слова не говорят «не установлен»: панель не ставит машине диагноз.
            : standing == EngineVersionStanding.Absent
                ? PanelStrings.EngineOwnVersionAbsent
                : installed.Length > 0
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture, PanelStrings.EngineOwnVersionFormat, installed)
                    : PanelStrings.EngineOwnVersionUnknown;

        // Свежая версия и кнопка главного действия решаются НИЖЕ, по состоянию: у отказа и у
        // «обновлять нечего» их быть не должно, а забытая видимость кнопки — это и есть дефект
        // «предлагает обновить на то же самое». Поэтому гасим ЗДЕСЬ, до всех веток.
        EngineFreshLineText.Text = string.Empty;
        EngineUpdateButton.IsVisible = false;

        if (noOwnServer)
        {
            // ОБНОВЛЯТЬ НЕЧЕМ: панель в этот сервер не входит. Кнопки гасим, а причину и путь
            // («в разделе „Сервер“») называет строка выше — она и есть ответ человеку.
            EngineFreshText.Text = string.Empty;
            EngineHintText.Text = string.Empty;
            EngineInstallFreshButton.IsVisible = false;
            EngineUpdateButton.IsEnabled = false;
            EngineStatusText.Text = string.Empty;
            EngineNotesShowAllButton.IsVisible = false;
            ShowEngineNotes(false);
            return;
        }

        EngineNotesShowAllButton.IsVisible = true;

        if (door is null || !door.Available)
        {
            EngineFreshText.Text = door is null || door.RefusalKey.Length == 0
                ? PanelStrings.EngineUpdateNoRecommended
                : EngineInstallPhrases.UpdateRefusal(door.RefusalKey);
            EngineInstallFreshButton.IsVisible = false;
            EngineUpdateButton.IsEnabled = false;

            // ОБНОВЛЯТЬ НЕЧЕМ — и подсказки про кнопки нет: она звала бы кнопку, которой на экране
            // нет (тот же класс, что находка F-1). Слова о причине уже сказаны строкой выше.
            EngineHintText.Text = string.Empty;
            EngineStatusText.Text = string.Empty;
            return;
        }

        // ЧТО СКАЗАТЬ ПРО ОБНОВЛЕНИЕ — из сравнения СВОЕЙ версии с рекомендуемой. Три состояния,
        // и «обновлять нечего» — одно из них, а не молчание карточки. Состояние посчитано ВЫШЕ
        // и один раз (вместе с фактом присутствия): второй расчёт рядом разошёлся бы с первым.
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        EngineFreshText.Text = standing switch
        {
            EngineVersionStanding.Same => PanelStrings.EngineUpdateSameLine,

            // СВОЯ НОВЕЕ — панель свою версию не понижает (решение владельца 14.10.2026):
            // понижение это его решение, а не щелчок панели, и кнопки здесь нет.
            EngineVersionStanding.Newer => string.Format(
                culture, PanelStrings.EngineUpdateNewerFormat, installed, door.Recommended),

            // Версию не прочитали — говорим рекомендацию и оставляем кнопку: поставить или
            // починить сервер DSH этим и надо. Сравнивать не с чем, и это названо первой строкой.
            _ => string.Format(culture, PanelStrings.EngineUpdateRecommendedFormat, door.Recommended),
        };

        // КНОПКА ГЛАВНОГО ДЕЙСТВИЯ — РОВНО ДВА СОСТОЯНИЯ, и оба ведут к УСТАНОВКЕ ПРОВЕРЕННОЙ
        // версии (действие одно — `RunEngineUpdate(freshWanted: false)`):
        //   * «стоит старше»    — «Обновить»;
        //   * «сервера нет»     — «Установить» (A5 §7): обновлять нечего, и подпись обязана
        //                          называть то, что произойдёт. Это и есть выход из тупика:
        //                          прежде в этом состоянии не было НИ ОДНОЙ кнопки.
        // Ни «то же самое», ни «своя новее» кнопки не получают: первое — обновлять нечего,
        // второе — понижение, а его панель не делает.
        var installAction = standing is EngineVersionStanding.Older or EngineVersionStanding.Absent;

        EngineUpdateButton.IsVisible = installAction;
        EngineUpdateButton.IsEnabled = !_engineBusy;
        EngineUpdateButton.Content = standing == EngineVersionStanding.Absent
            ? PanelStrings.EngineInstallButton
            : PanelStrings.EngineUpdateButton;

        // ПОДСКАЗКА КАРТОЧКИ — ПО СОСТОЯНИЮ (находка F-1). Прежде она собиралась БЕЗУСЛОВНО и звала
        // кнопку «Обновить», а в состоянии «сервера нет» кнопка подписана «Установить» — подсказка
        // начинала врать ровно в том состоянии, ради которого делалась установка. И вторая половина
        // подсказки (про непроверенную версию) видна только вместе со СВОЕЙ кнопкой.
        PanelToolTip.Set(
            EngineUpdateButton,
            standing == EngineVersionStanding.Absent
                ? PanelStrings.TipEngineInstallButton
                : PanelStrings.TipEngineUpdateButton);

        // ⚠️ СВЕЖАЯ БЕЗ ЗАМКА: строку показываем, а КНОПКИ НЕТ. Панель ставит движок только по замку,
        // и кнопка, которая всегда отказывает, — ловушка; причина сказана словами в самой строке.
        //
        // ⚠️ И В СОСТОЯНИИ «СЕРВЕРА DSH НЕТ» ЕЁ НЕТ ВОВСЕ (A5 §7): у незнакомца на чистой машине
        // выбор «поставить непроверенную» — вредный совет, а не забота; проверенная версия у него
        // единственная дверь. Заодно это держит подсказку честной: она рассказывает только о том,
        // что человек видит.
        // ⚠️ И СРАВНЕНИЕ СО СТОЯЩЕЙ (находка F-1, слово владельца 20.10.2026). Прежнее правило
        // смотрело только на рекомендованную версию — и на скрине владельца кнопка «Поставить
        // непроверенную…» была видна и «что-то делала», хотя ставить было нечего: стояла 0.2.1-rc.2,
        // свежая в реестре — ТА ЖЕ 0.2.1-rc.2, рекомендованная — 0.1.5-rc.2. Теперь свежая обязана
        // быть новее И рекомендуемой, И стоящей: первая половина — готовый ответ двери, вторая —
        // правило панели (`EngineUpdateDecisions.FreshWorthShowing`). Стоящая не прочитана — «новее»
        // не доказано, и кнопки нет (главная кнопка при этом остаётся).
        var freshVisible = standing != EngineVersionStanding.Absent
                           && door.FreshInstallable
                           && EngineUpdateDecisions.FreshWorthShowing(door.FreshIsNewer, installed, door.Fresh);

        EngineFreshLineText.Text = door.FreshIsNewer
            ? string.Format(
                culture,
                door.FreshInstallable ? PanelStrings.EngineUpdateFreshLineFormat : PanelStrings.EngineUpdateFreshNoLockFormat,
                door.Fresh)
            : string.Empty;

        EngineInstallFreshButton.IsVisible = freshVisible;
        EngineInstallFreshButton.IsEnabled = !_engineBusy;
        EngineInstallFreshButton.Content = PanelStrings.EngineUpdateFreshButton;

        // ПОДСКАЗКА КАРТОЧКИ — ПО СОСТОЯНИЮ (находка F-1). Прежде она собиралась БЕЗУСЛОВНО и звала
        // кнопку «Обновить», а в состоянии «сервера нет» кнопка подписана «Установить» — подсказка
        // начинала врать ровно в том состоянии, ради которого делалась установка. И вторая половина
        // подсказки (про непроверенную версию) видна только вместе со СВОЕЙ кнопкой.
        EngineHintText.Text = EngineHintParts(standing, freshVisible);

        EngineStatusText.Text = string.Empty;
    }

    /// <summary>
    /// ПОДСКАЗКА КАРТОЧКИ ДВИЖКА — ПО СОСТОЯНИЮ, ОДНИМ МЕСТОМ (находка F-1).
    ///
    /// Правило простое и названное: подсказка говорит про ТЕ кнопки, которые человек видит.
    /// В состоянии «сервера DSH нет» это «Установить» (и только она), в остальных — «Обновить»,
    /// а вторая половина (про непроверенную версию) — только когда её кнопка действительно видима.
    ///
    /// ⚠️ Отдельным членом, а не строкой в отрисовке: иначе проверке до правила не дотянуться —
    /// окно на проверках собирается без двери движка, и «подсказка совпадает с подписью» нечем измерить.
    /// </summary>
    internal static string EngineHintParts(EngineVersionStanding standing, bool freshButtonVisible)
    {
        var main = standing == EngineVersionStanding.Absent
            ? PanelStrings.EngineInstallHint
            : PanelStrings.EngineUpdateMainHint;

        // ⚠️ ДВЕ ФРАЗЫ — ДВЕ СТРОКИ (находка F-1, слово владельца 20.10.2026). Прежде они
        // склеивались ПРОБЕЛОМ, и на карточке получалось «…панель проверила сама непроверенная
        // версия ставится только этой кнопкой…» — две мысли в одном предложении. Вторая строка
        // видна ТОЛЬКО вместе со своей кнопкой: правило F-1 действует и здесь.
        return freshButtonVisible
            ? main + System.Environment.NewLine + PanelStrings.EngineUpdateFreshHint
            : main;
    }

    /// <summary>
    /// ПОКАЗАТЬ ВЫЖИМКУ ПРО ВЕРСИИ: заметки и беды из файла подсказок — строками в СВОЁМ поле.
    /// Пусто — говорим словами, что заметок нет: пустое поле человек прочитал бы как «всё хорошо».
    /// </summary>
    private void RenderEngineNotes(IReadOnlyList<string>? notes)
    {
        EngineNotesPanel.Children.Clear();

        var lines = notes ?? Array.Empty<string>();

        // ⚠️ Свёрнутая карточка ОСТАЁТСЯ свёрнутой: отрисовка сама видимость НЕ включает (иначе
        // первая же перерисовка раскрыла бы выжимку обратно и выдавила кнопки вниз).
        ShowEngineNotes(EngineNotesTitleText.IsVisible);

        foreach (var line in lines)
            EngineNotesPanel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>
    /// ПОКАЗАТЬ ССЫЛКУ НА ВЫПУСКИ ДВИЖКА. Пустой адрес — ссылки нет вовсе: «ссылка в никуда» хуже
    /// её отсутствия. Открывает её <see cref="OpenEngineReleases"/> — и только по щелчку человека.
    /// </summary>
    private void RenderEngineReleases(string? address)
    {
        EngineReleasesButton.IsVisible = ProductLinks.ShouldShow(address);
    }

    /// <summary>
    /// ОТКРЫТЬ СТРАНИЦУ ВЫПУСКОВ ДВИЖКА В БРАУЗЕРЕ — **только по щелчку человека**, тем же путём,
    /// что страница выпусков панели: панель сама по этому адресу не ходит и ничего туда не шлёт.
    /// Неудача открытия говорится словами в строке состояния блока, а не молчанием.
    /// </summary>
    public void OpenEngineReleases()
    {
        var address = _engineUpdate?.ReleasesAddress;

        if (!ProductLinks.ShouldShow(address)) return;

        var error = (_engineBrowse ?? ProductLinks.Open)(address!);

        if (error.Length > 0) EngineStatusText.Text = error;
    }

    /// <summary>
    /// ДАТЬ ОКНУ ДВЕРЬ БРАУЗЕРА (проверки подставляют свой ответ: настоящий щелчок открыл бы браузер
    /// у человека). Отдельной дверью — потому что «открывается ТОЛЬКО по щелчку» это утверждение
    /// о пути, и его надо уметь проверить, не открывая ничего.
    /// </summary>
    public void AttachEngineBrowse(Func<string, string> browse) => _engineBrowse = browse;

    /// <summary>
    /// НАЖАТИЕ. Порядок один и тот же у обеих кнопок, и он — исполнение решений владельца:
    /// 1. **спросить ВСЕГДА** (<see cref="ConfirmEngineWindow"/>) — признака «идёт работа» у панели
    ///    нет, поэтому спрашиваем перед каждой подменой версии;
    /// 2. **работать в стороне от нитки окна**: установка и проба занимают минуты, и замершее окно
    ///    «не отвечает» — это худший вид поломки (правило шва: правда о работе видна на живом пути);
    /// 3. у отдельной кнопки непроверенной версии **сначала подтверждение словами** (разово,
    ///    без галочек и «запомнить»).
    /// </summary>
    private async void RunEngineUpdate(bool freshWanted)
    {
        var door = _engineUpdate;

        if (door is null || !door.Available)
        {
            EngineStatusText.Text = door is null || door.RefusalKey.Length == 0
                ? PanelStrings.EngineUpdateNoRecommended
                : EngineInstallPhrases.UpdateRefusal(door.RefusalKey);
            return;
        }

        if (_engineBusy) return;

        if (freshWanted && !_engineFreshConfirmed)
        {
            EngineStatusText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.EngineUpdateFreshWarningFormat, door.Fresh);
            EngineInstallFreshButton.Content = PanelStrings.EngineUpdateFreshConfirmButton;
            _engineFreshConfirmed = true;
            return;
        }

        // (1) СПРОСИТЬ ВСЕГДА: без ответа «да» не начинается ничего. Вопрос собирает ДВЕРЬ — она
        // знает, какая версия будет поставлена (`UpdateQuestionFor`) и что станется с журналом
        // сессий (блок C: формат v4 и молчаливый откат). Пока окно собирало вопрос САМО, подменяя
        // текст свежей версией, всё, что дверь добавляла к вопросу, до человека не доходило —
        // у отдельной кнопки терялось предупреждение о журнале (находка блока C).
        var question = door.UpdateQuestionFor(freshWanted);

        if (!await AskEngine(question))
        {
            _engineFreshConfirmed = false;
            EngineInstallFreshButton.Content = PanelStrings.EngineUpdateFreshButton;
            EngineStatusText.Text = PanelStrings.EngineUpdateAskCancelled;
            return;
        }

        // (2) Работа — в стороне: окно остаётся живым и говорит, что идёт и почему это не мгновение.
        _engineBusy = true;
        EngineUpdateButton.IsEnabled = false;
        EngineInstallFreshButton.IsEnabled = false;
        EngineStatusText.Text = PanelStrings.EngineUpdateWorking;

        string answer;

        try
        {
            answer = await System.Threading.Tasks.Task.Run(() => door.Run(freshWanted));
        }
        catch (Exception error)
        {
            // Путь обновления исключений не бросает (беда — это ответ), но окно не имеет права
            // замолчать: человек должен увидеть, что работа не сделана.
            answer = error.GetType().Name + ": " + error.Message;
        }

        _engineBusy = false;
        _engineFreshConfirmed = false;
        EngineInstallFreshButton.Content = PanelStrings.EngineUpdateFreshButton;

        RefreshEngineBlock();

        // ⚠️ Строку ответа ставим ПОСЛЕ перерисовки блока: перерисовка чистит строку состояния.
        EngineStatusText.Text = answer;
    }

    /// <summary>
    /// ОТКАТ ПО КНОПКЕ (этап E3.3): тот же порядок, что у обновления — **спросить ВСЕГДА** и работать
    /// в стороне от нитки окна. Вопрос здесь другой и он — суть отката: **сколько сессий прежний
    /// сервер DSH не увидит**. Число даёт дверь (`EngineJournalFuse`, снимает с диска), а не окно:
    /// замер `l2` показал, что движок об этом молчит (197 → 196 без поля и без слова), поэтому
    /// предупредить может только панель.
    /// </summary>
    private async void RunEngineRollback()
    {
        var door = _engineUpdate;

        if (door is null || _engineBusy) return;

        if (!door.RollbackAvailable)
        {
            EngineStatusText.Text = door.RollbackQuestion.Text;
            return;
        }

        if (!await AskEngine(door.RollbackQuestion))
        {
            EngineStatusText.Text = PanelStrings.EngineUpdateAskCancelled;
            return;
        }

        _engineBusy = true;
        EngineUpdateButton.IsEnabled = false;
        EngineInstallFreshButton.IsEnabled = false;
        EngineRollbackButton.IsEnabled = false;
        EngineStatusText.Text = PanelStrings.EngineUpdateWorking;

        string answer;

        try
        {
            answer = await System.Threading.Tasks.Task.Run(() => door.Rollback());
        }
        catch (Exception error)
        {
            answer = error.GetType().Name + ": " + error.Message;
        }

        _engineBusy = false;

        RefreshEngineBlock();

        EngineStatusText.Text = answer;
    }
}
