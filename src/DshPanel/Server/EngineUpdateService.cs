using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ДВЕРИ РАЗДЕЛА ОБНОВЛЕНИЯ ДВИЖКА: чем узнать версии и чем обновить.
///
/// **Зачем дверями, а не прямо.** Тем же правилом, что у установки и у исполнителя: проверки обязаны
/// проходить путь целиком, **не трогая машину** (ни сети, ни npm, ни портов). Здесь двери читают
/// версии и делают работу, а решения блока живут в <see cref="EngineUpdateDecisions"/>.
///
/// <paramref name="Ready"/> — «здесь обновлять вообще можно»: у прогона проверки, у изолированного
/// запуска и там, где своей папки движка нет, двери — <see cref="None"/>, и окно обязано сказать
/// это словами, а не показывать пустые строки.
/// </summary>
public sealed record EngineUpdateSectionDoors(
    Func<EngineHints> ReadHints,
    Func<string> ReadFresh,
    Func<int> OwnPort,
    EngineUpdateDoors Run,
    bool Ready = true)
{
    /// <summary>
    /// ЕСТЬ ЛИ ЗАМОК ДЛЯ ЭТОЙ ВЕРСИИ. Поставить движок панель умеет **только по замку**
    /// (`EngineInstallDecisions.Decide` берёт версию из подсказок и сверяет её с замком), поэтому
    /// «свежая версия» без замка ставится НЕ МОЖЕТ — и панель обязана сказать это **до** нажатия,
    /// а не «поставить» вместо неё проверенную.
    /// </summary>
    public Func<string, bool> HasLockFor { get; init; } = _ => false;

    /// <summary>
    /// ОТКАТ ПО КНОПКЕ (этап E3.3): что стоит сейчас, какие папки версий есть и чем вернуться.
    /// Пусто — отката в этом прогоне нет вовсе (прогон проверки, изоляция), и окно обязано сказать
    /// это словами, а не показывать мёртвую кнопку.
    /// </summary>
    public Func<EngineInstallRecord> ReadRecord { get; init; } = () => EngineInstallRecord.Empty;

    /// <summary>Папки версий, которые есть в папке движка (их читает тот, у кого есть пути).</summary>
    public Func<IReadOnlyList<string>> ReadFolders { get; init; } = () => Array.Empty<string>();

    /// <summary>
    /// ЧТО СТОИТ СЕЙЧАС — своя версия сервера DSH (<c>EngineUpdateNotice.Installed</c>: своя
    /// поставленная, иначе версия общей установки человека). Пусто — не прочитали, и карточка
    /// скажет это словами, а не предложит обновление наугад.
    /// </summary>
    public Func<string> ReadInstalled { get; init; } = () => string.Empty;

    /// <summary>
    /// СЕРВЕР DSH НА МАШИНЕ ЕСТЬ ВООБЩЕ (A5 §7) — отдельный факт присутствия, а не «версия пуста».
    /// Умолчание <c>true</c> выбрано намеренно: «не знаем про присутствие» не должно превращаться
    /// в «сервера нет» и предлагать установку там, где её никто не просил.
    /// </summary>
    public Func<bool> ReadPresent { get; init; } = () => true;

    /// <summary>Двери отката: погасить свой движок, проверить прежнюю версию живьём, переписать запись.</summary>
    public EngineRollbackDoors RollbackDoors { get; init; } = EngineRollbackDoors.None;

    /// <summary>Дверей нет: обновлять нечем (прогон проверки, изоляция, нет своей папки движка).</summary>
    public static EngineUpdateSectionDoors None { get; } = new(
        () => EngineHints.Empty,
        () => string.Empty,
        () => 0,
        EngineUpdateDoors.None,
        Ready: false);
}

/// <summary>
/// РАЗДЕЛ «ОБНОВЛЕНИЕ ДВИЖКА» СО СТОРОНЫ ДЕЛА (пункты 4–7 плана E3.2): показывает две строки
/// (рекомендуемую и свежую), решает, что делает главная кнопка, и ведёт путь обновления.
///
/// **Три правила, и все три — решения владельца:**
///
/// 1. **Главная кнопка ставит только ПРОВЕРЕННУЮ версию** — ту, что в файле подсказок
///    (<see cref="EngineUpdateDecisions.MainButtonInstallsFresh"/> — `false` навсегда, а не «пока»).
///    Свежая из реестра показывается ОТДЕЛЬНОЙ строкой и ставится только отдельной кнопкой с
///    подтверждением.
/// 2. **Спрашивать ВСЕГДА** перед обновлением (<see cref="EngineUpdatePolicy.AskAlwaysBeforeUpdate"/>):
///    признака «идёт работа» у панели нет, поэтому вопрос задаёт окно, а путь начинается шагом
///    <see cref="EngineUpdateStepKind.AskHuman"/>.
/// 3. **«Полуархива не бывает»**: неудавшаяся предохранительная копия профиля — отказ, и установка
///    не начинается вовсе (<see cref="EngineUpdateRefusal.CopyNotWhole"/>).
///
/// ⚠️ Что здесь НЕ делается: ни одной строки в словаре, ни одного действия с диском. Работу делает
/// исполнитель (<see cref="EngineUpdateRun"/>) через двери, а слова живут в трёх словарях.
/// </summary>
public sealed class EngineUpdateService : IEngineUpdateSection
{
    private readonly EngineUpdateSectionDoors _doors;
    private readonly Action<string>? _log;

