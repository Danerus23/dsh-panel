using System;
using System.Collections.Generic;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ССЫЛКА ВХОДА: разбор строки движка, порт, годность чужой ссылки и обезвреживание.
///
/// Всё здесь — ЧИСТЫЕ решения: ни диска, ни сети, ни браузера. Именно поэтому «принимает ли
/// сервер эту ссылку» перебирается по всем ответам, а не проверяется на живом сервере один раз.
///
/// ⚠️ В ссылке ТОКЕН (красная линия 7). Отдельная проверка ниже сторожит, что он вырезается
/// и что наружу из разбора уходит только обезвреженная строка.
/// </summary>
public class EntryLinkTests
{
    private const string Token = "SECRET-TOKEN-9f3a";

    private static string Line(int port) => $"dsh web: http://127.0.0.1:{port}/?token={Token}";

    /// <summary>Форма строки движка измерена на живом сервере (`docs\ENGINE.md` §6).</summary>
    [Fact]
    public void Ссылка_разбирается_из_строки_движка()
    {
        Assert.Equal(
            $"http://127.0.0.1:3080/?token={Token}",
            EntryLinkDecisions.Parse(Line(3080)));

        // Ссылка может стоять в середине строки и в окружении другого текста.
        Assert.Equal(
            "http://127.0.0.1:3097/?token=x",
            EntryLinkDecisions.Parse("префикс dsh web: http://127.0.0.1:3097/?token=x и хвост"));

        // Строка без ссылки, пустая и null — пусто, а не исключение.
        foreach (var none in new[] { "обычная строка", "dsh web:", "", "   ", null })
            Assert.Equal(string.Empty, EntryLinkDecisions.Parse(none));

        // И у https-ссылки разбор тот же: движок сегодня печатает http, но форма не приколочена.
        Assert.Equal("https://127.0.0.1:1/?token=y", EntryLinkDecisions.Parse("dsh web: https://127.0.0.1:1/?token=y"));
    }

