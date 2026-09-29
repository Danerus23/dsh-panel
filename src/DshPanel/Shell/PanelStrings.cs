using DshPanel.Localization;

namespace DshPanel.Shell;

/// <summary>
/// Строки интерфейса панели — в одном месте, по одной на КЛЮЧ СЛОВАРЯ.
///
/// Здесь нет ни одного текста: имя члена и есть ключ, а сам текст лежит в
/// <c>Localization\ru.json</c> (и в двух соседних словарях). Поэтому «строка в коде напрямую»
/// больше не существует как вид: чтобы показать человеку что-то, надо завести член здесь
/// и запись в словаре, и разойтись им уже нечем.
///
/// **Имя члена = ключ словаря.** Это правило, а не совпадение: ключи берутся у этого типа
/// отражением (<c>--lang-selftest</c>, <c>tests\DshPanel.Tests\LocalizationTests.cs</c>),
/// и так видны обе беды сразу — член без записи в словаре и запись без члена. В v1 такого
/// правила не было, и человек однажды прочитал в окне имя ключа <c>about.noLink</c> вместо
/// объяснения, почему ссылка не открылась.
///
/// Точки вызова (<c>PanelStrings.ServerRunning</c>) при переносе на словари НЕ менялись:
/// их около трёхсот, и переписывать их значило бы смешать перенос строк с правкой логики.
/// </summary>
public static class PanelStrings
{
    /// <summary>Подсказка значка в трее.</summary>
    public static string TrayToolTip => Loc.T(nameof(TrayToolTip));

    // --- состояние в меню значка (три строки шапки) --------------------------

    /// <summary>Строка состояния: сервер работает (поднят панелью или взят ею под управление).</summary>
    public static string TrayStatusServerRunningFormat => Loc.T(nameof(TrayStatusServerRunningFormat));

    /// <summary>Строка состояния: сервер работает, но поднят НЕ панелью.</summary>
    public static string TrayStatusServerForeignFormat => Loc.T(nameof(TrayStatusServerForeignFormat));

    /// <summary>Строка состояния: порт свободен, сервера нет.</summary>
    public static string TrayStatusServerStopped => Loc.T(nameof(TrayStatusServerStopped));

    /// <summary>Строка состояния: на порту кто-то есть, но это не DSH.</summary>
    public static string TrayStatusServerBusy => Loc.T(nameof(TrayStatusServerBusy));

    /// <summary>Строка состояния: панель сервера не знает вовсе.</summary>
    public static string TrayStatusServerUnbound => Loc.T(nameof(TrayStatusServerUnbound));

    /// <summary>Строка состояния: активный агент и его баланс.</summary>
    public static string TrayStatusAgentFormat => Loc.T(nameof(TrayStatusAgentFormat));

    /// <summary>Агент не выбран — вместо имени в строке состояния.</summary>
    public static string TrayStatusNoAgent => Loc.T(nameof(TrayStatusNoAgent));

    /// <summary>Баланса нет — вместо суммы в строке состояния (короче, чем у подсказки значка).</summary>
    public static string TrayStatusNoBalance => Loc.T(nameof(TrayStatusNoBalance));

    /// <summary>
    /// Текст шарика самотеста трея. Именно из словаря, а не литералом: шарик — то, что человек
    /// видит на своём рабочем столе, и русский текст на китайской панели был бы дефектом.
    /// </summary>
    public static string TraySelfTestBalloonText => Loc.T(nameof(TraySelfTestBalloonText));

    /// <summary>Пункт меню значка: показать окно панели.</summary>
    public static string ShowPanelText => Loc.T(nameof(ShowPanelText));

    /// <summary>Пункт меню значка: завершить панель целиком.</summary>
    public static string ExitText => Loc.T(nameof(ExitText));

    /// <summary>Строка журнала: окно показано.</summary>
    public static string WindowShownLog => Loc.T(nameof(WindowShownLog));

    /// <summary>Строка журнала: окно спрятано в трей.</summary>
    public static string WindowHiddenLog => Loc.T(nameof(WindowHiddenLog));

    /// <summary>Строка журнала: показывать окно в этом прогоне нельзя.</summary>
    public static string WindowSuppressedLog => Loc.T(nameof(WindowSuppressedLog));

    /// <summary>Строка журнала: пришла просьба от второго запуска ярлыка.</summary>
    public static string ShowFromSignalLog => Loc.T(nameof(ShowFromSignalLog));

    // --- сервер -------------------------------------------------------------

    /// <summary>Состояние: сервер отвечает.</summary>
    public static string ServerRunning => Loc.T(nameof(ServerRunning));

    /// <summary>Состояние: на порту никого.</summary>
    public static string ServerStopped => Loc.T(nameof(ServerStopped));

    /// <summary>Состояние: порт слушает чужая программа.</summary>
    public static string ServerForeign => Loc.T(nameof(ServerForeign));

    /// <summary>Состояние: панель ещё не связана с сервером (окно без контроллера).</summary>
    public static string ServerUnbound => Loc.T(nameof(ServerUnbound));

    /// <summary>Кнопка «Запустить».</summary>
    public static string StartButton => Loc.T(nameof(StartButton));

    /// <summary>Кнопка «Остановить».</summary>
    public static string StopButton => Loc.T(nameof(StopButton));

    /// <summary>
    /// Кнопка остановки ВСТРОЕННОГО сервера. Многоточие здесь — не украшение: оно честно
    /// говорит, что кнопка сначала спросит подтверждение, а не остановит сразу.
    /// </summary>
    public static string StopButtonAsks => Loc.T(nameof(StopButtonAsks));

    /// <summary>
    /// Найден работающий сервер DSH, который панель не поднимала. Панель обязана его показать
    /// и спросить — это первый шаг встраивания (решение владельца 24.09.2026).
    /// </summary>
    public static string FoundServerFormat => Loc.T(nameof(FoundServerFormat));

    /// <summary>
    /// Дописано, когда найденный сервер стоит на порту владельца: это не мелочь, а прямое
    /// предупреждение о том, через что идёт его текущая работа.
    /// </summary>
    public static string FoundServerOwnerPortSuffix => Loc.T(nameof(FoundServerOwnerPortSuffix));

    /// <summary>Порты посмотреть не удалось — это НЕ «серверов нет», и говорить так нельзя.</summary>
    public static string FoundTableUnreadable => Loc.T(nameof(FoundTableUnreadable));

    /// <summary>
    /// Кнопка «Взять под управление»: взять найденный сервер под управление, своего не поднимая.
    ///
    /// До 26.09.2026 подпись была «Встроиться». Слово заменено вместе с решением владельца
    /// «спросить ОДИН раз и запомнить ответ»: кнопка теперь не подключает панель на сеанс,
    /// а берёт сервер в свои — панель запоминает согласие и больше не переспрашивает.
    /// </summary>
    public static string AdoptButton => Loc.T(nameof(AdoptButton));

    /// <summary>
    /// Пояснение к кнопке. Обязано СЛОВАМИ сказать, что именно панель берёт: что она будет
    /// считать этот сервер своим и сможет гасить его одной кнопкой, и что отвязаться можно
    /// кнопкой «Отвязаться» — сервер при этом продолжит работу (решение владельца 26.09.2026).
    /// </summary>
    public static string AdoptHint => Loc.T(nameof(AdoptHint));

    /// <summary>
    /// Отказ поднять СВОЙ сервер, когда рядом уже работает найденный DSH. Панель не поднимает
    /// второй движок на тех же данных — так закрыта находка 25.09.2026: кнопка «Запустить» была
    /// заряжена в профиль владельца (свой порт свободен, а `DSH_HOME` тот же самый).
    /// </summary>
    public static string ParallelStartRefusedFormat => Loc.T(nameof(ParallelStartRefusedFormat));

    /// <summary>Журнал: согласие на найденный сервер запомнено — спрашивать больше не будем.</summary>
    public static string AdoptedConsentSavedLogFormat => Loc.T(nameof(AdoptedConsentSavedLogFormat));

    /// <summary>Журнал: согласие забыто (кнопка «Отвязаться»). Порт не называется: сервер жив.</summary>
    public static string AdoptedConsentForgottenLog => Loc.T(nameof(AdoptedConsentForgottenLog));

    /// <summary>Журнал: согласие было запомнено раньше — панель взяла сервер сама, не спрашивая.</summary>
    public static string AdoptedConsentRestoredLogFormat => Loc.T(nameof(AdoptedConsentRestoredLogFormat));

    /// <summary>Кнопка «Отвязаться»: перестать управлять найденным сервером, не гася его.</summary>
    public static string DetachButton => Loc.T(nameof(DetachButton));

    // --- подтверждение остановки встроенного сервера ------------------------

    /// <summary>Заголовок окна подтверждения.</summary>
    public static string ConfirmStopTitle => Loc.T(nameof(ConfirmStopTitle));

    /// <summary>
    /// Текст подтверждения. Он называет порт, процесс и прямо говорит, что может оборваться:
    /// подтверждение спрашивается КАЖДЫЙ раз (решение владельца 24.09.2026).
    /// </summary>
    public static string ConfirmStopFormat => Loc.T(nameof(ConfirmStopFormat));

    /// <summary>Кнопка, которая действительно гасит. Слово на ней — про действие, а не про «ОК».</summary>
    public static string ConfirmStopYes => Loc.T(nameof(ConfirmStopYes));

    /// <summary>Кнопка отказа. Не «Нет», а «Отмена»: человек должен видеть, что ничего не произошло.</summary>
    public static string ConfirmStopNo => Loc.T(nameof(ConfirmStopNo));

    /// <summary>Состояние на время запуска. Честно предупреждает, что ждать долго.</summary>
    public static string ServerStarting => Loc.T(nameof(ServerStarting));

    /// <summary>Состояние на время остановки.</summary>
    public static string ServerStopping => Loc.T(nameof(ServerStopping));

    /// <summary>Состояние на время встраивания в найденный сервер.</summary>
    public static string ServerAdopting => Loc.T(nameof(ServerAdopting));

    /// <summary>Состояние на время отвязки от найденного сервера.</summary>
    public static string ServerDetaching => Loc.T(nameof(ServerDetaching));

    // --- автозапуск ---------------------------------------------------------

    /// <summary>Подпись галочки автозапуска.</summary>
    public static string AutostartCheck => Loc.T(nameof(AutostartCheck));

    /// <summary>Записи автозапуска нет.</summary>
    public static string AutostartOff => Loc.T(nameof(AutostartOff));

    /// <summary>Запись ведёт на эту копию.</summary>
    public static string AutostartSelfFormat => Loc.T(nameof(AutostartSelfFormat));

    /// <summary>Запись есть, но файла по её пути нет. Панель переведёт её на себя при старте.</summary>
    public static string AutostartMissingFormat => Loc.T(nameof(AutostartMissingFormat));

    /// <summary>Запись ведёт на другую живую копию панели 2.0 — её панель не трогает.</summary>
    public static string AutostartOtherFormat => Loc.T(nameof(AutostartOtherFormat));

    /// <summary>Реестр не читается: состояние неизвестно, панель не трогает ничего.</summary>
    public static string AutostartUnknown => Loc.T(nameof(AutostartUnknown));

    /// <summary>Окно открыто без автозапуска (так его снимает --shot и так его видят тесты).</summary>
    public static string AutostartUnbound => Loc.T(nameof(AutostartUnbound));

    /// <summary>Дописано к состоянию, когда прогон проверки читает запись, но менять её не имеет права.</summary>
    public static string AutostartLockedSuffix => Loc.T(nameof(AutostartLockedSuffix));

    /// <summary>Изменить автозапуск не удалось — так и говорим, а не «включено».</summary>
    public static string AutostartFailed => Loc.T(nameof(AutostartFailed));

    /// <summary>Строка журнала: автозапуск включён.</summary>
    public static string AutostartOnLog => Loc.T(nameof(AutostartOnLog));

    /// <summary>Строка журнала: автозапуск выключен.</summary>
    public static string AutostartOffLog => Loc.T(nameof(AutostartOffLog));

    /// <summary>Строка журнала: изменить запись не удалось (с путём хранилища).</summary>
    public static string AutostartFailedLogFormat => Loc.T(nameof(AutostartFailedLogFormat));

    /// <summary>Строка журнала: прогон проверки машинную запись не меняет.</summary>
    public static string AutostartLockedLogFormat => Loc.T(nameof(AutostartLockedLogFormat));

    /// <summary>Строка журнала: стартовая сверка перевела запись на эту копию.</summary>
    public static string AutostartRepairedLogFormat => Loc.T(nameof(AutostartRepairedLogFormat));

    /// <summary>Строка журнала: сверка при старте менять ничего не стала.</summary>
    public static string AutostartKeptLogFormat => Loc.T(nameof(AutostartKeptLogFormat));

    /// <summary>Короткое описание состояния — для журнала, а не для окна.</summary>
    public static string AutostartWhereOffLog => Loc.T(nameof(AutostartWhereOffLog));

    public static string AutostartWhereSelfLog => Loc.T(nameof(AutostartWhereSelfLog));

    public static string AutostartWhereMissingLogFormat => Loc.T(nameof(AutostartWhereMissingLogFormat));

    public static string AutostartWhereOtherLogFormat => Loc.T(nameof(AutostartWhereOtherLogFormat));

    public static string AutostartWhereUnknownLog => Loc.T(nameof(AutostartWhereUnknownLog));

    // --- настройки ----------------------------------------------------------

    /// <summary>Заголовок окна настроек.</summary>
    public static string SettingsTitle => Loc.T(nameof(SettingsTitle));

    /// <summary>Раздел «Общее».</summary>
    public static string SettingsGeneral => Loc.T(nameof(SettingsGeneral));

    /// <summary>Раздел «Сервер».</summary>
    public static string SettingsServerSection => Loc.T(nameof(SettingsServerSection));

    /// <summary>Пункт меню значка и кнопка в окне панели.</summary>
    public static string SettingsMenuText => Loc.T(nameof(SettingsMenuText));

    /// <summary>Строка журнала: окно настроек открыто.</summary>
    public static string SettingsOpenedLog => Loc.T(nameof(SettingsOpenedLog));

    /// <summary>Строка журнала: окно настроек уже было открыто — вывели его на передний план.</summary>
    public static string SettingsRaisedLog => Loc.T(nameof(SettingsRaisedLog));

    /// <summary>Подпись поля рабочей папки.</summary>
    public static string SettingsWorkDirLabel => Loc.T(nameof(SettingsWorkDirLabel));

    /// <summary>Подсказка внутри поля, когда оно пустое.</summary>
    public static string SettingsWorkDirPlaceholder => Loc.T(nameof(SettingsWorkDirPlaceholder));

    /// <summary>Откуда взято предложение рабочей папки.</summary>
    public static string WorkDirSourcePreviousPanel => Loc.T(nameof(WorkDirSourcePreviousPanel));

    /// <summary>Рабочая папка не задана: сервер поднимается из папки панели.</summary>

    /// <summary>Рабочая папка задана человеком.</summary>

    /// <summary>Предложение рабочей папки, которую человек уже использует.</summary>
    public static string SettingsSuggestFormat => Loc.T(nameof(SettingsSuggestFormat));

    public static string SettingsSuggestButton => Loc.T(nameof(SettingsSuggestButton));

    /// <summary>Подпись списка тем.</summary>
    public static string SettingsThemeLabel => Loc.T(nameof(SettingsThemeLabel));

    public static string SettingsThemeSystem => Loc.T(nameof(SettingsThemeSystem));

    public static string SettingsThemeLight => Loc.T(nameof(SettingsThemeLight));

    public static string SettingsThemeDark => Loc.T(nameof(SettingsThemeDark));

    public static string SettingsCheckEnvironment => Loc.T(nameof(SettingsCheckEnvironment));

    public static string SettingsSave => Loc.T(nameof(SettingsSave));

    public static string SettingsSaved => Loc.T(nameof(SettingsSaved));

    public static string SettingsSaveFailed => Loc.T(nameof(SettingsSaveFailed));

    /// <summary>Почему настройки показаны умолчаниями.</summary>
    public static string SettingsReadOnlyNote => Loc.T(nameof(SettingsReadOnlyNote));

    /// <summary>Заголовок вопроса о несохранённой правке (жалоба владельца, п. 16 `DESIGN.md`).</summary>
    public static string SettingsUnsavedTitle => Loc.T(nameof(SettingsUnsavedTitle));

    /// <summary>Сам вопрос: параметры изменены — сохранить их перед закрытием?</summary>
    public static string SettingsUnsavedQuestion => Loc.T(nameof(SettingsUnsavedQuestion));

    /// <summary>Кнопка «выйти без сохранения»: правка пропадёт, и человек сказал это сам.</summary>
    public static string SettingsUnsavedDiscard => Loc.T(nameof(SettingsUnsavedDiscard));

    /// <summary>Кнопка «Отмена»: окно остаётся открытым. Отдельная строка от гашения сервера —
    /// там «Отмена» значит «не гасить», и связывать эти два решения ключом нельзя.</summary>
    public static string SettingsUnsavedCancel => Loc.T(nameof(SettingsUnsavedCancel));

    /// <summary>Строка журнала перед причиной, по которой настройки не разобрались.</summary>
    public static string SettingsLoadLog => Loc.T(nameof(SettingsLoadLog));

    /// <summary>Строка журнала: прогон проверки настройки не меняет.</summary>
    public static string SettingsLockedLog => Loc.T(nameof(SettingsLockedLog));

    /// <summary>Строка журнала: сохранить не удалось (с путём хранилища).</summary>
    public static string SettingsSaveFailedLog => Loc.T(nameof(SettingsSaveFailedLog));

    /// <summary>Строка журнала: настройки сохранены.</summary>
    public static string SettingsSavedLogFormat => Loc.T(nameof(SettingsSavedLogFormat));

    /// <summary>
    /// Строка журнала: настройки УЖЕ сохранены, а тему применить не удалось (причина — двумя
    /// подстановками). Отдельная строка, а не молчание и не «не сохранилось»: файл к этому
    /// мгновению записан, и объявить запись неудачной значило бы соврать — ровно это и вышло
    /// у владельца 26.09.2026 в 23:10:12, когда встраивание шло в фоновой нитке.
    /// </summary>
    public static string SettingsThemeFailedLogFormat => Loc.T(nameof(SettingsThemeFailedLogFormat));

    /// <summary>Короткое обещание: когда применится рабочая папка.</summary>
    public static string SettingsWorkDirAppliesNextStart => Loc.T(nameof(SettingsWorkDirAppliesNextStart));

    /// <summary>Подпись поля порта.</summary>
    public static string SettingsPortLabel => Loc.T(nameof(SettingsPortLabel));

    /// <summary>
    /// Пояснение к порту. Здесь прямо сказано и про запрет порта владельца, и про то, что
    /// панель не съезжает на другой порт молча: обещать «порт найдётся сам» нельзя.
    /// </summary>
    public static string SettingsPortHint => Loc.T(nameof(SettingsPortHint));

    /// <summary>
    /// Человек набрал порт, который панель занять не может. Говорим это СРАЗУ и называем,
    /// что будет вместо него: узнать о подмене после сохранения — значит искать сервер не там.
    /// </summary>
    public static string SettingsPortForbiddenFormat => Loc.T(nameof(SettingsPortForbiddenFormat));

    // --- что панель видит вокруг -------------------------------------------

    /// <summary>Порт, который панель займёт, поднимая свой сервер (уже приведённый к допустимому).</summary>

    public static string ServerNotStartedWithPort => Loc.T(nameof(ServerNotStartedWithPort));

    public static string ServerRunningOwnFormat => Loc.T(nameof(ServerRunningOwnFormat));

    public static string ServerRunningForeignFormat => Loc.T(nameof(ServerRunningForeignFormat));

    /// <summary>Панель встроилась в найденный сервер — это отдельное состояние, не «поднят не панелью».</summary>
    public static string ServerAdoptedFormat => Loc.T(nameof(ServerAdoptedFormat));

    public static string ServerPortBusyFormat => Loc.T(nameof(ServerPortBusyFormat));

    // --- длительность словами (ОБЩИЕ строки панели) -------------------------
    //
    // ⚠️ Эти строки — НЕ «пиковые», хотя и родились вместе с окнами пика: это слова о
    // ДЛИТЕЛЬНОСТИ, и говорит их вся панель. Второго набора таких же слов быть не должно —
    // два одинаковых текста начинают жить своей жизнью и однажды расходятся (переводчик поймал
    // ровно это 26.09.2026: у часов автокопии завелись дословные двойники этих трёх строк).
    // Поэтому имя у них общее — <c>Span…</c>, без «пика»: пики такие же потребители, как расписание.

    /// <summary>Меньше минуты — «меньше минуты»: ноль минут звучал бы как ошибка счёта.</summary>
    public static string SpanLessMinute => Loc.T(nameof(SpanLessMinute));

    /// <summary>Длительность: только минуты — «40 мин».</summary>
    public static string SpanMinutesFormat => Loc.T(nameof(SpanMinutesFormat));

    /// <summary>Длительность: ровное число часов — «25 ч».</summary>
    public static string SpanHoursFormat => Loc.T(nameof(SpanHoursFormat));

    /// <summary>Длительность: часы и минуты — «3 ч 5 мин».</summary>
    public static string SpanHoursMinutesFormat => Loc.T(nameof(SpanHoursMinutesFormat));

    // --- окна пика ----------------------------------------------------------

    /// <summary>Сейчас полная цена.</summary>
    public static string PeakInPeak => Loc.T(nameof(PeakInPeak));

    /// <summary>Сейчас вдвое дешевле.</summary>
    public static string PeakOffPeak => Loc.T(nameof(PeakOffPeak));

    /// <summary>Когда начнётся пик: «Пик начнётся через 2 ч 15 мин (13:00)».</summary>
    public static string PeakNextStartFormat => Loc.T(nameof(PeakNextStartFormat));

    /// <summary>Когда пик кончится.</summary>
    public static string PeakNextEndFormat => Loc.T(nameof(PeakNextEndFormat));

    /// <summary>Строка расписания с источником и датой проверки.</summary>
    public static string PeakScheduleSourceFormat => Loc.T(nameof(PeakScheduleSourceFormat));

    /// <summary>
    /// Заголовок таблицы окон по дням недели. Подставляется название часового пояса: окна
    /// в профиле агента лежат в UTC, а человек читает их по своим часам — и без пояса
    /// «04:00» читалось бы как «на твоих часах».
    /// </summary>
    public static string PeakTableHeadingFormat => Loc.T(nameof(PeakTableHeadingFormat));

