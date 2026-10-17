using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// СЛОВА УСТАНОВКИ ДВИЖКА — одна дверь (как <c>SkillPhrases</c> у умений и <c>EngineRpc</c> у вызовов).
///
/// **Зачем одна дверь.** Установка говорит с человеком в трёх местах: строкой хода в журнале,
/// отказом с причиной и списком расхождений после установки. Если бы каждое из них собирало фразу
/// само, однажды три места сказали бы разное об одном и том же — а человек читает именно их.
///
/// ⚠️ **Строк в коде нет.** Здесь только имена ключей и сборка: сами фразы лежат в трёх словарях
/// (`ru`, `en`, `zh`), и проверка `EngineInstallTests` требует, чтобы у каждого ключа была строка
/// на всех трёх языках. Числа (сколько чего не сошлось) подставляются доводами.
/// </summary>
public static class EngineInstallPhrases
{
    /// <summary>Строка хода: что панель делает прямо сейчас и куда ставит.</summary>
    public static string Started(string version, string installDir) =>
        Loc.T("EngineInstallStartedFormat", version, installDir);

    /// <summary>Отказ: Node не нашёлся.</summary>
    public static string NodeMissing() => Loc.T("EngineInstallNodeMissing");

    /// <summary>Отказ: Node старше порога — с названной версией.</summary>
    public static string NodeOld(string version, string required) => Loc.T("EngineInstallNodeOld", version, required);

    /// <summary>Отказ: npm не нашёлся.</summary>
    public static string NpmMissing() => Loc.T("EngineInstallNpmMissing");

    /// <summary>Отказ: npm отказал — с кодом и последней сказанной строкой.</summary>
    public static string NpmFailed(int code, string output) =>
        Loc.T("EngineInstallNpmFailed", code, output.Length > 0 ? output : Loc.T("EngineInstallOutputSilent"));

    /// <summary>Отказ: установка не уложилась в срок.</summary>
    public static string TooLong(int minutes) => Loc.T("EngineInstallTooLong", minutes);

    /// <summary>Отказ: место установки не подготовилось.</summary>
    public static string NoPlace(string installDir, string reason) =>
        Loc.T("EngineInstallNoPlace", installDir, reason);

    /// <summary>Отказ: дерево разошлось с проверенным набором.</summary>
    public static string TreeWrong(string problems) => Loc.T("EngineInstallTreeWrong", problems);

    /// <summary>Строка шага обновления по ключу (с доводами, если они есть).</summary>
    public static string UpdateStep(string key, params object[] args) => Loc.T(key, args);

    /// <summary>Отказ пути обновления по ключу.</summary>
    public static string UpdateRefusal(string key) => Loc.T(key);

    /// <summary>
    /// ЗАМКА ДЛЯ ЭТОЙ ВЕРСИИ НЕТ — ставить её панель не умеет (ставит только по замку).
    /// Говорится ДО работы: иначе панель поставила бы по замку другую версию, а объявила эту.
    /// </summary>
    public static string NoLockForVersion(string version) =>
        Loc.T(EngineInstallRefusals.NoLockForVersion, version);

    /// <summary>Движок погашен — одна строка после дела.</summary>
    public static string UpdateStopped() => Loc.T(EngineInstallRefusals.Stopped);

    /// <summary>Погасить движок не удалось.</summary>
    public static string UpdateStopFailed() => Loc.T(EngineInstallRefusals.StopFailed);

    /// <summary>Копия профиля снята.</summary>
    public static string UpdateCopied() => Loc.T(EngineInstallRefusals.Copied);

    /// <summary>Проба прошла.</summary>
    public static string UpdateProbed() => Loc.T(EngineInstallRefusals.Probed);

    /// <summary>Проба не прошла.</summary>
    public static string UpdateProbeFailed() => Loc.T(EngineInstallRefusals.ProbeFailed);

    /// <summary>Обновление состоялось.</summary>
    public static string UpdateDone(string version) => Loc.T(EngineInstallRefusals.Done, version);
    /// <summary>Ход: свой Node понадобился — качаем официальный выпуск.</summary>
    public static string NodeDownloading(string release) => Loc.T("EngineInstallNodeDownloadingFormat", release);

