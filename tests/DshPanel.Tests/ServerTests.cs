using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки серверного слоя. Чистые решения перебираются по ВСЕМ сочетаниям, а не по одному
/// примеру: именно так ловится «правило, которое обходит само себя».
///
/// Ни одна проверка здесь не поднимает сервер и не трогает каталоги: запуск настоящего движка —
/// дело самотеста на собранном exe (`--server-selftest`), где он идёт в изоляции.
/// </summary>
public class ServerTests
{
    private sealed class FakeServer : IServerControl
    {
        public FakeServer(ServerState state, ServerOwner owner = ServerOwner.None)
        {
            State = state;
            Owner = owner;
        }

        public ServerState State { get; set; }
        public ServerOwner Owner { get; set; }

        /// <summary>
        /// Запомнено ли согласие человека на встроенный сервер. Проверки ставят его вручную:
        /// «запомнено» приходит из настроек панели, а подставной сервер настроек не имеет.
        /// </summary>
        public bool ConsentRemembered { get; set; }

        /// <summary>
        /// Ссылка входа (в ней токен) или пусто. Ставится вручную: проверки не показывают ссылку
        /// нигде, они проверяют только РЕШЕНИЕ о доступности кнопки.
        /// </summary>
        public string EntryLink { get; set; } = string.Empty;

        public int Refreshes { get; private set; }
        public int Scans { get; private set; }

        /// <summary>Что отвечает разведка. Пусто — «ничего не нашли».</summary>
        public FoundServer? Found { get; set; }

        /// <summary>Удалось ли прочитать таблицу портов. False — «посмотреть не удалось».</summary>
        public bool TableReadable { get; set; } = true;

        /// <summary>С каким подтверждением звали остановку — по записям и проверяем правило.</summary>
        public List<bool> StopCalls { get; } = new();

        public List<FoundServer> AdoptCalls { get; } = new();

        public int DetachCalls { get; private set; }

        public ServerState Refresh()
        {
            Refreshes++;
            return State;
        }

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed)
        {
            StopCalls.Add(confirmed);
            return State;
        }

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan()
        {
            Scans++;
            IReadOnlyList<FoundServer> found = Found is null
                ? Array.Empty<FoundServer>()
                : new[] { Found.Value };

            return new DiscoveryResult(TableReadable, found);
        }

        public ServerState Adopt(FoundServer found)
        {
            AdoptCalls.Add(found);
            return State;
        }

