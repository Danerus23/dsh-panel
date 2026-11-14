using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ИТОГ САМОТЕСТА КАЛИТКИ: код возврата, итоговая строка и ВСЁ, что прогон сказал.
///
/// <see cref="Checks"/> и <see cref="Failed"/> — ЧИСЛА, а не «сколько получилось»: по ним приёмка
/// отличает «двенадцать проверок прошли» от «проверок не было вовсе, и потому всё зелено».
/// </summary>
public sealed record PhoneSelfTestResult(int Code, string Title, IReadOnlyList<string> Report)
{
    /// <summary>Сколько строк отчёта влияют на исход (то есть «что-то проверили»).</summary>
    public int Checks { get; init; }

    /// <summary>Сколько из них провалилось.</summary>
    public int Failed { get; init; }
}

/// <summary>
/// САМОТЕСТ КАЛИТКИ ТЕЛЕФОНА (<c>--phone-selftest</c>, этап F1.e) — живая проверка наружной двери
/// панели ТОЛЬКО В ИЗОЛЯЦИИ.
///
/// **Зачем он отдельным ключом.** Калитка — первая вещь панели, которую видит **другое устройство**,
/// и проверить её «на глазок» нечем: страница отдаётся браузеру, сопряжение идёт кодом, а вопрос
/// агента приходит кадром из потока движка. Соседи (сервер, копии, обновление) устроены так же —
/// ключ, числа, код возврата.
///
/// **Что здесь НАСТОЯЩЕЕ, а что подставное — и это главное в файле.**
///
///   * НАСТОЯЩИЙ слушатель: <see cref="PhoneGate"/> поднимается на свободном порту петли
///     (связывание на <c>0</c>), и все запросы идут по-настоящему — TCP, HTTP, cookie;
///   * НАСТОЯЩИЙ файл пропуска под корнем прогона: сопряжение, отвязка устройства и «тот же
///     cookie больше не пускает» проверяются по ДИСКУ, а не по памяти процесса;
///   * НАСТОЯЩИЙ движок: он поднимается под корнем прогона со своим <c>DSH_HOME</c>
///     (как <c>--server-selftest</c>), и подписка на <c>$events</c> открывается в него по-настоящему;
///   * подставные ДВЕРИ — ровно там, где живого не бывает: вопрос агента требует ключа модели
///     (замер <c>docs\PHONE-LIVE.md</c> §4), поэтому кадры <c>ready</c>/<c>waterfall</c>/<c>cancel</c>
///     подаются по заказу проверки.
///
/// ⚠️ **Порты <c>43127</c>, <c>3080</c> и <c>3081</c> здесь не занимаются НИКОГДА**: все порты
/// берутся свободными у системы (<see cref="FreePort"/>), а запрет объявлен решением
/// (<see cref="PhoneGate.PortForbidden"/>) и проверяется числами. Кадр <c>ready</c> от живого
/// движка отдаёт домашний каталог машины (замер §3) — поэтому в отчёт из него не берётся НИЧЕГО,
/// кроме «пришёл» и длины <c>clientId</c>; а на подставных кадрах отдельно проверяется, что
/// домашний каталог не виден ни в одном ответе телефону и не уходит в движок.
///
/// **Коды возврата** — как у соседей: <c>0</c> успех, <c>1</c> провал, <c>2</c> неприменимо
/// (нет изоляции либо на машине нет движка, а без него главная часть F1 не прогонялась).
/// ⚠️ «Нет движка» — это НЕ успех.
/// </summary>
public static class PhoneSelfTest
{
    /// <summary>Сколько ждём ответа сервера панели на своём порту. Как у самотеста сервера.</summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(240);

    /// <summary>
    /// ПРОГНАТЬ САМОТЕСТ. Зовётся из <c>Program.Main</c> и из проверок.
    ///
    /// <paramref name="runRoot"/> — корень изоляции; <c>null</c> или пусто — прогон без изоляции,
    /// и это отказ кодом 2 СЛОВАМИ (то же поведение, что у <c>--server-selftest</c>): калитка
    /// пишет файл пропуска и поднимает настоящий движок, а у обычного прогона это данные владельца.
    /// </summary>
    /// <param name="locateEngine">
    /// Поиск движка — дверью, как у соседей (<see cref="ServerController"/> ищет его делегатом).
    /// Нужен не для красоты: без этого проверка «нет движка — это код 2» гоняла бы НАСТОЯЩИЙ поиск
    /// и на машине с движком поднимала бы живой сервер там, где проверяется ветка «нечего проверять».
    /// </param>
    public static PhoneSelfTestResult Run(
        TimeSpan timeout,
        string? runRoot = null,
        Func<DshEngine?>? locateEngine = null)
    {
        // Контекст берётся у процесса, когда он есть: так самотест на собранном exe видит ровно те
        // пути, по которым работает вся панель. В проверках контекста нет — и тогда корень приходит
        // доводом. Признак «без изоляции» при этом ОДИН: нет ни контекста с корнем, ни довода.
        var context = TryCurrentContext();
        var isolated = context?.IsIsolated ?? !string.IsNullOrWhiteSpace(runRoot);

        if (!isolated)
        {
            return new PhoneSelfTestResult(
                2,
                "КАЛИТКА НЕПРИМЕНИМО",
                new[]
                {
                    "САМОТЕСТ КАЛИТКИ БЕЗ ИЗОЛЯЦИИ НЕ ИДЁТ: он поднимает настоящий движок (тот пишет " +
                    "в домашний каталог движка) и пишет файл пропуска телефона. Без изоляции это данные " +
                    "владельца. Запустите с «--run-root <свой путь>».",
                });
        }

        // СЛОВА ДЛЯ ОТЧЁТА: у `--phone-selftest` нет окна, и `Loc.Init` до сюда не доходит.
        // Без этого отчёт и проверки видели бы ИМЕНА КЛЮЧЕЙ, а не слова (и «номер порта в отказе»
        // не находился бы вовсе: шаблон — это текст словаря).
        if (Loc.KeyCount == 0) Loc.Init();

        var paths = context?.Paths ?? AppPaths.Under(runRoot!);
        var state = new RunState(paths, timeout, locateEngine ?? (() => DshEngine.Locate()));

        try
        {
            return state.Go();
        }
        finally
        {
            state.Cleanup();
        }
    }

