using System.Text.Json.Serialization;
using DshPanel.Localization;

namespace DshPanel.Settings;

/// <summary>Тема оформления панели.</summary>
public enum PanelTheme
{
    /// <summary>Как в Windows.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// Настройки панели 2.0.
///
/// Здесь ТОЛЬКО те поля, которые код читает СЕЙЧАС, — и это правило, а не совпадение.
/// Поле, которое ничего не меняет, врёт человеку: он ставит галочку, ничего не происходит,
/// и он перестаёт верить и остальным. Поэтому в `settings.json` попадает настройка вместе
/// с кодом, который её исполняет, а не заранее «на будущее».
///
/// Значения по умолчанию взяты у v1 (<c>dsh-tray\AppPaths.cs</c>, класс <c>AppSettings</c>),
/// чтобы человек, перешедший с 1.x, не получил другого поведения:
/// <c>serverWorkingDir</c> — пусто = папка панели, <c>theme</c> — «как в Windows».
///
/// **Умолчание порта прошло два решения владельца, и оба видны здесь.** 24.09.2026 он выбрал
/// <b>3081</b>: 3080 тогда значился запретным — на нём стоял его живой DSH. 26.09.2026 он решил
/// убрать панель 1.x совсем, и 2.0 встала на её место: умолчание стало <b>3080</b>
/// (<see cref="ServerDecisions.DefaultServerPort"/>), а запрет на его ЗАНЯТИЕ остался у прогонов
/// проверки и живёт правом в контроллере сервера. Файлы, где 3081 лежит как умолчание, узнаются
/// по признаку <see cref="ServerPortChosen"/> и переводятся при чтении (<c>SettingsStore</c>).
/// </summary>
public sealed class PanelSettings
{
    /// <summary>Тема «как в Windows» — умолчание, как было в v1.</summary>
    public const string ThemeSystem = "auto";

    public const string ThemeLight = "light";

    public const string ThemeDark = "dark";

    /// <summary>
    /// Рабочая папка, из которой поднимается сервер (то, что движок считает рабочим каталогом).
    /// Пусто — папка панели: это умолчание v1 (`serverWorkingDir`), и оно остаётся умолчанием 2.0.
    /// </summary>
    [JsonPropertyName("serverWorkingDir")]
    public string ServerWorkingDir { get; set; } = string.Empty;

    /// <summary>
    /// Порт, который панель занимает, поднимая СВОЙ сервер. Постоянный: по нему человек находит
    /// свой сервер, и он переживает перезапуск панели.
    ///
    /// ⚠️ Умолчание — <b>3080</b>, тот же порт, на котором стояла панель 1.x: владелец убирает 1.x
    /// и переезжает на 2.0 (решение владельца 26.09.2026), и адрес его сервера обязан остаться
    /// прежним. До этого решения умолчанием был 3081, а 3080 значился запретным.
    ///
    /// Значение приводится к настоящему порту (<see cref="ServerDecisions.NormalizePort"/>) —
    /// и при сохранении, и в окне настроек. Право ЗАНЯТЬ порт владельца здесь не спрашивается
    /// намеренно: файл настроек принадлежит человеку, и он вправе записать туда любой настоящий
    /// порт, включая 3080. Право решает другое — где панель этот порт ДЕЙСТВИТЕЛЬНО займёт
    /// (<see cref="Server.ServerController"/>), и прогон проверки занимает 3081, а не 3080.
    /// </summary>
    [JsonPropertyName("serverPort")]
    public int ServerPort { get; set; } = Server.ServerDecisions.DefaultServerPort;

