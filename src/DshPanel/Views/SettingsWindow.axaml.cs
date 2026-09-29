using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
public partial class SettingsWindow : Window, IQuietClose
{
    private ISettingsControl? _settings;
    private IAutostartControl? _autostart;
    private IBalanceControl? _balance;
    private IPricingControl? _pricing;
    private AutostartPanelModel _autostartModel = new();

    /// <summary>Проверка выпусков для РАЗДЕЛА «Обновление» — тот же контроллер, что у окна обновления.</summary>
    private IUpdateControl? _update;

    /// <summary>Движок установки для того же раздела: готовит замену файлов и запускает сценарий.</summary>
    private IUpdateInstall? _install;

    /// <summary>«Запомнить пропущенную версию» — владелец настроек, как у окна обновления.</summary>
    private Func<string, bool>? _skipUpdate;

    /// <summary>
    /// Закрыть панель ПОСЛЕ запуска сценария замены — та же дверь, что у окна обновления
    /// (<c>PanelShell.Exit</c>). Единственный случай, когда панель просят завершиться, и зовётся
    /// он ровно по щелчку человека.
    /// </summary>
    private Action? _exitForUpdate;

    private bool _updateBusy;
    private UpdatePreparation? _updatePrepared;
    private string _updateStatus = string.Empty;
    private IReadOnlyList<WorkDirSuggestion> _suggestions = Array.Empty<WorkDirSuggestion>();

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
        ThemeLabel.Text = PanelStrings.SettingsThemeLabel;
        LanguageLabel.Text = PanelStrings.SettingsLanguageLabel;
        LanguageHintText.Text = PanelStrings.SettingsLanguageAppliesNextStart;
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
        PeakWhereHintText.Text = PanelStrings.SettingsPeakWhereHint;

        ServerSectionTitle.Text = PanelStrings.SettingsServerSection;
        ServerSectionSubtitleText.Text = PanelStrings.ServerSectionSubtitle;
        CheckEnvironmentButton.Content = PanelStrings.SettingsCheckEnvironment;
        SaveButton.Content = PanelStrings.SettingsSave;

        // РАЗДЕЛ «ОБНОВЛЕНИЕ»: подписи ставятся здесь, подписи и подсказки — из словаря. Тексты
        // те же, что у окна обновления, где они означают то же самое: два набора слов для одного
        // действия однажды разошлись бы.
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

        UpdateNotesTitleText.Text = PanelStrings.UpdateNotesTitle;
        UpdateNotesEmptyText.Text = PanelStrings.UpdateNotesEmpty;

        UpdateReplaceButton.Content = PanelStrings.UpdateReplaceButton;
        PanelToolTip.Set(UpdateReplaceButton, PanelStrings.TipUpdateReplaceButton);
        UpdateReplaceButton.Click += (_, _) => ReplaceUpdate();

        // ДВЕРЬ В «О ПРОГРАММЕ» — та же подпись, что была у двери в главном окне и что стоит
        // у пункта меню значка: дверь одна, и три разных текста для неё разошлись бы.
        AboutButton.Content = PanelStrings.AboutButton;

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
        BackupScheduleHintText.Text = PanelStrings.SettingsBackupScheduleHint;
        BackupKeepHintText.Text = PanelStrings.SettingsBackupKeepHint;
        BackupLiveNoteText.Text = PanelStrings.SettingsBackupLiveNote;

        // Состав копии: ключи. Раздел появился 26.09.2026 вместе с решением владельца «ключи снова
        // входят в копию, как в v1», и живёт он здесь потому, что это НАСТРОЙКА: окно копий
        // показывает то же значение, но меняет его через владельца настроек.
        KeysSectionTitle.Text = PanelStrings.SettingsKeysSection;
        BackupKeysCheck.Content = PanelStrings.BackupKeysCheck;
        BackupKeysWarningText.Text = PanelStrings.BackupKeysWarning;
        BackupKeysDirsLabel.Text = PanelStrings.SettingsKeysDirsLabel;
        BackupKeysDirsHint.Text = PanelStrings.SettingsKeysDirsHint;

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
        PanelToolTip.Set(AboutButton, PanelStrings.TipAboutButton);

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

        // Дверь «О программе» — как кнопка настроек в главном окне: она не имеет отношения
        // ни к серверу, ни к балансу, и открываться обязана всегда.
        AboutButton.Click += (_, _) => AboutRequested?.Invoke();
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
    /// Человек попросил окно «О программе». Окно настроек, как и главное, ничего не решает:
    /// оно сообщает о просьбе связке, а та выводит на передний план уже открытое окно
    /// (или открывает его впервые). Второго окна «О программе» в панели быть не должно.
    /// </summary>
    public event Action? AboutRequested;

    /// <summary>
    /// Номера разделов. Числа, а не строки: по строкам решение о разделе зависело бы от языка,
    /// и «Общее» на китайском перестало бы быть первым.
    ///
    /// «Резервное копирование» открыто наружу: это САМЫЙ ДЛИННЫЙ раздел, и съёмка кадра
    /// фотографирует именно его — только на нём видно и полосу прокрутки, и то, что она
    /// не наезжает на поля (жалоба владельца 27.09.2026).
    /// </summary>
    private const int GeneralSection = 0;

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