        public ServerState Detach()
        {
            DetachCalls++;
            return State;
        }
    }

    // ------------------------------------------------------------ классификация

    /// <summary>Кто на порту — таблица ожиданий записана явно, а не формулой из кода.</summary>
    [Fact]
    public void Кто_на_порту_перебирает_все_сочетания()
    {
        var cases = new (int ListenerPid, bool Fingerprint, ServerPresence Expected)[]
        {
            (0,  false, ServerPresence.Stopped),      // никто не слушает
            (0,  true,  ServerPresence.Stopped),      // ответа быть не может: слушать некому
            (-1, false, ServerPresence.Stopped),      // таблицу не получили — тоже «никого»
            (1234, false, ServerPresence.BusyByOther), // слушает, но не DSH
            (1234, true,  ServerPresence.Running),     // слушает и отвечает отпечатком
        };

        Assert.Equal(5, cases.Length);
        foreach (var c in cases)
        {
            Assert.Equal(c.Expected, ServerDecisions.Classify(c.ListenerPid, c.Fingerprint));
        }
    }

    [Fact]
    public void Отпечаток_узнаётся_только_по_401_и_своей_строке()
    {
        const string Real = "dsh web authentication required; reopen the URL printed by dsh web.";

        Assert.True(ServerDecisions.LooksLikeDshWeb(401, Real));
        Assert.True(ServerDecisions.LooksLikeDshWeb(401, Real.ToUpperInvariant()));
        Assert.True(ServerDecisions.LooksLikeDshWeb(401, "  " + Real + "\n"));

        // 200 в ответ на GET / означает, что отвечает НЕ DSH: без cookie он доступа не даёт.
        Assert.False(ServerDecisions.LooksLikeDshWeb(200, Real));
        Assert.False(ServerDecisions.LooksLikeDshWeb(401, "authentication required"));
        Assert.False(ServerDecisions.LooksLikeDshWeb(401, null));
        Assert.False(ServerDecisions.LooksLikeDshWeb(401, ""));
    }

    /// <summary>
    /// ПОРТ ВЛАДЕЛЬЦА: запрет на ЗАНЯТИЕ остался, но стал правом, которое приходит открытым
    /// текстом (решение владельца 26.09.2026 — 2.0 встаёт на место 1.x, и её сервер поднимается
    /// на 3080). Проверка перебирает ОБА состояния права: с правом 3080 — обычный порт, без права
    /// он запрещён. `<c>IsAllowedPort</c>` без второго аргумента не существует вовсе — забыть
    /// выдать право нельзя, вызов не соберётся.
    /// </summary>
    [Fact]
    public void Порт_владельца_разрешён_только_по_явному_праву()
    {
        Assert.Equal(3080, ServerDecisions.OwnerPort);

        // С правом — любой настоящий порт, включая порт владельца.
        foreach (var good in new[] { 1, 80, ServerDecisions.OwnerPort, 3097, 65535 })
            Assert.True(ServerDecisions.IsAllowedPort(good, mayOccupyOwnerPort: true), $"с правом: {good}");

        // Без права — любой, КРОМЕ порта владельца. Это и есть красная линия в коде.
        foreach (var good in new[] { 1, 80, ServerDecisions.RunFallbackPort, 3097, 65535 })
            Assert.True(ServerDecisions.IsAllowedPort(good, mayOccupyOwnerPort: false), $"без права: {good}");

        Assert.False(ServerDecisions.IsAllowedPort(ServerDecisions.OwnerPort, mayOccupyOwnerPort: false));
        Assert.True(ServerDecisions.IsAllowedPort(ServerDecisions.OwnerPort, mayOccupyOwnerPort: true));

        // Мусор запрещён при ЛЮБОМ праве: право про порт владельца, а не про «любое число».
        foreach (var mayOccupy in new[] { false, true })
        {
            foreach (var bad in new[] { 0, -1, 65536, 100000, int.MinValue, int.MaxValue })
                Assert.False(ServerDecisions.IsAllowedPort(bad, mayOccupy), $"право {mayOccupy}: {bad}");
        }
    }

    [Fact]
    public void Кнопки_зависят_от_состояния_и_от_того_кто_поднял_сервер()
    {
        Assert.True(ServerDecisions.CanStart(ServerPresence.Stopped));
        Assert.False(ServerDecisions.CanStart(ServerPresence.Running));
        Assert.False(ServerDecisions.CanStart(ServerPresence.BusyByOther));

        // Свой сервер панель гасит свободно — это её процесс.
        Assert.True(ServerDecisions.CanStop(ServerOwner.Panel, ServerPresence.Running, false, false));
        Assert.True(ServerDecisions.CanStop(ServerOwner.Panel, ServerPresence.Running, true, false));

        // Встроенный — ТОЛЬКО по подтверждению, и это решение владельца 24.09.2026.
        Assert.False(ServerDecisions.CanStop(ServerOwner.Adopted, ServerPresence.Running, false, false));
        Assert.True(ServerDecisions.CanStop(ServerOwner.Adopted, ServerPresence.Running, true, false));

        // Ничем не управляем — гасить нечего, сколько бы серверов ни работало рядом.
        Assert.False(ServerDecisions.CanStop(ServerOwner.None, ServerPresence.Running, true, false));

        // Нечего гасить и в остальных состояниях порта.
        foreach (var owner in new[] { ServerOwner.None, ServerOwner.Panel, ServerOwner.Adopted })
        {
            foreach (var confirmed in new[] { false, true })
            {
                foreach (var consent in new[] { false, true })
                {
                    Assert.False(ServerDecisions.CanStop(owner, ServerPresence.Stopped, confirmed, consent));
                    Assert.False(ServerDecisions.CanStop(owner, ServerPresence.BusyByOther, confirmed, consent));
                }
            }
        }

        // Подтверждение спрашивается РОВНО у встроенного и ровно у работающего.
        Assert.True(ServerDecisions.NeedsStopConfirmation(ServerOwner.Adopted, ServerPresence.Running, false));
        Assert.False(ServerDecisions.NeedsStopConfirmation(ServerOwner.Panel, ServerPresence.Running, false));
        Assert.False(ServerDecisions.NeedsStopConfirmation(ServerOwner.None, ServerPresence.Running, false));
        Assert.False(ServerDecisions.NeedsStopConfirmation(ServerOwner.Adopted, ServerPresence.Stopped, false));
    }

    /// <summary>
    /// ЗАПОМНЕННОЕ СОГЛАСИЕ отменяет подтверждение — и ровно оно одно. Решение владельца
    /// 26.09.2026: «спросить ОДИН раз и запомнить ответ». Проверка перебирает все сочетания:
    /// согласие не должно ни «протекать» на чужой сервер, ни отменять подтверждение там,
    /// где человека не спрашивали.
    /// </summary>
    [Fact]
    public void Запомненное_согласие_отменяет_подтверждение_только_у_своего_встроенного()
    {
        // Встроенный с запомненным согласием: подтверждения нет, гасить можно без него.
        Assert.False(ServerDecisions.NeedsStopConfirmation(ServerOwner.Adopted, ServerPresence.Running, true));
        Assert.True(ServerDecisions.CanStop(ServerOwner.Adopted, ServerPresence.Running, false, true));

        // Свой сервер панели согласие не портит и не улучшает: он и так гасится свободно.
        Assert.True(ServerDecisions.CanStop(ServerOwner.Panel, ServerPresence.Running, false, true));
        Assert.False(ServerDecisions.NeedsStopConfirmation(ServerOwner.Panel, ServerPresence.Running, true));

        // Ничем не управляем — согласие не даёт права гасить чужое.
        Assert.False(ServerDecisions.CanStop(ServerOwner.None, ServerPresence.Running, false, true));
        Assert.False(ServerDecisions.CanStop(ServerOwner.None, ServerPresence.Running, true, true));

        // Сервера нет — гасить нечего даже с согласием.
        foreach (var owner in new[] { ServerOwner.None, ServerOwner.Panel, ServerOwner.Adopted })
        {
            Assert.False(ServerDecisions.CanStop(owner, ServerPresence.Stopped, false, true));
            Assert.False(ServerDecisions.CanStop(owner, ServerPresence.BusyByOther, false, true));
            Assert.False(ServerDecisions.NeedsStopConfirmation(owner, ServerPresence.Stopped, true));
        }

        // Согласия НЕТ — правило прежнее, даже когда человек только что нажал «гасить подтверждаю».
        Assert.True(ServerDecisions.NeedsStopConfirmation(ServerOwner.Adopted, ServerPresence.Running, false));
        Assert.False(ServerDecisions.CanStop(ServerOwner.Adopted, ServerPresence.Running, false, false));
    }

    /// <summary>
    /// ЗАМОК ОТ ВТОРОГО ДВИЖКА — чистое решение, по которому контроллер отказывается поднимать
    /// свой сервер. Перебираются все четыре сочетания: право «поднимать рядом» снимает замок
    /// РОВНО тогда, когда оно выдано, и не снимает его «за компанию» ни в каком другом случае.
    /// </summary>
    [Fact]
    public void Чужой_сервер_запрещает_поднимать_свой_пока_нет_права()
    {
        Assert.True(ServerDecisions.BlockedByForeignServer(
            allowParallelStart: false, foundForeignServer: true));

        Assert.False(ServerDecisions.BlockedByForeignServer(
            allowParallelStart: false, foundForeignServer: false));

        // Право выдано явно — замок снят, и это осознанный выбор того, кто его просил.
        Assert.False(ServerDecisions.BlockedByForeignServer(
            allowParallelStart: true, foundForeignServer: true));

        Assert.False(ServerDecisions.BlockedByForeignServer(
            allowParallelStart: true, foundForeignServer: false));
    }

    /// <summary>
    /// Предлагать встроиться можно только тогда, когда панель ничем не управляет: иначе она
    /// предлагала бы взять под управление то, чем уже управляет (или свой же сервер).
    /// </summary>
    [Fact]
    public void Встроиться_предлагаем_только_когда_панель_ничем_не_управляет()
    {
        Assert.True(ServerDecisions.CanAdopt(ServerOwner.None, foundSomething: true));
        Assert.False(ServerDecisions.CanAdopt(ServerOwner.None, foundSomething: false));
        Assert.False(ServerDecisions.CanAdopt(ServerOwner.Panel, foundSomething: true));
        Assert.False(ServerDecisions.CanAdopt(ServerOwner.Adopted, foundSomething: true));
    }

    /// <summary>
    /// Порт владельца нельзя ЗАНЯТЬ без права, но запрет не распространяется на то, чтобы его
    /// ВИДЕТЬ: на 3080 стоит живой DSH владельца, и ради него весь шаг встраивания и делается.
    ///
    /// Здесь же сторожится ГЛАВНОЕ сочетание шага: порт владельца БЕЗ права приводится к
    /// <see cref="ServerDecisions.RunFallbackPort"/> (3081), а не к умолчанию. Сведи эти два
    /// ответа в один — и прогон проверки привёл бы 3080 к 3080, то есть занял бы порт владельца.
    /// </summary>
    [Fact]
    public void Порт_владельца_без_права_приводится_к_запасному()
    {
        // С правом порт владельца остаётся собой — так его занимает обычный запуск человеком.
        Assert.Equal(ServerDecisions.OwnerPort, ServerDecisions.NormalizePort(
            ServerDecisions.OwnerPort, mayOccupyOwnerPort: true));

        // Без права — запасной порт, и это ДРУГОЕ число, а не умолчание.
        Assert.Equal(ServerDecisions.RunFallbackPort, ServerDecisions.NormalizePort(
            ServerDecisions.OwnerPort, mayOccupyOwnerPort: false));
        Assert.NotEqual(ServerDecisions.DefaultServerPort, ServerDecisions.RunFallbackPort);

        // Мусор и непонятные числа — умолчание (3080) при праве и запасной (3081) без права.
        foreach (var bad in new[] { 0, -1, -3080, 65536, 100000, int.MinValue, int.MaxValue })
        {
            Assert.Equal(ServerDecisions.DefaultServerPort, ServerDecisions.NormalizePort(bad, true));
            Assert.Equal(ServerDecisions.RunFallbackPort, ServerDecisions.NormalizePort(bad, false));
        }

        // Настоящий порт остаётся собой при любом праве.
        foreach (var good in new[] { 1, 80, ServerDecisions.RunFallbackPort, 3097, 65535 })
        {
            Assert.Equal(good, ServerDecisions.NormalizePort(good, true));
            Assert.Equal(good, ServerDecisions.NormalizePort(good, false));
        }

        // А вот увидеть сервер на порту владельца панель обязана — это и есть главный случай.
        Assert.True(new FoundServer(ServerDecisions.OwnerPort, 4242, "node").IsOwnerPort);
        Assert.False(new FoundServer(3097, 4242, "node").IsOwnerPort);
    }

    /// <summary>
    /// РЕШЕНИЕ ОБ АВТОПОДЪЁМЕ — чистая функция, и перебираются ВСЕ восемь сочетаний: правило
    /// «поднимать, только когда панель ничем не управляет, на порту никого и рядом нет чужого DSH»
    /// не должно обходить само себя ни в одном случае.
    /// </summary>
    [Fact]
    public void Автоподъём_решается_тремя_условиями()
    {
        var cases = new[]
        {
            // owner, presence, чужой найден, ожидание
            (ServerOwner.None, ServerPresence.Stopped, false, true),
            (ServerOwner.None, ServerPresence.Stopped, true, false),
            (ServerOwner.None, ServerPresence.Running, false, false),
            (ServerOwner.None, ServerPresence.Running, true, false),
            (ServerOwner.None, ServerPresence.BusyByOther, false, false),
            (ServerOwner.None, ServerPresence.BusyByOther, true, false),
            (ServerOwner.Panel, ServerPresence.Stopped, false, false),
            (ServerOwner.Panel, ServerPresence.Stopped, true, false),
            (ServerOwner.Adopted, ServerPresence.Stopped, false, false),
            (ServerOwner.Adopted, ServerPresence.Stopped, true, false),
        };

        foreach (var (owner, presence, foreign, expected) in cases)
        {
            Assert.Equal(expected, ServerDecisions.ShouldAutoStart(owner, presence, foreign));
        }

        // Ровно один случай из перебора — «поднимаем»: ошибка в любую сторону видна сразу.
        Assert.Single(cases, c => c.Item4);
    }

    // ------------------------------------------------------------ журнал движка

    /// <summary>
    /// Токен запуска в журнал панели попадать не должен: журнал — файл на диске, и он уезжает
    /// в резервную копию.
    /// </summary>
    [Fact]
    public void Токен_запуска_вырезается_из_журнала()
    {
        var line = "dsh web: http://127.0.0.1:51234/?token=AbC123-_xyz more text";
        var redacted = EngineLog.Redact(line);

        Assert.DoesNotContain("AbC123", redacted);
        Assert.Contains("token=***", redacted);
        Assert.Contains("more text", redacted);

        // Вторая форма — токен в JSON-поле.
        var json = EngineLog.Redact("{\"url\":\"http://127.0.0.1:1/\",\"token\":\"secret-value\"}");
        Assert.DoesNotContain("secret-value", json);
        Assert.Contains("***", json);

        // Строка без токена не портится; пустое и null дают пустую строку, а не падение.
        Assert.Equal("обычная строка", EngineLog.Redact("обычная строка"));
        Assert.Equal(string.Empty, EngineLog.Redact(null));
        Assert.Equal(string.Empty, EngineLog.Redact(""));
    }

    // ------------------------------------------------------------ что видит человек

    [Fact]
    public void Строки_панели_говорят_о_состоянии_сервера()
    {
        var running = new ServerPanelModel(new FakeServer(
            new ServerState(ServerPresence.Running, 51234, 42, "node", "сервер DSH отвечает на порту 51234"),
            ServerOwner.Panel));

        Assert.Equal(PanelStrings.ServerRunning, running.StatusText);
        Assert.Equal("сервер DSH отвечает на порту 51234", running.DetailText);
        Assert.False(running.CanStart);
        Assert.True(running.CanStop);
        Assert.False(running.NeedsStopConfirmation);
        Assert.Equal(PanelStrings.StopButton, running.StopButtonText);

        // Тот же работающий сервер, но поднятый НЕ панелью и под управление не взятый: показать
        // его панель обязана, а гасить — нет.
        var runningNotOurs = new ServerPanelModel(new FakeServer(
            new ServerState(ServerPresence.Running, 3081, 9, "node", "сервер DSH отвечает на порту 3081")));

        Assert.Equal(PanelStrings.ServerRunning, runningNotOurs.StatusText);
        Assert.False(runningNotOurs.CanStop);

        // Встроенный: кнопка доступна, но она СПРОСИТ — и подпись об этом говорит прямо.
        var adopted = new ServerPanelModel(new FakeServer(
            new ServerState(ServerPresence.Running, 3080, 19804, "node", "сервер DSH отвечает на порту 3080"),
            ServerOwner.Adopted));

        Assert.True(adopted.CanStop);
        Assert.True(adopted.NeedsStopConfirmation);
        Assert.Equal(PanelStrings.StopButtonAsks, adopted.StopButtonText);
        Assert.True(adopted.CanDetach);
        Assert.False(adopted.CanAdopt);

        var foreign = new ServerPanelModel(new FakeServer(
            new ServerState(ServerPresence.BusyByOther, 51234, 7, "chrome", "порт занят другой программой")));

        Assert.Equal(PanelStrings.ServerForeign, foreign.StatusText);
        Assert.False(foreign.CanStart);
        Assert.False(foreign.CanStop);

        var stopped = new ServerPanelModel(new FakeServer(
            ServerState.Stopped(0, "панель ещё не запускала сервер")));

        Assert.Equal(PanelStrings.ServerStopped, stopped.StatusText);
        Assert.True(stopped.CanStart);
        Assert.False(stopped.CanStop);

        // Окно без контроллера (съёмка, тесты) обязано быть честным, а не бодрым.
        var unbound = new ServerPanelModel();
        Assert.Equal(PanelStrings.ServerUnbound, unbound.StatusText);
        Assert.False(unbound.CanStart);
        Assert.False(unbound.CanStop);
    }

    /// <summary>
    /// Окно показывает найденный чужой сервер и предлагает встроиться — но не тогда, когда
    /// панель уже чем-то управляет, и не тогда, когда порты посмотреть не удалось.
    /// </summary>
    [Fact]
    public void Окно_предлагает_встроиться_в_найденный_сервер()
    {
        var found = new FoundServer(3080, 19804, "node");

        // Ничего не нашли: говорить нечего, кнопки нет.
        var nothing = new ServerPanelModel(new FakeServer(ServerState.Stopped(0)), DiscoveryResult.Nothing);
        Assert.Empty(nothing.FoundText);
        Assert.False(nothing.CanAdopt);
        Assert.Null(nothing.Candidate);

        // Нашли: строка называет порт и процесс, кандидат отдан окну, встраивание разрешено.
        var offer = new ServerPanelModel(new FakeServer(ServerState.Stopped(0)), new DiscoveryResult(true, new[] { found }));
        Assert.Contains("3080", offer.FoundText);
        Assert.Contains("19804", offer.FoundText);

        // Порт владельца назван отдельно: через него идёт текущая работа человека.
        Assert.Contains(ServerDecisions.OwnerPort.ToString(), offer.FoundText);
        Assert.Equal(PanelStrings.AdoptButton, offer.AdoptButtonText);
        Assert.True(offer.CanAdopt);
        Assert.Equal(found, offer.Candidate);

        // Панель уже управляет сервером — предлагать второе нельзя.
        var busy = new ServerPanelModel(
            new FakeServer(ServerState.Stopped(0), ServerOwner.Panel), new DiscoveryResult(true, new[] { found }));
        Assert.Empty(busy.FoundText);
        Assert.False(busy.CanAdopt);

        var alreadyAdopted = new ServerPanelModel(
            new FakeServer(ServerState.Stopped(0), ServerOwner.Adopted), new DiscoveryResult(true, new[] { found }));
        Assert.Empty(alreadyAdopted.FoundText);
        Assert.False(alreadyAdopted.CanAdopt);

        // Таблицу портов прочитать не удалось — это НЕ «серверов нет», и нельзя показывать
        // молчание вместо честного «не смотрели».
        var blind = new ServerPanelModel(
            new FakeServer(ServerState.Stopped(0)), DiscoveryResult.Unreadable);
        Assert.Equal(PanelStrings.FoundTableUnreadable, blind.FoundText);
        Assert.False(blind.CanAdopt);
    }

    /// <summary>
    /// Пояснение к кнопке обязано СЛОВАМИ сказать, что именно панель берёт: что сервер станет
    /// своим и гасится одной кнопкой, что ответ ЗАПОМНИТСЯ и что отвязаться можно — причём сервер
    /// останется жив. Без этих слов человек отвечал бы «да» на непонятный вопрос.
    /// </summary>
    [Fact]
    public void Пояснение_к_кнопке_называет_что_именно_панель_берёт()
    {
        Assert.Contains("своим", PanelStrings.AdoptHint, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.StopButton, PanelStrings.AdoptHint, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.DetachButton, PanelStrings.AdoptHint, StringComparison.Ordinal);
        Assert.Contains("запомнится", PanelStrings.AdoptHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("продолжит работу", PanelStrings.AdoptHint, StringComparison.Ordinal);

        // И подпись кнопки говорит про то, что панель БЕРЁТ сервер, а не «подключается на сеанс».
        Assert.Contains("под управление", PanelStrings.AdoptButton, StringComparison.Ordinal);
    }

    /// <summary>
    /// Окно показывает найденный сервер и кнопку «Взять под управление» — и она ДОСТУПНА.
    /// Это не украшение: отказавшись поднимать свой сервер рядом с найденным, панель отправляет
    /// человека именно сюда, и кнопка, которую видно, но нажать нельзя, оставила бы его без выхода.
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_найденный_сервер_и_доступную_кнопку()
    {
        var window = new MainWindow();
        var server = new FakeServer(ServerState.Stopped(0)) { Found = new FoundServer(3080, 19804, "node") };

        window.Attach(server);
        window.RefreshNow();
        Dispatcher.UIThread.RunJobs();

        var panel = window.FindControl<Border>("FoundServerPanel");
        var found = window.FindControl<TextBlock>("FoundServerText");
        var hint = window.FindControl<TextBlock>("AdoptHintText");
        var adopt = window.FindControl<Button>("AdoptServerButton");

        Assert.NotNull(panel);
        Assert.NotNull(found);
        Assert.NotNull(hint);
        Assert.NotNull(adopt);

        Assert.True(panel!.IsVisible, "найденный сервер не показан — предлагать взять его нечем");
        Assert.Contains("3080", found!.Text ?? string.Empty);
        Assert.Equal(PanelStrings.AdoptButton, adopt!.Content);
        Assert.True(adopt.IsEnabled, "кнопка «Взять под управление» обязана быть доступна");
        Assert.Contains(PanelStrings.DetachButton, hint!.Text ?? string.Empty);
    }

    /// <summary>
    /// КНОПКА «ОТКРЫТЬ АГЕНТА»: доступна ровно тогда, когда сервер отвечает И ссылка известна.
    /// Недоступная — говорит СЛОВАМИ, почему: «сервер не работает» и «ссылка ещё неизвестна»
    /// требуют от человека разного.
    ///
    /// ⚠️ И главное: САМОЙ ССЫЛКИ здесь нет ни в одном из ответов модели. В ней токен, и наружу
    /// она выходит ровно одним путём — в браузер по щелчку (красная линия 7).
    /// </summary>
    [Fact]
    public void Кнопка_открыть_агента_зависит_от_сервера_и_ссылки()
    {
        const string Secret = "http://127.0.0.1:3080/?token=SECRET-TOKEN-42";

        var running = new FakeServer(
            new ServerState(ServerPresence.Running, 3080, 42, "node", "отвечает"), ServerOwner.Panel);

        // Сервер отвечает, а ссылки панель ещё не знает — нажимать нечем, и она это говорит.
        var noLink = new ServerPanelModel(running);
        Assert.False(noLink.CanOpenAgent);
        Assert.Equal(PanelStrings.OpenAgentNoLinkHint, noLink.OpenAgentHint);

        // Ссылка есть — кнопка доступна, и подсказка не выдаёт её содержимое.
        running.EntryLink = Secret;

        var ready = new ServerPanelModel(running);
        Assert.True(ready.CanOpenAgent);
        Assert.Equal(PanelStrings.OpenAgentReadyHint, ready.OpenAgentHint);
        Assert.Equal(PanelStrings.OpenAgentButton, ready.OpenAgentButtonText);

        // Сервер не работает — открывать нечего, даже когда ссылка сохранилась с прошлого раза.
        var stopped = new FakeServer(ServerState.Stopped(0)) { EntryLink = Secret };
        var idle = new ServerPanelModel(stopped);

        Assert.False(idle.CanOpenAgent);
        Assert.Equal(PanelStrings.OpenAgentNoServerHint, idle.OpenAgentHint);

        // Окно без контроллера — тоже «открывать нечего», а не «сейчас откроем».
        var unbound = new ServerPanelModel();
        Assert.False(unbound.CanOpenAgent);
        Assert.Equal(PanelStrings.OpenAgentNoServerHint, unbound.OpenAgentHint);

        // НИ ОДНА из подсказок и подписей не содержит ссылки и токена.
        var texts = new[]
        {
            noLink.OpenAgentHint, ready.OpenAgentHint, idle.OpenAgentHint, unbound.OpenAgentHint,
            ready.OpenAgentButtonText,
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// РЕШЕНИЕ ВЛАДЕЛЬЦА 27.09.2026, закреплённое проверкой: НИ ОДНА из строк про эту кнопку
    /// не называет панель.
    ///
    /// Зачем отдельная проверка. Слово «панель» в этой кнопке читалось как «открыть саму панель» —
    /// то есть окно, которое и так открыто; владелец увидел это живьём и переименовал в «Открыть
    /// агента». Без проверки решение держалось бы только на значении в словаре: подпись кнопки
    /// и её текст в окне берутся из ОДНОГО источника, поэтому сравнение «что в окне = что в словаре»
    /// зелёное при любой формулировке — и возврат старого слова не уронил бы ничего.
    ///
    /// Каждый язык проверяется СВОИМ словом панели: «панель» / «panel» / «面板».
    /// </summary>
    [Fact]
    public void Ни_одна_строка_про_эту_кнопку_не_называет_панель()
    {
        var cases = new (string Language, string Panel)[]
        {
            ("ru", "панель"),
            (Localization.LanguageDecisions.English, "panel"),
            ("zh", "面板"),
        };

        Assert.Equal(3, cases.Length);

        foreach (var (language, panel) in cases)
        {
            foreach (var key in AgentButtonKeys())
            {
                var text = Localization.Loc.TIn(language, key);

                Assert.True(text != key, $"{language}: у ключа {key} нет записи в словаре");
                Assert.DoesNotContain(panel, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Кнопка и сообщения о ДЕЙСТВИИ зовут агента — своим словом в каждом языке («агент» / «agent» /
    /// «智能体»), тем же, каким он назван в остальных строках словаря.
    ///
    /// ⚠️ Две строки-ПРИЧИНЫ (<c>OpenAgentNoLinkHint</c> и <c>OpenAgentNoLinkTitle</c>) в этот
    /// список не входят намеренно: они называют причину — «ссылка входа ещё неизвестна», — а не
    /// действие, и вставленное туда слово «агент» сделало бы подсказку и заголовок хуже, ничего
    /// не уточнив. Их сторожит проверка выше: панели в них нет.
    /// </summary>
    [Fact]
    public void Кнопка_и_сообщения_о_действии_зовут_агента()
    {
        var cases = new (string Language, string Agent)[]
        {
            ("ru", "агент"),
            (Localization.LanguageDecisions.English, "agent"),
            ("zh", "智能体"),
        };

        string[] actionKeys =
        {
            nameof(PanelStrings.OpenAgentButton),
            nameof(PanelStrings.OpenAgentReadyHint),
            nameof(PanelStrings.OpenAgentNoServerHint),
            nameof(PanelStrings.OpenAgentFailedTitle),
            nameof(PanelStrings.OpenAgentOpenedLog),
            nameof(PanelStrings.OpenAgentOpenFailedLogFormat),
        };

        Assert.Equal(3, cases.Length);
        Assert.Equal(6, actionKeys.Length);

        foreach (var (language, agent) in cases)
        {
            foreach (var key in actionKeys)
            {
                var text = Localization.Loc.TIn(language, key);

                Assert.Contains(agent, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Ключи строк этой двери — один список на обе проверки выше.</summary>
    private static string[] AgentButtonKeys() => new[]
    {
        nameof(PanelStrings.OpenAgentButton),
        nameof(PanelStrings.OpenAgentReadyHint),
        nameof(PanelStrings.OpenAgentNoServerHint),
        nameof(PanelStrings.OpenAgentNoLinkHint),
        nameof(PanelStrings.OpenAgentFailedTitle),
        nameof(PanelStrings.OpenAgentNoLinkTitle),
        nameof(PanelStrings.OpenAgentOpenedLog),
        nameof(PanelStrings.OpenAgentOpenFailedLogFormat),
    };

    /// <summary>
    /// Окно показывает ту же кнопку, что модель: проверяются НАСТОЯЩИЕ элементы разметки.
    /// Недоступная кнопка обязана быть видна и объяснена — «есть, но нажать нельзя и непонятно
    /// почему» человек читает как поломку.
    ///
    /// ⚠️ **Постоянной надписи под кнопкой больше НЕТ** (замечание владельца 28.09.2026: она
    /// читалась как ОБРАЩЕНИЕ ПРОГРАММЫ к человеку — «Откроется работа вашего агента в браузере…»).
    /// Поэтому причина недоступности проверяется ТАМ, ГДЕ ОНА ТЕПЕРЬ ЖИВЁТ: в подсказке кнопки.
    /// Проверка обязана ходить тем же путём, что человек: он наводит мышь и читает подсказку.
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_кнопку_открыть_агента_и_объясняет_отказ()
    {
        var window = new MainWindow();
        var server = new FakeServer(
            new ServerState(ServerPresence.Running, 3080, 42, "node", "отвечает"), ServerOwner.Panel);

        window.Attach(server);
        window.RefreshNow();
        Dispatcher.UIThread.RunJobs();

        var button = window.FindControl<Button>("OpenAgentButton");

        Assert.NotNull(button);
        Assert.Equal(PanelStrings.OpenAgentButton, PanelTestStand.Label(button!));

        // Надписи под кнопкой нет вовсе — и это проверяется прямо: она была обращением к человеку
        // от лица программы и занимала строку на виду.
        Assert.Null(window.FindControl<TextBlock>("OpenAgentHintText"));

        // Ссылки панель ещё не знает — кнопка недоступна, и ПОДСКАЗКА называет причину.
        Assert.False(button.IsEnabled);
        Assert.Equal(PanelStrings.OpenAgentNoLinkHint, ToolTip.GetTip(button) as string);

        // Ссылка появилась — кнопка доступна, и подсказка говорит, что произойдёт.
        server.EntryLink = "http://127.0.0.1:3080/?token=SECRET-TOKEN-42";
        window.RefreshNow();
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsEnabled);
        Assert.Equal(PanelStrings.OpenAgentReadyHint, ToolTip.GetTip(button) as string);

        // Ни подпись, ни подсказка ссылку не выдают.
        Assert.DoesNotContain("SECRET-TOKEN-42", ToolTip.GetTip(button) as string ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TOKEN-42", button.Content?.ToString() ?? string.Empty, StringComparison.Ordinal);

        // И щелчок поднимает ПРОСЬБУ, а не открывает браузер сам: окно не знает, где ссылка.
        var asked = 0;
        window.OpenAgentRequested += () => asked++;

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// Окно показывает то же, что модель: проверяем НАСТОЯЩИЕ элементы разметки, а не только
    /// модель, — иначе «модель права» ничего не говорит о том, что человек увидит.
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_состояние_сервера_и_правильные_кнопки()
    {
        var window = new MainWindow();
        var server = new FakeServer(
            new ServerState(ServerPresence.Running, 51234, 42, "node", "сервер DSH отвечает на порту 51234"),
            ServerOwner.Panel);
        window.Attach(server);
        window.RefreshNow();
        Dispatcher.UIThread.RunJobs();

        var status = window.FindControl<TextBlock>("ServerStatusText");
        var detail = window.FindControl<TextBlock>("ServerDetailText");
        var start = window.FindControl<Button>("StartServerButton");
        var stop = window.FindControl<Button>("StopServerButton");

        Assert.NotNull(status);
        Assert.NotNull(detail);
        Assert.NotNull(start);
        Assert.NotNull(stop);

        Assert.Equal(PanelStrings.ServerRunning, status!.Text);
        Assert.Equal("сервер DSH отвечает на порту 51234", detail!.Text);
        Assert.False(start!.IsEnabled);
        Assert.True(stop!.IsEnabled);
        Assert.Equal(PanelStrings.StartButton, PanelTestStand.Label(start));
        Assert.Equal(PanelStrings.StopButton, PanelTestStand.Label(stop));

        // Панель спрашивает состояние у сервера, а не рисует придуманное.
        Assert.True(server.Refreshes >= 1, "окно не спросило состояние у сервера");
    }
}