    /// <summary>
    /// Токен вырезается ДО того, как строка попадёт в журнал или отчёт. Иначе он переживёт сессию
    /// в файле и уедет в резервную копию — ровно то, из-за чего в v1 ссылку и считали секретом.
    /// </summary>
    [Fact]
    public void Ссылка_уходит_в_журнал_только_обезвреженной()
    {
        var url = EntryLinkDecisions.Parse(Line(3080));
        var redacted = EntryLinkDecisions.Redacted(url);

        Assert.DoesNotContain(Token, redacted, StringComparison.Ordinal);
        Assert.Contains("token=***", redacted, StringComparison.Ordinal);

        // Адрес и порт при этом остаются: без них строка не объясняет ничего.
        Assert.Contains("127.0.0.1:3080", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Порт_берётся_из_ссылки_а_не_угадывается()
    {
        foreach (var port in new[] { 1, 80, 3080, 3081, 65535 })
            Assert.Equal(port, EntryLinkDecisions.PortOf($"http://127.0.0.1:{port}/?token=x"));

        // Ссылка БЕЗ порта не «порт 80»: про НАШ сервер она ничего не говорит.
        Assert.Equal(0, EntryLinkDecisions.PortOf("http://127.0.0.1/?token=x"));

        // Мусор и пустое — тоже 0, а не исключение.
        foreach (var bad in new[] { "не ссылка", "", null, "http://" })
            Assert.Equal(0, EntryLinkDecisions.PortOf(bad));
    }

    /// <summary>
    /// Ссылка годится, только если она про ЭТОТ порт. Вывод движка мог остаться от прежнего
    /// запуска, и открыть человеку вчерашний адрес хуже, чем не открыть.
    /// </summary>
    [Fact]
    public void Ссылка_сверяется_с_портом_сервера()
    {
        var url = $"http://127.0.0.1:3081/?token={Token}";

        Assert.True(EntryLinkDecisions.MatchesPort(url, 3081));
        Assert.False(EntryLinkDecisions.MatchesPort(url, 3080));
        Assert.False(EntryLinkDecisions.MatchesPort(url, 0));
        Assert.False(EntryLinkDecisions.MatchesPort(url, -1));

        // Ссылка без порта не совпадает ни с чем — и это честно.
        Assert.False(EntryLinkDecisions.MatchesPort("http://127.0.0.1/?token=x", 80));
    }

    /// <summary>
    /// ГОДНОСТЬ ЧУЖОЙ ССЫЛКИ — правило, выведенное ЗАМЕРОМ на живом сервере владельца 27.09.2026,
    /// и оно не такое, каким кажется: рабочий ответ там <b>303 SeeOther</b> с <c>Set-Cookie</c>,
    /// а не 200 и не 302. Наивное «200 или 302» сделало бы кнопку мёртвой ровно там, где она нужнее
    /// всего, поэтому таблица ответов записана здесь ЯВНО, а не формулой из кода.
    /// </summary>
    [Fact]
    public void Ссылка_годится_по_успешному_ответу_с_cookie()
    {
        // Замер: токен принят — 303 и cookie. Годится.
        Assert.True(EntryLinkDecisions.Accepts(LinkProbe.Answer(303, setCookie: true)));
        Assert.True(EntryLinkDecisions.Accepts(LinkProbe.Answer(302, setCookie: true)));
        Assert.True(EntryLinkDecisions.Accepts(LinkProbe.Answer(200, setCookie: true)));
        Assert.True(EntryLinkDecisions.Accepts(LinkProbe.Answer(301, setCookie: true)));

        // Кода мало: 200 БЕЗ cookie означает, что отвечает не вход в панель.
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(200, setCookie: false)));
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(303, setCookie: false)));

        // Замер: тот же адрес БЕЗ токена — 401 с отпечатком. Открывать нечего.
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(401, setCookie: false)));
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(403, setCookie: false)));
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(404, setCookie: false)));
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(500, setCookie: false)));

        // Даже с cookie: 4xx/5xx — это не «принял ссылку».
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(401, setCookie: true)));
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Answer(500, setCookie: true)));

        // Запрос не прошёл вовсе — молчание не подтверждение, и панель при этом не падает.
        Assert.False(EntryLinkDecisions.Accepts(LinkProbe.Failed2));
    }

    /// <summary>
    /// Чужая ссылка годится только когда сошлись ВСЕ условия: порт тот самый, хост — своя машина
    /// И порт её принимает.
    /// </summary>
    [Fact]
    public void Чужая_ссылка_годится_только_про_этот_порт_и_по_живому_ответу()
    {
        var url = $"http://127.0.0.1:3080/?token={Token}";
        var good = LinkProbe.Answer(303, setCookie: true);

        Assert.True(EntryLinkDecisions.UsableFallback(url, 3080, good));

        // Порт не тот — не годится, каким бы живым порт ни был.
        Assert.False(EntryLinkDecisions.UsableFallback(url, 3081, good));

        // Порт тот, а ссылку он не принял — тоже не годится.
        Assert.False(EntryLinkDecisions.UsableFallback(url, 3080, LinkProbe.Answer(401, false)));
        Assert.False(EntryLinkDecisions.UsableFallback(url, 3080, LinkProbe.Failed2));

        // Пустой ссылки не бывает «годной».
        Assert.False(EntryLinkDecisions.UsableFallback(string.Empty, 3080, good));
        Assert.False(EntryLinkDecisions.UsableFallback(null, 3080, good));
    }

    /// <summary>
    /// ⚠️ ГОДНА ТОЛЬКО ССЫЛКА НА СВОЮ МАШИНУ, и это про утечку токена (красная линия 7).
    ///
    /// Файл панели 1.x — ЧУЖОЙ: в нём может стоять любой адрес, а в ссылке лежит токен входа.
    /// Сверка одного лишь порта означала бы, что подставленное в файл
    /// <c>http://example.com:3080/?token=…</c> признаётся годным — и панель сама уносит токен
    /// на чужой хост. Нашла холодная проверка 27.09.2026 прогоном на собранной сборке.
    /// </summary>
    [Fact]
    public void Своя_машина_это_три_имени_а_не_любой_хост()
    {
        // Свои имена: 127.0.0.1, localhost (регистр не важен) и ::1 — в ссылке только в скобках:
        // литерал IPv6 без скобок делает адрес неразбираемым, и это НЕ наша машина, а мусор.
        foreach (var host in new[] { "127.0.0.1", "localhost", "LOCALHOST", "LocalHost", "[::1]" })
            Assert.True(EntryLinkDecisions.IsLoopbackHost($"http://{host}:3080/?token=x"), host);

        // А без скобок — уже не адрес: `http://::1:3080/…` не разбирается, и «своей машиной»
        // такое объявлять нельзя. Проверка стоит здесь, чтобы скобки не потерялись однажды.
        Assert.False(EntryLinkDecisions.IsLoopbackHost("http://::1:3080/?token=x"));

        // Чужие: и домен, и адрес в сети, и похожее на localhost имя.
        foreach (var host in new[]
                 {
                     "example.com", "192.168.1.5", "10.0.0.1", "0.0.0.0",
                     "localhost.example.com", "127.0.0.1.example.com", "8.8.8.8",
                 })
        {
            Assert.False(EntryLinkDecisions.IsLoopbackHost($"http://{host}:3080/?token=x"), host);
        }

        // Мусор и пустое — не «своя машина» по умолчанию, а «не годится».
        foreach (var bad in new[] { "", "   ", null, "не ссылка", "ftp://127.0.0.1/x" })
            Assert.False(EntryLinkDecisions.IsLoopbackHost(bad));

        // И в САМОМ решении: чужой хост не спасают ни совпавший порт, ни живой ответ с cookie.
        var good = LinkProbe.Answer(303, setCookie: true);

        Assert.False(EntryLinkDecisions.UsableFallback($"http://example.com:3080/?token={Token}", 3080, good));
        Assert.False(EntryLinkDecisions.UsableFallback($"http://192.168.1.5:3080/?token={Token}", 3080, good));

        Assert.True(EntryLinkDecisions.UsableFallback($"http://127.0.0.1:3080/?token={Token}", 3080, good));
        Assert.True(EntryLinkDecisions.UsableFallback($"http://localhost:3080/?token={Token}", 3080, good));
        Assert.True(EntryLinkDecisions.UsableFallback($"http://[::1]:3080/?token={Token}", 3080, good));
    }

    /// <summary>
    /// Файл панели 1.x (`web-url.txt`) хранит ГОТОВУЮ ссылку, а не строку движка. Читается он
    /// терпимо к пробелам и переводам строк — файл могли открыть и сохранить руками.
    /// </summary>
    [Fact]
    public void Файл_чужой_ссылки_читается_как_готовая_ссылка()
    {
        var url = $"http://127.0.0.1:3080/?token={Token}";

        Assert.Equal(url, EntryLinkDecisions.ReadFile(url).Url);
        Assert.Equal(url, EntryLinkDecisions.ReadFile("  " + url + "  \r\n").Url);
        Assert.Equal(3080, EntryLinkDecisions.ReadFile(url).Port);

        // Строка вывода движка в этом файле не лежит: там только адрес. Не разобралось — пусто.
        Assert.True(EntryLinkDecisions.ReadFile(Line(3080)).IsEmpty);
        Assert.True(EntryLinkDecisions.ReadFile("").IsEmpty);
        Assert.True(EntryLinkDecisions.ReadFile(null).IsEmpty);
        Assert.True(EntryLinkDecisions.ReadFile("   ").IsEmpty);
    }
}

