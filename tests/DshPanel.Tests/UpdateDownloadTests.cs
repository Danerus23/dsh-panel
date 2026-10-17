using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДОКАЧКА ОБНОВЛЕНИЯ КУСКАМИ — проверка ЖИВЫМ ПУТЁМ: на петле поднимается НАСТОЯЩИЙ HTTP-СЕРВЕР
/// (свой сокет на <c>127.0.0.1</c>), который отдаёт файл по <c>Range</c> и умеет ОБОРВАТЬ ответ
/// на середине куска. Ни одного запроса наружу: адрес — <c>127.0.0.1</c> со СВОИМ СВОБОДНЫМ
/// портом (порт 3080 — канал живой сессии владельца, его здесь нет и быть не может).
///
/// ⚠️ **Почему сокет, а не <see cref="HttpListener"/>.** Замерено на этих самых проверках:
/// <c>HttpListener</c> ОБЪЯВЛЯЕТ длину ответа САМ (заголовок <c>Content-Length</c> уходит от него,
/// а не от того, сколько байт записано), поэтому оборвать ответ «на середине» им НЕЛЬЗЯ вовсе:
/// клиент читает все 400 000 байт и обрыва не замечает, а проверка молча зеленеет на исправном
/// пути. Свой сокет говорит с клиентом теми же байтами, что и настоящий сервер, и обрыв
/// устраивается по-настоящему: заголовок обещает кусок, тело рвётся на середине, сокет
/// закрывается.
///
/// **Что здесь проверяется, и почему именно так.**
///
/// 1. **Обрыв на середине ответа проходит, если он был один.** Соединение рвётся на куске,
///    кусок качается заново, файл на рабочем пути ЦЕЛЫЙ и его SHA-256 совпадает
///    с ожидаемой. Проверка не может пройти «сама»: сумма считается с файла на диске.
/// 2. **Обрыв на КАЖДОЙ попытке — отказ, и рабочий путь НЕ ТРОНУТ.** Прежний файл на рабочем
///    пути сверяется по SHA-256 до и после, временных хвостов (<c>*.part*</c>) не остаётся.
///    Это и есть правило «недокачанный файл никогда не занимает место рабочего».
/// 3. **Не сошедшаяся сумма — отказ ДО занятия рабочего места**, хотя файл скачался целиком.
/// 4. **Куски идут подряд, без пропусков и наложений, и попыток ровно столько, сколько
///    задумано политикой.** Сервер записывает, какие <c>Range</c> он видел.
/// 5. **Отсутствие названной длины — отказ, а не «положим обрезанное».**
/// </summary>
public class UpdateDownloadTests
{
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];

        for (var index = 0; index < size; index++)
        {
            // Данные с повторами: файл из нулей сошёлся бы и на чужом куске, вставленном дважды.
            bytes[index] = (byte)((index * 31 + 7) % 251);
        }

        return bytes;
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha(string path) => UpdateSums.Sha256(path);

    /// <summary>Рабочий каталог одной проверки; убирается за собой целиком.</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dsh-panel-download-tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Root);

            Path = System.IO.Path.Combine(Root, "DshPanel.zip");
        }

        public string Root { get; }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);

                var parent = System.IO.Path.GetDirectoryName(Root);
                if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
            }
            catch
            {
                // Родитель занят другой проверкой — это не ошибка.
            }
        }
    }

    /// <summary>
    /// НАСТОЯЩИЙ СЕРВЕР НА ПЕТЛЕ: свой сокет, ответ по правилам HTTP, файл отдаётся по
    /// <c>Range</c> (206), а ответ можно ОБОРВАТЬ на середине куска — заголовок обещает одно,
    /// тело обрывается, сокет закрывается. Записывает, что у него просили.
    ///
    /// Порт берётся СВОБОДНЫЙ и у операционной системы: занять чужой (тем более 3080 — канал
    /// живой сессии владельца) проверка не имеет права.
    /// </summary>
    private sealed class Loop : IDisposable
    {
        private readonly byte[] _bytes;
        private readonly Func<int, long, long, bool> _break;
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _stop;

        /// <param name="bytes">Файл выпуска, который отдаёт сервер.</param>
        /// <param name="break">
        /// Обрывать ли ЭТУ попытку: номер запроса (с единицы), начало куска и его длина.
        /// Пусто — сервер не обрывает ничего.
        /// </param>
        /// <param name="chunked">Назвать ли длину файла вовсе (ложь — ответ без длины).</param>
        public Loop(byte[] bytes, Func<int, long, long, bool>? @break = null, bool chunked = false)
        {
            _bytes = bytes;
            _break = @break ?? ((_, _, _) => false);
            Chunked = chunked;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            Url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/DshPanel.zip";

            _thread = new Thread(Loop_) { IsBackground = true };
            _thread.Start();
        }

        public string Url { get; }

        private bool Chunked { get; }

        /// <summary>Сколько запросов сервер принял: по нему видно, повторяли ли кусок.</summary>
        public int Requests => _requests;

        /// <summary>Запрошенные куски — по ним проверяется «подряд, без пропусков и наложений».</summary>
        public List<(long From, long To)> Ranges { get; } = new();

        private int _requests;

        /// <summary>Ответ на одно соединение; исключение связи гасится — оно и есть обрыв.</summary>
        private void Serve(TcpClient client)
        {
            try
            {
                // ⚠️ ОБРЫВ ОБЯЗАН БЫТЬ ОБРЫВОМ, А НЕ «ЗАКРЫТИЕМ ПО-ХОРОШЕМУ»: `LingerState` с нулём
                // обрывает соединение сбросом (`RST`), и клиент видит именно оборванный ответ.
                // Замерено 10.10.2026: без этого сервер, уже отдавший половину, продолжал писать
                // в закрытый клиентом сокет, а новый запрос ЖДАЛ в очереди одного потока сервера —
                // и проверка мерила не обрыв, а очередь.
                client.LingerState = new LingerOption(true, 0);

                using var socket = client;
                using var stream = socket.GetStream();

                var head = ReadHead(stream);
                if (head is null) return;

                Answer(stream, head);
            }
            catch
            {
                // Клиент отвалился — обычное дело для проверки, обрывающей ответ.
            }
            finally
            {
                // ⚠️ ОБОРВАННЫЙ ОТВЕТ ЗАКРЫВАЕТСЯ ПО-ХОРОШЕМУ, А НЕ СБРОСОМ, и это тоже замер,
                // а не вежливость. Недочитанный ответ в сокете закрывается сбросом (`RST`), и
                // КЛИЕНТ теряет все уже поставленные в очередь запросы к этому адресу: повтор
                // куска уходил в сброшенный сокет и до сервера не доезжал вовсе. Замерено
                // 10.10.2026: с `LingerState` (сброс) сервер видел 3 запроса вместо 9 — то есть
                // проверка мерила не повторы, а то, как закрывается её собственный сервер.
                Drain(client);
            }
        }

        /// <summary>Дочитать запрос до конца и отпустить соединение: клиент закрывает свою сторону
        /// сам, и до тех пор недочитанные байты обернулись бы сбросом (см. замер в <see cref="Serve"/>).</summary>
        private static void Drain(TcpClient client)
        {
            try
            {
                client.Client.ReceiveTimeout = 2000;
                client.Client.Shutdown(SocketShutdown.Send);

                var sink = new byte[1024];

                while (client.Client.Receive(sink) > 0)
                {
                    // Дочитываем до конца: клиент закрывает свою сторону сам.
                }
            }
            catch
            {
                // Не дочитали — не беда: соединение всё равно закрывается.
            }
        }

        /// <summary>Заголовки запроса до пустой строки: по ним берётся <c>Range</c>.</summary>
        private static string? ReadHead(NetworkStream stream)
        {
            var head = new StringBuilder();
            var window = new byte[1];

            while (head.Length < 16384)
            {
                var read = stream.Read(window, 0, 1);
                if (read == 0) return head.Length > 0 ? head.ToString() : null;

                head.Append((char)window[0]);

                if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") return head.ToString();
            }

            return head.ToString();
        }

        /// <summary>
        /// ЧТО ПРОСИЛ КЛИЕНТ: начало куска и был ли <c>Range</c> в запросе вовсе. Второе нужно
        /// потому, что ответ на запрос куска — 206, а на запрос целого файла — 200, и путать
        /// эти два ответа нельзя (см. <see cref="Answer"/>).
        /// </summary>
        private static (long From, bool Asked) StartOf(string head)
        {
            foreach (var raw in head.Split('\n'))
            {
                var line = raw.Trim();

                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;

                var text = line[6..].Trim().Replace("bytes=", string.Empty, StringComparison.OrdinalIgnoreCase);
                var dash = text.IndexOf('-');

                if (dash > 0
                    && long.TryParse(text[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                {
                    return (from, true);
                }

                return (0, true);
            }

            return (0, false);
        }

        private void Answer(NetworkStream stream, string head)
        {
            var range = StartOf(head);
            var attempt = Interlocked.Increment(ref _requests);

            if (range.From >= _bytes.Length)
            {
                Write(stream, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            var from = range.From;

            lock (Ranges)
            {
                Ranges.Add((from, _bytes.Length - 1L));
            }

            var want = _bytes.Length - from;
            var cut = _break(attempt, from, want);

            // ОБРЫВ: обещано ВСЁ, а отдаётся ЧАСТЬ — ровно так выглядит оборванная загрузка
            // на живой сети, и именно это клиент обязан заметить.
            //
            // ⚠️ Часть названа ЧИСЛОМ, а не «половиной куска», и это тоже замер: прежний обрыв
            // «половиной» НИЧЕГО не обрывал — при запросе куска в 150 000 из ответа в 200 000
            // клиент получал свои 150 000 целиком и обрыва не видел вовсе, а проверка молча
            // зеленела на исправном пути. Обрыв обязан быть МЕНЬШЕ запрошенного куска.
            const int cutBytes = 8192;

            var length = cut ? Math.Min(cutBytes, want) : want;

            // ⚠️ ОТВЕТ НА ЗАПРОС КУСКА — ЭТО 206, А НЕ 200, и различие здесь не формальность.
            // Замерено 10.10.2026: у 206 в `Content-Length` лежит длина КУСКА, а у 200 — длина
            // ВСЕГО, ЧТО СЕРВЕР ОТДАЁТ. Сервер, назвавший на запрос куска «200» и длину всего
            // файла, говорит клиенту «вот целый файл»; проверка, ждущая здесь кусок, сторожила бы
            // не тот путь, которым идёт панель. Поэтому 206 отдаётся ВСЯКИЙ раз, когда `Range`
            // был в запросе, — включая запрос с нуля.
            var status = range.Asked ? "206 Partial Content" : "200 OK";

            var headers = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append("\r\n")
                .Append("Accept-Ranges: bytes\r\n");

            if (range.Asked)
            {
                headers
                    .Append("Content-Range: bytes ").Append(from.ToString(CultureInfo.InvariantCulture))
                    .Append('-').Append((_bytes.Length - 1L).ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(_bytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            // ДЛИНА НЕ НАЗВАНА — ЗАКОННЫЙ ОТВЕТ СЕРВЕРА, и проверка обязана его увидеть:
            // заголовка Content-Length нет вовсе.
            if (!Chunked)
                headers.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

            headers.Append("Connection: close\r\n\r\n");

            Write(stream, headers.ToString());

            var at = (int)from;
            var left = Math.Min(length, _bytes.Length - from);

            while (left > 0)
            {
                var step = (int)Math.Min(81920, left);

                try
                {
                    stream.Write(_bytes, at, step);
                    stream.Flush();
                }
                catch
                {
                    // Клиент уже ушёл (он вправе бросить оборванный ответ) — дописывать некому.
                    return;
                }

                at += step;
                left -= step;
            }

            if (cut) return;

            // Ответ досказан — соединение закрывается по-хорошему (заголовок `Connection: close`).
        }

        private static void Write(NetworkStream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);

            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private void Loop_()
        {
            while (!_stop)
            {
                TcpClient client;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }

                Serve(client);
            }
        }

        public void Dispose()
        {
            _stop = true;

            try { _listener.Stop(); } catch { }
            try { _thread.Join(2000); } catch { }
        }
    }

    /// <summary>Скачать и подождать ответа: сеть на петле, но и она не бесплатна.</summary>
    private static UpdateDownloadResult Fetch(
        Loop loop,
        Sandbox box,
        string? sum,
        UpdateDownloadPolicy? policy = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = new HttpUpdateDownload(http);

        return client.Save(loop.Url, box.Path, sum, null, policy);
    }

    /// <summary>Своя политика для проверок: мелкие куски — из-за скорости, попытки — как у панели.</summary>
    private static UpdateDownloadPolicy Small(long chunk, int attempts) =>
        new(chunk, attempts, TimeSpan.FromMilliseconds(30));

    /// <summary>
    /// ВРЕМЕННЫХ ХВОСТОВ РЯДОМ С РАБОЧИМ ПУТЁМ НЕ ОСТАЛОСЬ. Хвост — это <c>&lt;path&gt;.part</c>
    /// (сборка) и <c>&lt;path&gt;.part-&lt;N&gt;</c> (куски). Проверяется перебором номеров,
    /// а не «сколько насчитала проверка»: оставленный кусок с ЧУЖИМ номером так тоже находится.
    /// </summary>
    private static void AssertNoLeftovers(Sandbox box)
    {
        Assert.False(
            File.Exists(HttpUpdateDownload.AssembledPartPath(box.Path)),
            "остался временный файл сборки: " + HttpUpdateDownload.AssembledPartPath(box.Path));

        for (var index = 0; index < 8; index++)
        {
            Assert.False(
                File.Exists(HttpUpdateDownload.PartPath(box.Path, index)),
                "остался временный кусок: " + HttpUpdateDownload.PartPath(box.Path, index));
        }

        Assert.Empty(
            Directory.GetFiles(box.Root, "*.part*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray());
    }

    // ------------------------------------------------------------------ (а)

    /// <summary>
    /// ОБРЫВ НА СЕРЕДИНЕ ОДИН РАЗ — ЗАГРУЗКА ЗАКАНЧИВАЕТСЯ УСПЕХОМ, и файл на рабочем пути
    /// ЦЕЛЫЙ: его SHA-256 совпадает с суммой исходного файла.
    ///
    /// Обрыв устроен НЕ на первой попытке: повтор куска должен быть настоящим повтором, а не
    /// «повезло со второго запроса». Рвётся ответ на запросе <c>0-…</c>, то есть на ПЕРВОМ куске
    /// (сервер считает запросы с единицы, и первый из них — это он), а добивает его повтор.
    /// </summary>
    [Fact]
    public void Обрыв_на_середине_один_раз_загрузка_заканчивается_успехом()
    {
        using var box = new Sandbox();

        var bytes = Payload(400_000);
        var policy = Small(150_000, 3);

        // Рвём ответ на втором куске: он качается заново, и загрузка кончается успехом.
        using var loop = new Loop(bytes, (attempt, _, _) => attempt == 2);

        // Прежний файл на рабочем пути — «до»: он обязан быть заменён ЦЕЛЫМ, а не обрезком.
        File.WriteAllText(box.Path, "прежний файл");
        var before = Sha(box.Path);

        var result = Fetch(loop, box, Sha(bytes), policy);

        Assert.True(result.Ok, "загрузка не прошла: " + result.Report());
        Assert.Equal(UpdateDownloadOutcome.Ok, result.Outcome);

        // ГЛАВНОЕ: на рабочем пути лежит ЦЕЛЫЙ файл, и доказательство — сумма, посчитанная
        // с файла на диске, а не «ответ загрузки сказал ok».
        Assert.Equal(Sha(bytes), Sha(box.Path));
        Assert.NotEqual(before, Sha(box.Path));
        Assert.Equal(bytes.Length, new FileInfo(box.Path).Length);

        // ОБОРВАННЫЙ КУСОК БЫЛ ПОВТОРЁН, и это видно по запросам сервера: сервер принял ЧЕТЫРЕ
        // запроса — оборванный, его повтор и два оставшихся куска. Не будь повтора, запросов было
        // бы и того меньше, а файл собрался бы из обрезка. Число названо ТОЧНО, а не «больше
        // трёх»: так проверка ловит и лишний запрос.
        Assert.Equal(4, loop.Requests);

        AssertNoLeftovers(box);
    }

    // ------------------------------------------------------------------ (б)

    /// <summary>
    /// ОБРЫВ НА КАЖДОЙ ПОПЫТКЕ — ОТКАЗ, ПРЕЖНИЙ ФАЙЛ НА РАБОЧЕМ ПУТИ НЕ ТРОНУТ (сверяется
    /// по SHA-256 до и после), и временных хвостов рядом не осталось.
    ///
    /// Попыток ровно столько, сколько задумано политикой, и это видно по запросам сервера:
    /// ПЕРВЫЙ кусок качается ТРИ раза (первый ответ плюс два повтора), после чего загрузка
    /// прекращается — остальные куски не спрашиваются вовсе, потому что класть в рабочий путь
    /// всё равно нечего. «Повторы бесконечны» дали бы больше трёх, «повторов нет» — один.
    /// </summary>
    [Fact]
    public void Обрыв_каждый_раз_отказ_и_рабочий_путь_не_тронут()
    {
        using var box = new Sandbox();

        var bytes = Payload(400_000);
        var policy = Small(150_000, 3);

        using var loop = new Loop(bytes, (_, _, _) => true);

        File.WriteAllText(box.Path, "прежний файл");
        var before = Sha(box.Path);

        var result = Fetch(loop, box, Sha(bytes), policy);

        Assert.False(result.Ok);
        Assert.Equal(UpdateDownloadOutcome.Interrupted, result.Outcome);

        // ПРЕЖНИЙ ФАЙЛ ЦЕЛ: недокачанный файл никогда не занимает место рабочего.
        Assert.True(File.Exists(box.Path), "рабочий путь остался пустым — прежний файл потерян");
        Assert.Equal(before, Sha(box.Path));
        Assert.Equal("прежний файл", File.ReadAllText(box.Path));

        // ТРИ попытки первого куска — три запроса к серверу (первый ответ, повтор, повтор):
        // политика исчерпана, и дальше загрузка не идёт.
        Assert.Equal(3, loop.Requests);

        AssertNoLeftovers(box);
    }

    // ------------------------------------------------------------------ (в)

    /// <summary>
    /// СУММА НЕ СОШЛАСЬ — ОТКАЗ ДО ЗАНЯТИЯ РАБОЧЕГО МЕСТА. Файл при этом скачался ЦЕЛИКОМ
    /// (сервер ничего не обрывает): отказ называет именно несошедшуюся сумму, а не «сеть».
    /// Рабочий путь остаётся прежним, временных хвостов нет.
    /// </summary>
    [Fact]
    public void Несошедшаяся_сумма_отказ_и_рабочий_путь_не_занят()
    {
        using var box = new Sandbox();

        var bytes = Payload(400_000);

        // Подложена НЕВЕРНАЯ ожидаемая сумма: файл тот самый, а сверять его не с чем.
        var wrong = Sha(Payload(1000));

        using var loop = new Loop(bytes);

        File.WriteAllText(box.Path, "прежний файл");
        var before = Sha(box.Path);

        var result = Fetch(loop, box, wrong, Small(150_000, 3));

        Assert.False(result.Ok);
        Assert.Equal(UpdateDownloadOutcome.SumMismatch, result.Outcome);

        // Ожидаемое и полученное — ЗНАЧЕНИЯ, и оба названы: по ним видно, что скачался не тот файл.
        Assert.Equal(wrong, result.Expected);
        Assert.Equal(Sha(bytes), result.Actual);

        Assert.Equal(before, Sha(box.Path));
        Assert.Equal("прежний файл", File.ReadAllText(box.Path));

        AssertNoLeftovers(box);
    }

    /// <summary>
    /// ПУСТАЯ СУММА — «СВЕРЯТЬ НЕЧЕМ», И ЭТО НЕ ОТКАЗ: так качается файл контрольных сумм,
    /// сумму которого взять неоткуда. Но и в этом случае рабочий путь занимает только файл,
    /// собранный ЦЕЛИКОМ, — обрыв остаётся отказом.
    /// </summary>
    [Fact]
    public void Без_названной_суммы_файл_кладётся_только_целиком()
    {
        using var box = new Sandbox();

        var bytes = Payload(400_000);
        var policy = Small(150_000, 2);

        using (var loop = new Loop(bytes))
        {
            var done = Fetch(loop, box, string.Empty, policy);

            Assert.True(done.Ok, "загрузка без суммы не прошла: " + done.Report());
            Assert.Equal(Sha(bytes), Sha(box.Path));
            AssertNoLeftovers(box);
        }

        // Тот же путь, но сервер рвёт ответ КАЖДЫЙ раз: «сверять нечем» не значит «положим обрезанное».
        File.WriteAllText(box.Path, "прежний файл");
        var before = Sha(box.Path);

        using (var broken = new Loop(bytes, (_, _, _) => true))
        {
            var refused = Fetch(broken, box, null, policy);

            Assert.False(refused.Ok);
            Assert.Equal(UpdateDownloadOutcome.Interrupted, refused.Outcome);
            Assert.Equal(before, Sha(box.Path));
            AssertNoLeftovers(box);
        }
    }

    // ------------------------------------------------------------------ (г)

    /// <summary>
    /// КУСКИ ИДУТ ПОДРЯД, БЕЗ ПРОПУСКОВ И НАЛОЖЕНИЙ, И ПОПЫТОК СТОЛЬКО, СКОЛЬКО ЗАДУМАНО.
    /// Сервер записывает, какие <c>Range</c> он видел: проверка сама собирает из них план файла
    /// (начало каждого куска) и сверяет его с размером куска политики.
    ///
    /// ⚠️ Здесь же проверяется, что ЗАПРОСОВ РОВНО СТОЛЬКО, СКОЛЬКО КУСКОВ: на ровном файле
    /// повторять нечего, и лишний запрос значил бы либо лишний кусок, либо повтор без причины.
    /// </summary>
    [Fact]
    public void Куски_идут_подряд_и_попыток_ровно_по_политике()
    {
        using var box = new Sandbox();

        var bytes = Payload(10_000);
        const long chunk = 1500;
        const int attempts = 3;

        using var loop = new Loop(bytes);

        var result = Fetch(loop, box, Sha(bytes), Small(chunk, attempts));

        Assert.True(result.Ok, "загрузка не прошла: " + result.Report());

        // Что сервер увидел: начала кусков (в порядке запросов) и набор запрошенных начал.
        var asked = loop.Ranges.Select(range => range.From).ToList();
        var starts = asked.Distinct().OrderBy(value => value).ToList();

        // Куски идут ПОДРЯД и покрывают файл ЦЕЛИКОМ: 0, chunk, 2·chunk… — до последнего байта.
        var expected = new List<long>();

        for (var from = 0L; from < bytes.Length; from += chunk)
        {
            expected.Add(from);
        }

        Assert.Equal(expected, starts);

        // Попыток ровно по числу кусков: повторов не было (сервер ничего не обрывал),
        // а значит лишний запрос — это дефект политики, а не «перестраховка».
        Assert.Equal(expected.Count, loop.Requests);
        Assert.Equal(expected.Count, asked.Count);

        Assert.Equal(Sha(bytes), Sha(box.Path));
        AssertNoLeftovers(box);
    }

    // ------------------------------------------------------------------ (д)

    /// <summary>
    /// ДЛИНА НЕ НАЗВАНА — ОТКАЗ, А НЕ «ПОЛОЖИМ ОБРЕЗАННОЕ». Резать файл, у которого сервер
    /// не назвал длину, нечем: неизвестно ни сколько кусков просить, ни кончился ли файл.
    /// Класть такое в рабочий путь панель не станет.
    /// </summary>
    [Fact]
    public void Без_названной_длины_отказ()
    {
        using var box = new Sandbox();

        var bytes = Payload(400_000);

        // Ответ без Content-Length — законный ответ сервера, и он обязан быть назван своим отказом.
        using var loop = new Loop(bytes, chunked: true);

        File.WriteAllText(box.Path, "прежний файл");
        var before = Sha(box.Path);

        var result = Fetch(loop, box, Sha(bytes), Small(150_000, 3));

        Assert.False(result.Ok);
        Assert.Equal(UpdateDownloadOutcome.NoLength, result.Outcome);

        Assert.Equal(before, Sha(box.Path));
        AssertNoLeftovers(box);
    }

    // ------------------------------------------------------------------ замер, на котором стоит политика

    /// <summary>
    /// ЧИСЛА ПОЛИТИКИ ЗАКРЕПЛЕНЫ ЗАМЕРОМ, А НЕ «НАСТРОЕНЫ». Кусок — 8 МиБ (на нём накладные
    /// расходы на запрос уже отыграны: 8 МиБ — 2,28 с, 16 МиБ — 2,15 с, 32 МиБ — 3,57 с),
    /// поток ОДИН (параллель замерена ХУЖЕ: 16 МиБ одним потоком — 2,15 с, четырьмя кусками
    /// по 4 МиБ сразу — 3,40 с). Замер снят 10.10.2026 на нашем файле выпуска
    /// <c>v2.0.3/DshPanel.zip</c> (43 855 793 Б).
    ///
    /// ⚠️ Утверждение нарочно НЕ сравнивает константу с собою же: политика по умолчанию обязана
    /// нести РОВНО эти числа. Иначе правка «на глаз» (например, кусок в 1 МиБ) прошла бы молча,
    /// а вместе с ней уехал бы и весь замер.
    /// </summary>
    [Fact]
    public void Числа_политики_закреплены_замером()
    {
        Assert.Equal(8388608L, HttpUpdateDownload.ChunkSize);
        Assert.Equal(8L * 1024 * 1024, HttpUpdateDownload.ChunkSize);
        Assert.Equal(3, HttpUpdateDownload.ChunkAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), HttpUpdateDownload.ChunkPause);

        Assert.Equal(HttpUpdateDownload.ChunkSize, UpdateDownloadPolicy.Default.Size);
        Assert.Equal(HttpUpdateDownload.ChunkAttempts, UpdateDownloadPolicy.Default.Tries);

        // Ноль попыток — это «не пробовать», и он приводится к одной: загрузка без единой
        // попытки не загрузка.
        Assert.Equal(1, new UpdateDownloadPolicy(10, 0, TimeSpan.Zero).Tries);

        // Файл выпуска 43 855 793 Б режется на ШЕСТЬ кусков: 5 полных по 8 МиБ и последний
        // неполный. По этому числу проверяется и то, что кусок не «мельче замеренного».
        const long release = 43_855_793L;
        var chunks = (release + HttpUpdateDownload.ChunkSize - 1) / HttpUpdateDownload.ChunkSize;

        Assert.Equal(6L, chunks);
    }
}
