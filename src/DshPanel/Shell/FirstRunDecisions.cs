namespace DshPanel.Shell;

/// <summary>
/// ЧТО ОКНО ПЕРВОГО ЗАПУСКА ГОВОРИТ ЧЕЛОВЕКУ — одна строка на один ответ (A5, вариант «а»).
///
/// ⚠️ Строк не больше двух, и это решение, а не следствие: на чистой машине истинны три беды
/// (нет сервера DSH, нет ключа модели, сервер не поднялся), но «сервера нет» не дублирует
/// причину, когда она уже названа строкой выше — «нет сервера DSH». Одно исправление — одно место
/// (`_design-patterns-draft.md` §3.18).
/// </summary>
public enum FirstRunRowKind
{
    /// <summary>Сервер DSH панель не нашла: поднимать нечего, ставится из раздела «Обновление».</summary>
    EngineMissing,

    /// <summary>Сервер DSH есть, но не поднялся: причина названа словами, путь — окно панели.</summary>
    ServerNotStarted,

    /// <summary>Ключа модели нет: не будет ни баланса, ни работы агента.</summary>
    KeyMissing,

    /// <summary>Файл с ключом не читается — это НЕ «ключа нет» (правило «прочиталось ≠ нет»).</summary>
    KeyUnreadable,
}

/// <summary>
/// ПОЧЕМУ СЕРВЕР DSH НЕ ПОДНЯЛСЯ — ЗАКРЫТЫЙ СПИСОК ПРИЧИН (A5 §2.3).
///
/// ⚠️ Список закрыт намеренно: в окно первого запуска не имеет права попасть машинный текст
/// (имя типа исключения, код возврата, путь). Причина приходит СЛОВАМИ из словаря, и последняя
/// причина в списке — «подробности в окне панели»: это и есть выход для всего, что в список
/// не влезло.
/// </summary>
public enum FirstRunReason
{
    /// <summary>На порту панели отвечает не сервер DSH: порт занят другой программой.</summary>
    PortBusy,

    /// <summary>Система не дала запустить программу (отказ в запуске процесса).</summary>
    StartRefused,

    /// <summary>Программа завершилась, не начав отвечать.</summary>
    ExitedSilent,

    /// <summary>Всё остальное: подробности — в окне панели, а не в окне первого запуска.</summary>
    Unknown,
}

/// <summary>
/// ФАКТЫ ПЕРВОГО ЗАПУСКА — ТОЛЬКО ТО, ЧТО ОКНО ГОВОРИТ, И НИЧЕГО ЛИШНЕГО.
///
/// ⚠️ **Значения ключа здесь нет и быть не может** (красная линия 7): окну нужно ответить
/// «найден / не найден / не прочитан», а не показать секрет. Пути ключа окно тоже не знает —
/// у него нет ни одного пути, только факты.
///
/// ⚠️ <see cref="EnginePresent"/> — ОТДЕЛЬНЫЙ факт присутствия, а не пустая строка версии:
/// «не установлен» говорить нельзя, потому что поиск сервера DSH пуст и когда пакет есть,
/// а Node нет (`Server\DshEngine.cs`). Поэтому окно и говорит «не найден».
/// </summary>
public sealed record FirstRunFacts(
    bool EnginePresent,
    FirstRunReason? ServerProblem,
    bool KeyFound,
    bool KeyReadable)
{
    /// <summary>Ничего не известно — так окно выглядит, пока факты не прочитаны.</summary>
    public static FirstRunFacts Unknown { get; } = new(true, null, true, true);
}

/// <summary>
/// ДВЕРЬ ФАКТОВ ПЕРВОГО ЗАПУСКА (<see cref="FirstRunFacts"/>) — по образцу остальных дверей панели.
///
/// Зачем дверь, а не запись фактов доводом окна: у окна есть кнопка «Проверить снова», и она обязана
/// спросить ИСТИНУ заново, а не перерисовать прежний ответ. Съёмка кадра даёт свою дверь —
/// образцовую (файл образцовых дверей съёмки), и в ней нет ни одного обращения к машине.
/// </summary>
public interface IFirstRunFacts
{
    /// <summary>Прочитать факты заново. Значение ключа не возвращается никогда.</summary>
    FirstRunFacts Read();
}

/// <summary>Дверь фактов из функции — чтобы проверке и съёмке не заводить по классу на случай.</summary>
public sealed class FirstRunFactsDoor : IFirstRunFacts
{
    private readonly Func<FirstRunFacts> _read;

