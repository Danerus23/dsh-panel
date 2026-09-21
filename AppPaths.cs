using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DshTray;

/// <summary>
/// Пути приложения.
///
/// Настройки и окна тарифа живут в профиле пользователя (%APPDATA%\DshPanel), поэтому
/// обновление приложения их не затирает. Состояние запуска — журналы, pid и ссылка для
/// входа — в %LOCALAPPDATA%\DshPanel: рядом с .exe писать нельзя, если панель поставлена
/// в Program Files, а без файла ссылки не откроется браузер (сервер отвечает 401 на
/// голый адрес). Старые места читаются: настройки — из %APPDATA%\DeepSeekHarness,
/// ссылка — из папки logs рядом с .exe.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string baseDir)
    {
        BaseDir = baseDir;
    }

    public string BaseDir { get; }

    /// <summary>Имя продукта: заголовки, ярлыки, подсказки.</summary>
    public const string ProductName = "DSH Panel";

    /// <summary>Папка продукта в профиле: %APPDATA%\<это> и %LOCALAPPDATA%\<это>.</summary>
    public const string ProductFolder = "DshPanel";

    /// <summary>Как папка называлась до переименования — оттуда переносим настройки.</summary>
    private const string LegacyFolder = "DeepSeekHarness";

    /// <summary>
    /// Как переписывается файл настроек при переносе: тот же вид, что у AppSettings.Save —
    /// с отступами и без экранирования кириллицы, — чтобы файл остался читаемым человеку.
    /// </summary>
    private static readonly JsonSerializerOptions NodeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // --- состояние запуска -------------------------------------------------

    /// <summary>Папка состояния: журналы, pid и ссылка для входа.</summary>
    public static string StateDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_STATE");
            if (!string.IsNullOrWhiteSpace(custom)) return Environment.ExpandEnvironmentVariables(custom.Trim());

            return DefaultStateDir;
        }
    }

    /// <summary>
    /// Папка состояния по умолчанию (%LOCALAPPDATA%\DshPanel) — без учёта DSH_PANEL_STATE.
    ///
    /// Нужна отдельно от <see cref="StateDir"/>, потому что в ней лежит свой переносимый Node
    /// и поставленный в него движок: их надо находить и тогда, когда панель запущена с другим
    /// каталогом состояния (так работают проверки в песочнице — из-за этого проверка «пакет dsh
    /// найден» падала на машине, где всё стоит).
    /// </summary>
    public static string DefaultStateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    /// <summary>Прежнее место состояния — папка logs рядом с .exe.</summary>
    public string LegacyStateDir => Path.Combine(BaseDir, "logs");

    public string LogDir => StateDir;
    public string LogPath => Path.Combine(LogDir, "dsh-web.log");

    /// <summary>Куда приложение пишет ссылку для входа, когда само поднимает сервер.</summary>
    public string OwnUrlPath => Path.Combine(LogDir, "web-url.txt");

    public string PidPath => Path.Combine(LogDir, "server.pid");

    /// <summary>Ссылка, оставленная прежней сборкой: сервер мог быть поднят ею.</summary>
    public string LegacyOwnUrlPath => Path.Combine(LegacyStateDir, "web-url.txt");

    // --- данные пользователя -----------------------------------------------

    /// <summary>Папка пользовательских данных: переживает обновление приложения.</summary>
    public static string DataDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_DATA");
            if (!string.IsNullOrWhiteSpace(custom)) return Environment.ExpandEnvironmentVariables(custom.Trim());

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder);
        }
    }

    /// <summary>
    /// Прежняя папка настроек — до переименования продукта: по умолчанию
    /// %APPDATA%\DeepSeekHarness.
    ///
    /// Переменная DSH_PANEL_LEGACY_DATA подменяет её целиком — это подмена для проверок:
    /// tools\check-migrate.ps1 кладёт туда свои файлы и гоняет перенос через сам exe, не трогая
    /// настоящий профиль (без неё сценарий «файл прежней панели новее панельного» можно было бы
    /// проверить только на живой машине).
    /// </summary>
    public static string LegacyDataDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_LEGACY_DATA");
            if (!string.IsNullOrWhiteSpace(custom)) return Environment.ExpandEnvironmentVariables(custom.Trim());

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolder);
        }
    }

    /// <summary>
    /// Переносит настройки и окна тарифа из прежней папки в новую — один раз, копированием:
    /// в старой папке ничего не трогаем. Перенос идёт, только пока своего файла в папке панели
    /// ещё нет; существующий файл не перезаписывается никогда.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            // Сборочная и CI-самопроверка запускают панель во временном профиле: перенос из
            // прежней папки притащил бы туда настройки того, кто собирает, и проверка
            // рассказывала бы про его машину. Ключ выключает перенос целиком.
            if (Environment.GetEnvironmentVariable("DSH_PANEL_NO_MIGRATE") == "1") return;

            var target = DataDir;
            var source = LegacyDataDir;
            if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(target);
            var carriedSettings = false;
            foreach (var name in new[] { "settings.json", "pricing.json" })
            {
                // Каждый файл переносим сам по себе: один может не скопироваться (занят, нет
                // места, нет прав), и тогда второй всё равно должен доехать в этом же запуске,
                // а не ждать следующего.
                try
                {
                    var from = Path.Combine(source, name);
                    var to = Path.Combine(target, name);
                    if (!File.Exists(from)) continue;

                    // Свой файл панели — источник истины, и перезаписывать его нечем: перенос
                    // задуман на один момент перехода, пока своего файла в папке панели нет.
                    // Раньше копия обновлялась, пока файл прежней панели новее, — а прежняя
                    // панель может работать месяцами, и её файл новее всегда. Владелец получал
                    // бы чужие настройки при каждом запуске: без пройденного мастера, со своим
                    // портом и рабочей папкой, — и мастер первой настройки предлагался бы снова.
                    if (File.Exists(to)) continue;

                    File.Copy(from, to, overwrite: false);
                    if (name == "settings.json") carriedSettings = true;
                }
                catch
                {
                    // Этот файл не перенёсся — пробуем следующий.
                }
            }

            // Ключи переносим только тогда, когда настройки и правда пришли из прежней папки:
            // в своих настройках панели эту галочку человек ставит сам.
            if (carriedSettings) CarryOverKeyBackup(Path.Combine(target, "settings.json"));
        }
        catch
        {
            // Профиль недоступен — работаем со значениями по умолчанию.
        }
    }

    /// <summary>
    /// Прежние версии кладывали ключи в каждую копию молча. Если в перенесённых настройках
    /// про это ещё не сказано, а ключи на машине есть — включаем это один раз, чтобы человек
    /// не потерял то, что копировалось раньше. Дальше он решает сам галочкой в окне копий.
    /// </summary>
    private static void CarryOverKeyBackup(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return;

            // Правим разобранный JSON, а не текст и не объект AppSettings: файл настроек может
            // нести поля прежних схем (у владельца это backupWorkspaceDir и kitKeepCount),
            // и пересохранение через AppSettings.Save их бы потеряло. Здесь меняются ровно два
            // свойства, всё остальное в файле остаётся как было.
            var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            if (root == null) return;

            // Про это уже сказано — не трогаем файл вовсе: дальше человек решает сам галочкой.
            // Ищем свойство по имени, а не подстрокой в тексте: подстрока нашлась бы и в значении,
            // и в чужом поле вроде backupWithKeysHint.
            var alreadySaid = root.Any(pair => string.Equals(pair.Key, "backupWithKeys", StringComparison.OrdinalIgnoreCase));
            if (alreadySaid) return;

            var directories = new[] { SshDir }.Where(Directory.Exists).ToList();
            if (directories.Count == 0) return;

            root["backupWithKeys"] = true;
            root["backupKeyDirs"] = new JsonArray(directories.Select(directory => (JsonNode)JsonValue.Create(directory)).ToArray());

            // Файл настроек — без BOM: с ним его не разберёт ни сама панель, ни инструменты.
            File.WriteAllText(settingsPath, root.ToJsonString(NodeOptions), new UTF8Encoding(false));

            // Молча включать упаковку ключей нельзя: от этой галочки архив становится секретом,
            // и человек должен видеть, откуда она взялась. Папку журнала AppLog берёт из
            // каталога состояния, поэтому от экземпляра AppPaths тут нужен только он.
            AppLog.Write(
                new AppPaths(AppContext.BaseDirectory),
                "настройки прежней панели перенесены: упаковка ключей включена автоматически, каталогов ключей: "
                + directories.Count);
        }
        catch
        {
            // Настройки не поправились — человек просто увидит галочку выключенной.
        }
    }

    /// <summary>
    /// Окна тарифа: рабочая копия в профиле пользователя. При первом запуске
    /// копия делается из pricing.json, который лежит рядом с .exe.
    /// </summary>
    public string PricingPath
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_TRAY_PRICING");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;

            try
            {
                Directory.CreateDirectory(DataDir);
                var userCopy = Path.Combine(DataDir, "pricing.json");
                if (!File.Exists(userCopy) && File.Exists(ShippedPricingPath))
                {
                    File.Copy(ShippedPricingPath, userCopy);
                }

                if (File.Exists(userCopy)) return userCopy;
            }
            catch
            {
                // Профиль недоступен — работаем с файлом рядом с .exe.
            }

            return ShippedPricingPath;
        }
    }

    /// <summary>Образец окон тарифа, который кладётся вместе с приложением.</summary>
    public string ShippedPricingPath => Path.Combine(BaseDir, "pricing.json");

    /// <summary>Настройки в профиле пользователя.</summary>
    public string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>Совсем старое место настроек (рядом с .exe) — читаем, если нового нет.</summary>
    public string LegacySettingsPath => Path.Combine(BaseDir, "settings.json");
    public string IconPath => Path.Combine(BaseDir, "dsh-tray.ico");

    public string BalanceScriptPath =>
        Resolve("DSH_TRAY_BALANCE_SCRIPT", Path.Combine(BaseDir, "tools", "balance.mjs"));

    /// <summary>
    /// Где искать ссылку для входа: сначала своё состояние, потом папка logs прежней сборки
    /// рядом с .exe — сервер мог быть поднят ею до перехода на новое место хранения.
    /// </summary>
    public IEnumerable<string> UrlCandidates()
    {
        yield return OwnUrlPath;
        yield return LegacyOwnUrlPath;
    }

    /// <summary>
    /// Папка данных DSH. Переменная DSH_HOME уважается: с её помощью можно
    /// развернуть вторую копию «себя» рядом с рабочей и проверить восстановление,
    /// не трогая текущую сессию.
    /// </summary>
    public static string DshHome
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    public static string CredentialsPath => Path.Combine(DshHome, ".credentials.yaml");

    /// <summary>
    /// Каталог ключей SSH текущего пользователя. Переменная DSH_PANEL_SSH_DIR уводит его
    /// в сторону: проверки копий и наката иначе задевали бы настоящие ключи владельца
    /// машины — накат закрывает каталоги ключей на владельца.
    /// </summary>
    public static string SshDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_SSH_DIR");
            if (!string.IsNullOrWhiteSpace(custom)) return Environment.ExpandEnvironmentVariables(custom.Trim());

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        }
    }

    /// <summary>
    /// Путь для показа человеку: домашний каталог заменяется на %USERPROFILE%.
    /// Нужно, чтобы длинный путь не обрезался в узких подписях окна.
    /// </summary>
    public static string Display(string path)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)
                && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                return "%USERPROFILE%" + path[home.Length..];
            }
        }
        catch
        {
            // Не получилось — показываем путь как есть.
        }

        return path;
    }

    /// <summary>Папка данных DSH для этого запуска (та же логика, что у DshHome).</summary>
    public string DshHomePath => DshHome;

    /// <summary>Профили DSH: профиль web — то, где живут плагины веб-интерфейса.</summary>
    public string ProfilesDir => Path.Combine(DshHomePath, "profiles");

    private static string Resolve(string environmentVariable, string fallback)
    {
        var custom = Environment.GetEnvironmentVariable(environmentVariable);
        return !string.IsNullOrWhiteSpace(custom) ? custom : fallback;
    }
}

