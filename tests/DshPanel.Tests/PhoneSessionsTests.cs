using System;
using System.Text.Json;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СПИСОК РАЗГОВОРОВ НА ТЕЛЕФОНЕ: ПОДЧИНЁННЫЕ СЕССИИ НЕ ПОКАЗЫВАЮТСЯ (блок D плана 2.2.0).
///
/// **От какого замера.** `_team\220\l2-migraciya.md` (копия профиля владельца, 18.10.2026): из
/// **197** сессий **179** — подчинённые, и открыть их телефоном нельзя
/// (`session/page` → «subagent Sessions require their durable parent address»). Решение капитана:
/// не показывать их в списке — **фильтром на стороне панели**, без нового вызова, пути или адреса.
///
/// **Что здесь доказывается, и почему не «на словах»:**
/// * признак подчинённости берётся из ОБЪЯВЛЕНИЯ движка (`parentSessionId`, `origin: "subagent"`)
///   и срабатывает по любому из двух;
/// * форма ответа проверяется, а не предполагается: нет `result.value`-массива — ответ НЕ трогается,
///   и это названо причиной (`Shape`), а не проглочено;
/// * то, чего мы не трогали, остаётся ДВИЖКОВЫМ: незнакомые поля сессий, `rpcId`, порядок — сырым
///   текстом;
/// * когда подчинённых нет — тело остаётся БАЙТ В БАЙТ прежним (обычный случай чистой установки).
/// </summary>
public class PhoneSessionsTests
{
    /// <summary>Ответ движка той же формы, что у настоящего: конверт `rpcId` + `result`.</summary>
    private static string Envelope(params string[] sessions) =>
        "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":{\"items\":["
        + string.Join(",", sessions) + "]}}}";

    /// <summary>Корневая сессия: ни родителя, ни происхождения подчинённой.</summary>
    private const string Root = "{\"sessionId\":\"s-1\",\"running\":false,\"blank\":false,\"cwd\":\"C:\\\\работа\"}";

    /// <summary>Подчинённая по РОДИТЕЛЮ — так выглядит 179 из 197 в замере `l2`.</summary>
    private const string ByParent =
        "{\"sessionId\":\"s-2\",\"parentSessionId\":\"s-1\",\"running\":false,\"blank\":false}";

    /// <summary>Подчинённая по ПРОИСХОЖДЕНИЮ — второй объявленный движком признак.</summary>
    private const string ByOrigin = "{\"sessionId\":\"s-3\",\"origin\":\"subagent\",\"running\":false,\"blank\":false}";

    private static string[] Ids(string body)
    {
        using var document = JsonDocument.Parse(body);
        var value = document.RootElement.GetProperty(PhoneSessions.ResultField).GetProperty(PhoneSessions.ValueField).GetProperty(PhoneSessions.ItemsField);

        var ids = new string[value.GetArrayLength()];
        var index = 0;

        foreach (var session in value.EnumerateArray())
        {
            ids[index++] = session.GetProperty("sessionId").GetString() ?? string.Empty;
        }

        return ids;
    }

    /// <summary>
    /// ГЛАВНЫЙ СЛУЧАЙ: из трёх сессий телефону остаётся ОДНА — корневая. И оба признака
    /// подчинённости работают порознь, иначе половина строк осталась бы на экране.
    /// </summary>
    [Fact]
    public void Подчинённые_сессии_убираются_а_корневые_остаются()
    {
        var filtered = PhoneSessions.Filter(Envelope(Root, ByParent, ByOrigin));

        Assert.Equal(1, filtered.Kept);
        Assert.Equal(2, filtered.Dropped);
        Assert.True(filtered.Hides);
        Assert.False(filtered.ShapeBroken, filtered.Shape);
        Assert.Equal(new[] { "s-1" }, Ids(filtered.Body));
    }

    /// <summary>
    /// ПОДЧИНЁННЫХ НЕТ — ТЕЛО НЕ ТРОГАЕТСЯ ВОВСЕ. Это не мелочь: у чистой установки список пуст,
    /// и переписывать его «на всякий случай» значило бы менять ответ движка без причины.
    /// </summary>
    [Fact]
    public void Без_подчинённых_тело_остаётся_прежним()
    {
        var body = Envelope(Root);
        var filtered = PhoneSessions.Filter(body);

        Assert.Equal(body, filtered.Body);
        Assert.Equal(0, filtered.Dropped);
        Assert.False(filtered.Hides);

        // И пустой список — тоже прежним.
        var empty = Envelope();
        Assert.Equal(empty, PhoneSessions.Filter(empty).Body);

        // ⚠️ И тело С ПРОБЕЛАМИ И ПЕРЕВОДАМИ СТРОК — тоже прежним, БАЙТ В БАЙТ. Это не придирка:
        // именно так проверка ловит «переписали, но вышло похоже». Первая редакция сравнивала только
        // компактное тело, и мутация «пересобирать всегда» на ней ВЫЖИЛА (найдено 18.10.2026): сборка
        // заново давала ту же строку, и «не трогали вовсе» ничем не отличалось от «трогали».
        var spaced = "{\r\n  \"type\": \"server-response\",\r\n  \"rpcId\": \"r1\",\r\n"
            + "  \"result\": { \"ok\": true, \"value\": { \"items\": [ " + Root + " ] } }\r\n}";

        Assert.Equal(spaced, PhoneSessions.Filter(spaced).Body);
    }

    /// <summary>
    /// ФОРМА ОТВЕТА ПРОВЕРЯЕТСЯ, А НЕ ПРЕДПОЛАГАЕТСЯ. Всё, что не совпало с замеренной формой
    /// (`result.value.items` — массив), отдаётся дальше КАК ЕСТЬ, а причина называется: отфильтровать
    /// наугад значило бы однажды выбросить из списка живые разговоры.
    ///
    /// ⚠️ В отказах назван ПУТЬ, на котором не сошлось (`result-fields(ok,value)`, `value-fields(sessions)`,
    /// `items-object(sessions)`): по этим словам видно, КУДА движок переложил список, — а без них
    /// «не той формы» не подсказало бы ничего.
    /// </summary>
    [Fact]
    public void Незнакомая_форма_не_трогается_и_причина_названа()
    {
        var cases = new (string Body, string Shape)[]
        {
            (string.Empty, "empty"),
            ("это не JSON вовсе", "not-json"),
            ("[1,2,3]", "root-array[3]"),
            ("{\"rpcId\":\"r1\"}", "result-нет поля"),
            ("{\"result\":{\"ok\":true}}", "result-fields(ok)"),
            ("{\"result\":{\"ok\":true,\"value\":[]}}", "value-array[0]"),
            ("{\"result\":{\"ok\":true,\"value\":{\"sessions\":[]}}}", "value-fields(sessions)"),
            ("{\"result\":{\"ok\":true,\"value\":{\"items\":{\"sessions\":[]}}}}", "items-object(sessions)"),
        };

        foreach (var (body, shape) in cases)
        {
            var filtered = PhoneSessions.Filter(body);

            Assert.True(filtered.ShapeBroken, $"форма «{shape}» выдана за разобранную");
            Assert.Equal(shape, filtered.Shape);
            Assert.Equal(0, filtered.Dropped);
            Assert.Equal(body, filtered.Body);
        }
    }

    /// <summary>
    /// ЧУЖОЕ ОСТАЁТСЯ ЧУЖИМ: незнакомые поля сессии, порядок и `rpcId` переносятся как есть.
    /// Список сессий — ответ ДВИЖКА, и панель не имеет права переписывать в нём то, чего не просили:
    /// иначе однажды пропало бы поле, которого мы не знаем (у 0.2.x в нём, например, `formatStatus`).
    /// </summary>
    [Fact]
    public void Незнакомые_поля_и_порядок_сохраняются()
    {
        const string withUnknown =
            "{\"sessionId\":\"s-1\",\"formatStatus\":\"migration-required\",\"agentAvailable\":true,"
            + "\"projections\":{\"title\":\"разговор\"},\"updatedAt\":1760000000000}";

        var body = Envelope(withUnknown, ByParent);
        var filtered = PhoneSessions.Filter(body);

        using var document = JsonDocument.Parse(filtered.Body);
        var root = document.RootElement;

        Assert.Equal("server-response", root.GetProperty("type").GetString());
        Assert.Equal("r1", root.GetProperty("rpcId").GetString());

        var session = root.GetProperty(PhoneSessions.ResultField).GetProperty(PhoneSessions.ValueField).GetProperty(PhoneSessions.ItemsField)[0];

        Assert.Equal("s-1", session.GetProperty("sessionId").GetString());
        Assert.Equal("migration-required", session.GetProperty("formatStatus").GetString());
        Assert.True(session.GetProperty("agentAvailable").GetBoolean());
        Assert.Equal("разговор", session.GetProperty("projections").GetProperty("title").GetString());
        Assert.Equal(1760000000000, session.GetProperty("updatedAt").GetInt64());
        Assert.Equal(1, root.GetProperty(PhoneSessions.ResultField).GetProperty(PhoneSessions.ValueField).GetProperty(PhoneSessions.ItemsField).GetArrayLength());
    }

    /// <summary>
    /// ПРИЗНАКИ ПО ОТДЕЛЬНОСТИ: пустой или отсутствующий родитель подчинённой НЕ делает,
    /// а `origin` сравнивается без учёта регистра. Ровно эти границы решают, кого спрятать.
    /// </summary>
    [Fact]
    public void Признаки_подчинённости_проверяются_по_отдельности()
    {
        Assert.False(PhoneSessions.Subordinate(Element("{}")));
        Assert.False(PhoneSessions.Subordinate(Element("{\"parentSessionId\":null}")));
        Assert.False(PhoneSessions.Subordinate(Element("{\"parentSessionId\":\"\"}")));
        Assert.False(PhoneSessions.Subordinate(Element("{\"parentSessionId\":\"   \"}")));
        Assert.False(PhoneSessions.Subordinate(Element("{\"origin\":\"root\"}")));

        Assert.True(PhoneSessions.Subordinate(Element("{\"parentSessionId\":\"s-1\"}")));
        Assert.True(PhoneSessions.Subordinate(Element("{\"origin\":\"subagent\"}")));
        Assert.True(PhoneSessions.Subordinate(Element("{\"origin\":\"SUBAGENT\"}")));

        // Не объект — не сессия: не наше дело решать за движок.
        Assert.False(PhoneSessions.Subordinate(Element("\"строка\"")));
        Assert.False(PhoneSessions.Subordinate(default));
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);

        // Клон живёт дольше документа — элементу из `using`-документа доверять нельзя.
        return document.RootElement.Clone();
    }

