using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DshPanel.Agents;
using DshPanel.Backup;
using DshPanel.Balance;
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;

namespace DshPanel.Shell;

/// <summary>
/// ОБРАЗЦОВЫЕ ДАННЫЕ ДЛЯ КАДРОВ — ВЫДУМАННЫЕ И ОБЕЗЛИЧЕННЫЕ, В ОДНОМ МЕСТЕ.
///
/// **Зачем.** До 12.10.2026 `--shot` снимал окна **пустыми**: прогон проверки не читает данные
/// владельца, поэтому в кадрах не было ни таблиц, ни списков, ни чисел — по такому кадру нельзя
/// судить вид окна (владелец ткнул в это 11.10.2026, увидев пустую «Историю цен»), а в публичную
/// витрину уезжали кадры со словами «прогон проверки» и «баланс не читается».
///
/// **Правило первое и неотменяемое:** образцовые данные — **выдуманные** и **обезличенные**.
/// Ни баланса владельца, ни его адресов, ни имени пользователя, ни путей его каталогов: пути —
/// через маску (`~`), адреса — как в разделе «Телефон» (`192.168.0.х`).
///
/// ⚠️ **Это НЕ чтение данных владельца и не подмена их в обычной жизни.** Значения ниже —
/// литералы; ни один из них не берётся с машины. Подставные двери (<see cref="ShotDrawers"/>)
/// живут только в ветви съёмки `Program.Shot` и не выдаются ни обычному запуску, ни прогону
/// проверки: правило изоляции не ослаблено ни на шаг.
/// </summary>
public static class ShotSamples
{
    /// <summary>Порт своего сервера на кадре: тот, что панель занимает на самом деле.</summary>
    public const int Port = ServerDecisions.DefaultServerPort;

    /// <summary>
    /// Номер процесса — ВЫДУМАННЫЙ. Настоящего в кадре быть не может: он часть данных машины,
    /// а кадр уезжает в публичную витрину.
    /// </summary>
    public const int Pid = 4242;

    /// <summary>Имя процесса: у движка это всегда `node.exe`, и оно ничего личного не несёт.</summary>
    public const string Process = "node.exe";

    /// <summary>Рабочая папка — под маской, как её показывает панель человеку.</summary>
    public const string WorkDir = "~";

    /// <summary>Домашний каталог движка — под маской.</summary>
    public const string DshHome = "~\\.dsh";

    /// <summary>Файл настроек панели — под маской.</summary>
    public const string SettingsFile = "~\\AppData\\Local\\DshPanel2\\settings.json";

    /// <summary>Каталог файла настроек — под маской; его и открывает действие строки на кадре.</summary>
    public const string SettingsFolder = "~\\AppData\\Local\\DshPanel2";

    /// <summary>Папка копий — под маской.</summary>
    public const string BackupFolder = "~\\DshBackups";

    /// <summary>Каталог движка — под маской; версия настоящая, потому что она — не личное.</summary>
    public const string EngineDir = "~\\AppData\\Local\\DshPanel2\\engine\\0.1.5-rc.2";

    /// <summary>
    /// ПУТЬ ЗАПИСИ АВТОЗАПУСКА НА КАДРЕ — под маской, как его показывает панель человеку
    /// (<c>Shell\DisplayMask</c>). Настоящий путь в кадре был бы приметой машины и каталогом
    /// пользователя, а кадр уезжает в публичную витрину (красная линия 7).
    /// </summary>
    public const string AutostartPath = "~\\AppData\\Local\\DshPanel2\\DshPanel.exe";

    /// <summary>Версия движка — та, что панель ставит: «проверена» она по замеру, а не по вкусу.</summary>
    public const string EngineVersion = "0.1.5-rc.2";

    /// <summary>Версия Node: выдуманная, но правдоподобная (порог замка — 22.19.0).</summary>
    public const string NodeVersion = "22.11.0";

    /// <summary>Версия npm: она лежит ВНУТРИ установки Node, и на кадре это видно.</summary>
    public const string NpmVersion = "10.9.0";

    /// <summary>Аренда адреса: выдуманная форма, как её видит человек в разделе «Телефон».</summary>
    public const string PhoneAddress = "192.168.0.х";