    /// <summary>
    /// Выбирал ли человек порт САМ. <c>false</c> — в файле лежит умолчание, и панель вправе
    /// перевести его на новое.
    ///
    /// Зачем отдельный признак, а не «сравнить с умолчанием». До 26.09.2026 умолчанием 2.0 был
    /// <b>3081</b>, и это значение лежит в файлах у всех, кто уже поставил 2.0. Признак не выставлен
    /// и в файле ровно 3081 — значит порт человек НЕ выбирал, и его надо перевести на новый
    /// умолчание 3080 (с решением владельца «убираем 1.x» это и есть его прежний адрес).
    /// Признак выставлен — не трогаем: человек сохранял настройки, и 3081 в его файле это ЕГО
    /// выбор, а не остаток умолчания. Разница между «не выбирал» и «выбрал 3081» ничем, кроме
    /// этого признака, не выражается.
    ///
    /// Ставит признак кнопка «Сохранить» в окне настроек — единственная дверь, которой порт
    /// меняет человек.
    /// </summary>
    [JsonPropertyName("serverPortChosen")]
    public bool ServerPortChosen { get; set; }

    /// <summary>Тема оформления: <c>auto</c>, <c>light</c> или <c>dark</c>.</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = ThemeSystem;

    /// <summary>
    /// Язык интерфейса: <c>auto</c> («как в системе»), <c>ru</c>, <c>en</c> или <c>zh</c>.
    ///
    /// Устроено как тема и по той же причине: файл настроек человек правит руками, поэтому
    /// любое незнакомое значение приводится к <c>auto</c> (<see cref="NormalizeLanguage"/>),
    /// а не роняет панель и не оставляет её без строк.
    ///
    /// ⚠️ Язык применяется при СЛЕДУЮЩЕМ запуске панели (решение дирижёра, этап 5): окна и меню
    /// собираются один раз при старте, и пересобрать их на ходу — отдельная работа. Человеку
    /// об этом сказано словами в окне настроек, а не умолчанием.
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = LanguageDecisions.Auto;

    /// <summary>
    /// Активный агент: по нему панель показывает баланс и окна пика и по нему же предупреждает.
    /// Решение владельца 24.09.2026: агентов будет несколько («баланс, тарифы и окна пика —
    /// на каждого агента»), и переключатель есть уже сейчас. Пока список из одного — DeepSeek.
    /// </summary>
    [JsonPropertyName("activeAgent")]
    public string ActiveAgent { get; set; } = Agents.AgentCatalog.DefaultId;

    /// <summary>Обновлять баланс автоматически. Умолчание v1 — включено.</summary>
    [JsonPropertyName("balanceAutoRefresh")]
    public bool BalanceAutoRefresh { get; set; } = true;

    /// <summary>Периодичность обновления баланса, минут. Умолчание v1 — 5.</summary>
    [JsonPropertyName("balanceRefreshMinutes")]
    public int BalanceRefreshMinutes { get; set; } = 5;

    /// <summary>Предупреждать о низком балансе. Умолчание v1 — выключено.</summary>
    [JsonPropertyName("balanceWarnEnabled")]
    public bool BalanceWarnEnabled { get; set; }

    /// <summary>Порог предупреждения — в валюте счёта.</summary>
    [JsonPropertyName("balanceWarnThreshold")]
    public decimal BalanceWarnThreshold { get; set; }

    /// <summary>
    /// За сколько минут предупредить о начале пика. 0 — не предупреждать.
    /// Настройки в v1 не было: это решение владельца 24.09.2026 — «уведомить меня о наступлении
    /// пика за указанное время».
    /// </summary>
    [JsonPropertyName("peakNotifyMinutes")]
    public int PeakNotifyMinutes { get; set; }

    /// <summary>
    /// Папка, куда панель кладёт резервные копии. Пусто — умолчание v1
    /// («Документы\DeepSeekHarness-Backups», <see cref="Backup.BackupNaming.DefaultFolder"/>).
    ///
    /// Поле появилось 26.09.2026 вместе с окном копий, и это не «на будущее»: окно показывает
    /// папку человеку и кладёт туда архивы. Пустая строка — не «не задано», а «взято умолчание»,
    /// и окно говорит об этом словами: иначе человек считает, что папку выбрал он сам.
    /// </summary>
    [JsonPropertyName("backupFolder")]
    public string BackupFolder { get; set; } = string.Empty;