    /// <summary>
    /// ПРИНЯЛ ЛИ ДВИЖОК ОТВЕТ — по конверту, а не по коду HTTP. Замер `l1`: старая дверь отвечает
    /// `200 {ok:true}` (принял), а новые `userQuestions/*` — `200 {ok:true, value:false}` (не принял).
    /// Путать эти два нельзя: во втором случае человек прочитал бы «ответ отправлен», а вопрос висел бы.
    /// </summary>
    [Fact]
    public void Принятие_ответа_решается_по_конверту()
    {
        Assert.Equal(
            PhoneDecisions.AnswerJudge.Accepted,
            PhoneDecisions.JudgeAnswer("{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}"));

        Assert.Equal(
            PhoneDecisions.AnswerJudge.NotTaken,
            PhoneDecisions.JudgeAnswer("{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true,\"value\":false}}"));

        Assert.Equal(
            PhoneDecisions.AnswerJudge.EnvelopeError,
            PhoneDecisions.JudgeAnswer(
                "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":false,"
                + "\"error\":{\"code\":\"gateway/internal\"}}}"));

        // Не наша беда в разборе — не повод объявлять ответ потерянным.
        Assert.Equal(PhoneDecisions.AnswerJudge.Accepted, PhoneDecisions.JudgeAnswer(string.Empty));
        Assert.Equal(PhoneDecisions.AnswerJudge.Accepted, PhoneDecisions.JudgeAnswer("не json"));
        Assert.Equal(PhoneDecisions.AnswerJudge.Accepted, PhoneDecisions.JudgeAnswer("{\"result\":{\"value\":0}}"));
        Assert.Equal(PhoneDecisions.AnswerJudge.Accepted, PhoneDecisions.JudgeAnswer(null));
    }

