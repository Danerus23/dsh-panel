using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// Что окно панели показывает про сервер и что разрешает нажимать.
///
/// Отдельно от окна и без единой ссылки на Avalonia — намеренно: именно это и проверяется
/// тестами без экрана. Тексты и решения о кнопках перебираются по состояниям, а окно остаётся
/// тонким: подставить строки да нажать-отпустить кнопки.
///
/// Здесь же живёт предложение встроиться в найденный сервер: панель обязана ПОКАЗАТЬ его
/// человеку и спросить, а не встраиваться молча (решение владельца 24.09.2026).
/// </summary>
public sealed class ServerPanelModel
{
    private readonly IServerControl? _server;
    private readonly DiscoveryResult _discovery;

    public ServerPanelModel(IServerControl? server = null, DiscoveryResult discovery = default)
    {
        _server = server;
        _discovery = discovery;
    }

    public ServerState State => _server?.State ?? default;

    /// <summary>Кто поднял сервер, которым управляет панель.</summary>
    public ServerOwner Owner => _server?.Owner ?? ServerOwner.None;

    /// <summary>Что нашла разведка — для проверок и для окна.</summary>
    public DiscoveryResult Discovery => _discovery;

    /// <summary>Крупная строка состояния.</summary>
    public string StatusText => _server is null
        ? PanelStrings.ServerUnbound
        : _server.State.Presence switch
        {
            ServerPresence.Running => PanelStrings.ServerRunning,
            ServerPresence.BusyByOther => PanelStrings.ServerForeign,
            _ => PanelStrings.ServerStopped,
        };

    /// <summary>Пояснение под строкой состояния: порт, процесс или причина отказа.</summary>
    public string DetailText => _server is null
        ? PanelStrings.ServerUnboundDetail
        : _server.State.Detail;

    /// <summary>
    /// Сервер, которого панель предлагает взять под управление. <c>null</c> — предлагать нечего:
    /// либо ничего не нашли, либо панель уже чем-то управляет.
    /// </summary>
    public FoundServer? Candidate =>
        ServerDecisions.CanAdopt(Owner, _discovery.Any) ? _discovery.First : null;

