using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Headless;

/// <summary>
/// ИСПОЛНЕНИЕ двух режимов без окна: `--backup &lt;папка&gt;` и `--restore &lt;архив&gt;`.
///
/// Здесь только ввод-вывод. Решения (что можно, что нельзя и что об этом говорить) живут в
/// <see cref="HeadlessDecisions"/>, разведка сервера — в <see cref="HeadlessServer"/>. Слой тонкий
/// намеренно: точки входа тесты не видят (`Program.Main`), поэтому всё, что можно проверить,
/// обязано жить вне её.
///
/// **Ни окна, ни значка, ни уведомлений.** Режим возвращается из `Program.Main` до обычного запуска
/// и печатает строки отчёта; гашения сервера здесь нет вовсе — только чтение (красная линия 6).
///
/// Работа идёт через <see cref="BackupController"/> — ту же дверь, что у окна копий. Это не
/// «переиспользование кода», а условие честности отчёта: состав копии, согласия и право прогона
/// у экрана и у ключа обязаны быть ОДНИ, иначе скрипт и человек снимали бы разные копии.
/// </summary>
public static class HeadlessRun
{
    /// <summary>
    /// Точка входа обоих режимов. Возвращает код: <c>0</c> успех, <c>1</c> провал, <c>2</c> отказ.
    ///
    /// <paramref name="probe"/> и <paramref name="clock"/> — швы для проверок без диска и без сети;
    /// у настоящего прогона они пусты, и берутся системные источники.
    /// </summary>
    public static int Run(string[] rest, ServerProbe? probe = null, Func<DateTimeOffset>? clock = null)
    {
        var request = HeadlessDecisions.Parse(rest);

        if (request is null)
        {
            Console.WriteLine("КОПИИ ПРОВАЛ: это не режим копии и не режим наката — разбирать нечего");
            return 2;
        }

        if (!request.Ok)
        {
            Console.WriteLine(request.Refusal);
            return 2;
        }

        var mode = request.Mode;
        var context = RunContext.Current;

        // Право прогона — ДО всего остального, и до создания папок тоже: без корня этот режим
        // не имеет права сделать ни одной записи (красная линия 5).
        if (!HeadlessDecisions.HasRight(context, humanLaunch: false))
        {
            Console.WriteLine(HeadlessDecisions.IsolationRefusal(mode));
            return 2;
        }

        var source = probe ?? ServerProbe.System();
        var code = 1;

        try
        {
            code = mode == HeadlessMode.Backup
                ? Copy(request, context, clock ?? (() => DateTimeOffset.Now), source)
                : Restore(request, context, source);
        }
        catch (Exception error)
        {
            // Необработанное исключение в отчёте — это «провал без причины», а причина человеку
            // нужна: строку печатаем и выходим кодом 1.
            Say(mode, $"исключение режима: {error.GetType().Name}: {error.Message}");
        }

        Console.WriteLine(HeadlessDecisions.Verdict(mode, code));
        return code;
    }

    // --- копия ----------------------------------------------------------------

    private static int Copy(HeadlessRequest request, RunContext context, Func<DateTimeOffset> clock, ServerProbe probe)
    {
        const HeadlessMode mode = HeadlessMode.Backup;
        var paths = context.Paths;

        var load = new SettingsStore(paths.SettingsFile).Load();
        var settings = load.Settings;
        var engine = DshEngine.Locate();

        // ПРАВО ВЗЯТЬ МАШИННУЮ УСТАНОВКУ ДВИЖКА (A4, находка C1 CH-08). Прогон без окна человеком
        // не запускается, поэтому ответ ожидаемо «нельзя» — но СПРАШИВАЕТСЯ, а не решается
        // литералом: одно место правды на всю панель (`RunRights`), и завтра это условие
        // поменяется там же, где все остальные.
        var mayTakeMachineEngine = RunRights.MachineEngineData(context, humanLaunch: false);

        Say(mode, "режим: копия без окна — полная, имя по правилу v1; движок и Node берутся " +
                  "только из своего корня прогона, машинная установка — только с правом");
        Say(mode, $"изоляция: корень прогона {DisplayMask.Path(paths.Root)}");
        Say(mode, $"настройки: {DisplayMask.Path(paths.SettingsFile)} — " +
                  (load.Ok ? "прочитаны" : "НЕ разобраны, взяты умолчания"));
        if (load.Problem.Length > 0) Say(mode, "настройки: " + load.Problem);

        foreach (var line in HeadlessDecisions.CompositionLines(
                     settings.BackupWithKeys, request.Shareable, settings.BackupKeyDirs.Count))
        {
            Say(mode, line);
        }

        // СТРОКА О ДВИЖКЕ — ТРЁХЗНАЧНАЯ, и это правка правды, а не стиля: прежде она обещала
        // «полную (с движком и Node)» там, где в изоляции движка в копии не будет вовсе.
        Say(mode, EngineLine(paths, engine, mayTakeMachineEngine));

        // Сервер — ТОЛЬКО чтением. Гасить его этот режим не умеет и не станет: гасит человек.
        var server = HeadlessServer.Read(probe, settings.ServerPort);
        Say(mode, $"порт этого прогона: {settings.ServerPort} — на нём панель поднимает СВОЙ сервер " +
                  "(проверяем только его: чужие серверы машины к данным прогона отношения не имеют)");
        Say(mode, HeadlessDecisions.ServerLine(server.Answer, server.Port, mode));

        var created = !Directory.Exists(request.Path);

        if (File.Exists(request.Path))
            return Refuse(mode, $"по пути «{DisplayMask.Path(request.Path)}» лежит ФАЙЛ, а нужна папка для архива");

        Directory.CreateDirectory(request.Path);
        Say(mode, $"папка копий: {DisplayMask.Path(request.Path)} — {(created ? "создана" : "уже была")}");

        var archive = HeadlessDecisions.ArchivePath(request.Path, clock());
        Say(mode, $"файл: {Path.GetFileName(archive)}");

        var journal = new List<string>();
        var controller = Controller(context, paths, () => settings, () => engine, clock, journal);

        var outcome = controller.CreateCopy(
            archive,
            HeadlessDecisions.ServerMayRun(server.Answer),
            request.Shareable,
            what => Say(mode, "… " + what));

        Print(journal);
        Say(mode, outcome.Summary());

        return outcome.Ok ? 0 : 1;
    }

