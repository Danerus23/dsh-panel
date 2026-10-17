namespace DshPanel.Server;

/// <summary>Вид расхождения установленного дерева с проверенным набором.</summary>
public enum EngineTreeProblemKind
{
    /// <summary>Пакет из замка в дереве НЕ ЛЕЖИТ.</summary>
    Missing,

    /// <summary>Пакет лежит, но ВЕРСИЯ другая — это другое дерево, а не то, что проверяли.</summary>
    Version,

    /// <summary>В дереве есть пакет, которого в замке НЕТ.</summary>
    Extra,
}

/// <summary>Одно расхождение: что не так, с чем и что нашли.</summary>
public sealed record EngineTreeProblem(EngineTreeProblemKind Kind, string Name, string Expected, string Actual);

/// <summary>
/// ЧЕМ КОНЧИЛАСЬ СВЕРКА УСТАНОВЛЕННОГО ДЕРЕВА С ЗАМКОМ.
///
/// ⚠️ <see cref="Checked"/> — не украшение: пустой замок дал бы «расхождений нет», и это читалось
/// бы как «всё сошлось», хотя сверять было не с чем. «Не с чем сверять» и «сошлось» — разные ответы.
/// </summary>
public sealed record EngineTreeReport(bool Checked, IReadOnlyList<EngineTreeProblem> Problems, int Skipped)
{
    public static EngineTreeReport Nothing(EngineTreeProblemKind kind) =>
        new(false, new[] { new EngineTreeProblem(kind, string.Empty, string.Empty, string.Empty) }, 0);

    /// <summary>Сверка прошла и расхождений нет.</summary>
    public bool Ok => Checked && Problems.Count == 0;

    public IReadOnlyList<EngineTreeProblem> Missing =>
        Problems.Where(problem => problem.Kind == EngineTreeProblemKind.Missing).ToList();

    public IReadOnlyList<EngineTreeProblem> Versions =>
        Problems.Where(problem => problem.Kind == EngineTreeProblemKind.Version).ToList();

    public IReadOnlyList<EngineTreeProblem> Extra =>
        Problems.Where(problem => problem.Kind == EngineTreeProblemKind.Extra).ToList();

    /// <summary>Короткая строка для журнала: числа расхождений и сколько пакетов этой машине не нужны.</summary>
    public string Numbers =>
        $"нет пакетов {Missing.Count}, других версий {Versions.Count}, лишних {Extra.Count}, " +
        $"этой машине не нужны {Skipped}";
}

/// <summary>
/// СВЕРКА УСТАНОВЛЕННОГО ДЕРЕВА С ПРОВЕРЕННЫМ НАБОРОМ (решение владельца 10.10.2026: *«установленное
/// дерево сверяй с проверенным набором — пакеты и суммы, расхождение — отказ словами»*).
///
/// **Что сверяет панель, а что npm.** Суммы вложений (`integrity`) проверяет **npm при установке
/// по замку**: он отказывается ставить пакет, чья сумма не сошлась, и панель называет это отказом.
/// Панель сверяет **СОСТАВ и ВЕРСИИ** дерева после установки — то, что npm не перепроверяет, когда
/// установка уже прошла: остался ли каждый пакет из замка, той ли он версии и не появилось ли
/// лишнего. Разделение названо прямо, потому что «мы проверили суммы» было бы неправдой.
///
/// **Сверка идёт ПО ПУТЯМ, а не по именам.** В дереве npm один и тот же пакет лежит в разных
/// местах (`node_modules/a/node_modules/x` и `node_modules/x`), поэтому имя — не адрес: сверка по
/// именам объявила бы лишним законный вложенный пакет.
///
/// **Не всё, что лежит в замке, обязано лежать на диске** (замер 10.10.2026): из 588 путей
/// на Windows появляются 519 — 68 необязательных пакетов просят `darwin`/`linux` или `musl`/`glibc`,
/// а один помечен `extraneous` и не ставится вовсе. Такие записи считаются <see cref="EngineTreeReport.Skipped"/>,
/// а НЕ пропажей: иначе честная установка выглядела бы расхождением, и настоящая пропажа утонула бы
/// среди них.
///
/// **Лишний пакет — тоже расхождение.** Замок описывает дерево ЦЕЛИКОМ: пакет, которого в нём нет,
/// означает, что у человека стоит не то дерево, которое проверяли пробой умений.
/// </summary>
public static class EngineTreeCheck
{
    /// <summary>
    /// Сверить дерево с замком НА ЭТОЙ МАШИНЕ. Обращения к диску здесь НЕТ: пути пакетов и их
    /// версии приходят доводами — поэтому сверка проверяется подставными деревьями, без установки
    /// и без сети.
    /// </summary>
    /// <param name="expected">Проверенный набор.</param>
    /// <param name="installedPaths">Пути пакетов, которые лежат в установленном дереве.</param>
    /// <param name="installedVersionOf">Версия пакета по его пути из <c>package.json</c> (пусто — не прочитать).</param>
    public static EngineTreeReport Against(
        EngineTreeLock expected,
        IReadOnlyList<string> installedPaths,
        Func<string, string?> installedVersionOf) =>
        Against(expected, installedPaths, installedVersionOf, EnginePlatform.Os, EnginePlatform.Cpu, EnginePlatform.Libc);

    /// <summary>
    /// То же, но с ЯВНО названной машиной: `os`, `cpu` и `libc` приходят доводами, поэтому правило
    /// «что этой машине не нужно» проверяется и на чужой платформе — без чужой машины под рукой.
    /// </summary>
    public static EngineTreeReport Against(
        EngineTreeLock expected,
        IReadOnlyList<string> installedPaths,
        Func<string, string?> installedVersionOf,
        string os,
        string cpu,
        string libc)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(installedPaths);
        ArgumentNullException.ThrowIfNull(installedVersionOf);

        if (!expected.Any) return EngineTreeReport.Nothing(EngineTreeProblemKind.Missing);

        var installed = new HashSet<string>(installedPaths, StringComparer.OrdinalIgnoreCase);
        var problems = new List<EngineTreeProblem>();
        var skipped = 0;

        foreach (var entry in expected.Entries)
        {
            if (!entry.NeededHere(os, cpu, libc))
            {
                skipped++;
                continue;
            }

            if (!installed.Contains(entry.Path))
            {
                problems.Add(new EngineTreeProblem(
                    EngineTreeProblemKind.Missing, entry.Name, entry.Version, entry.Path));
                continue;
            }

            var actual = (installedVersionOf(entry.Path) ?? string.Empty).Trim();

            if (actual.Length == 0)
            {
                // Версию не прочитать — это НЕ «совпало»: молчание не подтверждение.
                problems.Add(new EngineTreeProblem(
                    EngineTreeProblemKind.Version, entry.Name, entry.Version, entry.Path));
                continue;
            }

            if (!string.Equals(actual, entry.Version, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new EngineTreeProblem(
                    EngineTreeProblemKind.Version, entry.Name, entry.Version, actual));
            }
        }

        var declared = expected.Entries
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in installedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (declared.Contains(path)) continue;

            problems.Add(new EngineTreeProblem(
                EngineTreeProblemKind.Extra, path, string.Empty, installedVersionOf(path)?.Trim() ?? string.Empty));
        }

        return new EngineTreeReport(true, problems, skipped);
    }
}