    private EngineHints _hints = EngineHints.Empty;
    private string _fresh = string.Empty;
    private string _recommended = string.Empty;
    private string _installed = string.Empty;
    private bool _present = true;

    public EngineUpdateService(EngineUpdateSectionDoors doors, Action<string>? log = null)
    {
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _log = log;

        Refresh();
    }

    /// <summary>Есть чем обновлять: двери даны, подсказки прочитаны, проверенная версия известна.</summary>
    public bool Available => _doors.Ready && _recommended.Length > 0;

    /// <summary>Почему нельзя — ключом словаря; пусто, когда можно.</summary>
    public string RefusalKey => Available
        ? string.Empty
        : _doors.Ready ? EngineUpdateFlow.Words.NoRecommended : EngineInstallRefusals.UpdateUnavailable;

    /// <summary>Рекомендуемая (проверенная пробой) версия; пусто — нет.</summary>
    public string Recommended => _recommended;

    /// <summary>Своя стоящая версия сервера DSH; пусто — прочитать не удалось.</summary>
    public string Installed => _installed;

    /// <summary>СЕРВЕР DSH ЕСТЬ НА МАШИНЕ ВООБЩЕ — факт присутствия, а не пустая строка версии (A5 §7).</summary>
    public bool EnginePresent => _present;

    /// <summary>Свежая версия из реестра npm; пусто — не узнали.</summary>
    public string Fresh => _fresh;

    /// <summary>Свежая НОВЕЕ рекомендуемой — только тогда её и предлагаем отдельной кнопкой.</summary>
    public bool FreshIsNewer => EngineUpdateDecisions.FreshIsNewer(_fresh, _recommended);

    /// <summary>
    /// МОЖНО ЛИ ПОСТАВИТЬ СВЕЖУЮ. Панель ставит движок **только по замку**: у непроверенной версии
    /// замка нет (замок снимается замером с проверенной версии), поэтому её нельзя не только
    /// «одним щелчком главной кнопки», но и отдельной дверью — <b>пока для неё нет замка</b>.
    /// Раньше панель в этом случае молча ставила ПРОВЕРЕННУЮ версию и объявляла свежую — то есть
    /// говорила неправду; теперь она говорит это ДО нажатия.
    /// </summary>
    public bool FreshInstallable => _fresh.Length > 0 && _doors.HasLockFor(_fresh);

    /// <summary>
    /// СТАВИТЬ: <paramref name="freshWanted"/> = «человек нажал ОТДЕЛЬНУЮ кнопку непроверенной версии»
    /// (главная кнопка всегда идёт с `false`). Отвечает ПОСЛЕДНЕЙ строкой пути — тем, чем кончилось.
    /// </summary>
    public string Run(bool freshWanted)
    {
        if (!_doors.Ready)
            return EngineInstallPhrases.UpdateRefusal(EngineInstallRefusals.UpdateUnavailable);

        // СНАЧАЛА ПЛАН («можно ли вообще»), ПОТОМ ЗАМОК («можно ли ИМЕННО ЭТУ версию»):
        // «свежая не новее» — ответ более точный, чем «замка нет», и человеку нужен он.
        var plan = EngineUpdateFlow.Plan(_hints, _fresh, freshWanted, _doors.OwnPort());

        // НЕПРОВЕРЕННАЯ ВЕРСИЯ БЕЗ ЗАМКА НЕ СТАВИТСЯ — и об этом говорится ДО всякой работы:
        // иначе панель поставила бы по замку другую версию, а сказала бы про эту.
        if (plan.Any && freshWanted && !FreshInstallable)
            return EngineInstallPhrases.NoLockForVersion(plan.Version);

        var result = EngineUpdateRun.Run(plan, _doors.Run, _log);

        // Список поставленного мог измениться (и подсказки могли обновиться) — перечитываем:
        // окно после работы обязано показывать то же, что покажет следующий запуск панели.
        Refresh();

        return result.Lines.Count > 0 ? result.Lines[^1] : string.Empty;
    }

    /// <summary>
    /// ВЕРНУТЬСЯ К ПРЕЖНЕЙ ВЕРСИИ (этап E3.3): можно ли и на какую. Пустая цель — возвращаться
    /// некуда, и окно говорит об этом словами, а не показывает мёртвую кнопку.
    /// </summary>
    public EngineRollbackPlan RollbackPlan() =>
        EngineRollbackDecisions.Decide(_doors.ReadRecord(), _doors.ReadFolders());