    /// <summary>
    /// Класть ли в копию каталоги приватных ключей. По умолчанию **НЕТ** — как было и в v1:
    /// «по умолчанию в архив не попадает ни один приватный ключ». Ключи — личные файлы,
    /// и решать про них должен владелец машины, а не приложение.
    ///
    /// Решение владельца 26.09.2026: ключи снова могут входить в копию — «как в v1», то есть
    /// **по отдельному разрешению**. Включённое разрешение означает, что архив сам становится
    /// ключом доступа, и об этом сказано словами и в настройках, и в окне копий.
    /// </summary>
    [JsonPropertyName("backupWithKeys")]
    public bool BackupWithKeys { get; set; }

    /// <summary>
    /// Свои каталоги ключей — сверх каталога SSH текущего пользователя. По умолчанию пусто.
    ///
    /// Найденное у человека (например каталог ключей подписи приложения) панель **предлагает
    /// кнопкой**, а не подставляет сюда молча: это его каталоги, и решает про них он —
    /// то же правило, что у рабочей папки от панели v1 (решение владельца 24.09.2026).
    /// </summary>
    [JsonPropertyName("backupKeyDirs")]
    public List<string> BackupKeyDirs { get; set; } = new();

    /// <summary>
    /// ⚠️ МЁРТВОЕ ПОЛЕ. Прежде — «копия для передачи» как ПОСТОЯННАЯ настройка. Решением владельца
    /// 27.09.2026 (п. 11 `docs\DESIGN.md`, его слова: *«функция по созданию копии и передачи
    /// кому-либо смешалась с настройками резервных копий, хотя это просто отдельная фича»*) передача
    /// стала **разовым действием на одну копию**: её решает окно копий, а не файл настроек.
    ///
    /// **Ни один путь копии это значение не читает** — ни окно, ни расписание, ни `--backup`
    /// (у режима без окна свой ключ `--shareable`). Значение приводит к `false` дверь настроек
    /// (<see cref="SettingsStore.Clean"/>) — и при чтении, и при записи, — поэтому первое же
    /// сохранение настроек стирает его окончательно, и это ЦЕЛЬ, а не потеря.
    ///
    /// **Удалить поле нельзя, и это не осторожность.** У человека в файле настроек уже лежит ключ
    /// `backupShareable` (его пишет нынешняя сборка), а список известных ключей берётся у САМОГО
    /// типа (<see cref="SettingsStore.Unknown"/>): убери свойство — и панель объявит человеку
    /// незнакомым ключ его собственного файла. Сторожит это проверка
    /// «Мёртвое_поле_старых_настроек_не_делает_копию_передаваемой».
    /// </summary>
    [JsonPropertyName("backupShareable")]
    public bool BackupShareable { get; set; }

    /// <summary>
    /// Снимать ли копии автоматически по расписанию. **Умолчание — включено**, и это не
    /// «навязывание»: в v1 автокопия была включена («Автокопия включена: каждые 24 ч, хранить
    /// 2 последних»), и человек, перешедший с 1.x, обязан получить то же поведение, а не тихо
    /// остаться без копий. Кому это не нужно — снимает галочку в настройках.
    /// </summary>
    [JsonPropertyName("backupScheduleEnabled")]
    public bool BackupScheduleEnabled { get; set; } = true;

    /// <summary>
    /// Через сколько часов снимать копию. Умолчание — 24 (умолчание v1), границы 1…168.
    ///
    /// Расписание именно «каждые N часов», а не «ежедневно в час X»: **решение владельца**.
    /// Панель смотрит, сколько прошло с ПРОШЛОЙ копии, и если панель была выключена — снимает
    /// копию при запуске, то есть догоняет пропущенное. Привязка к часу суток так не умеет:
    /// пропущенный день потерялся бы молча.
    /// </summary>
    [JsonPropertyName("backupEveryHours")]
    public int BackupEveryHours { get; set; } = 24;

    /// <summary>
    /// Сколько последних копий хранить. Умолчание — 2 (умолчание v1), границы 1…20.
    /// Старые сверх предела удаляются ротацией, но **самая свежая не удаляется никогда**,
    /// а предохранительные копии перед накатом ротация не трогает вовсе
    /// (<see cref="Backup.BackupRotation"/>).
    /// </summary>
    [JsonPropertyName("backupKeepCount")]
    public int BackupKeepCount { get; set; } = 2;

