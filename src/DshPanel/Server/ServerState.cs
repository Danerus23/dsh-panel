using DshPanel.Shell;
namespace DshPanel.Server;

/// <summary>Что находится на порту. Три случая, и средний — самый важный: чужое.</summary>
public enum ServerPresence
{
    /// <summary>Порт свободен: сервера нет.</summary>
    Stopped,

    /// <summary>Порт слушает кто-то, но это не сервер DSH. Гасить его панель не имеет права.</summary>
    BusyByOther,

    /// <summary>На порту отвечает сервер DSH — по отпечатку, а не по догадке.</summary>
    Running,
}

/// <summary>
/// Кто поднял сервер, которым управляет панель. Одно значение, а не два признака: два
/// независимых булева «поднят панелью» и «взят под управление» допускают состояние
/// «оба сразу», которого не бывает, — и однажды оно бы возникло.
/// </summary>
public enum ServerOwner
{
    /// <summary>Панель ничем не управляет: своего сервера не поднимала и в чужой не встраивалась.</summary>
    None,

    /// <summary>Сервер поднят ЭТОЙ панелью — гасится свободно, это её процесс.</summary>
    Panel,

    /// <summary>
    /// Сервер найден работающим и взят под управление человеком (решение владельца 24.09.2026).
    /// Такой сервер панель гасит по отдельному подтверждению КАЖДЫЙ раз — но только пока согласие
    /// не запомнено. Решение владельца 26.09.2026: ответ «беру под управление» спрашивается один
    /// раз и запоминается, после чего панель считает это окружение своим и гасит сервер свободно.
    /// </summary>
    Adopted,
}

/// <summary>
/// Состояние сервера: всё, что панель о нём знает, одним значением.
///
/// Записи, а не класса с полями — намеренно: состояние сравнивается в тестах и печатается
/// в отчёт целиком, и мутируемое состояние здесь было бы лишней возможностью ошибиться.
/// </summary>
public readonly record struct ServerState(
    ServerPresence Presence,
    int Port,
    int Pid,
    string ProcessName,
    string Detail)
{
    public static ServerState Stopped(int port, string? detail = null) =>
        new(ServerPresence.Stopped, port, 0, string.Empty, detail ?? PanelStrings.SrvPortFree);

    public bool IsRunning => Presence == ServerPresence.Running;
}

/// <summary>
/// Найденный работающий сервер DSH, который подняла не панель. Это ещё НЕ взятый под управление
/// сервер: панель обязана показать его человеку и спросить (решение владельца 24.09.2026),
/// а не встраиваться молча.
/// </summary>
public readonly record struct FoundServer(int Port, int Pid, string ProcessName)
{
    /// <summary>Порт владельца: именно на нём чаще всего и стоит его живой DSH.</summary>
    public bool IsOwnerPort => Port == ServerDecisions.OwnerPort;
}

/// <summary>Чем кончилось опознание процесса, слушающего порт. <c>Match</c> — это сервер DSH.</summary>
public enum DshProcessVerdict
{
    Match,

    /// <summary>Порт никто не слушает — процесса нет.</summary>
    NotListening,

    /// <summary>Образ процесса не node.exe.</summary>
    NotNode,

    /// <summary>Порт не ответил отпечатком сервера DSH.</summary>
    NoFingerprint,

    /// <summary>В командной строке нет пути к <c>@deepseek-ai\dsh\lib\bin.js</c>.</summary>
    NotDshBin,

    /// <summary>В командной строке нет подкоманды <c>web</c>.</summary>
    NoWebCommand,

    /// <summary>
    /// Порт из командной строки не тот, что слушает процесс. Это не «наш сервер с другой
    /// записью», а противоречие: слушает один, а объявлен другой.
    /// </summary>
    PortMismatch,
}

/// <summary>
/// Факты о процессе, который слушает порт. Всё, что нужно для решения «это сервер DSH»,
/// и ничего, что нельзя подставить в проверке: поэтому решение ниже — чистая функция,
/// а не разбор живого процесса.
/// </summary>
public readonly record struct DshProcessFacts(
    bool ListenerAlive,
    bool IsNode,
    string CommandLine,
    bool FingerprintMatched);

