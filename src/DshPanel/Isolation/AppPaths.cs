namespace DshPanel.Isolation;

/// <summary>
/// ЕДИНСТВЕННЫЙ источник путей панели.
///
/// Зачем отдельный класс, а не «пути по месту». В v1 путь брался где понадобится, и это
/// дало два случая, когда проверка трогала данные владельца: один раз прогон подменил
/// данные и состояние, но НЕ домашний каталог движка — и показал в журнале настоящий
/// баланс владельца; другой раз проверка замены файлов перенаправила боевую запись
/// автозапуска на свою временную копию. Оба случая — про одно: путь, вычисленный в
/// стороне от общего корня, уводит прогон к чужим данным.
///
/// Поэтому здесь два способа получить пути, и оба выводят ВСЁ из одного корня:
/// <see cref="ForOwner"/> — настоящий прогон, <see cref="Under"/> — изолированный.
/// Третьего способа нет, и добавлять его нельзя.
/// </summary>
public sealed class AppPaths
{
    /// <summary>
    /// Имя папки 2.0. Взято отличным от папки v1 НАМЕРЕННО: решение владельца —
    /// 2.0 встаёт РЯДОМ, а не поверх, и разойтись они обязаны каталогами.
    /// </summary>
    /// <remarks>
    /// Имя ЗАКРЕПЛЕНО решением владельца 24.09.2026 — «оставить <c>DshPanel2</c> и <c>DSHPanel2</c>»
    /// (вопрос 4 в <c>AGENTS.md</c>), и это решение, а не рабочее допущение: <c>docs/ISOLATION.md</c>
    /// («Имя папки 2.0 — <c>DshPanel2</c>, и это РЕШЕНО владельцем»), <c>docs/ROADMAP.md</c>
    /// («✅ Свои имена уже есть»). Прежняя редакция этого примечания называла значение
    /// «ещё НЕ принятым» — это было неправдой.
    /// Менять его — только в этом месте. Отличие от папки v1 стережёт проверка
    /// <c>IsolationTests.Каталоги_двух_версий_не_совпадают</c>.
    /// </remarks>
    public const string ProductFolder = "DshPanel2";

    /// <summary>
    /// Имя переменной, которой уводят в сторону каталог ключей SSH (см. <see cref="SshDir"/>).
    /// Имя взято у v1 без изменений: этим же именем пользуются прогоны проверок копий и наката,
    /// и переименование заставило бы их задевать настоящие ключи человека.
    /// </summary>
    public const string SshDirVariable = "DSH_PANEL_SSH_DIR";

    /// <summary>Корень всех данных панели. Всё остальное выводится только из него.</summary>
    public string Root { get; }

    public string StateDir { get; }
    public string DataDir { get; }
    public string BackupsDir { get; }
    public string LogFile { get; }
    public string SettingsFile { get; }
    public string EngineDir { get; }

    /// <summary>
    /// Запись автозапуска ИЗОЛИРОВАННОГО прогона: у настоящего она живёт в реестре
    /// (<c>HKCU\...\Run</c>), а не в файле, и этот путь у него не используется.
    ///
    /// Свойство всё равно есть у обоих прогонов, и это намеренно: путь обязан выводиться из
    /// одного корня и попадать в <see cref="AllPaths"/>. Иначе изолированный прогон получил бы
    /// способ записать автозапуск в обход корня — ровно та дыра, из-за которой в v1 проверка
    /// замены файлов перевела боевую запись владельца на свою временную копию.
    /// </summary>
    public string AutostartFile { get; }

    /// <summary>Домашний каталог движка DSH. У настоящего прогона он вне корня панели — это общий <c>~/.dsh</c>.</summary>
    public string DshHome { get; }

    /// <summary>
    /// Имя файла ключей доступа внутри домашнего каталога движка: ключ модели и секрет подписи
    /// cookie входа. Имя вынесено сюда, потому что его знают ДВОЕ — чтение ключа (свойство ниже)
    /// и «копия для передачи», которая обязана НЕ положить этот файл в архив
    /// (<c>Backup\BackupPlan.cs</c>). Второе имя рядом означало бы, что однажды поправят одно.
    /// </summary>
    public const string CredentialsFileName = ".credentials.yaml";

    public string CredentialsPath => Path.Combine(DshHome, CredentialsFileName);

    /// <summary>
    /// Имя файла ССЫЛКИ ВХОДА внутри состояния панели: в нём лежит адрес сервера ВМЕСТЕ С ТОКЕНОМ.
    ///
    /// Имя вынесено сюда по той же причине, что и имя файла ключей: его знают ДВОЕ — запись
    /// ссылки сервером и «копия для передачи», которая обязана НЕ положить этот файл в архив
    /// (<c>Backup\BackupPlan.cs</c>). Второе имя рядом означало бы, что однажды поправят одно.
    /// </summary>
    public const string EntryLinkFileName = "entry-link.txt";