    /// <summary>
    /// РЕЖИМ ОБЪЁМА копии: <c>auto</c>, <c>full</c> или <c>custom</c> (<see cref="Backup.BackupScope"/>).
    /// Значения латиницей, как у темы и языка, и по той же причине: файл настроек правят руками,
    /// и значение в нём обязано читаться без словаря. Незнакомое значение — <c>auto</c>:
    /// непонятное обязано означать поведение, которое было у панели до появления режимов.
    /// </summary>
    [JsonPropertyName("backupScope")]
    public string BackupScope { get; set; } = Backup.BackupScopeDecisions.Auto;

    /// <summary>
    /// Лишние имена папок для режима «свой фильтр» — то, что человек назвал мусором СВЕРХ
    /// автоматического списка (<c>vendor</c>, <c>target</c>, <c>.venv</c>). Действуют на все корни.
    /// Пусто — режим «свой фильтр» ведёт себя как автоматический, и окно говорит об этом словами,
    /// а не молчанием.
    /// </summary>
    [JsonPropertyName("backupExtraExclusions")]
    public List<string> BackupExtraExclusions { get; set; } = new();

    /// <summary>
    /// Порт найденного сервера DSH, который человек ОДИН РАЗ взял под управление.
    /// <c>0</c> — согласия нет: панель спросит, как в первый раз.
    ///
    /// Решение владельца 26.09.2026: «спросить ОДИН раз и запомнить ответ». Согласие живёт
    /// в настройках, а не в памяти, потому что обязано пережить перезапуск панели: до этого
    /// панель после каждого запуска находила сервер заново и снова спрашивала человека.
    ///
    /// ⚠️ Здесь может стоять и порт 3080 — и это НЕ ошибка. Запрет порта владельца
    /// (<see cref="ServerDecisions.IsAllowedPort"/>) — запрет его ЗАНЯТЬ, а найденный сервер
    /// владельца стоит как раз на 3080: он и есть главный случай встраивания. Поэтому порт
    /// согласия приводится к виду отдельным предикатом (<see cref="NormalizeAdoptedPort"/>),
    /// а не тем же, что порт своего сервера.
    ///
    /// Забывается согласие ровно одним способом — кнопкой «Отвязаться»: она пишет сюда 0.
    /// Гашение встроенного сервера согласие НЕ забывает: человек сказал «это окружение моё»,
    /// и следующий запуск его DSH панель снова возьмёт под управление — уже не спрашивая.
    /// </summary>
    [JsonPropertyName("adoptedServerPort")]
    public int AdoptedServerPort { get; set; }

    /// <summary>
    /// КОГДА страница цен разбиралась в последний раз — круговая дата-время (см.
    /// <see cref="Pricing.PricingDecisions.Stamp"/>). Пусто — никогда.
    ///
    /// Зачем в файле, а не в памяти: решение владельца 27.09.2026 — разбор идёт «при запуске
    /// панели, но НЕ чаще раза в сутки». Отметка в памяти означала бы разбор при КАЖДОМ запуске
    /// панели, то есть «чаще раза в сутки» ровно настолько, насколько часто человек её открывает.
    ///
    /// ⚠️ Это НЕ настройка человека, а след работы панели — как запомненное согласие на сервер.
    /// Поэтому в окне она не показывается полем, а читается словами («проверено 28.09.2026, 14:32»),
    /// и через «Сохранить» проходит НЕТРОНУТОЙ.
    /// </summary>
    [JsonPropertyName("pricingCheckedAt")]
    public string PricingCheckedAt { get; set; } = string.Empty;

