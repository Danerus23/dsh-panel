using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// МУЛЬТИПЛЕКСОР ДВИЖКА — КЛИЕНТСКАЯ СТОРОНА (этап F1, кусок 2): кадр открытия потока,
/// склейка обрывков и разбор того, что движок присылает.
///
/// **Живого движка здесь нет и не должно быть.** Разбор кадров — чистая функция, и проверяется
/// он ПОДСТАВНЫМИ кадрами, снятыми живьём (<c>docs\PHONE-LIVE.md</c> §3–4). Живой мультиплексор
/// требует поднятого движка, входа по cookie и петли — это замер, а не проверка набора.
///
/// ⚠️ **Чего эти проверки НЕ доказывают:** что движок примет наше рукопожатие и пришлёт `ready`.
/// Дверь к нему написана по замеру и проверена чтением кода; живой прогон — за `--phone-selftest`
/// (F1.e) и за первой пробой с телефона владельца.
/// </summary>
public class EngineMuxTests
{
    /// <summary>Кадр, снятый живьём: `ready` приходит ПЕРВЫМ и несёт `clientId` — и, к сожалению,
    /// ещё и домашний каталог машины.</summary>
    private const string ReadyFrame =
        "{\"type\":\"item\",\"streamId\":\"e1\",\"value\":{\"type\":\"ready\"," +
        "\"clientId\":\"9c1f0a2e-1111-2222-3333-444455556666\",\"host\":{\"home\":\"C:\\\\Users\\\\кто-то\"}}}";

    /// <summary>Водопад вопроса — тот самый кадр, что снят живьём (§4, опыт А).</summary>
    private const string QuestionFrame =
        "{\"type\":\"item\",\"streamId\":\"e1\",\"value\":{\"type\":\"waterfall\"," +
        "\"event\":\"user-questions/request\",\"eventId\":\"8b4b3ed5-0000-1111-2222-333344445555\"," +
        "\"agentId\":\"session-c16b\",\"request\":{\"questions\":[{\"id\":\"q1\"," +
        "\"question\":\"Продолжать?\",\"header\":\"Вопрос\",\"options\":[{\"label\":\"Да\"},{\"label\":\"Нет\"}]}]}}}";

    /// <summary>Снятие: кто-то ответил раньше нас (§4, опыт А).</summary>
    private const string CancelFrame =
        "{\"type\":\"item\",\"streamId\":\"e1\",\"value\":{\"type\":\"cancel\"," +
        "\"eventId\":\"8b4b3ed5-0000-1111-2222-333344445555\"}}";

    /// <summary>КАДР ОТКРЫТИЯ ПОТОКА — по замеру: вид, имя потока, имя двери и ПУСТЫЕ доводы.</summary>
    [Fact]
    public void Кадр_открытия_потока_собран_по_замеру()
    {
        var frame = EngineMuxFrames.Open(EngineMux.StreamId, EngineMux.EventsEndpoint);

        Assert.Contains("\"type\":\"open\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"streamId\":\"" + EngineMux.StreamId + "\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":\"$events\"", frame, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"args\":{}}", frame, StringComparison.Ordinal);

        // Кадр закрытия — тот же вид, с тем же именем потока.
        Assert.Contains("\"type\":\"cancel\"", EngineMuxFrames.Cancel(EngineMux.StreamId), StringComparison.Ordinal);
    }

    /// <summary>АДРЕС КАНАЛА: `http` → `ws`, `https` → `wss`, путь — от движка.</summary>
    [Fact]
    public void Адрес_канала_переводится_в_сокет()
    {
        Assert.Equal("ws://127.0.0.1:43127/api/remote.mux", EngineMux.Endpoint("http://127.0.0.1:43127").ToString());
        Assert.Equal("wss://example:1/api/remote.mux", EngineMux.Endpoint("https://example:1/").ToString());
    }

    /// <summary>
    /// `READY` РАЗОБРАН, `clientId` ВЗЯТ, А ДОМАШНИЙ КАТАЛОГ НЕ ПРОЧИТАН ВОВСЕ.
    ///
    /// ⚠️ Это главная проверка потока: `ready` отдаёт `host.home` (замер §3), и если бы разбор
    /// носил его в подробности кадра, он уехал бы в журнал панели — то есть наружу из панели.
    /// Поля, в которое его можно положить, у разобранного кадра НЕТ, и проверка это стережёт.
    /// </summary>
    [Fact]
    public void Ready_даёт_clientId_и_не_читает_дом()
    {
        var frame = PhoneEventFrames.Parse(ReadyFrame);

        Assert.Equal(PhoneEventKind.Ready, frame.Kind);
        Assert.Equal("9c1f0a2e-1111-2222-3333-444455556666", frame.ClientId);

        // Ни в одном поле разобранного кадра домашнего пути нет.
        Assert.DoesNotContain("home", frame.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", frame.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", frame.QuestionsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", frame.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", frame.ClientId, StringComparison.Ordinal);
    }

