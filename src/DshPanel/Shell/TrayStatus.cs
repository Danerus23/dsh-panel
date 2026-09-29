using System.Globalization;
using DshPanel.Server;

namespace DshPanel.Shell;

/// <summary>
/// Тон строки состояния — то, что человек видит ЦВЕТОМ: зелёный, янтарный, красный, серый.
///
/// Отдельным перечислением, а не цветом: решение «эта строка хорошая или плохая» принимается
/// здесь и только здесь, а <see cref="Tray.TrayPalette"/> переводит его в точки на экране.
/// Так цвет можно проверить перебором состояний, не поднимая ни меню, ни значка, ни рабочего
/// стола владельца, — и так же отдельно проверяется, что цвета читаются на фоне меню.
///
/// <see cref="Neutral"/> — НЕ «плохо» и не «хорошо», а «панель не знает»: так выглядит строка,
/// у которой сервера нет вовсе (окно открыто без контроллера, прогон проверки). Смешать это
/// с «не запущен» значит сказать человеку неправду серым цветом.
/// </summary>
public enum TrayTone
{
    /// <summary>Панель не знает состояния (серый).</summary>
    Neutral,

    /// <summary>Всё хорошо (зелёный).</summary>
    Good,

    /// <summary>Внимание: не сломано, но и не хорошо (янтарный).</summary>
    Warning,

    /// <summary>Плохо: не работает (красный).</summary>
    Bad,
}

/// <summary>Строка меню значка: готовый текст и его тон.</summary>
public sealed record TrayStatusLine(string Text, TrayTone Tone);

/// <summary>
/// ЧЕТЫРЕ строки состояния, которые человек видит в меню значка: сервер, агент с балансом, тариф
/// и обновление.
///
/// Отдельной ЧИСТОЙ функцией — по той же причине, что у <see cref="TrayTooltip"/>: это
/// единственное место, где состояние попадает в меню, и оно обязано проверяться перебором
/// состояний, а не «один раз посмотрели». Строки собираются из готовых значений: меню не знает
/// ни про контроллер сервера, ни про баланс, ни про обновление — оно только показывает то,
/// что ему дали.
///
/// **Строки берутся из словаря, а не собираются склейкой.** Слово «Сервер:» — часть строки
/// словаря, потому что в китайском порядок слов и двоеточие другие, и склейка «слово + значение»
/// переводится только наполовину (то же правило, что у подсказки значка).
///
/// **Состояний сервера пять, и они говорят разное.** «Не подключён к панели» (окно открыто без
/// сервера — так его видят тесты и съёмка), «не запущен» (порт свободен), «занят чужой программой»
/// (на порту кто-то есть, но это не DSH), «работает… поднят панелью» и «работает… поднят не
/// панелью». Последние два различаются нарочно: это ровно тот случай, ради которого делалось
/// встраивание, — человек должен видеть, чей это сервер, не открывая окно.
///
/// ⚠️ **Строка обновления приходит ГОТОВОЙ** (<see cref="TrayStatusLine"/>): её решение живёт
/// у контроллера обновления (<c>UpdateController.TrayLine</c>) — там, где лежит состояние
/// проверки. Здесь из неё ничего не выводится, иначе решений о версиях стало бы два.
/// </summary>
public sealed record TrayStatusLines(
    TrayStatusLine Server,
    TrayStatusLine Agent,
    TrayStatusLine Peak,
    TrayStatusLine Update);

