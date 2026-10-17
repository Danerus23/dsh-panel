using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DshPanel.Files;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЖИВАЯ ПРОБА ПОСТАВЛЕННОГО ДВИЖКА (этап E3.2) — проверки БЕЗ живого движка.
///
/// **Что здесь проверяется и почему именно это.** Проба поднимает НАСТОЯЩИЙ движок, и в обычном
/// наборе проверок её прогонять нельзя. Но у неё есть часть, от живого движка не зависящая вовсе, —
/// и она важнее всего остального: **что проба делает с МАШИНОЙ и что говорит, когда не вышло**.
/// Именно здесь живёт беда, которую иначе не поймать: не погашенный движок остаётся чужим
/// процессом на машине, а брошенный временный дом — вторым профилем на диске.
///
/// Поэтому двери подставные, а проверяется по ним ровно то, что видно снаружи: звался ли
/// <c>Stop</c> (и с тем ли портом), звался ли <c>Start</c>, чтó проба вернула и что сказала
/// строками. Подставной движок при этом НАСТОЯЩИЙ сокет — как в <c>EngineRpcTests</c>: иначе
/// «проба прошла» доказывало бы только то, что подставной объект умеет отвечать «да».
///
/// ⚠️ Порт 3080 не занимается НИКОГДА, а дом пробы — свой временный каталог под `%TEMP%`:
/// каталогов и профиля владельца эти проверки не касаются.
/// </summary>
public sealed class EngineLiveProbeTests
{
    /// <summary>
    /// ПОДНЯТЬ НЕ УДАЛОСЬ — <c>Stop</c> ОБЯЗАН БЫТЬ ЗВАН. Это не вежливость: подъём бывает
    /// наполовину удачным (движок уже запускается, а отпечатком ещё не ответил), и без гашения
    /// на машине остался бы чужой процесс, о котором панель уже забыла.
    /// </summary>
    [Fact]
    public void Проба_зовёт_остановку_даже_когда_поднять_не_удалось()
    {
        var root = TempRoot("fail");

        try
        {
            var bin = FakeEngineBin(root, "9.9.9-fail");
            var starts = 0;
            var stops = new List<int>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    starts++;
                    return new EngineLiveProbeRun(
                        false, 4181, string.Empty, "порт занят", new[] { "движок| отказ движка" });
                },
                port => stops.Add(port));

            var result = EngineLiveProbe.Run("node.exe", bin, Path.Combine(root, "home"), AppPaths.Under(root), doors);

