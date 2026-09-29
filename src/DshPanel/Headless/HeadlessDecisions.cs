using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;

namespace DshPanel.Headless;

/// <summary>Какой из двух режимов без окна запущен.</summary>
public enum HeadlessMode
{
    /// <summary><c>--backup &lt;папка&gt;</c> — снять полную копию и положить архив в папку.</summary>
    Backup,

    /// <summary><c>--restore &lt;архив&gt;</c> — показать план наката и разложить копию.</summary>
    Restore,
}

/// <summary>
/// Разобранная просьба: что делать, с чем и какие согласия даны. Пусто в <see cref="Refusal"/> —
/// работать можно; непусто — это готовый текст отказа (код 2), и он же печатается в отчёт.
/// </summary>
public sealed record HeadlessRequest(
    HeadlessMode Mode,
    string Path,
    bool WithEngine,
    bool WithPanel,
    bool WithKeys,
    bool Shareable,
    string Refusal)
{
    public bool Ok => Refusal.Length == 0;
}

/// <summary>
/// Чем кончилась разведка сервера. Все три ответа разные, и путать их нельзя: «не работает»
/// и «посмотреть не удалось» — это не одно и то же, а на разнице между ними стоит честность отчёта.
/// </summary>
public enum ServerAnswer
{
    /// <summary>На порту этого прогона сервер DSH не отвечает.</summary>
    Idle,

    /// <summary>Отвечает: копия будет «на ходу», а накат поверх живого движка не делается.</summary>
    Working,

    /// <summary>Посмотреть не удалось (таблица портов не читается) — это НЕ «не работает».</summary>
    Unknown,
}

/// <summary>
/// РЕШЕНИЯ ДВУХ РЕЖИМОВ БЕЗ ОКНА: `--backup &lt;папка&gt;` и `--restore &lt;архив&gt;`.
///
/// Зачем они вообще. До 26.09.2026 копию умел снять только экран, а приёмка этапа 4 идёт
/// **на чистой ВМ в неинтерактивной сессии**: там окна нет вовсе, и «снять копию, развернуть её
/// и продолжить» проверить было нечем. Отсюда два ключа — снять копию и вернуть её без окна.
///
/// Почему разбор и решения живут ЗДЕСЬ, а не в `Program.Main`: точка входа для тестов недоступна,
/// а решения эти — про данные человека (красная линия 5), значит обязаны быть проверяемыми без
/// диска, без сети и без окна. Исполнение — рядом (<c>HeadlessRun</c>), разведка сервера — рядом
/// (<c>HeadlessServer</c>).
///
/// Четыре правила, и каждое выросло из случая, а не из осторожности:
///
/// 1. **Только в изоляции и только с явным путём.** Право берётся той же дверью, что у контроллера
///    копий (<see cref="BackupController.For"/>): без корня прогона здесь отказ кодом 2, а не
///    «попробуем тихонько». Путь обязан быть полным: у <c>[IO.File]</c> рабочий каталог ПРОЦЕССА,
///    а не каталог PowerShell.
/// 2. **Состав копии — из настроек, а передача — разовым ключом.** Приватные ключи — разрешение
///    человека, и живёт оно в `settings.json`; «копия для передачи» решением владельца 27.09.2026
///    (п. 11) настройкой быть перестала и стала РАЗОВЫМ действием на одну копию — потому у
///    `--backup` есть свой ключ <c>--shareable</c>, и второго места правды у него нет.
///    Ключей «положи ключи» у `--backup` не бывает: это разрешение живёт в настройках, и попытка
///    передать его ключом — отказ, а не молчание.
/// 3. **Согласия наката по умолчанию ВЫКЛЮЧЕНЫ** — как в <see cref="RestoreOptions"/> и в окне
///    копий: движок, настройки панели и ключи возвращаются только явными ключами.
/// 4. **Сервер не гасится никогда.** Работает ли он — вопрос разведки и только чтения; говорит
///    об этом <see cref="ServerLine"/> словами, а не кодом.
/// </summary>
public static class HeadlessDecisions
{
    public const string BackupKey = "--backup";
    public const string RestoreKey = "--restore";
    public const string WithEngineKey = "--with-engine";
    public const string WithPanelKey = "--with-panel";
    public const string WithKeysKey = "--with-keys";