    /// <summary>
    /// Ссылка входа в панель — ФАЙЛ-СЕКРЕТ: в ссылке токен (красная линия 7).
    ///
    /// Живёт под корнем панели (а не в памяти) намеренно: сервер панель при выходе не гасит,
    /// и после перезапуска панели кнопка «Открыть панель» обязана работать по-прежнему.
    /// Удаляется вместе с сервером: ссылка без сервера — мёртвый адрес.
    ///
    /// ⚠️ Контроллер сервера этот путь НЕ ЗНАЕТ: чтение и запись приходят к нему делегатами,
    /// а выдаёт их <c>App.StartPanel</c> по праву <see cref="RunRights.LocalData"/>. Иначе прогон
    /// проверки прочитал бы файл владельца и подсмотрел его состояние — ровно тот класс дефектов,
    /// ради которого изоляция и заведена.
    /// </summary>
    public string EntryLinkFile { get; }

    /// <summary>
    /// Запись «этот сервер — наш»: порт, номер процесса и время его создания
    /// (<see cref="Server.OwnServerRecord"/>).
    ///
    /// Нужна ровно для одного случая: панель перезапустили, а её сервер жив. Без записи панель
    /// приняла бы СВОЙ сервер за найденный чужой — предложила бы в него «встроиться», а
    /// «Остановить» отказывалась бы гасить. Права на неё те же, что у ссылки входа.
    /// </summary>
    public string OwnServerFile { get; }

    /// <summary>
    /// ФАЙЛ ИСТОРИИ ЦЕН: точки изменения тарифа на странице цен
    /// (<see cref="Pricing.PricingHistory"/>).
    ///
    /// Лежит рядом с остальными файлами состояния и выводится из того же корня — второго места,
    /// где вычисляются пути панели, нет. Права на него те же, что у файлов состояния сервера:
    /// прогон без права истории владельца не читает и своей не пишет.
    /// </summary>
    public string PricingHistoryFile { get; }

    /// <summary>
    /// Каталог ключей SSH текущего пользователя — тот, который по решению владельца 26.09.2026
    /// снова может попасть в копию и вернуться из неё.
    ///
    /// Отдельным свойством, а не «вычислено по месту», по той же причине, что и остальные пути:
    /// перечень <see cref="AllPaths"/> обязан включать его, иначе изолированный прогон однажды
    /// возьмёт ключи владельца — ровно тот способ, которым изоляция в v1 и обходилась.
    ///
    /// У настоящего прогона каталог уводится в сторону переменной <c>DSH_PANEL_SSH_DIR</c>
    /// (так делала и v1): прогон проверки копий иначе задел бы настоящие ключи человека,
    /// а накат закрывает каталоги ключей на владельца. У изолированного прогона каталог лежит
    /// ПОД ЕГО КОРНЕМ, и переменная на него не влияет: изоляция не должна держаться на честном
    /// слове того, кто её запускает.
    /// </summary>
    public string SshDir { get; }

    private AppPaths(
        string root, string state, string data, string backups, string engine, string dshHome, string ssh)
    {
        Root = root;
        StateDir = state;
        DataDir = data;
        BackupsDir = backups;
        EngineDir = engine;
        DshHome = dshHome;
        SshDir = ssh;
        LogFile = Path.Combine(state, "panel.log");
        SettingsFile = Path.Combine(root, "settings.json");
        AutostartFile = Path.Combine(state, "autostart.txt");
        EntryLinkFile = Path.Combine(state, EntryLinkFileName);
        OwnServerFile = Path.Combine(state, "own-server.txt");
        PricingHistoryFile = Path.Combine(state, "pricing-history.json");
    }

