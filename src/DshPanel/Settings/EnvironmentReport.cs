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
        SkillFacts? skillFacts = null,
        ToolPlaces? places = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var culture = CultureInfo.CurrentCulture;

        // ГДЕ ИСКАТЬ npm И pnpm (задача F-3, слово владельца 20.10.2026; шимы и кэш corepack —
        // аудит 22.10.2026). Один список мест на весь отчёт: и «найден», и версия отвечают по нему,
        // и второго списка быть не должно — иначе строка однажды скажет «найден», а версия не
        // прочитается из другого места.
        var tools = places ?? ToolPlaces.For(engine);

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

        // ⚠️ npm И pnpm ИЩУТСЯ ПО ВСЕМ МЕСТАМ, А НЕ ПО ОДНОМУ (задача F-3, слово владельца
        // 20.10.2026). Прежде «найден» значило «лежит пакетом в node_modules движка панели»,
        // и отчёт показывал «не найден» при живом npm рядом с Node и живом pnpm в %APPDATA%\npm.
        found.Add(Versioned(PanelStrings.EnvNpm, npmVersion, found: ToolFound(tools, "npm"), culture));

        var pnpmFound = ToolFound(tools, "pnpm");

        // ⚠️ pnpm ПАНЕЛИ НЕ НУЖЕН, и это НЕ беда машины: нет его — строка СЕРАЯ с подписью
        // «панель им не пользуется» (чинить нечего). Нашёлся, а версия не прочиталась — жёлтая
        // «версия не прочитана»: ложного зелёного не бывает, зелёный — только «найден» С версией.
        var pnpmLine = Versioned(PanelStrings.EnvPnpm, pnpmVersion, pnpmFound, culture);

        found.Add(pnpmLine with
        {
            Detail = pnpmVersion.Length > 0 ? pnpmVersion : PanelStrings.EnvPnpmUnused,
            Result = pnpmVersion.Length > 0 ? pnpmVersion : (pnpmFound ? string.Empty : PanelStrings.EnvNotFound),
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

    /// <summary>
    /// ГДЕ ИСКАТЬ npm И pnpm (задача F-3, слово владельца 20.10.2026; дополнено по независимому
    /// аудиту 22.10.2026 — шимы и кэш corepack).
    ///
    /// **От какого случая.** Отчёт показывал npm и pnpm «не найден», хотя `npm.cmd` лежал рядом
    /// с Node (`C:\Program Files\nodejs\npm.cmd`) — панель этим npm ставит движок, — а `pnpm.cmd`
    /// в `%APPDATA%\npm`. Прежний поиск смотрел ОДНО место: `node_modules\npm` в каталоге пакетов,
    /// выведенном из пути движка панели, а у движка, поставленного панелью, npm там не лежит вовсе.
    ///
    /// **Места, и все обязательны:** рядом с Node (там `node_modules\npm` и `npm.cmd`),
    /// `%APPDATA%\npm` (там живут команды-обёртки, в том числе pnpm), PATH и **corepack** — его шимы
    /// (`shims\pnpm.cmd`) и кэш пакетов (`v1\pnpm\&lt;версия&gt;`). «Не найден» — только когда нет
    /// НИ В ОДНОМ месте: ложное «не найден» и ложное «версия не прочитана» одинаково врут.
    ///
    /// ⚠️ **ПОЧЕМУ COREPACK.** Node 25+ не включает corepack в поставку; но если pnpm поставлен
    /// через corepack, то на машине нет ни `node_modules\pnpm` рядом с Node, ни пакета
    /// в `%APPDATA%\npm` — есть только шим в PATH (или в самом доме corepack) и пакет в кэше.
    /// Прежние три места такой установки не видели вовсе.
    ///
    /// ⚠️ Строки для проверок ПОДСТАВЛЯЮТСЯ (подставные корни): проверка не имеет права зависеть
    /// от PATH, `%APPDATA%`, `%LOCALAPPDATA%` и `COREPACK_HOME` машины, на которой идёт, — иначе она
    /// проверяла бы машину, а не правило.
    /// </summary>
    public sealed record ToolPlaces(
        string NextToNode,
        string AppDataNpm,
        IReadOnlyList<string> PathDirectories,
        IReadOnlyList<string> CorepackHomes)
    {
        /// <summary>
        /// Места для ЭТОЙ машины: каталог Node — у найденного движка, `%APPDATA%` и PATH — у машины
        /// (или подставленные доводами). Пустая строка — «места нет»: она не место поиска.
        ///
        /// <para><paramref name="localAppDataDirectory"/> и <paramref name="corepackHome"/> —
        /// подстановка для проверок. Дом corepack берётся из трёх источников, и все три —
        /// настоящие: `COREPACK_HOME` (его corepack признаёт сам), умолчание Windows
        /// `%LOCALAPPDATA%\node\corepack` и `%APPDATA%\node\corepack` (его назвал аудит; цена
        /// проверки — одно обращение к диску). Совпавшие пути не дублируются.</para>
        /// </summary>
        public static ToolPlaces For(
            DshEngine? engine,
            string? appDataDirectory = null,
            string? pathVariable = null,
            string? localAppDataDirectory = null,
            string? corepackHome = null)
        {
            var node = string.Empty;

            try
            {
                node = engine is null ? string.Empty : Path.GetDirectoryName(engine.NodePath) ?? string.Empty;
            }
            catch
            {
                // Путь Node бывает каким угодно (переменные, кавычки) — «не знаю каталога» честнее
                // падения отчёта из-за одной строки.
                node = string.Empty;
            }

            var appData = Clean(appDataDirectory ?? Environment.GetEnvironmentVariable("APPDATA"));
            var localAppData = Clean(localAppDataDirectory ?? Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            var corepack = Clean(corepackHome ?? Environment.GetEnvironmentVariable("COREPACK_HOME"));
            var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            var directories = new List<string>();

            foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim().Trim('"');
                if (trimmed.Length == 0) continue;
                if (!directories.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) directories.Add(trimmed);
            }

            var corepackHomes = new List<string>();

            AddCorepackHome(corepackHomes, corepack);

            if (localAppData.Length > 0)
                AddCorepackHome(corepackHomes, Path.Combine(localAppData, "node", "corepack"));

            if (appData.Length > 0)
                AddCorepackHome(corepackHomes, Path.Combine(appData, "node", "corepack"));

            return new ToolPlaces(
                node,
                appData.Length == 0 ? string.Empty : Path.Combine(appData, "npm"),
                directories,
                corepackHomes);
        }

        /// <summary>Обрезать путь из переменной окружения: пустая строка — «места нет».</summary>
        private static string Clean(string? value) => (value ?? string.Empty).Trim().Trim('"');

        /// <summary>Добавить дом corepack, не повторяя уже названный (путь сравнивается без регистра).</summary>
        private static void AddCorepackHome(List<string> homes, string home)
        {
            if (home.Length == 0) return;
            if (!homes.Contains(home, StringComparer.OrdinalIgnoreCase)) homes.Add(home);
        }
    }

    /// <summary>
    /// ГДЕ МОЖЕТ ЛЕЖАТЬ ПАКЕТ: `node_modules\<package>` рядом с Node, в `%APPDATA%\npm` и в каждом
    /// каталоге PATH (портативный Node кладёт пакет рядом с командой). Пустые места не выдумываются:
    /// пустая строка — не место поиска.
    /// </summary>
    private static IEnumerable<string> PackageDirectories(ToolPlaces places, string package)
    {
        foreach (var root in new[] { places.NextToNode, places.AppDataNpm }.Concat(places.PathDirectories))
        {
            if (root.Length == 0) continue;

            yield return Path.Combine(root, "node_modules", package);
        }
    }

    /// <summary>
    /// ГДЕ ЛЕЖИТ ПАКЕТ, ПОСТАВЛЕННЫЙ COREPACK: `<дом corepack>\v1\<package>\<версия>`.
    ///
    /// **Почему каталог надо ПЕРЕЧИСЛЯТЬ, а не сложить путь.** Имя папки внутри `v1\<package>` —
    /// это версия, а не имя пакета; другого места, где corepack пишет свой кэш, нет. Поэтому
    /// каталог считается установкой пакета только тогда, когда **сам `package.json` называет** его
    /// (`name` = пакет): чужой каталог в `v1\pnpm` не делает pnpm установленным и версии не даёт.
    ///
    /// ⚠️ **Ошибка чтения каталога — не находка:** нет кэша, нет прав, каталог занят — просто
    /// «здесь пакета не видно», и остальные места продолжают проверяться.
    /// </summary>
    private static IEnumerable<string> CorepackPackageDirectories(ToolPlaces places, string package)
    {
        foreach (var home in places.CorepackHomes)
        {
            if (home.Length == 0) continue;

            var manager = Path.Combine(home, "v1", package);

            // Кэша этого пакета в доме нет — обычное дело (дом corepack есть, а pnpm не ставили):
            // без этой проверки каждое открытие раздела стоило бы исключения на пустом месте.
            if (!Directory.Exists(manager)) continue;

            string[] versions;

            try
            {
                versions = Directory.GetDirectories(manager);
            }
            catch
            {
                continue;
            }

            foreach (var directory in versions)
            {
                if (string.Equals(
                        Platform.VersionProbe.NameFromPackage(directory),
                        package,
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                }
            }
        }
    }

    /// <summary>
    /// ГДЕ МОЖЕТ ЛЕЖАТЬ КОМАНДА: `<package>.cmd` / `<package>.exe` / `<package>` — рядом с Node,
    /// в `%APPDATA%\npm`, в каждом каталоге PATH и в **шимах corepack** (`<дом corepack>\shims`).
    /// pnpm панель ставит не пакетом, а командой-обёрткой в `%APPDATA%\npm`, а corepack — шимом;
    /// без этих списков он «не находился».
    /// </summary>
    private static IEnumerable<string> CommandFiles(ToolPlaces places, string package)
    {
        var names = new[] { package + ".cmd", package + ".exe", package };

        var directories = new List<string> { places.NextToNode, places.AppDataNpm };
        directories.AddRange(places.PathDirectories);

        foreach (var home in places.CorepackHomes)
        {
            if (home.Length == 0) continue;

            directories.Add(Path.Combine(home, "shims"));
        }

        foreach (var directory in directories)
        {
            if (directory.Length == 0) continue;

            foreach (var name in names) yield return Path.Combine(directory, name);
        }
    }

    /// <summary>
    /// НАЙДЕН ЛИ npm/pnpm — ПО ПРАВДЕ И ВО ВСЕХ МЕСТАХ СРАЗУ (задача F-3). Установка бывает и пакетом
    /// (`node_modules\npm`, кэш corepack), и одной командой-обёрткой (`npm.cmd`/`pnpm.cmd`, как у pnpm
    /// в `%APPDATA%\npm`, и шим corepack); «не найден» — только когда нет ни того, ни другого
    /// ни в одном месте.
    /// </summary>
    public static bool ToolFound(ToolPlaces places, string package) =>
        PackageDirectories(places, package).Any(Directory.Exists)
        || CorepackPackageDirectories(places, package).Any()
        || CommandFiles(places, package).Any(File.Exists);

    /// <summary>
    /// ВЕРСИЯ npm/pnpm — из `package.json` УСТАНОВЛЕННОГО ПАКЕТА (те же места, что у поиска, включая
    /// кэш corepack), и только если он читается. Найдено одной командой или шимом (пакета нигде нет) —
    /// версия пустая: вызывающий скажет «версия не прочитана» словами, а не выдумает номер.
    /// Чужой версии (например, версии Node) здесь не бывает — <see cref="Platform.VersionProbe.FromPackage"/>
    /// сверяет имя пакета.
    /// </summary>
    public static string ToolVersion(ToolPlaces places, string package) =>
        PackageDirectories(places, package)
            .Concat(CorepackPackageDirectories(places, package))
            .Select(directory => Platform.VersionProbe.FromPackage(directory, package))
            .FirstOrDefault(version => version.Length > 0) ?? string.Empty;
}