public static class TrayStatus
{
    /// <summary>
    /// Собрать четыре строки. <paramref name="connected"/> — знает ли панель сервер ВООБЩЕ
    /// (в проверках и съёмке контроллера нет, и «нет сервера» обязано отличаться от «сервер
    /// не запущен»: умолчание <see cref="ServerPresence"/> — как раз «остановлен», и различить
    /// их по одному перечислению нельзя).
    /// </summary>
    /// <param name="balanceSummary">
    /// Готовая сумма баланса или пусто, если её нет. Пусто — не пустое место в меню, а слова
    /// «нет данных»: строка, у которой пропало значение, выглядит как сломанная панель.
    /// </param>
    /// <param name="inPeak">
    /// Идёт ли пик СЕЙЧАС. Тон тарифа берётся отсюда, а не из готовой надписи: цвет обязан
    /// следовать факту, а не разбору переведённой строки на «свой» и «чужой» (разбор сломался бы
    /// на первом же языке, где две формулировки совпали).
    /// </param>
    /// <param name="update">
    /// Готовая строка обновления (текст и тон) — от контроллера обновления. Выдумывать её здесь
    /// нельзя: «проверено 28.09.2026» — это факт о проверке, а не о сервере.
    /// </param>
    public static TrayStatusLines Build(
        bool connected,
        ServerPresence presence,
        ServerOwner owner,
        int port,
        string agentTitle,
        string balanceSummary,
        bool inPeak,
        TrayStatusLine update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var culture = CultureInfo.CurrentCulture;

        var server = !connected
            ? PanelStrings.TrayStatusServerUnbound
            : presence switch
            {
                // Чужой работающий DSH называется чужим прямо здесь: иначе строка «работает»
                // читалась бы как «работает наш», а панель им не управляет.
                ServerPresence.Running when owner == ServerOwner.None => string.Format(
                    culture, PanelStrings.TrayStatusServerForeignFormat, port),
                ServerPresence.Running => string.Format(
                    culture, PanelStrings.TrayStatusServerRunningFormat, port),
                ServerPresence.BusyByOther => PanelStrings.TrayStatusServerBusy,
                _ => PanelStrings.TrayStatusServerStopped,
            };

        var agent = string.Format(
            culture,
            PanelStrings.TrayStatusAgentFormat,
            string.IsNullOrWhiteSpace(agentTitle) ? PanelStrings.TrayStatusNoAgent : agentTitle.Trim(),
            string.IsNullOrWhiteSpace(balanceSummary) ? PanelStrings.TrayStatusNoBalance : balanceSummary.Trim());

        // Тариф — готовой строкой окна («Пик: полная цена» / «Вне пика: вдвое дешевле»):
        // в меню она читается сама по себе, и второй её формулировки проекту не нужно.
        var peak = inPeak ? PanelStrings.PeakInPeak : PanelStrings.PeakOffPeak;

        return new TrayStatusLines(
            new TrayStatusLine(server, ServerTone(connected, presence)),
            new TrayStatusLine(agent, AgentTone(agentTitle, balanceSummary)),
            new TrayStatusLine(peak, PeakTone(inPeak)),
            update);
    }

    /// <summary>
    /// Тон строки сервера. Решение владельца 27.09.2026: работает — зелёный, не работает
    /// (остановлен или порт занят чужой программой) — красный, панель не знает — серый.
    ///
    /// **Работающий ЧУЖОЙ сервер — тоже зелёный**, и это осознанный выбор. Красный здесь значил бы
    /// «DSH не работает», а он работает; чей он — сказано словами в самой строке («поднят не
    /// панелью»), и второй раз пугать цветом незачем. Янтарный был бы третьим ответом на вопрос,
    /// у которого два ответа: работает или нет.
    /// </summary>
    public static TrayTone ServerTone(bool connected, ServerPresence presence) =>
        !connected
            ? TrayTone.Neutral
            : presence == ServerPresence.Running ? TrayTone.Good : TrayTone.Bad;

    /// <summary>
    /// Тон строки агента. Баланс есть — зелёный; баланса нет или агент не выбран — янтарный.
    /// Это «внимание», а не «авария»: панель не сломана, она просто ещё не знает суммы.
    /// </summary>
    public static TrayTone AgentTone(string agentTitle, string balanceSummary) =>
        string.IsNullOrWhiteSpace(agentTitle) || string.IsNullOrWhiteSpace(balanceSummary)
            ? TrayTone.Warning
            : TrayTone.Good;

    /// <summary>Тон строки тарифа: пик — янтарный (дороже), вне пика — зелёный.</summary>
    public static TrayTone PeakTone(bool inPeak) => inPeak ? TrayTone.Warning : TrayTone.Good;

    /// <summary>
    /// Тон ОГОНЬКА на значке в трее — тот же язык, что у строки сервера: зелёный — сервер отвечает,
    /// красный — не работает (остановлен или порт занят чужим), серый — панель не знает
    /// (нет сервера вовсе: окно без контроллера, прогон проверки).
    ///
    /// Отдельной функцией, а не «возьмём тон строки сервера»: обещание у значка своё (человек
    /// смотрит на рисунок, а не на меню), и проверка обязана называть его своими словами. Сегодня
    /// ответы совпадают, и это совпадение проверяется перебором, а не подразумевается.
    /// </summary>
    public static TrayTone IconTone(bool connected, ServerPresence presence) =>
        !connected
            ? TrayTone.Neutral
            : presence == ServerPresence.Running ? TrayTone.Good : TrayTone.Bad;
}