    /// <summary>
    /// <c>--shareable</c> — снять ОДНУ копию для передачи: файл ключей доступа в архив не кладётся.
    /// Решение владельца 27.09.2026 (п. 11 <c>docs\DESIGN.md</c>): это разовое действие человека,
    /// а не настройка, — и без ключа способность исчезла бы у режима без окна вовсе.
    ///
    /// Ключ принадлежит КОПИИ и только ей: у наката он означает совсем другое, поэтому там отказ.
    /// </summary>
    public const string ShareableKey = "--shareable";

    /// <summary>
    /// Ключи этих режимов. Лежат здесь, а не в <see cref="StartModes"/>, по одной причине: разбор
    /// просьбы и перечень известных ключей обязаны брать их из ОДНОГО места, иначе однажды
    /// поправят одно.
    /// </summary>
    public static readonly string[] Keys =
    {
        BackupKey, RestoreKey, WithEngineKey, WithPanelKey, WithKeysKey, ShareableKey,
    };

    /// <summary>Это ключ режима без окна (а не его согласие)?</summary>
    public static bool IsMode(string key) => key is BackupKey or RestoreKey;

    /// <summary>Заголовок отчёта: у копии и наката они разные, чтобы строки можно было читать и грепать.</summary>
    public static string Name(HeadlessMode mode) => mode == HeadlessMode.Backup ? "КОПИЯ" : "НАКАТ";

    /// <summary>Строка отчёта со своим заголовком. Каждая строка — факт, а не украшение.</summary>
    public static string Line(HeadlessMode mode, string text) => Name(mode) + "| " + text;

    /// <summary>
    /// Последняя строка отчёта — по коду возврата, а не «на всякий случай». Коды те же, что у всех
    /// проверок панели: <c>0</c> успех, <c>1</c> провал, <c>2</c> неприменимо (отказ).
    /// </summary>
    public static string Verdict(HeadlessMode mode, int code) => code switch
    {
        0 => Name(mode) + " УСПЕХ",
        1 => Name(mode) + " ПРОВАЛ",
        _ => Name(mode) + " ОТКАЗ",
    };

    // --- разбор просьбы -------------------------------------------------------

