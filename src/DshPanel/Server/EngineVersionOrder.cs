using System.Globalization;

namespace DshPanel.Server;

/// <summary>
/// ПОРЯДОК ВЕРСИЙ — С ПРЕДРЕЛИЗНОЙ ЧАСТЬЮ, ПО SemVer, ОДНИМ МЕСТОМ (задача F-1b, слово владельца
/// 20.10.2026).
///
/// **От какого случая.** Сравнение версий движка резало номер до `X.Y.Z` (`EngineNode.Compare` — он
/// про версии Node, где предрелизных хвостов не бывает вовсе), и `0.2.1-rc.3` против `0.2.1-rc.2`
/// считалось «то же самое». Следствие было ложным отрицанием ровно в том сценарии, ради которого
/// правило и делалось: новый rc-выпуск движка не показывал кнопку «Поставить непроверенную…».
///
/// **Правило, и оно же ответ на «почему так»:**
///
/// <code>
///     0.2.1  >  0.2.1-rc.3  >  0.2.1-rc.2  >  0.2.1-alpha.5  >  0.2.1-alpha
/// </code>
///
/// * **числа — как числа**, а не строки: «2.10.0» новее «2.9.9» (по строкам вышло бы наоборот);
///   без равенства чисел хвост не смотрится вовсе;
/// * **хвост** (всё после первого `-`) сравнивается по SemVer, идентификатор за идентификатором:
///   числовые — как числа («rc.10» новее «rc.9»), текстовые — лексикографически без учёта регистра;
///   числовой идентификатор МЛАДШЕ текстового (так требует SemVer — «alpha.5» < «alpha.beta»);
///   короткий список младше длинного («alpha» < «alpha.1»);
/// * **финальный выпуск новее своего предрелиза**: отсутствие хвоста выше любого хвоста;
/// * `+hash` — хеш сборки, а не версия; буква `v` спереди — обычай тегов (`v1.21.0` = `1.21.0`);
/// * **не версия — не «старая версия»**: <see cref="IsVersion"/> отвечает «похоже ли это на номер»,
///   и вызывающий обязан сказать словами, что версия не разобрана, а не выдумывать успех.
///
/// ⚠️ **Второго такого правила в панели быть не должно.** Им пользуются и обновление панели
/// (`Update\UpdateDecisions` — там сравнение версий панели), и решения о движке
/// (`EngineUpdateDecisions`). До этой задачи SemVer-правило жило только в `UpdateDecisions`,
/// а движок сравнивался по числу — два правила об одном и том же.
/// </summary>
public static class EngineVersionOrder
{
    /// <summary>
    /// ЧИСЛОВАЯ ЧАСТЬ ВЕРСИИ: <c>2.0.0+481fb6b</c> → <c>2.0.0</c>, <c>v1.21.0</c> → <c>1.21.0</c>.
    ///
    /// Хеш коммита к <c>ProductVersion</c> дописывает сам .NET, и человеку он ничего не говорит.
    /// Буква <c>v</c> спереди — обычай тегов GitHub: <c>v1.21.0</c> и <c>1.21.0</c> одна версия.
    ///
    /// ⚠️ **Единственная реализация в панели.** Её зовут и окно «О программе», и сравнение версий,
    /// и разбор ответа GitHub: второе такое правило однажды разошлось бы с первым.
    /// </summary>
    public static string Numeric(string? version)
    {
        var text = (version ?? string.Empty).Trim();

        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        return text.Trim();
    }

    /// <summary>
    /// ПОХОЖЕ ЛИ ЭТО НА ВЕРСИЮ — а не на мусор. Хотя бы одно число в начале (после <c>v</c> и до
    /// <c>+</c>) и только допустимые знаки версии: цифры, латинские буквы, точка, дефис, подчёркивание.
    /// Пусто и «не версия» — <c>false</c>; вызывающий говорит об этом СЛОВАМИ.
    /// </summary>
    public static bool IsVersion(string? text)
    {
        var value = Numeric(text);

        if (value.Length == 0 || !char.IsDigit(value[0])) return false;

        foreach (var symbol in value)
        {
            if (char.IsDigit(symbol) || char.IsAsciiLetter(symbol)) continue;
            if (symbol is '.' or '-' or '_') continue;

            return false;
        }

        return Numbers(value).Length > 0;
    }