    /// <summary>Код сопряжения на кадре — выдуманные шесть цифр. Настоящий в кадр не попадает никогда.</summary>
    public const string PhoneCode = "482913";

    /// <summary>Баланс на кадре — выдуманное число. Настоящий баланс владельца запрещён красной линией 7.</summary>
    public const decimal Balance = 12.34m;

    /// <summary>Валюта баланса.</summary>
    public const string Currency = "USD";

    /// <summary>Когда «проверяли» баланс — выдуманное, но правдоподобное время.</summary>
    public static readonly DateTimeOffset CheckedAt = new(2026, 10, 12, 12, 40, 0, TimeSpan.FromHours(3));

    /// <summary>
    /// ДАТА ОБРАЗЦОВОГО ВЫПУСКА НА КАДРЕ — 17.10.2026, И ЭТО ОДИН ИСТОЧНИК ДЛЯ ВСЕХ КАДРОВ (t13, 18.10.2026).
    ///
    /// **От какого случая.** Владелец увидел на кадре «Обновление» строку «Выпущен: 27.09.2026»
    /// и «проверено 28.09.2026», а выпуск 2.1.0 вышел **17.10.2026**. Кадр витрины называл дату,
    /// которой у выпуска нет: читатель сверил бы её со страницей релиза и решил, что панель не
    /// знает собственного выпуска. Сценарий кадра при этом верен (есть версия новее установленной,
    /// есть дата проверки) — менялись ТОЛЬКО даты, и меняются они здесь, в образцовой двери,
    /// а не текстом окна: окно обязано показывать то, что ему дали.
    ///
    /// Значение — строка в том виде, в каком её отдаёт GitHub (`published_at`): её разбирает
    /// и печатает ОДНО место (<see cref="DshPanel.Update.UpdateLines.PublishedLine"/>).
    /// Время — 10:00 UTC, чтобы при любом поясе от −9 до +13 местная дата оставалась 17.10.2026
    /// (полночь по UTC показала бы 18.10 у нас — и кадр снова соврал бы).
    /// </summary>
    public const string ReleasePublished = "2026-10-24T10:00:00Z";

    /// <summary>
    /// КОГДА ПРОВЕРЯЛИ ОБРАЗЦОВЫЙ ВЫПУСК — 18.10.2026, время то же, что стояло в образце
    /// (12:00 UTC, то есть 15:00 по машине владельца). Дата выпуска — 17.10, проверка — на
    /// следующий день: ровно так и было в цикле 2.1.0.
    /// </summary>
    public static readonly DateTimeOffset ReleaseCheckedAt = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// СНИМОК ЦЕН ДЛЯ КАДРА «ПИКИ И ТАРИФЫ» — ЧИСЛА СТРАНИЦЫ ЦЕН (U16, 2.4.0).
    ///
    /// ⚠️ **От какого случая.** Витрина рисовала выдуманную модель `deepseek-chat` и старые числа
    /// ($0.25/$0.50), а с 24.10.2026 страница цен даёт `deepseek-flash` и $0.003/$0.006, $0.15/$0.3,
    /// $0.6/$1.2 (фикстура `tests/DshPanel.Tests/fixtures/pricing-en.html`). Кадр витрины — не
    /// разбор страницы, но он ОБЯЗАН называть те же числа и ту модель, что видит человек.
    /// Модель берётся у профиля агента (`AgentCatalog.DeepSeek.PricingModel`), а не литералом:
    /// второго имени модели в кадре быть не должно.
    ///
    /// **От какого случая.** Владелец увидел на кадре «пиков» ПУСТОЙ блок «Стоимость» со словами
    /// «Цены не читались: это прогон проверки — в сеть панель не ходит»: кадр показывал нашу
    /// механику вместо вида продукта. Окно цен ЖДЁТ дверь (<c>IPricingControl</c>) и рисует
    /// таблицу по ней — значит лечение и есть образцовая дверь, а окно не меняется
    /// (<see cref="ShotDrawers.Pricing"/>). Пары те же, что видит человек в «Истории цен»:
    /// числа страницы цен: $0.003/$0.006, $0.15/$0.3, $0.6/$1.2.
    /// </summary>
    public static PricingSnapshot PricingSnapshotSample() =>
        Snapshot(
            ReleaseCheckedAt,
            ("1M INPUT TOKENS (CACHE HIT)", "$0.003", "$0.006"),
            ("1M INPUT TOKENS (CACHE MISS)", "$0.15", "$0.3"),
            ("1M OUTPUT TOKENS", "$0.6", "$1.2"));