    /// <summary>
    /// ОКНА ПИКА, ВЗЯТЫЕ СО СТРАНИЦЫ ЦЕН, — строкой JSON (<see cref="Pricing.PricingWindows"/>).
    /// Пусто — расписание берётся из профиля агента, и это умолчание.
    ///
    /// Здесь лежит ВТОРОЙ источник расписания, и он появился не «на всякий случай»: кнопка
    /// «Обновить информацию» в окне «Пики и тарифы» разбирает страницу цен, а та публикует
    /// и окна пика. Панель 1.x правило называла прямо: «окна применяются только по подтверждению,
    /// цены — показ». Подтверждение — щелчок человека по этой кнопке; автоматический разбор
    /// (раз в сутки) окон НЕ берёт и расписание не меняет.
    ///
    /// ⚠️ Значение — след работы панели, а не настройка: в окне оно не показывается полем
    /// и через «Сохранить» проходит нетронутым (как <see cref="AdoptedServerPort"/>).
    /// </summary>
    [JsonPropertyName("pricingPeakWindows")]
    public string PricingPeakWindows { get; set; } = string.Empty;

    /// <summary>
    /// ПОСЛЕДНЯЯ ПРОЧИТАННАЯ ТАБЛИЦА ЦЕН — строкой JSON (<see cref="Pricing.PricingMemory"/>).
    /// Пусто — панель цены ещё не читала.
    ///
    /// **Зачем в файле.** Разбор страницы закрыт суточным гейтом: разобрали сегодня — второй раз
    /// сегодня не пойдём. До 28.09.2026 в файле лежала только отметка времени, а самих цен не было
    /// нигде, поэтому после перезапуска панели таблица «Стоимость» стояла ПУСТОЙ до нажатия
    /// «Обновить информацию»: гейт закрыт (разбора не будет), память процесса пуста (показывать
    /// нечего). Владелец описал это словами: *«сами тарифы не сохраняются: обновлял вчера, сегодня
    /// снова пусто, только пики сохранены»* — окна пика как раз лежали в файле
    /// (<see cref="PricingPeakWindows"/>), а цены нет.
    ///
    /// ⚠️ Это след работы панели, а не настройка человека: в окне оно полем не показывается
    /// и через «Сохранить» проходит НЕТРОНУТЫМ — ровно как согласие на сервер, окна пика и следы
    /// обновления. Иначе первое же нажатие «Сохранить» в любом разделе стёрло бы прочитанные цены.
    /// </summary>
    [JsonPropertyName("pricingLast")]
    public string PricingLast { get; set; } = string.Empty;

    /// <summary>
    /// КОГДА выпуски панели проверялись в последний раз — круговая дата-время ТОГО ЖЕ формата,
    /// что у цен (<see cref="Pricing.PricingDecisions.Stamp"/>). Пусто — никогда.
    ///
    /// Зачем в файле, а не в памяти: решение владельца — проверка идёт «при запуске панели,
    /// но НЕ чаще раза в сутки». Отметка в памяти означала бы проверку при КАЖДОМ запуске,
    /// то есть «чаще раза в сутки» ровно настолько, насколько часто человек открывает панель.
    ///
    /// ⚠️ Формат берётся у цен, а не пишется здесь заново: вторая реализация формата однажды
    /// разошлась бы с первой, и отметки перестали бы читаться.
    /// </summary>
    [JsonPropertyName("updateCheckedAt")]
    public string UpdateCheckedAt { get; set; } = string.Empty;

    /// <summary>
    /// ПОСЛЕДНЯЯ УВИДЕННАЯ ВЕРСИЯ выпуска (тег как он пришёл от GitHub). Ею закрыто правило
    /// «шарик один раз на версию»: проверка идёт при каждом запуске панели, и без этой записи
    /// человек получал бы один и тот же шарик каждый день, пока не обновится.
    ///
    /// ⚠️ Это след работы панели, а не настройка человека: в окне настроек полем не показывается
    /// и через «Сохранить» проходит НЕТРОНУТЫМ — как согласие на найденный сервер и след цен.
    /// </summary>
    [JsonPropertyName("updateLatest")]
    public string UpdateLatest { get; set; } = string.Empty;

    /// <summary>Когда выпуск опубликован (как назвал GitHub) — показывается человеку словами.</summary>
    [JsonPropertyName("updatePublished")]
    public string UpdatePublished { get; set; } = string.Empty;