    // ---- ОСМОТР ФОРМЫ: блок E перемеряет её живым прогоном ------------------------------------

    /// <summary>
    /// ОСМОТР НАЗЫВАЕТ ПУТЬ, ЧИСЛА И ПОЛЯ — и это ГЛАВНОЕ, чего не хватило блоку D.
    ///
    /// **От какого случая.** Фильтр дважды искал список не там (`result.value`, потом `result.items`),
    /// и оба раза ошибку «подтверждал» подставной ответ самой панели. Осмотр появился затем, чтобы
    /// форму называл ЖИВОЙ прогон: путь до массива, сколько сессий и подчинённых, какие поля есть.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть путь `result.items`»** (фильтр перестанет
    /// видеть список) и **«считать подчинённых по одному признаку»**.
    /// </summary>
    [Fact]
    public void Осмотр_называет_путь_числа_и_поля()
    {
        // Форма 0.2.1 из замера: поля сессии включают formatStatus и agentAvailable.
        const string newLine =
            "{\"sessionId\":\"s-1\",\"updatedAt\":1,\"agentAvailable\":true,\"formatStatus\":\"current\","
            + "\"running\":false,\"blank\":false,\"parentSessionId\":null,\"origin\":\"root\",\"cwd\":\"C:\\\\р\"}";

        var examined = PhoneSessions.Examine(Envelope(newLine, ByParent, ByOrigin));

        Assert.True(examined.Found);
        Assert.Equal(PhoneSessions.ItemsPath, examined.Container);
        Assert.True(examined.Expected, "измеренный путь обязан совпасть с тем, что ждёт фильтр");
        Assert.Equal(3, examined.Sessions);
        Assert.Equal(2, examined.Subordinate);
        Assert.True(examined.ParentField);
        Assert.True(examined.OriginField);
        Assert.True(examined.FormatField, "поле formatStatus у 0.2.1 есть — осмотр обязан его видеть");
        Assert.Contains("formatStatus", examined.Fields, StringComparison.Ordinal);

        // Линия 0.1.5: тех же двух полей формата НЕТ — и осмотр это различает, а не догадывается.
        var old = PhoneSessions.Examine(Envelope(Root, ByParent));

        Assert.Equal(2, old.Sessions);
        Assert.Equal(1, old.Subordinate);
        Assert.False(old.FormatField, "у 0.1.5 поля formatStatus нет");
    }

