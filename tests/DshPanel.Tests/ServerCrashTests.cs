using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СТОРОЖ СЕРВЕРА: ШАРИК + ЖУРНАЛ + СОБЫТИЕ «УПАЛ САМ» (DEF-052/053, 2.4.0).
///
/// **От какого случая.** 18.10.2026 сервер DSH на 3080 умер, и панель не сказала об этом человеку
/// ничем: журнал получил улики только после отдельной работы, шарика не было вовсе, а код выхода
/// и время оставались неизвестными. Слово владельца: упал сам — шарик, строка журнала и событие
/// «сервер упал сам (код X), время, хвост вывода». И **никакого подъёма**: ни кнопки, ни авто —
/// запуск остаётся за человеком.
///
/// **На чём проверяется.** На фактах, поданных швом контроллера
/// (<see cref="ServerController.ReportEngineExit"/>), — так же, как это делает настоящий путь
/// завершения; сервер при этом не поднимается, порт не занимается, данные владельца не читаются.
///
/// **Мутации, которые обязаны упасть:**
/// M1 «не поднимать событие» (убрать `Crashed?.Invoke`) — красные «три канала» и «код записан»;
/// M2 «называть падением остановку панелью» (`IsCrash` без проверки `stoppedByPanel`) — красная
/// «остановка панелью падением не называется»;
/// M3 «добавить авто-подъём» (позвать `Start()` после события) — красная «падение не поднимает
/// сервер само» (счётчик поиска движка становится 1);
/// M4 «не маскировать хвост» (отдать сырые строки в событие) — красная «токен в хвосте обезврежен».
/// </summary>
public class ServerCrashTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 15, 40, 19, TimeSpan.FromHours(3));

    /// <summary>Контроллер под своим корнем и с журналом в список; движок не ищется без спроса.</summary>
    private static ServerController Build(string root, List<string> logged, Action? onLocateEngine = null) =>
        new(
            root,
            () => root,
            line => { lock (logged) logged.Add(line); },
            mayOccupyOwnerPort: false,
            clock: () => At,
            locateEngine: () =>
            {
                onLocateEngine?.Invoke();
                return null;
            });

    [Fact]
    public void Три_канала_падения_и_код_записан()
    {
        var root = TempRoots.For("server-crash");
        var logged = new List<string>();

        try
        {
            Directory.CreateDirectory(root);

            using var controller = Build(root, logged);

            var crashes = new List<ServerCrash>();
            controller.Crashed += crash => crashes.Add(crash);

            controller.ReportEngineExit(
                pid: 4242,
                exitCode: 7,
                at: At,
                tail: new[] { "хвост вывода", "Engine crashed" },
                ours: true,
                stoppedByPanel: false);

            string[] lines;
            lock (logged) lines = logged.ToArray();

            // КАНАЛ 1 — ЖУРНАЛ: строка «сам», код выхода, время, хвост.
            Assert.False(lines.Length < 2, $"в журнал легло {lines.Length} строк — запись не собралась");
            Assert.Contains("4242", lines[0], StringComparison.Ordinal);
            Assert.Contains("7", lines[0], StringComparison.Ordinal);
            Assert.Contains(EngineExitReport.Moment(At), lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, line => line.Contains("Engine crashed", StringComparison.Ordinal));

            // КАНАЛ 2 — СОБЫТИЕ: ровно одно, с кодом, моментом и хвостом.
            var crash = Assert.Single(crashes);
            Assert.Equal(4242, crash.Pid);
            Assert.Equal(7, crash.ExitCode);
            Assert.Equal(At, crash.At);
            Assert.Contains("Engine crashed", crash.Tail);

            // КАНАЛ 3 — ТЕКСТ ШАРИКА: называет код и время и НЕ обещает подъёма.
            var text = ServerCrashDecisions.NoticeText(crash);
            Assert.Contains("7", text, StringComparison.Ordinal);
            Assert.Contains(EngineExitReport.Moment(At), text, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.NotifyServerCrashedTitle, PanelStrings.NotifyServerCrashedTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("перезапус", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("кнопк", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("не поднимает", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// ОСТАНОВКА ПАНЕЛЬЮ (и человеком через неё) ПАДЕНИЕМ НЕ НАЗЫВАЕТСЯ: журнал пишется, событие —
    /// нет. Иначе после «Остановить» панель обвиняла бы программу в том, что сделал человек.
    ///
    /// **Мутация, которая обязана упасть:** `IsCrash` без проверки `stoppedByPanel`.
    /// </summary>
    [Fact]
    public void Остановка_панелью_падением_не_называется()
    {
        var root = TempRoots.For("server-crash-panel");
        var logged = new List<string>();

        try
        {
            Directory.CreateDirectory(root);

            using var controller = Build(root, logged);

            var crashes = 0;
            controller.Crashed += _ => crashes++;

            controller.ReportEngineExit(4242, 0, At, Array.Empty<string>(), ours: true, stoppedByPanel: true);

            string[] lines;
            lock (logged) lines = logged.ToArray();

            Assert.NotEmpty(lines);
            Assert.Equal(0, crashes);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// ПАДЕНИЕ НЕ ПОДНИМАЕТ СЕРВЕР САМО (слово владельца): после события движок не ищется вовсе,
    /// то есть ни авто-подъёма, ни попытки подъёма не происходит. Ни кнопки, ни строки «поднять»
    /// в сообщении нет — шарик только сообщает.
    ///
    /// **Мутация, которая обязана упасть:** позвать `Start()` в обработчике/после события.
    /// </summary>
    [Fact]
    public void Падение_не_поднимает_сервер_само()
    {
        var root = TempRoots.For("server-crash-noauto");
        var logged = new List<string>();
        var engineLookups = 0;

        try
        {
            Directory.CreateDirectory(root);

            using var controller = Build(root, logged, () => Interlocked.Increment(ref engineLookups));

            controller.Crashed += crash => ServerCrashDecisions.NoticeText(crash);

            controller.ReportEngineExit(4242, 7, At, new[] { "хвост" }, ours: true, stoppedByPanel: false);

            Assert.Equal(0, engineLookups);
            Assert.Equal(ServerPresence.Stopped, controller.State.Presence);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// «ПАДЕНИЕ» — ТОЛЬКО СВОЙ ПРОЦЕСС И ТОЛЬКО БЕЗ СЛОВА ПАНЕЛИ: все четыре исхода матрицы.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Падение_это_свой_процесс_и_без_слова_панели(bool ours, bool stoppedByPanel, bool expected) =>
        Assert.Equal(expected, ServerCrashDecisions.IsCrash(ours, stoppedByPanel));

    /// <summary>
    /// ТОКЕН В ХВОСТЕ ОБЕЗВРЕЖЕН ДО СОБЫТИЯ: хвост приходит сырым, а в <see cref="ServerCrash.Tail"/>
    /// попадает уже через маску — подписчиков может стать больше одного, и забывший маску вынес бы
    /// ссылку входа наружу.
    ///
    /// **Мутация, которая обязана упасть:** отдать в событие сырые строки.
    /// </summary>
    [Fact]
    public void Токен_в_хвосте_обезврежен()
    {
        var root = TempRoots.For("server-crash-secret");
        var logged = new List<string>();

        try
        {
            Directory.CreateDirectory(root);

            using var controller = Build(root, logged);

            var crashes = new List<ServerCrash>();
            controller.Crashed += crash => crashes.Add(crash);

            controller.ReportEngineExit(
                4242,
                1,
                At,
                new[] { "вход: https://127.0.0.1:3080/?token=SECRET123" },
                ours: true,
                stoppedByPanel: false);

            var crash = Assert.Single(crashes);

            string[] tail;
            lock (logged) tail = logged.ToArray();

            Assert.DoesNotContain("SECRET123", string.Join("\n", crash.Tail), StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET123", string.Join("\n", tail), StringComparison.Ordinal);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// АВТО-ПОДЪЁМА НЕТ И В КОДЕ СТОРОЖА: ни шов падения, ни подписчик шарика не зовут
    /// `Start`/`Restart`. Поведенческое утверждение («движок не ищется») ловит запуск, который
    /// дошёл до поиска, но не ловит запуск, отбитый ранними проверками, — поэтому рядом стоит
    /// сторож по исходнику: он падает от самого слова «поднять» в этих двух местах.
    ///
    /// **Мутация, которая обязана упасть:** вписать `Start(...)` в `ReportEngineExit` или
    /// в подписку `_server.Crashed +=`.
    /// </summary>
    [Fact]
    public void Авто_подъёма_нет_в_коде_сторожа()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        var controller = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Server", "ServerController.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "App.axaml.cs"));

        // Шов падения: от объявления до следующего члена класса.
        var seamStart = controller.IndexOf("internal void ReportEngineExit", StringComparison.Ordinal);
        Assert.True(seamStart >= 0, "шов ReportEngineExit не найден — сторож ослеп");

        var seamEnd = controller.IndexOf("\n    private ", seamStart, StringComparison.Ordinal);
        Assert.True(seamEnd > seamStart, "не видно конца шва ReportEngineExit — сторож ослеп");

        var seam = controller[seamStart..seamEnd];

        Assert.DoesNotContain("Start(", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart(", seam, StringComparison.Ordinal);

        // Подписчик шарика: от подписки до конца объявления.
        var notice = app.IndexOf("_server.Crashed +=", StringComparison.Ordinal);
        Assert.True(notice >= 0, "подписка на Crashed не найдена — сторож ослеп");

        var noticeEnd = app.IndexOf("ServerCrashDecisions.NoticeText", notice, StringComparison.Ordinal);
        Assert.True(noticeEnd > notice, "не видно конца подписки — сторож ослеп");

        var subscription = app[notice..noticeEnd];

        Assert.DoesNotContain("Start(", subscription, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart(", subscription, StringComparison.Ordinal);
    }

    /// <summary>
    /// ТРЕТИЙ КАНАЛ ПОДКЛЮЧЁН К ШАРИКУ, А НЕ ТОЛЬКО ОБЪЯВЛЕН: `App.axaml.cs` подписывается на
    /// <c>Crashed</c> и показывает шарик видом <see cref="NoticeKind.ServerCrashed"/>. Вид в перечне —
    /// значит изолированный прогон о падении своего подставного процесса молчит
    /// (<see cref="IsolationRules.ShouldNotify"/>), как и о любом другом.
    ///
    /// **Мутации, которые обязаны упасть:** убрать подписку из `App` · убрать вид из `NoticeKind`.
    /// </summary>
    [Fact]
    public void Шарик_подключён_к_событию_и_молчит_в_прогоне()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        var app = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "App.axaml.cs"));

        Assert.Contains("_server.Crashed +=", app, StringComparison.Ordinal);
        Assert.Contains("NoticeKind.ServerCrashed", app, StringComparison.Ordinal);
        Assert.Contains("ServerCrashDecisions.NoticeText", app, StringComparison.Ordinal);

        Assert.False(IsolationRules.ShouldNotify(NoticeKind.ServerCrashed, isolatedRun: true));
        Assert.True(IsolationRules.ShouldNotify(NoticeKind.ServerCrashed, isolatedRun: false));
    }
}
