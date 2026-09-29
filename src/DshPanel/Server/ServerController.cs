using System.Diagnostics;
using System.Globalization;
using System.Text;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// То, что нужно окну панели от сервера. Отдельный интерфейс — не украшение: окно проверяется
/// без экрана и без запуска настоящего движка, а для этого ему нужна подмена.
/// </summary>
public interface IServerControl
{
    ServerState State { get; }

    /// <summary>Кто поднял сервер, которым управляет панель. Влияет на право гашения.</summary>
    ServerOwner Owner { get; }

    /// <summary>
    /// Согласие человека на ЭТОТ встроенный сервер ЗАПОМНЕНО (порт лежит в настройках панели).
    ///
    /// Решение владельца 26.09.2026: ответ «беру под управление» спрашивается один раз и
    /// запоминается. Запомнено — панель считает окружение своим и гасит сервер свободно;
    /// не запомнено (не спрашивали, или записать не удалось) — спрашивает подтверждение
    /// каждый раз, как раньше.
    /// </summary>
    bool ConsentRemembered { get; }

    /// <summary>Перечитать состояние (кто на порту, отвечает ли сервер).</summary>
    ServerState Refresh();

    /// <summary>Поднять СВОЙ сервер и дождаться, пока он ответит. Возвращает состояние на момент выхода.</summary>
    ServerState Start(TimeSpan timeout);

    /// <summary>
    /// Погасить сервер.
    ///
    /// <paramref name="confirmed"/> обязателен только для встроенного сервера
    /// (<see cref="ServerOwner.Adopted"/>), и только пока согласие не запомнено
    /// (<see cref="ConsentRemembered"/>): его панель не поднимала, через него может идти
    /// текущая работа человека, и подтверждение спрашивается КАЖДЫЙ раз (решение владельца
    /// 24.09.2026). Запомненное согласие — это уже данный один раз ответ «беру под управление»
    /// (решение владельца 26.09.2026), и тогда подтверждения не нужно. Свой сервер панель гасит
    /// и без подтверждения — это её процесс.
    /// </summary>
    ServerState Stop(bool confirmed);

    /// <summary>
    /// Перезапуск: погасить и поднять заново.
    ///
    /// <paramref name="confirmed"/> ОБЯЗАТЕЛЕН и значит ровно то же, что у <see cref="Stop"/>:
    /// у ВСТРОЕННОГО сервера (<see cref="ServerOwner.Adopted"/>) без подтверждения человека и без
    /// запомненного согласия (<see cref="ConsentRemembered"/>) контроллер не гасит чужой процесс —
    /// а значит и перезапускать нечего, и ответ будет тот же, что у неподтверждённой остановки.
    /// Предохранитель стоит ЗДЕСЬ, а не в окне: окно лишь спрашивает по предикату, а решает
    /// контроллер — и решает так же, как при гашении (красная линия 6).
    /// </summary>
    ServerState Restart(TimeSpan timeout, bool confirmed);

    /// <summary>
    /// Найти работающие серверы DSH, которые панель НЕ поднимала. Свой сервер и тот, в который
    /// панель уже встроена, в находку не попадают: предлагать встроиться в то, чем уже
    /// управляешь, — это шум. На этой же находке стоит ЗАМОК от второго движка: пока она
    /// не пуста, свой сервер панель не поднимает (см. <c>allowParallelStart</c>).
    /// </summary>
    DiscoveryResult Scan();

    /// <summary>
    /// Взять найденный сервер под управление. Своего сервера при этом НЕ поднимает.
    /// Приметы проверяются заново: между находкой и согласием человека мир мог измениться.
    /// </summary>
    ServerState Adopt(FoundServer found);

    /// <summary>Перестать управлять найденным сервером, НЕ гася его. Отвязались — он живёт дальше.</summary>
    ServerState Detach();

    /// <summary>
    /// ССЫЛКА ВХОДА в панель (в ней ТОКЕН) или пустая строка, если панель её не знает.
    ///
    /// ⚠️ Это СЕКРЕТ: она не показывается человеку ни текстом, ни подсказкой, ни в подписях,
    /// ни в журнале — только уходит в браузер по его щелчку (красная линия 7). Из контроллера
    /// она выходит одним свойством, чтобы второго способа её достать не завелось.
    /// </summary>
    string EntryLink { get; }
}

/// <summary>
/// Сервер DSH: поднять, погасить, показать состояние, найти чужой и встроиться в него.
///
/// Устройство простое и намеренно скучное: панель ЗАПОМИНАЕТ дескриптор процесса, который
/// запустила сама, и только его имеет право гасить свободно. Встроенный сервер гасится
/// по подтверждению — но тоже не «по примете из командной строки», а по тому же процессу,
/// который панель увидела в момент встраивания (номер процесса плюс время его создания).
/// И у этого правила есть одно исключение, названное владельцем 26.09.2026: если человек
/// ОДИН РАЗ сказал «беру под управление», ответ запоминается в настройках, и дальше панель
/// считает это окружение своим — берёт сервер сама и гасит свободно.
///
/// Что панель НЕ делает: **не занимает порт владельца без права** (3080 — решение владельца
/// 26.09.2026: обычный запуск человеком его занимает, прогон проверки приводится к 3081),
/// не пишет в его
/// `~/.dsh` (домашний каталог движка приходит снаружи и в изолированном прогоне лежит под корнем),
/// не ставит и не обновляет движок, **не поднимает свой сервер рядом с найденным чужим**
/// (замок от второго движка: данные у них одни и те же, находка 25.09.2026 — право «поднимать
/// рядом» приходит явным параметром и по умолчанию выключено) и не гасит без подтверждения то,
/// что подняла не она, пока согласие не запомнено.
///
/// Встроившись, панель берёт у окружения АДРЕС сервера. Домашний каталог движка (`DSH_HOME`)
/// она по-прежнему берёт из своего окружения: у работающего сервера его нигде не спросить
/// (`docs\ENGINE.md` §11, п. 4). Для обычного запуска человеком это тот же `~/.dsh` — то есть
/// на практике совпадает, но обещать «панель узнала DSH_HOME чужого сервера» нельзя.
/// </summary>
public sealed class ServerController : IServerControl, IDisposable
{
    /// <summary>
    /// Сколько строк вывода движка панель помнит для объяснения неудачи. Держать больше
    /// незачем: человеку нужна последняя причина, а не весь журнал запуска.
    /// </summary>
    private const int RememberedEngineLines = 40;

    /// <summary>
    /// Как часто панель по-настоящему осматривает порты машины. Опрос состояния идёт раз в секунду,
    /// и полный осмотр на каждом тике был бы тратой: находка не появляется и не исчезает мгновенно.
    /// Отдельная ручка — потому что проверке нужен свежий ответ сейчас же.
    /// </summary>
    public static readonly TimeSpan DefaultScanInterval = TimeSpan.FromSeconds(2);

    /// <summary>Сколько ждать, пока порт действительно освободится после гашения.</summary>
    public static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(20);

    private readonly string _dshHome;
    private readonly Func<string> _workingDirectory;
    private readonly Func<int> _requestedPort;
    private readonly Action<string> _log;
    private readonly Func<DshEngine?> _locateEngine;
    private readonly IProcessKiller _killer;
    private readonly ServerProbe _probe;
    private readonly TimeSpan _stopWait;
    private readonly TimeSpan _scanInterval;
    private readonly Func<int>? _readAdoptedPort;
    private readonly Action<int>? _rememberAdoptedPort;
    private readonly bool _allowParallelStart;
    private readonly bool _mayOccupyOwnerPort;
    private readonly ServerStateFiles _files;
    private readonly Func<string>? _readFoundServerLink;
    private readonly Func<string, LinkProbe> _linkProbe;

