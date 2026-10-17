using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КАДРЫ ЭКРАНА ТЕЛЕФОНА — ДВЕ ТЕМЫ, ОБРАЗЦОВОЕ СОСТОЯНИЕ, БЕЗ ЛИЧНЫХ ДАННЫХ.
///
/// **Зачем это отдельно от ворот и отдельно от съёмки окон.** Desktop-съёмка (`--shot`) телефон
/// **не снимает вовсе**: страница телефона — HTML, который отдаёт калитка, и окна у неё нет.
/// Поэтому кадр делается **уже существующим браузерным прогоном** (Edge в безголовом режиме через
/// отладочный протокол) — тем же, которым стерегут поведение страницы. Владелец и проверяющий
/// должны видеть экран, **не беря телефон в руки** (требование владельца 12.10.2026).
///
/// ⚠️ **Съёмка идёт ТОЛЬКО на петле и на свободном порту**, под своим временным корнем:
/// ни одного байта наружу, порты `3080`/`3081`/`43127` не занимаются никогда.
///
/// ⚠️ **Личных данных в кадре быть не может по устройству:** калитка поднимается на `127.0.0.1`,
/// адрес машины ей не передаётся вовсе, сопряжение — подставное (файл пропуска под корнем прогона),
/// а текст вопроса — образцовый. Проверка это ещё и **стережёт**: в кадре не должно быть ни
/// номера адреса машины, ни домашнего каталога.
/// </summary>
public class PhoneFrameTests
{
    /// <summary>
    /// КУДА КЛАДУТСЯ КАДРЫ — И ПОЧЕМУ ПО УМОЛЧАНИЮ НЕ В РЕПОЗИТОРИЙ (решение владельца 17.10.2026).
    ///
    /// ⚠️ Было: кадры всегда писались в ОТСЛЕЖИВАЕМЫЙ `docs\design\phone-2026-10-12\`. После
    /// обычного `dotnet test` рабочее дерево становилось ГРЯЗНЫМ, а `make-release-tree` требует
    /// чистоты — то есть проверка мешала выпуску. Теперь по умолчанию кадры идут в
    /// git-игнорируемый `upd-build\` (прогон дерево не пачкает), а в `docs\` — только по ЯВНОМУ
    /// флагу окружения, когда кадры обновляют осознанно и коммитит их человек.
    /// </summary>
    private const string FramesIntoBuild = @"upd-build\phone-frames";

    /// <summary>Отслеживаемый каталог макетов: сюда кадры идут ТОЛЬКО по флагу (коммитит человек).</summary>
    private const string FramesIntoDocs = @"docs\design\phone-2026-10-12";

    /// <summary>
    /// Флаг осознанного обновления кадров: `DSH_PANEL_FRAMES_INTO_DOCS=1` — писать в `docs\`.
    /// Без него — в `upd-build\phone-frames\`. Читается на каждом прогоне, а не кэшируется: так
    /// флаг можно включить на ОДИН вызов `dotnet test` и не получить «залипшее» поведение.
    /// </summary>
    private const string IntoDocsFlag = "DSH_PANEL_FRAMES_INTO_DOCS";

    /// <summary>Каталог кадров этого прогона: `upd-build\phone-frames` или (по флагу) `docs\design\…`.</summary>
    private static string FramesFolder() =>
        Environment.GetEnvironmentVariable(IntoDocsFlag) == "1"
            ? Path.Combine(Root(), FramesIntoDocs)
            : Path.Combine(Root(), FramesIntoBuild);

    private const string EventId = "event-frame-1";