    /// <summary>День, в котором окон пика нет вовсе: цена вне пика держится круглые сутки.</summary>
    public static string PeakTableOffPeakAllDay => Loc.T(nameof(PeakTableOffPeakAllDay));

    public static string PeakDaysDaily => Loc.T(nameof(PeakDaysDaily));

    public static string PeakDaysWorkWeek => Loc.T(nameof(PeakDaysWorkWeek));

    public static string PeakDaysWeekend => Loc.T(nameof(PeakDaysWeekend));

    public static string PeakDaySunday => Loc.T(nameof(PeakDaySunday));
    public static string PeakDayMonday => Loc.T(nameof(PeakDayMonday));
    public static string PeakDayTuesday => Loc.T(nameof(PeakDayTuesday));
    public static string PeakDayWednesday => Loc.T(nameof(PeakDayWednesday));
    public static string PeakDayThursday => Loc.T(nameof(PeakDayThursday));
    public static string PeakDayFriday => Loc.T(nameof(PeakDayFriday));
    public static string PeakDaySaturday => Loc.T(nameof(PeakDaySaturday));

    // --- баланс -------------------------------------------------------------

    public static string BalanceTitle => Loc.T(nameof(BalanceTitle));

    /// <summary>Баланс ещё не спрашивали.</summary>
    public static string BalanceNotRequested => Loc.T(nameof(BalanceNotRequested));

    /// <summary>Ключа нет в файле ключей: имя ключа и путь (путь маскированный).</summary>
    public static string BalanceNoKeyFormat => Loc.T(nameof(BalanceNoKeyFormat));

    /// <summary>Запрос не удался.</summary>
    public static string BalanceFailedFormat => Loc.T(nameof(BalanceFailedFormat));

    /// <summary>Ответ есть, но счёт недоступен (ключ не принят).</summary>
    public static string BalanceUnavailable => Loc.T(nameof(BalanceUnavailable));

    /// <summary>В ответе не оказалось ни одной суммы.</summary>
    public static string BalanceNoData => Loc.T(nameof(BalanceNoData));

    /// <summary>Когда баланс обновлён.</summary>
    public static string BalanceCheckedFormat => Loc.T(nameof(BalanceCheckedFormat));

    public static string BalanceRefreshButton => Loc.T(nameof(BalanceRefreshButton));

    public static string BalanceToppedUpFormat => Loc.T(nameof(BalanceToppedUpFormat));

    public static string BalanceGrantedFormat => Loc.T(nameof(BalanceGrantedFormat));

    /// <summary>Прогон проверки не читает ключ и не ходит в сеть.</summary>
    public static string BalanceLocked => Loc.T(nameof(BalanceLocked));

    /// <summary>Строка журнала: чем кончился запрос баланса.</summary>
    public static string BalanceLogFormat => Loc.T(nameof(BalanceLogFormat));

    /// <summary>Строка журнала: автоматическое сообщение показано.</summary>
    public static string NotifyShownLogFormat => Loc.T(nameof(NotifyShownLogFormat));

    /// <summary>Строка журнала: автоматическое сообщение подавлено изоляцией.</summary>
    public static string NotifySuppressedLogFormat => Loc.T(nameof(NotifySuppressedLogFormat));

    // --- блок «Сообщить о проблеме» (требование владельца 29.09.2026, DESIGN.md п. 38) ----

    /// <summary>Заголовок окна сообщения о проблеме.</summary>
    public static string IssueTitle => Loc.T(nameof(IssueTitle));

    /// <summary>Заголовок внутри окна.</summary>
    public static string IssueHeading => Loc.T(nameof(IssueHeading));

    /// <summary>Подзаголовок: что делает это окно и чего оно не делает.</summary>
    public static string IssueSubtitle => Loc.T(nameof(IssueSubtitle));

    /// <summary>Подпись поля «что случилось».</summary>
    public static string IssueMessageLabel => Loc.T(nameof(IssueMessageLabel));

    /// <summary>Подсказка в пустом поле «что случилось».</summary>
    public static string IssueMessageWatermark => Loc.T(nameof(IssueMessageWatermark));

    /// <summary>Подпись над текстом, который уйдёт.</summary>
    public static string IssuePreviewLabel => Loc.T(nameof(IssuePreviewLabel));

    /// <summary>Кнопка: открыть страницу нового сообщения в браузере.</summary>
    public static string IssueOpenButton => Loc.T(nameof(IssueOpenButton));

    /// <summary>Кнопка: скопировать текст в буфер.</summary>
    public static string IssueCopyButton => Loc.T(nameof(IssueCopyButton));

    /// <summary>Кнопка: сохранить текст в файл.</summary>
    public static string IssueSaveButton => Loc.T(nameof(IssueSaveButton));

    /// <summary>Кнопка: закрыть окно.</summary>
    public static string IssueCloseButton => Loc.T(nameof(IssueCloseButton));

    /// <summary>Строка состояния: текст скопирован.</summary>
    public static string IssueCopied => Loc.T(nameof(IssueCopied));

    /// <summary>Строка состояния: буфера обмена нет.</summary>
    public static string IssueClipboardUnavailable => Loc.T(nameof(IssueClipboardUnavailable));

    /// <summary>Заголовок окна выбора файла для сохранения отчёта.</summary>
    public static string IssueSaveTitle => Loc.T(nameof(IssueSaveTitle));

    /// <summary>Название типа файла в окне сохранения.</summary>
    public static string IssueSaveFileType => Loc.T(nameof(IssueSaveFileType));

    /// <summary>Строка состояния: отчёт сохранён (подставляется имя файла).</summary>
    public static string IssueSavedFormat => Loc.T(nameof(IssueSavedFormat));

    /// <summary>Отчёт: журнал пуст или недоступен — слова вместо пустого блока.</summary>
    public static string IssueNoLog => Loc.T(nameof(IssueNoLog));

    /// <summary>Слова о том, что журнал сокращён: в ссылку GitHub помещаются не все строки.</summary>
    public static string IssueLogTrimmedFormat => Loc.T(nameof(IssueLogTrimmedFormat));

    /// <summary>Слова о том, что сокращён СВОЙ текст человека — он длиннее, чем влезает в ссылку.</summary>
    public static string IssueMessageTrimmed => Loc.T(nameof(IssueMessageTrimmed));

    /// <summary>
    /// Строка окна истории: панель ещё НИ РАЗУ не читала цены (первой цены нет вовсе).
    /// Это другое состояние, чем «первая цена есть, а изменений не было»: там человек видит дату
    /// и цены, а здесь показывать нечего.
    /// </summary>
    public static string PricingHistoryNeverChecked => Loc.T(nameof(PricingHistoryNeverChecked));

    /// <summary>Как панель запущена: обычный запуск человеком.</summary>
    public static string EnvLaunchHuman => Loc.T(nameof(EnvLaunchHuman));

    /// <summary>Как панель запущена: прогон проверки (не изолированный).</summary>
    public static string EnvLaunchCheck => Loc.T(nameof(EnvLaunchCheck));

    /// <summary>Как панель запущена: изолированный прогон.</summary>
    public static string EnvLaunchIsolated => Loc.T(nameof(EnvLaunchIsolated));

    /// <summary>Строка журнала: окно «Сообщить о проблеме» открыто.</summary>
    public static string IssueOpenedLog => Loc.T(nameof(IssueOpenedLog));

    /// <summary>Строка журнала: окно уже было открыто — вывели на передний план.</summary>
    public static string IssueRaisedLog => Loc.T(nameof(IssueRaisedLog));

    /// <summary>Строка журнала: просьба открыть отчёт, а окна у панели нет.</summary>
    public static string PanelLogIssueUnavailable => Loc.T(nameof(PanelLogIssueUnavailable));

    /// <summary>Подпись двери в окне «О программе».</summary>
    public static string AboutIssueButton => Loc.T(nameof(AboutIssueButton));

    /// <summary>Заголовок блока о сообщении о проблеме в окне «О программе».</summary>
    public static string AboutIssueHeading => Loc.T(nameof(AboutIssueHeading));

    /// <summary>Пояснение к блоку: что произойдёт и чего не произойдёт.</summary>
    public static string AboutIssueNote => Loc.T(nameof(AboutIssueNote));
    /// <summary>Подсказка двери: что произойдёт по щелчку.</summary>
    public static string TipAboutIssueButton => Loc.T(nameof(TipAboutIssueButton));

    public static string TipIssueOpenButton => Loc.T(nameof(TipIssueOpenButton));
    public static string TipIssueCopyButton => Loc.T(nameof(TipIssueCopyButton));
    public static string TipIssueSaveButton => Loc.T(nameof(TipIssueSaveButton));
    public static string TipIssueCloseButton => Loc.T(nameof(TipIssueCloseButton));

    // --- ТЕЛО ОТЧЁТА О ПРОБЛЕМЕ: слова, которые уезжают наружу --------------------
    //
    // ⚠️ Здесь был ДЕФЕКТ, и назвал его владелец 29.09.2026: текст отчёта собирался русскими
    // литералами прямо в `Issue\IssueReport.cs`. Значит на английской и китайской панели отчёт
    // уезжал на GitHub по-русски — при том, что всё остальное окно было переведено. Поэтому
    // каждый заголовок, каждая подпись и каждая оговорка тела отчёта живут здесь, а не в коде:
    // разъехаться им больше нечем (сторожит `IssueReportTests.В_исходнике_отчёта_нет_русского_литерала`).

    /// <summary>Отчёт: заголовок раздела «что случилось» (печатается как заголовок Markdown).</summary>
    public static string IssueReportHeadingWhat => Loc.T(nameof(IssueReportHeadingWhat));

    /// <summary>Отчёт: заголовок раздела с версиями и состоянием панели.</summary>
    public static string IssueReportHeadingPanel => Loc.T(nameof(IssueReportHeadingPanel));

    /// <summary>Отчёт: заголовок раздела с хвостом журнала панели.</summary>
    public static string IssueReportHeadingLog => Loc.T(nameof(IssueReportHeadingLog));

    /// <summary>Отчёт: подсказка в пустом разделе «что случилось» (скрытый комментарий Markdown).</summary>
    public static string IssueReportMessageHint => Loc.T(nameof(IssueReportMessageHint));

    /// <summary>Отчёт: подпись строки «версия панели».</summary>
    public static string IssueReportVersion => Loc.T(nameof(IssueReportVersion));

    /// <summary>Отчёт: подпись строки «движок DSH».</summary>
    public static string IssueReportEngine => Loc.T(nameof(IssueReportEngine));

    /// <summary>Отчёт: подпись строки «как панель запущена».</summary>
    public static string IssueReportLaunch => Loc.T(nameof(IssueReportLaunch));

    /// <summary>Отчёт: подпись строки «состояние сервера».</summary>
    public static string IssueReportServer => Loc.T(nameof(IssueReportServer));

    /// <summary>Отчёт: порт сервера в скобках после состояния (подстановка — номер порта).</summary>
    public static string IssueReportPortFormat => Loc.T(nameof(IssueReportPortFormat));

    /// <summary>Отчёт: «не удалось узнать» — ответ вместо пустого места, когда факта нет.</summary>
    public static string IssueReportUnknown => Loc.T(nameof(IssueReportUnknown));

    /// <summary>Отчёт: «неизвестна» — версия панели, которую прочитать не удалось.</summary>
    public static string IssueReportVersionUnknown => Loc.T(nameof(IssueReportVersionUnknown));

    /// <summary>Отчёт: чем заменяется пустое значение в строке отчёта.</summary>
    public static string IssueReportEmptyValue => Loc.T(nameof(IssueReportEmptyValue));

    /// <summary>
    /// Отчёт: слова о том, чего в нём НЕТ и почему. Печатается рядом с текстом — человеку,
    /// который решает, отправлять ли отчёт (требование владельца, `DESIGN.md` п. 38).
    /// </summary>
    public static string IssueReportOmittedNote => Loc.T(nameof(IssueReportOmittedNote));

    /// <summary>
    /// Отчёт: оговорка о ЯЗЫКЕ ЖУРНАЛА. Хвост журнала панель ведёт по-русски осознанно (для
    /// владельца) и не переводит, поэтому строка нужна НА КАЖДОМ языке: без неё человек на
    /// английском отчёте читает русские строки и считает, что панель сломала язык.
    ///
    /// ⚠️ Это ровно тот случай, который просил назвать владелец: русский текст журнала уезжает
    /// наружу вместе с отчётом, и молчать об этом в отчёте нельзя.
    /// </summary>
    public static string IssueReportLogRussianNote => Loc.T(nameof(IssueReportLogRussianNote));

    // --- шарик о запуске панели (требование владельца 29.09.2026) ----------

    /// <summary>Заголовок шарика «панель запущена».</summary>
    public static string NotifyPanelStartedTitle => Loc.T(nameof(NotifyPanelStartedTitle));

    /// <summary>
    /// Текст шарика «панель запущена»: что панель работает и где её искать.
    /// Ни версии, ни порта, ни пути, ни ссылки входа — в шарике о запуске нет ни одной
    /// подробности, которая не нужна, чтобы понять «панель жива, она в трее».
    /// </summary>
    public static string NotifyPanelStartedText => Loc.T(nameof(NotifyPanelStartedText));

    // --- сообщения о балансе и пике ----------------------------------------

    public static string NotifyLowBalanceTitle => Loc.T(nameof(NotifyLowBalanceTitle));

    /// <summary>Текст шарика о низком балансе: баланс, валюта и порог.</summary>
    public static string NotifyLowBalanceFormat => Loc.T(nameof(NotifyLowBalanceFormat));

    public static string NotifyPeakSoonTitle => Loc.T(nameof(NotifyPeakSoonTitle));

    /// <summary>Текст шарика о приближении пика: через сколько и во сколько.</summary>
    public static string NotifyPeakSoonFormat => Loc.T(nameof(NotifyPeakSoonFormat));

    // --- подсказка значка ---------------------------------------------------

    /// <summary>Подсказка значка: агент, баланс, тариф.</summary>
    public static string TrayToolTipFormat => Loc.T(nameof(TrayToolTipFormat));

    /// <summary>В подсказке значка баланса пока нет.</summary>
    public static string TrayToolTipNoBalance => Loc.T(nameof(TrayToolTipNoBalance));

    // --- раздел настроек «Баланс и тариф» ----------------------------------

    public static string SettingsBalanceSection => Loc.T(nameof(SettingsBalanceSection));

    public static string SettingsActiveAgentLabel => Loc.T(nameof(SettingsActiveAgentLabel));

    public static string SettingsActiveAgentHint => Loc.T(nameof(SettingsActiveAgentHint));

    public static string SettingsBalanceAuto => Loc.T(nameof(SettingsBalanceAuto));

    public static string SettingsBalancePeriodLabel => Loc.T(nameof(SettingsBalancePeriodLabel));

    public static string SettingsBalanceWarn => Loc.T(nameof(SettingsBalanceWarn));

    public static string SettingsBalanceThresholdLabel => Loc.T(nameof(SettingsBalanceThresholdLabel));

    public static string SettingsPeakNotifyLabel => Loc.T(nameof(SettingsPeakNotifyLabel));

    public static string SettingsPeakNotifyHint => Loc.T(nameof(SettingsPeakNotifyHint));

    /// <summary>
    /// Куда уехали расписание пиков и таблица цен из раздела «Баланс и тариф» (решение владельца
    /// 28.09.2026: у них своё окно). Сказано словами, а не пропажей: раздел, у которого что-то
    /// исчезло молча, читается как сломанный.
    /// </summary>
    public static string SettingsPeakWhereHint => Loc.T(nameof(SettingsPeakWhereHint));

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ» В НАСТРОЙКАХ (решение владельца 28.09.2026): «это будет просто
    /// нормальный пункт меню как и остальные во вкладке настройки, может быть просто „обновления“».
    /// </summary>
    public static string UpdateSectionTitle => Loc.T(nameof(UpdateSectionTitle));

    /// <summary>Подзаголовок раздела «Обновление»: что именно здесь видно и что можно сделать.</summary>
    public static string UpdateSectionSubtitle => Loc.T(nameof(UpdateSectionSubtitle));

    // --- отчёт окружения (раздел «Сервер»): группы и строки находок ---------------------------

    /// <summary>Группа «Папки»: где лежат данные, настройки, копии, рабочая папка и порт.</summary>
    public static string EnvGroupSetup => Loc.T(nameof(EnvGroupSetup));

    public static string EnvDshHome => Loc.T(nameof(EnvDshHome));
    public static string EnvSettingsFile => Loc.T(nameof(EnvSettingsFile));
    public static string EnvBackupsDir => Loc.T(nameof(EnvBackupsDir));
    public static string EnvWorkDir => Loc.T(nameof(EnvWorkDir));
    public static string EnvServerPort => Loc.T(nameof(EnvServerPort));
    public static string EnvServerState => Loc.T(nameof(EnvServerState));
    public static string EnvFromSetting => Loc.T(nameof(EnvFromSetting));
    public static string EnvFromPanel => Loc.T(nameof(EnvFromPanel));

    /// <summary>Подпись журнала: рабочая папка не задана, взята папка панели.</summary>
    public static string WorkDirFromPanel => Loc.T(nameof(WorkDirFromPanel));

    /// <summary>Группа «Чем поднимается сервер»: Node, npm, pnpm.</summary>
    public static string EnvGroupRuntime => Loc.T(nameof(EnvGroupRuntime));

    public static string EnvNode => Loc.T(nameof(EnvNode));
    public static string EnvNpm => Loc.T(nameof(EnvNpm));
    public static string EnvPnpm => Loc.T(nameof(EnvPnpm));

    /// <summary>Группа «Движок DSH» — версия и путь.</summary>
    public static string EnvGroupDsh => Loc.T(nameof(EnvGroupDsh));

    public static string EnvEngine => Loc.T(nameof(EnvEngine));

    /// <summary>«Найден» без версии, «не найден» и «версия не прочитана» — три разных ответа.</summary>
    public static string EnvFound => Loc.T(nameof(EnvFound));
    public static string EnvNotFound => Loc.T(nameof(EnvNotFound));
    public static string EnvValueUnknown => Loc.T(nameof(EnvValueUnknown));

    // --- копии (этап 4) -----------------------------------------------------

    /// <summary>Единицы размера. Числа подставляются с точкой (<c>InvariantCulture</c>) — как в v1.</summary>
    public static string BackupUnitGb => Loc.T(nameof(BackupUnitGb));

    public static string BackupUnitMb => Loc.T(nameof(BackupUnitMb));

    public static string BackupUnitKb => Loc.T(nameof(BackupUnitKb));

    public static string BackupUnitB => Loc.T(nameof(BackupUnitB));

    /// <summary>Длительность: миллисекунды, секунды, минуты — по тому же правилу, что в v1.</summary>
    public static string BackupTookMs => Loc.T(nameof(BackupTookMs));

    public static string BackupTookSec => Loc.T(nameof(BackupTookSec));

    public static string BackupTookMin => Loc.T(nameof(BackupTookMin));

    // --- окно «Копии» (этап 4) ----------------------------------------------
    //
    // Правило для НОВЫХ экранов (названо 25.09.2026 в AGENTS.md): подписи кнопок, заголовки,
    // вопросы согласий и строки отчёта живут ЗДЕСЬ, а не в разметке. Разметка скелета писалась
    // до этого правила, и тексты в ней ещё есть — этап локализации их заберёт.

    /// <summary>Строка списка, когда время копии не удалось выяснить ни из описи, ни из файла.</summary>
    public static string BackupTimeUnknown => Loc.T(nameof(BackupTimeUnknown));

    /// <summary>Вид копии в строке списка — те же слова, что были в v1.</summary>
    public static string BackupKindFull => Loc.T(nameof(BackupKindFull));

    public static string BackupKindThin => Loc.T(nameof(BackupKindThin));

    /// <summary>
    /// Подписи КОЛОНОК таблицы готовых копий (замечание владельца 28.09.2026: строки читались
    /// абзацем, и было непонятно, что по ним можно щёлкнуть). Колонок пять, и каждая отвечает
    /// на свой вопрос: когда снята, какая, сколько весит, сколько в ней файлов, что в ней есть.
    /// </summary>
    public static string BackupTableTime => Loc.T(nameof(BackupTableTime));

    public static string BackupTableKind => Loc.T(nameof(BackupTableKind));

    public static string BackupTableSize => Loc.T(nameof(BackupTableSize));

    public static string BackupTableFiles => Loc.T(nameof(BackupTableFiles));

    public static string BackupTableFeatures => Loc.T(nameof(BackupTableFeatures));

    /// <summary>Особенность копии в таблице: в архиве есть движок (и Node) — копия полная.</summary>
    public static string BackupWithEngine => Loc.T(nameof(BackupWithEngine));

    public static string BackupFilesFormat => Loc.T(nameof(BackupFilesFormat));

    public static string BackupWithSessions => Loc.T(nameof(BackupWithSessions));

    public static string BackupWithKey => Loc.T(nameof(BackupWithKey));

    public static string BackupManifestBrokenFormat => Loc.T(nameof(BackupManifestBrokenFormat));

    public static string BackupUnknown => Loc.T(nameof(BackupUnknown));

    /// <summary>Заголовок окна копий.</summary>
    public static string BackupWindowTitle => Loc.T(nameof(BackupWindowTitle));

    public static string BackupHeading => Loc.T(nameof(BackupHeading));

    /// <summary>Заголовок раздела в настройках и подпись поля.</summary>
    public static string BackupSectionTitle => Loc.T(nameof(BackupSectionTitle));

    public static string BackupFolderLabel => Loc.T(nameof(BackupFolderLabel));

    /// <summary>Пустая настройка — это умолчание v1, и оно называется целиком, а не подразумевается.</summary>
    public static string BackupFolderHintFormat => Loc.T(nameof(BackupFolderHintFormat));

    public static string BackupFolderCustomHint => Loc.T(nameof(BackupFolderCustomHint));

    public static string BackupCreateButton => Loc.T(nameof(BackupCreateButton));

    public static string BackupRefreshButton => Loc.T(nameof(BackupRefreshButton));

    public static string BackupListCountFormat => Loc.T(nameof(BackupListCountFormat));

    public static string BackupListEmpty => Loc.T(nameof(BackupListEmpty));

    public static string BackupListMissingFolderFormat => Loc.T(nameof(BackupListMissingFolderFormat));

    /// <summary>Причина, по которой список может быть неполным. Путь показывается через маску.</summary>
    public static string BackupFolderUnreadableFormat => Loc.T(nameof(BackupFolderUnreadableFormat));