    public FirstRunFactsDoor(Func<FirstRunFacts> read) =>
        _read = read ?? throw new ArgumentNullException(nameof(read));

    public FirstRunFacts Read() => _read();
}

/// <summary>
/// РЕШЕНИЯ ОКНА ПЕРВОГО ЗАПУСКА — ЧИСТЫЕ ФУНКЦИИ (A5).
///
/// Здесь нет ни одного обращения к диску, настройкам и движку: факты приходят записью. Поэтому
/// «показывать ли окно», «сколько в нём строк» и «какая из них первая» перебираются проверками,
/// а не проверяются глазами на живой машине.
/// </summary>
public static class FirstRunDecisions
{
    /// <summary>
    /// СТРОКИ ОКНА В ПОРЯДКЕ ЗАВИСИМОСТИ: сначала то, без чего не работает ничего (сервер DSH),
    /// потом ключ модели. Нормы на порядок в проверенных руководствах нет — это наше решение,
    /// и оно названо (`_team\c2\a5-vid.md` §2).
    /// </summary>
    public static IReadOnlyList<FirstRunRowKind> Rows(FirstRunFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var rows = new List<FirstRunRowKind>(2);

        if (!facts.EnginePresent)
        {
            rows.Add(FirstRunRowKind.EngineMissing);
        }
        else if (facts.ServerProblem is not null)
        {
            // «Сервера нет» называется ОТДЕЛЬНОЙ строкой только тогда, когда сервер DSH на машине
            // есть: иначе причина уже названа строкой «Сервер DSH не найден», и две строки с одним
            // исправлением — шум.
            rows.Add(FirstRunRowKind.ServerNotStarted);
        }

        if (!facts.KeyReadable && facts.KeyFound)
        {
            // Файл есть, ключ в нём есть, а прочитать не вышло — это третий факт, и он НЕ «ключа нет».
            rows.Add(FirstRunRowKind.KeyUnreadable);
        }
        else if (!facts.KeyFound)
        {
            rows.Add(FirstRunRowKind.KeyMissing);
        }

        return rows;
    }

    /// <summary>
    /// ПОКАЗЫВАТЬ ЛИ ОКНО В ЭТОМ ЗАПУСКЕ. Четыре условия, и снятое любое из них — дефект:
    ///
    /// * <paramref name="humanLaunch"/> — окно не показывается прогону проверки: он живёт на машине
    ///   человека и не имеет права показывать ему окна (красная линия 8);
    /// * <paramref name="isolated"/> — и не показывается изолированному прогону;
    /// * <paramref name="shownAt"/> — «один раз»: отметка о показе живёт в настройках профиля,
    ///   и пустая она только у профиля, который окна ещё не видел;
    /// * <paramref name="rows"/> — окно показывается, только когда ему ЕСТЬ ЧТО сказать. На готовой
    ///   машине окна нет вовсе, и отметка тогда не ставится (это названное следствие, а не недоделка).
    /// </summary>
    public static bool ShouldShow(
        bool humanLaunch,
        bool isolated,
        string? shownAt,
        IReadOnlyList<FirstRunRowKind> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return humanLaunch && !isolated && string.IsNullOrWhiteSpace(shownAt) && rows.Count > 0;
    }