/// <summary>
/// Решения о сервере — ЧИСТЫЕ функции. Здесь нет ни одного обращения к системе, и это главное
/// свойство файла: именно такие решения перебираются тестами по всем сочетаниям, а не «проверяются
/// на глазок» один раз. Всё, что требует ввода-вывода (кто слушает порт, что ответил сервер,
/// как выглядит командная строка процесса), живёт рядом — в <see cref="PortTable"/>,
/// <see cref="FreePort"/>, <see cref="ProcessFacts"/>, <see cref="ServerDiscovery"/>
/// и <see cref="ServerController"/>.
/// </summary>
public static class ServerDecisions
{
    /// <summary>
    /// Отпечаток сервера DSH: так отвечает `GET /` без cookie (проверено разведкой, `docs\ENGINE.md` §5).
    /// Строка специфичная — по ней «наш» сервер отличается от любой другой программы на порту.
    /// </summary>
    public const string Fingerprint = "dsh web authentication required";

    /// <summary>
    /// Порт владельца — 3080. На нём стоит его живой DSH, и через него идёт канал сессии агента.
    ///
    /// ⚠️ **Решение владельца 26.09.2026: запрет 3080 для 2.0 СНЯТ** — он переезжает с панели
    /// 1.x, и 2.0 обязана поднимать свой сервер именно на 3080, иначе переезд не имеет смысла.
    /// Но снят он ТОЛЬКО для обычного запуска панели человеком: прогон проверки не занимает
    /// этот порт ни при каких обстоятельствах — ни один прогон не имеет права оборвать канал
    /// ЭТОЙ сессии. Право приходит открытым текстом
    /// (<see cref="IsAllowedPort(int, bool)"/>, <c>mayOccupyOwnerPort</c>) и по умолчанию
    /// не выдаётся нигде.
    ///
    /// ⚠️ И это по-прежнему запрет на ЗАНЯТИЕ порта, а не на то, чтобы его видеть: найденный
    /// на 3080 живой сервер панель обязана показать и предложить встроиться (решение владельца
    /// 24.09.2026) — иначе весь шаг «встроиться» не имел бы главного случая.
    /// </summary>
    public const int OwnerPort = 3080;

    /// <summary>
    /// Порт, который панель занимает, поднимая СВОЙ сервер: **3080** — тот же, на котором стояла
    /// панель 1.x (`AppSettings.serverPort`). Владелец убирает 1.x и переезжает на 2.0, поэтому
    /// умолчание 2.0 обязано совпасть с привычным ему адресом (решение владельца 26.09.2026).
    ///
    /// До этого решения умолчанием был <see cref="RunFallbackPort"/>: 3080 значился запретным.
    /// </summary>
    public const int DefaultServerPort = OwnerPort;

    /// <summary>
    /// Куда приводится порт, когда права занять порт владельца у прогона НЕТ.
    ///
    /// Отдельная константа, а не «умолчание»: без неё приведение порта владельца дало бы
    /// <see cref="DefaultServerPort"/> — то есть тот же 3080, и прогон проверки всё равно занял бы
    /// порт владельца. Это ровно тот случай, ради которого приведение и разделено на два ответа.
    ///
    /// Число — прежнее умолчание 2.0 (3081): так файл настроек, где оно лежало как умолчание,
    /// узнаётся при чтении и переводится на новое (см. <c>PanelSettings.ServerPortChosen</c>).
    /// </summary>
    public const int RunFallbackPort = 3081;

    public const int MinPort = 1;

    public const int MaxPort = 65535;

    /// <summary>
    /// Порт, который панели разрешено занять: любой настоящий, а порт владельца — **только
    /// по явному праву**.
    ///
    /// Право — ОБЯЗАТЕЛЬНЫЙ параметр без значения по умолчанию, и это главное в подписи:
    /// «canOccupyOwnerPort по умолчанию false» означало бы, что забыть его выдать безопасно,
    /// а забыть запретить — нет; здесь наоборот, и забыть выдать право нельзя, потому что
    /// вызов просто не соберётся. Ровно так же устроено право «поднимать рядом» в контроллере.
    /// </summary>
    public static bool IsAllowedPort(int port, bool mayOccupyOwnerPort) =>
        port is > 0 and < 65536 && (port != OwnerPort || mayOccupyOwnerPort);

