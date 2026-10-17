using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// ФУНКЦИЯ ПАНЕЛИ, которой нужны умения движка. Список ведётся здесь ОДНИМ местом: второй список
/// (у телефона свой, у обновления свой) разошёлся бы с этим на первой же правке — в проекте это
/// уже случалось с ключами и с путями.
/// </summary>
public enum PanelFunction
{
    /// <summary>Баланс: нужен ключ модели.</summary>
    Balance,

    /// <summary>Копии и накат: нужна раскладка профиля движка.</summary>
    Copies,

    /// <summary>Калитка телефона (этап F): нужны вызовы движка.</summary>
    Phone,

    /// <summary>Обновление движка (этап E3): нужна версия и живой движок для пробы после установки.</summary>
    EngineUpdate,

    /// <summary>Значок в трее: не нужно ничего — панель живёт и без движка.</summary>
    Tray,
}

/// <summary>
/// РЕШЕНИЕ ГЕЙТА: пускаем функцию или отказываем — и если отказываем, то СЛОВАМИ, собранными из
/// трёх ключей (функция, умение, причина). Ключи, а не фразы: панель трёхъязычная, а отказ читает
/// человек.
/// </summary>
public sealed record GateVerdict(
    PanelFunction Function,
    bool Allowed,
    string FunctionKey,
    string SkillKey,
    string ReasonKey)
{
    /// <summary>Из чего собрана фраза — почти всегда удобнее звать <see cref="Text"/>.</summary>
    public static GateVerdict Yes(PanelFunction function) =>
        new(function, true, string.Empty, string.Empty, string.Empty);

    public static GateVerdict No(PanelFunction function, EngineSkill skill) =>
        new(function, false, SkillGate.NameKey(function), SkillKeys.Name(skill), SkillKeys.Reason(skill));

    /// <summary>
    /// СТРОКА ДЛЯ ЧЕЛОВЕКА. Пусто — функция доступна. Собирается ЗДЕСЬ и только здесь: собери её
    /// вызывающий, второй сборщик однажды разошёлся бы с первым (то же правило, что у отчёта
    /// об окружении).
    /// </summary>
    public string Text() => Allowed
        ? string.Empty
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc.T(SkillKeys.GateRefusedFormat),
            Loc.T(FunctionKey),
            Loc.T(SkillKey),
            Loc.T(ReasonKey));
}

/// <summary>
/// ГЕЙТ ПО УМЕНИЯМ: функция без нужного умения **отказывает словами**, а не молчит и не падает.
///
/// **Зачем.** До этого панель обещала функции независимо от того, умеет ли она их с этим движком:
/// ключа модели нет — кнопка баланса всё равно есть, раскладки профиля нет — копии всё равно
/// предлагаются. Решение владельца (этап E2): «нет умения → функция недоступна и объясняет словами,
/// остальное живёт».
///
/// ⚠️ **Проверка идёт в МОМЕНТ ПРОСЬБЫ, а не на каждом запуске.** Умения спрашиваются тогда, когда
/// человек зовёт функцию (и когда состояние показывает строку), а не при старте панели: дёргать
/// движок зря владелец запретил прямо.
///
/// ⚠️ **Решение — ЧИСТАЯ функция от вердиктов умений.** Факты для них добывает
/// <see cref="EngineSkillProbe"/>: файловые — сразу (<see cref="EngineSkillProbe.None"/>), живые —
/// пробой (<see cref="EngineSkillProbe.Gather"/>), и зовёт её тот, кому живые умения и нужны.
/// Здесь сети нет вовсе, поэтому обе стороны (умение есть · умения нет) проверяются без машины.
/// </summary>
public static class SkillGate
{
    /// <summary>Какие умения нужны функции. Пустой список — функция не зависит от движка.</summary>
    public static IReadOnlyList<EngineSkill> Needs(PanelFunction function) => function switch
    {
        PanelFunction.Balance => new[] { EngineSkill.ModelKey },
        PanelFunction.Copies => new[] { EngineSkill.BackupLayout },

        // Телефон и обновление движка объявлены ЗАРАНЕЕ, хотя кода у них ещё нет: список умений —
        // это договор, и заводить второй в этапах F и E3 значило бы однажды разойтись с этим.
        PanelFunction.Phone => new[]
        {
            EngineSkill.EntryLink,
            EngineSkill.SessionList,
            EngineSkill.TaskPrompt,
            EngineSkill.TaskCancel,
            EngineSkill.QuestionAnswer,
        },

        PanelFunction.EngineUpdate => new[] { EngineSkill.EngineVersion, EngineSkill.SessionList },

        PanelFunction.Tray => Array.Empty<EngineSkill>(),
        _ => Array.Empty<EngineSkill>(),
    };

    /// <summary>Имя функции для человека — ключом словаря.</summary>
    public static string NameKey(PanelFunction function) => function switch
    {
        PanelFunction.Balance => "PanelFunctionBalance",
        PanelFunction.Copies => "PanelFunctionCopies",
        PanelFunction.Phone => "PanelFunctionPhone",
        PanelFunction.EngineUpdate => "PanelFunctionEngineUpdate",
        PanelFunction.Tray => "PanelFunctionTray",
        _ => string.Empty,
    };

    /// <summary>
    /// Решение по функции. Отказ называет ПЕРВОЕ недостающее умение в порядке объявления — человеку
    /// важно, чего именно не хватает, чтобы понять, чем это лечится.
    /// </summary>
    public static GateVerdict Decide(PanelFunction function, IReadOnlyList<SkillVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        foreach (var skill in Needs(function))
        {
            var verdict = verdicts.FirstOrDefault(v => v.Skill == skill);

            // Умения в наборе нет вовсе — это «не знаем», и честный ответ тут «недоступно»:
            // пропустить проверку значило бы разрешить функцию, ничего о ней не зная.
            if (verdict is null || !verdict.Ok) return GateVerdict.No(function, skill);
        }

        return GateVerdict.Yes(function);
    }

    /// <summary>Функции, доступные при этих вердиктах, — для отчёта словами.</summary>
    public static IReadOnlyList<PanelFunction> Allowed(IReadOnlyList<SkillVerdict> verdicts) =>
        Enum.GetValues<PanelFunction>().Where(f => Decide(f, verdicts).Allowed).ToList();
}
