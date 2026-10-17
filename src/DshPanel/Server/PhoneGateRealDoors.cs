using System.Globalization;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// НАСТОЯЩИЕ ДВЕРИ КАЛИТКИ: движок, его поток событий, файл пропуска, часы и журнал.
///
/// **Почему отдельным файлом.** Калитка — про наружную дверь (кого пускать, что ему можно),
/// а здесь — про то, КАК панель ходит в движок и в свой файл пропуска. Ровно так же разведены
/// копии, обновление и сервер: решение проверяется подставными дверями, а «как оно устроено
/// на самом деле» живёт здесь и проверяется живым прогоном.
///
/// **Три вещи, ради которых это одно место:**
///
/// 1. **Cookie движка — ОДНА на калитку.** Вход (`EngineRpcLogin.Exchange`) стоит одного запроса
///    к движку, и делать его на каждый вызов телефона незачем. Cookie живёт в замыкании, а после
///    отказа входа (`401`/`403` от движка) забывается — и следующий вызов входит заново. Так
///    перезапуск движка под работающей калиткой лечится сам, а не «перезапустите панель».
/// 2. **Токен входа никуда не уезжает.** Адрес движка берётся у ссылки входа ОДНОЙ дверью
///    (<see cref="EngineRpcLogin.BaseUrlOf"/>): она отрезает строку запроса, то есть сам токен.
///    Телефону не уходит ни ссылка, ни cookie движка, ни его `clientId` — за это отвечает калитка.
/// 3. **Пути — у `AppPaths`, права — у `PhonePassFiles`.** Файл пропуска знает только тот, кому
///    дано право на свои данные панели: прогон без права получил бы <see cref="PhonePassFiles.None"/>,
///    а не путь владельца (красная линия 4).
/// </summary>
public static class PhoneGateRealDoors
{
    /// <summary>Сколько ждём обмена ссылки входа на cookie. Столько же, сколько проба движка.</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Сколько ждём ответа движка на вызов телефона.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждём открытия потока событий.</summary>
    private static readonly TimeSpan MuxTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// СОБРАТЬ ДВЕРИ. <paramref name="entryLink"/> — ссылка входа движка (её знает контроллер
    /// сервера, и только он); <paramref name="address"/> — ДВЕРЬ адреса: тот, на котором калитка
    /// должна встать, и он спрашивается в момент запуска, а не при сборке (иначе щелчок человека
    /// в разделе до калитки не доезжает — на этом и споткнулся живой прогон 11.10.2026);
    /// <paramref name="port"/> — порт калитки; <paramref name="bound"/> — обратная дверь:
    /// калитка сообщает адрес, на котором ВСТАЛА, и панель записывает его следом.
    /// </summary>
    public static PhoneGateDoors Build(
        AppPaths paths,
        Func<string> entryLink,
        Func<string> address,
        int port,
        Action<string> bound,
        Action<string> log,
        Func<IServerControl?>? server = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(entryLink);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(log);

        // COOKIE ДВИЖКА — ОДНА и под замком: её читают и вызов, и подписка, а вход может идти
        // из двух ниток сразу (телефон шлёт два запроса подряд — это нормально).
        var gate = new object();
        var cookie = string.Empty;

        string BaseUrl() => EngineRpcLogin.BaseUrlOf(entryLink());

        // ВХОД: обмен ссылки на cookie. Пусто — движка нет или вход не состоялся, и об этом
        // говорится словами (молчаливый «пустой ответ» телефону не отдаётся никогда).
        //
        // ⚠️ В слова идёт КОД, а не `session.Detail`: в подробности бывает текст ошибки .NET,
        // а в тексте — адрес запроса, то есть САМ ТОКЕН ВХОДА. Наружу (телефону, в журнал,
        // в отчёт) он не выходит ни при каких обстоятельствах (красная линия 7).
        bool Login(out string why)
        {
            why = string.Empty;

            var baseUrl = BaseUrl();
            if (baseUrl.Length == 0)
            {
                why = PanelStrings.PhoneGateNoEngine;
                return false;
            }

            lock (gate)
            {
                if (cookie.Length > 0) return true;

                var session = EngineRpcLogin.Exchange(entryLink(), LoginTimeout);

                if (!session.Ok)
                {
                    why = string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.PhoneGateLoginFailedFormat, session.StatusCode);
                    return false;
                }

                cookie = session.CookieHeader;
                return true;
            }
        }

