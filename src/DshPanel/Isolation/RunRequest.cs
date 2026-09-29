namespace DshPanel.Isolation;

/// <summary>
/// Разобранная просьба о прогоне: обычный он или изолированный, и если изолированный — где корень.
///
/// Разбор отделён от применения намеренно: это чистый разбор аргументов и окружения, значит
/// его можно проверить тестами по одному случаю на каждый отказ, не поднимая приложение.
/// </summary>
public sealed record RunRequest
{
    /// <summary>Переменная окружения с корнем изолированного прогона.</summary>
    public const string RootVariable = "DSH_PANEL_RUN_ROOT";

    /// <summary>Имя ключа командной строки, без ведущих дефисов.</summary>
    public const string RootSwitch = "run-root";

    /// <summary>Ключ в том виде, в каком его пишут в командной строке.</summary>
    public static string RootSwitchArgument => "--" + RootSwitch;

    /// <summary>Ключ как признак изоляции: так же, как он написан в командной строке.</summary>
    public const string RootSwitchFact = "--" + RootSwitch;

    public bool IsIsolated { get; }

    /// <summary>Корень изолированного прогона. У настоящего — <c>null</c>.</summary>
    public string? Root { get; }

    /// <summary>
    /// Почему прогон сочтён изолированным — перечень фактов, которые ДЕЙСТВИТЕЛЬНО были,
    /// только на чтение. Нужен самопроверке: она обязана перебирать перечень, а не запомненный
    /// список, иначе новый способ изоляции останется вне проверки, и она будет зелёной.
    ///
    /// ⚠️ Перечень обязан совпадать с тем, чем корень задан на самом деле: при корне из ключа
    /// называть переменную окружения — это ложь в отчёте прогона (найдено ревизией 26.09.2026).
    /// Потому имена источников лежат рядом с их разбором, а совпадение перечня с источниками
    /// проверяется отдельной проверкой.
    /// </summary>
    public IReadOnlyList<string> Facts { get; }

    private RunRequest(bool isolated, string? root, IReadOnlyList<string> facts)
    {
        IsIsolated = isolated;
        Root = root;
        Facts = facts;
    }

    /// <summary>Обычный прогон: панель запустил человек на своей машине.</summary>
    public static RunRequest Owner() => new(false, null, NoFacts);

    private static readonly IReadOnlyList<string> NoFacts = Array.AsReadOnly(Array.Empty<string>());

    private static readonly IReadOnlyList<string> SwitchOnlyFacts =
        Array.AsReadOnly(new[] { RootSwitchFact });

    private static readonly IReadOnlyList<string> VariableOnlyFacts =
        Array.AsReadOnly(new[] { RootVariable });

    private static readonly IReadOnlyList<string> BothSourcesFacts =
        Array.AsReadOnly(new[] { RootSwitchFact, RootVariable });

    /// <summary>
    /// Тот же список аргументов, но без пары «ключ корня + путь».
    ///
    /// Нужен потому, что режимы работы разбираются по первому своему аргументу, а ключ корня
    /// может стоять где угодно. Без этой зачистки «корень, затем проба изоляции» не опознавался
    /// бы как проба и падал бы отказом «не сказано, что делать» — проверено на собранном exe.
    /// Разбор ключа живёт здесь же, чтобы не появилось второго его понимания.
    /// </summary>
    public static string[] WithoutRootSwitch(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var kept = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], RootSwitchArgument, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(args[i]);
                continue;
            }

            i++; // пропускаем и значение ключа
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Разбирает аргументы командной строки и окружение.
    ///
    /// Два источника корня нужны оба. Ключ — потому что изоляцию просят явно и её должно быть
    /// видно в командной строке процесса. Переменная — потому что панель перезапускает себя
    /// и запускает дочерние процессы, и они обязаны унаследовать изоляцию, а не потерять её.
    ///
    /// Все отказы здесь громкие. Молчаливого «непонятно, значит обычный прогон» быть не должно:
    /// именно так прогон проверки и уходит по данным владельца.
    /// </summary>
    public static RunRequest Parse(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var fromArgs = ReadSwitch(args);
        var fromEnv = environment(RootVariable);
        if (string.IsNullOrWhiteSpace(fromEnv)) fromEnv = null;

        if (fromArgs is not null && fromEnv is not null && !SamePath(fromArgs, fromEnv))
            throw new ArgumentException(
                $"Корень изолированного прогона задан дважды и по-разному: ключом «{fromArgs}» " +
                $"и переменной {RootVariable} «{fromEnv}». Какой из них настоящий — неизвестно, " +
                "а угадывать в вопросе изоляции нельзя.");

        var root = fromArgs ?? fromEnv;
        if (root is null) return Owner();

        // Перечень называет КАЖДЫЙ источник, который корень задал. Оба сразу — тоже законно,
        // если путь один и тот же (разные пути отвергнуты выше): тогда признак изоляции переживёт
        // потерю любого из двух, и это стоит назвать, а не выбирать один «главный».
        var facts =
            fromArgs is not null && fromEnv is not null ? BothSourcesFacts :
            fromArgs is not null ? SwitchOnlyFacts :
            VariableOnlyFacts;

        return new RunRequest(true, Check(root), facts);
    }

    private static string? ReadSwitch(string[] args)
    {
        string? found = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], RootSwitchArgument, StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Ключ «{RootSwitchArgument}» указан без пути к корню.");

            var value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
                throw new ArgumentException(
                    $"Ключ «{RootSwitchArgument}» указан без пути к корню (следом идёт «{value}»).");

            if (found is not null)
                throw new ArgumentException($"Ключ «{RootSwitchArgument}» указан дважды.");

            found = value;
            i++;
        }

        return found;
    }

    /// <summary>
    /// Отказы, без которых изоляция становится самообманом.
    ///
    /// Первый и главный: корнем нельзя объявить настоящий корень панели или что-то внутри него.
    /// Иначе прогон называется изолированным, а пишет владельцу в его же каталоги — ровно так
    /// в v1 «изолированный» накат погасил рабочий сервер владельца. Второй: корень не должен
    /// содержать настоящий — это признак путаницы, и разбираться с ней надо до запуска,
    /// а не после.
    /// </summary>
    private static string Check(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException(
                $"Корень изолированного прогона обязан быть полным путём, а не «{root}»: " +
                "относительный корень зависит от рабочего каталога процесса и уводит прогон неизвестно куда.");

        var isolated = Normalize(root);
        var owner = Normalize(AppPaths.ForOwner().Root);

        if (same(isolated, owner))
            throw new ArgumentException(
                $"Корень изолированного прогона совпал с настоящим корнем панели «{owner}». " +
                "Это не изоляция, а запись владельцу в его же каталоги.");

        if (inside(isolated, owner))
            throw new ArgumentException(
                $"Корень изолированного прогона «{isolated}» лежит ВНУТРИ настоящего корня «{owner}». " +
                "Копирование и очистка такого корня задели бы данные владельца.");

        if (inside(owner, isolated))
            throw new ArgumentException(
                $"Корень изолированного прогона «{isolated}» СОДЕРЖИТ настоящий корень «{owner}». " +
                "Значит настоящие данные лежат внутри того, что объявлено изолированным.");

        return isolated;

        static bool same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool inside(string child, string parent) =>
            child.StartsWith(
                Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