    /// <summary>
    /// Пути настоящего прогона — того, который запустил человек на своей машине.
    /// Домашний каталог движка здесь ОБЩИЙ с v1: движок один на машину, и панель к нему
    /// только подключается. Всё остальное — в своей папке (см. <see cref="ProductFolder"/>).
    /// </summary>
    public static AppPaths ForOwner()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductFolder);

        return new AppPaths(
            root: root,
            state: Path.Combine(root, "state"),
            data: Path.Combine(root, "data"),
            backups: Path.Combine(root, "backups"),
            engine: Path.Combine(root, "engine"),
            dshHome: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"),
            ssh: OwnerSshDir());
    }

    /// <summary>
    /// Каталог ключей SSH настоящего прогона: <c>DSH_PANEL_SSH_DIR</c>, если задан, иначе
    /// <c>%USERPROFILE%\.ssh</c>. Перенесено из v1 «как есть» (<c>AppPaths.SshDir</c>) —
    /// вместе с раскрытием переменных окружения: путь в переменной пишут руками.
    /// </summary>
    private static string OwnerSshDir()
    {
        try
        {
            var custom = Environment.GetEnvironmentVariable(SshDirVariable);
            if (!string.IsNullOrWhiteSpace(custom))
                return Environment.ExpandEnvironmentVariables(custom.Trim());
        }
        catch
        {
            // Переменную не прочитать — берём умолчание: это не повод не строить пути вовсе.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    }

    /// <summary>
    /// Пути изолированного прогона: ВСЁ, включая домашний каталог движка, лежит под
    /// <paramref name="root"/>. Это и есть изоляция: прогон физически не может дотянуться
    /// до чужих данных, потому что путей к ним у него нет — они нигде не вычисляются.
    /// </summary>
    public static AppPaths Under(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException($"Корень изолированного прогона обязан быть полным путём, а не «{root}»", nameof(root));

        // Приводим к каноническому виду здесь, а не у вызывающего: один источник путей
        // не должен зависеть от того, с хвостовым разделителем его позвали или без.
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        return new AppPaths(
            root: root,
            state: Path.Combine(root, "state"),
            data: Path.Combine(root, "data"),
            backups: Path.Combine(root, "backups"),
            engine: Path.Combine(root, "engine"),
            dshHome: Path.Combine(root, "dsh-home"),

            // Ключи изолированного прогона — под его корнем, и переменная тут ни при чём:
            // иначе прогон проверки копий достал бы настоящие ключи владельца.
            ssh: Path.Combine(root, "ssh"));
    }

    /// <summary>
    /// Все пути, которые панель отдаёт наружу. Нужен, чтобы проверка могла пройти по списку,
    /// а не по запомненным свойствам: новое свойство, забытое в проверке, — это ровно тот
    /// способ, которым изоляция в v1 и обходилась.
    /// </summary>
    public IReadOnlyList<string> AllPaths => new[]
    {
        Root, StateDir, DataDir, BackupsDir, LogFile, SettingsFile, EngineDir, AutostartFile,
        DshHome, CredentialsPath, SshDir, EntryLinkFile, OwnServerFile, PricingHistoryFile,
    };

    /// <summary>
    /// Настоящие каталоги v1. Шаг передачи дел их ЧИТАЕТ и ничего в них не пишет —
    /// поэтому они и лежат здесь, в одном месте, а не разбросаны по коду переноса.
    /// </summary>
    public static class V1
    {
        /// <summary>Папка v1. Именно она не должна совпасть с <see cref="ProductFolder"/>.</summary>
        public const string ProductFolder = "DshPanel";

        /// <summary>Папка прежней генерации — до v1.</summary>
        public const string LegacyFolder = "DeepSeekHarness";

        /// <summary>Состояние v1.</summary>
        public static string StateDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

        /// <summary>Данные v1 — отсюда берётся перенос настроек.</summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder);

        /// <summary>
        /// Файл настроек v1. ЧИТАЕТСЯ и только ради одного: узнать рабочую папку, которую человек
        /// уже использует, чтобы ПРЕДЛОЖИТЬ её в настройках 2.0 (решение владельца 24.09.2026).
        /// Ничего сюда не пишется, и читает его только настоящий прогон человеком.
        /// </summary>
        public static string SettingsFile => Path.Combine(DataDir, "settings.json");

        /// <summary>Каталог прежней генерации — второй возможный источник переноса.</summary>
        public static string LegacyDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolder);

        /// <summary>
        /// Ссылка входа панели 1.x — тот самый <c>web-url.txt</c>, в котором лежит токен.
        ///
        /// ЧИТАЕТСЯ и только: своего сервера 2.0 у панели 1.x не отнимает, и писать в её каталог
        /// панель не имеет права вовсе. Нужен он ровно для одного случая — человек уже перешёл
        /// на 2.0 и взял ЕГО сервер (тот, что подняла 1.x) под управление: своей ссылки у такого
        /// сервера нет, а 1.x её записала.
        ///
        /// Читает его только обычный запуск панели человеком: это чужой каталог, и в прогоне
        /// проверки источник молчит (право — как у машинного окружения, <see cref="RunRights.MachineData"/>).
        /// </summary>
        public static string EntryLinkFile => Path.Combine(StateDir, "web-url.txt");
    }
}