/// <summary>Настройки, которые переживают перезапуск (settings.json в профиле пользователя).</summary>
public sealed class AppSettings
{
    [JsonPropertyName("openBrowserOnStart")]
    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>Порт локального сервера. Ключ командной строки --port важнее этого значения.</summary>
    [JsonPropertyName("serverPort")]
    public int ServerPort { get; set; } = 3080;

    /// <summary>
    /// Язык интерфейса: «auto» — по языку системы (не русский/английский/китайский —
    /// значит английский), либо «ru», «en», «zh». Меняется в «Настройках».
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Тема оформления окон: «auto» — как в Windows, «light» — всегда светлая,
    /// «dark» — всегда тёмная. Меняется в «Настройках»; разбирает значение
    /// <see cref="Theme.ModeFrom"/> (пустое или неизвестное — «как в Windows»).
    /// </summary>
    [JsonPropertyName("theme")]
    public string ThemeMode { get; set; } = "auto";

    /// <summary>Мастер первой настройки пройден — второй раз его не показываем.</summary>
    [JsonPropertyName("onboarded")]
    public bool Onboarded { get; set; }

    /// <summary>Путь к node.exe, если панель не нашла его сама (пусто — искать самим).</summary>
    [JsonPropertyName("nodePath")]
    public string NodePath { get; set; } = "";

