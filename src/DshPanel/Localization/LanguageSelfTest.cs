using System.Reflection;
using DshPanel.Shell;

namespace DshPanel.Localization;

/// <summary>
/// Проба словарей НА СОБРАННОЙ СБОРКЕ — то, чего не видно из исходников: какие словари
/// действительно лежат внутри exe, что в них пропущено и какие ключи не принадлежат панели.
///
/// Проверяется пять вещей, и каждая строка отчёта ВЛИЯЕТ на исход:
///
/// 1. сколько всего ключей в трёх словарях вместе;
/// 2. чего нет или что оставлено пустым в каждом языке (<c>--lang-selftest</c> показывает
///    это человеку, а <see cref="Loc.CompareLanguages"/> отдаёт кодом возврата);
/// 3. у каких ключей РАСХОДЯТСЯ подстановки: <c>{0}</c> в одном языке и <c>{1}</c> в другом
///    означает падение <c>string.Format</c> прямо в окне;
/// 4. какие члены <c>PanelStrings</c> остались без записи в словаре — тогда человек прочитает
///    в окне имя ключа вместо текста (ровно этот случай был в v1: <c>about.noLink</c>);
/// 5. какие записи словаря не принадлежат ни одному члену — это ключ, оставшийся от прежней
///    строки; он молчит и мешает считать опись полной.
///
/// За собой проба не прибирает ничего: она только ЧИТАЕТ сборку и печатает отчёт —
/// ни файлов, ни реестра, ни сети, поэтому изоляции не требует.
/// </summary>
public static class LanguageSelfTest
{
    /// <summary>Ключи, у которых подстановки разошлись: ключ и по строке на язык.</summary>
    public static List<string> DivergedPlaceholders(out Dictionary<string, List<string>> gaps, out int total)
    {
        gaps = Loc.CompareLanguages(out total);

        var diverged = new List<string>();
        var russian = Loc.Dictionary("ru");

        foreach (var key in russian.Keys)
        {
            var reference = russian[key];

            foreach (var language in Loc.Known)
            {
                if (language == "ru") continue;

                // Язык, которого ещё нет, — не расхождение подстановок, а пропуск целиком:
                // о нём говорит полный список выше, и повторять его здесь незачем.
                var text = Loc.TIn(language, key);
                if (text == key || text.Length == 0) continue;

                if (!LanguageDecisions.SamePlaceholders(reference, text))
                {
                    diverged.Add($"{key} ({language}): {Describe(LanguageDecisions.Placeholders(reference))} " +
                                 $"≠ {Describe(LanguageDecisions.Placeholders(text))}");
                }
            }
        }

        return diverged;
    }

    public static int Run()
    {
        var report = new List<string>();
        var failed = false;

        var known = Members();
        var russian = Loc.Dictionary("ru");

        report.Add($"языки: {string.Join(", ", Loc.Known)}");
        report.Add($"члены PanelStrings: {known.Count}");
        report.Add($"ключей в русском словаре: {russian.Count}");

        var gaps = Loc.CompareLanguages(out var total);
        report.Add($"ключей всего (объединение трёх словарей): {total}");

        foreach (var language in Loc.Known)
        {
            var missing = gaps[language];
            report.Add($"язык {language}: пропущено или пусто {missing.Count}");

            // Первые двадцать имён — достаточно, чтобы понять, чего именно нет, и не превратить
            // отчёт в список на тысячу строк. Дальше — только число.
            foreach (var key in missing.Take(20)) report.Add($"  {language} — нет: {key}");

            if (missing.Count > 20) report.Add($"  {language} — и ещё {missing.Count - 20}");
        }

        var diverged = DivergedPlaceholders(out _, out _);
        report.Add($"подстановки разошлись у ключей: {diverged.Count}");
        foreach (var line in diverged.Take(20)) report.Add("  " + line);

        // Член без записи в словаре: в окне человек увидит имя ключа. Это и есть дефект v1.
        var withoutEntry = known.Where(name => !russian.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        report.Add($"членов PanelStrings без записи в русском словаре: {withoutEntry.Count}");
        foreach (var name in withoutEntry.Take(40)) report.Add("  нет записи: " + name);

        // Запись без члена: ключ остался от прежней строки и живёт сам по себе.
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var withoutMember = russian.Keys.Where(key => !knownSet.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();
        report.Add($"записей словаря без члена PanelStrings: {withoutMember.Count}");
        foreach (var key in withoutMember.Take(40)) report.Add("  лишняя запись: " + key);

        failed = failed
                 || gaps.Values.Any(missing => missing.Count > 0)
                 || diverged.Count > 0
                 || withoutEntry.Count > 0
                 || withoutMember.Count > 0;

        // Английский словарь — тот, на который падает панель, если строки нет в выбранном языке.
        // Пустой он или нет, видно по счётчику: без него «перевода нет» и «словаря нет» —
        // разные состояния, и человеку важно знать, какое именно.
        report.Add($"английский словарь: строк {Loc.Dictionary("en").Count}");
        report.Add($"китайский словарь: строк {Loc.Dictionary("zh").Count}");

        foreach (var line in report) Console.WriteLine("ЯЗЫК| " + line);
        Console.WriteLine(failed ? "ЯЗЫК ПРОВАЛ" : "ЯЗЫК УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Члены <c>PanelStrings</c> — по одному на ключ. Отражение, а не переписанный список:
    /// переписанный однажды разошёлся бы с типом, и новый ключ объявили бы «лишним».
    /// </summary>
    public static List<string> Members() =>
        typeof(PanelStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static string Describe(IReadOnlyList<int> placeholders) =>
        placeholders.Count == 0 ? "нет подстановок" : "{" + string.Join("}{", placeholders) + "}";
}