    /// <summary>
    /// Приводит желаемый порт к тому, который панель действительно может занять: непонятное
    /// число — умолчание (<see cref="DefaultServerPort"/>), порт владельца без права —
    /// <see cref="RunFallbackPort"/>.
    ///
    /// Два разных ответа, а не один: с правом «не подходит» значит «возьми 3080», без права —
    /// «возьми 3081». Сведи их в один, и прогон проверки привёл бы 3080 к 3080 и занял бы
    /// порт владельца, то есть ровно то, чего красная линия не велит делать никогда.
    ///
    /// Возвращает порт — и вызывающий обязан сказать об этом словами, если порт изменился:
    /// молча съехать на другой значит соврать человеку о том, где его сервер.
    /// </summary>
    public static int NormalizePort(int port, bool mayOccupyOwnerPort) =>
        IsAllowedPort(port, mayOccupyOwnerPort)
            ? port
            : mayOccupyOwnerPort ? DefaultServerPort : RunFallbackPort;

    /// <summary>
    /// Поднимать ли СВОЙ сервер самому при старте панели — по тому, что панель уже знает.
    ///
    /// Три условия, и каждое из случая:
    ///
    /// * <paramref name="owner"/> — панель ничем не управляет: если у неё уже есть свой сервер
    ///   или она во что-то встроена, второй движок на тех же данных не нужен;
    /// * <paramref name="presence"/> — на порту никого. Работающий сервер (свой или найденный)
    ///   поднимать заново нельзя, а занятый чужой программой порт даст движку EADDRINUSE;
    /// * <paramref name="foundForeignServer"/> — ЗАМОК ОТ ВТОРОГО ДВИЖКА (находка 25.09.2026):
    ///   найденный работающий DSH работает на том же `~/.dsh`, и второй движок там трогает
    ///   текущую работу владельца.
    ///
    /// Право прогона (изолированность, обычный запуск человеком) сюда НЕ входит и спрашивается
    /// ДО разведки: это разные вопросы — «можно ли вообще» и «можно ли сейчас».
    /// </summary>
    public static bool ShouldAutoStart(ServerOwner owner, ServerPresence presence, bool foundForeignServer) =>
        owner == ServerOwner.None
        && presence == ServerPresence.Stopped
        && !foundForeignServer;

    /// <summary>Кто на порту: никто, чужой или сервер DSH. Решается двумя фактами, оба — извне.</summary>
    public static ServerPresence Classify(int listenerPid, bool fingerprintMatched)
    {
        // Никто не слушает — и не важно, ответил ли кто-то на запрос: ответа быть не может.
        if (listenerPid <= 0) return ServerPresence.Stopped;

        // Слушают, и это сервер DSH — единственный факт, по которому панель признаёт сервер своим.
        if (fingerprintMatched) return ServerPresence.Running;

        // Слушают, но не DSH. Молча гасить такое нельзя: это может быть чужая работа с
        // несохранёнными данными. В v1 на этом стояла отдельная ветка отказа, и она оправдалась.
        return ServerPresence.BusyByOther;
    }

    /// <summary>
    /// Тот ли это сервер. Код 401 обязателен: 200 в ответ на `GET /` означает, что отдаёт
    /// что-то другое (у DSH без cookie доступа нет вовсе).
    /// </summary>
    public static bool LooksLikeDshWeb(int statusCode, string? body) =>
        statusCode == 401
        && body is not null
        && body.Contains(Fingerprint, StringComparison.OrdinalIgnoreCase);

    // --- решения о кнопках ------------------------------------------------

    /// <summary>Можно ли жать «Запустить»: только когда на своём порту никого.</summary>
    public static bool CanStart(ServerPresence presence) => presence == ServerPresence.Stopped;

    /// <summary>
    /// Предлагать ли встроиться в найденный сервер. Предлагаем ровно тогда, когда панель ещё
    /// ничем не управляет: если у неё уже есть свой сервер или она во что-то встроена, второе
    /// предложение было бы шумом и поводом ошибиться.
    /// </summary>
    public static bool CanAdopt(ServerOwner owner, bool foundSomething) =>
        foundSomething && owner == ServerOwner.None;

