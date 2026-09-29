using DshPanel.Server;

namespace DshPanel.Headless;

/// <summary>Ответ разведки: что на порту и (если нашлось) чей это процесс.</summary>
public readonly record struct HeadlessServerFacts(ServerAnswer Answer, int Port, int Pid);

/// <summary>
/// РАЗВЕДКА СЕРВЕРА для режимов без окна — один вопрос и **только чтение**: отвечает ли на порту
/// этого прогона сервер DSH. Ни гашения, ни остановки, ни записи: режим, который умеет снять копию
/// без окна, не имеет права гасить чужой сервер (красная линия 6 — гасит только человек: свой
/// сервер по согласию, найденный и взятый под управление — по отдельному подтверждению каждый раз).
///
/// Отвечает та же разведка, что и у окна (<see cref="ServerDiscovery"/>): таблица портов, образ
/// процесса, командная строка и отпечаток `401 dsh web authentication required`. Своего способа
/// опознать сервер в панели нет и быть не должно — иначе однажды «свой» и «чужой» разойдутся.
///
/// ⚠️ **Разведка СУЖЕНА до порта этого прогона**, и это решение, а не экономия времени. Порт берётся
/// из настроек (`serverPort` — тот же, на котором панель поднимает СВОЙ сервер), потому что
/// спрашивается ровно одно: работает ли движок, который держит **данные этого прогона**. Чужие
/// серверы машины (например живой DSH владельца на 3080) к этим данным отношения не имеют: накат
/// в изолированный корень их не касается. Спрашивай мы про все порты — `--restore` отказывал бы
/// на всякой машине, где у человека что-то запущено, то есть режим не работал бы никогда.
///
/// ⚠️ **Чего разведка не умеет — и это названо, а не спрятано:** по порту нельзя узнать домашний
/// каталог движка (`DSH_HOME`) найденного процесса: он лежит в его окружении, а не в командной
/// строке. Поэтому «на этом порту работает сервер DSH» — утверждение о ПОРТЕ, а не о том, что
/// это сервер именно этого прогона.
/// </summary>
public static class HeadlessServer
{
    /// <summary>
    /// Что на порту. <paramref name="port"/> ≤ 0 — порта нет вовсе (так выглядит неразобранная
    /// настройка), и тогда ответ «не отвечает»: спрашивать нечего.
    /// </summary>
    public static HeadlessServerFacts Read(ServerProbe probe, int port)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (port <= 0) return new HeadlessServerFacts(ServerAnswer.Idle, 0, 0);

        // Дверь разведки сужается ЗДЕСЬ: дальше всю работу делает ServerDiscovery — и приметы
        // владения, и отпечаток, — а нам остаётся один порт вместо всех слушающих программ машины.
        var narrowed = probe with
        {
            Listeners = () => Only(probe.Listeners(), port),
        };

        var found = ServerDiscovery.Find(narrowed);

        // «Посмотреть не удалось» — отдельный ответ, а не «не работает»: молчание таблицы портов
        // это не молчание сервера.
        if (!found.TableReadable) return new HeadlessServerFacts(ServerAnswer.Unknown, port, 0);

        var first = found.First;
        return first is null
            ? new HeadlessServerFacts(ServerAnswer.Idle, port, 0)
            : new HeadlessServerFacts(ServerAnswer.Working, first.Value.Port, first.Value.Pid);
    }

    /// <summary>Слушатели ровно этого порта. Отбор здесь, потому что дверь подменяется целиком.</summary>
    private static IReadOnlyList<PortTable.Listener>? Only(IReadOnlyList<PortTable.Listener>? listeners, int port) =>
        listeners?.Where(listener => listener.Port == port).ToArray();
}
