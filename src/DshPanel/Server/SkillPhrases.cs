using System.Globalization;
using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// СТРОКИ УМЕНИЙ ДЛЯ ЧЕЛОВЕКА — ОДНО МЕСТО. Их читают ДВОЕ: проба (`--server-selftest`) и отчёт
/// об окружении (раздел «Сервер»). Собери их каждый у себя — и однажды проба сказала бы одно,
/// а окно другое (то же правило, что у самого отчёта: один разбор, два показа).
///
/// Слова живут в трёх словарях, здесь только сборка: имя умения, слово вердикта
/// (**проверена · не проверялась · известна беда** — слова владельца) и подробность
/// (способ проверки у проверенного, причина у беды).
/// </summary>
public static class SkillPhrases
{
    /// <summary>Строка одного умения: «имя: вердикт — подробность».</summary>
    public static string Line(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return string.Format(
            CultureInfo.CurrentCulture,
            Loc.T(SkillKeys.LineBareFormat),
            Loc.T(SkillKeys.Name(verdict.Skill)),
            Value(verdict));
    }

    /// <summary>
    /// СЛОВО ВНУТРИ МЕТКИ — то, чем строка помечена (решение владельца 14.10.2026: «метки плотные,
    /// слово ВНУТРИ метки, строчными, без точки»). Метка без слова запрещена нормой WCAG 2.2 SC 1.4.1:
    /// цвет не имеет права быть единственным носителем смысла, поэтому слово здесь обязательно.
    ///
    /// ⚠️ **Одна тонкость предмета: у версии сервера DSH «не прочитана» — это НЕ беда.** Версию
    /// прочитать не удалось, работать от этого панель не перестала; красная метка сказала бы
    /// «сломано» там, где верно «неизвестно». Поэтому у этого умения своё слово и свой жёлтый тон.
    /// </summary>
    public static string Word(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.State switch
        {
            SkillState.Ok => Loc.T(SkillKeys.Checked),
            SkillState.Missing => Unread(verdict)
                ? Loc.T(SkillKeys.VersionUnread)
                : NotDeclared(verdict)
                    ? Loc.T(SkillKeys.NotDeclared)
                    : Loc.T(SkillKeys.Trouble),

            // «Не проверялась» — НЕ беда: панель об этом сервер DSH не спрашивала.
            _ => Loc.T(SkillKeys.NotChecked),
        };
    }

    /// <summary>
    /// БЕДА, КОТОРАЯ НЕ ПОЛОМКА: умение проверено ПО ОБЪЯВЛЕНИЮ сервера, и сервер такого вызова
    /// не объявляет (DEF-048, D-9). Прежнее слово метки — «не работает» — говорило человеку
    /// «сломано», хотя чинить нечего: у этого сервера DSH просто нет вызова. Способ проверки
    /// («по объявлению сервера») и есть признак случая — он приходит из
    /// <see cref="EngineSkills.Measure"/> тем же вердиктом.
    /// </summary>
    public static bool NotDeclared(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.State == SkillState.Missing && verdict.HowKey == SkillKeys.HowDeclared;
    }

    /// <summary>
    /// КОРОТКАЯ ПОДРОБНОСТЬ рядом с меткой — одна фраза, без точки и без имён вызовов
    /// (решение владельца 14.10.2026). У «не проверялась» подробность тоже есть: она называет
    /// ПРИЧИНУ, а не оставляет человека с догадкой.
    /// </summary>
    public static string Detail(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return Loc.T(verdict.State == SkillState.Ok ? verdict.HowKey : verdict.ReasonKey);
    }

    /// <summary>Версия сервера DSH не прочиталась — «неизвестно», а не «сломано».</summary>
    public static bool Unread(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.State == SkillState.Missing && verdict.Skill == EngineSkill.EngineVersion;
    }

    /// <summary>
    /// ВЕРДИКТ С ПОДРОБНОСТЬЮ, БЕЗ ИМЕНИ — то, что печатает отчёт пробы
    /// (<c>--server-selftest</c>) и текст отчёта об окружении.
    ///
    /// ⚠️ **Собирается из ТЕХ ЖЕ двух значений, что и метка со строкой раздела** (см. <see cref="Word"/>
    /// и <see cref="Detail"/>): собери их кто-нибудь у себя — проба сказала бы одно, а окно другое.
    /// </summary>
    public static string Value(SkillVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return Pair(Word(verdict), Detail(verdict));
    }