    private readonly object _gate = new();
    private readonly object _scanGate = new();
    private readonly Queue<string> _engineTail = new();

    private Process? _process;
    private string _lastEngineLine = string.Empty;
    private bool _exitReported;
    private int _port;

    // Номер процесса СВОЕГО сервера, узнанного ПО ЗАПИСИ после перезапуска панели: живого
    // дескриптора у такого сервера нет, а разведке его надо исключать из находки — иначе панель
    // нашла бы сама себя и предложила в себя «встроиться».
    private int _ownPid;

    // Ссылка входа. В ПАМЯТИ — чтобы кнопка работала сразу после запуска сервера, на диске —
    // чтобы она работала и после перезапуска панели (сервер панель при выходе не гасит).
    private string _entryLink = string.Empty;
    private bool _entryLinkLoaded;

    // Запасной источник ссылки — файл панели 1.x. Ответ порта по чужой ссылке измеряется сетью,
    // а спрашивают ссылку каждую секунду (окно перерисовывается), поэтому ответ запоминается
    // по порту: без этого панель делала бы HTTP-запрос раз в секунду на ровном месте.
    private int _fallbackCheckedPort;
    private string _fallbackLink = string.Empty;

    // Причина последнего отказа ПОДНЯТЬ свой сервер (замок от второго движка). Хранится не для
    // красоты: состояние панели перечитывается каждую секунду, и без этой памяти причина отказа
    // жила бы ровно одно мгновение — человек нажал «Запустить» и не успел бы прочитать, почему
    // ничего не произошло. Держится, пока причина верна, и гаснет вместе с ней.
    private string _startRefusal = string.Empty;

    // Встроенный сервер: порт, номер процесса и время его создания. Время нужно, чтобы гашение
    // не попало в чужой процесс с тем же номером (номера переиспользуются).
    private int _adoptedPort;
    private int _adoptedPid;
    private long _adoptedStartTicks;

    private DateTime _lastScanAt = DateTime.MinValue;
    private DiscoveryResult _lastScan = DiscoveryResult.Nothing;

    /// <param name="workingDirectory">
    /// Функция, а не строка: рабочий каталог сервера — это НАСТРОЙКА, и она спрашивается
    /// в момент запуска. Иначе смена рабочей папки в окне настроек не подействовала бы
    /// до перезапуска панели, а обещано «при следующем запуске сервера».
    /// </param>
    /// <param name="requestedPort">
    /// Порт, который панель занимает, поднимая СВОЙ сервер, — тоже функция и тоже настройка.
    /// По умолчанию <see cref="ServerDecisions.DefaultServerPort"/>. Неподходящее значение
    /// (мусор или порт владельца) приводится к умолчанию, и об этом пишется строка в журнал:
    /// молча съехать на другой порт значит соврать человеку о том, где его сервер.
    /// </param>
    /// <param name="adoptedPort">
    /// Порт ЗАПОМНЕННОГО согласия человека на найденный сервер: <c>0</c> — согласия нет.
    ///
    /// Контроллер сервера про настройки не знает: он не читает их и не сохраняет, а спрашивает
    /// и записывает через эти два делегата. Так «где лежит согласие» остаётся делом вызывающего
    /// (у обычного запуска — файл настроек панели), и второй правды о нём не заводится.
    /// </param>
    /// <param name="rememberAdoptedPort">
    /// Записать согласие на порт (значение <c>0</c> — забыть). Хранилище может отказать
    /// (прогон без права писать настройки) — тогда согласия просто нет, и панель спрашивает
    /// подтверждение как раньше. Это ровно тот случай, ради которого подтверждение и осталось.
    /// </param>
    /// <param name="allowParallelStart">
    /// Право поднять СВОЙ сервер, когда рядом уже работает найденный DSH. По умолчанию
    /// <c>false</c>, то есть ЗАМОК ВКЛЮЧЁН: в обычном запуске панель и найденный сервер работают
    /// на одном `~/.dsh`, и второй движок там — это второй движок на тех же данных
    /// (находка 25.09.2026). Право выдаётся ЯВНО и открытым текстом, и просит его только тот,
    /// кому это действительно нужно: прогон в изоляции, где свой корень и свои данные.
    /// Считать его «из окружения» нельзя — именно так право и теряется незаметно.
    /// </param>
    /// <param name="mayOccupyOwnerPort">
    /// Право занять порт ВЛАДЕЛЬЦА (3080) — ЯВНЫЙ параметр и БЕЗ значения по умолчанию.
    ///
    /// Решение владельца 26.09.2026: панель 1.x уходит, и 2.0 поднимает свой сервер на 3080,
    /// то есть на том же порту, где стояла 1.x. Но через 3080 идёт канал сессии агента, и
    /// прогон проверки не имеет права его занять: без права <see cref="ServerDecisions.NormalizePort"/>
    /// приведёт 3080 к <see cref="ServerDecisions.RunFallbackPort"/>, и прогон встанет рядом,
    /// а не на канал. Значения по умолчанию нет намеренно: забыть выдать право безопасно
    /// (панель просто встанет на 3081), а забыть запретить — нет, и такой вызов обязан
    /// не собраться, а не «случиться молча».
    /// </param>
    /// <param name="files">
    /// Файлы состояния сервера — ссылка входа и запись «этот сервер наш». Делегатами, а не путями:
    /// контроллер не должен знать, где они лежат, иначе прогон проверки прочитал бы файлы владельца
    /// (см. <see cref="ServerStateFiles"/>). Не передан — <see cref="ServerStateFiles.None"/>.
    /// </param>
    /// <param name="foundServerEntryLink">
    /// Запасной источник ссылки входа — ТОЛЬКО ЧТЕНИЕ: файл панели 1.x (`web-url.txt`). У найденного
    /// сервера своей ссылки нет, а человеку кнопка нужна. Право читать чужой каталог — как у
    /// машинного окружения: выдаёт его <c>App.StartPanel</c> и только обычному запуску человеком.
    /// Не передан — источник молчит.
    /// </param>
    /// <param name="linkProbe">
    /// Шов для проверок: что ответил порт по чужой ссылке. В работе — настоящее измерение
    /// (<see cref="ServerDiscovery.ProbeLink"/>); правило годности — чистое
    /// (<see cref="EntryLinkDecisions.Accepts"/>), и оно перебирается тестами без сети.
    /// </param>
    public ServerController(
        string dshHome,
        Func<string> workingDirectory,
        Action<string> log,
        bool mayOccupyOwnerPort,
        Func<DshEngine?>? locateEngine = null,
        Func<int>? requestedPort = null,
        IProcessKiller? killer = null,
        ServerProbe? probe = null,
        TimeSpan? stopWait = null,
        TimeSpan? scanInterval = null,
        Func<int>? adoptedPort = null,
        Action<int>? rememberAdoptedPort = null,
        bool allowParallelStart = false,
        ServerStateFiles? files = null,
        Func<string>? foundServerEntryLink = null,
        Func<string, LinkProbe>? linkProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dshHome);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        _dshHome = dshHome;
        _workingDirectory = workingDirectory;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _locateEngine = locateEngine ?? (() => DshEngine.Locate());
        _requestedPort = requestedPort ?? (() => ServerDecisions.DefaultServerPort);
        _mayOccupyOwnerPort = mayOccupyOwnerPort;
        _killer = killer ?? new TreeProcessKiller();
        _probe = probe ?? ServerProbe.System();
        _stopWait = stopWait ?? DefaultStopWait;
        _scanInterval = scanInterval ?? DefaultScanInterval;
        _readAdoptedPort = adoptedPort;
        _rememberAdoptedPort = rememberAdoptedPort;
        _allowParallelStart = allowParallelStart;
        _files = files ?? ServerStateFiles.None;
        _readFoundServerLink = foundServerEntryLink;
        _linkProbe = linkProbe ?? ServerDiscovery.ProbeLink;

