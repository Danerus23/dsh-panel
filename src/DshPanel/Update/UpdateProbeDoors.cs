using DshPanel.Shell;
using DshPanel.Tray;

namespace DshPanel.Update;

/// <summary>
/// СОСТОЯНИЕ ВЫПУСКОВ ДЛЯ ЖИВОЙ ПРОБЫ ОКНА (T4, 2.4.2) — «выпуск новее, вложения на месте».
///
/// **Зачем своя дверь, если есть настоящий контроллер.** Настоящий <see cref="UpdateController"/>
/// берёт вложения выпуска ТОЛЬКО из живого ответа GitHub, а в настройки их не кладёт намеренно
/// («адрес вложения живёт один выпуск»). В прогоне проверки сети нет вовсе, значит настоящий
/// контроллер отдал бы <see cref="ReleaseAssets.None"/>, раздел сказал бы «сначала проверьте
/// выпуски», кнопка подготовки осталась бы погашенной — и живой сцене «нажал, идёт подготовка»
/// было бы неоткуда начаться. Ровно по этой же причине образцовый ответ есть у съёмки
/// (<c>Program.ShotUpdateControl</c>), и он тоже вымышленный.
///
/// ⚠️ **Ни одного настоящего данного здесь нет и быть не может:** версия вымышленная (9.9.9),
/// адреса ведут в никуда (<c>example.invalid</c>), сети проба не касается. Всё, что она
/// доказывает, — это поведение ОКНА и СОСТОЯНИЯ ПОДГОТОВКИ, а не работа с GitHub.
/// </summary>
internal sealed class ProbeUpdateControl : IUpdateControl
{
    /// <summary>Версия вымышленного выпуска: заведомо новее панели, иначе готовить нечего.</summary>
    public const string LatestVersion = "9.9.9";

    /// <summary>Версия «установленной панели» на время пробы — тоже вымышленная.</summary>
    public const string CurrentVersion = "2.4.1";

    private static readonly UpdateRelease Answer = new(
        Ok: true,
        Error: string.Empty,
        Latest: LatestVersion,
        PageUrl: ProductLinks.ReleasesPageUrl,
        Published: "2026-10-24T00:00:00Z",
        Notes: string.Empty,
        NotesRaw: string.Empty,
        CheckedAt: DateTimeOffset.Now)
    {
        // ОБА ВЛОЖЕНИЯ НАЗВАНЫ: без архива и без файла сумм раздел честно сказал бы «сначала
        // проверьте выпуски» (<see cref="UpdateLines.PrepareState"/>), и кнопка была бы погашена.
        // Установщика нет — после DEF-060 панель его и не качает.
        Assets = new ReleaseAssets(
            ArchiveUrl: "https://example.invalid/DshPanel.zip",
            SetupUrl: string.Empty,
            SumsUrl: "https://example.invalid/SHA256SUMS.txt"),
    };

    public UpdateRelease Result => Answer;

    /// <summary>Проверки в полёте нет: живой пробе нужна не она, а сама подготовка.</summary>
    public bool Busy => false;

    /// <summary>Право ходить в сеть — да, но проба им не пользуется: <see cref="Check"/> только считает.</summary>
    public bool Allowed => true;

    public string Current => CurrentVersion;

    /// <summary>Выпуск новее установленной панели — по тому же правилу, что у настоящего контроллера.</summary>
    public bool Newer => UpdateDecisions.IsNewer(Answer.Latest, Current);

    /// <summary>Ничего не пропущено: пропуск — ответ человека, а у пробы человека нет.</summary>
    public bool Skipped => false;

    /// <summary>Строки-объяснения у пробы свои и пустые: их читает человек, а не проверка.</summary>
    public string SourceText => string.Empty;

    public string StatusText => string.Empty;

    public string AttemptText => string.Empty;

    public TrayStatusLine TrayLine => new(StatusText, TrayTone.Neutral);

    /// <summary>Сколько раз просили проверить выпуски. Проба этим числом стережёт свою тишину.</summary>
    public int Checks { get; private set; }

    /// <summary>
    /// Проверка выпусков — СЧЁТЧИК, а не запрос: сети у прогона проверки нет, и «проверить»
    /// здесь значило бы пойти в GitHub от имени владельца (красная линия 4).
    /// </summary>
    public void Check() => Checks++;

#pragma warning disable CS0067 // События объявлены потому, что они в двери; у пробы им неоткуда взяться.
    public event Action<string>? Announce;

    public event Action? Changed;
#pragma warning restore CS0067
}