    /// <summary>Путь к lib\bin.js пакета dsh, если панель не нашла его сама.</summary>
    [JsonPropertyName("dshBinPath")]
    public string DshBinPath { get; set; } = "";

    /// <summary>
    /// Каталог, из которого запускается DSH (рабочий каталог сервера). Пусто — папка панели,
    /// как было у прежних версий; мастер настройки предлагает выбрать свою.
    /// </summary>
    [JsonPropertyName("serverWorkingDir")]
    public string ServerWorkingDir { get; set; } = "";

    [JsonPropertyName("balanceAutoRefresh")]
    public bool BalanceAutoRefresh { get; set; } = true;

    [JsonPropertyName("balanceRefreshMinutes")]
    public int BalanceRefreshMinutes { get; set; } = 5;

    [JsonPropertyName("balanceWarnEnabled")]
    public bool BalanceWarnEnabled { get; set; }

    [JsonPropertyName("balanceWarnThreshold")]
    public decimal BalanceWarnThreshold { get; set; } = 5;

    [JsonPropertyName("peakAutoCheck")]
    public bool PeakAutoCheck { get; set; } = true;

    [JsonPropertyName("peakCheckHours")]
    public int PeakCheckHours { get; set; } = 24;

    // --- резервные копии ---------------------------------------------------

