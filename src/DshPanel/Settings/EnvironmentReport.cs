using System.Globalization;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>
/// ЧЕМ ПОМЕЧЕНА СТРОКА ОТЧЁТА — состояние словами И цветом, а не цветом одним.
///
/// **От какого случая (владелец, 14.10.2026).** В разделе «Сервер» он увидел полотно технического
/// текста: «найден»/«не найден» стояли обычными словами в общем столбце и состоянием не читались.
/// Решение владельца по рендеру: **строка состояния — плотная метка со словом внутри** (образец
/// GOV.UK Tag: «Use the Tag component to show users the status of something»), а **серое состояние —
/// просто серым текстом, без плашки** (образец GOV.UK «complete multiple tasks»: «grey text with no
/// background colour»). Тона — из уже принятой палитры панели.
///
/// ⚠️ **Цвет НИКОГДА не единственный носитель смысла** (WCAG 2.2 SC 1.4.1): слово стоит внутри
/// каждой метки, и это проверяется отдельно. Метка **не интерактивна** (не ссылка и не кнопка).
/// </summary>
public enum EnvLineState
{
    /// <summary>Метки нет вовсе: значение — обычный текст (например, номер порта).</summary>
    None,

    /// <summary>Зелёная метка: «найден», «проверено».</summary>
    Ok,

    /// <summary>Жёлтая метка: «не прочитана». Неизвестно — не то же самое, что сломано.</summary>
    Warning,

    /// <summary>Красная метка: «не работает». Красный — ТОЛЬКО ошибка (GOV.UK).</summary>
    Error,

    /// <summary>Серое состояние — ПРОСТО СЕРЫЙ ТЕКСТ без плашки: «не найден», «не проверялась».</summary>
    Grey,
}

/// <summary>
/// СТРОКА ОТЧЁТА: ЧТО ИСКАЛИ, ЧЕМ ЭТО ОКАЗАЛОСЬ И ЧТО С ЭТИМ МОЖНО СДЕЛАТЬ.
///
/// **Зачем это отдельным разбором.** Слова владельца 27.09.2026, а потом повторно 28.09.2026
/// (он прислал кадр раздела «Сервер»): *«я просил как то сделать здесь нормальную группировку
/// с указанием пути; так же нужно указать какая версия сейчас DSH, версия node и так далее»*.
///
/// **Три части у каждой строки, и все три нужны:**
/// * <see cref="What"/> — ЧТО искали (человеческими словами: «Node»);
/// * <see cref="Word"/> и <see cref="State"/> — ЧЕМ оказалось, меткой состояния («найден» зелёной) —
///   или ничего, если значение состоянием не является (номер порта);
/// * <see cref="Detail"/> — КОРОТКАЯ ПОДРОБНОСТЬ рядом: «версия 22.11.0», путь под маской.
///
/// ⚠️ **Путь показывается МАСКОЙ** (<see cref="DisplayMask"/>, как в «Копиях»): `~\.dsh`, а не
/// `C:\Users\<имя>\.dsh`. Прежде раздел показывал путь целиком, и кадр раздела уносил имя
/// пользователя — этот долг закрыт 14.10.2026 (`_design-library\90-our-deviations.md`).
/// **Настоящий путь при этом не теряется:** он лежит в <see cref="Folder"/> и наружу не выводится —
/// им открывается папка по кнопке строки.
///
/// ⚠️ **Ничего не выдумывается.** Нет сервера — строка говорит «не найден»; версия не прочиталась —
/// так и сказано. Молчащая строка читается как поломка панели, а выдуманное число — как факт.
/// </summary>
public sealed record EnvironmentLine(string What, string Result = "")
{
    /// <summary>Тон метки состояния.</summary>
    public EnvLineState State { get; init; } = EnvLineState.None;

    /// <summary>Слово ВНУТРИ метки: «найден», «не работает». Пусто — метки нет.</summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>Короткая подробность рядом с меткой: одна фраза, без точки.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// Папка, которую открывает действие строки («Открыть папку»). Пусто — действия нет вовсе.
    /// Это НАСТОЯЩИЙ путь: он не показывается, а только открывается.
    /// </summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>Строка отчёта словами: «что: чем оказалось — подробность». Для журнала и самотеста.</summary>
    public string Text()
    {
        var value = Word.Length > 0
            ? (Detail.Length > 0 ? Word + " — " + Detail : Word)
            : (Detail.Length > 0 ? Detail : Result);

        return value.Length == 0 ? What : What + ": " + value;
    }
}

