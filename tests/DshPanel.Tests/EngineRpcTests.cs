using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПОВЕРХНОСТЬ RPC ДВИЖКА: список имён и разбор его ответов (этап E плана).
///
/// **Зачем проверки здесь, если есть живая проба.** Живая проба (`--server-selftest`) отвечает
/// на вопрос «признаёт ли ЭТОТ движок эти имена» — и её нельзя прогнать в обычном наборе: она
/// поднимает настоящий движок и требует изоляции. Здесь проверяется то, что от движка НЕ зависит:
/// правильно ли панель строит вызов и как она понимает ответы. Это и есть сторож формы вызова:
/// движок, сменивший форму, отвечает `400`/`415`/`404` — и проверка обязана упасть.
///
/// **Сервер в проверках — НАСТОЯЩИЙ сокет** (`TcpListener` на петле, свой свободный порт), а не
/// подставной объект: только так проверяется, что наша форма вызова доходит по проводу и что
/// разбор видит настоящий HTTP. Форма, которую отдаёт сервер, снята ЗАМЕРОМ 10.10.2026 на нашем
/// движке `@deepseek-ai/dsh` `0.1.5-rc.2` (разбор — в `EngineRpc.cs`).
///
/// ⚠️ Порт 3080 не занимается НИКОГДА — он канал живой сессии владельца; здесь берётся порт,
/// который система выдала сама.
/// </summary>
public sealed class EngineRpcTests
{
    // ---- разбор ответов --------------------------------------------------------------------

    /// <summary>Метода нет — движок отвечает `404 not found` ДО разбора тела.</summary>
    [Fact]
    public void Неизвестный_метод_отсекается_404()
    {
        using var engine = new RawEngine((_, _, _) => (404, "text/plain", "not found"));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call("dshPanelProbe/noSuchMethod");

        Assert.Equal(EngineRpcAnswerKind.NoMethod, answer.Kind);
        Assert.Equal(404, answer.StatusCode);
        Assert.False(answer.MethodExists, "ответ 404 не должен считаться признанием метода");
    }

    /// <summary>Метод ЕСТЬ, доводы не те — это признание имени, а не отказ в нём.</summary>
    [Fact]
    public void Занятый_метод_с_негодными_доводами_признаётся()
    {
        using var engine = new RawEngine((_, _, _) => (200, "application/json", Envelope(Error("gateway/arguments-invalid"))));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call("session/search", "{\"dshPanelProbe\":true}");

        Assert.Equal(EngineRpcAnswerKind.Refused, answer.Kind);
        Assert.Equal("gateway/arguments-invalid", answer.Code);
        Assert.True(answer.MethodExists);
    }

    /// <summary>Потоковый метод, позванный одиночным вызовом: имя признано, содержимое — нет.</summary>
    [Fact]
    public void Потоковый_метод_признаётся_по_коду_движка()
    {
        using var engine = new RawEngine((_, _, _) => (200, "application/json", Envelope(Error("gateway/signature-invalid"))));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call("session/control");

        Assert.Equal(EngineRpcAnswerKind.Refused, answer.Kind);
        Assert.True(answer.MethodExists);
    }

    /// <summary>Настоящий успех разбирается как успех: конверт с `result` и без ошибки.</summary>
    [Fact]
    public void Успешный_ответ_разбирается()
    {
        using var engine = new RawEngine((_, _, _) => (200, "application/json", Envelope("{\"ok\":true,\"value\":{\"items\":[]}}")));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call(EngineRpcSurface.RealCallEndpoint, EngineRpcSurface.RealCallPayload);

        Assert.Equal(EngineRpcAnswerKind.Ok, answer.Kind);
        Assert.True(answer.MethodExists);
    }

    /// <summary>Без входа движок не пускает — и это НЕ признание метода.</summary>
    [Fact]
    public void Без_входа_ответ_401()
    {
        using var engine = new RawEngine((_, _, _) => (401, "text/plain", "unauthorized"));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", string.Empty, TimeSpan.FromSeconds(10));

        var answer = client.Call(EngineRpcSurface.RealCallEndpoint);

        Assert.Equal(EngineRpcAnswerKind.Unauthorized, answer.Kind);
        Assert.False(answer.MethodExists);
    }