/// <summary>
/// МЕДЛЕННЫЙ ПОДСТАВНОЙ ДВИЖОК УСТАНОВКИ ДЛЯ ЖИВОЙ ПРОБЫ ОКНА (T4, 2.4.2).
///
/// **От какого случая.** Владелец 24.10.2026 на 2.4.1: шарик → закрыл окно настроек → открыл, —
/// и новое окно построило пустое состояние: «ничего не идёт», кнопка подготовки снова доступна,
/// второй старт разрешён. Живая сцена требует, чтобы подготовка ШЛА, пока окно закрывают
/// и открывают снова, — а настоящая идёт минутами, ходит в сеть и пишет в каталоги владельца.
/// Значит нужен движок, который ничего не качает, но ведёт себя как настоящий: называет ход
/// и отвечает готовностью — и делает это ТОГДА, когда проба скажет.
///
/// **Две калитки, и обе — рука пробы.** <see cref="NextStep"/> разрешает второй шаг хода
/// (числа вырастают), <see cref="Finish"/> — ответ готовностью. Без калиток проба не смогла бы
/// поймать состояние «окно закрыли, работа идёт»: подготовка успела бы кончиться до закрытия,
/// и проверять было бы нечего. Часы и таймауты калиток — свои (5 с), чтобы зависшая проба
/// называлась провалом, а не висела до сторожа.
///
/// ⚠️ **Ни одного файла и ни одного каталога владельца.** Ответ готовностью несёт ВЫМЫШЛЕННЫЕ
/// пути: их показывает окно, и по ним ничего не читается и не пишется. Ни сети, ни диска здесь
/// нет вовсе — в этом и смысл подстановки (та же причина, что у <c>UpdateSelfTest.FakeDownload</c>).
/// </summary>
internal sealed class ProbeUpdateInstall : IUpdateInstall
{
    /// <summary>Сколько ждёт калитка, прежде чем продолжить без неё (проба тогда падает числами).</summary>
    public static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Размер «архива выпуска» — число настоящее (так уезжает DshPanel.zip) и ни к чему не обязывает.</summary>
    public const long ArchiveTotal = 44_730_712;

    /// <summary>Первый шаг хода: столько «принято» к моменту, когда окно закрывают.</summary>
    public const long FirstDone = 12_000_000;

    /// <summary>Второй шаг хода: столько «принято» к моменту, когда прежнее окно уже закрыто.</summary>
    public const long SecondDone = 22_000_000;

    /// <summary>Имя файла в строке хода — вымышленного выпуска из <see cref="ProbeUpdateControl"/>.</summary>
    public const string ArchiveName = "DshPanel-9.9.9.zip";

    /// <summary>Пауза между шагами: ход должен быть виден ДО того, как проба успеет его прочитать.</summary>
    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(120);

    private readonly ManualResetEventSlim _next = new(false);
    private readonly ManualResetEventSlim _finish = new(false);

    /// <summary>Право готовить замену — есть: раздел по нему решает, доступна ли кнопка.</summary>
    public bool Allowed => true;

    /// <summary>Сколько раз движок ВОШЁЛ в подготовку. Второй старт сюда не доходит (DEF-058).</summary>
    public int Prepared { get; private set; }

    /// <summary>Сколько раз звали замену файлов. Обязано остаться нулём: проба ничего не заменяет.</summary>
    public int Launched { get; private set; }

    /// <summary>Первый шаг хода назван — проба может закрывать окно.</summary>
    public bool FirstStepReported { get; private set; }

    /// <summary>Разрешить второй шаг хода (числа вырастут на глазах).</summary>
    public void NextStep() => _next.Set();

    /// <summary>Разрешить ответ готовностью — подготовка закончится удачно.</summary>
    public void Finish() => _finish.Set();

    public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null)
    {
        Prepared++;

        // ПЕРВЫЙ СНИМОК — НОЛЬ ПРИНЯТОГО, ровно как у живого загрузчика: тот, узнав размер,
        // сообщает «принято 0 из всего» (`HttpUpdateDownload.Save`, `progress?.Invoke(onDisk, total)`),
        // и только потом идут куски. Это не педантизм: средняя скорость считается ОТ ПЕРВОГО
        // СНИМКА этапа (<see cref="UpdateProgressMeter"/>), и снимок «сразу двенадцать мегабайт»
        // показал бы в строке хода выдуманные гигабайты в секунду — то есть проба мерила бы себя,
        // а не поведение окна.
        progress?.Invoke(new UpdateProgress(UpdateStage.Archive, ArchiveName, 0, ArchiveTotal));

        Thread.Sleep(StepDelay);
        progress?.Invoke(new UpdateProgress(UpdateStage.Archive, ArchiveName, FirstDone, ArchiveTotal));
        FirstStepReported = true;

        // КАЛИТКА ПЕРВОГО ШАГА: работа «замирает» на ходу, пока окно закрывают и открывают.
        _next.Wait(GateTimeout);

        Thread.Sleep(StepDelay);
        progress?.Invoke(new UpdateProgress(UpdateStage.Archive, ArchiveName, SecondDone, ArchiveTotal));

        // КАЛИТКА ГОТОВНОСТИ: ответ приходит тогда, когда проба уже проверила второй старт.
        _finish.Wait(GateTimeout);

        return new UpdatePreparation(
            Ok: true,
            Refusal: UpdateRefusal.None,
            Detail: string.Empty,
            Version: release,
            StagedFolder: @"C:\ProgramData\DshPanel-probe\staged\" + release,
            BackupFolder: @"C:\ProgramData\DshPanel-probe\backup",
            TargetFolder: @"C:\ProgramData\DshPanel-probe\panel",
            ScriptPath: @"C:\ProgramData\DshPanel-probe\update.cmd",
            LogPath: @"C:\ProgramData\DshPanel-probe\update.log",
            CommandLine: string.Empty);
    }

    /// <summary>
    /// Замена файлов — СЧЁТЧИК и отказ. Проба не нажимает последнюю кнопку: подменять панель
    /// в прогоне проверки запрещено, и «запустилось» здесь было бы ложью.
    /// </summary>
    public string Launch(UpdatePreparation preparation)
    {
        Launched++;

        return PanelStrings.UpdateLaunchNotStarted;
    }
}