    /// <summary>
    /// Требует ли гашение отдельного подтверждения.
    ///
    /// Да — для встроенного сервера, но ровно до тех пор, пока согласие НЕ запомнено. Человек,
    /// однажды ответивший «беру под управление», считает это окружение своим (решение владельца
    /// 26.09.2026: «спросить ОДИН раз и запомнить ответ»), и переспрашивать его каждый раз значило
    /// бы это решение не исполнять. Запомненного согласия нет (его не было, или записать его
    /// не удалось) — спрашиваем каждый раз, как решил владелец 24.09.2026.
    /// </summary>
    public static bool NeedsStopConfirmation(
        ServerOwner owner, ServerPresence presence, bool consentRemembered) =>
        owner == ServerOwner.Adopted && presence == ServerPresence.Running && !consentRemembered;

    /// <summary>
    /// Мешает ли найденный чужой сервер поднять СВОЙ.
    ///
    /// Замок включён по умолчанию и снимается только ЯВНЫМ правом «поднимать рядом»: в обычном
    /// запуске панель и найденный DSH работают на ОДНОМ домашнем каталоге движка (<c>~/.dsh</c>),
    /// поэтому второй движок там — это второй движок на тех же данных, а не «ещё один сервер»
    /// (находка 25.09.2026: кнопка «Запустить» была заряжена в профиль владельца). Осознанно
    /// рядом поднимают только прогоны в изоляции — у них свой корень и свои данные.
    /// </summary>
    public static bool BlockedByForeignServer(bool allowParallelStart, bool foundForeignServer) =>
        foundForeignServer && !allowParallelStart;

    /// <summary>
    /// Можно ли жать «Остановить».
    ///
    /// Свой сервер панель гасит свободно: это её процесс, и она за него отвечает.
    /// Найденный — по подтверждению, спрошенному КАЖДЫЙ раз (решение владельца 24.09.2026),
    /// потому что через такой сервер может идти его текущая работа. Но если человек уже сказал
    /// «беру под управление» и ответ запомнен (решение владельца 26.09.2026), подтверждения
    /// не нужно: спрашивать одно и то же дважды — это не осторожность, а неисполненное решение.
    /// </summary>
    public static bool CanStop(
        ServerOwner owner, ServerPresence presence, bool confirmed, bool consentRemembered) =>
        presence == ServerPresence.Running
        && (owner == ServerOwner.Panel || (owner == ServerOwner.Adopted && (confirmed || consentRemembered)));

    // --- приметы владения: это сервер DSH? --------------------------------

    /// <summary>
    /// Имя пакета движка внутри пути к bin.js. Проверяется как часть ЦЕЛОГО аргумента, а не
    /// вхождение подстроки в командную строку: `Contains` по всей строке признал бы нашим
    /// и `…\lib\bin.js.old` (находка аудита v1 23.09.2026).
    /// </summary>
    private const string DshPackageMark = @"@deepseek-ai\dsh\lib\bin.js";

    /// <summary>
    /// Путь к движку DSH. Сравниваются слэши обоих видов: движок и прежние сборки звали по-разному,
    /// и свой сервер из-за формы записи не должен становиться чужим.
    /// </summary>
    public static bool LooksLikeDshBin(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return false;

        var normalized = argument.Trim().Trim('"').Replace('/', '\\');
        return normalized.EndsWith(DshPackageMark, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Аргументы командной строки по одному, с снятыми кавычками. Разбор нарочно простой: нас
    /// интересует путь к файлу, а не полная семантика cmd — экранирования внутри кавычек
    /// в командной строке node не бывает.
    ///
    /// Перенесено из v1 без изменения поведения: на разборе стоит примета владения, и менять
    /// работающее ради красоты здесь нечего.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string? commandLine)
    {
        var text = commandLine ?? string.Empty;
        var result = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) break;

            string argument;
            if (text[index] == '"')
            {
                var end = text.IndexOf('"', index + 1);
                if (end < 0)
                {
                    argument = text[(index + 1)..];
                    index = text.Length;
                }
                else
                {
                    argument = text[(index + 1)..end];
                    index = end + 1;
                }
            }
            else
            {
                var end = index;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                argument = text[index..end];
                index = end;
            }

            if (argument.Length > 0) result.Add(argument);
        }

        return result;
    }

    /// <summary>Есть ли в командной строке подкоманда <c>web</c> — именно отдельным аргументом.</summary>
    public static bool HasWebCommand(string? commandLine) =>
        Arguments(commandLine).Any(a => a.Equals("web", StringComparison.OrdinalIgnoreCase));

    /// <summary>Есть ли среди аргументов путь к движку DSH.</summary>
    public static bool HasDshBin(string? commandLine) =>
        Arguments(commandLine).Any(LooksLikeDshBin);