    [JsonPropertyName("backupEnabled")]
    public bool BackupEnabled { get; set; }

    [JsonPropertyName("backupIntervalHours")]
    public int BackupIntervalHours { get; set; } = 24;

    [JsonPropertyName("backupKeepCount")]
    public int BackupKeepCount { get; set; } = 5;

    /// <summary>Пустая строка — папка по умолчанию (Документы\DeepSeekHarness-Backups).</summary>
    [JsonPropertyName("backupFolder")]
    public string BackupFolder { get; set; } = "";

    /// <summary>Вкладывать движок DSH и Node: архив становится тяжёлым, зато оффлайн.</summary>
    [JsonPropertyName("backupWithEngine")]
    public bool BackupWithEngine { get; set; }

    /// <summary>Вкладывать историю сессий и вложения.</summary>
    [JsonPropertyName("backupWithSessions")]
    public bool BackupWithSessions { get; set; }

    /// <summary>
    /// Кладывать ли в копию ключи и сертификаты. По умолчанию нет: это приватные файлы,
    /// и архив с ними становится секретом. Спрашивается мастером и меняется в окне копий.
    /// </summary>
    [JsonPropertyName("backupWithKeys")]
    public bool BackupWithKeys { get; set; }

    /// <summary>
    /// Дополнительные каталоги ключей (кроме своего `.ssh`), которые человек разрешил
    /// кладывать в копию. Пустой список — только `.ssh`, и то лишь при включённой упаковке.
    /// </summary>
    [JsonPropertyName("backupKeyDirs")]
    public List<string> BackupKeyDirs { get; set; } = new();