/// <summary>
/// Запись «этот сервер наш»: формат, разбор и сверка процесса.
///
/// Формат — одна строка <c>порт;PID;время создания</c>; сверяются ОБА числа, потому что номера
/// процессов переиспользуются, и по одному номеру панель однажды погасила бы чужой процесс (урок v1).
/// </summary>
public class OwnServerRecordTests
{
    private const long Ticks = 63_812_345_678_901_234;

    [Fact]
    public void Запись_переживает_запись_и_чтение()
    {
        var record = new OwnServerRecord(3081, 4242, Ticks);
        var parsed = OwnServerRecord.Parse(record.Format());

        Assert.NotNull(parsed);
        Assert.Equal(record, parsed!.Value);

        // Строка — ровно три числа и разделители, ничего лишнего.
        Assert.Equal($"3081;4242;{Ticks}", record.Format());
    }

    /// <summary>
    /// Мусор, обрезанная строка и пустое дают «записи нет», а не «запись с нулями»: по нулевой
    /// записи панель не признала бы своим ничего, но и сказать о ней было бы нечего.
    /// </summary>
    [Fact]
    public void Испорченная_запись_значит_записи_нет()
    {
        foreach (var bad in new[]
                 {
                     null, "", "   ", "3081", "3081;4242", "не;числа;вовсе",
                     "0;4242;100", "3081;0;100", "3081;4242;0", "70000;4242;100", "3081;-5;100",
                 })
        {
            Assert.Null(OwnServerRecord.Parse(bad));
        }

        // Лишние поля не мешают: формат растёт дописыванием, и старые записи обязаны читаться.
        Assert.Equal(new OwnServerRecord(3081, 4242, Ticks), OwnServerRecord.Parse($"3081;4242;{Ticks};лишнее"));
    }

    /// <summary>
    /// «Тот же процесс» — это совпавшие НОМЕР и ВРЕМЯ СОЗДАНИЯ. Номер один — по нему сын чужой
    /// работы признавался бы своим; время одно — по нему запись пережила бы перезапуск.
    /// </summary>
    [Fact]
    public void Тот_же_процесс_это_номер_и_время_создания()
    {
        var record = new OwnServerRecord(3081, 4242, Ticks);

        Assert.True(record.Matches(4242, Ticks));
        Assert.False(record.Matches(4242, Ticks + 1));   // номер переиспользован другим процессом
        Assert.False(record.Matches(4243, Ticks));       // слушает уже не тот процесс
        Assert.False(record.Matches(0, Ticks));          // никто не слушает
        Assert.False(record.Matches(4242, 0));           // время создания не прочитать
    }
}