    /// <summary>
    /// ПОЧЕМУ СЕРВЕР DSH НЕ ПОДНЯЛСЯ — ЧИСТОЕ РЕШЕНИЕ ПО УСТОЙЧИВЫМ ФАКТАМ (A5 §2.3).
    ///
    /// **Что здесь НЕ является причиной.** «Не отвечает прямо сейчас» — не причина: в первые
    /// секунды после старта панель уже пишет «связи нет», хотя связь есть (`00-brief-c2.md` §5).
    /// Поэтому причина берётся только из УСТОЙЧИВОГО факта: занятый порт, отказ системы в запуске,
    /// завершившийся процесс — либо из уже записанной строки отказа контроллера.
    ///
    /// ⚠️ **ПОКА ИДЁТ ПОПЫТКА СТАРТА, ПРИЧИНЫ НЕТ ВООБЩЕ (DEF-054, 2.4.0).** Это отдельный факт,
    /// а не «ещё одно неработающее состояние», и он спрашивается ПЕРВЫМ. От какого случая: при
    /// старте ПК панель поднимает сервер в фоне, и в тот же миг проверка первого запуска видела
    /// «не работает» — журнал 23.10.2026 10:07:42: «Сервер DSH запущен: node PID 27040» и
    /// «окно первого запуска показано: Сервер DSH не поднялся» в одну секунду. При этом контроллер
    /// в такие секунды отдаёт **свой** устойчивый ответ «ещё стартует, достраивает профиль»
    /// (`EngineFirstRun.StillStartingDetail`) и процесс не гасит, — то есть окно говорило человеку
    /// «не поднялся» ровно о том, что поднимается. Отсюда правило: идёт попытка — окно молчит,
    /// а решит она или отложенная проверка ПОСЛЕ попытки (см. `App.ShowFirstRunAfterStart`).
    ///
    /// ⚠️ Возвращается <c>null</c>, когда говорить нечего: «сервер ещё не поднимали» — это не
    /// «сервер не поднялся», и окно не имеет права выдавать одно за другое.
    /// </summary>
    /// <param name="enginePresent">Сервер DSH на машине есть. Нет — строку не спрашиваем вовсе.</param>
    /// <param name="running">Сервер отвечает прямо сейчас.</param>
    /// <param name="starting">
    /// ИДЁТ ПОПЫТКА СТАРТА (контроллер поднимает процесс или процесс жив, но ещё не ответил).
    /// Пока она идёт, причина не называется: это не неудача, а «ещё не кончилось».
    /// </param>
    /// <param name="presence">Что стоит на порту (занят ли он чужой программой).</param>
    /// <param name="owner">Кому принадлежит сервер (панель поднимала его или нет).</param>
    /// <param name="detail">Последнее слово контроллера об отказе; пусто или «порт свободен» — не отказ.</param>
    public static FirstRunReason? Reason(
        bool enginePresent,
        bool running,
        bool starting,
        Server.ServerPresence presence,
        Server.ServerOwner owner,
        string? detail)
    {
        if (!enginePresent || running) return null;

        // ПОПЫТКА ИДЁТ — ЭТО НЕ НЕУДАЧА. Проверка молчит; после попытки её спросят снова.
        // ПОПЫТКА ИДЁТ — ЭТО НЕ НЕУДАЧА. Проверка молчит; после попытки её спросят снова.
        if (starting) return null;

        if (presence == Server.ServerPresence.BusyByOther) return FirstRunReason.PortBusy;

        var text = (detail ?? string.Empty).Trim();

        if (text.Length > 0 && text == PanelStrings.SrvEngineStartRefused) return FirstRunReason.StartRefused;

        if (owner == Server.ServerOwner.Panel || (text.Length > 0 && text == PanelStrings.SrvEngineExited))
            return FirstRunReason.ExitedSilent;

        // Всё остальное, что контроллер уже назвал отказом, — «подробности в окне панели»:
        // машинный текст в окно первого запуска не идёт. Свободный порт отказом не считается.
        if (text.Length > 0 && text != PanelStrings.SrvPortFree) return FirstRunReason.Unknown;

        return null;
    }

    /// <summary>
    /// СЛОВА ПРИЧИНЫ — ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ. Ключ выбирается здесь, а фразы живут в словарях:
    /// так причина не может попасть в окно машинным текстом.
    /// </summary>
    public static string ReasonText(FirstRunReason reason) => reason switch
    {
        FirstRunReason.PortBusy => PanelStrings.FirstRunReasonPortBusy,
        FirstRunReason.StartRefused => PanelStrings.FirstRunReasonStartRefused,
        FirstRunReason.ExitedSilent => PanelStrings.FirstRunReasonExitedSilent,
        _ => PanelStrings.FirstRunReasonUnknown,
    };

    /// <summary>
    /// ОТМЕТКА «ОКНО ПОКАЗАНО» — моментом времени, а не <c>true</c>: по ней видно, когда это было,
    /// и она же отличается от «не было» пустой строкой (как <c>PhoneConsentAt</c> и
    /// <c>AdoptedServerPort</c>).
    ///
    /// ⚠️ Формат — ТОТ ЖЕ, что у остальных отметок панели (`PricingDecisions.Stamp`, «O» по Гринвичу):
    /// второй формат рядом означал бы, что однажды прочитают не тем разбором и решат «отметки нет».
    /// </summary>
    public static string Stamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