    /// <summary>
    /// Разбирает аргументы режима. <c>null</c> — это не наш ключ (разбирать нечего);
    /// иначе запись с готовым текстом отказа, если просьба невыполнима.
    ///
    /// Отказы здесь ГРОМКИЕ и с причиной — по тому же правилу, что у <see cref="StartModes.Refuse"/>:
    /// опечатка в скрипте не должна выглядеть как «режим отработал».
    /// </summary>
    public static HeadlessRequest? Parse(string[] rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        if (rest.Length == 0 || !IsMode(rest[0])) return null;

        var mode = rest[0] == BackupKey ? HeadlessMode.Backup : HeadlessMode.Restore;
        var what = mode == HeadlessMode.Backup ? "папки для архива" : "архива копии";
        var example = mode == HeadlessMode.Backup
            ? $"{BackupKey} C:\\Копии"
            : $"{RestoreKey} C:\\Копии\\dsh2-backup-2026-09-26-120000-full.zip";

        // Путь — ОБЯЗАТЕЛЬНЫЙ аргумент, ровно как имя файла у «--shot»: без него делать нечего,
        // и «неизвестный ключ» здесь был бы неправдой — ключ известный, аргумента нет.
        if (rest.Length < 2)
        {
            return Refuse(mode,
                $"ключ «{rest[0]}» указан без {what} — делать нечего. Нужен полный путь: «{example}».");
        }

        if (rest[1].StartsWith('-'))
        {
            return Refuse(mode,
                $"ключ «{rest[0]}» указан без {what} (следом идёт ключ «{rest[1]}»). " +
                $"Нужен полный путь: «{example}».");
        }

        var path = rest[1];

        // Относительный путь отвергается, и причина не «так аккуратнее»: у [IO.File] рабочий каталог
        // ПРОЦЕССА, а не каталог PowerShell, поэтому «..\копии» означало бы не то, что человек
        // написал в строке. Грабля области, записана в AGENTS.md и стоила уже не одного прогона.
        if (!Path.IsPathFullyQualified(path))
        {
            return Refuse(mode,
                $"путь «{path}» не полный: у [IO.File] рабочий каталог ПРОЦЕССА, а не каталог " +
                $"PowerShell, поэтому путь обязан быть абсолютным — «{example}».");
        }

        var withEngine = false;
        var withPanel = false;
        var withKeys = false;
        var shareable = false;

        for (var i = 2; i < rest.Length; i++)
        {
            var argument = rest[i];

            // Согласия наката — только у наката. У копии состав берётся ИЗ НАСТРОЕК (приватные
            // ключи — разрешение человека, и оно живёт в `settings.json`), а передача — разовое
            // действие с ключом `--shareable`; тот же ключ здесь означал бы совсем другое,
            // поэтому это отказ, а не молчание.
            if (mode == HeadlessMode.Backup && argument is WithEngineKey or WithPanelKey or WithKeysKey)
            {
                return Refuse(mode,
                    $"«{argument}» относится к накату, а не к копии: приватные ключи в копию кладёт " +
                    "НАСТРОЙКА панели (раздел «Резервное копирование»), то есть состав берётся " +
                    $"из настроек, а «копия для передачи» — разовое действие с ключом «{ShareableKey}». " +
                    "Состав в настройках и разовое решение — разные вещи, и путать их нельзя.");
            }

            // И обратно: передача — про КОПИЮ. У наката этот ключ означал бы «вернуть передаваемое»,
            // чего не существует вовсе.
            if (mode == HeadlessMode.Restore && argument == ShareableKey)
            {
                return Refuse(mode,
                    $"«{ShareableKey}» относится к копии, а не к накату: передают КОПИЮ, а накат " +
                    "возвращает её на место. Снимите копию для передачи отдельным запуском " +
                    $"«{BackupKey} <папка> {ShareableKey}».");
            }

            switch (argument)
            {
                case WithEngineKey: withEngine = true; break;
                case WithPanelKey: withPanel = true; break;
                case WithKeysKey: withKeys = true; break;
                case ShareableKey: shareable = true; break;

                default:
                    return Refuse(mode, mode == HeadlessMode.Backup
                        ? $"лишний аргумент «{argument}»: у режима «{BackupKey}» больше ничего нет — " +
                          $"папка идёт вторым аргументом, а из ключей только «{ShareableKey}»."
                        : $"лишний аргумент «{argument}»: у режима «{RestoreKey}» есть только согласия " +
                          $"{WithEngineKey}, {WithPanelKey} и {WithKeysKey} — больше ничего.");
            }
        }

        return new HeadlessRequest(mode, path, withEngine, withPanel, withKeys, shareable, string.Empty);
    }

    private static HeadlessRequest Refuse(HeadlessMode mode, string reason) =>
        new(mode, string.Empty, false, false, false, false, Name(mode) + " ОТКАЗ: " + reason);

    // --- право прогона --------------------------------------------------------

    /// <summary>
    /// Имеет ли ЭТОТ прогон право снимать копию и раскладывать её. Дверь та же, что у контроллера
    /// копий (<see cref="BackupController.For"/>), и второго её понимания в панели быть не должно:
    /// у человека, открывшего панель, право своё, у изолированного прогона — своё, а у прогона
    /// проверки без корня прав нет вовсе. Здесь человеком и не пахнет: режим без окна запускает скрипт.
    /// </summary>
    public static bool HasRight(RunContext context, bool humanLaunch)
    {
        ArgumentNullException.ThrowIfNull(context);
        return BackupController.For(context, humanLaunch);
    }

