using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>Что вышло из чтения файла настроек: сами настройки и честный признак, разобрался ли файл.</summary>
public readonly record struct SettingsLoad(PanelSettings Settings, bool Ok, string Problem);

/// <summary>
/// Хранилище настроек — один файл `settings.json` под корнем прогона
/// (<c>AppPaths.SettingsFile</c>: у человека это `%LOCALAPPDATA%\DshPanel2`, в изолированном
/// прогоне — под его корнем). Путь приходит снаружи, вторым способом его не получить.
///
/// Три свойства, каждое из случая:
///
/// 1. **Файл без BOM.** `.json` с BOM не разбирает Node — на этом уже спотыкались в области
///    (`C:\Harness\AGENTS.md`, правило 9), и панель пишет файлы строго без метки.
/// 2. **Запись через временный файл.** Панель могут выключить в момент записи; тогда человек
///    получил бы обрезанный `settings.json` и потерял все настройки. Пишем рядом, потом переносим.
/// 3. **Мусор в файле не роняет панель.** Файл правят руками; непрочитанный JSON — это
///    «настройки по умолчанию плюс честная строка о причине», а не исключение при старте.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Чтение файла, который правят РУКАМИ, и потому терпимое к регистру ключей (дефект Д7).
    ///
    /// До 26.09.2026 здесь стояло только <c>WriteIndented</c>, а <c>System.Text.Json</c> по
    /// умолчанию сверяет имена свойств С УЧЁТОМ регистра. Человек, написавший <c>ServerWorkingDir</c>
    /// вместо <c>serverWorkingDir</c>, получал <c>Ok = true</c> и пустое замечание — то есть
    /// «настройки прочитаны», а значение молча взято умолчанием (на этом уже споткнулся живой
    /// прогон в лаборатории: рабочая папка потерялась). Ключ принимается независимо от регистра,
    /// а чего этот флаг НЕ покрывает — дословно называется замечанием в <see cref="Load"/>.
    /// </summary>
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Где лежит файл — для журнала и отчёта проверки.</summary>
    public string Describe => _path;

    public bool Exists => File.Exists(_path);

    public SettingsLoad Load()
    {
        if (!File.Exists(_path)) return new SettingsLoad(PanelSettings.Default, true, string.Empty);

        try
        {
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text)) return new SettingsLoad(PanelSettings.Default, true, string.Empty);

            var parsed = JsonSerializer.Deserialize<PanelSettings>(text, Reading);
            if (parsed is null) return Bad(PanelStrings.SettingsBadFileEmpty);

            var clean = Clean(parsed);
            return new SettingsLoad(clean, true, Join(OldDefaultPort(parsed, clean), Unknown(text)));
        }
        catch (Exception ex)
        {
            return Bad($"{ex.GetType().Name} — {ex.Message}");
        }

        SettingsLoad Bad(string why) =>
            new(
                PanelSettings.Default,
                false,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.SettingsBadFileFormat,
                    why));
    }

    /// <summary>
    /// Ключи, которых панель не знает: молчание о них и есть дефект Д7 — человек правит файл руками,
    /// пишет ключ по-своему и не узнаёт, что значение не применилось. Неизвестное имя называется
    /// словами, с указанием, какое имя панель знает (сравнение без учёта регистра).
    ///
    /// ⚠️ Настоящие опечатки ЭТИМ не лечатся: ключ <c>serverWrokingDir</c> остаётся неизвестным
    /// и просто называется — «принять любой регистр» и «угадать опечатку» разные вещи.
    /// </summary>
    private static string Unknown(string text)
    {
        List<string> written;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;

            written = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        }
        catch
        {
            // Разобрать не удалось — об этом скажет внешний catch: причину назовёт он, а не это.
            return string.Empty;
        }

        var known = Written();
        var unknown = written
            .Where(name => !known.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unknown.Length == 0) return string.Empty;

        // Строка целиком — из словаря: её читает человек в окне настроек, и она обязана
        // переводиться вместе с остальными. Кавычки-ёлочки вокруг имён — знаки, а не текст.
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.SettingsUnknownKeysFormat,
            string.Join(", ", unknown.Select(name => "«" + name + "»")),
            string.Join(", ", known));
    }

    /// <summary>
    /// Имена ключей, которые панель читает, — взяты у САМОГО типа настроек, а не переписаны
    /// списком: переписанный список однажды разошёлся бы с типом, и новый ключ объявили бы
    /// «неизвестным» (в проекте на таком уже спотыкались — «новое свойство, забытое в подмене»).
    ///
    /// Имя берётся из того же <see cref="JsonPropertyNameAttribute"/>, которым размечен тип:
    /// второго места, где записано имя ключа, не появляется.
    /// </summary>
    private static IReadOnlyList<string> Written() =>
        typeof(PanelSettings)
            .GetProperties()
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Where(name => name.Length > 0)
            .ToArray();

    /// <summary>Два замечания в одну строку: порт и непринятые ключи говорят о разном.</summary>
    private static string Join(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + " · " + right;

    /// <summary>
    /// ПЕРЕНОС СТАРОГО УМОЛЧАНИЯ ПОРТА — и ровно в одном случае.
    ///
    /// До решения владельца 26.09.2026 (ночь) умолчанием 2.0 был порт <b>3081</b>: 3080 значился
    /// запретным, потому что на нём стоял его живой DSH. Теперь владелец убирает панель 1.x и
    /// переезжает на 2.0, и умолчание стало <b>3080</b> — тем адресом, к которому он привык.
    ///
    /// Значит в файлах у всех, кто уже поставил 2.0, лежит 3081, и это НЕ выбор человека:
    /// это умолчание прежней версии. Отличить одно от другого можно только явным признаком
    /// (<see cref="PanelSettings.ServerPortChosen"/>), и он здесь и спрашивается:
    ///
    /// * признак НЕ выставлен и в файле ровно 3081 → порт не выбирали, переводим на 3080
    ///   и ГОВОРИМ об этом строкой (молчаливая подмена порта — это враньё о том, где сервер);
    /// * признак выставлен → человек сохранял настройки, и 3081 в его файле это ЕГО выбор:
    ///   не трогаем. Иначе панель однажды переехала бы с порта, который человек выбрал руками;
    /// * в файле что-то другое → это уже не умолчание прежней версии, и переносить нечего.
    ///
    /// Перенос правит УЖЕ приведённые настройки (<paramref name="clean"/>), а не исходный разбор:
    /// второе место, где порт приводится к виду, завело бы вторую правду о нём.
    /// </summary>
    private static string OldDefaultPort(PanelSettings parsed, PanelSettings clean)
    {
        if (parsed.ServerPortChosen) return string.Empty;
        if (parsed.ServerPort != ServerDecisions.RunFallbackPort) return string.Empty;

        clean.ServerPort = ServerDecisions.DefaultServerPort;

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.SettingsPortMigratedFormat,
            ServerDecisions.RunFallbackPort,
            ServerDecisions.DefaultServerPort);
    }

    public bool Save(PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var text = JsonSerializer.Serialize(Clean(settings), Format);
            var temp = _path + ".tmp";

            // Без BOM — намеренно (см. комментарий класса).
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Приводит настройки к известному виду перед записью и после чтения — в одном месте.</summary>
    public static PanelSettings Clean(PanelSettings settings) => new()
    {
        ServerWorkingDir = PanelSettings.NormalizeWorkDir(settings.ServerWorkingDir),

        // Порт приводится здесь же, и это единственная дверь для файла и окна. Право занять порт
        // владельца берётся выданным: 3080 — теперь УМОЛЧАНИЕ (решение владельца 26.09.2026),
        // а запрет на его занятие прогоном проверки живёт там, где порт занимают, — в контроллере
        // сервера (см. PanelSettings.NormalizeServerPort).
        ServerPort = PanelSettings.NormalizeServerPort(settings.ServerPort),

        // Признак «человек выбирал порт сам» проходит дверь НЕТРОНУТЫМ, и это не мелочь:
        // обнули его Clean — перенос старого умолчания срабатывал бы при каждом чтении, и панель
        // каждый запуск переписывала бы порт, выбранный человеком руками.
        ServerPortChosen = settings.ServerPortChosen,
        Theme = PanelSettings.NormalizeTheme(settings.Theme),

        // Язык — через свою дверь: «de», «русский» и пустое значение означают «как в системе»,
        // а не пустое окно. Файл правят руками, и это не ошибка человека, а обычное дело.
        Language = PanelSettings.NormalizeLanguage(settings.Language),
        ActiveAgent = Agents.AgentCatalog.Find(settings.ActiveAgent).Id,
        BalanceAutoRefresh = settings.BalanceAutoRefresh,
        BalanceRefreshMinutes = PanelSettings.ClampRefreshMinutes(settings.BalanceRefreshMinutes),
        BalanceWarnEnabled = settings.BalanceWarnEnabled,
        BalanceWarnThreshold = PanelSettings.ClampThreshold(settings.BalanceWarnThreshold),
        PeakNotifyMinutes = PanelSettings.ClampPeakNotify(settings.PeakNotifyMinutes),
        BackupFolder = PanelSettings.NormalizeFolder(settings.BackupFolder),

        // Разрешение на ключи и список каталогов — одна дверь на всю панель: и файл, и окно
        // настроек, и окно копий проходят через Clean, поэтому «в файле пусто, а в памяти
        // не пусто» разойтись не может.
        BackupWithKeys = settings.BackupWithKeys,
        BackupKeyDirs = PanelSettings.NormalizeKeyDirs(settings.BackupKeyDirs),

        // ⚠️ МЁРТВОЕ ПОЛЕ «копия для передачи» приводится к false ЗДЕСЬ, и это единственная его
        // дверь. Прежде значение жило в настройках; решением владельца 27.09.2026 (п. 11) передача
        // стала разовым действием на одну копию (окно копий и ключ `--shareable`), и поле осталось
        // только затем, чтобы ключ `backupShareable` в файле человека не объявили незнакомым
        // (см. `PanelSettings.BackupShareable`). Приведение стоит и на чтении, и на записи: одной
        // дороги к «передаваемой копии» из файла настроек не остаётся вовсе.
        BackupShareable = false,

        // Расписание, хранение и режим объёма — тоже через свою дверь, и это важно не «для порядка»:
        // и часовой пояс, и число копий, и режим человек однажды напишет руками. Мусор в часах
        // (0, −5, 100000) не должен превращаться ни в «копия каждую секунду», ни в «копий нет» —
        // поэтому границы, а не доверие файлу.
        BackupScheduleEnabled = settings.BackupScheduleEnabled,
        BackupEveryHours = PanelSettings.ClampBackupHours(settings.BackupEveryHours),
        BackupKeepCount = PanelSettings.ClampBackupKeep(settings.BackupKeepCount),
        BackupScope = PanelSettings.NormalizeBackupScope(settings.BackupScope),
        BackupExtraExclusions = PanelSettings.NormalizeExtraExclusions(settings.BackupExtraExclusions),

        // Запомненное согласие на найденный сервер — через свою дверь: порт 3080 здесь ЗАКОНЕН
        // (на нём стоит сервер владельца, его панель и берёт под управление), а мусор становится
        // нулём, то есть «согласия нет».
        AdoptedServerPort = PanelSettings.NormalizeAdoptedPort(settings.AdoptedServerPort),

        // След работы панели по странице цен — тоже через свою дверь и по той же причине:
        // неразобранная отметка времени означает «никогда» (панель прочитает цены при первом
        // запуске), а битый или нелепый набор окон — «как в профиле агента», а не «расписание
        // пропало». Обе двери живут в Pricing\PricingDecisions.cs, здесь только вызов.
        PricingCheckedAt = Pricing.PricingDecisions
            .TryParse(settings.PricingCheckedAt, out var pricingCheckedAt)
            ? Pricing.PricingDecisions.Stamp(pricingCheckedAt)
            : string.Empty,
        PricingPeakWindows = Pricing.PricingWindows.Normalize(settings.PricingPeakWindows),

        // Последняя прочитанная таблица цен — тем же путём и по той же причине, что окна пика:
        // мусор, битый JSON и запись без строк становятся пустой строкой, то есть «панель ещё
        // не читала цены», а не «цены пропали». Дверь живёт в Pricing\PricingDecisions.cs.
        PricingLast = Pricing.PricingMemory.Normalize(settings.PricingLast),

        // Следы проверки обновления — через ту же дверь отметки времени, что у цен, и по той же
        // причине: неразобранная отметка означает «никогда» (панель проверит выпуски при первом
        // запуске), а не «проверяли только что». Формат берётся у цен, второй его реализации
        // в панели нет.
        UpdateCheckedAt = Pricing.PricingDecisions
            .TryParse(settings.UpdateCheckedAt, out var updateCheckedAt)
            ? Pricing.PricingDecisions.Stamp(updateCheckedAt)
            : string.Empty,

        // Версия, дата, адрес, заметки и сырое тело выпуска проходят дверь КАК ЕСТЬ: у них нет
        // «правильного вида», который панель могла бы навязать, — это то, что ответил GitHub.
        // Приводится к виду только пустое: пробелы вместо значения означали бы «выпуск без имени».
        UpdateLatest = (settings.UpdateLatest ?? string.Empty).Trim(),
        UpdatePublished = (settings.UpdatePublished ?? string.Empty).Trim(),
        UpdatePageUrl = (settings.UpdatePageUrl ?? string.Empty).Trim(),
        UpdateNotes = (settings.UpdateNotes ?? string.Empty).Trim(),
        UpdateNotesRaw = (settings.UpdateNotesRaw ?? string.Empty).Trim(),

        // Пропущенная версия — решение ЧЕЛОВЕКА, и оно тоже приводится к виду: версия сравнивается
        // как версия, поэтому «v2.1.0» и «2.1.0+хеш» — одна и та же пропущенная версия.
        UpdateSkippedVersion = Update.UpdateDecisions.Numeric(settings.UpdateSkippedVersion),

        // Размер и положение главного окна — той же дверью и по той же причине: файл правят
        // руками, а абсурдная координата завела бы окно туда, где его не найти. Правило живёт
        // в ядре размещения (`Shell\WindowPlacement.cs`): отрицательная координата ЗАКОННА
        // (второй монитор слева), отрицательный размер — мусор; ноль в обоих полях позиции
        // означает «не задано» (умолчания полей нулевые).
        WindowX = WindowPlacement.NormalizeCoordinate(settings.WindowX),
        WindowY = WindowPlacement.NormalizeCoordinate(settings.WindowY),
        WindowWidth = WindowPlacement.NormalizeSize(settings.WindowWidth),
        WindowHeight = WindowPlacement.NormalizeSize(settings.WindowHeight),
    };
}