    /// <summary>
    /// СЛОМАННАЯ ФОРМА ВЫЗОВА — это падение пробы, а не «метода нет». Движок отвечает `400`
    /// (тело не JSON) и `415` (не тот content-type); оба вида обязаны называться «не разобрано».
    /// </summary>
    [Theory]
    [InlineData(400, "body is not JSON")]
    [InlineData(415, "content type must be application/json")]
    public void Сломанная_форма_вызова_не_считается_признанием(int status, string body)
    {
        using var engine = new RawEngine((_, _, _) => (status, "text/plain", body));
        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call("session/list");

        Assert.Equal(EngineRpcAnswerKind.Malformed, answer.Kind);
        Assert.False(answer.MethodExists);
    }

    // ---- форма вызова: то, что панель кладёт на провод ---------------------------------------

    /// <summary>
    /// ФОРМА ВЫЗОВА: метод стоит В ПУТИ, доводы — ВНУТРИ `payload.args`. Сервер здесь строгий:
    /// он отвечает `200` только на ту форму, которую снял замер, и `404`/`400` на любую другую.
    /// Поэтому мутация, сменившая форму (метод в теле, доводы мимо `args`), роняет эту проверку.
    /// </summary>
    [Fact]
    public void Форма_вызова_та_же_что_у_движка()
    {
        using var engine = new RawEngine((path, body, _) =>
        {
            // Метод — сегмент пути, и он же назван в теле: так велит замер.
            if (!path.StartsWith("/api/", StringComparison.Ordinal)) return (404, "text/plain", "not found");

            var endpoint = path["/api/".Length..];
            if (!body.Contains($"\"method\":\"{endpoint}\"", StringComparison.Ordinal)) return (400, "text/plain", "method does not match endpoint");
            if (!body.Contains("\"type\":\"client-request\"", StringComparison.Ordinal)) return (400, "text/plain", "invalid client-request message");
            if (!body.Contains("\"payload\":{\"args\":", StringComparison.Ordinal)) return (400, "text/plain", "invalid client-request message");
            if (!body.Contains("\"rpcId\":\"", StringComparison.Ordinal)) return (400, "text/plain", "invalid client-request message");

            return (200, "application/json", Envelope("{\"ok\":true,\"value\":{\"items\":[]}}"));
        });

        using var client = new EngineRpcClient($"http://127.0.0.1:{engine.Port}", "dsh-auth-test=1", TimeSpan.FromSeconds(10));

        var answer = client.Call("session/list", "{\"_request\":{}}");

        Assert.Equal(EngineRpcAnswerKind.Ok, answer.Kind);
        Assert.True(answer.MethodExists);

        var seen = Assert.Single(engine.Seen);
        Assert.Equal("/api/session/list", seen.Path);
        Assert.Contains("\"method\":\"session/list\"", seen.Body, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"args\":{\"_request\":{}}}", seen.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВХОД: обмен ссылки на cookie — `303` плюс `Set-Cookie`. И обратная сторона: без заголовка
    /// входа не бывает.
    /// </summary>
    [Fact]
    public void Вход_обменивает_ссылку_на_cookie()
    {
        using var engine = new RawEngine((_, _, _) => (303, "text/plain", string.Empty), setCookie: "dsh-auth-test=abc; Path=/; HttpOnly");

        var ok = EngineRpcLogin.Exchange($"http://127.0.0.1:{engine.Port}/?token=probe", TimeSpan.FromSeconds(10));

        Assert.True(ok.Ok, ok.Detail);
        Assert.Equal(303, ok.StatusCode);
        Assert.StartsWith("dsh-auth-test=", ok.CookieHeader, StringComparison.Ordinal);
    }

    /// <summary>Токен входа уходит ТОЛЬКО на свою машину: чужой адрес отвергается без запроса.</summary>
    [Fact]
    public void Ссылка_на_чужую_машину_не_трогается()
    {
        var session = EngineRpcLogin.Exchange("http://evil.example/?token=probe", TimeSpan.FromSeconds(5));

        Assert.False(session.Ok);
        Assert.Empty(session.CookieHeader);
    }

    // ---- список имён -------------------------------------------------------------------------

    /// <summary>
    /// СПИСОК СТОРОЖИТ САМ СЕБЯ: число имён, отсутствие повторов, форма имени и то, что выдуманное
    /// имя в нём не появилось (иначе обратная сторона пробы ничего не значила бы).
    /// </summary>
    [Fact]
    public void Список_имён_цел_и_выдуманного_в_нём_нет()
    {
        var methods = EngineRpcSurface.Methods;

        Assert.Equal(EngineRpcSurface.MeasuredCount, methods.Count);
        Assert.Equal(84, methods.Count);
        Assert.Equal(methods.Count, methods.Select(m => m.Endpoint).Distinct(StringComparer.Ordinal).Count());

        Assert.All(methods, m =>
        {
            var parts = m.Endpoint.Split('/');
            Assert.True(parts.Length == 2, $"имя «{m.Endpoint}» не вида «семейство/метод»");
            Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            Assert.False(m.Endpoint.Contains('.', StringComparison.Ordinal), $"имя «{m.Endpoint}» с точкой — у нашей версии движка имена со слэшем");
        });

        Assert.DoesNotContain(EngineRpcSurface.BogusEndpoint, methods.Select(m => m.Endpoint));
        Assert.Equal(19, EngineRpcSurface.Namespaces.Count);

        // Список «только чтение» — не пустой: будущей калитке телефона нужны прежде всего эти имена.
        Assert.NotEmpty(EngineRpcSurface.Reading);
        Assert.All(EngineRpcSurface.Reading, m => Assert.True(m.ReadOnly));

        // Версия, на которой список снят, названа — иначе «замер» неотличим от выдумки.
        Assert.False(string.IsNullOrWhiteSpace(EngineRpcSurface.MeasuredOnVersion));
        Assert.False(string.IsNullOrWhiteSpace(EngineRpcSurface.MeasuredOn));
    }

    /// <summary>
    /// ВТОРОЙ СНИМОК ПОВЕРХНОСТИ (линия 0.2.x) НАЗВАН ЧИСЛАМИ И НЕ РАСХОДИТСЯ САМ С СОБОЙ.
    ///
    /// Замер 18.10.2026 (задача t3 плана 2.2.0, свой разбор 29 файлов объявлений): у `0.2.1-rc.2`
    /// объявлено 144 имени — 77 наших живы, 7 пропали, 67 новых в 16 семействах. Здесь стерегутся
    /// именно эти числа и устройство списков: снятые не пересекаются с нужными, нужные — подмножество
    /// каталога, новые не пересекаются с каталогом вовсе, и все имена одной формы.
    /// </summary>
    [Fact]
    public void Второй_снимок_поверхности_назван_числами()
    {
        var catalog = EngineRpcSurface.Methods.Select(m => m.Endpoint).ToList();

        // Каталог 0.1.5 цел: 84 = 77 нужных + 7 снятых с надобности.
        Assert.Equal(EngineRpcSurface.MeasuredCount, catalog.Count);
        Assert.Equal(EngineRpcSurface.RequiredCountOnKnown, EngineRpcSurface.RequiredTotal);
        Assert.Equal(77, EngineRpcSurface.RequiredTotal);
        Assert.Equal(7, EngineRpcSurface.Retired.Count);
        Assert.Equal(catalog.Count, EngineRpcSurface.RequiredTotal + EngineRpcSurface.Retired.Count);

        // У 0.2.1: 67 новых в 16 семействах, и всего объявлено 144 = 77 + 67.
        Assert.Equal(EngineRpcSurface.KnownAddedCount, EngineRpcSurface.KnownAdded.Count);
        Assert.Equal(67, EngineRpcSurface.KnownAdded.Count);
        Assert.Equal(EngineRpcSurface.KnownAddedFamilies, EngineRpcSurface.KnownAddedNamespaces.Count);
        Assert.Equal(16, EngineRpcSurface.KnownAddedNamespaces.Count);
        Assert.Equal(
            EngineRpcSurface.KnownOnDeclaredCount,
            EngineRpcSurface.RequiredTotal + EngineRpcSurface.KnownAdded.Count);
        Assert.Equal(144, EngineRpcSurface.KnownOnDeclaredCount);

        // Снятые — из каталога, нужные — каталог без них, новые — не из каталога вовсе.
        Assert.All(EngineRpcSurface.Retired, name => Assert.Contains(name, catalog));
        Assert.DoesNotContain(EngineRpcSurface.Retired, name => EngineRpcSurface.Required.Any(m => m.Endpoint == name));
        Assert.DoesNotContain(EngineRpcSurface.KnownAdded, name => catalog.Contains(name));
        Assert.Equal(EngineRpcSurface.KnownAdded.Count, EngineRpcSurface.KnownAdded.Distinct(StringComparer.Ordinal).Count());

        // Форма имён у новых — та же, что у каталога: семейство/метод, без точек и повторов.
        Assert.All(EngineRpcSurface.KnownAdded, name =>
        {
            var parts = name.Split('/');
            Assert.True(parts.Length == 2, $"новое имя «{name}» не вида «семейство/метод»");
            Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(part)));
            Assert.False(name.Contains('.', StringComparison.Ordinal), $"новое имя «{name}» с точкой");
        });

        // Версия и дата второго замера названы — иначе «второй снимок» неотличим от выдумки.
        Assert.False(string.IsNullOrWhiteSpace(EngineRpcSurface.KnownOnVersion));
        Assert.False(string.IsNullOrWhiteSpace(EngineRpcSurface.KnownOn));
    }