    /// <summary>Адрес страницы выпуска — по нему человек читает заметки целиком.</summary>
    [JsonPropertyName("updatePageUrl")]
    public string UpdatePageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Заметки к выпуску НА ЯЗЫКЕ ПАНЕЛИ — уже выбранный блок (см.
    /// <see cref="Update.UpdateDecisions.PickNotes"/>).
    /// </summary>
    [JsonPropertyName("updateNotes")]
    public string UpdateNotes { get; set; } = string.Empty;

    /// <summary>
    /// СЫРОЕ тело выпуска, каким оно пришло от GitHub, — все языковые блоки сразу.
    ///
    /// Хранится рядом с выбранным блоком намеренно: **язык панели меняется**, и заметки обязаны
    /// пересобраться на новом языке, а не ждать следующей проверки (так же устроена панель 1.x,
    /// см. <c>dsh-tray\UpdateService.RawNotes</c>). Выбранного блока для этого мало.
    /// </summary>
    [JsonPropertyName("updateNotesRaw")]
    public string UpdateNotesRaw { get; set; } = string.Empty;

    /// <summary>
    /// ВЕРСИЯ, КОТОРУЮ ЧЕЛОВЕК ПРОПУСТИЛ. Пусто — не пропускал ничего.
    ///
    /// Это ЕДИНСТВЕННОЕ поле обновления, которое человек меняет сам (пункт «пропустить эту
    /// версию»), и потому оно названо решением, а не следом работы: панель обязана молчать
    /// о пропущенном выпуске и тогда, когда он всё ещё новее установленной панели.
    /// Забывается пропуск просто: следующий выпуск новее — и о нём скажут.
    /// </summary>
    [JsonPropertyName("updateSkippedVersion")]
    public string UpdateSkippedVersion { get; set; } = string.Empty;

    /// <summary>
    /// Размер и положение ГЛАВНОГО окна: X, Y, ширина, высота — в пикселях экрана.
    /// Умолчания НУЛЕВЫЕ, и это значит «не задано»: при первом запуске окно берёт размер
    /// из разметки (860x720), а место — по центру главного экрана.
    ///
    /// Решение владельца 26.09.2026: окно, которое человек поставил и растянул, обязано
    /// открыться там же. Раньше оно каждый запуск вставало по умолчанию, и это выглядело
    /// как «панель забыла, где я её оставил».
    ///
    /// ⚠️ Умолчания обязаны оставаться НУЛЕВЫМИ: ноль здесь значит «не задано», и это то самое
    /// слово, по которому окно берёт размер из разметки, а место — по центру. Ненулевое умолчание
    /// означало бы «человек уже выбрал», и вернуться к «не задано» он не смог бы.
    /// (До 26.09.2026 здесь стояло ещё одно основание — проверка «в файле настроек нет числа 3080»;
    /// она снята вместе с запретом порта владельца: 3080 теперь УМОЛЧАНИЕ порта, и в файле он есть.)
    ///
    /// Приводит значения к сравнимому виду <see cref="SettingsStore.Clean"/> — единственная
    /// дверь настроек; что именно считается мусором, решает <see cref="Shell.WindowPlacement"/>:
    /// отрицательная координата там ЗАКОННА (второй монитор слева), а отрицательный размер —
    /// нет. Здесь только поля; правило живёт в одном месте.
    /// </summary>
    [JsonPropertyName("windowX")]
    public int WindowX { get; set; }

    /// <inheritdoc cref="WindowX" />
    [JsonPropertyName("windowY")]
    public int WindowY { get; set; }

    /// <inheritdoc cref="WindowX" />
    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; }

    /// <inheritdoc cref="WindowX" />
    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; }

    /// <summary>Границы, за которые настройку не пускают: файл правят руками, а таймер — не место для чудес.</summary>
    public const int RefreshMinutesMin = 1;

    public const int RefreshMinutesMax = 1440;

    public const int PeakNotifyMinutesMax = 1440;

    /// <summary>
    /// Границы расписания копий: от часа до недели. Числа взяты те же, что у периодичности баланса
    /// по смыслу («файл правят руками, а таймер — не место для чудес»), но полоса своя: чаще раза
    /// в час копия не нужна (она стоит времени и места), реже раза в неделю — это уже «копий нет».
    /// </summary>
    public const int BackupHoursMin = 1;

