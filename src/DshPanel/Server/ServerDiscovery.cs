using System.Net.Http;

namespace DshPanel.Server;

/// <summary>
/// Всё, что контроллер сервера спрашивает у СИСТЕМЫ: кто слушает порт, как выглядит командная
/// строка чужого процесса, жив ли он, отвечает ли порт отпечатком.
///
/// Одна дверь — не украшение, а условие проверяемости. Без неё нельзя доказать главное правило
/// шага: «без подтверждения встроенный сервер не гасится». С настоящим опросом такая проверка
/// либо искала бы на машине настоящий DSH (и зависела бы от того, что там сейчас запущено),
/// либо гасила бы живой процесс.
///
/// Приметы владения при этом проверяются на НАСТОЯЩИХ командных строках: они записаны
/// в тестах дословно (см. <c>DiscoveryTests</c>), а подменяется только источник фактов.
/// </summary>
public sealed record ServerProbe(
    Func<int, int> ListenerPid,
    Func<IReadOnlyList<PortTable.Listener>?> Listeners,
    Func<int, string> CommandLine,
    Func<int, string> ProcessName,
    Func<int, long> StartTicks,
    Func<int, bool> AnswersFingerprint)
{
    /// <summary>Настоящие источники: таблица портов, сведения о процессе и HTTP-отпечаток.</summary>
    public static ServerProbe System() => new(
        PortTable.GetListenerPid,
        PortTable.ListListeners,
        ProcessFacts.CommandLine,
        ProcessFacts.Name,
        ProcessFacts.StartTicks,
        ServerDiscovery.AnswersFingerprint);
}

/// <summary>Что нашла разведка. Пустой список и «посмотреть не удалось» — РАЗНЫЕ ответы.</summary>
public readonly record struct DiscoveryResult(bool TableReadable, IReadOnlyList<FoundServer> Found)
{
    public static DiscoveryResult Unreadable => new(false, Array.Empty<FoundServer>());

    public static DiscoveryResult Nothing => new(true, Array.Empty<FoundServer>());

    /// <summary>
    /// Есть ли находка. Через образец, а не через <c>Found.Count</c>: у значения по умолчанию
    /// (<c>default(DiscoveryResult)</c>, а именно так строится модель окна без разведки) список
    /// равен <c>null</c>. Обнаружено тестом: окно падало при первом же обращении к предложению
    /// встроиться — то есть в самом обычном случае «разведки ещё не было».
    /// </summary>
    public bool Any => Found is { Count: > 0 };

    /// <summary>Первая находка (порядок задаёт сама разведка) или <c>null</c>.</summary>
    public FoundServer? First => Found is { Count: > 0 } list ? list[0] : null;

    public int Count => Found?.Count ?? 0;
}

/// <summary>
/// Разведка: найти работающий сервер DSH, который панель НЕ поднимала.
///
/// Это первый шаг встраивания (решение владельца 24.09.2026, порядок — после этапа 3): панель
/// обязана обнаружить уже запущенный сервер, показать его человеку и спросить, встроиться ли.
/// Главный случай здесь — порт владельца 3080, на котором стоит его живой DSH: для разведки
/// это НЕ запрет (запрещён только ЗАПУСК на этом порту, <see cref="ServerDecisions.IsAllowedPort"/>),
/// а самый ожидаемый ответ.
///
/// Порядок проверок выбран по цене, и это существенно: сначала таблица портов (один вызов),
/// потом командная строка процесса (быстро, без сети) и только в самом конце HTTP-отпечаток —
/// и лишь у тех процессов, чья командная строка уже похожа на движок DSH. Спрашивать отпечаток
/// у всех слушающих программ было бы и медленно, и грубо.
/// </summary>
public static class ServerDiscovery
{
    /// <summary>
    /// Найти все серверы DSH среди слушающих портов.
    /// </summary>
    /// <param name="excludePids">
    /// Процессы, которые панель не предлагает: свой собственный сервер (она за него и так
    /// отвечает) и сервер, в который она уже встроена. Без этого панель предлагала бы
    /// встроиться в то, чем уже управляет.
    /// </param>
    public static DiscoveryResult Find(ServerProbe probe, IReadOnlySet<int>? excludePids = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var listeners = probe.Listeners();
        if (listeners is null) return DiscoveryResult.Unreadable;

        var found = new List<FoundServer>();

        foreach (var listener in listeners)
        {
            if (listener.Pid <= 0 || listener.Port <= 0) continue;
            if (excludePids is not null && excludePids.Contains(listener.Pid)) continue;

            var commandLine = probe.CommandLine(listener.Pid);
            var name = probe.ProcessName(listener.Pid);

            // Сначала — то, что не стоит сети. Так отсеиваются все чужие программы машины.
            if (!ProcessFacts.IsNodeImage(name, commandLine)) continue;
            if (!ServerDecisions.LooksLikeDshCommandLine(commandLine, listener.Port)) continue;

            // И только теперь — решающий признак: отпечаток сервера DSH на этом порту.
            var facts = new DshProcessFacts(
                ListenerAlive: true,
                IsNode: true,
                CommandLine: commandLine,
                FingerprintMatched: probe.AnswersFingerprint(listener.Port));

            if (!ServerDecisions.LooksLikeDshProcess(facts, listener.Port)) continue;

            found.Add(new FoundServer(listener.Port, listener.Pid, name));
        }

        // Порядок по порту — чтобы предложение не зависело от порядка строк в таблице портов:
        // «предложили то, что нашлось первым» — это не решение, а случайность.
        found.Sort((left, right) => left.Port.CompareTo(right.Port));
        return new DiscoveryResult(true, found);
    }

    /// <summary>
    /// Что ответил порт по ГОТОВОЙ ссылке. Единственное место, где это измеряется сетью; решение
    /// «годится ли» принимает чистая функция <see cref="EntryLinkDecisions.Accepts"/>.
    ///
    /// **Редиректы НЕ разворачиваются** (<c>AllowAutoRedirect = false</c>) намеренно: живой сервер
    /// отвечает на ссылку с токеном кодом <b>303 SeeOther</b>, и разверни мы его — проверка уехала бы
    /// по адресу из <c>Location</c>, то есть «годность» зависела бы от чужого ответа. Замер
    /// на работающем сервере владельца 27.09.2026.
    ///
    /// Cookie читается из заголовков, а тело не скачивается вовсе (<c>ResponseHeadersRead</c>):
    /// панели нужен только факт «токен принят», а не страница входа.
    /// </summary>
    public static LinkProbe ProbeLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return LinkProbe.Failed2;

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

            using var response = client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();

            var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
                            && values.Any(value => !string.IsNullOrWhiteSpace(value));

            return LinkProbe.Answer((int)response.StatusCode, setCookie);
        }
        catch
        {
            // Не ответил — значит и открывать нечего: молчание это не «работает».
            return LinkProbe.Failed2;
        }
    }

    /// <summary>
    /// Ответил ли на порту сервер DSH. Единственный признак владения, который у нас есть,
    /// и он же — то, по чему панель отличает свой сервер от чужой программы на порту.
    ///
    /// Ошибка сети означает «не ответил», а не «наверное, наш»: молчание — не подтверждение.
    /// </summary>
    public static bool AnswersFingerprint(int port)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

            using var response = client
                .GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseContentRead)
                .GetAwaiter().GetResult();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ServerDecisions.LooksLikeDshWeb((int)response.StatusCode, body);
        }
        catch
        {
            // Не ответил — значит и не сервер: молчание это не «работает».
            return false;
        }
    }
}