    /// <summary>
    /// НАША ВЕРСИЯ (0.1.5) ПРОХОДИТ СВЕРКУ ЦЕЛИКОМ: все 84 имени объявлены, потерь нет, незнакомых
    /// нет, новых знакомых ноль. Это сторож того, что правка под 0.2.x не ослабила проверку старой линии.
    /// </summary>
    [Fact]
    public void Движок_нашей_линии_проходит_сверку_целиком()
    {
        var declared = EngineRpcSurface.Methods.Select(m => m.Endpoint).ToList();

        Assert.Empty(EngineRpcSurface.MissingRequired(declared));
        Assert.Empty(EngineRpcSurface.LostKnown(declared));
        Assert.Empty(EngineRpcSurface.UnknownAdded(declared));
        Assert.Equal(0, EngineRpcSurface.KnownAddedPresent(declared));
    }

    /// <summary>
    /// ЛИНИЯ 0.2.1 ПРОХОДИТ ПО НУЖНЫМ ИМЕНАМ: 77 нужных объявлены, 7 снятых отсутствуют — и это
    /// законно, новых знакомых все 67, незнакомых ноль. Именно этот набор проба и назвала годным.
    /// </summary>
    [Fact]
    public void Движок_линии_0_2_1_проходит_по_нужным_именам()
    {
        var declared = EngineRpcSurface.Required.Select(m => m.Endpoint)
            .Concat(EngineRpcSurface.KnownAdded)
            .ToList();

        Assert.Equal(EngineRpcSurface.KnownOnDeclaredCount, declared.Count);
        Assert.Empty(EngineRpcSurface.MissingRequired(declared));
        Assert.Equal(EngineRpcSurface.Retired.Count, EngineRpcSurface.LostKnown(declared).Count);
        Assert.Empty(EngineRpcSurface.UnknownAdded(declared));
        Assert.Equal(EngineRpcSurface.KnownAddedCount, EngineRpcSurface.KnownAddedPresent(declared));
    }