        State = ServerState.Stopped(0, PanelStrings.SrvNotStartedYet);
    }

    public ServerState State { get; private set; }

    public ServerOwner Owner { get; private set; } = ServerOwner.None;

    /// <summary>
    /// Согласие человека на ЭТОТ встроенный сервер запомнено. Сверяется и порт: согласие,
    /// данное серверу на 3080, не делает своим сервер на 3097, в который панель встроилась руками.
    /// </summary>
    public bool ConsentRemembered =>
        Owner == ServerOwner.Adopted && _adoptedPort > 0 && RememberedPort() == _adoptedPort;

    /// <summary>Порт, которым панель сейчас занята: свой или встроенный (0 — никакой).</summary>
    public int Port => ActivePort();

    /// <summary>Домашний каталог движка, которым пользуется этот контроллер (для отчётов проверок).</summary>
    public string DshHome => _dshHome;

    private int ActivePort()
    {
        lock (_gate) return _adoptedPort > 0 ? _adoptedPort : _port;
    }

    // --- состояние ---------------------------------------------------------

    public ServerState Refresh()
    {
        lock (_gate)
        {
            ForgetExitedProcess();

            // СВОЙ СЕРВЕР, УЗНАННЫЙ ПО ЗАПИСИ, — ДО ветки запомненного согласия. Панель при выходе
            // сервер не гасит, и после перезапуска её собственный сервер выглядел бы «найденным
            // чужим»: панель предложила бы в него «встроиться», а «Остановить» отказывалась бы.
            RestoreOwnServer();

            // Согласие, запомненное человеком, панель восстанавливает САМА: он уже ответил один раз
            // (решение владельца 26.09.2026), и переспрашивать — значит не исполнять его ответ.
            var restored = TryRestoreAdoption();
            if (restored is not null) return State = restored.Value;

            // Встроенный сервер мог завершиться сам. Это НЕ «сервер остановлен панелью»:
            // панель его не гасила, и путать эти два случая нельзя.
            if (Owner == ServerOwner.Adopted && _probe.StartTicks(_adoptedPid) == 0)
            {
                var gone = _adoptedPort;
                ClearAdopted();
                return State = ServerState.Stopped(
                    gone, string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedExitedFormat, gone));
            }

            var port = ActivePort();
            if (port <= 0)
            {
                // Отказ поднять свой сервер повторяется, пока он верен: панель перечитывает
                // состояние каждую секунду, и причина, живущая одно мгновение, — это «нажал,
                // и ничего не произошло» (нашла приёмка 26.09.2026). Причина гаснет вместе
                // с тем, из-за кого она была: найденный сервер ушёл — гипотеза перестала быть верной.
                if (_startRefusal.Length > 0)
                {
                    if (ForeignServerBlocker() is not null)
                        return State = ServerState.Stopped(0, _startRefusal);

                    _startRefusal = string.Empty;
                }

                return State = ServerState.Stopped(0, PanelStrings.SrvNotStartedYet);
            }

            var listener = _probe.ListenerPid(port);
            if (listener < 0)
            {
                // «Не смог узнать» — не то же самое, что «свободен». Врать человеку нельзя.
                return State = new ServerState(
                    ServerPresence.Stopped, port, 0, string.Empty,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvListenerUnknownFormat, port));
            }

            var answers = listener > 0 && _probe.AnswersFingerprint(port);
            var presence = ServerDecisions.Classify(listener, answers);

            var state = presence switch
            {
                ServerPresence.Running => new ServerState(
                    ServerPresence.Running, port, listener, ProcessName(listener),
                    RunningDetail(port)),

                ServerPresence.BusyByOther => new ServerState(
                    ServerPresence.BusyByOther, port, listener, ProcessName(listener),
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortBusyFormat, port, ProcessName(listener), listener)),

                _ => ServerState.Stopped(port, string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortFreeFormat, port)),
            };

            // Запасной источник ссылки входа прогревается ЗДЕСЬ, а не при первом её запросе.
            // Причина не косметическая: ссылку спрашивает окно при перерисовке, то есть на НИТКЕ
            // ИНТЕРФЕЙСА, а измерение годности ходит в сеть (до двух секунд ожидания). Осмотр
            // состояния идёт в ФОНЕ — значит и прогревать надо здесь.
            if (state.Presence == ServerPresence.Running && Owner == ServerOwner.Adopted)
                WarmForeignLink(port);

            return State = state;
        }
    }

    /// <summary>
    /// Узнать СВОЙ СЕРВЕР после перезапуска панели — по записи, оставленной при его запуске.
    ///
    /// Зачем это нужно. Панель при выходе сервер **не гасит** (так решено: закрытие окна не должно
    /// обрывать работу человека). Значит после перезапуска панели её собственный сервер продолжает
    /// работать, а живого дескриптора процесса у новой панели нет — и без записи он выглядел бы
    /// «найденным чужим»: панель предложила бы в него «встроиться», а «Остановить» отказывалась бы.
    ///
    /// Три сверки, и все обязательны: на записанном порту слушает **тот же** номер процесса,
    /// **время создания** совпадает (номера переиспользуются — урок v1) и порт отвечает
    /// отпечатком DSH. Совпало — сервер наш, хотя дескриптора и нет: метод проставляет
    /// <c>_port</c>, <c>_ownPid</c> и <c>Owner</c>, а состояние собирает обычный ход
    /// <see cref="Refresh"/> — второго сборщика состояния не заводится.
    ///
    /// Возвращает <c>void</c> намеренно: наружу он состояния не отдаёт (его собирает вызывающий
    /// тем же ходом), и подпись с <c>ServerState?</c> обещала бы то, чего метод не делает.
    /// </summary>
    private void RestoreOwnServer()
    {
        if (Owner != ServerOwner.None || _port > 0) return;

        var record = OwnServerRecord.Parse(ReadOwnServerFile());
        if (record is not { } saved) return;

        var listener = _probe.ListenerPid(saved.Port);

        // «Не смог узнать» — НЕ то же самое, что «сервера нет»: запись не трогаем, врать нельзя.
        if (listener < 0) return;

        if (listener == 0 || listener != saved.Pid)
        {
            ForgetStaleOwnRecord(saved);
            return;
        }

        if (!saved.Matches(listener, _probe.StartTicks(listener)))
        {
            // Номер тот же, а процесс ДРУГОЙ: номера переиспользуются, и по одному номеру панель
            // однажды погасила бы чужой процесс. Свой сервер ушёл — записи верить больше нечем.
            ForgetStaleOwnRecord(saved);
            return;
        }

        if (!_probe.AnswersFingerprint(saved.Port)) return;

        _port = saved.Port;
        _ownPid = saved.Pid;
        Owner = ServerOwner.Panel;

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.SrvOwnServerRestoredLogFormat, _port, _ownPid));
    }

    /// <summary>
    /// Снять запись о своём сервере, которая больше не верна: на записанном порту никого или уже
    /// ДРУГОЙ процесс. Снимается вместе со ссылкой входа — ссылка живёт ровно столько, сколько
    /// живёт сервер, и мёртвый адрес человеку показывать незачем.
    /// </summary>
    private void ForgetStaleOwnRecord(OwnServerRecord saved)
    {
        _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnServerStaleLogFormat, saved.Port));

        ClearOwnServerFiles();
        _port = 0;
        _ownPid = 0;
    }

    /// <summary>
    /// Что РЕАЛЬНО было снято. Нужно ровно затем, чтобы журнал не врал: у встроенного сервера
    /// своей ссылки не бывает вовсе, и строка «ссылка входа забыта» на нём — ложь о том, чего
    /// не было (нашёл живой прогон самотеста сервера 27.09.2026).
    /// </summary>
    private readonly record struct ClearedFiles(bool OwnRecord, bool EntryLink);

    /// <summary>
    /// Запись о своём сервере и ссылка входа — снимаются вместе: обе описывают ОДИН живой сервер.
    /// Отказ хранилища панель не роняет: строка в журнал, и всё.
    ///
    /// Возвращает, было ли что снимать: вызывающий говорит словами ТОЛЬКО о том, что было.
    /// </summary>
    private ClearedFiles ClearOwnServerFiles()
    {
        // Спрашиваем ДО уборки — и у файла, а не только у памяти: после перезапуска панели ссылка
        // лежит на диске и в память ещё не читалась.
        var hadLink = _entryLink.Trim().Length > 0 || ReadEntryLinkFile().Trim().Length > 0;
        var hadRecord = ReadOwnServerFile().Trim().Length > 0;

        _entryLink = string.Empty;
        _entryLinkLoaded = true;
        _fallbackCheckedPort = 0;
        _fallbackLink = string.Empty;

        try { _files.ClearOwnServer(); }
        catch (Exception ex) { Report(ex, PanelStrings.SrvOwnServerRecordFailedLogFormat); }

        try { _files.ClearEntryLink(); }
        catch (Exception ex) { Report(ex, PanelStrings.SrvEntryLinkSaveFailedLogFormat); }

        return new ClearedFiles(hadRecord, hadLink);
    }

    /// <summary>Строка файла «свой сервер» или пусто, если файла нет или он не читается.</summary>
    private string ReadOwnServerFile()
    {
        try
        {
            return _files.ReadOwnServer();
        }
        catch
        {
            // Файла нет или он занят — это «записи нет», а не повод не собирать состояние.
            return string.Empty;
        }
    }

    /// <summary>Строка файла ссылки входа или пусто.</summary>
    private string ReadEntryLinkFile()
    {
        try
        {
            return _files.ReadEntryLink();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Одна дверь для «сообщить о сбое хранилища»: исключение превращается в строку.</summary>
    private void Report(Exception error, string format) =>
        _log(string.Format(
            CultureInfo.CurrentCulture, format, $"{error.GetType().Name} — {error.Message}"));

    /// <summary>
    /// Взять под управление сервер, на который человек уже дал согласие: его порт лежит
    /// в настройках панели.
    ///
    /// Попытка повторяется при КАЖДОМ осмотре, а не один раз при старте: человек мог погасить
    /// свой DSH и поднять заново, и панель обязана узнать его снова. В журнал уходит только
    /// успех: неудачная попытка означает «сервер ещё не подняли», а не событие, и строка о ней
    /// каждые две секунды была бы шумом, в котором утонуло бы настоящее сообщение.
    ///
    /// Приметы проверяются заново тем же кодом, что и по кнопке (<see cref="AdoptCore"/>):
    /// запомнен ПОРТ, а не процесс, и на порту за это время мог оказаться кто угодно.
    /// </summary>
    private ServerState? TryRestoreAdoption()
    {
        if (Owner != ServerOwner.None) return null;

        var remembered = RememberedPort();
        if (remembered <= 0) return null;

        var found = Scan().Found.FirstOrDefault(server => server.Port == remembered);

        // Не нашли — молча: это «сервер ещё не подняли», а не отказ, о котором надо говорить.
        if (found.Port != remembered) return null;

        var state = AdoptCore(found, out _);
        if (Owner != ServerOwner.Adopted) return null;

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AdoptedConsentRestoredLogFormat, _adoptedPort));

        return state;
    }

    /// <summary>
    /// Порт запомненного согласия или <c>0</c>. Исключение из хранилища здесь гасится намеренно:
    /// значение спрашивается на каждом осмотре (раз в секунду), и падать из-за чужого хранилища
    /// панель не должна. Не прочиталось — значит согласия нет, и панель просто спросит, как раньше.
    /// </summary>
    private int RememberedPort()
    {
        try
        {
            return Math.Max(0, _readAdoptedPort?.Invoke() ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Записать порт согласия (<c>0</c> — забыть). Хранилище приходит делегатом: контроллер
    /// сервера про настройки не знает. Отказ хранилища не роняет панель и не врёт ей: не записалось —
    /// согласия нет, и подтверждение спрашивается как раньше.
    /// </summary>
    private void Remember(int port)
    {
        if (_rememberAdoptedPort is null) return;

        try
        {
            _rememberAdoptedPort(port);
        }
        catch (Exception ex)
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvRememberFailedFormat, ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// Забыть запомненное согласие, если оно было ровно про ЭТОТ порт: порт теперь НАШ.
    ///
    /// Согласие означало «на этом порту стоит найденный сервер, я разрешил взять его под
    /// управление». Панель подняла здесь СВОЙ — прежнее согласие перестало быть верным, и оставить
    /// его значило бы однажды принять за своё то, что на этот порт придёт после.
    /// </summary>
    private void ForgetConsentForOurPort(int port)
    {
        if (RememberedPort() != port) return;

        Remember(0);

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.SrvConsentForgottenOwnPortFormat, port));
    }

    /// <summary>
    /// Записать на диск «этот сервер наш»: порт, номер процесса и время его создания.
    ///
    /// Пишется при УСПЕШНОМ запуске своего процесса, а не при остановке: запись нужна ровно
    /// на случай «панель перезапустили, а сервер жив», и взяться ей тогда больше неоткуда.
    /// Отказ хранилища панель не роняет: сервер работает, а узнанным после перезапуска он
    /// не будет — об этом честно говорит строка в журнал.
    /// </summary>
    private void RecordOwnServer(int port)
    {
        // Писать некуда (прогон без права на файлы панели) — и говорить «записано» НЕЛЬЗЯ:
        // журнал читают, чтобы понять, что было на самом деле. О подавлении уже сказано одной
        // строкой при выдаче этих делегатов (`ServerStateFilesSuppressedLog`).
        if (!_files.Persists) return;

        var pid = _process?.Id ?? 0;
        var ticks = pid > 0 ? _probe.StartTicks(pid) : 0;

        if (pid <= 0 || ticks <= 0)
        {
            _log(PanelStrings.SrvOwnServerNoStartTimeLog);
            return;
        }

        var record = new OwnServerRecord(port, pid, ticks);

        try
        {
            _files.WriteOwnServer(record.Format());
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.SrvOwnServerRecordedLogFormat, port, pid));
        }
        catch (Exception ex)
        {
            Report(ex, PanelStrings.SrvOwnServerRecordFailedLogFormat);
        }
    }

    // --- ссылка входа ------------------------------------------------------

    /// <summary>
    /// Ссылка входа (в ней ТОКЕН) или пусто. Единственный выход секрета из контроллера —
    /// он же и вход в браузер, и больше нигде эта строка не показывается.
    ///
    /// Два источника, и порядок между ними не случаен:
    ///
    /// 1. **Своя ссылка** — её напечатал движок, которого подняла панель. Живёт в памяти и
    ///    в файле (файл — чтобы пережить перезапуск панели: сервер панель не гасит);
    /// 2. **Запасной источник — файл панели 1.x, ТОЛЬКО ЧТЕНИЕ.** Своей ссылки у НАЙДЕННОГО
    ///    сервера нет: панель его не поднимала и вывода движка не видела. А человеку, взявшему
    ///    такой сервер под управление, кнопка нужна — и 1.x свою ссылку записала. Берётся она
    ///    лишь тогда, когда порт в ней совпадает с портом сервера И порт по ней отвечает
    ///    (<see cref="EntryLinkDecisions.UsableFallback"/>).
    /// </summary>
    public string EntryLink
    {
        get
        {
            var own = OwnEntryLink();
            return own.Length > 0 ? own : ForeignEntryLink();
        }
    }

    /// <summary>Своя ссылка. Пусто, если сервер не наш или ссылки у него нет.</summary>
    private string OwnEntryLink()
    {
        int port;
        string memory;

        lock (_gate)
        {
            if (Owner != ServerOwner.Panel || _port <= 0) return string.Empty;

            port = _port;
            memory = _entryLink;

            // Файл читается ОДИН раз за прогон: второго писателя у него нет, а спрашивают ссылку
            // каждую секунду (окно перерисовывается). Прочитанное остаётся в памяти.
            if (memory.Length == 0 && !_entryLinkLoaded)
            {
                _entryLinkLoaded = true;
                memory = EntryLinkDecisions.ReadFile(ReadEntryLinkFile()).Url;
            }
        }

        if (!EntryLinkDecisions.MatchesPort(memory, port)) return string.Empty;

        lock (_gate) _entryLink = memory;
        return memory;
    }

    /// <summary>
    /// Ссылка найденного (взятого под управление) сервера — из файла панели 1.x, и только если
    /// порт совпадает и порт принимает ссылку.
    ///
    /// Здесь только ЧТЕНИЕ ЗАПАСЕННОГО: измерение годности ходит в сеть и делается в
    /// <see cref="WarmForeignLink"/> — то есть в фоне, вместе с осмотром состояния. Спрашивать
    /// сеть отсюда значило бы подвесить окно на время запроса.
    /// </summary>
    private string ForeignEntryLink()
    {
        lock (_gate)
        {
            if (Owner != ServerOwner.Adopted) return string.Empty;

            var port = ActivePort();
            return port > 0 && _fallbackCheckedPort == port ? _fallbackLink : string.Empty;
        }
    }

    /// <summary>
    /// Прогреть запасной источник ссылки: прочитать файл панели 1.x, измерить, принимает ли порт
    /// эту ссылку, и запомнить ответ ПО ПОРТУ. Запоминается и отрицательный ответ: измерение ходит
    /// в сеть, а осмотр состояния идёт раз в секунду, и без памяти панель делала бы HTTP-запрос
    /// каждую секунду на ровном месте.
    /// </summary>
    private void WarmForeignLink(int port)
    {
        if (port <= 0 || _readFoundServerLink is null) return;
        if (_fallbackCheckedPort == port) return;

        string candidate;
        try
        {
            candidate = EntryLinkDecisions.ReadFile(_readFoundServerLink()).Url;
        }
        catch
        {
            // Чужой каталог может быть недоступен — это «ссылки нет», а не повод не показывать панель.
            candidate = string.Empty;
        }

        // ХОСТ и ПОРТ проверяются ДО сети. Файл панели 1.x чужой: в нём может стоять любой адрес,
        // а в ссылке — токен входа. Спроси мы сеть по такой ссылке, токен уже ушёл бы на чужой хост,
        // и «не годна» было бы сказано после утечки (красная линия 7).
        var local = EntryLinkDecisions.MatchesPort(candidate, port)
                    && EntryLinkDecisions.IsLoopbackHost(candidate);

        var probe = local ? _linkProbe(candidate) : LinkProbe.Failed2;
        var link = EntryLinkDecisions.UsableFallback(candidate, port, probe) ? candidate : string.Empty;

        _fallbackCheckedPort = port;
        _fallbackLink = link;

        if (link.Length > 0)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.SrvEntryLinkFromV1LogFormat,
                EntryLinkDecisions.Redacted(link)));
        }
    }

    /// <summary>
    /// Запомнить ссылку входа. Вызывается с СЫРОЙ строкой вывода движка (токен ещё внутри):
    /// маскирование делает журнал, а не этот путь — иначе сохранять было бы нечего.
    /// </summary>
    private void SaveEntryLink(string url)
    {
        lock (_gate)
        {
            _entryLink = url;
            _entryLinkLoaded = true;
        }

        try
        {
            if (_files.Persists) _files.WriteEntryLink(url);
        }
        catch (Exception ex)
        {
            Report(ex, PanelStrings.SrvEntryLinkSaveFailedLogFormat);
        }

        // В журнал — ТОЛЬКО обезвреженная строка: токен в журнал не попадает никогда (красная линия 7).
        // ⚠️ И сказано ровно то, что было: «в памяти» — когда писать некуда (прогон без права).
        // «Сохранена» на несохранённой ссылке — это ложь в журнале, а журнал читают, чтобы понять,
        // что было на самом деле.
        _log(string.Format(
            CultureInfo.CurrentCulture,
            _files.Persists
                ? PanelStrings.SrvEntryLinkFoundLogFormat
                : PanelStrings.SrvEntryLinkFoundInMemoryLogFormat,
            EntryLinkDecisions.Redacted(url)));
    }

    /// <summary>
    /// Строка состояния работающего сервера.
    ///
    /// Для встроенного она называет главное: панель его не поднимала. Но **только пока согласие
    /// не запомнено**. Человек, однажды ответивший «беру под управление» (решение владельца
    /// 26.09.2026), считает это окружение своим, и строка «панель встроена… поднят не панелью»
    /// висела бы у него постоянно, называя его же решение чужим. Правда при этом не теряется:
    /// она остаётся в журнале, в отчёте окружения в настройках (<c>ServerAdoptedFormat</c>) и
    /// в карточке найденного сервера — там строки не менялись.
    /// </summary>
    private string RunningDetail(int port) => Owner switch
    {
        ServerOwner.Adopted when ConsentRemembered =>
            string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAnswersFormat, port),

        ServerOwner.Adopted =>
            string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAnswersAdoptedFormat, port),

        _ => string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAnswersFormat, port),
    };

    // --- разведка: найти чужой сервер --------------------------------------

    public DiscoveryResult Scan()
    {
        int ownPid;
        int adoptedPid;

        lock (_gate)
        {
            ownPid = OwnProcessId();

            // Свой сервер, узнанный ПО ЗАПИСИ после перезапуска панели: живого дескриптора нет,
            // и без этого исключения панель нашла бы САМА СЕБЯ и предложила в себя «встроиться».
            if (ownPid <= 0 && Owner == ServerOwner.Panel) ownPid = _ownPid;

            adoptedPid = _adoptedPid;
        }

        // Замок разведки — ОТДЕЛЬНЫЙ: отпечаток по сети занимает до двух секунд, и держать
        // на это время общий замок значит подвесить окно, которое просто читает состояние.
        lock (_scanGate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastScanAt < _scanInterval) return _lastScan;
            _lastScanAt = now;

            var exclude = new HashSet<int>();
            if (ownPid > 0) exclude.Add(ownPid);
            if (adoptedPid > 0) exclude.Add(adoptedPid);

            return _lastScan = ServerDiscovery.Find(_probe, exclude);
        }
    }

    public ServerState Adopt(FoundServer found)
    {
        lock (_gate)
        {
            var state = AdoptCore(found, out var refusal);

            if (refusal.Length > 0)
            {
                _log(refusal);
                return state;
            }

            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedLogFormat,
                found.Port, found.ProcessName, found.Pid));

            // Согласие человека запоминается ЗДЕСЬ и живёт в настройках: он ответил один раз,
            // и панель обязана помнить это после перезапуска. Забыть встроенный сервер, не найдя
            // его живым, нельзя — поэтому пишем порт только тогда, когда встраивание состоялось.
            if (Owner == ServerOwner.Adopted && _adoptedPort == found.Port) Remember(_adoptedPort);

            return state;
        }
    }

    /// <summary>
    /// Ядро встраивания: приметы проверяются ЗАНОВО, потому что между находкой и согласием
    /// человека (или между запусками панели и восстановлением согласия) мир мог измениться —
    /// сервер мог завершиться, а порт достаться другой программе.
    ///
    /// Журнала не пишет и согласие не запоминает: слова у кнопки человека и у восстановления
    /// запомненного согласия разные, а неудачная попытка восстановления вообще не событие.
    /// </summary>
    private ServerState AdoptCore(FoundServer found, out string refusal)
    {
        refusal = string.Empty;

        if (Owner != ServerOwner.None)
        {
            refusal = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAlreadyManagedFormat, ActivePort());
            return State;
        }

        var listener = _probe.ListenerPid(found.Port);
        if (listener != found.Pid || !_probe.AnswersFingerprint(found.Port))
        {
            refusal = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptGoneFormat, found.Port);
            return State = new ServerState(
                ServerPresence.Stopped, 0, 0, string.Empty,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptNothingFormat, found.Port));
        }

        var startedAt = _probe.StartTicks(found.Pid);
        if (startedAt == 0)
        {
            refusal = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptNoStartTimeFormat, found.Pid);
            return State = new ServerState(
                ServerPresence.Stopped, 0, 0, string.Empty,
                PanelStrings.SrvAdoptNoProcessInfo);
        }

        _adoptedPort = found.Port;
        _adoptedPid = found.Pid;
        _adoptedStartTicks = startedAt;
        Owner = ServerOwner.Adopted;

        return Refresh();
    }

    public ServerState Detach()
    {
        lock (_gate)
        {
            if (Owner != ServerOwner.Adopted) return State;

            var port = _adoptedPort;
            ClearAdopted();

            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvDetachedLogFormat, port));

            // Отвязка — единственный способ ЗАБЫТЬ согласие: человек сказал «больше не твоё»,
            // и панель обязана спросить заново. Сам сервер при этом жив — отвязка не гасит.
            Remember(0);

            return State = ServerState.Stopped(
                0, string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvDetachedFormat, port));
        }
    }

    // --- запуск и остановка ------------------------------------------------

    public ServerState Start(TimeSpan timeout)
    {
        lock (_gate)
        {
            // Уже запускаем или уже работает — второй сервер не нужен (в v1 автостарт и кнопка
            // шли параллельно и открывали человеку две вкладки).
            if (_process is not null && !_process.HasExited) return Refresh();

            // Панель уже управляет найденным сервером: он работает, и второй движок на тех же
            // данных не поднимаем. Отдельная ветка, потому что разведка исключает встроенный
            // сервер из находки — на замок ниже он не попал бы вовсе.
            if (Owner == ServerOwner.Adopted) return Refresh();

            // ЗАМОК ОТ ВТОРОГО ДВИЖКА (находка 25.09.2026). Найденный работающий DSH — это, как
            // правило, сервер самого человека, и данные у него те же самые (`~/.dsh`): движок
            // при старте перезаписывает `profiles/web/cordis.yml`, а в профиле стоит
            // `patchReload: live` — то есть второй движок трогает текущую работу владельца.
            // Поэтому панель отказывается, а не «поднимает рядом»: человека она отправляет
            // к кнопке «Взять под управление» — она в этот момент и видна.
            var blocker = ForeignServerBlocker();
            if (blocker is not null)
            {
                return Refuse(string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.ParallelStartRefusedFormat,
                    blocker.Value.Port));
            }

            // Замок пропустил — прежний отказ больше не верен, и показывать его нельзя.
            _startRefusal = string.Empty;

            var engine = _locateEngine();
            if (engine is null)
            {
                return Fail(PanelStrings.SrvNoEngine);
            }

            var requested = _requestedPort();

            // Право занять порт владельца приходит ЯВНО (<c>mayOccupyOwnerPort</c>), и здесь оно
            // и решает: у обычного запуска человеком 3080 остаётся собой (решение владельца
            // 26.09.2026 — 2.0 встаёт на место 1.x), у прогона проверки приводится к 3081.
            var port = ServerDecisions.NormalizePort(requested, _mayOccupyOwnerPort);
            if (port != requested)
                _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortReplacedLogFormat, requested, port));

            // Порт, который прямо сейчас нельзя занять, движку не подойдёт: он упадёт с EADDRINUSE
            // и не переселится (docs\ENGINE.md §5). Молча съехать на другой порт нельзя тем более:
            // человек ищет свой сервер по адресу, и «вроде порт тот, а сервера нет» — хуже отказа.
            if (!FreePort.CanBind(port, _mayOccupyOwnerPort))
            {
                return Fail(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortBusyStartFormat, port));
            }

            _port = port;
            ClearAdopted();
            _engineTail.Clear();
            _lastEngineLine = string.Empty;
            _exitReported = false;

            // Рабочий каталог спрашивается ЗДЕСЬ — в момент запуска сервера, а не при построении
            // контроллера: это настройка, и она обязана применяться без перезапуска панели.
            var workDir = ResolveWorkingDirectory();

            if (!TryStartProcess(engine, port, workDir, out var error))
            {
                _port = 0;
                return Fail(error);
            }

            Owner = ServerOwner.Panel;
            _ownPid = OwnProcessId();

            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.SrvEngineStartedLogFormat,
                _process!.Id, port, _dshHome, DshPanel.Shell.DisplayMask.Path(workDir)));

            // Порт теперь НАШ — и если он же лежал в запомненном согласии на найденный сервер,
            // прежнее согласие больше не верно: оно было про «найденный сервер», а на этом порту
            // теперь стоит наш собственный. Оставить его — значит однажды принять за него чужой.
            ForgetConsentForOurPort(port);

            // Запись «этот сервер наш» — на диск, а не только в память: панель при выходе сервер
            // не гасит, и после её перезапуска эта запись и есть единственный способ узнать свой.
            RecordOwnServer(port);

            return WaitUntilRunning(timeout);
        }
    }

    /// <summary>
    /// Каталог, из которого поднимется движок. Пустая настройка означает «папка панели» —
    /// ровно так было и в v1 (`serverWorkingDir`: «Пусто — папка панели»).
    /// </summary>
    private string ResolveWorkingDirectory()
    {
        var configured = _workingDirectory();
        return string.IsNullOrWhiteSpace(configured) ? _dshHome : configured;
    }

    private bool TryStartProcess(DshEngine engine, int port, string workDir, out string error)
    {
        error = string.Empty;

        var start = new ProcessStartInfo(engine.NodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        start.ArgumentList.Add(engine.BinPath);
        start.ArgumentList.Add("web");
        start.ArgumentList.Add("--no-open");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));

        // Домашний каталог движка — СВОЙ. Это и есть изоляция: панель не касается ~/.dsh владельца.
        start.Environment["DSH_HOME"] = _dshHome;

        // PATH собираем сами: движок зовёт npm и pnpm, а панель могла быть запущена раньше, чем
        // Node появился в окружении (урок v1: после установки сервер не поднимался до перезапуска).
        var nodeDir = Path.GetDirectoryName(engine.NodePath) ?? string.Empty;
        var npmDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var path = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        start.Environment["Path"] = $"{nodeDir};{npmDir};{path}";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);

        try
        {
            if (!process.Start())
            {
                error = PanelStrings.SrvEngineStartRefused;
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            error = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineStartFailedFormat, ex.GetType().Name, ex.Message);
            process.Dispose();
            return false;
        }

        _process = process;
        return true;
    }

    /// <summary>
    /// Ждать, пока сервер ответит. Терпеливо и без сдачи: первый запуск в чистом профиле
    /// достраивает его около минуты, и это НЕ ошибка (ровно на этом v1 потеряла 11 прогонов).
    /// </summary>
    private ServerState WaitUntilRunning(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            var current = Refresh();
            if (current.IsRunning) return current;

            if (_process is null || _process.HasExited) break;
        }

        // Процесс жив, но ещё не ответил — значит достраивает профиль. Панель не сдаётся
        // и не гасит его: окно продолжает спрашивать состояние, и сервер появится сам.
        if (_process is not null && !_process.HasExited)
        {
            var detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineStillStartingFormat, _port);
            return State = ServerState.Stopped(_port, detail);
        }

        ForgetExitedProcess();
        return Fail(PanelStrings.SrvEngineExited);
    }

    public ServerState Stop(bool confirmed)
    {
        lock (_gate)
        {
            if (Owner == ServerOwner.Adopted) return StopAdopted(confirmed);

            var process = _process;

            // СВОЙ сервер, узнанный ПО ЗАПИСИ после перезапуска панели: живого дескриптора
            // у панели нет. Без этой ветки «Остановить» отказывалась бы гасить СВОЙ же сервер —
            // а он её, и только её, и слушается.
            if (process is null && Owner == ServerOwner.Panel) return StopRecordedOwn();

            if (process is null)
            {
                Refresh();

                // Сервер на порту есть, но поднят не панелью и под управление не взят.
                if (State.Presence == ServerPresence.Running)
                {
                    return State = State with
                    {
                        Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvForeignRunningFormat, State.Port),
                    };
                }

                return State;
            }

            var port = _port;
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvStoppingEngineLogFormat, process.Id));

            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(20_000);
            }
            catch (Exception ex)
            {
                _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvStopEngineFailedLogFormat, ex.GetType().Name, ex.Message));
            }

            process.Dispose();
            _process = null;
            Owner = ServerOwner.None;
            _ownPid = 0;

            // Свой сервер погашен — запись «этот сервер наш» и ссылка входа больше не верны:
            // ссылка живёт ровно столько, сколько живёт сервер. Строка говорится ТОЛЬКО о том,
            // что было: у сервера, поднятого без ссылки (её ещё не напечатали), снимать нечего.
            if (ClearOwnServerFiles().EntryLink) _log(PanelStrings.SrvEntryLinkClearedLog);

            // Ждём, пока порт действительно освободится: «убили процесс» и «порт свободен» —
            // разные утверждения, и человеку панель обязана показать второе.
            var released = WaitForPortRelease(port);
            _port = 0;

            var state = released
                ? ServerState.Stopped(0, PanelStrings.SrvStopped)
                : new ServerState(ServerPresence.BusyByOther, port, 0, string.Empty,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortStillBusyFormat, port));

            _log(released
                ? PanelStrings.SrvStoppedPortFreeLog
                : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortLeftBusyFormat, port));
            return State = state;
        }
    }

    /// <summary>
    /// Гашение ВСТРОЕННОГО сервера. Оно устроено отдельно от гашения своего, и это главное
    /// решение шага: чужой сервер панель гасит только по подтверждению, спрошенному каждый раз
    /// (решение владельца 24.09.2026 — «одна кнопка с предупреждением один раз» отвергнуто).
    ///
    /// Без подтверждения и без запомненного согласия не происходит НИЧЕГО: не «гасим, но жалуемся
    /// в журнал». Это проверяется тестом с подставным убийцей — с настоящим такая проверка убила бы
    /// свой же процесс.
    ///
    /// ⚠️ С 26.09.2026 у правила ровно одно исключение, и оно тоже решение владельца: если согласие
    /// на этот сервер ЗАПОМНЕНО (<see cref="ConsentRemembered"/>), человек уже ответил «беру под
    /// управление» — и панель гасит свободно. Свободное гашение даёт РОВНО запомненное согласие:
    /// не записалось (прогон без права писать настройки) — спрашиваем, как раньше.
    /// </summary>
    private ServerState StopAdopted(bool confirmed)
    {
        var port = _adoptedPort;

        if (!confirmed && !ConsentRemembered)
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedStopUnconfirmedLogFormat, port));
            Refresh();

            return State = State with
            {
                Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedNeedsConfirmFormat, port),
            };
        }

        // Перечитываем состояние ПЕРЕД гашением: между согласием человека и этим мгновением
        // сервер мог завершиться, а порт — достаться другой программе. Гасить чужое нельзя.
        Refresh();

        if (State.Presence != ServerPresence.Running)
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvNothingToStopLogFormat, port));
            ClearAdopted();
            return State with { Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvGoneNothingToStopFormat, port) };
        }

        var pid = State.Pid;
        _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvStoppingAdoptedLogFormat, State.ProcessName, pid, port));

        if (!_killer.Kill(pid, _adoptedStartTicks, out var error))
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedStopFailedLogFormat, error));
            return State = State with
            {
                Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedStopFailedFormat, port, error),
            };
        }

        ClearAdopted();
        _port = 0;

        // Согласие здесь НЕ забывается намеренно: человек сказал, что это окружение его. Когда он
        // поднимет свой DSH снова, панель возьмёт его под управление молча — ровно это и значит
        // «спросить один раз». Забывает согласие только «Отвязаться».

        var released = WaitForPortRelease(port);
        _log(released
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedStoppedPortFreeFormat, port)
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvAdoptedStoppedPortBusyFormat, port));

        return State = released
            ? ServerState.Stopped(0, PanelStrings.SrvAdoptedStopped)
            : new ServerState(ServerPresence.BusyByOther, port, 0, string.Empty,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortStillBusyFormat, port));
    }

    /// <summary>
    /// Гашение СВОЕГО сервера, узнанного по записи: живого дескриптора у панели нет, потому что
    /// панель между запуском сервера и этим мгновением перезапускали.
    ///
    /// Гасится ТЕМ ЖЕ способом и с ТОЙ ЖЕ сверкой, что встроенный: номер процесса ВМЕСТЕ со
    /// временем его создания (<see cref="IProcessKiller"/>). Меньшего здесь не хватило бы:
    /// номера переиспользуются, и по одному номеру панель однажды погасила бы чужой процесс
    /// (урок v1). Сверка берётся у ЗАПИСИ, а не у живого опроса: запись сделана в момент запуска,
    /// и именно она говорит, какой процесс был нашим.
    ///
    /// Ошибка гашения — словами и в журнал, и в состояние: молчание здесь означало бы «нажал,
    /// и ничего не произошло».
    /// </summary>
    private ServerState StopRecordedOwn()
    {
        var port = _port;
        var saved = OwnServerRecord.Parse(ReadOwnServerFile());

        if (saved is not { } record || record.Port != port)
        {
            // Гасить нечем: записи нет или она про другой порт. Это НЕ «всё в порядке» —
            // человек нажал «Остановить», и он обязан узнать, почему ничего не вышло.
            _log(PanelStrings.SrvOwnRecordMissingLog);
            _ownPid = 0;
            Owner = ServerOwner.None;
            return State = ServerState.Stopped(0, PanelStrings.SrvOwnRecordMissingLog);
        }

        Refresh();

        if (State.Presence != ServerPresence.Running)
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnGoneNothingToStopFormat, port));
            ClearOwnServerFiles();
            _port = 0;
            _ownPid = 0;
            Owner = ServerOwner.None;

            return State = State with
            {
                Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnGoneNothingToStopFormat, port),
            };
        }

        var pid = State.Pid;
        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.SrvStoppingOwnByRecordLogFormat, State.ProcessName, pid, port));

        if (!_killer.Kill(pid, record.StartTicks, out var error))
        {
            _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnStopFailedLogFormat, error));
            return State = State with
            {
                Detail = string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnStopFailedFormat, port, error),
            };
        }

        var cleared = ClearOwnServerFiles();
        _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvOwnServerClearedLogFormat, port));

        // Ссылка была — и о ней сказано; не было — и говорить нечего (см. ClearedFiles).
        if (cleared.EntryLink) _log(PanelStrings.SrvEntryLinkClearedLog);

        _port = 0;
        _ownPid = 0;
        Owner = ServerOwner.None;

        var released = WaitForPortRelease(port);
        _log(released
            ? PanelStrings.SrvStoppedPortFreeLog
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortLeftBusyFormat, port));

        return State = released
            ? ServerState.Stopped(0, PanelStrings.SrvStopped)
            : new ServerState(ServerPresence.BusyByOther, port, 0, string.Empty,
                string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvPortStillBusyFormat, port));
    }

    /// <summary>
    /// Перезапуск — это «остановить» плюс «запустить», и предохранитель у него ТОТ ЖЕ.
    ///
    /// <paramref name="confirmed"/> сначала уходит в <see cref="Stop"/>: у ВСТРОЕННОГО сервера
    /// (взятого под управление) без подтверждения человека и без запомненного согласия не
    /// происходит НИЧЕГО — панель не гасит чужой процесс, через который может идти текущая работа
    /// человека. Тогда перезапуск возвращает ровно то же состояние и пишет ту же строку журнала,
    /// что неподтверждённая остановка, и НЕ поднимает ничего: это не «перезапуск вполсилы»,
    /// а тот же отказ (красная линия 6).
    ///
    /// С подтверждением перезапуск ВСТРОЕННОГО сервера значит: он гасится (как «Остановить»),
    /// и на его место поднимается СВОЙ сервер панели — на её настроенном порту (у обычного запуска
    /// это тот же 3080, где и стоял найденный). Ссылка входа берётся заново ОБЫЧНЫМ путём: её
    /// печатает движок, и ловит её <see cref="Start"/> (<see cref="WaitUntilRunning"/>) — своего
    /// способа для ссылки перезапуск не заводит.
    /// </summary>
    public ServerState Restart(TimeSpan timeout, bool confirmed)
    {
        lock (_gate)
        {
            if (Owner == ServerOwner.Adopted && !confirmed && !ConsentRemembered)
                return StopAdopted(confirmed: false);

            Stop(confirmed);
            return Start(timeout);
        }
    }

    // --- служебное ---------------------------------------------------------

    /// <summary>Последние строки вывода движка (уже с вырезанным токеном) — для отчётов проверок.</summary>
    public IReadOnlyList<string> EngineTail()
    {
        lock (_gate) return _engineTail.ToArray();
    }

    /// <summary>
    /// Порт освободился? «Убили процесс» и «порт свободен» — разные утверждения (та же грабля,
    /// что и в v1), и человеку панель показывает второе.
    /// </summary>
    private bool WaitForPortRelease(int port)
    {
        var deadline = DateTime.UtcNow + _stopWait;
        while (DateTime.UtcNow < deadline && _probe.ListenerPid(port) != 0)
            Thread.Sleep(250);

        return _probe.ListenerPid(port) == 0;
    }

    /// <summary>Забыть встроенный сервер: панель больше им не управляет и гасить его не станет.</summary>
    private void ClearAdopted()
    {
        _adoptedPort = 0;
        _adoptedPid = 0;
        _adoptedStartTicks = 0;
        Owner = ServerOwner.None;
    }

    private int OwnProcessId()
    {
        try
        {
            return _process is not null && !_process.HasExited ? _process.Id : 0;
        }
        catch
        {
            return 0;
        }
    }

    private ServerState Fail(string detail)
    {
        var suffix = _lastEngineLine.Length == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineLineSuffixFormat, _lastEngineLine);
        _log(detail + suffix);
        _port = 0;
        Owner = ServerOwner.None;
        return State = ServerState.Stopped(0, detail);
    }

    /// <summary>
    /// Отказ поднять СВОЙ сервер. Отдельно от <see cref="Fail"/>: тот объясняет неудачу ЗАПУСКА
    /// и приписывает к причине последнюю строку движка, а здесь движок и не запускался —
    /// чужая строка из прошлого запуска только запутала бы человека.
    /// </summary>
    private ServerState Refuse(string detail)
    {
        _log(detail);
        _startRefusal = detail;
        _port = 0;
        Owner = ServerOwner.None;
        return State = ServerState.Stopped(0, detail);
    }

    /// <summary>
    /// Найденный работающий DSH, из-за которого панель НЕ поднимает свой, или <c>null</c>, если
    /// поднимать можно. Свой процесс и сервер под управлением в находку не попадают — их исключает
    /// разведка, и это правильно: они не «чужие». Право «поднимать рядом» приходит ЯВНО
    /// (<c>allowParallelStart</c>), а не выводится из окружения: выведенное право однажды
    /// теряется незаметно.
    /// </summary>
    private FoundServer? ForeignServerBlocker()
    {
        var found = Scan();
        if (!ServerDecisions.BlockedByForeignServer(_allowParallelStart, found.Any)) return null;

        return found.First;
    }

    private void ForgetExitedProcess()
    {
        if (_process is null || !_process.HasExited) return;

        if (!_exitReported)
        {
            _exitReported = true;
            _log(_lastEngineLine.Length == 0
                ? string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineExitedItselfFormat, _process.ExitCode)
                : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineExitedItselfDetailFormat, _process.ExitCode, _lastEngineLine));
        }

        _process.Dispose();
        _process = null;
        if (Owner == ServerOwner.Panel) Owner = ServerOwner.None;

        // Сервер ушёл сам — запись «этот сервер наш» и ссылка входа перестали быть верными.
        // Не снять их значило бы показывать человеку мёртвый адрес и держать «свой» сервер,
        // которого нет.
        _ownPid = 0;
        ClearOwnServerFiles();
    }

    /// <summary>
    /// Строка вывода движка входит в панель ЗДЕСЬ и только здесь: сюда её приносят оба потока
    /// процесса (<c>OutputDataReceived</c> и <c>ErrorDataReceived</c>).
    ///
    /// <c>internal</c>, а не <c>private</c>, — потому что это ШОВ: доказать, что ссылка входа
    /// ловится ДО маскирования и не утекает в журнал, можно только подав сюда настоящую строку
    /// движка. Через процесс этого не сделать: командную строку движка панель собирает сама,
    /// и подставить в неё свой вывод нечем. Второго способа подсунуть панели вывод движка
    /// не заводится — снаружи сборки этого шва не существует.
    /// </summary>
    internal void Remember(string? line)
    {
        if (line is null) return;

        // ССЫЛКА ВХОДА ловится ДО маскирования, и это не порядок ради порядка: в строке движка
        // лежит токен, маскирование его вырезает, и из «token=***» ссылку уже не собрать. Сюда
        // приходит СЫРАЯ строка вывода — единственное место в панели, где токен вообще виден.
        var link = EntryLinkDecisions.Parse(line);

        var redacted = EngineLog.Redact(line);
        if (redacted.Length == 0) return;

        lock (_gate)
        {
            // Ссылка принимается, только если она про НАШ порт: движок мог напечатать адрес
            // прежнего запуска, а открыть человеку вчерашний адрес хуже, чем не открыть.
            if (link.Length > 0 && !EntryLinkDecisions.MatchesPort(link, _port)) link = string.Empty;

            _lastEngineLine = redacted;
            _engineTail.Enqueue(redacted);
            while (_engineTail.Count > RememberedEngineLines) _engineTail.Dequeue();
        }

        if (link.Length > 0) SaveEntryLink(link);

        _log(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineLineLogFormat, redacted));
    }

    private string ProcessName(int pid)
    {
        var name = _probe.ProcessName(pid);
        return name.Length > 0 ? name : PanelStrings.SrvProcessUnknown;
    }

    /// <summary>
    /// Закрытие панели сервер НЕ гасит: он живёт своей жизнью, как и в v1 — иначе закрытие окна
    /// обрывало бы человеку работу. Освобождаем только дескриптор процесса.
    ///
    /// ⚠️ **Запись «этот сервер наш» при выходе НЕ снимается** — ровно ради этого случая: следующая
    /// панель по ней узнает свой работающий сервер. Снимается она при остановке сервера и при его
    /// самостоятельном уходе, и больше нигде.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
            Owner = ServerOwner.None;
        }
    }
}