    public const int BackupHoursMax = 168;

    /// <summary>
    /// Границы хранения копий: хотя бы одна (иначе хранить нечего) и не больше двадцати.
    /// Двадцать — не «технический предел», а граница здравого смысла: полные копии бывают
    /// в сотни мегабайт, и двадцать — это уже десятки гигабайт на диске человека.
    /// </summary>
    public const int BackupKeepMin = 1;

    public const int BackupKeepMax = 20;

    public static int ClampRefreshMinutes(int minutes) => Math.Clamp(minutes, RefreshMinutesMin, RefreshMinutesMax);

    public static int ClampPeakNotify(int minutes) => Math.Clamp(minutes, 0, PeakNotifyMinutesMax);

    public static int ClampBackupHours(int hours) => Math.Clamp(hours, BackupHoursMin, BackupHoursMax);

    public static int ClampBackupKeep(int count) => Math.Clamp(count, BackupKeepMin, BackupKeepMax);

    /// <summary>
    /// Режим объёма в сравнимом виде. Решает <see cref="Backup.BackupScopeDecisions.Normalize"/>
    /// — там же и довод: непонятное значение из файла означает <c>auto</c>, а не третий режим.
    /// Здесь только дверь, чтобы у настройки было одно место правды.
    /// </summary>
    public static string NormalizeBackupScope(string? value) => Backup.BackupScopeDecisions.Normalize(value);

    /// <summary>
    /// Лишние имена папок в сравнимом виде (без пустых, без повторов, регистр не важен) —
    /// тем же правилом, что каталоги ключей: оба списка человек пишет руками в одну строку.
    /// </summary>
    public static List<string> NormalizeExtraExclusions(IEnumerable<string>? names) =>
        Backup.BackupScopeDecisions.NormalizeExclusions(names);

    public static decimal ClampThreshold(decimal value) => value < 0 ? 0 : value;

    public static PanelSettings Default => new();

    /// <summary>Порядок тем в списке окна настроек — он же порядок значений в файле.</summary>
    public static readonly string[] ThemeValues = { ThemeSystem, ThemeLight, ThemeDark };

    /// <summary>
    /// Порядок языков в списке окна настроек — он же порядок значений в файле. Список берётся
    /// у чистых решений (<see cref="Localization.LanguageDecisions.Known"/>), чтобы «язык, который
    /// предлагает окно» и «язык, который понимает словарь» не разошлись.
    /// </summary>
    public static readonly string[] LanguageValues =
    {
        Localization.LanguageDecisions.Auto,
        "ru",
        Localization.LanguageDecisions.English,
        "zh",
    };

    /// <summary>
    /// Приводит язык к известному значению. Неизвестное (в том числе пустое и мусор) — «как в системе»:
    /// файл настроек человек правит руками, и панель обязана это пережить, а не остаться без строк.
    /// </summary>
    public static string NormalizeLanguage(string? value) => Localization.LanguageDecisions.Normalize(value);

    /// <summary>Номер языка в списке — по нему окно ставит выбор, не сравнивая строки.</summary>
    public static int LanguageIndex(string? value)
    {
        var normalized = NormalizeLanguage(value);
        var index = Array.IndexOf(LanguageValues, normalized);
        return index < 0 ? 0 : index;
    }

    /// <summary>Значение языка по номеру. Номер вне списка даёт «как в системе», а не исключение.</summary>
    public static string LanguageAt(int index) =>
        index >= 0 && index < LanguageValues.Length ? LanguageValues[index] : Localization.LanguageDecisions.Auto;

    /// <summary>
    /// Приводит тему к известному значению. Неизвестное (в том числе пустое и мусор) — «как в Windows»:
    /// файл настроек человек может править руками, и панель обязана это пережить, а не упасть.
    /// </summary>
    public static string NormalizeTheme(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();

        return text switch
        {
            ThemeLight => ThemeLight,
            ThemeDark => ThemeDark,
            _ => ThemeSystem,
        };
    }