    /// <summary>
    /// ЛИНИЯ 0.2.0 — НЕ ПОДМНОЖЕСТВО 0.2.1, И ЭТО ИЗМЕРЕНО (разведка E4 оставляла разницу
    /// неизмеренной; закрыто замером 18.10.2026): три имени есть ТОЛЬКО у `0.2.0-rc.2` —
    /// `terminal/follow`, `terminal/resize`, `terminal/write`. Панель их не знает, поэтому проба
    /// на 0.2.0 обязана назвать тревогой ровно их, а не «всё сломалось».
    /// </summary>
    [Fact]
    public void Движок_линии_0_2_0_называет_три_чужих_имени()
    {
        var declared = EngineRpcSurface.Required.Select(m => m.Endpoint)
            .Concat(EngineRpcSurface.KnownAdded)
            .Where(name => name is not ("terminal/attach" or "cotTranslation/limits"
                or "cotTranslation/translate" or "pluginMarket/listIndexes" or "pluginMarket/search"
                or "pluginMarket/setIndexes" or "remoteAccess/follow"))
            .Append("terminal/follow").Append("terminal/resize").Append("terminal/write")
            .ToList();

        Assert.Empty(EngineRpcSurface.MissingRequired(declared));

        var unknown = EngineRpcSurface.UnknownAdded(declared);

        Assert.Equal(
            new[] { "terminal/follow", "terminal/resize", "terminal/write" },
            unknown.ToArray());
    }

    // ---- опись движка ------------------------------------------------------------------------