    /// <summary>Контекст прогона, если он установлен. В проверках его нет — и это не ошибка.</summary>
    private static RunContext? TryCurrentContext()
    {
        try
        {
            return RunContext.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ---- состояние прогона ------------------------------------------------------------------

    private sealed class RunState
    {
        private readonly List<string> _report = new();
        private readonly List<string> _log = new();
        private readonly List<string> _failed = new();
        private readonly List<PhoneGate> _started = new();
        private readonly List<string> _stageRoots = new();
        private readonly List<(ServerController Controller, int Port)> _servers = new();

        private readonly AppPaths _paths;
        private readonly TimeSpan _timeout;
        private readonly Func<DshEngine?> _locateEngine;

        /// <summary>Был ли корень прогона на диске ДО нас. Свой корень самотест убирает,
        /// чужой (названный человеком) — никогда.</summary>
        private readonly bool _rootExistedBefore;

        private string _phase = string.Empty;
        private int _checks;
        private int _passes;

        public RunState(AppPaths paths, TimeSpan timeout, Func<DshEngine?> locateEngine)
        {
            _paths = paths;
            _timeout = timeout;
            _locateEngine = locateEngine;
            _rootExistedBefore = Directory.Exists(paths.Root);
        }

        /// <summary>
        /// ЧАСЫ ПОДСТАВНЫХ ДВЕРЕЙ: ими двигает проверка, а не календарь. Без этого «кэш на 30 с»
        /// проверялся бы сном на полминуты, то есть не проверялся бы вовсе.
        /// </summary>
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

        /// <summary>Перевести часы вперёд — так истекает кэш архива в проверке.</summary>
        public void Advance(TimeSpan step) => Now = Now.Add(step);

        /// <summary>
        /// СВОЙ КОРЕНЬ НА КАЖДЫЙ РАЗДЕЛ. Не украшение: файл пропуска — ОДИН на корень, и раздел,
        /// оставивший в нём сопряжённое устройство, сломал бы соседу проверку «без cookie не пускает».
        /// Изоляция внутри изоляции: у каждого раздела свой `state\phone-pass.txt`.
        /// </summary>
        private AppPaths Stage(string name)
        {
            var root = Path.Combine(_paths.Root, "stage", name);

            _stageRoots.Add(root);
            Directory.CreateDirectory(root);

            return AppPaths.Under(root);
        }

        private void Line(string text) => _report.Add(text);

        private void Info(string text) => _report.Add("  " + text);

        private void Log(string text) => _log.Add(text);

        private void Phase(string title)
        {
            _phase = title;
            _report.Add("— " + title + " —");
        }

        /// <summary>
        /// ЧТО СТАЛО ВИДНО, НО ИСХОД НЕ РЕШАЕТ. Отдельный вид строки, и он нужен: у соседей
        /// «без этого „успех“ означал бы лишь „исключений не было“», а здесь обратная беда —
        /// строка «движок найден = False» при отсутствии движка ЗАКОННА, и записать её проверкой
        /// значило бы объявить провалом панели чистую машину (её ответ — «неприменимо», код 2).
        /// Такие строки на исход НЕ влияют и потому печатаются без «= True/False».
        /// </summary>
        private void Note(string what, bool ok) => _report.Add($"{what}: {(ok ? "да" : "нет")}");

        /// <summary>
        /// ОДНА ПРОВЕРКА. Каждая такая строка ВЛИЯЕТ НА ИСХОД — иначе она была бы украшением,
        /// и «успех» означал бы лишь «исключений не было».
        /// </summary>
        private void Check(string what, bool ok)
        {
            _checks++;

            if (ok) _passes++;
            else _failed.Add(_phase + ": " + what);

            _report.Add($"{what} = {ok}");
        }

        public PhoneSelfTestResult Go()
        {
            Line("Самотест калитки телефона. Только изоляция и только свои свободные порты: "
                 + $"запрещённые номера ({ServerDecisions.OwnerPort}, {ServerDecisions.RunFallbackPort}, "
                 + $"{PhonePass.DefaultPort}) не занимаются — каждый порт берётся свободным у системы.");
            Line($"корень прогона = {DisplayMask.Path(_paths.Root)}");
            Line($"домашний каталог движка = {DisplayMask.Path(_paths.DshHome)}");

            var gate = Gate();
            var question = Question();
            var breaker = Breaker();
            var live = Live();
            var frames = Frames();
            var tasks = Tasks();
            var signals = Signals();
            _report.Add(string.Empty);
            Line($"проверок: {_checks}; прошло: {_passes}; провалилось: {_failed.Count}");
            Line($"разделов: открытие {gate}, страница и сопряжение {question}, рубильник {breaker}, "
                 + $"живое рукопожатие {live}, вопрос на подставных кадрах {frames}, "
                 + $"двери S3 {tasks}, S4a {signals} (0 — прошёл, 1 — провал, 2 — неприменимо)");

            if (_failed.Count > 0)
            {
                Line($"провалившиеся проверки ({_failed.Count}), каждая из них и делает исход провалом:");
                foreach (var item in _failed.ToArray()) Line("  ! " + item);
            }

            var code = _failed.Count > 0 || gate == 1 || question == 1 || breaker == 1 || tasks == 1 || signals == 1
                ? 1
                : 0;

            // «Нет движка» — НЕ «зелено»: без живого рукопожатия главная часть F1 не прогонялась.
            // Но и «провал» это не: на чистой машине проверять нечего. Код 2 — как у соседей.
            if (code == 0 && live == 2) code = 2;

            // ОТЧЁТ ЗАКАНЧИВАЕТСЯ ЖУРНАЛОМ — и он уходит в него ОБЕЗВРЕЖЕННЫМ (см. Mask):
            // строки журнала собраны панелью и полны путей, а отчёт читает человек.
            foreach (var item in _log.ToArray()) _report.Add("ЖУРНАЛ| " + Mask(item));

            var title = code switch
            {
                2 => "КАЛИТКА НЕПРИМЕНИМО",
                0 => "КАЛИТКА УСПЕХ",
                _ => "КАЛИТКА ПРОВАЛ",
            };

            return new PhoneSelfTestResult(code, title, _report)
            {
                Checks = _checks,
                Failed = _failed.Count,
            };
        }

        /// <summary>
        /// ОБЕЗВРЕДИТЬ СТРОКУ ОТЧЁТА. Токен входа вырезается (<see cref="EngineLog.Redact"/>),
        /// а каталог человека заменяется на <c>~</c> — И В ЛЮБОМ МЕСТЕ СТРОКИ, а не только в начале:
        /// строки журнала собраны панелью и «путь с началом» в них встречается редко
        /// (`движок запущен: … DSH_HOME C:\Users\…`). Отчёт читает человек и уезжает в журнал
        /// прогонов, и личных путей в нём быть не должно (красная линия 7).
        /// </summary>
        private static string Mask(string? line)
        {
            var text = EngineLog.Redact(line);

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                text = text.Replace(profile, "~", StringComparison.OrdinalIgnoreCase);

            return text;
        }

        public void Cleanup()
        {
            foreach (var gate in _started)
            {
                try
                {
                    gate.Dispose();
                }
                catch
                {
                    // Калитка уже закрыта — этого и добивались.
                }
            }

            _started.Clear();

            // ⚠️ ПОРЯДОК ЗДЕСЬ НЕ ФОРМАЛЬНОСТЬ, и он стоил прогона: движок держит рабочий каталог
            // раздела, поэтому каталог убирается ПОСЛЕ того, как сервер погашен и порт освободился.
            // Убрать раньше значит оставить в `%TEMP%` живой `node` и непустой корень.
            foreach (var (controller, port) in _servers)
            {
                try
                {
                    controller.Stop(confirmed: false);
                }
                catch
                {
                    // Сервер уже погашен — этого и добивались.
                }

                for (var wait = 0; wait < 50 && port > 0 && PortTable.GetListenerPid(port) != 0; wait++)
                    Thread.Sleep(100);

                try
                {
                    controller.Dispose();
                }
                catch
                {
                    // Дескриптор уже отпущен.
                }
            }

            _servers.Clear();

            foreach (var root in _stageRoots)
            {
                // ⚠️ С ПОВТОРАМИ, и это не «на всякий случай»: движок гасится вместе с деревом
                // своих процессов, а его подпроцессы отпускают рабочий каталог не в ту же секунду.
                // Одна попытка оставила бы в `%TEMP%` непустой корень — мусор на машине человека
                // от прогона, который обязан прибирать за собой (красная линия 8).
                //
                // ⚠️ Уборка идёт ОДНИМ местом панели (`Files\TreeDeletion`): у `Directory.Delete`
                // с `recursive: true` на junction и на файле «только для чтения» дерево остаётся,
                // не отличаясь этим от успеха (замер 10.10.2026), а вне `TreeDeletion` такой вызов
                // роняет ворота `TreeDeletionTests`.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (Files.TreeDeletion.Remove(root).Ok) break;

                    Thread.Sleep(300);
                }
            }

            // Корень прогона убирается ТОЛЬКО если он НАШ — то есть его не было до прогона
            // и кроме наших разделов в нём ничего не лежит. Так каталог, названный человеком (или
            // корень соседнего прогона), не трогается НИКОГДА: уборка за собой не имеет права
            // стать уборкой чужого (красная линия 4).
            if (!_rootExistedBefore
                && Directory.Exists(Path.Combine(_paths.Root, "stage"))
                && _stageRoots.All(root => !Directory.Exists(root))
                && Directory.EnumerateFileSystemEntries(_paths.Root).All(entry =>
                    string.Equals(Path.GetFileName(entry), "stage", StringComparison.OrdinalIgnoreCase)))
            {
                Files.TreeDeletion.Remove(_paths.Root);
            }
        }

        // ---- двери ---------------------------------------------------------------------------

        /// <summary>Подставной движок: записывает, ЧЕМ его позвали, и отвечает тем, что задала проверка.</summary>
        private sealed class StubEngine
        {
            public List<Call> Calls { get; } = new();

            public PhoneEngineAnswer Answer { get; set; } =
                new(200, "{\"type\":\"server-response\",\"rpcId\":\"stub\",\"result\":{\"ok\":true}}");

            /// <summary>Ответы ПО ИМЕНИ вызова: у дверей S3 за один запрос бывает два вызова.</summary>
            public Dictionary<string, PhoneEngineAnswer> Answers { get; } = new(StringComparer.Ordinal);

            /// <summary>Задержка вызова: нажата — вызов «думает», пока проверка не отпустит (DEF-072).</summary>
            public ManualResetEventSlim Hold { get; } = new(true);

            public Func<string, string, PhoneEngineAnswer> Door => (endpoint, args) =>
            {
                lock (Calls) Calls.Add(new Call(endpoint, args));

                Hold.Wait(TimeSpan.FromSeconds(10));

                return Answers.TryGetValue(endpoint, out var answer) ? answer : Answer;
            };

            public Call? Last(string endpoint)
            {
                lock (Calls) return Calls.LastOrDefault(call => call.Endpoint == endpoint);
            }

            public bool Called(string endpoint, Func<string, bool>? args = null)
            {
                lock (Calls) return Calls.Any(call => call.Endpoint == endpoint && (args is null || args(call.Args)));
            }

            /// <summary>Сколько раз позвали имя — число, по которому видно «второго вызова не было».</summary>
            public int Count(string endpoint)
            {
                lock (Calls) return Calls.Count(call => call.Endpoint == endpoint);
            }
        }

        private readonly record struct Call(string Endpoint, string Args);

        /// <summary>Подставная подписка: кадры подаёт проверка, живой двери здесь нет.</summary>
        private sealed class StubLink : IPhoneEventLink
        {
            private readonly Action<string> _onFrame;

            public StubLink(Action<string> onFrame) => _onFrame = onFrame;

            public bool Alive { get; private set; } = true;

            public void Emit(string frame) => _onFrame(frame);

            public void Dispose() => Alive = false;
        }

        /// <summary>
        /// ПОДСТАВНАЯ ДВЕРЬ ЖИВОГО ПОТОКА (S1): проверка подаёт кадры `session/follow` сама.
        /// Нужна потому, что живой ход требует ключа модели: без него ни `snapshot`, ни `assistant-stream`
        /// не увидеть, а дверь, её заголовки и порядок событий проверить можно и так.
        /// </summary>
        private sealed class StubFollow
        {
            private Action<string>? _onFrame;

            public int Opens { get; private set; }

            public string Session { get; private set; } = string.Empty;

            public PhoneFollowDoors Doors => new(Open, _ => { }, () => true);

            public void Emit(string frame) => _onFrame?.Invoke(frame);

            private IPhoneFollowLink? Open(string sessionId, Action<string> onFrame)
            {
                Opens++;
                Session = sessionId;
                _onFrame = onFrame;

                return new Link(this);
            }

            private sealed class Link : IPhoneFollowLink
            {
                private readonly StubFollow _owner;

                public Link(StubFollow owner) => _owner = owner;

                public bool Alive => true;

                public void Dispose() => _owner._onFrame = null;
            }
        }

        /// <summary>
        /// ПОДСТАВНАЯ ДВЕРЬ ЧТЕНИЯ ОЧЕРЕДИ (<c>session/control</c>): кадры подаёт проверка — как
        /// у живой ленты. Живой записи здесь нет ни одной: очередь собирается из baseline, а его
        /// форма проверяется без движка.
        /// </summary>
        private sealed class StubControl
        {
            private readonly object _gate = new();
            private readonly List<Action<string>> _subscribers = new();
            private readonly List<Link> _links = new();

            /// <summary>Сколько раз калитка открыла поток.</summary>
            public int Opens
            {
                get
                {
                    lock (_gate) return _links.Count;
                }
            }

            /// <summary>Сколько потоков ЖИВО СЕЙЧАС: после ответа обязано быть 0 (контракт §5.4).</summary>
            public int Live
            {
                get
                {
                    lock (_gate) return _links.Count(link => link.Active);
                }
            }

            public PhoneControlDoors Doors => new(Open, _ => { }, () => true);

            public void Emit(string frame)
            {
                Action<string>[] subscribers;

                lock (_gate) subscribers = _subscribers.ToArray();

                foreach (var subscriber in subscribers) subscriber(frame);
            }

            private IPhoneQueueLink? Open(Action<string> onFrame)
            {
                lock (_gate)
                {
                    var link = new Link();

                    _links.Add(link);
                    _subscribers.Add(onFrame);

                    return link;
                }
            }

            private sealed class Link : IPhoneQueueLink
            {
                public bool Active { get; private set; } = true;

                public bool Alive => Active;

                public void Dispose() => Active = false;
            }
        }
        /// <summary>
        /// ПОДСТАВНАЯ ДВЕРЬ АРХИВА (<c>workspace/follow</c>): кадр подаёт проверка.
        /// <see cref="Silent"/> — поток открылся, а baseline не пришёл: так проверяется правило
        /// «не прочиталось — список как есть, причина в журнале».
        /// </summary>
        private sealed class StubWorkspace
        {
            private readonly object _gate = new();
            private readonly List<Link> _links = new();

            /// <summary>Что отдать первым кадром после открытия (обычно <c>baseline</c>).</summary>
            public string? Reply { get; set; }

            /// <summary>Открыть поток и промолчать: baseline не придёт.</summary>
            public bool Silent { get; set; }

            public int Opens
            {
                get
                {
                    lock (_gate) return _links.Count;
                }
            }

            public int Live
            {
                get
                {
                    lock (_gate) return _links.Count(link => link.Active);
                }
            }

            public PhoneWorkspaceDoors Doors => new(Open, _ => { }, () => true);

            private IPhoneWorkspaceLink? Open(Action<string> onFrame)
            {
                lock (_gate)
                {
                    var link = new Link();

                    _links.Add(link);

                    if (!Silent && Reply is { } frame) onFrame(frame);

                    return link;
                }
            }

            private sealed class Link : IPhoneWorkspaceLink
            {
                public bool Active { get; private set; } = true;

                public bool Alive => Active;

                public void Dispose() => Active = false;
            }
        }

        /// <summary>Свободный порт петли: система выдаёт его связыванием на <c>0</c>.</summary>
        private static int FreeLoopbackPort()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);

                try
                {
                    listener.Start();
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                    if (!PhoneGate.PortForbidden(port) && port != PhonePass.DefaultPort) return port;
                }
                finally
                {
                    listener.Stop();
                }
            }

