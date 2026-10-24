namespace DshPanel.Server;

/// <summary>
/// ИМЕНА КЛЮЧЕЙ СТРОК ДЛЯ УМЕНИЙ. Здесь только имена: <c>PanelStrings</c> объявляет строку
/// свойством с этим именем, а три словаря дают перевод. Второго списка фраз в коде не будет —
/// иначе он разошёлся бы со словарём на первой же правке (то же правило, что у отказов обновления).
///
/// ⚠️ **Причины отказов — ТОЖЕ КЛЮЧИ, а не русские фразы.** Умения показывает и проба, и отчёт
/// об окружении, а отчёт трёхъязычный: русская причина, написанная в коде, попала бы в английскую
/// строку — ровно тот дефект языка, который в проекте уже ловили 29.09.2026.
/// </summary>
public static class SkillKeys
{
    public const string Title = "EnvSkillsTitle";

    /// <summary>Строка умения без имени: «вердикт — подробность». Значение строки отчёта.</summary>
    public const string ValueFormat = "EnvSkillValueFormat";

    /// <summary>Строка умения с именем: «имя: вердикт — подробность».</summary>
    public const string LineBareFormat = "EnvSkillLineBareFormat";

    /// <summary>Название строки-итога в отчёте (сам итог — <see cref="SummaryFormat"/>).</summary>
    public const string SummaryLabel = "EnvSkillsSummaryLabel";

    /// <summary>Вердикты словами владельца (14.10.2026): проверено · не работает · не проверялась.</summary>
    public const string Checked = "EnvSkillChecked";
    public const string NotChecked = "EnvSkillNotChecked";
    public const string Trouble = "EnvSkillTrouble";

    /// <summary>
    /// Слово для беды, которая НЕ поломка: сервер DSH такого вызова не объявляет (DEF-048, кадр
    /// `settings-server`). «Не работает» читалось как «сломано», а верное — «этого у сервера нет».
    /// Признак случая — способ проверки «по объявлению сервера» (<see cref="HowDeclared"/>).
    /// </summary>
    public const string NotDeclared = "EnvSkillNotDeclared";

    /// <summary>
    /// Версия сервера DSH не прочиталась — «не прочитана» и жёлтая метка, а не красная беда:
    /// «неизвестно» и «сломано» — разные ответы (решение владельца 14.10.2026).
    /// </summary>
    public const string VersionUnread = "EnvSkillVersionUnread";

    /// <summary>Строка «Живая проба умений»: отвечает на вопрос «а это про сейчас?».</summary>
    public const string ProbeLabel = "EnvSkillsProbe";

    /// <summary>Причина «не проверялась» — у всех непроверенных умений одна.</summary>
    public const string ReasonNotChecked = "EnvSkillReasonNotChecked";

    /// <summary>Сводка: сколько умений не проверялось.</summary>
    public const string UncheckedFormat = "EnvSkillsUncheckedFormat";

    /// <summary>Строка версий словами пробы: какая версия движка и на каких панель проверена.</summary>
    public const string VersionsFormat = "EnvSkillVersionsFormat";

    /// <summary>Название строки версий в отчёте (значение — <see cref="VersionsValueFormat"/>).</summary>
    public const string VersionsLabel = "EnvSkillVersionsLabel";

    /// <summary>Значение строки версий в отчёте: версия движка и на каких панель проверена.</summary>
    public const string VersionsValueFormat = "EnvSkillVersionsValueFormat";

    /// <summary>Ни на одной версии панель не проверялась — так это и называется.</summary>
    public const string VersionsNone = "EnvSkillVersionsNone";

    /// <summary>Версия движка не прочиталась.</summary>
    public const string VersionUnknown = "EnvSkillVersionUnknown";
    public const string SummaryFormat = "EnvSkillsSummaryFormat";

    /// <summary>
    /// МОМЕНТ ПРОБЫ — начало сводки: «смотрено сейчас: {0} (это про этот сервер DSH, а не запись
    /// о версии)». Заведён по находке владельца 14.10.2026: без момента общий вердикт файла
    /// подсказок читался как ответ про его сервер, и проверенная версия выглядела полурабочей.
    /// </summary>
    public const string MomentFormat = "EnvSkillsMomentFormat";

    /// <summary>Пробы не было: «панель сейчас не спрашивала этот сервер DSH».</summary>
    public const string MomentNotAsked = "EnvSkillsMomentNotAsked";

    /// <summary>Отказ гейта: функция · умение · причина (три подстановки).</summary>
    public const string GateRefusedFormat = "SkillGateRefusedFormat";

    public const string HowLive = "EnvSkillHowLive";
    public const string HowDeclared = "EnvSkillHowDeclared";
    public const string HowFile = "EnvSkillHowFile";
    public const string HowVersion = "EnvSkillHowVersion";

    /// <summary>Имя умения для человека.</summary>
    public static string Name(EngineSkill skill) => skill switch
    {
        EngineSkill.EntryLink => "EnvSkillEntryLink",
        EngineSkill.SessionList => "EnvSkillSessionList",
        EngineSkill.TaskPrompt => "EnvSkillTaskPrompt",
        EngineSkill.TaskCancel => "EnvSkillTaskCancel",
        EngineSkill.QuestionAnswer => "EnvSkillQuestionAnswer",
        EngineSkill.ModelKey => "EnvSkillModelKey",
        EngineSkill.BackupLayout => "EnvSkillBackupLayout",
        EngineSkill.EngineVersion => "EnvSkillEngineVersion",
        _ => string.Empty,
    };

    /// <summary>Ключ причины отказа для умения.</summary>
    public static string Reason(EngineSkill skill, bool controlChannelKnown = true) => skill switch
    {
        EngineSkill.EntryLink => "EnvSkillEntryLinkNo",
        EngineSkill.SessionList => "EnvSkillSessionListNo",
        EngineSkill.TaskPrompt => "EnvSkillTaskPromptNo",
        EngineSkill.TaskCancel => "EnvSkillTaskCancelNo",
        EngineSkill.QuestionAnswer => controlChannelKnown ? "EnvSkillQuestionAnswerNoStream" : "EnvSkillQuestionAnswerNoChannel",
        EngineSkill.ModelKey => "EnvSkillModelKeyNo",
        EngineSkill.BackupLayout => "EnvSkillBackupLayoutNo",
        EngineSkill.EngineVersion => "EnvSkillEngineVersionNo",
        _ => string.Empty,
    };
}