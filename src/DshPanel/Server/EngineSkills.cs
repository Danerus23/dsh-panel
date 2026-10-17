namespace DshPanel.Server;

/// <summary>
/// УМЕНИЕ ПАНЕЛИ С ЭТИМ ДВИЖКОМ — то, что человеку обещать можно, а не то, что «вроде должно
/// работать». Список взят у владельца (план `ROADMAP-BORROWED.md`, этап E2): каждая строка —
/// отдельное обещание, и у каждой свой способ проверки.
///
/// **Почему умения, а не версии.** Номер версии ничего не говорит о том, что панель умеет:
/// движок может сменить имена вызовов, не сменив номер, и наоборот. Решение владельца —
/// «совместимость по умениям»: обещание даётся только после ПРОБЫ, а не по номеру.
/// </summary>
public enum EngineSkill
{
    /// <summary>Движок напечатал ссылку входа, и она ведёт на его же порт.</summary>
    EntryLink,

    /// <summary>Список сессий читается: живой вызов отвечает.</summary>
    SessionList,

    /// <summary>Постановка задачи возможна: движок объявляет вызов задачи.</summary>
    TaskPrompt,

    /// <summary>Отмена возможна: движок объявляет вызов отмены.</summary>
    TaskCancel,

    /// <summary>Ответ на вопрос агента возможен: есть канал управления и он открывается.</summary>
    QuestionAnswer,

    /// <summary>Баланс возможен: файл ключа модели на месте.</summary>
    ModelKey,

    /// <summary>Копии возможны: раскладка профиля движка на месте.</summary>
    BackupLayout,

    /// <summary>Версию движка удалось прочитать (по ней ищутся подсказки).</summary>
    EngineVersion,
}

/// <summary>
/// ЧТО ИЗМЕРЕНО — то, из чего выводится вердикт по умению. Отдельный набор фактов, а не «проба
/// сама себе решает»: так вердикты проверяются подставными фактами (есть умение / нет умения),
/// и проверка не зависит ни от сети, ни от машины.
/// </summary>
public sealed record SkillFacts
{
    /// <summary>Ссылка входа прочитана и ведёт на порт пробы.</summary>
    public bool EntryLinkParsed { get; init; }

    /// <summary>Живой вызов списка сессий принят движком.</summary>
    public bool SessionListAnswered { get; init; }

    /// <summary>Какие имена вызовов признал движок.</summary>
    public IReadOnlyCollection<string> RecognisedEndpoints { get; init; } = Array.Empty<string>();

    /// <summary>Канал управления открылся (рукопожатие потока).</summary>
    public bool ControlStreamOpened { get; init; }

    /// <summary>Файл ключа модели на месте.</summary>
    public bool ModelKeyFile { get; init; }

    /// <summary>Раскладка профиля движка на месте (есть каталог профилей).</summary>
    public bool ProfileLayout { get; init; }

    /// <summary>Версия движка прочитана (пусто — нет).</summary>
    public string EngineVersion { get; init; } = string.Empty;

    /// <summary>
    /// ЖИВАЯ ПРОБА БЫЛА: панель спрашивала движок (список сессий, канал управления, ссылку входа).
    /// Без неё живые умения честно называются «не проверялась», а не «нет»: «не спрашивали» и
    /// «спросили и не вышло» — две разные правды, и путать их значит врать человеку.
    /// </summary>
    public bool Probed { get; init; }

    /// <summary>
    /// КОГДА ЭТИ ФАКТЫ СНЯТЫ. Нужен, чтобы человек не спутал ДВА РАЗНЫХ ответа об одном и том же
    /// (находка владельца 14.10.2026, ночь): «панель проверена на этой версии целиком» — это запись
    /// ФАЙЛА ПОДСКАЗОК о версии вообще, а таблица умений отвечает «что у меня СЕЙЧАС». Без момента
    /// рядом эти два ответа читаются как один, и проверенная версия выглядит полурабочей.
    ///
    /// Пусто — момент не назван (факты собраны не пробой, а умолчанием прогона без движка).
    /// </summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    public bool Has(string endpoint) => RecognisedEndpoints.Contains(endpoint);
}