    /// <summary>
    /// Отказ прогону, у которого нет корня. Красная линия 5: **полная копия и накат — только
    /// в изоляции и только с явным путём.** Без корня каталоги берутся у владельца, и «копия»
    /// легла бы в его же данные, а накат затёр бы их.
    /// </summary>
    public static string IsolationRefusal(HeadlessMode mode)
    {
        var subject = mode == HeadlessMode.Backup ? "полная копия допустима" : "накат копии допустим";

        return Name(mode) + " ОТКАЗ: " + subject +
            " ТОЛЬКО в изолированном прогоне и только с явным путём: без корня это запись " +
            "в каталоги владельца. Запустите с «" + RunRequest.RootSwitchArgument + " <свой путь>» " +
            "или задайте переменную " + RunRequest.RootVariable + " (красная линия 5).";
    }

    // --- пути -----------------------------------------------------------------

    /// <summary>
    /// Куда ляжет архив: названная человеком папка плюс имя по ОБЩЕМУ правилу панели — полная копия,
    /// то есть с движком. Имя собирается здесь, потому что его видит человек в проводнике, и правило
    /// у него одно на всю панель (<see cref="BackupNaming.ArchiveName"/>, префикс <c>dsh2-backup-</c>:
    /// папка копий общая с панелью 1.x, и различаются панели именно именами), — а не «своё у каждого
    /// экрана».
    /// </summary>
    public static string ArchivePath(string folder, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return Path.Combine(
            Path.TrimEndingDirectorySeparator(folder),
            BackupNaming.ArchiveName(now, withEngine: true));
    }

    // --- сервер: только чтение, только слова ---------------------------------

    /// <summary>
    /// Делает ли работающий сервер накат невозможным. Да — и только он. Ключ не умеет спрашивать,
    /// а накат поверх живого движка — ровно то, от чего предупреждает окно копий: файлы сессий
    /// живой процесс держит и дописывает. «Посмотреть не удалось» — это НЕ «работает»: на этом
    /// отказ не строим, но говорим об этом словами (<see cref="ServerLine"/>).
    /// </summary>
    public static bool BlocksRestore(ServerAnswer answer) => answer == ServerAnswer.Working;

    /// <summary>
    /// Считать ли, что сервер мог работать. «Не удалось посмотреть» идёт сюда же, и это осознанно:
    /// пропущенная оговорка «копия снята на ходу» — это находка В2, ради которой оговорка и заведена,
    /// а лишняя оговорка только настораживает. Ошибка в эту сторону дешевле.
    /// </summary>
    public static bool ServerMayRun(ServerAnswer answer) => answer != ServerAnswer.Idle;

    /// <summary>
    /// Слова о сервере. Код в отчёте не годится: человек читает строки, и «работает ли сервер»
    /// должен быть ответом, а не значением перечня.
    ///
    /// ⚠️ Утверждение здесь — про ПОРТ этого прогона, а не про машину: на порту владельца (3080)
    /// может работать его живой DSH, и к данным изолированного прогона он отношения не имеет.
    /// </summary>
    public static string ServerLine(ServerAnswer answer, int port, HeadlessMode mode)
    {
        var subject = $"сервер DSH на порту {port}";

        return answer switch
        {
            ServerAnswer.Working when mode == HeadlessMode.Backup =>
                $"{subject} отвечает — копия снята НА ХОДУ: сессии в этот момент могли дописываться",
            ServerAnswer.Working =>
                $"{subject} отвечает — накат поверх живого движка не делается",

            ServerAnswer.Idle when mode == HeadlessMode.Backup =>
                $"{subject} не отвечает — копия снята на остановленном сервере",
            ServerAnswer.Idle =>
                $"{subject} не отвечает — накат можно делать на остановленном сервере",

            _ when mode == HeadlessMode.Backup =>
                $"работает ли {subject} — выяснить не удалось (таблица портов не читается): " +
                "копия помечена как снятая на ходу, чтобы оговорки не потерять",
            _ =>
                $"работает ли {subject} — выяснить не удалось (таблица портов не читается): " +
                "накат идёт, но занятые движком файлы могут не замениться — движок назовёт их в отчёте",
        };
    }