    /// <summary>
    /// СПИСОК ПЕРЕЛОЖИЛИ В ДРУГОЕ МЕСТО — ЭТО ВИДНО. Осмотр ищет массив не только по измеренному
    /// пути: иначе «переложили» выглядело бы как «списка нет», и панель молчала бы о самом важном.
    /// </summary>
    [Fact]
    public void Осмотр_видит_чужой_путь_и_это_не_тот_что_ждём()
    {
        var moved = PhoneSessions.Examine(
            "{\"result\":{\"ok\":true,\"value\":{\"sessions\":[{\"sessionId\":\"s-1\"}]}}}");

        Assert.True(moved.Found);
        Assert.Equal("result.value.sessions", moved.Container);
        Assert.False(moved.Expected, "чужой путь выдан за ожидаемый");

        var top = PhoneSessions.Examine("{\"result\":{\"ok\":true,\"items\":[{\"sessionId\":\"s-1\"}]}}");

        Assert.True(top.Found);
        Assert.Equal("result.items", top.Container);
        Assert.False(top.Expected);
    }

    /// <summary>
    /// ПУТЬ ДО СПИСКА — ЛИТЕРАЛ, А НЕ КОНСТАНТА ПРОДУКТА (блок E плана 2.2.0).
    ///
    /// **От какого случая.** Фильтр дважды искал список не там, и оба раза ошибку «подтверждала»
    /// проверка, читавшая ожидание из того же места, где лежал факт. Здесь путь записан **словами
    /// замера** (`result.value.items`, 18.10.2026, живые 0.1.5 и 0.2.1 на копии 198 сессий), и он
    /// же — договор с движком: переложит апстрим список в другое место — упадёт ЭТА проверка,
    /// а не молча перестанет фильтровать телефон.
    ///
    /// Мутации, которые обязаны упасть здесь: **«вернуть путь `result.value`»**, **«вернуть
    /// `result.items`»**, **«переименовать контейнер»**.
    /// </summary>
    [Fact]
    public void Путь_до_списка_записан_замером_а_не_догадкой()
    {
        Assert.Equal("result.value.items", PhoneSessions.ItemsPath);
        Assert.Equal("result", PhoneSessions.ResultField);
        Assert.Equal("value", PhoneSessions.ValueField);
        Assert.Equal("items", PhoneSessions.ItemsField);

        // И имя самого вызова — одно и то же у калитки и у фильтра.
        Assert.Equal("session/list", PhoneDecisions.SessionListEndpoint);
        Assert.Contains(
            PhoneDecisions.Allowed,
            call => string.Equals(call.Endpoint, PhoneDecisions.SessionListEndpoint, StringComparison.Ordinal));
    }
    /// <summary>
    /// ПУСТОЙ СПИСОК — БЕЗ КОНТЕЙНЕРА ВОВСЕ (замер: `result = {ok}`, 62 знака). Осмотр говорит
    /// «не нашёл» и называет причину, а не притворяется пустым списком с формой.
    /// </summary>
    [Fact]
    public void Осмотр_на_пустом_ответе_говорит_что_не_нашёл()
    {
        foreach (var (body, reason) in new[]
                 {
                     ("{\"result\":{\"ok\":true}}", "result-fields(ok)"),
                     ("{\"result\":{\"ok\":true,\"value\":{\"items\":{}}}}", "result-fields(ok,value)"),
                     ("{\"result\":{\"ok\":true,\"value\":{}}}", "result-fields(ok,value)"),
                     ("{}", "result-нет поля"),
                 })
        {
            var examined = PhoneSessions.Examine(body);

            Assert.False(examined.Found, $"«{reason}» выдано за найденный список");
            Assert.Equal(reason, examined.Container);
            Assert.False(examined.Expected);
        }

        Assert.False(PhoneSessions.Examine(string.Empty).Found);
        Assert.False(PhoneSessions.Examine("не json").Found);
    }
}