    // --- дверь «открыть папку копий проводником» (п. 31 `docs\DESIGN.md`) ----
    //
    // Слова владельца: *«Во вкладке копии и восстановление добавить возможность открыть папку
    // через проводник рядом с указанием пути для быстрого перехода»*. Форма двери — та же, что
    // у каталога настроек (`AgentBrowser.TryOpenFolder`), а СЛОВА свои: человек читает про папку
    // КОПИЙ, а не про каталог настроек, — иначе отказ назвал бы не то место.

    /// <summary>Кнопка рядом с путём папки копий: показать папку в проводнике.</summary>
    public static string BackupOpenFolderButton => Loc.T(nameof(BackupOpenFolderButton));

    /// <summary>Подсказка кнопки: что именно откроется.</summary>
    public static string TipBackupOpenFolderButton => Loc.T(nameof(TipBackupOpenFolderButton));

    /// <summary>Строка журнала: папка копий открыта по щелчку человека.</summary>
    public static string BackupFolderOpenedLog => Loc.T(nameof(BackupFolderOpenedLog));

    /// <summary>Строка журнала: папку копий не открываем — это прогон проверки (красная линия 8: он не показывает окон).</summary>
    public static string BackupFolderRefusedLog => Loc.T(nameof(BackupFolderRefusedLog));

    /// <summary>Отказ: открывать нечего — папки копий ещё нет (до первой копии её не бывает).</summary>
    public static string BackupFolderMissing => Loc.T(nameof(BackupFolderMissing));

    /// <summary>Отказ открыть папку копий — с причиной от оболочки.</summary>
    public static string BackupFolderFailedFormat => Loc.T(nameof(BackupFolderFailedFormat));

    // --- согласие про сервер перед копией и накатом --------------------------
    //
    // Решение владельца 26.09.2026: панель ПРЕДЛАГАЕТ погасить сервер; свой сервер гасит
    // по согласию, найденный и взятый под управление — только по отдельному подтверждению
    // (это правило уже действует, вопрос 3). Копия «на ходу» возможна, но допущение называется.

    public static string BackupStopTitle => Loc.T(nameof(BackupStopTitle));

    public static string BackupStopQuestionFormat => Loc.T(nameof(BackupStopQuestionFormat));

    public static string BackupStopYes => Loc.T(nameof(BackupStopYes));

    public static string BackupStopNo => Loc.T(nameof(BackupStopNo));

    public static string BackupStopFailedFormat => Loc.T(nameof(BackupStopFailedFormat));

    public static string BackupRunning => Loc.T(nameof(BackupRunning));

    public static string BackupDoneFormat => Loc.T(nameof(BackupDoneFormat));

    public static string BackupFailedFormat => Loc.T(nameof(BackupFailedFormat));

    public static string BackupFileFormat => Loc.T(nameof(BackupFileFormat));

    // --- накат из окна копий ------------------------------------------------

    public static string RestorePlanHeadingFormat => Loc.T(nameof(RestorePlanHeadingFormat));

    /// <summary>Панель читает архив заранее и ничего не меняет — и говорит это прямо.</summary>
    public static string RestoreReadOnlyNote => Loc.T(nameof(RestoreReadOnlyNote));

    public static string RestoreEngineCheck => Loc.T(nameof(RestoreEngineCheck));

    public static string RestorePanelCheck => Loc.T(nameof(RestorePanelCheck));

    public static string RestoreConsentNote => Loc.T(nameof(RestoreConsentNote));

    public static string RestoreRunButton => Loc.T(nameof(RestoreRunButton));

    public static string RestoreCancelButton => Loc.T(nameof(RestoreCancelButton));

    public static string RestoreRunning => Loc.T(nameof(RestoreRunning));

    public static string RestoreDoneFormat => Loc.T(nameof(RestoreDoneFormat));

    /// <summary>
    /// Данные легли, но накат неполный (находка В6: на exFAT не создаются ссылки). «Не сделано»
    /// здесь — прямая ложь, а «разложена» — тоже: часть профиля движка не заработает.
    /// </summary>
    public static string RestoreIncompleteFormat => Loc.T(nameof(RestoreIncompleteFormat));

    public static string RestoreFailedFormat => Loc.T(nameof(RestoreFailedFormat));

    /// <summary>Находка В5: версия движка, которой снята копия, показывается человеку ДО наката.</summary>
    public static string RestoreVersionSameFormat => Loc.T(nameof(RestoreVersionSameFormat));

    public static string RestoreVersionDiffersFormat => Loc.T(nameof(RestoreVersionDiffersFormat));

    /// <summary>
    /// Третий ответ находки В5 (дефект Д3): версия копии есть, а нынешняя НЕИЗВЕСТНА — движка
    /// на машине нет вовсе. До 26.09.2026 здесь стоял второй ответ, и человек на чистой машине
    /// читал «той же версии, что стоит сейчас», хотя не стоит ничего. Тексты о версии лежат
    /// рядом и читаются вместе: два из них уже были, третий появился из живого прогона наката.
    /// </summary>
    public static string RestoreVersionNoEngineFormat => Loc.T(nameof(RestoreVersionNoEngineFormat));

    public static string RestoreVersionUnknown => Loc.T(nameof(RestoreVersionUnknown));

    // --- исход создания ссылки (находка В6) ----------------------------------
    //
    // ⚠️ Тексты, которые нужны для дела, живут ЗДЕСЬ, а не в документах: `docs\*`, `AGENTS.md`
    // и `README.md` — территория дирижёра. Проба носителя (VHDX, отформатирован в гостя как exFAT)
    // дала дословный ответ Windows: «Для завершения операции требуются локальные тома NTFS.»
    // (код 1 у `mklink /J`). До 26.09.2026 этот ответ читался и ВЫБРАСЫВАЛСЯ, поэтому на каждую
    // из 482 ссылок отчёт писал одинаковое «создать не удалось» без причины.

    /// <summary>Ответ Windows про ссылки — дословно: он и есть причина, названная словами.</summary>
    public static string JunctionWindowsFormat => Loc.T(nameof(JunctionWindowsFormat));

    /// <summary>Читаемый довод, когда видно, что том не NTFS (exFAT / FAT32 и прочие).</summary>
    public static string JunctionNotNtfsFormat => Loc.T(nameof(JunctionNotNtfsFormat));

    /// <summary>Ответа нет вовсе — так тоже бывает, и об этом говорим, а не молчим.</summary>
    public static string JunctionNoAnswer => Loc.T(nameof(JunctionNoAnswer));

    /// <summary>Что делать человеку — одной строкой, потому что в отчёте таких строк ровно одна.</summary>
    public static string JunctionAdvice => Loc.T(nameof(JunctionAdvice));

    // --- накат: сведение отказов ссылок в одну строку (находка В6) ------------

    /// <summary>
    /// Единственная строка отчёта про нелёгшие ссылки: причина словами и ЧИСЛО. Отдельные имена
    /// идут следом и не больше четырёх — пример, а не 482 одинаковые строки.
    /// </summary>
    public static string RestoreLinksFailedFormat => Loc.T(nameof(RestoreLinksFailedFormat));

    /// <summary>Первые имена нелёгших ссылок (не больше четырёх) и «и ещё N» вместо остальных.</summary>
    public static string RestoreLinksFailedNamesFormat => Loc.T(nameof(RestoreLinksFailedNamesFormat));

    public static string RestoreLinksFailedMoreFormat => Loc.T(nameof(RestoreLinksFailedMoreFormat));

    /// <summary>Отметка в итоге наката: ссылки не легли, и успешным такой накат не выглядит.</summary>
    public static string RestoreLinksNotCreatedFormat => Loc.T(nameof(RestoreLinksNotCreatedFormat));

    /// <summary>
    /// Почему группа движка или Node НЕ раскладывается в изолированном прогоне: её цель — место
    /// на ЭТОЙ машине, а оно лежит вне корня прогона (`Restore\RestoreConfine.cs`). Сказать об этом
    /// обязательно: пропуск, о котором молчат, человек читает как потерю данных.
    /// </summary>
    public static string RestoreEngineOutsideRootFormat => Loc.T(nameof(RestoreEngineOutsideRootFormat));

    /// <summary>Кнопка в главном окне: дверь в окно копий.</summary>
    public static string BackupsButton => Loc.T(nameof(BackupsButton));

    /// <summary>Подсказка под списком: как вернуть данные.</summary>
    public static string BackupListSelectHint => Loc.T(nameof(BackupListSelectHint));

    /// <summary>Строка журнала: окно копий открыто.</summary>
    public static string BackupsOpenedLog => Loc.T(nameof(BackupsOpenedLog));

    /// <summary>Строка журнала: окно копий уже открыто — подняли его на передний план.</summary>
    public static string BackupsRaisedLog => Loc.T(nameof(BackupsRaisedLog));

    /// <summary>Отказ прогона проверки: копии в нём не снимаются и не раскладываются (красная линия 5).</summary>
    public static string BackupNotAllowed => Loc.T(nameof(BackupNotAllowed));

    public static string BackupStopCancel => Loc.T(nameof(BackupStopCancel));

    // --- ключи в копии (решение владельца 26.09.2026: «как в v1») ---------------------------

    /// <summary>Галочка состава копии: класть ли в неё приватные ключи. По умолчанию НЕТ.</summary>
    public static string BackupKeysCheck => Loc.T(nameof(BackupKeysCheck));

    /// <summary>
    /// Предупреждение рядом с галочкой. Оно обязано быть: включённое разрешение означает, что
    /// архив САМ становится ключом доступа, и человеку об этом говорят до, а не после.
    /// </summary>
    public static string BackupKeysWarning => Loc.T(nameof(BackupKeysWarning));

    /// <summary>Заголовок списка каталогов, которые попадут в копию.</summary>
    public static string BackupKeysLabel => Loc.T(nameof(BackupKeysLabel));

    /// <summary>Разрешение выключено — говорим словами, что ни один каталог не поедет.</summary>
    public static string BackupKeysOff => Loc.T(nameof(BackupKeysOff));

    /// <summary>Разрешение включено, а существующих каталогов нет.</summary>
    public static string BackupKeysEmpty => Loc.T(nameof(BackupKeysEmpty));

    /// <summary>Строка списка: имя группы в архиве и каталог, откуда она взята.</summary>
    public static string BackupKeysItemFormat => Loc.T(nameof(BackupKeysItemFormat));

    /// <summary>Кнопка для найденного у человека каталога: предлагаем кнопкой, не подставляем молча.</summary>
    public static string BackupKeysAddFormat => Loc.T(nameof(BackupKeysAddFormat));

    /// <summary>Строка журнала и отчёта: решение о ключах в прогоне проверки менять нельзя.</summary>
    public static string BackupKeysLockedLog => Loc.T(nameof(BackupKeysLockedLog));

    /// <summary>Строка журнала: решение о ключах изменено и сохранено.</summary>
    public static string BackupKeysSavedLogFormat => Loc.T(nameof(BackupKeysSavedLogFormat));

    /// <summary>Замечание описи: что именно поехало в копию и чем от этого становится архив.</summary>
    public static string BackupKeysIncludedFormat => Loc.T(nameof(BackupKeysIncludedFormat));

    /// <summary>Каталог, названный человеком, на диске не найден. Молчать нельзя: он его назвал.</summary>
    public static string BackupKeyDirMissingFormat => Loc.T(nameof(BackupKeyDirMissingFormat));

    /// <summary>Замечание описи, когда разрешения нет: человек не должен думать, что «унёс всё».</summary>
    public static string BackupKeysNotIncluded => Loc.T(nameof(BackupKeysNotIncluded));

    /// <summary>Третье согласие наката: ключи возвращаются только по нему.</summary>
    public static string RestoreKeysCheck => Loc.T(nameof(RestoreKeysCheck));

    public static string RestoreKeysNeedConsent => Loc.T(nameof(RestoreKeysNeedConsent));

    /// <summary>Отчёт: ключи легли туда, откуда взяты (копия снята на этой машине этим пользователем).</summary>
    public static string RestoreKeysToOwnCopy => Loc.T(nameof(RestoreKeysToOwnCopy));

    /// <summary>Отчёт: каталог SSH вернулся в свой <c>.ssh</c> (у каждого пользователя он свой).</summary>
    public static string RestoreKeysToCurrentSsh => Loc.T(nameof(RestoreKeysToCurrentSsh));

    /// <summary>Отказ: незнакомый чужой каталог ключей не восстанавливается, и это названо словами.</summary>
    public static string RestoreKeysForeignFormat => Loc.T(nameof(RestoreKeysForeignFormat));

    /// <summary>То же, но в описи и пути нет: сказать всё равно надо, а назвать нечего.</summary>
    public static string RestoreKeysForeignNoPath => Loc.T(nameof(RestoreKeysForeignNoPath));

    /// <summary>
    /// Каталог ключей закрыть на владельца не удалось — об этом говорим, а не молчим. Второе место
    /// подстановки — ПРИЧИНА (дефект Д8): «не удалось» без неё не говорит человеку, что поправить.
    /// </summary>
    public static string RestoreKeysNotTightenedFormat => Loc.T(nameof(RestoreKeysNotTightenedFormat));

    /// <summary>Раздел настроек: состав копии.</summary>
    public static string SettingsKeysSection => Loc.T(nameof(SettingsKeysSection));

    public static string SettingsKeysDirsLabel => Loc.T(nameof(SettingsKeysDirsLabel));

    public static string SettingsKeysDirsHint => Loc.T(nameof(SettingsKeysDirsHint));

    // --- «эта копия — для передачи» (решения владельца 26.09.2026 и 27.09.2026) ---------------
    //
    // Довод владельца: копией делятся с другим человеком, и ключ модели уехал бы вместе с ней.
    // Поэтому файл ключей доступа в такой архив НЕ кладётся вовсе — не бланкируется: пустое
    // значение в нём роняет движок целиком («the value for … is empty; remove the key instead»).
    //
    // ⚠️ Решением владельца 27.09.2026 (п. 11 `docs\DESIGN.md`) это РАЗОВОЕ действие на ОДНУ
    // копию, а не настройка: галочка живёт в окне копий, снимается сама после копии, а ночные
    // копии передаваемыми не бывают никогда. Сочетание с приватными ключами запрещено
    // (п. 12, 29.09.2026) — словами отказа служит `BackupShareableWithKeysRefused`.

    /// <summary>Галочка в окне копий: «эта копия — для передачи», без ключей доступа.</summary>
    public static string BackupShareableCheck => Loc.T(nameof(BackupShareableCheck));

    /// <summary>Слово о СВОЕЙ копии в журнале: она снимается со всем, включая ключ доступа.</summary>
    public static string BackupShareableOff => Loc.T(nameof(BackupShareableOff));

    /// <summary>Предупреждение — видно, когда галочка стоит; говорит и то, что она снимется сама.</summary>
    public static string BackupShareableWarning => Loc.T(nameof(BackupShareableWarning));

    /// <summary>Пояснение, когда галочка снята: это своя копия, и передавать её нельзя.</summary>
    public static string BackupShareableOffNote => Loc.T(nameof(BackupShareableOffNote));

    /// <summary>Отказ от сочетания «приватные ключи + копия для передачи»: называет ОБА переключателя и что сделать.</summary>
    public static string BackupShareableWithKeysRefused => Loc.T(nameof(BackupShareableWithKeysRefused));

    /// <summary>Замечание в отчёте о снятии копии: файла ключей в архиве нет — и почему.</summary>
    public static string BackupShareableNote => Loc.T(nameof(BackupShareableNote));

    // --- три режима объёма копии (v2.2, `PROJECT.md` §3.3) -------------------
    //
    // Формулировка владельца: «автоматический — всё нужное без восстановимого мусора, `.git`
    // остаётся; полный — всё как есть, ничего не пропускать; свой фильтр — названные лишние имена
    // сверх автоматического списка». Режим выбирается в ОКНЕ КОПИЙ (решение владельца: настройка —
    // настройкой, действие — действием), а значение живёт в файле настроек.

    /// <summary>Подпись списка режимов объёма в окне копий.</summary>
    public static string BackupScopeLabel => Loc.T(nameof(BackupScopeLabel));

    /// <summary>Режим «автоматический» — умолчание и поведение прежних версий.</summary>
    public static string BackupScopeAuto => Loc.T(nameof(BackupScopeAuto));

    /// <summary>Режим «полный»: всё как есть, ничего не пропускать.</summary>
    public static string BackupScopeFull => Loc.T(nameof(BackupScopeFull));

    /// <summary>Режим «свой фильтр»: автоматический плюс названные человеком лишние имена.</summary>
    public static string BackupScopeCustom => Loc.T(nameof(BackupScopeCustom));

    /// <summary>Пояснение к автоматическому режиму — видно, когда он выбран.</summary>
    public static string BackupScopeAutoHint => Loc.T(nameof(BackupScopeAutoHint));

    /// <summary>
    /// Предупреждение к полному режиму: он снимает ВСЁ, включая восстановимый мусор, и потому
    /// копия бывает в разы больше. Сказано ДО начала копии — это требование решения владельца.
    /// </summary>
    public static string BackupScopeFullWarning => Loc.T(nameof(BackupScopeFullWarning));

    /// <summary>Пояснение к своему фильтру — что именно он делает с автоматическим списком.</summary>
    public static string BackupScopeCustomHint => Loc.T(nameof(BackupScopeCustomHint));

    /// <summary>Свой фильтр без названных имён: работает как автоматический, и это сказано словами.</summary>
    public static string BackupScopeCustomEmpty => Loc.T(nameof(BackupScopeCustomEmpty));

    /// <summary>Сколько примерно займут данные копии в выбранном режиме.</summary>
    public static string BackupScopeEstimateFormat => Loc.T(nameof(BackupScopeEstimateFormat));

    /// <summary>Размер считается в фоне: обход дерева небыстрый, и окно не должно замирать.</summary>
    public static string BackupScopeEstimateRunning => Loc.T(nameof(BackupScopeEstimateRunning));

    /// <summary>Подпись поля с лишними именами папок.</summary>
    public static string BackupScopeExtraLabel => Loc.T(nameof(BackupScopeExtraLabel));

    /// <summary>Как заполнять поле лишних имён и на что они действуют.</summary>
    public static string BackupScopeExtraHint => Loc.T(nameof(BackupScopeExtraHint));

    /// <summary>Кнопка сохранения списка лишних имён.</summary>
    public static string BackupScopeExtraSave => Loc.T(nameof(BackupScopeExtraSave));

    /// <summary>Журнал: режим объёма сохранён в настройки.</summary>
    public static string BackupScopeSavedLogFormat => Loc.T(nameof(BackupScopeSavedLogFormat));

    /// <summary>Журнал: список лишних имён сохранён (сколько и какие).</summary>
    public static string BackupScopeExtraSavedLogFormat => Loc.T(nameof(BackupScopeExtraSavedLogFormat));

    /// <summary>Замечание в отчёте: копия снята полным режимом — ничего не пропускалось.</summary>
    public static string BackupScopeFullNote => Loc.T(nameof(BackupScopeFullNote));

    /// <summary>Замечание в отчёте: что именно не попало в копию по своему фильтру.</summary>
    public static string BackupScopeCustomNoteFormat => Loc.T(nameof(BackupScopeCustomNoteFormat));

    /// <summary>
    /// Полный режим и названные лишние имена вместе: фильтр не действует, и об этом сказано —
    /// иначе выключенный фильтр выглядел бы забытым.
    /// </summary>
    public static string BackupScopeExtrasIgnored => Loc.T(nameof(BackupScopeExtrasIgnored));

    // --- расписание автокопий и ротация (v2.2) -------------------------------
    //
    // Решение владельца: копия снимается каждые N часов, панель смотрит, сколько прошло с прошлой,
    // и догоняет пропущенное при запуске. Работающий сервер ради копии НЕ гасится (решение
    // 26.09.2026: «агент у пользователя живёт постоянно, как ассистент») — копия снимается
    // на ходу, и человек об этом предупреждается. Прежнее требование аудита «перед копированием
    // останавливать сервер» (находка В2) этим отменено.

    /// <summary>Причина часов: расписание выключено человеком.</summary>
    public static string BackupScheduleOff => Loc.T(nameof(BackupScheduleOff));

    /// <summary>Причина часов: копий ещё не было — снимаем первую.</summary>
    public static string BackupScheduleNoCopies => Loc.T(nameof(BackupScheduleNoCopies));

    /// <summary>Причина часов: пора — сколько прошло из интервала.</summary>
    public static string BackupScheduleDueFormat => Loc.T(nameof(BackupScheduleDueFormat));

    /// <summary>Причина часов: рано — сколько прошло из интервала.</summary>
    public static string BackupScheduleEarlyFormat => Loc.T(nameof(BackupScheduleEarlyFormat));

    /// <summary>
    /// Причина часов: прошлая копия помечена будущим временем (часы переводили) — ждать нельзя,
    /// иначе копия не снимется вовсе.
    /// </summary>
    public static string BackupScheduleFutureFormat => Loc.T(nameof(BackupScheduleFutureFormat));

    /// <summary>Причина часов: после неудачной попытки ждём сторож, а не долбим диск.</summary>
    public static string BackupScheduleRetryFormat => Loc.T(nameof(BackupScheduleRetryFormat));

    /// <summary>Дописано к причине на первом такте после запуска: панель догоняет пропущенное.</summary>
    public static string BackupScheduleStartedSuffix => Loc.T(nameof(BackupScheduleStartedSuffix));

    /// <summary>Строка журнала: решение часов автокопии с причиной словами.</summary>
    public static string BackupScheduleLogFormat => Loc.T(nameof(BackupScheduleLogFormat));

    /// <summary>Журнал: часы автокопии запущены.</summary>
    public static string BackupScheduleClockLog => Loc.T(nameof(BackupScheduleClockLog));

    /// <summary>
    /// Журнал: автокопия подавлена — фоновая работа в изолированном прогоне не делается.
    /// Подавленное решение обязано быть названо, иначе «панель молчала» ничем не объяснить.
    /// </summary>
    public static string BackupScheduleSuppressedLog => Loc.T(nameof(BackupScheduleSuppressedLog));

    /// <summary>Журнал: копия по расписанию снята.</summary>
    public static string BackupScheduleDoneFormat => Loc.T(nameof(BackupScheduleDoneFormat));

    /// <summary>Журнал и сообщение: копия по расписанию не снята.</summary>
    public static string BackupScheduleFailedFormat => Loc.T(nameof(BackupScheduleFailedFormat));

    /// <summary>Сообщение человеку: копия по расписанию снята на ходу, целостности она не даёт.</summary>
    public static string NotifyLiveCopyTitle => Loc.T(nameof(NotifyLiveCopyTitle));