    // --- накат ----------------------------------------------------------------

    private static int Restore(HeadlessRequest request, RunContext context, ServerProbe probe)
    {
        const HeadlessMode mode = HeadlessMode.Restore;
        var paths = context.Paths;

        // Архив обязан существовать. Это не «провал наката», а неверный вызов: накатывать нечего,
        // и код у такого ответа — 2 (неприменимо), как у «посмотреть не удалось».
        if (!File.Exists(request.Path))
            return Refuse(mode, $"архива «{DisplayMask.Path(request.Path)}» нет — накатывать нечего");

        var load = new SettingsStore(paths.SettingsFile).Load();
        var settings = load.Settings;
        var engine = DshEngine.Locate();

        Say(mode, "режим: накат без окна — сначала план, потом раскладка");
        Say(mode, $"изоляция: корень прогона {DisplayMask.Path(paths.Root)}");
        Say(mode, $"настройки: {DisplayMask.Path(paths.SettingsFile)} — " +
                  (load.Ok ? "прочитаны" : "НЕ разобраны, взяты умолчания"));
        if (load.Problem.Length > 0) Say(mode, "настройки: " + load.Problem);

        Say(mode, $"архив: {DisplayMask.Path(request.Path)} — {BackupFormat.Size(new FileInfo(request.Path).Length)}");

        var server = HeadlessServer.Read(probe, settings.ServerPort);
        Say(mode, $"порт этого прогона: {settings.ServerPort} — на нём панель поднимает СВОЙ сервер " +
                  "(проверяем только его: чужие серверы машины к данным прогона отношения не имеют)");
        Say(mode, HeadlessDecisions.ServerLine(server.Answer, server.Port, mode));

        // Отказ — ДО чтения архива и до первой записи: причина здесь полная, а «сначала план»
        // относится к тому случаю, когда накат действительно будет делаться.
        if (HeadlessDecisions.BlocksRestore(server.Answer))
        {
            return Refuse(mode,
                $"на порту {server.Port} отвечает работающий сервер DSH, а накат поверх живого движка " +
                "не делается: файлы сессий он держит и дописывает. Ключ не умеет спрашивать — " +
                "остановите сервер и повторите (сам режим его НЕ гасит, красная линия 6).");
        }

        var journal = new List<string>();
        var controller = Controller(context, paths, () => settings, () => engine, () => DateTimeOffset.Now, journal);

        // ПЛАН — до первой записи: он только читает архив и отвечает на вопрос «что ляжет и что нет».
        var plan = controller.PlanRestore(request.Path, request.WithEngine, request.WithPanel, request.WithKeys);

        foreach (var line in HeadlessDecisions.PlanLines(plan)) Say(mode, line);

        // Находка В5: копию, снятую одной версией движка, другая может не открыть. Правило показа
        // одно на всю панель — то же, что у окна копий (<see cref="RestoreEngine.VersionNote"/>).
        Say(mode, "версия движка: " + RestoreEngine.VersionNote(
            CopyEngineVersion(plan.Manifest), BackupEngine.InstalledVersion(engine)));

        foreach (var line in HeadlessDecisions.ConsentLines(request.WithEngine, request.WithPanel, request.WithKeys))
        {
            Say(mode, line);
        }

        if (request.WithEngine)
        {
            Say(mode, "движок и Node: цели считаются от ЭТОЙ машины и в изолированном прогоне обязаны " +
                      "лежать под его корнем — цель вне корня не раскладывается (сказано в группах выше)");
        }

        if (!plan.Ok)
            return Refuse(mode, "накат не начат: " + plan.Error);

        var result = controller.RunRestore(
            plan,
            request.WithEngine,
            request.WithPanel,
            request.WithKeys,
            HeadlessDecisions.ServerMayRun(server.Answer),
            what => Say(mode, "… " + what));

        Print(journal);
        Say(mode, result.Summary());

        return result.Ok ? 0 : 1;
    }

