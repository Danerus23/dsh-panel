namespace DshPanel.Isolation;

/// <summary>
/// Контекст прогона: единственное место, где сказано, изолирован ли этот запуск,
/// и единственный источник путей для всего остального кода.
///
/// Почему так, а не «флаг в статическом поле где-нибудь». В v1 признак изоляции был общим
/// на всю панель, и это правильно; но рядом с ним жило глобальное изменяемое состояние,
/// и перенос домена в v2 упирался в него как в одну из семи помех. Здесь состояние тоже
/// одно на всю панель — иначе признак перестанет быть общим, — но оно УСТАНАВЛИВАЕТСЯ ОДИН РАЗ
/// и после этого только читается. Установить второй контекст нельзя: попытка — ошибка,
/// а не тихая замена.
///
/// <see cref="Current"/> НЕ имеет запасного значения. Это главное свойство файла. Код,
/// который забыл про изоляцию, обязан упасть с внятной ошибкой, а не пойти по данным
/// владельца: молчаливый запасной путь — это ровно тот механизм, которым в v1 изоляция
/// и обходилась.
/// </summary>
public sealed class RunContext
{
    private static RunContext? _installed;

    /// <summary>ЕДИНСТВЕННЫЙ признак «это не прогон владельца». Другого определения в панели быть не должно.</summary>
    public bool IsIsolated { get; }

    /// <summary>Все пути панели. Получать их можно только отсюда.</summary>
    public AppPaths Paths { get; }

    /// <summary>Почему прогон сочтён изолированным — только на чтение.</summary>
    public IReadOnlyList<string> IsolationFacts { get; }

    private RunContext(bool isolated, AppPaths paths, IReadOnlyList<string> facts)
    {
        IsIsolated = isolated;
        Paths = paths;
        IsolationFacts = facts;
    }

    /// <summary>
    /// Собирает контекст по разобранной просьбе. Чистая сборка: ничего никуда не устанавливает,
    /// поэтому её можно свободно звать из тестов.
    /// </summary>
    public static RunContext Create(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RunContext(
            isolated: request.IsIsolated,
            paths: request.IsIsolated ? AppPaths.Under(request.Root!) : AppPaths.ForOwner(),
            facts: request.Facts);
    }

    /// <summary>Контекст этого процесса. Без установленного контекста — исключение, а не «настоящий прогон».</summary>
    public static RunContext Current => _installed ?? throw new InvalidOperationException(
        "Контекст прогона не установлен. Запасного пути к настоящим каталогам нет намеренно: " +
        "код, забывший про изоляцию, обязан упасть здесь, а не пойти по данным владельца.");

    /// <summary>Установить контекст. Ровно один раз за процесс.</summary>
    public static void Install(RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_installed is not null)
            throw new InvalidOperationException(
                "Контекст прогона уже установлен. Второго быть не может: иначе признак изоляции " +
                "перестал бы быть общим для всей панели, и часть кода решала бы по-своему.");

        _installed = context;
    }
}