    /// <summary>
    /// НАСТРОЙКИ ДЛЯ КАДРА: рабочие значения, а не умолчания.
    ///
    /// ⚠️ **Почему не <c>PanelSettings.Default</c>** (а это и был прежний кадр): у умолчаний
    /// у порога баланса ноль, у расписания копий — «не выбрано», у темы — «как в Windows».
    /// Кадр с умолчаниями читается человеком как «панель ничего не умеет» — ровно на это владелец
    /// и жаловался, глядя на витрину. Значения ниже — те, что человек увидит у себя, поработав
    /// с панелью день.
    /// </summary>
    public static PanelSettings Settings() => new()
    {
        ServerWorkingDir = WorkDir,
        ServerPort = Port,
        ServerPortChosen = true,
        Theme = PanelSettings.ThemeSystem,
        Language = LanguageDecisions.Auto,
        ActiveAgent = AgentCatalog.DefaultId,

        BalanceAutoRefresh = true,
        BalanceRefreshMinutes = 2,
        BalanceWarnEnabled = true,
        BalanceWarnThreshold = 15,
        PeakNotifyMinutes = 1,

        BackupFolder = BackupFolder,
        BackupScheduleEnabled = true,
        BackupEveryHours = 24,
        BackupKeepCount = 2,
        BackupScope = BackupScopeDecisions.Auto,
        BackupWithKeys = true,

        PhoneEnabled = true,
        PhonePort = PhonePass.DefaultPort,
        PhonePortChosen = true,
        PhoneLastAddress = PhoneAddress,
        PhoneLastSeen = PricingDecisions.Stamp(CheckedAt),
        PhoneConsentAt = PricingDecisions.Stamp(CheckedAt),
    };