            Assert.Equal(1, starts);
            Assert.Equal(new[] { 4181 }, stops.ToArray());  // именно тот порт, что назвала дверь
            Assert.False(result.Ok);
            Assert.Contains("порт занят", result.Detail, StringComparison.Ordinal);
            Assert.Contains(result.Lines, line => line.Contains("отказ движка", StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// ОБЪЯВЛЕНИЙ НЕТ — ПОДНИМАТЬ НЕЧЕГО: движок не читается, значит и спрашивать его не о чем.
    /// Проверка смотрит на счётчик: <c>Start</c> НЕ ЗВАЛСЯ, а отказ сказан словами.
    /// </summary>
    [Fact]
    public void Без_объявлений_движка_проба_даже_не_поднимает()
    {
        var root = TempRoot("decl");

        try
        {
            var noEngine = Path.Combine(root, "нет-такого-движка", "lib", "bin.js");
            var started = 0;
            var stopped = 0;

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    started++;
                    return new EngineLiveProbeRun(false, 4182, string.Empty, string.Empty, Array.Empty<string>());
                },
                _ => stopped++);

            var result = EngineLiveProbe.Run("node.exe", noEngine, Path.Combine(root, "home"), AppPaths.Under(root), doors);

            Assert.False(result.Ok);
            Assert.Equal(0, started);
            Assert.Equal(0, stopped);
            Assert.Contains("не читается", result.Detail, StringComparison.Ordinal);
            Assert.Contains(result.Lines, line => line.Contains("не читается", StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// ПОДНЯЛСЯ, А ПРОБА НЕ ПРОШЛА — «получилось» панель не говорит, и движок всё равно погашен.
    /// Движок здесь подложный и НЕ ОТВЕЧАЕТ: порт свободен, слушать на нём некому — ровно так
    /// выглядит движок, который поднялся и не отвечает.
    /// </summary>
    [Fact]
    public void Проба_не_прошла_значит_не_получилось_и_движок_погашен()
    {
        var root = TempRoot("noanswer");

        try
        {
            var bin = FakeEngineBin(root, "9.9.9-noanswer");
            var home = Path.Combine(root, "home");
            var dead = FreePort.Find();
            var stops = new List<int>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    Directory.CreateDirectory(home);
                    return new EngineLiveProbeRun(
                        true, dead, $"http://127.0.0.1:{dead}/?token=probe", "сервер отвечает", Array.Empty<string>());
                },
                port =>
                {
                    stops.Add(port);
                    TreeDeletion.Remove(home);
                });

            var result = EngineLiveProbe.Run("node.exe", bin, home, AppPaths.Under(root), doors);

            Assert.False(result.Ok);
            Assert.Equal(dead, Assert.Single(stops));
            Assert.Contains(
                result.Lines,
                line => line.StartsWith("вход по ссылке входа состоялся", StringComparison.Ordinal)
                        && line.EndsWith("= False", StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// УСПЕШНЫЙ ПУТЬ: имена и версия проброшены, порт назван, движок погашен. Подложный движок
    /// отвечает как настоящий, объявления подложены ВСЕ (84 имени из списка панели) — поэтому
    /// проверка доходит до живой части пробы, а не спотыкается на описи.
    /// </summary>
    [Fact]
    public void Успешный_путь_пробрасывает_имена_версию_и_порт()
    {
        var root = TempRoot("ok");

        try
        {
            using var engine = new FakeEngine();
            var bin = FakeEngineBin(root, "9.9.9-live");
            var home = Path.Combine(root, "home");
            var stops = new List<int>();
            var steps = new List<string>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    Directory.CreateDirectory(home);
                    return new EngineLiveProbeRun(
                        true, engine.Port, $"http://127.0.0.1:{engine.Port}/?token=probe",
                        "сервер отвечает отпечатком", Array.Empty<string>());
                },
                port =>
                {
                    stops.Add(port);
                    TreeDeletion.Remove(home);
                });

            var result = EngineLiveProbe.Run("node.exe", bin, home, AppPaths.Under(root), doors, steps.Add);

            Assert.True(result.Ok, string.Join(" | ", result.Lines));
            Assert.Equal(EngineRpcSurface.Methods.Count, result.Total);
            Assert.Equal(EngineRpcSurface.Methods.Count, result.Recognised);
            Assert.Equal("9.9.9-live", result.Version);
            Assert.Equal(engine.Port, Assert.Single(stops));

            var port = engine.Port.ToString(CultureInfo.InvariantCulture);
            Assert.Contains(port, result.Detail, StringComparison.Ordinal);
            Assert.Contains("9.9.9-live", result.Detail, StringComparison.Ordinal);

            // Ход пробы и её строки — одно и то же: окно показывает человеку ровно то, что вернулось.
            Assert.Equal(steps.Count, result.Lines.Count);
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// УБОРКА ЗА СОБОЙ: дом убран — и это ПРОВЕРЕНО (каталога нет), путь дома назван в строках,
    /// а <c>Stop</c> позван с тем же портом, что называла дверь подъёма.
    /// </summary>
    [Fact]
    public void Уборка_за_собой_видна_и_идёт_на_тот_же_порт()
    {
        var root = TempRoot("clean");

        try
        {
            using var engine = new FakeEngine();
            var bin = FakeEngineBin(root, "9.9.9-clean");
            var home = Path.Combine(root, "home");
            var stops = new List<int>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    Directory.CreateDirectory(home);
                    File.WriteAllText(Path.Combine(home, "профиль.txt"), "временный дом пробы");
                    return new EngineLiveProbeRun(
                        true, engine.Port, $"http://127.0.0.1:{engine.Port}/?token=probe",
                        "сервер отвечает", Array.Empty<string>());
                },
                port =>
                {
                    stops.Add(port);
                    TreeDeletion.Remove(home);
                });

            var result = EngineLiveProbe.Run("node.exe", bin, home, AppPaths.Under(root), doors);

            Assert.Equal(engine.Port, Assert.Single(stops));
            Assert.False(Directory.Exists(home));
            Assert.Contains(
                result.Lines,
                line => line.Contains("временный дом пробы убран = True", StringComparison.Ordinal));
            Assert.Contains(result.Lines, line => line.Contains(home, StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// А ЕСЛИ НЕ УБРАЛОСЬ — проба ГОВОРИТ. Молчание здесь было бы ложью: дом остался на диске
    /// (второй профиль движка рядом с панелью), и человек обязан об этом узнать — путь назван
    /// и в строках, и в <c>Detail</c>, который показывает окно обновления.
    /// </summary>
    [Fact]
    public void Брошенный_дом_назван_словами_в_ответе()
    {
        var root = TempRoot("left");

        try
        {
            using var engine = new FakeEngine();
            var bin = FakeEngineBin(root, "9.9.9-left");
            var home = Path.Combine(root, "home");
            var stops = new List<int>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    Directory.CreateDirectory(home);
                    return new EngineLiveProbeRun(
                        true, engine.Port, $"http://127.0.0.1:{engine.Port}/?token=probe",
                        "сервер отвечает", Array.Empty<string>());
                },
                port => stops.Add(port));   // уборки НЕТ — ровно та беда, о которой проба обязана сказать

            var result = EngineLiveProbe.Run("node.exe", bin, home, AppPaths.Under(root), doors);

            Assert.Equal(engine.Port, Assert.Single(stops));
            Assert.True(Directory.Exists(home), "подставные двери дом не убирают — проверке нужно, чтобы он остался");
            Assert.Contains(result.Lines, line => line.Contains("убран = False", StringComparison.Ordinal));
            Assert.Contains(home, result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// НАСТОЯЩИЕ ДВЕРИ БЕЗ ДВИЖКА: подъём не удался — и за собой двери убрали САМИ.
    ///
    /// Движок здесь не поднимается вовсе (Node назван несуществующим путём), поэтому проверка
    /// безопасна и быстра — но она проверяет то, чего подставные двери доказать НЕ МОГУТ:
    /// НАСТОЯЩУЮ уборку временного дома (<see cref="TreeDeletion"/>) и главную красную линию —
    /// файлы состояния ПАНЕЛИ не тронуты, потому что проба пишет СВОИ, под своим домом.
    /// </summary>
    [Fact]
    public void Настоящие_двери_убирают_дом_и_не_трогают_файлы_панели()
    {
        var root = TempRoot("real");
        var panel = TempRoot("panel");

        try
        {
            var bin = FakeEngineBin(root, "9.9.9-real");
            var home = Path.Combine(root, "engine-probe-9.9.9-real");
            var paths = AppPaths.Under(panel);

            var result = EngineLiveProbe.Run(
                Path.Combine(root, "нет-такого-node.exe"), bin, home, paths, EngineLiveProbeDoors.Real);

            Assert.False(result.Ok);
            Assert.False(Directory.Exists(home), "временный дом пробы остался на диске");
            Assert.Contains(
                result.Lines,
                line => line.Contains("временный дом пробы убран = True", StringComparison.Ordinal));

            // КРАСНАЯ ЛИНИЯ: файлы состояния панели проба не заводит и не трогает.
            Assert.False(Directory.Exists(paths.StateDir), "проба завела каталог состояния панели");
            Assert.False(File.Exists(paths.EntryLinkFile));
            Assert.False(File.Exists(paths.OwnServerFile));
        }
        finally
        {
            TreeDeletion.Remove(root);
            TreeDeletion.Remove(panel);
        }
    }

    /// <summary>
    /// ПРОБА ПОВЕРХНОСТИ НА ПУСТОМ ПУТИ — отказ и понятные строки. Живой движок для этого
    /// не нужен вовсе: опись не читается, входа нет, признать нечего.
    /// </summary>
    [Fact]
    public void Пустая_проба_поверхности_отвечает_отказом_словами()
    {
        var root = TempRoot("rpc");

        try
        {
            var result = EngineRpcProbe.Measure(
                null, "http://127.0.0.1:1/?token=probe", 1, AppPaths.Under(root));

            Assert.False(result.Ok);
            Assert.Empty(result.Version);
            Assert.Equal(0, result.Recognised);
            Assert.Equal(EngineRpcSurface.Methods.Count, result.Total);
            Assert.Contains(result.Lines, line => line == "опись движка прочитана = False");
            Assert.Contains(result.Lines, line => line.StartsWith("движок: версия", StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// МЕСТО ПРОБЫ — СВОЁ, И ЭТО СТОРОЖИТСЯ. Проба вычищает своё место и удаляет его целиком,
    /// поэтому корень панели, дом движка или их предок местом пробы быть НЕ МОГУТ: одна опечатка
    /// в вызывающем снесла бы человеку профиль. Проверка обязана упасть, если предохранитель убрать.
    /// </summary>
    [Fact]
    public void Место_пробы_не_может_быть_корнем_панели_или_домом_движка()
    {
        var root = TempRoot("place");

        try
        {
            var paths = AppPaths.Under(root);
            var bin = FakeEngineBin(root, "9.9.9-place");

            // Дом движка заводится ЗАРАНЕЕ и с меткой внутри: «предохранитель не сработал» должно
            // быть видно по пропавшему файлу, а не по отсутствию папки, которой и не было.
            Directory.CreateDirectory(paths.DshHome);
            var marker = Path.Combine(paths.DshHome, "profiles.txt");
            File.WriteAllText(marker, "профиль на месте");

            var started = 0;
            var stopped = new List<int>();

            var doors = new EngineLiveProbeDoors(
                (_, _, _) =>
                {
                    started++;
                    return new EngineLiveProbeRun(true, 0, string.Empty, "поднят", Array.Empty<string>());
                },
                port => stopped.Add(port));

            // Корень панели, дом движка и ПРЕДОК корня — все три не годятся.
            var parent = Path.GetDirectoryName(root)!;

            foreach (var place in new[] { paths.Root, paths.DshHome, parent })
            {
                var result = EngineLiveProbe.Run("node.exe", bin, place, paths, doors);

                Assert.False(result.Ok);
                Assert.Contains("место пробы", result.Detail, StringComparison.Ordinal);
            }

            Assert.Equal(0, started);
            Assert.Empty(stopped);
            Assert.True(Directory.Exists(root), "предохранитель не сработал — корень тронут");
            Assert.True(File.Exists(marker), "предохранитель не сработал — дом движка тронут");
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    /// <summary>
    /// ПРОБА ВИДИТ ПОДЛОГ: движок, признавший ВЫДУМАННОЕ имя, не имеет права пройти пробу —
    /// иначе «имя признано» не значило бы ничего, и смена поверхности проходила бы незамеченной.
    /// </summary>
    [Fact]
    public void Проба_поверхности_видит_подлог()
    {
        var root = TempRoot("bogus");

        try
        {
            using var engine = new FakeEngine(recogniseBogus: true);
            var bin = FakeEngineBin(root, "9.9.9-bogus");

            var result = EngineRpcProbe.Measure(
                bin, $"http://127.0.0.1:{engine.Port}/?token=probe", engine.Port, AppPaths.Under(root));

            Assert.False(result.Ok);
            Assert.Equal(EngineRpcSurface.Methods.Count, result.Recognised);
            Assert.Contains(
                result.Lines,
                line => line.StartsWith("выдуманное имя движок НЕ признаёт", StringComparison.Ordinal)
                        && line.EndsWith("= False", StringComparison.Ordinal));
        }
        finally
        {
            TreeDeletion.Remove(root);
        }
    }

    // ---- подложное ---------------------------------------------------------------------------

    /// <summary>Свой временный корень. Каталогов владельца эти проверки не касаются вовсе.</summary>
    private static string TempRoot(string mark)
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-live-" + mark + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// ПОДЛОЖНЫЙ ПАКЕТ ДВИЖКА: <c>lib\bin.js</c>, <c>package.json</c> с версией и объявления ВСЕХ
    /// имён панели — ровно те, что читает <see cref="EngineRpcDeclarations"/>. Сверка «опись против
    /// списка» на нём сходится в обе стороны, поэтому проба доходит до живой части; ничего
    /// настоящего на машине для этого не нужно.
    /// </summary>
    private static string FakeEngineBin(string root, string version)
    {
        var package = Path.Combine(root, "dsh");
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// подложный движок");
        File.WriteAllText(
            Path.Combine(package, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"" + version + "\"}");

        var plugin = Path.Combine(package, "node_modules", "@deepseek-ai", "dsh-fake", "lib");
        Directory.CreateDirectory(plugin);

        var text = new StringBuilder("interface TypertRemoteMap {\n");

        foreach (var method in EngineRpcSurface.Methods)
            text.Append("  '").Append(method.Endpoint).Append("': (a: object) => Promise<RemoteResult<object>>\n");

        text.Append("}\n");
        File.WriteAllText(Path.Combine(plugin, EngineRpcDeclarations.DeclarationFileName), text.ToString());

        return Path.Combine(package, "lib", "bin.js");
    }

    /// <summary>
    /// ПОДЛОЖНЫЙ ДВИЖОК НА СВОЁМ СОКЕТЕ. Отвечает так же, как настоящий (форма снята замером
    /// 10.10.2026, разбор — в <c>EngineRpc.cs</c>): вход — <c>303</c> с <c>Set-Cookie</c>;
    /// без cookie — <c>401</c>; знакомое имя — <c>200</c> с нашим конвертом (доводы не те = имя есть);
    /// выдуманное — <c>404 not found</c>. Рукопожатие канала управления не даётся: на этом пути
    /// отвечает <c>404</c>, и умение «ответ на вопрос агента» честно читается как «нет».
    /// </summary>
    private sealed class FakeEngine : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _worker;
        private readonly bool _recogniseBogus;
        private volatile bool _stop;

        public FakeEngine(bool recogniseBogus = false)
        {
            _recogniseBogus = recogniseBogus;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _worker = new Thread(Loop) { IsBackground = true };
            _worker.Start();
        }

        public int Port { get; }

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

            var (method, path, cookie) = ReadRequest(stream);
            if (method.Length == 0) return;

            var (status, contentType, text, setCookie) = Decide(path, cookie);
            var payload = Encoding.UTF8.GetBytes(text);

            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(status == 200 ? "OK" : "ERR").Append("\r\n")
                .Append("Content-Type: ").Append(contentType).Append("\r\n")
                .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
                .Append("Connection: close\r\n");

            if (setCookie) head.Append("Set-Cookie: dsh-auth-probe=1; Path=/; HttpOnly\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private (int Status, string ContentType, string Body, bool SetCookie) Decide(string path, string cookie)
        {
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                // ССЫЛКА ВХОДА: обмен на cookie — так отвечает настоящий движок (303 + Set-Cookie).
                return (303, "text/plain", string.Empty, true);
            }

            if (cookie.Length == 0) return (401, "text/plain", "unauthorized", false);

            var endpoint = path["/api/".Length..];

            if (endpoint == "remote.mux") return (404, "text/plain", "not found", false);
            if (endpoint == EngineRpcSurface.BogusEndpoint && !_recogniseBogus)
                return (404, "text/plain", "not found", false);

            var result = endpoint == EngineRpcSurface.RealCallEndpoint
                ? "{\"ok\":true,\"value\":{\"items\":[]}}"
                : "{\"ok\":false,\"error\":{\"code\":\"gateway/arguments-invalid\",\"message\":\"probe\",\"details\":{}}}";

            return (200, "application/json",
                "{\"type\":\"server-response\",\"rpcId\":\"probe\",\"result\":" + result + "}", false);
        }

        private static (string Method, string Path, string Cookie) ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return (string.Empty, string.Empty, string.Empty);

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

            // Тело дочитывается, даже когда оно не нужно: закрыть соединение с невычитанным телом
            // значит отдать клиенту обрыв вместо ответа.
            var body = text.ToString()[(headerEnd + 4)..];

            while (body.Length < length)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                body += Encoding.UTF8.GetString(buffer, 0, read);
            }

            return (
                first.Length > 0 ? first[0] : string.Empty,
                first.Length > 1 ? first[1] : string.Empty,
                cookie);
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
        }
    }
}