    /// <summary>
    /// Дешёвая часть примет — без сети. По ней разведка решает, стоит ли вообще спрашивать
    /// отпечаток у порта: спрашивать у каждой слушающей программы на машине было бы и медленно,
    /// и грубо (десятки HTTP-запросов в секунду на ровном месте).
    ///
    /// Итоговое решение всё равно принимает <see cref="ClassifyDshProcess"/> — с отпечатком.
    /// </summary>
    public static bool LooksLikeDshCommandLine(string? commandLine, int port)
    {
        if (!HasDshBin(commandLine)) return false;
        if (!HasWebCommand(commandLine)) return false;

        var declared = PortInCommandLine(commandLine);
        return declared <= 0 || declared == port;
    }

    /// <summary>
    /// Порт, объявленный в командной строке: «--port 3080», «--port=3080», «--port:3080»
    /// (движок и прежние сборки могли звать по-разному). Хвост <c>(?!\d)</c> не даёт порту 3080
    /// совпасть с 30800. <c>0</c> — порт не объявлен вовсе.
    ///
    /// Регулярка перенесена из v1 как есть: на ней стоит примета владения.
    /// </summary>
    public static int PortInCommandLine(string? commandLine)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            commandLine ?? string.Empty,
            @"--port[=:\s]+""?(\d{1,5})(?!\d)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success) return 0;
        return int.TryParse(match.Groups[1].Value, out var port) ? port : 0;
    }

    /// <summary>Объявлен ли в командной строке именно этот порт.</summary>
    public static bool CommandLineHasPort(string? commandLine, int port) =>
        PortInCommandLine(commandLine) == port;

    /// <summary>
    /// Первый элемент командной строки — путь к самому исполняемому файлу.
    /// </summary>
    public static string FirstToken(string? commandLine)
    {
        var text = (commandLine ?? string.Empty).TrimStart();
        if (text.Length == 0) return string.Empty;

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Это сервер DSH? Решение принимается по фактам, и порядок проверок — от дешёвого
    /// и обязательного к уточняющему.
    ///
    /// Обязательны ВСЕ три приметы, названные владельцем 24.09.2026 («отпечаток, bin.js и --port
    /// в командной строке — приметы владения из v1»): отпечаток <c>401 dsh web authentication
    /// required</c>, путь к <c>@deepseek-ai\dsh\lib\bin.js</c> и подкоманда <c>web</c>.
    ///
    /// Про порт есть уточнение, и оно измерено, а не придумано: если порт в командной строке
    /// ОБЪЯВЛЕН и не совпадает с тем, кто слушает порт, — это противоречие, и такой процесс
    /// своим не признаётся. Если порт не объявлен вовсе, примета не пропадает: у движка есть
    /// умолчание, и решает тогда отпечаток. Замер 24.09.2026 на живом сервере владельца
    /// (`docs\ENGINE.md` §5) показал, что он объявляет порт явно:
    /// <c>node.exe …\@deepseek-ai\dsh\lib\bin.js web --no-open --port 3080</c>, — то есть
    /// на сегодняшнем поведении это уточнение не сказывается ничем.
    /// </summary>
    public static DshProcessVerdict ClassifyDshProcess(DshProcessFacts facts, int port)
    {
        if (!facts.ListenerAlive) return DshProcessVerdict.NotListening;
        if (!facts.IsNode) return DshProcessVerdict.NotNode;

        // Отпечаток — решающий признак: он принадлежит именно серверу DSH, а не похожей программе.
        if (!facts.FingerprintMatched) return DshProcessVerdict.NoFingerprint;

        if (!HasDshBin(facts.CommandLine)) return DshProcessVerdict.NotDshBin;
        if (!HasWebCommand(facts.CommandLine)) return DshProcessVerdict.NoWebCommand;

        var declared = PortInCommandLine(facts.CommandLine);
        return declared <= 0 || declared == port ? DshProcessVerdict.Match : DshProcessVerdict.PortMismatch;
    }

    /// <summary>Короткий ответ на тот же вопрос. Подробности — <see cref="ClassifyDshProcess"/>.</summary>
    public static bool LooksLikeDshProcess(DshProcessFacts facts, int port) =>
        ClassifyDshProcess(facts, port) == DshProcessVerdict.Match;
}