/// <summary>
/// КАРТОЧКА ОТЧЁТА: имя, одна строка пояснения под ним и строки-находки.
///
/// Форма взята у GOV.UK (summary list / task list): имя блока — заголовок, под ним **одна короткая
/// строка**, а не абзац, и в ней сказано, о чём этот список. Прежний подзаголовок раздела
/// («Что панель видит на этой машине») убран: владелец спросил *«зачем этот текст?»*, и ответа
/// у него не было — теперь пояснение стоит там, где оно что-то объясняет: под своим списком.
/// </summary>
public sealed record EnvironmentGroup(
    string Title,
    IReadOnlyList<EnvironmentLine> Lines,
    string Subtitle = "");

/// <summary>
/// ОТЧЁТ ОКРУЖЕНИЯ ЦЕЛИКОМ. Собирается ОДНИМ разбором (<see cref="EnvironmentReport.Build"/>) и
/// показывается ДВУМЯ способами: карточками в разделе «Сервер» (<c>Views\SettingsWindow</c>) и
/// текстом в отчёте самотеста (<see cref="Text"/>). Второго способа собрать тот же отчёт быть
/// не должно: он бы разошёлся с первым, и человек читал бы одно, а проверка мерила другое.
/// </summary>
public sealed record EnvironmentReport(IReadOnlyList<EnvironmentGroup> Groups)
{
    /// <summary>Отчёт текстом — для журнала и для отчёта самотеста.</summary>
    public string Text()
    {
        var lines = new List<string>();

        foreach (var group in Groups)
        {
            lines.Add(group.Title);

            foreach (var line in group.Lines) lines.Add("    " + line.Text());
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Собрать отчёт. Всё, что нужно, приходит ПАРАМЕТРАМИ: разбор не знает ни о контроллере, ни
    /// о настройках, поэтому его можно прогнать на подставных значениях — и «нет сервера DSH»,
    /// и «сервер есть, а Node не отвечает» проверяются без настоящей машины.
    /// </summary>
    /// <param name="engine">Найденные Node и сервер DSH; <c>null</c> — не нашли.</param>
    /// <param name="dshVersion">Версия сервера DSH (пусто — прочитать не удалось).</param>
    /// <param name="nodeVersion">Версия Node (пусто — спросить не удалось).</param>
    /// <param name="paths">Пути панели: свой корень, DSH_HOME, файл настроек.</param>
    /// <param name="workDir">Рабочая папка сервера, которая действует сейчас.</param>
    /// <param name="workDirConfigured">Она задана настройкой или взята по умолчанию.</param>
    /// <param name="serverPort">Порт, который панель займёт на самом деле.</param>
    /// <param name="portState">Состояние ЭТОГО порта словами: «свободен», «занят своим сервером».</param>
    public static EnvironmentReport Build(
        DshEngine? engine,
        string dshVersion,
        string nodeVersion,
        string npmVersion,
        string pnpmVersion,
        AppPaths paths,
        string workDir,
        bool workDirConfigured,
        int serverPort,
        string portState,
        IReadOnlyList<SkillVerdict>? skills = null,
        EngineHints? hints = null,
        SkillFacts? skillFacts = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var culture = CultureInfo.CurrentCulture;

        // --- УМЕНИЯ ПАНЕЛИ С ЭТИМ СЕРВЕРОМ DSH -----------------------------------------------
        //
        // Строка на умение: имя · метка состояния со словом · подробность. НИКАКОЙ ШАПКИ КОЛОНОК:
        // имя блока уже сказано заголовком карточки, а шапка «Что · Значение» над девятью строками
        // объясняла бы то, что и так видно (GOV.UK summary list шапки не имеет).
        //
        // ⚠️ «Не проверялась» — это НЕ беда и не пустое место: панель не спрашивала сервер DSH,
        // и так и говорит. Спросить — дело того, кому умение нужно.
        var skillsGroup = new List<EnvironmentLine>();

        foreach (var skill in skills ?? Array.Empty<SkillVerdict>())
        {
            skillsGroup.Add(new EnvironmentLine(Loc.T(SkillKeys.Name(skill.Skill)))
            {
                State = Tone(skill),
                Word = SkillPhrases.Word(skill),
                Detail = SkillPhrases.Detail(skill),
                Result = SkillPhrases.Value(skill),
            });
        }

        if (skills is { Count: > 0 })
        {
            // ЖИВАЯ ПРОБА — отдельная строка, и она отвечает на вопрос «а это про СЕЙЧАС?»
            // (находка владельца 14.10.2026, ночь): без неё вердикт файла подсказок о версии
            // вообще читался как ответ про его сервер. Момент пробы — подробность этой строки.
            skillsGroup.Add(new EnvironmentLine(Loc.T(SkillKeys.ProbeLabel))
            {
                State = skillFacts?.MeasuredAt is null ? EnvLineState.Grey : EnvLineState.Ok,
                Word = skillFacts?.MeasuredAt is null
                    ? Loc.T(SkillKeys.NotChecked)
                    : Loc.T(SkillKeys.Checked),
                Detail = SkillPhrases.Moment(skillFacts).Trim().TrimEnd(';'),
            });

            // ВЕРСИИ: какая у сервера DSH и на каких панель проверена. Список приходит ИЗ ФАЙЛА
            // ПОДСКАЗОК; файла нет — «ни на одной версии», и это правда, а не пустое место.
            // Строка без метки: это не состояние, а справка — и она стоит всегда, когда есть
            // таблица умений: пропажа строки читалась бы как «панель больше не знает версий».
            var verified = hints?.VerifiedList() ?? string.Empty;

            skillsGroup.Add(new EnvironmentLine(Loc.T(SkillKeys.VersionsLabel))
            {
                Detail = SkillPhrases.VersionsValue(dshVersion, verified),
                Result = SkillPhrases.VersionsValue(dshVersion, verified),
            });

            // ПОДСКАЗКИ ПРО НАШУ ВЕРСИЮ: выжимка и известные беды, на языке панели. Заметок нет —
            // строк нет: пустых строк в отчёте быть не должно.
            var hint = hints?.Find(dshVersion);

            var notes = EngineHints.NotesFor(hint, Loc.Language);
            if (notes.Count > 0)
                skillsGroup.Add(new EnvironmentLine(PanelStrings.EnvSkillNotes, string.Join("; ", notes)));

            var troubles = EngineHints.TroublesFor(hint, Loc.Language);
            if (troubles.Count > 0)
                skillsGroup.Add(new EnvironmentLine(PanelStrings.EnvSkillTroubles, string.Join("; ", troubles)));
        }

        // --- ЧТО НАЙДЕНО НА ЭТОЙ МАШИНЕ ------------------------------------------------------
        //
        // Один список вместо трёх групп CODE-порядка («Папки», «Чем поднимается сервер», «DSH»):
        // группировка осталась, но её теперь несёт САМА КАРТОЧКА, а не подзаголовки внутри неё —
        // восемь строк человек читает одним взглядом, и три заголовка над ними только дробили его.
        var found = new List<EnvironmentLine>();

        // САМ СЕРВЕР DSH — первой строкой: с него начинается ответ «что тут есть».
        // Версия не прочиталась — это НЕ «не работает»: жёлтая метка «не прочитана».
        found.Add(new EnvironmentLine(PanelStrings.EnvEngine)
        {
            State = engine is null
                ? EnvLineState.Grey
                : (dshVersion.Length > 0 ? EnvLineState.Ok : EnvLineState.Warning),
            Word = engine is null
                ? PanelStrings.EnvNotFound
                : (dshVersion.Length > 0 ? PanelStrings.EnvFound : PanelStrings.EnvValueUnknown),
            Detail = dshVersion.Length > 0
                ? string.Format(culture, PanelStrings.EnvVersionFormat, dshVersion)
                : string.Empty,
            Result = engine is null ? PanelStrings.EnvNotFound : dshVersion,
        });

        found.Add(Versioned(PanelStrings.EnvNode, nodeVersion, found: engine is not null, culture));
        found.Add(Versioned(PanelStrings.EnvNpm, npmVersion, found: NpmFound(engine, "npm"), culture));

        found.Add(new EnvironmentLine(PanelStrings.EnvPnpm)
        {
            State = PnpmFound(engine, pnpmVersion) ? EnvLineState.Ok : EnvLineState.Grey,
            Word = PnpmFound(engine, pnpmVersion) ? PanelStrings.EnvFound : PanelStrings.EnvNotFound,
            Detail = pnpmVersion.Length > 0 ? pnpmVersion : PanelStrings.EnvPnpmUnused,
            Result = pnpmVersion.Length > 0 ? pnpmVersion : PanelStrings.EnvNotFound,
        });

        // ПАПКИ: путь — МАСКОЙ, а действие строки открывает саму папку общей дверью панели
        // (`Shell\AgentBrowser.TryOpenFolder` — та же, которой пользуются «Копии»).
        found.Add(new EnvironmentLine(PanelStrings.EnvDshHome)
        {
            State = EnvLineState.Ok,
            Word = PanelStrings.EnvFoundP,
            Detail = DisplayMask.Path(paths.DshHome),
            Result = DisplayMask.Path(paths.DshHome),
            Folder = paths.DshHome,
        });

        found.Add(new EnvironmentLine(PanelStrings.EnvWorkDir)
        {
            State = EnvLineState.Ok,
            Word = PanelStrings.EnvFoundF,
            Detail = string.Format(
                culture,
                PanelStrings.EnvValueDetailFormat,
                DisplayMask.Path(workDir),
                workDirConfigured ? PanelStrings.EnvFromSetting : PanelStrings.EnvFromPanel),
            Result = DisplayMask.Path(workDir),
            Folder = workDir,
        });

        found.Add(new EnvironmentLine(PanelStrings.EnvSettingsFile)
        {
            State = EnvLineState.Ok,
            Word = PanelStrings.EnvFound,
            Detail = DisplayMask.Path(Path.GetDirectoryName(paths.SettingsFile) ?? string.Empty),
            Result = DisplayMask.Path(paths.SettingsFile),
            Folder = Path.GetDirectoryName(paths.SettingsFile) ?? string.Empty,
        });

        // ПОРТ — СТРОКА БЕЗ МЕТКИ: номер порта состоянием не является, и красить его нечем.
        // Состояние порта стоит рядом с номером, а не отдельной строкой: это одна мысль.
        found.Add(new EnvironmentLine(PanelStrings.EnvServerPort)
        {
            Detail = string.Format(culture, PanelStrings.EnvValueDetailFormat, serverPort, portState),
            Result = serverPort.ToString(CultureInfo.InvariantCulture) + " — " + portState,
        });

        var groups = new List<EnvironmentGroup>();

        if (skillsGroup.Count > 0)
            groups.Add(new EnvironmentGroup(
                Loc.T(SkillKeys.Title), skillsGroup, PanelStrings.EnvSkillsSubtitle));

        groups.Add(new EnvironmentGroup(
            PanelStrings.EnvFoundTitle, found, PanelStrings.EnvFoundSubtitle));

        return new EnvironmentReport(groups);
    }

    /// <summary>
    /// Версия пакета по его каталогу — из его же <c>package.json</c>. Не прочиталась — пустая
    /// строка, и вызывающий скажет об этом словами: выдумывать номер нельзя.
    /// </summary>
    public static string VersionOf(string? packageDirectory) =>
        Platform.VersionProbe.FromPackage(packageDirectory);

    /// <summary>
    /// ТОН МЕТКИ УМЕНИЯ. Красный — только беда; «версия не прочиталась» — жёлтая («неизвестно»);
    /// «не спрашивали» — серое состояние без плашки.
    /// </summary>
    private static EnvLineState Tone(SkillVerdict verdict) => verdict.State switch
    {
        SkillState.Ok => EnvLineState.Ok,
        SkillState.Missing => SkillPhrases.Unread(verdict) ? EnvLineState.Warning : EnvLineState.Error,
        _ => EnvLineState.Grey,
    };

    /// <summary>
    /// СТРОКА НАХОДКИ С ВЕРСИЕЙ: «найден» зелёной меткой с версией в подробности; «не найден» —
    /// серым текстом; «версия не прочитана» — жёлтой меткой. Три РАЗНЫХ ответа, и все три сказаны.
    /// </summary>
    private static EnvironmentLine Versioned(string what, string version, bool found, CultureInfo culture)
    {
        if (!found) return new EnvironmentLine(what) { State = EnvLineState.Grey, Word = PanelStrings.EnvNotFound };

        if (version.Length == 0)
            return new EnvironmentLine(what) { State = EnvLineState.Warning, Word = PanelStrings.EnvValueUnknown };

        return new EnvironmentLine(what)
        {
            State = EnvLineState.Ok,
            Word = PanelStrings.EnvFound,
            Detail = string.Format(culture, PanelStrings.EnvVersionFormat, version),
            Result = version,
        };
    }

    /// <summary>Лежит ли пакет рядом с Node (теми же путями, что у копий) — второй раз не выдумываем.</summary>
    private static bool NpmFound(DshEngine? engine, string package)
    {
        var root = engine is null ? null : Backup.BackupPlanner.NpmDirectory(engine);

        return root is not null && Directory.Exists(Path.Combine(root, "node_modules", package));
    }

    /// <summary>pnpm панели не нужен, но его наличие — факт машины: показываем как есть.</summary>
    private static bool PnpmFound(DshEngine? engine, string version) =>
        version.Length > 0 || NpmFound(engine, "pnpm");
}