/// <summary>
/// ДВЕРЬ ПОСЛЕДНЕЙ ПРОБЫ УМЕНИЙ — откуда отчёт об окружении берёт факты «здесь и сейчас».
///
/// **От какого случая (задание владельца 14.10.2026, §2).** На одной карточке стояли рядом
/// вердикт ФАЙЛА ПОДСКАЗОК о версии вообще («панель проверена на этой версии целиком…»)
/// и сводка таблицы — ответ про ЭТОТ сервер. Читались они как один ответ.
///
/// ⚠️ **Пробы не было — так и говорится**, а вердикты файла подсказок за свои не выдаются:
/// пустые факты — честный ответ «панель сейчас не спрашивала этот сервер DSH».
/// Настоящую пробу заказывает <see cref="LiveSkillProbe"/> (в фоне, одна на свой сервер).
/// </summary>
public sealed record SkillProbeDoor(
    Func<SkillFacts> Facts,
    Action Refresh)
{
    /// <summary>
    /// «ФАКТЫ ПРИШЛИ» — СООБЩЕНИЕ О ДАННЫХ, А НЕ ЧАСЫ (задание владельца 14.10.2026, §3).
    ///
    /// **От какого случая.** Владелец открыл раздел «Сервер», увидел «кучу красных» меток и решил,
    /// что панель сломана. Оказалось, надо нажать «Проверить окружение»; его слова: *«Я думал панель
    /// автоматически это делает»*. Проба умений идёт В ФОНЕ (сеть в перерисовке окна держать нельзя)
    /// и о своём результате **никому не сообщала**: раздел рисовался один раз и до нажатия кнопки
    /// не менялся.
    ///
    /// ⚠️ **Подписка живёт на ДВЕРИ, а не у пробы:** окно знает только дверь (через владельца
    /// настроек), а проба — свой объект, и второго пути «узнать, что факты пришли» быть не должно.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// СООБЩИТЬ ПОДПИСЧИКАМ, ЧТО ФАКТЫ ОБНОВИЛИСЬ. Зовёт **ТОЛЬКО проба**
    /// (<see cref="LiveSkillProbe"/>), и зовёт уже НА НИТКЕ ИНТЕРФЕЙСА — туда её сообщение
    /// отправляет <c>Shell\UiThread.Post</c>: проба идёт в фоне, а окно живёт на своей нитке.
    /// </summary>
    public void Signal() => Changed?.Invoke();

    /// <summary>Пробы не было: факты пустые, пересчитывать нечего, и сообщать не о чем. Умолчание
    /// прогона проверки — подписываться на него некому и незачем.</summary>
    public static SkillProbeDoor None { get; } = new(() => new SkillFacts(), () => { });

    /// <summary>
    /// ГОТОВЫЕ ФАКТЫ КАК ЕСТЬ (подставные — прогоном проверки). Зовётся тем, у кого проба уже
    /// есть: так проверяется, что отчёт показывает ИМЕННО ИХ, а не догадку.
    /// </summary>
    public static SkillProbeDoor Ready(SkillFacts facts) => new(() => facts, () => { });
}

/// <summary>ТРИ СОСТОЯНИЯ УМЕНИЯ — словами владельца: проверена · не проверялась · известна беда.</summary>
public enum SkillState
{
    /// <summary>Проверена и работает.</summary>
    Ok,

    /// <summary>Известна беда: проверена и НЕ работает (или движок такого не объявляет).</summary>
    Missing,

    /// <summary>Не проверялась: панель об этом движок не спрашивала.</summary>
    Unknown,
}

/// <summary>
/// ВЕРДИКТ ПО УМЕНИЮ: состояние, почему — КЛЮЧОМ словаря, и всегда — КАК ЭТО ПРОВЕРЕНО (тоже
/// ключом). Ключи, а не фразы: умения показываются и в отчёте об окружении, а он трёхъязычный —
/// русская причина, написанная здесь словами, попала бы в английскую строку.
///
/// <see cref="ReasonKey"/> пуст у подтверждённого умения. <see cref="HowKey"/> — способ проверки:
/// «проверена» бывает добыта живым вызовом, а бывает прочитана по объявлению движка, и путать эти
/// два нельзя (решение владельца: решать пробой, а не номером версии).
/// </summary>
public sealed record SkillVerdict(EngineSkill Skill, SkillState State, string ReasonKey, string HowKey)
{
    /// <summary>Умение подтверждено — проверено и работает.</summary>
    public bool Ok => State == SkillState.Ok;

    /// <summary>Проверено и не работает — «известна беда».</summary>
    public bool Broken => State == SkillState.Missing;

    public static SkillVerdict Yes(EngineSkill skill, string howKey) => new(skill, SkillState.Ok, string.Empty, howKey);

    public static SkillVerdict No(EngineSkill skill, string reasonKey, string howKey) =>
        new(skill, SkillState.Missing, reasonKey, howKey);

    /// <summary>НЕ ПРОВЕРЯЛАСЬ: панель движок об этом не спрашивала. Причина — свой ключ.</summary>
    public static SkillVerdict Unknown(EngineSkill skill, string howKey) =>
        new(skill, SkillState.Unknown, SkillKeys.ReasonNotChecked, howKey);
}