        PhoneEngineAnswer Engine(string endpoint, string argsJson)
        {
            if (!Login(out var why)) return PhoneEngineAnswer.Failed(why);

            string value;

            lock (gate) value = cookie;

            using var client = new EngineRpcClient(BaseUrl(), value, CallTimeout);
            var answer = client.CallRaw(endpoint, argsJson);

            // ВХОД ПЕРЕЧИТЫВАЕТСЯ ПОСЛЕ ОТКАЗА: движок мог перезапуститься, и его cookie
            // перестала значить что-либо. Забудем её здесь — и следующий вызов войдёт заново,
            // а не будет получать «не принят» до перезапуска панели.
            if (answer.RefusedOurLogin)
            {
                lock (gate) cookie = string.Empty;

                log(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.PhoneGateLoginStaleFormat, answer.Status));
            }

            return answer;
        }

        // COOKIE ДЛЯ ПОДПИСКИ ДОБЫВАЕТСЯ ЗДЕСЬ ЖЕ, И ЭТО НЕ МЕЛОЧЬ.
        // Подписка на события открывается ПРИ ЗАПУСКЕ калитки — то есть ДО первого вызова
        // телефона, и своей cookie у панели в этот миг ещё нет. Открой подписку с пустой cookie —
        // и движок ответит `401` (замер §1), рукопожатие не состоится, а вопрос агента не дойдёт
        // до телефона НИКОГДА: вызова, который добыл бы вход, может не быть вовсе. Поэтому дверь
        // событий сама зовёт вход, если cookie ещё нет.
        string CurrentCookie()
        {
            if (!Login(out _)) return string.Empty;

            lock (gate) return cookie;
        }

        // ⚠️ «ЕСТЬ ЛИ ВХОД» — ВОПРОС, А НЕ СНИМОК, И ЭТО ИСПРАВЛЕНО ПО ЖИВОМУ ДЕФЕКТУ (14.10.2026).
        //
        // **Что было.** Признак считался ЗДЕСЬ, когда двери собирались, то есть на старте панели.
        // А ссылка входа появляется позже: после перезапуска панели свой сервер узнаётся по записи
        // `own-server.txt` (в журнале владельца — секундой позже сборки дверей), и только тогда
        // `ServerController.EntryLink` отдаёт ссылку (`Owner == Panel`, `_port > 0`). Итог: признак
        // навсегда оставался `false`, калитка не открывала подписку ВООБЩЕ, телефон владельца
        // сопрягся и молчал, а раздел «Телефон» и раздел «Обновление» говорили про работающий СВОЙ
        // сервер «связи с сервером DSH нет» и «он поднят не панелью».
        //
        // **Почему «нет входа» вообще надо называть.** Ссылка входа — адрес с токеном, по которому
        // панель входит в СВОЙ сервер. Сервер, поднятый НЕ панелью, такой ссылки не оставляет:
        // панель не знает, чем в него войти, и обещать вопрос агента было бы ложью (находка
        // владельца 13.10.2026). Поэтому дверь остаётся, но отвечает на вопрос СЕЙЧАС:
        // `EngineMux.DoorsWhen` спрашивает `entryLink()` в тот миг, когда калитка решает открывать
        // подписку, — и вход, появившийся позже, доезжает до калитки БЕЗ перезапуска панели.
        // Адрес и cookie движка при этом по-прежнему берутся в момент ОТКРЫТИЯ двери.
        var events = EngineMux.DoorsWhen(
            () => entryLink().Trim().Length > 0,
            BaseUrl,
            CurrentCookie,
            MuxTimeout,
            log);

        return new PhoneGateDoors(
            address,
            port,
            Engine,
            PhonePassFiles.Under(paths, log),
            events,
            () => DateTimeOffset.Now,
            log,
            bound,
            server);
    }
}