    /// <summary>Вердикт и подробность одним значением — общий вид для обоих состояний.</summary>
    private static string Pair(string verdict, string detail) =>
        string.Format(CultureInfo.CurrentCulture, Loc.T(SkillKeys.ValueFormat), verdict, detail);

    /// <summary>
    /// Сводка по умениям: сколько подтверждено, сколько не проверялось — И КОГДА ЭТО СМОТРЕЛИ.
    ///
    /// **От какого случая (находка владельца 14.10.2026, ночь).** На одной карточке стояли рядом
    /// два РАЗНЫХ ответа: «Панель проверена на этой версии целиком: все 8 умений подтверждены живым
    /// прогоном в изоляции, список вызовов — 84 из 84» (это запись ФАЙЛА ПОДСКАЗОК о версии вообще)
    /// и таблица «умений подтверждено: 2 из 8; не проверялось: 3» (это ответ «что у меня СЕЙЧАС»).
    /// Человек читал проверенную версию как полурабочую.
    ///
    /// ⚠️ Момент печатается ВСЕГДА, и всегда честный: проба была — названо её время; пробы не было —
    /// так и сказано («панель сейчас не спрашивала»). Молчание здесь и было причиной путаницы.
    /// </summary>
    public static string Summary(IReadOnlyList<SkillVerdict> verdicts, SkillFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        var confirmed = EngineSkills.Confirmed(verdicts);
        var uncheckedSkills = EngineSkills.Unchecked(verdicts);

        var text = string.Format(
            CultureInfo.CurrentCulture, Loc.T(SkillKeys.SummaryFormat), confirmed, verdicts.Count);

        if (uncheckedSkills > 0)
            text += "; " + string.Format(CultureInfo.CurrentCulture, Loc.T(SkillKeys.UncheckedFormat), uncheckedSkills);

        return Moment(facts) + text;
    }

    /// <summary>
    /// КОГДА СМОТРЕЛИ УМЕНИЯ — началом сводки. Проба была: «смотрено сейчас: 14.10.2026, 00:12 (это
    /// про этот сервер DSH, а не запись о версии)». Пробы нет: «панель сейчас не спрашивала этот
    /// сервер DSH».
    ///
    /// ⚠️ Слова «а не запись о версии» стоят здесь НЕ для красоты: именно их не хватило владельцу,
    /// чтобы не прочитать общий вердикт файла подсказок как ответ про его сервер.
    /// </summary>
    public static string Moment(SkillFacts? facts)
    {
        if (facts?.MeasuredAt is not { } at)
            return Loc.T(SkillKeys.MomentNotAsked) + " ";

        var when = DshPanel.Pricing.PricingDecisions.CheckedText(
            DshPanel.Pricing.PricingDecisions.Stamp(at));

        return string.Format(CultureInfo.CurrentCulture, Loc.T(SkillKeys.MomentFormat), when) + " ";
    }

    /// <summary>
    /// СТРОКА ВЕРСИЙ СЛОВАМИ ПРОБЫ: какая версия движка и на каких версиях панель проверена.
    /// Второе приходит из файла подсказок (этап E2, шаг 5); пока его нет — «ни на одной версии»,
    /// и это правда, а не пустое место.
    /// </summary>
    public static string Versions(string engineVersion, string verified) =>
        string.Format(CultureInfo.CurrentCulture, Loc.T(SkillKeys.VersionsFormat), Version(engineVersion), Verified(verified));

    /// <summary>
    /// ЗНАЧЕНИЕ СТРОКИ ВЕРСИЙ ДЛЯ ОТЧЁТА — то же самое, но под названием строки («Версии»), потому
    /// что отчёт показывает версию и список отдельными столбцами. Собирается из тех же двух
    /// значений, что и <see cref="Versions"/>, — разойтись им нечем.
    /// </summary>
    public static string VersionsValue(string engineVersion, string verified) =>
        string.Format(CultureInfo.CurrentCulture, Loc.T(SkillKeys.VersionsValueFormat), Version(engineVersion), Verified(verified));

    /// <summary>Версия движка словами: пусто — «не прочиталась».</summary>
    private static string Version(string engineVersion) =>
        string.IsNullOrWhiteSpace(engineVersion) ? Loc.T(SkillKeys.VersionUnknown) : engineVersion.Trim();

    /// <summary>Список проверенных версий словами: пусто — «ни на одной версии».</summary>
    private static string Verified(string verified) =>
        string.IsNullOrWhiteSpace(verified) ? Loc.T(SkillKeys.VersionsNone) : verified.Trim();
}