    // --- общее ----------------------------------------------------------------

    /// <summary>
    /// Контроллер копий — та же дверь, что у окна. Право прогона берётся общей функцией (здесь оно
    /// уже проверено, и это второй предохранитель, а не первый), а «держаться корня прогона»
    /// включается по признаку изоляции: в изолированном прогоне движок и Node по чужому месту
    /// не раскладываются вовсе.
    /// </summary>
    private static BackupController Controller(
        RunContext context,
        AppPaths paths,
        Func<PanelSettings> settings,
        Func<DshEngine?> engine,
        Func<DateTimeOffset> clock,
        List<string> journal) =>
        new(
            paths,
            settings,
            allowed: BackupController.For(context, humanLaunch: false),

            // Право — то же, что спрошено выше: одно решение, а не второе рядом.
            mayTakeMachineEngineData: RunRights.MachineEngineData(context, humanLaunch: false),

            locateEngine: engine,
            clock: clock,
            log: journal.Add,
            confineToRunRoot: context.IsIsolated);

    /// <summary>
    /// ЧТО ЖДЁТ ЧЕЛОВЕКА ОТ ДВИЖКА В ЭТОЙ КОПИИ — ТРИ СОСТОЯНИЯ, ТРИ РАЗНЫХ ТЕКСТА (A4, §4.5).
    ///
    /// 1. движок найден и он СВОЙ (под корнем прогона) либо право есть — копия с движком;
    /// 2. движок найден, но он МАШИННЫЙ и права нет — копия без него, и причина называется
    ///    ТЕМ ЖЕ ключом, что у плана (<see cref="PanelStrings.BackupEngineOutsideRunRoot"/>):
    ///    одни слова на всю панель, чтобы отчёт и замечание не спорили;
    /// 3. движка нет вовсе — тоже ГОТОВЫМ ключом (<see cref="PanelStrings.BackupEngineNotFound"/>).
    ///
    /// ⚠️ Движок при этом НЕ обнуляется: он остаётся прочитанным (версия в опись), а в копию
    /// не попадает — это решает план по пути. Обнулить его значило бы соврать второй раз:
    /// «движок и Node не найдены» там, где они найдены и не положены.
    ///
    /// ⚠️ **НИ ОДНОГО РУССКОГО ЛИТЕРАЛА В ЭТОЙ СТРОКЕ** (находка вычитки `a-3-klyucha.md` §3,
    /// мутация CD8). Прежде состояния 2 и 3 были написаны здесь по-русски: строка 2 ещё и
    /// СКЛЕИВАЛА словарный ключ с русской скобкой «(движок найден: …)», и на панели с `en`/`zh`
    /// человек читал два языка в одном предложении. Теперь оба состояния — ЦЕЛИКОМ словарные;
    /// проверка `HeadlessModesTests.Строка_отчёта_о_движке_целиком_из_словаря` роняет возврат литерала.
    ///
    /// Строка состояния 1 остаётся отчётным голосом: рядом с ней нет нашей фразы из словаря,
    /// и внутренние имена в отчёте разрешены (отступление №4).
    /// </summary>
    internal static string EngineLine(AppPaths paths, DshEngine? engine, bool mayTakeMachineEngine)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (engine is null) return PanelStrings.BackupEngineNotFound;

        var inside = BackupPlanner.IsInside(engine.BinPath, paths.Root);

        return inside || mayTakeMachineEngine
            ? $"движок найден: {DisplayMask.Path(engine.BinPath)} (версия {BackupEngine.InstalledVersion(engine)})"
            : PanelStrings.BackupEngineOutsideRunRoot;
    }

    /// <summary>Версия движка, которой снята копия (поле описи <c>versions.dsh</c>). Пусто — не записана.</summary>
    private static string CopyEngineVersion(BackupManifest? manifest) =>
        manifest?.Versions is { } versions && versions.TryGetValue("dsh", out var version)
            ? version
            : string.Empty;

    private static void Say(HeadlessMode mode, string text) =>
        Console.WriteLine(HeadlessDecisions.Line(mode, text));

    /// <summary>Строки журнала панели: их немного, и они объясняют отказы — терять их нельзя.</summary>
    private static void Print(List<string> journal)
    {
        foreach (var line in journal) Console.WriteLine("ЖУРНАЛ| " + line);
    }

    private static int Refuse(HeadlessMode mode, string reason)
    {
        Console.WriteLine(HeadlessDecisions.Name(mode) + " ОТКАЗ: " + reason);
        return 2;
    }
}