    /// <summary>
    /// Строка про найденный чужой сервер. Пустая — говорить нечего.
    ///
    /// Отдельно сказано, когда порты посмотреть НЕ удалось: «ничего не нашли» и «не смотрели» —
    /// разные утверждения, и второе нельзя показывать первым.
    /// </summary>
    public string FoundText
    {
        get
        {
            if (_server is null) return string.Empty;
            if (Owner != ServerOwner.None) return string.Empty;

            if (!_discovery.TableReadable) return PanelStrings.FoundTableUnreadable;

            var candidate = Candidate;
            if (candidate is null) return string.Empty;

            var text = string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.FoundServerFormat,
                candidate.Value.Port,
                candidate.Value.Pid);

            return candidate.Value.IsOwnerPort
                ? text + PanelStrings.FoundServerOwnerPortSuffix
                : text;
        }
    }

    /// <summary>Предлагать ли встроиться: панель ничем не управляет, а сервер найден.</summary>
    public bool CanAdopt => Candidate is not null;

    public string AdoptButtonText => PanelStrings.AdoptButton;

    /// <summary>Отвязаться можно только от того, во что панель встроилась.</summary>
    public bool CanDetach => _server is not null && Owner == ServerOwner.Adopted;

    public string DetachButtonText => PanelStrings.DetachButton;

    public bool CanStart => _server is not null && ServerDecisions.CanStart(_server.State.Presence);

    /// <summary>
    /// Кнопка «Перезапустить» доступна РОВНО тогда, когда доступна «Остановить».
    ///
    /// Это НЕ случайное совпадение и не «пока так вышло»: перезапуск — это «остановить» плюс
    /// «запустить», и права у него ТЕ ЖЕ САМЫЕ. Поэтому здесь спрашивается тот же предикат
    /// (<see cref="CanStop"/>), а не заводится второй такой же: второй предикат однажды разошёлся
    /// бы с первым, и кнопка разрешала бы то, чего «Остановить» не делает, — то есть гасила бы
    /// чужой сервер без подтверждения. Предохранитель при этом остаётся в КОНТРОЛЛЕРЕ
    /// (<see cref="IServerControl.Restart"/>): окно спрашивает, а не решает.
    /// </summary>
    public bool CanRestart => CanStop;

    /// <summary>
    /// Кнопка «Остановить» доступна. У встроенного сервера она ТОЖЕ доступна — но нажатие
    /// спросит подтверждение (<see cref="NeedsStopConfirmation"/>), если согласие не запомнено,
    /// а не погасит сразу.
    /// </summary>
    public bool CanStop => _server is not null
        && ServerDecisions.CanStop(Owner, _server.State.Presence, confirmed: true, ConsentRemembered);

    /// <summary>
    /// Согласие человека на этот сервер запомнено (панель помнит ответ «беру под управление»).
    /// </summary>
    public bool ConsentRemembered => _server?.ConsentRemembered ?? false;

    /// <summary>
    /// Спросить подтверждение перед гашением. Да — для встроенного сервера, и КАЖДЫЙ раз,
    /// через него может идти текущая работа человека. Но если человек уже ответил «беру под
    /// управление» и ответ запомнен (решение владельца 26.09.2026), спрашивать нечего: он
    /// ответил. Тогда кнопка гасит сразу — и подпись у неё без многоточия.
    /// </summary>
    public bool NeedsStopConfirmation => _server is not null
        && ServerDecisions.NeedsStopConfirmation(Owner, _server.State.Presence, ConsentRemembered);

    public string StartButtonText => PanelStrings.StartButton;

    // --- ссылка входа: кнопка «Открыть агента» -------------------------------
    //
    // ⚠️ САМА ССЫЛКА ЗДЕСЬ НЕ ПОКАЗЫВАЕТСЯ И НЕ ОТДАЁТСЯ: в ней токен входа, и она уходит
    // ровно в одно место — в браузер по щелчку человека (красная линия 7). Модель отвечает
    // только на вопрос «можно ли нажать» и «почему нельзя», и второе — словами.

    /// <summary>
    /// Ссылка входа известна. Значение при этом НЕ показывается: см. комментарий выше.
    /// </summary>
    private bool LinkKnown => (_server?.EntryLink.Length ?? 0) > 0;

    /// <summary>Сервер отвечает.</summary>
    private bool ServerAnswers => _server is not null && _server.State.Presence == ServerPresence.Running;

    /// <summary>
    /// Можно ли открыть РАБОТУ АГЕНТА в браузере: сервер отвечает И ссылка известна. Оба условия
    /// обязательны, и оба честные: без сервера открывать нечего, без ссылки — нечем.
    /// </summary>
    public bool CanOpenAgent => ServerAnswers && LinkKnown;

    public string OpenAgentButtonText => PanelStrings.OpenAgentButton;

    /// <summary>
    /// Подсказка под кнопкой. Недоступная кнопка обязана СКАЗАТЬ, почему она недоступна:
    /// «сервер не работает» и «ссылка ещё неизвестна» — разные положения дел, и человеку
    /// от них нужно разное.
    /// </summary>
    public string OpenAgentHint => !ServerAnswers
        ? PanelStrings.OpenAgentNoServerHint
        : LinkKnown
            ? PanelStrings.OpenAgentReadyHint
            : PanelStrings.OpenAgentNoLinkHint;

    /// <summary>
    /// Подпись кнопки остановки. Многоточие у встроенного сервера — не украшение: оно честно
    /// говорит, что кнопка сначала спросит, а не сделает. У сервера с запомненным согласием
    /// многоточия нет: спрашивать нечего, человек уже ответил.
    /// </summary>
    public string StopButtonText =>
        NeedsStopConfirmation ? PanelStrings.StopButtonAsks : PanelStrings.StopButton;

    /// <summary>
    /// Подпись кнопки перезапуска. Многоточие — по ТОМУ ЖЕ признаку, что у «Остановить»
    /// (<see cref="NeedsStopConfirmation"/>), и это не совпадение: перезапуск найденного сервера
    /// сначала спрашивает ровно то же подтверждение, а у сервера с запомненным согласием
    /// спрашивать нечего — человек уже ответил.
    ///
    /// ⚠️ Второго предиката подтверждения у перезапуска НЕТ намеренно: два предиката об одном
    /// и том же однажды разошлись бы, и кнопка обещала бы вопрос, которого не будет (или наоборот).
    /// </summary>
    public string RestartButtonText =>
        NeedsStopConfirmation ? PanelStrings.RestartButtonAsks : PanelStrings.RestartButton;
}