/// <summary>
/// ВЫВОД УМЕНИЙ ИЗ ФАКТОВ — ЧИСТАЯ функция, и это здесь главное.
///
/// Проба поверхности (`--server-selftest`) отвечает на вопрос «какие ВЫЗОВЫ признаёт движок»;
/// умения отвечают на другой: **что из этого умеет панель**. Разница не косметическая: имена
/// могут быть признаны, а нужного канала — не быть, ключа — не быть, раскладки профиля — не быть.
///
/// ⚠️ **Постановка задачи и отмена проверяются по ОБЪЯВЛЕНИЮ, а не вызовом, и это честно
/// названо** ключом способа проверки: позвать задачу значит НАЧАТЬ РАБОТУ агента, а отмену —
/// прервать чужую. Проба этого не делает, и человек видит границу, а не выдуманное «проверено».
/// </summary>
public static class EngineSkills
{
    /// <summary>Все умения в порядке показа — от входа к раскладке.</summary>
    public static IReadOnlyList<EngineSkill> All { get; } = new[]
    {
        EngineSkill.EntryLink,
        EngineSkill.SessionList,
        EngineSkill.TaskPrompt,
        EngineSkill.TaskCancel,
        EngineSkill.QuestionAnswer,
        EngineSkill.ModelKey,
        EngineSkill.BackupLayout,
        EngineSkill.EngineVersion,
    };

    /// <summary>
    /// Вердикты по всем умениям. Каждое умение отвечает САМО за себя: нет ключа — панель всё равно
    /// скажет про сессии и копии (решение владельца: «нет умения → функция недоступна и объясняет
    /// словами, остальное живёт»).
    /// </summary>
    public static IReadOnlyList<SkillVerdict> Measure(SkillFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Живые умения без пробы — «не проверялась», а не «нет»: панель не спрашивала движок,
        // и назвать это бедой значило бы выдумать поломку. Файловые и версия проверяются всегда:
        // они и так под рукой.
        SkillVerdict Live(EngineSkill skill, bool ok, string troubleKey)
        {
            if (!facts.Probed) return SkillVerdict.Unknown(skill, SkillKeys.HowLive);

            return ok ? SkillVerdict.Yes(skill, SkillKeys.HowLive) : SkillVerdict.No(skill, troubleKey, SkillKeys.HowLive);
        }

        return new[]
        {
            Live(EngineSkill.EntryLink, facts.EntryLinkParsed, SkillKeys.Reason(EngineSkill.EntryLink)),
            Live(EngineSkill.SessionList, facts.SessionListAnswered, SkillKeys.Reason(EngineSkill.SessionList)),

            facts.Has("session/prompt")
                ? SkillVerdict.Yes(EngineSkill.TaskPrompt, SkillKeys.HowDeclared)
                : SkillVerdict.No(EngineSkill.TaskPrompt, SkillKeys.Reason(EngineSkill.TaskPrompt), SkillKeys.HowDeclared),

            facts.Has("session/cancel")
                ? SkillVerdict.Yes(EngineSkill.TaskCancel, SkillKeys.HowDeclared)
                : SkillVerdict.No(EngineSkill.TaskCancel, SkillKeys.Reason(EngineSkill.TaskCancel), SkillKeys.HowDeclared),

            // Ответ на вопрос агента требует ДВУХ вещей: имени канала управления и того, что канал
            // действительно открывается. Одного имени мало: потоковый вызов одиночным запросом не
            // отвечает вовсе, и признание имени тут ничего не доказывает.
            Live(
                EngineSkill.QuestionAnswer,
                facts.Has("session/control") && facts.ControlStreamOpened,
                SkillKeys.Reason(EngineSkill.QuestionAnswer, controlChannelKnown: facts.Has("session/control"))),

            facts.ModelKeyFile
                ? SkillVerdict.Yes(EngineSkill.ModelKey, SkillKeys.HowFile)
                : SkillVerdict.No(EngineSkill.ModelKey, SkillKeys.Reason(EngineSkill.ModelKey), SkillKeys.HowFile),

            facts.ProfileLayout
                ? SkillVerdict.Yes(EngineSkill.BackupLayout, SkillKeys.HowFile)
                : SkillVerdict.No(EngineSkill.BackupLayout, SkillKeys.Reason(EngineSkill.BackupLayout), SkillKeys.HowFile),

            facts.EngineVersion.Length > 0
                ? SkillVerdict.Yes(EngineSkill.EngineVersion, SkillKeys.HowVersion)
                : SkillVerdict.No(EngineSkill.EngineVersion, SkillKeys.Reason(EngineSkill.EngineVersion), SkillKeys.HowVersion),
        };
    }

    /// <summary>Сколько умений подтвердилось — для одной строки в отчёте.</summary>
    public static int Confirmed(IReadOnlyList<SkillVerdict> verdicts) => verdicts.Count(v => v.Ok);

    /// <summary>Сколько умений НЕ проверялось — «панель не спрашивала движок».</summary>
    public static int Unchecked(IReadOnlyList<SkillVerdict> verdicts) => verdicts.Count(v => v.State == SkillState.Unknown);

    /// <summary>Умения, у которых известна беда, в порядке показа.</summary>
    public static IReadOnlyList<SkillVerdict> Broken(IReadOnlyList<SkillVerdict> verdicts) =>
        verdicts.Where(v => v.Broken).ToList();

    /// <summary>Умения, которых не хватает или о которых не спрашивали, — «функция не работает».</summary>
    public static IReadOnlyList<SkillVerdict> Missing(IReadOnlyList<SkillVerdict> verdicts) =>
        verdicts.Where(v => !v.Ok).ToList();
}