    /// <summary>Когда копия делалась в последний раз — чтобы расписание переживало перезапуск.</summary>
    [JsonPropertyName("backupLastAt")]
    public DateTime? BackupLastAt { get; set; }

    [JsonPropertyName("backupLastPath")]
    public string BackupLastPath { get; set; } = "";

    // --- обновления ---------------------------------------------------------

    /// <summary>Когда в последний раз спрашивали GitHub — для окна «Обновления» и журнала.
    /// Саму проверку это поле больше не ограничивает: она идёт при каждом запуске панели,
    /// а от повторных шариков защищает <see cref="UpdateAnnounced"/>.</summary>
    [JsonPropertyName("updateCheckedAt")]
    public DateTime? UpdateCheckedAt { get; set; }

    /// <summary>Версия, о которой человеку уже сказали шариком. Проверка идёт при каждом
    /// запуске, поэтому без этого поля шарик показывался бы на каждом старте, пока человек не
    /// обновится. Пусто — ещё ни о чём не говорили.</summary>
    [JsonPropertyName("updateAnnounced")]
    public string UpdateAnnounced { get; set; } = "";

    /// <summary>Какая версия лежит на GitHub по последней проверке (пусто — ещё не проверяли).</summary>
    [JsonPropertyName("updateLatest")]
    public string UpdateLatest { get; set; } = "";

    /// <summary>Дата выпуска на GitHub — для окна обновлений.</summary>
    [JsonPropertyName("updatePublished")]
    public string UpdatePublished { get; set; } = "";

    /// <summary>Страница выпуска — чтобы открыть её кнопкой.</summary>
    [JsonPropertyName("updatePageUrl")]
    public string UpdatePageUrl { get; set; } = "";

    /// <summary>Заметки выпуска — их читает человек перед обновлением. Это ВЫБРАННЫЙ блок
    /// (на языке интерфейса) и обрезанный по пределу показа: готовый текст для окна.</summary>
    [JsonPropertyName("updateNotes")]
    public string UpdateNotes { get; set; } = "";

    /// <summary>
    /// Тело выпуска, из которого выбирается блок языка: все три языка и машинные метки.
    /// Обычное тело лежит как пришло из релиза; тело длиннее предела — уже разобранные и
    /// безопасно обрезанные блоки (см. UpdateService.RawNotes). Хранится, чтобы заметки
    /// можно было пересобрать на другом языке: язык интерфейса меняется в любой момент,
    /// а проверка обновлений бывает не чаще раза в сутки.
    /// </summary>
    [JsonPropertyName("updateNotesRaw")]
    public string UpdateNotesRaw { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Понятные интерфейсу языки: «auto» плюс три словаря.</summary>
    private static bool IsKnownLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed is "auto" or "ru" or "en" or "zh";
    }

    /// <summary>
    /// Читает настройки. Если нового файла ещё нет, а старый (рядом с .exe) остался —
    /// берём значения из него, чтобы ничего не потерялось. Если прочитать не удалось ничего,
    /// отдаём значения по умолчанию: панель обязана запуститься.
    /// </summary>
    public static AppSettings Load(string path, string legacyPath = null)
    {
        foreach (var candidate in new[] { path, legacyPath })
        {
            if (TryLoad(candidate, out var loaded)) return loaded;
        }

        return new AppSettings();
    }

