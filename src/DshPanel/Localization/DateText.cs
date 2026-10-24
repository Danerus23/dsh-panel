using System.Globalization;

namespace DshPanel.Localization;

/// <summary>
/// ДАТА И ВРЕМЯ В ОКНЕ — по ЯЗЫКУ ПАНЕЛИ, а не по культуре Windows (DEF-041).
///
/// **От какого случая.** Язык панели культуру не выставляет (присваиваний <c>CurrentCulture</c>
/// в <c>src</c> нет), а даты рисовались ЖЁСТКИМ шаблоном <c>dd.MM.yyyy</c>: в английском окне
/// человек читал «22.10.2026». Google «Dates and times» называет такую запись двусмысленной —
/// «a date written as 04/05/09 means different things in different regions», «Not recommended:
/// 02.12.2017»; если уж числа, то <c>YYYY-MM-DD</c>. Банк нашёл это у нас замером
/// (<c>_design-library\70-writing.md</c> §10.2, правило Р-5) и назвал дефектом, а не стилем.
///
/// **Почему одна функция, а не шаблон в каждом окне.** Шаблон жил в семи местах — и разъезжался:
/// в двух он шёл с <c>InvariantCulture</c>, в пяти с текущей культурой, а журнал
/// (<c>Server\EngineExitReport.cs</c>) обязан остаться машинным. Одна дверь
/// (<see cref="Date(DateTime, string?)"/>, <see cref="DateAndTime(DateTime, string?)"/>) держит
/// правило в одном месте, и «новый экран с датой» получает его, не выбирая шаблон заново.
///
/// **Правило по языкам** (банк §10.2, Р-5; ru оставлен обычаем):
/// <list type="bullet">
/// <item>ru — <c>22.10.2026</c>: обычай языка, владелец называл эту дату словами;</item>
/// <item>en — <c>Oct 22, 2026</c>: месяц СЛОВОМ, порядок «месяц — день — год» однозначен;</item>
/// <item>zh — <c>2026年10月22日</c>: обычная китайская запись, ни с чем не спутать.</item>
/// </list>
/// Культура берётся у САМОГО языка (en-US / zh-CN / ru-RU), а не у машины: иначе на русской
/// Windows английское «Oct» превратилось бы в «окт», и текст снова разошёлся бы с языком окна.
///
/// **Хранение отметок эта дверь не трогает.** В файлах живёт <c>"O"</c> по инвариантной культуре
/// (<c>PricingDecisions.Stamp</c>, <c>FirstRunDecisions.Stamp</c>) — машинная строка, которая
/// обязана читаться одинаково при любом языке; формат — ТОЛЬКО для показа.
/// </summary>
public static class DateText
{
    /// <summary>Только дата: <c>22.10.2026</c> / <c>Oct 22, 2026</c> / <c>2026年10月22日</c>.</summary>
    /// <param name="at">Момент, уже приведённый к тому поясу, который должен увидеть человек.</param>
    /// <param name="language">Язык панели; <c>null</c> — выбранный сейчас (<see cref="Loc.Language"/>).</param>
    public static string Date(DateTime at, string? language = null) =>
        Render(at, language, withTime: false);

    /// <inheritdoc cref="Date(DateTime, string?)"/>
    public static string Date(DateTimeOffset at, string? language = null) =>
        Render(at.DateTime, language, withTime: false);

    /// <summary>
    /// Дата и время: <c>22.10.2026, 14:32</c> / <c>Oct 22, 2026, 14:32</c> /
    /// <c>2026年10月22日 14:32</c>. Время — часы и минуты, круглые сутки: это отметка «когда
    /// проверяли», а не время суток, которое человек читает по своим часам.
    /// </summary>
    public static string DateAndTime(DateTime at, string? language = null) =>
        Render(at, language, withTime: true);

    /// <inheritdoc cref="DateAndTime(DateTime, string?)"/>
    public static string DateAndTime(DateTimeOffset at, string? language = null) =>
        Render(at.DateTime, language, withTime: true);

    /// <summary>
    /// Язык, по которому форматировать: названный явно, иначе выбранный в панели. «auto» и мусор
    /// приводятся к выбранному языку — второй раз решать «как в системе» здесь незачем: это уже
    /// сделал <see cref="LanguageDecisions.Resolve"/> при запуске.
    /// </summary>
    public static string Language(string? language)
    {
        var wanted = LanguageDecisions.Normalize(language);
        return wanted == LanguageDecisions.Auto ? Loc.Language : wanted;
    }

    /// <summary>
    /// Шаблон и культура одного языка — ОДНА пара на язык. Разделять их нельзя: шаблон без своей
    /// культуры даёт «окт 22, 2026», а культура без шаблона — системный порядок полей, из-за
    /// которого запись и стала двусмысленной.
    /// </summary>
    private static (CultureInfo Culture, string Date, string DateAndTime) Rule(string? language) =>
        Language(language) switch
        {
            "ru" => (CultureInfo.GetCultureInfo("ru-RU"), "dd.MM.yyyy", "dd.MM.yyyy, HH:mm"),
            "zh" => (CultureInfo.GetCultureInfo("zh-CN"), "yyyy'年'M'月'd'日'", "yyyy'年'M'月'd'日' HH:mm"),
            _ => (CultureInfo.GetCultureInfo("en-US"), "MMM d, yyyy", "MMM d, yyyy, HH:mm"),
        };

    private static string Render(DateTime at, string? language, bool withTime)
    {
        var rule = Rule(language);

        return at.ToString(withTime ? rule.DateAndTime : rule.Date, rule.Culture);
    }
}