    // --- строки отчёта --------------------------------------------------------

    /// <summary>
    /// Что именно попадёт в копию — СЛОВАМИ. Приватные ключи берутся ИЗ НАСТРОЕК, а передача —
    /// разовое решение ЭТОГО запуска (ключ <see cref="ShareableKey"/>), и это разные вещи: первое
    /// делает ключом доступа сам архив, второе убирает из архива файл ключей доступа.
    /// Промолчать об этом в отчёте нельзя — это решения о данных человека.
    ///
    /// ⚠️ Запрещённое сочетание называется здесь ДО начала работы: причину нужно прочитать раньше,
    /// чем человек будет ждать копию, которой не будет. Само правило — в ОДНОМ месте домена
    /// (<see cref="BackupPlanner.ShareableWithKeysRefusal"/>), и берётся оно оттуда же, откуда его
    /// берут окно копий и план.
    /// </summary>
    public static IReadOnlyList<string> CompositionLines(bool withKeys, bool shareable, int keyDirectories)
    {
        var lines = new List<string>
        {
            $"состав копии: приватные ключи — НАСТРОЙКА панели, «копия для передачи» — разовое " +
            $"действие этого запуска (ключ «{ShareableKey}»)",
            withKeys
                ? "приватные ключи: КЛАДУТСЯ (разрешение включено) — сам архив становится ключом доступа"
                : "приватные ключи: НЕ кладутся (разрешение выключено — умолчание)",
            shareable
                ? "копия для передачи: файл ключей доступа (.credentials.yaml) в архив НЕ кладётся"
                : "своя копия: файл ключей доступа (.credentials.yaml) в архив кладётся",
            $"своих каталогов ключей в настройках: {keyDirectories} (каталог SSH берётся вместе с ними)",
        };

        var refusal = BackupPlanner.ShareableWithKeysRefusal(withKeys, shareable);
        if (refusal.Length > 0) lines.Add("ОТКАЗ: " + refusal);

        return lines;
    }

    /// <summary>
    /// Согласия наката. По умолчанию ВЫКЛЮЧЕНЫ — как в <see cref="RestoreOptions"/> и в окне копий:
    /// подмена работающего ломает работающее, поэтому «вернуть движок», «вернуть настройки панели»
    /// и «вернуть ключи» — три отдельных решения, и каждое называется вслух.
    /// </summary>
    public static IReadOnlyList<string> ConsentLines(bool withEngine, bool withPanel, bool withKeys) => new[]
    {
        "согласия: по умолчанию выключены — подмена работающего ломает работающее",
        withEngine
            ? $"движок и Node: возвращаются ({WithEngineKey})"
            : $"движок и Node: НЕ возвращаются (нужен {WithEngineKey})",
        withPanel
            ? $"настройки и состояние панели: возвращаются ({WithPanelKey})"
            : $"настройки и состояние панели: НЕ возвращаются (нужен {WithPanelKey})",
        withKeys
            ? $"ключи: возвращаются ({WithKeysKey})"
            : $"ключи: НЕ возвращаются (нужен {WithKeysKey})",
        "предохранительная копия: снимается ВСЕГДА — без неё накат не начинается",
    };

    /// <summary>
    /// Строки ПЛАНА наката: сначала то, что вернёт копия (та же строка, что показывает окно),
    /// потом каждая группа отдельно — по ней и видно, что ляжет, а что пропущено и почему
    /// (расхождение рабочей папки, находка В3, живёт именно в строке группы).
    ///
    /// Печатается ДО наката, и это не украшение: план — то, ради чего на копию смотрят до первой
    /// записи. Сам план только читает архив.
    /// </summary>
    public static IReadOnlyList<string> PlanLines(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var lines = new List<string> { "план наката: " + plan.Summary() };
        lines.AddRange(plan.Groups.Select(group => "группа: " + group.Describe()));
        return lines;
    }
}