    /// <summary>
    /// СОСТОЯНИЕ СЕРВЕРА НА КАДРЕ: работает, наш, на своём порту.
    ///
    /// ⚠️ **Живой сервер владельца в кадр не попадает и попасть не может:** проба процессов
    /// на съёмке не подставляется (кроме кадра «два найденных сервера», где она выдумана),
    /// а это состояние — литерал. И <c>Detail</c> здесь НЕ «порт свободен»: сервер работает,
    /// и строка состояния обязана говорить то же, что заголовок (иначе кадр противоречит сам себе).
    /// </summary>
    public static ServerState Server() => new(
        ServerPresence.Running,
        Port,
        Pid,
        Process,
        string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAnswersFormat, Port));

    /// <summary>Запись о найденном сервере для кадра «два найденных сервера» — выдуманная.</summary>
    public static FoundServer Found(int port, int pid, string? ownVersion = null) =>
        new(port, pid, Process, ownVersion is null ? string.Empty : EngineDir, ownVersion ?? string.Empty);

    /// <summary>
    /// БАЛАНС НА КАДРЕ — прочитанный, с числом и подробностью.
    ///
    /// ⚠️ <c>Summary</c> здесь ОБРАЗЦОВАЯ СТРОКА, а не перевод словаря: её собирает контроллер
    /// баланса, и своей копии формата у съёмки быть не должно (разойдясь, они показали бы разное).
    /// Число выдумано; настоящий баланс владельца в кадр не попадает никогда (красная линия 7).
    /// </summary>
    public static BalanceResult BalanceResult() => new(
        Ok: true,
        Available: true,
        Total: Balance,
        Currency: Currency,
        Summary: Balance.ToString("0.00", CultureInfo.InvariantCulture) + " " + Currency,
        Detail: string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.BalanceCheckedFormat,
            PricingDecisions.CheckedText(PricingDecisions.Stamp(CheckedAt))),
        Error: string.Empty,
        CheckedAt: CheckedAt,
        AgentId: AgentCatalog.DefaultId);

    /// <summary>Состояние тарифа — считается ЧИСТОЙ функцией по расписанию агента и моменту.</summary>
    public static PeakState Peak() => PeakDecisions.State(AgentCatalog.DeepSeek, CheckedAt);

    /// <summary>
    /// ТЕЛЕФОН НА КАДРЕ: разрешён, калитка слушает, два устройства, живой код и адрес под маской.
    ///
    /// ⚠️ **Устройства выдуманы, и внутренние номера тут НЕ показываются** — их место занимает
    /// короткое имя телефона (решение владельца 12.10.2026). Настоящий пропуск в кадр не попадает:
    /// дверь съёмки — литералы, файла пропуска у неё нет вовсе.
    /// </summary>
    /// ⚠️ **Отметки времени — ЧИТАЕМЫЕ, а не машинные** (`CheckedText`, «12.10.2026, 12:40»):
    /// до 13.10.2026 здесь стоял машинный штамп `Stamp` (`2026-10-12T09:40:00.0000000+00:00`),
    /// и на витрине выпуска он был виден человеку прямо в строке состояния.
    public static PhoneSectionInfo Phone() => new(
        Allowed: true,
        Listening: true,
        DeviceCount: 2,
        Devices: new[]
        {
            new PhoneDeviceView("phone-1", "12.10.2026, 11:02", "12.10.2026, 12:40", "Pixel 8"),
            new PhoneDeviceView("phone-2", "12.10.2026, 11:31", "12.10.2026, 12:12", string.Empty),
        },
        LiveCode: true,
        ConsentText: PricingDecisions.CheckedText(PricingDecisions.Stamp(CheckedAt)),
        LastAddress: PhoneAddress,
        ChosenAddress: string.Empty,
        LastSeenText: PricingDecisions.CheckedText(PricingDecisions.Stamp(CheckedAt)));

    /// <summary>
    /// ОТЧЁТ ОКРУЖЕНИЯ НА КАДРЕ — КАРТОЧКАМИ, ПУТИ ПОД МАСКОЙ, СОСТОЯНИЯ МЕТКАМИ.
    ///
    /// Форма — та же, что у человека (решение владельца 14.10.2026): карточка умений со строкой
    /// «Живая проба умений» и карточка находок с метками состояний и действием строки у папок.
    ///
    /// ⚠️ **Путь здесь уже под маской** (`~`): на экране раздел показывает ИМЕННО ту подробность,
    /// что лежит в <see cref="EnvironmentLine.Detail"/>, а настоящую папку несёт
    /// <see cref="EnvironmentLine.Folder"/> — она наружу не выводится ничем. Для кадра оба пути
    /// под маской: имя пользователя в кадр не попадает (красная линия 7).
    ///
    /// ⚠️ **Умения на кадре — смешанные НАРОЧНО** (подтверждено · не работает · не прочитана ·
    /// не проверялась): по кадру одного цвета нельзя судить ни о метке, ни о её контрасте.
    /// </summary>
    public static EnvironmentReport Environment() => new(new[]
    {
        new EnvironmentGroup(
            PanelStrings.EnvSkillsTitle,
            new[]
            {
                Skill(PanelStrings.EnvSkillEntryLink, PanelStrings.EnvSkillChecked, EnvLineState.Ok, PanelStrings.EnvSkillHowLive),
                Skill(PanelStrings.EnvSkillSessionList, PanelStrings.EnvSkillChecked, EnvLineState.Ok, PanelStrings.EnvSkillHowLive),
                Skill(PanelStrings.EnvSkillQuestionAnswer, PanelStrings.EnvSkillChecked, EnvLineState.Ok, PanelStrings.EnvSkillHowLive),
                // ⚠️ У постановки задачи и отмены метка — «не поддерживается сервером», а НЕ
                // «не работает» (DEF-048, D-9): сервер такого вызова не объявляет, чинить нечего.
                // Слово берётся тем же ключом, каким его назовёт `SkillPhrases.Word` в живом окне.
                Skill(PanelStrings.EnvSkillTaskPrompt, PanelStrings.EnvSkillNotDeclared, EnvLineState.Error, PanelStrings.EnvSkillTaskPromptNo),
                Skill(PanelStrings.EnvSkillTaskCancel, PanelStrings.EnvSkillNotDeclared, EnvLineState.Error, PanelStrings.EnvSkillTaskCancelNo),
                Skill(PanelStrings.EnvSkillModelKey, PanelStrings.EnvSkillChecked, EnvLineState.Ok, PanelStrings.EnvSkillHowFile),
                Skill(PanelStrings.EnvSkillBackupLayout, PanelStrings.EnvSkillChecked, EnvLineState.Ok, PanelStrings.EnvSkillHowFile),
                Skill(PanelStrings.EnvSkillEngineVersion, PanelStrings.EnvSkillVersionUnread, EnvLineState.Warning, PanelStrings.EnvSkillEngineVersionNo),
                new EnvironmentLine(PanelStrings.EnvSkillsProbe)
                {
                    State = EnvLineState.Grey,
                    Word = PanelStrings.EnvSkillNotChecked,
                    Detail = PanelStrings.EnvSkillsMomentNotAsked,
                },
            },
            PanelStrings.EnvSkillsSubtitle),

        new EnvironmentGroup(
            PanelStrings.EnvFoundTitle,
            new[]
            {
                Found(PanelStrings.EnvEngine, EnvLineState.Ok, PanelStrings.EnvFound,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.EnvVersionFormat, EngineVersion)),

                Found(PanelStrings.EnvNode, EnvLineState.Ok, PanelStrings.EnvFound,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.EnvVersionFormat, NodeVersion)),

                Found(PanelStrings.EnvNpm, EnvLineState.Ok, PanelStrings.EnvFound,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.EnvVersionFormat, NpmVersion)),

                Found(PanelStrings.EnvPnpm, EnvLineState.Grey, PanelStrings.EnvNotFound, PanelStrings.EnvPnpmUnused),

                Folder(PanelStrings.EnvDshHome, PanelStrings.EnvFoundP, DshHome),

                new EnvironmentLine(PanelStrings.EnvWorkDir)
                {
                    State = EnvLineState.Ok,
                    Word = PanelStrings.EnvFoundF,
                    Detail = string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.EnvValueDetailFormat, WorkDir, PanelStrings.EnvFromPanel),
                    Folder = WorkDir,
                },

                Folder(PanelStrings.EnvSettingsFile, PanelStrings.EnvFound, SettingsFolder),

                new EnvironmentLine(PanelStrings.EnvServerPort)
                {
                    Detail = string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.EnvValueDetailFormat, Port, PanelStrings.EnvPortOwn),
                },
            },
            PanelStrings.EnvFoundSubtitle),
    });

    /// <summary>Строка умения на кадре: имя, метка состояния со словом и короткая подробность.</summary>
    private static EnvironmentLine Skill(string name, string word, EnvLineState state, string detail) =>
        new(name) { State = state, Word = word, Detail = detail };

    /// <summary>Строка находки с версией: состояние, слово и подробность.</summary>
    private static EnvironmentLine Found(string name, EnvLineState state, string word, string detail) =>
        new(name) { State = state, Word = word, Detail = detail };

    /// <summary>Строка-папка: метка, путь под маской и действие строки, открывающее эту папку.</summary>
    private static EnvironmentLine Folder(string name, string word, string path) =>
        new(name) { State = EnvLineState.Ok, Word = word, Detail = path, Folder = path };

    /// <summary>
    /// ИСТОРИЯ ЦЕН НА КАДРЕ — ЧЕТЫРЕ ЗАПИСИ, И ОДНА ИЗ НИХ С ИЗМЕНЕНИЕМ ЦЕНЫ.
    ///
    /// **От какого случая.** До 12.10.2026 кадр «История цен» показывал **одни прочерки**: файла
    /// истории прогону проверки не выдают вовсе (`PricingHistoryFiles.None`), и сравнивать было
    /// не с чем. По такому кадру нельзя судить ни о списке, ни о таблице изменений — а он уезжает
    /// в публичную витрину.
    ///
    /// Возвращается **ГОТОВАЯ СТРОКА ФАЙЛА** — тем же кодом, каким её пишет панель
    /// (<see cref="PricingHistoryStore.Encode"/>), а не «похожий JSON»: подставь мы свою форму,
    /// кадр показывал бы не то, что видит человек.
    ///
    /// ⚠️ **История живая на запись только в памяти:** чтение отдаёт эту строку, запись кладёт
    /// в переменную прогона. Ни одного файла владельца съёмка не касается.
    /// </summary>
    public static string PricingHistoryText()
    {
        var anchor = Snapshot(
            new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(3)),
            ("1M INPUT TOKENS (CACHE HIT)", "$0.004", "$0.008"),
            ("1M INPUT TOKENS (CACHE MISS)", "$0.20", "$0.40"),
            ("1M OUTPUT TOKENS", "$0.80", "$1.60"));

        var first = Change(
            new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.FromHours(3)),
            new[] { new PriceChangeRow("1M INPUT TOKENS (CACHE HIT)", "$0.004", "$0.003", "$0.008", "$0.006") });

        var second = Change(
            new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.FromHours(3)),
            new[] { new PriceChangeRow("1M INPUT TOKENS (CACHE MISS)", "$0.20", "$0.15", "$0.40", "$0.3") });

        var third = Change(
            new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(3)),
            new[] { new PriceChangeRow("1M OUTPUT TOKENS", "$0.80", "$0.6", "$1.60", "$1.2") });

        return PricingHistoryStore.Encode(new PricingHistory(anchor, new[] { first, second, third }, 0));
    }

    /// <summary>Снимок таблицы цен — выдуманный, с окнами пика агента по умолчанию.</summary>
    private static PricingSnapshot Snapshot(DateTimeOffset at, params (string Item, string OffPeak, string Peak)[] rows) =>
        new(
            at,
            PricingDecisions.Url(LanguageDecisions.Auto),
            LanguageDecisions.Auto,
            AgentCatalog.DeepSeek.PricingModel,
            rows.Select(row => new PriceRow(row.Item, row.OffPeak, row.Peak)).ToList(),
            AgentCatalog.DeepSeek.PeakWindows);

    /// <summary>Запись об изменении — выдуманная: окна те же, меняются только строки цены.</summary>
    private static PricingChange Change(DateTimeOffset at, IReadOnlyList<PriceChangeRow> prices)
    {
        var windows = AgentCatalog.DeepSeek.PeakWindows;

        return new PricingChange(
            at,
            PricingDecisions.Url(LanguageDecisions.Auto),
            LanguageDecisions.Auto,
            AgentCatalog.DeepSeek.PricingModel,
            prices,
            windows,
            windows);
    }

    /// <summary>
    /// ТРИ ОБРАЗЦОВЫЕ КОПИИ для окна «Копии»: полная, полная и снятая перед накатом.
    /// ⚠️ Тип — НАСТОЯЩИЙ (<see cref="BackupEntry"/>), а не своя запись на месте: список копий
    /// раскладывает ОДИН разбор (<c>Views\BackupTableView</c>), и подставлять в него чужую форму
    /// значило бы проверять не тот вид, который увидит человек. Имя файла — выдуманное, путь —
    /// под маской, размер и число файлов — тоже выдуманы.
    /// </summary>
    public static IReadOnlyList<BackupEntry> Backups() => new[]
    {
        new BackupEntry
        {
            Path = BackupFolder + "\\dsh2-backup-2026-10-12-0300.zip",
            CreatedAt = new DateTime(2026, 10, 12, 3, 0, 0),
            Bytes = 128_450_560,
            Files = 5_412,
            WithEngine = true,
            WithSessions = true,
            WithCredentials = true,
            Readable = true,
            EngineVersion = EngineVersion,
        },
        new BackupEntry
        {
            Path = BackupFolder + "\\dsh2-backup-2026-10-11-0300.zip",
            CreatedAt = new DateTime(2026, 10, 11, 3, 0, 0),
            Bytes = 127_910_144,
            Files = 5_398,
            WithEngine = true,
            WithSessions = true,
            Readable = true,
            EngineVersion = EngineVersion,
        },
        new BackupEntry
        {
            Path = BackupFolder + "\\dsh2-before-restore-2026-10-10-2145.zip",
            CreatedAt = new DateTime(2026, 10, 10, 21, 45, 0),
            Bytes = 126_320_640,
            Files = 5_377,
            WithEngine = true,
            Readable = true,
            EngineVersion = EngineVersion,
        },
    };
}