    /// <summary>
    /// ОПИСЬ ДВИЖКА ЧИТАЕТСЯ У НЕГО САМОГО и сверяется в ОБЕ стороны: и «наше имя пропало»,
    /// и «у движка появилось незнакомое». Дерево движка подложено своё — проверка не зависит
    /// от того, стоит ли движок на машине.
    /// </summary>
    [Fact]
    public void Опись_движка_читается_и_сверяется_в_обе_стороны()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-rpc-decl-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Path.Combine(root, "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// движок");
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"9.9.9-probe\"}");

            var plugin = Path.Combine(package, "node_modules", "@deepseek-ai", "dsh-probe", "lib");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(
                Path.Combine(plugin, "typert.remote-client.d.ts"),
                "interface TypertRemoteMap {\n  'goals/clear': (a: string) => Promise<RemoteResult<void>>\n  'session/list': (b: object) => Promise<RemoteResult<object>>\n}\n" +
                "interface TypertRemoteScopeMap {\n  'agent:fileReferences/list': (q: string) => Promise<RemoteResult<object>>\n}\n");

            var declaration = EngineRpcDeclarations.Read(Path.Combine(package, "lib", "bin.js"));

            Assert.Equal("9.9.9-probe", declaration.Version);
            Assert.Equal(new[] { "goals/clear", "session/list" }, declaration.Endpoints.OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Single(declaration.Files);

            // Проверяемое семейство из области (`agent:…`) в опись НЕ попадает: это не то же имя.
            Assert.DoesNotContain("agent:fileReferences/list", declaration.Endpoints);

            var pinned = new[]
            {
                new EngineRpcMethod("goals/clear", EngineRpcMode.Unary, false),
                new EngineRpcMethod("session/list", EngineRpcMode.Unary, true),
                new EngineRpcMethod("session/gone", EngineRpcMode.Unary, true),
            };

            Assert.Equal(new[] { "session/gone" }, EngineRpcDeclarations.Gone(declaration, pinned));
            Assert.Empty(EngineRpcDeclarations.Missing(declaration, pinned));

            var narrower = new[] { new EngineRpcMethod("goals/clear", EngineRpcMode.Unary, false) };
            Assert.Equal(new[] { "session/list" }, EngineRpcDeclarations.Missing(declaration, narrower));
            Assert.Empty(EngineRpcDeclarations.Gone(declaration, narrower));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ПЛОСКАЯ РАСКЛАДКА СВОЕЙ УСТАНОВКИ — та, которой панель будет ставить движок сама
    /// (`npm install --prefix <папка>`, этап E3): пакеты движка лежат **рядом** с ним, в том же
    /// `node_modules\@deepseek-ai`, а не внутри пакета движка.
    ///
    /// ⚠️ Дефект, найденный замером 10.10.2026 на `0.2.0-rc.2`: объявления на диске **есть**
    /// (26 файлов), а панель говорила «рядом с движком нет node_modules/@deepseek-ai — сверять
    /// нечего» и не называла даже версию движка. Проверка держит обе раскладки.
    /// </summary>
    [Fact]
    public void Опись_читается_и_в_плоской_раскладке_своей_установки()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-rpc-flat-" + Guid.NewGuid().ToString("N"));

        try
        {
            // <корень>\node_modules\@deepseek-ai\dsh\lib\bin.js — движок лежит рядом со своими пакетами
            var package = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// движок");
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"9.9.9-flat\"}");

            var plugin = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh-probe", "lib");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(
                Path.Combine(plugin, "typert.remote-client.d.ts"),
                "interface TypertRemoteMap {\n  'session/list': (a: object) => Promise<RemoteResult<object>>\n  'session/page': (b: object) => Promise<RemoteResult<object>>\n}\n");

            var declaration = EngineRpcDeclarations.Read(Path.Combine(package, "lib", "bin.js"));

            Assert.Equal("9.9.9-flat", declaration.Version);
            Assert.Equal(new[] { "session/list", "session/page" }, declaration.Endpoints.OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Single(declaration.Files);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ВЛОЖЕННАЯ РАСКЛАДКА ГЛАВНЕЕ ПЛОСКОЙ — и лишних имён не приносит. Рядом с движком бывают
    /// и **чужие** пакеты `@deepseek-ai` (у человека их сколько угодно); читать обе раскладки сразу
    /// значило бы объявить чужие имена именами движка и уронить проверку «у движка появилось
    /// незнакомое» ложной тревогой.
    /// </summary>
    [Fact]
    public void Вложенная_раскладка_главнее_плоской()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-rpc-both-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// движок");
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"9.9.9-both\"}");

            // Свои пакеты движка — вложенные.
            var own = Path.Combine(package, "node_modules", "@deepseek-ai", "dsh-own", "lib");
            Directory.CreateDirectory(own);
            File.WriteAllText(Path.Combine(own, "typert.remote-client.d.ts"), "interface TypertRemoteMap {\n  'session/list': (a: object) => Promise<RemoteResult<object>>\n}\n");

            // Чужой пакет рядом — его имён в описи движка быть не должно.
            var foreign = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh-foreign", "lib");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "typert.remote-client.d.ts"), "interface TypertRemoteMap {\n  'foreign/method': (a: object) => Promise<RemoteResult<object>>\n}\n");

            var declaration = EngineRpcDeclarations.Read(Path.Combine(package, "lib", "bin.js"));

            Assert.Equal(new[] { "session/list" }, declaration.Endpoints);
            Assert.DoesNotContain("foreign/method", declaration.Endpoints);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ВЕРСИЯ ЧИТАЕТСЯ ДАЖЕ КОГДА СВЕРЯТЬ НЕЧЕГО. «Объявлений нет» — это про имена, а не про версию:
    /// прежний код возвращал «не знаю» с ПУСТОЙ версией, и в отчёте стояло «версия «»», а умение
    /// «Версия движка» читалось как беда (дефект, найденный замером 10.10.2026).
    /// </summary>
    [Fact]
    public void Версия_движка_читается_даже_когда_сверять_нечего()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-rpc-ver-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Path.Combine(root, "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// движок");
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"9.9.9-alone\"}");

            var declaration = EngineRpcDeclarations.Read(Path.Combine(package, "lib", "bin.js"));

            Assert.Equal("9.9.9-alone", declaration.Version);
            Assert.Empty(declaration.Endpoints);
            Assert.NotEmpty(declaration.Detail);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Опись не прочиталась — это ответ «не знаю», а не пустая опись и не падение.</summary>
    [Fact]
    public void Без_движка_опись_говорит_что_не_прочиталась()
    {
        var declaration = EngineRpcDeclarations.Read(Path.Combine(Path.GetTempPath(), "нет-такого-движка", "lib", "bin.js"));

        Assert.Empty(declaration.Endpoints);
        Assert.Empty(declaration.Version);
        Assert.NotEmpty(declaration.Detail);
    }

    // ---- подложный движок --------------------------------------------------------------------

    private static string Envelope(string result) => "{\"type\":\"server-response\",\"rpcId\":\"probe\",\"result\":" + result + "}";

    private static string Error(string code) =>
        "{\"ok\":false,\"error\":{\"code\":\"" + code + "\",\"message\":\"probe\",\"details\":{}}}";

    /// <summary>
    /// ДВИЖОК НА СВОЁМ СОКЕТЕ: слушает петлю на порту, который выдала система, и отвечает тем,
    /// что скажет проверка. Тело и путь каждого запроса запоминаются — по ним и проверяется форма.
    /// </summary>
    private sealed class RawEngine : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string, string, (int Status, string ContentType, string Body)> _handler;
        private readonly string _setCookie;
        private readonly Thread _worker;
        private readonly List<(string Path, string Body)> _seen = new();
        private volatile bool _stop;

        public RawEngine(
            Func<string, string, string, (int Status, string ContentType, string Body)> handler,
            string setCookie = "")
        {
            _handler = handler;
            _setCookie = setCookie;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _worker = new Thread(Loop) { IsBackground = true };
            _worker.Start();
        }

        public int Port { get; }

        public IReadOnlyList<(string Path, string Body)> Seen
        {
            get { lock (_seen) return _seen.ToList(); }
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    using var client = _listener.AcceptTcpClient();
                    Answer(client);
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private void Answer(TcpClient client)
        {
            using var stream = client.GetStream();

            var (method, path, _, body) = ReadRequest(stream);
            if (method.Length == 0) return;

            lock (_seen) _seen.Add((path, body));

            var (status, contentType, text) = _handler(path, body, method);
            var payload = Encoding.UTF8.GetBytes(text ?? string.Empty);

            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(status == 200 ? "OK" : "ERR").Append("\r\n")
                .Append("Content-Type: ").Append(contentType).Append("\r\n")
                .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
                .Append("Connection: close\r\n");

            if (_setCookie.Length > 0) head.Append("Set-Cookie: ").Append(_setCookie).Append("\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static (string Method, string Path, string Cookie, string Body) ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return (string.Empty, string.Empty, string.Empty, string.Empty);

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            var head = text.ToString()[..headerEnd];
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');

            var length = 0;
            var cookie = string.Empty;

            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), out length);

                if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    cookie = line["Cookie:".Length..].Trim();
            }

            var body = text.ToString()[(headerEnd + 4)..];

            while (body.Length < length)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                body += Encoding.UTF8.GetString(buffer, 0, read);
            }

            return (first.Length > 0 ? first[0] : string.Empty, first.Length > 1 ? first[1] : string.Empty, cookie, body);
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
        }
    }
}
