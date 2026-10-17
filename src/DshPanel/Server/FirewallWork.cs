namespace DshPanel.Server;

/// <summary>Что панель собирается сделать с правилом брандмауэра.</summary>
public enum FirewallAction
{
    /// <summary>Поставить узкое правило на порт калитки.</summary>
    Add,

    /// <summary>Убрать своё правило.</summary>
    Remove,
}

/// <summary>
/// ИЗМЕНЕНИЕ ПРАВИЛА С ПРОВЕРКОЙ: СНАЧАЛА ДЕЛАЕМ, ПОТОМ ЧИТАЕМ (этап F4, кусок 1).
///
/// **Зачем отдельно от двери.** Дверь (<see cref="IFirewallDoor"/>) умеет ровно две вещи: прочитать
/// состояние и попросить машину измениться. Но «код возврата <c>0</c>» — это ещё не «правило
/// стоит»: <c>netsh</c> мог отработать, а правило не встать; снятие могло уйти в никуда. Поэтому
/// успех подтверждается ЧТЕНИЕМ, и это правило живёт здесь — то есть его можно проверить, подставив
/// дверь с подставной машиной, ни разу не тронув настоящий брандмауэр.
///
/// ⚠️ **Что считается успехом, и это не «мягко»:**
///
/// * постановка — только <see cref="FirewallPresence.Ready"/>: правило с нашим именем есть, оно
///   включённое, входящее, для частного профиля, и в нём НАШ порт и НАШ путь. Правило «с нашим
///   именем, но не такое» успехом не объявляется: сказать «готово» о непроверенном — это ровно
///   то, за что панель уже платила (проверка, которая не может упасть, ничего не стоит);
/// * снятие — только <see cref="FirewallPresence.Absent"/>: правила с нашим именем больше нет.
///   ⚠️ Ради этого проверка и нужна: она отвечает на вопрос владельца «снятие проверяет, что ушло
///   именно наше» — убираем МЫ ВСЕГДА по имени панели (<see cref="FirewallDecisions.Remove"/>),
///   а после уборки убеждаемся, что имени больше нет.
///
/// Отказ человека в подтверждении Windows и любая беда двери возвращаются КАК ЕСТЬ: там слова уже
/// сказаны, и второй раз их собирать значило бы разойтись с ними.
/// </summary>
public static class FirewallWork
{
    public static FirewallChange Run(IFirewallDoor door, FirewallAction action, int port)
    {
        ArgumentNullException.ThrowIfNull(door);

        var change = action == FirewallAction.Add ? door.Add(port) : door.Remove();

        if (!change.Done) return change;

        var state = door.Read(port);

        if (action == FirewallAction.Add)
        {
            return state.Presence == FirewallPresence.Ready
                ? FirewallChange.Yes(FirewallDecisions.DoneAdd(port))
                : FirewallChange.No(FirewallDecisions.Failed(state.Words));
        }

        return state.Presence == FirewallPresence.Absent
            ? FirewallChange.Yes(FirewallDecisions.DoneRemove())
            : FirewallChange.No(FirewallDecisions.NotRemoved());
    }
}