            throw new InvalidOperationException("не нашлось свободного порта петли, который калитка вправе занять");
        }

        /// <summary>Связывается ли порт прямо сейчас (после закрытия слушателя системе нужно мгновение).</summary>
        private static bool CanBind(int port)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);

                try
                {
                    listener.Start();
                    return true;
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
                finally
                {
                    listener.Stop();
                }
            }

            return false;
        }

        /// <summary>Ответ калитки: код, тело и ЗНАЧЕНИЕ нашей cookie (если она пришла).</summary>
        private sealed record Reply(int Status, string Body, string Cookie)
        {
            public bool Carries(string words) => Body.Contains(words, StringComparison.Ordinal);
        }

        private sealed class Stand : IDisposable
        {
            public Stand(PhoneGateDoors doors, PhoneGate gate, int port)
            {
                Doors = doors;
                Gate = gate;
                Port = port;

                Http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port + "/"),
                    Timeout = TimeSpan.FromSeconds(20),
                };
            }

            public PhoneGateDoors Doors { get; }

            public PhoneGate Gate { get; }

            public int Port { get; }

            public HttpClient Http { get; }

            public void Dispose()
            {
                Gate.Dispose();
                Http.Dispose();
            }
        }

        /// <summary>Калитка на свободном порту петли: подставные двери, свой корень, настоящий слушатель.</summary>
        private Stand Build(
            AppPaths paths,
            bool enabled,
            bool human,
            PhoneEventDoors events,
            StubEngine engine,
            int? port = null,
            PhoneFollowDoors? follow = null,
            PhoneControlDoors? control = null,
            PhoneWorkspaceDoors? workspace = null)
        {
            var chosen = port ?? FreeLoopbackPort();

            var doors = new PhoneGateDoors(
                () => "127.0.0.1",
                chosen,
                engine.Door,
                PhonePassFiles.Under(paths, Log),
                events,
                () => Now,
                Log,
                Bound: _ => { },
                Follow: follow,
                Control: control,
                Workspace: workspace);

            // ⚠️ Контекст здесь ИЗОЛИРОВАННЫЙ (свой корень): обычный запуск человеком его не даст,
            // и калитка откажет по праву прежде, чем дойдёт до порта. Право самотеста этой калитке
            // НЕ выдаётся — им занимается RunGate, а здесь проверяется сам отказ.
            var context = RunContext.Create(RunRequest.Parse(
                new[] { RunRequest.RootSwitchArgument, paths.Root }, _ => null));

            return new Stand(doors, new PhoneGate(doors, () => enabled, human, context), chosen);
        }

        /// <summary>
        /// КАЛИТКА С ПРАВОМ САМОТЕСТА. Право выдаётся ровно здесь и ровно затем, чтобы калитку
        /// можно было проверить: изолированному прогону его не даёт никто
        /// (<see cref="RunRights.MayOpenPhoneGate"/>). Сам отказ по праву проверяется ОТДЕЛЬНОЙ
        /// калиткой (<see cref="Build"/>) — и это первая проверка раздела «открытие».
        /// </summary>
        private Stand RunGate(
            AppPaths paths,
            bool enabled,
            PhoneEventDoors events,
            StubEngine engine,
            int? port = null,
            PhoneFollowDoors? follow = null,
            PhoneControlDoors? control = null,
            PhoneWorkspaceDoors? workspace = null)
        {
            var stand = Build(paths, enabled, human: true, events, engine, port, follow, control, workspace);

            stand.Gate.AllowForSelfTest = true;

            return stand;
        }

        /// <summary>Запрос: заголовок нашей cookie собирается ЗДЕСЬ, в одном месте.</summary>
        private static async Task<Reply> Send(
            HttpClient http, HttpMethod method, string path, string? json = null, string? cookie = null,
            string? language = null, string? host = null)
        {
            using var request = new HttpRequestMessage(method, path);

            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", PhoneGate.CookieName + "=" + cookie);

            if (language is not null) request.Headers.TryAddWithoutValidation("Accept-Language", language);

            if (host is not null) request.Headers.Host = host;

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            var set = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values)
                : string.Empty;

            return new Reply((int)response.StatusCode, text, PhoneGate.CookieValueOf(set));
        }

        /// <summary>Сопрячь телефон по-настоящему: код, страница, cookie. Пусто — не сопряглось.</summary>
        private static async Task<string> Pair(Stand stand)
        {
            var code = stand.Gate.IssueCode();

            var reply = await Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}");

            return reply.Status == 200 ? reply.Cookie : string.Empty;
        }

        /// <summary>
        /// ОДИН ЖИВОЙ ПОТОК В ПРОВЕРКЕ: своё TCP-соединение (HttpClient незавершённый ответ
        /// не отдаёт), запрос — вручную, чтение — ограниченное время. <paramref name="afterOpen"/> —
        /// что подать в подставную дверь, когда калитка уже открыла поток.
        /// </summary>
        private static string SseOnce(
            Stand stand, string path, string cookie, Action afterOpen, int readMs = 1500)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, stand.Port);

            using var stream = client.GetStream();
            stream.ReadTimeout = 250;

            var head = "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + stand.Port + "\r\n"
                       + "Cookie: " + PhoneGate.CookieName + "=" + cookie + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            Thread.Sleep(300);
            afterOpen();

            var seen = new StringBuilder();
            var buffer = new byte[8192];
            var deadline = Environment.TickCount64 + readMs;

            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read > 0) seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                catch (IOException)
                {
                    // Срок чтения: новых байт пока нет — это не ошибка проверки.
                }
            }

            return seen.ToString();
        }

        /// <summary>Подождать состояния вопроса: кадры приходят фоном, и «сейчас же» — это гонка.</summary>
        private static async Task<string> WaitForQuestion(Stand stand, string cookie, string state)
        {
            var last = string.Empty;

            for (var attempt = 0; attempt < 60; attempt++)
            {
                last = (await Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)).Body;

                if (last.Contains("\"state\":\"" + state + "\"", StringComparison.Ordinal)) return last;

                await Task.Delay(50);
            }

            return last;
        }

        // ---- (1) открытие калитки: право, выключенный телефон, занятый порт --------------------

        /// <summary>
        /// Открывается ли калитка ТОЛЬКО по праву и не съезжает ли с занятого порта молча.
        /// <c>0</c> — проверено, <c>1</c> — открытие провалилось (дальше идти некуда).
        /// </summary>
        private int Gate()
        {
            Phase("открытие калитки (право, выключенный телефон, занятый порт)");

            var paths = Stage("gate");
            var events = PhoneEventDoors.None;

            Line($"  корень раздела: {DisplayMask.Path(paths.Root)} — свой файл пропуска, чтобы разделы не мешали друг другу");

            // ИЗОЛИРОВАННЫЙ ПРОГОН ПРАВА НЕ ИМЕЕТ — и это проверка не формальности: гоняй самотест
            // право, он бы открыл порт наружу, то есть сделал бы ровно то, чего калитка не делает.
            using (var blind = Build(paths, enabled: true, human: true, events, new StubEngine()))
            {
                var start = blind.Gate.Start();

                Check("изолированному прогону калитка не открывается", !start.Opened);
                Check("отказ называет причину словами", start.Text.Length > 0);
                Check("право спрашивается ДО связывания: порт свободен", CanBind(blind.Port));
            }

            var engine = new StubEngine();

            using var stand = RunGate(paths, enabled: true, events, engine);

            var opened = stand.Gate.Start();
            _started.Add(stand.Gate);

            Check("калитка открывается по праву (человек + телефон включён)", opened.Opened);
            Check("порт калитки — свой свободный, не порт владельца и не запасной",
                !PhoneGate.PortForbidden(stand.Port) && stand.Port != PhonePass.DefaultPort
                && PhoneGate.PortAllowed(stand.Port));

            if (!opened.Opened)
            {
                Info("причина отказа: " + opened.Text);
                return 1;
            }

            // ВЫКЛЮЧЕННЫЙ ТЕЛЕФОН — второй экземпляр калитки на своём порту.
            using (var off = Build(paths, enabled: false, human: true, events, new StubEngine()))
            {
                var refused = off.Gate.Start();

                Check("при выключенном телефоне калитка отказывает словами",
                    !refused.Opened && refused.Text.Length > 0);
                Check("выключенный телефон не занимает порт", CanBind(off.Port));
            }

            // ЗАНЯТЫЙ ПОРТ — свой свободный, занятый своим же слушателем: молчаливого переезда нет.
            var busy = new TcpListener(IPAddress.Loopback, 0);
            busy.Start();

            try
            {
                var busyPort = ((IPEndPoint)busy.LocalEndpoint).Port;

                using var blocked = RunGate(paths, enabled: true, events, new StubEngine(), busyPort);

                var refused = blocked.Gate.Start();

                Check("занятый порт: отказ словами, а не переезд",
                    !refused.Opened && refused.ReasonKey == PanelStrings.PhoneGatePortBusyFormat);
                Check("занятый порт назван в отказе числом",
                    refused.Text.Contains(busyPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                Check("на занятом порту калитка не слушает", !blocked.Gate.Listening);
            }
            finally
            {
                busy.Stop();
            }

            return 0;
        }

        // ---- (2) страница, сопряжение, попытки, белый список -----------------------------------

        private int Question()
        {
            Phase("страница входа, сопряжение, попытки, страж белого списка");

            var paths = Stage("pair");
            var engine = new StubEngine();
            var events = PhoneEventDoors.None;

            using var stand = RunGate(paths, enabled: true, events, engine);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки сопряжения", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var page = Send(stand.Http, HttpMethod.Get, "/ask").GetAwaiter().GetResult();

            Check("страница входа отдаётся кодом 200", page.Status == 200);
            Check("на странице есть поле для шести цифр",
                page.Carries("id=\"code\"") && page.Carries("maxlength=\"6\""));

            // НЕВЕРНЫЙ КОД — 403 и попытка посчитана.
            var wrong = Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"000000\"}")
                .GetAwaiter().GetResult();

            Check("неверный код: 403 и слова, а не молчание", wrong.Status == 403 && wrong.Body.Length > 0);
            Check("неверный код не ставит cookie", wrong.Cookie.Length == 0);
            Check("неудачная попытка ничего не записала в пропуск",
                PhonePass.Load(PhonePassFiles.Under(paths, Log)).DeviceCount == 0);

            // ВЕРНЫЙ КОД ПУСКАЕТ И СТАВИТ COOKIE. Проверяется по ФАЙЛУ: устройство записано,
            // а cookie — подпись именно его.
            var code = stand.Gate.IssueCode();
            Check("у калитки есть живой код", stand.Gate.HasLiveCode());

            var paired = Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}")
                .GetAwaiter().GetResult();

            Check("верный код пускает (200)", paired.Status == 200);
            Check("cookie поставлена", paired.Cookie.Length > 0);

            var pass = PhonePass.Load(PhonePassFiles.Under(paths, Log));

            Check("сопряжённое устройство записано в файл пропуска", pass.DeviceCount == 1);
            Check("файл пропуска лежит под корнем прогона", File.Exists(paths.PhonePassFile));

            if (paired.Cookie.Length == 0)
            {
                Check("сопряжение состоялось", false);
                return 1;
            }

            var cookie = paired.Cookie;

            // ПОВТОР ТОГО ЖЕ КОДА НЕ ПУСКАЕТ: код умирает первым делом — до записи пропуска.
            var again = Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"" + code + "\"}")
                .GetAwaiter().GetResult();

            Check("повтор того же кода не пускает", again.Status == 403 && again.Cookie.Length == 0);
            Check("после успеха живого кода нет", !stand.Gate.HasLiveCode());
            Check("повтор не завёл второго устройства",
                PhonePass.Load(PhonePassFiles.Under(paths, Log)).DeviceCount == 1);

            // ПРЕДЕЛ ПОПЫТОК ДОВОДИТСЯ ДО ПАУЗЫ: калитка отвечает 429 и говорит, сколько ждать.
            var paused = false;
            var replies = 0;

            while (replies < 40 && !paused)
            {
                var reply = Send(stand.Http, HttpMethod.Post, "/pair", "{\"code\":\"111111\"}")
                    .GetAwaiter().GetResult();

                replies++;
                paused = reply.Status == 429 && reply.Body.Length > 0;
            }

            Check($"перебор кода упирается в паузу (429 на {replies}-й неудаче)", paused);
            Info($"  порог паузы — {PhoneDecisions.BlockAfter} неудачи подряд, предел с адреса — {PhoneDecisions.AddressLimit}");

            // БЕЗ COOKIE — 401 на обеих дверях вопроса.
            var questionAnon = Send(stand.Http, HttpMethod.Get, "/question").GetAwaiter().GetResult();
            Check("без cookie вопрос не отдаётся (401)", questionAnon.Status == 401);

            var answerAnon = Send(
                    stand.Http, HttpMethod.Post, "/answer", "{\"eventId\":\"probe\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}")
                .GetAwaiter().GetResult();

            Check("без cookie ответ не принимается (401)", answerAnon.Status == 401);

            // ЧУЖОЕ ИМЯ — 403 СЛОВАМИ: белый список, а не мост наружу.
            var callsBefore = engine.Calls.Count;

            var foreign = Send(
                    stand.Http, HttpMethod.Post, "/api/settings/mutate", "{\"args\":{}}", cookie)
                .GetAwaiter().GetResult();

            Check("чужое имя отвергнуто (403) и названо в словах",
                foreign.Status == 403 && foreign.Carries("settings/mutate"));
            Check("чужое имя до движка не дошло", engine.Calls.Count == callsBefore);

            // РАЗРЕШЁННЫЙ ВЫЗОВ УХОДИТ В ДВИЖОК и возвращает ЕГО ответ как есть.
            engine.Answer = new PhoneEngineAnswer(
                200, "{\"type\":\"server-response\",\"rpcId\":\"stub\",\"result\":{\"ok\":true,\"value\":{\"items\":[1,2,3]}}}");

            var allowed = Send(
                    stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie)
                .GetAwaiter().GetResult();

            var sent = engine.Last("session/list");

            Check("разрешённый вызов уходит в движок",
                sent is { } call && call.Args.Contains("_request", StringComparison.Ordinal));
            // С t4 старый `/api/session/list` отдаёт НЕ тело движка, а строки по белому списку.
            Check("ответ телефону — строки по белому списку (ok/total/shown/items)",
                allowed.Status == 200
                && allowed.Carries("\"ok\":true")
                && allowed.Carries("\"total\":0")
                && allowed.Carries("\"items\":[]"));
            Check("пропуск в движок не уехал (уезжает только конверт с доводами)",
                sent is { } c2 && !c2.Args.Contains("phone-", StringComparison.OrdinalIgnoreCase));

            // СЛИШКОМ БОЛЬШОЕ ТЕЛО — 413: предел объявлен и соблюдается.
            Check("слишком большое тело отвергнуто (413)", TooBig(stand, cookie));

            // ПОТОКОВОЕ ИМЯ ПО POST — отказ СЛОВАМИ, а не отказ движка.
            var stream = Send(
                    stand.Http, HttpMethod.Post, "/api/session/follow", "{\"args\":{\"request\":{}}}", cookie)
                .GetAwaiter().GetResult();

            Check("потоковое имя по POST отвергнуто словами",
                stream.Status == 403 && stream.Carries("session/follow"));

            // СЛУЖЕБНАЯ ДВЕРЬ ДВИЖКА телефону не открыта: он отвечает своей дверью (`POST /answer`).
            var service = Send(stand.Http, HttpMethod.Post, "/api/$events/result", "{\"args\":{}}", cookie)
                .GetAwaiter().GetResult();

            Check("служебная дверь ответа сырым POST не зовётся (403)", service.Status == 403);

            // НЕЗНАКОМЫЙ ПУТЬ — 404: калитка не проксирует движок целиком.
            var unknown = Send(stand.Http, HttpMethod.Get, "/plugins/events").GetAwaiter().GetResult();
            Check("незнакомый путь не уходит в движок (404)", unknown.Status == 404);

            return 0;
        }

        /// <summary>
        /// СЛИШКОМ БОЛЬШОЕ ТЕЛО. Отправляется СЫРЫМ сокетом и дочитывается до конца: калитка
        /// отвечает, только дочитав остаток (иначе Windows шлёт `RST` и ответ до клиента не доходит —
        /// см. <c>PhoneGate.LingerAsync</c>). Именно поэтому здесь не `HttpClient`: он в этом месте
        /// падает на сбросе, и «413» выглядело бы как «соединение разорвано».
        /// </summary>
        private static bool TooBig(Stand stand, string cookie)
        {
            var body = "{\"args\":{\"text\":\"" + new string('a', PhoneGate.MaxBodyBytes + 4096) + "\"}}";

            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, stand.Port);

                using var stream = client.GetStream();
                stream.ReadTimeout = 20000;
                stream.WriteTimeout = 20000;

                var head = "POST /api/session/list HTTP/1.1\r\nHost: 127.0.0.1\r\n"
                           + "Content-Type: application/json\r\n"
                           + "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n"
                           + "Cookie: " + PhoneGate.CookieName + "=" + cookie + "\r\n\r\n";

                var bytes = Encoding.UTF8.GetBytes(head + body);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                var buffer = new byte[8192];
                var read = stream.Read(buffer, 0, buffer.Length);

                if (read <= 0) return false;

                var text = Encoding.UTF8.GetString(buffer, 0, read);

                // Дочитываем остаток, чтобы калитка не получила RST — так делает настоящий клиент.
                try
                {
                    while (stream.Read(buffer, 0, buffer.Length) > 0)
                    {
                        // Тело ответа не нужно: нужен код.
                    }
                }
                catch
                {
                    // Сокет закрылся — ответ уже прочитан.
                }

                return text.StartsWith("HTTP/1.1 413", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- (3) рубильник: отвязка устройства и остановка калитки -----------------------------

        private int Breaker()
        {
            Phase("рубильник: отвязка устройства и освобождение порта");

            var paths = Stage("breaker");
            var engine = new StubEngine();
            var events = PhoneEventDoors.None;

            using var stand = RunGate(paths, enabled: true, events, engine);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки рубильника", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var cookie = Pair(stand).GetAwaiter().GetResult();

            if (cookie.Length == 0)
            {
                Check("сопряжение для проверки рубильника состоялось", false);
                return 1;
            }

            var before = Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie).GetAwaiter().GetResult();
            Check("до отвязки cookie пускает (200)", before.Status == 200);

            // ОТВЯЗКА УСТРОЙСТВА. Пишется тем же файлом, каким его прочитает калитка: подпись
            // у отвязанного устройства осталась бы верной, и без записи оно продолжило бы ходить.
            var pass = PhonePass.Load(PhonePassFiles.Under(paths, Log));
            var device = pass.SafeDevices.FirstOrDefault();

            if (device is null)
            {
                Check("в файле пропуска есть устройство для отвязки", false);
                return 1;
            }

            var saved = pass.WithoutDevice(device.Id).Save(PhonePassFiles.Under(paths, Log));

            Check("отвязка записана в файл пропуска (устройств 0)",
                saved && PhonePass.Load(PhonePassFiles.Under(paths, Log)).DeviceCount == 0);
            Check("пропуск при отвязке не сменился (иначе отвязалось бы всё)",
                PhonePass.Load(PhonePassFiles.Under(paths, Log)).Any);

            var after = Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie).GetAwaiter().GetResult();
            Check("тот же cookie после отвязки не пускает (401)", after.Status == 401);

            // ОСТАНОВКА КАЛИТКИ — ПОРТ ОСВОБОДИЛСЯ. Не «состояние говорит, что не слушает»,
            // а связывание тем же портом.
            stand.Gate.Stop();

            Check("после остановки калитка не слушает", !stand.Gate.Listening);
            Check("после остановки порт освободился (связывание тем же портом проходит)", CanBind(stand.Port));
            Check("об остановке сказано в журнале",
                                _log.Any(line => line.Contains(Loc.T(PanelStrings.PhoneGateStoppedLog), StringComparison.Ordinal)));

            return 0;
        }

        // ---- (4) ЖИВОЕ РУКОПОЖАТИЕ МУЛЬТИПЛЕКСОРА (главное, чего не было) -----------------------

        /// <summary>
        /// ЖИВОЙ ДВИЖОК ПОД КОРНЕМ ПРОГОНА, ЖИВОЙ ВЫЗОВ ТЕЛЕФОНА И НАСТОЯЩАЯ ПОДПИСКА НА <c>$events</c>.
        ///
        /// <c>0</c> — рукопожатие состоялось и названо числом; <c>1</c> — провал; <c>2</c> — движка
        /// на машине нет (неприменимо, и это НЕ успех).
        /// </summary>
        private int Live()
        {
            Phase("живой движок: рукопожатие мультиплексора и вызов телефона");

            var paths = Stage("live");
            var engine = _locateEngine();

            // ⚠️ ЭТО НЕ ПРОВЕРКА, А НАБЛЮДЕНИЕ: на чистой машине движка нет, и «нет» здесь —
            // правильный ответ, за который прогон отвечает кодом 2 («неприменимо»), а не «провал».
            // Проверкой он стал бы ложью о панели.
            Note("движок найден (node и @deepseek-ai/dsh)", engine is not null);

            if (engine is null)
            {
                Info("на этой машине нет Node и пакета @deepseek-ai/dsh — живое рукопожатие проверить нечем");
                Info("это НЕ «зелено»: рукопожатие мультиплексора — та часть F1, которая без движка не прогонялась");
                return 2;
            }

            Info($"  node: {DisplayMask.Path(engine.NodePath)}");
            Info($"  движок: {DisplayMask.Path(engine.BinPath)}");

            var files = ServerStateFiles.Under(paths, Log);

            var controller = new ServerController(
                paths.DshHome,
                () => Directory.CreateDirectory(paths.DataDir).FullName,
                Log,
                mayOccupyOwnerPort: false,
                requestedPort: FreeLoopbackPort,
                allowParallelStart: true,
                files: files);

            // Контроллер берётся НА УЧЁТ раздела: движок держит рабочий каталог, и уборка обязана
            // погасить его прежде, чем убирать корень. `using` здесь не годится — он сработал бы
            // в конце метода, то есть ПОСЛЕ уборки.
            _servers.Add((controller, 0));

            var startedAt = Stopwatch.StartNew();
            var server = controller.Start(_timeout);
            startedAt.Stop();

            Check($"свой сервер поднялся на своём порту (за {startedAt.Elapsed.TotalSeconds:F1} с)", server.IsRunning);

            if (server.IsRunning) _servers[^1] = (controller, server.Port);

            if (!server.IsRunning)
            {
                Info("состояние сервера: " + server.Detail);
                return 1;
            }

            Check("движок живёт под корнем прогона (свой DSH_HOME)",
                paths.DshHome.StartsWith(
                    Path.TrimEndingDirectorySeparator(paths.Root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            Check("порт сервера — не порт владельца",
                ServerDecisions.IsAllowedPort(server.Port, mayOccupyOwnerPort: false)
                && server.Port != ServerDecisions.OwnerPort);

            // ССЫЛКУ ВХОДА НЕ ПЕЧАТАЕМ: в ней токен (красная линия 7). В отчёт — только порт.
            //
            // ⚠️ Ждём появления ФАЙЛА ссылки, как это делает самотест сервера: движок печатает
            // ссылку своим выводом, а читает её отдельная нитка процесса — к мгновению, когда
            // сервер уже отвечает, строки в файле может ещё не быть. Ожидание здесь не украшение:
            // без него «ссылки нет» означало бы «мы поторопились», а не «панель не умеет».
            for (var wait = 0; wait < 50 && !File.Exists(paths.EntryLinkFile); wait++) Thread.Sleep(100);

            var entry = EntryLinkDecisions.ReadFile(
                File.Exists(paths.EntryLinkFile) ? File.ReadAllText(paths.EntryLinkFile) : string.Empty).Url;

            Check("ссылка входа сохранена и она про наш порт", EntryLinkDecisions.MatchesPort(entry, server.Port));
            Check("ссылка входа ведёт на свою машину (токен уходит только на петлю)",
                EntryLinkDecisions.IsLoopbackHost(entry));
            Info($"  ссылка входа: порт {server.Port}; сама строка в отчёт не выводится (в ней токен)");

            // --- ЖИВОЙ ВЫЗОВ ТЕЛЕФОНА: разрешённое имя уходит в движок, ответ приходит телефону ---
            var stub = new StubEngine();
            var noEvents = PhoneEventDoors.None;

            using var stand = RunGate(paths, enabled: true, noEvents, stub);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась рядом с живым движком", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var cookie = Pair(stand).GetAwaiter().GetResult();
            Check("сопряжение на живой калитке состоялось", cookie.Length > 0);

            var list = Send(
                    stand.Http, HttpMethod.Post, "/api/session/list", "{\"args\":{\"_request\":{}}}", cookie)
                .GetAwaiter().GetResult();

            Check("живой session/list принят движком (200)", list.Status == 200);

            // ТЕЛЕФОНУ — СВОИ СТРОКИ: форма ответа — наша (`total/shown/items`), а путей в ней
            // нет вовсе (с поправки §0.4 контракта дверь собирает строки по allowlist).
            Check(
                "список для телефона собран по белому списку (total/shown/items)",
                list.Body.Contains("\"total\"", StringComparison.Ordinal)
                && list.Body.Contains("\"shown\"", StringComparison.Ordinal)
                && list.Body.Contains("\"items\"", StringComparison.Ordinal));
            Check(
                "в списке для телефона нет cwd/workingDirectory",
                !list.Body.Contains("\"cwd\"", StringComparison.Ordinal)
                && !list.Body.Contains("\"workingDirectory\"", StringComparison.Ordinal));

            Info($"  session/list: ответ телефону {list.Body.Length} знаков (содержимое не печатаем: там имена разговоров человека)");

            // --- ФОРМА СПИСКА: ТО, ЧТО ПИШЕТ ДВИЖОК, ПРОТИВ ТОГО, ЧТО ЖДЁТ КАЛИТКА -----------
            //
            // ⚠️ ЭТИ ПРОВЕРКИ ПОЯВИЛИСЬ ПОСЛЕ ДВУХ НАХОДОК (блоки D и E, 18.10.2026). Сначала фильтр
            // искал `result.value` — по аналогии с другим вызовом; потом `result.items` — по
            // ПОДСТАВНОМУ ответу самой панели, то есть проверка подтверждала мою же ошибку. Живой
            // прогон на копии профиля (198 сессий) показал правду: `result.value.items`. Теперь форму
            // меряет ЖИВОЙ прогон и печатает ИМЕНА полей.
            //
            // ⚠️ МЕРЯЕТСЯ ОТВЕТ ДВИЖКА, А НЕ ДВЕРЬ КАЛИТКИ: с t4 дверь уже собрана по allowlist,
            // и «какой формы был ответ движка» по ней не видно. Поэтому здесь — прямой вызов
            // движка тем же входом, каким ходит калитка.
            var shapeLogin = EngineRpcLogin.Exchange(entry, TimeSpan.FromSeconds(20));
            var shapeEngine = shapeLogin.Ok
                ? new EngineRpcClient(
                        EngineRpcLogin.BaseUrlOf(entry), shapeLogin.CookieHeader, TimeSpan.FromSeconds(10))
                    .CallRaw("session/list", "{\"_request\":{}}")
                : null;

            var shape = PhoneSessions.Examine(shapeEngine?.Body);

            if (shape.Found)
            {
                Check(
                    $"путь до списка — тот, что ждёт калитка («{PhoneSessions.ItemsPath}»)"
                    + (shape.Expected ? string.Empty : $" — пришёл «{shape.Container}»"),
                    shape.Expected);

                Info($"  в списке: сессий {shape.Sessions}, подчинённых {shape.Subordinate}");
                Info($"  поля сессии: {(shape.Fields.Length == 0 ? "(список пуст)" : shape.Fields)}");

                if (shape.Sessions > 0)
                {
                    // ПОЛЯ СЕССИИ ВИДНЫ ТОЛЬКО НА НЕПУСТОМ СПИСКЕ — на чистой машине их не из чего
                    // взять, и «провал» там был бы ложью о панели.
                    Check(
                        "подчинённые сессии отличимы: у сессий есть parentSessionId или origin",
                        shape.ParentField || shape.OriginField);

                    Info(
                        "  формат журнала: "
                        + (shape.FormatField ? "поле formatStatus есть (это линия 0.2.x)" : "поля formatStatus нет (это 0.1.x)"));
                }
                else
                {
                    Note("поля сессии видны (список непустой)", false);
                }
            }
            else
            {
                // ПУСТОЙ СПИСОК ПРИХОДИТ БЕЗ КОНТЕЙНЕРА ВОВСЕ (`result = {ok}`, 62 знака) — это
                // законный ответ чистой установки, а не поломка. Проверять здесь нечего, и «провал»
                // был бы неправдой; но и молчать нельзя — строка говорит, чего не увидели.
                Note("список непустой (форма списка видна только на непустом)", false);
                Info($"  контейнера списка в ответе нет ({shape.Container}) — это ответ чистой установки");
            }

            // --- САМО РУКОПОЖАТИЕ: настоящий WS к /api/remote.mux и кадр `ready` ---
            //
            // Дверь берётся ТА ЖЕ, которой пользуется калитка (`EngineMux.Doors`), и cookie в неё
            // добывается тем же входом (`EngineRpcLogin.Exchange`), каким её добывает
            // `PhoneGateRealDoors`: иначе проверялось бы не то, чем панель ходит в движок.
            var login = EngineRpcLogin.Exchange(entry, TimeSpan.FromSeconds(20));

            Check($"вход по ссылке входа состоялся (код {login.StatusCode})", login.Ok);

            if (!login.Ok)
            {
                Info("без входа рукопожатие не состоится — и это уже находка, а не «не проверилось»");
                return 1;
            }

            // ⚠️ ЧАСЫ ЗАВОДЯТСЯ ЗДЕСЬ, А ЧИСЛО СЧИТАЕТСЯ ОТ ОТКРЫТИЯ КАНАЛА: `watch` запускается
            // до рукопожатия, и уже внутри обратного вызова берётся отметка, когда канал открылся.
            // Так в отчёт идёт ЧЕСТНОЕ «сколько ждёт калитка, пока поток заговорит», а не «сколько
            // заняло рукопожатие вместе с кадром» — и рядом видно обе величины.
            var watch = Stopwatch.StartNew();
            long openedAt = 0;

            var ready = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

            var doors = EngineMux.Doors(
                () => EngineRpcLogin.BaseUrlOf(entry),
                () => login.CookieHeader,
                TimeSpan.FromSeconds(15),
                Log);

            using var link = doors.Open(frame =>
            {
                // Первый же кадр потока — признак, что канал заговорил: отметка берётся ДО разбора
                // и до всякого `ready`, иначе «готовность» включала бы разбор.
                if (Interlocked.CompareExchange(ref openedAt, watch.ElapsedMilliseconds, 0) == 0) { }

                var parsed = PhoneEventFrames.Parse(frame);

                if (parsed.Kind != PhoneEventKind.Ready) return;

                // ⚠️ Из кадра `ready` здесь НЕ ЧИТАЕТСЯ НИЧЕГО, кроме длины clientId: в нём лежит
                // домашний каталог машины (замер §3), и в отчёт ему хода нет.
                ready.TrySetResult(parsed.ClientId.Length);
            });

            if (link is null)
            {
                Check("подписка на $events открылась (рукопожатие канала)", false);
                Info("дверь событий вернула null: рукопожатие не состоялось — причина строкой в журнале ниже");
                return 1;
            }

            Check("подписка жива сразу после открытия", link.Alive);

            var got = ready.Task.Wait(TimeSpan.FromSeconds(20));
            watch.Stop();

            if (!got)
            {
                Check("кадр ready пришёл с непустым clientId", false);
                Info("за 20 с кадр `ready` не пришёл: канал открылся, а движок не заговорил");
                return 1;
            }

            var clientIdLength = ready.Task.Result;
            var readyAt = watch.ElapsedMilliseconds;

            Check("кадр ready пришёл с непустым clientId", clientIdLength > 0);
            Info($"  готовность подписки мультиплексора: {readyAt} мс (от открытия канала до кадра `ready`)");
            Info($"  первым кадром потока оказался `ready` через {openedAt} мс — от него до кадра {readyAt - openedAt} мс");
            Info($"  clientId длиной {clientIdLength} знаков — в отчёт значение не выводится");

            // КАНАЛ ДОКАЗЫВАЕТСЯ ДЕЛОМ: ответ на несуществующий вопрос отвергается как «вопрос
            // не наш». Будь канал событий не открыт, телефону сказали бы «связи нет» (503), —
            // это РАЗНЫЕ ответы, и по ним видно, что clientId у калитки есть.
            var broken = Send(
                    stand.Http, HttpMethod.Post, "/answer",
                    "{\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}", cookie)
                .GetAwaiter().GetResult();

            Check("живой канал событий есть: ответ отвергнут как устаревший (403), а не как «связи нет» (503)",
                broken.Status == 403 && !broken.Carries(Loc.T(PanelStrings.PhoneGateNoChannel)));

            var stopped = controller.Stop(confirmed: false);
            Check("свой сервер погашен", !stopped.IsRunning);
            Check("порт сервера после гашения свободен", CanBind(server.Port));
            return 0;
        }

        // ---- (5) вопрос агента на подставных кадрах --------------------------------------------

        private int Frames()
        {
            Phase("вопрос агента на подставных кадрах (живой вопрос требует ключа модели)");

            Info("живой вопрос агента здесь не поднимается: он появляется только внутри хода агента, "
                 + "а ход без ключа модели не идёт (замер docs\\PHONE-LIVE.md §4)");

            var paths = Stage("frames");

            // ФОРМЫ КАДРОВ — ЗАМЕР (§3, §4), а не выдумка: ровно так отвечает настоящий движок.
            const string domashny = "C:\\Users\\probe-home";
            const string clientId = "11111111-2222-3333-4444-555555555555";
            const string eventId = "8b4b3ed5-0000-4000-8000-000000000001";

            var ready = "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\","
                        + "\"clientId\":\"" + clientId + "\",\"host\":{\"home\":\""
                        + domashny.Replace("\\", "\\\\") + "\"}}}";

            var waterfall = "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
                            + "\"event\":\"user-questions/request\",\"eventId\":\"" + eventId + "\","
                            + "\"agentId\":\"session-c16b-0000\",\"request\":{\"questions\":"
                            + "[{\"id\":\"q1\",\"question\":\"Ставить задачу?\",\"header\":\"Вопрос\","
                            + "\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}";

            var cancel = "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":"
                         + "{\"type\":\"cancel\",\"eventId\":\"" + eventId + "\"}}";

            var engine = new StubEngine();
            var links = new List<StubLink>();

            var events = new PhoneEventDoors(
                onFrame =>
                {
                    var made = new StubLink(onFrame);

                    lock (links) links.Add(made);

                    return made;
                },
                Log);

            // ПОДСТАВНАЯ ДВЕРЬ ЖИВОГО ПОТОКА (S1): ею проверяются заголовки SSE и порядок кадров.
            var follow = new StubFollow();

            using var stand = RunGate(paths, enabled: true, events, engine, follow: follow.Doors);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки вопроса", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var cookie = Pair(stand).GetAwaiter().GetResult();

            if (cookie.Length == 0)
            {
                Check("сопряжение для проверки вопроса состоялось", false);
                return 1;
            }

            // ПОДСТАВНАЯ ДВЕРЬ ЕСТЬ — подписка открыта: это и есть канал, по которому придёт вопрос.
            Check("дверь событий открыта калиткой", links.Count > 0);

            if (links.Count == 0) return 1;

            // ПУСТО: вопросов нет — и телефону об этом ГОВОРЯТ СЛОВАМИ, а не молчанием.
            var none = Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie).GetAwaiter().GetResult();

            Check("без вопроса телефону сказано словами, что вопросов сейчас нет",
                none.Status == 200 && none.Body.Contains(Loc.T(PanelStrings.PhoneGateNoQuestionNow)));

            links[0].Emit(ready);
            links[0].Emit(waterfall);

            var withQuestion = WaitForQuestion(stand, cookie, "question").GetAwaiter().GetResult();

            Check("вопрос показан телефону (состояние question)",
                withQuestion.Contains("\"state\":\"question\"", StringComparison.Ordinal));
            Check("телефон видит eventId вопроса", withQuestion.Contains(eventId, StringComparison.Ordinal));
            Check("телефон видит вопросы (id и текст)",
                withQuestion.Contains("\"id\":\"q1\"", StringComparison.Ordinal) && withQuestion.Contains("Ставить задачу?", StringComparison.Ordinal));
            Check("домашний каталог машины не виден в ответе на вопрос",
                withQuestion.IndexOf(domashny, StringComparison.OrdinalIgnoreCase) < 0
                && withQuestion.IndexOf("home", StringComparison.OrdinalIgnoreCase) < 0);

            // ОТВЕТ УХОДИТ В ДВЕРЬ РОВНО С НАШИМ clientId И НАШИМ ОТВЕТОМ.
            engine.Answer = new PhoneEngineAnswer(
                200, "{\"type\":\"server-response\",\"rpcId\":\"stub\",\"result\":{\"ok\":true}}");

            var answered = Send(
                    stand.Http, HttpMethod.Post, "/answer",
                    "{\"eventId\":\"" + eventId + "\",\"answers\":[{\"id\":\"q1\",\"selected\":[\"Да\"]}]}",
                    cookie)
                .GetAwaiter().GetResult();

            Check("ответ принят от телефона (200)", answered.Status == 200);

            var sent = engine.Last(PhoneDecisions.EventsResultEndpoint);

            Check("ответ ушёл служебной дверью движка ($events/result)",
                sent is { } call && call.Endpoint == PhoneDecisions.EventsResultEndpoint);
            Check("в ответе — НАШ clientId из кадра ready",
                sent is { } c1 && c1.Args.Contains(clientId, StringComparison.Ordinal));
            Check("в ответе — тот же eventId", sent is { } c2 && c2.Args.Contains(eventId, StringComparison.Ordinal));
            Check("ответы телефона ушли как есть",
                sent is { } c3 && c3.Args.Contains("\"selected\":[\"Да\"]", StringComparison.Ordinal));
            Check("домашний каталог машины не ушёл в дверь движка",
                sent is { } c4 && c4.Args.IndexOf(domashny, StringComparison.OrdinalIgnoreCase) < 0);

            // ВОПРОС СНЯТ ОТВЕТОМ — и телефону об этом говорят словами.
            var afterAnswer = Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("после ответа телефону сказано словами, что на вопрос уже ответили",
                afterAnswer.Body.Contains(Loc.T(PanelStrings.PhoneGateQuestionCancelled), StringComparison.Ordinal));

            // CANCEL СНИМАЕТ ВОПРОС: кто-то ответил раньше — телефон обязан узнать, а не ждать вечно.
            links[0].Emit(waterfall);

            var second = WaitForQuestion(stand, cookie, "question").GetAwaiter().GetResult();
            Check("второй вопрос показан телефону", second.Contains("\"state\":\"question\"", StringComparison.Ordinal));

            links[0].Emit(cancel);

            var cancelled = WaitForQuestion(stand, cookie, "cancelled").GetAwaiter().GetResult();

            Check("cancel снимает вопрос и телефону сказано словами",
                cancelled.Contains("\"state\":\"cancelled\"", StringComparison.Ordinal)
                && cancelled.Contains(Loc.T(PanelStrings.PhoneGateQuestionCancelled), StringComparison.Ordinal));
            Check("домашний каталог машины не виден и после снятия вопроса",
                cancelled.IndexOf(domashny, StringComparison.OrdinalIgnoreCase) < 0);

            // ЧУЖОЙ cancel НЕ СНИМАЕТ НАШ ВОПРОС: снятие — по нашему eventId и ни по какому другому.
            links[0].Emit(waterfall);

            var third = WaitForQuestion(stand, cookie, "question").GetAwaiter().GetResult();
            Check("третий вопрос показан телефону", third.Contains("\"state\":\"question\"", StringComparison.Ordinal));

            links[0].Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":"
                          + "{\"type\":\"cancel\",\"eventId\":\"чужой-0000-0000-0000-000000000000\"}}");

            Thread.Sleep(200);

            var kept = Send(stand.Http, HttpMethod.Get, "/question", cookie: cookie).GetAwaiter().GetResult();
            Check("чужой cancel наш вопрос не снимает", kept.Body.Contains("\"state\":\"question\"", StringComparison.Ordinal));

            // ---- S1+S2: оболочка, ассеты, новый вопрос, JSON-отмена, SSE --------------------

            var shell = Send(stand.Http, HttpMethod.Get, "/", cookie: cookie).GetAwaiter().GetResult();

            Check("корень отдаёт оболочку пульта, а не старую страницу",
                shell.Status == 200 && shell.Carries("id=\"root\"") && !shell.Carries("id=\"code\""));
            Check("оболочка знает про вход и слова пульта",
                shell.Carries("&quot;authed&quot;:true")
                && shell.Carries(Loc.T(PanelStrings.PhoneWebListTitle))
                && shell.Carries("/assets/"));
            Check("у оболочки нет внешних адресов",
                !shell.Carries("http://") && !shell.Carries("https://"));

            var assetMatch = System.Text.RegularExpressions.Regex.Match(
                shell.Body, "src=\"(/assets/[^\"]+\\.js)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            Check("в оболочке есть путь к скрипту пульта", assetMatch.Success);

            if (assetMatch.Success)
            {
                var asset = Send(stand.Http, HttpMethod.Get, assetMatch.Groups[1].Value)
                    .GetAwaiter().GetResult();

                Check("файл пульта отдаётся из exe", asset.Status == 200 && asset.Body.Length > 0);
            }

            var oldPage = Send(stand.Http, HttpMethod.Get, "/ask").GetAwaiter().GetResult();

            Check("запасной адрес /ask отдаёт прежнюю страницу",
                oldPage.Status == 200 && oldPage.Carries("id=\"code\""));

            var twice = stand.Gate.Start();

            Check("второй Start при живой двери идемпотентен", twice.Opened);
            Check("после второго Start корень отвечает",
                Send(stand.Http, HttpMethod.Get, "/").GetAwaiter().GetResult().Status == 200);

            // НОВАЯ ДВЕРЬ ВОПРОСА: карточка по белому списку (без clientId/agentId/intent).
            links[0].Emit(waterfall);

            WaitForQuestion(stand, cookie, "question").GetAwaiter().GetResult();
            var fresh = Send(stand.Http, HttpMethod.Get, "/api/phone/question", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("новый адрес вопроса отдаёт карточку по белому списку",
                fresh.Status == 200
                && fresh.Body.Contains("\"state\":\"question\"", StringComparison.Ordinal)
                && fresh.Body.Contains("\"id\":\"q1\"", StringComparison.Ordinal)
                && fresh.Body.Contains("\"expiresAt\":", StringComparison.Ordinal));
            Check("карточка не отдаёт clientId, agentId и intent",
                !fresh.Body.Contains(clientId, StringComparison.Ordinal)
                && !fresh.Body.Contains("agentId", StringComparison.Ordinal)
                && !fresh.Body.Contains("intent", StringComparison.Ordinal));

            // JSON-ОТМЕНА: телефон получает JSON, а не HTML-страницу (поправка §0.6).
            var jsonCancel = Send(
                    stand.Http, HttpMethod.Post, "/cancel",
                    "{\"eventId\":\"" + eventId + "\"}", cookie)
                .GetAwaiter().GetResult();

            Check("отмена по JSON отвечает JSON, а не страницей",
                jsonCancel.Status == 200
                && jsonCancel.Body.Contains("\"ok\":true", StringComparison.Ordinal)
                && jsonCancel.Body.Contains(Loc.T(PanelStrings.PhonePageCancelled), StringComparison.Ordinal));

            // ЖИВОЙ ПОТОК: заголовки, snapshot, запись и недописанный ответ — через подставную дверь.
            var sse = SseOnce(
                stand, "/api/phone/live?session=session-c16b-0000", cookie,
                () =>
                {
                    follow.Emit(
                        "{\"type\":\"item\",\"value\":{\"type\":\"snapshot\",\"header\":{\"id\":\"session-c16b-0000\"},"
                        + "\"cursor\":5,\"hasMore\":false,\"records\":[{\"type\":\"event\",\"event\":{\"type\":\"user/message\","
                        + "\"seq\":6,\"time\":1,\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"привет\"}],"
                        + "\"source\":{\"kind\":\"user\"}}}},{\"type\":\"event\",\"event\":{\"type\":\"system/message\","
                        + "\"seq\":7,\"time\":2,\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"системный\"}]}}}}]}}");
                    follow.Emit(
                        "{\"type\":\"item\",\"value\":{\"type\":\"assistant-stream\",\"frame\":{\"type\":\"chunk\","
                        + "\"attemptId\":\"a1\",\"revision\":1,\"index\":1,\"time\":3,\"chunk\":{\"type\":\"text-delta\",\"text\":\"кусочек\"}}}}");
                });

            Check("живой поток открылся как SSE (200 и text/event-stream)",
                sse.Contains("HTTP/1.1 200", StringComparison.Ordinal)
                && sse.Contains("text/event-stream", StringComparison.Ordinal));
            Check("поток отдал snapshot с нормализованной записью",
                sse.Contains("event: snapshot", StringComparison.Ordinal)
                && sse.Contains("\"type\":\"user\"", StringComparison.Ordinal));
            Check("системное сообщение движка телефону не ушло",
                !sse.Contains("system/message", StringComparison.Ordinal)
                && !sse.Contains("системный", StringComparison.Ordinal));
            Check("недописанный ответ пришёл кадром assistant",
                sse.Contains("event: assistant", StringComparison.Ordinal)
                && sse.Contains("кусочек", StringComparison.Ordinal));
            Check("домашний каталог машины не виден и в потоке",
                sse.IndexOf(domashny, StringComparison.OrdinalIgnoreCase) < 0);
            return 0;
        }

        // ---- (6) двери S3: задача, новая задача, стоп, очередь, «убрать» ------------------------

        /// <summary>
        /// ДВЕРИ S3 НА ПОДСТАВНЫХ ДВЕРЯХ (контракт S3 §11.2, шаги 1–14) — без движка и без сети.
        ///
        /// Чем это доказано: подставной движок считает вызовы и отдаёт ответы ПО ИМЕНИ, подставная
        /// дверь очереди подаёт кадр <c>baseline</c> и знает, закрыт ли поток. Ни одна живая запись
        /// и ни одно живое значение сюда не входит: проверяются форма доводов, порядок отказов, сроки
        /// и слова, а не чужой движок.
        /// </summary>
        private int Tasks()
        {
            Phase("двери S3: задача, новая задача, стоп, очередь, «убрать»");

            var paths = Stage("tasks");
            var engine = new StubEngine();
            var control = new StubControl();

            using var stand = RunGate(paths, enabled: true, PhoneEventDoors.None, engine, control: control.Doors);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки дверей S3", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var cookie = Pair(stand).GetAwaiter().GetResult();

            if (cookie.Length == 0)
            {
                Check("телефон сопрягся для проверки дверей S3", false);
                return 1;
            }

            var promptBody = "{\"sessionId\":\"session-1\",\"requestId\":\"req-1\",\"text\":\"построй отчёт\"}";

            // 1. БЕЗ ВХОДА И С ЧУЖОЙ ДВЕРЬЮ — ДО ДЕЙСТВИЯ.
            var noCookie = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody)
                .GetAwaiter().GetResult();

            Check("D1: без cookie — 401 словами S3",
                noCookie.Status == 401
                && noCookie.Carries(Loc.T(nameof(PanelStrings.PhoneTaskUnauthorized))));

            var foreign = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody, cookie,
                host: "evil.example:80").GetAwaiter().GetResult();

            Check("D1: чужой Host — 403 ДО действия и словами S3",
                foreign.Status == 403
                && foreign.Carries(Loc.T(nameof(PanelStrings.PhoneTaskForeignDoor)))
                && !foreign.Carries("evil.example"));

            Check("D1: отказы до действия НЕ зовут движок",
                !engine.Called("session/prompt"));

            // 2. МЕТОД: GET на дверь записи — 405 своими словами.
            var wrongMethod = Send(stand.Http, HttpMethod.Get, "/api/phone/prompt", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("D1: GET — 405 словами S3",
                wrongMethod.Status == 405
                && wrongMethod.Carries(Loc.T(nameof(PanelStrings.PhoneTaskNotAccepted))));

            // 3. ПРИНЯТО, И ДОВОДЫ — РОВНО §2.2.
            engine.Answers["session/prompt"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":true,\"value\":{\"accepted\":true}}}");

            var accepted = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody, cookie)
                .GetAwaiter().GetResult();

            Check("D1: принято — 200 accepted с адресом и requestId",
                accepted.Status == 200
                && accepted.Carries("\"accepted\":true")
                && accepted.Carries("session-1")
                && accepted.Carries("req-1")
                && accepted.Carries(Loc.T(nameof(PanelStrings.PhoneTaskAccepted))));

            var sent = engine.Last("session/prompt");

            Check("D1: доводы движку — mode=queue и тот же requestId",
                sent is { } call
                && call.Args.Contains("\"mode\":\"queue\"", StringComparison.Ordinal)
                && call.Args.Contains("\"requestId\":\"req-1\"", StringComparison.Ordinal)
                && call.Args.Contains("\"text\":\"построй отчёт\"", StringComparison.Ordinal));
            Check("D1: лишнее поле телефона (`cwd`) в движок не ушло",
                !engine.Called("session/prompt", args => args.Contains("cwd", StringComparison.Ordinal)));

            // 4. ПОВТОР — ТОТ ЖЕ requestId (на нём держится идемпотентность движка).
            var again = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody, cookie)
                .GetAwaiter().GetResult();

            Check("D1: повтор уходит с ТЕМ ЖЕ requestId",
                again.Status == 200
                && engine.Last("session/prompt") is { } repeat
                && repeat.Args.Contains("\"requestId\":\"req-1\"", StringComparison.Ordinal));

            // 5. ЗАЩЁЛКА: ВТОРАЯ ОТПРАВКА В ПОЛЁТЕ — 403 И БЕЗ ВЫЗОВА (DEF-072).
            engine.Hold.Reset();

            var inFlight = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody, cookie);

            var callsBefore = engine.Count("session/prompt");

            for (var attempt = 0; attempt < 400 && engine.Count("session/prompt") == callsBefore; attempt++)
                Thread.Sleep(5);

            var busy = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", promptBody, cookie)
                .GetAwaiter().GetResult();

            engine.Hold.Set();
            inFlight.GetAwaiter().GetResult();

            Check("DEF-072: вторая отправка в полёте — 403 словами и без второго вызова",
                busy.Status == 403
                && busy.Carries(Loc.T(nameof(PanelStrings.PhoneTaskBusy))));

            // 6. D2 — ДВА ВЫЗОВА, ПОВТОР БЕЗ СОЗДАНИЯ.
            engine.Answers["session/create"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":true,\"value\":{\"sessionId\":\"session-new-1\",\"cwd\":\"C:\\\\маркер\"}}}");


            var newBody = "{\"requestId\":\"new-1\",\"text\":\"сделай сайт\"}";

            var created = Send(stand.Http, HttpMethod.Post, "/api/phone/new", newBody, cookie)
                .GetAwaiter().GetResult();

            Check("D2: создан разговор и принята задача (repeated:false)",
                created.Status == 200
                && created.Carries("\"repeated\":false")
                && created.Carries("session-new-1")
                && created.Carries(Loc.T(nameof(PanelStrings.PhoneTaskNewAccepted))));

            Check("D2: доводы создания ПУСТЫЕ — ни папки, ни пресета",
                engine.Last("session/create") is { } create
                && create.Args.Contains("\"request\":{}", StringComparison.Ordinal));

            Check("D2: путь из ответа движка телефону не ушёл", !created.Carries("маркер"));

            var createdAgain = Send(stand.Http, HttpMethod.Post, "/api/phone/new", newBody, cookie)
                .GetAwaiter().GetResult();

            Check("D2: повтор — тот же разговор и НИ ОДНОГО нового создания",
                createdAgain.Status == 200
                && createdAgain.Carries("\"repeated\":true")
                && createdAgain.Carries("session-new-1")
                && engine.Count("session/create") == 1);

            // 7. D3 — ДВА ЧЕСТНЫХ ИСХОДА, И «НЕТ АГЕНТА» — НЕ 502.
            engine.Answers["session/cancel"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":true,\"value\":{\"accepted\":true}}}");

            var stopped = Send(stand.Http, HttpMethod.Post, "/api/phone/stop", "{\"sessionId\":\"session-1\"}", cookie)
                .GetAwaiter().GetResult();

            Check("D3: ход остановлен — 200 stopped словами S3",
                stopped.Status == 200
                && stopped.Carries("\"stopped\":true")
                && stopped.Carries(Loc.T(nameof(PanelStrings.PhoneStopDone))));

            engine.Answers["session/cancel"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":false,\"error\":{\"code\":\"session/not-found\"}}}");

            var idle = Send(stand.Http, HttpMethod.Post, "/api/phone/stop", "{\"sessionId\":\"session-1\"}", cookie)
                .GetAwaiter().GetResult();

            Check("D3: нет живого агента — 200 idle, а НЕ 502",
                idle.Status == 200
                && idle.Carries("\"state\":\"idle\"")
                && idle.Carries(Loc.T(nameof(PanelStrings.PhoneStopIdle))));

            // 8. D5 — «УБРАНО» И «УЖЕ НЕТ» ОДИНАКОВО УСПЕШНЫ.
            engine.Answers["session/updateQueue"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":true,\"value\":{\"accepted\":true}}}");

            var removed = Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
                "{\"sessionId\":\"session-1\",\"itemId\":\"m-1\"}", cookie).GetAwaiter().GetResult();

            Check("D5: пункт убран — 200 removed",
                removed.Status == 200
                && removed.Carries("\"state\":\"removed\"")
                && removed.Carries(Loc.T(nameof(PanelStrings.PhoneQueueRemoved))));

            Check("D5: доводы — itemId и action.kind=remove",
                engine.Last("session/updateQueue") is { } queueCall
                && queueCall.Args.Contains("\"itemId\":\"m-1\"", StringComparison.Ordinal)
                && queueCall.Args.Contains("\"action\":{\"kind\":\"remove\"}", StringComparison.Ordinal));

            engine.Answers["session/updateQueue"] = new PhoneEngineAnswer(
                200, "{\"result\":{\"ok\":false,\"error\":{\"code\":\"session/queue-item-not-found\"}}}");

            var gone = Send(stand.Http, HttpMethod.Post, "/api/phone/queue/remove",
                "{\"sessionId\":\"session-1\",\"itemId\":\"m-1\"}", cookie).GetAwaiter().GetResult();

            Check("D5: повтор — 200 gone, а не ошибка и без кода",
                gone.Status == 200
                && gone.Carries("\"state\":\"gone\"")
                && gone.Carries(Loc.T(nameof(PanelStrings.PhoneQueueAlreadyGone)))
                && !gone.Carries("queue-item-not-found"));

            // 9. D4 — ОЧЕРЕДЬ ИЗ ПРОЕКЦИИ inbox, ПОРЯДОК, ФИЛЬТР И ЗАКРЫТЫЙ ПОТОК.
            var baseline = QueueBaseline(
                "session-1",
                "[" + QueueItem("m-1", "первое") + "," + QueueItem("m-2", "второе") + "]",
                "[" + QueueItem("m-3", "в текущий ход") + ","
                + QueueItem("srv-1", "служебное", "user-approval") + "]");

            var queue = QueueOnce(stand, control, baseline, cookie);

            Check("D4: очередь прочитана — 3 пункта, steering первой",
                queue.Status == 200
                && queue.Carries("\"total\":3")
                && queue.Carries("\"shown\":3")
                && queue.Body.IndexOf("m-3", StringComparison.Ordinal) < queue.Body.IndexOf("m-1", StringComparison.Ordinal));
            Check("D4: место пункта вычислено (steering и queued)",
                queue.Carries("\"placement\":\"steering\"") && queue.Carries("\"placement\":\"queued\""));
            Check("D4: служебный пункт не показан и его id не ушёл",
                !queue.Carries("srv-1") && !queue.Carries("служебное"));
            Check("D4: запрещённых полей в ответе нет",
                !queue.Carries("next-turn") && !queue.Carries("asOfSeq") && !queue.Carries("projections"));
            Check("D4: поток session/control ЗАКРЫТ до ответа", control.Opens == 1 && control.Live == 0);

            // 10. СОРОК ПЯТЬ ПУНКТОВ — ДВАДЦАТЬ В ОТВЕТЕ И ЧЕСТНЫЕ ЧИСЛА.
            var many = string.Join(",", Enumerable.Range(1, 45).Select(index => QueueItem("m-" + index, "пункт " + index)));
            var big = QueueBaseline("session-1", "[" + many + "]", "[]");

            var limited = QueueOnce(stand, control, big, cookie);

            Check("D4: 45 пунктов — показано 20, hasMore, total 45",
                limited.Status == 200
                && limited.Carries("\"total\":45")
                && limited.Carries("\"shown\":20")
                && limited.Carries("\"hasMore\":true"));

            // 11. СЕССИЯ НЕ ОТКРЫТА — state no-live, а не «пусто».
            var other = QueueOnce(stand, control, baseline.Replace("\"session-1\"", "\"session-9\"", StringComparison.Ordinal), cookie);

            Check("D4: блока сессии нет — 200 no-live словами",
                other.Status == 200
                && other.Carries("\"state\":\"no-live\"")
                && other.Carries(Loc.T(nameof(PanelStrings.PhoneQueueNoLive))));

            // 12. BASELINE НЕ ПРИШЁЛ — 502 В СВОЙ СРОК, А НЕ МОЛЧАНИЕ.
            var startedAt = Stopwatch.StartNew();
            var silent = QueueOnce(stand, control, null, cookie);
            startedAt.Stop();

            Check("D4: baseline не пришёл — 502 словами в свой срок",
                silent.Status == 502
                && silent.Carries(Loc.T(nameof(PanelStrings.PhoneQueueUnreadable)))
                && startedAt.Elapsed < TimeSpan.FromSeconds(6));

            // 13. СЛОВА — ЯЗЫК ТЕЛЕФОНА (zh), А НЕ ПАНЕЛИ.
            var chinese = Send(stand.Http, HttpMethod.Post, "/api/phone/prompt", "{}", language: "zh")
                .GetAwaiter().GetResult();

            Check("слова дверей S3 — на языке телефона (zh)",
                chinese.Status == 401
                && chinese.Carries(Loc.TIn("zh", nameof(PanelStrings.PhoneTaskUnauthorized)))
                && !chinese.Carries(Loc.TIn("ru", nameof(PanelStrings.PhoneTaskUnauthorized))));

            return 0;
        }

        /// <summary>Одно чтение очереди с поданным кадром: запрос идёт своей ниткой, кадр — когда поток открыт.</summary>
        /// <summary>Строка JSON в читаемом виде (кириллица остаётся словами) — для подставных кадров.</summary>
        private static string QueueQuote(string? text) =>
            "\"" + (text ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\"";

        /// <summary>Один пункт очереди в форме проекции <c>inbox</c> (контракт §5.2).</summary>
        private static string QueueItem(string id, string text, string kind = "user") =>
            "{\"id\":" + QueueQuote(id) + ",\"source\":{\"kind\":" + QueueQuote(kind)
            + "},\"content\":[{\"type\":\"text\",\"text\":" + QueueQuote(text) + "}]}";

        /// <summary>
        /// КАДР BASELINE — РОВНО ТА ФОРМА, какую отдаёт 0.2.1: очередь лежит в
        /// <c>value.projections[&lt;sessionId&gt;].values.inbox</c> (поправка §0.1). Ни <c>queues</c>,
        /// ни <c>jobs</c> в этой форме нет — и это тоже часть проверки.
        /// </summary>
        private static string QueueBaseline(string sessionId, string nextTurn, string nextStep) =>
            "{\"type\":\"item\",\"streamId\":\"phone-queue-1\",\"value\":{\"type\":\"baseline\",\"value\":{\"projections\":{"
            + QueueQuote(sessionId)
            + ":{\"asOfSeq\":3,\"values\":{\"inbox\":{\"next-turn\":" + nextTurn
            + ",\"next-step\":" + nextStep + "}}}}}}}";

        private static Reply QueueOnce(Stand stand, StubControl control, string? frame, string cookie)
        {
            var before = control.Opens;
            var task = Send(stand.Http, HttpMethod.Get, "/api/phone/queue?session=session-1", cookie: cookie);

            for (var attempt = 0; attempt < 600 && control.Opens == before; attempt++) Thread.Sleep(5);

            if (frame is not null) control.Emit(frame);

            return task.GetAwaiter().GetResult();
        }
        // ---- (7) S4a: 14-е имя, архив, живая очередь и признак «список мог измениться» ---------

        /// <summary>
        /// S4a НА ПОДСТАВНЫХ ДВЕРЯХ: 14-е имя, фильтр архива, событие <c>queue</c> в живом SSE и
        /// признак <c>stale</c> от кадра <c>api-session/status</c>. Ни живого движка, ни сети —
        /// только форма и правила; живое рукопожатие проверяет раздел (5).
        /// </summary>
        private int Signals()
        {
            Phase("S4a: 14-е имя архива, живая очередь и признак списка");

            var paths = Stage("signals");
            var engine = new StubEngine();
            var links = new List<StubLink>();

            var events = new PhoneEventDoors(
                onFrame =>
                {
                    var made = new StubLink(onFrame);

                    lock (links) links.Add(made);

                    return made;
                },
                Log);

            var follow = new StubFollow();
            var control = new StubControl();
            var workspace = new StubWorkspace();

            using var stand = RunGate(
                paths, enabled: true, events, engine,
                follow: follow.Doors, control: control.Doors, workspace: workspace.Doors);

            if (!stand.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки S4a", false);
                return 1;
            }

            _started.Add(stand.Gate);

            var cookie = Pair(stand).GetAwaiter().GetResult();

            if (cookie.Length == 0)
            {
                Check("телефон сопрягся для проверки S4a", false);
                return 1;
            }

            // 1. 14-Е ИМЯ: В СПИСКЕ, ПОТОК, А СЫРЫМ POST — ОТКАЗ СЛОВАМИ.
            var call = PhoneDecisions.Allowed
                .FirstOrDefault(item => item.Endpoint == PhoneDecisions.WorkspaceFollowEndpoint);

            Check("S4a: workspace/follow — 14-е имя белого списка и это ПОТОК",
                call is not null && call.Mode == EngineRpcMode.Stream && PhoneDecisions.Allowed.Count == 14);

            var posted = Send(
                    stand.Http, HttpMethod.Post, "/api/workspace/follow", "{\"args\":{}}", cookie)
                .GetAwaiter().GetResult();

            Check("S4a: POST /api/workspace/follow — отказ словами и без вызова движка",
                posted.Status == 403
                && posted.Carries(string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.T(nameof(PanelStrings.PhoneGateStreamRefusedFormat)),
                    PhoneDecisions.WorkspaceFollowEndpoint))
                && !engine.Called("workspace/follow"));

            // 2. АРХИВ: СПИСОК БЕЗ АРХИВНЫХ, СЧЁТ В ЖУРНАЛЕ БЕЗ ИМЁН.
            engine.Answers[PhoneDecisions.SessionListEndpoint] =
                new PhoneEngineAnswer(200, SessionList("s-1", "s-arch", "s-2"));
            workspace.Reply = WorkspaceBaseline("s-arch");

            var first = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("S4a: архивная сессия не показана, остальные на месте",
                first.Status == 200
                && !first.Carries("s-arch")
                && first.Carries("s-1")
                && first.Carries("s-2"));

            Check("S4a: данные рабочего пространства (path/title/sessionIds) не уехали",
                !first.Carries("маркер-путь")
                && !first.Carries("маркер-название")
                && !first.Carries("s-pin"));

            var journal = Journal;

            Check("S4a: в журнал — только счёт скрытых, без имён и путей",
                journal.Contains("архивные сессии", StringComparison.Ordinal)
                && !journal.Contains("s-arch", StringComparison.Ordinal)
                && !journal.Contains("маркер-путь", StringComparison.Ordinal)
                && !journal.Contains("маркер-название", StringComparison.Ordinal));

            var again = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("S4a: повторный запрос тоже без архивных и без ВТОРОГО потока",
                again.Status == 200 && !again.Carries("s-arch") && workspace.Opens == 1);

            // 3. НОЛЬ СКРЫТЫХ — СТРОКИ В ЖУРНАЛЕ НЕТ (кэш истекает: часы двигает проверка).
            Advance(TimeSpan.FromSeconds(31));

            engine.Answers[PhoneDecisions.SessionListEndpoint] =
                new PhoneEngineAnswer(200, SessionList("s-1", "s-2"));
            workspace.Reply = WorkspaceBaseline();

            var hiddenBefore = JournalLines("архивные сессии");
            var zero = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("S4a: при 0 скрытых строки в журнале нет",
                zero.Status == 200 && JournalLines("архивные сессии") == hiddenBefore);

            // 4. АРХИВ НЕ ПРОЧИТАЛСЯ — СПИСОК КАК ЕСТЬ, ПРИЧИНА В ЖУРНАЛЕ.
            Advance(TimeSpan.FromSeconds(31));

            engine.Answers[PhoneDecisions.SessionListEndpoint] =
                new PhoneEngineAnswer(200, SessionList("s-1", "s-arch"));
            workspace.Silent = true;

            var failure = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                .GetAwaiter().GetResult();

            workspace.Silent = false;

            Check("S4a: архив не прочитался — список отдан как есть, причина в журнале",
                failure.Status == 200
                && failure.Carries("s-arch")
                && Journal.Contains("архив сессий не прочитан", StringComparison.Ordinal));

            // 5. ЖИВАЯ ОЧЕРЕДЬ В ТОМ ЖЕ SSE: СВОЁ — ЕСТЬ, ЧУЖОЕ — НЕТ.
            // КАДРЫ ПОДАЁТ ФОНОВАЯ НИТКА: калитка открывает control не в тот же миг, что ленту,
            // и «подать сразу после запроса» — гонка. Нитка ждёт открытия и шлёт кадры,
            // а клиент в это время читает SSE.
            var pump = new Thread(() =>
            {
                for (var attempt = 0; attempt < 500 && control.Opens == 0; attempt++) Thread.Sleep(10);

                control.Emit(QueueBaseline("session-live", "[" + QueueItem("m-1", "первое") + "]", "[]"));
                control.Emit(Projection(
                    "session-live", "inbox",
                    "{\"next-turn\":[" + QueueItem("m-2", "второе") + "],\"next-step\":[]}"));
                control.Emit(Projection("session-live", "workingDirectory", "\"маркер-путь\""));
                // ЧУЖАЯ ПРОЕКЦИЯ С ТЕКСТАМИ ХОДА — второй вид утечки (F-01): её тоже не должно быть в журнале.
                control.Emit(Projection(
                    "session-live", "turnOutline",
                    "[{\"turn\":1,\"prompt\":\"маркер-текст\",\"response\":\"маркер-ответ\"}]"));
                control.Emit(Projection(
                    "session-other", "inbox",
                    "{\"next-turn\":[" + QueueItem("x-1", "чужое") + "],\"next-step\":[]}"));
            })
            { IsBackground = true };

            pump.Start();

            var sse = SseOnce(
                stand, "/api/phone/live?session=session-live", cookie, () => { }, readMs: 4000);

            pump.Join(TimeSpan.FromSeconds(5));

            Check("S4a: живой SSE отдал event: queue из baseline и из кадра inbox",
                sse.Contains("event: queue", StringComparison.Ordinal)
                && sse.Contains("первое", StringComparison.Ordinal)
                && sse.Contains("второе", StringComparison.Ordinal));

            Check("S4a: чужая сессия и чужая проекция телефону не уехали",
                !sse.Contains("маркер-путь", StringComparison.Ordinal)
                && !sse.Contains("workingDirectory", StringComparison.Ordinal)
                && !sse.Contains("чужое", StringComparison.Ordinal)
                && !sse.Contains("x-1", StringComparison.Ordinal));

            // ЖУРНАЛ ПОСЛЕ ЖИВОЙ ЛЕНТЫ — ОБЯЗАТЕЛЬНАЯ ПРОВЕРКА (F-01): кадры управления — ЧУЖАЯ РАБОТА,
            // и в журнал они не попадают ни сырыми, ни кусками (строка до фильтра уносила
            // workingDirectory — путь владельца, turnOutline — тексты ходов, и чужие сессии).
            // Проверяются МАРКЕРЫ, а не «похоже на кадр»: так улика не зависит от формы кадра.
            var journalAfterLive = Journal;

            Check("S4a: журнал после живой ленты чист — ни маркеров, ни сырых кадров, ни чужих сессий",
                !journalAfterLive.Contains("маркер-путь", StringComparison.Ordinal)
                && !journalAfterLive.Contains("маркер-текст", StringComparison.Ordinal)
                && !journalAfterLive.Contains("session-other", StringComparison.Ordinal)
                && !journalAfterLive.Contains("phone-queue-1", StringComparison.Ordinal)
                && !journalAfterLive.Contains("asOfSeq", StringComparison.Ordinal));

            // ПОТОК CONTROL ЖИВЁТ РОВНО СТОЛЬКО, СКОЛЬКО ЛЕНТА: после ухода клиента он обязан закрыться.
            var deadline = DateTime.UtcNow.AddSeconds(8);

            while (control.Live > 0 && DateTime.UtcNow < deadline)
            {
                control.Emit(Projection("session-live", "title", "\"чужой ключ\""));
                // КАДР, КОТОРЫЙ ТОЧНО ПИШЕТСЯ В СОКЕТ (`turn/start` разбор считает «мимо»), —
                // нам нужна именно неудачная запись: по ней калитка узнаёт уход клиента.
                follow.Emit(
                    "{\"type\":\"item\",\"value\":{\"type\":\"assistant-stream\",\"frame\":{\"type\":\"chunk\","
                    + "\"attemptId\":\"a1\",\"revision\":1,\"index\":1,\"time\":1,\"chunk\":{\"type\":\"text-delta\","
                    + "\"text\":\"кусочек\"}}}}");
                Thread.Sleep(50);
            }

            Check("S4a: поток control закрыт вместе с SSE и не переживает экран", control.Live == 0);

            // 6. ПРИЗНАК «СПИСОК МОГ ИЗМЕНИТЬСЯ» — ИЗ УЖЕ ОТКРЫТОГО $events.
            engine.Answers[PhoneDecisions.SessionListEndpoint] =
                new PhoneEngineAnswer(200, SessionList("s-1"));

            var staleBody = string.Empty;

            for (var attempt = 0; attempt < 100 && !links.Any(link => link.Alive); attempt++) Thread.Sleep(10);

            for (var attempt = 0; attempt < 100 && !staleBody.Contains("\"stale\":true", StringComparison.Ordinal); attempt++)
            {
                if (links.Count > 0)
                {
                    links[0].Emit(
                        "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"emit\","
                        + "\"event\":\"api-session/status\",\"sessionId\":\"s-hidden\",\"running\":false}}");
                }

                staleBody = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                    .GetAwaiter().GetResult().Body;

                Thread.Sleep(20);
            }

            Check("S4a: кадр api-session/status ставит признак stale без адреса сессии",
                staleBody.Contains("\"stale\":true", StringComparison.Ordinal)
                && !staleBody.Contains("s-hidden", StringComparison.Ordinal));

            var cleared = Send(stand.Http, HttpMethod.Get, "/api/phone/sessions", cookie: cookie)
                .GetAwaiter().GetResult();

            Check("S4a: признак снимается при выдаче списка", !cleared.Carries("\"stale\""));

            // 6б. ЖИВАЯ ПОДСКАЗКА СПИСКА (G1, слово владельца 11.11.2026): СВОЁ $events, ОДИН БИТ.
            // Список на событие не читается вовсе; в кадре — только имя события и пустой объект.
            StubLink? hintLink = null;

            var hint = SseOnce(
                stand, "/api/phone/list/live", cookie,
                () =>
                {
                    // ЖДЁМ ОТКРЫТИЯ ВТОРОГО ПОТОКА: первый — своя подписка калитки (вопросы).
                    for (var attempt = 0; attempt < 200 && hintLink is null; attempt++)
                    {
                        lock (links) hintLink = links.Count >= 2 ? links[^1] : null;

                        if (hintLink is null) Thread.Sleep(10);
                    }

                    hintLink?.Emit(
                        "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"emit\","
                        + "\"event\":\"api-session/status\",\"sessionId\":\"s-g1-скрытая\",\"running\":true,"
                        + "\"cwd\":\"маркер-путь\"}}");
                });

            Check("G1: по кадру api-session/status подсказка списка шлёт event: stale",
                hint.Contains("event: stale", StringComparison.Ordinal));

            Check("G1: в кадре подсказки — только имя события и пустой объект",
                hint.Contains("data: {}", StringComparison.Ordinal)
                && !hint.Contains("s-g1-скрытая", StringComparison.Ordinal)
                && !hint.Contains("running", StringComparison.Ordinal)
                && !hint.Contains("маркер-путь", StringComparison.Ordinal));

            // УХОД ТЕЛЕФОНА: подписка подсказки обязана закрыться, а не пережить экран.
            // Часы двигаем перед каждым кадром: на замороженных часах окно склейки не кончается.
            var hintClosed = false;
            var hintDeadline = DateTime.UtcNow.AddSeconds(8);

            while (!hintClosed && DateTime.UtcNow < hintDeadline)
            {
                Advance(PhoneGate.ListLiveCoalesce);

                hintLink?.Emit(
                    "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"emit\","
                    + "\"event\":\"api-session/status\",\"sessionId\":\"s-g1-скрытая\",\"running\":false}}");

                Thread.Sleep(50);

                hintClosed = hintLink is null || !hintLink.Alive;
            }

            Check("G1: уход телефона закрывает $events подсказки", hintClosed);

            // 7. БЕЗ session/control ЛЕНТА РАБОТАЕТ, А ОЧЕРЕДЬ МОЛЧИТ — И ПРИЧИНА НАЗВАНА.
            var quietPaths = Stage("signals-quiet");
            var quietFollow = new StubFollow();

            using var quiet = RunGate(
                quietPaths, enabled: true, PhoneEventDoors.None, new StubEngine(), follow: quietFollow.Doors);

            if (!quiet.Gate.Start().Opened)
            {
                Check("калитка открылась для проверки молчащей очереди", false);
                return 1;
            }

            _started.Add(quiet.Gate);

            var quietCookie = Pair(quiet).GetAwaiter().GetResult();

            var quietSse = SseOnce(
                quiet, "/api/phone/live?session=session-quiet", quietCookie,
                () => quietFollow.Emit(
                    "{\"type\":\"item\",\"value\":{\"type\":\"snapshot\",\"header\":{\"id\":\"session-quiet\"},"
                    + "\"cursor\":1,\"hasMore\":false,\"records\":[]}}"));

            Check("S4a: без session/control лента работает, очередь молчит без ложных обещаний",
                quietSse.Contains("event: snapshot", StringComparison.Ordinal)
                && !quietSse.Contains("event: queue", StringComparison.Ordinal));

            Check("S4a: причина молчания очереди названа в журнале",
                Journal.Contains("живая очередь молчит", StringComparison.Ordinal));

            return 0;
        }

        /// <summary>Снимок журнала — строкой: проверки читают его так же, как человек.</summary>
        private string Journal => string.Join("\n", _log.ToArray());

        /// <summary>Сколько строк журнала содержат слова — чтобы «строки нет» было проверяемо.</summary>
        private int JournalLines(string words) =>
            _log.ToArray().Count(line => line.Contains(words, StringComparison.Ordinal));

        /// <summary>ОТВЕТ <c>session/list</c> РОВНО ТОЙ ФОРМЫ, какую отдаёт движок (result → value → items).</summary>
        private static string SessionList(params string[] sessionIds) =>
            "{\"result\":{\"ok\":true,\"value\":{\"items\":["
            + string.Join(
                ",",
                sessionIds.Select(id =>
                    "{\"sessionId\":" + QueueQuote(id) + ",\"updatedAt\":1761770000000,"
                    + "\"running\":false,\"blank\":false}"))
            + "]}}}";

        /// <summary>
        /// BASELINE <c>workspace/follow</c> — С МАРКЕРАМИ в тех полях, которые наружу не должны
        /// уехать (<c>items[].path/title/sessionIds</c>, <c>pinnedSessionIds</c>): так утечка ловится
        /// строкой, а не надеждой.
        /// </summary>
        private static string WorkspaceBaseline(params string[] archived) =>
            "{\"type\":\"item\",\"streamId\":\"phone-workspace-1\",\"value\":{\"type\":\"baseline\",\"value\":{"
            + "\"items\":[{\"workspaceId\":\"w-1\",\"path\":\"маркер-путь\",\"title\":\"маркер-название\","
            + "\"sessionIds\":[\"s-1\"],\"createdAt\":1,\"updatedAt\":2}],"
            + "\"archivedSessionIds\":[" + string.Join(",", archived.Select(QueueQuote)) + "],"
            + "\"pinnedSessionIds\":[\"s-pin\"]}}}";

        /// <summary>Кадр проекции <c>session/control</c> — ровно та форма, какую отдаёт 0.2.1.</summary>
        private static string Projection(string sessionId, string key, string value) =>
            "{\"type\":\"item\",\"streamId\":\"phone-queue-1\",\"value\":{\"type\":\"projection\","
            + "\"sessionId\":" + QueueQuote(sessionId) + ",\"key\":" + QueueQuote(key)
            + ",\"value\":" + value + ",\"seq\":9}}";
    }
}