    /// <summary>Подписи пяти разделов — в порядке списка слева.</summary>
    private static string[] SectionTitles() => new[]
    {
        PanelStrings.SettingsGeneral,
        PanelStrings.BackupSectionTitle,
        PanelStrings.SettingsBalanceSection,
        PanelStrings.SettingsServerSection,
        PanelStrings.UpdateSectionTitle,
    };

    /// <summary>Панели разделов — в том же порядке, что и подписи.</summary>
    private StackPanel[] SectionPanels() => new[]
    {
        GeneralSectionPanel,
        BackupSectionPanel,
        BalanceSectionPanel,
        ServerSectionPanel,
        UpdateSectionPanel,
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

        return chosen;
    }

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

        // Размер и положение ГЛАВНОГО окна в этом окне не показываются — но обязаны пройти
        // через сохранение НЕТРОНУТЫМИ, по той же причине: окно пишет настройки целиком,
        // и обнулять геометрию каждым «Сохранить» значило бы молча терять её.
        // Проверка — «Сохранение_из_окна_не_забывает_размер_и_положение_окна».
        WindowX = _settings?.Settings.WindowX ?? 0,
        WindowY = _settings?.Settings.WindowY ?? 0,
        WindowWidth = _settings?.Settings.WindowWidth ?? 0,
        WindowHeight = _settings?.Settings.WindowHeight ?? 0,
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
        Func<string, bool>? skipUpdate = null,
        Action? exitForUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _autostart = autostart;
        _balance = balance;
        _pricing = pricing;

        // РАЗДЕЛ «ОБНОВЛЕНИЕ» — решение владельца 28.09.2026. Ему нужны ТЕ ЖЕ два домена, что окну
        // обновления: состояние выпусков и движок установки. Ни сервера, ни копий ему не нужно.
        if (_update is not null) _update.Changed -= RenderUpdateSection;

        _update = update;
        _install = install;
        _skipUpdate = skipUpdate;
        _exitForUpdate = exitForUpdate;

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
            : PanelStrings.BackupFolderCustomHint;
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
        AgentHintText.Text = PanelStrings.SettingsActiveAgentHint;

        BalanceAutoCheck.IsChecked = settings.BalanceAutoRefresh;
        RefreshMinutesBox.Value = PanelSettings.ClampRefreshMinutes(settings.BalanceRefreshMinutes);

        BalanceWarnCheck.IsChecked = settings.BalanceWarnEnabled;
        ThresholdBox.Value = settings.BalanceWarnThreshold;