    /// <summary>Ход: качаем архив Node.</summary>
    public static string NodeFetching(string archive) => Loc.T("EngineInstallNodeFetchingFormat", archive);

    /// <summary>Удача: свой Node скачан, распакован и проверен.</summary>
    public static string NodeReady(string version, string path) => Loc.T("EngineInstallNodeReadyFormat", version, path);

    /// <summary>Отказ: официальный файл сумм не прочитался.</summary>
    public static string NodeSourceFailed(string reason) => Loc.T("EngineInstallNodeSourceFailed", reason);

    /// <summary>Отказ: нашего архива нет в официальном файле сумм.</summary>
    public static string NodeSumMissing(string archive) => Loc.T("EngineInstallNodeSumMissing", archive);

    /// <summary>Отказ: официальная сумма и закреплённая разошлись.</summary>
    public static string NodeSumMismatch() => Loc.T("EngineInstallNodeSumMismatch");

    /// <summary>Отказ: архив не скачался.</summary>
    public static string NodeDownloadFailed(string reason) => Loc.T("EngineInstallNodeDownloadFailed", reason);

    /// <summary>Отказ: архив не распаковался.</summary>
    public static string NodeUnpackFailed(string kind) => Loc.T("EngineInstallNodeUnpackFailed", kind);

    /// <summary>Отказ: распакованный Node не запускается или моложе порога.</summary>
    public static string NodeBroken(string version) => Loc.T("EngineInstallNodeBroken", version);

    /// <summary>
    /// ОТКАТ: установка не удалась, папка версии убрана, панель осталась на прежней версии —
    /// и с предупреждением про сессии, дословные слова владельца: *«вернёте движок — потеряете
    /// работу, сделанную на новой версии: её журнал новее, старый его не читает»* (замер: сессии
    /// формата v4 старая версия не открывает вовсе).
    /// </summary>
    public static string RolledBack(string version) => Loc.T("EngineInstallRolledBackFormat", version);

    /// <summary>Журнал: убраны версии движка, которые больше не нужны (держим текущую и прежнюю).</summary>
    public static string Removed(int count) => Loc.T("EngineInstallRemovedFormat", count);

    /// <summary>Журнал: запись «что стоит сейчас» не удалась — откат в таком случае некуда делать.</summary>
    public static string RecordFailed(string reason) => Loc.T("EngineInstallRecordFailedFormat", reason);

    /// <summary>Строка удачи: движок поставлен и сверен, с числами сверки.</summary>
    public static string Done(string version, string numbers) =>
        Loc.T("EngineInstallDoneFormat", version, numbers);

    /// <summary>Числа сверки словами: сколько не сошлось и кого эта машина не требует.</summary>
    public static string Numbers(EngineTreeReport? tree) =>
        tree is null
            ? string.Empty
            : Loc.T("EngineTreeNumbersFormat", tree.Missing.Count, tree.Versions.Count, tree.Extra.Count, tree.Skipped);

    /// <summary>Одно расхождение словами: «нет пакета „X“ (в замке 1.2.3)» и родственные.</summary>
    public static string Problem(EngineTreeProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return problem.Kind switch
        {
            EngineTreeProblemKind.Missing => Loc.T("EngineTreeProblemMissingFormat", problem.Name, problem.Expected),
            EngineTreeProblemKind.Version => Loc.T("EngineTreeProblemVersionFormat", problem.Name, problem.Actual, problem.Expected),
            _ => Loc.T("EngineTreeProblemExtraFormat", problem.Name),
        };
    }

    /// <summary>
    /// РАСХОЖДЕНИЯ СВЕРКИ СЛОВАМИ: первые <see cref="EngineInstaller.NamedProblems"/> названы
    /// поимённо, остальные — числом. «Разошлось 400 пакетов» без единого имени не сказало бы, что
    /// чинить, а перечисление всех четырёхсот человек не прочитает.
    /// </summary>
    public static string TreeProblems(EngineTreeReport? tree)
    {
        if (tree is null) return string.Empty;

        var named = tree.Problems.Take(EngineInstaller.NamedProblems).Select(Problem).ToList();
        var rest = tree.Problems.Count - named.Count;

        if (rest > 0) named.Add(Loc.T("EngineTreeProblemsMoreFormat", rest));

        return string.Join("; ", named);
    }
}