    /// <summary>ВОДОПАД — ЭТО ВОПРОС, и из кадра берутся РОВНО вопросы: ни `agentId`,
    /// ни служебные поля движка телефону не уезжают.</summary>
    [Fact]
    public void Водопад_даёт_вопросы_и_ничего_лишнего()
    {
        var frame = PhoneEventFrames.Parse(QuestionFrame);

        Assert.Equal(PhoneEventKind.Question, frame.Kind);
        Assert.Equal("8b4b3ed5-0000-1111-2222-333344445555", frame.EventId);
        Assert.Contains("Продолжать?", frame.QuestionsJson, StringComparison.Ordinal);
        Assert.StartsWith("[", frame.QuestionsJson, StringComparison.Ordinal);
        Assert.EndsWith("]", frame.QuestionsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("agentId", frame.QuestionsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("session-c16b", frame.QuestionsJson, StringComparison.Ordinal);
    }

    /// <summary>СНЯТИЕ И ОШИБКА РАЗЛИЧАЮТСЯ: «вопрос снят» — это не «канал сломался»,
    /// и калитка говорит про них разное.</summary>
    [Fact]
    public void Снятие_и_ошибка_различаются()
    {
        var cancelled = PhoneEventFrames.Parse(CancelFrame);

        Assert.Equal(PhoneEventKind.Cancelled, cancelled.Kind);
        Assert.Equal("8b4b3ed5-0000-1111-2222-333344445555", cancelled.EventId);

        var failure = PhoneEventFrames.Parse(
            "{\"type\":\"error\",\"streamId\":\"e1\",\"error\":{\"code\":\"gateway/internal\",\"message\":\"C:\\\\Users\\\\кто-то\"}}");

        Assert.Equal(PhoneEventKind.Failure, failure.Kind);
        Assert.Contains("gateway/internal", failure.Detail, StringComparison.Ordinal);

        // ⚠️ Текст ошибки движка (в нём бывают пути) в подробность НЕ переносится.
        Assert.DoesNotContain("Users", failure.Detail, StringComparison.Ordinal);

        // Закрытие потока — тоже Failure: молчание выглядело бы живым каналом.
        Assert.Equal(PhoneEventKind.Failure, PhoneEventFrames.Parse("{\"type\":\"end\",\"streamId\":\"e1\"}").Kind);
    }

    /// <summary>ЧУЖОЕ И МУСОР НЕ РОНЯЮТ РАЗБОР: поток чужой работы не имеет права уронить калитку
    /// исключением — иначе «движок прислал не то» гасило бы дверь телефона.</summary>
    [Fact]
    public void Чужой_кадр_и_мусор_разбираются_как_мимо()
    {
        foreach (var text in new[]
                 {
                     string.Empty,
                     "не json вовсе",
                     "[]",
                     "{\"type\":\"item\",\"streamId\":\"e1\"}",
                     "{\"type\":\"item\",\"streamId\":\"e1\",\"value\":{\"type\":\"emit\",\"event\":\"другое\"}}",
                     "{\"type\":\"item\",\"streamId\":\"e1\",\"value\":{\"type\":\"waterfall\",\"event\":\"чужой/водопад\"}}",
                 })
        {
            var frame = PhoneEventFrames.Parse(text);

            Assert.Equal(PhoneEventKind.Other, frame.Kind);
            Assert.Empty(frame.ClientId);
            Assert.Empty(frame.EventId);
        }
    }

    /// <summary>ОБРЫВКИ СКЛЕИВАЮТСЯ В ОДИН КАДР: сообщение WebSocket бывает разрезано, и разбери
    /// калитка половину как целое — вопрос уехал бы обрезанным.</summary>
    [Fact]
    public void Обрывки_склеиваются_в_один_кадр()
    {
        var buffer = new MuxTextBuffer();
        var bytes = Encoding.UTF8.GetBytes(QuestionFrame);

        var head = bytes.Take(10).ToArray();
        var tail = bytes.Skip(10).ToArray();

        Assert.Null(buffer.Add(head, head.Length, endOfMessage: false));

        var whole = buffer.Add(tail, tail.Length, endOfMessage: true);

        Assert.Equal(QuestionFrame, whole);
        Assert.Equal(PhoneEventKind.Question, PhoneEventFrames.Parse(whole).Kind);

        // Следующий кадр начинается с чистого листа: склеенное не липнет к следующему.
        var next = Encoding.UTF8.GetBytes(CancelFrame);
        Assert.Equal(CancelFrame, buffer.Add(next, next.Length, endOfMessage: true));
    }

    /// <summary>
    /// COOKIE ДВИЖКА ПРИКЛАДЫВАЕТСЯ К МУЛЬТИПЛЕКСОРУ — и это проверяется ПРЯМО, потому что без неё
    /// рукопожатие получает `401` (замер §1) и подписка не откроется ВООБЩЕ: «вопросов нет» было бы
    /// неправдой. Проверяется на живом `ClientWebSocketOptions`, а не на подставном: здесь важно,
    /// что именно эта версия .NET принимает наше значение cookie.
    /// </summary>
    [Fact]
    public void Cookie_прикладывается_к_мультиплексору()
    {
        var log = new System.Collections.Generic.List<string>();

        using var socket = new ClientWebSocket();

        Assert.True(EngineMux.ApplyCookie(socket.Options, "http://127.0.0.1:3080", "dsh-auth-abc=v1.payload.signature", log.Add));
        Assert.Empty(log);

        // Пустой cookie — не «приложилась»: об этом калитка обязана знать.
        Assert.False(EngineMux.ApplyCookie(socket.Options, "http://127.0.0.1:3080", string.Empty, log.Add));
    }
}