        PeakNotifyBox.Value = PanelSettings.ClampPeakNotify(settings.PeakNotifyMinutes);
        PeakNotifyHintText.Text = PanelStrings.SettingsPeakNotifyHint;
    }

    private void OnSave(object? sender, RoutedEventArgs e) => SavePending();

    // ------------------------------------------------------------------ раздел «Обновление»

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ» — ответ на вопрос «что у меня стоит и что лежит на GitHub», внутри
    /// настроек, как и остальные разделы (решение владельца 28.09.2026).
    ///
    /// **Ни одного своего решения здесь нет.** Сравнение версий, выбор заметок по языку, «пропущена
    /// ли эта версия», «можно ли готовить замену» — всё это живёт в ядре обновления, и раздел
    /// показывает ГОТОВЫЕ ответы теми же чистыми функциями, что окно обновления
    /// (<see cref="UpdateWindow.VerdictLine"/> и соседи). Второго сравнения версий в панели быть
    /// не должно: оно однажды разошлось бы с первым, и человек увидел бы два разных ответа
    /// об одном выпуске в двух местах.
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

        var current = update?.Current ?? AboutWindow.PanelVersion;
        var latest = result?.Latest ?? string.Empty;
        var skipped = update?.Skipped == true;

        UpdateCurrentText.Text = UpdateWindow.CurrentLine(current);
        UpdateGithubText.Text = UpdateWindow.GithubLine(ok, latest);

        UpdatePublishedText.Text = UpdateWindow.PublishedLine(ok, result?.Published);
        UpdatePublishedText.IsVisible = UpdatePublishedText.Text?.Length > 0;

        UpdateVerdictText.Text = UpdateWindow.VerdictLine(ok, latest, current, skipped);
        PanelLook.Tone(UpdateVerdictText, UpdateWindow.VerdictTone(ok, latest, current, skipped));

        UpdateSourceText.Text = update?.SourceText ?? PanelStrings.UpdateNeverChecked;

        // ЗАМЕТКИ: своё поле с прокруткой и постоянной высотой — как в окне обновления. Пустые
        // заметки говорят о себе словами: пустое место читалось бы как «не догрузилось».
        var notes = UpdateNotes.Parse(result?.Notes);
        UpdateNotesView.Fill(UpdateNotesPanel, notes);
        UpdateNotesEmptyText.IsVisible = notes.Count == 0;
        UpdateNotesScroll.IsVisible = notes.Count > 0;

        var assets = result?.Assets ?? ReleaseAssets.None;
        var (ready, hint) = UpdateWindow.PrepareState(_install, ok, assets);

        UpdateHintText.Text = hint;
        UpdatePrepareButton.Content = _updateBusy
            ? PanelStrings.UpdatePreparingButton
            : PanelStrings.UpdatePrepareButton;

        // Подготовленная сборка уже лежит на диске: второй раз готовить нечего, и кнопка молчит
        // делом — она недоступна.
        UpdatePrepareButton.IsEnabled = ready && !_updateBusy && _updatePrepared is null;
        UpdateCheckButton.IsEnabled = update?.Allowed == true;
        PanelToolTip.Set(
            UpdateCheckButton,
            update?.Allowed == true ? PanelStrings.TipUpdateCheckNowButton : PanelStrings.UpdateRefusedLocked);

        UpdateSkipButton.IsEnabled = ok && latest.Trim().Length > 0 && !_updateBusy;
        UpdateGithubButton.IsEnabled = UpdateWindow.GithubAddress(result?.PageUrl).Length > 0;

        UpdateStatusText.Text = _updateStatus;

        var prepared = _updatePrepared is { Ok: true };

        UpdatePreparedText.IsVisible = prepared;
        UpdateReplaceButton.IsVisible = prepared;

        if (prepared)
        {
            UpdatePreparedText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdatePreparedStagedFormat,
                DisplayMask.Path(_updatePrepared!.StagedFolder));
        }

        UpdateReplaceButton.IsEnabled = prepared;
    }

    /// <summary>«Проверить сейчас»: суточный гейт не спрашивается — человек попросил.</summary>
    public void CheckUpdateNow() => _update?.Check();

    /// <summary>Открыть страницу выпуска в браузере — ТОЛЬКО по щелчку человека.</summary>
    public void OpenUpdateOnGithub()
    {
        var address = UpdateWindow.GithubAddress(_update?.Result.PageUrl);
        var error = ProductLinks.Open(address);

        if (error.Length > 0) _updateStatus = error;

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

        _updateStatus = saved
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateSkipDoneFormat,
                version)
            : PanelStrings.UpdateSkipFailed;

        RenderUpdateSection();
    }

    /// <summary>
    /// «Скачать и подготовить» — в фоне: скачивание в сотни мегабайт на нитке интерфейса заморозило
    /// бы окно. Файлы панели при этом НЕ заменяются: замену запускает отдельная кнопка.
    /// </summary>
    public void PrepareUpdate()
    {
        if (_update is null || _install is null) return;

        _updateBusy = true;
        _updatePrepared = null;
        _updateStatus = string.Empty;
        RenderUpdateSection();

        Task.Run(() =>
        {
            try
            {
                var result = _update.Result;
                var preparation = _install.Prepare(
                    result.Latest,
                    result.Assets,
                    progress => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        UpdateProgressBar.IsVisible = true;
                        UpdateProgressText.Text = UpdateWindow.ProgressLine(progress);
                        UpdateProgressBar.IsIndeterminate = progress.Percent < 0;

                        if (progress.Percent >= 0) UpdateProgressBar.Value = progress.Percent;
                    }));

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _updatePrepared = preparation;

                    if (!preparation.Ok)
                    {
                        _updateStatus = UpdateRefusalLines.Line(preparation);
                    }

                    _updateBusy = false;
                    UpdateProgressBar.IsVisible = false;
                    RenderUpdateSection();
                });
            }
            catch (Exception error)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _updateStatus = $"{error.GetType().Name}: {error.Message}";
                    _updateBusy = false;
                    UpdateProgressBar.IsVisible = false;
                    RenderUpdateSection();
                });
            }
        });
    }

    /// <summary>
    /// «Обновить и перезапустить панель» — отдельным нажатием и только после подготовки. При
    /// неудаче раздел говорит причину словами и панель НЕ закрывается.
    /// </summary>
    public void ReplaceUpdate()
    {
        var install = _install;
        var preparation = _updatePrepared;

        if (install is null || preparation is null) return;

        var error = install.Launch(preparation);

        if (error.Length > 0)
        {
            _updateStatus = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateLaunchFailedFormat,
                error);

            RenderUpdateSection();
            return;
        }

        _updateStatus = string.Empty;
        _exitForUpdate?.Invoke();
    }

    /// <summary>Строка состояния раздела — для проверок без экрана.</summary>
    public string UpdateStatusLine => _updateStatus;

    /// <summary>Что подготовлено — для проверок без экрана (пусто — не готовили).</summary>
    public UpdatePreparation? UpdatePrepared => _updatePrepared;

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
    /// РАЗДЕЛ «СЕРВЕР»: отчёт окружения ГРУППАМИ (просьба владельца 28.09.2026). Строки собирает
    /// одна дверь отчёта (<see cref="SettingsController.EnvironmentInfo"/>), раскладывает их
    /// <see cref="EnvironmentPanelView"/> — окно не решает ни что показать, ни как это называется.
    /// </summary>
    private void RenderEnvironment()
    {
        if (_settings is null) return;

        EnvironmentPanelView.Fill(EnvironmentPanel, _settings.EnvironmentInfo());
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
}