    /// <summary>Текст сообщения о копии на ходу: что именно произошло и что делать для целостной копии.</summary>
    public static string NotifyLiveCopyFormat => Loc.T(nameof(NotifyLiveCopyFormat));

    /// <summary>Сообщение человеку: копия не снялась (человека у окна нет, иначе он не узнает).</summary>
    public static string NotifyBackupFailedTitle => Loc.T(nameof(NotifyBackupFailedTitle));

    /// <summary>Текст сообщения о неудачной копии: причина.</summary>
    public static string NotifyBackupFailedFormat => Loc.T(nameof(NotifyBackupFailedFormat));

    /// <summary>Отказ панели: идёт другая работа с копиями — второй не начинаем.</summary>
    public static string BackupBusy => Loc.T(nameof(BackupBusy));

    /// <summary>Журнал ротации: удалять нечего — сколько копий и сколько храним.</summary>
    public static string BackupRotationNothingFormat => Loc.T(nameof(BackupRotationNothingFormat));

    /// <summary>Журнал ротации: копий столько, храним столько, удаляем перечисленное.</summary>
    public static string BackupRotationPlanFormat => Loc.T(nameof(BackupRotationPlanFormat));

    /// <summary>Одна удаляемая копия в причине ротации: имя и размер.</summary>
    public static string BackupRotationItemFormat => Loc.T(nameof(BackupRotationItemFormat));

    /// <summary>Журнал ротации: предохранительные копии перед накатом не трогаем (сколько их).</summary>
    public static string BackupRotationSafetyKeptFormat => Loc.T(nameof(BackupRotationSafetyKeptFormat));

    /// <summary>
    /// Журнал ротации: копии панели 1.x не удаляем и не считаем своими (сколько их). Отдельная строка,
    /// а не молчание: папка копий общая, и человек, видящий чужой архив в списке, обязан понимать,
    /// почему тот не убирается.
    /// </summary>
    public static string BackupRotationLegacyKeptFormat => Loc.T(nameof(BackupRotationLegacyKeptFormat));

    /// <summary>Журнал ротации: копии без разборчивого времени в имени не удаляем (какие).</summary>
    public static string BackupRotationUndatedFormat => Loc.T(nameof(BackupRotationUndatedFormat));

    /// <summary>Журнал и окно: ротация удалила столько копий и освободила столько.</summary>
    public static string BackupRotationDoneFormat => Loc.T(nameof(BackupRotationDoneFormat));

    /// <summary>Итог ротации с отказами: удалено, освобождено и что не отдалось.</summary>
    public static string BackupRotationFailedFormat => Loc.T(nameof(BackupRotationFailedFormat));

    /// <summary>Один не удалённый файл: имя и тип ошибки.</summary>
    public static string BackupRotationFailedItemFormat => Loc.T(nameof(BackupRotationFailedItemFormat));

    // --- раздел настроек «Копии»: расписание и хранение (v2.2) ---------------

    /// <summary>Галочка: снимать копии автоматически (умолчание — включено, как было в v1).</summary>
    public static string SettingsBackupScheduleCheck => Loc.T(nameof(SettingsBackupScheduleCheck));

    /// <summary>Подпись поля периодичности автокопии.</summary>
    public static string SettingsBackupEveryLabel => Loc.T(nameof(SettingsBackupEveryLabel));

    /// <summary>Подпись поля, сколько копий хранить.</summary>
    public static string SettingsBackupKeepLabel => Loc.T(nameof(SettingsBackupKeepLabel));

    /// <summary>Как работает расписание: «каждые N часов» и догон пропущенного при запуске.</summary>
    public static string SettingsBackupScheduleHint => Loc.T(nameof(SettingsBackupScheduleHint));

    /// <summary>Как работает хранение: лишние удаляются, свежая и предохранительные — нет.</summary>
    public static string SettingsBackupKeepHint => Loc.T(nameof(SettingsBackupKeepHint));

    /// <summary>
    /// Строка-совет в разделе настроек: автокопия снимается НА ХОДУ, сервер и агент не
    /// прерываются; целостную копию снимают вручную, когда работа не идёт. Решение владельца
    /// 26.09.2026 — человек обязан узнать это там, где включает расписание.
    /// </summary>
    public static string SettingsBackupLiveNote => Loc.T(nameof(SettingsBackupLiveNote));

    /// <summary>Та же правда в окне копий: копию можно снять на ходу, но целостной она не будет.</summary>
    public static string BackupLiveAdvice => Loc.T(nameof(BackupLiveAdvice));

    // --- добавлено переносом строк на словари (этап 5) ---

    /// <summary>Заголовок окна «О программе».</summary>
    public static string AboutTitle => Loc.T(nameof(AboutTitle));

    /// <summary>Пункт меню значка и кнопка: открыть окно «О программе». Многоточие — как у настроек: за кнопкой окно, а не действие.</summary>
    public static string AboutButton => Loc.T(nameof(AboutButton));

    /// <summary>Подзаголовок раздела «Что делает панель».</summary>
    public static string AboutPurposeHeading => Loc.T(nameof(AboutPurposeHeading));

    /// <summary>Одна фраза о том, зачем панель нужна.</summary>
    public static string AboutPurpose => Loc.T(nameof(AboutPurpose));

    /// <summary>Строка версии панели.</summary>
    public static string AboutVersionFormat => Loc.T(nameof(AboutVersionFormat));

    /// <summary>Лицензия продукта. Решение владельца 26.09.2026.</summary>
    public static string AboutLicense => Loc.T(nameof(AboutLicense));

    /// <summary>Автор продукта. Имя — не перевод: оно одинаково во всех трёх словарях.</summary>
    public static string AboutCopyright => Loc.T(nameof(AboutCopyright));

    /// <summary>Подзаголовок раздела ссылок.</summary>
    public static string AboutLinksHeading => Loc.T(nameof(AboutLinksHeading));

    /// <summary>Подпись ссылки на репозиторий.</summary>
    public static string AboutRepoLink => Loc.T(nameof(AboutRepoLink));

    /// <summary>Подпись ссылки на донаты.</summary>
    public static string AboutDonateLink => Loc.T(nameof(AboutDonateLink));

    /// <summary>Пояснение про донаты: почему просят и на что идёт.</summary>
    public static string AboutDonateNote => Loc.T(nameof(AboutDonateNote));

    /// <summary>Честная строка про качество переводов.</summary>
    public static string AboutTranslationNote => Loc.T(nameof(AboutTranslationNote));

    /// <summary>Пустой адрес ссылки. Ровно этот случай в v1 показывал имя ключа вместо объяснения.</summary>
    public static string AboutNoLink => Loc.T(nameof(AboutNoLink));

    /// <summary>Открыть ссылку не вышло — с причиной.</summary>
    public static string AboutLinkFailedFormat => Loc.T(nameof(AboutLinkFailedFormat));

    /// <summary>Кнопка закрытия окна «О программе».</summary>
    public static string AboutCloseButton => Loc.T(nameof(AboutCloseButton));

    /// <summary>Строка журнала: окно «О программе» открыто.</summary>
    public static string AboutOpenedLog => Loc.T(nameof(AboutOpenedLog));

    /// <summary>Строка журнала: окно «О программе» уже было открыто.</summary>
    public static string AboutRaisedLog => Loc.T(nameof(AboutRaisedLog));

    /// <summary>Подпись списка выбора языка.</summary>
    public static string SettingsLanguageLabel => Loc.T(nameof(SettingsLanguageLabel));

    /// <summary>Значение «язык как в системе».</summary>
    public static string SettingsLanguageAuto => Loc.T(nameof(SettingsLanguageAuto));

    /// <summary>Название языка — на нём самом, во всех трёх словарях одинаково.</summary>
    public static string SettingsLanguageRussian => Loc.T(nameof(SettingsLanguageRussian));

    /// <summary>Название языка — на нём самом, во всех трёх словарях одинаково.</summary>
    public static string SettingsLanguageEnglish => Loc.T(nameof(SettingsLanguageEnglish));

    /// <summary>Название языка — на нём самом, во всех трёх словарях одинаково.</summary>
    public static string SettingsLanguageChinese => Loc.T(nameof(SettingsLanguageChinese));

    /// <summary>Решение дирижёра: язык применяется при следующем запуске, и человеку об этом сказано словами.</summary>
    public static string SettingsLanguageAppliesNextStart => Loc.T(nameof(SettingsLanguageAppliesNextStart));

    /// <summary>Имя продукта: заголовок главного окна. Не переводится.</summary>
    public static string AppName => Loc.T(nameof(AppName));

    /// <summary>Заголовок в самом окне панели. Не переводится.</summary>
    public static string AppTitle => Loc.T(nameof(AppTitle));

    /// <summary>
    /// МОНОГРАММА ЗНАКА в шапке главного окна: буквы «DSH». Латинские во всех трёх языках —
    /// имя изделия не переводят, а подпись рядом переводится (AppTitle).
    /// </summary>
    public static string AppMonogram => Loc.T(nameof(AppMonogram));

    /// <summary>Заголовок окна настроек в разметке.</summary>
    public static string SettingsWindowTitle => Loc.T(nameof(SettingsWindowTitle));

    /// <summary>Пояснение под строкой состояния, пока сервер поднимается: окно обязано остаться живым.</summary>
    public static string ServerStartingDetail => Loc.T(nameof(ServerStartingDetail));

    /// <summary>Пояснение, когда окно построено без контроллера сервера.</summary>
    public static string ServerUnboundDetail => Loc.T(nameof(ServerUnboundDetail));

    /// <summary>Строка журнала: значок трея поставлен.</summary>
    public static string PanelLogTrayPlaced => Loc.T(nameof(PanelLogTrayPlaced));

    /// <summary>Строка журнала: просьба открыть настройки, а окна у панели нет.</summary>
    public static string PanelLogSettingsUnavailable => Loc.T(nameof(PanelLogSettingsUnavailable));

    /// <summary>Строка журнала: просьба открыть копии, а окна у панели нет.</summary>
    public static string PanelLogBackupsUnavailable => Loc.T(nameof(PanelLogBackupsUnavailable));

    /// <summary>Строка журнала: панель завершается.</summary>
    public static string PanelLogExit => Loc.T(nameof(PanelLogExit));

    /// <summary>Строка журнала: часы обновления баланса запущены.</summary>
    public static string PanelLogBalanceClock => Loc.T(nameof(PanelLogBalanceClock));

    /// <summary>Строка журнала: значок не встал — тип ошибки и её текст.</summary>
    public static string PanelLogTrayIconFailedFormat => Loc.T(nameof(PanelLogTrayIconFailedFormat));

    /// <summary>Значок трея взят из сборки — что именно собралось.</summary>
    public static string TrayIconOwnFormat => Loc.T(nameof(TrayIconOwnFormat));

    /// <summary>Свой значок не собрался, стоит системный запасной — с причиной.</summary>
    public static string TrayIconFallbackFormat => Loc.T(nameof(TrayIconFallbackFormat));

    /// <summary>Причина: файла значка в сборке нет.</summary>
    public static string TrayIconNoResource => Loc.T(nameof(TrayIconNoResource));

    /// <summary>Причина: байты значка не читаются как .ico.</summary>
    public static string TrayIconBytesBroken => Loc.T(nameof(TrayIconBytesBroken));

    /// <summary>Причина: в значке нет ни одного размера.</summary>
    public static string TrayIconNoFrames => Loc.T(nameof(TrayIconNoFrames));

    /// <summary>Причина: Windows не собрала значок из кадра — размер и объём кадра.</summary>
    public static string TrayIconBuildFailedFormat => Loc.T(nameof(TrayIconBuildFailedFormat));

    /// <summary>Дописано к причине, когда Windows назвала код ошибки.</summary>
    public static string TrayIconBuildFailedCodeFormat => Loc.T(nameof(TrayIconBuildFailedCodeFormat));

    /// <summary>Что получилось: кадр источника, размер и число кадров.</summary>
    public static string TrayIconFrameFormat => Loc.T(nameof(TrayIconFrameFormat));

    /// <summary>
    /// Строка журнала: огонёк состояния на значке не собрался — с причиной. Отступление, а не
    /// ошибка: значок при этом остаётся прежним (без огонька), и трей продолжает работать.
    /// </summary>
    public static string TrayIconDotFailedFormat => Loc.T(nameof(TrayIconDotFailedFormat));

    /// <summary>Причина: у значка нет основы — класть огонёк не на что.</summary>
    public static string TrayIconDotNoBase => Loc.T(nameof(TrayIconDotNoBase));

    /// <summary>Причина: точки значка не читаются — с кодом ошибки Windows.</summary>
    public static string TrayIconDotNoPixelsFormat => Loc.T(nameof(TrayIconDotNoPixelsFormat));

    /// <summary>Причина: у значка нет канала прозрачности (форму задаёт маска, а не точки).</summary>
    public static string TrayIconDotNoAlpha => Loc.T(nameof(TrayIconDotNoAlpha));

    /// <summary>Причина: Windows не собрала значок с огоньком — с кодом ошибки.</summary>
    public static string TrayIconDotBuildFailedFormat => Loc.T(nameof(TrayIconDotBuildFailedFormat));

    /// <summary>Строка окна настроек. Перенесено из Settings\SettingsStore.cs.</summary>
    public static string SettingsBadFileEmpty => Loc.T(nameof(SettingsBadFileEmpty));

    /// <summary>Строка окна настроек. Перенесено из Settings\SettingsStore.cs.</summary>
    public static string SettingsBadFileFormat => Loc.T(nameof(SettingsBadFileFormat));