    /// <summary>
    /// Приводит рабочую папку к сравнимому виду: без хвостового разделителя и лишних пробелов.
    /// Пусто остаётся пустым — это и означает «папка панели».
    /// </summary>
    public static string NormalizeWorkDir(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? string.Empty : Path.TrimEndingDirectorySeparator(text);
    }

    /// <summary>
    /// Порт запомненного согласия в сравнимом виде: настоящий порт остаётся собой (в том числе
    /// порт владельца — его панель как раз и берёт под управление), а мусор и пустое значение
    /// становятся <c>0</c>, то есть «согласия нет».
    ///
    /// Отдельный предикат, а не <see cref="ServerDecisions.NormalizePort"/>: тот приводит порт
    /// к тому, который панель вправе ЗАНЯТЬ, и 3080 превратил бы в 3081 — то есть согласие
    /// на сервер владельца молча перестало бы работать, и панель спрашивала бы снова.
    /// </summary>
    public static int NormalizeAdoptedPort(int port) => port is > 0 and < 65536 ? port : 0;

    /// <summary>
    /// Порт СВОЕГО сервера в сравнимом виде — дверь для файла настроек и окна.
    ///
    /// Право «занять порт владельца» здесь берётся ВЫДАННЫМ (<c>mayOccupyOwnerPort: true</c>),
    /// и это осознанно: настройки принадлежат человеку, и любой настоящий порт в них законен —
    /// включая 3080, который теперь и есть умолчание. Решает не эта дверь, а та, где порт
    /// ДЕЙСТВИТЕЛЬНО занимают: <see cref="Server.ServerController"/> спрашивает право
    /// у <see cref="Isolation.RunRights.MayOccupyOwnerPort"/> и без права приводит порт
    /// к <see cref="ServerDecisions.RunFallbackPort"/>. Прогон проверки настроек не пишет вовсе
    /// (<c>ISettingsControl.Writable</c>), поэтому «прогон записал себе порт владельца» невозможен.
    /// </summary>
    public static int NormalizeServerPort(int port) =>
        Server.ServerDecisions.NormalizePort(port, mayOccupyOwnerPort: true);

    /// <summary>
    /// Папка копий в сравнимом виде: без лишних пробелов. Пусто остаётся пустым — это и означает
    /// «умолчание v1». Само умолчание здесь НЕ подставляется: значение, подставленное в файл,
    /// перестало бы быть умолчанием, и вернуться к нему человек уже не смог бы.
    /// </summary>
    public static string NormalizeFolder(string? value) => (value ?? string.Empty).Trim();

    /// <summary>
    /// Каталоги ключей в сравнимом виде: без пустых, без лишних пробелов и без повторов
    /// (Windows не различает регистр в путях). Порядок СОХРАНЯЕТСЯ: он задаёт номера групп
    /// в архиве (<c>keys/1-ssh</c>, <c>keys/2-…</c>), и перестановка меняла бы раскладку
    /// уже снятых копий.
    /// </summary>
    public static List<string> NormalizeKeyDirs(IEnumerable<string>? directories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var directory in directories ?? Enumerable.Empty<string>())
        {
            var text = (directory ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            if (!seen.Add(text)) continue;

            result.Add(text);
        }

        return result;
    }

    public static PanelTheme ToTheme(string? value) => NormalizeTheme(value) switch
    {
        ThemeLight => PanelTheme.Light,
        ThemeDark => PanelTheme.Dark,
        _ => PanelTheme.System,
    };

    public static string ThemeValue(PanelTheme theme) => theme switch
    {
        PanelTheme.Light => ThemeLight,
        PanelTheme.Dark => ThemeDark,
        _ => ThemeSystem,
    };

    /// <summary>Номер темы в списке — по нему окно ставит выбор, не сравнивая строки.</summary>
    public static int ThemeIndex(string? value)
    {
        var normalized = NormalizeTheme(value);
        var index = Array.IndexOf(ThemeValues, normalized);
        return index < 0 ? 0 : index;
    }

    /// <summary>Значение темы по номеру. Номер вне списка даёт «как в Windows», а не исключение.</summary>
    public static string ThemeAt(int index) =>
        index >= 0 && index < ThemeValues.Length ? ThemeValues[index] : ThemeSystem;
}