    /// <summary>
    /// СРАВНИТЬ ДВЕ ВЕРСИИ: &lt;0 — левая младше, 0 — одна версия, &gt;0 — левая новее.
    /// Полный ответ: числа, затем предрелизная часть (см. правило класса).
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var plain = CompareByNumber(left, right);
        if (plain != 0) return plain;

        var leftPre = PreRelease(Numeric(left));
        var rightPre = PreRelease(Numeric(right));

        if (leftPre.Length == 0 && rightPre.Length == 0) return 0;

        // ФИНАЛЬНЫЙ ВЫПУСК НОВЕЕ СВОЕГО ПРЕДРЕЛИЗА — иначе человек, поставивший rc, не получил бы
        // финальный выпуск никогда.
        if (leftPre.Length == 0) return 1;
        if (rightPre.Length == 0) return -1;

        return ComparePreRelease(leftPre, rightPre);
    }

    /// <summary>ЛЕВАЯ НОВЕЕ ПРАВОЙ (полное правило, с предрелизной частью).</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>
    /// СРАВНЕНИЕ ПО ЧИСЛАМ, БЕЗ ПРЕДРЕЛИЗНОЙ ЧАСТИ. Это ОТДЕЛЬНОЕ правило и отдельный ответ:
    /// им пользуется объявление о новой версии панели (`EngineUpdateNotice`), где решение владельца
    /// записано прямо — «новее здесь значит старшая часть версии больше, а решает всё равно проба,
    /// а не хвост номера». Менять его на SemVer значило бы менять другое решение, а не это.
    /// </summary>
    public static bool IsNewerByNumber(string? candidate, string? current) => CompareByNumber(candidate, current) > 0;

    /// <summary>Сравнение ЧИСЛОВЫХ частей версии: «2.10.0» новее «2.9.9», «2.0» равна «2.0.0».</summary>
    public static int CompareByNumber(string? left, string? right)
    {
        var a = Numbers(Numeric(left));
        var b = Numbers(Numeric(right));

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var x = index < a.Length ? a[index] : 0;
            var y = index < b.Length ? b[index] : 0;

            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    /// <summary>
    /// Числовая часть версии, разобранная на числа: «2.10.0-rc.1» → 2, 10, 0. Текст до хвоста
    /// читается по кускам до первой нецифры: «2.0.0» и «2.0» дают одно и то же начало, а
    /// «2.0.beta» — только два числа (дальше разбирать нечего).
    /// </summary>
    private static int[] Numbers(string version)
    {
        var head = version;
        var dash = head.IndexOf('-');
        if (dash >= 0) head = head[..dash];

        var parts = head.Split('.');
        var result = new List<int>();

        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) break;

            result.Add(value);
        }

        return result.ToArray();
    }

    /// <summary>Предрелизный хвост версии: «2.0.0-rc.2» → «rc.2»; у финальной версии пусто.</summary>
    private static string PreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? string.Empty : version[(dash + 1)..].Trim();
    }

    /// <summary>
    /// Сравнение хвостов по правилам SemVer: числа сравниваются как числа («rc.10» новее
    /// «rc.2»), слова — по алфавиту, короткий список младше длинного. Числовой кусок младше
    /// буквенного — так требует SemVer, и это ровно тот случай, ради которого правило и нужно.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            if (index >= a.Length) return -1;
            if (index >= b.Length) return 1;

            var leftIsNumber = int.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue);
            var rightIsNumber = int.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue);

            if (leftIsNumber && rightIsNumber)
            {
                if (leftValue != rightValue) return leftValue.CompareTo(rightValue);
                continue;
            }

            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;

            var compare = string.Compare(a[index], b[index], StringComparison.OrdinalIgnoreCase);
            if (compare != 0) return compare;
        }

        return 0;
    }
}