    /// <summary>Строка окна настроек. Перенесено из Settings\SettingsStore.cs.</summary>
    public static string SettingsUnknownKeysFormat => Loc.T(nameof(SettingsUnknownKeysFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KeyDirAndroid => Loc.T(nameof(KeyDirAndroid));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KeyDirAndroidByVariableFormat => Loc.T(nameof(KeyDirAndroidByVariableFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KeyDirAndroidNearProfile => Loc.T(nameof(KeyDirAndroidNearProfile));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KillAnotherProcessFormat => Loc.T(nameof(KillAnotherProcessFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KillNoPid => Loc.T(nameof(KillNoPid));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string KillNoStartTime => Loc.T(nameof(KillNoStartTime));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedExitedFormat => Loc.T(nameof(SrvAdoptedExitedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedLogFormat => Loc.T(nameof(SrvAdoptedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedNeedsConfirmFormat => Loc.T(nameof(SrvAdoptedNeedsConfirmFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStopFailedFormat => Loc.T(nameof(SrvAdoptedStopFailedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStopFailedLogFormat => Loc.T(nameof(SrvAdoptedStopFailedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStopped => Loc.T(nameof(SrvAdoptedStopped));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStoppedPortBusyFormat => Loc.T(nameof(SrvAdoptedStoppedPortBusyFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStoppedPortFreeFormat => Loc.T(nameof(SrvAdoptedStoppedPortFreeFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptedStopUnconfirmedLogFormat => Loc.T(nameof(SrvAdoptedStopUnconfirmedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptGoneFormat => Loc.T(nameof(SrvAdoptGoneFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptNoProcessInfo => Loc.T(nameof(SrvAdoptNoProcessInfo));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptNoStartTimeFormat => Loc.T(nameof(SrvAdoptNoStartTimeFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAdoptNothingFormat => Loc.T(nameof(SrvAdoptNothingFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAlreadyManagedFormat => Loc.T(nameof(SrvAlreadyManagedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAnswersAdoptedFormat => Loc.T(nameof(SrvAnswersAdoptedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvAnswersFormat => Loc.T(nameof(SrvAnswersFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvDetachedFormat => Loc.T(nameof(SrvDetachedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvDetachedLogFormat => Loc.T(nameof(SrvDetachedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineExited => Loc.T(nameof(SrvEngineExited));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineExitedItselfDetailFormat => Loc.T(nameof(SrvEngineExitedItselfDetailFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineExitedItselfFormat => Loc.T(nameof(SrvEngineExitedItselfFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineLineLogFormat => Loc.T(nameof(SrvEngineLineLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineLineSuffixFormat => Loc.T(nameof(SrvEngineLineSuffixFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineStartedLogFormat => Loc.T(nameof(SrvEngineStartedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineStartFailedFormat => Loc.T(nameof(SrvEngineStartFailedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineStartRefused => Loc.T(nameof(SrvEngineStartRefused));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvEngineStillStartingFormat => Loc.T(nameof(SrvEngineStillStartingFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvForeignRunningFormat => Loc.T(nameof(SrvForeignRunningFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvGoneNothingToStopFormat => Loc.T(nameof(SrvGoneNothingToStopFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvListenerUnknownFormat => Loc.T(nameof(SrvListenerUnknownFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvNoEngine => Loc.T(nameof(SrvNoEngine));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvNothingToStopLogFormat => Loc.T(nameof(SrvNothingToStopLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvNotStartedYet => Loc.T(nameof(SrvNotStartedYet));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortBusyFormat => Loc.T(nameof(SrvPortBusyFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortBusyStartFormat => Loc.T(nameof(SrvPortBusyStartFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortFree => Loc.T(nameof(SrvPortFree));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortFreeFormat => Loc.T(nameof(SrvPortFreeFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortLeftBusyFormat => Loc.T(nameof(SrvPortLeftBusyFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortReplacedLogFormat => Loc.T(nameof(SrvPortReplacedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvPortStillBusyFormat => Loc.T(nameof(SrvPortStillBusyFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvProcessUnknown => Loc.T(nameof(SrvProcessUnknown));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvRememberFailedFormat => Loc.T(nameof(SrvRememberFailedFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvStopEngineFailedLogFormat => Loc.T(nameof(SrvStopEngineFailedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvStopped => Loc.T(nameof(SrvStopped));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvStoppedPortFreeLog => Loc.T(nameof(SrvStoppedPortFreeLog));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvStoppingAdoptedLogFormat => Loc.T(nameof(SrvStoppingAdoptedLogFormat));

    /// <summary>Строка сервера: состояние, отказ или строка журнала панели.</summary>
    public static string SrvStoppingEngineLogFormat => Loc.T(nameof(SrvStoppingEngineLogFormat));

    // --- автоподъём сервера при старте панели (решение владельца 26.09.2026) ----

    /// <summary>Строка журнала: автоподъём включён, панель поднимает свой сервер сама.</summary>
    public static string AutoStartEnabledLog => Loc.T(nameof(AutoStartEnabledLog));

    /// <summary>Строка журнала: у этого прогона нет права поднимать сервер — с причиной.</summary>
    public static string AutoStartRefusedLogFormat => Loc.T(nameof(AutoStartRefusedLogFormat));

    /// <summary>Причина отказа автоподъёма: прогон изолированный.</summary>
    public static string AutoStartRefusedIsolated => Loc.T(nameof(AutoStartRefusedIsolated));

    /// <summary>Причина отказа автоподъёма: это прогон проверки, а не запуск человеком.</summary>
    public static string AutoStartRefusedCheckRun => Loc.T(nameof(AutoStartRefusedCheckRun));

    /// <summary>Строка журнала: файлы состояния сервера в этом прогоне не читаются и не пишутся.</summary>
    public static string ServerStateFilesSuppressedLog => Loc.T(nameof(ServerStateFilesSuppressedLog));

    /// <summary>Строка журнала: права «только владелец» на файл ссылки поставить не удалось.</summary>
    public static string SrvEntryLinkRightsFailedFormat => Loc.T(nameof(SrvEntryLinkRightsFailedFormat));

    /// <summary>Строка журнала: автоподъём решил поднимать.</summary>
    public static string AutoStartStartingLog => Loc.T(nameof(AutoStartStartingLog));

    /// <summary>Строка журнала: автоподъём пропущен — рядом работает найденный DSH (замок от второго движка).</summary>
    public static string AutoStartSkipForeignFormat => Loc.T(nameof(AutoStartSkipForeignFormat));

    /// <summary>Строка журнала: автоподъём пропущен — панель уже управляет сервером.</summary>
    public static string AutoStartSkipManagedLog => Loc.T(nameof(AutoStartSkipManagedLog));

    /// <summary>Строка журнала: автоподъём пропущен — на порту уже кто-то отвечает.</summary>
    public static string AutoStartSkipBusyFormat => Loc.T(nameof(AutoStartSkipBusyFormat));

    // --- ссылка входа и кнопка «Открыть панель» ------------------------------
    //
    // ⚠️ Ни одна из этих строк НЕ СОДЕРЖИТ САМОЙ ССЫЛКИ и не принимает её подстановкой:
    // в ссылке токен, и показывать её человеку нельзя ни текстом, ни подсказкой.

    /// <summary>Подпись кнопки и пункта меню: открыть работу агента в браузере.</summary>
    public static string OpenAgentButton => Loc.T(nameof(OpenAgentButton));

    /// <summary>Подсказка под доступной кнопкой: работа агента откроется в браузере по щелчку человека.</summary>
    public static string OpenAgentReadyHint => Loc.T(nameof(OpenAgentReadyHint));

    /// <summary>Подсказка под недоступной кнопкой: сервер не работает.</summary>
    public static string OpenAgentNoServerHint => Loc.T(nameof(OpenAgentNoServerHint));

    /// <summary>Подсказка под недоступной кнопкой: ссылка входа ещё неизвестна.</summary>
    public static string OpenAgentNoLinkHint => Loc.T(nameof(OpenAgentNoLinkHint));

    /// <summary>Заголовок сообщения: открыть агента не удалось.</summary>
    public static string OpenAgentFailedTitle => Loc.T(nameof(OpenAgentFailedTitle));

    /// <summary>Заголовок сообщения: ссылка входа неизвестна.</summary>
    public static string OpenAgentNoLinkTitle => Loc.T(nameof(OpenAgentNoLinkTitle));

    /// <summary>Строка журнала: работа агента открыта в браузере по щелчку человека.</summary>
    public static string OpenAgentOpenedLog => Loc.T(nameof(OpenAgentOpenedLog));

    /// <summary>Строка журнала: открыть агента в браузере не удалось — с причиной.</summary>
    public static string OpenAgentOpenFailedLogFormat => Loc.T(nameof(OpenAgentOpenFailedLogFormat));

    /// <summary>Строка журнала: браузер не открываем — это прогон проверки, а не запуск человеком.</summary>
    public static string AgentBrowserSuppressedLog => Loc.T(nameof(AgentBrowserSuppressedLog));

    /// <summary>Строка журнала: ссылку входа сохранить не удалось — с причиной.</summary>
    public static string SrvEntryLinkSaveFailedLogFormat => Loc.T(nameof(SrvEntryLinkSaveFailedLogFormat));

    /// <summary>Строка журнала: ссылка входа получена и сохранена (в самой строке токена нет).</summary>
    public static string SrvEntryLinkFoundLogFormat => Loc.T(nameof(SrvEntryLinkFoundLogFormat));

    /// <summary>Строка журнала: ссылка входа получена и живёт только в памяти — писать её некуда.</summary>
    public static string SrvEntryLinkFoundInMemoryLogFormat => Loc.T(nameof(SrvEntryLinkFoundInMemoryLogFormat));

    /// <summary>Строка журнала: ссылка входа взята у панели 1.x (в самой строке токена нет).</summary>
    public static string SrvEntryLinkFromV1LogFormat => Loc.T(nameof(SrvEntryLinkFromV1LogFormat));

    /// <summary>Строка журнала: ссылка входа забыта — своего сервера больше нет.</summary>
    public static string SrvEntryLinkClearedLog => Loc.T(nameof(SrvEntryLinkClearedLog));

    // --- свой сервер, узнанный после перезапуска панели -----------------------

    /// <summary>Строка журнала: свой сервер записан на диск (порт и процесс).</summary>
    public static string SrvOwnServerRecordedLogFormat => Loc.T(nameof(SrvOwnServerRecordedLogFormat));

    /// <summary>Строка журнала: запись о своём сервере не сохранилась — с причиной.</summary>
    public static string SrvOwnServerRecordFailedLogFormat => Loc.T(nameof(SrvOwnServerRecordFailedLogFormat));

    /// <summary>Строка журнала: время создания своего процесса не прочитать — записывать нечего.</summary>
    public static string SrvOwnServerNoStartTimeLog => Loc.T(nameof(SrvOwnServerNoStartTimeLog));

    /// <summary>Строка журнала: свой сервер узнан по записи после перезапуска панели.</summary>
    public static string SrvOwnServerRestoredLogFormat => Loc.T(nameof(SrvOwnServerRestoredLogFormat));

    /// <summary>Строка журнала: запись о своём сервере устарела и снята.</summary>
    public static string SrvOwnServerStaleLogFormat => Loc.T(nameof(SrvOwnServerStaleLogFormat));

    /// <summary>Строка журнала: запись о своём сервере снята при остановке.</summary>
    public static string SrvOwnServerClearedLogFormat => Loc.T(nameof(SrvOwnServerClearedLogFormat));

    /// <summary>Отказ: записи о своём сервере нет — гасить по ней нечем.</summary>
    public static string SrvOwnRecordMissingLog => Loc.T(nameof(SrvOwnRecordMissingLog));

    /// <summary>Отказ: своего сервера на порту уже нет — гасить нечего.</summary>
    public static string SrvOwnGoneNothingToStopFormat => Loc.T(nameof(SrvOwnGoneNothingToStopFormat));

    /// <summary>Строка журнала: гашу свой сервер по записи — процесс, PID и порт.</summary>
    public static string SrvStoppingOwnByRecordLogFormat => Loc.T(nameof(SrvStoppingOwnByRecordLogFormat));

    /// <summary>Строка журнала: гашение своего сервера по записи не удалось — с причиной.</summary>
    public static string SrvOwnStopFailedLogFormat => Loc.T(nameof(SrvOwnStopFailedLogFormat));

    /// <summary>Состояние: свой сервер погасить не удалось — порт и причина.</summary>
    public static string SrvOwnStopFailedFormat => Loc.T(nameof(SrvOwnStopFailedFormat));

    /// <summary>Строка журнала: порт стал нашим — прежнее согласие на найденный сервер забыто.</summary>
    public static string SrvConsentForgottenOwnPortFormat => Loc.T(nameof(SrvConsentForgottenOwnPortFormat));

    /// <summary>Замечание плана копии: файл ссылки входа в «копию для передачи» не кладётся.</summary>
    public static string BackupEntryLinkNote => Loc.T(nameof(BackupEntryLinkNote));

    /// <summary>Замечание в настройках: порт из файла — умолчание прежней версии панели и заменён.</summary>
    public static string SettingsPortMigratedFormat => Loc.T(nameof(SettingsPortMigratedFormat));

    /// <summary>Замечание плана: архив лежит в корне копируемого дерева.</summary>
    public static string BackupArchiveInRootFormat => Loc.T(nameof(BackupArchiveInRootFormat));

    /// <summary>Отчёт копии: путь архива не разобран — с причиной.</summary>
    public static string BackupArchivePathBrokenFormat => Loc.T(nameof(BackupArchivePathBrokenFormat));

    /// <summary>Отчёт копии: план не собрался — с причиной.</summary>
    public static string BackupCannotBuildFormat => Loc.T(nameof(BackupCannotBuildFormat));

    /// <summary>Строка отчёта: копия снята — объём, число файлов, время и чем снята.</summary>
    public static string BackupEngineDoneFormat => Loc.T(nameof(BackupEngineDoneFormat));

    /// <summary>Замечание описи: копия снята на работающем сервере, и это не «застывшее» состояние.</summary>
    public static string BackupEngineLiveCopyNote => Loc.T(nameof(BackupEngineLiveCopyNote));

    /// <summary>Замечание плана: движка и Node нет — копия несамодостаточна.</summary>
    public static string BackupEngineNotFound => Loc.T(nameof(BackupEngineNotFound));

    /// <summary>Замечание плана: один корень содержит другой.</summary>
    public static string BackupFolderContainsFormat => Loc.T(nameof(BackupFolderContainsFormat));

    /// <summary>Замечание плана: папка копий внутри копируемого дерева — иначе копия росла бы вдвое.</summary>
    public static string BackupFolderInsideRootFormat => Loc.T(nameof(BackupFolderInsideRootFormat));

    /// <summary>Замечание плана: один корень лежит внутри другого.</summary>
    public static string BackupFolderNestedFormat => Loc.T(nameof(BackupFolderNestedFormat));

    /// <summary>Замечание плана: каталог ключей оказался корнем диска, имени для группы у него нет.</summary>
    public static string BackupKeyDirIsDriveRootFormat => Loc.T(nameof(BackupKeyDirIsDriveRootFormat));

    /// <summary>Одно место на всю панель: слова про пустой план (чистая машина, терять нечего).</summary>
    public static string BackupNothingToLose => Loc.T(nameof(BackupNothingToLose));

    /// <summary>Отчёт копии: снять не удалось — с причиной.</summary>
    public static string BackupNotTakenFormat => Loc.T(nameof(BackupNotTakenFormat));

    /// <summary>Замечание описи: проверить архив нечем, и ложного «проверено» не будет.</summary>
    public static string BackupNoVerifier => Loc.T(nameof(BackupNoVerifier));

    /// <summary>Замечание плана: каталог глобальных пакетов не определён по пути движка.</summary>
    public static string BackupNpmDirUnknownFormat => Loc.T(nameof(BackupNpmDirUnknownFormat));

    /// <summary>Отказ: по пути нет ни файла, ни каталога.</summary>
    public static string BackupPathMissing => Loc.T(nameof(BackupPathMissing));

    /// <summary>Отказ: путь не задан.</summary>
    public static string BackupPathNotSet => Loc.T(nameof(BackupPathNotSet));

    /// <summary>Строка отчёта плана: собрать копию нельзя — с причиной.</summary>
    public static string BackupPlanCannotBuildFormat => Loc.T(nameof(BackupPlanCannotBuildFormat));

    /// <summary>Отказ без причины: пустой план обязан быть назван словами (дефект Д2).</summary>
    public static string BackupPlanEmptyNoReason => Loc.T(nameof(BackupPlanEmptyNoReason));

    /// <summary>Замечание плана: названного каталога ключей нет на диске — с назначением.</summary>
    public static string BackupPlanKeyDirMissingFormat => Loc.T(nameof(BackupPlanKeyDirMissingFormat));

    /// <summary>Подстановка причины, когда её нет: пустое место хуже честного «не названа».</summary>
    public static string BackupReasonUnnamed => Loc.T(nameof(BackupReasonUnnamed));

    /// <summary>Замечание описи: права архива закрыть не удалось — с причиной от Windows.</summary>
    public static string BackupRightsNotRestrictedFormat => Loc.T(nameof(BackupRightsNotRestrictedFormat));

    /// <summary>Дописано к отчёту: права закрыть не удалось, и это прямо названо.</summary>
    public static string BackupRightsOpenWarning => Loc.T(nameof(BackupRightsOpenWarning));

    /// <summary>Дописано к отчёту: права архива закрыты на владельца.</summary>
    public static string BackupRightsOwnerOnly => Loc.T(nameof(BackupRightsOwnerOnly));

    /// <summary>Что кладёт в копию корень домашнего каталога движка.</summary>
    public static string BackupRootDshHome => Loc.T(nameof(BackupRootDshHome));

    /// <summary>Что кладёт в копию корень глобальных пакетов npm.</summary>
    public static string BackupRootEnginePackages => Loc.T(nameof(BackupRootEnginePackages));

    /// <summary>Что кладёт в копию корень каталога приватных ключей.</summary>
    public static string BackupRootKeys => Loc.T(nameof(BackupRootKeys));

    /// <summary>Что кладёт в копию корень Node.</summary>
    public static string BackupRootNode => Loc.T(nameof(BackupRootNode));

    /// <summary>Что кладёт в копию корень папки панели.</summary>
    public static string BackupRootPanel => Loc.T(nameof(BackupRootPanel));

    /// <summary>Строка отчёта плана: сколько корней и что в них.</summary>
    public static string BackupRootsFormat => Loc.T(nameof(BackupRootsFormat));

    /// <summary>Что кладёт в копию корень рабочей папки.</summary>
    public static string BackupRootWorkingFolder => Loc.T(nameof(BackupRootWorkingFolder));

    /// <summary>Замечание плана: два корня оказались одной папкой.</summary>
    public static string BackupSameFolderFormat => Loc.T(nameof(BackupSameFolderFormat));

    /// <summary>Отказ плана: две группы с одним именем — записи смешались бы. Здесь не замечание, а отказ.</summary>
    public static string BackupSameGroupNameFormat => Loc.T(nameof(BackupSameGroupNameFormat));

    /// <summary>Отказ: текущего пользователя определить не удалось.</summary>
    public static string BackupUserUnknown => Loc.T(nameof(BackupUserUnknown));

    /// <summary>Отказ копии: валидатор нашёл поломку, архив оставлен для разбора и копией не считается.</summary>
    public static string BackupVerifyFailedFormat => Loc.T(nameof(BackupVerifyFailedFormat));

    /// <summary>Замечание плана: рабочая папка не задана, и это не «тихая потеря».</summary>
    public static string BackupWorkDirNotSet => Loc.T(nameof(BackupWorkDirNotSet));

    /// <summary>Дописано к отчёту: валидатор нашёл поломку.</summary>
    public static string BackupZipBroken => Loc.T(nameof(BackupZipBroken));

    /// <summary>Дописано к отчёту: проверить архив нечем.</summary>
    public static string BackupZipUnverifiable => Loc.T(nameof(BackupZipUnverifiable));

    /// <summary>Дописано к отчёту: сторонний валидатор архив принял.</summary>
    public static string BackupZipVerified => Loc.T(nameof(BackupZipVerified));

    /// <summary>Отчёт: 7-Zip не найден, копия делается своими силами.</summary>
    public static string SevenZipMissing => Loc.T(nameof(SevenZipMissing));

    /// <summary>Отказ 7-Zip: добавлять нечего — путь пуст.</summary>
    public static string SevenZipNothingToAdd => Loc.T(nameof(SevenZipNothingToAdd));

    /// <summary>Отказ 7-Zip: процесс не запустился.</summary>
    public static string SevenZipNotStarted => Loc.T(nameof(SevenZipNotStarted));

    /// <summary>Отказ 7-Zip: у корня нет имени папки.</summary>
    public static string SevenZipRootHasNoName => Loc.T(nameof(SevenZipRootHasNoName));

    /// <summary>Отказ 7-Zip: у корня нет родительского каталога.</summary>
    public static string SevenZipRootHasNoParent => Loc.T(nameof(SevenZipRootHasNoParent));

    /// <summary>Отказ 7-Zip: не уложился в срок — срок назван.</summary>
    public static string SevenZipTimeoutFormat => Loc.T(nameof(SevenZipTimeoutFormat));

    /// <summary>Отказ чтения: архива нет по пути (путь под маской).</summary>
    public static string ZipArchiveMissingFormat => Loc.T(nameof(ZipArchiveMissingFormat));

    /// <summary>Отказ чтения: путь к архиву не задан.</summary>
    public static string ZipArchivePathNotSet => Loc.T(nameof(ZipArchivePathNotSet));

    /// <summary>Отказ: в архиве нет ни одного файла.</summary>
    public static string ZipCreatedEmpty => Loc.T(nameof(ZipCreatedEmpty));

    /// <summary>Отказ: архив создан, но не читается — копией не считается.</summary>
    public static string ZipCreatedUnreadable => Loc.T(nameof(ZipCreatedUnreadable));

    /// <summary>Отказ записи: архив не создать по пути (путь под маской) — с причиной.</summary>
    public static string ZipCreateFailedFormat => Loc.T(nameof(ZipCreateFailedFormat));

    /// <summary>Отказ: записи архива не перечислить — описи не собрать.</summary>
    public static string ZipEntriesUnreadableFormat => Loc.T(nameof(ZipEntriesUnreadableFormat));

    /// <summary>Отказ: файл не прочитан — имя, путь под маской, тип ошибки и её слова.</summary>
    public static string ZipFileUnreadableFormat => Loc.T(nameof(ZipFileUnreadableFormat));

    /// <summary>Строка группы архива: пусто без причины.</summary>
    public static string ZipGroupEmptyFormat => Loc.T(nameof(ZipGroupEmptyFormat));

    /// <summary>Строка группы архива: пусто, и названа причина.</summary>
    public static string ZipGroupEmptyWithNoteFormat => Loc.T(nameof(ZipGroupEmptyWithNoteFormat));

    /// <summary>Отказ поиска группы: записей нет.</summary>
    public static string ZipGroupEntriesMissing => Loc.T(nameof(ZipGroupEntriesMissing));

    /// <summary>Строка группы архива: имя группы и число файлов.</summary>
    public static string ZipGroupFilesFormat => Loc.T(nameof(ZipGroupFilesFormat));

    /// <summary>Отказ поиска группы: записи лежат под другими именами — они названы.</summary>
    public static string ZipGroupForeignNamesFormat => Loc.T(nameof(ZipGroupForeignNamesFormat));

    /// <summary>Отказ поиска группы: имя не задано.</summary>
    public static string ZipGroupNameNotSet => Loc.T(nameof(ZipGroupNameNotSet));

    /// <summary>Отказ раскладки: две группы с одним именем.</summary>
    public static string ZipLayoutDuplicateGroupFormat => Loc.T(nameof(ZipLayoutDuplicateGroupFormat));

    /// <summary>Отказ раскладки: имя группы не совпало с именем папки на диске.</summary>
    public static string ZipLayoutGroupMismatchFormat => Loc.T(nameof(ZipLayoutGroupMismatchFormat));

    /// <summary>Отказ раскладки: вложенное имя группы — 7-Zip так не умеет.</summary>
    public static string ZipLayoutNestedGroupFormat => Loc.T(nameof(ZipLayoutNestedGroupFormat));

    /// <summary>Отказ раскладки: у источника нет имени папки — это корень диска.</summary>
    public static string ZipLayoutNoFolderNameFormat => Loc.T(nameof(ZipLayoutNoFolderNameFormat));

    /// <summary>Решение раскладки: 7-Zip нет, архивируем встроенным писателем.</summary>
    public static string ZipLayoutNoSevenZip => Loc.T(nameof(ZipLayoutNoSevenZip));

    /// <summary>Отказ раскладки: источников нет.</summary>
    public static string ZipLayoutNoSources => Loc.T(nameof(ZipLayoutNoSources));

    /// <summary>Отказ раскладки: у источника нет имени группы.</summary>
    public static string ZipLayoutSourceNoGroupFormat => Loc.T(nameof(ZipLayoutSourceNoGroupFormat));

    /// <summary>Отказ чтения описи: архива нет по пути (путь под маской).</summary>
    public static string ZipManifestArchiveMissingFormat => Loc.T(nameof(ZipManifestArchiveMissingFormat));

    /// <summary>Отказ чтения описи: она пуста.</summary>
    public static string ZipManifestEmpty => Loc.T(nameof(ZipManifestEmpty));

    /// <summary>Отказ чтения описи: записи описи в архиве нет — это не наша копия.</summary>
    public static string ZipManifestEntryMissingFormat => Loc.T(nameof(ZipManifestEntryMissingFormat));

    /// <summary>Замечание: опись чужого вида — это не копия панели.</summary>
    public static string ZipManifestForeignKindFormat => Loc.T(nameof(ZipManifestForeignKindFormat));

    /// <summary>Находка: опись лежит под другим именем — оно названо.</summary>
    public static string ZipManifestFoundUnderFormat => Loc.T(nameof(ZipManifestFoundUnderFormat));

    /// <summary>Отказ чтения описи: ошибка JSON — названа строка.</summary>
    public static string ZipManifestJsonErrorFormat => Loc.T(nameof(ZipManifestJsonErrorFormat));

    /// <summary>Состояние описи: её нет.</summary>
    public static string ZipManifestMissing => Loc.T(nameof(ZipManifestMissing));

    /// <summary>Отказ чтения описи: не читается — с типом ошибки.</summary>
    public static string ZipManifestNotReadableFormat => Loc.T(nameof(ZipManifestNotReadableFormat));

    /// <summary>Отказ: опись не легла в архив — тип ошибки и её слова.</summary>
    public static string ZipManifestNotWrittenFormat => Loc.T(nameof(ZipManifestNotWrittenFormat));

    /// <summary>Отказ чтения описи: путь к архиву не задан.</summary>
    public static string ZipManifestPathNotSet => Loc.T(nameof(ZipManifestPathNotSet));

    /// <summary>Состояние описи: прочитана.</summary>
    public static string ZipManifestRead => Loc.T(nameof(ZipManifestRead));

    /// <summary>Состояние описи: не разбирается.</summary>
    public static string ZipManifestUnreadable => Loc.T(nameof(ZipManifestUnreadable));

    /// <summary>Замечание: версия описи новее той, что понимает панель.</summary>
    public static string ZipManifestVersionFormat => Loc.T(nameof(ZipManifestVersionFormat));

    /// <summary>Строка отчёта: архив не создан — с причиной.</summary>
    public static string ZipNotCreatedFormat => Loc.T(nameof(ZipNotCreatedFormat));

    /// <summary>Строка отчёта: архив не читается — с причиной.</summary>
    public static string ZipNotReadableFormat => Loc.T(nameof(ZipNotReadableFormat));

    /// <summary>Строка отчёта: архив читается — число файлов и состояние описи.</summary>
    public static string ZipReadSummaryFormat => Loc.T(nameof(ZipReadSummaryFormat));

    /// <summary>Подстановка причины, когда её нет.</summary>
    public static string ZipReasonUnnamed => Loc.T(nameof(ZipReasonUnnamed));

    /// <summary>Замечание: 7-Zip не справился, переходим на встроенный писатель.</summary>
    public static string ZipSevenZipFailedFormat => Loc.T(nameof(ZipSevenZipFailedFormat));

    /// <summary>Строка отчёта: копия снята — объём, число файлов, время и чем снята (встроенный писатель).</summary>
    public static string ZipSummaryDoneFormat => Loc.T(nameof(ZipSummaryDoneFormat));

    /// <summary>Дописано к отчёту: сколько записей с выходом за каталог.</summary>
    public static string ZipUnsafeEntriesFormat => Loc.T(nameof(ZipUnsafeEntriesFormat));

    // --- накат: строки движка Restore\RestoreEngine.cs (перенос на словари, этап 5) -----------
    //
    // Строки, которые человек читает в ПЛАНЕ наката, в его ИТОГЕ и в отказах ссылок — то есть
    // в окне копий и в режиме наката без окна (`--restore`). Перенесены ДОСЛОВНО: на этих текстах
    // стоят проверки (`tests\DshPanel.Tests\RestoreTests.cs`, `BackupJunctionTests.cs`,
    // `BackupKeyTests.cs`, `RestoreEmptyMachineTests.cs`), и пересказ сломал бы не перевод,
    // а доказательство.
    //
    // Имя говорит, ЧТО в строке: оно и есть то, что человек увидит при пропаже перевода
    // (`docs\LOCALIZATION.md` §8), а «Rest01» ему не скажет ничего. Строка с подстановками
    // оканчивается на `Format`.
    //
    // ⚠️ Разделители (« · », «; », отступ в два пробела) ключами НЕ стали: букв в них нет,
    // и переводить их нечего — так же сделано в `Backup\ZipReader.cs` и `Backup\BackupPlan.cs`.
    //
    // ⚠️ Итог наката и строка окна — РАЗНЫЕ строки, и это не разнобой: движок говорит
    // «накат сделан / накат НЕПОЛНЫЙ / накат не сделан» (с числами и причиной), а окно добавляет
    // к этому «Копия разложена…» (`RestoreDoneFormat`, `RestoreIncompleteFormat`,
    // `RestoreFailedFormat`). Путать их нельзя: первое — отчёт движка, второе — подпись окна.

    /// <summary>Строка группы в плане и в отчёте о раскладке: имя группы, цель под маской, число файлов. Одна на две точки — человек видит одно и то же до наката и после.</summary>
    public static string RestoreGroupLandsFormat => Loc.T(nameof(RestoreGroupLandsFormat));

    /// <summary>Группа, которая НЕ раскладывается, и причина словами. Пустая цель — решение человека, а не ошибка: молчание о ней человек прочитал бы как потерю данных.</summary>
    public static string RestoreGroupNotLaidOutFormat => Loc.T(nameof(RestoreGroupNotLaidOutFormat));

    /// <summary>План не собрался: причина названа прямо, потому что «накат невозможен» без «почему» — отписка.</summary>
    public static string RestorePlanImpossibleFormat => Loc.T(nameof(RestorePlanImpossibleFormat));

    /// <summary>План собрался: сколько файлов, в скольких группах и что за группы. Это ответ на «что именно я собираюсь наложить».</summary>
    public static string RestorePlanReadyFormat => Loc.T(nameof(RestorePlanReadyFormat));

    /// <summary>Дописано к плану, когда в архиве есть записи с выходом за каталог: пропуск обязан быть виден, а не выглядеть тихой потерей.</summary>
    public static string RestorePlanUnsafeSkippedFormat => Loc.T(nameof(RestorePlanUnsafeSkippedFormat));

    // --- таблица «что вернём» (п. 32 `docs\DESIGN.md`) ----------------------
    //
    // Слова владельца: *«Что вернём, копия? Текст очень неструктурированный, просто сплошной,
    // и читать невозможно»*. Приём тот же, что у таблицы копий (п. 29): группы и строки, а не
    // абзац. Подписи колонок называют то, что человек и спрашивает: что за группа, куда ляжет,
    // сколько в ней файлов и что с ней будет.

    /// <summary>Заголовок колонки: имя группы в архиве.</summary>
    public static string RestoreTableGroup => Loc.T(nameof(RestoreTableGroup));

    /// <summary>Заголовок колонки: куда ляжет (путь показывается через маску).</summary>
    public static string RestoreTableTarget => Loc.T(nameof(RestoreTableTarget));

    /// <summary>Заголовок колонки: сколько файлов вернётся — число из описи архива.</summary>
    public static string RestoreTableFiles => Loc.T(nameof(RestoreTableFiles));

    /// <summary>Заголовок колонки: что с группой — вернётся или почему нет.</summary>
    public static string RestoreTableState => Loc.T(nameof(RestoreTableState));

    /// <summary>Состояние группы: она раскладывается.</summary>
    public static string RestoreTableLands => Loc.T(nameof(RestoreTableLands));

    /// <summary>Состояние группы, которая не раскладывается и причины не назвала: молчащая клетка читалась бы как «всё в порядке».</summary>
    public static string RestoreTableNotLands => Loc.T(nameof(RestoreTableNotLands));

    /// <summary>
    /// Итоговая строка под таблицей: сколько файлов и в скольких группах вернётся. Короткая —
    /// подробности стоят в строках.
    ///
    /// ⚠️ Число файлов — только по ВЫБРАННЫМ группам («к восстановлению»), а не по всему архиву:
    /// иначе счётчик спорил бы со строками таблицы, где у группы стоит «не вернётся».
    /// </summary>
    public static string RestoreTableSummaryFormat => Loc.T(nameof(RestoreTableSummaryFormat));

    /// <summary>Итог наката, которому показывать больше нечего: не легло ничего. Коротко — но с причиной, а не просто «не сделано».</summary>
    public static string RestoreNotDoneFormat => Loc.T(nameof(RestoreNotDoneFormat));

    /// <summary>Итог удачного наката: число файлов, объём и время. Слово «сделан» здесь не украшение — им человек отличается от «НЕПОЛНЫЙ».</summary>
    public static string RestoreDoneSummaryFormat => Loc.T(nameof(RestoreDoneSummaryFormat));

    /// <summary>Итог НЕПОЛНОГО наката (находка В6): данные легли, но не все, и причина названа. «Не сделано» здесь — ложь, а «сделано» — тоже.</summary>
    public static string RestoreIncompleteSummaryFormat => Loc.T(nameof(RestoreIncompleteSummaryFormat));

    /// <summary>Дописано к итогу, когда ссылки созданы: число нужно, потому что без ссылок часть профиля движка не заработает.</summary>
    public static string RestoreLinksCreatedFormat => Loc.T(nameof(RestoreLinksCreatedFormat));

    /// <summary>Дописано к итогу: куда легла предохранительная копия. Это путь назад, и человек должен знать, где он лежит.</summary>
    public static string RestoreSafetyCopyPathFormat => Loc.T(nameof(RestoreSafetyCopyPathFormat));

    /// <summary>Дописано к итогу: что пропущено — одним перечислением через точку с запятой.</summary>
    public static string RestoreSkippedFormat => Loc.T(nameof(RestoreSkippedFormat));

    /// <summary>Отказ: накат без явного пути к архиву не делается — иначе панель угадывала бы, что раскладывать.</summary>
    public static string RestoreArchivePathNotSet => Loc.T(nameof(RestoreArchivePathNotSet));

    /// <summary>Отказ: «архив без нашей описи», и подстановкой идёт то, чего именно не хватает (слова уже сказаны чтением архива).</summary>
    public static string RestoreManifestMissingFormat => Loc.T(nameof(RestoreManifestMissingFormat));

    /// <summary>Отказ: опись чужого вида — это не копия панели. Вид назван: по нему человек поймёт, что за архив ему попался.</summary>
    public static string RestoreForeignKindFormat => Loc.T(nameof(RestoreForeignKindFormat));

    /// <summary>Отказ: опись новее понимаемой. Названы обе версии и что делать: молчаливая раскладка такого архива разошлась бы с замыслом.</summary>
    public static string RestoreManifestNewerFormat => Loc.T(nameof(RestoreManifestNewerFormat));

    /// <summary>Замечание группе: в описи не записан путь источника, значит раскладывать её не по чему. Это названная причина, а не пустая группа.</summary>
    public static string RestoreSourcePathNotRecorded => Loc.T(nameof(RestoreSourcePathNotRecorded));

    /// <summary>Отказ: в описи нет ни одной группы — раскладывать нечего.</summary>
    public static string RestoreNoGroups => Loc.T(nameof(RestoreNoGroups));

    /// <summary>Отказ: записи архива не попали ни в одну группу описи — так выглядит копия ПРЕЖНЕЙ панели (у v1 имена понятий не совпадали с именами папок). Разложить её «по догадке» значит потерять данные.</summary>
    public static string RestoreOldPanelArchive => Loc.T(nameof(RestoreOldPanelArchive));

    /// <summary>Замечание плана: сколько в архиве записей с выходом за каталог. Они не будут разложены, и об этом говорят ДО наката.</summary>
    public static string RestoreUnsafeEntriesFormat => Loc.T(nameof(RestoreUnsafeEntriesFormat));

    /// <summary>Замечание плана: сколько в описи ссылок-джункций. Архив их содержимым не несёт — они создаются заново после раскладки.</summary>
    public static string RestoreLinksInManifestFormat => Loc.T(nameof(RestoreLinksInManifestFormat));

    /// <summary>Замечание плана: движок и Node в копии есть, но по умолчанию не возвращаются, и причина сказана — подмена работающего движка копией ломает то, что уже работает.</summary>
    public static string RestoreEngineNotReturned => Loc.T(nameof(RestoreEngineNotReturned));

    /// <summary>Замечание плана: ключи в копии есть, а согласия на них нет. Подстановкой идёт само правило (<c>RestoreKeysNeedConsent</c>): одно правило — один текст, а не два похожих.</summary>
    public static string RestoreKeysNotReturnedFormat => Loc.T(nameof(RestoreKeysNotReturnedFormat));

    /// <summary>Замечание плана про файл ключей движка: пустой файл ключа роняет движок целиком, поэтому такой файл не возвращается.</summary>
    public static string RestoreCredentialsFileNote => Loc.T(nameof(RestoreCredentialsFileNote));

    /// <summary>Причина отказа плана: ни одна группа не выбрана к раскладке. Это не «план не собрался», а решение человека — и названо оно отдельно.</summary>
    public static string RestoreNoGroupSelected => Loc.T(nameof(RestoreNoGroupSelected));

    /// <summary>Отказ наката: он не начат, потому что не собрался план. Причина плана идёт подстановкой, а не пересказом.</summary>
    public static string RestoreNotStartedFormat => Loc.T(nameof(RestoreNotStartedFormat));

    /// <summary>Замечание наката на работающем сервере: живой движок держит файлы сессий и дописывает их. Допущение «на ходу» называется словами, а не подразумевается.</summary>
    public static string RestoreServerRunningNote => Loc.T(nameof(RestoreServerRunningNote));

    /// <summary>Строка хода работы: снимается предохранительная копия. Человек видит, что панель не молчит перед первой записью.</summary>
    public static string RestoreProgressSafetyCopy => Loc.T(nameof(RestoreProgressSafetyCopy));

    /// <summary>Замечание наката на пустой машине (дефект Д1): предохранительная копия не нужна, потому что терять нечего. Слова про пустой план идут подстановкой — они одни на всю панель (<c>BackupPlan.NothingToLoseText</c>).</summary>
    public static string RestoreSafetyCopyNotNeededFormat => Loc.T(nameof(RestoreSafetyCopyNotNeededFormat));

    /// <summary>Отказ: предохранительную копию снять не удалось, и накат без пути назад не начинается вовсе. Причина копии — подстановкой.</summary>
    public static string RestoreSafetyCopyFailedFormat => Loc.T(nameof(RestoreSafetyCopyFailedFormat));

    /// <summary>
    /// Отказ наката, когда ссылки не легли: число из общего, а причина — в строке пропущенных.
    ///
    /// ⚠️ Это НЕ <c>RestoreLinksNotCreatedFormat</c> («ссылок не легло»): там ЗАМЕЧАНИЕ, здесь
    /// ПРИЧИНА ОТКАЗА, и на разные формулировки стоят разные проверки
    /// (`BackupJunctionTests.Ссылки_не_легли_накат_неполный_и_данные_на_месте` считает вхождения
    /// «создать не удалось»). Слить их значило бы сломать проверку, а не убрать повтор.
    /// </summary>
    public static string RestoreLinksCreateFailedFormat => Loc.T(nameof(RestoreLinksCreateFailedFormat));

    /// <summary>Строка пропущенных: сколько записей с выходом за каталог не разложено.</summary>
    public static string RestoreSkippedUnsafeEntriesFormat => Loc.T(nameof(RestoreSkippedUnsafeEntriesFormat));

    /// <summary>Строка пропущенных: сколько записей шло сквозь junction-ссылку — такая запись переписала бы ГЛОБАЛЬНЫЙ движок вместо копии.</summary>
    public static string RestoreSkippedThroughLinkFormat => Loc.T(nameof(RestoreSkippedThroughLinkFormat));

    /// <summary>Строка пропущенных: файл ключа, который не хранит ни одного ключа, — на нём движок падает целиком, и сервер не поднимается вовсе.</summary>
    public static string RestoreSkippedEmptyCredentials => Loc.T(nameof(RestoreSkippedEmptyCredentials));

    /// <summary>Строка пропущенных: записи БЕЗ ГРУППЫ в описи — настоящий сигнал (вида у них нет вовсе). Отличается от невыбранных групп намеренно (дефект Д5).</summary>
    public static string RestoreUnmatchedEntriesFormat => Loc.T(nameof(RestoreUnmatchedEntriesFormat));

    /// <summary>Пример записи без группы. Не больше двух имён — иначе настоящий сигнал тонет в списке.</summary>
    public static string RestoreUnmatchedEntryFormat => Loc.T(nameof(RestoreUnmatchedEntryFormat));

    /// <summary>Строка пропущенных: записи НЕВЫБРАННЫХ групп. Группа в описи есть, просто человек её не выбрал, — и это не потеря (дефект Д5).</summary>
    public static string RestoreUnselectedEntriesFormat => Loc.T(nameof(RestoreUnselectedEntriesFormat));

    /// <summary>Пример записи невыбранной группы — как и у записей без группы, не больше двух имён.</summary>
    public static string RestoreUnselectedEntryFormat => Loc.T(nameof(RestoreUnselectedEntryFormat));

    /// <summary>Строка пропущенных: сколько файлов было занято. «Легло всё» и «легло почти всё» — разные вещи, и молчать об этом нельзя.</summary>
    public static string RestoreLockedFilesFormat => Loc.T(nameof(RestoreLockedFilesFormat));

    /// <summary>Отказ: ни один файл не лёг на место. Показывать больше нечего, поэтому строка короткая — в отличие от неполного наката.</summary>
    public static string RestoreNothingLanded => Loc.T(nameof(RestoreNothingLanded));

    /// <summary>Причина отказа наката: занятые файлы. Данные при этом легли не целиком, и это сказано прямо, а не умолчано.</summary>
    public static string RestoreLockedFilesFailedFormat => Loc.T(nameof(RestoreLockedFilesFailedFormat));

    /// <summary>Причина, по которой группа панели не раскладывается: настройки и состояние возвращаются только по отдельному согласию, и это правило v1.</summary>
    public static string RestorePanelNeedsConsent => Loc.T(nameof(RestorePanelNeedsConsent));

    /// <summary>Причина: в описи нет пути рабочей папки — группу проектов раскладывать не по чему.</summary>
    public static string RestoreWorkingFolderPathEmpty => Loc.T(nameof(RestoreWorkingFolderPathEmpty));

    /// <summary>Находка В3: копия снята с другой рабочей папкой, чем нынешняя. Названы оба пути и то, что поправить в настройках: сессии движка привязаны к пути.</summary>
    public static string RestoreWorkingFolderDiffersFormat => Loc.T(nameof(RestoreWorkingFolderDiffersFormat));

    /// <summary>Причина, по которой группа движка не раскладывается: движок и Node возвращаются только по отдельному согласию.</summary>
    public static string RestoreEngineNeedsConsent => Loc.T(nameof(RestoreEngineNeedsConsent));

    /// <summary>Причина: в описи нет пути движка — раскладывать не по чему.</summary>
    public static string RestoreEnginePathEmpty => Loc.T(nameof(RestoreEnginePathEmpty));

    /// <summary>Ответ-причина: движка на этой машине нет, значит раскладываем по записанному в описи пути.</summary>
    public static string RestoreEngineAbsentUsesRecordedPath => Loc.T(nameof(RestoreEngineAbsentUsesRecordedPath));

    /// <summary>Ответ-причина: движок на этой машине уже есть, значит раскладываем по ЕГО месту, а не по чужому пути из описи.</summary>
    public static string RestoreEnginePresentUsesOwnPath => Loc.T(nameof(RestoreEnginePresentUsesOwnPath));

    /// <summary>Причина: вид группы в описи не записан (опись писала не эта панель), и пути тоже нет — раскладывать не по чему.</summary>
    public static string RestoreKindUnrecordedPathEmpty => Loc.T(nameof(RestoreKindUnrecordedPathEmpty));

    /// <summary>Ответ-причина: вид группы не записан, поэтому раскладываем по записанному пути с заменой чужого профиля на свой.</summary>
    public static string RestoreKindUnrecordedUsesRecordedPath => Loc.T(nameof(RestoreKindUnrecordedUsesRecordedPath));

    /// <summary>Пропуск ссылки: её путь выходит за свою группу. Такая ссылка завела бы файлы наружу, и об этом говорится словами.</summary>
    public static string RestoreLinkOutsideRootFormat => Loc.T(nameof(RestoreLinkOutsideRootFormat));

    /// <summary>Пропуск ссылки: цели на этой машине нет. Мёртвая ссылка хуже отсутствующей, поэтому она не создаётся — и причина названа вместе с путём под маской.</summary>
    public static string RestoreLinkTargetMissingFormat => Loc.T(nameof(RestoreLinkTargetMissingFormat));

    /// <summary>Пропуск ссылки: на её месте непустой каталог. Данные важнее красоты — каталог не трогаем.</summary>
    public static string RestoreLinkFolderNotEmptyFormat => Loc.T(nameof(RestoreLinkFolderNotEmptyFormat));

    /// <summary>Строка хода работы: создаётся ссылка, названо её имя внутри группы.</summary>
    public static string RestoreProgressLinkFormat => Loc.T(nameof(RestoreProgressLinkFormat));

    /// <summary>Причина отказа ссылки: <c>mklink</c> не ответил за 15 секунд — процесс снят. Это ответ ПАНЕЛИ, а не Windows, и он тоже обязан быть произносимым.</summary>
    public static string JunctionMklinkTimeout => Loc.T(nameof(JunctionMklinkTimeout));

    /// <summary>
    /// Замечание плана: копия снята на другой машине или другим пользователем.
    /// Имена машины и человека здесь НЕ называются (красная линия 7) — назван только факт,
    /// по которому видно, почему путям из описи верить нельзя.
    /// </summary>
    public static string RestoreOtherMachineNote => Loc.T(nameof(RestoreOtherMachineNote));

    // --- подсказки кнопок (ToolTip) -----------------------------------------
    //
    // Решение владельца 27.09.2026, шаг 1 «живости вида»: подсказка обязана быть у КАЖДОЙ кнопки
    // панели, и она говорит две вещи — что произойдёт по нажатию и, где кнопка бывает недоступна,
    // ПОЧЕМУ она недоступна. Второе — не украшение: недоступная кнопка без причины читается
    // как поломка панели, и человек ищет дефект там, где его нет.
    //
    // ⚠️ Почему подсказок нет у пунктов меню значка: это меню рисует Windows (`TrackPopupMenu`),
    // и нативных подсказок у его пунктов не бывает вовсе. Подсказка есть у САМОГО значка
    // (`TrayToolTipFormat`) — она показывает агента, баланс и тариф.
    //
    // ⚠️ Строки, которые уже сказаны человеку рядом с кнопкой (например `AdoptHint` или
    // `OpenAgentReadyHint`), здесь НЕ повторяются: подсказка берёт ту же строку. Двойник
    // разошёлся бы с ней на первом же изменении.

    /// <summary>Кнопка «О программе…»: что она откроет.</summary>
    public static string TipAboutButton => Loc.T(nameof(TipAboutButton));

    /// <summary>Кнопка «Запустить»: что произойдёт.</summary>
    public static string TipStartServerButton => Loc.T(nameof(TipStartServerButton));

    /// <summary>Почему «Запустить» недоступна: сервер уже отвечает.</summary>
    public static string TipStartServerRunning => Loc.T(nameof(TipStartServerRunning));

    /// <summary>Почему «Запустить» недоступна: порт занят чужой программой.</summary>
    public static string TipStartServerPortBusy => Loc.T(nameof(TipStartServerPortBusy));

    /// <summary>Почему кнопка сервера недоступна: идёт запуск, остановка или встраивание.</summary>
    public static string TipServerBusy => Loc.T(nameof(TipServerBusy));

    /// <summary>Почему кнопки сервера недоступны: окно построено без связи с сервером.</summary>
    public static string TipServerUnbound => Loc.T(nameof(TipServerUnbound));

    /// <summary>Почему «Остановить» недоступна: панель этим сервером не управляет.</summary>
    public static string TipStopServerNotMine => Loc.T(nameof(TipStopServerNotMine));

    /// <summary>Кнопка «Остановить»: что произойдёт.</summary>
    public static string TipStopServerButton => Loc.T(nameof(TipStopServerButton));

    /// <summary>Кнопка «Остановить» у найденного сервера: сначала будет вопрос.</summary>
    public static string TipStopServerAsks => Loc.T(nameof(TipStopServerAsks));

    /// <summary>Почему «Остановить» недоступна: сервер не работает.</summary>
    public static string TipStopServerNothing => Loc.T(nameof(TipStopServerNothing));

    /// <summary>Почему «Остановить» недоступна: на порту чужая программа.</summary>
    public static string TipStopServerForeign => Loc.T(nameof(TipStopServerForeign));

    /// <summary>Кнопка «Отвязаться»: что произойдёт и что будет с сервером.</summary>
    public static string TipDetachServerButton => Loc.T(nameof(TipDetachServerButton));

    /// <summary>Кнопка «Настройки…» в шапке главного окна.</summary>
    public static string TipSettingsButton => Loc.T(nameof(TipSettingsButton));

    /// <summary>Кнопка «Копии…» в шапке главного окна.</summary>
    public static string TipBackupsButton => Loc.T(nameof(TipBackupsButton));

    /// <summary>Кнопка обновления баланса: что произойдёт.</summary>
    public static string TipRefreshBalanceButton => Loc.T(nameof(TipRefreshBalanceButton));

    /// <summary>Почему обновление баланса недоступно: прогон проверки.</summary>
    public static string TipRefreshBalanceLocked => Loc.T(nameof(TipRefreshBalanceLocked));

    /// <summary>Почему обновление баланса недоступно: запрос уже идёт.</summary>
    public static string TipRefreshBalanceBusy => Loc.T(nameof(TipRefreshBalanceBusy));

    /// <summary>Кнопка-предложение рабочей папки в настройках.</summary>
    public static string TipSuggestWorkDirButton => Loc.T(nameof(TipSuggestWorkDirButton));

    /// <summary>Кнопка «Проверить окружение» в настройках.</summary>
    public static string TipCheckEnvironmentButton => Loc.T(nameof(TipCheckEnvironmentButton));

    /// <summary>Кнопка «Сохранить» в настройках и в вопросе о несохранённой правке.</summary>
    public static string TipSaveSettingsButton => Loc.T(nameof(TipSaveSettingsButton));

    /// <summary>Кнопка «Создать копию» в окне копий.</summary>
    public static string TipCreateBackupButton => Loc.T(nameof(TipCreateBackupButton));

    /// <summary>Кнопка обновления списка копий.</summary>
    public static string TipRefreshBackupsButton => Loc.T(nameof(TipRefreshBackupsButton));

    /// <summary>Кнопка-предложение найденного каталога ключей.</summary>
    public static string TipAddKeyDirButton => Loc.T(nameof(TipAddKeyDirButton));

    /// <summary>Кнопка сохранения списка лишних имён своего фильтра.</summary>
    public static string TipSaveExtrasButton => Loc.T(nameof(TipSaveExtrasButton));

    /// <summary>Кнопка «Восстановить» в плане наката.</summary>
    public static string TipRunRestoreButton => Loc.T(nameof(TipRunRestoreButton));

    /// <summary>Кнопка «Отмена» в плане наката: убрать план и ничего не менять.</summary>
    public static string TipCancelRestoreButton => Loc.T(nameof(TipCancelRestoreButton));

    /// <summary>Почему кнопка копий недоступна: идёт копия или накат.</summary>
    public static string TipBackupBusy => Loc.T(nameof(TipBackupBusy));

    /// <summary>Почему кнопка копий недоступна: этот прогон копии не снимает.</summary>
    public static string TipBackupNotAllowed => Loc.T(nameof(TipBackupNotAllowed));

    /// <summary>Почему состав копии недоступен: в этом прогоне он не меняется.</summary>
    public static string TipBackupCompositionLocked => Loc.T(nameof(TipBackupCompositionLocked));

    /// <summary>Кнопка «Закрыть» в окне «О программе».</summary>
    public static string TipCloseAboutButton => Loc.T(nameof(TipCloseAboutButton));

    /// <summary>Ссылка на страницу проекта в окне «О программе».</summary>
    public static string TipRepoLinkButton => Loc.T(nameof(TipRepoLinkButton));

    /// <summary>Ссылка на поддержку проекта в окне «О программе».</summary>
    public static string TipDonateLinkButton => Loc.T(nameof(TipDonateLinkButton));

    /// <summary>Кнопка подтверждения остановки встроенного сервера.</summary>
    public static string TipConfirmStopYesButton => Loc.T(nameof(TipConfirmStopYesButton));

    /// <summary>Кнопка отказа в окне подтверждения остановки.</summary>
    public static string TipConfirmStopNoButton => Loc.T(nameof(TipConfirmStopNoButton));

    /// <summary>Кнопка «Выйти без сохранения» в вопросе о несохранённой правке.</summary>
    public static string TipUnsavedDiscardButton => Loc.T(nameof(TipUnsavedDiscardButton));

    /// <summary>Кнопка «Отмена» в вопросе о несохранённой правке.</summary>
    public static string TipUnsavedCancelButton => Loc.T(nameof(TipUnsavedCancelButton));

    /// <summary>Кнопка «Отмена» в вопросе о гашении сервера перед копией.</summary>
    public static string TipBackupStopCancelButton => Loc.T(nameof(TipBackupStopCancelButton));

    /// <summary>Кнопка «Продолжить на ходу»: копия без остановки сервера.</summary>
    public static string TipBackupStopLiveButton => Loc.T(nameof(TipBackupStopLiveButton));

    /// <summary>Кнопка «Погасить и продолжить»: целостная копия.</summary>
    public static string TipBackupStopStopButton => Loc.T(nameof(TipBackupStopStopButton));

    // --- перезапуск сервера (запрос владельца 27.09.2026) -------------------
    //
    // Кнопка встаёт МЕЖДУ «Запустить» и «Остановить»: перезапуск — это ровно те два действия
    // подряд, и стоять он обязан там, где человек их и ищет. Для найденного (взятого под
    // управление) сервера кнопка спрашивает ТО ЖЕ подтверждение, что «Остановить»: гасится
    // чужой процесс, через который может идти текущая работа человека.

    /// <summary>Кнопка перезапуска: погасить и поднять заново.</summary>
    public static string RestartButton => Loc.T(nameof(RestartButton));

    /// <summary>
    /// Кнопка перезапуска у НАЙДЕННОГО сервера: многоточие честно говорит, что кнопка сначала
    /// спросит подтверждение (то же правило, что у «Остановить»).
    /// </summary>
    public static string RestartButtonAsks => Loc.T(nameof(RestartButtonAsks));

    /// <summary>Состояние на время перезапуска: панель занята, кнопки сервера заблокированы.</summary>
    public static string ServerRestarting => Loc.T(nameof(ServerRestarting));

    /// <summary>Подсказка «Перезапустить»: что произойдёт по нажатию.</summary>
    public static string TipRestartServerButton => Loc.T(nameof(TipRestartServerButton));

    /// <summary>Подсказка «Перезапустить» у найденного сервера: сначала будет вопрос.</summary>
    public static string TipRestartServerAsks => Loc.T(nameof(TipRestartServerAsks));

    /// <summary>Почему «Перезапустить» недоступна: на порту чужая программа.</summary>
    public static string TipRestartServerForeign => Loc.T(nameof(TipRestartServerForeign));

    /// <summary>Почему «Перезапустить» недоступна: панель этим сервером не управляет.</summary>
    public static string TipRestartServerNotMine => Loc.T(nameof(TipRestartServerNotMine));

    /// <summary>Почему «Перезапустить» недоступна: сервер не работает.</summary>
    public static string TipRestartServerNothing => Loc.T(nameof(TipRestartServerNothing));

    // --- пики и тарифы: короткая строка в главном окне и ОТДЕЛЬНОЕ окно -----
    //
    // Слова владельца 27.09.2026: «Там где написано „окна пика“, проверено и дата с временем
    // нужно… остальная информация не нужна в принципе». Поэтому в главном окне остаётся одна
    // строка — когда окна проверены, — а всё остальное (по дням недели, цены, переключение,
    // откуда взяты окна) живёт в своём окне, куда ведёт кнопка рядом с «Обновить баланс».

    /// <summary>Короткая строка главного окна: «Пики: проверено 24.09.2026, 19:40».</summary>
    public static string PeakCheckedFormat => Loc.T(nameof(PeakCheckedFormat));

    /// <summary>Кнопка-дверь в окно «Пики и тарифы».</summary>
    public static string PeakButton => Loc.T(nameof(PeakButton));

    /// <summary>Подсказка двери: что человек увидит в окне.</summary>
    public static string TipPeakButton => Loc.T(nameof(TipPeakButton));

    /// <summary>
    /// Кнопка окна «Пики и тарифы»: разобрать страницу цен ЗАНОВО, вручную.
    ///
    /// Зачем отдельная дверь. Автоматический разбор идёт при запуске панели и не чаще раза
    /// в сутки (решение владельца 27.09.2026), а человеку иногда нужно «прямо сейчас»: он увидел
    /// на странице другую цену и хочет проверить. Кнопка и есть этот повод — и он же служит
    /// ПОДТВЕРЖДЕНИЕМ для окон пика со страницы (правило панели 1.x: окна — по подтверждению).
    /// </summary>
    public static string PeakRefreshButton => Loc.T(nameof(PeakRefreshButton));

    /// <summary>Подсказка кнопки: что она сделает.</summary>
    public static string TipPeakRefreshButton => Loc.T(nameof(TipPeakRefreshButton));

    /// <summary>Подсказка кнопки в прогоне проверки: сети у панели здесь нет.</summary>
    public static string TipPeakRefreshLocked => Loc.T(nameof(TipPeakRefreshLocked));

    /// <summary>Подсказка кнопки, пока разбор уже идёт.</summary>
    public static string TipPeakRefreshBusy => Loc.T(nameof(TipPeakRefreshBusy));

    /// <summary>Заголовок окна «Пики и тарифы».</summary>
    public static string PeakWindowTitle => Loc.T(nameof(PeakWindowTitle));

    /// <summary>Крупный заголовок внутри окна.</summary>
    public static string PeakWindowHeading => Loc.T(nameof(PeakWindowHeading));

    /// <summary>Серый подзаголовок: про что это окно.</summary>
    public static string PeakWindowSubtitle => Loc.T(nameof(PeakWindowSubtitle));

    /// <summary>Заголовок колонки таблицы: день недели.</summary>
    public static string PeakTableColumnDay => Loc.T(nameof(PeakTableColumnDay));

    /// <summary>
    /// Заголовок ПЕРВОЙ таблицы — «График пиков»: по дням недели, где дорогое время, а где дешёвое.
    ///
    /// ⚠️ Название таблицы — решение дирижёра, названное владельцу вместе с отчётом (замечание
    /// владельца 27.09.2026: «табличка пиков собрана неверно»). Таблиц теперь ДВЕ, и у каждой
    /// своё имя: «График пиков» отвечает на «когда дешевле», «Стоимость» — на «сколько стоит».
    /// Прежде одна таблица отвечала на оба вопроса сразу и не отвечала ни на один.
    /// </summary>
    public static string PeakTableTitle => Loc.T(nameof(PeakTableTitle));

    /// <summary>Заголовок колонки первой таблицы: окна ДОРОГОГО времени этого дня.</summary>
    public static string PeakTableColumnPeak => Loc.T(nameof(PeakTableColumnPeak));

    /// <summary>
    /// Заголовок колонки первой таблицы: окна ДЕШЁВОГО времени этого дня — обратная сторона пика.
    ///
    /// Отдельной колонкой, а не «всё прочее»: владелец смотрел панель живьём и жаловался, что
    /// «текст сливается в строку». Окна пика и окна дешёвого времени — два разных ответа,
    /// и человеку нужны оба: по первому он ждёт, по второму работает.
    /// </summary>
    public static string PeakTableColumnOffPeak => Loc.T(nameof(PeakTableColumnOffPeak));

    /// <summary>Ячейка колонки «Пик» у дня, в котором пика нет вовсе: «нет», а не пустое место.</summary>
    public static string PeakTableNoPeak => Loc.T(nameof(PeakTableNoPeak));

    /// <summary>
    /// Короткая строка ПОД таблицей графика: «сейчас: вне пика, до 04:00» — где человек находится
    /// прямо сейчас и до каких пор. Подстановки: состояние и местное время переключения.
    /// </summary>
    public static string PeakNowLineFormat => Loc.T(nameof(PeakNowLineFormat));

    /// <summary>
    /// Та же строка, когда переключения впереди нет вовсе: «сейчас: вне пика». Выдумать час было бы
    /// хуже, чем сказать «сейчас» без «до».
    /// </summary>
    public static string PeakNowLineNoTimeFormat => Loc.T(nameof(PeakNowLineNoTimeFormat));

    /// <summary>Короткое слово состояния для строки «сейчас»: «пик».</summary>
    public static string PeakShortInPeak => Loc.T(nameof(PeakShortInPeak));

    /// <summary>Короткое слово состояния для строки «сейчас»: «вне пика».</summary>
    public static string PeakShortOffPeak => Loc.T(nameof(PeakShortOffPeak));

    // --- ВТОРАЯ таблица: цена за единицу -------------------------------------
    //
    // Замечание владельца 27.09.2026: табличка пиков собрана неверно — в ней смешались расписание
    // и цена. Теперь цена живёт СВОЕЙ таблицей, а данные для неё даёт страница цен: ввод с
    // попаданием в кэш, ввод с промахом и вывод.

    /// <summary>Заголовок второй таблицы, когда модель со страницы ещё неизвестна: «Стоимость».</summary>
    public static string PriceTableTitle => Loc.T(nameof(PriceTableTitle));

    /// <summary>Тот же заголовок с именем модели: «Стоимость — deepseek-flash».</summary>
    public static string PriceTableTitleFormat => Loc.T(nameof(PriceTableTitleFormat));

    /// <summary>Заголовок колонки второй таблицы: за какую единицу напечатана цена.</summary>
    public static string PriceTableColumnItem => Loc.T(nameof(PriceTableColumnItem));

    /// <summary>Цены нет: страница не напечатала её для этого тарифа. Знак, а не пустое место.</summary>
    public static string PriceValueMissing => Loc.T(nameof(PriceValueMissing));

    /// <summary>Отказ разбора: страница пришла пустой.</summary>
    public static string PricePageEmpty => Loc.T(nameof(PricePageEmpty));

    /// <summary>Отказ разбора: на странице не нашлось таблицы с ценами.</summary>
    public static string PriceTableMissing => Loc.T(nameof(PriceTableMissing));

    /// <summary>Откуда взяты цены: адрес страницы и когда проверено.</summary>
    public static string PriceSourceFormat => Loc.T(nameof(PriceSourceFormat));

    /// <summary>Цен ещё не читали и причин не читать нет.</summary>
    public static string PriceNeverRead => Loc.T(nameof(PriceNeverRead));

    /// <summary>Цены не читались: это прогон проверки — в сеть панель не ходит.</summary>
    public static string PriceLocked => Loc.T(nameof(PriceLocked));

    /// <summary>Цены прочитать не удалось — с причиной.</summary>
    public static string PriceFailedFormat => Loc.T(nameof(PriceFailedFormat));

    /// <summary>Строка журнала об исходе разбора: сколько строк цены прочитано или почему нет.</summary>
    public static string PriceLogFormat => Loc.T(nameof(PriceLogFormat));

    /// <summary>Вторая половина строки журнала: сколько строк цены прочитано.</summary>
    public static string PriceLogRowsFormat => Loc.T(nameof(PriceLogRowsFormat));

    /// <summary>Строка журнала: часы разбора страницы цен включены.</summary>
    public static string PanelLogPricingClock => Loc.T(nameof(PanelLogPricingClock));

    /// <summary>Строка журнала: разбор страницы цен в этом прогоне не делается (нет права).</summary>
    public static string PanelLogPricingLocked => Loc.T(nameof(PanelLogPricingLocked));

    /// <summary>Строка журнала: окно «Пики и тарифы» открыто.</summary>
    public static string PeakOpenedLog => Loc.T(nameof(PeakOpenedLog));

    /// <summary>Строка журнала: окно «Пики и тарифы» уже открыто — вывели на передний план.</summary>
    public static string PeakRaisedLog => Loc.T(nameof(PeakRaisedLog));

    /// <summary>Строка журнала: просьба открыть пики, а окна у панели нет (так бывает у прогонов).</summary>
    public static string PanelLogPeaksUnavailable => Loc.T(nameof(PanelLogPeaksUnavailable));

    // --- окно настроек: вид «как в harness» (запрос владельца 27.09.2026) ---
    //
    // Вторичная кнопка справа сверху — дверь в каталог настроек панели, и ✕ рядом с ней.
    // ✕ закрывает окно ТЕМ ЖЕ путём, что крестик окна: несохранённую правку панель не теряет,
    // а спрашивает (п. 16 `docs\DESIGN.md`) — поэтому у кнопки своя строка журнала и своя
    // подсказка, а не подпись «Закрыть» от окна «О программе».

    /// <summary>Вторичная кнопка окна настроек: открыть каталог настроек панели.</summary>
    public static string SettingsOpenFolderButton => Loc.T(nameof(SettingsOpenFolderButton));

    /// <summary>Подсказка вторичной кнопки: что откроется.</summary>
    public static string TipSettingsOpenFolderButton => Loc.T(nameof(TipSettingsOpenFolderButton));

    /// <summary>Строка журнала: каталог настроек открыт по щелчку человека.</summary>
    public static string SettingsFolderOpenedLog => Loc.T(nameof(SettingsFolderOpenedLog));

    /// <summary>Строка журнала: каталог настроек не открываем — это прогон проверки.</summary>
    public static string SettingsFolderRefusedLog => Loc.T(nameof(SettingsFolderRefusedLog));

    /// <summary>Отказ: открывать нечего — каталог настроек неизвестен.</summary>
    public static string SettingsFolderMissing => Loc.T(nameof(SettingsFolderMissing));

    /// <summary>Отказ открыть каталог настроек — с причиной от оболочки.</summary>
    public static string SettingsFolderFailedFormat => Loc.T(nameof(SettingsFolderFailedFormat));

    /// <summary>Серый подзаголовок раздела «Общее»: что здесь настраивается.</summary>
    public static string SettingsGeneralSubtitle => Loc.T(nameof(SettingsGeneralSubtitle));

    /// <summary>Серый подзаголовок раздела «Резервное копирование».</summary>
    public static string BackupSectionSubtitle => Loc.T(nameof(BackupSectionSubtitle));

    /// <summary>Серый подзаголовок раздела «Баланс и тариф».</summary>
    public static string BalanceSectionSubtitle => Loc.T(nameof(BalanceSectionSubtitle));

    /// <summary>Серый подзаголовок раздела «Сервер».</summary>
    public static string ServerSectionSubtitle => Loc.T(nameof(ServerSectionSubtitle));

    // --- обновление панели: проверка выпусков и извещение (первый срез) -------
    //
    // ⚠️ Здесь ТОЛЬКО те строки, которые ядро действительно показывает. Пункт меню, шарик
    // и окно «Доступно обновление» собирает дирижёр — и свои подписи заводит тогда же:
    // мёртвая строка (объявлена, переведена и никем не вызвана) ловится проверкой
    // `tests\DshPanel.Tests\SourceStringsTests.cs`.
    //
    // Ни скачивания, ни замены файлов в строках нет намеренно: их делает второй срез.

    /// <summary>Состояние: выпуск новее этой панели — «Доступна версия {0}».</summary>
    public static string UpdateAvailableFormat => Loc.T(nameof(UpdateAvailableFormat));

    /// <summary>Состояние: новее ничего нет — «Последняя версия».</summary>
    public static string UpdateLatest => Loc.T(nameof(UpdateLatest));

    /// <summary>Состояние: человек сказал «пропустить эту версию» — и панель об этом говорит.</summary>
    public static string UpdateSkippedFormat => Loc.T(nameof(UpdateSkippedFormat));

    /// <summary>Когда проверяли в последний раз: «проверено 28.09.2026, 14:32».</summary>
    public static string UpdateCheckedFormat => Loc.T(nameof(UpdateCheckedFormat));

    /// <summary>Выпуски ещё не спрашивали и причин не спрашивать нет.</summary>
    public static string UpdateNeverChecked => Loc.T(nameof(UpdateNeverChecked));

    /// <summary>Проверка не делалась: это прогон проверки — в сеть панель не ходит.</summary>
    public static string UpdateLocked => Loc.T(nameof(UpdateLocked));

    /// <summary>Проверить не удалось — с причиной.</summary>
    public static string UpdateFailedFormat => Loc.T(nameof(UpdateFailedFormat));

    /// <summary>Отказ GitHub по числу запросов (403/429) — это лимит, а не поломка.</summary>
    public static string UpdateRateLimited => Loc.T(nameof(UpdateRateLimited));

    /// <summary>Отказ GitHub: выпусков у репозитория ещё нет.</summary>
    public static string UpdateNoRelease => Loc.T(nameof(UpdateNoRelease));

    /// <summary>Отказ GitHub: ответ пришёл, но тега выпуска в нём нет.</summary>
    public static string UpdateNoTag => Loc.T(nameof(UpdateNoTag));

    /// <summary>Ответ GitHub не разобрался как JSON.</summary>
    public static string UpdateBadJson => Loc.T(nameof(UpdateBadJson));

    /// <summary>GitHub ответил отказом, который не назван выше, — с кодом ответа.</summary>
    public static string UpdateHttpFailedFormat => Loc.T(nameof(UpdateHttpFailedFormat));

    /// <summary>Строка журнала об исходе проверки: какая версия увиделась или почему её нет.</summary>
    public static string UpdateLogFormat => Loc.T(nameof(UpdateLogFormat));

    /// <summary>Строка журнала: часы проверки выпусков включены.</summary>
    public static string PanelLogUpdateClock => Loc.T(nameof(PanelLogUpdateClock));

    /// <summary>Строка журнала: проверка выпусков в этом прогоне не делается (нет права).</summary>
    public static string PanelLogUpdateLocked => Loc.T(nameof(PanelLogUpdateLocked));

    // --- обновление панели: ВИДИМАЯ часть — меню, строка состояния, окно (третий шаг) ---
    //
    // ⚠️ Здесь ТОЛЬКО то, что панель действительно показывает. Каждая строка обязана иметь
    // вызов в продукте: объявленная, переведённая и никем не вызванная строка — мёртвая,
    // и её ловит `tests\DshPanel.Tests\SourceStringsTests.cs`.
    //
    // ⚠️ Имена ключей отказа, итога и этапов — НЕ выдуманы здесь: их отдаёт ядро
    // (`UpdateRefusals.Key`, `UpdateOutcomes.ReasonKey`), а `Update\UpdatePhrases.cs` переводит
    // ключ в строку. Сторожит это `UpdatePhrasesTests`: «у каждого значения ядра есть строка
    // в словаре» — проверка, которая обязана уметь падать.

    /// <summary>Пункт меню значка: проверить выпуски сразу, не дожидаясь суточного гейта.</summary>
    public static string CheckUpdateMenuText => Loc.T(nameof(CheckUpdateMenuText));

    /// <summary>Пункт меню значка и кнопка главного окна: открыть окно «Обновление панели».</summary>
    public static string UpdateWindowMenuText => Loc.T(nameof(UpdateWindowMenuText));

    /// <summary>
    /// Строка состояния обновления, когда всё в порядке: «Обновление: проверено 28.09.2026, 14:32».
    /// Отдельно от <see cref="UpdateCheckedFormat"/>: в меню значка строка читается сама по себе,
    /// и без слова «Обновление» она выглядела бы датой без хозяина.
    /// </summary>
    public static string UpdateTrayCheckedFormat => Loc.T(nameof(UpdateTrayCheckedFormat));

    /// <summary>Подсказка двери в окно обновления (кнопка главного окна).</summary>

    /// <summary>Заголовок окна «Обновление панели».</summary>
    public static string UpdateWindowTitle => Loc.T(nameof(UpdateWindowTitle));

    /// <summary>Крупная шапка окна.</summary>
    public static string UpdateWindowHeading => Loc.T(nameof(UpdateWindowHeading));

    /// <summary>Серый подзаголовок окна: что здесь видно.</summary>
    public static string UpdateWindowSubtitle => Loc.T(nameof(UpdateWindowSubtitle));

    /// <summary>Кнопка «Проверить сейчас» — щелчок человека, мимо суточного гейта.</summary>
    public static string UpdateCheckNowButton => Loc.T(nameof(UpdateCheckNowButton));

    /// <summary>Подсказка кнопки «Проверить сейчас».</summary>
    public static string TipUpdateCheckNowButton => Loc.T(nameof(TipUpdateCheckNowButton));

    /// <summary>Что стоит сейчас: «Установлено: 2.0.0».</summary>
    public static string UpdateCurrentFormat => Loc.T(nameof(UpdateCurrentFormat));

    /// <summary>Что лежит на GitHub: «На GitHub: v2.1.0».</summary>
    public static string UpdateOnGithubFormat => Loc.T(nameof(UpdateOnGithubFormat));

    /// <summary>На GitHub данных нет: ещё не проверяли, не вышло или прогон без права.</summary>
    public static string UpdateOnGithubUnknown => Loc.T(nameof(UpdateOnGithubUnknown));

    /// <summary>Дата выпуска: «Выпущен: 27.09.2026».</summary>
    public static string UpdatePublishedFormat => Loc.T(nameof(UpdatePublishedFormat));

    /// <summary>Дата выпуска не разобралась — панель говорит «неизвестна», а не выдумывает её.</summary>
    public static string UpdatePublishedUnknown => Loc.T(nameof(UpdatePublishedUnknown));

    /// <summary>Заголовок раздела заметок к выпуску.</summary>
    public static string UpdateNotesTitle => Loc.T(nameof(UpdateNotesTitle));

    /// <summary>Заметок нет: сказать это словами, а не пустым местом.</summary>
    public static string UpdateNotesEmpty => Loc.T(nameof(UpdateNotesEmpty));

    /// <summary>Кнопка «Открыть на GitHub» — открывает страницу выпуска в браузере человека.</summary>
    public static string UpdateOpenGithubButton => Loc.T(nameof(UpdateOpenGithubButton));

    /// <summary>Подсказка кнопки «Открыть на GitHub».</summary>
    public static string TipUpdateOpenGithubButton => Loc.T(nameof(TipUpdateOpenGithubButton));

    /// <summary>Кнопка «Пропустить эту версию» — ответ человека, о ней больше не напоминаем.</summary>
    public static string UpdateSkipButton => Loc.T(nameof(UpdateSkipButton));

    /// <summary>Подсказка кнопки «Пропустить эту версию».</summary>
    public static string TipUpdateSkipButton => Loc.T(nameof(TipUpdateSkipButton));

    /// <summary>Ответ после пропуска: что именно произошло, словами.</summary>
    public static string UpdateSkipDoneFormat => Loc.T(nameof(UpdateSkipDoneFormat));

    /// <summary>Пропуск не запомнился (настройки недоступны на запись) — об этом человеку.</summary>
    public static string UpdateSkipFailed => Loc.T(nameof(UpdateSkipFailed));

    /// <summary>Кнопка «Скачать и подготовить»: скачать, сверить сумму, распаковать, снять копию.</summary>
    public static string UpdatePrepareButton => Loc.T(nameof(UpdatePrepareButton));

    /// <summary>Кнопка на время подготовки.</summary>
    public static string UpdatePreparingButton => Loc.T(nameof(UpdatePreparingButton));

    /// <summary>Подсказка кнопки «Скачать и подготовить»: что она делает и чего НЕ делает.</summary>
    public static string TipUpdatePrepareButton => Loc.T(nameof(TipUpdatePrepareButton));

    /// <summary>Пояснение под кнопками: замена файлов сама не запускается.</summary>
    public static string UpdatePrepareHint => Loc.T(nameof(UpdatePrepareHint));

    /// <summary>Адреса вложений панель узнаёт при проверке — что делать, если данных нет.</summary>
    public static string UpdateNeedCheckHint => Loc.T(nameof(UpdateNeedCheckHint));

    /// <summary>Прогон проверки замену файлов не готовит вовсе.</summary>
    public static string UpdateInstallUnavailable => Loc.T(nameof(UpdateInstallUnavailable));

    /// <summary>«Подготовлено обновление v2.1.0» — то, что человек увидел после подготовки.</summary>
    public static string UpdatePreparedTitleFormat => Loc.T(nameof(UpdatePreparedTitleFormat));

    /// <summary>Где лежит подготовленная сборка.</summary>
    public static string UpdatePreparedStagedFormat => Loc.T(nameof(UpdatePreparedStagedFormat));

    /// <summary>Где лежит страховочная копия прежней панели (путь назад).</summary>
    public static string UpdatePreparedBackupFormat => Loc.T(nameof(UpdatePreparedBackupFormat));

    /// <summary>Где лежит сценарий замены файлов.</summary>
    public static string UpdatePreparedScriptFormat => Loc.T(nameof(UpdatePreparedScriptFormat));

    /// <summary>Где лежит журнал замены — по нему панель рассказывает итог при следующем запуске.</summary>
    public static string UpdatePreparedLogFormat => Loc.T(nameof(UpdatePreparedLogFormat));

    /// <summary>Кнопка «Обновить и перезапустить панель» — отдельное нажатие, самое рискованное.</summary>
    public static string UpdateReplaceButton => Loc.T(nameof(UpdateReplaceButton));

    /// <summary>Подсказка кнопки замены: что она сделает с панелью.</summary>
    public static string TipUpdateReplaceButton => Loc.T(nameof(TipUpdateReplaceButton));

    /// <summary>Пояснение у кнопки замены: панель закроется, сценарий заменит файлы и поднимет её.</summary>
    public static string UpdateReplaceHint => Loc.T(nameof(UpdateReplaceHint));

    /// <summary>Сценарий замены не запустился — причина словами, а не молчанием.</summary>
    public static string UpdateLaunchFailedFormat => Loc.T(nameof(UpdateLaunchFailedFormat));

    /// <summary>Система не создала процесс сценария — тоже названная причина.</summary>
    public static string UpdateLaunchNotStarted => Loc.T(nameof(UpdateLaunchNotStarted));

    /// <summary>Ход подготовки: «Архив панели: 42 %».</summary>
    public static string UpdateProgressFormat => Loc.T(nameof(UpdateProgressFormat));

    /// <summary>Ход подготовки, когда размер файла неизвестен: процент был бы выдумкой.</summary>
    public static string UpdateProgressUnknownFormat => Loc.T(nameof(UpdateProgressUnknownFormat));

    /// <summary>Подпись перед причиной отказа: «Обновление не подготовлено».</summary>
    public static string UpdateRefusalTitle => Loc.T(nameof(UpdateRefusalTitle));

    /// <summary>Строка журнала: окно обновления в этом прогоне не открывается.</summary>
    public static string PanelLogUpdateWindowUnavailable => Loc.T(nameof(PanelLogUpdateWindowUnavailable));

    /// <summary>Строка журнала: окно обновления открыто.</summary>
    public static string UpdateOpenedLog => Loc.T(nameof(UpdateOpenedLog));

    /// <summary>Строка журнала: окно обновления уже открыто — поднял его.</summary>
    public static string UpdateRaisedLog => Loc.T(nameof(UpdateRaisedLog));

    /// <summary>Строка журнала: человек пропустил версию.</summary>
    public static string UpdateSkipSavedLogFormat => Loc.T(nameof(UpdateSkipSavedLogFormat));

    /// <summary>Строка журнала: подготовка обновления началась.</summary>
    public static string UpdatePrepareStartedLogFormat => Loc.T(nameof(UpdatePrepareStartedLogFormat));

    /// <summary>Строка журнала: обновление подготовлено (версия и папка сборки).</summary>
    public static string UpdatePreparedDoneLogFormat => Loc.T(nameof(UpdatePreparedDoneLogFormat));

    /// <summary>Строка журнала: сценарий замены запущен по щелчку человека.</summary>
    public static string UpdateLaunchLogFormat => Loc.T(nameof(UpdateLaunchLogFormat));

    /// <summary>Строка журнала: сценарий замены запустить не удалось.</summary>
    public static string UpdateLaunchFailedLogFormat => Loc.T(nameof(UpdateLaunchFailedLogFormat));

    /// <summary>Строка журнала: замена файлов состоялась — при следующем запуске.</summary>
    public static string UpdateAppliedLogFormat => Loc.T(nameof(UpdateAppliedLogFormat));

    /// <summary>Заголовок сообщения о неудавшемся обновлении.</summary>
    public static string UpdateNoticeTitle => Loc.T(nameof(UpdateNoticeTitle));

    /// <summary>Текст сообщения: до какой версии не применилось и почему (причина — по ключу ядра).</summary>
    public static string UpdateNoticeFailedFormat => Loc.T(nameof(UpdateNoticeFailedFormat));

    // --- причины отказа движка установки (ключи отдаёт `UpdateRefusals.Key`) ---
    //
    // ⚠️ ПО СТРОКЕ НА КАЖДЫЙ РАЗЛИЧИМЫЙ СЛУЧАЙ, а не «на тему». Пока пять причин распаковки
    // делили один ключ, слова причины приходилось класть в `UpdatePreparation.Detail` — русским
    // литералом из движка, — и на английском и китайском внутри чужой фразы стоял русский текст
    // («Could not download the release file: файл сумм: …»). Дефект найден 29.09.2026.

    /// <summary>Отказ: у прогона нет права готовить замену файлов.</summary>
    public static string UpdateRefusedLocked => Loc.T(nameof(UpdateRefusedLocked));

    /// <summary>Отказ: выпуск не новее установленной панели.</summary>
    public static string UpdateAlreadyLatest => Loc.T(nameof(UpdateAlreadyLatest));

    /// <summary>Отказ: номер версии выпуска не разобран.</summary>
    public static string UpdateNoReleaseVersion => Loc.T(nameof(UpdateNoReleaseVersion));

    /// <summary>Отказ: не названа папка панели, которую надо заменить.</summary>
    public static string UpdateRefusedNoTarget => Loc.T(nameof(UpdateRefusedNoTarget));

    /// <summary>Отказ: в выпуске нет архива панели.</summary>
    public static string UpdateNoArchive => Loc.T(nameof(UpdateNoArchive));

    /// <summary>Отказ: в выпуске нет файла контрольных сумм.</summary>
    public static string UpdateNoSums => Loc.T(nameof(UpdateNoSums));

    /// <summary>Отказ: файл контрольных сумм скачать не удалось (значение — причина от загрузчика).</summary>
    public static string UpdateSumsDownloadFailedFormat => Loc.T(nameof(UpdateSumsDownloadFailedFormat));

    /// <summary>Отказ: файл сумм пуст или не разобрался ни на одну строку.</summary>
    public static string UpdateSumsUnreadable => Loc.T(nameof(UpdateSumsUnreadable));

    /// <summary>Отказ: архив панели скачать не удалось (значение — причина от загрузчика).</summary>
    public static string UpdateArchiveDownloadFailedFormat => Loc.T(nameof(UpdateArchiveDownloadFailedFormat));

    /// <summary>Отказ: суммы для архива в файле нет (значение — имя файла).</summary>
    public static string UpdateArchiveSumMissingFormat => Loc.T(nameof(UpdateArchiveSumMissingFormat));

    /// <summary>Отказ: сумма архива не сошлась (значения — ожидаемое и полученное).</summary>
    public static string UpdateArchiveSumMismatchFormat => Loc.T(nameof(UpdateArchiveSumMismatchFormat));

    /// <summary>Отказ: скачанного архива нет там, где его ждут.</summary>
    public static string UpdateArchiveMissing => Loc.T(nameof(UpdateArchiveMissing));

    /// <summary>Отказ: не названа папка, в которую распаковывать архив.</summary>
    public static string UpdateUnpackFolderMissing => Loc.T(nameof(UpdateUnpackFolderMissing));

    /// <summary>Отказ: архив не читается.</summary>
    public static string UpdateArchiveUnreadable => Loc.T(nameof(UpdateArchiveUnreadable));

    /// <summary>Отказ: архив читается, но файлов в нём нет.</summary>
    public static string UpdateArchiveEmpty => Loc.T(nameof(UpdateArchiveEmpty));

    /// <summary>Отказ: в архиве есть записи мимо каталога распаковки (значение — сколько их).</summary>
    public static string UpdateArchiveUnsafeFormat => Loc.T(nameof(UpdateArchiveUnsafeFormat));

    /// <summary>Отказ: разобрать архив не удалось (значение — причина от исключения).</summary>
    public static string UpdateArchiveBrokenFormat => Loc.T(nameof(UpdateArchiveBrokenFormat));

    /// <summary>Отказ: в распакованном архиве нет собранной панели.</summary>
    public static string UpdateNoExe => Loc.T(nameof(UpdateNoExe));

    /// <summary>Отказ: версия внутри архива не совпала с версией выпуска (значения — внутри и ожидалось).</summary>
    public static string UpdateVersionMismatchFormat => Loc.T(nameof(UpdateVersionMismatchFormat));

    /// <summary>Отказ: установщик выпуска скачать не удалось (значение — причина от загрузчика).</summary>
    public static string UpdateSetupDownloadFailedFormat => Loc.T(nameof(UpdateSetupDownloadFailedFormat));

    /// <summary>Отказ: суммы для установщика в файле нет (значение — имя файла).</summary>
    public static string UpdateSetupSumMissingFormat => Loc.T(nameof(UpdateSetupSumMissingFormat));

    /// <summary>Отказ: сумма установщика не сошлась (значения — ожидаемое и полученное).</summary>
    public static string UpdateSetupSumMismatchFormat => Loc.T(nameof(UpdateSetupSumMismatchFormat));

    /// <summary>Отказ: страховочную копию прежней панели снять не удалось.</summary>
    public static string UpdateNoBackup => Loc.T(nameof(UpdateNoBackup));

    /// <summary>Отказ: сценарий замены или шапку журнала записать не удалось (значение — причина от исключения).</summary>
    public static string UpdateWriteFailedFormat => Loc.T(nameof(UpdateWriteFailedFormat));

    /// <summary>Отказ, которого панель не знает: причина не названа (журнал правили руками).</summary>
    public static string UpdateRefusedUnknown => Loc.T(nameof(UpdateRefusedUnknown));

    // --- причины итога замены (ключи отдаёт `UpdateOutcomes.ReasonKeyFor`) ---

    /// <summary>Итог: файлы заменены, но панель всё ещё работает прежней версией.</summary>
    public static string UpdateReasonStillOld => Loc.T(nameof(UpdateReasonStillOld));

    /// <summary>Итог: файлы заменены, но панель из своей папки не поднялась.</summary>
    public static string UpdateReasonNoPanel => Loc.T(nameof(UpdateReasonNoPanel));

    /// <summary>Итог: замена не удалась, прежняя версия возвращена из страховочной копии.</summary>
    public static string UpdateReasonRolledBack => Loc.T(nameof(UpdateReasonRolledBack));

    /// <summary>Итог: вернуть прежнюю версию не удалось.</summary>
    public static string UpdateReasonRestoreFailed => Loc.T(nameof(UpdateReasonRestoreFailed));

    /// <summary>Итог: откатываться было некуда — годной страховочной копии нет.</summary>
    public static string UpdateReasonNoBackup => Loc.T(nameof(UpdateReasonNoBackup));

    /// <summary>Итог: панель не вышла вовремя — файлы не трогали.</summary>
    public static string UpdateReasonPanelRunning => Loc.T(nameof(UpdateReasonPanelRunning));

    /// <summary>Итог: работала вторая копия панели — файлы не трогали.</summary>
    public static string UpdateReasonMutexBusy => Loc.T(nameof(UpdateReasonMutexBusy));

    /// <summary>Итог: замок одной копии не удалось проверить — файлы не трогали.</summary>
    public static string UpdateReasonMutexUnknown => Loc.T(nameof(UpdateReasonMutexUnknown));

    /// <summary>Итог, которого панель не знает: журнал замены правили руками.</summary>
    public static string UpdateReasonUnknown => Loc.T(nameof(UpdateReasonUnknown));

    // --- этапы подготовки (значения отдаёт `UpdateStage`) ---

    /// <summary>Этап: скачивается файл контрольных сумм.</summary>
    public static string UpdateStageSums => Loc.T(nameof(UpdateStageSums));

    /// <summary>Этап: скачивается архив панели.</summary>
    public static string UpdateStageArchive => Loc.T(nameof(UpdateStageArchive));

    /// <summary>Этап: скачивается установщик выпуска.</summary>
    public static string UpdateStageSetup => Loc.T(nameof(UpdateStageSetup));

    /// <summary>Этап: сверяется SHA-256 скачанного.</summary>
    public static string UpdateStageVerify => Loc.T(nameof(UpdateStageVerify));

    /// <summary>Этап: архив распаковывается и сверяется версия внутри.</summary>
    public static string UpdateStageUnpack => Loc.T(nameof(UpdateStageUnpack));

    /// <summary>Этап: снимается страховочная копия прежней панели.</summary>
    public static string UpdateStageBackup => Loc.T(nameof(UpdateStageBackup));

    /// <summary>Этап: пишется сценарий замены и шапка журнала.</summary>
    public static string UpdateStageScript => Loc.T(nameof(UpdateStageScript));

    // --- ИСТОРИЯ ЦЕН: точки изменения и своё окно ----------------------------
    //
    // Требование владельца 28.09.2026 (п. 26 `docs\DESIGN.md`): хранить не каждую проверку,
    // а ТОЧКИ ИЗМЕНЕНИЯ, и о смене тарифа ПРЕДУПРЕЖДАТЬ шариком, называя ЧТО именно изменилось.
    // Записи лежат структурой, а слова собираются при показе — поэтому все строки ниже живут
    // здесь и читаются в момент, когда человек смотрит окно или приходит шарик.

    /// <summary>Заголовок шарика: тариф на странице изменился.</summary>
    public static string NotifyPricingChangedTitle => Loc.T(nameof(NotifyPricingChangedTitle));

    /// <summary>Шарик, строка про цены: «цены изменились: {0}» — модель и сами строки цены.</summary>
    public static string NotifyPricingPricesFormat => Loc.T(nameof(NotifyPricingPricesFormat));

    /// <summary>Одна строка цены в шарике: «{0} {1}: {2} → {3}» — строка, тариф, было, стало.</summary>
    public static string NotifyPricingPriceRowFormat => Loc.T(nameof(NotifyPricingPriceRowFormat));

    /// <summary>
    /// Шарик, строка про окна пика: «на странице цены окна пика изменились: было {0}, стало {1}».
    /// Сказано именно «на странице цены»: расписание панели эти окна не меняют — их применяет
    /// подтверждение человека (правило панели 1.x).
    /// </summary>
    public static string NotifyPricingWindowsFormat => Loc.T(nameof(NotifyPricingWindowsFormat));

    /// <summary>Дверь в окно истории — рядом с «Обновить информацию» в окне «Пики и тарифы».</summary>
    public static string PricingHistoryButton => Loc.T(nameof(PricingHistoryButton));

    /// <summary>Подсказка двери: что человек увидит в окне истории.</summary>
    public static string TipPricingHistoryButton => Loc.T(nameof(TipPricingHistoryButton));

    /// <summary>Заголовок окна истории.</summary>
    public static string PricingHistoryWindowTitle => Loc.T(nameof(PricingHistoryWindowTitle));

    /// <summary>Крупный заголовок внутри окна истории.</summary>
    public static string PricingHistoryHeading => Loc.T(nameof(PricingHistoryHeading));

    /// <summary>Серый подзаголовок окна истории: про что оно.</summary>
    public static string PricingHistorySubtitle => Loc.T(nameof(PricingHistorySubtitle));

    /// <summary>Подпись самой свежей записи в списке слева: «{0} — текущие».</summary>
    public static string PricingHistoryEntryCurrentFormat => Loc.T(nameof(PricingHistoryEntryCurrentFormat));

    /// <summary>С чем сравнивается выбранная запись: «к предыдущей записи ({0})».</summary>
    public static string PricingHistoryComparedFormat => Loc.T(nameof(PricingHistoryComparedFormat));

    /// <summary>
    /// С чем сравнивается ПЕРВАЯ ТОЧКА ИЗМЕНЕНИЯ: «к первой цене ({0})». Слова человека, а не
    /// разбора: «якорь» и «базовая точка» — внутренний язык, и показывать его нельзя.
    /// </summary>
    public static string PricingHistoryAgainstAnchorFormat => Loc.T(nameof(PricingHistoryAgainstAnchorFormat));

    /// <summary>
    /// ПЕРВАЯ СТРОКА СПИСКА: «{0} — первая цена». Слова владельца 29.09.2026 (п. 39
    /// <c>docs\DESIGN.md</c>): список не пуст, как только панель хоть раз прочитала цены.
    /// </summary>
    public static string PricingHistoryFirstPriceFormat => Loc.T(nameof(PricingHistoryFirstPriceFormat));

    /// <summary>
    /// Пояснение к первой цене, когда изменения уже были: когда панель её получила. Сравнивать её
    /// не с чем — она первая, и это сказано человеку прямо.
    /// </summary>
    public static string PricingHistoryFirstPriceNoteFormat => Loc.T(nameof(PricingHistoryFirstPriceNoteFormat));

    /// <summary>
    /// Изменений ещё не было: «это первая цена, которую панель получила: {0}; с тех пор цены
    /// не менялись». Тот же случай, что и <see cref="PricingHistoryFirstPriceFormat"/>, но с ответом
    /// на вопрос «а сейчас сколько?»: сейчас — столько же.
    /// </summary>
    public static string PricingHistoryEmptyFormat => Loc.T(nameof(PricingHistoryEmptyFormat));

    /// <summary>Самая старая запись ушла за предел: «хранятся последние {0} изменений: …».</summary>
    public static string PricingHistoryDroppedFormat => Loc.T(nameof(PricingHistoryDroppedFormat));

    /// <summary>Заголовок таблицы цен выбранной записи.</summary>
    public static string PricingHistoryPricesHeading => Loc.T(nameof(PricingHistoryPricesHeading));

    /// <summary>В этой записи цены не менялись — только окна пика.</summary>
    public static string PricingHistoryPricesUnchanged => Loc.T(nameof(PricingHistoryPricesUnchanged));

    /// <summary>Заголовок таблицы окон пика выбранной записи.</summary>
    public static string PricingHistoryWindowsHeading => Loc.T(nameof(PricingHistoryWindowsHeading));

    /// <summary>В этой записи окна пика не менялись — только цены.</summary>
    public static string PricingHistoryWindowsUnchanged => Loc.T(nameof(PricingHistoryWindowsUnchanged));

    /// <summary>Заголовок колонки «изменение к предыдущей записи».</summary>
    public static string PricingHistoryColumnChange => Loc.T(nameof(PricingHistoryColumnChange));

    /// <summary>Строка таблицы окон: какими они были в предыдущей записи.</summary>
    public static string PricingHistoryColumnBefore => Loc.T(nameof(PricingHistoryColumnBefore));

    /// <summary>Строка таблицы окон: какими они стали в выбранной записи.</summary>
    public static string PricingHistoryColumnAfter => Loc.T(nameof(PricingHistoryColumnAfter));

    /// <summary>Стрелка подорожания. Знак, а не слово: он одинаков на трёх языках.</summary>
    public static string PricingHistoryArrowUp => Loc.T(nameof(PricingHistoryArrowUp));

    /// <summary>Стрелка удешевления.</summary>
    public static string PricingHistoryArrowDown => Loc.T(nameof(PricingHistoryArrowDown));

    /// <summary>Сравнивать не с чем: цены на одной из сторон нет вовсе.</summary>
    public static string PricingHistoryArrowNone => Loc.T(nameof(PricingHistoryArrowNone));

    /// <summary>Строка журнала: записана молчаливая базовая точка истории.</summary>
    public static string PricingHistoryAnchorLogFormat => Loc.T(nameof(PricingHistoryAnchorLogFormat));

    /// <summary>Строка журнала: записано изменение тарифа, всего записей столько-то.</summary>
    public static string PricingHistorySavedLogFormat => Loc.T(nameof(PricingHistorySavedLogFormat));

    /// <summary>Строка журнала: самая старая запись ушла за предел хранения.</summary>
    public static string PricingHistoryDroppedLogFormat => Loc.T(nameof(PricingHistoryDroppedLogFormat));

    /// <summary>Строка журнала: истории сохранить некуда — у прогона нет права писать файлы состояния.</summary>
    public static string PricingHistoryUnsavedLog => Loc.T(nameof(PricingHistoryUnsavedLog));

    /// <summary>Строка журнала: окно истории открыто.</summary>
    public static string PricingHistoryOpenedLog => Loc.T(nameof(PricingHistoryOpenedLog));

    /// <summary>Строка журнала: окно истории уже открыто — вывели на передний план.</summary>
    public static string PricingHistoryRaisedLog => Loc.T(nameof(PricingHistoryRaisedLog));

    /// <summary>Строка журнала: просьба открыть историю, а окна у панели нет (так бывает у прогонов).</summary>
    public static string PanelLogPricingHistoryUnavailable => Loc.T(nameof(PanelLogPricingHistoryUnavailable));
}