    /// <summary>
    /// СНИМОК ЭКРАНА: ожидание и вопрос, светлая и тёмная тема — ЧЕТЫРЕ кадра.
    ///
    /// Мутации, которые обязаны упасть здесь: «кадр одноцветный (белый лист)» · «тёмная тема
    /// не применилась» (кадр дневной на оба имени) · «в кадре личные данные» · «кадра нет вовсе».
    /// </summary>
    [Fact]
    public async Task Кадры_экрана_телефона_в_двух_темах()
    {
        var browser = PhoneBrowserTests.Browser();

        Assert.False(
            browser is null,
            "браузера нет — кадр снять нечем (нужны Edge или Chrome; искали "
            + string.Join(", ", PhoneBrowserTests.BrowserPaths()) + ")");

        using var stand = new PhoneBrowserTests.Stand();

        Assert.True(stand.Gate.Start().Opened);

        var cookie = await PhoneBrowserTests.Pair(stand);

        stand.Events.Emit("{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"ready\",\"clientId\":\"client-frame\"}}");

        var profile = Path.Combine(Path.GetTempPath(), "dsh-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        using var live = new PhoneBrowserTests.BrowserRun(browser!, profile);

        var folder = FramesFolder();

        Directory.CreateDirectory(folder);

        try
        {
            await live.WaitForDevTools();
            await live.Cookie(PhoneGate.CookieName, cookie);
            await live.Enable();

            // ТЕЛЕФОН СНЯТ В РАЗМЕРЕ ТЕЛЕФОНА, а не окна настольной панели: у страницы своя ширина.
            await live.Screen(390, 780, 1);

            // ── ОЖИДАНИЕ: вопроса нет ──────────────────────────────────────────────────────────
            await live.Navigate(PhoneBrowserTests.Url(stand.Port));
            await live.Settled();

            var waitingLight = await Save(live, folder, "waiting-light.png");

            await live.Dark();
            await live.Reload();
            await live.Settled();

            var waitingDark = await Save(live, folder, "waiting-dark.png");

            // ── ВОПРОС: агент ждёт ответа ─────────────────────────────────────────────────────
            await live.Light();

            stand.Events.Emit(
                "{\"type\":\"item\",\"streamId\":\"phone-events\",\"value\":{\"type\":\"waterfall\","
                + "\"event\":\"user-questions/request\",\"eventId\":\"" + EventId + "\",\"agentId\":\"session-1\","
                + "\"request\":{\"questions\":[{\"id\":\"q1\",\"question\":\"Обновить движок до проверенной версии?\","
                + "\"options\":[{\"label\":\"Обновить\"},{\"label\":\"Оставить как есть\"}]}]}}}");

            await live.Navigate(PhoneBrowserTests.Url(stand.Port));
            await live.Settled();

            var questionLight = await Save(live, folder, "question-light.png");

            await live.Dark();
            await live.Reload();
            await live.Settled();

            var questionDark = await Save(live, folder, "question-dark.png");

            // ⚠️ ЧЕТЫРЕ КАДРА — И ВСЕ РАЗНЫЕ: тёмная тема обязана отличаться от светлой, иначе
            // «тёмный кадр» был бы просто вторым именем светлого (это и есть дешёвая мутация).
            Assert.NotEqual(waitingLight.Hash, waitingDark.Hash);
            Assert.NotEqual(questionLight.Hash, questionDark.Hash);
            Assert.NotEqual(waitingLight.Hash, questionLight.Hash);

            // ⚠️ И КАДР — НЕ БЕЛЫЙ ЛИСТ: у него много разных цветов.
            foreach (var shot in new[] { waitingLight, waitingDark, questionLight, questionDark })
            {
                Assert.True(shot.Colors >= 24, $"{shot.Name}: в кадре всего {shot.Colors} цветов — похоже на пустой лист");
                Assert.True(shot.Bytes > 4000, $"{shot.Name}: кадр в {shot.Bytes} байт — это не страница");
            }

            // ⚠️ ЛИЧНЫХ ДАННЫХ В КАДРЕ НЕТ — и это проверяется по САМОЙ СТРАНИЦЕ, а не по глазам.
            var html = await stand.Http.GetStringAsync("/", TestContext.Current.CancellationToken);

            Assert.DoesNotContain("C:\\Users", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("192.168.0.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("phone-", html, StringComparison.Ordinal);
        }
        finally
        {
            live.Stop();
            PhoneBrowserTests.TryRemove(profile);
        }
    }

    /// <summary>Кадр сохранён: имя, размер, число разных цветов и хеш — по нему и сравниваются темы.</summary>
    private sealed record Shot(string Name, int Bytes, int Colors, string Hash);

    private static async Task<Shot> Save(PhoneBrowserTests.BrowserRun live, string folder, string name)
    {
        var png = await live.Shot();
        var path = Path.Combine(folder, name);

        File.WriteAllBytes(path, png);

        Assert.True(File.Exists(path), $"кадр {name} не записался");

        return new Shot(name, png.Length, Colors(png), Hash(png));
    }

    /// <summary>
    /// ЧИСЛО РАЗНЫХ ЦВЕТОВ В КАДРЕ — по пикселям PNG, без картинковых библиотек: `zlib` внутри PNG
    /// распаковывается встроенным `DeflateStream`, фильтры строк снимаются вручную. Столько же
    /// делает оснастка вида для окон (`PanelTestStand.ColorsIn`) — и по той же причине: «файл
    /// создан» не отличает страницу от белого листа.
    /// </summary>
    private static int Colors(byte[] png)
    {
        var colors = new HashSet<int>();

        foreach (var pixel in Pixels(png))
        {
            if (colors.Count > 4096) break;

            colors.Add(pixel);
        }

        return colors.Count;
    }

    /// <summary>Хеш кадра — им и сравниваются темы между собой.</summary>
    private static string Hash(byte[] png)
    {
        var sum = System.Security.Cryptography.SHA256.HashData(png);

        return Convert.ToHexString(sum)[..16];
    }

    /// <summary>
    /// ПИКСЕЛИ PNG: разбор заголовка кусков, распаковка `IDAT` и снятие фильтров строк.
    ///
    /// ⚠️ Поддерживается ровно то, что отдаёт браузер: 8 бит на канал, `RGB` или `RGBA`,
    /// без чересстрочности. Всё остальное — **громкий отказ**, а не «насчитали сколько вышло»:
    /// молча посчитанные нули выглядели бы как провал проверки, а не как неумение её читать.
    /// </summary>
    private static IEnumerable<int> Pixels(byte[] png)
    {
        Assert.True(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50, "кадр — не PNG");

        var width = 0;
        var height = 0;
        var channels = 0;
        using var data = new MemoryStream();

        var at = 8;

        while (at + 8 <= png.Length)
        {
            var size = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var kind = Encoding.ASCII.GetString(png, at + 4, 4);

            if (kind == "IHDR")
            {
                width = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11];
                height = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15];

                var depth = png[at + 16];
                var color = png[at + 17];

                Assert.True(depth == 8, $"в кадре {depth} бит на канал — читать его проверка не умеет");
                Assert.True(png[at + 20] == 0, "чересстрочный PNG читать проверка не умеет");

                channels = color switch { 2 => 3, 6 => 4, _ => 0 };

                Assert.True(channels > 0, $"в кадре вид цвета {color} — читать его проверка не умеет");
            }
            else if (kind == "IDAT")
            {
                data.Write(png, at + 8, size);
            }
            else if (kind == "IEND")
            {
                break;
            }

            at += size + 12;
        }

        data.Position = 0;

        using var raw = new MemoryStream();
        using (var inflate = new System.IO.Compression.ZLibStream(data, System.IO.Compression.CompressionMode.Decompress))
        {
            inflate.CopyTo(raw);
        }

        var bytes = raw.ToArray();
        var stride = (width * channels) + 1;
        var line = new byte[width * channels];
        var previous = new byte[width * channels];

        for (var row = 0; row < height; row++)
        {
            var filter = bytes[row * stride];

            Buffer.BlockCopy(bytes, (row * stride) + 1, line, 0, line.Length);

            Unfilter(filter, line, previous, channels);

            for (var column = 0; column < width; column++)
            {
                var index = column * channels;

                yield return (line[index] << 16) | (line[index + 1] << 8) | line[index + 2];
            }

            (previous, line) = (line, previous);
        }
    }

    /// <summary>Снятие фильтра строки PNG (`0` — нет, `1` — Sub, `2` — Up, `3` — Average, `4` — Paeth).</summary>
    private static void Unfilter(byte filter, byte[] line, byte[] previous, int channels)
    {
        for (var i = 0; i < line.Length; i++)
        {
            int left = i >= channels ? line[i - channels] : 0;
            int up = previous[i];
            int upLeft = i >= channels ? previous[i - channels] : 0;

            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + left),
                2 => (byte)(line[i] + up),
                3 => (byte)(line[i] + ((left + up) / 2)),
                4 => (byte)(line[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidOperationException($"неизвестный фильтр строки PNG: {filter}"),
            };
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var a = Math.Abs(estimate - left);
        var b = Math.Abs(estimate - up);
        var c = Math.Abs(estimate - upLeft);

        return a <= b && a <= c ? left : b <= c ? up : upLeft;
    }

    /// <summary>Корень репозитория — вверх от каталога сборки по файлу <c>DshPanel.slnx</c>.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DshPanel.slnx"))) return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет DshPanel.slnx");

        return string.Empty;
    }
}