    /// <summary>
    /// Читает настройки из конкретного файла и говорит, вышло ли это. Отличие от
    /// <see cref="Load"/> только в честном ответе: там «не вышло» неотличимо от «файла нет»,
    /// а накату копии это различие нужно — см. <see cref="Reload"/>.
    /// </summary>
    private static bool TryLoad(string path, out AppSettings settings)
    {
        settings = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            if (loaded == null) return false;

            Normalize(loaded);
            settings = loaded;
            return true;
        }
        catch
        {
            // Битый или нечитаемый файл — это не повод не запускаться.
            return false;
        }
    }

    /// <summary>Правдоподобные значения для полей, которые могли прийти какими угодно.</summary>
    private static void Normalize(AppSettings loaded)
    {
        if (loaded.ServerPort < 1 || loaded.ServerPort > 65535) loaded.ServerPort = 3080;
        if (!IsKnownLanguage(loaded.Language)) loaded.Language = "auto";
        if (loaded.BalanceRefreshMinutes <= 0) loaded.BalanceRefreshMinutes = 5;
        if (loaded.BalanceWarnThreshold < 0) loaded.BalanceWarnThreshold = 0;
        if (loaded.PeakCheckHours <= 0) loaded.PeakCheckHours = 24;
        if (loaded.BackupIntervalHours <= 0) loaded.BackupIntervalHours = 24;
        if (loaded.BackupKeepCount < 1) loaded.BackupKeepCount = 5;
        if (loaded.BackupKeepCount > 100) loaded.BackupKeepCount = 100;
        loaded.BackupKeyDirs ??= new List<string>();
    }

    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Настройку не сохранить — не беда, приложение работает и так.
        }
    }

    /// <summary>
    /// Перечитывает файл настроек в этот же объект. Нужно после наката резервной копии:
    /// файл мог быть заменён чужим, а на руках у панели остался бы прежний набор значений —
    /// и она затерла бы восстановленный файл при первом же сохранении.
    ///
    /// Возвращает false, если файла нет или прочитать его не удалось; в этом случае значения
    /// объекта НЕ меняются. Иначе вызывающему не отличить «файл прочитан» от «файл битый»:
    /// AppSettings.Load на битом файле молча отдаёт умолчания, и сохранение такого объекта
    /// положило бы их поверх восстановленного файла (панель ушла бы на 3080 и снова показала
    /// мастер первой настройки, а отчёт говорил бы, что настройки вернулись).
    /// </summary>
    public bool Reload(string path)
    {
        if (!TryLoad(path, out var loaded)) return false;

        OpenBrowserOnStart = loaded.OpenBrowserOnStart;
        ServerPort = loaded.ServerPort;
        Language = loaded.Language;
        ThemeMode = loaded.ThemeMode;
        Onboarded = loaded.Onboarded;
        NodePath = loaded.NodePath;
        DshBinPath = loaded.DshBinPath;
        ServerWorkingDir = loaded.ServerWorkingDir;
        BalanceAutoRefresh = loaded.BalanceAutoRefresh;
        BalanceRefreshMinutes = loaded.BalanceRefreshMinutes;
        BalanceWarnEnabled = loaded.BalanceWarnEnabled;
        BalanceWarnThreshold = loaded.BalanceWarnThreshold;
        PeakAutoCheck = loaded.PeakAutoCheck;
        PeakCheckHours = loaded.PeakCheckHours;
        BackupEnabled = loaded.BackupEnabled;
        BackupIntervalHours = loaded.BackupIntervalHours;
        BackupKeepCount = loaded.BackupKeepCount;
        BackupFolder = loaded.BackupFolder;
        BackupWithEngine = loaded.BackupWithEngine;
        BackupWithSessions = loaded.BackupWithSessions;
        BackupWithKeys = loaded.BackupWithKeys;
        BackupKeyDirs = loaded.BackupKeyDirs;
        BackupLastAt = loaded.BackupLastAt;
        BackupLastPath = loaded.BackupLastPath;
        UpdateCheckedAt = loaded.UpdateCheckedAt;
        UpdateAnnounced = loaded.UpdateAnnounced;
        UpdateLatest = loaded.UpdateLatest;
        UpdatePublished = loaded.UpdatePublished;
        UpdatePageUrl = loaded.UpdatePageUrl;
        UpdateNotes = loaded.UpdateNotes;
        UpdateNotesRaw = loaded.UpdateNotesRaw;

        return true;
    }
}