    /// <summary>Есть куда возвращаться: прежняя версия, которую ставила панель, лежит на диске.</summary>
    public bool RollbackAvailable => RollbackPlan().Any;

    /// <summary>Куда вернёмся — версия; пусто, когда возвращаться некуда (ею подписана кнопка).</summary>
    public string RollbackTarget => RollbackPlan().Target;

    /// <summary>
    /// ВЫЖИМКА ПРО ВЕРСИИ: заметки и беды из файла подсказок на языке панели — строками для своего
    /// поля. Пусто — окно говорит словами, что заметок нет (а не показывает пустое поле).
    /// </summary>
    public IReadOnlyList<string> Notes =>
        EngineLines.Digest(_hints, _recommended, _fresh, FreshIsNewer, Localization.Loc.Language);

    /// <summary>
    /// АДРЕС ВЫПУСКОВ ДВИЖКА для ссылки. Он ОДИН и не зависит от того, что прочиталось: адрес —
    /// не факт машины, а место, куда панель ведёт человека за подробностями (замер 11.10.2026).
    /// </summary>
    public string ReleasesAddress => ProductLinks.EngineReleasesUrl;

    /// <summary>Вопрос перед обновлением: показывается ВСЕГДА, даже когда обновлять нечем (слова отказа).</summary>
    public PanelQuestion UpdateQuestion => new(
        PanelStrings.EngineUpdateAskTitle,
        Available
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.EngineUpdateAskFormat, _recommended)
            : EngineInstallPhrases.UpdateRefusal(RefusalKey),
        PanelStrings.EngineUpdateAskYes,
        PanelStrings.EngineUpdateAskNo,
        PanelStrings.TipEngineUpdateAskYes,
        PanelStrings.TipEngineUpdateAskNo);

    /// <summary>Вопрос перед откатом: **последствие для сессий — словами**, а не числами.</summary>
    public PanelQuestion RollbackQuestion
    {
        get
        {
            var plan = RollbackPlan();

            return new PanelQuestion(
                PanelStrings.EngineRollbackAskTitle,
                plan.Any
                    ? string.Format(CultureInfo.CurrentCulture, PanelStrings.EngineRollbackAskFormat, plan.Current, plan.Target)
                    : EngineInstallPhrases.UpdateRefusal(plan.RefusalKey),
                PanelStrings.EngineRollbackAskYes,
                PanelStrings.EngineRollbackAskNo,
                PanelStrings.TipEngineRollbackYes,
                PanelStrings.TipEngineRollbackNo);
        }
    }

    /// <summary>
    /// ВЕРНУТЬСЯ: исполняет откат и отвечает последней строкой — тем, чем кончилось. Отказ «некуда»
    /// не трогает машину вовсе.
    /// </summary>
    public string Rollback()
    {
        var plan = RollbackPlan();

        if (!plan.Any)
            return EngineInstallPhrases.UpdateRefusal(plan.RefusalKey);

        var result = EngineRollbackRun.Run(plan, _doors.OwnPort(), _doors.RollbackDoors, _log);

        Refresh();

        return result.Lines.Count > 0 ? result.Lines[^1] : string.Empty;
    }

    /// <summary>ЗАНОВО СПРОСИТЬ ВЕРСИИ: подсказки (наш файл) и свежую (реестр npm).</summary>
    public void Refresh()
    {
        // ДВЕРЕЙ НЕТ — В СЕТЬ НЕ ИДЁМ ВОВСЕ: в прогоне проверки и в кадре окна обновление движка
        // недоступно, и читать ради двух строк, которые всё равно не показываются, незачем.
        if (!_doors.Ready)
        {
            _hints = EngineHints.Empty;
            _fresh = string.Empty;
            _recommended = string.Empty;
            _installed = string.Empty;
            _present = true;
            return;
        }

        try
        {
            _installed = (_doors.ReadInstalled() ?? string.Empty).Trim();
            _present = _doors.ReadPresent();
        }
        catch (Exception error)
        {
            // Версию читает диск; окно не должно падать из-за чужой беды, но и молчать о ней нельзя.
            _installed = string.Empty;
            _log?.Invoke(PanelStrings.EngineUpdateInstalledUnavailableLog + " " + error.GetType().Name);
        }

        try
        {
            _hints = _doors.ReadHints() ?? EngineHints.Empty;
        }
        catch (Exception error)
        {
            // Чтение подсказок исключений не бросает, но окно не должно падать из-за чужой беды.
            _hints = EngineHints.Empty;
            _log?.Invoke(PanelStrings.EngineHintsUnavailableLog + " " + error.GetType().Name);
        }

        try
        {
            _fresh = (_doors.ReadFresh() ?? string.Empty).Trim();
        }
        catch (Exception error)
        {
            _fresh = string.Empty;
            _log?.Invoke(PanelStrings.EngineUpdateFreshUnavailableLog + " " + error.GetType().Name);
        }

        _recommended = EngineUpdateDecisions.Recommended(_hints);
    }
}
